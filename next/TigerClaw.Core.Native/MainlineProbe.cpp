#include "SentenceLearning.h"
#include "SentenceLearningStore.h"
#include "LearningText.h"
#include "RuntimeSentenceDecoder.h"
#include <nlohmann/json.hpp>
#include <fstream>

using namespace tiger::core;
namespace
{
    using Json = nlohmann::json;
    std::u16string Text(const Json& value, const char* name, const char* fallback = "")
    { return learningUtf16(value.value(name, std::string(fallback))); }
}
int RunMainlineProbe(const std::filesystem::path& input, const std::filesystem::path& output)
{
    std::ifstream reader(input); std::ofstream writer(output);
    if (!reader || !writer) throw std::runtime_error("Cannot open mainline probe files");
    for (std::string line; std::getline(reader, line);)
    {
        auto root = Json::parse(line);
        std::vector<SentenceLearningEvent> events;
        if (root.contains("events")) for (const auto& e : root["events"])
        {
            SentenceLearningEvent event;
            event.id = e.value("id", std::to_string(events.size())); event.time = e.value("time", 1);
            event.mode = Text(e, "mode", "test-v1"); event.code = Text(e, "code"); event.text = Text(e, "text");
            event.context = Text(e, "context"); event.levels = e.value("levels", 1); events.push_back(std::move(event));
        }
        auto snapshot = SentenceLearningSnapshot::build(events);
        Json values = Json::array();
        if (root.at("op") == "journal")
        {
            SentenceLearningStore store(std::filesystem::path(Text(root, "path")));
            store.confirm(events); store.refresh();
            for (const auto& e : store.entries()) values.push_back({{"id",e.id}, {"time",e.time},
                {"mode",learningUtf8(e.mode)}, {"code",learningUtf8(e.code)}, {"text",learningUtf8(e.text)},
                {"context",learningUtf8(e.context)}, {"levels",e.levels}});
        }
        else if (root.at("op") == "learning")
        {
            for (const auto& q : root.at("queries"))
            {
                auto mode = Text(q, "mode", "test-v1"), code = Text(q, "code"), text = Text(q, "text"), context = Text(q, "context");
                values.push_back({{"score", snapshot->score(mode, code, text, context)},
                    {"confidence", snapshot->confidenceScore(mode, code, text, context)},
                    {"prefix", snapshot->prefixScore(mode, code, text, context)}, {"hash", learningUtf8(learningConfigurationHash(text))}});
            }
        }
        else if (root.at("op") == "decode")
        {
            std::vector<CompactLexicon::Entry> entries;
            for (const auto& row : root.at("lexicon"))
            {
                std::vector<std::u16string> texts;
                for (const auto& text : row[1]) texts.push_back(learningUtf16(text.get<std::string>()));
                entries.emplace_back(learningUtf16(row[0].get<std::string>()), std::move(texts));
            }
            SchemaLexicon schema{CompactLexicon::Build(entries), {}, {}};
            if (root.contains("supplements"))
            {
                auto supplementValues = std::make_shared<std::vector<SentenceSupplementEntry>>();
                for (const auto& e : root["supplements"]) supplementValues->push_back(SentenceSupplementEntry::Create(learningUtf16(e[0].get<std::string>()), e[1].get<std::int64_t>()));
                schema.supplements = supplementValues;
            }
            RuntimeSentenceSettings settings; settings.optimalCodeHighFrequencyLimit = 0; if (!root.value("isolation", false)) settings.isolation = {0, 0, false};
            settings.lattice.autoSelectMinCodeLength = std::clamp(root.value("auto_select_min_code_length", 3), 0, 128);
            settings.lattice.duplicateSingles = settings.lattice.autoSelectMinCodeLength > 0;
            settings.lattice.beamWidth = root.value("beam", 2000); settings.lattice.candidateLimit = root.value("limit", 5);
            settings.lattice.preserveTruncatedEvidence = root.value("preserve", false);
            settings.lattice.protectedIsolationFactor = root.value("protectedFactor", 1.0);
            settings.lattice.lexicalWeight = root.value("lexical", 0.0);
            settings.lattice.canonicalReward = root.value("canonical", 0.0);
            RuntimeSentenceDecoder decoder(schema, std::filesystem::path(Text(root, "model")), settings);
            decoder.SetLearning(snapshot, Text(root, "mode", "test-v1"));
            std::shared_ptr<const SentenceLockedPrefix> locked;
            for (const auto& raw : root.at("raws"))
            {
                auto rawText = learningUtf16(raw.get<std::string>());
                if (locked && !rawText.starts_with(locked->raw)) locked.reset();
                auto result = decoder.DecodeResult(rawText, true, {}, locked);
                if (root.contains("lockRaw") && Text(root, "lockRaw") == rawText && !locked)
                {
                    const auto& c = result->lattice->candidates.at(root.value("lockIndex", 1));
                    locked = std::make_shared<SentenceLockedPrefix>(SentenceLockedPrefix{rawText, c.text, c.boundary});
                }
                Json candidates = Json::array();
                for (const auto& c : result->lattice->candidates)
                    candidates.push_back({{"text", learningUtf8(c.text)}, {"score", c.score}, {"mass", c.logMass},
                        {"learning", c.learningScore}, {"code", c.codeScore}, {"source", c.source}, {"directRank", c.directRank},
                        {"lexical", c.lexicalScore}, {"segmented", learningUtf8(SegmentedSentenceCode(result->lattice->raw, c.boundary))}});
                auto prefixes = result->evidence.prefixes;
                std::sort(prefixes.begin(), prefixes.end(), [](const auto& a, const auto& b) {
                    return a.text < b.text || (a.text == b.text && a.rawLength < b.rawLength); });
                Json prefixValues = Json::array();
                for (const auto& p : prefixes) prefixValues.push_back({{"text",learningUtf8(p.text)}, {"raw",p.rawLength},
                    {"share",p.share}, {"baseShare",p.baseShare}, {"closed",p.boundaryClosed}, {"boundaryShare",p.boundaryShare}});
                values.push_back({{"raw", learningUtf8(result->lattice->raw)}, {"candidates", candidates},
                    {"evidence", {{"truncated",result->evidence.confidenceTruncated}, {"proposal",learningUtf8(result->evidence.proposal)},
                    {"low",result->evidence.neutralLowConfidence}, {"merged",result->evidence.mergedIncompleteTail}, {"prefixes",prefixValues}}}});
            }
        }
        else throw std::invalid_argument("Unknown mainline probe operation");
        writer << values.dump() << '\n';
    }
    if (!writer) throw std::runtime_error("Cannot write mainline probe result");
    return 0;
}
