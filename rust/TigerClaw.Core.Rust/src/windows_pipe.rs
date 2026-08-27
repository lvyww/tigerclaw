use std::ffi::OsStr;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::ptr;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;
use serde::Serialize;
use tigerclaw_core_rust::ui_state::Publisher;

use tigerclaw_core_rust::protocol::handle_line;
use tigerclaw_core_rust::state::{
    register_owned_child, terminate_owned_children, CoreState,
};
use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, ERROR_MORE_DATA, ERROR_PIPE_CONNECTED, GetLastError, HANDLE,
    INVALID_HANDLE_VALUE,
};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FlushFileBuffers, PIPE_ACCESS_DUPLEX, ReadFile, WriteFile,
};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_MESSAGE,
    PIPE_TYPE_MESSAGE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::System::Memory::{OpenFileMappingW, MapViewOfFile, UnmapViewOfFile, FILE_MAP_READ};
use windows_sys::Win32::System::Threading::{
    CreateEventW, CreateMutexW, ReleaseMutex, SetEvent,
};

const PIPE_NAME: &str = r"\\.\pipe\BimeIPC";
const PIPE_BUFFER_BYTES: u32 = 64 * 1024;
const SINGLE_INSTANCE_MUTEX: &str = r"Local\TigerClaw.Core.SingleInstance";
const HOOK_NATIVE_EXIT_EVENT: &str = r"Local\TigerClaw.Hook.Native.Exit.v1";
const GENERIC_READ_WRITE: u32 = 0xC000_0000;
const OPEN_EXISTING: u32 = 3;

struct SingleInstanceGuard(HANDLE);

impl SingleInstanceGuard {
    fn acquire() -> io::Result<Option<Self>> {
        let name: Vec<u16> = OsStr::new(SINGLE_INSTANCE_MUTEX)
            .encode_wide()
            .chain(Some(0))
            .collect();
        let handle = unsafe { CreateMutexW(ptr::null(), 1, name.as_ptr()) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe { CloseHandle(handle) };
            return Ok(None);
        }
        Ok(Some(Self(handle)))
    }
}

impl Drop for SingleInstanceGuard {
    fn drop(&mut self) {
        unsafe {
            ReleaseMutex(self.0);
            CloseHandle(self.0);
        }
    }
}

pub fn run(state: Arc<Mutex<CoreState>>) -> io::Result<()> {
    let Some(_single_instance) = SingleInstanceGuard::acquire()? else {
        return Ok(());
    };
    let ui = Arc::new(Mutex::new(Publisher::new(r"Local\TigerClaw.UiState.v1", 131072).ok_or_else(|| io::Error::other("UI state MMF unavailable"))?));
    let heartbeat = Arc::new(Mutex::new(Publisher::new(r"Local\TigerClaw.Heartbeat.v1", 16).ok_or_else(|| io::Error::other("heartbeat MMF unavailable"))?));
    if let Ok(mut value) = heartbeat.lock() {
        value.heartbeat();
    }
    let heartbeat_copy = Arc::clone(&heartbeat);
    thread::spawn(move || loop {
        thread::sleep(std::time::Duration::from_secs(5));
        if let Ok(mut value) = heartbeat_copy.lock() { value.heartbeat(); }
    });
    // Match the C# runtime: keep the status overlay alive for the lifetime of Core.
    let supervisor_state = Arc::clone(&state);
    thread::spawn(move || overlay_supervisor(supervisor_state));
    if let Ok(core) = state.lock() { publish_ui(&core, &ui); }
    loop {
        if state.lock().ok().is_some_and(|core| core.shutdown_requested) {
            break;
        }
        let pipe = create_pipe()?;
        let connected = unsafe { ConnectNamedPipe(pipe, ptr::null_mut()) } != 0
            || unsafe { GetLastError() } == ERROR_PIPE_CONNECTED;
        if !connected {
            unsafe { CloseHandle(pipe) };
            continue;
        }

        // `ConnectNamedPipe` is intentionally synchronous so client reads
        // stay message-mode compatible.  The exit request wakes this blocked
        // accept by opening a short-lived second client connection.  Check
        // the flag before handing that wake-up instance to a worker.
        if state.lock().ok().is_some_and(|core| core.shutdown_requested) {
            unsafe { CloseHandle(pipe) };
            break;
        }

        let client_state = Arc::clone(&state);
        let client_ui = Arc::clone(&ui);
        let pipe_value = pipe as usize;
        thread::spawn(move || {
            let pipe = pipe_value as HANDLE;
            let _ = handle_client(pipe, client_state, client_ui);
            unsafe {
                FlushFileBuffers(pipe);
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
            }
        });
    }

    // Match the C# shutdown order: give the Native Hook its cooperative exit
    // signal, allow sidecars/UI processes a short grace period, then stop
    // only children that this Core actually launched.  No process-name scan
    // is used, so unrelated applications and a separately launched Hook are
    // never terminated by the Rust Core.
    signal_hook_native_exit();
    thread::sleep(Duration::from_millis(300));
    let owned_children = state
        .lock()
        .ok()
        .map(|core| Arc::clone(&core.owned_children));
    if let Some(owned_children) = owned_children {
        terminate_owned_children(&owned_children);
    }
    Ok(())
}

fn overlay_supervisor(state: Arc<Mutex<CoreState>>) {
    let mut retry_ms = 10_000u64;
    let mut next_attempt = std::time::Instant::now();
    loop {
        thread::sleep(std::time::Duration::from_secs(3));
        if state.lock().ok().is_some_and(|core| core.shutdown_requested) {
            return;
        }
        let alive = overlay_alive();
        if alive { retry_ms = 10_000; next_attempt = std::time::Instant::now() + std::time::Duration::from_secs(10); continue; }
        if std::time::Instant::now() < next_attempt { continue; }
        let snapshot = state.lock().ok().map(|core| {
            (core.base_dir.clone(), Arc::clone(&core.owned_children))
        });
        if let Some((Some(base), owned_children)) = snapshot {
            let exe = std::path::Path::new(&base).join("TigerClaw.Overlay.exe");
            if exe.exists() {
                if let Ok(child) = std::process::Command::new(&exe).current_dir(&base).spawn() {
                    register_owned_child(&owned_children, child, exe.to_string_lossy());
                }
            }
        }
        next_attempt = std::time::Instant::now() + std::time::Duration::from_millis(retry_ms);
        retry_ms = (retry_ms + 10_000).min(120_000);
    }
}

fn overlay_alive() -> bool {
    let wide: Vec<u16> = OsStr::new(r"Local\TigerClaw.OverlayHeartbeat.v1").encode_wide().chain(Some(0)).collect();
    unsafe {
        let h = OpenFileMappingW(FILE_MAP_READ, 0, wide.as_ptr());
        if h.is_null() { return false; }
        let p = MapViewOfFile(h, FILE_MAP_READ, 0, 0, 16);
        if p.Value.is_null() { CloseHandle(h); return false; }
        let seq = *(p.Value as *const i64); let tick = *((p.Value as *const i64).add(1));
        UnmapViewOfFile(p); CloseHandle(h);
        if seq <= 0 || tick <= 0 { return false; }
        let now = monotonic_ms(); now >= tick && now - tick <= 6000
    }
}

fn monotonic_ms() -> i64 {
    unsafe { windows_sys::Win32::System::SystemInformation::GetTickCount64() as i64 }
}

fn create_pipe() -> io::Result<HANDLE> {
    let name: Vec<u16> = OsStr::new(PIPE_NAME).encode_wide().chain(Some(0)).collect();
    let pipe = unsafe {
        CreateNamedPipeW(
            name.as_ptr(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            PIPE_BUFFER_BYTES,
            PIPE_BUFFER_BYTES,
            0,
            ptr::null(),
        )
    };
    if pipe == INVALID_HANDLE_VALUE {
        Err(io::Error::last_os_error())
    } else {
        Ok(pipe)
    }
}

fn wake_acceptor() {
    let name: Vec<u16> = OsStr::new(PIPE_NAME).encode_wide().chain(Some(0)).collect();
    let handle = unsafe {
        CreateFileW(
            name.as_ptr(),
            GENERIC_READ_WRITE,
            0,
            ptr::null(),
            OPEN_EXISTING,
            0,
            0 as HANDLE,
        )
    };
    if handle != INVALID_HANDLE_VALUE {
        unsafe { CloseHandle(handle) };
    }
}

fn signal_hook_native_exit() {
    let name: Vec<u16> = OsStr::new(HOOK_NATIVE_EXIT_EVENT)
        .encode_wide()
        .chain(Some(0))
        .collect();
    let handle = unsafe { CreateEventW(ptr::null(), 1, 0, name.as_ptr()) };
    if !handle.is_null() {
        unsafe {
            SetEvent(handle);
            CloseHandle(handle);
        }
    }
}

fn handle_client(pipe: HANDLE, state: Arc<Mutex<CoreState>>, ui: Arc<Mutex<Publisher>>) -> io::Result<()> {
    let mut pending = Vec::new();
    let mut buffer = [0_u8; 8192];
    loop {
        let mut read = 0_u32;
        let ok = unsafe {
            ReadFile(
                pipe,
                buffer.as_mut_ptr(),
                buffer.len() as u32,
                &mut read,
                ptr::null_mut(),
            )
        };
        if ok == 0 && unsafe { GetLastError() } != ERROR_MORE_DATA {
            return Ok(());
        }
        if read == 0 {
            return Ok(());
        }
        pending.extend_from_slice(&buffer[..read as usize]);
        while let Some(newline) = pending.iter().position(|byte| *byte == b'\n') {
            let line = pending.drain(..=newline).collect::<Vec<_>>();
            let line = String::from_utf8_lossy(&line[..line.len() - 1]);
            let (response, shutdown_requested) = {
                let mut core = state.lock().map_err(|_| io::Error::other("state poisoned"))?;
                let response = handle_line(&mut core, line.trim_end_matches('\r'));
                publish_ui(&core, &ui);
                (response, core.shutdown_requested)
            };
            let write_result = response
                .map(|response| write_all(pipe, format!("{response}\n").as_bytes()))
                .transpose();
            // The response must reach Overlay/Dialog before the accept loop
            // is woken.  This preserves their synchronous success result
            // while still making a blocked `ConnectNamedPipe` interruptible.
            if shutdown_requested { wake_acceptor(); }
            write_result?;
            if shutdown_requested { return Ok(()); }
        }
    }
}

#[allow(non_snake_case)]
#[derive(Serialize)]
struct OverlayState<'a> {
    IsOff: bool, IsChinese: bool, StatusText: &'a str, CandidateVisible: bool,
    InputCode: &'a str, Candidates: Vec<String>, CompositionState: i32,
    CaretX: i32, CaretY: i32, VerticalCandidates: bool, ShowCandidateIndex: bool,
    HideCandidateItems: bool, CodeMasking: &'a str, ThemeName: &'a str,
    FontName: &'a str, FontSize: f64, CandidateAnnotations: Vec<String>,
    HideStatusBar: bool, SoundSeq: i64, SoundVk: i32, SoundVolumePercent: i32,
    ShowInputCodeInCandidateWindow: bool, CandidateExpandDelayMs: i32,
    AnnotationExpandDelayMs: i32, IsNativeHook: bool, SelectedCandidateIndex: i32,
    CaretHeight: i32,
}

fn publish_ui(state: &CoreState, publisher: &Arc<Mutex<Publisher>>) {
    let (input_code, candidates) = tigerclaw_core_rust::protocol::overlay_input_and_candidates(state);
    let annotations = tigerclaw_core_rust::protocol::overlay_candidate_annotations(state);
    let awaiting_fresh_caret = state
        .fresh_caret_deadline
        .is_some_and(|deadline| std::time::Instant::now() < deadline);
    let payload = serde_json::to_vec(&OverlayState {
        IsOff: state.hook_native_disabled,
        IsChinese: state.keyboard_open,
        StatusText: if state.hook_native_disabled { "禁" } else if state.keyboard_open { "中" } else { "EN" },
        CandidateVisible: !awaiting_fresh_caret
            && (!candidates.is_empty() || !input_code.is_empty())
            && !state.config.hide_candidate,
        InputCode: &input_code,
        Candidates: candidates,
        CompositionState: if !state.keyboard_open {
            0
        } else if state.input_buffer.is_empty() {
            1
        } else if state.config.sentence_active() {
            5
        } else {
            2
        },
        CaretX: state.caret_x,
        CaretY: state.caret_y,
        VerticalCandidates: state.config.vertical_candidates,
        ShowCandidateIndex: state.config.show_candidate_index,
        HideCandidateItems: state.config.hide_candidate,
        CodeMasking: &state.config.code_masking,
        ThemeName: &state.config.theme,
        FontName: &state.config.font_name,
        FontSize: state.config.font_size,
        CandidateAnnotations: annotations,
        HideStatusBar: state.config.hide_status_bar || !state.ime_active,
        SoundSeq: state.sound_seq,
        SoundVk: state.sound_vk,
        SoundVolumePercent: state.config.sound_volume,
        ShowInputCodeInCandidateWindow: state.config.show_input_code,
        CandidateExpandDelayMs: state.config.candidate_expand_delay_ms,
        AnnotationExpandDelayMs: state.config.annotation_expand_delay_ms,
        IsNativeHook: state.native_hook_active,
        SelectedCandidateIndex: if state.config.sentence_active() { state.selected_candidate as i32 } else { -1 },
        CaretHeight: state.caret_height,
    }).unwrap_or_default();
    if let Ok(mut value) = publisher.lock() { value.publish_json(&payload); }
}

fn write_all(pipe: HANDLE, mut data: &[u8]) -> io::Result<()> {
    while !data.is_empty() {
        let mut written = 0_u32;
        let ok = unsafe {
            WriteFile(
                pipe,
                data.as_ptr(),
                data.len() as u32,
                &mut written,
                ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        data = &data[written as usize..];
    }
    Ok(())
}
