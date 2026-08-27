use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::Path;

use crate::text::read_text;

const YES: &str = "是";
const NO: &str = "否";

pub const DEFAULT_PAIRS: &[(&str, &str)] = &[
    ("开机自动启动", YES),
    ("隐藏状态栏", NO),
    ("码表存储位置", "码表"),
    ("当前码表", ""),
    ("主题", "默认"),
    ("默认中文", YES),
    ("中文状态下使用英文标点", NO),
    ("shift切换中英文", YES),
    ("Ctrl+空格切换中英文", YES),
    ("Alt+\\启用或禁用外挂版", YES),
    ("自动切换系统语言", YES),
    ("Ctrl+等号手动加词", YES),
    ("Ctrl+m切换最近码表", NO),
    ("最近码表对", ""),
    ("回车清屏", NO),
    ("TAB清屏", YES),
    ("竖排候选", YES),
    ("显示候选序号", YES),
    ("`键拼音反查", YES),
    ("显示注释", YES),
    ("显示拆分", NO),
    ("延时显示候选(毫秒)", ""),
    ("延时展开注释和拆分(毫秒)", ""),
    ("每页候选个数", "5"),
    ("翻页键", "- ="),
    ("分号次选", YES),
    ("引号三选", YES),
    ("/输出顿号", YES),
    ("隐藏候选", NO),
    ("候选窗显示编码", NO),
    ("编码伪装", ""),
    ("空码自动清屏", YES),
    ("最大码长", "4"),
    ("中英文不限长混合输入", NO),
    ("整句输入", NO),
    ("自动启用整句模式", YES),
    ("整句神经重排", YES),
    ("整句自动提前上屏", NO),
    ("最大码长无重自动上屏", YES),
    ("字体", "#霞鹜文楷 GB 屏幕阅读版"),
    ("字体大小", "17"),
    ("使用剪贴板上屏", NO),
    ("使用剪贴板上屏白名单", "Pure Writer.exe,Notepad.exe"),
    ("开启打字音效(娱乐)", NO),
    ("按键音量0~100", "30"),
];

#[derive(Debug, Clone)]
pub struct Config {
    values: HashMap<String, String>,
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
    pub early_commit: bool,
    pub auto_enable_sentence: bool,
    pub ctrl_space_toggle: bool,
    pub shift_toggle: bool,
    pub default_chinese: bool,
    pub hide_status_bar: bool,
    pub code_masking: String,
    pub current_schema: String,
    pub code_root: String,
    pub theme: String,
    pub font_name: String,
    pub font_size: f64,
    pub vertical_candidates: bool,
    pub show_candidate_index: bool,
    pub hide_candidate: bool,
    pub show_input_code: bool,
    pub candidate_expand_delay_ms: i32,
    pub annotation_expand_delay_ms: i32,
    pub sound_volume: i32,
    pub key_sound: bool,
    pub neural_rerank: bool,
    pub auto_switch_system_layout: bool,
    pub native_hook_alt_backslash: bool,
    pub use_clipboard_commit: bool,
    pub clipboard_whitelist: String,
    pub ctrl_equal_add_ci: bool,
    pub ctrl_m_switch_schema: bool,
    pub recent_schemas: Vec<String>,
    pub cn_use_en_punc: bool,
    pub slash_outputs_dunhao: bool,
    pub page_keys: String,
    pub back_query: bool,
    pub show_comment: bool,
    pub show_split: bool,
}

impl Default for Config {
    fn default() -> Self {
        let mut config = Self {
            values: HashMap::new(),
            semicolon_second: true,
            quote_third: true,
            tab_clear: true,
            max_code_length: 4,
            auto_commit_no_repeat: true,
            empty_code_clear: true,
            enter_clear: false,
            page_size: 5,
            mixed_input: false,
            sentence_input: false,
            early_commit: false,
            auto_enable_sentence: true,
            ctrl_space_toggle: true,
            shift_toggle: true,
            default_chinese: true,
            hide_status_bar: false,
            code_masking: String::new(),
            current_schema: String::new(),
            code_root: "码表".to_owned(),
            theme: "默认".to_owned(),
            font_name: "#霞鹜文楷 GB 屏幕阅读版".to_owned(),
            font_size: 17.0,
            vertical_candidates: true,
            show_candidate_index: true,
            hide_candidate: false,
            show_input_code: false,
            candidate_expand_delay_ms: 0,
            annotation_expand_delay_ms: 0,
            sound_volume: 30,
            key_sound: false,
            neural_rerank: true,
            auto_switch_system_layout: true,
            native_hook_alt_backslash: true,
            use_clipboard_commit: false,
            clipboard_whitelist: "Pure Writer.exe,Notepad.exe".to_owned(),
            ctrl_equal_add_ci: true,
            ctrl_m_switch_schema: false,
            recent_schemas: Vec::new(),
            cn_use_en_punc: false,
            slash_outputs_dunhao: true,
            page_keys: "- =".to_owned(),
            back_query: true,
            show_comment: true,
            show_split: false,
        };
        for (key, value) in DEFAULT_PAIRS {
            config.values.insert((*key).to_owned(), (*value).to_owned());
        }
        config.refresh_typed();
        config
    }
}

impl Config {
    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        let mut config = Self::default();
        for raw in read_text(path)?.lines() {
            let line = raw.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            let Some(separator) = line.find(|ch| matches!(ch, '\t' | ' ' | '=' | ',')) else {
                continue;
            };
            let (key, value) = line.split_at(separator);
            let value = value.get(1..).unwrap_or_default();
            let key = key.trim();
            let Some(canonical) = canonical_key(key) else { continue; };
            let value = value.trim();
            let value = if canonical == "码表存储位置" {
                normalize_path_setting(value)
            } else {
                value.to_owned()
            };
            config.values.insert(canonical.to_owned(), value);
        }
        config.refresh_typed();
        Ok(config)
    }

    pub fn text(&self) -> String {
        let mut lines = Vec::new();
        for (key, default) in DEFAULT_PAIRS {
            let value = self.values.get(*key).map(String::as_str).unwrap_or(default);
            lines.push(format!("{key}\t{value}"));
        }
        lines.join("\n") + "\n"
    }

    pub fn write_to(&self, path: impl AsRef<Path>) -> io::Result<()> {
        let path = path.as_ref();
        if let Some(parent) = path.parent() {
            if !parent.as_os_str().is_empty() {
                fs::create_dir_all(parent)?;
            }
        }
        fs::write(path, self.text())
    }

    pub fn set(&mut self, key: &str, value: &str) -> Result<bool, String> {
        let key = key.trim();
        if key.is_empty() {
            return Err("key is empty".to_owned());
        }
        let key = canonical_key(key).ok_or_else(|| "unknown key".to_owned())?;
        if !is_known(key) {
            return Err("unknown key".to_owned());
        }
        let value = value.trim();
        let value = if key == "码表存储位置" {
            normalize_path_setting(value)
        } else {
            value.to_owned()
        };
        let old = self.values.get(key).cloned().unwrap_or_default();
        if old == value {
            return Ok(false);
        }
        self.values.insert(key.to_owned(), value);
        if key == "最近码表对" {
            self.recent_schemas = parse_recent(self.values.get(key).map(String::as_str).unwrap_or(""));
        }
        self.refresh_typed();
        Ok(true)
    }

    pub fn get(&self, key: &str) -> String {
        self.values.get(key).cloned().unwrap_or_default()
    }

    pub fn sentence_active(&self) -> bool {
        self.sentence_input
            || (self.auto_enable_sentence && self.current_schema.contains("整句"))
    }

    pub fn record_recent_schema(&mut self, name: &str) {
        let name = name.trim();
        if name.is_empty() {
            return;
        }
        self.recent_schemas.retain(|item| !item.eq_ignore_ascii_case(name));
        self.recent_schemas.insert(0, name.to_owned());
        self.recent_schemas.truncate(2);
        self.values
            .insert("最近码表对".to_owned(), self.recent_schemas.join("|"));
    }

    fn refresh_typed(&mut self) {
        self.semicolon_second = self.flag("分号次选", true);
        self.quote_third = self.flag("引号三选", true);
        self.tab_clear = self.flag("TAB清屏", true);
        self.max_code_length = self.number("最大码长", 4).clamp(1, 16);
        self.auto_commit_no_repeat = self.flag("最大码长无重自动上屏", true);
        self.empty_code_clear = self.flag("空码自动清屏", true);
        self.enter_clear = self.flag("回车清屏", false);
        self.page_size = self.number("每页候选个数", 5).clamp(1, 10);
        self.mixed_input = self.flag("中英文不限长混合输入", false);
        self.sentence_input = self.flag("整句输入", false);
        self.early_commit = self.flag("整句自动提前上屏", false);
        self.auto_enable_sentence = self.flag("自动启用整句模式", true);
        self.ctrl_space_toggle = self.flag("Ctrl+空格切换中英文", true);
        self.shift_toggle = self.flag("shift切换中英文", true);
        self.default_chinese = self.flag("默认中文", true);
        self.hide_status_bar = self.flag("隐藏状态栏", false);
        self.code_masking = self.get("编码伪装");
        self.current_schema = self.get("当前码表");
        self.code_root = {
            let root = self.get("码表存储位置");
            if root.is_empty() {
                "码表".to_owned()
            } else {
                root
            }
        };
        self.theme = self.get("主题");
        self.font_name = self.get("字体");
        self.font_size = self
            .values
            .get("字体大小")
            .and_then(|value| value.parse().ok())
            .unwrap_or(17.0);
        self.vertical_candidates = self.flag("竖排候选", true);
        self.show_candidate_index = self.flag("显示候选序号", true);
        self.hide_candidate = self.flag("隐藏候选", false);
        self.show_input_code = self.flag("候选窗显示编码", false);
        self.candidate_expand_delay_ms = self.signed("延时显示候选(毫秒)", 0);
        self.annotation_expand_delay_ms = self.signed("延时展开注释和拆分(毫秒)", 0);
        self.sound_volume = self.number("按键音量0~100", 30) as i32;
        self.key_sound = self.flag("开启打字音效(娱乐)", false);
        self.neural_rerank = self.flag("整句神经重排", true);
        self.auto_switch_system_layout = self.flag("自动切换系统语言", true);
        self.native_hook_alt_backslash = self.flag("Alt+\\启用或禁用外挂版", true);
        self.use_clipboard_commit = self.flag("使用剪贴板上屏", false);
        self.clipboard_whitelist = self.get("使用剪贴板上屏白名单");
        self.ctrl_equal_add_ci = self.flag("Ctrl+等号手动加词", true);
        self.ctrl_m_switch_schema = self.flag("Ctrl+m切换最近码表", false);
        self.recent_schemas = parse_recent(&self.get("最近码表对"));
        self.cn_use_en_punc = self.flag("中文状态下使用英文标点", false);
        self.slash_outputs_dunhao = self.flag("/输出顿号", true);
        self.page_keys = self.get("翻页键");
        if self.page_keys.is_empty() {
            self.page_keys = "- =".to_owned();
        }
        self.back_query = self.flag("`键拼音反查", true);
        self.show_comment = self.flag("显示注释", true);
        self.show_split = self.flag("显示拆分", false);
    }

    fn flag(&self, key: &str, default: bool) -> bool {
        self.values
            .get(key)
            .map(|value| parse_bool(value))
            .unwrap_or(default)
    }

    fn number(&self, key: &str, default: usize) -> usize {
        self.values
            .get(key)
            .and_then(|value| value.parse().ok())
            .unwrap_or(default)
    }

    fn signed(&self, key: &str, default: i32) -> i32 {
        self.values
            .get(key)
            .and_then(|value| value.parse().ok())
            .unwrap_or(default)
    }
}

fn is_known(key: &str) -> bool {
    DEFAULT_PAIRS.iter().any(|(known, _)| *known == key)
}

/// Keep the object-form Dialog/test API useful without maintaining a second
/// typed setter.  The on-disk C# contract uses the Chinese names, while older
/// Rust callers used a handful of snake_case aliases.
pub fn canonical_key(key: &str) -> Option<&'static str> {
    let trimmed = key.trim();
    DEFAULT_PAIRS
        .iter()
        .find(|(known, _)| known.eq_ignore_ascii_case(trimmed))
        .map(|(known, _)| *known)
        .or_else(|| match trimmed {
            "semicolon_second" => Some("分号次选"),
            "quote_third" => Some("引号三选"),
            "tab_clear" => Some("TAB清屏"),
            "max_code_length" => Some("最大码长"),
            "auto_commit_no_repeat" => Some("最大码长无重自动上屏"),
            "empty_code_clear" => Some("空码自动清屏"),
            "enter_clear" => Some("回车清屏"),
            "page_size" => Some("每页候选个数"),
            "page_keys" => Some("翻页键"),
            "mixed_input" => Some("中英文不限长混合输入"),
            "sentence_input" => Some("整句输入"),
            "early_commit" => Some("整句自动提前上屏"),
            "auto_enable_sentence" => Some("自动启用整句模式"),
            "ctrl_space_toggle" => Some("Ctrl+空格切换中英文"),
            "shift_toggle" => Some("shift切换中英文"),
            "default_chinese" => Some("默认中文"),
            "code_root" => Some("码表存储位置"),
            "current_schema" => Some("当前码表"),
            "code_masking" => Some("编码伪装"),
            "neural_rerank" => Some("整句神经重排"),
            "show_input_code" => Some("候选窗显示编码"),
            "hide_candidate" => Some("隐藏候选"),
            "back_query" => Some("`键拼音反查"),
            "auto_start" => Some("开机自动启动"),
            "hide_status_bar" => Some("隐藏状态栏"),
            "theme" => Some("主题"),
            "cn_use_en_punc" => Some("中文状态下使用英文标点"),
            "native_hook_alt_backslash" => Some("Alt+\\启用或禁用外挂版"),
            "auto_switch_system_layout" => Some("自动切换系统语言"),
            "ctrl_equal_add_ci" => Some("Ctrl+等号手动加词"),
            "ctrl_m_switch_schema" => Some("Ctrl+m切换最近码表"),
            "recent_schemas" => Some("最近码表对"),
            "vertical_candidates" => Some("竖排候选"),
            "show_candidate_index" => Some("显示候选序号"),
            "show_comment" => Some("显示注释"),
            "show_split" => Some("显示拆分"),
            "candidate_expand_delay_ms" => Some("延时显示候选(毫秒)"),
            "annotation_expand_delay_ms" => Some("延时展开注释和拆分(毫秒)"),
            "slash_outputs_dunhao" => Some("/输出顿号"),
            "candidate_hide" => Some("隐藏候选"),
            "show_code" => Some("候选窗显示编码"),
            "clear_on_no_code" => Some("空码自动清屏"),
            "max_auto_commit" => Some("最大码长无重自动上屏"),
            "font_name" => Some("字体"),
            "font_size" => Some("字体大小"),
            "use_clipboard_commit" => Some("使用剪贴板上屏"),
            "clipboard_whitelist" => Some("使用剪贴板上屏白名单"),
            "key_sound" => Some("开启打字音效(娱乐)"),
            "sound_volume" => Some("按键音量0~100"),
            _ => None,
        })
}

fn parse_bool(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "是" | "true" | "on" | "yes" | "1"
    )
}

fn parse_recent(raw: &str) -> Vec<String> {
    raw.split('|')
        .map(str::trim)
        .filter(|item| !item.is_empty())
        .map(ToOwned::to_owned)
        .take(2)
        .collect()
}

fn normalize_path_setting(value: &str) -> String {
    let mut path = value.trim().to_owned();
    if path.is_empty() {
        return "码表".to_owned();
    }
    while path.len() > 1 && (path.ends_with('/') || path.ends_with('\\')) {
        // Preserve a Windows drive root (`C:\\`) while removing redundant
        // separators from ordinary relative/absolute directory settings.
        if path.len() == 3
            && path.as_bytes().get(1) == Some(&b':')
            && path.as_bytes().get(2).is_some_and(|byte| *byte == b'/' || *byte == b'\\')
        {
            break;
        }
        path.pop();
    }
    if path.is_empty() { "码表".to_owned() } else { path }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_chinese_tab_config() {
        let path = std::env::temp_dir().join("tigerclaw-rust-config-test.txt");
        fs::write(&path, "分号次选\t否\n引号三选=on\nTAB清屏 是\n").unwrap();
        let config = Config::load(&path).unwrap();
        assert!(!config.semicolon_second);
        assert!(config.quote_third);
        assert!(config.tab_clear);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn schema_name_enables_sentence_when_auto_flag_is_on() {
        let mut config = Config::default();
        assert!(!config.sentence_active());
        config.set("当前码表", "虎整句").unwrap();
        assert!(config.sentence_active());
        config.set("自动启用整句模式", "否").unwrap();
        assert!(!config.sentence_active());
    }

    #[test]
    fn code_root_is_normalized_like_core() {
        let path = std::env::temp_dir().join(format!(
            "tigerclaw-rust-config-path-{}",
            std::process::id()
        ));
        fs::write(&path, "码表存储位置\tfoo\\\\\n").unwrap();
        let mut config = Config::load(&path).unwrap();
        assert_eq!(config.code_root, "foo");
        config.set("code_root", "bar///").unwrap();
        assert_eq!(config.code_root, "bar");
        config.set("码表存储位置", "").unwrap();
        assert_eq!(config.code_root, "码表");
        let _ = fs::remove_file(path);
    }
}
