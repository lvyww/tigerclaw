#pragma once
#include "SchemaLexicon.h"
#include "SentenceCharacterRanks.h"
#include "SentenceLattice.h"
#include "SentenceIsolation.h"
#include "SentenceNgramTransition.h"
#include "MappedSentenceNgram.h"
#include "SentenceFivegramModel.h"
#include "SentencePathQuery.h"
#include "SentenceEarlyEvidence.h"
#include "RuntimeSentenceSettings.h"
#include <mutex>
#include <fstream>

namespace tiger::core
{
    struct RuntimeSentenceResult
    {
        std::shared_ptr<const SentenceLatticeResult> lattice;
        SentenceEarlyEvidence evidence;
    };

    // Owns a complete, immutable decoding configuration plus its mutable model
    // caches. Construct a replacement off-thread when schema/settings change;
    // publication/generation management belongs to the future input host.
    class RuntimeSentenceDecoder
    {
    public:
        RuntimeSentenceDecoder(const SchemaLexicon& schema, const std::filesystem::path& modelPath,
            RuntimeSentenceSettings settings = {})
            : _settings(std::move(settings)),
              _lexicon(BuildLexicon(schema.table, _settings)),
              _supplements(SentenceSupplementMatcher::Build(schema.supplements
                  ? std::span<const SentenceSupplementEntry>(*schema.supplements) : std::span<const SentenceSupplementEntry>{}))
        {
            // Explicit old-format fixtures remain readable for differential
            // tests. A failed fivegram never falls back to another model/file.
            std::ifstream probe(modelPath, std::ios::binary);
            char magic[8]{}; probe.read(magic, sizeof magic); probe.close();
            if (std::string_view(magic, 8) == "TCSKNM01") _legacy = std::make_unique<MappedSentenceNgram>(modelPath);
            else
            {
                if (!_settings.scoreSentenceBoundaries) throw std::invalid_argument("Fivegram search requires BOS/EOS scoring");
                SentenceFivegramModel model(modelPath);
                _fivegram = model.CreateQuery(); // query lease retains the mapping
                _history = {_fivegram->BeginHistory(), [this](auto& state, auto target) { return _fivegram->Step(state, target); }};
            }
        }
        RuntimeSentenceDecoder(const RuntimeSentenceDecoder&) = delete;
        RuntimeSentenceDecoder& operator=(const RuntimeSentenceDecoder&) = delete;

        SentenceLatticeResult DecodeFull(std::u16string_view raw)
        {
            std::lock_guard lock(_decodeMutex);
            return DecodeLocked(raw, nullptr, true);
        }
        std::shared_ptr<const SentenceLatticeResult> Decode(std::u16string_view raw)
        {
            return DecodeResult(raw)->lattice;
        }
        std::shared_ptr<const RuntimeSentenceResult> DecodeResult(std::u16string_view raw,
            bool includeEvidence = false, std::u16string_view requiredPrefix = {}, std::shared_ptr<const SentenceLockedPrefix> lockedPrefix = {}, std::stop_token stop = {})
        {
            std::lock_guard lock(_decodeMutex);
            if (stop.stop_requested()) throw std::runtime_error("Sentence decode canceled");
            auto normalized = NormalizeSentenceRaw(raw);
            if (_cachedLocked != lockedPrefix) { _cached.reset(); _cachedResult.reset(); }
            _cachedLocked = std::move(lockedPrefix);
            if (_cachedResult && _cachedRaw == normalized && _cachedEvidence == includeEvidence && _cachedRequired == requiredPrefix)
                return _cachedResult;
            auto nextLattice = DecodeLocked(raw, _cached.get(), false, stop);
            if (_cached && _cachedRaw == normalized) nextLattice.expanded = 0;
            auto nextResult = std::make_shared<RuntimeSentenceResult>();
            if (includeEvidence)
                nextResult->evidence = BuildSentenceEarlyEvidence(_lexicon, nextLattice,
                    LegacyTransition(),
                    _settings.lattice,
                    [&](auto text) { return SentenceIsolationPenalty(text,
                        [](auto value) { return SentenceCharacterRanks::Default().GetRank(value); },
                        [&](auto a, auto b) { return ObservedBigram(a, b); }, _settings.isolation); }, requiredPrefix, _fivegram ? &_history : nullptr);
            nextResult->lattice = std::make_shared<const SentenceLatticeResult>(std::move(nextLattice));
            std::u16string ownedRequired(requiredPrefix);
            // Publish the candidate/evidence pair only after every allocation
            // and score operation succeeds. Previously returned pairs stay owned.
            if (stop.stop_requested()) throw std::runtime_error("Sentence decode canceled");
            _cachedRequired.swap(ownedRequired);
            _cachedRaw.swap(normalized);
            _cachedEvidence = includeEvidence;
            _cached = nextResult->lattice;
            _cachedResult = std::move(nextResult);
            return _cachedResult;
        }
        void SetLearning(std::shared_ptr<const SentenceLearningSnapshot> snapshot, std::u16string mode)
        {
            std::lock_guard lock(_decodeMutex);
            _learning = {std::move(snapshot), std::move(mode), false};
            _cached.reset(); _cachedResult.reset(); _cachedRaw.clear();
        }
        void ResetCache()
        {
            std::lock_guard lock(_decodeMutex);
            _cached.reset();
            _cachedResult.reset();
            _cachedRaw.clear();
            _cachedRequired.clear();
            _cachedEvidence = false;
        }
        bool HasCompleteCandidate(std::u16string_view raw, std::u16string_view required = {},
            std::optional<std::u16string_view> excluded = {}, bool groupEligibleOnly = false, const SentenceLockedPrefix* locked = nullptr) const
        {
            return HasCompleteSentenceCandidate(_lexicon, raw, _settings.lattice.duplicateSingles, required, excluded, groupEligibleOnly, locked);
        }
        std::size_t CompetingBoundaryEnd(std::u16string_view raw, std::size_t committed, std::size_t proposed, std::size_t elements) const
        { return CompetingSentenceBoundaryEnd(_lexicon, raw, committed, proposed, elements); }
        bool IsProperCodePrefix(std::u16string_view code) const { return _lexicon.IsProperPrefix(NormalizeSentenceRaw(code)); }
        bool IsSupplementalFragment(std::u16string_view text) const { return _supplements.Contains(text); }
        bool AllowsDuplicateSingles() const { return _settings.lattice.duplicateSingles; }
    private:
        SentenceTransition LegacyTransition()
        {
            return [this](auto a, auto b, auto c)
            {
                if (!_legacy) throw std::logic_error("Fivegram search requires complete history");
                return ScoreSentenceNgramTransition(_legacy->Model(), a, b, c, _settings.scoreSentenceBoundaries);
            };
        }
        bool ObservedBigram(std::u16string_view a, std::u16string_view b)
        { return _fivegram ? _fivegram->HasObservedBigram(a, b) : _legacy->Model().HasObservedBigram(a, b); }
        SentenceLatticeResult DecodeLocked(std::u16string_view raw, const SentenceLatticeResult* previous, bool full = false, std::stop_token stop = {})
        {
            return DecodeSentenceLattice(_lexicon, raw,
                LegacyTransition(),
                _settings.lattice,
                [&](auto text)
                {
                    return SentenceIsolationPenalty(text, [](auto value) { return SentenceCharacterRanks::Default().GetRank(value); },
                        [&](auto a, auto b) { return ObservedBigram(a, b); }, _settings.isolation);
                },
                [&](int state, auto element) { return _supplements.Advance(state, element); }, previous, _fivegram ? &_history : nullptr, &_learning, [&](const auto& item)
                { return SentenceIsolationPenalty(item.text, [](auto value) { return SentenceCharacterRanks::Default().GetRank(value); },
                    [&](auto a, auto b) { return ObservedBigram(a, b); }, _settings.isolation, item.boundary.get(),
                    _settings.lattice.protectedIsolationFactor); }, full ? nullptr : _cachedLocked.get(), stop);
        }
    public:
        static SentenceLexicon BuildLexicon(const CompactLexicon& table, const RuntimeSentenceSettings& settings)
        {
            std::vector<CompactLexicon::Entry> entries;
            entries.reserve(table.Count());
            for (std::uint32_t i = 0; i < table.Count(); ++i)
            {
                auto code = table.Code(i);
                if (TrimText(code).empty()) continue;
                std::vector<std::u16string> values;
                std::unordered_set<std::u16string> seen;
                for (std::uint32_t j = 0; j < table.CandidateCount(i); ++j)
                {
                    auto packed = table.Candidate(i, j);
                    auto text = CandidateCommitText(packed);
                    if (!text.empty() && seen.emplace(text).second) values.emplace_back(text);
                }
                if (!values.empty()) entries.emplace_back(code, std::move(values));
            }
            return SentenceLexicon::Build(entries, SentenceCharacterRanks::Default().TakeTop(settings.optimalCodeHighFrequencyLimit),
                settings.fullCodeWhitelist);
        }
    private:
        RuntimeSentenceSettings _settings;
        SentenceLearningQuery _learning;
        std::shared_ptr<const SentenceLockedPrefix> _cachedLocked;
        std::unique_ptr<MappedSentenceNgram> _legacy;
        std::unique_ptr<SentenceFivegramModel::Query> _fivegram;
        SentenceHistoryTransition _history;
        SentenceLexicon _lexicon;
        SentenceSupplementMatcher _supplements;
        std::shared_ptr<const SentenceLatticeResult> _cached;
        std::shared_ptr<const RuntimeSentenceResult> _cachedResult;
        std::u16string _cachedRaw, _cachedRequired;
        bool _cachedEvidence = false;
        std::mutex _decodeMutex; // covers every access to model score/bigram caches
    };
}
