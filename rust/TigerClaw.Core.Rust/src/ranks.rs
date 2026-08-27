use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::Path;

#[derive(Debug, Default, Clone)]
pub struct CharacterRanks {
    rank_by_character: HashMap<String, i32>,
}

impl CharacterRanks {
    pub const UNKNOWN: i32 = 20001;

    pub fn parse(text: &str) -> Self {
        let mut ranks = Self::default();
        let mut rank = 0;
        for raw in text.lines() {
            let text = raw.trim();
            if text.is_empty() || text.starts_with('#') {
                continue;
            }
            rank += 1;
            ranks.rank_by_character.entry(text.to_owned()).or_insert(rank);
        }
        ranks
    }

    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        Ok(Self::parse(&fs::read_to_string(path)?))
    }

    pub fn load_embedded() -> Self {
        Self::parse(include_str!(
            "../../../next/TigerClaw.Core/Data/sentence_char_ranks.txt"
        ))
    }

    pub fn rank(&self, character: &str) -> i32 {
        if character.is_empty() {
            return Self::UNKNOWN;
        }
        self.rank_by_character
            .get(character)
            .copied()
            .unwrap_or(Self::UNKNOWN)
    }

    pub fn is_empty(&self) -> bool {
        self.rank_by_character.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unknown_rank_is_stable() {
        let ranks = CharacterRanks::default();
        assert_eq!(ranks.rank("龘"), CharacterRanks::UNKNOWN);
        assert_eq!(ranks.rank(""), CharacterRanks::UNKNOWN);
    }

    #[test]
    fn embedded_frequency_list_starts_with_common_characters() {
        let ranks = CharacterRanks::load_embedded();
        assert!(!ranks.is_empty());
        assert_eq!(ranks.rank("的"), 1);
        assert_eq!(ranks.rank("一"), 2);
        assert!(ranks.rank("龘") > 3000);
        assert_eq!(ranks.rank("A"), CharacterRanks::UNKNOWN);
    }
}
