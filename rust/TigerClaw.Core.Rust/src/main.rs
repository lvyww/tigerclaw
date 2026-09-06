use std::io::{self, BufRead, Write};
use std::sync::Arc;
#[cfg(windows)]
use std::sync::{Arc, Mutex};

use tigerclaw_core_rust::protocol::handle_line;
use tigerclaw_core_rust::state::CoreState;
use tigerclaw_core_rust::lexicon::Lexicon;
use tigerclaw_core_rust::config::Config;
use tigerclaw_core_rust::ngram::NgramModel;

#[cfg(windows)]
mod windows_pipe;

fn main() -> io::Result<()> {
    let stdio = std::env::args().any(|argument| argument == "--stdio");
    let lexicon_path = std::env::args()
        .collect::<Vec<_>>()
        .windows(2)
        .find(|pair| pair[0] == "--lexicon")
        .map(|pair| pair[1].clone());
    let config_path = std::env::args()
        .collect::<Vec<_>>()
        .windows(2)
        .find(|pair| pair[0] == "--config")
        .map(|pair| pair[1].clone());
    let ngram_path = std::env::args()
        .collect::<Vec<_>>()
        .windows(2)
        .find(|pair| pair[0] == "--ngram")
        .map(|pair| pair[1].clone());
    let sentence_exe = std::env::args().collect::<Vec<_>>().windows(2).find(|pair| pair[0] == "--sentence-exe").map(|pair| pair[1].clone());
    let qwen_model = std::env::args().collect::<Vec<_>>().windows(2).find(|pair| pair[0] == "--qwen-model").map(|pair| pair[1].clone());
    let root = std::env::current_exe().ok().and_then(|path| path.parent().map(|p| p.to_path_buf()));
    let lexicon_path = lexicon_path.or_else(|| root.as_ref().map(|p| p.join(r"码表\虎码\常用字词.txt").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    let config_path = config_path.or_else(|| root.as_ref().map(|p| p.join("config.txt").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    let ngram_path = ngram_path.or_else(|| root.as_ref().map(|p| p.join(r"Models\sentence-ngram-v2.bin").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    let sentence_exe = sentence_exe.or_else(|| root.as_ref().map(|p| p.join(r"sentence\TigerClaw.Sentence.exe").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    let qwen_model = qwen_model.or_else(|| root.as_ref().map(|p| p.join(r"sentence\Models\sentence-qwen-q8.gguf").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    if stdio {
        return run_stdio(lexicon_path, config_path, ngram_path, sentence_exe, qwen_model);
    }

    #[cfg(windows)]
    {
        let mut state = CoreState::default();
        if let Some(path) = lexicon_path {
            state.lexicon = Lexicon::load(&path)?;
            state.lexicon_path = Some(path);
        }
        if let Some(path) = config_path {
            state.config = Arc::new(Config::load(&path)?);
            state.config_path = Some(path);
        }
        if let Some(path) = ngram_path {
            state.sentence_model = NgramModel::load(path).ok().map(Arc::new);
        }
        state.sentence_exe = sentence_exe;
        state.qwen_model = qwen_model;
        state.base_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(|p| p.to_string_lossy().into_owned()));
        return windows_pipe::run(Arc::new(Mutex::new(state)));
    }

    #[cfg(not(windows))]
    {
        eprintln!("TigerClaw.Core.Rust: use --stdio outside Windows");
        Ok(())
    }
}

fn run_stdio(lexicon_path: Option<String>, config_path: Option<String>, ngram_path: Option<String>, sentence_exe: Option<String>, qwen_model: Option<String>) -> io::Result<()> {
    let stdin = io::stdin();
    let mut stdout = io::stdout().lock();
    let mut state = CoreState::default();
    if let Some(path) = lexicon_path {
        state.lexicon = Lexicon::load(&path)?;
        state.lexicon_path = Some(path);
    }
    if let Some(path) = config_path {
        state.config = Arc::new(Config::load(&path)?);
        state.config_path = Some(path);
    }
    if let Some(path) = ngram_path {
        state.sentence_model = NgramModel::load(path).ok().map(Arc::new);
    }
    state.sentence_exe = sentence_exe;
    state.qwen_model = qwen_model;
    state.base_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(|p| p.to_string_lossy().into_owned()));
    for line in stdin.lock().lines() {
        if let Some(response) = handle_line(&mut state, &line?) {
            writeln!(stdout, "{response}")?;
            stdout.flush()?;
        }
    }
    Ok(())
}
