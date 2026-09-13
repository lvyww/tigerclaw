#pragma once
#include "SchemaLexicon.h"
#include "SentenceCharacterRanks.h"
#include "SentenceLattice.h"
#include "SentenceIsolation.h"
#include "SentenceNgramTransition.h"
#include "MappedSentenceNgram.h"
#include "SentencePathQuery.h"
#include "SentenceEarlyEvidence.h"
#include "RuntimeSentenceSettings.h"
#include <mutex>

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
            : _settings(std::move(settings)), _model(modelPath),
              _lexicon(BuildLexicon(schema.table, _settings)),
              _supplements(SentenceSupplementMatcher::Build(schema.supplements
                  ? std::span<const SentenceSupplementEntry>(*schema.supplements) : std::span<const SentenceSupplementEntry>{})) {}
        RuntimeSentenceDecoder(const RuntimeSentenceDecoder&) = delete;
        RuntimeSentenceDecoder& operator=(const RuntimeSentenceDecoder&) = delete;

        SentenceLatticeResult DecodeFull(std::u16string_view raw)
        {
            std::lock_guard lock(_decodeMutex);
            return DecodeLocked(raw, nullptr);
        }
        std::shared_ptr<const SentenceLatticeResult> Decode(std::u16string_view raw)
        {
            return DecodeResult(raw)->lattice;
        }
        std::shared_ptr<const RuntimeSentenceResult> DecodeResult(std::u16string_view raw,
            bool includeEvidence = false, std::u16string_view requiredPrefix = {})
        {
            std::lock_guard lock(_decodeMutex);
            auto normalized = NormalizeSentenceRaw(raw);
            if (_cachedResult && _cachedRaw == normalized && _cachedEvidence == includeEvidence && _cachedRequired == requiredPrefix)
                return _cachedResult;
            auto nextLattice = DecodeLocked(raw, _cached.get());
            if (_cached && _cachedRaw == normalized) nextLattice.expanded = 0;
            auto nextResult = std::make_shared<RuntimeSentenceResult>();
            if (includeEvidence)
                nextResult->evidence = BuildSentenceEarlyEvidence(_lexicon, nextLattice,
                    [&](auto a, auto b, auto c) { return ScoreSentenceNgramTransition(_model.Model(), a, b, c, _settings.scoreSentenceBoundaries); },
                    _settings.lattice,
                    [&](auto text) { return SentenceIsolationPenalty(text,
                        [](auto value) { return SentenceCharacterRanks::Default().GetRank(value); },
                        [&](auto a, auto b) { return _model.Model().HasObservedBigram(a, b); }, _settings.isolation); }, requiredPrefix);
            nextResult->lattice = std::make_shared<const SentenceLatticeResult>(std::move(nextLattice));
            std::u16string ownedRequired(requiredPrefix);
            // Publish the candidate/evidence pair only after every allocation
            // and score operation succeeds. Previously returned pairs stay owned.
            _cachedRequired.swap(ownedRequired);
            _cachedRaw.swap(normalized);
            _cachedEvidence = includeEvidence;
            _cached = nextResult->lattice;
            _cachedResult = std::move(nextResult);
            return _cachedResult;
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
            std::optional<std::u16string_view> excluded = {}, bool groupEligibleOnly = false) const
        {
            return HasCompleteSentenceCandidate(_lexicon, raw, _settings.lattice.duplicateSingles, required, excluded, groupEligibleOnly);
        }
        bool IsProperCodePrefix(std::u16string_view code) const { return _lexicon.IsProperPrefix(NormalizeSentenceRaw(code)); }
        bool AllowsDuplicateSingles() const { return _settings.lattice.duplicateSingles; }
    private:
        SentenceLatticeResult DecodeLocked(std::u16string_view raw, const SentenceLatticeResult* previous)
        {
            return DecodeSentenceLattice(_lexicon, raw,
                [&](auto a, auto b, auto c) { return ScoreSentenceNgramTransition(_model.Model(), a, b, c, _settings.scoreSentenceBoundaries); },
                _settings.lattice,
                [&](auto text)
                {
                    return SentenceIsolationPenalty(text, [](auto value) { return SentenceCharacterRanks::Default().GetRank(value); },
                        [&](auto a, auto b) { return _model.Model().HasObservedBigram(a, b); }, _settings.isolation);
                },
                [&](int state, auto element) { return _supplements.Advance(state, element); }, previous);
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
        MappedSentenceNgram _model;
        SentenceLexicon _lexicon;
        SentenceSupplementMatcher _supplements;
        std::shared_ptr<const SentenceLatticeResult> _cached;
        std::shared_ptr<const RuntimeSentenceResult> _cachedResult;
        std::u16string _cachedRaw, _cachedRequired;
        bool _cachedEvidence = false;
        std::mutex _decodeMutex; // covers every access to model score/bigram caches
    };
}
