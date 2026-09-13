#pragma once
#include "SentenceCommitTracker.h"

namespace tiger::core
{
    class SentenceEmptyCodeTracker
    {
        struct Pending
        {
            std::u16string text, committed, baseRaw;
            std::size_t lastSegmentStart;
            bool unique;
        };
        std::optional<Pending> _pending;
    public:
        void Reset() { _pending.reset(); }
        bool PendingCommit() const { return _pending.has_value(); }
        // context describes the composition BEFORE this one-key append. The
        // caller resets on navigation, backspace, schema/settings change/cancel.
        // complete(raw, required, excluded, groupOnly) is an exact lexicon query;
        // properPrefix(code) must not invoke Beam or a language model.
        template<class Complete, class ProperPrefix>
        std::optional<SentenceCommitProposal> Append(const SentenceLatticeResult& visible,
            const SentenceEarlyEvidence& evidence, const SentenceCommitContext& context,
            std::u16string_view appendedRaw, std::size_t minimumRetained,
            Complete&& complete, ProperPrefix&& properPrefix)
        {
            if (!context.enabled || context.suspended || !context.lexiconCurrent ||
                appendedRaw.size() != context.fullRaw.size() + 1 || !appendedRaw.starts_with(context.fullRaw) ||
                !((appendedRaw.back() >= u'a' && appendedRaw.back() <= u'z') ||
                  (appendedRaw.back() >= u'A' && appendedRaw.back() <= u'Z')))
            { Reset(); return {}; }
            const SentenceBeamState* captured = nullptr;
            bool unique = false;
            if (!_pending)
            {
                if (visible.raw != context.fullRaw || visible.candidates.empty()) return {};
                bool explicitRank = std::any_of(context.fullRaw.begin(), context.fullRaw.end(),
                    [](char16_t c) { return SentenceDigit(c) || c == u';' || c == u'\''; });
                std::vector<const SentenceBeamState*> eligible;
                for (const auto& candidate : visible.candidates)
                    if (explicitRank || candidate.maxRank <= 1) eligible.push_back(&candidate);
                if (eligible.empty()) return {};
                captured = eligible[0]; unique = eligible.size() == 1;
                if (captured->text.size() <= context.committedText.size() ||
                    !captured->text.starts_with(context.committedText)) return {};
                if (!unique)
                {
                    if (evidence.confidenceTruncated || evidence.prefixes.empty() ||
                        captured->text != visible.candidates[0].text) return {};
                    double maximum = eligible[0]->logMass, total = 0;
                    for (auto candidate : eligible) maximum = std::max(maximum, candidate->logMass);
                    for (auto candidate : eligible) total += std::exp(candidate->logMass - maximum);
                    if (!(total > 0) || std::exp(captured->logMass - maximum) / total < .99999) return {};
                }
            }
            if (complete(appendedRaw, context.committedText, std::optional<std::u16string_view>{}, false))
            { Reset(); return {}; }
            if (!_pending)
            {
                auto boundary = captured->boundary;
                _pending = Pending{captured->text, std::u16string(context.committedText),
                    std::u16string(context.fullRaw), boundary && boundary->previous ? boundary->previous->rawLength : 0, unique};
            }
            const auto& pending = *_pending;
            if (pending.committed != context.committedText || !appendedRaw.starts_with(pending.baseRaw) ||
                pending.baseRaw.size() >= appendedRaw.size() || pending.lastSegmentStart >= appendedRaw.size())
            { Reset(); return {}; }
            if (properPrefix(appendedRaw.substr(pending.lastSegmentStart)) ||
                appendedRaw.size() - pending.baseRaw.size() < minimumRetained) return {};
            if (pending.unique && complete(pending.baseRaw, pending.committed,
                std::optional<std::u16string_view>(pending.text), true)) { Reset(); return {}; }
            SentenceCommitProposal proposal{pending.text, pending.baseRaw.size(), 0};
            Reset();
            return proposal;
        }
    };
}
