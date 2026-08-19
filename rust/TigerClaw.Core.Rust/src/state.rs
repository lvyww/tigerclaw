use std::collections::{HashMap, VecDeque};
use std::cell::Cell;

use crate::lexicon::Lexicon;
use crate::config::Config;
use crate::ngram::NgramModel;

const KEY_REPLAY_CAPACITY: usize = 512;

#[derive(Debug)]
pub struct CoreState {
    pub keyboard_open: bool,
    pub input_buffer: String,
    pub ime_active: bool,
    pub caret_x: i32,
    pub caret_y: i32,
    pub config_version: u64,
    pub lexicon_version: u64,
    key_responses: HashMap<String, String>,
    key_response_order: VecDeque<String>,
    pub lexicon: Lexicon,
    pub selected_candidate: usize,
    pub config: Config,
    pub config_path: Option<String>,
    pub lexicon_path: Option<String>,
    pub mixed_prefix: String,
    pub mixed_segments: Vec<(String, String)>,
    pub raw_input: String,
    pub sentence_candidates: Vec<String>,
    pub sentence_model: Option<NgramModel>,
    pub sentence_exe: Option<String>,
    pub qwen_model: Option<String>,
    pub base_dir: Option<String>,
    pub focus_hwnd: i64,
    pub focus_process_id: i64,
    pub commit_history: Vec<String>,
    pub cancel_composition_pending: Cell<bool>,
}

impl Default for CoreState {
    fn default() -> Self {
        Self {
            keyboard_open: true,
            input_buffer: String::new(),
            ime_active: false,
            caret_x: 0,
            caret_y: 0,
            config_version: 1,
            lexicon_version: 1,
            key_responses: HashMap::new(),
            key_response_order: VecDeque::new(),
            lexicon: Lexicon::default(),
            selected_candidate: 0,
            config: Config::default(),
            config_path: None,
            lexicon_path: None,
            mixed_prefix: String::new(),
            mixed_segments: Vec::new(),
            raw_input: String::new(),
            sentence_candidates: Vec::new(),
            sentence_model: None,
            sentence_exe: None,
            qwen_model: None,
            base_dir: None,
            focus_hwnd: 0,
            focus_process_id: 0,
            commit_history: Vec::new(),
            cancel_composition_pending: Cell::new(false),
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
}

fn replay_key(client_session: &str, event_id: &str) -> Option<String> {
    if client_session.trim().is_empty() || event_id.trim().is_empty() {
        None
    } else {
        Some(format!("{client_session}\n{event_id}"))
    }
}
