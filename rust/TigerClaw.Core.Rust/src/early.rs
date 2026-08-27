use crate::decoder::DecodeResult;
use crate::lexicon::text_elements;

#[derive(Debug, Clone)]
struct Evidence {
    raw: String,
    proposal: String,
    raw_lengths: Vec<(String, usize)>,
}

#[derive(Debug, Default, Clone)]
pub struct EarlyCommitRuntime {
    evidence: Vec<Evidence>,
    pub committed_text: String,
    pub committed_raw_length: usize,
    last_early_commit_raw_length: usize,
    pub suspended: bool,
}

impl EarlyCommitRuntime {
    pub fn reset(&mut self) {
        *self = Self::default();
    }

    pub fn reset_evidence(&mut self) {
        self.evidence.clear();
    }

    pub fn suspend(&mut self) {
        self.suspended = true;
        self.evidence.clear();
    }

    pub fn on_backspace(&mut self) {
        self.reset_evidence();
    }

    pub fn try_commit(
        &mut self,
        result: &DecodeResult,
        enabled: bool,
        full_raw: &str,
        neural_top_text: Option<&str>,
    ) -> String {
        if !enabled || self.suspended {
            self.reset_evidence();
            return String::new();
        }
        let evidence_raw = if result.raw.is_empty() {
            return String::new();
        } else {
            result.raw.clone()
        };
        let current_generation = evidence_raw == full_raw;
        let previous_generation = evidence_raw.len() + 1 == full_raw.len()
            && full_raw.starts_with(&evidence_raw);
        if !current_generation && !previous_generation {
            self.reset_evidence();
            return String::new();
        }
        if evidence_raw.len() <= 4
            || result.early.confidence_truncated
            || result.early.proposal.is_empty()
        {
            self.reset_evidence();
            return String::new();
        }
        let mut proposal = result.early.proposal.clone();
        if let Some(top) = result.candidates.first() {
            if top.supplement_score > 0.0 {
                while text_len(&proposal) > text_len(&self.committed_text)
                    && !top.text.starts_with(&proposal)
                    && !(self.committed_text.clone() + &top.text).starts_with(&proposal)
                {
                    proposal = pop_char(&proposal);
                }
            }
        }
        if text_len(&proposal) <= text_len(&self.committed_text)
            || !proposal.starts_with(&self.committed_text)
        {
            self.reset_evidence();
            return String::new();
        }
        // A completed Qwen response is accepted only for the exact decoded
        // generation.  When it is available, C# constrains the confidence
        // proposal to the neural top text; incomplete-tail evidence is marked
        // as exempt by the caller and therefore passes `None` here.
        if let Some(top_text) = neural_top_text {
            while text_len(&proposal) > text_len(&self.committed_text)
                && !top_text.starts_with(&proposal)
                && !(self.committed_text.clone() + top_text).starts_with(&proposal)
            {
                proposal = pop_char(&proposal);
            }
            if text_len(&proposal) <= text_len(&self.committed_text)
                || !proposal.starts_with(&self.committed_text)
            {
                self.reset_evidence();
                return String::new();
            }
        }
        let extends = self.evidence.last().is_some_and(|last| {
            evidence_raw.len() == last.raw.len() + 1 && evidence_raw.starts_with(&last.raw)
        });
        if !extends {
            self.evidence.clear();
        }
        self.evidence.push(Evidence {
            raw: evidence_raw.clone(),
            proposal: proposal.clone(),
            raw_lengths: result.early.raw_lengths.clone(),
        });
        if self.evidence.len() > 3 {
            self.evidence.remove(0);
        }
        if self.evidence.len() < 3 {
            return String::new();
        }
        let proposals: Vec<String> = self.evidence.iter().map(|item| item.proposal.clone()).collect();
        let mut stable = longest_common_prefix(&proposals);
        let mut committed_raw = stable_raw_length(&self.evidence, &stable);
        while text_len(&stable) > text_len(&self.committed_text) && committed_raw == 0 {
            stable = pop_char(&stable);
            committed_raw = stable_raw_length(&self.evidence, &stable);
        }
        if committed_raw <= self.committed_raw_length || committed_raw > evidence_raw.len() {
            return String::new();
        }
        if text_len(&stable) <= text_len(&self.committed_text) {
            return String::new();
        }
        let committed_elements = text_elements(&self.committed_text).len();
        let stable_elements = text_elements(&stable);
        let commit = stable_elements[committed_elements..].concat();
        if text_len(&commit) < 1 {
            return String::new();
        }
        if evidence_raw.len().saturating_sub(self.last_early_commit_raw_length) < 3 {
            return String::new();
        }
        self.committed_text = stable;
        self.committed_raw_length = committed_raw;
        self.last_early_commit_raw_length = committed_raw;
        self.reset_evidence();
        commit
    }
}

fn text_len(text: &str) -> usize {
    text_elements(text).len()
}

fn pop_char(text: &str) -> String {
    let mut elements = text_elements(text);
    elements.pop();
    elements.concat()
}

fn longest_common_prefix(texts: &[String]) -> String {
    if texts.iter().any(|text| text.is_empty()) {
        return String::new();
    }
    let split: Vec<Vec<String>> = texts.iter().map(|text| text_elements(text)).collect();
    let shortest = split.iter().map(Vec::len).min().unwrap_or(0);
    for n in (1..=shortest).rev() {
        let prefix: String = split[0][..n].concat();
        if split.iter().all(|item| item[..n] == split[0][..n]) {
            return prefix;
        }
    }
    String::new()
}

fn stable_raw_length(evidence: &[Evidence], text: &str) -> usize {
    let mut value = 0usize;
    for entry in evidence {
        let Some(raw) = entry
            .raw_lengths
            .iter()
            .find(|(prefix, _)| prefix == text)
            .map(|(_, raw)| *raw)
        else {
            return 0;
        };
        if raw == 0 {
            return 0;
        }
        if value == 0 {
            value = raw;
        } else if value != raw {
            return 0;
        }
    }
    value
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn longest_common_prefix_keeps_shared_characters() {
        assert_eq!(
            longest_common_prefix(&["甲乙丙".to_owned(), "甲乙丁".to_owned(), "甲乙戊".to_owned()]),
            "甲乙"
        );
        assert_eq!(
            longest_common_prefix(&["甲".to_owned(), "甲".to_owned(), "甲乙戊".to_owned()]),
            "甲"
        );
    }
}
