use std::collections::HashMap;
use std::io;
use std::path::Path;

use crate::text::read_text;

#[derive(Debug, Clone)]
pub struct SupplementEntry {
    pub text: String,
    pub weight: i64,
    pub reward: f64,
}

impl SupplementEntry {
    pub fn create(text: String, weight: i64) -> Self {
        let weight = weight.clamp(1, 1_000_000_000);
        let reward = (9.0 + 2.0 * ((weight as f64) / 1000.0).ln()).clamp(0.0, 16.0);
        Self { text, weight, reward }
    }
}

#[derive(Debug, Clone, Default)]
struct Node {
    transitions: HashMap<String, usize>,
    failure: usize,
    reward: f64,
}

#[derive(Debug, Clone, Default)]
pub struct SupplementMatcher {
    nodes: Vec<Node>,
}

impl SupplementMatcher {
    pub fn empty() -> Self {
        Self {
            nodes: vec![Node::default()],
        }
    }

    pub fn is_empty(&self) -> bool {
        self.nodes.len() <= 1
    }

    pub fn load_from_code_table_dir(dir: impl AsRef<Path>) -> Self {
        let path = dir.as_ref().join("补充语料.txt");
        match Self::load_file(&path) {
            Ok(entries) if !entries.is_empty() => Self::build(&entries),
            _ => Self::empty(),
        }
    }

    pub fn load_file(path: impl AsRef<Path>) -> io::Result<Vec<SupplementEntry>> {
        let mut last_wins = HashMap::new();
        for raw in read_text(path)?.lines() {
            let uncommented = strip_inline_comment(raw);
            let line = uncommented.trim();
            if line.is_empty() {
                continue;
            }
            let mut parts = line.split_whitespace();
            let Some(text) = parts.next() else {
                continue;
            };
            let mut weight = 1000_i64;
            if let Some(weight_text) = parts.next() {
                if parts.next().is_some() {
                    continue;
                }
                match weight_text.parse::<i64>() {
                    Ok(value) if value > 0 => weight = value,
                    _ => continue,
                }
            }
            last_wins.insert(text.to_owned(), SupplementEntry::create(text.to_owned(), weight));
        }
        Ok(last_wins.into_values().collect())
    }

    pub fn build(entries: &[SupplementEntry]) -> Self {
        let mut nodes = vec![Node::default()];
        for entry in entries {
            if entry.text.is_empty() || entry.reward <= 0.0 {
                continue;
            }
            let mut state = 0usize;
            for ch in entry.text.chars() {
                let key = ch.to_string();
                if let Some(&next) = nodes[state].transitions.get(&key) {
                    state = next;
                } else {
                    let next = nodes.len();
                    nodes[state].transitions.insert(key, next);
                    nodes.push(Node::default());
                    state = next;
                }
            }
            nodes[state].reward = nodes[state].reward.max(entry.reward);
        }
        if nodes.len() == 1 {
            return Self::empty();
        }

        let mut queue = std::collections::VecDeque::new();
        let root_children: Vec<(String, usize)> =
            nodes[0].transitions.iter().map(|(k, v)| (k.clone(), *v)).collect();
        for (_, child) in &root_children {
            nodes[*child].failure = 0;
            queue.push_back(*child);
        }
        while let Some(current) = queue.pop_front() {
            let transitions: Vec<(String, usize)> = nodes[current]
                .transitions
                .iter()
                .map(|(k, v)| (k.clone(), *v))
                .collect();
            for (key, next) in transitions {
                let mut fallback = nodes[current].failure;
                while fallback != 0 && !nodes[fallback].transitions.contains_key(&key) {
                    fallback = nodes[fallback].failure;
                }
                nodes[next].failure = nodes[fallback]
                    .transitions
                    .get(&key)
                    .copied()
                    .filter(|value| *value != next)
                    .unwrap_or(0);
                let inherited = nodes[nodes[next].failure].reward;
                nodes[next].reward = nodes[next].reward.max(inherited);
                queue.push_back(next);
            }
        }
        Self { nodes }
    }

    pub fn advance(&self, state: usize, text_element: &str) -> (usize, f64) {
        if self.is_empty() || text_element.is_empty() {
            return (0, 0.0);
        }
        let mut current = if state < self.nodes.len() { state } else { 0 };
        while current != 0 && !self.nodes[current].transitions.contains_key(text_element) {
            current = self.nodes[current].failure;
        }
        if let Some(&next) = self.nodes[current].transitions.get(text_element) {
            current = next;
        }
        (current, self.nodes[current].reward)
    }
}

fn strip_inline_comment(line: &str) -> String {
    let mut result = String::with_capacity(line.len());
    let mut slash_run = 0usize;
    for ch in line.chars() {
        if ch == '#' {
            if slash_run % 2 == 1 {
                // Keep an escaped hash as data, but remove its escaping
                // slash, matching CoreRuntimeState.StripInlineComment.
                if result.ends_with('\\') {
                    result.pop();
                }
                result.push('#');
                slash_run = 0;
                continue;
            }
            break;
        }
        result.push(ch);
        slash_run = if ch == '\\' { slash_run.saturating_add(1) } else { 0 };
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overlapping_entries_keep_largest_reward() {
        let matcher = SupplementMatcher::build(&[
            SupplementEntry::create("茧师".to_owned(), 1000),
            SupplementEntry::create("师".to_owned(), 1000),
        ]);
        let mut reward = 0.0;
        let mut state = 0;
        for ch in ["茧", "师"] {
            let (next, value) = matcher.advance(state, ch);
            state = next;
            reward = value;
        }
        assert!(reward > 0.0);
    }

    #[test]
    fn supplemental_loader_uses_detected_encoding_and_escaped_comments() {
        let path = std::env::temp_dir().join(format!(
            "tigerclaw-rust-supplement-{}.txt",
            std::process::id()
        ));
        // UTF-16LE is one of the encodings accepted by the C# loader.
        let mut bytes = vec![0xFF, 0xFE];
        for unit in "甲#乙 2000 # comment\n".encode_utf16() {
            bytes.extend_from_slice(&unit.to_le_bytes());
        }
        std::fs::write(&path, bytes).unwrap();
        let entries = SupplementMatcher::load_file(&path).unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].text, "甲");
        assert_eq!(entries[0].weight, 1000);

        std::fs::write(&path, "甲\\#乙 2000 # comment\n").unwrap();
        let entries = SupplementMatcher::load_file(&path).unwrap();
        assert_eq!(entries[0].text, "甲#乙");
        assert_eq!(entries[0].weight, 2000);
        let _ = std::fs::remove_file(path);
    }
}
