use std::ffi::OsStr;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::ptr;
use std::sync::{Arc, Mutex};
use std::thread;
use serde::Serialize;
use tigerclaw_core_rust::ui_state::Publisher;

use tigerclaw_core_rust::protocol::handle_line;
use tigerclaw_core_rust::state::CoreState;
use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_PIPE_CONNECTED, GetLastError, HANDLE, INVALID_HANDLE_VALUE,
};
use windows_sys::Win32::Storage::FileSystem::{
    FlushFileBuffers, PIPE_ACCESS_DUPLEX, ReadFile, WriteFile,
};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE,
    PIPE_TYPE_BYTE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::System::Memory::{OpenFileMappingW, MapViewOfFile, UnmapViewOfFile, FILE_MAP_READ};

const PIPE_NAME: &str = r"\\.\pipe\BimeIPC";
const PIPE_BUFFER_BYTES: u32 = 64 * 1024;

pub fn run(state: Arc<Mutex<CoreState>>) -> io::Result<()> {
    let ui = Arc::new(Mutex::new(Publisher::new(r"Local\TigerClaw.UiState.v1", 131072).ok_or_else(|| io::Error::other("UI state MMF unavailable"))?));
    let heartbeat = Arc::new(Mutex::new(Publisher::new(r"Local\TigerClaw.Heartbeat.v1", 16).ok_or_else(|| io::Error::other("heartbeat MMF unavailable"))?));
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
        let pipe = create_pipe()?;
        let connected = unsafe { ConnectNamedPipe(pipe, ptr::null_mut()) } != 0
            || unsafe { GetLastError() } == ERROR_PIPE_CONNECTED;
        if !connected {
            unsafe { CloseHandle(pipe) };
            continue;
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
}

fn overlay_supervisor(state: Arc<Mutex<CoreState>>) {
    let mut retry_ms = 10_000u64;
    let mut next_attempt = std::time::Instant::now();
    loop {
        thread::sleep(std::time::Duration::from_secs(3));
        let alive = overlay_alive();
        if alive { retry_ms = 10_000; next_attempt = std::time::Instant::now() + std::time::Duration::from_secs(10); continue; }
        if std::time::Instant::now() < next_attempt { continue; }
        let base = state.lock().ok().and_then(|s| s.base_dir.clone());
        if let Some(base) = base {
            let exe = std::path::Path::new(&base).join("TigerClaw.Overlay.exe");
            if exe.exists() { let _ = std::process::Command::new(&exe).current_dir(&base).spawn(); }
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
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
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
        if ok == 0 || read == 0 {
            return Ok(());
        }
        pending.extend_from_slice(&buffer[..read as usize]);
        while let Some(newline) = pending.iter().position(|byte| *byte == b'\n') {
            let line = pending.drain(..=newline).collect::<Vec<_>>();
            let line = String::from_utf8_lossy(&line[..line.len() - 1]);
            let response = {
                let mut core = state.lock().map_err(|_| io::Error::other("state poisoned"))?;
                handle_line(&mut core, line.trim_end_matches('\r'))
            };
            if let Some(response) = response {
                if let Ok(core) = state.lock() { publish_ui(&core, &ui); }
                write_all(pipe, format!("{response}\n").as_bytes())?;
            }
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
    let candidates = if state.config.sentence_input { state.sentence_candidates.clone() } else { state.lexicon.candidates(&state.input_buffer) };
    let input_code = format!("{}{}", state.mixed_prefix, state.input_buffer);
    let payload = serde_json::to_vec(&OverlayState { IsOff: false, IsChinese: state.keyboard_open, StatusText: if state.keyboard_open { "中" } else { "EN" }, CandidateVisible: !candidates.is_empty(), InputCode: &input_code, Candidates: candidates, CompositionState: 0, CaretX: state.caret_x, CaretY: state.caret_y, VerticalCandidates: true, ShowCandidateIndex: true, HideCandidateItems: false, CodeMasking: "", ThemeName: "", FontName: "", FontSize: 14.0, CandidateAnnotations: Vec::new(), HideStatusBar: !state.ime_active, SoundSeq: 0, SoundVk: 0, SoundVolumePercent: 0, ShowInputCodeInCandidateWindow: false, CandidateExpandDelayMs: 0, AnnotationExpandDelayMs: 0, IsNativeHook: false, SelectedCandidateIndex: state.selected_candidate as i32, CaretHeight: 20 }).unwrap_or_default();
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
