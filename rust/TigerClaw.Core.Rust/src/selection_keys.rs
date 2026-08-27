use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::Path;

use crate::text::read_text;

const UTF8_BOM: &[u8] = &[0xEF, 0xBB, 0xBF];

#[derive(Debug, Clone)]
pub struct SelectionKeyBindings {
    bindings: Vec<Vec<u64>>,
    key_map: HashMap<u64, usize>,
}

impl Default for SelectionKeyBindings {
    fn default() -> Self {
        let mut bindings = vec![Vec::new(); 10];
        for number in 1..=9 {
            bindings[number - 1].push(0x30 + number as u64);
        }
        bindings[9].push(0x30);
        Self::from_bindings(bindings)
    }
}

impl SelectionKeyBindings {
    pub fn load_or_create(path: &Path) -> io::Result<Self> {
        if !path.exists() {
            let bindings = Self::default();
            bindings.save(path)?;
            return Ok(bindings);
        }

        let text = read_text(path)?;
        Self::parse(&text).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
    }

    pub fn parse(text: &str) -> Result<Self, String> {
        let mut bindings = Self::default().bindings;
        for raw_line in text.trim_start_matches('\u{feff}').lines() {
            let line = raw_line.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }

            let mut parts = line.split_whitespace();
            let label = parts.next().unwrap_or_default();
            let number = parse_selection_label(label)
                .ok_or_else(|| format!("自定义选重键存在无效标签：{label}"))?;
            let mut keys = Vec::new();
            for token in parts {
                let vk = parse_virtual_key(token)
                    .ok_or_else(|| format!("自定义选重键存在无效按键：{token}"))?;
                if !keys.contains(&vk) {
                    keys.push(vk);
                }
            }
            bindings[number - 1] = keys;
        }
        Ok(Self::from_bindings(bindings))
    }

    pub fn save(&self, path: &Path) -> io::Result<()> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let mut bytes = UTF8_BOM.to_vec();
        bytes.extend_from_slice(self.file_text().as_bytes());
        fs::write(path, bytes)
    }

    pub fn selection_for_key(&self, vk: u64) -> Option<usize> {
        self.key_map.get(&vk).copied().or_else(|| {
            let generic = match vk {
                0xA0 | 0xA1 => 0x10,
                0xA2 | 0xA3 => 0x11,
                0xA4 | 0xA5 => 0x12,
                0x5C => 0x5B,
                _ => return None,
            };
            self.key_map.get(&generic).copied()
        })
    }

    pub fn config_text(&self) -> String {
        self.bindings
            .iter()
            .enumerate()
            .map(|(index, keys)| {
                let suffix = keys
                    .iter()
                    .map(|vk| format!(" 0x{vk:02X}"))
                    .collect::<String>();
                format!("{}选{suffix}", index + 1)
            })
            .collect::<Vec<_>>()
            .join("\r\n")
    }

    fn from_bindings(bindings: Vec<Vec<u64>>) -> Self {
        let mut key_map = HashMap::new();
        for (index, keys) in bindings.iter().enumerate() {
            for &vk in keys {
                key_map.entry(vk).or_insert(index + 1);
            }
        }
        Self { bindings, key_map }
    }

    fn file_text(&self) -> String {
        format!(
            "# TigerClaw 自定义选重键配置\r\n# 格式：<n选> <键1> <键2> ...\r\n# 键可以写成：十进制（49）、十六进制（0x31）、或键名（VK_1）\r\n\r\n{}",
            self.config_text()
        )
    }
}

pub fn resolve_virtual_key(vk: u64, scan: u64, extended: bool) -> u64 {
    match vk {
        0x10 if scan == 0x2A => 0xA0,
        0x10 if scan == 0x36 => 0xA1,
        0x11 => if extended { 0xA3 } else { 0xA2 },
        0x12 => if extended { 0xA5 } else { 0xA4 },
        _ => vk,
    }
}

pub fn is_modifier(vk: u64) -> bool {
    matches!(vk, 0x10 | 0x11 | 0x12 | 0x14 | 0x5B | 0x5C | 0xA0..=0xA5)
}

fn parse_selection_label(token: &str) -> Option<usize> {
    token.strip_suffix('选')?.parse::<usize>().ok().filter(|number| (1..=10).contains(number))
}

fn parse_virtual_key(token: &str) -> Option<u64> {
    let upper = token.to_ascii_uppercase();
    if let Some(hex) = upper.strip_prefix("0X") {
        return u64::from_str_radix(hex, 16).ok();
    }
    if let Ok(value) = upper.parse::<u64>() {
        return Some(value);
    }
    named_virtual_key(&upper)
}

fn named_virtual_key(token: &str) -> Option<u64> {
    let fixed = match token {
        "VK_SHIFT" => 0x10,
        "VK_LSHIFT" => 0xA0,
        "VK_RSHIFT" => 0xA1,
        "VK_CONTROL" => 0x11,
        "VK_LCONTROL" => 0xA2,
        "VK_RCONTROL" => 0xA3,
        "VK_MENU" => 0x12,
        "VK_LMENU" => 0xA4,
        "VK_RMENU" => 0xA5,
        "VK_LWIN" => 0x5B,
        "VK_RWIN" => 0x5C,
        "VK_CAPITAL" => 0x14,
        "VK_SPACE" => 0x20,
        "VK_BACK" => 0x08,
        "VK_RETURN" => 0x0D,
        "VK_TAB" => 0x09,
        "VK_ESCAPE" => 0x1B,
        "VK_OEM_1" => 0xBA,
        "VK_OEM_2" => 0xBF,
        "VK_OEM_4" => 0xDB,
        "VK_OEM_5" => 0xDC,
        "VK_OEM_6" => 0xDD,
        "VK_OEM_7" => 0xDE,
        "VK_OEM_COMMA" => 0xBC,
        "VK_OEM_PERIOD" => 0xBE,
        _ => 0,
    };
    if fixed != 0 {
        return Some(fixed);
    }
    let suffix = token.strip_prefix("VK_")?;
    if suffix.len() == 1 {
        let byte = suffix.as_bytes()[0];
        if byte.is_ascii_digit() || byte.is_ascii_uppercase() {
            return Some(byte as u64);
        }
    }
    if let Some(number) = suffix.strip_prefix('F').and_then(|value| value.parse::<u64>().ok()) {
        if (1..=24).contains(&number) {
            return Some(0x6F + number);
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_names_numbers_empty_slots_and_first_duplicate_wins() {
        let bindings = SelectionKeyBindings::parse(
            "1选 VK_SPACE 0x31\n2选 49 VK_F2\n3选\n10选 VK_0",
        ).unwrap();
        assert_eq!(bindings.selection_for_key(0x20), Some(1));
        assert_eq!(bindings.selection_for_key(0x31), Some(1));
        assert_eq!(bindings.selection_for_key(0x71), Some(2));
        assert_eq!(bindings.selection_for_key(0x33), None);
        assert!(bindings.config_text().contains("3选\r\n"));
    }

    #[test]
    fn generic_modifiers_match_resolved_left_and_right_keys() {
        let bindings = SelectionKeyBindings::parse("2选 VK_SHIFT\n3选 VK_RCONTROL").unwrap();
        assert_eq!(bindings.selection_for_key(0xA0), Some(2));
        assert_eq!(bindings.selection_for_key(0xA1), Some(2));
        assert_eq!(bindings.selection_for_key(0xA2), None);
        assert_eq!(bindings.selection_for_key(0xA3), Some(3));
        assert_eq!(resolve_virtual_key(0x10, 0x36, false), 0xA1);
    }

    #[test]
    fn rejects_invalid_labels_and_tokens() {
        assert!(SelectionKeyBindings::parse("11选 VK_1").is_err());
        assert!(SelectionKeyBindings::parse("1选 VK_NOT_A_KEY").is_err());
    }

    #[test]
    fn saved_file_has_bom_and_loads_back() {
        let path = std::env::temp_dir().join(format!(
            "tigerclaw-rust-selection-bindings-{}.txt",
            std::process::id()
        ));
        let bindings = SelectionKeyBindings::parse("1选 VK_SPACE\n2选 VK_F2").unwrap();
        bindings.save(&path).unwrap();
        let bytes = fs::read(&path).unwrap();
        assert!(bytes.starts_with(UTF8_BOM));
        let loaded = SelectionKeyBindings::load_or_create(&path).unwrap();
        assert_eq!(loaded.selection_for_key(0x20), Some(1));
        assert_eq!(loaded.selection_for_key(0x71), Some(2));
        let _ = fs::remove_file(path);
    }
}
