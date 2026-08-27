#[cfg(windows)]
use serde::{Deserialize, Serialize};

#[cfg(windows)]
#[derive(Debug, Serialize)]
struct Request<'a> {
    #[serde(rename = "type")]
    kind: &'static str,
    seq: u64,
    generation: u64,
    raw_code: &'a str,
    candidates: &'a [String],
}

#[cfg(windows)]
#[derive(Debug, Deserialize)]
struct Response {
    success: bool,
    seq: u64,
    generation: u64,
    raw_code: Option<String>,
    scores: Option<Vec<f64>>,
}

#[cfg(windows)]
pub fn rerank(
    exe: &str,
    model: &str,
    raw_code: &str,
    candidates: &[String],
    generation: u64,
    owned_children: &crate::state::OwnedChildren,
) -> Option<Vec<f64>> {
    use std::fs::OpenOptions;
    use std::io::{BufRead, BufReader, Write};
    use std::os::windows::io::AsRawHandle;
    use std::process::{Command, Stdio};
    use std::sync::{Arc, mpsc};
    use std::time::Duration;
    use windows_sys::Win32::System::IO::CancelIoEx;

    if candidates.is_empty() || candidates.len() > 5 {
        return None;
    }
    let pipe = r"\\.\pipe\TigerClaw.Sentence.v1";
    // Mirror C#'s `ProcessLauncher.TryLaunchSentence`: the sidecar is a
    // persistent server that loops accepting one pipe connection per
    // request, so only spawn it once. Spawning fresh on every call (as
    // before) relaunches the process and reloads its Qwen GGUF model on
    // every keystroke, which is why Windows showed the process-starting
    // busy cursor on every key while sentence neural rerank was on.
    if !crate::state::has_running_child(owned_children, exe) && !sentence_instance_exists() {
        let child = Command::new(exe)
            .args(["--parent-pid", &std::process::id().to_string(), "--pipe", "TigerClaw.Sentence.v1", "--model", model])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .ok()?;
        crate::state::register_owned_child(owned_children, child, exe.to_owned());
    }
    let mut file = None;
    for _ in 0..100 {
        if let Ok(handle) = OpenOptions::new().read(true).write(true).open(pipe) {
            file = Some(handle);
            break;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let file = Arc::new(file?);
    let request = serde_json::to_string(&Request { kind: "rerank", seq: 1, generation, raw_code, candidates }).ok()?;
    {
        let mut writer = &*file;
        writeln!(writer, "{request}").ok()?;
        writer.flush().ok()?;
    }
    // A named-pipe ReadLine can otherwise block the rerank worker forever if
    // the optional sidecar crashes after accepting the request.  Read on a
    // short-lived helper and bound the wait exactly like the C# client.  The
    // latest-only caller owns at most one such helper at a time.
    let (sender, receiver) = mpsc::sync_channel(1);
    let reader_file = Arc::clone(&file);
    std::thread::spawn(move || {
        let mut line = String::new();
        let result = BufReader::new(&*reader_file)
            .read_line(&mut line)
            .map(|_| line);
        let _ = sender.send(result);
    });
    let line = match receiver.recv_timeout(Duration::from_secs(5)) {
        Ok(result) => result.ok()?,
        Err(_) => {
            // The reader uses this exact File handle through Arc, so
            // CancelIoEx interrupts its pending ReadFile even though the I/O
            // was issued by the helper thread. Keeping our Arc alive makes
            // the raw handle valid until cancellation has been requested.
            unsafe {
                CancelIoEx(file.as_raw_handle() as _, std::ptr::null());
            }
            let _ = receiver.recv_timeout(Duration::from_millis(250));
            return None;
        }
    };
    let response: Response = serde_json::from_str(&line).ok()?;
    if !response.success
        || response.seq != 1
        || response.generation != generation
        || response.raw_code.as_deref() != Some(raw_code)
    {
        return None;
    }
    let scores = response.scores?;
    (scores.len() == candidates.len() && scores.iter().all(|score| score.is_finite()))
        .then_some(scores)
}

#[cfg(windows)]
fn sentence_instance_exists() -> bool {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError};
    use windows_sys::Win32::System::Threading::CreateMutexW;

    let name: Vec<u16> = OsStr::new(r"Local\TigerClaw.Sentence.SingleInstance")
        .encode_wide()
        .chain(Some(0))
        .collect();
    let handle = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
    if handle.is_null() {
        return false;
    }
    let exists = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
    unsafe { CloseHandle(handle) };
    exists
}

#[cfg(not(windows))]
pub fn rerank(
    _: &str,
    _: &str,
    _: &str,
    _: &[String],
    _: u64,
    _: &crate::state::OwnedChildren,
) -> Option<Vec<f64>> {
    None
}
