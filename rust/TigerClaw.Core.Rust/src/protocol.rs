use serde_json::{Value, json};

use crate::state::CoreState;

pub const PROTOCOL_VERSION: u64 = 1;

pub fn handle_line(state: &mut CoreState, line: &str) -> Option<String> {
    let message: Value = match serde_json::from_str(line) {
        Ok(value) => value,
        Err(_) => return Some(simple_response(state, 0, false, json!({}))),
    };
    let message_type = message.get("type").and_then(Value::as_str).unwrap_or("");
    let seq = message.get("seq").and_then(Value::as_i64).unwrap_or(0);

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
                "lexicon_loaded": state.lexicon_path.is_some(),
                "core_path": std::env::current_exe()
                    .ok()
                    .and_then(|path| path.into_os_string().into_string().ok())
                    .unwrap_or_default(),
            })
            .to_string(),
        ),
        "query_state" => Some(state_response(state, seq, false)),
        "reload_config" => Some(reload_config(state, &message, seq)),
        "reload_mb" => Some(reload_lexicon(state, &message, seq)),
        "get_config" => Some(config_response(state, seq)),
        "set_config" => Some(set_config(state, &message, seq)),
        "get_schema_list" => Some(simple_response(state, seq, true, json!({"schema_list":"","current_schema":""}))),
        "construct_ci" => Some(simple_response(state, seq, true, json!({"code":state.lexicon.construct_code(string(&message, "text"))}))),
        "get_last_ci" => {
            let history_len = message.get("history_len").and_then(Value::as_u64).unwrap_or(0) as usize;
            let text = if history_len < state.commit_history.len() { state.commit_history[state.commit_history.len() - history_len - 1].clone() } else { String::new() };
            Some(simple_response(state, seq, true, json!({"text":text})))
        },
        "get_selection_key_config" => Some(simple_response(state, seq, true, json!({"config_text":"","default_text":"","config_path":""}))),
        "set_selection_key_config" => Some(simple_response(state, seq, true, json!({"config_text": string(&message, "config_text")}))),
        "get_send_history_count" => Some(simple_response(state, seq, true, json!({"count":0}))),
        "add_ci" => Some(add_ci(state, &message, seq)),
        "open_official" | "open_mb_folder" | "export_mb" => Some(state_response(state, seq, false)),
        "exit_core" => { std::process::exit(0); }
        "show_menu" => Some(launch_ui(state, seq, "TigerClaw.Overlay.exe", "--menu")),
        "show_config" => Some(launch_ui(state, seq, "TigerClaw.Dialog.exe", "--config")),
        "show_addci" => Some(launch_ui(state, seq, "TigerClaw.Dialog.exe", "--addci")),
        "ctrl_space" => {
            state.keyboard_open = !state.keyboard_open;
            state.input_buffer.clear();
            state.mixed_prefix.clear();
            state.mixed_segments.clear();
            state.raw_input.clear();
            state.sentence_candidates.clear();
            Some(state_response(state, seq, true))
        }
        "caret" => {
            state.caret_x = integer(&message, "x");
            state.caret_y = integer(&message, "y");
            None
        }
        "ime_active" => {
            state.ime_active = message
                .get("active")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            None
        }
        "focus" => { state.focus_hwnd=message.get("hwnd").and_then(Value::as_i64).unwrap_or(0); state.focus_process_id=message.get("processId").or_else(||message.get("process_id")).and_then(Value::as_i64).unwrap_or(0); None },
        "composition_canceled" => { clear_composition(state); None },
        "hook_native_disabled" => None,
        "key" => {
            let client_session = string(&message, "client_session");
            let event_id = string(&message, "event_id");
            if let Some(response) =
                state.replayed_key_response(client_session, event_id, seq)
            {
                return Some(response);
            }

            let response = handle_key(state, &message, seq);
            state.store_key_response(client_session, event_id, &response);
            Some(response)
        }
        _ => Some(state_response(state, seq, false)),
    }
}

fn handle_key(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let action = message.get("action").and_then(Value::as_str).unwrap_or("");
    if action != "down" || !state.keyboard_open {
        return state_response(state, seq, false);
    }

    let vk = message.get("vk").and_then(Value::as_u64).unwrap_or(0);
    let ctrl = boolean(message, "ctrl");
    let alt = boolean(message, "alt");
    let win = boolean(message, "win");
    if ctrl || alt || win {
        return state_response(state, seq, false);
    }

    match vk {
        0x41..=0x5a => {
            let mut character = char::from_u32(vk as u32).unwrap_or_default();
            let uppercase = boolean(message, "shift") ^ boolean(message, "capsLock");
            if !uppercase {
                character = character.to_ascii_lowercase();
            }
            if !state.config.mixed_input
                && !state.config.sentence_input
                && state.input_buffer.chars().count() >= state.config.max_code_length
            {
                return state_response(state, seq, false);
            }
            state.raw_input.push(character);
            if state.config.sentence_input {
                state.input_buffer.push(character);
                state.sentence_candidates = crate::sentence::decode(
                    &state.input_buffer,
                    &state.lexicon,
                    state.config.max_code_length,
                    32,
                );
                if let Some(model) = state.sentence_model.as_ref() {
                    state.sentence_candidates.sort_by(|a, b| {
                        model.score(b).partial_cmp(&model.score(a)).unwrap_or(std::cmp::Ordering::Equal)
                    });
                }
                rerank_sentence(state);
                state.selected_candidate = 0;
                return state_response(state, seq, true);
            }
            if state.config.mixed_input
                && state.input_buffer.chars().count() >= state.config.max_code_length
            {
                let candidates = state.lexicon.candidates(&state.input_buffer);
                let segment_code = std::mem::take(&mut state.input_buffer);
                let candidate = candidates.first().cloned().unwrap_or_default();
                state.mixed_segments.push((segment_code, candidate.clone()));
                state.mixed_prefix.push_str(&candidate);
            }
            state.input_buffer.push(character);
            state.selected_candidate = 0;
            let candidates = state.lexicon.candidates(&state.input_buffer);
            if state.config.auto_commit_no_repeat
                && state.input_buffer.chars().count() >= state.config.max_code_length
                && candidates.len() == 1
            {
                return commit_response(state, seq, true, candidates[0].clone());
            }
            state_response(state, seq, true)
        }
        0x08 if !state.input_buffer.is_empty() => {
            state.input_buffer.pop();
            state.raw_input.pop();
            if state.config.sentence_input {
                state.sentence_candidates = crate::sentence::decode(
                    &state.input_buffer,
                    &state.lexicon,
                    state.config.max_code_length,
                    32,
                );
                if let Some(model) = state.sentence_model.as_ref() {
                    state.sentence_candidates.sort_by(|a, b| {
                        model.score(b).partial_cmp(&model.score(a)).unwrap_or(std::cmp::Ordering::Equal)
                    });
                }
                rerank_sentence(state);
            }
            state.selected_candidate = 0;
            state_response(state, seq, true)
        }
        0x08 if state.config.mixed_input && !state.mixed_segments.is_empty() => {
            if let Some((code, text)) = state.mixed_segments.pop() {
                if !text.is_empty() {
                    let _ = state.mixed_prefix.strip_suffix(&text).map(|_| ());
                    let new_len = state.mixed_prefix.len().saturating_sub(text.len());
                    state.mixed_prefix.truncate(new_len);
                }
                state.input_buffer = code;
                state.selected_candidate = 0;
                state_response(state, seq, true)
            } else {
                state_response(state, seq, false)
            }
        }
        0x1b if !state.input_buffer.is_empty() => {
            state.input_buffer.clear();
            state.mixed_prefix.clear();
            state.mixed_segments.clear();
            state.raw_input.clear();
            state.sentence_candidates.clear();
            state.selected_candidate = 0;
            state_response(state, seq, true)
        }
        0x27 | 0xba | 0xde | 0x20 | 0x31..=0x39 | 0x30
            if !state.input_buffer.is_empty() => {
            if (vk == 0xba && !state.config.semicolon_second)
                || (vk == 0xde && !state.config.quote_third)
            {
                return state_response(state, seq, false);
            }
            let candidates = if state.config.sentence_input {
                state.sentence_candidates.clone()
            } else {
                state.lexicon.candidates(&state.input_buffer)
            };
            let rank = if vk == 0x20 {
                state.selected_candidate
            } else if vk == 0xba {
                1
            } else if vk == 0xde || vk == 0x27 {
                2
            } else if vk == 0x30 {
                9
            } else {
                (vk - 0x31) as usize
            };
            if let Some(commit_text) = candidates.get(rank) {
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
            state_response(state, seq, false)
        }
        0x09 | 0x26 | 0x28 if !state.input_buffer.is_empty() => {
            let count = if state.config.sentence_input {
                state.sentence_candidates.len()
            } else {
                state.lexicon.candidates(&state.input_buffer).len()
            };
            if count == 0 {
                return state_response(state, seq, false);
            }
            if vk == 0x09 && state.config.tab_clear {
                state.input_buffer.clear();
                state.mixed_prefix.clear();
                state.mixed_segments.clear();
                state.raw_input.clear();
                state.selected_candidate = 0;
                return state_response(state, seq, true);
            }
            if vk == 0x28 || vk == 0x09 {
                state.selected_candidate = (state.selected_candidate + 1) % count;
            } else {
                state.selected_candidate =
                    (state.selected_candidate + count - 1) % count;
            }
            state_response(state, seq, true)
        }
        0xbc | 0xbe | 0xbf | 0xba | 0xde if !state.input_buffer.is_empty() => {
            let punctuation = match vk {
                0xbc => ",",
                0xbe => ".",
                0xbf => "/",
                0xba => ";",
                0xde => "'",
                _ => "",
            };
            let candidates = if state.config.sentence_input { state.sentence_candidates.clone() } else { state.lexicon.candidates(&state.input_buffer) };
            if !punctuation.is_empty() && !candidates.is_empty() {
                let index = state.selected_candidate.min(candidates.len() - 1);
                let mut commit = candidates[index].clone();
                if !state.mixed_prefix.is_empty() { commit = format!("{}{}", state.mixed_prefix, commit); }
                commit.push_str(punctuation);
                state.input_buffer.clear();
                state.mixed_prefix.clear();
                state.raw_input.clear();
                state.sentence_candidates.clear();
                state.selected_candidate = 0;
                return commit_response(state, seq, true, commit);
            }
            state_response(state, seq, false)
        }
        0x0d if !state.input_buffer.is_empty() && state.config.enter_clear => {
            state.input_buffer.clear();
            state.selected_candidate = 0;
            state.mixed_prefix.clear();
            state.mixed_segments.clear();
            state.raw_input.clear();
            state.sentence_candidates.clear();
            state_response(state, seq, true)
        }
        0x0d if !state.input_buffer.is_empty() => {
            let commit_text = if state.config.sentence_input {
                state.sentence_candidates.get(state.selected_candidate).cloned().unwrap_or_else(|| state.raw_input.clone())
            } else if state.config.mixed_input {
                std::mem::take(&mut state.raw_input)
            } else {
                std::mem::take(&mut state.input_buffer)
            };
            state.mixed_prefix.clear();
            state.mixed_segments.clear();
            state.input_buffer.clear();
            state.sentence_candidates.clear();
            commit_response(state, seq, true, commit_text)
        }
        _ => state_response(state, seq, false),
    }
}

fn commit_response(state: &mut CoreState, seq: i64, handled: bool, commit_text: String) -> String {
    if !commit_text.is_empty() {
        state.commit_history.push(commit_text.clone());
        if state.commit_history.len() > 64 { state.commit_history.remove(0); }
    }
    let mut response: Value = serde_json::from_str(&state_response(state, seq, handled))
        .expect("state response is valid JSON");
    response["commit_text"] = commit_text.into();
    response.to_string()
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
            state.config = config;
            state.config_path = Some(path);
            state.config_version = state.config_version.saturating_add(1);
            state.input_buffer.clear();
            state.selected_candidate = 0;
            state.cancel_composition_pending.set(true);
            simple_response(state, seq, true, json!({"cancel_composition":true,"config_version":state.config_version}))
        }
        Err(error) => json!({"type":"response","seq":seq,"success":false,"handled":false,"error":error.to_string()}).to_string(),
    }
}

fn reload_lexicon(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let path = string(message, "path");
    let path = if path.is_empty() {
        state.lexicon_path.clone().unwrap_or_default()
    } else {
        path.to_owned()
    };
    if path.is_empty() {
        return json!({"type":"response","seq":seq,"success":false,"handled":false,"error":"lexicon path is required"}).to_string();
    }
    match crate::lexicon::Lexicon::load(&path) {
        Ok(lexicon) => {
            state.lexicon = lexicon;
            state.lexicon_path = Some(path);
            state.lexicon_version = state.lexicon_version.saturating_add(1);
            state.input_buffer.clear();
            state.mixed_prefix.clear();
            state.raw_input.clear();
            state.sentence_candidates.clear();
            state.selected_candidate = 0;
            state_response(state, seq, true)
        }
        Err(error) => json!({"type":"response","seq":seq,"success":false,"handled":false,"error":error.to_string()}).to_string(),
    }
}

fn config_response(state: &CoreState, seq: i64) -> String {
    simple_response(state, seq, true, json!({"config_text": config_text(state), "config_version":state.config_version}))
}

fn set_config(state: &mut CoreState, message: &Value, seq: i64) -> String {
    if let (Some(key), Some(value)) = (message.get("key").and_then(Value::as_str), message.get("value").and_then(Value::as_str)) {
        let changed = apply_config_text_value(&mut state.config, key, value);
        if !changed { return simple_response(state, seq, false, json!({"changed":false,"config_version":state.config_version,"lexicon_version":state.lexicon_version,"error":"unsupported config key"})); }
        state.config_version = state.config_version.saturating_add(1);
        clear_composition(state);
        persist_config(state);
        return simple_response(state, seq, true, json!({"changed":true,"config_version":state.config_version,"lexicon_version":state.lexicon_version,"cancel_composition":true}));
    }
    let Some(config) = message.get("config").or_else(|| message.get("values")) else {
        return json!({"type":"response","seq":seq,"success":false,"handled":false,"error":"config object is required"}).to_string();
    };
    let mut next = state.config.clone();
    if let Some(value) = config.get("分号次选").or_else(|| config.get("semicolon_second")) { next.semicolon_second = value.as_bool().unwrap_or(next.semicolon_second); }
    if let Some(value) = config.get("引号三选").or_else(|| config.get("quote_third")) { next.quote_third = value.as_bool().unwrap_or(next.quote_third); }
    if let Some(value) = config.get("TAB清屏").or_else(|| config.get("tab_clear")) { next.tab_clear = value.as_bool().unwrap_or(next.tab_clear); }
    if let Some(value) = config.get("最大码长").or_else(|| config.get("max_code_length")) { if let Some(n)=value.as_u64() { next.max_code_length=(n as usize).clamp(1,16); } }
    if let Some(value) = config.get("中英文不限长混合输入").or_else(|| config.get("mixed_input")) { next.mixed_input = value.as_bool().unwrap_or(next.mixed_input); }
    if let Some(value) = config.get("整句输入").or_else(|| config.get("sentence_input")) { next.sentence_input = value.as_bool().unwrap_or(next.sentence_input); }
    state.config = next;
    state.config_version = state.config_version.saturating_add(1);
    state.input_buffer.clear();
    state.mixed_prefix.clear();
    state.mixed_segments.clear();
    state.raw_input.clear();
    state.sentence_candidates.clear();
    state.selected_candidate = 0;
    state.cancel_composition_pending.set(true);
    persist_config(state);
    simple_response(state, seq, true, json!({"changed":true,"config_version":state.config_version,"lexicon_version":state.lexicon_version,"cancel_composition":true}))
}

fn add_ci(state: &mut CoreState, message: &Value, seq: i64) -> String {
    let code = string(message, "code");
    let text = string(message, "text");
    let ok = state.lexicon.add_candidate(code, text);
    if ok { state.lexicon_version = state.lexicon_version.saturating_add(1); }
    if ok {
        if let Some(path) = state.lexicon_path.as_deref() {
            let _ = std::fs::OpenOptions::new().create(true).append(true).open(path).and_then(|mut file| std::io::Write::write_all(&mut file, state.lexicon.export_line(code, text).as_bytes()));
        }
    }
    simple_response(state, seq, ok, json!({"lexicon_version":state.lexicon_version}))
}

fn launch_ui(state: &CoreState, seq: i64, executable: &str, arg: &str) -> String {
    let Some(base)=state.base_dir.as_deref() else { return simple_response(state,seq,false,json!({"error":"base directory unavailable"})); };
    let path=std::path::Path::new(base).join(executable);
    let mut command = std::process::Command::new(&path);
    if !arg.is_empty() { command.arg(arg); }
    let ok=path.exists() && command.current_dir(base).spawn().is_ok();
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

fn clear_composition(state: &mut CoreState) { state.input_buffer.clear(); state.mixed_prefix.clear(); state.mixed_segments.clear(); state.raw_input.clear(); state.sentence_candidates.clear(); state.selected_candidate=0; }

fn simple_response(state: &CoreState, seq: i64, success: bool, extra: Value) -> String {
    let mut response: Value = serde_json::from_str(&state_response(state, seq, success)).expect("state response JSON");
    response["success"] = success.into(); response["handled"] = success.into();
    if let Value::Object(values) = extra { for (key, value) in values { response[key] = value; } }
    response.to_string()
}

fn config_text(state: &CoreState) -> String { format!("分号次选\t{}\n引号三选\t{}\nTAB清屏\t{}\n最大码长\t{}\n最大码长无重自动上屏\t{}\n空码自动清屏\t{}\n回车清屏\t{}\n每页候选个数\t{}\n中英文不限长混合输入\t{}\n整句输入\t{}\n", yes_no(state.config.semicolon_second),yes_no(state.config.quote_third),yes_no(state.config.tab_clear),state.config.max_code_length,yes_no(state.config.auto_commit_no_repeat),yes_no(state.config.empty_code_clear),yes_no(state.config.enter_clear),state.config.page_size,yes_no(state.config.mixed_input),yes_no(state.config.sentence_input)) }
fn yes_no(value: bool) -> &'static str { if value { "是" } else { "否" } }
fn apply_config_text_value(config: &mut crate::config::Config, key: &str, value: &str) -> bool { let enabled=matches!(value.trim().to_ascii_lowercase().as_str(),"是"|"true"|"on"|"1"); match key { "分号次选"=>config.semicolon_second=enabled, "引号三选"=>config.quote_third=enabled, "TAB清屏"=>config.tab_clear=enabled, "中英文不限长混合输入"=>config.mixed_input=enabled, "整句输入"=>config.sentence_input=enabled, "最大码长"=> { let Ok(n)=value.trim().parse::<usize>() else{return false}; config.max_code_length=n.clamp(1,16); }, "每页候选个数"=> { let Ok(n)=value.trim().parse::<usize>() else{return false}; config.page_size=n.clamp(1,10); }, _=>return false }; true }

fn state_response(state: &CoreState, seq: i64, handled: bool) -> String {
    let display_buffer = format!("{}{}", state.mixed_prefix, state.input_buffer);
    let candidates = if state.config.sentence_input {
        state.sentence_candidates.clone()
    } else {
        state.lexicon.candidates(&state.input_buffer)
    };
    let cancel = state.cancel_composition_pending.replace(false);
    // The flag is edge-triggered: callers receive it once, then normal responses resume.
    // This mirrors the C# bridge's deferred composition cancellation contract.
    json!({
        "type": "response",
        "seq": seq,
        "success": true,
        "handled": handled,
        "input_buffer": display_buffer,
        "candidates": candidates,
        "selected_index": state.selected_candidate,
        "keyboard_open": state.keyboard_open,
        "composition_tracking": false,
        "composition_pending": false,
        "cancel_composition": cancel,
        "ensure_system_layout_en": false,
        "native_hook_alt_backslash_toggle_enabled": false,
        "auto_switch_system_layout_enabled": false,
        "use_clipboard_commit": false,
        "clipboard_commit_whitelist": "",
        "config_version": state.config_version,
        "lexicon_version": state.lexicon_version,
    })
    .to_string()
}

fn persist_config(state: &CoreState) {
    let Some(path) = state.config_path.as_deref() else { return; };
    let _ = std::fs::write(path, config_text(state));
}

fn rerank_sentence(state: &mut CoreState) {
    let Some(exe) = state.sentence_exe.as_deref() else { return; };
    let Some(model) = state.qwen_model.as_deref() else { return; };
    let count = state.sentence_candidates.len().min(5);
    if count == 0 { return; }
    let candidates = state.sentence_candidates[..count].to_vec();
    let Some(scores) = crate::qwen::rerank(exe, model, &state.raw_input, &candidates, 1) else { return; };
    if scores.len() != count { return; }
    let mut ranked: Vec<(f64, String)> = candidates.into_iter().zip(scores).map(|(text, score)| {
        let ngram = state.sentence_model.as_ref().map(|m| m.score(&text)).unwrap_or(0.0);
        (ngram + 0.84 * score, text)
    }).collect();
    ranked.sort_by(|a, b| b.0.partial_cmp(&a.0).unwrap_or(std::cmp::Ordering::Equal));
    for (index, (_, text)) in ranked.into_iter().enumerate() { state.sentence_candidates[index] = text; }
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
    fn handshake_is_protocol_compatible() {
        let mut state = CoreState::default();
        let response = handle_line(&mut state, r#"{"type":"hello","seq":7}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["type"], "response");
        assert_eq!(value["seq"], 7);
        assert_eq!(value["protocol_version"], 1);
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
    fn selector_keys_and_tab_move_candidate_selection() {
        let path = std::env::temp_dir().join("tigerclaw-rust-selector-test.txt");
        std::fs::write(&path, "一\tabcd\n二\tabcd\n三\tabcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        for (seq, vk) in [65_u64, 66, 67, 68].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 1, vk);
            handle_line(&mut state, &line);
        }
        let response = handle_line(&mut state, r#"{"type":"key","seq":5,"action":"down","vk":9}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["selected_index"], 1);
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
        assert_eq!(value["handled"], false);
        let response = handle_line(&mut state, r#"{"type":"key","seq":6,"action":"down","vk":9}"#).unwrap();
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
        assert_eq!(value["input_buffer"], "abcd");
        assert_eq!(state.raw_input, "abcd");
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn sentence_mode_decodes_and_commits_selected_sentence() {
        let path = std::env::temp_dir().join("tigerclaw-rust-sentence-protocol-test.txt");
        std::fs::write(&path, "我\tab\n爱\tcd\n你\tcd\n").unwrap();
        let mut state = CoreState::default();
        state.lexicon = crate::lexicon::Lexicon::load(&path).unwrap();
        state.config.sentence_input = true;
        state.config.max_code_length = 2;
        for (seq, vk) in [65_u64, 66, 67, 68].into_iter().enumerate() {
            let line = format!(r#"{{"type":"key","seq":{},"action":"down","vk":{}}}"#, seq + 1, vk);
            handle_line(&mut state, &line);
        }
        let response = handle_line(&mut state, r#"{"type":"query_state","seq":5}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["candidates"].as_array().unwrap().len(), 2);
        handle_line(&mut state, r#"{"type":"key","seq":6,"action":"down","vk":9}"#);
        let response = handle_line(&mut state, r#"{"type":"key","seq":7,"action":"down","vk":32}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert!(value["commit_text"] == "我爱" || value["commit_text"] == "我你");
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
        assert_eq!(value["code"], "ab cd");
        let response = handle_line(&mut state, r#"{"type":"add_ci","seq":2,"code":"abcd","text":"我爱"}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["success"], true);
        state.commit_history.push("我爱".to_owned());
        let response = handle_line(&mut state, r#"{"type":"get_last_ci","seq":3,"history_len":0}"#).unwrap();
        let value: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(value["text"], "我爱");
        let _ = std::fs::remove_file(path);
    }
}
