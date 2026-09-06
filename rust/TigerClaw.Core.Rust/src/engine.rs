//! Process-scoped resources and composition-scoped engine sessions.
//!
//! `protocol::handle_line` remains the Windows compatibility adapter. New
//! frontends use this module so composition data cannot bleed between clients.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, Weak};

use crate::config::Config;
use crate::lexicon::Lexicon;
use crate::ngram::NgramModel;
use crate::protocol;
use crate::state::CoreState;

#[derive(Debug)]
pub enum EngineError {
    InvalidRuntime,
    InvalidState,
    UnsupportedCommand,
    Internal,
}

#[derive(Debug, Default)]
pub struct EngineResources {
    pub config: Config,
    pub lexicon: Lexicon,
    pub sentence_model: Option<Arc<NgramModel>>,
}

#[derive(Debug)]
struct ResourceSnapshot {
    epoch: u64,
    config: Arc<Config>,
    lexicon: Lexicon,
    sentence_model: Option<Arc<NgramModel>>,
}

impl ResourceSnapshot {
    fn from_resources(epoch: u64, resources: EngineResources) -> Self {
        Self {
            epoch,
            config: Arc::new(resources.config),
            lexicon: resources.lexicon,
            sentence_model: resources.sentence_model,
        }
    }
}

#[derive(Debug)]
struct RuntimeState {
    next_session_id: u64,
    keyboard_open: bool,
    resources: Arc<ResourceSnapshot>,
    sessions: HashMap<u64, Weak<SessionInner>>,
}

#[derive(Debug)]
struct RuntimeInner {
    state: Mutex<RuntimeState>,
}

#[derive(Clone, Debug)]
pub struct EngineRuntime {
    inner: Arc<RuntimeInner>,
}

#[derive(Debug, Eq, PartialEq)]
enum SessionLifecycle {
    Inactive,
    Active,
    Destroyed,
}

#[derive(Debug)]
struct SessionState {
    lifecycle: SessionLifecycle,
    resource_epoch: u64,
    core: CoreState,
}

#[derive(Debug)]
struct SessionInner {
    id: u64,
    runtime: Weak<RuntimeInner>,
    state: Mutex<SessionState>,
}

#[derive(Clone, Debug)]
pub struct EngineSession {
    inner: Arc<SessionInner>,
}

impl Default for EngineRuntime {
    fn default() -> Self {
        Self::new(EngineResources::default())
    }
}

impl EngineRuntime {
    pub fn new(resources: EngineResources) -> Self {
        let snapshot = Arc::new(ResourceSnapshot::from_resources(1, resources));
        Self {
            inner: Arc::new(RuntimeInner {
                state: Mutex::new(RuntimeState {
                    next_session_id: 1,
                    keyboard_open: true,
                    resources: snapshot,
                    sessions: HashMap::new(),
                }),
            }),
        }
    }

    pub fn create_session(&self) -> Result<EngineSession, EngineError> {
        let mut runtime = self.inner.state.lock().map_err(|_| EngineError::Internal)?;
        let id = runtime.next_session_id;
        runtime.next_session_id = runtime.next_session_id.saturating_add(1);
        let snapshot = Arc::clone(&runtime.resources);
        let mut core = CoreState::with_resources(
            Arc::clone(&snapshot.config),
            snapshot.lexicon.clone(),
            snapshot.sentence_model.clone(),
        );
        core.keyboard_open = runtime.keyboard_open;
        let inner = Arc::new(SessionInner {
            id,
            runtime: Arc::downgrade(&self.inner),
            state: Mutex::new(SessionState {
                lifecycle: SessionLifecycle::Inactive,
                resource_epoch: snapshot.epoch,
                core,
            }),
        });
        runtime.sessions.insert(id, Arc::downgrade(&inner));
        Ok(EngineSession { inner })
    }

    /// Atomically advances the resource epoch and clears every composition.
    pub fn replace_resources(&self, resources: EngineResources) -> Result<u64, EngineError> {
        // Keep the runtime lock while every session observes the new snapshot.
        // Key handling takes the same runtime-then-session lock order, so a key
        // is either fully processed on the old epoch or begins after this barrier.
        let mut runtime = self.inner.state.lock().map_err(|_| EngineError::Internal)?;
        let epoch = runtime.resources.epoch.saturating_add(1);
        let snapshot = Arc::new(ResourceSnapshot::from_resources(epoch, resources));
        runtime.resources = Arc::clone(&snapshot);
        runtime.sessions.retain(|_, session| session.strong_count() > 0);
        let sessions = runtime.sessions.values().filter_map(Weak::upgrade).collect::<Vec<_>>();
        for session in sessions {
            let mut state = session.state.lock().map_err(|_| EngineError::Internal)?;
            if state.lifecycle == SessionLifecycle::Destroyed {
                continue;
            }
            state.core.config = Arc::clone(&snapshot.config);
            state.core.lexicon = snapshot.lexicon.clone();
            state.core.sentence_model = snapshot.sentence_model.clone();
            state.core.clear_composition();
            state.resource_epoch = snapshot.epoch;
        }
        Ok(snapshot.epoch)
    }

    pub fn resource_epoch(&self) -> Result<u64, EngineError> {
        self.inner
            .state
            .lock()
            .map(|state| state.resources.epoch)
            .map_err(|_| EngineError::Internal)
    }
}

impl EngineSession {
    pub fn id(&self) -> u64 {
        self.inner.id
    }

    pub fn activate(&self) -> Result<(), EngineError> {
        self.with_active_state(false, |state, _| {
            if state.lifecycle != SessionLifecycle::Inactive {
                return Err(EngineError::InvalidState);
            }
            state.lifecycle = SessionLifecycle::Active;
            Ok(())
        })
    }

    pub fn deactivate(&self) -> Result<(), EngineError> {
        self.with_active_state(false, |state, _| {
            if state.lifecycle != SessionLifecycle::Active {
                return Err(EngineError::InvalidState);
            }
            state.core.clear_composition();
            state.lifecycle = SessionLifecycle::Inactive;
            Ok(())
        })
    }

    pub fn destroy(&self) -> Result<(), EngineError> {
        let runtime = self.inner.runtime.upgrade().ok_or(EngineError::InvalidRuntime)?;
        let mut runtime_state = runtime.state.lock().map_err(|_| EngineError::Internal)?;
        let mut state = self.inner.state.lock().map_err(|_| EngineError::Internal)?;
        if state.lifecycle == SessionLifecycle::Destroyed {
            return Err(EngineError::InvalidState);
        }
        state.core.clear_composition();
        state.lifecycle = SessionLifecycle::Destroyed;
        runtime_state.sessions.remove(&self.inner.id);
        Ok(())
    }

    pub fn handle_line(&self, line: &str) -> Result<Option<String>, EngineError> {
        if !is_session_command(line) {
            return Err(EngineError::UnsupportedCommand);
        }
        self.with_active_state(true, |state, runtime| {
            state.core.keyboard_open = runtime.keyboard_open;
            let response = protocol::handle_line(&mut state.core, line);
            if is_ctrl_space(line) {
                runtime.keyboard_open = state.core.keyboard_open;
            }
            Ok(response)
        })
    }

    pub fn resource_epoch(&self) -> Result<u64, EngineError> {
        self.with_active_state(false, |state, _| Ok(state.resource_epoch))
    }

    fn with_active_state<T>(
        &self,
        require_active: bool,
        operation: impl FnOnce(&mut SessionState, &mut RuntimeState) -> Result<T, EngineError>,
    ) -> Result<T, EngineError> {
        let runtime = self.inner.runtime.upgrade().ok_or(EngineError::InvalidRuntime)?;
        let mut runtime_state = runtime.state.lock().map_err(|_| EngineError::Internal)?;
        let mut state = self.inner.state.lock().map_err(|_| EngineError::Internal)?;
        if state.lifecycle == SessionLifecycle::Destroyed {
            return Err(EngineError::InvalidState);
        }
        if require_active && state.lifecycle != SessionLifecycle::Active {
            return Err(EngineError::InvalidState);
        }
        operation(&mut state, &mut runtime_state)
    }
}

fn is_ctrl_space(line: &str) -> bool {
    serde_json::from_str::<serde_json::Value>(line)
        .ok()
        .and_then(|message| message.get("type").and_then(|value| value.as_str()).map(str::to_owned))
        .as_deref() == Some("ctrl_space")
}

/// Resource/configuration mutations use `EngineRuntime::replace_resources`.
/// This prevents the legacy JSON handler from accidentally creating a
/// session-local lexicon or configuration fork in a multi-session frontend.
fn is_session_command(line: &str) -> bool {
    matches!(
        serde_json::from_str::<serde_json::Value>(line)
            .ok()
            .and_then(|message| message.get("type").and_then(|value| value.as_str()).map(str::to_owned))
            .as_deref(),
        Some(
            "hello"
                | "query_state"
                | "ctrl_space"
                | "caret"
                | "ime_active"
                | "focus"
                | "composition_canceled"
                | "hook_native_disabled"
                | "key"
        )
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::Value;

    fn runtime_with_entries(entries: &[(&str, &str)]) -> EngineRuntime {
        let mut lexicon = Lexicon::default();
        for (code, text) in entries {
            assert!(lexicon.add_candidate(code, text));
        }
        EngineRuntime::new(EngineResources {
            config: Config::default(),
            lexicon,
            sentence_model: None,
        })
    }

    fn key(session: &EngineSession, seq: u64, vk: u64) -> Value {
        let response = session
            .handle_line(&format!(r#"{{"type":"key","seq":{seq},"action":"down","vk":{vk}}}"#))
            .unwrap()
            .unwrap();
        serde_json::from_str(&response).unwrap()
    }

    fn query(session: &EngineSession, seq: u64) -> Value {
        let response = session
            .handle_line(&format!(r#"{{"type":"query_state","seq":{seq}}}"#))
            .unwrap()
            .unwrap();
        serde_json::from_str(&response).unwrap()
    }

    #[test]
    fn sessions_isolate_composition_lifecycle_and_destruction() {
        let runtime = runtime_with_entries(&[("abc", "甲"), ("xyz", "乙"), ("xyzd", "丙")]);
        let a = runtime.create_session().unwrap();
        let b = runtime.create_session().unwrap();
        a.activate().unwrap();
        b.activate().unwrap();

        for (seq, vk) in [65, 66, 67].into_iter().enumerate() {
            key(&a, (seq + 1) as u64, vk);
        }
        for (seq, vk) in [88, 89, 90].into_iter().enumerate() {
            key(&b, (seq + 1) as u64, vk);
        }
        assert_eq!(query(&a, 4)["input_buffer"], "abc");
        assert_eq!(query(&b, 4)["input_buffer"], "xyz");

        assert_eq!(key(&a, 5, 32)["commit_text"], "甲");
        assert_eq!(query(&b, 5)["input_buffer"], "xyz");

        a.deactivate().unwrap();
        assert!(a.handle_line(r#"{"type":"query_state","seq":6}"#).is_err());
        assert_eq!(key(&b, 6, 68)["input_buffer"], "xyzd");
        a.destroy().unwrap();
        assert_eq!(query(&b, 7)["input_buffer"], "xyzd");
    }

    #[test]
    fn resource_replacement_clears_sessions_at_a_single_new_epoch() {
        let runtime = runtime_with_entries(&[("abc", "甲")]);
        let a = runtime.create_session().unwrap();
        let b = runtime.create_session().unwrap();
        a.activate().unwrap();
        b.activate().unwrap();
        key(&a, 1, 65);
        key(&b, 1, 66);

        let epoch = runtime.replace_resources(EngineResources::default()).unwrap();
        assert_eq!(epoch, 2);
        assert_eq!(runtime.resource_epoch().unwrap(), epoch);
        assert_eq!(a.resource_epoch().unwrap(), epoch);
        assert_eq!(b.resource_epoch().unwrap(), epoch);
        assert_eq!(query(&a, 2)["input_buffer"], "");
        assert_eq!(query(&b, 2)["input_buffer"], "");
    }

    #[test]
    fn keyboard_mode_is_global_while_composition_remains_session_owned() {
        let runtime = runtime_with_entries(&[("abc", "甲"), ("xyz", "乙")]);
        let a = runtime.create_session().unwrap();
        let b = runtime.create_session().unwrap();
        a.activate().unwrap();
        b.activate().unwrap();
        key(&b, 1, 88);

        a.handle_line(r#"{"type":"ctrl_space","seq":1}"#).unwrap();
        let state = query(&b, 2);
        assert_eq!(state["keyboard_open"], false);
        assert_eq!(state["input_buffer"], "x");
    }

    #[test]
    fn resource_mutation_is_not_a_session_command() {
        let runtime = EngineRuntime::default();
        let session = runtime.create_session().unwrap();
        session.activate().unwrap();
        assert!(matches!(
            session.handle_line(r#"{"type":"set_config","seq":1,"key":"TAB清屏","value":"是"}"#),
            Err(EngineError::UnsupportedCommand)
        ));
    }
}
