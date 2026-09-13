#pragma once
#include "SentenceEarlyEvidence.h"
#include <optional>

namespace tiger::core
{
    struct SentenceCommitContext
    {
        bool enabled = false, suspended = false, lexiconCurrent = true;
        std::u16string_view fullRaw, committedText;
        std::size_t committedRaw = 0, lastCommitRaw = 0, minimumRetained = 3;
        std::optional<std::u16string_view> acceptedNeuralTop; // only if accepted for this exact evidence raw
    };
    struct SentenceCommitProposal
    {
        std::u16string text;
        std::size_t rawLength;
        double share;
    };
    // Caller serializes Observe/reset with composition changes. A returned full
    // prefix is a decision, not an OS commit; caller consumes only its new suffix.
    class SentenceCommitTracker
    {
        struct Tracker { std::u16string text; std::size_t raw; int count = 0, strong = 0, gaps = 0; double share = 0; };
        using Key = std::pair<std::u16string, std::size_t>;
        std::map<Key, Tracker> _trackers;
        std::u16string _lastRaw;
    public:
        void Reset() { _trackers.clear(); _lastRaw.clear(); }
        std::optional<SentenceCommitProposal> Observe(const SentenceLatticeResult& lattice,
            const SentenceEarlyEvidence& evidence, const SentenceCommitContext& context)
        {
            auto raw = std::u16string_view(lattice.raw);
            bool current = context.fullRaw == raw;
            bool preceding = context.fullRaw.size() == raw.size() + 1 && context.fullRaw.starts_with(raw);
            if (!context.enabled || context.suspended || !context.lexiconCurrent || (!current && !preceding) ||
                raw.size() <= 4 || evidence.confidenceTruncated) { Reset(); return {}; }
            if (_lastRaw != raw)
            {
                if (!_lastRaw.empty() && (raw.size() != _lastRaw.size() + 1 || !raw.starts_with(_lastRaw))) _trackers.clear();
                _lastRaw = raw;
                auto accepted = context.acceptedNeuralTop;
                if (!lattice.candidates.empty() && lattice.candidates[0].supplementScore > 0)
                    accepted = lattice.candidates[0].text;
                std::map<Key, SentencePrefixEvidence> qualifying;
                for (const auto& prefix : evidence.prefixes)
                {
                    if (prefix.text.empty() || !prefix.boundaryClosed || prefix.share < 0.995 || prefix.rawLength <= context.committedRaw ||
                        prefix.text.size() <= context.committedText.size() || !prefix.text.starts_with(context.committedText) ||
                        (accepted && !accepted->starts_with(prefix.text))) continue;
                    bool visible = evidence.mergedIncompleteTail;
                    if (!visible)
                        for (const auto& candidate : lattice.candidates)
                        {
                            if (!candidate.text.starts_with(prefix.text)) continue;
                            for (auto boundary = candidate.boundary; boundary; boundary = boundary->previous)
                                if (boundary->rawLength == prefix.rawLength && boundary->textLength == prefix.text.size()) visible = true;
                        }
                    if (visible) qualifying[{prefix.text, prefix.rawLength}] = prefix;
                }
                std::map<Key, Tracker> next;
                if (qualifying.empty() && (evidence.neutralLowConfidence || evidence.mergedIncompleteTail))
                {
                    for (auto [key, tracker] : _trackers)
                    {
                        const SentencePrefixEvidence* self = nullptr;
                        for (const auto& prefix : evidence.prefixes)
                            if (prefix.text == tracker.text && prefix.rawLength == tracker.raw) { self = &prefix; break; }
                        if (!self) continue;
                        bool contradicted = false;
                        for (const auto& prefix : evidence.prefixes)
                        {
                            if (prefix.text.empty() || prefix.text.starts_with(tracker.text) || tracker.text.starts_with(prefix.text)) continue;
                            auto common = std::mismatch(prefix.text.begin(), prefix.text.end(), tracker.text.begin(), tracker.text.end());
                            if (common.first != prefix.text.begin() && prefix.share > self->share) contradicted = true;
                        }
                        if (contradicted || ++tracker.gaps > 3) continue;
                        tracker.share = self->share;
                        next.emplace(key, std::move(tracker));
                    }
                }
                else
                {
                    for (const auto& [key, prefix] : qualifying)
                    {
                        auto found = _trackers.find(key);
                        Tracker tracker = found == _trackers.end() ? Tracker{prefix.text, prefix.rawLength} : found->second;
                        tracker.count = std::min(3, tracker.count + 1);
                        tracker.strong = prefix.share >= 0.99999 ? std::min(2, tracker.strong + 1) : 0;
                        tracker.gaps = 0; tracker.share = prefix.share;
                        next.emplace(key, std::move(tracker));
                    }
                }
                _trackers = std::move(next);
            }
            const Tracker* best = nullptr;
            std::size_t bestLength = 0;
            for (const auto& [key, tracker] : _trackers)
            {
                if ((tracker.count < 3 && tracker.strong < 2) || tracker.raw <= context.committedRaw || tracker.raw > raw.size() ||
                    raw.size() - tracker.raw < std::max(std::size_t{3}, context.minimumRetained) ||
                    tracker.text.size() <= context.committedText.size() || !tracker.text.starts_with(context.committedText)) continue;
                auto length = TextElementStarts(tracker.text).size();
                if (!best || length > bestLength || (length == bestLength && (tracker.share > best->share ||
                    (tracker.share == best->share && tracker.raw < best->raw)))) { best = &tracker; bestLength = length; }
            }
            if (!best || context.lastCommitRaw > raw.size() || raw.size() - context.lastCommitRaw < 3) return {};
            SentenceCommitProposal proposal{best->text, best->raw, best->share};
            Reset(); // host applies proposal and updates committed context atomically
            return proposal;
        }
    };
}
