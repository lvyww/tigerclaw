use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::Path;
use std::sync::Arc;

#[derive(Debug, Default, Clone)]
pub struct Lexicon {
    by_code: Arc<HashMap<String, Vec<String>>>,
}

impl Lexicon {
    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        let content = fs::read_to_string(path)?;
        let mut lexicon = Self::default();
        for raw in content.lines() {
            let line = strip_comment(raw).trim();
            if line.is_empty() || line.starts_with("#") {
                continue;
            }
            let fields: Vec<&str> = line
                .split(['\t', ' '])
                .filter(|field| !field.is_empty())
                .collect();
            if fields.len() < 2 {
                continue;
            }
            let (text, code) = if looks_like_code(fields[0]) {
                (fields[1], fields[0])
            } else if looks_like_code(fields[1]) {
                (fields[0], fields[1])
            } else {
                continue;
            };
            let code = code.to_ascii_lowercase();
            let values = Arc::make_mut(&mut lexicon.by_code).entry(code).or_default();
            if !values.iter().any(|value| value == text) {
                values.push(text.to_owned());
            }
        }
        Ok(lexicon)
    }

    pub fn candidates(&self, code: &str) -> Vec<String> {
        self.by_code
            .get(&code.trim().to_ascii_lowercase())
            .cloned()
            .unwrap_or_default()
    }

    pub fn add_candidate(&mut self, code: &str, text: &str) -> bool {
        let code = code.trim().to_ascii_lowercase();
        let text = text.trim();
        if code.is_empty() || text.is_empty() {
            return false;
        }
        let values = Arc::make_mut(&mut self.by_code).entry(code).or_default();
        if !values.iter().any(|value| value == text) {
            values.insert(0, text.to_owned());
        }
        true
    }

    pub fn construct_code(&self, text: &str) -> String {
        text.chars().map(|ch| {
            self.by_code.iter().find_map(|(code, values)| {
                if values.iter().any(|value| value == &ch.to_string()) { Some(code.clone()) } else { None }
            }).unwrap_or_default()
        }).collect::<Vec<_>>().join(" ")
    }

    pub fn export_line(&self, code: &str, text: &str) -> String {
        format!("{}\t{}\n", text.trim(), code.trim())
    }
}

fn strip_comment(line: &str) -> &str {
    let mut escaped = false;
    for (index, ch) in line.char_indices() {
        if ch == '#' && !escaped {
            return &line[..index];
        }
        escaped = ch == '\\' && !escaped;
        if ch != '\\' {
            escaped = false;
        }
    }
    line
}

fn looks_like_code(value: &str) -> bool {
    !value.is_empty() && value.chars().all(|ch| ch.is_ascii_alphanumeric() || ";'".contains(ch))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_both_common_column_orders() {
        let path = std::env::temp_dir().join("tigerclaw-rust-lexicon-test.txt");
        fs::write(&path, "你好\tabcd\nxyzw\t世界\nabc#comment\t无效\n").unwrap();
        let lexicon = Lexicon::load(&path).unwrap();
        assert_eq!(lexicon.candidates("ABCD"), vec!["你好"]);
        assert_eq!(lexicon.candidates("xyzw"), vec!["世界"]);
        let _ = fs::remove_file(path);
    }
}
