use crate::decoder::{decode_full, DecodeResult, DecoderOptions};
use crate::lexicon::Lexicon;
use crate::ngram::NgramModel;
use crate::ranks::CharacterRanks;
use crate::supplement::SupplementMatcher;

pub fn decode(raw: &str, lexicon: &Lexicon, _max_code_length: usize, limit: usize) -> Vec<String> {
    decode_result(
        raw,
        lexicon,
        None,
        &CharacterRanks::default(),
        &SupplementMatcher::empty(),
        limit,
        false,
        "",
    )
    .candidates
    .into_iter()
    .map(|item| item.text)
    .collect()
}

pub fn decode_result(
    raw: &str,
    lexicon: &Lexicon,
    model: Option<&NgramModel>,
    ranks: &CharacterRanks,
    supplements: &SupplementMatcher,
    limit: usize,
    include_early: bool,
    required_prefix: &str,
) -> DecodeResult {
    let options = DecoderOptions {
        beam_width: if model.is_some() { 2000 } else { 100 },
        ..DecoderOptions::default()
    };
    decode_full(
        raw,
        lexicon,
        model,
        ranks,
        supplements,
        limit,
        options,
        include_early,
        required_prefix,
    )
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
