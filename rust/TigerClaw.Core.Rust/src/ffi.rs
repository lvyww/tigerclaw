#![allow(clippy::missing_safety_doc)]

use std::collections::{HashMap, VecDeque};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::Mutex;

use serde_json::{json, Value};

use crate::engine::{EngineError, EngineRuntime, EngineSession};

pub const TIGERCLAW_ABI_VERSION: u32 = 1;

const KEY_DOWN: u32 = 1;
const KEY_UP: u32 = 2;
const SHIFT: u32 = 1;
const CONTROL: u32 = 1 << 1;
const ALT: u32 = 1 << 2;
const META: u32 = 1 << 3;
const CAPS_LOCK: u32 = 1 << 4;
const REPLAY_CAPACITY: usize = 512;

#[repr(C)]
#[allow(non_camel_case_types)]
pub struct tigerclaw_runtime_t { _private: [u8; 0] }
#[repr(C)]
#[allow(non_camel_case_types)]
pub struct tigerclaw_session_t { _private: [u8; 0] }
#[repr(C)]
#[allow(non_camel_case_types)]
pub struct tigerclaw_snapshot_t { _private: [u8; 0] }

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[allow(non_camel_case_types)]
pub enum tigerclaw_status_t {
    TIGERCLAW_OK = 0,
    TIGERCLAW_ERR_INVALID_ARGUMENT = 1,
    TIGERCLAW_ERR_INVALID_UTF8 = 2,
    TIGERCLAW_ERR_INVALID_HANDLE = 3,
    TIGERCLAW_ERR_INVALID_STATE = 4,
    TIGERCLAW_ERR_OUT_OF_MEMORY = 5,
    TIGERCLAW_ERR_RESOURCE_UNAVAILABLE = 6,
    TIGERCLAW_ERR_STALE_GENERATION = 7,
    TIGERCLAW_ERR_INTERNAL_ERROR = 8,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[allow(non_camel_case_types)]
pub struct tigerclaw_utf8_slice_t { pub ptr: *const u8, pub len: usize }
#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[allow(non_camel_case_types)]
pub struct tigerclaw_key_event_t {
    pub key: u32,
    pub action: u32,
    pub modifiers: u32,
    pub event_id: u64,
}

struct RuntimeHandle { engine: EngineRuntime }
struct SessionHandle {
    engine: EngineSession,
    command_lock: Mutex<()>,
    state: Mutex<FfiSessionState>,
}
struct FfiSessionState {
    revision: u64,
    replay: HashMap<u64, SnapshotData>,
    replay_order: VecDeque<u64>,
}
#[derive(Clone)]
struct SnapshotData {
    revision: u64,
    preedit: Vec<u8>,
    commit: Vec<u8>,
    candidates: Vec<Vec<u8>>,
    selected_index: usize,
}
struct SnapshotHandle { data: SnapshotData }

fn empty_slice() -> tigerclaw_utf8_slice_t {
    tigerclaw_utf8_slice_t { ptr: std::ptr::null(), len: 0 }
}
fn slice(bytes: &[u8]) -> tigerclaw_utf8_slice_t {
    if bytes.is_empty() { empty_slice() } else { tigerclaw_utf8_slice_t { ptr: bytes.as_ptr(), len: bytes.len() } }
}
fn guard<T>(fallback: T, action: impl FnOnce() -> T) -> T {
    catch_unwind(AssertUnwindSafe(action)).unwrap_or(fallback)
}
unsafe fn runtime_handle<'a>(ptr: *mut tigerclaw_runtime_t) -> Option<&'a RuntimeHandle> {
    unsafe { (ptr as *mut RuntimeHandle).as_ref() }
}
unsafe fn session_handle<'a>(ptr: *mut tigerclaw_session_t) -> Option<&'a SessionHandle> {
    unsafe { (ptr as *mut SessionHandle).as_ref() }
}
unsafe fn snapshot_handle<'a>(ptr: *const tigerclaw_snapshot_t) -> Option<&'a SnapshotHandle> {
    unsafe { (ptr as *const SnapshotHandle).as_ref() }
}
fn make_snapshot(data: SnapshotData) -> *mut tigerclaw_snapshot_t {
    Box::into_raw(Box::new(SnapshotHandle { data })) as *mut tigerclaw_snapshot_t
}
fn engine_status(error: EngineError) -> tigerclaw_status_t {
    match error {
        EngineError::InvalidRuntime => tigerclaw_status_t::TIGERCLAW_ERR_INVALID_HANDLE,
        EngineError::InvalidState => tigerclaw_status_t::TIGERCLAW_ERR_INVALID_STATE,
        EngineError::UnsupportedCommand => tigerclaw_status_t::TIGERCLAW_ERR_RESOURCE_UNAVAILABLE,
        EngineError::Internal => tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR,
    }
}
fn snapshot_from_response(revision: u64, response: &str) -> Result<SnapshotData, tigerclaw_status_t> {
    let value: Value = serde_json::from_str(response).map_err(|_| tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR)?;
    let candidates = value.get("candidates").and_then(Value::as_array).map(|values| {
        values.iter().filter_map(Value::as_str).map(|text| text.as_bytes().to_vec()).collect()
    }).unwrap_or_default();
    Ok(SnapshotData {
        revision,
        preedit: value.get("input_buffer").and_then(Value::as_str).unwrap_or_default().as_bytes().to_vec(),
        commit: value.get("commit_text").and_then(Value::as_str).unwrap_or_default().as_bytes().to_vec(),
        candidates,
        selected_index: value.get("selected_index").and_then(Value::as_u64).unwrap_or(0) as usize,
    })
}
fn protocol_line(session_id: u64, sequence: u64, event: tigerclaw_key_event_t) -> String {
    if event.key == 0x20 && event.modifiers & CONTROL != 0 {
        return json!({"type":"ctrl_space","seq":sequence}).to_string();
    }
    json!({
        "type":"key", "seq":sequence, "action":if event.action == KEY_DOWN {"down"} else {"up"},
        "vk":event.key, "shift":event.modifiers & SHIFT != 0, "ctrl":event.modifiers & CONTROL != 0,
        "alt":event.modifiers & ALT != 0, "win":event.modifiers & META != 0,
        "capsLock":event.modifiers & CAPS_LOCK != 0,
        "client_session":format!("ffi:{}", session_id), "event_id":event.event_id.to_string()
    }).to_string()
}
fn store_replay(state: &mut FfiSessionState, event_id: u64, data: SnapshotData) {
    if state.replay.insert(event_id, data).is_none() {
        state.replay_order.push_back(event_id);
        while state.replay.len() > REPLAY_CAPACITY {
            if let Some(oldest) = state.replay_order.pop_front() { state.replay.remove(&oldest); }
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn tigerclaw_abi_version() -> u32 { TIGERCLAW_ABI_VERSION }

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_runtime_create(out: *mut *mut tigerclaw_runtime_t) -> tigerclaw_status_t {
    guard(tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR, || {
        let Some(out) = (unsafe { out.as_mut() }) else { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_ARGUMENT; };
        *out = Box::into_raw(Box::new(RuntimeHandle { engine: EngineRuntime::default() })) as *mut tigerclaw_runtime_t;
        tigerclaw_status_t::TIGERCLAW_OK
    })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_runtime_destroy(runtime: *mut tigerclaw_runtime_t) {
    guard((), || if !runtime.is_null() { unsafe { drop(Box::from_raw(runtime as *mut RuntimeHandle)) } });
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_session_create(runtime: *mut tigerclaw_runtime_t, out: *mut *mut tigerclaw_session_t) -> tigerclaw_status_t {
    guard(tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR, || {
        let Some(runtime) = (unsafe { runtime_handle(runtime) }) else { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_HANDLE; };
        let Some(out) = (unsafe { out.as_mut() }) else { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_ARGUMENT; };
        let engine = match runtime.engine.create_session() { Ok(value) => value, Err(error) => return engine_status(error) };
        *out = Box::into_raw(Box::new(SessionHandle {
            engine, command_lock: Mutex::new(()),
            state: Mutex::new(FfiSessionState { revision: 0, replay: HashMap::new(), replay_order: VecDeque::new() })
        })) as *mut tigerclaw_session_t;
        tigerclaw_status_t::TIGERCLAW_OK
    })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_session_destroy(session: *mut tigerclaw_session_t) {
    guard((), || if !session.is_null() { unsafe { drop(Box::from_raw(session as *mut SessionHandle)) } });
}
fn with_session(ptr: *mut tigerclaw_session_t, action: impl FnOnce(&SessionHandle) -> tigerclaw_status_t) -> tigerclaw_status_t {
    guard(tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR, || {
        let Some(session) = (unsafe { session_handle(ptr) }) else { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_HANDLE; };
        action(session)
    })
}
#[unsafe(no_mangle)]
pub extern "C" fn tigerclaw_session_activate(ptr: *mut tigerclaw_session_t) -> tigerclaw_status_t {
    with_session(ptr, |session| {
        let _command = match session.command_lock.lock() { Ok(value) => value, Err(_) => return tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR };
        if let Err(error) = session.engine.activate() { return engine_status(error); }
        match session.state.lock() {
            Ok(mut state) => { state.revision = state.revision.saturating_add(1); tigerclaw_status_t::TIGERCLAW_OK }
            Err(_) => tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR,
        }
    })
}
#[unsafe(no_mangle)]
pub extern "C" fn tigerclaw_session_deactivate(ptr: *mut tigerclaw_session_t) -> tigerclaw_status_t {
    with_session(ptr, |session| {
        let _command = match session.command_lock.lock() { Ok(value) => value, Err(_) => return tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR };
        if let Err(error) = session.engine.deactivate() { return engine_status(error); }
        match session.state.lock() {
            Ok(mut state) => { state.revision = state.revision.saturating_add(1); tigerclaw_status_t::TIGERCLAW_OK }
            Err(_) => tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR,
        }
    })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_session_process_key(
    ptr: *mut tigerclaw_session_t, event: *const tigerclaw_key_event_t, out: *mut *mut tigerclaw_snapshot_t
) -> tigerclaw_status_t {
    guard(tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR, || {
        let Some(event) = (unsafe { event.as_ref() }) else { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_ARGUMENT; };
        let Some(out) = (unsafe { out.as_mut() }) else { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_ARGUMENT; };
        *out = std::ptr::null_mut();
        if !matches!(event.action, KEY_DOWN | KEY_UP) { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_ARGUMENT; }
        let Some(session) = (unsafe { session_handle(ptr) }) else { return tigerclaw_status_t::TIGERCLAW_ERR_INVALID_HANDLE; };
        let _command = match session.command_lock.lock() { Ok(value) => value, Err(_) => return tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR };
        let mut state = match session.state.lock() { Ok(value) => value, Err(_) => return tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR };
        if let Some(data) = state.replay.get(&event.event_id).cloned() {
            *out = make_snapshot(data);
            return tigerclaw_status_t::TIGERCLAW_OK;
        }
        let line = protocol_line(session.engine.id(), state.revision.saturating_add(1), *event);
        let response = match session.engine.handle_line(&line) {
            Ok(Some(value)) => value, Ok(None) => return tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR,
            Err(error) => return engine_status(error),
        };
        state.revision = state.revision.saturating_add(1);
        let data = match snapshot_from_response(state.revision, &response) { Ok(value) => value, Err(status) => return status };
        store_replay(&mut state, event.event_id, data.clone());
        *out = make_snapshot(data);
        tigerclaw_status_t::TIGERCLAW_OK
    })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_snapshot_free(ptr: *mut tigerclaw_snapshot_t) {
    guard((), || if !ptr.is_null() { unsafe { drop(Box::from_raw(ptr as *mut SnapshotHandle)) } });
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_snapshot_revision(ptr: *const tigerclaw_snapshot_t) -> u64 {
    guard(0, || unsafe { snapshot_handle(ptr).map_or(0, |value| value.data.revision) })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_snapshot_preedit(ptr: *const tigerclaw_snapshot_t) -> tigerclaw_utf8_slice_t {
    guard(empty_slice(), || unsafe { snapshot_handle(ptr).map_or_else(empty_slice, |value| slice(&value.data.preedit)) })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_snapshot_commit(ptr: *const tigerclaw_snapshot_t) -> tigerclaw_utf8_slice_t {
    guard(empty_slice(), || unsafe { snapshot_handle(ptr).map_or_else(empty_slice, |value| slice(&value.data.commit)) })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_snapshot_candidate_count(ptr: *const tigerclaw_snapshot_t) -> usize {
    guard(0, || unsafe { snapshot_handle(ptr).map_or(0, |value| value.data.candidates.len()) })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_snapshot_candidate(ptr: *const tigerclaw_snapshot_t, index: usize) -> tigerclaw_utf8_slice_t {
    guard(empty_slice(), || unsafe { snapshot_handle(ptr).and_then(|value| value.data.candidates.get(index)).map_or_else(empty_slice, |value| slice(value)) })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tigerclaw_snapshot_selected_index(ptr: *const tigerclaw_snapshot_t) -> usize {
    guard(0, || unsafe { snapshot_handle(ptr).map_or(0, |value| value.data.selected_index) })
}
#[unsafe(no_mangle)]
pub extern "C" fn tigerclaw_status_message(status: tigerclaw_status_t) -> tigerclaw_utf8_slice_t {
    let message = match status {
        tigerclaw_status_t::TIGERCLAW_OK => b"ok".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_INVALID_ARGUMENT => b"invalid argument".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_INVALID_UTF8 => b"invalid UTF-8".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_INVALID_HANDLE => b"invalid handle".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_INVALID_STATE => b"invalid session state".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_OUT_OF_MEMORY => b"out of memory".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_RESOURCE_UNAVAILABLE => b"resource unavailable".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_STALE_GENERATION => b"stale generation".as_slice(),
        tigerclaw_status_t::TIGERCLAW_ERR_INTERNAL_ERROR => b"internal error".as_slice(),
    };
    slice(message)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn process(session: *mut tigerclaw_session_t, key: u32, event_id: u64) -> *mut tigerclaw_snapshot_t {
        let event = tigerclaw_key_event_t { key, action: KEY_DOWN, modifiers: 0, event_id };
        let mut snapshot = std::ptr::null_mut();
        assert_eq!(
            unsafe { tigerclaw_session_process_key(session, &event, &mut snapshot) },
            tigerclaw_status_t::TIGERCLAW_OK
        );
        snapshot
    }

    #[test]
    fn lifecycle_and_replay_return_real_immutable_snapshots() {
        let mut runtime = std::ptr::null_mut();
        let mut session = std::ptr::null_mut();
        assert_eq!(unsafe { tigerclaw_runtime_create(&mut runtime) }, tigerclaw_status_t::TIGERCLAW_OK);
        assert_eq!(unsafe { tigerclaw_session_create(runtime, &mut session) }, tigerclaw_status_t::TIGERCLAW_OK);
        assert_eq!(tigerclaw_session_activate(session), tigerclaw_status_t::TIGERCLAW_OK);

        let first = process(session, 0x41, 10);
        assert_eq!(unsafe { tigerclaw_snapshot_revision(first) }, 2);
        let preedit = unsafe { tigerclaw_snapshot_preedit(first) };
        assert_eq!(unsafe { std::slice::from_raw_parts(preedit.ptr, preedit.len) }, b"a");
        assert_eq!(unsafe { tigerclaw_snapshot_commit(first).len }, 0);

        let replay = process(session, 0x41, 10);
        assert_eq!(unsafe { tigerclaw_snapshot_revision(replay) }, 2);
        assert_eq!(unsafe { tigerclaw_snapshot_preedit(replay).len }, 1);

        let second = process(session, 0x42, 11);
        assert_eq!(unsafe { tigerclaw_snapshot_revision(second) }, 3);
        let preedit = unsafe { tigerclaw_snapshot_preedit(second) };
        assert_eq!(unsafe { std::slice::from_raw_parts(preedit.ptr, preedit.len) }, b"ab");

        unsafe { tigerclaw_snapshot_free(first) };
        unsafe { tigerclaw_snapshot_free(replay) };
        unsafe { tigerclaw_snapshot_free(second) };
        assert_eq!(tigerclaw_session_deactivate(session), tigerclaw_status_t::TIGERCLAW_OK);
        unsafe { tigerclaw_session_destroy(session) };
        unsafe { tigerclaw_runtime_destroy(runtime) };
    }

    #[test]
    fn processing_requires_an_active_session_and_valid_event() {
        let mut runtime = std::ptr::null_mut();
        let mut session = std::ptr::null_mut();
        assert_eq!(unsafe { tigerclaw_runtime_create(&mut runtime) }, tigerclaw_status_t::TIGERCLAW_OK);
        assert_eq!(unsafe { tigerclaw_session_create(runtime, &mut session) }, tigerclaw_status_t::TIGERCLAW_OK);
        let event = tigerclaw_key_event_t { key: 0x41, action: KEY_DOWN, modifiers: 0, event_id: 1 };
        let mut snapshot = std::ptr::null_mut();
        assert_eq!(unsafe { tigerclaw_session_process_key(session, &event, &mut snapshot) }, tigerclaw_status_t::TIGERCLAW_ERR_INVALID_STATE);
        assert_eq!(unsafe { tigerclaw_session_process_key(session, std::ptr::null(), &mut snapshot) }, tigerclaw_status_t::TIGERCLAW_ERR_INVALID_ARGUMENT);
        unsafe { tigerclaw_session_destroy(session) };
        unsafe { tigerclaw_runtime_destroy(runtime) };
    }

    #[test]
    fn session_observes_runtime_destruction() {
        let mut runtime = std::ptr::null_mut();
        let mut session = std::ptr::null_mut();
        assert_eq!(unsafe { tigerclaw_runtime_create(&mut runtime) }, tigerclaw_status_t::TIGERCLAW_OK);
        assert_eq!(unsafe { tigerclaw_session_create(runtime, &mut session) }, tigerclaw_status_t::TIGERCLAW_OK);
        unsafe { tigerclaw_runtime_destroy(runtime) };
        assert_eq!(tigerclaw_session_activate(session), tigerclaw_status_t::TIGERCLAW_ERR_INVALID_HANDLE);
        unsafe { tigerclaw_session_destroy(session) };
    }
}
