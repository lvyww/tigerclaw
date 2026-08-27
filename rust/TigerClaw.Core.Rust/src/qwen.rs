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
    use std::process::{Command, Stdio};
    use std::sync::mpsc;
    use std::time::Duration;

    if candidates.is_empty() || candidates.len() > 5 {
        return None;
    }
    let pipe = r"\\.\pipe\TigerClaw.Sentence.v1";
    let child = Command::new(exe)
        .args(["--parent-pid", &std::process::id().to_string(), "--pipe", "TigerClaw.Sentence.v1", "--model", model])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .ok()?;
    crate::state::register_owned_child(owned_children, child, exe.to_owned());
    let mut file = None;
    for _ in 0..100 {
        if let Ok(handle) = OpenOptions::new().read(true).write(true).open(pipe) {
            file = Some(handle);
            break;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let mut file = file?;
    let request = serde_json::to_string(&Request { kind: "rerank", seq: 1, generation, raw_code, candidates }).ok()?;
    writeln!(file, "{request}").ok()?;
    file.flush().ok()?;
    // A named-pipe ReadLine can otherwise block the rerank worker forever if
    // the optional sidecar crashes after accepting the request.  Read on a
    // short-lived helper and bound the wait exactly like the C# client.  The
    // latest-only caller owns at most one such helper at a time.
    let (sender, receiver) = mpsc::sync_channel(1);
    std::thread::spawn(move || {
        let mut line = String::new();
        let result = BufReader::new(file)
            .read_line(&mut line)
            .map(|_| line);
        let _ = sender.send(result);
    });
    let line = receiver.recv_timeout(Duration::from_secs(5)).ok()?.ok()?;
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
