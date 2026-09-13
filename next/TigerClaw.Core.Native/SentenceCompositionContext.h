#pragma once
#include "SentenceCommitTracker.h"

namespace tiger::core
{
    // Serialized host-owned composition storage. Raw casing is retained here;
    // normalization belongs to the decoder, never to literal output.
    class SentenceCompositionContext
    {
        std::u16string _raw, _committed;
        std::size_t _committedRaw = 0, _lastCommitRaw = 0;
    public:
        std::u16string_view Raw() const { return _raw; }
        std::u16string_view CommittedText() const { return _committed; }
        std::u16string_view UncommittedRaw() const { return std::u16string_view(_raw).substr(_committedRaw); }
        void Clear() { _raw.clear(); _committed.clear(); _committedRaw = _lastCommitRaw = 0; }
        void Append(char16_t key) { _raw.push_back(key); }
        // The physical backspace is consumed by the composition, even when it
        // removes its final live key; it must not erase already committed text.
        void Backspace()
        {
            if (_raw.size() <= _committedRaw + 1) Clear();
            else _raw.pop_back();
        }
        SentenceCommitContext CommitContext(bool enabled, bool suspended = false,
            bool lexiconCurrent = true, std::size_t minimumRetained = 3) const
        {
            SentenceCommitContext context;
            context.enabled = enabled; context.suspended = suspended; context.lexiconCurrent = lexiconCurrent;
            context.fullRaw = _raw; context.committedText = _committed;
            context.committedRaw = _committedRaw; context.lastCommitRaw = _lastCommitRaw;
            context.minimumRetained = minimumRetained;
            return context;
        }
        // Generation validation and evidence eligibility belong to the caller.
        // All allocations precede mutation so a failed allocation cannot consume
        // raw code without returning the corresponding commit text.
        std::u16string ApplyPrefix(const SentenceCommitProposal& proposal)
        {
            if (proposal.rawLength <= _committedRaw || proposal.rawLength > _raw.size() ||
                proposal.text.size() <= _committed.size() || !proposal.text.starts_with(_committed))
                throw std::invalid_argument("Sentence commit does not extend the active prefix");
            std::u16string suffix = proposal.text.substr(_committed.size());
            std::u16string committed = proposal.text;
            _committed.swap(committed);
            _committedRaw = _lastCommitRaw = proposal.rawLength;
            return suffix;
        }
        bool AcceptsCandidate(std::u16string_view text) const { return text.starts_with(_committed); }
        std::u16string CandidateSuffix(std::u16string_view text) const
        {
            return AcceptsCandidate(text) ? std::u16string(text.substr(_committed.size())) : std::u16string(UncommittedRaw());
        }
        std::u16string FinishLiteral()
        {
            std::u16string result(UncommittedRaw());
            Clear();
            return result;
        }
        // A missing or incompatible candidate falls back to the uncommitted raw
        // suffix, with no attempt to strip a text prefix from that literal code.
        std::u16string Finish(std::optional<std::u16string_view> candidate, std::u16string_view punctuation = {})
        {
            std::u16string result = candidate ? CandidateSuffix(*candidate) : std::u16string(UncommittedRaw());
            result.append(punctuation);
            Clear();
            return result;
        }
    };
}
