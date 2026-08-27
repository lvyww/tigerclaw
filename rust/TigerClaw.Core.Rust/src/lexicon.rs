use std::collections::{HashMap, HashSet};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use crate::text::read_text;

#[derive(Debug, Clone)]
pub struct LexiconCandidate {
    /// Text sent to the target application.  A table entry may use the
    /// `display=>commit` notation; the packed representation never leaks out
    /// of the lexicon API.
    pub text: String,
    /// Text shown in the candidate/overlay UI.
    pub display_text: String,
    pub rank: i32,
    pub log_rank: f64,
    pub text_elements: Vec<String>,
}

#[derive(Debug, Clone)]
struct Row {
    code: String,
    text: String,
    frequency: i64,
    order: usize,
}

/// A code-table snapshot. `by_code` is kept compact for the hot key path;
/// the derived indexes make legality, construct-code and quick-symbol
/// decisions deterministic and cheap at runtime.
#[derive(Debug, Default, Clone)]
pub struct Lexicon {
    by_code: HashMap<String, Vec<String>>,
    ranked: HashMap<String, Vec<LexiconCandidate>>,
    prefixes: HashSet<String>,
    code_lengths: Vec<usize>,
    construct_map: HashMap<String, String>,
    full_code_map: HashMap<String, String>,
    short_symbol_heads: HashSet<char>,
    auto_short_symbols: HashSet<String>,
    annotations: HashMap<(String, String), String>,
    comments: HashMap<String, String>,
    splits: HashMap<String, String>,
}

impl Lexicon {
    pub fn from_entries(entries: &[(&str, &str)]) -> Self {
        let rows = entries
            .iter()
            .enumerate()
            .map(|(order, (code, text))| Row {
                code: normalize_code(code),
                text: pack_entry(text),
                frequency: 0,
                order,
            })
            .collect();
        Self::from_rows(rows, &[])
    }

    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        let path = path.as_ref();
        if path.is_dir() {
            return Self::load_directory(path);
        }
        let content = read_text(path)?;
        let mut rows = Vec::new();
        parse_rows(
            &content,
            path.extension()
                .and_then(|ext| ext.to_str())
                .is_some_and(|ext| ext.eq_ignore_ascii_case("yaml")),
            &mut rows,
            0,
        );
        Ok(Self::from_rows(rows, &[]))
    }

    /// Load every supported top-level file in a schema. A schema is an
    /// atomic snapshot: unreadable files return an error instead of silently
    /// publishing a partial lexicon. `补充语料.txt`, `构词.txt` and
    /// `用户调整.txt` are resources rather than ordinary code rows.
    pub fn load_directory(dir: impl AsRef<Path>) -> io::Result<Self> {
        let dir = dir.as_ref();
        let mut files: Vec<PathBuf> = fs::read_dir(dir)?
            .map(|entry| entry.map(|item| item.path()))
            .collect::<Result<_, _>>()?;
        files.retain(|path| {
            path.is_file()
                && (path.extension().and_then(|ext| ext.to_str()).is_some_and(|ext| {
                    ext.eq_ignore_ascii_case("txt") || ext.eq_ignore_ascii_case("yaml")
                })
                    || path.file_name().and_then(|name| name.to_str()).is_some_and(|name| {
                        name.ends_with(".注释") || name.ends_with(".拆分")
                    }))
                && !is_named(path, "补充语料.txt")
        });
        files.sort_by(|left, right| {
            let l = left.file_name().unwrap_or_default().to_string_lossy().to_ascii_lowercase();
            let r = right.file_name().unwrap_or_default().to_string_lossy().to_ascii_lowercase();
            l.cmp(&r)
        });

        let mut rows = Vec::new();
        let mut construct_rows = Vec::new();
        let mut adjustments = Vec::new();
        let mut comments = HashMap::new();
        let mut splits = HashMap::new();
        let mut order = 0;
        for path in files {
            let name = path.file_name().and_then(|name| name.to_str()).unwrap_or_default();
            let content = read_text(&path)?;
            if name.eq_ignore_ascii_case("构词.txt") {
                parse_rows(&content, false, &mut construct_rows, order);
                order += content.lines().count();
                continue;
            }
            if name.eq_ignore_ascii_case("用户调整.txt") {
                adjustments.extend(content.lines().map(ToOwned::to_owned));
                continue;
            }
            if name.ends_with(".注释") {
                parse_metadata(&content, &mut comments);
                continue;
            }
            if name.ends_with(".拆分") {
                parse_metadata_replace(&content, &mut splits);
                continue;
            }
            let yaml = path
                .extension()
                .and_then(|ext| ext.to_str())
                .is_some_and(|ext| ext.eq_ignore_ascii_case("yaml"));
            parse_rows(&content, yaml, &mut rows, order);
            order += content.lines().count();
        }
        let mut lexicon = Self::from_rows(rows, &construct_rows);
        lexicon.comments = comments;
        lexicon.splits = splits;
        lexicon.rebuild_annotations();
        lexicon.apply_adjustments(&adjustments);
        Ok(lexicon)
    }

    pub fn candidates(&self, code: &str) -> Vec<String> {
        self.by_code
            .get(&normalize_code(code))
            .map(|values| values.iter().map(|value| display_text(value)).collect())
            .unwrap_or_default()
    }

    pub fn ranked(&self, code: &str) -> &[LexiconCandidate] {
        self.ranked.get(&normalize_code(code)).map(Vec::as_slice).unwrap_or(&[])
    }

    pub fn code_lengths(&self) -> &[usize] { &self.code_lengths }

    pub fn max_code_length(&self) -> usize { self.code_lengths.last().copied().unwrap_or(1) }

    pub fn has_code(&self, code: &str) -> bool {
        self.by_code.get(&normalize_code(code)).is_some_and(|values| !values.is_empty())
    }

    pub fn is_proper_prefix(&self, code: &str) -> bool { self.prefixes.contains(&normalize_code(code)) }

    pub fn is_non_terminal(&self, code: &str) -> bool { self.is_proper_prefix(code) }

    pub fn is_unique_terminal(&self, code: &str) -> bool {
        let code = normalize_code(code);
        self.by_code.get(&code).is_some_and(|values| values.len() == 1 && !self.prefixes.contains(&code))
    }

    pub fn short_symbol_enabled(&self, symbol: char) -> bool { self.short_symbol_heads.contains(&symbol) }

    pub fn is_auto_short_symbol(&self, code: &str) -> bool {
        self.auto_short_symbols.contains(&normalize_code(code))
    }

    pub fn annotation(&self, code: &str, text: &str) -> Option<&str> {
        self.annotations
            .get(&(normalize_code(code), commit_text(text)))
            .map(String::as_str)
    }

    pub fn annotations_for_code(&self, code: &str, pinyin: bool, show_comment: bool, show_split: bool) -> Vec<String> {
        let code = normalize_code(code);
        let Some(values) = self.by_code.get(&code) else { return Vec::new(); };
        values
        .iter()
            .map(|entry| {
                if display_text(entry) != commit_text(entry) {
                    return String::new();
                }
                let text = commit_text(entry);
                if pinyin {
                    let split = self.split_code(&text);
                    let full = self.full_code(&text);
                    if !split.is_empty() && !full.is_empty() { format!("{split} | {full}") }
                    else if !split.is_empty() { split }
                    else { full }
                } else {
                    let split = show_split.then(|| self.split_code(&text)).unwrap_or_default();
                    let comment = show_comment.then(|| self.comments.get(&text).cloned().unwrap_or_default()).unwrap_or_default();
                    match (split.is_empty(), comment.is_empty()) {
                        (false, false) => format!("{split} {comment}"),
                        (false, true) => split,
                        (true, false) => comment,
                        (true, true) => String::new(),
                    }
                }
            })
            .collect()
    }

    pub fn annotation_for_candidate(&self, code: &str, index: usize, pinyin: bool, show_comment: bool, show_split: bool) -> String {
        let code = normalize_code(code);
        let Some(entry) = self.by_code.get(&code).and_then(|values| values.get(index)) else {
            return String::new();
        };
        if display_text(entry) != commit_text(entry) {
            return String::new();
        }
        let text = commit_text(entry);
        if pinyin {
            let split = self.split_code(&text);
            let full = self.full_code(&text);
            if !split.is_empty() && !full.is_empty() { format!("{split} | {full}") }
            else if !split.is_empty() { split }
            else { full }
        } else {
            let split = show_split.then(|| self.split_code(&text)).unwrap_or_default();
            let comment = show_comment.then(|| self.comments.get(&text).cloned().unwrap_or_default()).unwrap_or_default();
            match (split.is_empty(), comment.is_empty()) {
                (false, false) => format!("{split} {comment}"),
                (false, true) => split,
                (true, false) => comment,
                (true, true) => String::new(),
            }
        }
    }

    /// Return the per-text-element full-code spelling used in pinyin
    /// annotations.  The explicit full map wins, with construct-code data as
    /// a deterministic fallback for tables that do not provide a separate
    /// full-code file.
    pub fn full_code(&self, text: &str) -> String {
        text_elements(text)
            .into_iter()
            .map(|element| {
                self.full_code_map
                    .get(&element)
                    .cloned()
                    .or_else(|| self.construct_map.get(&element).cloned())
                    .unwrap_or_default()
            })
            .filter(|code| !code.is_empty())
            .collect::<Vec<_>>()
            .join("·")
    }

    /// Return split-map values joined by the same visible separator as C#.
    pub fn split_code(&self, text: &str) -> String {
        let elements = text_elements(text);
        let mut values = Vec::with_capacity(elements.len());
        for element in elements {
            if let Some(value) = self.splits.get(&element) {
                if !value.is_empty() { values.push(value.clone()); }
            }
        }
        values.join("·")
    }

    pub fn add_candidate(&mut self, code: &str, text: &str) -> bool {
        let code = normalize_code(code);
        let text = pack_entry(text.trim());
        if code.is_empty() || text.is_empty() { return false; }
        let values = self.by_code.entry(code).or_default();
        if values.iter().any(|value| commit_text(value) == commit_text(&text)) {
            return false;
        }
        values.insert(0, text);
        self.rebuild_index();
        true
    }

    /// Apply C#'s `{添加}`/Dialog semantics: replace an existing candidate
    /// with the same commit identity and append the new entry to the end of
    /// that code's stable list.
    pub fn append_candidate(&mut self, code: &str, text: &str) -> bool {
        let code = normalize_code(code);
        let text = pack_entry(text.trim());
        if code.is_empty() || text.is_empty() { return false; }
        let values = self.by_code.entry(code).or_default();
        let identity = commit_text(&text);
        values.retain(|value| commit_text(value) != identity);
        values.push(text);
        self.rebuild_index();
        true
    }

    pub fn contains_candidate(&self, code: &str, text: &str) -> bool {
        let text = commit_text(text);
        self.by_code
            .get(&normalize_code(code))
            .is_some_and(|values| values.iter().any(|value| commit_text(value) == text))
    }

    pub fn top_candidate(&self, code: &str) -> Option<String> {
        self.by_code
            .get(&normalize_code(code))
            .and_then(|values| values.first())
            .map(|value| commit_text(value))
    }

    pub fn display_candidate(&self, code: &str, index: usize) -> Option<String> {
        self.by_code
            .get(&normalize_code(code))
            .and_then(|values| values.get(index))
            .map(|value| display_text(value))
    }

    pub fn commit_candidate(&self, code: &str, index: usize) -> Option<String> {
        self.by_code
            .get(&normalize_code(code))
            .and_then(|values| values.get(index))
            .map(|value| commit_text(value))
    }

    pub fn move_top(&mut self, code: &str, text: &str) -> bool {
        let code = normalize_code(code);
        let text = pack_entry(text.trim());
        if code.is_empty() || commit_text(&text).is_empty() { return false; }
        let values = self.by_code.entry(code).or_default();
        let identity = commit_text(&text);
        let Some(index) = values.iter().position(|value| commit_text(value) == identity) else {
            values.insert(0, text);
            self.rebuild_index();
            return true;
        };
        if index == 0 { return false; }
        let value = values.remove(index);
        values.insert(0, value);
        self.rebuild_index();
        true
    }

    pub fn delete_candidate(&mut self, code: &str, text: &str) -> bool {
        let code = normalize_code(code);
        let Some(values) = self.by_code.get_mut(&code) else { return false; };
        let old_len = values.len();
        let text = commit_text(text);
        values.retain(|value| commit_text(value) != text);
        let changed = old_len != values.len();
        if changed { self.rebuild_index(); }
        changed
    }

    pub fn advance_candidate(&mut self, code: &str, text: &str) -> bool {
        let code = normalize_code(code);
        let Some(values) = self.by_code.get_mut(&code) else { return false; };
        let text = commit_text(text);
        let Some(index) = values.iter().position(|value| commit_text(value) == text) else { return false; };
        if index == 0 { return false; }
        values.swap(index, index - 1);
        self.rebuild_index();
        true
    }

    pub fn construct_code(&self, text: &str) -> String {
        let mut source = text.to_owned();
        for token in REMOVE_TOKENS { source = source.replace(token, ""); }
        let codes: Vec<String> = text_elements(&source)
            .into_iter()
            .filter_map(|element| {
                self.construct_map.get(&element).cloned().or_else(|| {
                    // C# treats an ASCII letter as a pseudo single character
                    // when constructing a phrase, using its lowercase letter
                    // twice as the full code.
                    (element.len() == 1 && element.as_bytes()[0].is_ascii_alphabetic())
                        .then(|| element.to_ascii_lowercase().repeat(2))
                })
            })
            .collect();
        match codes.len() {
            0 => String::new(),
            1 => codes[0].clone(),
            2 => {
                if codes[0].len() >= 2 && codes[1].len() >= 2 {
                    format!("{}{}", &codes[0][..2], &codes[1][..2])
                } else {
                    String::new()
                }
            }
            3 => {
                if codes[0].is_empty() || codes[1].is_empty() || codes[2].len() < 2 {
                    String::new()
                } else {
                    format!("{}{}{}", &codes[0][..1], &codes[1][..1], &codes[2][..2])
                }
            }
            _ => {
                if codes[0].is_empty() || codes[1].is_empty() || codes[2].is_empty() {
                    String::new()
                } else {
                    let last = codes.last().unwrap();
                    if last.is_empty() {
                        String::new()
                    } else {
                        format!("{}{}{}{}", &codes[0][..1], &codes[1][..1], &codes[2][..1], &last[..1])
                    }
                }
            }
        }
    }

    /// Adjustment files use the same operation prefixes as C# Core.
    pub fn apply_adjustments(&mut self, lines: &[String]) {
        for raw in lines {
            let line = raw.trim();
            let (operation, payload) = if line.starts_with("{添加}") { ("add", &line["{添加}".len()..]) }
                else if line.starts_with("{删除}") { ("delete", &line["{删除}".len()..]) }
                else if line.starts_with("{置顶}") { ("top", &line["{置顶}".len()..]) }
                else if line.starts_with("{前移}") { ("advance", &line["{前移}".len()..]) }
                else { continue };
            let Some((code, text)) = parse_pair(payload) else { continue; };
            match operation {
                "add" => { self.append_candidate(&code, &text); }
                "delete" => { self.delete_candidate(&code, &text); }
                "top" => { self.move_top(&code, &text); }
                "advance" => { self.advance_candidate(&code, &text); }
                _ => {}
            }
        }
    }

    pub fn export_line(&self, code: &str, text: &str) -> String {
        format!("{}\t{}\n", code.trim(), encode_entry(&format_entry(&pack_entry(text))))
    }

    /// Export in the code-first form understood by both C# and Rust loaders.
    pub fn export_all(&self) -> String {
        let mut codes: Vec<&String> = self.by_code.keys().collect();
        codes.sort_by(|left, right| left.to_ascii_lowercase().cmp(&right.to_ascii_lowercase()));
        let mut body = String::new();
        for code in codes {
            if let Some(values) = self.by_code.get(code) {
                if !values.is_empty() {
                    body.push_str(code);
                    body.push('\t');
                    for (index, value) in values.iter().enumerate() {
                        if index > 0 { body.push(' '); }
                        body.push_str(&format_entry(value));
                    }
                    body.push('\n');
                }
            }
        }
        body
    }

    /// C#'s export format is code-first with a single space between the code
    /// and all candidates, and keeps one row per code.  Keep `export_all`
    /// available for protocol/tests while exposing the exact file payload
    /// separately.
    pub fn export_csharp(&self) -> String {
        let mut codes: Vec<&String> = self.by_code.keys().collect();
        codes.sort_by(|left, right| left.to_ascii_lowercase().cmp(&right.to_ascii_lowercase()));
        let mut body = String::new();
        for code in codes {
            let Some(values) = self.by_code.get(code) else { continue; };
            if values.is_empty() { continue; }
            body.push_str(code);
            body.push(' ');
            for (index, value) in values.iter().enumerate() {
                if index > 0 { body.push(' '); }
                body.push_str(&encode_entry(&format_entry(value)));
            }
            body.push('\n');
        }
        body
    }

    fn from_rows(mut rows: Vec<Row>, construct_rows: &[Row]) -> Self {
        // A few legacy tables contain text-only rows.  C# infers their code
        // from the construct map after loading all coded rows; retain them
        // until that map has been built instead of silently dropping them.
        let no_code_rows: Vec<Row> = rows
            .iter()
            .filter(|row| row.code.is_empty())
            .cloned()
            .collect();
        rows.retain(|row| !row.code.is_empty() && !row.text.is_empty());
        rows.sort_by(|left, right| right.frequency.cmp(&left.frequency).then(left.order.cmp(&right.order)));
        let mut lexicon = Self::default();
        for row in rows {
            let values = lexicon.by_code.entry(normalize_code(&row.code)).or_default();
            if !values.iter().any(|value| value == &row.text) { values.push(row.text); }
        }
        let mut construct_rows = construct_rows.to_vec();
        construct_rows.sort_by(|left, right| right.frequency.cmp(&left.frequency).then(left.order.cmp(&right.order)));
        let mut explicit_construct_chars = HashSet::new();
        let mut derived_rows: Vec<Row> = lexicon.by_code.iter().flat_map(|(code, values)| values.iter().map(move |text| Row {
            code: code.clone(), text: text.clone(), frequency: 0, order: 0,
        })).collect();
        derived_rows.sort_by(|left, right| left.code.cmp(&right.code).then(left.text.cmp(&right.text)));
        for row in construct_rows {
            let code = normalize_code(&row.code);
            // `构词.txt` is a single-character override table.  C# ignores
            // phrase rows here; accepting each character from a phrase would
            // accidentally make a partial override win over the regular
            // construct-code selection.
            let elements = text_elements(&commit_text(&row.text));
            if elements.len() != 1 {
                continue;
            }
            for ch in elements {
                // Construct-file order (already frequency/order sorted) is
                // authoritative for word construction.
                if !lexicon.construct_map.contains_key(&ch) {
                    lexicon.construct_map.insert(ch.clone(), code.clone());
                    explicit_construct_chars.insert(ch.clone());
                }
            }
        }
        for row in derived_rows {
            let code = normalize_code(&row.code);
            for ch in text_elements(&commit_text(&row.text)) {
                if !explicit_construct_chars.contains(&ch) {
                    let replace_construct = match lexicon.construct_map.get(&ch) {
                        None => true,
                        Some(old) => construct_priority(&code) > construct_priority(old)
                            || (construct_priority(&code) == construct_priority(old) && code < *old),
                    };
                    if replace_construct { lexicon.construct_map.insert(ch.clone(), code.clone()); }
                }
                let replace_full = match lexicon.full_code_map.get(&ch) {
                    None => true,
                    Some(old) => code.len() > old.len()
                        || (code.len() == old.len() && code < *old),
                };
                if replace_full { lexicon.full_code_map.insert(ch, code.clone()); }
            }
        }
        lexicon.rebuild_index();
        for row in no_code_rows {
            if row.text.is_empty() {
                continue;
            }
            let code = lexicon.construct_code(&commit_text(&row.text));
            if code.is_empty() {
                continue;
            }
            let code = normalize_code(&code);
            let values = lexicon.by_code.entry(code).or_default();
            if !values.iter().any(|value| value == &row.text) {
                values.push(row.text);
            }
        }
        lexicon.rebuild_index();
        lexicon
    }

    fn rebuild_annotations(&mut self) {
        self.annotations.clear();
        let keys: Vec<(String, String)> = self
            .by_code
            .iter()
            .flat_map(|(code, values)| values.iter().map(move |value| (code.clone(), commit_text(value))))
            .collect();
        for (code, text) in keys {
            let mut value = String::new();
            if let Some(split) = self.splits.get(&text) { value.push_str(split); }
            if let Some(comment) = self.comments.get(&text) {
                if !value.is_empty() { value.push(' '); }
                value.push_str(comment);
            }
            if !value.is_empty() { self.annotations.insert((code, text), value); }
        }
    }

    fn rebuild_index(&mut self) {
        self.ranked.clear();
        self.prefixes.clear();
        self.short_symbol_heads.clear();
        self.auto_short_symbols.clear();
        let mut lengths = HashSet::new();
        let mut codes: Vec<String> = self.by_code.keys().cloned().collect();
        codes.sort();
        for code in codes {
            let Some(values) = self.by_code.get(&code) else { continue; };
            if values.is_empty() { continue; }
            for index in 1..code.len() { self.prefixes.insert(code[..index].to_owned()); }
            if let Some(head) = code.chars().next() { self.short_symbol_heads.insert(head); }
            lengths.insert(code.len());
            let ranked = values.iter().enumerate().map(|(index, text)| LexiconCandidate {
                text: commit_text(text),
                display_text: display_text(text),
                rank: (index + 1) as i32,
                log_rank: ((index + 1) as f64).ln(),
                text_elements: text_elements(&commit_text(text)),
            }).collect();
            self.ranked.insert(code, ranked);
        }
        // C# reserves the leading `z` quick-symbol path when no ordinary
        // code uses z after its first position (the historical marker is
        // inferred from an `a`-headed table).  Preserve that compatibility
        // even for tables which do not contain a concrete z row yet.
        let z_is_code = self
            .by_code
            .keys()
            .any(|code| code.chars().skip(1).any(|ch| ch.eq_ignore_ascii_case(&'z')));
        let a_is_head = self.by_code.keys().any(|code| code.starts_with('a'));
        if !z_is_code && a_is_head {
            self.short_symbol_heads.insert('z');
        }
        let symbols: Vec<char> = self.short_symbol_heads.iter().copied().filter(|ch| matches!(ch, ';' | '/' | '[' | 'z')).collect();
        for symbol in symbols {
            let mut codes: Vec<(String, usize)> = self.by_code.iter().filter(|(code, _)| code.starts_with(symbol)).map(|(code, values)| (code.clone(), values.len())).collect();
            codes.sort_by(|left, right| left.0.cmp(&right.0));
            for (prefix, _) in &codes {
                let total: usize = codes.iter().filter(|(code, _)| code.starts_with(prefix)).map(|(_, count)| *count).sum();
                if total == 1 { self.auto_short_symbols.insert(prefix.clone()); }
            }
        }
        let mut code_lengths: Vec<usize> = lengths.into_iter().collect();
        code_lengths.sort_unstable();
        self.code_lengths = code_lengths;
    }
}

const REMOVE_TOKENS: &[&str] = &[
    "\n", "\r", "\t", " ", "=", "，", "-", "。", "·", "【", "、", "】", "；",
    "）", "！", "@", "#", "￥", "%", "……", "&", "*", "（", "+", "《", "——", "》", "~", "{", "|", "}", "？", "：",
    ",", ".", "`", "[", "\\", "]", "/", ";", "'", ")", "!", "$", "^", "<", "_", ">", "?", "\"",
];

fn parse_rows(content: &str, yaml: bool, rows: &mut Vec<Row>, order_base: usize) {
    let mut body = !yaml;
    for (line_number, raw) in content.lines().enumerate() {
        let trimmed = raw.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') { continue; }
        if yaml && !body {
            if trimmed == "..." { body = true; }
            continue;
        }
        let line = strip_comment(trimmed).trim();
        if line.is_empty() || line.starts_with('{') || line == "---" || line == "..." { continue; }
        let fields: Vec<&str> = line.split(|ch: char| ch == '\t' || ch == ' ').filter(|field| !field.is_empty()).collect();
        if fields.is_empty() { continue; }
        if fields.len() == 1 {
            let text = clean_entry(fields[0]);
            // A single token is a legal no-code row in the C# loader.  Do
            // not discard it merely because the text happens to consist of
            // ASCII code characters (for example a numeric symbol entry).
            if !text.is_empty() {
                rows.push(Row { code: String::new(), text, frequency: 0, order: order_base + line_number });
            }
            continue;
        }
        let order = order_base + line_number;
        if looks_like_code(fields[0]) {
            let code = normalize_code(fields[0]);
            let mut frequency = 0;
            let mut end = fields.len();
            if end > 1 && fields[end - 1].parse::<i64>().is_ok() { frequency = fields[end - 1].parse().unwrap_or(0); end -= 1; }
            for text in &fields[1..end] {
                let text = clean_entry(text);
                if !text.is_empty() { rows.push(Row { code: code.clone(), text, frequency, order }); }
            }
        } else {
            // C# accepts both text-first forms:
            //   text<TAB>code<TAB>frequency
            //   text<TAB>frequency<TAB>code
            // and the two-column text<TAB>code variant.  Whitespace is also
            // accepted for legacy tables, with \s escapes preserving spaces.
            let mut code = "";
            let mut frequency = 0;
            if looks_like_code(fields[1]) {
                code = fields[1];
                if fields.len() > 2 { frequency = fields[2].parse().unwrap_or(0); }
            } else if fields.len() > 2 && fields[1].parse::<i64>().is_ok() && looks_like_code(fields[2]) {
                code = fields[2];
                frequency = fields[1].parse().unwrap_or(0);
            } else if fields[1].parse::<i64>().is_ok() {
                // Text + frequency, with no explicit code.  Infer it once
                // the complete table's construct map is available.
                let text = clean_entry(fields[0]);
                if !text.is_empty() {
                    rows.push(Row {
                        code: String::new(),
                        text,
                        frequency: fields[1].parse().unwrap_or(0),
                        order,
                    });
                }
                continue;
            }
            let text = clean_entry(fields[0]);
            if !code.is_empty() && !text.is_empty() {
                rows.push(Row { code: normalize_code(code), text, frequency, order });
            }
        }
    }
}

fn parse_pair(payload: &str) -> Option<(String, String)> {
    let fields: Vec<&str> = payload.trim().split(|ch: char| ch == '\t' || ch == ' ').filter(|field| !field.is_empty()).collect();
    if fields.len() < 2 { return None; }
    if looks_like_code(fields[0]) { Some((normalize_code(fields[0]), clean_entry(fields[1]))) }
    else if looks_like_code(fields[1]) { Some((normalize_code(fields[1]), clean_entry(fields[0]))) }
    else { None }
}

fn parse_metadata(content: &str, target: &mut HashMap<String, String>) {
    for raw in content.lines() {
        let line = strip_comment(raw).trim();
        if line.is_empty() { continue; }
        let fields: Vec<&str> = line
            .split(|ch: char| ch == '\t' || ch == ' ')
            .filter(|field| !field.is_empty())
            .collect();
        let key = fields.first().copied().unwrap_or_default();
        let value = fields.get(1).copied().unwrap_or_default();
        if !key.is_empty() && !value.is_empty() {
            let key = decode_entry_escapes(key);
            let value = decode_entry_escapes(value);
            target
                .entry(key)
                .and_modify(|old| {
                    if !old.is_empty() { old.push(' '); }
                    old.push_str(&value);
                })
                .or_insert(value);
        }
    }
}

fn parse_metadata_replace(content: &str, target: &mut HashMap<String, String>) {
    for raw in content.lines() {
        let line = strip_comment(raw).trim();
        if line.is_empty() { continue; }
        let fields: Vec<&str> = line
            .split(|ch: char| ch == '\t' || ch == ' ')
            .filter(|field| !field.is_empty())
            .collect();
        let key = fields.first().copied().unwrap_or_default();
        let value = fields.get(1).copied().unwrap_or_default();
        if !key.is_empty() && !value.is_empty() {
            target.insert(decode_entry_escapes(key), decode_entry_escapes(value));
        }
    }
}

fn normalize_code(value: &str) -> String { value.trim().to_ascii_lowercase() }

fn construct_priority(code: &str) -> i32 {
    match code.chars().next().map(|ch| ch.to_ascii_lowercase()) {
        Some('o') => 1,
        Some('z') => 2,
        Some(ch) if ch.is_ascii_alphabetic() => 3,
        _ => 0,
    }
}

fn clean_entry(value: &str) -> String {
    let value = value.trim();
    pack_entry(&decode_entry_escapes(value))
}

const DISPLAY_COMMIT_SEPARATOR: char = '\u{001e}';
const DISPLAY_COMMIT_MARKER: &str = "=>";

fn pack_entry(value: &str) -> String {
    let value = value.trim();
    let Some((display, commit)) = value.split_once(DISPLAY_COMMIT_MARKER) else {
        return value.to_owned();
    };
    let display = display.trim();
    let commit = commit.trim();
    if display.is_empty() || commit.is_empty() || display == commit {
        return value.to_owned();
    }
    format!("{display}{DISPLAY_COMMIT_SEPARATOR}{commit}")
}

fn display_text(value: &str) -> String {
    value
        .split_once(DISPLAY_COMMIT_SEPARATOR)
        .map(|(display, _)| display.to_owned())
        .unwrap_or_else(|| value.to_owned())
}

fn commit_text(value: &str) -> String {
    value
        .split_once(DISPLAY_COMMIT_SEPARATOR)
        .map(|(_, commit)| commit.to_owned())
        .unwrap_or_else(|| value.to_owned())
}

fn format_entry(value: &str) -> String {
    let display = display_text(value);
    let commit = commit_text(value);
    if display == commit {
        display
    } else {
        format!("{display}{DISPLAY_COMMIT_MARKER}{commit}")
    }
}

fn encode_entry(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('\t', "\\t")
        .replace("\r\n", "\\n")
        .replace('\n', "\\n")
        .replace(' ', "\\s")
}

/// A dependency-free approximation of .NET StringInfo text elements.  Keep
/// combining marks, variation selectors and ZWJ emoji continuations attached
/// to their base element for construction and candidate metadata.
pub fn text_elements(value: &str) -> Vec<String> {
    let mut result: Vec<String> = Vec::new();
    let mut join_next = false;
    let mut regional_count = 0usize;
    for ch in value.chars() {
        let combining = ('\u{0300}'..='\u{036f}').contains(&ch)
            || ('\u{1ab0}'..='\u{1aff}').contains(&ch)
            || ('\u{1dc0}'..='\u{1dff}').contains(&ch)
            || ('\u{20d0}'..='\u{20ff}').contains(&ch)
            || ('\u{fe00}'..='\u{fe0f}').contains(&ch)
            || ('\u{e0100}'..='\u{e01ef}').contains(&ch)
            // Emoji skin-tone modifiers are text-element continuations.
            || ('\u{1f3fb}'..='\u{1f3ff}').contains(&ch);
        let regional = ('\u{1f1e6}'..='\u{1f1ff}').contains(&ch);
        if combining || ch == '\u{200d}' || join_next || (regional && regional_count % 2 == 1) {
            if let Some(last) = result.last_mut() {
                last.push(ch);
            } else {
                result.push(ch.to_string());
            }
        } else {
            result.push(ch.to_string());
        }
        if regional {
            regional_count += 1;
        } else {
            regional_count = 0;
        }
        join_next = ch == '\u{200d}';
    }
    result
}

fn strip_comment(line: &str) -> &str {
    let mut slash_run = 0usize;
    for (index, ch) in line.char_indices() {
        if ch == '#' {
            if slash_run % 2 == 1 {
                // The backslash belongs to the escape spelling and is
                // removed by the caller's entry unescaper.  Leave the '#'
                // in the slice while retaining the exact C# boundary.
                slash_run = 0;
                continue;
            }
            return &line[..index];
        }
        slash_run = if ch == '\\' { slash_run.saturating_add(1) } else { 0 };
    }
    line
}

fn looks_like_code(value: &str) -> bool {
    !value.is_empty()
        && value.chars().all(|ch| ch.is_ascii_alphanumeric() || ";'/[]-=".contains(ch))
}

/// Decode the escape spelling used by the C# code-table exporter.  The
/// transformation is deliberately sequential (`\\\\` first), matching
/// `WrapDec` and keeping literal backslashes lossless.
fn decode_entry_escapes(value: &str) -> String {
    let mut output = String::with_capacity(value.len());
    let mut chars = value.chars();
    while let Some(ch) = chars.next() {
        if ch != '\\' {
            output.push(ch);
            continue;
        }
        match chars.next() {
            Some('\\') => output.push('\\'),
            Some('t') => output.push('\t'),
            Some('n') => output.push_str("\r\n"),
            Some('s') => output.push(' '),
            Some('#') => output.push('#'),
            Some(other) => {
                output.push('\\');
                output.push(other);
            }
            None => output.push('\\'),
        }
    }
    output
}

fn is_named(path: &Path, name: &str) -> bool {
    path.file_name().and_then(|value| value.to_str()).is_some_and(|value| value.eq_ignore_ascii_case(name))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_both_common_column_orders() {
        let path = std::env::temp_dir().join("tigerclaw-rust-lexicon-test.txt");
        fs::write(&path, "你好\tabcd\nxyzw\t世界\n123\t数字码\nabc#comment\t无效\n").unwrap();
        let lexicon = Lexicon::load(&path).unwrap();
        assert_eq!(lexicon.candidates("ABCD"), vec!["你好"]);
        assert_eq!(lexicon.candidates("xyzw"), vec!["世界"]);
        assert_eq!(lexicon.candidates("123"), vec!["数字码"]);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn frequency_sort_is_stable_and_legality_is_indexed() {
        let path = std::env::temp_dir().join("tigerclaw-rust-lexicon-frequency.txt");
        fs::write(&path, "abcd\t低\t1\nabcd\t高\t9\nabce\t长\t1\n").unwrap();
        let lexicon = Lexicon::load(&path).unwrap();
        assert_eq!(lexicon.candidates("abcd"), vec!["高", "低"]);
        assert!(lexicon.is_proper_prefix("abc"));
        assert!(!lexicon.is_unique_terminal("abc"));
        assert!(lexicon.is_unique_terminal("abce"));
        let _ = fs::remove_file(path);
    }

    #[test]
    fn directory_excludes_supplement_and_applies_adjustments() {
        let dir = std::env::temp_dir().join(format!("tigerclaw-rust-schema-{}", std::process::id()));
        let _ = fs::create_dir_all(&dir);
        fs::write(dir.join("a.txt"), "abcd\t甲\nabcd\t乙\n").unwrap();
        fs::write(dir.join("补充语料.txt"), "abcd\t错误\n").unwrap();
        fs::write(dir.join("用户调整.txt"), "{置顶}abcd\t乙\n").unwrap();
        let lexicon = Lexicon::load_directory(&dir).unwrap();
        assert_eq!(lexicon.candidates("abcd"), vec!["乙", "甲"]);
        // C# constructs a two-character phrase from the first two code keys
        // of each character (``ab`` + ``ab``), even when both characters
        // happen to use the same four-key source code.
        assert_eq!(lexicon.construct_code("甲乙"), "abab");
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn construct_file_ignores_phrase_rows_and_overrides_single_elements() {
        let dir = std::env::temp_dir().join(format!(
            "tigerclaw-rust-construct-{}",
            std::process::id()
        ));
        let _ = fs::create_dir_all(&dir);
        fs::write(dir.join("main.txt"), "abcd\t甲\nefgh\t乙\n").unwrap();
        fs::write(dir.join("构词.txt"), "zz\t甲\nxx\t甲乙\n").unwrap();
        let lexicon = Lexicon::load_directory(&dir).unwrap();
        assert_eq!(lexicon.construct_code("甲"), "zz");
        // The phrase row must not install a partial `甲`/`乙` override.
        assert_eq!(lexicon.construct_code("乙"), "efgh");
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn construct_choice_is_deterministic() {
        let lexicon = Lexicon::from_entries(&[("z", "甲"), ("a", "甲")]);
        assert_eq!(lexicon.construct_code("甲"), "a");
    }

    #[test]
    fn display_and_commit_forms_stay_separate() {
        let path = std::env::temp_dir().join("tigerclaw-rust-display-commit.txt");
        fs::write(&path, "abcd\t显示=>实际\n").unwrap();
        let lexicon = Lexicon::load(&path).unwrap();
        assert_eq!(lexicon.candidates("abcd"), vec!["显示"]);
        assert_eq!(lexicon.ranked("abcd")[0].text, "实际");
        assert_eq!(lexicon.top_candidate("abcd").as_deref(), Some("实际"));
        assert_eq!(lexicon.export_all(), "abcd\t显示=>实际\n");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn metadata_uses_text_elements_and_packed_entries_have_no_annotation() {
        let dir = std::env::temp_dir().join(format!(
            "tigerclaw-rust-metadata-{}",
            std::process::id()
        ));
        let _ = fs::create_dir_all(&dir);
        fs::write(dir.join("main.txt"), "ab\t显示=>实际\nab\t组合👩‍💻\n").unwrap();
        fs::write(dir.join("注释.注释"), "实际 注释\n组合👩‍💻 组合说明\n").unwrap();
        fs::write(dir.join("拆分.拆分"), "组 纟且\n合 口一\n👩‍💻 人机\n").unwrap();
        let lexicon = Lexicon::load_directory(&dir).unwrap();
        assert_eq!(lexicon.annotation_for_candidate("ab", 0, false, true, false), "");
        assert_eq!(lexicon.annotation_for_candidate("ab", 1, false, true, true), "纟且·口一·人机 组合说明");
        assert_eq!(lexicon.split_code("👩‍💻"), "人机");
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn text_elements_keep_combining_emoji_and_flag_sequences_together() {
        let elements = text_elements("e\u{301}👩‍💻🇨🇳");
        assert_eq!(elements, vec!["e\u{301}", "👩‍💻", "🇨🇳"]);
    }

    #[test]
    fn text_only_rows_use_construct_code_after_coded_rows_load() {
        let path = std::env::temp_dir().join("tigerclaw-rust-no-code-row.txt");
        fs::write(&path, "甲\tabcd\n乙\tcdef\n甲乙\n").unwrap();
        let lexicon = Lexicon::load(&path).unwrap();
        assert!(lexicon.contains_candidate("abcd", "甲乙"));
        let _ = fs::remove_file(path);
    }
}
