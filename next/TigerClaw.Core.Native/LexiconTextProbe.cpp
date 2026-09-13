#include "LexiconText.h"
#include "LexiconFile.h"
#include "CompactLexicon.h"
#include "CodeCase.h"
#include "LexiconAssembly.h"
#include "LexiconOrder.h"
#include "TextElements.h"
#include "WordConstruction.h"
#include "SchemaLexicon.h"
#include "PinyinLexicon.h"
#include "ConfigParser.h"
#include "RuntimePaths.h"
#include "RecentSchemas.h"
#include "CandidatePage.h"
#include "OutputServices.h"
#include "OutputState.h"
#include "SendHistory.h"
#include "KeyPostProcessor.h"
#include "Punctuation.h"
#include "SelectionKeys.h"
#include "OrdinaryComposition.h"
#include "UpperCaseComposition.h"
#include "ManualTimer.h"
#include "PinyinComposition.h"
#include "ChineseComposition.h"
#include "ChineseInputSession.h"
#include "ActionShortcuts.h"
#include "MixedInputDecoder.h"
#include "MappedSentenceNgram.h"
#include "SentenceLattice.h"
#include "SentenceNgramTransition.h"
#include "SentenceIsolation.h"
#include "SentenceCharacterRanks.h"
#include "SentenceSupplement.h"
#include "SentencePathQuery.h"
#include "SentencePrefixEvidence.h"
#include "SentenceEarlyEvidence.h"
#include "SentenceNeuralRanking.h"
#include <cstdlib>
#include <nlohmann/json.hpp>
#include <iostream>

int RunLexiconTextProbe()
{
    std::unique_ptr<tiger::core::MappedSentenceNgram> sentenceModel;
#ifdef _WIN32
    wchar_t* modelPath = nullptr;
    std::size_t modelPathLength = 0;
    if (_wdupenv_s(&modelPath, &modelPathLength, L"TIGERCLAW_NATIVE_PROBE_SENTENCE_MODEL") != 0)
        throw std::runtime_error("Unable to read sentence probe model path");
    std::unique_ptr<wchar_t, decltype(&std::free)> ownedModelPath(modelPath, &std::free);
    if (modelPath && *modelPath)
        sentenceModel = std::make_unique<tiger::core::MappedSentenceNgram>(std::filesystem::path(modelPath));
#else
    if (auto path = std::getenv("TIGERCLAW_NATIVE_PROBE_SENTENCE_MODEL"); path && *path)
    {
        std::u8string utf8;
        for (auto p = path; *p; ++p) utf8 += static_cast<char8_t>(static_cast<unsigned char>(*p));
        sentenceModel = std::make_unique<tiger::core::MappedSentenceNgram>(std::filesystem::path(utf8));
    }
#endif
    std::string line;
    while (std::getline(std::cin, line))
    {
        auto input = nlohmann::json::parse(line);
        if (input.contains("neural_candidates"))
        {
            std::vector<tiger::core::SentenceBeamState> candidates;
            std::vector<double> scores;
            for (const auto& value : input["neural_candidates"])
            {
                tiger::core::SentenceBeamState candidate;
                for (const auto& unit : value["text"]) candidate.text += static_cast<char16_t>(unit.get<unsigned>());
                candidate.score = value["base"].get<double>(); candidate.maxRank = value["rank"].get<std::size_t>();
                if (value["segmented"].get<bool>())
                {
                    auto previous = std::make_shared<tiger::core::SentenceBoundary>(tiger::core::SentenceBoundary{nullptr, 1, 2});
                    candidate.boundary = std::make_shared<tiger::core::SentenceBoundary>(tiger::core::SentenceBoundary{previous, 2, 4});
                }
                if (scores.size() < 5) scores.push_back(value["neural"].get<double>());
                candidates.push_back(std::move(candidate));
            }
            auto output = nlohmann::json::array();
            for (auto rank : tiger::core::RankSentenceNeural(candidates, scores, input["duplicates"].get<bool>()))
                output.push_back({{"index", rank.index}, {"score", rank.finalScore}});
            for (std::size_t i = 5; i < candidates.size(); ++i) output.push_back({{"index", i}, {"score", candidates[i].score}});
            std::cout << output.dump() << '\n'; continue;
        }
        if (input.contains("prefix_evidence"))
        {
            std::vector<tiger::core::SentenceBeamState> candidates;
            for (const auto& value : input["prefix_evidence"])
            {
                tiger::core::SentenceBeamState candidate;
                for (const auto& unit : value["text"]) candidate.text += static_cast<char16_t>(unit.get<unsigned>());
                candidate.logMass = value["mass"].get<double>();
                for (const auto& boundary : value["boundaries"])
                    candidate.boundary = std::make_shared<tiger::core::SentenceBoundary>(tiger::core::SentenceBoundary{
                        candidate.boundary, boundary[0].get<std::size_t>(), boundary[1].get<std::size_t>()});
                candidates.push_back(std::move(candidate));
            }
            auto output = nlohmann::json::array();
            for (const auto& item : tiger::core::BuildSentencePrefixEvidence(candidates))
                output.push_back({{"text", std::vector<unsigned>(item.text.begin(), item.text.end())}, {"raw", item.rawLength},
                    {"share", item.share}, {"boundary_share", item.boundaryShare}, {"closed", item.boundaryClosed}});
            std::cout << output.dump() << '\n';
            continue;
        }
        if (input.contains("supplement_dir"))
        {
            auto name = input["supplement_dir"].get<std::string>();
            auto entries = tiger::core::LoadSentenceSupplements(std::filesystem::path(std::u8string(name.begin(), name.end())));
            auto output = nlohmann::json::array();
            for (const auto& entry : entries)
                output.push_back({{"text", std::vector<unsigned>(entry.text.begin(), entry.text.end())},
                    {"weight", entry.weight}, {"reward", entry.reward}});
            std::cout << output.dump() << '\n';
            continue;
        }
        if (input.contains("sentence_rank_queries"))
        {
            const auto& ranks = tiger::core::SentenceCharacterRanks::Default();
            auto values = nlohmann::json::array();
            for (const auto& query : input["sentence_rank_queries"])
            {
                std::u16string value;
                for (const auto& unit : query) value += static_cast<char16_t>(unit.get<unsigned>());
                values.push_back(ranks.GetRank(value));
            }
            auto tops = nlohmann::json::array();
            for (int count : {-1, 0, 1, 1500, 6000, 100000})
            {
                auto set = ranks.TakeTop(count);
                std::vector<int> selected;
                for (const auto& value : set) selected.push_back(ranks.GetRank(value));
                std::sort(selected.begin(), selected.end());
                tops.push_back(selected);
            }
            std::cout << nlohmann::json({{"ranks", values}, {"tops", tops}}).dump() << '\n';
            continue;
        }
        if (input.contains("sentence_raw"))
        {
            auto read = [](const nlohmann::json& value)
            {
                std::u16string text;
                for (const auto& unit : value) text += static_cast<char16_t>(unit.get<unsigned>());
                return text;
            };
            auto units = [](std::u16string_view text) { return std::vector<unsigned>(text.begin(), text.end()); };
            std::vector<tiger::core::CompactLexicon::Entry> entries;
            for (const auto& row : input["entries"])
            {
                std::vector<std::u16string> values;
                for (const auto& value : row[1]) values.push_back(read(value));
                entries.emplace_back(read(row[0]), std::move(values));
            }
            tiger::core::SentenceLexicon::CharacterSet common, whitelist;
            for (const auto& value : input["common"]) common.insert(read(value));
            for (const auto& value : input["whitelist"]) whitelist.insert(read(value));
            auto lexicon = tiger::core::SentenceLexicon::Build(entries, common, whitelist);
            if (input.contains("path_queries"))
            {
                auto output = nlohmann::json::array();
                for (const auto& query : input["path_queries"])
                {
                    auto excluded = query["excluded"].is_null() ? std::optional<std::u16string>{} : read(query["excluded"]);
                    output.push_back(tiger::core::HasCompleteSentenceCandidate(lexicon, read(input["sentence_raw"]),
                        input["duplicates"].get<bool>(), read(query["required"]),
                        excluded ? std::optional<std::u16string_view>(*excluded) : std::nullopt, query["group"].get<bool>()));
                }
                std::cout << output.dump() << '\n';
                continue;
            }
            tiger::core::SentenceLatticeSettings settings;
            settings.beamWidth = input["beam"].get<std::size_t>();
            settings.candidateLimit = input["limit"].get<std::size_t>();
            settings.rankPenalty = input["rank_penalty"].get<double>();
            settings.emittedReward = input["reward"].get<double>();
            settings.wholeSingleReward = input["single_reward"].get<double>();
            settings.duplicateSingles = input["duplicates"].get<bool>();
            tiger::core::SentenceIsolationSettings isolationSettings{0, 0, false};
            std::vector<tiger::core::SentenceSupplementEntry> supplementEntries;
            if (input.contains("supplements"))
                for (const auto& entry : input["supplements"])
                    supplementEntries.push_back(tiger::core::SentenceSupplementEntry::Create(read(entry[0]), entry[1].get<std::int64_t>()));
            auto supplements = tiger::core::SentenceSupplementMatcher::Build(supplementEntries);
            if (input.contains("isolation"))
            {
                const auto& value = input["isolation"];
                isolationSettings = {value["threshold"].get<int>(), value["lambda"].get<double>(), value["log"].get<bool>()};
            }
            auto sequence = input.contains("sequence") ? input["sequence"] : nlohmann::json::array({input["sentence_raw"]});
            std::optional<tiger::core::SentenceLatticeResult> cached;
            for (const auto& raw : sequence)
            {
            auto result = tiger::core::DecodeSentenceLattice(lexicon, read(raw),
                [&](auto a, auto b, auto c) { return sentenceModel
                    ? tiger::core::ScoreSentenceNgramTransition(sentenceModel->Model(), a, b, c, input.value("boundaries", true))
                    : 0.0; }, settings, [&](auto text)
                {
                    return tiger::core::SentenceIsolationPenalty(text, [&](auto unit)
                        { return tiger::core::SentenceCharacterRanks::Default().GetRank(unit); },
                        [&](auto a, auto b) { return sentenceModel && sentenceModel->Model().HasObservedBigram(a, b); }, isolationSettings);
                }, [&](int state, auto element) { return supplements.Advance(state, element); }, cached ? &*cached : nullptr);
            auto candidates = nlohmann::json::array();
            for (const auto& item : result.candidates)
                candidates.push_back({{"text", units(item.text)}, {"code", units(tiger::core::SegmentedSentenceCode(result.raw, item.boundary))},
                    {"score", item.score}, {"mass", item.logMass}, {"rank", item.maxRank}, {"supplement", item.supplementScore}});
            nlohmann::json evidence = nullptr;
            if (input.contains("evidence_required"))
            {
                auto early = tiger::core::BuildSentenceEarlyEvidence(lexicon, result,
                    [&](auto a, auto b, auto c) { return sentenceModel ? tiger::core::ScoreSentenceNgramTransition(sentenceModel->Model(),
                        a, b, c, input.value("boundaries", true)) : 0.0; }, settings,
                    [&](auto text) { return tiger::core::SentenceIsolationPenalty(text,
                        [](auto value) { return tiger::core::SentenceCharacterRanks::Default().GetRank(value); },
                        [&](auto a, auto b) { return sentenceModel && sentenceModel->Model().HasObservedBigram(a, b); }, isolationSettings); },
                    read(input["evidence_required"]));
                auto prefixes = nlohmann::json::array(), lengths = nlohmann::json::array();
                for (const auto& prefix : early.prefixes)
                    prefixes.push_back({{"text", units(prefix.text)}, {"raw", prefix.rawLength}, {"share", prefix.share},
                        {"boundary_share", prefix.boundaryShare}, {"closed", prefix.boundaryClosed}});
                for (const auto& [text, length] : early.rawLengths) lengths.push_back({units(text), length});
                evidence = {{"prefixes", prefixes}, {"lengths", lengths}, {"proposal", units(early.proposal)}, {"share", early.proposalShare},
                    {"neutral_tail", early.neutralIncompleteTail}, {"merged_tail", early.mergedIncompleteTail},
                    {"low", early.neutralLowConfidence}, {"truncated", early.confidenceTruncated}};
            }
            std::cout << nlohmann::json({{"raw", units(result.raw)}, {"expanded", result.expanded}, {"candidates", candidates}, {"evidence", evidence}}).dump() << '\n';
            cached = std::move(result);
            }
            continue;
        }
        if (input.contains("ngram_path"))
        {
            auto path = input["ngram_path"].get<std::string>();
            std::u8string pathUtf8;
            for (unsigned char byte : path) pathUtf8 += static_cast<char8_t>(byte);
            tiger::core::MappedSentenceNgram model{std::filesystem::path(pathUtf8)};
            auto read = [](const nlohmann::json& value)
            {
                std::u16string text;
                for (const auto& unit : value) text += static_cast<char16_t>(unit.get<unsigned>());
                return text;
            };
            auto results = nlohmann::json::array();
            for (const auto& query : input["queries"])
            {
                auto a = read(query[0]), b = read(query[1]), c = read(query[2]);
                results.push_back({model.Model().LogProbability(a, b, c, true),
                    model.Model().LogProbability(a, b, c, false), model.Model().HasObservedBigram(b, c)});
            }
            std::cout << results.dump() << '\n';
            continue;
        }
        if (input.contains("mixed_units"))
        {
            auto read = [](const nlohmann::json& value)
            {
                std::u16string text;
                for (const auto& unit : value) text += static_cast<char16_t>(unit.get<unsigned>());
                return text;
            };
            auto units = [](std::u16string_view text)
            {
                auto result = nlohmann::json::array();
                for (auto unit : text) result.push_back(static_cast<unsigned>(unit));
                return result;
            };
            tiger::core::FixedLengthMixedDecoder decoder([](std::u16string_view code)
                { return code.find_first_of(u"xX") != std::u16string_view::npos ? std::u16string{} : std::u16string(code); },
                [](std::u16string_view text) { return u"[" + std::u16string(text) + u"]"; });
            std::unordered_map<std::size_t, std::u16string> preferred;
            for (const auto& pair : input["preferred"]) preferred[pair[0].get<std::size_t>()] = read(pair[1]);
            auto result = decoder.Decode(read(input["mixed_units"]), input["maximum"].get<int>(), 1, preferred);
            auto segments = nlohmann::json::array();
            for (const auto& segment : result.segments) segments.push_back({units(segment.code), units(segment.candidate)});
            std::cout << nlohmann::json{{"raw", units(result.raw)}, {"prefix", units(result.prefix)},
                {"active", units(result.active)}, {"surface", units(result.surface)}, {"segments", segments}}.dump() << '\n';
            continue;
        }
        if (input.contains("mixed_decode"))
        {
            auto units = [](const std::string& s) { return std::u16string(s.begin(), s.end()); };
            auto narrow = [](std::u16string_view s)
            {
                auto bytes = tiger::core::EncodeUtf8Text(s);
                return std::string(bytes.begin(), bytes.end());
            };
            tiger::core::FixedLengthMixedDecoder decoder([](std::u16string_view code)
                { return code.find_first_of(u"xX") != std::u16string_view::npos ? std::u16string{} : tiger::core::FoldOrdinalCode(code); },
                [](std::u16string_view text) { return u"[" + std::u16string(text) + u"]"; });
            std::unordered_map<std::size_t, std::u16string> preferred;
            for (const auto& pair : input["preferred"]) preferred[pair[0].get<std::size_t>()] = units(pair[1].get<std::string>());
            auto result = decoder.Decode(units(input["mixed_decode"].get<std::string>()), input["maximum"].get<int>(), 1, preferred);
            auto segments = nlohmann::json::array();
            for (const auto& segment : result.segments) segments.push_back({narrow(segment.code), narrow(segment.candidate)});
            std::cout << nlohmann::json{{"raw", narrow(result.raw)}, {"prefix", narrow(result.prefix)},
                {"active", narrow(result.active)}, {"surface", narrow(result.surface)}, {"segments", segments}}.dump() << '\n';
            continue;
        }
        if (input.contains("shortcut_parse"))
        {
            std::u16string value;
            for (const auto& unit : input["shortcut_parse"]) value.push_back(static_cast<char16_t>(unit.get<int>()));
            auto gesture = tiger::core::ShortcutGesture::Parse(value);
            auto result = nlohmann::json(nullptr);
            if (gesture)
            {
                auto bytes = tiger::core::EncodeUtf8Text(gesture->ConfigString());
                result = std::string(bytes.begin(), bytes.end());
            }
            std::cout << result.dump() << '\n';
            continue;
        }
        if (input.contains("shortcut_create"))
        {
            int key = input["shortcut_create"][0].get<int>(), flags = input["shortcut_create"][1].get<int>();
            auto gesture = tiger::core::ShortcutGesture::Create(key, flags & 1, flags & 2, flags & 4, flags & 8);
            auto matches = nlohmann::json::array(), conflicts = nlohmann::json::array();
            if (gesture)
            {
                for (int f = 0; f < 16; ++f) matches.push_back(gesture->Matches(key, f & 4, f & 1, f & 2, f & 8));
                for (int f = 0; f < 4; ++f) conflicts.push_back(static_cast<int>(tiger::core::ReservedShortcutConflict(*gesture, f & 1, f & 2)));
            }
            std::cout << nlohmann::json{{"valid", gesture.has_value()}, {"matches", matches}, {"conflicts", conflicts}}.dump() << '\n';
            continue;
        }
        if (input.contains("currency_command"))
        {
            auto raw = input.at("currency_command").get<std::string>();
            auto result = tiger::core::ConvertUpperCaseCurrency(std::u16string(raw.begin(), raw.end()));
            auto bytes = tiger::core::EncodeUtf8Text(result);
            std::cout << nlohmann::json(std::string(bytes.begin(), bytes.end())).dump() << '\n';
            continue;
        }
        if (input.contains("timer_command"))
        {
            auto raw = input.at("timer_command").get<std::string>();
            std::u16string code(raw.begin(), raw.end()); // ASCII command fixtures only
            auto delay = tiger::core::ManualTimerCommandDelay(code);
            std::cout << (delay ? nlohmann::json(*delay) : nlohmann::json(nullptr)).dump() << '\n';
            continue;
        }
        if (input.contains("upper_numeric"))
        {
            std::u16string code;
            for (const auto& item : input["upper_numeric"])
            {
                int unit = item.get<int>();
                if (unit < 0 || unit > 65535) throw std::invalid_argument("Invalid UTF-16 unit");
                code.push_back(static_cast<char16_t>(unit));
            }
            std::cout << nlohmann::json{{"numeric", tiger::core::IsUpperCaseNumericPrefix(code)},
                {"command", static_cast<int>(tiger::core::ClassifyUpperCaseCommand(code))}}.dump() << '\n';
            continue;
        }
        if (input.contains("ordinary_trace"))
        {
            using namespace tiger::core;
            std::vector<CompactLexicon::Entry> entries;
            // This isolated trace uses ASCII fixture text; Unicode table/text
            // semantics have their separate probes.
            auto units = [](const std::string& text) { return std::u16string(text.begin(), text.end()); };
            for (const auto& entry : input["entries"])
            {
                std::vector<std::u16string> texts;
                for (const auto& text : entry["texts"]) texts.push_back(units(text.get<std::string>()));
                entries.emplace_back(units(entry["code"].get<std::string>()), std::move(texts));
            }
            SchemaLexicon schema{CompactLexicon::Build(entries), {}, BuildLexiconMetadata(entries)};
            OrdinarySettings settings;
            settings.maxCodeLength = input.at("max").get<int>();
            settings.pageSize = input.at("size").get<int>();
            settings.maxCodeAutoCommit = input.at("auto").get<bool>();
            settings.clearOnNoCode = input.at("clear").get<bool>();
            settings.enterClear = input.at("enter").get<bool>();
            settings.tabClear = input.at("tab").get<bool>();
            settings.englishPunctuation = input.value("english", false);
            settings.slashIsDunhao = input.value("slash", true);
            settings.secondSemicolon = input.value("second", false);
            settings.thirdQuote = input.value("third", false);
            settings.mixedInput = input.value("mixed", false);
            auto mixedDecoder = [&]
            {
                return FixedLengthMixedDecoder([&](std::u16string_view code)
                    {
                        auto page = ReadCandidatePage(schema.table, code, 0, 1);
                        return page.entries.empty() ? std::u16string{} : page.entries.front();
                    }, [](std::u16string_view text) { return std::u16string(CandidateCommitText(text)); });
            };
            OrdinaryComposition composition;
            SelectionKeys selection;
            OutputState outputState;
            SendHistory history;
            bool uppercaseTrace = input.value("uppercase", false);
            bool pinyinTrace = input.value("pinyin", false);
            PinyinComposition py;
            ChineseComposition chinese(MakeUpperCaseServices([](std::u16string_view)
                { throw std::logic_error("Timer command is outside this trace"); }), mixedDecoder());
            ChineseInputSession chineseSession(MakeUpperCaseServices([](std::u16string_view)
                { throw std::logic_error("Timer command is outside this trace"); }), mixedDecoder());
            // No S key occurs in the trace, so Ds/DS timer commands cannot
            // arise. Currency and numeric-prefix services are production code.
            UpperCaseComposition upper(MakeUpperCaseServices([](std::u16string_view)
                { throw std::logic_error("Timer command is outside this trace"); }));
            char16_t upperStart = static_cast<char16_t>(input.value("upper_start", 65));
            auto trace = nlohmann::json::array();
            auto utf8 = [](std::u16string_view text)
            {
                auto bytes = EncodeUtf8Text(text);
                return std::string(bytes.begin(), bytes.end());
            };
            for (const auto& key : input["ordinary_trace"])
            {
                int vk = key.is_object() ? key.at("vk").get<int>() : key.get<int>();
                bool shift = key.is_object() && key.value("shift", false);
                if (input.value("chinese", false))
                {
                    if (input.value("postprocess", false))
                    {
                        PostProcessResult result;
                        if (key.is_object() && key.contains("schema_max"))
                        {
                            settings.maxCodeLength = key["schema_max"].get<int>();
                            settings.mixedInput = key["schema_mixed"].get<bool>();
                            chineseSession.RefreshAfterSchemaSwitch(settings);
                            result.handled = true;
                        }
                        else if (input.value("physical", false))
                        {
                            InputKeyEvent event;
                            event.vk = vk; event.shift = shift;
                            event.action = key.value("up", false) ? u"up" : u"down";
                            event.ctrl = key.value("ctrl", false);
                            result = chineseSession.ProcessKey(event, schema, schema.table, settings, true, true, true,
                                selection, LoadActionBindings({}), {}, {}, CtrlSpaceState::Time{});
                        }
                        else result = key.is_object() && key.contains("language")
                            ? PostProcessResult{true, chineseSession.SetChinese(key["language"].get<bool>()), {}, false}
                            : vk == 0x14 ? chineseSession.CapsLockKey(shift, false, false, false, false, schema, schema.table)
                            : chineseSession.KeyDown(vk, shift, schema, schema.table, settings, true, selection);
                        std::vector<std::string> page;
                        for (const auto& entry : chineseSession.Page(schema, schema.table, settings).entries) page.push_back(utf8(entry));
                        trace.push_back({{"handled", result.handled}, {"text", utf8(result.text)}, {"raw", utf8(chineseSession.ActiveCode())}, {"full", utf8(chineseSession.Raw())},
                            {"composing", result.composing}, {"buffer", utf8(result.inputBuffer)}, {"page", page}, {"mode", chineseSession.IsChinese() ? static_cast<int>(chineseSession.Mode()) : 0}});
                        continue;
                    }
                    auto result = chinese.KeyDown(vk, shift, schema, schema.table, settings, true, selection, outputState, history);
                    std::vector<std::string> page;
                    for (const auto& entry : chinese.Page(schema, schema.table, settings).entries) page.push_back(utf8(entry));
                    trace.push_back({{"handled", result.handled}, {"text", utf8(result.text)}, {"raw", utf8(chinese.ActiveCode())}, {"full", utf8(chinese.Raw())},
                        {"composing", result.composing}, {"buffer", utf8(result.inputBuffer)}, {"page", page}, {"mode", static_cast<int>(chinese.Mode())}});
                    continue;
                }
                if (pinyinTrace)
                {
                    auto result = py.Active() ? py.KeyDown(vk, shift, true, schema.table, settings, selection, outputState, history, {}) : py.Start();
                    std::vector<std::string> page;
                    for (const auto& entry : py.Page(schema.table, settings).entries) page.push_back(utf8(entry));
                    trace.push_back({{"handled", result.handled}, {"text", utf8(result.text)}, {"raw", utf8(py.Raw())},
                        {"composing", result.composing}, {"buffer", utf8(result.inputBuffer)}, {"page", page}});
                    continue;
                }
                if (uppercaseTrace)
                {
                    auto result = upper.Active() ? upper.KeyDown(vk, shift, settings.tabClear) : upper.Start(upperStart);
                    trace.push_back({{"handled", result.handled}, {"text", utf8(result.text)}, {"raw", utf8(upper.Raw())},
                        {"composing", result.composing}, {"buffer", utf8(result.inputBuffer)}, {"page", nlohmann::json::array()}});
                    continue;
                }
                if (!composition.Active())
                {
                    bool shortStart = (vk == 186 && schema.metadata.shortSemicolon) || (vk == 191 && schema.metadata.shortSlash) || (vk == 219 && schema.metadata.shortBracket);
                    if ((vk < 65 || vk > 90) && !shortStart) vk = 65;
                    shift = false;
                }
                auto result = composition.KeyDown(vk, shift, schema, settings, selection, outputState, history).value();
                std::vector<std::string> page;
                for (const auto& entry : composition.Page(schema, settings).entries) page.push_back(utf8(entry));
                trace.push_back({{"handled", result.handled}, {"text", utf8(result.text)}, {"raw", utf8(composition.Raw())},
                    {"composing", result.composing}, {"buffer", utf8(result.inputBuffer)}, {"page", page}});
            }
            std::cout << trace.dump() << '\n';
            continue;
        }
        if (input.contains("selection_format"))
        {
            tiger::core::SelectionBindings bindings;
            for (const auto& item : input["selection_format"])
                bindings[item.at("number").get<int>()] = item.at("keys").get<std::vector<int>>();
            auto text = tiger::core::BuildSelectionBindingsText(bindings);
            auto file = tiger::core::BuildSelectionFileText(bindings);
            std::cout << nlohmann::json{{"text", std::vector<std::uint16_t>(text.begin(), text.end())},
                {"file", std::vector<std::uint16_t>(file.begin(), file.end())}}.dump() << '\n';
            continue;
        }
        if (input.contains("selection_lines"))
        {
            std::vector<std::u16string> lines;
            for (const auto& raw : input["selection_lines"])
            {
                std::u16string lineText;
                for (const auto& value : raw)
                {
                    int unit = value.get<int>();
                    if (unit < 0 || unit > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    lineText.push_back(static_cast<char16_t>(unit));
                }
                lines.push_back(std::move(lineText));
            }
            auto parsed = tiger::core::ParseSelectionBindings(lines);
            auto bindings = nlohmann::json::array();
            for (const auto& [number, keys] : parsed.bindings) bindings.push_back({{"number", number}, {"keys", keys}});
            std::cout << nlohmann::json{{"success", parsed.success}, {"bindings", bindings},
                {"error", std::vector<std::uint16_t>(parsed.error.begin(), parsed.error.end())}}.dump() << '\n';
            continue;
        }
        if (input.contains("punct_vk"))
        {
            tiger::core::OutputState state;
            if (input.at("armed").get<bool>()) state.Observe(0x31, true, false, {});
            int vk = input.at("punct_vk").get<int>();
            bool english = input.at("english").get<bool>();
            auto result = input.at("shift").get<bool>() ? tiger::core::ResolveShiftChineseSymbol(vk, english) :
                tiger::core::ResolveChineseSymbol(vk, english, input.at("slash").get<bool>(), state);
            std::cout << nlohmann::json{{"text", result ? nlohmann::json(std::vector<std::uint16_t>(result->begin(), result->end())) : nlohmann::json(nullptr)}, {"armed", state.DecimalArmed()}}.dump() << '\n';
            continue;
        }
        if (input.contains("post_trace"))
        {
            tiger::core::KeyPostProcessor processor;
            auto output = nlohmann::json::array();
            auto units = [](const std::u16string& text) { return std::vector<std::uint16_t>(text.begin(), text.end()); };
            for (const auto& step : input["post_trace"])
            {
                tiger::core::PostProcessResult result;
                result.handled = step.at("handled").get<bool>();
                result.inputBuffer = u"ab"; result.composing = true;
                for (const auto& item : step.at("text"))
                {
                    int unit = item.get<int>();
                    if (unit < 0 || unit > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    result.text.push_back(static_cast<char16_t>(unit));
                }
                int quote = step.at("quote").get<int>();
                if (quote) result.text = processor.EmitQuote(quote == 2);
                int flags = step.at("flags").get<int>();
                processor.Process(step.at("vk").get<int>(), step.at("down").get<bool>(), flags & 1, flags & 2, flags & 4, flags & 8, flags & 16, result);
                output.push_back({{"text", units(result.text)}, {"buffer", units(result.inputBuffer)}, {"composing", result.composing},
                    {"add", result.action == tiger::core::OutputAction::OpenAddWord}, {"history", units(processor.History().LastWord(20))},
                    {"count", processor.History().VisibleCount()}, {"repeat", units(processor.Output().RepeatBuffer())}, {"decimal", processor.Output().DecimalArmed()}});
            }
            std::cout << output.dump() << '\n';
            continue;
        }
        if (input.contains("guess_vk"))
        {
            auto result = tiger::core::GuessPassThroughText(input.at("guess_vk").get<int>(), input.at("shift").get<bool>(), input.at("caps").get<bool>());
            std::cout << (result ? nlohmann::json(std::vector<std::uint16_t>(result->begin(), result->end())) : nlohmann::json(nullptr)).dump() << '\n';
            continue;
        }
        if (input.contains("output_digit"))
        {
            std::u16string text;
            for (const auto& item : input.at("output_digit"))
            {
                int unit = item.get<int>();
                if (unit < 0 || unit > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                text.push_back(static_cast<char16_t>(unit));
            }
            int modifiers = input.at("modifiers").get<int>();
            std::cout << nlohmann::json::array({tiger::core::IsOutputEndingWithDigit(text),
                tiger::core::IsPassThroughOutputDigit(input.at("vk").get<int>(), modifiers & 1, modifiers & 2, modifiers & 4, modifiers & 8)}).dump() << '\n';
            continue;
        }
        if (input.contains("weekday"))
        {
            auto locale = input.at("locale").get<std::string>();
            auto value = tiger::core::LocalizedOutputDayName(input.at("weekday").get<int>(), &locale);
            std::cout << nlohmann::json(std::vector<std::uint16_t>(value.begin(), value.end())).dump() << '\n';
            continue;
        }
        if (input.contains("page_trace"))
        {
            tiger::core::CandidatePageTracker tracker;
            auto output = nlohmann::json::array();
            for (const auto& step : input["page_trace"])
            {
                std::u16string code;
                for (const auto& unit : step.at("code"))
                {
                    int value = unit.get<int>();
                    if (value < 0 || value > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    code.push_back(static_cast<char16_t>(value));
                }
                auto op = step.at("op").get<std::string>();
                auto total = step.at("total").get<std::uint32_t>();
                auto mode = step.at("mode").get<int>(), size = step.at("size").get<int>();
                if (op == "reset") tracker.Reset();
                else if (op == "move") tracker.Move(code, mode, total, size, step.at("delta").get<int>());
                else if (op == "get") tracker.Resolve(code, mode, total, size);
                else if (op == "key")
                {
                    auto setting = step.at("keys").get<std::string>(); // ASCII fixture setting identifiers
                    tracker.MoveKey(code, mode, total, size, std::u16string(setting.begin(), setting.end()), step.at("vk").get<int>(), step.at("shift").get<bool>());
                }
                else throw std::runtime_error("Unknown page operation");
                output.push_back(tracker.Index());
            }
            std::cout << output.dump() << '\n';
            continue;
        }
        if (input.contains("config_serialize"))
        {
            auto read = [](const nlohmann::json& raw)
            {
                std::u16string text;
                for (const auto& unit : raw)
                {
                    auto value = unit.get<int>();
                    if (value < 0 || value > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    text.push_back(static_cast<char16_t>(value));
                }
                return text;
            };
            tiger::core::ConfigValues config;
            for (const auto& pair : input["config_serialize"]) config.emplace_back(read(pair.at("key")), read(pair.at("value")));
            std::cout << nlohmann::json(tiger::core::SerializeConfig(config)).dump() << '\n';
            continue;
        }
        if (input.contains("recent_schemas"))
        {
            const auto& args = input["recent_schemas"];
            auto read = [](const nlohmann::json& raw)
            {
                std::u16string value;
                for (const auto& unit : raw)
                {
                    auto number = unit.get<int>();
                    if (number < 0 || number > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    value.push_back(static_cast<char16_t>(number));
                }
                return value;
            };
            auto history = tiger::core::SeedRecentSchemas(read(args.at("persisted")));
            for (const auto& name : args.at("record")) tiger::core::RecordRecentSchema(history, read(name));
            std::vector<std::u16string> schemas;
            for (const auto& name : args.at("schemas")) schemas.push_back(read(name));
            auto target = tiger::core::ChooseRecentSchema(schemas, read(args.at("current")), history);
            auto units = [](const std::u16string& value) { return std::vector<std::uint16_t>(value.begin(), value.end()); };
            auto output = nlohmann::json::array();
            for (const auto& name : history) output.push_back(units(name));
            std::cout << nlohmann::json{{"history", output}, {"serialized", units(tiger::core::SerializeRecentSchemas(history))}, {"target", units(target)}}.dump() << '\n';
            continue;
        }
        if (input.contains("runtime_paths"))
        {
            const auto& args = input["runtime_paths"];
            auto path = [](const std::string& value) { return std::filesystem::path(std::u8string(value.begin(), value.end())); };
            auto root = tiger::core::ResolveCodeRoot(path(args.at("base").get<std::string>()), path(args.at("root").get<std::string>()).u16string());
            auto selected = tiger::core::SelectSchemaDirectory(root, path(args.at("current").get<std::string>()).u16string());
            auto units = [](const std::u16string& value) { return std::vector<std::uint16_t>(value.begin(), value.end()); };
            auto names = nlohmann::json::array();
            for (const auto& name : tiger::core::GetSchemaNames(root)) names.push_back(units(name));
            std::cout << nlohmann::json{{"root", units(root.u16string())}, {"directory", units(selected.directory.u16string())},
                {"name", units(selected.name)}, {"fallback", selected.usedFallback}, {"schemas", names}}.dump() << '\n';
            continue;
        }
        if (input.contains("config_lines") || input.contains("config_file"))
        {
            std::vector<std::u16string> lines;
            for (auto& raw : input.value("config_lines", nlohmann::json::array()))
            {
                std::u16string text;
                for (auto& value : raw)
                {
                    auto unit = value.get<int>();
                    if (unit < 0 || unit > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    text.push_back(static_cast<char16_t>(unit));
                }
                lines.push_back(std::move(text));
            }
            auto output = nlohmann::json::array();
            auto settings = tiger::core::ParseConfigLines(lines);
            if (input.contains("config_file"))
            {
                auto name = input["config_file"].get<std::string>();
                settings = tiger::core::ReadConfigFile(std::filesystem::path(std::u8string(name.begin(), name.end())));
            }
            for (const auto& [key, value] : settings)
                output.push_back({{"key", std::vector<std::uint16_t>(key.begin(), key.end())}, {"value", std::vector<std::uint16_t>(value.begin(), value.end())},
                    {"bool_false", tiger::core::ParseConfigBool(value, false)}, {"bool_true", tiger::core::ParseConfigBool(value, true)}});
            std::cout << output.dump() << '\n';
            continue;
        }
        if (input.contains("pinyin_base"))
        {
            auto name = input["pinyin_base"].get<std::string>();
            auto table = tiger::core::LoadPinyinLexicon(std::filesystem::path(std::u8string(name.begin(), name.end())));
            std::cout << nlohmann::json{{"image", std::vector<std::uint8_t>(table.Image().begin(), table.Image().end())}}.dump() << '\n';
            continue;
        }
        if (input.contains("schema"))
        {
            auto name = input["schema"].get<std::string>();
            auto locale = input.value("locale", "zh-CN");
            auto snapshot = tiger::core::LoadSchemaLexicon(std::filesystem::path(std::u8string(name.begin(), name.end())), &locale);
            std::vector<std::u16string> keys;
            for (const auto& pair : snapshot.construction) keys.push_back(pair.first);
            std::sort(keys.begin(), keys.end());
            auto map = nlohmann::json::array();
            for (const auto& key : keys)
            {
                const auto& code = snapshot.construction.at(key);
                map.push_back({{"text", std::vector<std::uint16_t>(key.begin(), key.end())}, {"code", std::vector<std::uint16_t>(code.begin(), code.end())}});
            }
            auto textMap = [](const tiger::core::TextMap& values)
            {
                std::vector<std::u16string> names;
                for (const auto& pair : values) names.push_back(pair.first);
                std::sort(names.begin(), names.end());
                auto output = nlohmann::json::array();
                for (const auto& key : names)
                {
                    const auto& value = values.at(key);
                    output.push_back({{"text", std::vector<std::uint16_t>(key.begin(), key.end())}, {"value", std::vector<std::uint16_t>(value.begin(), value.end())}});
                }
                return output;
            };
            auto codeSet = [](const tiger::core::CodeSet& values)
            {
                auto output = nlohmann::json::array();
                for (const auto& key : values.Keys()) output.push_back(std::vector<std::uint16_t>(key.begin(), key.end()));
                return output;
            };
            const auto& meta = snapshot.metadata;
            std::cout << nlohmann::json{{"image", std::vector<std::uint8_t>(snapshot.table.Image().begin(), snapshot.table.Image().end())}, {"lookup", map},
                {"comments", textMap(meta.comments)}, {"splits", textMap(meta.splits)}, {"full_codes", textMap(meta.fullCodes)},
                {"unique", codeSet(meta.unique)}, {"nonterminal", codeSet(meta.nonTerminal)}, {"auto_short", codeSet(meta.autoShort)},
                {"flags", {meta.shortSemicolon, meta.shortSlash, meta.shortBracket, meta.shortZ}}}.dump() << '\n';
            continue;
        }
        if (input.contains("graphemes") || input.contains("word"))
        {
            auto read = [](const nlohmann::json& values)
            {
                std::u16string text;
                for (auto& value : values)
                {
                    int unit = value.get<int>();
                    if (unit < 0 || unit > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    text.push_back(static_cast<char16_t>(unit));
                }
                return text;
            };
            if (input.contains("graphemes"))
                std::cout << nlohmann::json(tiger::core::TextElementStarts(read(input["graphemes"]))).dump() << '\n';
            else
            {
                tiger::core::ConstructCodeMap lookup;
                for (auto& row : input["lookup"]) lookup[read(row["text"])] = read(row["code"]);
                auto code = tiger::core::ConstructWordCode(read(input["word"]), lookup);
                std::cout << nlohmann::json(std::vector<std::uint16_t>(code.begin(), code.end())).dump() << '\n';
            }
            continue;
        }
        if (input.contains("directory"))
        {
            auto name = input["directory"].get<std::string>();
            std::string locale = input.value("locale", "");
            auto ordered = tiger::core::GetOrderedLexiconFiles(std::filesystem::path(std::u8string(name.begin(), name.end())),
                input.contains("locale") ? &locale : nullptr);
            auto output = nlohmann::json::array();
            for (const auto& path : ordered)
            {
                auto file = path.filename().u16string();
                output.push_back(std::vector<std::uint16_t>(file.begin(), file.end()));
            }
            std::cout << output.dump() << '\n';
            continue;
        }
        if (input.contains("normalize") || input.contains("rows"))
        {
            auto read = [](const nlohmann::json& units)
            {
                std::u16string text;
                for (auto& unit : units)
                {
                    int c = unit.get<int>();
                    if (c < 0 || c > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    text.push_back(static_cast<char16_t>(c));
                }
                return text;
            };
            auto units = [](const std::u16string& text) { return std::vector<std::uint16_t>(text.begin(), text.end()); };
            if (input.contains("normalize"))
                std::cout << nlohmann::json{{"normalized", units(tiger::core::NormalizeCode(read(input["normalize"])))}}.dump() << '\n';
            else
            {
                std::vector<tiger::core::LexiconRow> rows;
                for (auto& row : input["rows"]) rows.push_back({read(row["code"]), read(row["text"]), row["freq"].get<std::int32_t>()});
                auto entries = tiger::core::AssembleCodedRows(rows);
                auto output = nlohmann::json::array();
                for (auto& [code, candidates] : entries)
                {
                    auto texts = nlohmann::json::array();
                    for (auto& text : candidates) texts.push_back(units(text));
                    output.push_back({{"code", units(code)}, {"candidates", std::move(texts)}});
                }
                std::cout << output.dump() << '\n';
            }
            continue;
        }
        if (input.contains("entries"))
        {
            auto read = [](const nlohmann::json& units)
            {
                std::u16string text;
                for (auto& unit : units)
                {
                    auto c = unit.get<int>();
                    if (c < 0 || c > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    text.push_back(static_cast<char16_t>(c));
                }
                return text;
            };
            std::vector<tiger::core::CompactLexicon::Entry> entries;
            for (auto& entry : input["entries"])
            {
                std::vector<std::u16string> values;
                for (auto& text : entry["candidates"]) values.push_back(read(text));
                entries.emplace_back(read(entry["code"]), std::move(values));
            }
            if (input.contains("construct_file"))
            {
                std::vector<tiger::core::LexiconRow> explicitRows, uncoded, inferred;
                auto name = input["construct_file"].get<std::string>();
                auto path = std::filesystem::path(std::u8string(name.begin(), name.end()));
                if (!name.empty() && std::filesystem::is_regular_file(path))
                    tiger::core::ReadLexiconFile(path, [&](tiger::core::LexiconRow row) { explicitRows.push_back(std::move(row)); });
                for (const auto& row : input["uncoded"]) uncoded.push_back({{}, read(row["text"]), row["freq"].get<std::int32_t>()});
                auto lookup = tiger::core::BuildConstructCodeMap(explicitRows, entries);
                tiger::core::AppendInferredRows(uncoded, lookup, inferred);
                auto units = [](const std::u16string& text) { return std::vector<std::uint16_t>(text.begin(), text.end()); };
                std::vector<std::u16string> keys;
                for (const auto& pair : lookup) keys.push_back(pair.first);
                std::sort(keys.begin(), keys.end());
                auto map = nlohmann::json::array(), rows = nlohmann::json::array();
                for (auto& key : keys) map.push_back({{"text", units(key)}, {"code", units(lookup.at(key))}});
                for (auto& row : inferred) rows.push_back({{"code", units(row.code)}, {"text", units(row.text)}, {"freq", row.frequency}});
                std::cout << nlohmann::json{{"lookup", map}, {"inferred", rows}}.dump() << '\n';
                continue;
            }
            if (input.contains("adjustments"))
            {
                std::vector<std::u16string> lines;
                for (const auto& adjustment : input["adjustments"]) lines.push_back(read(adjustment));
                tiger::core::ApplyAdjustments(entries, lines);
            }
            if (input.contains("edit_files"))
                for (const auto& edit : input["edit_files"])
                {
                    auto name = edit["path"].get<std::string>();
                    tiger::core::LoadAdjustmentFile(entries, std::filesystem::path(std::u8string(name.begin(), name.end())), edit.value("custom", false));
                }
            auto table = tiger::core::CompactLexicon::Build(entries);
            std::cout << nlohmann::json{{"image", std::vector<std::uint8_t>(table.Image().begin(), table.Image().end())}}.dump() << '\n';
            continue;
        }
        if (input.contains("file"))
        {
            auto name = input["file"].get<std::string>();
            auto path = std::filesystem::path(std::u8string(name.begin(), name.end()));
            nlohmann::json output{{"coded", nlohmann::json::array()}, {"uncoded", nlohmann::json::array()}};
            tiger::core::ReadLexiconFile(path, [&](tiger::core::LexiconRow row)
            {
                nlohmann::json result{{"text", std::vector<std::uint16_t>(row.text.begin(), row.text.end())}, {"freq", row.frequency}};
                if (!row.code.empty()) result["code"] = std::vector<std::uint16_t>(row.code.begin(), row.code.end());
                output[row.code.empty() ? "uncoded" : "coded"].push_back(std::move(result));
            });
            std::cout << output.dump() << '\n';
            continue;
        }
        if (input.contains("bytes"))
        {
            auto decoded = tiger::core::DecodeLexiconBytes(input["bytes"].get<std::vector<std::uint8_t>>());
            std::cout << nlohmann::json{{"decoded", std::vector<std::uint16_t>(decoded.begin(), decoded.end())}}.dump() << '\n';
            continue;
        }
        if (input.contains("lines"))
        {
            auto read = [](const nlohmann::json& units)
            {
                std::u16string text;
                for (auto& unit : units)
                {
                    int c = unit.get<int>();
                    if (c < 0 || c > 65535) throw std::runtime_error("Invalid UTF-16 unit");
                    text.push_back(static_cast<char16_t>(c));
                }
                return text;
            };
            tiger::core::LexiconLineParser parser(input.value("yaml", false),
                input.contains("positive") ? read(input["positive"]) : u"+",
                input.contains("negative") ? read(input["negative"]) : u"-");
            nlohmann::json output{{"coded", nlohmann::json::array()}, {"uncoded", nlohmann::json::array()}};
            for (const auto& raw : input["lines"])
                for (const auto& row : parser.Parse(read(raw)))
                {
                    nlohmann::json result{{"text", std::vector<std::uint16_t>(row.text.begin(), row.text.end())},
                        {"freq", row.frequency}};
                    if (!row.code.empty()) result["code"] = std::vector<std::uint16_t>(row.code.begin(), row.code.end());
                    output[row.code.empty() ? "uncoded" : "coded"].push_back(std::move(result));
                }
            std::cout << output.dump() << '\n';
            continue;
        }
        std::u16string value;
        for (const auto& unit : input.at("text"))
        {
            auto code = unit.get<int>();
            if (code < 0 || code > 65535) throw std::runtime_error("Invalid UTF-16 unit");
            value.push_back(static_cast<char16_t>(code));
        }
        auto units = [](const std::u16string& text)
        { return std::vector<std::uint16_t>(text.begin(), text.end()); };
        std::cout << nlohmann::json{
            {"comment", units(tiger::core::StripInlineComment(value))},
            {"decoded", units(tiger::core::DecodeLexiconEscapes(value))},
            {"token", units(tiger::core::ParseLexiconEntryToken(value))}
        }.dump() << '\n';
    }
    return 0;
}
