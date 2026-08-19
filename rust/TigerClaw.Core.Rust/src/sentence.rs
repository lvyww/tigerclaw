use crate::lexicon::Lexicon;

pub fn decode(raw: &str, lexicon: &Lexicon, max_code_length: usize, limit: usize) -> Vec<String> {
    let mut results = Vec::new();
    let mut path = Vec::new();
    visit(raw, 0, lexicon, max_code_length, &mut path, &mut results, limit);
    let mut unique = Vec::with_capacity(results.len());
    for result in results.drain(..) {
        if !unique.contains(&result) {
            unique.push(result);
        }
    }
    results.extend(unique);
    results.truncate(limit);
    results
}

fn visit(
    raw: &str,
    position: usize,
    lexicon: &Lexicon,
    max_code_length: usize,
    path: &mut Vec<String>,
    results: &mut Vec<String>,
    limit: usize,
) {
    if results.len() >= limit {
        return;
    }
    if position == raw.len() {
        results.push(path.concat());
        return;
    }
    let remaining = &raw[position..];
    for length in 1..=max_code_length.min(remaining.len()) {
        let end = position + length;
        if !raw.is_char_boundary(end) {
            continue;
        }
        let code = &raw[position..end];
        for candidate in lexicon.candidates(code).into_iter().take(8) {
            path.push(candidate);
            visit(raw, end, lexicon, max_code_length, path, results, limit);
            path.pop();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn segments_two_codes_into_sentence_candidates() {
        let path = std::env::temp_dir().join("tigerclaw-rust-sentence-test.txt");
        fs::write(&path, "我\tab\n爱\tcd\n你\tcd\n").unwrap();
        let lexicon = Lexicon::load(&path).unwrap();
        let results = decode("abcd", &lexicon, 2, 8);
        assert!(results.contains(&"我爱".to_owned()));
        assert!(results.contains(&"我你".to_owned()));
        let _ = fs::remove_file(path);
    }
}
