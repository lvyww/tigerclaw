use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};
use std::path::Path;

use serde_json::{Value, json};

use crate::decoder::DecodeResult;
use crate::lexicon::text_elements;
use crate::selection_keys::{SelectionKeyBindings, is_modifier, resolve_virtual_key};
use crate::state::{
    CompletedRerank, CompletedSentence, CoreState, SentenceJob, SentenceRerankJob,
};

pub const PROTOCOL_VERSION: u64 = 2;

pub fn handle_line(state: &mut CoreState, line: &str) -> Option<String> {
    let message: Value = match serde_json::from_str(line) {
        Ok(value) => value,
        Err(_) => return Some(simple_response(state, 0, false, json!({}))),
    };
    let message_type = message.get("type").and_then(Value::as_str).unwrap_or("");
    let seq = message.get("seq").and_then(Value::as_i64).unwrap_or(0);
    pump_sentence(state);

    match message_type {
        "hello" => Some(
            json!({
                "type": "response",
                "seq": seq,
                "success": true,
                "handled": false,
                "keyboard_open": state.keyboard_open,
                "protocol_version": PROTOCOL_VERSION,
                "core_build": env!("CARGO_PKG_VERSION"),
                "core_commit": "experimental",
                "core_branch": "rust-parallel",
                "sentence_model_loaded": state.sentence_model.is_some(),
                "sentence_ranks_loaded": !state.ranks.is_empty(),
                "lexicon_loaded": state.lexicon_path.is_some(),
                "ensure_system_layout_en": state.config.auto_switch_system_layout,
                "native_hook_alt_backslash_toggle_enabled": state.config.native_hook_alt_backslash,
                "auto_switch_system_layout_enabled": state.config.auto_switch_system_layout,
                "use_clipboard_commit": state.config.use_clipboard_commit,
                "clipboard_commit_whitelist": state.config.clipboard_whitelist,
                "core_path": std::env::current_exe()
                    .ok()
                    .and_then(|path| path.into_os_string().into_string().ok())
                    .unwrap_or_default(),
            })
            .to_string(),
        ),
        "query_state" => Some(query_state_response(state, &message, seq)),
        "reload_config" => Some(reload_config(state, &message, seq)),
        "reload_mb" => Some(reload_lexicon(state, &message, seq)),
        "get_config" => Some(config_response(state, seq)),
        "set_config" => Some(set_config(state, &message, seq)),
        "get_schema_list" => {
            let schemas = schema_list(state);
            Some(simple_response(state, seq, true, json!({
                "schema_list": schemas.join("\n"),
                "current_schema": state.config.current_schema
            })))
        }
        "construct_ci" => Some(simple_response(state, seq, true, json!({"code":state.lexicon.construct_code(string(&message, "text"))}))),
        "get_last_ci" => {
            let history_len = message.get("history_len").and_then(Value::as_u64).unwrap_or(0) as usize;
            let count = history_len.min(state.send_history.len()).min(20);
            let text = if count == 0 {
                String::new()
            } else {
                state.send_history[state.send_history.len() - count..].concat()
            };
            Some(simple_response(state, seq, true, json!({"text":text})))
        },
        "get_selection_key_config" => Some(selection_key_config_response(state, seq)),
        "set_selection_key_config" => Some(set_selection_key_config(state, &message, seq)),
        "get_send_history_count" => Some(simple_response(state, seq, true, json!({"count":state.send_history.len().min(20)}))),
        "add_ci" => Some(add_ci(state, &message, seq)),
        "open_official" => Some(open_target(state, seq, "https://github.com/lvyww/bime")),
        "open_mb_folder" => Some(open_mb_folder(state, seq)),
        "export_mb" => Some(export_mb(state, seq)),
        "exit_core" => {
            // Reply before requesting shutdown.  Overlay/Dialog wait for a
            // response synchronously; exiting from this match arm used to
            // turn a successful shutdown into a pipe timeout.
            state.shutdown_requested = true;
            state.reset_physical_chords();
            Some(simple_response(state, seq, true, json!({"shutdown_requested": true})))
        }
        "show_menu" => Some(launch_ui(state, seq, "TigerClaw.Overlay.exe", "--menu")),
        "show_config" => Some(launch_ui(state, seq, "TigerClaw.Dialog.exe", "--config")),
        "show_addci" => Some(launch_ui(state, seq, "TigerClaw.Dialog.exe", "--addci")),
        "ctrl_space" => {
            if !state.config.ctrl_space_toggle {
                return Some(state_response(state, seq, false));
            }
            state.ctrl_space_armed = false;
            state.ctrl_space_switched = false;
            state.ctrl_chord_down = false;
            state.space_chord_down = false;
            state.last_ctrl_up = None;
            state.keyboard_open = !state.keyboard_open;
            let commit_text = composition_raw_commit(state);
            clear_composition(state);
            Some(without_cancel_composition(if commit_text.is_empty() {
                state_response(state, seq, true)
            } else {
                commit_response(state, seq, true, commit_text)
            }))
        }
        "caret" => {
            state.caret_x = integer(&message, "x");
            state.caret_y = integer(&message, "y");
            let width = integer(&message, "width");
            if width > 0 { state.caret_width = width; }
            let height = integer(&message, "height");
            if height > 0 {
                state.caret_height = height;
            }
            // A standalone caret notification is the fresh anchor that C#
            // uses to release the short candidate-window suppression after a
            // key opened a composition without attached caret coordinates.
            state.fresh_caret = true;
            state.fresh_caret_deadline = None;
            None
        }
        "ime_active" => {
            state.ime_active = message
                .get("active")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            None
        }
        "focus" => {
            let hwnd = message.get("hwnd").and_then(Value::as_i64).unwrap_or(0);
            let process_id = message.get("processId").or_else(|| message.get("process_id")).and_then(Value::as_i64).unwrap_or(0);
            let process_name = string(&message, "processName").to_owned();
            let class_name = string(&message, "className").to_owned();
            let window_title = string(&message, "windowTitle").to_owned();
            let changed = state.focus_hwnd != hwnd
                || state.focus_process_id != process_id
                || state.focus_process_name != process_name
                || state.focus_class_name != class_name
                || state.focus_window_title != window_title;
            state.focus_hwnd = hwnd;
            state.focus_process_id = process_id;
            state.focus_process_name = process_name;
            state.focus_class_name = class_name;
            state.focus_window_title = window_title;
            if changed {
                state.reset_physical_chords();
                state.reset_composition_transients();
            }
            state.fresh_caret = false;
            state.fresh_caret_deadline = None;
            None
        },
        "composition_canceled" => {
            clear_composition(state);
            state.reset_physical_chords();
            state.reset_composition_transients();
            None
        },
        "hook_native_disabled" => {
            state.native_hook_active = true;
            state.hook_native_disabled = boolean(&message, "disabled");
            if state.hook_native_disabled {
                clear_composition(state);
            }
            None
        },
        "key" => {
            let client_session = string(&message, "client_session");
            let event_id = string(&message, "event_id");
            if let Some(response) =
                state.replayed_key_response(client_session, event_id, seq)
            {
                return Some(response);
            }

            let before_keyboard = state.keyboard_open;
            let action = string(&message, "action");
            let is_down = action.eq_ignore_ascii_case("down") || action.eq_ignore_ascii_case("key_down");
            let was_composing = !state.input_buffer.is_empty() || !state.raw_input.is_empty();
            if is_down && message.get("caret_x").is_some() && message.get("caret_y").is_some() {
                state.caret_x = integer(&message, "caret_x");
                state.caret_y = integer(&message, "caret_y");
                let width = integer(&message, "width");
                let height = integer(&message, "height");
                if width > 0 { state.caret_width = width; }
                if height > 0 { state.caret_height = height; }
                state.fresh_caret = true;
                state.fresh_caret_deadline = None;
            }
            if string(&message, "frontend").eq_ignore_ascii_case("hook_native") {
                state.native_hook_active = true;
            }
            if is_down && state.config.key_sound {
                state.sound_vk = message.get("vk").and_then(Value::as_i64).unwrap_or(0) as i32;
                state.sound_seq = state.sound_seq.saturating_add(1);
            }
            let response = handle_key(state, &message, seq);
            if is_down
                && !was_composing
                && (!state.input_buffer.is_empty() || !state.raw_input.is_empty())
                && !state.fresh_caret
            {
                state.fresh_caret_deadline = Some(Instant::now() + Duration::from_millis(30));
            }
            postprocess_key(state, &message, &response);
            let response = shape_key_response(state, &message, &response, before_keyboard);
            // Configuration requests can invalidate a TSF composition while
            // being served on a Dialog connection.  Only a real key response
            // is consumed by the TSF bridge, so keep this edge pending across
            // every non-key response and clear it only after shaping the next
            // physical-key response.
            state.cancel_composition_pending.set(false);
            state.store_key_response(client_session, event_id, &response);
            Some(response)
        }
        _ => Some(simple_response(state, seq, false, json!({}))),
    }
}

fn handle_key(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let action = message.get("action").and_then(Value::as_str).unwrap_or("");
    let raw_vk = message.get("vk").and_then(Value::as_u64).unwrap_or(0);
    let scan = message.get("scan").and_then(Value::as_u64).unwrap_or(0);
    let extended = boolean(message, "extended");
    let vk = resolve_virtual_key(raw_vk, scan, extended);
    let ctrl = boolean(message, "ctrl");
    let alt = boolean(message, "alt");
    let win = boolean(message, "win");
    let shift = boolean(message, "shift");
    let repeat = message.get("repeat").and_then(Value::as_i64).unwrap_or(1);

    let is_down = action.eq_ignore_ascii_case("down") || action.eq_ignore_ascii_case("key_down");
    let is_up = action.eq_ignore_ascii_case("up") || action.eq_ignore_ascii_case("key_up");

    if let Some(response) = handle_ctrl_space_chord(state, seq, vk, action, repeat, shift, alt, win) {
        return response;
    }

    // Legacy C# behavior: an idle bare right-control press switches to
    // English. This follows Ctrl+Space chord tracking so a right-control
    // chord is not switched to English before Space is processed.
    if is_down && vk == 0xA3 && !ctrl && !shift && !alt && !win && state.input_buffer.is_empty() && state.raw_input.is_empty() {
        state.reset_physical_chords();
        state.keyboard_open = false;
        return state_response(state, seq, true);
    }

    // A few frontends omit the quote key-down notification and only deliver
    // key-up.  Keep the fallback at the same state-machine boundary as C# so
    // smart quotes still commit an active first candidate.
    if vk == 0xDE {
        if action.eq_ignore_ascii_case("down") || action.eq_ignore_ascii_case("key_down") {
            state.quote_down_seen = true;
        } else if action.eq_ignore_ascii_case("up") || action.eq_ignore_ascii_case("key_up") {
            let had_down = state.quote_down_seen;
            state.quote_down_seen = false;
            if !had_down && !ctrl && !alt && !win && !boolean(message, "capsLock") {
                return handle_quote_key_up_fallback(state, seq, shift);
            }
        }
    }

    if is_up && state.handled_modifier_selection_keys.remove(&vk) {
        if matches!(vk, 0x10 | 0xA0 | 0xA1)
            && !state.shift_left_down
            && !state.shift_right_down
        {
            state.skip_shift_toggle_once = false;
        }
        return state_response(state, seq, true);
    }
    if is_down && is_modifier(vk) {
        if let Some(response) = try_handle_custom_selection(state, seq, vk) {
            if matches!(vk, 0x10 | 0xA0 | 0xA1) {
                state.skip_shift_toggle_once = true;
            }
            state.handled_modifier_selection_keys.insert(vk);
            return response;
        }
    }
    if matches!(vk, 0x10 | 0xA0 | 0xA1) {
        return handle_shift(state, seq, vk, action, ctrl, alt, win);
    }
    if is_down && vk == 0x14 {
        if !state.input_buffer.is_empty() || !state.raw_input.is_empty() {
            // CapsLock is a pass-through key, but C# commits the resolved
            // candidate for an ordinary composition first.  Language toggles
            // (Ctrl+Space/Shift) intentionally use the raw code; CapsLock is
            // the one physical path that uses the normal candidate resolver.
            let commit_text = composition_candidate_commit(state);
            clear_composition(state);
            return commit_response(state, seq, false, commit_text);
        }
        return state_response(state, seq, false);
    }
    // When CapsLock is already active, Windows lets the target application
    // receive ordinary keys.  The C# engine still handles the physical
    // CapsLock key above (committing any composition), but does not capture
    // the following letters/punctuation as an IME code.
    if is_down && boolean(message, "capsLock") {
        return state_response(state, seq, false);
    }
    if !is_down {
        if state.one_shot_vk == vk {
            state.one_shot_vk = 0;
        }
        return state_response(state, seq, false);
    }
    if is_down
        && ((ctrl && !alt && !win) || (alt && !ctrl && !win && !shift))
        && !state.input_buffer.is_empty()
        && !state.config.sentence_active()
        && (0x31..=0x39).contains(&vk)
    {
        let index = (vk - 0x31) as usize;
        let candidates = normal_candidates(state);
        if let Some(chosen) = candidates.get(index).cloned() {
            let chosen = normal_candidate_commit(state, index).unwrap_or(chosen);
            if state.one_shot_vk == vk {
                return state_response(state, seq, true);
            }
            let operation = if alt { "{前移}" } else if shift { "{删除}" } else { "{置顶}" };
            let code = state.input_buffer.clone();
            if apply_user_adjustment(state, operation, &code, &chosen) {
                state.one_shot_vk = vk;
                state.selected_candidate = 0;
                return state_response(state, seq, true);
            }
        }
    }
    if ctrl && !alt && !win && !shift {
        if let Some(response) = handle_ctrl_hotkey(state, seq, vk) {
            return response;
        }
    }
    // With NumLock off Windows may still report a VK_NUMPAD* key in a trace;
    // normal composition leaves it to the target application. Sentence mode
    // has an explicit numpad-code path below.
    if !state.config.sentence_active()
        && !state.pinyin_mode
        && !state.uppercase_mode
        && (0x60..=0x69).contains(&vk)
        && !boolean(message, "numLock")
    {
        return state_response(state, seq, false);
    }

    if ctrl || alt || win || !state.keyboard_open {
        if (ctrl || alt || win) && !is_modifier(vk) && !state.input_buffer.is_empty() {
            clear_composition(state);
            state.cancel_composition_pending.set(true);
        }
        return state_response(state, seq, false);
    }
    if shift {
        state.shift_chord_used = true;
    }
    if !shift && !is_modifier(vk) {
        if let Some(response) = try_handle_custom_selection(state, seq, vk) {
            return response;
        }
    }
    if is_down && !state.config.sentence_active() && !state.pinyin_mode && !state.uppercase_mode
        && !state.input_buffer.is_empty()
    {
        if let Some(delta) = normal_page_key(state, vk, shift) {
            move_normal_page(state, delta);
            return state_response(state, seq, true);
        }
    }

    match vk {
        0xC0 if state.input_buffer.is_empty() && state.raw_input.is_empty() && state.config.back_query && !state.pinyin_lexicon.code_lengths().is_empty() => {
            state.pinyin_mode = true;
            state.input_buffer.push('·');
            state.raw_input.push('·');
            state.selected_candidate = 0;
            state_response(state, seq, true)
        }
        0xBA if state.input_buffer.is_empty() && state.raw_input.is_empty() && !shift && state.lexicon.short_symbol_enabled(';') => {
            if state.lexicon.is_auto_short_symbol(";") {
                let output = state.lexicon.top_candidate(";").unwrap_or_else(|| "；".to_owned());
                commit_response(state, seq, true, output)
            } else {
                state.input_buffer.push(';');
                state.raw_input.push(';');
                state.selected_candidate = 0;
                state_response(state, seq, true)
            }
        }
        0xBF if state.input_buffer.is_empty() && state.raw_input.is_empty() && !shift && state.lexicon.short_symbol_enabled('/') => {
            if state.lexicon.is_auto_short_symbol("/") {
                let output = state.lexicon.top_candidate("/").unwrap_or_else(|| if state.config.slash_outputs_dunhao { "、".to_owned() } else { "/".to_owned() });
                commit_response(state, seq, true, output)
            } else {
                state.input_buffer.push('/');
                state.raw_input.push('/');
                state.selected_candidate = 0;
                state_response(state, seq, true)
            }
        }
        0xDB if state.input_buffer.is_empty() && state.raw_input.is_empty() && !shift && state.lexicon.short_symbol_enabled('[') => {
            if state.lexicon.is_auto_short_symbol("[") {
                let output = state.lexicon.top_candidate("[").unwrap_or_else(|| "【".to_owned());
                commit_response(state, seq, true, output)
            } else {
                state.input_buffer.push('[');
                state.raw_input.push('[');
                state.selected_candidate = 0;
                state_response(state, seq, true)
            }
        }
        0x5A if state.input_buffer.is_empty() && state.raw_input.is_empty() && !shift && state.lexicon.short_symbol_enabled('z') => {
            if state.lexicon.is_auto_short_symbol("z") {
                let output = state.lexicon.top_candidate("z").unwrap_or_else(|| "z".to_owned());
                commit_response(state, seq, true, output)
            } else {
                state.input_buffer.push('z');
                state.raw_input.push('z');
                state.selected_candidate = 0;
                state_response(state, seq, true)
            }
        }
        0x41..=0x5a => {
            let mut character = char::from_u32(vk as u32).unwrap_or_default().to_ascii_lowercase();
            let uppercase = boolean(message, "shift") ^ boolean(message, "capsLock");
            if state.uppercase_mode {
                character = if uppercase { character.to_ascii_uppercase() } else { character };
                state.input_buffer.push(character);
                state.raw_input.push(character);
                return state_response(state, seq, true);
            }
            // Shift+letter from idle starts the literal/uppercase mode.  This
            // check precedes sentence activation so a shifted first key never
            // enters the sentence decoder.
            if shift && state.input_buffer.is_empty() && state.raw_input.is_empty() {
                state.uppercase_mode = true;
                state.input_buffer.push(character.to_ascii_uppercase());
                state.raw_input.push(character.to_ascii_uppercase());
                return state_response(state, seq, true);
            }
            if state.pinyin_mode {
                // Pinyin lookup is case-insensitive and C# stores the code in
                // lowercase even when a shifted physical letter is supplied.
                state.input_buffer.push(character);
                state.raw_input.push(character);
                return state_response(state, seq, true);
            }
            if state.config.sentence_active() {
                return append_sentence_code(state, seq, character);
            }
            if state.config.mixed_input {
                let active_len = state.input_buffer.chars().count();
                let raw_len = state.raw_input.chars().count();
                let segment_start = raw_len.saturating_sub(active_len);
                if active_len >= state.config.max_code_length
                    && !normal_candidates(state).is_empty()
                {
                    // Capture the currently visible first candidate before
                    // appending the boundary key.  This is a soft preference,
                    // not a permanent reorder of the lexicon.
                    if let Some(candidate) = normal_candidate_commit(state, 0) {
                        state.mixed_preferred.insert(segment_start, candidate);
                    }
                }
                state.raw_input.push(if uppercase { character.to_ascii_uppercase() } else { character });
                rebuild_mixed_segments(state);
                state.selected_candidate = 0;
                return state_response(state, seq, true);
            }
            append_normal_code(state, seq, character)
        }
        0x08 if !state.input_buffer.is_empty() || !state.raw_input.is_empty() => {
            // Dispatch by the active composition mode before consulting the
            // global mixed-input option.  The option is normally enabled in
            // 虎整句 too, but a sentence composition must keep its complete
            // raw buffer and committed prefix boundary instead of being
            // rebuilt as fixed-width mixed-input segments.
            if state.config.sentence_active() {
                state.early.reset_evidence();
                if state.early.committed_raw_length > 0
                    && state.input_buffer.len()
                        <= state.early.committed_raw_length.saturating_add(1)
                {
                    clear_composition(state);
                    return state_response(state, seq, true);
                }
                state.input_buffer.pop();
                state.raw_input.pop();
                if state.input_buffer.is_empty() {
                    clear_composition(state);
                    return state_response(state, seq, true);
                }
                state.sentence_generation = state.sentence_generation.saturating_add(1);
                if state.sentence_decode_sync {
                    refresh_sentence(state, false);
                } else {
                    request_sentence_worker(state);
                }
                state.selected_candidate = 0;
                return state_response(state, seq, true);
            }
            if state.uppercase_mode || state.pinyin_mode {
                state.input_buffer.pop();
                state.raw_input.pop();
                let marker_only = state.pinyin_mode && state.input_buffer.chars().count() <= 1;
                if state.input_buffer.is_empty() || marker_only {
                    clear_composition(state);
                    return state_response(state, seq, true);
                }
                return state_response(state, seq, true);
            }
            if state.config.mixed_input {
                state.raw_input.pop();
                if state.raw_input.is_empty() {
                    clear_composition(state);
                    return state_response(state, seq, true);
                }
                rebuild_mixed_segments(state);
                state.selected_candidate = 0;
                return state_response(state, seq, true);
            }
            state.input_buffer.pop();
            state.raw_input.pop();
            if state.input_buffer.is_empty() {
                clear_composition(state);
                return state_response(state, seq, true);
            }
            state.selected_candidate = 0;
            state_response(state, seq, true)
        }
        0x1b if !state.input_buffer.is_empty() => {
            clear_composition(state);
            state_response(state, seq, true)
        }
        0xba | 0xde | 0x20 | 0x30..=0x39 | 0x60..=0x69
            if !state.input_buffer.is_empty() && (!shift || vk == 0x20) => {
            if state.uppercase_mode {
                return handle_uppercase_key(state, seq, vk, shift);
            }
            if state.pinyin_mode {
                return handle_pinyin_key(state, seq, vk, shift);
            }
            if state.config.sentence_active() {
                if vk == 0x20 {
                    ensure_sentence_current(state);
                    let candidates = published_sentence_candidates(state);
                    if let Some(commit_text) = candidates.get(state.selected_candidate) {
                        let commit_text = commit_text.clone();
                        clear_composition(state);
                        return commit_response(state, seq, true, commit_text);
                    }
                    return state_response(state, seq, true);
                }
                if let Some(mark) = sentence_code_char(vk, shift, &state.config) {
                    return append_sentence_code(state, seq, mark);
                }
                if let Some(punctuation) = punctuation_output(state, vk, shift) {
                    return commit_sentence_with_suffix(state, seq, &punctuation);
                }
                return state_response(state, seq, false);
            }
            if let Some(output) = quick_symbol_composition(state, vk, shift) {
                return commit_response(state, seq, true, output);
            }
            let rank = if vk == 0x20 {
                Some(state.selected_candidate)
            } else if vk == 0xba {
                state.config.semicolon_second.then_some(1)
            } else if vk == 0xde {
                state.config.quote_third.then_some(2)
            } else if vk == 0x30 || vk == 0x60 {
                Some(9)
            } else if (0x61..=0x69).contains(&vk) {
                Some((vk - 0x61) as usize)
            } else {
                Some((vk - 0x31) as usize)
            };
            if let Some(commit_text) = rank.and_then(|rank| normal_candidate_commit(state, rank)) {
                let commit_text = if state.config.mixed_input {
                    format!("{}{}", state.mixed_prefix, commit_text)
                } else {
                    commit_text.clone()
                };
                state.input_buffer.clear();
                state.mixed_prefix.clear();
                state.mixed_segments.clear();
                state.raw_input.clear();
                state.selected_candidate = 0;
                return commit_response(state, seq, true, commit_text);
            }
            if matches!(vk, 0xba | 0xde) {
                if let Some(punctuation) = punctuation_output(state, vk, shift) {
                    return commit_normal_with_suffix(state, seq, &punctuation);
                }
            }
            state_response(state, seq, false)
        }
        0x09 | 0x26 | 0x28 if !state.input_buffer.is_empty() => {
            if state.config.sentence_active() {
                ensure_sentence_current(state);
                state.early.suspend();
                let count = published_sentence_candidates(state).len().min(state.config.page_size);
                if vk == 0x09 && count == 0 {
                    if state.config.tab_clear { clear_composition(state); }
                    return state_response(state, seq, true);
                }
                if count == 0 { return state_response(state, seq, false); }
                if vk == 0x28 || (vk == 0x09 && !shift) {
                    state.selected_candidate = (state.selected_candidate + 1) % count;
                } else {
                    state.selected_candidate = (state.selected_candidate + count - 1) % count;
                }
                return state_response(state, seq, true);
            }
            if state.pinyin_mode { return handle_pinyin_navigation(state, seq, vk, shift); }
            if state.uppercase_mode {
                if vk == 0x09 && state.config.tab_clear { clear_composition(state); return state_response(state, seq, true); }
                return state_response(state, seq, false);
            }
            if vk == 0x09 && state.config.tab_clear {
                clear_composition(state);
                return state_response(state, seq, true);
            }
            // Normal C# candidates do not have a highlighted selected index;
            // Tab passes through when it is configured not to clear and
            // Up/Down are reserved for the target application.
            state_response(state, seq, false)
        }
        0x30..=0x39 | 0xba..=0xc0 | 0xdb..=0xde
            if is_punctuation_key(vk, shift) => {
            if state.uppercase_mode {
                return handle_uppercase_key(state, seq, vk, shift);
            }
            if state.pinyin_mode {
                return handle_pinyin_key(state, seq, vk, shift);
            }
            let Some(punctuation) = punctuation_output(state, vk, shift) else {
                return state_response(state, seq, false);
            };
            if state.input_buffer.is_empty() {
                return commit_response(state, seq, true, punctuation);
            }
            if state.config.sentence_active() {
                return commit_sentence_with_suffix(state, seq, &punctuation);
            }
            commit_normal_with_suffix_for_key(state, seq, &punctuation, vk, shift)
        }
        0x0d if state.uppercase_mode && !state.input_buffer.is_empty() => {
            handle_uppercase_key(state, seq, vk, false)
        }
        0x0d if state.input_buffer.is_empty() && state.raw_input.is_empty() => {
            // C# returns the physical Enter's newline as TextToOutput in
            // Chinese idle mode.  It is not handled by TSF, but it still
            // belongs in send-history for subsequent Dialog previews.
            commit_response(state, seq, false, "\n".to_owned())
        }
        0x0d if !state.input_buffer.is_empty() && state.config.enter_clear => {
            clear_composition(state);
            state_response(state, seq, true)
        }
        0x0d if !state.input_buffer.is_empty() => {
            let commit_text = if state.config.sentence_active() {
                ensure_sentence_current(state);
                let raw = state.raw_input.clone();
                let committed = state.early.committed_raw_length.min(raw.len());
                raw[committed..].to_owned()
            } else if state.config.mixed_input {
                std::mem::take(&mut state.raw_input)
            } else {
                std::mem::take(&mut state.input_buffer)
            };
            clear_composition(state);
            if commit_text.is_empty() {
                state_response(state, seq, true)
            } else {
                commit_response(state, seq, true, commit_text)
            }
        }
        _ => state_response(state, seq, false),
    }
}

const CTRL_SPACE_GRACE: Duration = Duration::from_millis(250);

fn handle_ctrl_space_chord(
    state: &mut CoreState,
    seq: i64,
    vk: u64,
    action: &str,
    repeat: i64,
    shift: bool,
    alt: bool,
    win: bool,
) -> Option<String> {
    let is_down = action.eq_ignore_ascii_case("down") || action.eq_ignore_ascii_case("key_down");
    let is_up = action.eq_ignore_ascii_case("up") || action.eq_ignore_ascii_case("key_up");
    if !is_down && !is_up {
        return None;
    }
    let is_ctrl = matches!(vk, 0x11 | 0xA2 | 0xA3);
    let is_space = vk == 0x20;
    if !is_ctrl && !is_space {
        if is_down && state.ctrl_chord_down {
            state.ctrl_chord_down = false;
            state.space_chord_down = false;
            state.ctrl_space_armed = false;
            state.ctrl_space_switched = false;
            state.last_ctrl_up = None;
        }
        if !state.ctrl_chord_down && !state.space_chord_down && state.ctrl_space_armed
            && state.last_ctrl_up.is_some_and(|time| time.elapsed() > CTRL_SPACE_GRACE)
        {
            state.ctrl_space_armed = false;
            state.last_ctrl_up = None;
        }
        return None;
    }
    if !state.config.ctrl_space_toggle {
        state.ctrl_chord_down = false;
        state.space_chord_down = false;
        state.ctrl_space_armed = false;
        state.ctrl_space_switched = false;
        state.last_ctrl_up = None;
        return None;
    }
    if shift || alt || win {
        state.ctrl_chord_down = false;
        state.space_chord_down = false;
        state.ctrl_space_armed = false;
        state.ctrl_space_switched = false;
        state.last_ctrl_up = None;
        return None;
    }

    // A plain Space is an ordinary candidate/commit key.  Track its physical
    // state for a possible later Ctrl release, but let the normal key path
    // process it unless Ctrl is actually down (or the release-order grace
    // window is active).
    if is_space && is_down && !state.ctrl_chord_down {
        state.space_chord_down = true;
        return None;
    }
    if is_space && is_up && !state.ctrl_chord_down
        && !state.last_ctrl_up.is_some_and(|time| time.elapsed() <= CTRL_SPACE_GRACE)
    {
        state.space_chord_down = false;
        return None;
    }

    if is_down {
        if is_ctrl {
            state.ctrl_chord_down = true;
            state.ctrl_space_armed = !state.space_chord_down;
            state.ctrl_space_switched = false;
            state.last_ctrl_up = None;
        } else {
            state.space_chord_down = true;
            if state.ctrl_chord_down && repeat <= 1 {
            return Some(toggle_from_physical_ctrl_space(state, seq));
            }
        }
        return Some(state_response(state, seq, false));
    }

    if is_ctrl {
        state.ctrl_chord_down = false;
        state.last_ctrl_up = Some(Instant::now());
        if state.space_chord_down {
            return Some(toggle_from_physical_ctrl_space(state, seq));
        }
    } else {
        state.space_chord_down = false;
        let within_grace = state.last_ctrl_up.is_some_and(|time| time.elapsed() <= CTRL_SPACE_GRACE);
        if state.ctrl_chord_down || within_grace {
            return Some(toggle_from_physical_ctrl_space(state, seq));
        }
    }
    if !state.ctrl_chord_down && !state.space_chord_down && state.ctrl_space_switched {
        state.ctrl_space_armed = false;
        state.ctrl_space_switched = false;
        state.last_ctrl_up = None;
    }
    Some(state_response(state, seq, false))
}

fn toggle_from_physical_ctrl_space(state: &mut CoreState, seq: i64) -> String {
    if !state.ctrl_space_armed || state.ctrl_space_switched {
        return state_response(state, seq, false);
    }
    state.keyboard_open = !state.keyboard_open;
    state.ctrl_space_switched = true;
    let commit_text = composition_raw_commit(state);
    clear_composition(state);
    if commit_text.is_empty() {
        state_response(state, seq, true)
    } else {
        commit_response(state, seq, true, commit_text)
    }
}

fn handle_quote_key_up_fallback(state: &mut CoreState, seq: i64, shift: bool) -> String {
    if state.config.sentence_active() || state.input_buffer.is_empty() {
        let quote = emit_smart_quote(state, shift);
        if state.config.sentence_active() && !state.input_buffer.is_empty() {
            return commit_sentence_with_suffix(state, seq, &quote);
        }
        return commit_response(state, seq, true, quote);
    }
    let mut output = normal_candidate_commit(state, 0).unwrap_or_default();
    if !output.is_empty() { output.push_str(&emit_smart_quote(state, shift)); }
    clear_composition(state);
    if output.is_empty() {
        state_response(state, seq, true)
    } else {
        commit_response(state, seq, true, output)
    }
}

fn quick_symbol_composition(state: &mut CoreState, vk: u64, shift: bool) -> Option<String> {
    if shift || state.input_buffer.len() != 1 {
        return None;
    }
    let current = state.input_buffer.as_str();
    let (expected, output) = match current {
        ";" if state.lexicon.short_symbol_enabled(';') => (0xBA, "；"),
        "/" if state.lexicon.short_symbol_enabled('/') => {
            (0xBF, if state.config.slash_outputs_dunhao { "、" } else { "/" })
        }
        "[" if state.lexicon.short_symbol_enabled('[') => (0xDB, "【"),
        _ => return None,
    };
    let has_candidate = !normal_candidates(state).is_empty();
    if vk != expected && !(vk == 0x20 && !has_candidate) {
        return None;
    }
    let prefix = state.mixed_prefix.clone();
    clear_composition(state);
    Some(format!("{prefix}{output}"))
}

fn composition_raw_commit(state: &CoreState) -> String {
    if !state.raw_input.is_empty() {
        state.raw_input.clone()
    } else {
        state.input_buffer.clone()
    }
}

fn composition_candidate_commit(state: &CoreState) -> String {
    if state.uppercase_mode {
        return uppercase_commit(&state.input_buffer);
    }
    if state.pinyin_mode {
        let code = state.input_buffer.strip_prefix('·').unwrap_or(&state.input_buffer);
        return pinyin_candidate_commit(state, code, 0)
            .unwrap_or_else(|| state.input_buffer.clone());
    }
    if state.config.sentence_active() {
        return state.raw_input.clone();
    }
    if state.config.mixed_input {
        let active = normal_candidate_commit(state, 0).unwrap_or_default();
        return format!("{}{}", state.mixed_prefix, active);
    }
    normal_candidate_commit(state, 0).unwrap_or_else(|| state.input_buffer.clone())
}

/// Append a normal code key using the same boundary decision as C# Core:
/// once the configured maximum has been reached, inspect the *extended* code
/// before deciding whether to keep it.  An illegal extension commits the
/// current first candidate and starts a fresh composition with the physical
/// boundary letter, so no key is lost.
fn append_normal_code(state: &mut CoreState, seq: i64, character: char) -> String {
    let current = state.input_buffer.clone();
    let max = state.config.max_code_length.clamp(1, 16);
    let extended = format!("{current}{character}");
    if current.is_empty() {
        state.input_buffer.push(character);
        state.raw_input.push(character);
        state.selected_candidate = 0;
        if max == 1 && state.config.auto_commit_no_repeat && state.lexicon.is_unique_terminal(&state.input_buffer) {
            let commit = state.lexicon.top_candidate(&state.input_buffer).unwrap_or_default();
            clear_composition(state);
            return commit_response(state, seq, true, commit);
        }
        return state_response(state, seq, true);
    }

    if current.chars().count() < max.saturating_sub(1) {
        state.input_buffer.push(character);
        state.raw_input.push(character);
        state.selected_candidate = 0;
        return state_response(state, seq, true);
    }

    if state.config.auto_commit_no_repeat && state.lexicon.is_unique_terminal(&extended) {
        let commit = state.lexicon.top_candidate(&extended).unwrap_or_default();
        state.raw_input.clear();
        state.input_buffer.clear();
        state.selected_candidate = 0;
        return commit_response(state, seq, true, commit);
    }

    // At max-1 C# deliberately accepts the final physical key even when the
    // resulting max-length code has no entry.  The decision to roll over is
    // made only when extending an already complete max-length code; this is
    // important for tables whose legal entries are shorter than the nominal
    // maximum and for the empty-code setting.
    if current.chars().count() == max.saturating_sub(1) {
        state.input_buffer.push(character);
        state.raw_input.push(character);
        state.selected_candidate = 0;
        return state_response(state, seq, true);
    }

    // Existing code or a proper prefix is legal even when it is longer than
    // the user's nominal max (whole-code entries and non-terminal paths are
    // supported by the C# decoder).
    if state.lexicon.has_code(&extended) || state.lexicon.is_non_terminal(&extended) {
        state.input_buffer.push(character);
        state.raw_input.push(character);
        state.selected_candidate = 0;
        return state_response(state, seq, true);
    }

    let first_candidate = normal_candidate_commit(state, 0).unwrap_or_default();
    if !first_candidate.is_empty() || state.config.empty_code_clear {
        state.input_buffer.clear();
        state.raw_input.clear();
        state.input_buffer.push(character);
        state.raw_input.push(character);
        state.selected_candidate = 0;
        return if first_candidate.is_empty() {
            state_response(state, seq, true)
        } else {
            commit_response(state, seq, true, first_candidate)
        };
    }

    // With empty-code clearing disabled and no candidate, preserve the raw
    // invalid code exactly as C# does.
    state.input_buffer.push(character);
    state.raw_input.push(character);
    state.selected_candidate = 0;
    state_response(state, seq, true)
}

fn rebuild_mixed_segments(state: &mut CoreState) {
    state.mixed_segments.clear();
    state.mixed_prefix.clear();
    let max = state.config.max_code_length.clamp(1, 16);
    let raw = state.raw_input.clone();
    let chars: Vec<char> = raw.chars().collect();
    let completed = if chars.is_empty() { 0 } else { ((chars.len() - 1) / max) * max };
    state.mixed_preferred
        .retain(|start, _| *start < completed);
    for start in (0..completed).step_by(max) {
        let code: String = chars[start..start + max].iter().collect();
        let candidate = state
            .mixed_preferred
            .get(&start)
            .cloned()
            .filter(|text| state.lexicon.contains_candidate(&code, text))
            .or_else(|| state.lexicon.top_candidate(&code))
            .unwrap_or_default();
        state.mixed_segments.push((code.clone(), candidate.clone()));
        state.mixed_prefix.push_str(if candidate.is_empty() { &code } else { &candidate });
    }
    state.input_buffer = chars[completed..].iter().collect();
}

fn postprocess_key(state: &mut CoreState, message: &Value, response: &str) {
    let action = string(message, "action");
    if !(action.eq_ignore_ascii_case("down") || action.eq_ignore_ascii_case("key_down")) {
        return;
    }
    let vk = message.get("vk").and_then(Value::as_u64).unwrap_or(0);
    let shift = boolean(message, "shift");
    let ctrl = boolean(message, "ctrl");
    let alt = boolean(message, "alt");
    let win = boolean(message, "win");
    let value: Value = serde_json::from_str(response).unwrap_or_default();
    let handled = value.get("handled").and_then(Value::as_bool).unwrap_or(false);
    let committed = value
        .get("commit_text")
        .and_then(Value::as_str)
        .unwrap_or("");

    // C# records a best-effort text guess for unhandled physical keys.  This
    // is what makes Dialog's recent-history preview useful while the IME is
    // disabled or a key is passed to the target application.
    if !handled {
        if let Some(text) = guess_pass_through_text(message) {
            append_send_history(state, &text);
        }
    }

    if vk == 0x08 && !handled {
        if let Some(last) = state.send_history.pop() {
            if last == "“" || last == "”" {
                state.left_double_quote = !state.left_double_quote;
                state.deleted_double_quote_armed = true;
            } else if last == "‘" || last == "’" {
                state.left_single_quote = !state.left_single_quote;
                state.deleted_single_quote_armed = true;
            } else {
                state.deleted_single_quote_armed = false;
                state.deleted_double_quote_armed = false;
            }
        }
    }

    let committed_digit = committed
        .chars()
        .last()
        .is_some_and(|ch| ch.is_ascii_digit() || ('０'..='９').contains(&ch));
    let pass_through_digit = !handled
        && !shift
        && !ctrl
        && !alt
        && !win
        && matches!(vk, 0x30..=0x39 | 0x60..=0x69);
    if committed_digit || pass_through_digit {
        state.dot_after_digit_armed = true;
    } else if !is_modifier(vk) {
        state.dot_after_digit_armed = false;
    }
}

fn append_send_history(state: &mut CoreState, text: &str) {
    if text.is_empty() {
        return;
    }
    for element in text_elements(text) {
        state.send_history.push(element);
    }
    // Keep enough recent context for the Dialog API while preventing an
    // unattended English session from growing without bound.
    const HISTORY_CAPACITY: usize = 256;
    if state.send_history.len() > HISTORY_CAPACITY {
        let remove = state.send_history.len() - HISTORY_CAPACITY;
        state.send_history.drain(..remove);
    }
}

fn guess_pass_through_text(message: &Value) -> Option<String> {
    if boolean(message, "ctrl") || boolean(message, "alt") || boolean(message, "win") {
        return None;
    }
    let vk = message.get("vk").and_then(Value::as_u64).unwrap_or(0);
    let shift = boolean(message, "shift");
    let caps = boolean(message, "capsLock") || boolean(message, "caps_lock");
    if vk == 0x20 {
        return Some(" ".to_owned());
    }
    if (0x41..=0x5a).contains(&vk) {
        let upper = shift ^ caps;
        return char::from_u32(vk as u32)
            .map(|ch| if upper { ch } else { ch.to_ascii_lowercase() })
            .map(|ch| ch.to_string());
    }
    if (0x30..=0x39).contains(&vk) {
        if !shift {
            return Some(char::from_u32(vk as u32).unwrap_or('0').to_string());
        }
        return Some(match vk {
            0x30 => ")",
            0x31 => "!",
            0x32 => "@",
            0x33 => "#",
            0x34 => "$",
            0x35 => "%",
            0x36 => "^",
            0x37 => "&",
            0x38 => "*",
            0x39 => "(",
            _ => unreachable!(),
        }.to_owned());
    }
    if (0x60..=0x69).contains(&vk) {
        return Some(char::from_u32((b'0' as u32) + vk as u32 - 0x60).unwrap_or('0').to_string());
    }
    if let Some(symbol) = uppercase_symbol(vk, shift) {
        return Some(symbol);
    }
    None
}

fn try_handle_custom_selection(state: &mut CoreState, seq: i64, vk: u64) -> Option<String> {
    if state.input_buffer.is_empty() || state.config.sentence_active() || state.uppercase_mode {
        return None;
    }
    let selection = state.selection_keys.selection_for_key(vk)?;
    let pinyin = state.pinyin_mode;
    let pinyin_code = state.input_buffer.strip_prefix('·').unwrap_or(&state.input_buffer).to_owned();
    let candidates = if pinyin {
        pinyin_candidates(state, &pinyin_code)
    } else {
        normal_candidates(state)
    };
    let active = if let Some(candidate) = candidates.get(selection - 1) {
        if pinyin {
            pinyin_candidate_commit(state, &pinyin_code, selection - 1).unwrap_or_else(|| candidate.clone())
        } else {
            normal_candidate_commit(state, selection - 1).unwrap_or_else(|| candidate.clone())
        }
    } else if is_digit_selection_key(vk) && !candidates.is_empty() {
        let digit = if selection == 10 { 0 } else { selection };
        let first = if pinyin {
            pinyin_candidate_commit(state, &pinyin_code, 0).unwrap_or_else(|| candidates[0].clone())
        } else {
            normal_candidate_commit(state, 0).unwrap_or_else(|| candidates[0].clone())
        };
        format!("{}{digit}", first)
    } else {
        String::new()
    };
    let commit_text = format!("{}{}", state.mixed_prefix, active);
    clear_composition(state);
    Some(commit_response(state, seq, true, commit_text))
}

fn is_digit_selection_key(vk: u64) -> bool {
    matches!(vk, 0x30..=0x39 | 0x60..=0x69)
}

fn commit_response(state: &mut CoreState, seq: i64, handled: bool, commit_text: String) -> String {
    if matches!(commit_text.as_str(), "{添加}" | "{加词}") {
        // These entries are commands rather than literal output.  Mirror
        // C#'s OpenAddCiWindow result and let the Windows host start Dialog.
        let _ = spawn_ui_process(state, "TigerClaw.Dialog.exe", "--addci");
        let mut response: Value = serde_json::from_str(&state_response(state, seq, handled))
            .expect("state response is valid JSON");
        response["commit_text"] = Value::String(String::new());
        response["open_add_ci_window"] = Value::Bool(true);
        return response.to_string();
    }
    if commit_text == "{隐藏候选}" {
        let old_config = state.config.clone();
        let old_config_path = state.config_path.clone();
        let hidden = !state.config.hide_candidate;
        let changed = state
            .config
            .set("隐藏候选", if hidden { "是" } else { "否" })
            .is_ok()
            && persist_config_result(state).is_ok();
        if changed {
            state.config_version = state.config_version.saturating_add(1);
        } else {
            state.config = old_config;
            state.config_path = old_config_path;
        }
        let mut response: Value = serde_json::from_str(&state_response(state, seq, handled))
            .expect("state response is valid JSON");
        response["commit_text"] = Value::String(String::new());
        response["changed"] = Value::Bool(changed);
        response["hide_candidate"] = Value::Bool(state.config.hide_candidate);
        return response.to_string();
    }
    let commit_text = normalize_commit_text(state, &commit_text);
    if !commit_text.is_empty() {
        // C# updates {重复上屏}'s source only for handled IME output.  A
        // pass-through Enter (whose TextToOutput is a newline for history)
        // must not replace the last candidate that the macro repeats.
        if handled {
            state.repeat_buffer = commit_text.clone();
        }
        state.commit_history.push(commit_text.clone());
        if state.commit_history.len() > 64 { state.commit_history.remove(0); }
        append_send_history(state, &commit_text);
    }
    let mut response: Value = serde_json::from_str(&state_response(state, seq, handled))
        .expect("state response is valid JSON");
    response["commit_text"] = commit_text.into();
    response.to_string()
}

/// Apply the small set of output macros implemented by C#'s
/// `NormalizeResultTextActions`/`ConvertOutputText`.  These are evaluated at
/// commit time, after candidate selection, so the emitted history and the
/// repeat macro observe the same text as the target application.
fn normalize_commit_text(state: &CoreState, text: &str) -> String {
    if text.is_empty() {
        return String::new();
    }
    if text == "{重复上屏}" {
        return state.repeat_buffer.clone();
    }
    if text == "。" && state.dot_after_digit_armed {
        return ".".to_owned();
    }
    if text.starts_with('{') && text.ends_with('}') && text.contains('|') {
        // C# chooses a random branch.  Selecting the first branch keeps the
        // Rust core deterministic for protocol replay while preserving the
        // candidate's legal output set.
        return text[1..text.len() - 1]
            .split('|')
            .find(|item| !item.is_empty())
            .unwrap_or_default()
            .to_owned();
    }
    match text {
        "{日期}" => format_date_macro("date"),
        "{日期.}" => format_date_macro("dot"),
        "{日期-}" => format_date_macro("dash"),
        "{日期/}" => format_date_macro("slash"),
        "{时分秒}" => format_date_macro("time_seconds"),
        "{时分}" => format_date_macro("time_minutes"),
        "{星期}" => format_date_macro("weekday_name"),
        "{周}" => format_date_macro("weekday"),
        _ => text.to_owned(),
    }
}

fn format_date_macro(kind: &str) -> String {
    let (year, month, day, hour, minute, second, weekday) = local_date_parts();
    match kind {
        "date" => format!("{year:04}年{month:02}月{day:02}日"),
        "dot" => format!("{year:04}.{month:02}.{day:02}"),
        "dash" => format!("{year:04}-{month:02}-{day:02}"),
        "slash" => format!("{year:04}/{month:02}/{day:02}"),
        "time_seconds" => format!("{hour:02}:{minute:02}:{second:02}"),
        "time_minutes" => format!("{hour:02}:{minute:02}"),
        "weekday_name" => ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"]
            .get(weekday)
            .unwrap_or(&"星期日")
            .to_string(),
        "weekday" => ["周日", "周一", "周二", "周三", "周四", "周五", "周六"]
            .get(weekday)
            .unwrap_or(&"周日")
            .to_string(),
        _ => String::new(),
    }
}

fn local_date_parts() -> (i32, u32, u32, u32, u32, u32, usize) {
    #[cfg(windows)]
    {
        let mut system = std::mem::MaybeUninit::<windows_sys::Win32::Foundation::SYSTEMTIME>::zeroed();
        unsafe {
            windows_sys::Win32::System::SystemInformation::GetLocalTime(system.as_mut_ptr());
            let system = system.assume_init();
            return (
                system.wYear as i32,
                system.wMonth as u32,
                system.wDay as u32,
                system.wHour as u32,
                system.wMinute as u32,
                system.wSecond as u32,
                system.wDayOfWeek as usize,
            );
        }
    }
    #[cfg(not(windows))]
    {
        let seconds = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_secs())
            .unwrap_or(0);
        let days = (seconds / 86_400) as i64;
        let day_seconds = seconds % 86_400;
        let z = days + 719_468;
        let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
        let doe = z - era * 146_097;
        let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
        let year = yoe + era * 400;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let day = doy - (153 * mp + 2) / 5 + 1;
        let month = mp + if mp < 10 { 3 } else { -9 };
        let year = year + if month <= 2 { 1 } else { 0 };
        let weekday = ((days + 4).rem_euclid(7)) as usize;
        (
            year as i32,
            month as u32,
            day as u32,
            (day_seconds / 3_600) as u32,
            ((day_seconds % 3_600) / 60) as u32,
            (day_seconds % 60) as u32,
            weekday,
        )
    }
}

fn reload_config(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let path = string(message, "path");
    let path = if path.is_empty() {
        state.config_path.clone().unwrap_or_default()
    } else {
        path.to_owned()
    };
    if path.is_empty() {
        return json!({"type":"response","seq":seq,"success":false,"handled":false,"error":"config path is required"}).to_string();
    }
    match crate::config::Config::load(&path) {
        Ok(config) => {
            let old_config = state.config.clone();
            let old_lexicon = state.lexicon.clone();
            let old_path = state.lexicon_path.clone();
            let old_supplements = state.supplements.clone();
            let old_lexicon_version = state.lexicon_version;
            let old_keyboard_open = state.keyboard_open;
            let old_config_path = state.config_path.clone();
            let old_pinyin = state.pinyin_lexicon.clone();
            let old_selection_keys = state.selection_keys.clone();
            let had_composition = !state.input_buffer.is_empty() || !state.raw_input.is_empty();
            state.config = config;
            state.config_path = Some(path.clone());
            state.keyboard_open = state.config.default_chinese;
            reload_selection_keys(state);
            reload_pinyin_lexicon(state);
            let has_schema_root = schema_root(state).is_some();
            let lexicon_ok = if has_schema_root {
                reload_schema_lexicon(state)
            } else {
                // Unit/stdio callers may intentionally provide an isolated
                // config without a code-root.  A real packaged Core with a
                // base directory still reports a missing configured schema.
                state.base_dir.is_none() || state.lexicon_path.is_some()
            };
            if !lexicon_ok {
                state.config = old_config;
                state.lexicon = old_lexicon;
                state.lexicon_path = old_path;
                state.supplements = old_supplements;
                state.lexicon_version = old_lexicon_version;
                state.keyboard_open = old_keyboard_open;
                state.config_path = old_config_path;
                state.pinyin_lexicon = old_pinyin;
                state.selection_keys = old_selection_keys;
                state.publish_sentence_assets();
                return simple_response(state, seq, false, json!({"error":"configured schema could not be loaded"}));
            }
            state.config_version = state.config_version.saturating_add(1);
            clear_composition(state);
            state.cancel_composition_pending.set(had_composition);
            simple_response(state, seq, true, json!({
                "config_version": state.config_version,
                "lexicon_version": state.lexicon_version
            }))
        }
        Err(error) => json!({"type":"response","seq":seq,"success":false,"handled":false,"error":error.to_string()}).to_string(),
    }
}

fn reload_lexicon(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let path = string(message, "path");
    let explicit = !path.is_empty();
    let path = if explicit {
        std::path::PathBuf::from(path)
    } else if let Some(dir) = schema_dir(state) {
        dir
    } else if let Some(path) = state.lexicon_path.as_deref() {
        std::path::PathBuf::from(path)
    } else {
        return json!({"type":"response","seq":seq,"success":false,"handled":false,"error":"configured schema directory is unavailable"}).to_string();
    };
    let loaded = if path.is_dir() {
        crate::lexicon::Lexicon::load_directory(&path)
    } else {
        crate::lexicon::Lexicon::load(&path)
    };
    match loaded {
        Ok(lexicon) => {
            state.lexicon = lexicon;
            state.lexicon_path = Some(path.to_string_lossy().into_owned());
            reload_selection_keys(state);
            reload_pinyin_lexicon(state);
            state.lexicon_version = state.lexicon_version.saturating_add(1);
            state.reload_supplements();
            state.publish_sentence_assets();
            if !explicit {
                if let Some(name) = Path::new(state.lexicon_path.as_deref().unwrap_or_default()).file_name().and_then(|name| name.to_str()) {
                    let _ = state.config.set("当前码表", name);
                }
            }
            without_cancel_composition(state_response(state, seq, true))
        }
        Err(error) => json!({"type":"response","seq":seq,"success":false,"handled":false,"error":error.to_string()}).to_string(),
    }
}

fn config_response(state: &CoreState, seq: i64) -> String {
    simple_response(state, seq, true, json!({"config_text": config_text(state), "config_version":state.config_version}))
}

fn selection_key_config_response(state: &CoreState, seq: i64) -> String {
    simple_response(state, seq, true, json!({
        "config_text": state.selection_keys.config_text(),
        "default_text": SelectionKeyBindings::default().config_text(),
        "config_path": state.selection_key_config_path.clone().unwrap_or_default()
    }))
}

fn set_selection_key_config(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let config_text = string(message, "config_text");
    let bindings = match SelectionKeyBindings::parse(config_text) {
        Ok(bindings) => bindings,
        Err(error) => {
            return simple_response(state, seq, false, json!({
                "config_text": state.selection_keys.config_text(),
                "error": error
            }));
        }
    };
    if let Some(path) = state.selection_key_config_path.as_deref() {
        if let Err(error) = bindings.save(std::path::Path::new(path)) {
            return simple_response(state, seq, false, json!({
                "config_text": state.selection_keys.config_text(),
                "error": error.to_string()
            }));
        }
    }
    state.selection_keys = bindings;
    selection_key_config_response(state, seq)
}

fn reload_selection_keys(state: &mut CoreState) {
    state.selection_keys = state
        .selection_key_config_path
        .as_deref()
        .and_then(|path| SelectionKeyBindings::load_or_create(std::path::Path::new(path)).ok())
        .unwrap_or_default();
}

fn reload_pinyin_lexicon(state: &mut CoreState) {
    let Some(base) = state.base_dir.as_deref() else {
        state.pinyin_lexicon = crate::lexicon::Lexicon::default();
        return;
    };
    let path = Path::new(base).join("拼音反查码表");
    state.pinyin_lexicon = if path.is_dir() {
        crate::lexicon::Lexicon::load_directory(path).unwrap_or_default()
    } else {
        crate::lexicon::Lexicon::default()
    };
}

fn set_config(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let mut next = state.config.clone();
    if let (Some(key), Some(value)) = (message.get("key").and_then(Value::as_str), message.get("value")) {
        let Some(canonical) = crate::config::canonical_key(key) else {
            return simple_response(state, seq, false, json!({"changed":false,"error":"unknown key"}));
        };
        let value = config_value_text(value);
        if let Err(error) = next.set(canonical, &value) {
            return simple_response(state, seq, false, json!({"changed":false,"error":error}));
        }
    } else if let Some(config) = message.get("config").or_else(|| message.get("values")).and_then(Value::as_object) {
        for (key, value) in config {
            let Some(canonical) = crate::config::canonical_key(key) else { continue; };
            let value = config_value_text(value);
            if let Err(error) = next.set(canonical, &value) {
                return simple_response(state, seq, false, json!({"changed":false,"error":error}));
            }
        }
    } else {
        return json!({"type":"response","seq":seq,"success":false,"handled":false,"error":"config object is required"}).to_string();
    }
    let changed_keys = config_changed_keys(&state.config, &next);
    if changed_keys.is_empty() {
        return simple_response(state, seq, true, json!({"changed":false,"config_version":state.config_version,"lexicon_version":state.lexicon_version}));
    }
    let invalidate = changed_keys.iter().any(|key| config_invalidates_composition(key));
    let schema_change = changed_keys.iter().any(|key| *key == "当前码表" || *key == "码表存储位置");
    let old_config = state.config.clone();
    let old_lexicon = state.lexicon.clone();
    let old_path = state.lexicon_path.clone();
    let old_supplements = state.supplements.clone();
    let old_lexicon_version = state.lexicon_version;
    let old_pinyin = state.pinyin_lexicon.clone();
    let old_config_path = state.config_path.clone();
    let old_selection_keys = state.selection_keys.clone();
    let old_keyboard_open = state.keyboard_open;
    state.config = next;
    if schema_change && !reload_schema_lexicon(state) {
        state.config = old_config;
        state.lexicon = old_lexicon;
        state.lexicon_path = old_path;
        state.supplements = old_supplements;
        state.lexicon_version = old_lexicon_version;
        state.pinyin_lexicon = old_pinyin;
        state.config_path = old_config_path;
        state.selection_keys = old_selection_keys.clone();
        state.keyboard_open = old_keyboard_open;
        state.publish_sentence_assets();
        return simple_response(state, seq, false, json!({"changed":false,"error":"configured schema could not be loaded","config_version":state.config_version,"lexicon_version":state.lexicon_version}));
    }
    if let Err(error) = persist_config_result(state) {
        state.config = old_config;
        state.lexicon = old_lexicon;
        state.lexicon_path = old_path;
        state.supplements = old_supplements;
        state.lexicon_version = old_lexicon_version;
        state.pinyin_lexicon = old_pinyin;
        state.publish_sentence_assets();
        state.selection_keys = old_selection_keys;
        state.config_path = old_config_path;
        state.keyboard_open = old_keyboard_open;
        return simple_response(state, seq, false, json!({"changed":false,"error":error,"config_version":state.config_version,"lexicon_version":state.lexicon_version}));
    }
    state.config_version = state.config_version.saturating_add(1);
    let had_composition = invalidate && (!state.input_buffer.is_empty() || !state.raw_input.is_empty());
    if invalidate {
        clear_composition(state);
        state.cancel_composition_pending.set(had_composition);
    }
    simple_response(state, seq, true, json!({"changed":true,"config_version":state.config_version,"lexicon_version":state.lexicon_version}))
}

fn config_value_text(value: &Value) -> String {
    match value {
        Value::Bool(flag) => if *flag { "是".to_owned() } else { "否".to_owned() },
        Value::String(text) => text.clone(),
        Value::Number(number) => number.to_string(),
        Value::Null => String::new(),
        other => other.to_string(),
    }
}

fn config_changed_keys<'a>(old: &'a crate::config::Config, next: &'a crate::config::Config) -> Vec<&'static str> {
    crate::config::DEFAULT_PAIRS.iter().filter_map(|(key, _)| (old.get(key) != next.get(key)).then_some(*key)).collect()
}

fn config_invalidates_composition(key: &str) -> bool {
    matches!(key,
        // These switches change the composition state machine itself.  UI,
        // selector and schema settings are live-updated while an existing
        // composition remains intact, matching ProtocolHandler.
        "中英文不限长混合输入" | "整句输入" | "自动启用整句模式")
}

fn add_ci(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let code = string(message, "code").trim().to_owned();
    let text = string(message, "text").trim().to_owned();
    if code.is_empty() || text.is_empty() {
        return simple_response(state, seq, false, json!({"error":"code and text are required"}));
    }
    let Some(raw_path) = state.lexicon_path.clone() else {
        return simple_response(state, seq, false, json!({"error":"active schema has no writable lexicon"}));
    };
    let path = {
        let candidate = Path::new(&raw_path);
        if candidate.is_dir() {
            candidate.join("用户调整.txt")
        } else {
            candidate
                .parent()
                .unwrap_or(candidate)
                .join("用户调整.txt")
        }
    };
    if let Some(parent) = path.parent() {
        if let Err(error) = std::fs::create_dir_all(parent) {
            return simple_response(state, seq, false, json!({"error":error.to_string()}));
        }
    }
    let line = format!("{{添加}}{}", state.lexicon.export_line(&code, &text));
    if let Err(error) = std::fs::OpenOptions::new().create(true).append(true).open(&path)
        .and_then(|mut file| std::io::Write::write_all(&mut file, line.as_bytes()))
    {
        return simple_response(state, seq, false, json!({"error":error.to_string()}));
    }
    // Dialog add-word uses the durable adjustment file and appends a new
    // candidate after the existing stable list, matching CoreRuntimeState's
    // TryAddCi/LoadAdjust behavior.  Stage the same operation before swapping
    // the live snapshot so a duplicate/reorder can never be reported as a
    // successful disk write with stale memory.
    let mut next = state.lexicon.clone();
    next.append_candidate(&code, &text);
    state.lexicon = next;
    state.lexicon_version = state.lexicon_version.saturating_add(1);
    state.publish_sentence_assets();
    simple_response(state, seq, true, json!({"lexicon_version":state.lexicon_version,"changed":true}))
}

fn apply_user_adjustment(state: &mut CoreState, operation: &str, code: &str, text: &str) -> bool {
    let Some(raw_path) = state.lexicon_path.clone() else { return false; };
    let path = {
        let active = Path::new(&raw_path);
        if active.is_dir() { active.join("用户调整.txt") } else { active.with_file_name("用户调整.txt") }
    };
    if let Some(parent) = path.parent() {
        if std::fs::create_dir_all(parent).is_err() { return false; }
    }
    // Stage the in-memory operation on a clone first.  This avoids writing a
    // no-op adjustment (for example, pressing the already-top candidate) and
    // keeps the live snapshot unchanged if the backing file is unavailable.
    let mut next = state.lexicon.clone();
    let changed = match operation {
        "{置顶}" => next.move_top(code, text),
        "{删除}" => next.delete_candidate(code, text),
        "{前移}" => next.advance_candidate(code, text),
        "{添加}" => next.add_candidate(code, text),
        _ => false,
    };
    if !changed {
        return false;
    }
    // Keep the adjustment payload lossless for spaces, tabs, newlines and
    // display=>commit entries, matching C#'s FormatLexiconEntryForExport.
    let line = format!("{operation}{}", state.lexicon.export_line(code, text));
    if std::fs::OpenOptions::new().create(true).append(true).open(&path)
        .and_then(|mut file| std::io::Write::write_all(&mut file, line.as_bytes())).is_err()
    {
        return false;
    }
    state.lexicon = next;
    state.lexicon_version = state.lexicon_version.saturating_add(1);
    state.publish_sentence_assets();
    changed
}

fn spawn_ui_process(state: &CoreState, executable: &str, arg: &str) -> bool {
    let Some(base) = state.base_dir.as_deref() else { return false; };
    let path=std::path::Path::new(base).join(executable);
    let mut command = std::process::Command::new(&path);
    if !arg.is_empty() { command.arg(arg); }
    if !path.exists() {
        return false;
    }
    let Ok(child) = command.current_dir(base).spawn() else {
        return false;
    };
    crate::state::register_owned_child(&state.owned_children, child, path.to_string_lossy());
    true
}

fn launch_ui(state: &CoreState, seq: i64, executable: &str, arg: &str) -> String {
    let ok = spawn_ui_process(state, executable, arg);
    #[cfg(windows)]
    if ok && executable.eq_ignore_ascii_case("TigerClaw.Overlay.exe") {
        unsafe {
            use std::os::windows::ffi::OsStrExt;
            let wide: Vec<u16> = std::ffi::OsStr::new(r"Local\TigerClaw.ShowMenu.v1").encode_wide().chain(Some(0)).collect();
            let h = windows_sys::Win32::System::Threading::CreateEventW(std::ptr::null(), 0, 0, wide.as_ptr());
            if !h.is_null() { windows_sys::Win32::System::Threading::SetEvent(h); windows_sys::Win32::Foundation::CloseHandle(h); }
        }
    }
    simple_response(state,seq,ok,json!({}))
}

fn clear_composition(state: &mut CoreState) {
    state.input_buffer.clear();
    state.mixed_prefix.clear();
    state.mixed_segments.clear();
    state.mixed_preferred.clear();
    state.raw_input.clear();
    state.sentence_candidates.clear();
    state.sentence_segmented.clear();
    state.early.reset();
    // Generation is a lifetime-wide invalidation token.  Resetting it to zero
    // allows an old completed decode to collide with a new composition after
    // the counter climbs back to the same value.  C# increments this token in
    // ClearCompositionInput for every clear, including non-sentence clears.
    state.sentence_generation = state.sentence_generation.saturating_add(1);
    state.sentence_decoded_raw.clear();
    state.sentence_decoded_lexicon_version = 0;
    state.sentence_last_result = DecodeResult::default();
    if let Ok(mut control) = state.sentence_worker.lock() {
        control.job = None;
    }
    if let Ok(mut completed) = state.sentence_completed.lock() {
        *completed = None;
    }
    if let Ok(mut completed) = state.sentence_rerank_completed.lock() {
        *completed = None;
    }
    if let Ok(mut worker) = state.sentence_rerank_worker.lock() {
        worker.job = None;
    }
    state.sentence_neural_accepted_raw.clear();
    state.sentence_neural_top_text.clear();
    // The background sentence worker holds this same lock for the entire
    // duration of a Beam Search call (not just a quick snapshot), and
    // clear_composition runs on the key-handling path while the outer
    // CoreState mutex is held, so blocking here on `lock()` would stall
    // every other queued key/query_state message until that in-flight
    // decode finishes. Skip the reset on contention instead: the next
    // real decode call already self-resets this same cache when it sees
    // an empty raw input (see decode_with's empty-raw branch), so nothing
    // is lost, just deferred.
    if let Ok(mut decoder) = state.sentence_decoder.try_lock() {
        decoder.reset();
    }
    state.selected_candidate = 0;
    // A caret anchor is consumed by the composition that opened it.  Keep a
    // standalone/key-attached caret from suppressing the next, unrelated
    // composition after this one has committed or been cancelled.
    state.fresh_caret = false;
    state.fresh_caret_deadline = None;
    state.reset_composition_transients();
}

fn sentence_code_char(vk: u64, shift: bool, config: &crate::config::Config) -> Option<char> {
    if shift {
        return None;
    }
    match vk {
        0xba if config.semicolon_second => Some(';'),
        0xde if config.quote_third => Some('\''),
        0x30..=0x39 => Some(char::from_u32((b'0' as u32) + (vk as u32 - 0x30)).unwrap_or('0')),
        0x60..=0x69 => Some(char::from_u32((b'0' as u32) + (vk as u32 - 0x60)).unwrap_or('0')),
        _ => None,
    }
}

fn live_raw_length(state: &CoreState) -> usize {
    state
        .input_buffer
        .len()
        .saturating_sub(state.early.committed_raw_length)
}

fn append_sentence_code(state: &mut CoreState, seq: i64, mark: char) -> String {
    if live_raw_length(state) >= 128 {
        return state_response(state, seq, true);
    }
    pump_sentence(state);
    state.input_buffer.push(mark);
    state.raw_input.push(mark);
    state.sentence_generation = state.sentence_generation.saturating_add(1);
    let extra = if state.sentence_decode_sync {
        refresh_sentence(state, true)
    } else {
        let result = state.sentence_last_result.clone();
        let neural_top = sentence_neural_top_for_result(state, &result);
        let extra = state.early.try_commit(
            &result,
            state.config.early_commit,
            &state.input_buffer,
            neural_top.as_deref(),
        );
        if !extra.is_empty() {
            // The completed decode still contains candidates relative to the
            // old committed prefix.  C# filters that snapshot immediately
            // after a partial commit; otherwise the async path publishes the
            // already committed text again until the next Beam result arrives
            // and selection/commit no longer agrees with the visible suffix.
            state.sentence_generation = state.sentence_generation.saturating_add(1);
            trim_sentence_result_after_early_commit(state, &extra);
        }
        request_sentence_worker(state);
        extra
    };
    state.selected_candidate = 0;
    if extra.is_empty() {
        state_response(state, seq, true)
    } else {
        commit_response(state, seq, true, extra)
    }
}

fn commit_sentence_with_suffix(state: &mut CoreState, seq: i64, suffix: &str) -> String {
    ensure_sentence_current(state);
    let candidates = published_sentence_candidates(state);
    let commit_text = candidates
        .get(state.selected_candidate)
        .cloned()
        .unwrap_or_else(|| uncommitted_sentence_raw(state));
    let commit_text = format!("{commit_text}{suffix}");
    clear_composition(state);
    commit_response(state, seq, true, commit_text)
}

fn uncommitted_sentence_raw(state: &CoreState) -> String {
    let committed = state.early.committed_raw_length.min(state.raw_input.len());
    state.raw_input[committed..].to_owned()
}

fn commit_normal_with_suffix(state: &mut CoreState, seq: i64, suffix: &str) -> String {
    commit_normal_with_suffix_for_key(state, seq, suffix, 0, false)
}

fn commit_normal_with_suffix_for_key(
    state: &mut CoreState,
    seq: i64,
    suffix: &str,
    vk: u64,
    shift: bool,
) -> String {
    let candidates = normal_candidates(state);
    let candidate_index = if state.selected_candidate < candidates.len() {
        state.selected_candidate
    } else {
        0
    };
    let candidate = normal_candidate_commit(state, candidate_index)
        .or_else(|| candidates.get(candidate_index).cloned())
        .unwrap_or_default();
    if candidate.is_empty() && state.mixed_prefix.is_empty() {
        clear_composition(state);
        return state_response(state, seq, true);
    }
    let suffix = if !shift
        && vk == 0xbe
        && !state.config.cn_use_en_punc
        && candidate
            .chars()
            .last()
            .is_some_and(|ch| ch.is_ascii_digit() || ('０'..='９').contains(&ch))
    {
        "."
    } else {
        suffix
    };
    let commit = format!("{}{}{}", state.mixed_prefix, candidate, suffix);
    clear_composition(state);
    commit_response(state, seq, true, commit)
}

fn is_punctuation_key(vk: u64, shift: bool) -> bool {
    if shift && (0x30..=0x39).contains(&vk) {
        return true;
    }
    matches!(vk, 0xba..=0xc0 | 0xdb..=0xde)
}

fn punctuation_output(state: &mut CoreState, vk: u64, shift: bool) -> Option<String> {
    if vk == 0xde {
        if state.config.cn_use_en_punc {
            return Some(if shift { "\"" } else { "'" }.to_owned());
        }
        return Some(emit_smart_quote(state, shift));
    }

    if !shift && vk == 0xbe && !state.config.cn_use_en_punc && state.dot_after_digit_armed {
        return Some(".".to_owned());
    }

    let value = if state.config.cn_use_en_punc {
        match (shift, vk) {
            (true, 0x30) => ")", (true, 0x31) => "!", (true, 0x32) => "@",
            (true, 0x33) => "#", (true, 0x34) => "$", (true, 0x35) => "%",
            (true, 0x36) => "^", (true, 0x37) => "&", (true, 0x38) => "*",
            (true, 0x39) => "(", (true, 0xbb) => "+", (true, 0xbc) => "<",
            (true, 0xbd) => "_", (true, 0xbe) => ">", (true, 0xbf) => "?",
            (true, 0xc0) => "~", (true, 0xdb) => "{", (true, 0xdc) => "|",
            (true, 0xdd) => "}", (true, 0xba) => ":",
            (false, 0xbb) => "=", (false, 0xbc) => ",", (false, 0xbd) => "-",
            (false, 0xbe) => ".", (false, 0xbf) => "/", (false, 0xc0) => "`",
            (false, 0xdb) => "[", (false, 0xdc) => "\\", (false, 0xdd) => "]",
            (false, 0xba) => ";",
            _ => return None,
        }
    } else {
        match (shift, vk) {
            (true, 0x30) => "）", (true, 0x31) => "！", (true, 0x32) => "@",
            (true, 0x33) => "#", (true, 0x34) => "￥", (true, 0x35) => "%",
            (true, 0x36) => "……", (true, 0x37) => "&", (true, 0x38) => "*",
            (true, 0x39) => "（", (true, 0xbb) => "+", (true, 0xbc) => "《",
            (true, 0xbd) => "——", (true, 0xbe) => "》", (true, 0xbf) => "？",
            (true, 0xc0) => "~", (true, 0xdb) => "{", (true, 0xdc) => "|",
            (true, 0xdd) => "}", (true, 0xba) => "：",
            (false, 0xbb) => "=", (false, 0xbc) => "，", (false, 0xbd) => "-",
            (false, 0xbe) => "。", (false, 0xbf) if state.config.slash_outputs_dunhao => "、",
            (false, 0xbf) => "/", (false, 0xc0) => "·", (false, 0xdb) => "【",
            (false, 0xdc) => "、", (false, 0xdd) => "】", (false, 0xba) => "；",
            _ => return None,
        }
    };
    Some(value.to_owned())
}

fn emit_smart_quote(state: &mut CoreState, double_quote: bool) -> String {
    let force_left = state
        .send_history
        .last()
        .is_some_and(|text| text == "：" || text == ":");
    let left = if double_quote { state.left_double_quote } else { state.left_single_quote };
    let deleted = if double_quote { state.deleted_double_quote_armed } else { state.deleted_single_quote_armed };
    let emit_left = if force_left && !deleted { true } else { left };
    if double_quote {
        state.left_double_quote = !emit_left;
        state.deleted_double_quote_armed = false;
        if emit_left { "“" } else { "”" }.to_owned()
    } else {
        state.left_single_quote = !emit_left;
        state.deleted_single_quote_armed = false;
        if emit_left { "‘" } else { "’" }.to_owned()
    }
}

fn trim_segmented(segmented: &str, raw_prefix: usize) -> String {
    if segmented.is_empty() || raw_prefix == 0 {
        return segmented.to_owned();
    }
    let mut raw_count = 0usize;
    let mut index = 0usize;
    let bytes = segmented.as_bytes();
    while index < bytes.len() && raw_count < raw_prefix {
        if bytes[index] != b' ' {
            raw_count += 1;
        }
        index += 1;
    }
    while index < bytes.len() && bytes[index] == b' ' {
        index += 1;
    }
    segmented[index..].to_owned()
}

fn refresh_sentence(state: &mut CoreState, count_early: bool) -> String {
    let result = decode_sentence_now(state);
    apply_sentence_result(state, result, state.sentence_generation);
    let extra = if count_early {
        let result = state.sentence_last_result.clone();
        let neural_top = sentence_neural_top_for_result(state, &result);
        state.early.try_commit(
            &result,
            state.config.early_commit,
            &state.input_buffer,
            neural_top.as_deref(),
        )
    } else {
        if !state.config.early_commit {
            state.early.reset_evidence();
        }
        String::new()
    };
    if !extra.is_empty() {
        // A partial commit changes the required text prefix and therefore
        // starts a new logical decode/rerank generation, matching C#.
        state.sentence_generation = state.sentence_generation.saturating_add(1);
        let result = decode_sentence_now(state);
        apply_sentence_result(state, result, state.sentence_generation);
    }
    extra
}

fn trim_sentence_result_after_early_commit(state: &mut CoreState, committed_delta: &str) {
    state
        .sentence_last_result
        .candidates
        .retain_mut(|candidate| {
            let Some(suffix) = candidate.text.strip_prefix(committed_delta) else {
                return false;
            };
            if suffix.is_empty() {
                return false;
            }
            candidate.text = suffix.to_owned();
            true
        });
    state.sentence_candidates = state
        .sentence_last_result
        .candidates
        .iter()
        .map(|candidate| candidate.text.clone())
        .collect();
    state.sentence_segmented = state
        .sentence_last_result
        .candidates
        .iter()
        .map(|candidate| candidate.segmented_code.clone())
        .collect();
    state.selected_candidate = 0;
}

fn simple_response(state: &CoreState, seq: i64, success: bool, extra: Value) -> String {
    let mut response: Value = serde_json::from_str(&state_response(state, seq, success)).expect("state response JSON");
    response["success"] = success.into(); response["handled"] = success.into();
    if let Value::Object(values) = extra { for (key, value) in values { response[key] = value; } }
    if let Some(object) = response.as_object_mut() {
        object.remove("cancel_composition");
    }
    response.to_string()
}

fn without_cancel_composition(response: String) -> String {
    let Ok(mut value) = serde_json::from_str::<Value>(&response) else {
        return response;
    };
    if let Some(object) = value.as_object_mut() {
        object.remove("cancel_composition");
    }
    value.to_string()
}

fn query_state_response(state: &CoreState, message: &Value, seq: i64) -> String {
    let mut response = json!({
        "type": "response",
        "seq": seq,
        "success": true,
        "handled": false,
        "input_buffer": overlay_input_and_candidates(state).0,
        "keyboard_open": state.keyboard_open,
        "composition_tracking": sentence_tracking(state),
        "composition_pending": sentence_pending(state),
        "config_version": state.config_version,
        "lexicon_version": state.lexicon_version,
    });
    if string(message, "frontend").eq_ignore_ascii_case("hook_native") {
        response["native_hook_alt_backslash_toggle_enabled"] = state.config.native_hook_alt_backslash.into();
        response["auto_switch_system_layout_enabled"] = state.config.auto_switch_system_layout.into();
        response["use_clipboard_commit"] = state.config.use_clipboard_commit.into();
        response["clipboard_commit_whitelist"] = state.config.clipboard_whitelist.clone().into();
    }
    response.to_string()
}

fn shape_key_response(state: &CoreState, message: &Value, response: &str, before_keyboard: bool) -> String {
    let frontend = string(message, "frontend");
    let mut value: Value = match serde_json::from_str(response) {
        Ok(value) => value,
        Err(_) => return response.to_owned(),
    };
    let hook = frontend.eq_ignore_ascii_case("hook_native");
    let tsf = message.get("tsf_stage").is_some();
    // Production TSF has a fixed 4096-byte response buffer and obtains the
    // candidate list/selection from the Overlay MMF.  Keep the legacy fields
    // for dependency-free callers/tests, but enforce the compact shape for a
    // real TSF stage, the native hook frontend, and oversized responses.
    if hook || tsf || response.len() > 3900 {
        if let Some(object) = value.as_object_mut() {
            object.remove("candidates");
            object.remove("selected_index");
            object.remove("native_hook_alt_backslash_toggle_enabled");
            object.remove("auto_switch_system_layout_enabled");
            object.remove("use_clipboard_commit");
            object.remove("clipboard_commit_whitelist");
            if hook && before_keyboard != state.keyboard_open && state.config.auto_switch_system_layout {
                object.insert("ensure_system_layout_en".to_owned(), Value::Bool(true));
            } else {
                object.remove("ensure_system_layout_en");
            }
        }
    } else if !hook {
        if let Some(object) = value.as_object_mut() {
            object.remove("ensure_system_layout_en");
        }
    }
    value.to_string()
}

fn config_text(state: &CoreState) -> String {
    state.config.text()
}

pub fn overlay_input_and_candidates(state: &CoreState) -> (String, Vec<String>) {
    if state.uppercase_mode {
        return (mask_display_code(state, &state.input_buffer), Vec::new());
    }
    if state.pinyin_mode {
        let code = state.input_buffer.strip_prefix('·').unwrap_or(&state.input_buffer);
        return (mask_display_code(state, &state.input_buffer), pinyin_candidates(state, code));
    }
    if state.config.sentence_active() {
        (
            mask_display_code(state, &sentence_display_code(state)),
            published_sentence_candidates(state),
        )
    } else {
        (
            format!(
                "{}{}",
                state.mixed_prefix,
                mask_display_code(state, &state.input_buffer)
            ),
            normal_candidates(state),
        )
    }
}

pub fn overlay_candidate_annotations(state: &CoreState) -> Vec<String> {
    if state.uppercase_mode || state.config.sentence_active() {
        return Vec::new();
    }
    let pinyin = state.pinyin_mode;
    let code = if pinyin {
        state.input_buffer.strip_prefix('·').unwrap_or(&state.input_buffer)
    } else {
        &state.input_buffer
    };
    let visible_len = if pinyin {
        pinyin_candidates(state, code).len()
    } else {
        normal_candidates(state).len()
    };
    let page_size = state.config.page_size.max(1);
    let page = if state.normal_page_code.eq_ignore_ascii_case(&state.input_buffer) {
        state.normal_page_index
    } else {
        0
    };
    (0..visible_len)
        .map(|index| {
            let absolute = page.saturating_mul(page_size).saturating_add(index);
            if pinyin {
                // Pinyin rows contain the reverse-lookup candidates, but the
                // annotation maps belong to the active code table.  C# uses
                // that same active-table split/full map while displaying a
                // pinyin candidate; consulting the pinyin lexicon itself
                // would usually return an empty annotation.
                let Some(candidate) = state.pinyin_lexicon.ranked(code).get(absolute) else {
                    return String::new();
                };
                let split = state.lexicon.split_code(&candidate.text);
                let full = state.lexicon.full_code(&candidate.text);
                if !split.is_empty() && !full.is_empty() {
                    format!("{split} | {full}")
                } else if !split.is_empty() {
                    split
                } else {
                    full
                }
            } else {
                state.lexicon.annotation_for_candidate(
                    code,
                    absolute,
                    false,
                    state.config.show_comment,
                    state.config.show_split,
                )
            }
        })
        .collect()
}

fn pinyin_candidates(state: &CoreState, code: &str) -> Vec<String> {
    let all = state.pinyin_lexicon.ranked(code);
    let page_size = state.config.page_size.max(1);
    let page_code_matches = state.normal_page_code == state.input_buffer;
    let page = if page_code_matches { state.normal_page_index } else { 0 };
    all.iter()
        .skip(page.saturating_mul(page_size))
        .take(page_size)
        .map(|candidate| candidate.display_text.clone())
        .collect()
}

fn pinyin_candidate_commit(state: &CoreState, code: &str, index: usize) -> Option<String> {
    let all = state.pinyin_lexicon.ranked(code);
    let page_size = state.config.page_size.max(1);
    let page_code_matches = state.normal_page_code == state.input_buffer;
    let page = if page_code_matches { state.normal_page_index } else { 0 };
    all.get(page.saturating_mul(page_size).saturating_add(index))
        .map(|candidate| candidate.text.clone())
}

fn handle_uppercase_key(state: &mut CoreState, seq: i64, vk: u64, shift: bool) -> String {
    if vk == 0x20 || vk == 0x0D {
        let output = uppercase_commit(&state.input_buffer);
        clear_composition(state);
        return commit_response(state, seq, true, output);
    }
    if vk == 0x09 {
        if state.config.tab_clear { clear_composition(state); return state_response(state, seq, true); }
        return state_response(state, seq, false);
    }
    if vk == 0x08 {
        state.input_buffer.pop();
        state.raw_input.pop();
        if state.input_buffer.is_empty() { clear_composition(state); return state_response(state, seq, true); }
        return state_response(state, seq, true);
    }
    if (0x30..=0x39).contains(&vk) {
        let ch = char::from_u32(vk as u32).unwrap_or_default();
        state.input_buffer.push(ch);
        state.raw_input.push(ch);
        return state_response(state, seq, true);
    }
    if (0x60..=0x69).contains(&vk) {
        let ch = char::from_u32((b'0' as u32) + vk as u32 - 0x60).unwrap_or_default();
        state.input_buffer.push(ch);
        state.raw_input.push(ch);
        return state_response(state, seq, true);
    }
    if (0x41..=0x5A).contains(&vk) {
        let ch = char::from_u32(vk as u32).unwrap_or_default();
        let ch = if shift { ch } else { ch.to_ascii_lowercase() };
        state.input_buffer.push(ch);
        state.raw_input.push(ch);
        return state_response(state, seq, true);
    }
    if let Some(symbol) = uppercase_symbol(vk, shift) {
        // ASCII punctuation is part of an uppercase/literal buffer only for
        // timer/currency macros.  Ordinary punctuation commits the literal
        // buffer then emits the symbol, as C# does.
        if matches!(vk, 0xBE | 0xBC) && is_timer_or_currency(&state.input_buffer) {
            state.input_buffer.push_str(&symbol);
            state.raw_input.push_str(&symbol);
            return state_response(state, seq, true);
        }
        let output = format!("{}{}", uppercase_commit(&state.input_buffer), symbol);
        clear_composition(state);
        return commit_response(state, seq, true, output);
    }
    state_response(state, seq, false)
}

/// Symbols typed in CnUpperCase are always the literal US keyboard symbols;
/// they do not follow the Chinese-punctuation preference used by idle and
/// ordinary candidate composition.
fn uppercase_symbol(vk: u64, shift: bool) -> Option<String> {
    let value = if shift {
        match vk {
            0x30 => ")", 0x31 => "!", 0x32 => "@", 0x33 => "#", 0x34 => "$",
            0x35 => "%", 0x36 => "^", 0x37 => "&", 0x38 => "*", 0x39 => "(",
            0xBB => "+", 0xBC => "<", 0xBD => "_", 0xBE => ">", 0xBF => "?",
            0xC0 => "~", 0xDB => "{", 0xDC => "|", 0xDD => "}", 0xBA => ":",
            0xDE => "\"",
            _ => return None,
        }
    } else {
        match vk {
            0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
            0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xBA => ";",
            0xDE => "'",
            _ => return None,
        }
    };
    Some(value.to_owned())
}

fn uppercase_commit(value: &str) -> String {
    if is_timer_code(value) {
        // The Windows C# implementation schedules a desktop reminder and
        // emits no text.  Keep the no-text contract in the cross-platform
        // core; the optional GUI reminder is deliberately outside Rust's
        // protocol process.
        return String::new();
    }
    if is_timer_or_currency(value) && value.starts_with('S') {
        return chinese_currency(value);
    }
    // The timer side effect is intentionally omitted in the cross-platform
    // core; preserving the literal code is the observable commit fallback.
    value.to_owned()
}

fn is_timer_code(value: &str) -> bool {
    let mut chars = value.chars();
    if !matches!(chars.next(), Some('D' | 'd')) {
        return false;
    }
    if !matches!(chars.next(), Some('S' | 's')) {
        return false;
    }
    let rest: String = chars.collect();
    !rest.is_empty()
        && rest.chars().all(|ch| ch.is_ascii_digit() || ch == ',' || ch == '.')
        && rest.replace(',', "").parse::<f64>().is_ok()
        && rest.replace(',', "").parse::<f64>().unwrap_or(0.0) > 0.0
}

fn is_timer_or_currency(value: &str) -> bool {
    let Some(rest) = value.get(1..) else { return false; };
    (value.starts_with('D') || value.starts_with('d') || value.starts_with('S'))
        && !rest.is_empty()
        && rest.chars().all(|ch| ch.is_ascii_digit() || ch == ',' || ch == '.')
}

fn chinese_currency(value: &str) -> String {
    let mut number = value.get(1..).unwrap_or_default().replace(',', "");
    if number.is_empty() || number.starts_with('-') || number.matches('.').count() > 1 {
        return "数字格式错误!".to_owned();
    }
    if !number.chars().all(|ch| ch.is_ascii_digit() || ch == '.') {
        return "数字格式错误!".to_owned();
    }
    if number.starts_with('.') {
        number.insert(0, '0');
    }
    let mut pieces = number.splitn(2, '.');
    let integer_text = pieces.next().unwrap_or("0");
    let fraction_text = pieces.next().unwrap_or("");
    let Ok(integer) = integer_text.parse::<u128>() else {
        return "数字格式错误!".to_owned();
    };
    if fraction_text.len() > 28 {
        return "数字格式错误!".to_owned();
    }
    let mut fraction_digits = fraction_text.bytes().map(|byte| (byte - b'0') as u8);
    let jiao = fraction_digits.next().unwrap_or(0);
    let mut fen = fraction_digits.next().unwrap_or(0);
    // Decimal.ToString rounds to the two displayed fractional places.  Carry
    // a third digit into the integer part rather than silently truncating it.
    if fraction_digits.next().is_some_and(|digit| digit >= 5) {
        fen = fen.saturating_add(1);
    }
    let mut integer = integer;
    let mut jiao = jiao;
    if fen >= 10 {
        fen = 0;
        jiao = jiao.saturating_add(1);
    }
    if jiao >= 10 {
        jiao = 0;
        integer = integer.saturating_add(1);
    }

    let mut output = String::new();
    if integer > 0 {
        output.push_str(&rmb_integer(integer));
        output.push('元');
    }
    if jiao == 0 && fen == 0 {
        if output.is_empty() { output.push('零'); }
        output.push('整');
        return output;
    }
    if jiao > 0 {
        if integer == 0 && output.is_empty() {
            output.push_str(&rmb_digit(jiao));
        } else {
            output.push_str(&rmb_digit(jiao));
        }
        output.push('角');
    } else if integer > 0 {
        output.push('零');
    }
    if fen > 0 {
        output.push_str(&rmb_digit(fen));
        output.push('分');
    }
    output
}

fn rmb_digit(digit: u8) -> &'static str {
    ["零", "壹", "贰", "叁", "肆", "伍", "陆", "柒", "捌", "玖"]
        .get(digit as usize)
        .copied()
        .unwrap_or("零")
}

fn rmb_integer(mut value: u128) -> String {
    let mut groups = Vec::new();
    while value > 0 {
        groups.push((value % 10_000) as u16);
        value /= 10_000;
    }
    let group_units = ["", "万", "亿", "兆", "京", "垓", "秭", "穰"];
    let mut output = String::new();
    let mut pending_zero = false;
    for index in (0..groups.len()).rev() {
        let group = groups[index];
        if group == 0 {
            if !output.is_empty() { pending_zero = true; }
            continue;
        }
        if !output.is_empty() && (pending_zero || group < 1000) {
            if !output.ends_with('零') { output.push('零'); }
        }
        output.push_str(&rmb_group(group));
        if let Some(unit) = group_units.get(index) {
            output.push_str(unit);
        }
        pending_zero = false;
    }
    output
}

fn rmb_group(value: u16) -> String {
    let units = ["仟", "佰", "拾", ""];
    let mut output = String::new();
    let mut pending_zero = false;
    for (index, unit) in units.iter().enumerate() {
        let divisor = [1000, 100, 10, 1][index];
        let digit = (value / divisor) % 10;
        if digit == 0 {
            if !output.is_empty() { pending_zero = true; }
            continue;
        }
        if pending_zero { output.push('零'); }
        output.push_str(rmb_digit(digit as u8));
        output.push_str(unit);
        pending_zero = false;
    }
    output
}

fn handle_pinyin_navigation(state: &mut CoreState, seq: i64, vk: u64, shift: bool) -> String {
    if vk == 0x09 && state.config.tab_clear {
        clear_composition(state);
        return state_response(state, seq, true);
    }
    if let Some(delta) = normal_page_key(state, vk, shift) {
        let code = state.input_buffer.strip_prefix('·').unwrap_or(&state.input_buffer).to_owned();
        let count = state.pinyin_lexicon.ranked(&code).len();
        if count > 0 {
            let pages = (count + state.config.page_size.max(1) - 1) / state.config.page_size.max(1);
            state.normal_page_index = (state.normal_page_index as i32 + delta).clamp(0, pages.saturating_sub(1) as i32) as usize;
            state.normal_page_code = state.input_buffer.clone();
        }
        return state_response(state, seq, true);
    }
    state_response(state, seq, false)
}

fn handle_pinyin_key(state: &mut CoreState, seq: i64, vk: u64, shift: bool) -> String {
    let code = state.input_buffer.strip_prefix('·').unwrap_or(&state.input_buffer).to_owned();
    let candidates = pinyin_candidates(state, &code);
    if vk == 0x20 {
        let output = pinyin_candidate_commit(state, &code, 0).unwrap_or_default();
        clear_composition(state);
        return if output.is_empty() { state_response(state, seq, true) } else { commit_response(state, seq, true, output) };
    }
    if vk == 0xBA && !shift && state.config.semicolon_second && candidates.len() >= 2 {
        let output = pinyin_candidate_commit(state, &code, 1).unwrap_or_default();
        clear_composition(state);
        return if output.is_empty() { state_response(state, seq, true) } else { commit_response(state, seq, true, output) };
    }
    if vk == 0xDE && !shift && state.config.quote_third && candidates.len() >= 3 {
        let output = pinyin_candidate_commit(state, &code, 2).unwrap_or_default();
        clear_composition(state);
        return if output.is_empty() { state_response(state, seq, true) } else { commit_response(state, seq, true, output) };
    }
    if (0x31..=0x39).contains(&vk) || vk == 0x30 {
        let rank = if vk == 0x30 { 9 } else { (vk - 0x31) as usize };
        let mut output = pinyin_candidate_commit(state, &code, 0).unwrap_or_default();
        if rank < candidates.len() {
            output = pinyin_candidate_commit(state, &code, rank).unwrap_or(output);
        }
        else if !output.is_empty() { output.push(char::from_u32(vk as u32).unwrap_or('0')); }
        clear_composition(state);
        return if output.is_empty() { state_response(state, seq, true) } else { commit_response(state, seq, true, output) };
    }
    if let Some(symbol) = punctuation_output(state, vk, shift) {
        let output = format!("{}{}", pinyin_candidate_commit(state, &code, 0).unwrap_or_default(), symbol);
        clear_composition(state);
        return commit_response(state, seq, true, output);
    }
    state_response(state, seq, false)
}

fn normal_candidates(state: &CoreState) -> Vec<String> {
    let all = state.lexicon.ranked(&state.input_buffer);
    if all.is_empty() { return Vec::new(); }
    let page_size = state.config.page_size.max(1);
    let page_code_matches = state.normal_page_code.eq_ignore_ascii_case(&state.input_buffer);
    let page = if page_code_matches { state.normal_page_index } else { 0 };
    all.iter()
        .skip(page.saturating_mul(page_size))
        .take(page_size)
        .map(|candidate| candidate.display_text.clone())
        .collect()
}

fn normal_candidate_commit(state: &CoreState, index: usize) -> Option<String> {
    let all = state.lexicon.ranked(&state.input_buffer);
    let page_size = state.config.page_size.max(1);
    let page_code_matches = state.normal_page_code.eq_ignore_ascii_case(&state.input_buffer);
    let page = if page_code_matches { state.normal_page_index } else { 0 };
    all.get(page.saturating_mul(page_size).saturating_add(index))
        .map(|candidate| candidate.text.clone())
}

fn normal_page_key(state: &CoreState, vk: u64, shift: bool) -> Option<i32> {
    match state.config.page_keys.trim() {
        "[ ]" => match (vk, shift) {
            (0xDB, false) => Some(-1),
            (0xDD, false) => Some(1),
            _ => None,
        },
        "Shift Tab/Tab" => match vk {
            0x09 if shift => Some(-1),
            0x09 if !shift => Some(1),
            _ => None,
        },
        "PageUp/PageDown" => match (vk, shift) {
            (0x21, false) => Some(-1),
            (0x22, false) => Some(1),
            _ => None,
        },
        _ => match (vk, shift) {
            (0xBD, false) => Some(-1),
            (0xBB, false) => Some(1),
            _ => None,
        },
    }
}

fn move_normal_page(state: &mut CoreState, delta: i32) {
    let code = state.input_buffer.clone();
    if state.normal_page_code != code {
        state.normal_page_code = code.clone();
        state.normal_page_index = 0;
    }
    let count = state.lexicon.candidates(&code).len();
    if count == 0 { state.normal_page_index = 0; return; }
    let pages = (count + state.config.page_size.max(1) - 1) / state.config.page_size.max(1);
    let page = state.normal_page_index as i32 + delta;
    state.normal_page_index = page.clamp(0, pages.saturating_sub(1) as i32) as usize;
}

fn state_response(state: &CoreState, seq: i64, handled: bool) -> String {
    let (display_buffer, candidates) = overlay_input_and_candidates(state);
    let cancel = state.cancel_composition_pending.get();
    json!({
        "type": "response",
        "seq": seq,
        "success": true,
        "handled": handled,
        "input_buffer": display_buffer,
        "candidates": candidates,
        "selected_index": state.selected_candidate,
        "keyboard_open": state.keyboard_open,
        "composition_tracking": sentence_tracking(state),
        "composition_pending": sentence_pending(state),
        "cancel_composition": cancel,
        "ensure_system_layout_en": state.config.auto_switch_system_layout,
        "native_hook_alt_backslash_toggle_enabled": state.config.native_hook_alt_backslash,
        "auto_switch_system_layout_enabled": state.config.auto_switch_system_layout,
        "use_clipboard_commit": state.config.use_clipboard_commit,
        "clipboard_commit_whitelist": state.config.clipboard_whitelist,
        "config_version": state.config_version,
        "lexicon_version": state.lexicon_version,
    })
    .to_string()
}

fn sentence_tracking(state: &CoreState) -> bool {
    state.config.sentence_active() && !state.input_buffer.is_empty()
}

fn sentence_pending(state: &CoreState) -> bool {
    if !sentence_tracking(state) {
        return false;
    }
    let running = state
        .sentence_worker
        .lock()
        .ok()
        .is_some_and(|control| control.running);
    running
        || state.sentence_decoded_raw != state.input_buffer
        || state.sentence_decoded_lexicon_version != state.lexicon_version
}

fn mask_display_code(state: &CoreState, input: &str) -> String {
    let mask = &state.config.code_masking;
    if mask.is_empty() || input.is_empty() {
        return input.to_owned();
    }
    let lut: Vec<char> = mask.chars().collect();
    if lut.is_empty() {
        return input.to_owned();
    }
    const CHAR_LUT: &str = "abcdefghijklmnopqrstuvwxyz;";
    input
        .chars()
        .map(|ch| {
            if ch.is_whitespace() {
                return ch;
            }
            let pos = CHAR_LUT.find(ch.to_ascii_lowercase()).unwrap_or(0) % lut.len();
            lut[pos]
        })
        .collect()
}

fn handle_shift(
    state: &mut CoreState,
    seq: i64,
    vk: u64,
    action: &str,
    ctrl: bool,
    alt: bool,
    win: bool,
) -> String {
    let right = vk == 0xA1;
    let is_down = action.eq_ignore_ascii_case("down") || action.eq_ignore_ascii_case("key_down");
    let is_up = action.eq_ignore_ascii_case("up") || action.eq_ignore_ascii_case("key_up");
    if is_down {
        if right {
            state.shift_right_down = true;
        } else {
            state.shift_left_down = true;
        }
        return state_response(state, seq, false);
    }
    if !is_up {
        return state_response(state, seq, false);
    }
    let had = if right {
        let value = state.shift_right_down;
        state.shift_right_down = false;
        value
    } else {
        let value = state.shift_left_down;
        state.shift_left_down = false;
        value
    };
    let can_toggle = had
        && state.config.shift_toggle
        && !state.shift_chord_used
        && !ctrl
        && !alt
        && !win;
    let can_toggle = can_toggle && !state.skip_shift_toggle_once;
    if !state.shift_left_down && !state.shift_right_down {
        state.shift_chord_used = false;
        state.skip_shift_toggle_once = false;
    }
    if can_toggle {
        state.keyboard_open = !state.keyboard_open;
        let commit_text = composition_raw_commit(state);
        clear_composition(state);
        return if commit_text.is_empty() {
            state_response(state, seq, true)
        } else {
            commit_response(state, seq, true, commit_text)
        };
    }
    state_response(state, seq, false)
}

fn handle_ctrl_hotkey(state: &mut CoreState, seq: i64, vk: u64) -> Option<String> {
    if vk == 0xBB && state.config.ctrl_equal_add_ci {
        if state.one_shot_vk == vk {
            return Some(state_response(state, seq, true));
        }
        state.one_shot_vk = vk;
        return Some(launch_ui(state, seq, "TigerClaw.Dialog.exe", "--addci"));
    }
    if vk == 0x4D && state.config.ctrl_m_switch_schema {
        if state.one_shot_vk == vk {
            return Some(state_response(state, seq, true));
        }
        if switch_recent_schema(state) {
            state.one_shot_vk = vk;
            return Some(state_response(state, seq, true));
        }
        return Some(state_response(state, seq, false));
    }
    None
}

fn schema_list(state: &CoreState) -> Vec<String> {
    let Some(root) = schema_root(state) else {
        return Vec::new();
    };
    let mut names: Vec<String> = std::fs::read_dir(root)
        .ok()
        .into_iter()
        .flatten()
        .filter_map(|entry| entry.ok())
        .filter(|entry| entry.path().is_dir())
        .filter_map(|entry| entry.file_name().into_string().ok())
        .collect();
    names.sort_by(|left, right| left.to_ascii_lowercase().cmp(&right.to_ascii_lowercase()));
    names
}

fn schema_root(state: &CoreState) -> Option<std::path::PathBuf> {
    let configured = state.config.code_root.trim();
    let path = if configured.is_empty() {
        return None;
    } else if std::path::Path::new(configured).is_absolute() {
        std::path::PathBuf::from(configured)
    } else {
        std::path::Path::new(state.base_dir.as_deref().unwrap_or("."))
            .join(configured)
    };
    if path.is_dir() {
        Some(path)
    } else {
        None
    }
}

fn schema_dir(state: &CoreState) -> Option<std::path::PathBuf> {
    let root = schema_root(state)?;
    let mut dirs: Vec<_> = std::fs::read_dir(&root)
        .ok()?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .collect();
    if dirs.is_empty() {
        return None;
    }
    if !state.config.current_schema.is_empty() {
        if let Some(matched) = dirs.iter().find(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.eq_ignore_ascii_case(&state.config.current_schema))
        }) {
            return Some(matched.clone());
        }
    }
    dirs.sort_by(|left, right| {
        left.file_name()
            .cmp(&right.file_name())
    });
    dirs.into_iter().next()
}

fn reload_schema_lexicon(state: &mut CoreState) -> bool {
    let Some(dir) = schema_dir(state) else {
        return false;
    };
    let Ok(lexicon) = crate::lexicon::Lexicon::load_directory(&dir) else {
        return false;
    };
    if let Some(name) = dir.file_name().and_then(|name| name.to_str()) {
        let _ = state.config.set("当前码表", name);
        state.config.record_recent_schema(name);
        // Keep the schema directory as the reload target.  Storing only
        // 常用字词.txt causes a later pathless reload_mb to discard all
        // secondary table files.
        state.lexicon_path = Some(dir.to_string_lossy().into_owned());
    }
    state.lexicon = lexicon;
    state.lexicon_version = state.lexicon_version.saturating_add(1);
    reload_pinyin_lexicon(state);
    state.reload_supplements();
    true
}

fn switch_recent_schema(state: &mut CoreState) -> bool {
    let schemas = schema_list(state);
    if schemas.len() < 2 {
        return false;
    }
    let current = state.config.current_schema.clone();
    let mut target = state
        .config
        .recent_schemas
        .iter()
        .find(|name| !name.eq_ignore_ascii_case(&current) && schemas.iter().any(|item| item.eq_ignore_ascii_case(name)))
        .cloned();
    if target.is_none() {
        let idx = schemas
            .iter()
            .position(|name| name.eq_ignore_ascii_case(&current))
            .unwrap_or(0);
        target = Some(schemas[(idx + 1) % schemas.len()].clone());
    }
    let Some(target) = target else {
        return false;
    };
    let _ = state.config.set("当前码表", &target);
    reload_schema_lexicon(state)
}

fn open_target(state: &CoreState, seq: i64, target: &str) -> String {
    let ok = std::process::Command::new("cmd")
        .args(["/C", "start", "", target])
        .spawn()
        .is_ok()
        || std::process::Command::new("xdg-open").arg(target).spawn().is_ok();
    simple_response(state, seq, ok, json!({}))
}

fn open_mb_folder(state: &CoreState, seq: i64) -> String {
    let path = schema_dir(state)
        .or_else(|| schema_root(state))
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_default();
    if path.is_empty() {
        return simple_response(state, seq, false, json!({}));
    }
    let ok = std::process::Command::new("cmd")
        .args(["/C", "start", "", &path])
        .spawn()
        .is_ok()
        || std::process::Command::new("xdg-open").arg(&path).spawn().is_ok();
    simple_response(state, seq, ok, json!({"path": path}))
}

fn export_mb(state: &CoreState, seq: i64) -> String {
    let base = state.base_dir.as_deref().map(Path::new).unwrap_or_else(|| Path::new("."));
    let schema = if state.config.current_schema.trim().is_empty() { "export" } else { state.config.current_schema.trim() };
    let safe: String = schema.chars().filter(|ch| !"<>:\"/\\|?*".contains(*ch)).collect();
    let safe = if safe.is_empty() { "export" } else { &safe };
    let dest_dir = base.join("码表导出");
    let timestamp = local_export_timestamp();
    let dest = dest_dir.join(format!("{safe} {timestamp}.txt"));
    let mut bytes = vec![0xEF, 0xBB, 0xBF];
    bytes.extend_from_slice(state.lexicon.export_csharp().as_bytes());
    let result = std::fs::create_dir_all(&dest_dir).and_then(|_| std::fs::write(&dest, bytes));
    let mut ok = result.is_ok();
    let mut extra = json!({"path": dest.to_string_lossy()});
    if let Err(error) = result {
        extra["error"] = Value::String(error.to_string());
    } else if let Err(error) = open_exported_file(&dest) {
        // Writing the export is not enough for the Dialog/Overlay command:
        // C# also opens Explorer and selects the generated file.  Report a
        // launch failure explicitly so the caller never sees a false success.
        ok = false;
        extra["error"] = Value::String(error);
    }
    simple_response(state, seq, ok, extra)
}

#[cfg(windows)]
fn open_exported_file(path: &Path) -> Result<(), String> {
    let argument = format!(r#"/select,"{}""#, path.to_string_lossy());
    std::process::Command::new("explorer.exe")
        .arg(argument)
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("failed to open Explorer: {error}"))
}

#[cfg(not(windows))]
fn open_exported_file(_path: &Path) -> Result<(), String> {
    Ok(())
}

fn local_export_timestamp() -> String {
    #[cfg(windows)]
    {
        let mut system = std::mem::MaybeUninit::<windows_sys::Win32::Foundation::SYSTEMTIME>::zeroed();
        unsafe {
            windows_sys::Win32::System::SystemInformation::GetLocalTime(system.as_mut_ptr());
            let system = system.assume_init();
            format!("{:04}{:02}{:02}-{:02}{:02}", system.wYear, system.wMonth, system.wDay, system.wHour, system.wMinute)
        }
    }
    #[cfg(not(windows))]
    {
        // Non-Windows tests do not need local-time fidelity; a stable UTC-shaped
        // filename still follows C#'s yyyyMMdd-HHmm contract.
        let seconds = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_secs())
            .unwrap_or(0);
        unix_timestamp_utc(seconds)
    }
}

#[cfg(not(windows))]
fn unix_timestamp_utc(seconds: u64) -> String {
    // Howard Hinnant's civil-from-days calculation, kept dependency-free for
    // the stdio build used by Linux differential tests.
    let days = (seconds / 86_400) as i64;
    let day_seconds = seconds % 86_400;
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = mp + if mp < 10 { 3 } else { -9 };
    let y = y + if m <= 2 { 1 } else { 0 };
    let hour = day_seconds / 3_600;
    let minute = (day_seconds % 3_600) / 60;
    format!("{:04}{:02}{:02}-{:02}{:02}", y, m, d, hour, minute)
}

fn persist_config_result(state: &mut CoreState) -> Result<(), String> {
    if state.config_path.is_none() {
        if let Some(base) = state.base_dir.as_deref() {
            state.config_path = Some(std::path::Path::new(base).join("config.txt").to_string_lossy().into_owned());
        }
    }
    let Some(path) = state.config_path.as_deref() else { return Ok(()); };
    state.config.write_to(path).map_err(|error| error.to_string())
}

fn pump_sentence(state: &mut CoreState) {
    let finished = state
        .sentence_completed
        .lock()
        .ok()
        .and_then(|mut completed| completed.take());
    if let Some(done) = finished {
        if done.generation == state.sentence_generation
            && done.lexicon_version == state.lexicon_version
            && done.result.raw == state.input_buffer
        {
            apply_sentence_result(state, done.result, done.generation);
        }
    }
    let reranked = state
        .sentence_rerank_completed
        .lock()
        .ok()
        .and_then(|mut completed| completed.take());
    if let Some(done) = reranked {
        if done.generation == state.sentence_generation
            && done.raw_code == state.input_buffer
            && done.committed_prefix == state.early.committed_text
            && done.texts.len() <= state.sentence_candidates.len()
        {
            let top_text = done.texts.first().cloned();
            for (index, text) in done.texts.iter().enumerate() {
                if let Some(position) = state
                    .sentence_candidates
                    .iter()
                    .position(|item| item == text)
                {
                    state.sentence_candidates.swap(index, position);
                    if index < state.sentence_segmented.len()
                        && position < state.sentence_segmented.len()
                    {
                        state.sentence_segmented.swap(index, position);
                    }
                }
            }
            // Keep the generation's n-gram candidate metadata in the same
            // visible order.  The score itself remains the n-gram base score;
            // the accepted neural top is recorded separately for early
            // commit's proposal constraint.
            let old_candidates = state.sentence_last_result.candidates.clone();
            state.sentence_last_result.candidates = state
                .sentence_candidates
                .iter()
                .filter_map(|text| old_candidates.iter().find(|item| {
                    let visible = item
                        .text
                        .strip_prefix(&done.committed_prefix)
                        .unwrap_or(&item.text);
                    visible == text
                }))
                .cloned()
                .collect();
            state.selected_candidate = 0;
            state.sentence_neural_accepted_raw = done.raw_code;
            state.sentence_neural_top_text = top_text
                .map(|text| format!("{}{}", done.committed_prefix, text))
                .unwrap_or_default();
        }
    }
}

fn ensure_sentence_current(state: &mut CoreState) {
    pump_sentence(state);
    if state.sentence_decoded_raw == state.input_buffer
        && state.sentence_decoded_lexicon_version == state.lexicon_version
    {
        return;
    }
    state.sentence_generation = state.sentence_generation.saturating_add(1);
    refresh_sentence(state, false);
}

fn decode_sentence_now(state: &mut CoreState) -> DecodeResult {
    let beam = if state.sentence_model.is_some() { 2000 } else { 100 };
    let mut decoder = state.sentence_decoder.lock().expect("decoder");
    decoder.set_beam_width(beam);
    decoder.decode(
        &state.input_buffer,
        &state.lexicon,
        state.sentence_model.as_deref(),
        &state.ranks,
        &state.supplements,
        20,
        state.config.early_commit,
        &state.early.committed_text,
    )
}

fn apply_sentence_result(state: &mut CoreState, result: DecodeResult, generation: u64) {
    state.sentence_last_result = result.clone();
    state.sentence_decoded_raw = result.raw.clone();
    state.sentence_decoded_lexicon_version = state.lexicon_version;
    state.sentence_generation = state.sentence_generation.max(generation);
    state.sentence_candidates = result.candidates.iter().map(|item| item.text.clone()).collect();
    state.sentence_segmented = result
        .candidates
        .iter()
        // Keep the decoder's full segmented raw code, as C# does. Display is
        // the single authority that removes the already committed raw prefix.
        // Pre-trimming here and trimming again in sentence_display_code turned
        // `jae fm jxj` into `xj` after `jae` committed instead of `fm jxj`.
        .map(|item| item.segmented_code.clone())
        .collect();
    request_sentence_rerank(state, generation);
}

fn sentence_neural_top_for_result(state: &CoreState, result: &DecodeResult) -> Option<String> {
    if result.early.ignore_neural_constraint
        || state.sentence_neural_accepted_raw != result.raw
        || state.sentence_neural_top_text.is_empty()
    {
        None
    } else {
        Some(state.sentence_neural_top_text.clone())
    }
}

fn request_sentence_worker(state: &CoreState) {
    let job = SentenceJob {
        generation: state.sentence_generation,
        raw: state.input_buffer.clone(),
        prefix: state.early.committed_text.clone(),
        include_early: state.config.early_commit,
        lexicon_version: state.lexicon_version,
    };
    let should_spawn = {
        let Ok(mut control) = state.sentence_worker.lock() else {
            return;
        };
        control.job = Some(job);
        if control.running {
            false
        } else {
            control.running = true;
            true
        }
    };
    if !should_spawn {
        return;
    }
    let decoder = Arc::clone(&state.sentence_decoder);
    let lexicon = Arc::clone(&state.shared_lexicon);
    let model = state.sentence_model.clone();
    let ranks = Arc::clone(&state.shared_ranks);
    let supplements = Arc::clone(&state.shared_supplements);
    let worker = Arc::clone(&state.sentence_worker);
    let completed = Arc::clone(&state.sentence_completed);
    thread::spawn(move || loop {
        let snapshot = {
            let Ok(control) = worker.lock() else {
                return;
            };
            control.job.clone()
        };
        let Some(snapshot) = snapshot else {
            if let Ok(mut control) = worker.lock() {
                control.running = false;
            }
            return;
        };
        let result = {
            let Ok(mut decoder) = decoder.lock() else {
                break;
            };
            decoder.set_beam_width(if model.is_some() { 2000 } else { 100 });
            decoder.decode(
                &snapshot.raw,
                lexicon.as_ref(),
                model.as_deref(),
                ranks.as_ref(),
                supplements.as_ref(),
                20,
                snapshot.include_early,
                &snapshot.prefix,
            )
        };
        if let Ok(mut slot) = completed.lock() {
            *slot = Some(CompletedSentence {
                generation: snapshot.generation,
                lexicon_version: snapshot.lexicon_version,
                result,
            });
        }
        let latest = worker.lock().ok().and_then(|control| {
            control.job.as_ref().map(|job| job.generation)
        });
        if latest == Some(snapshot.generation) {
            if let Ok(mut control) = worker.lock() {
                control.running = false;
            }
            return;
        }
    });
}

/// Blend each candidate's neural score into its base n-gram score, then
/// return the resulting display order. Mirrors C#'s
/// `ApplySentenceNeuralScores` + `CompareByLexiconRankThenScore`: rank still
/// takes priority over the blended score, so a later-rank candidate (e.g. an
/// alternate/legacy code for a common character) cannot leapfrog the code's
/// first-choice entry just because the raw n-gram or Qwen favors it. A pure
/// score sort would silently drop that ordering rule for the reranked top-5.
fn rerank_order(bases: &[f64], ranks: &[i32], scores: &[f64], texts: &[String]) -> Vec<usize> {
    let mut ranked: Vec<(i32, f64, usize)> = bases
        .iter()
        .zip(ranks.iter())
        .zip(scores.iter())
        .enumerate()
        .map(|(index, ((base, rank), score))| (*rank, base + 0.84 * score, index))
        .collect();
    ranked.sort_by(|left, right| {
        left.0.cmp(&right.0).then_with(|| {
            right
                .1
                .partial_cmp(&left.1)
                .unwrap_or(std::cmp::Ordering::Equal)
        }).then_with(|| texts[left.2].cmp(&texts[right.2]))
    });
    ranked.into_iter().map(|(_, _, index)| index).collect()
}

fn request_sentence_rerank(state: &mut CoreState, generation: u64) {
    if !state.config.neural_rerank {
        return;
    }
    let Some(exe) = state.sentence_exe.clone() else {
        return;
    };
    let Some(model) = state.qwen_model.clone() else {
        return;
    };
    let count = state.sentence_candidates.len().min(5);
    if count == 0 {
        return;
    }
    let display_texts: Vec<String> = state.sentence_last_result.candidates[..count]
        .iter()
        .map(|item| item.text.clone())
        .collect();
    // The decoder strips `committed_text` from visible candidates, but C#
    // sends the full candidate to Qwen so neural scores remain conditioned on
    // exactly the same text that produced the n-gram result.
    let texts: Vec<String> = display_texts
        .iter()
        .map(|text| format!("{}{}", state.early.committed_text, text))
        .collect();
    let bases: Vec<f64> = state.sentence_last_result.candidates[..count]
        .iter()
        .map(|item| item.score)
        .collect();
    let ranks: Vec<i32> = state.sentence_last_result.candidates[..count]
        .iter()
        .map(|item| item.max_lexicon_rank)
        .collect();
    let raw = state.sentence_decoded_raw.clone();
    let job = SentenceRerankJob {
        generation,
        raw_code: raw,
        committed_prefix: state.early.committed_text.clone(),
        texts,
        display_texts,
        bases,
        ranks,
        exe,
        model,
    };
    let should_spawn = {
        let Ok(mut worker) = state.sentence_rerank_worker.lock() else {
            return;
        };
        // Overwrite, rather than append to, the pending request.  The key
        // path may produce dozens of generations while one model request is
        // running; C# keeps only the newest one in this exact situation.
        worker.job = Some(job);
        if worker.running {
            false
        } else {
            worker.running = true;
            true
        }
    };
    if !should_spawn {
        return;
    }

    let worker = Arc::clone(&state.sentence_rerank_worker);
    let completed = Arc::clone(&state.sentence_rerank_completed);
    let owned_children = Arc::clone(&state.owned_children);
    thread::spawn(move || loop {
        let snapshot = {
            let Ok(control) = worker.lock() else {
                return;
            };
            control.job.clone()
        };
        let Some(snapshot) = snapshot else {
            if let Ok(mut control) = worker.lock() {
                control.running = false;
            }
            return;
        };

        let scores = crate::qwen::rerank(
            &snapshot.exe,
            &snapshot.model,
            &snapshot.raw_code,
            &snapshot.texts,
            snapshot.generation,
            &owned_children,
        );
        if let Some(scores) = scores.filter(|scores| scores.len() == snapshot.texts.len()) {
            let order = rerank_order(&snapshot.bases, &snapshot.ranks, &scores, &snapshot.texts);
            let texts = order
                .into_iter()
                .map(|index| snapshot.display_texts[index].clone())
                .collect();
            if let Ok(mut slot) = completed.lock() {
                *slot = Some(CompletedRerank {
                    generation: snapshot.generation,
                    raw_code: snapshot.raw_code.clone(),
                    committed_prefix: snapshot.committed_prefix.clone(),
                    texts,
                });
            }
        }

        let latest = worker.lock().ok().and_then(|control| {
            control.job.as_ref().map(|job| job.generation)
        });
        if latest == Some(snapshot.generation) {
            if let Ok(mut control) = worker.lock() {
                control.running = false;
            }
            return;
        }
        // A newer request replaced the pending slot while this call was in
        // flight.  Loop immediately and process only that latest snapshot.
    });
}

fn sentence_display_code(state: &CoreState) -> String {
    let full = &state.input_buffer;
    let committed = state.early.committed_raw_length;
    let live = if committed > 0 && committed <= full.len() {
        &full[committed..]
    } else {
        full.as_str()
    };
    let segmented = state
        .sentence_segmented
        .get(state.selected_candidate)
        .cloned()
        .filter(|text| !text.is_empty())
        .unwrap_or_default();
    if segmented.is_empty() {
        return live.to_owned();
    }
    let decoded = &state.sentence_decoded_raw;
    let full_display = if decoded == full {
        segmented
    } else if full.starts_with(decoded.as_str()) && !decoded.is_empty() {
        format!("{}{}", segmented, &full[decoded.len()..])
    } else if decoded.starts_with(full.as_str()) && !full.is_empty() {
        trim_segmented_to_raw_prefix(&segmented, full)
    } else {
        return live.to_owned();
    };
    if committed > 0 {
        let trimmed = trim_segmented(&full_display, committed);
        if trimmed.is_empty() && !live.is_empty() {
            live.to_owned()
        } else {
            trimmed
        }
    } else {
        full_display
    }
}

fn published_sentence_candidates(state: &CoreState) -> Vec<String> {
    if state.sentence_decoded_raw == state.input_buffer {
        return state
            .sentence_candidates
            .iter()
            .take(state.config.page_size)
            .cloned()
            .collect();
    }
    if !state.input_buffer.is_empty() && !state.sentence_candidates.is_empty() {
        return state
            .sentence_candidates
            .iter()
            .take(state.config.page_size)
            .cloned()
            .collect();
    }
    Vec::new()
}

fn trim_segmented_to_raw_prefix(segmented: &str, raw_prefix: &str) -> String {
    if segmented.is_empty() || raw_prefix.is_empty() {
        return raw_prefix.to_owned();
    }
    let bytes = segmented.as_bytes();
    let prefix = raw_prefix.as_bytes();
    let mut kept = 0usize;
    let mut raw_count = 0usize;
    let mut index = 0usize;
    while index < bytes.len() && raw_count < prefix.len() {
        if bytes[index] == b' ' {
            kept = index + 1;
            index += 1;
            continue;
        }
        if bytes[index] != prefix[raw_count] {
            return raw_prefix.to_owned();
        }
        raw_count += 1;
        kept = index + 1;
        index += 1;
    }
    if raw_count == prefix.len() {
        segmented[..kept].to_owned()
    } else {
        raw_prefix.to_owned()
    }
}

fn boolean(message: &Value, name: &str) -> bool {
    message.get(name).and_then(Value::as_bool).unwrap_or(false)
}

fn integer(message: &Value, name: &str) -> i32 {
    message.get(name).and_then(Value::as_i64).unwrap_or(0) as i32
}

fn string<'a>(message: &'a Value, name: &str) -> &'a str {
    message.get(name).and_then(Value::as_str).unwrap_or("")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rerank_order_keeps_first_choice_rank_ahead_of_a_higher_scoring_later_rank() {
        // Reproduces the observed bug: a rank-1 candidate ("是") with a much
        // lower base/neural score than a rank-2 alternate-code candidate
        // ("自己"/"题") must still sort first, matching
        // SentenceCandidate.CompareByLexiconRankThenScore in C#.
        let bases = [-13.7841, -5.3842];
        let ranks = [1, 2];
        let scores = [0.0, 0.0];
        let texts = vec!["是".to_owned(), "题".to_owned()];
        let order = rerank_order(&bases, &ranks, &scores, &texts);
        assert_eq!(order, vec![0, 1]);

        // Even a strong neural boost for the rank-2 candidate must not let
        // it leapfrog rank-1.
        let scores = [0.0, 20.0];
        let order = rerank_order(&bases, &ranks, &scores, &texts);
        assert_eq!(order, vec![0, 1]);
    }

    #[test]
    fn rerank_order_breaks_same_rank_ties_by_blended_score() {
        let bases = [-10.0, -9.0];
        let ranks = [1, 1];
        let scores = [0.0, 0.5];
        let texts = vec!["甲".to_owned(), "乙".to_owned()];
        let order = rerank_order(&bases, &ranks, &scores, &texts);
        assert_eq!(order, vec![1, 0]);
    }

    #[test]
    fn rerank_order_breaks_exact_ties_by_ordinal_text() {
        let bases = [-10.0, -10.0];
        let ranks = [1, 1];
        let scores = [0.0, 0.0];
        let texts = vec!["z".to_owned(), "a".to_owned()];
        assert_eq!(rerank_order(&bases, &ranks, &scores, &texts), vec![1, 0]);
    }

    #[test]
    fn handshake_is_protocol_compatible() {
        let mut state = CoreState::default();
        let response = handle_line(&mut state, r#"{"type":"hello","seq":7}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["type"], "response");
        assert_eq!(value["seq"], 7);
        assert_eq!(value["protocol_version"], PROTOCOL_VERSION);
        assert_eq!(value["keyboard_open"], true);
    }

    #[test]
    fn basic_composition_is_stateful() {
        let mut state = CoreState::default();
        let response = handle_line(
            &mut state,
            r#"{"type":"key","seq":1,"action":"down","vk":65}"#,
        )
        .unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["handled"], true);
        assert_eq!(value["input_buffer"], "a");

        let response = handle_line(
            &mut state,
            r#"{"type":"key","seq":2,"action":"down","vk":13}"#,
        )
        .unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["commit_text"], "a");
        assert_eq!(value["input_buffer"], "");
    }

    #[test]
    fn notifications_do_not_reply() {
        let mut state = CoreState::default();
        assert!(handle_line(&mut state, r#"{"type":"ime_active","active":true}"#).is_none());
        assert!(state.ime_active);
    }

    #[test]
    fn invalid_input_returns_error() {
        let mut state = CoreState::default();
        let response = handle_line(&mut state, "{").unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], false);
    }

    #[test]
    fn retried_physical_key_is_not_executed_twice() {
        let mut state = CoreState::default();
        let first = handle_line(
            &mut state,
            r#"{"type":"key","seq":10,"client_session":"tsf-1","event_id":"42","action":"down","vk":65}"#,
        )
        .unwrap();
        let retry = handle_line(
            &mut state,
            r#"{"type":"key","seq":11,"client_session":"tsf-1","event_id":"42","action":"down","vk":65}"#,
        )
        .unwrap();

        let first: Value = serde_json::from_str(&first).unwrap();
        let retry: Value = serde_json::from_str(&retry).unwrap();
        assert_eq!(first["input_buffer"], "a");
        assert_eq!(retry["input_buffer"], "a");
        assert_eq!(retry["seq"], 11);
        assert_eq!(state.input_buffer, "a");
    }

    #[test]
    fn space_and_digit_commit_lexicon_candidates() {
        let path = std::env::temp_dir().join("tigerclaw-rust-protocol-lexicon.txt");
        std::fs::write(&path, "你好\tabcd\n世界\tabcd\n第三\tabcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        let _ = handle_line(&mut state, r#"{"type":"key","seq":1,"action":"down","vk":65}"#);
        let _ = handle_line(&mut state, r#"{"type":"key","seq":2,"action":"down","vk":66}"#);
        let _ = handle_line(&mut state, r#"{"type":"key","seq":3,"action":"down","vk":67}"#);
        let _ = handle_line(&mut state, r#"{"type":"key","seq":4,"action":"down","vk":68}"#);
        let response = handle_line(&mut state, r#"{"type":"key","seq":5,"action":"down","vk":50}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["commit_text"], "世界");
        assert_eq!(value["input_buffer"], "");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn normal_max_code_rolls_boundary_letter_and_preserves_empty_code_setting() {
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::from_entries(&[
            ("abcd", "甲"),
            ("abcd", "乙"),
        ]);
        type_letters(&mut state, "abcd", 1);
        assert_eq!(state.input_buffer, "abcd");
        let rollover = type_vk(&mut state, 5, 0x58);
        assert_eq!(rollover["handled"], true);
        assert_eq!(rollover["commit_text"], "甲");
        assert_eq!(rollover["input_buffer"], "x");
        assert_eq!(state.raw_input, "x");

        let mut empty = CoreState::default();
        empty.lexicon = crate::lexicon::Lexicon::from_entries(&[("abcd", "甲")]);
        empty.config.empty_code_clear = true;
        type_letters(&mut empty, "abce", 1);
        let rollover = type_vk(&mut empty, 5, 0x58);
        assert_eq!(rollover["handled"], true);
        assert_eq!(rollover.get("commit_text"), None);
        assert_eq!(rollover["input_buffer"], "x");
        assert_eq!(empty.raw_input, "x");

        let mut no_clear = CoreState::default();
        no_clear.lexicon = crate::lexicon::Lexicon::from_entries(&[("zzzz", "甲")]);
        no_clear.config.empty_code_clear = false;
        type_letters(&mut no_clear, "abcd", 6);
        let overlong = type_vk(&mut no_clear, 10, 0x59);
        assert_eq!(overlong["input_buffer"], "abcdy");
        assert_eq!(no_clear.raw_input, "abcdy");
    }

    #[test]
    fn selector_keys_and_tab_move_candidate_selection() {
        let path = std::env::temp_dir().join("tigerclaw-rust-selector-test.txt");
        std::fs::write(&path, "一\tabcd\n二\tabcd\n三\tabcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        let _ = state.config.set("TAB清屏", "否");
        for (seq, vk) in [65_u64, 66, 67, 68].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 1, vk);
            handle_line(&mut state, &line);
        }
        let response = handle_line(&mut state, r#"{"type":"key","seq":5,"action":"down","vk":9}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        // Normal candidates are committed through Space/selection keys; Tab
        // passes through when TAB清屏 is disabled and does not move a
        // highlighted normal candidate (only sentence mode has traversal).
        assert_eq!(value["handled"], false);
        assert_eq!(value["selected_index"], 0);
        let response = handle_line(&mut state, r#"{"type":"key","seq":6,"action":"down","vk":186}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["commit_text"], "二");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn config_can_disable_selectors_and_make_tab_clear() {
        let path = std::env::temp_dir().join("tigerclaw-rust-config-protocol-test.txt");
        std::fs::write(&path, "一\tabcd\n二\tabcd\n分号次选\t否\nTAB清屏\t是\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        state.config = crate::config::Config::load(&path).unwrap();
        for (seq, vk) in [65_u64, 66, 67, 68].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 1, vk);
            handle_line(&mut state, &line);
        }
        let response = handle_line(&mut state, r#"{"type":"key","seq":5,"action":"down","vk":186}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["handled"], true);
        assert_eq!(value["commit_text"], "一；");
        for (seq, vk) in [65_u64, 66, 67, 68].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 6, vk);
            handle_line(&mut state, &line);
        }
        let response = handle_line(&mut state, r#"{"type":"key","seq":10,"action":"down","vk":9}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["handled"], true);
        assert_eq!(value["input_buffer"], "");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn reload_config_updates_version_and_behavior() {
        let path = std::env::temp_dir().join("tigerclaw-rust-reload-test.txt");
        std::fs::write(&path, "TAB清屏\t是\n").unwrap();
        let mut state = CoreState::default();
        state.config_path = Some(path.to_string_lossy().into_owned());
        let response = handle_line(&mut state, r#"{"type":"reload_config","seq":9}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], true);
        assert_eq!(value["config_version"], 2);
        assert!(state.config.tab_clear);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn reload_lexicon_updates_version_and_candidates() {
        let path = std::env::temp_dir().join("tigerclaw-rust-reload-lexicon.txt");
        std::fs::write(&path, "甲\tabcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon_path = Some(path.to_string_lossy().into_owned());
        let response = handle_line(&mut state, r#"{"type":"reload_mb","seq":10}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], true);
        assert_eq!(value["lexicon_version"], 2);
        assert_eq!(state.lexicon.candidates("abcd"), vec!["甲"]);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn mixed_input_keeps_raw_code_and_displays_prefix() {
        let path = std::env::temp_dir().join("tigerclaw-rust-mixed-test.txt");
        std::fs::write(&path, "你好\tabcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        state.config.mixed_input = true;
        for (seq, vk) in [65_u64, 66, 67, 68, 88].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 1, vk);
            handle_line(&mut state, &line);
        }
        let response = handle_line(&mut state, r#"{"type":"query_state","seq":6}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["input_buffer"], "你好x");
        let response = handle_line(&mut state, r#"{"type":"key","seq":7,"action":"down","vk":13}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["commit_text"], "abcdx");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn mixed_input_backspace_restores_completed_segment() {
        let path = std::env::temp_dir().join("tigerclaw-rust-mixed-backspace-test.txt");
        std::fs::write(&path, "你好\tabcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        state.config.mixed_input = true;
        for (seq, vk) in [65_u64, 66, 67, 68, 88].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 1, vk);
            handle_line(&mut state, &line);
        }
        handle_line(&mut state, r#"{"type":"key","seq":6,"action":"down","vk":8}"#);
        let response = handle_line(&mut state, r#"{"type":"key","seq":7,"action":"down","vk":8}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["input_buffer"], "abc");
        assert_eq!(state.raw_input, "abc");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn sentence_mode_decodes_and_commits_selected_sentence() {
        let path = std::env::temp_dir().join("tigerclaw-rust-sentence-protocol-test.txt");
        std::fs::write(&path, "我\tab\n爱\tcd\n你\tcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        state.config.sentence_input = true;
        let _ = state.config.set("TAB清屏", "否");
        state.config.sentence_input = true;
        state.config.max_code_length = 2;
        for (seq, vk) in [65_u64, 66, 67, 68].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 1, vk);
            handle_line(&mut state, &line);
        }
        let _ = handle_line(&mut state, r#"{"type":"query_state","seq":5}"#).unwrap();
        assert_eq!(overlay_input_and_candidates(&state).1.len(), 2);
        handle_line(&mut state, r#"{"type":"key","seq":6,"action":"down","vk":9}"#);
        let response = handle_line(&mut state, r#"{"type":"key","seq":7,"action":"down","vk":32}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert!(value["commit_text"] == "我爱" || value["commit_text"] == "我你");
        let _ = std::fs::remove_file(path);
    }

    fn type_vk(state: &mut CoreState, seq: i64, vk: u64) -> Value {
        let line = format!(r#"{{"type":"key","seq":{seq},"action":"down","vk":{vk}}}"#);
        let response = handle_line(state, &line).unwrap();
        serde_json::from_str(&response).unwrap()
    }

    fn type_shift_vk(state: &mut CoreState, seq: i64, vk: u64) -> Value {
        let line = format!(
            r#"{{"type":"key","seq":{seq},"action":"down","vk":{vk},"shift":true}}"#
        );
        let response = handle_line(state, &line).unwrap();
        serde_json::from_str(&response).unwrap()
    }

    #[test]
    fn chinese_punctuation_matches_csharp_for_idle_and_candidate_commit() {
        let mut idle = CoreState::default();
        assert_eq!(type_vk(&mut idle, 1, 0xbc)["commit_text"], "，");
        assert_eq!(type_shift_vk(&mut idle, 2, 0xba)["commit_text"], "：");

        let mut composing = CoreState::default();
        composing.lexicon = crate::lexicon::Lexicon::from_entries(&[("ab", "你好")]);
        type_letters(&mut composing, "ab", 1);
        let committed = type_vk(&mut composing, 3, 0xbe);
        assert_eq!(committed["commit_text"], "你好。");
        assert_eq!(committed["input_buffer"], "");
    }

    #[test]
    fn punctuation_config_and_slash_switch_are_respected() {
        let mut state = CoreState::default();
        state.config.set("中文状态下使用英文标点", "是").unwrap();
        assert_eq!(type_vk(&mut state, 1, 0xbf)["commit_text"], "/");
        assert_eq!(type_shift_vk(&mut state, 2, 0x31)["commit_text"], "!");

        state.config.set("中文状态下使用英文标点", "否").unwrap();
        state.config.set("/输出顿号", "否").unwrap();
        assert_eq!(type_vk(&mut state, 3, 0xbf)["commit_text"], "/");
    }

    #[test]
    fn period_after_pass_through_digit_stays_ascii() {
        let mut state = CoreState::default();
        let digit = type_vk(&mut state, 1, 0x31);
        assert_eq!(digit["handled"], false);
        assert_eq!(type_vk(&mut state, 2, 0xbe)["commit_text"], ".");
        assert_eq!(type_vk(&mut state, 3, 0xbe)["commit_text"], "。");
    }

    #[test]
    fn smart_quotes_alternate_and_backspace_restores_deleted_side() {
        let mut state = CoreState::default();
        assert_eq!(type_vk(&mut state, 1, 0xde)["commit_text"], "‘");
        assert_eq!(type_vk(&mut state, 2, 0xde)["commit_text"], "’");
        assert_eq!(type_vk(&mut state, 3, 0x08)["handled"], false);
        assert_eq!(type_vk(&mut state, 4, 0xde)["commit_text"], "’");
        assert_eq!(type_shift_vk(&mut state, 5, 0xde)["commit_text"], "“");
        assert_eq!(type_shift_vk(&mut state, 6, 0xde)["commit_text"], "”");
    }

    #[test]
    fn ctrl_space_and_shift_toggle_commit_raw_composition() {
        let mut ctrl_state = CoreState::default();
        type_letters(&mut ctrl_state, "ab", 1);
        let response = handle_line(&mut ctrl_state, r#"{"type":"ctrl_space","seq":3}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["commit_text"], "ab");
        assert_eq!(value["keyboard_open"], false);

        let mut shift_state = CoreState::default();
        type_letters(&mut shift_state, "cd", 1);
        type_vk(&mut shift_state, 3, 0x10);
        let response = handle_line(
            &mut shift_state,
            r#"{"type":"key","seq":4,"action":"up","vk":16}"#,
        ).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["commit_text"], "cd");
        assert_eq!(value["keyboard_open"], false);
    }

    #[test]
    fn physical_ctrl_space_toggles_once_for_both_release_orders() {
        for reverse_release in [false, true] {
            let mut state = CoreState::default();
            type_letters(&mut state, "ab", 1);
            let ctrl_down = r#"{"type":"key","seq":3,"action":"down","vk":17,"ctrl":true,"repeat":1}"#;
            let space_down = r#"{"type":"key","seq":4,"action":"down","vk":32,"ctrl":true,"repeat":1}"#;
            assert!(handle_line(&mut state, ctrl_down).is_some());
            let toggled = handle_line(&mut state, space_down).unwrap();
            let toggled: Value = serde_json::from_str(&toggled).unwrap();
            assert_eq!(toggled["keyboard_open"], false);
            assert_eq!(toggled["commit_text"], "ab");
            if reverse_release {
                handle_line(&mut state, r#"{"type":"key","seq":5,"action":"up","vk":17,"ctrl":false}"#);
                handle_line(&mut state, r#"{"type":"key","seq":6,"action":"up","vk":32,"ctrl":false}"#);
            } else {
                handle_line(&mut state, r#"{"type":"key","seq":5,"action":"up","vk":32,"ctrl":false}"#);
                handle_line(&mut state, r#"{"type":"key","seq":6,"action":"up","vk":17,"ctrl":false}"#);
            }
            assert_eq!(state.keyboard_open, false);
            assert_eq!(state.commit_history, vec!["ab"]);
        }
    }

    #[test]
    fn physical_ctrl_space_repeat_and_blocking_modifiers_do_not_retoggle() {
        let mut repeated = CoreState::default();
        handle_line(&mut repeated, r#"{"type":"key","seq":1,"action":"down","vk":17,"ctrl":true,"repeat":1}"#);
        handle_line(&mut repeated, r#"{"type":"key","seq":2,"action":"down","vk":32,"ctrl":true,"repeat":1}"#);
        handle_line(&mut repeated, r#"{"type":"key","seq":3,"action":"down","vk":32,"ctrl":true,"repeat":2}"#);
        handle_line(&mut repeated, r#"{"type":"key","seq":4,"action":"up","vk":32,"ctrl":true}"#);
        handle_line(&mut repeated, r#"{"type":"key","seq":5,"action":"up","vk":17}"#);
        assert!(!repeated.keyboard_open);

        let mut blocked = CoreState::default();
        handle_line(&mut blocked, r#"{"type":"key","seq":1,"action":"down","vk":17,"ctrl":true}"#);
        handle_line(&mut blocked, r#"{"type":"key","seq":2,"action":"down","vk":16,"ctrl":true,"shift":true}"#);
        handle_line(&mut blocked, r#"{"type":"key","seq":3,"action":"down","vk":32,"ctrl":true,"shift":true}"#);
        handle_line(&mut blocked, r#"{"type":"key","seq":4,"action":"up","vk":16,"ctrl":true,"shift":true}"#);
        handle_line(&mut blocked, r#"{"type":"key","seq":5,"action":"up","vk":17}"#);
        handle_line(&mut blocked, r#"{"type":"key","seq":6,"action":"up","vk":32}"#);
        assert!(blocked.keyboard_open);
    }

    #[test]
    fn physical_ctrl_shortcuts_disarm_pending_space_toggle() {
        for vk in [0x4d_u64, 0xbb, 0x31] {
            let mut state = CoreState::default();
            handle_line(&mut state, r#"{"type":"key","seq":1,"action":"down","vk":17,"ctrl":true}"#);
            let shortcut = format!(r#"{{"type":"key","seq":2,"action":"down","vk":{vk},"ctrl":true}}"#);
            handle_line(&mut state, &shortcut);
            handle_line(&mut state, r#"{"type":"key","seq":3,"action":"up","vk":17}"#);
            handle_line(&mut state, r#"{"type":"key","seq":4,"action":"down","vk":32}"#);
            handle_line(&mut state, r#"{"type":"key","seq":5,"action":"up","vk":32}"#);
            assert!(state.keyboard_open, "Ctrl+{vk:x} followed by Space must not toggle");
        }
    }

    #[test]
    fn pass_through_shortcut_cancels_active_composition() {
        let mut state = CoreState::default();
        type_letters(&mut state, "ab", 1);
        let response = handle_line(
            &mut state,
            r#"{"type":"key","seq":3,"action":"down","vk":67,"ctrl":true}"#,
        ).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["handled"], false);
        assert_eq!(value["input_buffer"], "");
        assert_eq!(value["cancel_composition"], true);
    }

    #[test]
    fn caps_lock_commits_composition_but_passes_following_keys() {
        let mut state = CoreState::default();
        type_letters(&mut state, "ab", 1);
        let committed = handle_line(
            &mut state,
            r#"{"type":"key","seq":3,"action":"down","vk":20,"capsLock":true}"#,
        )
        .unwrap();
        let committed: Value = serde_json::from_str(&committed).unwrap();
        assert_eq!(committed["handled"], false);
        assert_eq!(committed["commit_text"], "ab");
        assert_eq!(committed["input_buffer"], "");

        let passed = handle_line(
            &mut state,
            r#"{"type":"key","seq":4,"action":"down","vk":65,"capsLock":true}"#,
        )
        .unwrap();
        let passed: Value = serde_json::from_str(&passed).unwrap();
        assert_eq!(passed["handled"], false);
        assert_eq!(passed["input_buffer"], "");
    }

    #[test]
    fn key_caret_releases_fresh_anchor_suppression() {
        let mut state = CoreState::default();
        let response = handle_line(
            &mut state,
            r#"{"type":"key","seq":1,"action":"down","vk":65,"tsf_stage":"key_down"}"#,
        )
        .unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["input_buffer"], "a");
        assert!(state.fresh_caret_deadline.is_some());
        assert!(!state.fresh_caret);
        assert!(handle_line(
            &mut state,
            r#"{"type":"caret","x":100,"y":200,"width":3,"height":22}"#,
        )
        .is_none());
        assert!(state.fresh_caret);
        assert!(state.fresh_caret_deadline.is_none());
        assert_eq!(state.caret_x, 100);
        assert_eq!(state.caret_height, 22);
    }

    #[test]
    fn uppercase_mode_keeps_literal_case_and_currency_timer_macros() {
        let mut literal = CoreState::default();
        type_shift_vk(&mut literal, 1, 0x41);
        type_vk(&mut literal, 2, 0x32);
        type_vk(&mut literal, 3, 0x42);
        let committed = type_vk(&mut literal, 4, 0x20);
        assert_eq!(committed["commit_text"], "A2b");

        let mut currency = CoreState::default();
        type_shift_vk(&mut currency, 1, 0x53);
        for (seq, vk) in [(2, 0x31), (3, 0x32), (4, 0xBE), (5, 0x33), (6, 0x34)] {
            type_vk(&mut currency, seq, vk);
        }
        let committed = type_vk(&mut currency, 7, 0x20);
        assert_eq!(committed["commit_text"], "壹拾贰元叁角肆分");

        let mut timer = CoreState::default();
        type_shift_vk(&mut timer, 1, 0x44);
        type_shift_vk(&mut timer, 2, 0x53);
        type_vk(&mut timer, 3, 0x35);
        let committed = type_vk(&mut timer, 4, 0x20);
        assert_eq!(committed["commit_text"], "");
    }

    #[test]
    fn candidate_publication_obeys_page_size() {
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::from_entries(&[
            ("a", "一"), ("a", "二"), ("a", "三"), ("a", "四"),
        ]);
        state.config.page_size = 2;
        let value = type_vk(&mut state, 1, 0x41);
        assert_eq!(value["candidates"].as_array().unwrap().len(), 2);
    }

    #[test]
    fn unknown_message_is_a_failed_response() {
        let mut state = CoreState::default();
        let response = handle_line(&mut state, r#"{"type":"does_not_exist","seq":9}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], false);
        assert_eq!(value["handled"], false);
    }

    #[test]
    fn shifted_selector_keys_commit_punctuation_instead_of_selecting() {
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::from_entries(&[("ab", "一"), ("ab", "二")]);
        type_letters(&mut state, "ab", 1);
        let committed = type_shift_vk(&mut state, 3, 0xba);
        assert_eq!(committed["commit_text"], "一：");
        assert_eq!(committed["input_buffer"], "");
    }

    #[test]
    fn sentence_final_commit_does_not_repeat_early_committed_prefix() {
        let mut state = sentence_state(&[]);
        state.input_buffer = "abcdef".to_owned();
        state.raw_input = state.input_buffer.clone();
        state.early.committed_text = "前缀".to_owned();
        state.early.committed_raw_length = 3;
        state.sentence_candidates = vec!["剩余".to_owned()];
        state.sentence_decoded_raw = state.input_buffer.clone();
        state.sentence_decoded_lexicon_version = state.lexicon_version;

        let committed = type_vk(&mut state, 1, 0x20);
        assert_eq!(committed["commit_text"], "剩余");
        assert_eq!(committed["input_buffer"], "");
    }

    #[test]
    fn sentence_punctuation_after_early_commit_uses_only_live_suffix() {
        let mut state = sentence_state(&[]);
        state.input_buffer = "abcdef".to_owned();
        state.raw_input = state.input_buffer.clone();
        state.early.committed_text = "已上屏".to_owned();
        state.early.committed_raw_length = 3;
        state.sentence_candidates = vec!["尾部".to_owned()];
        state.sentence_decoded_raw = state.input_buffer.clone();
        state.sentence_decoded_lexicon_version = state.lexicon_version;

        let committed = type_vk(&mut state, 1, 0xbc);
        assert_eq!(committed["commit_text"], "尾部，");
    }

    fn type_letters(state: &mut CoreState, text: &str, start_seq: i64) -> i64 {
        let mut seq = start_seq;
        for ch in text.chars() {
            type_vk(state, seq, ch.to_ascii_uppercase() as u64);
            seq += 1;
        }
        seq
    }

    fn sentence_state(entries: &[(&str, &str)]) -> CoreState {
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::from_entries(entries);
        let _ = state.config.set("TAB清屏", "否");
        state.config.sentence_input = true;
        state
    }

    #[test]
    fn sentence_digits_and_semicolon_enter_encoding() {
        let mut state = sentence_state(&[("ot", "是"), ("j", "人"), ("j", "什么"), ("j", "怎样")]);
        type_letters(&mut state, "otj", 1);
        let digit = type_vk(&mut state, 4, 0x32);
        assert_eq!(digit["commit_text"], Value::Null);
        assert_eq!(digit["input_buffer"], "ot j2");
        assert_eq!(digit["candidates"][0], "是什么");
        let mut quoted = sentence_state(&[("ot", "是"), ("j", "人"), ("j", "什么"), ("j", "怎样")]);
        type_letters(&mut quoted, "otj", 1);
        let semicolon = type_vk(&mut quoted, 4, 0xba);
        assert_eq!(semicolon["commit_text"], Value::Null);
        assert_eq!(semicolon["candidates"][0], "是什么");
        let numpad = sentence_state(&[("ot", "是"), ("j", "人"), ("j", "什么"), ("j", "怎样")]);
        let mut numpad_state = numpad;
        type_letters(&mut numpad_state, "otj", 1);
        let pad = type_vk(&mut numpad_state, 4, 0x62);
        assert_eq!(pad["candidates"][0], "是什么");
    }

    fn auto_commit_state(entries: &[(&str, &str)]) -> CoreState {
        let mut state = sentence_state(entries);
        state.config.early_commit = true;
        state
    }

    #[test]
    fn sentence_early_commit_retains_last_character() {
        let mut state = auto_commit_state(&[
            ("abc", "甲乙"),
            ("de", "丙"),
            ("def", "丁"),
            ("defg", "戊"),
        ]);
        type_letters(&mut state, "abcde", 1);
        let second = type_vk(&mut state, 6, 0x46);
        assert_eq!(second["commit_text"], Value::Null);
        let committed = type_vk(&mut state, 7, 0x47);
        assert_eq!(committed["commit_text"], "甲乙");
        assert_eq!(committed["candidates"][0].as_str().unwrap().chars().count(), 1);
    }

    #[test]
    fn sentence_early_commit_uses_three_generation_prefix() {
        let mut state = auto_commit_state(&[
            ("abc", "甲"),
            ("de", "丙"),
            ("def", "丁"),
            ("defg", "乙戊"),
        ]);
        type_letters(&mut state, "abcde", 1);
        assert_eq!(type_vk(&mut state, 6, 0x46)["commit_text"], Value::Null);
        let committed = type_vk(&mut state, 7, 0x47);
        assert_eq!(committed["commit_text"], "甲");
        assert_eq!(committed["candidates"][0], "乙戊");
    }

    #[test]
    fn async_sentence_early_commit_snapshot_is_immediately_trimmed_to_live_suffix() {
        let mut state = sentence_state(&[
            ("abc", "甲"),
            ("de", "丙"),
            ("def", "丁"),
            ("defg", "乙戊"),
        ]);
        type_letters(&mut state, "abcdefg", 1);
        assert_eq!(state.sentence_candidates[0], "甲乙戊");

        // This is the state transition performed by the production async
        // path immediately after EarlyCommitRuntime returns "甲".
        state.early.committed_text = "甲".to_owned();
        state.early.committed_raw_length = 3;
        trim_sentence_result_after_early_commit(&mut state, "甲");

        assert_eq!(state.sentence_candidates[0], "乙戊");
        assert_eq!(overlay_input_and_candidates(&state).1[0], "乙戊");
    }

    #[test]
    fn sentence_display_trims_the_committed_raw_prefix_exactly_once() {
        let mut state = sentence_state(&[]);
        state.input_buffer = "jaefmjxj".to_owned();
        state.raw_input = state.input_buffer.clone();
        state.early.committed_text = "今".to_owned();
        state.early.committed_raw_length = 3;
        state.sentence_decoded_raw = state.input_buffer.clone();
        state.sentence_decoded_lexicon_version = state.lexicon_version;
        state.sentence_candidates = vec!["天斜".to_owned(), "丙科".to_owned()];
        state.sentence_segmented = vec!["jae fm jxj".to_owned(), "jae fmj xj".to_owned()];

        let (display, candidates) = overlay_input_and_candidates(&state);

        assert_eq!(display, "fm jxj");
        assert_eq!(candidates, vec!["天斜", "丙科"]);
    }

    #[test]
    fn sentence_backspace_stops_at_the_early_committed_raw_boundary() {
        let mut state = sentence_state(&[]);
        state.config.mixed_input = true;
        state.sentence_decode_sync = false;
        state.input_buffer = "jaefmjxj".to_owned();
        state.raw_input = state.input_buffer.clone();
        state.early.committed_text = "今".to_owned();
        state.early.committed_raw_length = 3;
        state.sentence_decoded_raw = state.input_buffer.clone();
        state.sentence_decoded_lexicon_version = state.lexicon_version;
        state.sentence_candidates = vec!["天斜".to_owned(), "丙科".to_owned()];
        state.sentence_segmented = vec!["jae fm jxj".to_owned(), "jae fmj xj".to_owned()];
        let generation = state.sentence_generation;

        assert_eq!(type_vk(&mut state, 1, 0x08)["input_buffer"], "fm jx");
        assert_eq!(state.input_buffer, "jaefmjx");
        assert_eq!(state.raw_input, "jaefmjx");
        assert_eq!(state.sentence_generation, generation + 1);
        assert_eq!(type_vk(&mut state, 2, 0x08)["input_buffer"], "fmj");
        assert_eq!(type_vk(&mut state, 3, 0x08)["input_buffer"], "fm");
        assert_eq!(type_vk(&mut state, 4, 0x08)["input_buffer"], "f");
        let cleared = type_vk(&mut state, 5, 0x08);
        assert_eq!(cleared["input_buffer"], "");
        assert!(state.input_buffer.is_empty());
        assert!(state.raw_input.is_empty());
        assert_eq!(state.early.committed_raw_length, 0);
    }

    #[test]
    fn sentence_early_commit_requires_stable_raw_boundary() {
        let mut state = auto_commit_state(&[
            ("ab", "甲乙"),
            ("abc", "甲乙"),
            ("cde", "丙"),
            ("def", "丁"),
            ("defg", "戊"),
        ]);
        type_letters(&mut state, "abcdef", 1);
        assert_eq!(type_vk(&mut state, 7, 0x47)["commit_text"], Value::Null);
    }

    #[test]
    fn sentence_early_commit_suspends_after_navigation() {
        let mut state = auto_commit_state(&[
            ("abc", "甲乙"),
            ("de", "丙"),
            ("def", "丁"),
            ("defg", "戊"),
        ]);
        type_letters(&mut state, "abcde", 1);
        type_vk(&mut state, 6, 0x09);
        assert_eq!(type_vk(&mut state, 7, 0x46)["commit_text"], Value::Null);
    }

    #[test]
    fn sentence_early_commit_requires_consecutive_append() {
        let mut state = auto_commit_state(&[
            ("abc", "甲乙"),
            ("de", "丙"),
            ("def", "丁"),
            ("defg", "戊"),
        ]);
        type_letters(&mut state, "abcde", 1);
        type_vk(&mut state, 6, 0x08);
        assert_eq!(type_vk(&mut state, 7, 0x45)["commit_text"], Value::Null);
    }

    #[test]
    fn sentence_enter_commits_remaining_raw() {
        let mut state = sentence_state(&[("ab", "我"), ("cd", "爱")]);
        type_letters(&mut state, "abcd", 1);
        let committed = type_vk(&mut state, 5, 0x0d);
        assert_eq!(committed["commit_text"], "abcd");
        assert_eq!(committed["input_buffer"], "");
    }

    #[test]
    fn sentence_holds_previous_candidates_while_decode_is_pending() {
        let mut state = sentence_state(&[("ot", "是"), ("ue", "的")]);
        state.publish_sentence_assets();
        state.sentence_decode_sync = false;
        {
            let mut decoder = state.sentence_decoder.lock().unwrap();
            decoder.test_decode_delay = std::time::Duration::from_millis(30);
        }
        type_letters(&mut state, "ot", 1);
        let mut ready = false;
        for seq in 20..70 {
            std::thread::sleep(std::time::Duration::from_millis(15));
            let line = format!(r#"{{"type":"query_state","seq":{seq}}}"#);
            let _ = handle_line(&mut state, &line).unwrap();
            let candidates = overlay_input_and_candidates(&state).1;
            if !candidates.is_empty() {
                assert_eq!(candidates[0], "是");
                ready = true;
                break;
            }
        }
        assert!(ready, "prefix decode should complete");
        let pending = type_vk(&mut state, 80, 0x55);
        assert_eq!(pending["input_buffer"], "otu");
        assert_eq!(pending["candidates"][0], "是");
        let mut caught_up = false;
        for seq in 90..140 {
            std::thread::sleep(std::time::Duration::from_millis(15));
            let line = format!(r#"{{"type":"query_state","seq":{seq}}}"#);
            let _ = handle_line(&mut state, &line).unwrap();
            if overlay_input_and_candidates(&state).1.is_empty() {
                caught_up = true;
                break;
            }
        }
        assert!(caught_up, "pending suffix should catch up to an empty list");
    }

    #[test]
    fn sentence_backspace_to_empty_ignores_stale_async_decode() {
        // Reproduces the reported bug: backspacing a sentence composition
        // fully empty while a real async decode for the just-deleted raw
        // code is still in flight. Before the fix, the wipe branch left
        // sentence_generation unchanged, so once that stale decode finished
        // and wrote into sentence_completed, the next pump_sentence call
        // would pass the generation guard and repopulate
        // sentence_decoded_raw/candidates from the deleted raw code even
        // though input_buffer had already been cleared. C#'s
        // ClearCompositionInput (called from this exact VK_BACK branch)
        // always increments _sentenceGeneration for exactly this reason.
        let mut state = sentence_state(&[("o", "噢"), ("ot", "是"), ("ue", "的")]);
        state.publish_sentence_assets();
        state.sentence_decode_sync = false;
        {
            let mut decoder = state.sentence_decoder.lock().unwrap();
            decoder.test_decode_delay = std::time::Duration::from_millis(120);
        }
        type_letters(&mut state, "ot", 1);
        assert!(state.sentence_worker.lock().unwrap().job.is_some());
        let generation_before_clear = state.sentence_generation;

        // Backspace both characters before the delayed decode completes.
        type_vk(&mut state, 3, 0x08);
        type_vk(&mut state, 4, 0x08);
        assert_eq!(state.input_buffer, "");
        assert!(
            state.sentence_generation > generation_before_clear,
            "clearing a composition must advance, never reset, its invalidation generation"
        );

        // Let the stale worker finish (it will still write whatever it
        // decoded into sentence_completed — that write racing the wipe is
        // expected and harmless), then explicitly pump it.
        std::thread::sleep(std::time::Duration::from_millis(250));
        let _ = handle_line(&mut state, r#"{"type":"query_state","seq":99}"#);

        assert_eq!(
            state.sentence_decoded_raw, "",
            "a stale decode for the deleted raw code must not repopulate decoded_raw"
        );
        assert!(
            state.sentence_candidates.is_empty(),
            "a stale decode must not repopulate candidates after the composition was wiped: {:?}",
            state.sentence_candidates
        );
        assert!(state.sentence_segmented.is_empty());
        assert_eq!(state.input_buffer, "");
    }

    #[test]
    fn stale_sentence_result_with_matching_generation_but_wrong_raw_is_rejected() {
        let mut state = sentence_state(&[("ab", "旧"), ("cd", "新")]);
        state.input_buffer = "cd".to_owned();
        state.raw_input = "cd".to_owned();
        state.sentence_generation = 7;
        let stale = crate::decoder::decode_full(
            "ab",
            &state.lexicon,
            None,
            &state.ranks,
            &state.supplements,
            20,
            crate::decoder::DecoderOptions::default(),
            false,
            "",
        );
        *state.sentence_completed.lock().unwrap() = Some(CompletedSentence {
            generation: 7,
            lexicon_version: state.lexicon_version,
            result: stale,
        });

        let _ = handle_line(&mut state, r#"{"type":"query_state","seq":91}"#);
        assert_eq!(state.input_buffer, "cd");
        assert_ne!(state.sentence_decoded_raw, "ab");
        assert!(!state.sentence_candidates.iter().any(|item| item == "旧"));
    }

    #[test]
    fn config_cancel_is_deferred_to_exactly_one_physical_key_response() {
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::from_entries(&[("ab", "甲")]);
        type_vk(&mut state, 1, 0x41);

        let changed: Value = serde_json::from_str(
            &handle_line(
                &mut state,
                r#"{"type":"set_config","seq":2,"key":"整句输入","value":"是"}"#,
            )
            .unwrap(),
        )
        .unwrap();
        assert!(changed.get("cancel_composition").is_none());
        assert!(state.cancel_composition_pending.get());

        let query: Value = serde_json::from_str(
            &handle_line(&mut state, r#"{"type":"query_state","seq":3}"#).unwrap(),
        )
        .unwrap();
        assert!(query.get("cancel_composition").is_none());
        assert!(state.cancel_composition_pending.get());

        let key: Value = serde_json::from_str(
            &handle_line(
                &mut state,
                r#"{"type":"key","seq":4,"action":"down","vk":66,"tsf_stage":"key_down"}"#,
            )
            .unwrap(),
        )
        .unwrap();
        assert_eq!(key["cancel_composition"], true);
        assert!(!state.cancel_composition_pending.get());

        let next: Value = serde_json::from_str(
            &handle_line(
                &mut state,
                r#"{"type":"key","seq":5,"action":"up","vk":66,"tsf_stage":"key_up"}"#,
            )
            .unwrap(),
        )
        .unwrap();
        assert_eq!(next["cancel_composition"], false);
    }

    #[test]
    fn query_state_is_compact_even_with_large_candidate_payloads() {
        let mut state = CoreState::default();
        state.input_buffer = "ab".to_owned();
        state.raw_input = "ab".to_owned();
        state.config.sentence_input = true;
        state.sentence_decoded_raw = "ab".to_owned();
        state.sentence_candidates = (0..10)
            .map(|index| format!("候选{index}{}", "长".repeat(256)))
            .collect();

        let response = handle_line(&mut state, r#"{"type":"query_state","seq":6}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert!(value.get("candidates").is_none());
        assert!(value.get("selected_index").is_none());
        assert!(value.get("cancel_composition").is_none());
        assert!(response.len() < 4096);
    }

    #[test]
    fn schema_list_and_code_masking_are_protocol_visible() {
        let root = std::env::temp_dir().join("tigerclaw-rust-schema-root");
        let schema = root.join("虎整句");
        let _ = std::fs::create_dir_all(&schema);
        std::fs::write(schema.join("常用字词.txt"), "我\tab\n").unwrap();
        let mut state = CoreState::default();
        state.base_dir = Some(root.parent().unwrap().to_string_lossy().into_owned());
        let _ = state.config.set("码表存储位置", &root.to_string_lossy());
        let _ = state.config.set("当前码表", "虎整句");
        let response = handle_line(&mut state, r#"{"type":"get_schema_list","seq":1}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert!(value["schema_list"].as_str().unwrap().contains("虎整句"));
        assert_eq!(value["current_schema"], "虎整句");
        assert!(state.config.sentence_active());

        let _ = state.config.set("自动启用整句模式", "否");
        let _ = state.config.set("编码伪装", "1234");
        state.config.mixed_input = false;
        state.config.sentence_input = false;
        handle_line(&mut state, r#"{"type":"key","seq":2,"action":"down","vk":65}"#);
        let response = handle_line(&mut state, r#"{"type":"query_state","seq":3}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["input_buffer"], "1");
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn shift_up_toggles_chinese_when_enabled() {
        let mut state = CoreState::default();
        assert!(state.keyboard_open);
        handle_line(&mut state, r#"{"type":"key","seq":1,"action":"down","vk":16}"#);
        let response = handle_line(&mut state, r#"{"type":"key","seq":2,"action":"up","vk":16}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["handled"], true);
        assert_eq!(value["keyboard_open"], false);
    }

    #[test]
    fn dialog_selection_key_config_changes_candidate_commit() {
        let path = std::env::temp_dir().join("tigerclaw-rust-selection-key-test.txt");
        std::fs::write(&path, "甲\tab\n乙\tab\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();

        let response = handle_line(
            &mut state,
            r#"{"type":"set_selection_key_config","seq":1,"config_text":"1选 VK_1\n2选 VK_SPACE"}"#,
        ).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], true);
        assert!(value["config_text"].as_str().unwrap().contains("2选 0x20"));

        handle_line(&mut state, r#"{"type":"key","seq":2,"action":"down","vk":65}"#);
        handle_line(&mut state, r#"{"type":"key","seq":3,"action":"down","vk":66}"#);
        let response = handle_line(&mut state, r#"{"type":"key","seq":4,"action":"down","vk":32}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["commit_text"], "乙");

        let invalid = handle_line(
            &mut state,
            r#"{"type":"set_selection_key_config","seq":5,"config_text":"11选 VK_1"}"#,
        ).unwrap();
        let invalid: Value = serde_json::from_str(&invalid).unwrap();
        assert_eq!(invalid["success"], false);
        assert!(invalid["error"].as_str().unwrap().contains("无效标签"));
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn resolved_modifier_selection_consumes_key_down_and_up() {
        let path = std::env::temp_dir().join("tigerclaw-rust-modifier-selection-test.txt");
        std::fs::write(&path, "甲\tab\n乙\tab\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        state.selection_keys = SelectionKeyBindings::parse("2选 VK_RSHIFT").unwrap();
        handle_line(&mut state, r#"{"type":"key","seq":1,"action":"down","vk":65}"#);
        handle_line(&mut state, r#"{"type":"key","seq":2,"action":"down","vk":66}"#);
        let down = handle_line(
            &mut state,
            r#"{"type":"key","seq":3,"action":"down","vk":16,"scan":54,"shift":true}"#,
        ).unwrap();
        let down: Value = serde_json::from_str(&down).unwrap();
        assert_eq!(down["commit_text"], "乙");
        let up = handle_line(
            &mut state,
            r#"{"type":"key","seq":4,"action":"up","vk":16,"scan":54}"#,
        ).unwrap();
        let up: Value = serde_json::from_str(&up).unwrap();
        assert_eq!(up["handled"], true);
        assert_eq!(up["keyboard_open"], true);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn dialog_commands_construct_add_and_return_history() {
        let path = std::env::temp_dir().join("tigerclaw-rust-dialog-test.txt");
        std::fs::write(&path, "我\tab\n爱\tcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        state.lexicon_path = Some(path.to_string_lossy().into_owned());
        let response = handle_line(&mut state, r#"{"type":"construct_ci","seq":1,"text":"我爱"}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["code"], "abcd");
        let response = handle_line(&mut state, r#"{"type":"add_ci","seq":2,"code":"abcd","text":"我爱"}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], true);
        state.commit_history.push("我爱".to_owned());
        let response = handle_line(&mut state, r#"{"type":"get_last_ci","seq":3,"history_len":0}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["text"], "");
        let enter = handle_line(&mut state, r#"{"type":"key","seq":4,"action":"down","vk":13}"#).unwrap();
        let enter: Value = serde_json::from_str(&enter).unwrap();
        assert_eq!(enter["handled"], false);
        assert_eq!(enter["commit_text"], "\n");
        let history = handle_line(&mut state, r#"{"type":"get_last_ci","seq":5,"history_len":1}"#).unwrap();
        let history: Value = serde_json::from_str(&history).unwrap();
        assert_eq!(history["text"], "\n");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn user_adjustments_reorder_delete_and_persist_through_protocol() {
        let dir = std::env::temp_dir().join(format!(
            "tigerclaw-rust-adjustments-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("常用字词.txt"), "ab\t甲\nab\t乙\nab\t丙\n").unwrap();

        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load_directory(&dir).unwrap();
        state.lexicon_path = Some(dir.to_string_lossy().into_owned());
        type_letters(&mut state, "ab", 1);

        // Ctrl+2 pins the second candidate, and its key-up rearms the
        // one-shot modifier action for the next physical selection key.
        let top = type_key(
            &mut state,
            3,
            0x32,
            r#""ctrl":true"#,
        );
        assert_eq!(top["handled"], true);
        assert_eq!(state.lexicon.candidates("ab"), vec!["乙", "甲", "丙"]);
        release_key(&mut state, 4, 0x32);

        // Ctrl+Shift+3 deletes the third candidate from the reordered list.
        let deleted = type_key(
            &mut state,
            5,
            0x33,
            r#""ctrl":true,"shift":true"#,
        );
        assert_eq!(deleted["handled"], true);
        assert_eq!(state.lexicon.candidates("ab"), vec!["乙", "甲"]);
        release_key(&mut state, 6, 0x33);

        // Alt+2 advances the second candidate above the first one.
        let advanced = type_key(
            &mut state,
            7,
            0x32,
            r#""alt":true"#,
        );
        assert_eq!(advanced["handled"], true);
        assert_eq!(state.lexicon.candidates("ab"), vec!["甲", "乙"]);
        release_key(&mut state, 8, 0x32);

        let adjustment = std::fs::read_to_string(dir.join("用户调整.txt")).unwrap();
        assert!(adjustment.contains("{置顶}ab\t乙"));
        assert!(adjustment.contains("{删除}ab\t丙"));
        assert!(adjustment.contains("{前移}ab\t甲"));
        let reloaded = crate::lexicon::Lexicon::load_directory(&dir).unwrap();
        assert_eq!(reloaded.candidates("ab"), vec!["甲", "乙"]);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn pinyin_reverse_lookup_loads_edits_displays_annotations_and_commits() {
        let root = std::env::temp_dir().join(format!(
            "tigerclaw-rust-pinyin-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let pinyin_dir = root.join("拼音反查码表");
        std::fs::create_dir_all(&pinyin_dir).unwrap();
        std::fs::write(pinyin_dir.join("拼音.txt"), "ni\t你\nni\t妮\n").unwrap();

        let mut state = CoreState::default();
        state.base_dir = Some(root.to_string_lossy().into_owned());
        // The active table supplies the C#-style full-code annotation for
        // the text returned by the reverse-lookup table.
        state.lexicon = crate::lexicon::Lexicon::from_entries(&[("abcd", "你"), ("efgh", "妮")]);
        reload_pinyin_lexicon(&mut state);

        let entered = type_key(&mut state, 1, 0xC0, "");
        assert_eq!(entered["handled"], true);
        assert_eq!(entered["input_buffer"], "·");
        type_key(&mut state, 2, 0x4E, "");
        let typed = type_key(&mut state, 3, 0x49, "");
        assert_eq!(typed["input_buffer"], "·ni");
        assert_eq!(typed["candidates"], json!(["你", "妮"]));
        assert_eq!(overlay_candidate_annotations(&state), vec!["abcd", "efgh"]);

        // Backspace edits only the pinyin code and keeps the marker; typing
        // the final vowel again restores the same candidate page.
        let edited = type_key(&mut state, 4, 0x08, "");
        assert_eq!(edited["input_buffer"], "·n");
        type_key(&mut state, 5, 0x49, "");
        let committed = type_key(&mut state, 6, 0x20, "");
        assert_eq!(committed["commit_text"], "你");
        assert!(!state.pinyin_mode);
        assert!(state.input_buffer.is_empty());

        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn exit_response_sets_shutdown_state_and_export_returns_csharp_file() {
        let root = std::env::temp_dir().join(format!(
            "tigerclaw-rust-export-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&root).unwrap();

        let mut state = CoreState::default();
        state.base_dir = Some(root.to_string_lossy().into_owned());
        state.config.current_schema = "测试".to_owned();
        state.lexicon = crate::lexicon::Lexicon::from_entries(&[("ab", "乙"), ("a", "甲")]);

        let response = handle_line(&mut state, r#"{"type":"export_mb","seq":1}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], true);
        let path = Path::new(value["path"].as_str().unwrap());
        let bytes = std::fs::read(path).unwrap();
        assert!(bytes.starts_with(&[0xEF, 0xBB, 0xBF]));
        assert_eq!(
            std::str::from_utf8(&bytes[3..]).unwrap(),
            "a 甲\nab 乙\n"
        );

        let response = handle_line(&mut state, r#"{"type":"exit_core","seq":2}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], true);
        assert_eq!(value["handled"], true);
        assert_eq!(value["shutdown_requested"], true);
        assert!(state.shutdown_requested);

        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn export_reports_write_failure_instead_of_false_success() {
        let blocker = std::env::temp_dir().join(format!(
            "tigerclaw-rust-export-blocker-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::write(&blocker, "not a directory").unwrap();
        let mut state = CoreState::default();
        state.base_dir = Some(blocker.to_string_lossy().into_owned());
        let response = handle_line(&mut state, r#"{"type":"export_mb","seq":3}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], false);
        assert!(value["error"].as_str().is_some_and(|error| !error.is_empty()));
        let _ = std::fs::remove_file(blocker);
    }

    fn type_key(state: &mut CoreState, seq: i64, vk: u64, modifiers: &str) -> Value {
        let suffix = if modifiers.is_empty() {
            String::new()
        } else {
            format!(",{modifiers}")
        };
        let line = format!(
            r#"{{"type":"key","seq":{seq},"action":"down","vk":{vk}{suffix}}}"#
        );
        serde_json::from_str(&handle_line(state, &line).unwrap()).unwrap()
    }

    fn release_key(state: &mut CoreState, seq: i64, vk: u64) -> Value {
        let line = format!(
            r#"{{"type":"key","seq":{seq},"action":"up","vk":{vk}}}"#
        );
        serde_json::from_str(&handle_line(state, &line).unwrap()).unwrap()
    }
}
