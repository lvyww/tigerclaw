#![cfg_attr(windows, windows_subsystem = "windows")]

use std::io::{self, BufRead, Write};
#[cfg(windows)]
use std::sync::Mutex;

use std::path::Path;
use std::sync::Arc;

use tigerclaw_core_rust::protocol::handle_line;
use tigerclaw_core_rust::state::CoreState;
use tigerclaw_core_rust::lexicon::Lexicon;
use tigerclaw_core_rust::config::Config;
use tigerclaw_core_rust::ngram::NgramModel;
use tigerclaw_core_rust::ranks::CharacterRanks;
use tigerclaw_core_rust::selection_keys::SelectionKeyBindings;

#[cfg(windows)]
mod windows_pipe;

fn main() -> io::Result<()> {
    let stdio = std::env::args().any(|argument| argument == "--stdio");
    // `--lexicon` is an explicit test/debug override.  Never discover a
    // hard-coded 虎码 file before reading config.txt: doing so makes a
    // configured schema appear to work while silently loading the wrong
    // table on restart.
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
    let config_path = config_path.or_else(|| root.as_ref().map(|p| p.join("config.txt").to_string_lossy().into_owned()));
    let ngram_path = ngram_path.or_else(|| root.as_ref().map(|p| p.join(r"Models\sentence-ngram-v2.bin").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    let sentence_exe = sentence_exe.or_else(|| root.as_ref().map(|p| p.join(r"sentence\TigerClaw.Sentence.exe").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    let qwen_model = qwen_model.or_else(|| root.as_ref().map(|p| p.join(r"sentence\Models\sentence-qwen-q8.gguf").to_string_lossy().into_owned()).filter(|p| std::path::Path::new(p).exists()));
    if stdio {
        return run_stdio(lexicon_path, config_path, ngram_path, sentence_exe, qwen_model);
    }

    #[cfg(windows)]
    {
        let state = build_state(
            lexicon_path,
            config_path,
            ngram_path,
            sentence_exe,
            qwen_model,
            root.as_deref(),
        )?;
        return windows_pipe::run(Arc::new(Mutex::new(state)));
    }

    #[cfg(not(windows))]
    {
        eprintln!("TigerClaw.Core.Rust: use --stdio outside Windows");
        Ok(())
    }
}

fn build_state(
    lexicon_path: Option<String>,
    config_path: Option<String>,
    ngram_path: Option<String>,
    sentence_exe: Option<String>,
    qwen_model: Option<String>,
    root: Option<&Path>,
) -> io::Result<CoreState> {
    let mut state = CoreState::default();
    state.base_dir = root.map(|path| path.to_string_lossy().into_owned());
    if let Some(path) = config_path {
        if !Path::new(&path).exists() {
            // Match C# EnsureConfigFile: a first run gets a durable default
            // config, so a later Dialog set_config is not memory-only.
            Config::default().write_to(&path)?;
        }
        state.config = Config::load(&path)?;
        state.config_path = Some(path);
        state.keyboard_open = state.config.default_chinese;
    }
    if let Some(path) = lexicon_path {
        state.lexicon = Lexicon::load(&path)?;
        state.lexicon_path = Some(path);
    } else {
        // Resolve the configured schema first, falling back to the first
        // directory in the same deterministic order as C# Core.  A schema
        // loads every supported file, not only 常用字词.txt.
        if let Some(dir) = configured_schema_dir(&state.config, root) {
            let lexicon = Lexicon::load_directory(&dir)?;
            state.lexicon = lexicon;
            state.lexicon_path = Some(dir.to_string_lossy().into_owned());
            if let Some(name) = dir.file_name().and_then(|name| name.to_str()) {
                let _ = state.config.set("当前码表", name);
                state.config.record_recent_schema(name);
            }
            if let Some(path) = state.config_path.as_deref() {
                // Persist the fallback schema selection on first startup.
                state.config.write_to(path)?;
            }
        }
    }
    if let Some(path) = ngram_path {
        state.sentence_model = NgramModel::load(path).ok().map(Arc::new);
    }
    state.sentence_exe = sentence_exe;
    state.qwen_model = qwen_model;
    if let Some(root) = root {
        let path = root.join("自定义选重键.txt");
        state.selection_keys = SelectionKeyBindings::load_or_create(&path).unwrap_or_default();
        state.selection_key_config_path = Some(path.to_string_lossy().into_owned());
        let pinyin_root = root.join("拼音反查码表");
        if pinyin_root.is_dir() {
            state.pinyin_lexicon = Lexicon::load_directory(&pinyin_root).unwrap_or_default();
        }
    }
    state.ranks = load_ranks(root);
    state.reload_supplements();
    state.sentence_decode_sync = false;
    Ok(state)
}

fn load_ranks(root: Option<&Path>) -> CharacterRanks {
    if let Some(root) = root {
        let override_path = root.join("Data").join("sentence_char_ranks.txt");
        if override_path.exists() {
            if let Ok(ranks) = CharacterRanks::load(&override_path) {
                if !ranks.is_empty() {
                    return ranks;
                }
            }
        }
    }
    CharacterRanks::load_embedded()
}

fn configured_schema_dir(config: &Config, base: Option<&Path>) -> Option<std::path::PathBuf> {
    let configured = config.code_root.trim();
    if configured.is_empty() {
        return None;
    }
    let root = if Path::new(configured).is_absolute() {
        std::path::PathBuf::from(configured)
    } else {
        base.map(|path| path.join(configured)).unwrap_or_else(|| std::path::PathBuf::from(configured))
    };
    if !root.is_dir() {
        return None;
    }
    let mut dirs: Vec<_> = std::fs::read_dir(&root)
        .ok()?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| path.is_dir())
        .collect();
    dirs.sort_by(|left, right| {
        left.file_name().unwrap_or_default().to_string_lossy().to_ascii_lowercase()
            .cmp(&right.file_name().unwrap_or_default().to_string_lossy().to_ascii_lowercase())
    });
    if let Some(current) = (!config.current_schema.trim().is_empty()).then_some(config.current_schema.trim()) {
        if let Some(dir) = dirs.iter().find(|path| {
            path.file_name().and_then(|name| name.to_str()).is_some_and(|name| name.eq_ignore_ascii_case(current))
        }) {
            return Some(dir.clone());
        }
    }
    dirs.into_iter().next()
}

fn run_stdio(lexicon_path: Option<String>, config_path: Option<String>, ngram_path: Option<String>, sentence_exe: Option<String>, qwen_model: Option<String>) -> io::Result<()> {
    let stdin = io::stdin();
    let mut stdout = io::stdout().lock();
    let root = std::env::current_exe().ok().and_then(|path| path.parent().map(|p| p.to_path_buf()));
    let mut state = build_state(lexicon_path, config_path, ngram_path, sentence_exe, qwen_model, root.as_deref())?;
    for line in stdin.lock().lines() {
        if let Some(response) = handle_line(&mut state, &line?) {
            writeln!(stdout, "{response}")?;
            stdout.flush()?;
        }
        if state.shutdown_requested {
            break;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn startup_resolves_configured_schema_and_loads_secondary_tables() {
        let root = std::env::temp_dir().join(format!(
            "tigerclaw-rust-startup-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let tables = root.join("tables");
        let selected = tables.join("Custom");
        let other = tables.join("Other");
        std::fs::create_dir_all(&selected).unwrap();
        std::fs::create_dir_all(&other).unwrap();
        std::fs::write(selected.join("主表.txt"), "wxyz\t主表词\n").unwrap();
        std::fs::write(selected.join("附加.txt"), "wxyz\t附加词\n").unwrap();
        std::fs::write(selected.join("补充语料.txt"), "wxyz\t不应成为候选\n").unwrap();
        std::fs::write(other.join("常用字词.txt"), "wxyz\t错误码表\n").unwrap();
        let config_path = root.join("config.txt");
        std::fs::write(
            &config_path,
            format!(
                "码表存储位置\t{}\n当前码表\tCustom\n",
                tables.to_string_lossy()
            ),
        )
        .unwrap();

        let state = build_state(
            None,
            Some(config_path.to_string_lossy().into_owned()),
            None,
            None,
            None,
            Some(root.as_path()),
        )
        .unwrap();
        assert_eq!(state.config.current_schema, "Custom");
        let selected_path = selected.to_string_lossy().into_owned();
        assert_eq!(state.lexicon_path.as_deref(), Some(selected_path.as_str()));
        assert_eq!(state.lexicon.candidates("wxyz"), vec!["主表词", "附加词"]);
        assert!(!state.lexicon.candidates("wxyz").contains(&"不应成为候选".to_owned()));
        let _ = std::fs::remove_dir_all(root);
    }
}
