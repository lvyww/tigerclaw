use std::cmp::Ordering;
use std::collections::HashMap;

use crate::lexicon::Lexicon;
use crate::lexicon::text_elements;
use crate::ngram::{NgramModel, BOS, EOS};
use crate::ranks::CharacterRanks;
use crate::supplement::SupplementMatcher;

const AGGREGATE_DURING_EXPANSION: usize = 256;

#[derive(Debug, Clone)]
pub struct DecodedCandidate {
    pub text: String,
    pub segmented_code: String,
    pub score: f64,
    pub confidence: f64,
    pub supplement_score: f64,
    pub max_lexicon_rank: i32,
    pub text_edges: Vec<usize>,
    pub raw_edges: Vec<usize>,
}

#[derive(Debug, Clone, Default)]
pub struct EarlyCommitEvidence {
    pub proposal: String,
    pub raw_lengths: Vec<(String, usize)>,
    pub confidence_truncated: bool,
    pub ignore_neural_constraint: bool,
}

#[derive(Debug, Clone, Default)]
pub struct DecodeResult {
    pub raw: String,
    pub candidates: Vec<DecodedCandidate>,
    pub early: EarlyCommitEvidence,
}

#[derive(Debug, Clone, Copy)]
pub struct DecoderOptions {
    pub beam_width: usize,
    pub rank_penalty: f64,
    pub emitted_character_reward: f64,
}

impl Default for DecoderOptions {
    fn default() -> Self {
        Self {
            beam_width: 2000,
            rank_penalty: 0.03,
            emitted_character_reward: 2.0,
        }
    }
}

#[derive(Clone, Copy)]
enum ModelRef<'a> {
    Neutral,
    Ngram(&'a NgramModel),
    Prefer(&'a PreferModel),
}

pub struct PreferModel {
    preferred: &'static [&'static str],
}

impl PreferModel {
    pub fn new(preferred: &'static [&'static str]) -> Self {
        Self { preferred }
    }
}

impl ModelRef<'_> {
    fn log_probability(self, previous2: &str, previous1: &str, target: &str) -> f64 {
        match self {
            Self::Neutral => 0.0,
            Self::Ngram(model) => model.log_probability(previous2, previous1, target),
            Self::Prefer(model) => {
                if model.preferred.iter().any(|item| *item == target) {
                    0.0
                } else {
                    -10.0
                }
            }
        }
    }

    fn has_observed_bigram(self, previous: &str, target: &str) -> bool {
        match self {
            Self::Neutral | Self::Prefer(_) => false,
            Self::Ngram(model) => model.has_observed_bigram(previous, target),
        }
    }
}

#[derive(Clone)]
struct BeamState {
    score: f64,
    log_mass: f64,
    text: String,
    previous2: String,
    previous1: String,
    max_lexicon_rank: i32,
    supplement_state: usize,
    supplement_score: f64,
    text_edges: Vec<usize>,
    raw_edges: Vec<usize>,
}

struct BeamBucket {
    pending: Vec<BeamState>,
    best_by_text: Option<HashMap<String, BeamState>>,
    was_truncated: bool,
    is_frozen: bool,
}

impl BeamBucket {
    fn new() -> Self {
        Self {
            pending: Vec::new(),
            best_by_text: None,
            was_truncated: false,
            is_frozen: false,
        }
    }

    fn is_empty(&self) -> bool {
        if self.is_frozen {
            return self.pending.is_empty();
        }
        match &self.best_by_text {
            Some(map) => map.is_empty(),
            None => self.pending.is_empty(),
        }
    }

    fn add(&mut self, item: BeamState) {
        if self.is_frozen {
            self.is_frozen = false;
            self.ensure_aggregated();
        }
        if self.best_by_text.is_none() {
            self.pending.push(item);
            if self.pending.len() >= AGGREGATE_DURING_EXPANSION {
                self.ensure_aggregated();
            }
            return;
        }
        self.add_aggregated(item);
    }

    fn limit(&mut self, width: usize) -> (Vec<BeamState>, bool) {
        if self.is_frozen {
            return (self.pending.clone(), self.was_truncated);
        }
        self.ensure_aggregated();
        let bounded = width.max(1);
        let mut values: Vec<BeamState> = self
            .best_by_text
            .as_ref()
            .map(|map| map.values().cloned().collect())
            .unwrap_or_default();
        let truncated_now = values.len() > bounded;
        let truncated = self.was_truncated || truncated_now;
        self.was_truncated = truncated;
        values = select_exact_top(values, bounded, compare_beam);
        self.pending = values.clone();
        self.best_by_text = None;
        self.is_frozen = true;
        (values, truncated)
    }

    fn ensure_aggregated(&mut self) {
        if self.best_by_text.is_some() {
            return;
        }
        let pending = std::mem::take(&mut self.pending);
        self.best_by_text = Some(HashMap::with_capacity(pending.len().max(1)));
        for item in pending {
            self.add_aggregated(item);
        }
    }

    fn add_aggregated(&mut self, item: BeamState) {
        let map = self.best_by_text.as_mut().expect("aggregated");
        match map.get_mut(&item.text) {
            Some(previous) => {
                let combined = log_sum_exp(previous.log_mass, item.log_mass);
                if is_better(&item, previous) {
                    *previous = item;
                }
                previous.log_mass = combined;
            }
            None => {
                map.insert(item.text.clone(), item);
            }
        }
    }
}

pub struct SentenceDecoder {
    options: DecoderOptions,
    pub test_decode_delay: std::time::Duration,
    cached_raw: String,
    cached_states: Vec<BeamBucket>,
    cached_result: Option<DecodeResult>,
    cached_limit: usize,
    cached_include_early: bool,
    cached_required_prefix: String,
}

impl std::fmt::Debug for SentenceDecoder {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("SentenceDecoder")
            .field("beam_width", &self.options.beam_width)
            .field("cached_raw", &self.cached_raw)
            .finish()
    }
}

impl Default for SentenceDecoder {
    fn default() -> Self {
        Self::new(DecoderOptions::default())
    }
}

impl SentenceDecoder {
    pub fn new(options: DecoderOptions) -> Self {
        Self {
            options,
            test_decode_delay: std::time::Duration::ZERO,
            cached_raw: String::new(),
            cached_states: Vec::new(),
            cached_result: None,
            cached_limit: 0,
            cached_include_early: false,
            cached_required_prefix: String::new(),
        }
    }

    pub fn reset(&mut self) {
        self.cached_raw.clear();
        self.cached_states.clear();
        self.cached_result = None;
        self.cached_limit = 0;
        self.cached_include_early = false;
        self.cached_required_prefix.clear();
    }

    pub fn set_beam_width(&mut self, width: usize) {
        let width = width.max(1);
        if self.options.beam_width != width {
            self.options.beam_width = width;
            self.reset();
        }
    }

    pub fn decode(
        &mut self,
        raw: &str,
        lexicon: &Lexicon,
        model: Option<&NgramModel>,
        ranks: &CharacterRanks,
        supplements: &SupplementMatcher,
        limit: usize,
        include_early: bool,
        required_prefix: &str,
    ) -> DecodeResult {
        self.decode_with(
            raw,
            lexicon,
            model.map(ModelRef::Ngram).unwrap_or(ModelRef::Neutral),
            ranks,
            supplements,
            limit,
            include_early,
            required_prefix,
            true,
        )
    }

    pub fn decode_full(
        &mut self,
        raw: &str,
        lexicon: &Lexicon,
        model: Option<&NgramModel>,
        ranks: &CharacterRanks,
        supplements: &SupplementMatcher,
        limit: usize,
        include_early: bool,
        required_prefix: &str,
    ) -> DecodeResult {
        self.decode_with(
            raw,
            lexicon,
            model.map(ModelRef::Ngram).unwrap_or(ModelRef::Neutral),
            ranks,
            supplements,
            limit,
            include_early,
            required_prefix,
            false,
        )
    }

    pub fn decode_prefer(
        &mut self,
        raw: &str,
        lexicon: &Lexicon,
        prefer: &PreferModel,
        ranks: &CharacterRanks,
        supplements: &SupplementMatcher,
        limit: usize,
        include_early: bool,
        required_prefix: &str,
    ) -> DecodeResult {
        self.decode_prefer_ex(
            raw,
            lexicon,
            prefer,
            ranks,
            supplements,
            limit,
            include_early,
            required_prefix,
            false,
        )
    }

    pub fn decode_prefer_ex(
        &mut self,
        raw: &str,
        lexicon: &Lexicon,
        prefer: &PreferModel,
        ranks: &CharacterRanks,
        supplements: &SupplementMatcher,
        limit: usize,
        include_early: bool,
        required_prefix: &str,
        incremental: bool,
    ) -> DecodeResult {
        self.decode_with(
            raw,
            lexicon,
            ModelRef::Prefer(prefer),
            ranks,
            supplements,
            limit,
            include_early,
            required_prefix,
            incremental,
        )
    }

    fn decode_with(
        &mut self,
        raw: &str,
        lexicon: &Lexicon,
        model: ModelRef<'_>,
        ranks: &CharacterRanks,
        supplements: &SupplementMatcher,
        limit: usize,
        include_early: bool,
        required_prefix: &str,
        incremental: bool,
    ) -> DecodeResult {
        if !self.test_decode_delay.is_zero() {
            std::thread::sleep(self.test_decode_delay);
        }
        let normalized = normalize_raw(raw);
        if normalized.is_empty() || !normalized.chars().any(|ch| ch.is_ascii_alphabetic()) {
            if incremental {
                self.reset();
                self.cached_raw = normalized.clone();
                self.cached_result = Some(DecodeResult {
                    raw: normalized.clone(),
                    ..DecodeResult::default()
                });
                self.cached_limit = limit;
                self.cached_include_early = include_early;
                self.cached_required_prefix = required_prefix.to_owned();
            }
            return DecodeResult {
                raw: normalized,
                ..DecodeResult::default()
            };
        }

        if incremental
            && self.cached_raw == normalized
            && self.cached_result.is_some()
            && self.cached_limit == limit
            && self.cached_include_early == include_early
            && self.cached_required_prefix == required_prefix
        {
            return self.cached_result.clone().unwrap();
        }

        if incremental && self.cached_raw == normalized && !self.cached_states.is_empty() {
            let result = emit(
                &normalized,
                &mut self.cached_states,
                lexicon,
                model,
                ranks,
                self.options.beam_width,
                limit,
                include_early,
                required_prefix,
            );
            self.cached_result = Some(result.clone());
            self.cached_limit = limit;
            self.cached_include_early = include_early;
            self.cached_required_prefix = required_prefix.to_owned();
            return result;
        }

        let length = normalized.len();
        let mut states = None;
        if incremental && !self.cached_raw.is_empty() && !self.cached_states.is_empty() {
            let old_raw = &self.cached_raw;
            let old_length = old_raw.len();
            if old_length == 1 || length == 1 || (old_length <= 4) != (length <= 4) {
                states = None;
            } else if length > old_length && normalized.starts_with(old_raw) {
                let max_consume = lexicon.max_code_length() + trailing_selector_span(&normalized);
                let from_pos = old_length.saturating_add(1).saturating_sub(max_consume);
                let mut next = std::mem::take(&mut self.cached_states);
                if next.len() < length + 1 {
                    next.resize_with(length + 1, BeamBucket::new);
                }
                next.truncate(length + 1);
                for bucket in next.iter_mut().skip(old_length + 1) {
                    *bucket = BeamBucket::new();
                }
                expand_range(
                    &normalized,
                    &mut next,
                    lexicon,
                    model,
                    supplements,
                    self.options,
                    from_pos,
                    length,
                    old_length as isize,
                );
                states = Some(next);
            } else if length < old_length && old_raw.starts_with(&normalized) {
                let mut next = std::mem::take(&mut self.cached_states);
                next.truncate(length + 1);
                states = Some(next);
            }
        }

        let mut states = states.unwrap_or_else(|| {
            let mut created = create_states(length);
            expand_range(
                &normalized,
                &mut created,
                lexicon,
                model,
                supplements,
                self.options,
                0,
                length,
                -1,
            );
            created
        });
        let result = emit(
            &normalized,
            &mut states,
            lexicon,
            model,
            ranks,
            self.options.beam_width,
            limit,
            include_early,
            required_prefix,
        );
        if incremental {
            self.cached_raw = normalized;
            self.cached_states = states;
            self.cached_result = Some(result.clone());
            self.cached_limit = limit;
            self.cached_include_early = include_early;
            self.cached_required_prefix = required_prefix.to_owned();
        }
        result
    }
}

pub fn decode(
    raw: &str,
    lexicon: &Lexicon,
    model: Option<&NgramModel>,
    limit: usize,
    options: DecoderOptions,
) -> Vec<DecodedCandidate> {
    decode_full(
        raw,
        lexicon,
        model,
        &CharacterRanks::default(),
        &SupplementMatcher::empty(),
        limit,
        options,
        false,
        "",
    )
    .candidates
}

pub fn decode_full(
    raw: &str,
    lexicon: &Lexicon,
    model: Option<&NgramModel>,
    ranks: &CharacterRanks,
    supplements: &SupplementMatcher,
    limit: usize,
    options: DecoderOptions,
    include_early: bool,
    required_prefix: &str,
) -> DecodeResult {
    SentenceDecoder::new(options).decode_full(
        raw,
        lexicon,
        model,
        ranks,
        supplements,
        limit,
        include_early,
        required_prefix,
    )
}

pub fn decode_with_prefer(
    raw: &str,
    lexicon: &Lexicon,
    prefer: &PreferModel,
    ranks: &CharacterRanks,
    supplements: &SupplementMatcher,
    limit: usize,
    options: DecoderOptions,
    include_early: bool,
    required_prefix: &str,
) -> DecodeResult {
    SentenceDecoder::new(options).decode_prefer(
        raw,
        lexicon,
        prefer,
        ranks,
        supplements,
        limit,
        include_early,
        required_prefix,
    )
}

fn create_states(length: usize) -> Vec<BeamBucket> {
    let mut states = Vec::with_capacity(length + 1);
    for _ in 0..=length {
        states.push(BeamBucket::new());
    }
    states[0].add(BeamState {
        score: 0.0,
        log_mass: 0.0,
        text: String::new(),
        previous2: BOS.to_owned(),
        previous1: BOS.to_owned(),
        max_lexicon_rank: 1,
        supplement_state: 0,
        supplement_score: 0.0,
        text_edges: Vec::new(),
        raw_edges: Vec::new(),
    });
    states
}

fn expand_range(
    raw: &str,
    states: &mut [BeamBucket],
    lexicon: &Lexicon,
    model: ModelRef<'_>,
    supplements: &SupplementMatcher,
    options: DecoderOptions,
    from_pos: usize,
    length: usize,
    minimum_consumed_end_exclusive: isize,
) {
    let allow_all_ranks = raw.len() <= 4;
    let beam_width = options.beam_width.max(1);
    for position in from_pos..length {
        let (current, _) = states[position].limit(beam_width);
        if current.is_empty() {
            continue;
        }
        if position > 0 && is_short_symbol(raw.as_bytes()[position] as char) {
            continue;
        }
        for &code_length in lexicon.code_lengths() {
            let code_end = position + code_length;
            if code_end > length {
                continue;
            }
            let code = &raw[position..code_end];
            let ranked = lexicon.ranked(code);
            if ranked.is_empty() {
                continue;
            }
            let (consumed_end, selected_rank) = read_code_suffix(raw, code_end);
            if (consumed_end as isize) <= minimum_consumed_end_exclusive {
                continue;
            }
            if length > 1 && consumed_end - position < 2 {
                continue;
            }
            for item in &current {
                for candidate in ranked {
                    let rank_matches = if selected_rank > 0 {
                        candidate.rank == selected_rank
                    } else {
                        allow_all_ranks || candidate.rank == 1
                    };
                    if !rank_matches {
                        continue;
                    }
                    let mut score = item.score;
                    let mut supplement_added = 0.0;
                    let mut supplement_state = item.supplement_state;
                    let mut previous2 = item.previous2.clone();
                    let mut previous1 = item.previous1.clone();
                    for target in &candidate.text_elements {
                        score += model.log_probability(&previous2, &previous1, target);
                        score += options.emitted_character_reward;
                        if !supplements.is_empty() {
                            let (next_state, reward) = supplements.advance(supplement_state, target);
                            supplement_state = next_state;
                            score += reward;
                            supplement_added += reward;
                        }
                        previous2 = previous1;
                        previous1 = target.clone();
                    }
                    if selected_rank == 0 {
                        score -= options.rank_penalty * candidate.log_rank;
                    }
                    let text = format!("{}{}", item.text, candidate.text);
                    let mut text_edges = item.text_edges.clone();
                    let mut raw_edges = item.raw_edges.clone();
                    text_edges.push(text_elements(&text).len());
                    raw_edges.push(consumed_end);
                    states[consumed_end].add(BeamState {
                        log_mass: item.log_mass + (score - item.score - supplement_added),
                        score,
                        text,
                        previous2,
                        previous1,
                        max_lexicon_rank: item.max_lexicon_rank.max(candidate.rank),
                        supplement_state,
                        supplement_score: item.supplement_score + supplement_added,
                        text_edges,
                        raw_edges,
                    });
                }
            }
        }
    }
}

fn emit(
    normalized: &str,
    states: &mut [BeamBucket],
    lexicon: &Lexicon,
    model: ModelRef<'_>,
    ranks: &CharacterRanks,
    beam_width: usize,
    limit: usize,
    include_early: bool,
    required_prefix: &str,
) -> DecodeResult {
    let mut result = DecodeResult {
        raw: normalized.to_owned(),
        ..DecodeResult::default()
    };
    let (completed, truncated) = states[normalized.len()].limit(beam_width);
    let evaluated: Vec<DecodedCandidate> = completed
        .into_iter()
        .filter(|item| required_prefix.is_empty() || item.text.starts_with(required_prefix))
        .map(|item| evaluate(item, model, ranks))
        .collect();
    if include_early {
        result.early = build_early_commit(
            normalized,
            states,
            &evaluated,
            truncated,
            lexicon,
            model,
            ranks,
            beam_width,
            required_prefix,
        );
    }
    let mut visible = select_exact_top(evaluated, limit.max(1), compare_decoded);
    if !required_prefix.is_empty() {
        for candidate in &mut visible {
            if let Some(stripped) = candidate.text.strip_prefix(required_prefix) {
                candidate.text = stripped.to_owned();
            }
        }
        visible.retain(|item| !item.text.is_empty() || required_prefix.is_empty());
    }
    for candidate in &mut visible {
        candidate.segmented_code = build_segmented(normalized, &candidate.raw_edges);
    }
    result.candidates = visible;
    result
}

fn evaluate(item: BeamState, model: ModelRef<'_>, ranks: &CharacterRanks) -> DecodedCandidate {
    let ending = model.log_probability(&item.previous2, &item.previous1, EOS)
        - isolation_penalty(&item.text, model, ranks);
    DecodedCandidate {
        text: item.text,
        segmented_code: String::new(),
        score: item.score + ending,
        confidence: item.log_mass + ending,
        supplement_score: item.supplement_score,
        max_lexicon_rank: item.max_lexicon_rank,
        text_edges: item.text_edges,
        raw_edges: item.raw_edges,
    }
}

fn build_segmented(raw: &str, raw_edges: &[usize]) -> String {
    if raw_edges.is_empty() {
        return String::new();
    }
    let mut pieces = Vec::with_capacity(raw_edges.len());
    let mut start = 0usize;
    for &end in raw_edges {
        if end > start && end <= raw.len() {
            pieces.push(&raw[start..end]);
            start = end;
        }
    }
    pieces.join(" ")
}

fn isolation_penalty(text: &str, model: ModelRef<'_>, ranks: &CharacterRanks) -> f64 {
    if ranks.is_empty() || text.is_empty() {
        return 0.0;
    }
    let elements = text_elements(text);
    let mut penalty = 0.0;
    for (index, element) in elements.iter().enumerate() {
        if ranks.rank(element) <= 3000 {
            continue;
        }
        let left_hit = index > 0 && model.has_observed_bigram(&elements[index - 1], element);
        let right_hit = index + 1 < elements.len()
            && model.has_observed_bigram(element, &elements[index + 1]);
        if !left_hit && !right_hit {
            penalty += 2.0;
        }
    }
    penalty
}

fn log_sum_exp(left: f64, right: f64) -> f64 {
    let max = left.max(right);
    max + ((left - max).exp() + (right - max).exp()).ln()
}

fn is_incomplete_code_tail(lexicon: &Lexicon, tail: &str) -> bool {
    if tail.is_empty()
        || !tail.chars().all(|ch| ch.is_ascii_alphabetic())
        || !lexicon.is_proper_prefix(tail)
    {
        return false;
    }
    tail.len() < 2 || lexicon.ranked(tail).is_empty()
}

fn build_early_commit(
    normalized: &str,
    states: &mut [BeamBucket],
    completed: &[DecodedCandidate],
    completed_truncated: bool,
    lexicon: &Lexicon,
    model: ModelRef<'_>,
    ranks: &CharacterRanks,
    beam_width: usize,
    required_prefix: &str,
) -> EarlyCommitEvidence {
    let mut truncated = completed_truncated;
    let mut by_text: HashMap<String, (DecodedCandidate, f64)> = HashMap::new();
    let mut add = |candidate: DecodedCandidate| {
        if candidate.text.is_empty() {
            return;
        }
        if !required_prefix.is_empty() && !candidate.text.starts_with(required_prefix) {
            return;
        }
        let confidence = candidate.confidence;
        match by_text.get_mut(&candidate.text) {
            Some((existing, mass)) => {
                *mass = log_sum_exp(*mass, confidence);
                if confidence > existing.confidence {
                    *existing = candidate;
                }
            }
            None => {
                by_text.insert(candidate.text.clone(), (candidate, confidence));
            }
        }
    };
    for candidate in completed {
        add(candidate.clone());
    }

    let mut uses_incomplete_tail = false;
    let max_code = lexicon.max_code_length().max(1);
    let max_tail = (max_code - 1).min(normalized.len().saturating_sub(1));
    for tail_length in 1..=max_tail {
        let consumed = normalized.len() - tail_length;
        let tail = &normalized[consumed..];
        if !is_incomplete_code_tail(lexicon, tail) || states[consumed].is_empty() {
            continue;
        }
        let (partial, partial_truncated) = states[consumed].limit(beam_width);
        if partial.is_empty() {
            continue;
        }
        uses_incomplete_tail = true;
        truncated |= partial_truncated;
        for item in partial {
            add(evaluate(item, model, ranks));
        }
    }

    let mut evidence = EarlyCommitEvidence {
        confidence_truncated: truncated,
        ..EarlyCommitEvidence::default()
    };
    if by_text.is_empty() {
        return evidence;
    }
    let mut ordered: Vec<(DecodedCandidate, f64)> = by_text.into_values().collect();
    if uses_incomplete_tail {
        ordered.sort_by(|left, right| {
            right
                .1
                .partial_cmp(&left.1)
                .unwrap_or(Ordering::Equal)
                .then_with(|| left.0.text.cmp(&right.0.text))
        });
    } else {
        ordered.sort_by(|left, right| compare_decoded(&left.0, &right.0));
    }

    let full_top = completed.iter().max_by(|left, right| {
        left.confidence
            .partial_cmp(&right.confidence)
            .unwrap_or(Ordering::Equal)
            .then_with(|| right.text.cmp(&left.text))
    });
    let max = ordered
        .iter()
        .map(|item| item.1)
        .fold(f64::NEG_INFINITY, f64::max);
    let mut total = 0.0;
    let mut prefix_mass: HashMap<String, f64> = HashMap::new();
    let mut prefix_elements: HashMap<String, usize> = HashMap::new();
    for (candidate, confidence) in &ordered {
        let weight = (confidence - max).exp();
        total += weight;
        let chars = text_elements(&candidate.text);
        for count in 1..chars.len() {
            let prefix: String = chars[..count].concat();
            *prefix_mass.entry(prefix.clone()).or_insert(0.0) += weight;
            prefix_elements.insert(prefix, count);
        }
    }
    let mut proposal = String::new();
    let mut proposal_elements = 0usize;
    for (prefix, mass) in &prefix_mass {
        let elements = prefix_elements[prefix];
        if *mass / total >= 0.995 && elements > proposal_elements {
            proposal = prefix.clone();
            proposal_elements = elements;
        } else if *mass / total >= 0.995
            && elements == proposal_elements
            && (proposal.is_empty() || prefix < &proposal)
        {
            proposal = prefix.clone();
        }
    }
    evidence.proposal = proposal.clone();
    if !proposal.is_empty() {
        let proposal_chars = text_elements(&proposal).len();
        let mut raw_lengths = Vec::new();
        for (candidate, _) in &ordered {
            if !candidate.text.starts_with(&proposal) {
                continue;
            }
            for (index, count) in candidate.text_edges.iter().enumerate() {
                if *count > proposal_chars {
                    break;
                }
                let prefix: String = text_elements(&candidate.text)[..*count].concat();
                if proposal.starts_with(&prefix)
                    && !raw_lengths.iter().any(|(existing, _)| existing == &prefix)
                {
                    raw_lengths.push((prefix, candidate.raw_edges[index]));
                }
            }
            if raw_lengths.len() == proposal_chars {
                break;
            }
        }
        evidence.raw_lengths = raw_lengths;
    }
    evidence.ignore_neural_constraint = uses_incomplete_tail
        && !ordered.is_empty()
        && full_top
            .map(|top| ordered[0].0.text != top.text)
            .unwrap_or(true);
    evidence
}

fn normalize_raw(raw: &str) -> String {
    raw.chars()
        .filter(|ch| !ch.is_whitespace())
        .map(|ch| ch.to_ascii_lowercase())
        .collect()
}

fn is_short_symbol(value: char) -> bool {
    matches!(value, ';' | '/' | '[')
}

fn trailing_selector_span(raw: &str) -> usize {
    let bytes = raw.as_bytes();
    let mut index = bytes.len();
    while index > 0 {
        let mark = bytes[index - 1] as char;
        if mark.is_ascii_digit() || mark == ';' || mark == '\'' {
            index -= 1;
        } else {
            break;
        }
    }
    bytes.len() - index
}

fn read_code_suffix(raw: &str, code_end: usize) -> (usize, i32) {
    if code_end >= raw.len() {
        return (code_end, 0);
    }
    let bytes = raw.as_bytes();
    let mark = bytes[code_end] as char;
    if mark == ';' {
        return (code_end + 1, 2);
    }
    if mark == '\'' {
        return (code_end + 1, 3);
    }
    if mark.is_ascii_digit() {
        let mut digit_end = code_end;
        while digit_end < bytes.len() && (bytes[digit_end] as char).is_ascii_digit() {
            digit_end += 1;
        }
        let token = &raw[code_end..digit_end];
        let rank = if token == "0" {
            10
        } else {
            token.parse().unwrap_or(0)
        };
        return (digit_end, rank);
    }
    (code_end, 0)
}

fn is_better(item: &BeamState, previous: &BeamState) -> bool {
    item.max_lexicon_rank < previous.max_lexicon_rank
        || (item.max_lexicon_rank == previous.max_lexicon_rank && item.score > previous.score)
}

fn compare_beam(left: &BeamState, right: &BeamState) -> Ordering {
    left.max_lexicon_rank
        .cmp(&right.max_lexicon_rank)
        .then_with(|| right.score.partial_cmp(&left.score).unwrap_or(Ordering::Equal))
        .then_with(|| left.text.cmp(&right.text))
}

fn compare_decoded(left: &DecodedCandidate, right: &DecodedCandidate) -> Ordering {
    left.max_lexicon_rank
        .cmp(&right.max_lexicon_rank)
        .then_with(|| right.score.partial_cmp(&left.score).unwrap_or(Ordering::Equal))
        .then_with(|| left.text.cmp(&right.text))
}

fn select_exact_top<T, F>(values: Vec<T>, limit: usize, mut cmp: F) -> Vec<T>
where
    F: FnMut(&T, &T) -> Ordering,
{
    let limit = limit.max(1);
    if values.len() <= limit {
        let mut values = values;
        values.sort_by(|left, right| cmp(left, right));
        return values;
    }
    let mut heap: Vec<T> = Vec::with_capacity(limit);
    for item in values {
        if heap.len() < limit {
            heap.push(item);
            let index = heap.len() - 1;
            sift_worst_up(&mut heap, index, &mut cmp);
        } else if cmp(&item, &heap[0]) == Ordering::Less {
            heap[0] = item;
            sift_worst_down(&mut heap, 0, &mut cmp);
        }
    }
    heap.sort_by(|left, right| cmp(left, right));
    heap
}

fn sift_worst_up<T, F>(heap: &mut [T], mut index: usize, cmp: &mut F)
where
    F: FnMut(&T, &T) -> Ordering,
{
    while index > 0 {
        let parent = (index - 1) / 2;
        if cmp(&heap[parent], &heap[index]) != Ordering::Less {
            return;
        }
        heap.swap(parent, index);
        index = parent;
    }
}

fn sift_worst_down<T, F>(heap: &mut [T], mut index: usize, cmp: &mut F)
where
    F: FnMut(&T, &T) -> Ordering,
{
    loop {
        let left = index * 2 + 1;
        if left >= heap.len() {
            return;
        }
        let right = left + 1;
        let worse = if right < heap.len() && cmp(&heap[right], &heap[left]) == Ordering::Greater {
            right
        } else {
            left
        };
        if cmp(&heap[index], &heap[worse]) != Ordering::Less {
            return;
        }
        heap.swap(index, worse);
        index = worse;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::lexicon::Lexicon;
    use std::fs;
    use std::sync::atomic::{AtomicU64, Ordering};

    static TEMP_FILE_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    fn lexicon(lines: &str) -> Lexicon {
        let path = std::env::temp_dir().join(format!(
            "tigerclaw-rust-decoder-{}-{}.txt",
            std::process::id(),
            TEMP_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::write(&path, lines).unwrap();
        let loaded = Lexicon::load(&path).unwrap();
        let _ = fs::remove_file(path);
        loaded
    }

    fn texts(raw: &str, lex: &Lexicon) -> Vec<String> {
        decode(
            raw,
            lex,
            None,
            20,
            DecoderOptions {
                beam_width: 100,
                ..DecoderOptions::default()
            },
        )
        .into_iter()
        .map(|item| item.text)
        .collect()
    }

    fn assert_same_result(left: &DecodeResult, right: &DecodeResult) {
        assert_eq!(
            left.candidates
                .iter()
                .map(|item| (item.text.as_str(), item.segmented_code.as_str(), item.max_lexicon_rank))
                .collect::<Vec<_>>(),
            right
                .candidates
                .iter()
                .map(|item| (item.text.as_str(), item.segmented_code.as_str(), item.max_lexicon_rank))
                .collect::<Vec<_>>()
        );
        for (left_item, right_item) in left.candidates.iter().zip(&right.candidates) {
            assert!((left_item.score - right_item.score).abs() < 1e-9);
            assert!((left_item.confidence - right_item.confidence).abs() < 1e-9);
        }
        assert_eq!(left.early.proposal, right.early.proposal);
        assert_eq!(
            left.early.confidence_truncated,
            right.early.confidence_truncated
        );
        assert_eq!(
            left.early.ignore_neural_constraint,
            right.early.ignore_neural_constraint
        );
    }

    #[test]
    fn gyfs_keeps_whole_code_path() {
        let lex = lexicon("土\tgy\n某\tfs\n堽\tgyfs\n");
        assert!(texts("gyfs", &lex).contains(&"堽".to_owned()));
    }

    #[test]
    fn gyfs2_uses_rank_two_whole_path() {
        let lex = lexicon("堽\tgyfs\n第二\tgyfs\n");
        assert!(texts("gyfs2", &lex).contains(&"第二".to_owned()));
    }

    #[test]
    fn pzjn2_selects_second_rank() {
        let lex = lexicon("粹\tpzjn\n踤\tpzjn\n");
        assert_eq!(texts("pzjn2", &lex).first().map(String::as_str), Some("踤"));
    }

    #[test]
    fn one_key_allows_all_ranks_and_rejects_embedded_one_key() {
        let lex = lexicon("人\tj\n什么\tj\n怎样\tj\n是\tot\n");
        let one = texts("j", &lex);
        assert!(one.contains(&"人".to_owned()));
        assert!(one.contains(&"什么".to_owned()));
        assert!(texts("otj", &lex).is_empty());
        assert_eq!(texts("otj2", &lex).first().map(String::as_str), Some("是什么"));
        assert_eq!(texts("j;", &lex).first().map(String::as_str), Some("什么"));
        assert_eq!(texts("j'", &lex).first().map(String::as_str), Some("怎样"));
    }

    #[test]
    fn phrase_paths_stay_legal() {
        let lex = lexicon(
            "那\tau\n依\tjti\n你\tjx\n之\tri\n见\tej\n今\tjae\n天\tfm\n们\tja\n怎\twh\n么\ttk\n样\tegy\n这\tvu\n上\tyf\n面\tbm\n还\tcu\n好\tbh\n",
        );
        assert!(texts("aujtijxriej", &lex).contains(&"那依你之见".to_owned()));
        assert!(texts("jaefmjxjawhtkegy", &lex).contains(&"今天你们怎么样".to_owned()));
        assert!(texts("vuyfbmcubh", &lex).contains(&"这上面还好".to_owned()));
    }

    #[test]
    fn supplement_reward_prefers_marked_phrase() {
        let lex = lexicon("茧\tabc\n师\tdef\n齿\tab\n烧\tcdef\n");
        let matcher = crate::supplement::SupplementMatcher::build(&[
            crate::supplement::SupplementEntry::create("茧师".to_owned(), 3000),
        ]);
        let options = DecoderOptions {
            beam_width: 100,
            emitted_character_reward: 0.0,
            ..DecoderOptions::default()
        };
        let result = decode_full(
            "abcdef",
            &lex,
            None,
            &CharacterRanks::default(),
            &matcher,
            20,
            options,
            false,
            "",
        );
        assert_eq!(
            result.candidates.first().map(|item| item.text.as_str()),
            Some("茧师")
        );
        assert!(result.candidates[0].supplement_score > 0.0);
    }

    #[test]
    fn segmented_code_inserts_display_spaces() {
        let lex = lexicon("是\tot\n人\tj\n什么\tj\n");
        let decoded = decode(
            "otj2",
            &lex,
            None,
            20,
            DecoderOptions {
                beam_width: 100,
                ..DecoderOptions::default()
            },
        );
        assert_eq!(
            decoded.first().map(|item| item.segmented_code.as_str()),
            Some("ot j2")
        );
    }

    #[test]
    fn isolation_penalty_applies_only_when_ranks_are_loaded() {
        let lex = lexicon("龘\tab\n");
        let options = DecoderOptions {
            beam_width: 8,
            emitted_character_reward: 0.0,
            ..DecoderOptions::default()
        };
        let empty = decode_full(
            "ab",
            &lex,
            None,
            &CharacterRanks::default(),
            &SupplementMatcher::empty(),
            8,
            options,
            false,
            "",
        );
        let ranked = decode_full(
            "ab",
            &lex,
            None,
            &CharacterRanks::parse("的\n一\n是\n"),
            &SupplementMatcher::empty(),
            8,
            options,
            false,
            "",
        );
        assert_eq!(empty.candidates[0].text, "龘");
        assert_eq!(ranked.candidates[0].text, "龘");
        assert_eq!(empty.candidates[0].score, ranked.candidates[0].score + 2.0);
    }

    #[test]
    fn incomplete_tail_feeds_confidence_without_changing_visible_candidates() {
        let lex = lexicon("甲乙\tab\n丁戊\tabc\n丙\tcd\n");
        let prefer = PreferModel::new(&["甲", "乙", "丙", "辛"]);
        let options = DecoderOptions {
            beam_width: 100,
            emitted_character_reward: 2.0,
            ..DecoderOptions::default()
        };
        let result = decode_with_prefer(
            "abc",
            &lex,
            &prefer,
            &CharacterRanks::default(),
            &SupplementMatcher::empty(),
            20,
            options,
            true,
            "",
        );
        assert_eq!(
            result.candidates.first().map(|item| item.text.as_str()),
            Some("丁戊")
        );
        assert!(result.early.ignore_neural_constraint);
        assert_eq!(result.early.proposal, "甲");
    }

    #[test]
    fn early_evidence_conditions_on_committed_prefix() {
        let lex = lexicon("甲乙\tab\n丁戊\tab\n");
        let options = DecoderOptions {
            beam_width: 100,
            ..DecoderOptions::default()
        };
        let unconditioned = decode_full(
            "ab",
            &lex,
            None,
            &CharacterRanks::default(),
            &SupplementMatcher::empty(),
            20,
            options,
            true,
            "",
        );
        assert_eq!(unconditioned.early.proposal, "");
        let conditioned = decode_full(
            "ab",
            &lex,
            None,
            &CharacterRanks::default(),
            &SupplementMatcher::empty(),
            20,
            options,
            true,
            "甲",
        );
        assert_eq!(conditioned.early.proposal, "甲");
        assert_eq!(
            conditioned.candidates.first().map(|item| item.text.as_str()),
            Some("乙")
        );
    }

    #[test]
    fn truncated_beam_marks_early_evidence() {
        let mut lines = String::new();
        let raw = "abcdef";
        let mut text = 0x4E00u32;
        for start in 0..raw.len() - 1 {
            for length in 2..=raw.len() - start {
                let code = &raw[start..start + length];
                lines.push_str(&format!("{}\t{}\n", char::from_u32(text).unwrap(), code));
                text += 1;
            }
        }
        let lex = lexicon(&lines);
        let result = decode_full(
            raw,
            &lex,
            None,
            &CharacterRanks::default(),
            &SupplementMatcher::empty(),
            20,
            DecoderOptions {
                beam_width: 1,
                ..DecoderOptions::default()
            },
            true,
            "",
        );
        assert!(result.early.confidence_truncated);
    }

    #[test]
    fn exact_visible_top_k_keeps_lexicon_order() {
        let mut entries = Vec::new();
        let mut expected = Vec::new();
        for index in 0..30 {
            let text = char::from_u32(0x4E00 + index).unwrap().to_string();
            entries.push(("ab", text.clone()));
            if expected.len() < 20 {
                expected.push(text);
            }
        }
        let owned: Vec<(String, String)> = entries
            .into_iter()
            .map(|(code, text)| (code.to_owned(), text))
            .collect();
        let pairs: Vec<(&str, &str)> = owned.iter().map(|(c, t)| (c.as_str(), t.as_str())).collect();
        let lex = Lexicon::from_entries(&pairs);
        let decoded = decode(
            "ab",
            &lex,
            None,
            20,
            DecoderOptions {
                beam_width: 100,
                ..DecoderOptions::default()
            },
        );
        assert_eq!(
            decoded.into_iter().map(|item| item.text).collect::<Vec<_>>(),
            expected
        );
    }

    #[test]
    fn incremental_matches_full_rebuild() {
        let lex = Lexicon::from_entries(&[
            ("ot", "是"),
            ("ue", "的"),
            ("tu", "我"),
            ("j", "人"),
            ("j", "什么"),
            ("j", "怎样"),
            ("jq", "件"),
            ("fi", "一"),
            ("fi", "一般"),
        ]);
        let ranks = CharacterRanks::default();
        let supplements = SupplementMatcher::empty();
        let options = DecoderOptions {
            beam_width: 20,
            ..DecoderOptions::default()
        };
        let samples = [
            "ot", "ueot", "ueottu", "otj2", "ueot;", "j'", "fiot", "fiota", "jqtusotu",
        ];
        for sample in samples {
            let mut decoder = SentenceDecoder::new(options);
            let mut grown = DecodeResult::default();
            for length in 1..=sample.len() {
                grown = decoder.decode(
                    &sample[..length],
                    &lex,
                    None,
                    &ranks,
                    &supplements,
                    20,
                    true,
                    "",
                );
            }
            let full = SentenceDecoder::new(options).decode_full(
                sample,
                &lex,
                None,
                &ranks,
                &supplements,
                20,
                true,
                "",
            );
            assert_same_result(&grown, &full);
            let same = decoder.decode(sample, &lex, None, &ranks, &supplements, 20, true, "");
            assert_same_result(&same, &full);
        }

        let mut decoder = SentenceDecoder::new(options);
        decoder.decode("jqtusotu", &lex, None, &ranks, &supplements, 20, true, "");
        for length in (1..8).rev() {
            let prefix = &"jqtusotu"[..length];
            let grown = decoder.decode(prefix, &lex, None, &ranks, &supplements, 20, true, "");
            let full = SentenceDecoder::new(options).decode_full(
                prefix, &lex, None, &ranks, &supplements, 20, true, "",
            );
            assert_same_result(&grown, &full);
        }

        let mut decoder = SentenceDecoder::new(options);
        decoder.decode("u", &lex, None, &ranks, &supplements, 20, true, "");
        let two = decoder.decode("ue", &lex, None, &ranks, &supplements, 20, true, "");
        assert_same_result(
            &two,
            &SentenceDecoder::new(options).decode_full(
                "ue", &lex, None, &ranks, &supplements, 20, true, "",
            ),
        );
        let one = decoder.decode("u", &lex, None, &ranks, &supplements, 20, true, "");
        assert_same_result(
            &one,
            &SentenceDecoder::new(options).decode_full(
                "u", &lex, None, &ranks, &supplements, 20, true, "",
            ),
        );

        let mut decoder = SentenceDecoder::new(options);
        for length in 1..=4 {
            decoder.decode(&"ueot"[..length], &lex, None, &ranks, &supplements, 20, true, "");
        }
        let with_selector = decoder.decode("ueot;", &lex, None, &ranks, &supplements, 20, true, "");
        assert_same_result(
            &with_selector,
            &SentenceDecoder::new(options).decode_full(
                "ueot;", &lex, None, &ranks, &supplements, 20, true, "",
            ),
        );
        let back = decoder.decode("ueot", &lex, None, &ranks, &supplements, 20, true, "");
        assert_same_result(
            &back,
            &SentenceDecoder::new(options).decode_full(
                "ueot", &lex, None, &ranks, &supplements, 20, true, "",
            ),
        );
        let retype = decoder.decode("ueottu", &lex, None, &ranks, &supplements, 20, true, "");
        assert_same_result(
            &retype,
            &SentenceDecoder::new(options).decode_full(
                "ueottu", &lex, None, &ranks, &supplements, 20, true, "",
            ),
        );
    }

    #[test]
    fn incomplete_tail_incremental_matches_full() {
        let lex = lexicon("甲乙\tab\n丁戊\tabc\n丙\tcd\n");
        let prefer = PreferModel::new(&["甲", "乙", "丙", "辛"]);
        let options = DecoderOptions {
            beam_width: 100,
            ..DecoderOptions::default()
        };
        let ranks = CharacterRanks::default();
        let supplements = SupplementMatcher::empty();
        let mut decoder = SentenceDecoder::new(options);
        for prefix in ["a", "ab", "abc"] {
            decoder.decode_prefer_ex(
                prefix,
                &lex,
                &prefer,
                &ranks,
                &supplements,
                20,
                true,
                "",
                true,
            );
        }
        let grown = decoder.decode_prefer_ex(
            "abc", &lex, &prefer, &ranks, &supplements, 20, true, "", true,
        );
        let full = decoder.decode_prefer(
            "abc", &lex, &prefer, &ranks, &supplements, 20, true, "",
        );
        assert_same_result(&grown, &full);
        assert_eq!(grown.candidates.first().map(|item| item.text.as_str()), Some("丁戊"));
        assert_eq!(grown.early.proposal, "甲");
    }
}
