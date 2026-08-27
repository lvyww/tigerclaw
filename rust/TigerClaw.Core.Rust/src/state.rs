use std::collections::{HashMap, HashSet, VecDeque};
use std::cell::Cell;
use std::path::Path;
use std::process::Child;
use std::sync::{Arc, Mutex};
use std::time::Instant;

use crate::lexicon::Lexicon;
use crate::config::Config;
use crate::decoder::{DecodeResult, SentenceDecoder};
use crate::early::EarlyCommitRuntime;
use crate::ngram::NgramModel;
use crate::ranks::CharacterRanks;
use crate::selection_keys::SelectionKeyBindings;
use crate::supplement::SupplementMatcher;

const KEY_REPLAY_CAPACITY: usize = 512;

/// A process started by the Rust core.  Keeping the actual `Child` handle is
/// intentional: shutdown can terminate exactly this process even if its PID
/// has since been recycled, without ever scanning or killing an unrelated
/// process with the same executable name.
#[derive(Debug)]
pub struct OwnedChild {
    pub executable: String,
    pub child: Child,
}

pub type OwnedChildren = Arc<Mutex<Vec<OwnedChild>>>;

pub fn register_owned_child(registry: &OwnedChildren, child: Child, executable: impl Into<String>) {
    let Ok(mut children) = registry.lock() else { return; };
    children.retain_mut(|owned| match owned.child.try_wait() {
        Ok(Some(_)) => false,
        Ok(None) | Err(_) => true,
    });
    children.push(OwnedChild {
        executable: executable.into(),
        child,
    });
}

/// Stop only processes for which this core retained a `Child` handle.  The
/// caller is responsible for any graceful signal (for example Native Hook's
/// named exit event) and may call this after a short grace period.
pub fn terminate_owned_children(registry: &OwnedChildren) -> usize {
    let Ok(mut children) = registry.lock() else { return 0; };
    let mut terminated = 0;
    for owned in children.iter_mut() {
        match owned.child.try_wait() {
            Ok(Some(_)) => {}
            Ok(None) => {
                if owned.child.kill().is_ok() {
                    let _ = owned.child.wait();
                    terminated += 1;
                }
            }
            Err(_) => {
                let _ = owned.child.kill();
                let _ = owned.child.wait();
            }
        }
    }
    children.clear();
    terminated
}

#[derive(Debug)]
pub struct CoreState {
    pub keyboard_open: bool,
    pub input_buffer: String,
    pub ime_active: bool,
    pub caret_x: i32,
    pub caret_y: i32,
    pub caret_width: i32,
    pub caret_height: i32,
    pub shift_left_down: bool,
    pub shift_right_down: bool,
    pub shift_chord_used: bool,
    /// A custom selection binding consumed a physical Shift press.  The
    /// matching key-up must not be mistaken for a Shift language toggle.
    pub skip_shift_toggle_once: bool,
    /// State for the physical Ctrl+Space chord sent by the TSF bridge.
    /// This is separate from the language-bar `ctrl_space` request.
    pub ctrl_chord_down: bool,
    pub space_chord_down: bool,
    pub ctrl_space_armed: bool,
    pub ctrl_space_switched: bool,
    pub last_ctrl_up: Option<Instant>,
    pub one_shot_vk: u64,
    pub config_version: u64,
    pub lexicon_version: u64,
    key_responses: HashMap<String, String>,
    key_response_order: VecDeque<String>,
    pub lexicon: Lexicon,
    pub pinyin_lexicon: Lexicon,
    pub selected_candidate: usize,
    pub config: Config,
    pub config_path: Option<String>,
    pub lexicon_path: Option<String>,
    pub mixed_prefix: String,
    pub mixed_segments: Vec<(String, String)>,
    /// Candidate chosen for a completed mixed-input segment.  C# keeps this
    /// as a soft hint so a re-decode after the next key/backspace does not
    /// unexpectedly jump back to the table's first candidate.
    pub mixed_preferred: HashMap<usize, String>,
    pub raw_input: String,
    pub sentence_candidates: Vec<String>,
    pub sentence_segmented: Vec<String>,
    pub sentence_model: Option<Arc<NgramModel>>,
    pub ranks: CharacterRanks,
    pub supplements: SupplementMatcher,
    pub sentence_decoder: Arc<Mutex<SentenceDecoder>>,
    pub sentence_decode_sync: bool,
    pub sentence_generation: u64,
    pub sentence_decoded_raw: String,
    pub sentence_decoded_lexicon_version: u64,
    pub sentence_last_result: DecodeResult,
    pub shared_lexicon: Arc<Lexicon>,
    pub shared_ranks: Arc<CharacterRanks>,
    pub shared_supplements: Arc<SupplementMatcher>,
    pub sentence_worker: Arc<Mutex<SentenceWorkerControl>>,
    pub sentence_completed: Arc<Mutex<Option<CompletedSentence>>>,
    pub sentence_rerank_completed: Arc<Mutex<Option<CompletedRerank>>>,
    /// Matching Qwen acceptance constrains the next early-commit proposal in
    /// the same way as C#'s `_sentenceNeuralAcceptedRaw`/top-text pair.
    pub sentence_neural_accepted_raw: String,
    pub sentence_neural_top_text: String,
    /// Latest-only neural rerank request.  A slow Qwen call must never build
    /// an unbounded thread queue while the user is typing; the worker drops
    /// superseded generations before starting the next request.
    pub sentence_rerank_worker: Arc<Mutex<SentenceRerankWorkerControl>>,
    /// Handles for UI/Sentence processes launched by this core.  The list is
    /// deliberately ownership-based rather than a process-name scan.
    pub owned_children: OwnedChildren,
    pub early: EarlyCommitRuntime,
    pub sentence_exe: Option<String>,
    pub qwen_model: Option<String>,
    pub base_dir: Option<String>,
    pub focus_hwnd: i64,
    pub focus_process_id: i64,
    pub commit_history: Vec<String>,
    /// Last normalized text emitted by a handled candidate.  Code-table
    /// entries may refer to it through the C# ``{重复上屏}`` macro.
    pub repeat_buffer: String,
    /// Text-element history used by Dialog's `history_len` API.  Keep this
    /// separate from commit groups so a multi-character commit and pass-through
    /// keys have the same C# stack semantics.
    pub send_history: Vec<String>,
    pub cancel_composition_pending: Cell<bool>,
    pub selection_keys: SelectionKeyBindings,
    pub selection_key_config_path: Option<String>,
    pub handled_modifier_selection_keys: HashSet<u64>,
    pub left_single_quote: bool,
    pub left_double_quote: bool,
    pub quote_down_seen: bool,
    pub deleted_single_quote_armed: bool,
    pub deleted_double_quote_armed: bool,
    pub dot_after_digit_armed: bool,
    pub normal_page_index: usize,
    pub normal_page_code: String,
    pub pinyin_mode: bool,
    pub uppercase_mode: bool,
    pub hook_native_disabled: bool,
    pub native_hook_active: bool,
    pub shutdown_requested: bool,
    pub focus_process_name: String,
    pub focus_class_name: String,
    pub focus_window_title: String,
    pub fresh_caret: bool,
    pub fresh_caret_deadline: Option<Instant>,
    pub sound_seq: i64,
    pub sound_vk: i32,
}

impl Default for CoreState {
    fn default() -> Self {
        Self {
            keyboard_open: true,
            input_buffer: String::new(),
            ime_active: false,
            caret_x: 0,
            caret_y: 0,
            caret_width: 2,
            caret_height: 20,
            shift_left_down: false,
            shift_right_down: false,
            shift_chord_used: false,
            skip_shift_toggle_once: false,
            ctrl_chord_down: false,
            space_chord_down: false,
            ctrl_space_armed: false,
            ctrl_space_switched: false,
            last_ctrl_up: None,
            one_shot_vk: 0,
            config_version: 1,
            lexicon_version: 1,
            key_responses: HashMap::new(),
            key_response_order: VecDeque::new(),
            lexicon: Lexicon::default(),
            pinyin_lexicon: Lexicon::default(),
            selected_candidate: 0,
            config: Config::default(),
            config_path: None,
            lexicon_path: None,
            mixed_prefix: String::new(),
            mixed_segments: Vec::new(),
            mixed_preferred: HashMap::new(),
            raw_input: String::new(),
            sentence_candidates: Vec::new(),
            sentence_segmented: Vec::new(),
            sentence_model: None,
            ranks: CharacterRanks::default(),
            supplements: SupplementMatcher::empty(),
            sentence_decoder: Arc::new(Mutex::new(SentenceDecoder::default())),
            sentence_decode_sync: true,
            sentence_generation: 0,
            sentence_decoded_raw: String::new(),
            sentence_decoded_lexicon_version: 0,
            sentence_last_result: DecodeResult::default(),
            shared_lexicon: Arc::new(Lexicon::default()),
            shared_ranks: Arc::new(CharacterRanks::default()),
            shared_supplements: Arc::new(SupplementMatcher::empty()),
            sentence_worker: Arc::new(Mutex::new(SentenceWorkerControl::default())),
            sentence_completed: Arc::new(Mutex::new(None)),
            sentence_rerank_completed: Arc::new(Mutex::new(None)),
            sentence_neural_accepted_raw: String::new(),
            sentence_neural_top_text: String::new(),
            sentence_rerank_worker: Arc::new(Mutex::new(SentenceRerankWorkerControl::default())),
            owned_children: Arc::new(Mutex::new(Vec::new())),
            early: EarlyCommitRuntime::default(),
            sentence_exe: None,
            qwen_model: None,
            base_dir: None,
            focus_hwnd: 0,
            focus_process_id: 0,
            commit_history: Vec::new(),
            repeat_buffer: "重复上屏".to_owned(),
            send_history: Vec::new(),
            cancel_composition_pending: Cell::new(false),
            selection_keys: SelectionKeyBindings::default(),
            selection_key_config_path: None,
            handled_modifier_selection_keys: HashSet::new(),
            left_single_quote: true,
            left_double_quote: true,
            quote_down_seen: false,
            deleted_single_quote_armed: false,
            deleted_double_quote_armed: false,
            dot_after_digit_armed: false,
            normal_page_index: 0,
            normal_page_code: String::new(),
            pinyin_mode: false,
            uppercase_mode: false,
            hook_native_disabled: false,
            native_hook_active: false,
            shutdown_requested: false,
            focus_process_name: String::new(),
            focus_class_name: String::new(),
            focus_window_title: String::new(),
            fresh_caret: false,
            fresh_caret_deadline: None,
            sound_seq: 0,
            sound_vk: 0,
        }
    }
}

impl CoreState {
    pub fn replayed_key_response(
        &self,
        client_session: &str,
        event_id: &str,
        response_seq: i64,
    ) -> Option<String> {
        let key = replay_key(client_session, event_id)?;
        let cached = self.key_responses.get(&key)?;
        let mut response: serde_json::Value = serde_json::from_str(cached).ok()?;
        response["seq"] = response_seq.into();
        Some(response.to_string())
    }

    pub fn reload_supplements(&mut self) {
        self.supplements = self
            .lexicon_path
            .as_deref()
            .and_then(|path| {
                let path = Path::new(path);
                if path.is_dir() { Some(path) } else { path.parent() }
            })
            .map(SupplementMatcher::load_from_code_table_dir)
            .unwrap_or_else(SupplementMatcher::empty);
        self.publish_sentence_assets();
        if let Ok(mut decoder) = self.sentence_decoder.lock() {
            decoder.reset();
        }
    }

    pub fn publish_sentence_assets(&mut self) {
        self.shared_lexicon = Arc::new(self.lexicon.clone());
        self.shared_ranks = Arc::new(self.ranks.clone());
        self.shared_supplements = Arc::new(self.supplements.clone());
    }

    pub fn store_key_response(&mut self, client_session: &str, event_id: &str, response: &str) {
        let Some(key) = replay_key(client_session, event_id) else {
            return;
        };
        if self.key_responses.contains_key(&key) {
            self.key_responses.insert(key, response.to_owned());
            return;
        }

        self.key_responses.insert(key.clone(), response.to_owned());
        self.key_response_order.push_back(key);
        while self.key_responses.len() > KEY_REPLAY_CAPACITY {
            if let Some(oldest) = self.key_response_order.pop_front() {
                self.key_responses.remove(&oldest);
            }
        }
    }

    pub fn reset_physical_chords(&mut self) {
        self.ctrl_chord_down = false;
        self.space_chord_down = false;
        self.ctrl_space_armed = false;
        self.ctrl_space_switched = false;
        self.last_ctrl_up = None;
        self.shift_left_down = false;
        self.shift_right_down = false;
        self.shift_chord_used = false;
        self.skip_shift_toggle_once = false;
        self.one_shot_vk = 0;
        self.handled_modifier_selection_keys.clear();
    }

    pub fn reset_composition_transients(&mut self) {
        self.normal_page_index = 0;
        self.normal_page_code.clear();
        self.pinyin_mode = false;
        self.uppercase_mode = false;
        self.quote_down_seen = false;
        self.deleted_single_quote_armed = false;
        self.deleted_double_quote_armed = false;
        self.dot_after_digit_armed = false;
    }
}

#[derive(Debug, Clone)]
pub struct SentenceJob {
    pub generation: u64,
    pub raw: String,
    pub prefix: String,
    pub include_early: bool,
    pub lexicon_version: u64,
}

#[derive(Debug, Default)]
pub struct SentenceWorkerControl {
    pub job: Option<SentenceJob>,
    pub running: bool,
}

#[derive(Debug, Clone)]
pub struct CompletedSentence {
    pub generation: u64,
    pub lexicon_version: u64,
    pub result: DecodeResult,
}

#[derive(Debug, Clone)]
pub struct CompletedRerank {
    pub generation: u64,
    pub raw_code: String,
    pub committed_prefix: String,
    pub texts: Vec<String>,
}

#[derive(Debug, Clone)]
pub struct SentenceRerankJob {
    pub generation: u64,
    pub raw_code: String,
    pub committed_prefix: String,
    /// Full candidates sent to Qwen, including any already committed prefix.
    pub texts: Vec<String>,
    /// The same candidates as currently displayed by Overlay.  Rerank order
    /// is returned in this representation so the UI list remains comparable
    /// after a partial early commit.
    pub display_texts: Vec<String>,
    pub bases: Vec<f64>,
    pub exe: String,
    pub model: String,
}

#[derive(Debug, Default)]
pub struct SentenceRerankWorkerControl {
    pub job: Option<SentenceRerankJob>,
    pub running: bool,
}

fn replay_key(client_session: &str, event_id: &str) -> Option<String> {
    if client_session.trim().is_empty() || event_id.trim().is_empty() {
        None
    } else {
        Some(format!("{client_session}\n{event_id}"))
    }
}
