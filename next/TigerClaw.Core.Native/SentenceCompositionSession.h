#pragma once
#include "SentenceCompositionContext.h"
#include "SentenceEmptyCodeTracker.h"
#include "SentenceNeuralRanking.h"
#include <limits>

namespace tiger::core
{
    struct SentenceCompositionRequest
    {
        std::uint64_t generation, lexiconVersion;
        std::u16string raw, requiredPrefix;
    };
    struct SentenceControlResult
    {
        bool handled = false, needsDecode = false;
        std::optional<std::u16string> commit;
    };
    struct SentenceNeuralRequest
    {
        SentenceCompositionRequest composition;
        std::vector<std::u16string> candidates;
    };
    // Host calls are serialized. Worker results carry the request unchanged;
    // this identity is distinct from the worker's scheduling/coalescing token.
    class SentenceCompositionSession
    {
        SentenceCompositionContext _context;
        SentenceCommitTracker _tracker;
        SentenceEmptyCodeTracker _emptyTracker;
        SentenceLatticeResult _visible;
        SentenceEarlyEvidence _evidence;
        std::uint64_t _generation = 1, _lexiconVersion = 0, _applied = 0;
        std::size_t _selected = 0;
        bool _manual = false;
        bool _continuation = false;
        std::uint64_t _neuralApplied = 0;
        std::u16string _neuralRaw, _neuralTop;
        void CheckGeneration() const
        {
            if (_generation == std::numeric_limits<std::uint64_t>::max())
                throw std::overflow_error("Sentence composition generation exhausted");
        }
    public:
        const SentenceCompositionContext& Context() const { return _context; }
        const std::vector<SentenceBeamState>& Candidates() const { return _visible.candidates; }
        std::size_t SelectedIndex() const { return _selected; }
        bool DecodeCurrent() const { return _applied == _generation && _visible.raw == _context.Raw(); }
        std::u16string DisplayCode() const
        {
            auto raw = _context.Raw();
            auto fallback = [&] { return std::u16string(_context.UncommittedRaw()); };
            if (_visible.candidates.empty()) return fallback();
            auto index = _selected < _visible.candidates.size() ? _selected : 0;
            auto segmented = SegmentedSentenceCode(_visible.raw, _visible.candidates[index].boundary);
            if (segmented.empty()) return fallback();
            std::size_t rawIndex = 0;
            for (auto& mark : segmented)
                if (mark != u' ' && rawIndex < raw.size()) mark = raw[rawIndex++];
            if (raw == _visible.raw) {}
            else if (raw.starts_with(_visible.raw)) segmented.append(raw.substr(_visible.raw.size()));
            else if (std::u16string_view(_visible.raw).starts_with(raw))
            {
                std::size_t kept = 0, count = 0;
                while (kept < segmented.size() && count < raw.size())
                {
                    auto mark = segmented[kept++];
                    if (mark == u' ') continue;
                    if (mark != raw[count++]) return fallback();
                }
                if (count != raw.size()) return fallback();
                segmented.resize(kept);
            }
            else return fallback();
            auto committedRaw = raw.size() - _context.UncommittedRaw().size();
            if (committedRaw)
            {
                std::size_t offset = 0, count = 0;
                while (offset < segmented.size() && count < committedRaw)
                    if (segmented[offset++] != u' ') ++count;
                while (offset < segmented.size() && segmented[offset] == u' ') ++offset;
                segmented.erase(0, offset);
                if (segmented.empty() && !_context.UncommittedRaw().empty()) return fallback();
            }
            return segmented;
        }
        SentenceCompositionRequest Request() const
        {
            return {_generation, _lexiconVersion, std::u16string(_context.Raw()), std::u16string(_context.CommittedText())};
        }
        std::optional<SentenceNeuralRequest> NeuralRequest() const
        {
            if (!DecodeCurrent() || _manual || _visible.candidates.empty() || _neuralApplied == _generation) return {};
            SentenceNeuralRequest request{Request(), {}};
            for (std::size_t i = 0; i < std::min(std::size_t{5}, _visible.candidates.size()); ++i)
                request.candidates.push_back(_visible.candidates[i].text);
            return request;
        }
        bool ApplyNeural(const SentenceNeuralRequest& request, std::span<const double> scores, bool duplicateSingles)
        {
            auto current = NeuralRequest();
            if (!current || request.composition.generation != _generation || request.composition.lexiconVersion != _lexiconVersion ||
                request.composition.raw != _context.Raw() || request.composition.requiredPrefix != _context.CommittedText() ||
                request.candidates != current->candidates || scores.size() != request.candidates.size() ||
                std::any_of(scores.begin(), scores.end(), [](double score) { return !std::isfinite(score); })) return false;
            auto ranks = RankSentenceNeural(_visible.candidates, scores, duplicateSingles);
            auto reordered = _visible.candidates;
            for (std::size_t i = 0; i < ranks.size(); ++i) reordered[i] = _visible.candidates[ranks[i].index];
            std::u16string raw(_context.Raw()), top(reordered[0].text);
            _visible.candidates.swap(reordered); _neuralRaw.swap(raw); _neuralTop.swap(top);
            _neuralApplied = _generation; _selected = 0;
            return true;
        }
        void Append(char16_t key)
        {
            CheckGeneration(); _context.Append(key); ++_generation; _manual = false; _emptyTracker.Reset();
            // Keep previous visible candidates while the new decode is pending.
        }
        template<class Complete, class ProperPrefix>
        std::optional<std::u16string> AppendWithAutoCommit(char16_t key, bool enabled,
            std::size_t minimumRetained, Complete&& complete, ProperPrefix&& properPrefix)
        {
            if (_context.UncommittedRaw().size() >= 128) return {};
            CheckGeneration();
            auto nextContext = _context;
            nextContext.Append(key);
            auto nextEmpty = _emptyTracker;
            auto proposal = nextEmpty.Append(_visible, _evidence,
                _context.CommitContext(enabled, _manual), nextContext.Raw(), minimumRetained,
                std::forward<Complete>(complete), std::forward<ProperPrefix>(properPrefix));
            if (proposal)
            {
                std::vector<SentenceBeamState> filtered;
                for (const auto& candidate : _visible.candidates)
                    if (candidate.text.starts_with(proposal->text)) filtered.push_back(candidate);
                auto output = nextContext.ApplyPrefix(*proposal);
                _context = std::move(nextContext); _emptyTracker = std::move(nextEmpty);
                _visible.candidates.swap(filtered); _selected = 0; ++_generation;
                _continuation = true; _manual = false; _tracker.Reset();
                _neuralRaw.clear(); _neuralTop.clear();
                return output;
            }
            _context = std::move(nextContext); _emptyTracker = std::move(nextEmpty); ++_generation;
            auto output = TryAutoCommit(enabled, minimumRetained);
            _manual = false;
            return output;
        }
        void Backspace()
        {
            CheckGeneration(); _context.Backspace(); ++_generation; _manual = false; _tracker.Reset(); _emptyTracker.Reset();
            if (_context.Raw().empty())
            {
                _visible = {}; _evidence = {}; _selected = 0; _continuation = false;
                _neuralRaw.clear(); _neuralTop.clear();
            }
        }
        void InvalidateLexicon(std::uint64_t version)
        {
            CheckGeneration(); _lexiconVersion = version; ++_generation;
            _tracker.Reset(); _visible = {}; _evidence = {}; _selected = 0; _manual = false;
            _emptyTracker.Reset(); _neuralRaw.clear(); _neuralTop.clear();
        }
        bool Apply(const SentenceCompositionRequest& request, const SentenceLatticeResult& lattice,
            const SentenceEarlyEvidence& evidence)
        {
            if (request.generation != _generation || request.lexiconVersion != _lexiconVersion ||
                request.raw != _context.Raw() || request.requiredPrefix != _context.CommittedText() ||
                _applied == _generation || lattice.raw != NormalizeSentenceRaw(request.raw)) return false;
            SentenceLatticeResult visible;
            visible.raw = request.raw; // restore original casing for engine identity/evidence
            visible.expanded = lattice.expanded; visible.confidenceTruncated = lattice.confidenceTruncated;
            bool firstOnly = _continuation && !std::any_of(request.raw.begin(), request.raw.end(),
                [](char16_t c) { return SentenceDigit(c) || c == u';' || c == u'\''; });
            for (const auto& candidate : lattice.candidates)
                if (_context.AcceptsCandidate(candidate.text) && (!firstOnly || candidate.maxRank <= 1)) visible.candidates.push_back(candidate);
            SentenceEarlyEvidence copiedEvidence = evidence;
            _visible = std::move(visible); _evidence = std::move(copiedEvidence);
            _applied = _generation; _selected = 0;
            return true;
        }
        bool Select(std::size_t index)
        {
            if (index >= _visible.candidates.size()) return false;
            _selected = index; _manual = true; _tracker.Reset(); _emptyTracker.Reset(); return true;
        }
        void Cancel()
        {
            CheckGeneration(); _context.Clear(); ++_generation;
            _tracker.Reset(); _visible = {}; _evidence = {}; _selected = 0; _manual = false;
            _emptyTracker.Reset(); _continuation = false; _neuralRaw.clear(); _neuralTop.clear();
        }
        // Schema migration imports raw code, not simulated physical key events:
        // no early commit, selector execution or intermediate decode occurs.
        // Build before mutation; raw may alias the current uncommitted suffix.
        void ReplaceRaw(std::u16string_view raw, std::uint64_t lexiconVersion)
        {
            CheckGeneration();
            SentenceCompositionContext next;
            for (char16_t key : raw) next.Append(key);
            Cancel();
            _context = std::move(next); _lexiconVersion = lexiconVersion;
        }
        // Inner sentence-mode dispatch only: physical modifiers, CN/EN toggles,
        // global shortcuts and output postprocessing belong to the outer host.
        SentenceControlResult ControlKey(int vk, bool shift, int pageSize, bool enterClear, bool tabClear)
        {
            if (vk == 0x08) { Backspace(); return {true, false, {}}; }
            if (vk == 0x1b) { Cancel(); return {true, false, {}}; }
            if (vk == 0x0d)
            {
                if (enterClear) { Cancel(); return {true, false, {}}; }
                return {true, false, FinishLiteral()};
            }
            if (vk != 0x09 && vk != 0x20 && vk != 0x26 && vk != 0x28) return {};
            _emptyTracker.Reset();
            // The host obtains the current decode and retries this key without
            // replaying outer-host effects. Only empty-code pending state was
            // canceled above, matching the reference control-key ordering.
            if (!DecodeCurrent()) return {true, true, {}};
            if (vk == 0x20)
            {
                if (_visible.candidates.empty()) return {true, false, {}};
                return {true, false, FinishSelected()};
            }
            if (vk == 0x09 && _visible.candidates.empty())
            {
                if (tabClear) Cancel();
                return {true, false, {}};
            }
            _manual = true; _tracker.Reset();
            auto count = std::min(_visible.candidates.size(), static_cast<std::size_t>(std::clamp(pageSize, 1, 10)));
            if (count)
            {
                bool backwards = vk == 0x26 || (vk == 0x09 && shift);
                _selected = backwards ? (_selected + count - 1) % count : (_selected + 1) % count;
            }
            return {true, false, {}};
        }
        std::optional<std::u16string> TryAutoCommit(bool enabled, std::size_t minimumRetained = 3)
        {
            CheckGeneration();
            auto context = _context.CommitContext(enabled, _manual, true, minimumRetained);
            if (_neuralRaw == _visible.raw && !_neuralRaw.empty()) context.acceptedNeuralTop = _neuralTop;
            auto proposal = _tracker.Observe(_visible, _evidence, context);
            if (!proposal) return {};
            std::vector<SentenceBeamState> filtered;
            for (const auto& candidate : _visible.candidates)
                if (candidate.text.starts_with(proposal->text)) filtered.push_back(candidate);
            auto output = _context.ApplyPrefix(*proposal);
            _visible.candidates.swap(filtered); _selected = 0; ++_generation;
            _emptyTracker.Reset(); _continuation = false;
            return output;
        }
        std::u16string FinishLiteral()
        {
            CheckGeneration(); auto output = _context.FinishLiteral();
            _neuralRaw.clear(); _neuralTop.clear();
            ++_generation; _tracker.Reset(); _visible = {}; _evidence = {}; _selected = 0; _manual = false;
            _emptyTracker.Reset(); _continuation = false;
            return output;
        }
        // nullopt means the host must first obtain/apply the current decode.
        // Never commit an old visible candidate while a newer raw suffix waits.
        std::optional<std::u16string> FinishSelected(std::u16string_view punctuation = {})
        {
            if (!DecodeCurrent()) return {};
            CheckGeneration();
            std::optional<std::u16string_view> candidate;
            if (_selected < _visible.candidates.size()) candidate = _visible.candidates[_selected].text;
            auto output = _context.Finish(candidate, punctuation);
            _neuralRaw.clear(); _neuralTop.clear();
            ++_generation; _tracker.Reset(); _visible = {}; _evidence = {}; _selected = 0; _manual = false;
            _emptyTracker.Reset(); _continuation = false;
            return output;
        }
    };
}
