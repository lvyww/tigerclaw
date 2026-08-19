use std::fs;
use std::io;
use std::path::Path;

#[derive(Debug, Clone)]
pub struct Config {
    pub semicolon_second: bool,
    pub quote_third: bool,
    pub tab_clear: bool,
    pub max_code_length: usize,
    pub auto_commit_no_repeat: bool,
    pub empty_code_clear: bool,
    pub enter_clear: bool,
    pub page_size: usize,
    pub mixed_input: bool,
    pub sentence_input: bool,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            semicolon_second: true,
            quote_third: true,
            tab_clear: false,
            max_code_length: 4,
            auto_commit_no_repeat: false,
            empty_code_clear: true,
            enter_clear: false,
            page_size: 5,
            mixed_input: false,
            sentence_input: false,
        }
    }
}

impl Config {
    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        let mut config = Self::default();
        for raw in fs::read_to_string(path)?.lines() {
            let line = raw.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            let Some((key, value)) = line.split_once('\t').or_else(|| line.split_once('=')) else {
                continue;
            };
            let enabled = parse_bool(value.trim());
            match key.trim() {
                "分号次选" | "semicolon_second" => config.semicolon_second = enabled,
                "引号三选" | "quote_third" => config.quote_third = enabled,
                "TAB清屏" | "tab_clear" => config.tab_clear = enabled,
                "最大码长" | "max_code_length" => {
                    if let Ok(value) = value.trim().parse::<usize>() {
                        config.max_code_length = value.clamp(1, 16);
                    }
                }
                "最大码长无重自动上屏" | "auto_commit_no_repeat" => {
                    config.auto_commit_no_repeat = enabled
                }
                "空码自动清屏" | "empty_code_clear" => config.empty_code_clear = enabled,
                "回车清屏" | "enter_clear" => config.enter_clear = enabled,
                "每页候选个数" | "page_size" => {
                    if let Ok(value) = value.trim().parse::<usize>() {
                        config.page_size = value.clamp(1, 10);
                    }
                }
                "中英文不限长混合输入" | "mixed_input" => config.mixed_input = enabled,
                "整句输入" | "sentence_input" => config.sentence_input = enabled,
                _ => {}
            }
        }
        Ok(config)
    }
}

fn parse_bool(value: &str) -> bool {
    matches!(value.to_ascii_lowercase().as_str(), "是" | "true" | "on" | "yes" | "1")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_chinese_tab_config() {
        let path = std::env::temp_dir().join("tigerclaw-rust-config-test.txt");
        fs::write(&path, "分号次选\t否\n引号三选\ton\nTAB清屏\t是\n").unwrap();
        let config = Config::load(&path).unwrap();
        assert!(!config.semicolon_second);
        assert!(config.quote_third);
        assert!(config.tab_clear);
        let _ = fs::remove_file(path);
    }
}
