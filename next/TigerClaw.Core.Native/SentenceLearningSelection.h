#pragma once
#include "SentenceBeam.h"
#include <optional>
#include <span>

namespace tiger::core
{
    // Composition-local corrections, released only with the exact returned
    // output. Persistence still requires a separate successful client receipt.
    class SentenceLearningSelection
    {
        std::shared_ptr<const SentenceLearningSnapshot> _snapshot;
        std::u16string _mode, _output;
        std::optional<SentenceBeamState> _baseline;
        std::vector<SentenceLearningEvent> _pending, _ready;
        static std::vector<SentenceLearningBoundary> Boundaries(const SentenceBeamState& candidate)
        {
            std::vector<SentenceLearningBoundary> result;
            for (auto p = candidate.boundary; p; p = p->previous)
                result.push_back({static_cast<int>(p->rawLength), static_cast<int>(p->textLength)});
            std::reverse(result.begin(), result.end()); return result;
        }
    public:
        void Configure(std::shared_ptr<const SentenceLearningSnapshot> snapshot, std::u16string mode)
        {
            if (_mode != mode) Clear();
            _snapshot = std::move(snapshot); _mode = std::move(mode);
        }
        void Clear() { _baseline.reset(); _pending.clear(); _ready.clear(); _output.clear(); }
        void Begin(const SentenceBeamState& first) { if (!_baseline && !_mode.empty()) _baseline = first; }
        void Edited(std::size_t end)
        {
            _baseline.reset(); _ready.clear(); _output.clear();
            std::erase_if(_pending, [&](const auto& e) { return e.rawEnd > static_cast<int>(end); });
        }
        void Capture(std::u16string_view raw, std::span<const SentenceBeamState> candidates, std::size_t index,
            std::size_t floor, const std::function<bool(std::u16string_view)>& supplemental = {})
        {
            if (_mode.empty() || index >= candidates.size()) { _baseline.reset(); return; }
            const auto& selected = candidates[index];
            const auto* before = _baseline ? &*_baseline : index > 0 ? &candidates.front() : nullptr;
            // Explicit non-first taps need no preceding Tab. Keep the first
            // manual baseline unless a Direct-only comparison needs an actual
            // composed opponent ahead of the selected Direct entry.
            if (before && (before->source & 1) && (selected.source & 1))
            {
                before = nullptr;
                for (std::size_t i = 0; i < index; ++i)
                    if (candidates[i].source == 2 && (!before || SentenceFinalScore(candidates[i]) > SentenceFinalScore(*before)))
                        before = &candidates[i];
            }
            auto legalSource = [](unsigned source) { return source == 1 || source == 2 || source == 3; };
            if (before && legalSource(before->source) && legalSource(selected.source))
            {
                auto a = Boundaries(*before), b = Boundaries(selected);
                auto events = sentenceLearningSelectionEvents(raw, before->text, selected.text, a, b,
                    static_cast<int>(floor), _mode, _snapshot, supplemental);
                sentenceLearningPlanLevels(events, _snapshot, raw, before->text, selected.text, a, b,
                    SentenceFinalScore(*before) - before->learningScore, SentenceFinalScore(selected) - selected.learningScore);
                _pending.insert(_pending.end(), events.begin(), events.end());
            }
            _baseline.reset();
        }
        void Release(std::u16string_view text, std::size_t rawEnd, std::u16string_view output)
        {
            for (const auto& e : _pending)
                if (e.mode == _mode && !learningLegacyPairMode(e.mode) && e.rawEnd <= static_cast<int>(rawEnd) &&
                    e.textStart >= 0 && e.textEnd >= e.textStart && e.textEnd <= static_cast<int>(text.size()) &&
                    text.substr(e.textStart, e.textEnd - e.textStart) == e.text) _ready.push_back(e);
            std::erase_if(_pending, [&](const auto& e) { return e.rawEnd <= static_cast<int>(rawEnd); });
            if (!_ready.empty()) _output = output;
        }
        std::vector<SentenceLearningEvent> Take(std::u16string_view output)
        {
            std::vector<SentenceLearningEvent> result;
            if (!output.empty() && output == _output) result.swap(_ready);
            _ready.clear(); _output.clear(); filterSentenceLearningForMode(result, _mode); return result;
        }
    };
}
