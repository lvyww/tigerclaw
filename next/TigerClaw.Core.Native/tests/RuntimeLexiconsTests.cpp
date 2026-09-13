#include "RuntimeLexicons.h"
#include "OutputServices.h"
#include "ChineseInputSession.h"
#include "RuntimeInput.h"
#include "MappedSentenceNgram.h"
#include "RuntimeSentenceDecoder.h"
#include "SentenceDecodeWorker.h"
#include "SentenceCompositionSession.h"
#include "RuntimeSentenceInput.h"
#include "RuntimeSentenceState.h"
#include <windows.h>
#include <fstream>
#include <iostream>
#include <thread>
#include <latch>
#include <stdexcept>
#include <source_location>
#include <semaphore>

using namespace tiger::core;
namespace
{
    void Check(bool value, std::source_location source = std::source_location::current())
    { if (!value) throw std::runtime_error("Runtime lexicon regression at line " + std::to_string(source.line())); }
    struct Fixture
    {
        std::filesystem::path root;
        Fixture()
        {
            auto base = std::filesystem::temp_directory_path();
            for (unsigned index = 0; index < 1000; ++index)
            {
                auto candidate = base / ("tiger-native-runtime-" + std::to_string(GetCurrentProcessId()) + "-" + std::to_string(GetTickCount64()) + "-" + std::to_string(index));
                if (std::filesystem::create_directory(candidate)) { root = std::move(candidate); return; }
            }
            throw std::runtime_error("Cannot allocate isolated fixture");
        }
        ~Fixture() { std::error_code error; std::filesystem::remove_all(root, error); }
    };
    void Write(const std::filesystem::path& path, std::u8string_view text)
    {
        std::ofstream output(path, std::ios::binary | std::ios::trunc);
        output.write(reinterpret_cast<const char*>(text.data()), static_cast<std::streamsize>(text.size()));
        if (!output) throw std::runtime_error("Cannot write fixture");
    }
    std::u16string First(const CompactLexicon& table, std::u16string_view code)
    {
        auto index = table.Find(code);
        Check(index.has_value());
        return table.Candidate(*index, 0);
    }
}
int main()
{
    try
    {
        Fixture fixture;
        auto localClock = ReadLocalOutputClock();
        SYSTEMTIME systemClock{}; GetLocalTime(&systemClock);
        // Permit a midnight boundary between the two calls, without treating
        // the wall clock as deterministic or asserting a locale-specific name.
        Check(localClock.year >= 1601 && localClock.year <= systemClock.wYear + 1);
        Check(localClock.month >= 1 && localClock.month <= 12 && !localClock.localizedDayName.empty());
        std::string english = "en-US";
        Check(LocalizedOutputDayName(0, &english) == u"Sunday");
        auto root = fixture.root;
        auto modelFile = root / "mapped-ngram.bin";
        std::u8string modelImage = u8"TCSKNM01";
        auto modelNumber = [&](std::uint64_t value, unsigned bytes)
        { for (unsigned i = 0; i < bytes; ++i) modelImage += static_cast<char8_t>(value >> (8 * i)); };
        modelNumber(1, 4); modelNumber(1, 4); // version, unigram count
        modelNumber(0, 4); modelNumber(std::bit_cast<std::uint32_t>(0.5f), 4);
        modelNumber(0, 8); modelNumber(0, 4); modelNumber(0, 8); modelNumber(0, 8);
        Write(modelFile, modelImage);
        {
            MappedSentenceNgram model(modelFile);
            Check(model.Model().LogProbability(u"", u"", u"a") == std::log(0.5));
            HANDLE blockedWriter = CreateFileW(modelFile.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
            Check(blockedWriter == INVALID_HANDLE_VALUE);
        }
        Write(modelFile, u8"bad model"); // mapping lifetime ended; file writable again
        bool invalidModel = false;
        try { MappedSentenceNgram model(modelFile); }
        catch (const std::invalid_argument&) { invalidModel = true; }
        Check(invalidModel);
        Write(modelFile, modelImage); // parsing failure also releases the file
        bool oversizedModel = false;
        try { ReadOnlyMapping mapping(modelFile, 1); }
        catch (const std::runtime_error&) { oversizedModel = true; }
        Check(oversizedModel);
        Write(modelFile, modelImage); // size failure releases the file
        SchemaLexicon sentenceSchema{CompactLexicon::Build(std::vector<CompactLexicon::Entry>{
            {u"aa", {u"display\x1e" u"a", u"other\x1e" u"a", u"b"}}, {u"bb", {u"b"}}}), {}, {}};
        sentenceSchema.supplements = std::make_shared<const std::vector<SentenceSupplementEntry>>(
            std::vector<SentenceSupplementEntry>{SentenceSupplementEntry::Create(u"ab", 1000)});
        RuntimeSentenceSettings sentenceSettings;
        sentenceSettings.optimalCodeHighFrequencyLimit = 0;
        sentenceSettings.isolation = {0, 0, false};
        {
            auto initial = std::make_shared<RuntimeLexiconSnapshot>();
            initial->schemaName = u"test\u6574\u53e5";
            initial->schema = std::make_shared<const SchemaLexicon>(sentenceSchema);
            initial->generation = 1;
            int loads = 0, releases = 0;
            SentenceServiceLifecycle service([&](std::stop_token) { ++loads; }, [&] { ++releases; },
                [](const SentenceNeuralRequest& request, std::stop_token)
                { return std::vector<double>(request.candidates.size(), -1); });
            RuntimeSentenceState runtime(modelFile, &service);
            runtime.Refresh(initial);
            runtime.Input().ImportRaw(u"AA", 1);
            Check(runtime.Input().WaitIdle(std::chrono::seconds(2))); runtime.Input().Pump();
            Check(service.WaitIdle(std::chrono::seconds(2)) && loads == 1);
            runtime.Input().KeyDown(0x28);
            auto sameGeneration = runtime.Input().Session().Request().generation;
            runtime.Refresh(initial);
            Check(runtime.Input().Session().Request().generation == sameGeneration);
            auto appearance = std::make_shared<RuntimeLexiconSnapshot>(*initial);
            appearance->generation = 11;
            appearance->config.emplace_back(u"\u4e3b\u9898", u"different");
            auto retainedInput = &runtime.Input();
            runtime.Refresh(appearance);
            Check(&runtime.Input() == retainedInput && runtime.Snapshot() == appearance);
            Check(runtime.Input().Session().Request().generation == sameGeneration && runtime.Input().Session().SelectedIndex() == 1);
            auto neuralOff = std::make_shared<RuntimeLexiconSnapshot>(*appearance);
            neuralOff->generation = 12;
            neuralOff->config.emplace_back(u"\u6574\u53e5\u795e\u7ecf\u91cd\u6392", u"false");
            runtime.Refresh(neuralOff);
            Check(&runtime.Input() == retainedInput && runtime.Input().Session().SelectedIndex() == 1);
            Check(service.WaitIdle(std::chrono::seconds(2)) && releases == 1);
            runtime.Refresh(initial); // enable neural without resetting the frozen selection
            Check(service.WaitIdle(std::chrono::seconds(2)) && loads == 2);
            runtime.Input().Pump(); Check(runtime.Input().Session().SelectedIndex() == 1);
            auto replacement = std::make_shared<RuntimeLexiconSnapshot>(*initial);
            replacement->generation = 2;
            replacement->config.emplace_back(u"\u6574\u53e5\u795e\u7ecf\u91cd\u6392", u"false");
            replacement->schema = std::make_shared<const SchemaLexicon>(SchemaLexicon{
                CompactLexicon::Build(std::vector<CompactLexicon::Entry>{{u"aa", {u"z"}}}), {}, {}});
            runtime.Refresh(replacement);
            Check(runtime.Input().ExportUncommittedRaw() == u"AA");
            Check(runtime.Input().WaitIdle(std::chrono::seconds(2))); runtime.Input().Pump();
            Check(runtime.Input().Session().Candidates()[0].text == u"z" && runtime.Input().Session().SelectedIndex() == 0);
            Check(runtime.Input().Session().Request().lexiconVersion == 2);
            Check(service.WaitIdle(std::chrono::seconds(2)) && releases == 2);
            auto invalid = std::make_shared<RuntimeLexiconSnapshot>(*replacement);
            invalid->schema.reset();
            bool rejected = false;
            try { runtime.Refresh(invalid); } catch (const std::invalid_argument&) { rejected = true; }
            Check(rejected && runtime.Snapshot() == replacement && runtime.Input().ExportUncommittedRaw() == u"AA");
            Check(runtime.Input().KeyDown(0x20).commit == std::optional<std::u16string>(u"z"));
            runtime.Refresh(initial);
            runtime.Input().ImportRaw(u"AbCD", 3);
            Check(service.WaitIdle(std::chrono::seconds(2)) && loads == 3);
            ChineseInputSession ordinary(MakeUpperCaseServices([](std::u16string_view) {}));
            auto targetSettings = LoadOrdinarySettings({}, 1); targetSettings.maxCodeLength = 2;
            ordinary.ImportRaw(u"zz", targetSettings);
            bool migrationFailed = false;
            try { runtime.MigrateToOrdinary(ordinary, targetSettings); }
            catch (const std::logic_error&) { migrationFailed = true; }
            Check(migrationFailed && ordinary.Raw() == u"zz" && runtime.Input().ExportUncommittedRaw() == u"AbCD");
            Check(service.WaitIdle(std::chrono::seconds(2)) && releases == 2);
            Check(runtime.Leave() == u"AbCD" && !runtime.Snapshot());
            Check(service.WaitIdle(std::chrono::seconds(2)) && releases == 3);
            Check(runtime.Leave().empty());
            ordinary.ImportRaw(u"AA", targetSettings);
            RuntimeSentenceState missingModel(root / "missing-sentence-model.bin");
            bool modelFailed = false;
            try { missingModel.MigrateFromOrdinary(ordinary, initial); }
            catch (const std::exception&) { modelFailed = true; }
            Check(modelFailed && ordinary.Raw() == u"AA" && !missingModel.Snapshot());
            Check(runtime.MigrateFromOrdinary(ordinary, initial));
            Check(ordinary.Raw().empty() && runtime.Input().ExportUncommittedRaw() == u"AA");
            Check(runtime.Input().WaitIdle(std::chrono::seconds(2))); runtime.Input().Pump();
            Check(runtime.Input().Session().Candidates()[0].text == u"a");
            runtime.MigrateToOrdinary(ordinary, targetSettings);
            Check(ordinary.Raw() == u"AA" && !runtime.Snapshot());
            ordinary.ClearComposition();
            ordinary.KeyDown(0x41, true, *initial->schema, initial->schema->table,
                targetSettings, true, SelectionKeys{});
            Check(ordinary.Mode() == ChineseMode::UpperCase);
            Check(!runtime.MigrateFromOrdinary(ordinary, initial) && ordinary.Raw() == u"A" && !runtime.Snapshot());
            ordinary.ClearComposition(); runtime.Refresh(initial);
            auto historyBefore = ordinary.PostProcessor().History().VisibleCount();
            auto key = [&](int vk, bool ctrl = false, bool shift = false, std::u16string action = u"down")
            {
                InputKeyEvent event; event.vk = vk; event.ctrl = ctrl; event.shift = shift; event.action = std::move(action);
                return runtime.TryProcessEditingKey(event, ordinary);
            };
            Check(!key(8) && !key(0x31) && !key(0x41, false, true)); // idle routes remain outside
            auto first = key(0x41); Check(first && first->handled && first->composing && first->inputBuffer == u"a");
            Check(!key(0x31, true) && !key(0x41, false, false, u"up"));
            Check(runtime.Input().ExportUncommittedRaw() == u"a");
            Check(key(0x41).has_value());
            auto committed = key(0x20);
            Check(committed && committed->text == u"a" && !committed->composing && committed->inputBuffer.empty());
            Check(ordinary.PostProcessor().Output().RepeatBuffer() == u"a");
            Check(ordinary.PostProcessor().History().VisibleCount() == historyBefore + 1);
            Check(!key(0x20));
            runtime.Input().ImportRaw(u"aa", 4);
            auto comma = key(0xbc); Check(comma && comma->text == u"a\uff0c" && !comma->composing);
            runtime.Input().ImportRaw(u"aa", 5);
            Check(key(0xba)->composing && runtime.Input().ExportUncommittedRaw() == u"aa;");
            runtime.Input().ImportRaw(u"aa", 6);
            Check(key(0xde)->composing && runtime.Input().ExportUncommittedRaw() == u"aa'");
            auto punctuationConfig = std::make_shared<RuntimeLexiconSnapshot>(*initial);
            punctuationConfig->config = {
                {u"\u5206\u53f7\u6b21\u9009", u"false"}, {u"\u5f15\u53f7\u4e09\u9009", u"false"},
                {u"\u4e2d\u6587\u72b6\u6001\u4e0b\u4f7f\u7528\u82f1\u6587\u6807\u70b9", u"true"}};
            runtime.Refresh(punctuationConfig);
            runtime.Input().ImportRaw(u"aa", 7);
            Check(key(0xba)->text == u"a;");
            runtime.Input().ImportRaw(u"aa", 8);
            Check(key(0xde)->text == u"a\u2018");
            runtime.Input().ImportRaw(u"aa", 9);
            Check(key(0xde)->text == u"a\u2019"); // shared alternating smart quote state
            runtime.Input().ImportRaw(u"aa", 10);
            Check(key(0x31, false, true)->text == u"a!");
            runtime.Input().ImportRaw(u"zz", 11);
            Check(key(0xbc)->text == u"zz,"); // absent candidate falls back to live raw
            runtime.Input().ImportRaw(u"AbCD", 12);
            InputKeyEvent caps; caps.vk = 0x14; caps.capsLock = true;
            auto literal = runtime.TryProcessEditingKey(caps, ordinary);
            Check(literal && !literal->handled && literal->text == u"AbCD" && !literal->composing);
            Check(runtime.Input().ExportUncommittedRaw().empty() && ordinary.IsChinese());
            runtime.Input().ImportRaw(u"AA", 13);
            caps.vk = 0x41;
            Check(!runtime.TryProcessEditingKey(caps, ordinary) && runtime.Input().ExportUncommittedRaw() == u"AA");
            runtime.Input().Cancel();
            runtime.Input().ImportRaw(u"AbCD", 14);
            InputKeyEvent chord; chord.vk = 0xa2; chord.ctrl = true;
            Check(!runtime.CtrlSpaceKey(chord, ordinary, true, CtrlSpaceState::Time{}));
            chord.vk = 0x20;
            auto toggled = runtime.CtrlSpaceKey(chord, ordinary, true, CtrlSpaceState::Time{});
            Check(toggled && toggled->handled && toggled->text == u"AbCD" && !ordinary.IsChinese());
            Check(runtime.Input().ExportUncommittedRaw().empty() && runtime.Snapshot() == punctuationConfig);
            chord.repeat = 2;
            Check(!runtime.CtrlSpaceKey(chord, ordinary, true, CtrlSpaceState::Time{}));
            chord.action = u"up"; chord.ctrl = false; chord.vk = 0xa2;
            Check(!runtime.CtrlSpaceKey(chord, ordinary, true, CtrlSpaceState::Time{}));
            chord.vk = 0x20;
            Check(!runtime.CtrlSpaceKey(chord, ordinary, true, CtrlSpaceState::Time{}));
            ordinary.SetChinese(true);
            runtime.Input().ImportRaw(u"Bb", 15);
            InputKeyEvent shift; shift.vk = 0xa0; shift.shift = true;
            auto shiftDown = runtime.ShiftKey(shift, ordinary, true);
            Check(shiftDown && !shiftDown->handled && ordinary.IsChinese());
            shift.action = u"up"; shift.shift = false;
            auto shiftUp = runtime.ShiftKey(shift, ordinary, true);
            Check(shiftUp && shiftUp->handled && shiftUp->text == u"Bb" && !ordinary.IsChinese());
            Check(runtime.Input().ExportUncommittedRaw().empty() && runtime.Snapshot() == punctuationConfig);
            ordinary.SetChinese(true);
            runtime.Input().ImportRaw(u"AA", 16);
            runtime.Input().KeyDown(0x28);
            auto focusGeneration = runtime.Input().Session().Request().generation;
            Check(runtime.Input().Session().SelectedIndex() == 1);
            shift.action = u"down"; shift.shift = true;
            runtime.ShiftKey(shift, ordinary, true);
            runtime.FocusChanged(ordinary);
            Check(runtime.Input().ExportUncommittedRaw() == u"AA" && runtime.Input().Session().SelectedIndex() == 1);
            Check(runtime.Input().Session().Request().generation == focusGeneration);
            shift.action = u"up"; shift.shift = false;
            auto orphanRelease = runtime.ShiftKey(shift, ordinary, true);
            Check(orphanRelease && !orphanRelease->handled && ordinary.IsChinese());
            runtime.CancelComposition(ordinary);
            Check(runtime.Input().WaitIdle(std::chrono::seconds(2))); runtime.Input().Pump();
            Check(runtime.Input().ExportUncommittedRaw().empty() && runtime.Input().Session().Candidates().empty());
            Check(ordinary.IsChinese() && runtime.Snapshot() == punctuationConfig);
            for (int modifier : {0xa2, 0xa4, 0x5b})
            {
                runtime.Input().ImportRaw(u"AA", 17);
                InputKeyEvent modified; modified.vk = modifier;
                modified.ctrl = modifier == 0xa2; modified.alt = modifier == 0xa4; modified.win = modifier == 0x5b;
                auto bare = runtime.TryProcessModifiedKey(modified, ordinary);
                Check(bare && !bare->handled && !bare->cancelCompositionBeforePass);
                Check(runtime.Input().ExportUncommittedRaw() == u"AA");
                modified.vk = 0x31; modified.action = u"up";
                Check(!runtime.TryProcessModifiedKey(modified, ordinary));
                Check(runtime.Input().ExportUncommittedRaw() == u"AA");
                modified.action = u"down";
                auto passed = runtime.TryProcessModifiedKey(modified, ordinary);
                Check(passed && !passed->handled && passed->text.empty() && passed->cancelCompositionBeforePass);
                Check(runtime.Input().WaitIdle(std::chrono::seconds(2))); runtime.Input().Pump();
                Check(runtime.Input().ExportUncommittedRaw().empty() && runtime.Input().Session().Candidates().empty());
                Check(runtime.Snapshot() == punctuationConfig && ordinary.IsChinese());
                auto idlePass = runtime.TryProcessModifiedKey(modified, ordinary);
                Check(idlePass && !idlePass->cancelCompositionBeforePass);
            }
            InputKeyEvent quoteEvent; quoteEvent.vk = 0xde; quoteEvent.action = u"up";
            auto observeQuote = [&]
            {
                return runtime.QuoteKey(quoteEvent, ordinary, *initial->schema,
                    initial->schema->table, targetSettings);
            };
            runtime.Input().ImportRaw(u"AA", 18);
            runtime.Input().KeyDown(0x28);
            auto quoteGeneration = runtime.Input().Session().Request().generation;
            auto quoteHistory = ordinary.PostProcessor().History().VisibleCount();
            Check(!observeQuote()); // orphan quote release is not an idle quote in sentence mode
            Check(runtime.Input().ExportUncommittedRaw() == u"AA" && runtime.Input().Session().SelectedIndex() == 1);
            Check(runtime.Input().Session().Request().generation == quoteGeneration);
            Check(ordinary.PostProcessor().History().VisibleCount() == quoteHistory);
            quoteEvent.action = u"down"; Check(!observeQuote());
            auto quoteCommit = runtime.TryProcessEditingKey(quoteEvent, ordinary);
            Check(quoteCommit && quoteCommit->handled && !quoteCommit->text.empty());
            Check(runtime.Input().ExportUncommittedRaw().empty());
            quoteEvent.action = u"up"; Check(!observeQuote()); // no double quote after normal commit
            auto idleQuote = observeQuote();
            Check(idleQuote && idleQuote->handled && !idleQuote->text.empty()); // truly idle fallback retained
            runtime.Leave();
        }
        {
            auto snapshot = std::make_shared<RuntimeLexiconSnapshot>();
            snapshot->schemaName = u"test\u6574\u53e5";
            snapshot->config.emplace_back(u"\u6574\u53e5\u81ea\u52a8\u63d0\u524d\u4e0a\u5c4f", u"true");
            snapshot->schema = std::make_shared<const SchemaLexicon>(SchemaLexicon{
                CompactLexicon::Build(std::vector<CompactLexicon::Entry>{{u"aa", {u"X"}}, {u"aabz", {u"Y"}}}), {}, {}});
            RuntimeSentenceState runtime(modelFile);
            runtime.Refresh(snapshot); runtime.Input().ImportRaw(u"aa", 0);
            Check(runtime.Input().WaitIdle(std::chrono::seconds(2))); runtime.Input().Pump();
            Check(!runtime.Input().KeyDown(0x42).commit);
            Check(runtime.Input().KeyDown(0x43).commit == std::optional<std::u16string>(u"X"));
            auto appearance = std::make_shared<RuntimeLexiconSnapshot>(*snapshot);
            appearance->config.emplace_back(u"\u4e3b\u9898", u"different");
            runtime.Refresh(appearance);
            Check(runtime.Input().Session().Context().CommittedText() == u"X");
            Check(runtime.Input().ExportUncommittedRaw() == u"bc");
            ChineseInputSession ordinary(MakeUpperCaseServices([](std::u16string_view) {}));
            auto previousRepeat = ordinary.PostProcessor().Output().RepeatBuffer();
            runtime.MigrateToOrdinary(ordinary, LoadOrdinarySettings({}, 1));
            Check(!runtime.Snapshot() && ordinary.Raw() == u"bc");
            Check(ordinary.PostProcessor().Output().RepeatBuffer() == previousRepeat); // migration does not commit
            runtime.Refresh(snapshot);
            Check(runtime.Input().ExportUncommittedRaw().empty());
        }
        {
            auto snapshot = std::make_shared<RuntimeLexiconSnapshot>();
            snapshot->schemaName = u"test\u6574\u53e5";
            snapshot->schema = std::make_shared<const SchemaLexicon>(sentenceSchema);
            RuntimeSentenceState runtime(modelFile); runtime.Refresh(snapshot);
            ChineseInputSession outer(MakeUpperCaseServices([](std::u16string_view) {}));
            auto ordinarySettings = LoadOrdinarySettings({}, 1);
            auto bindings = LoadActionBindings({});
            bindings.recentSchema = ShortcutGesture::Parse(u"Ctrl+VK_M");
            int switched = 0, adjusted = 0;
            auto send = [&](int vk, std::u16string action = u"down", bool ctrl = false, bool shift = false)
            {
                InputKeyEvent event; event.vk = vk; event.action = std::move(action); event.ctrl = ctrl; event.shift = shift;
                return runtime.ProcessKey(event, outer, *snapshot->schema, snapshot->schema->table,
                    ordinarySettings, true, true, true, SelectionKeys{}, bindings,
                    [&] { ++switched; return true; },
                    [&](CandidateAdjustment, std::u16string_view, std::u16string_view) { ++adjusted; return true; },
                    CtrlSpaceState::Time{});
            };
            Check(send(0x41).composing); Check(!send(0x41, u"up").handled);
            Check(send(0x41).composing); Check(!send(0x41, u"up").handled);
            Check(outer.Raw().empty() && runtime.Input().ExportUncommittedRaw() == u"aa");
            Check(send(0x28).handled && runtime.Input().Session().SelectedIndex() == 1);
            Check(!send(0x70).handled && runtime.Input().ExportUncommittedRaw() == u"aa");
            Check(send(0x20).text == u"b");
            Check(!send(0x20, u"up").handled && runtime.Input().ExportUncommittedRaw().empty());
            send(0x41); send(0x41);
            auto generation = runtime.Input().Session().Request().generation;
            Check(!send(0xde, u"up").handled);
            Check(runtime.Input().Session().Request().generation == generation);
            auto add = send(0xbb, u"down", true);
            Check(add.handled && add.action == OutputAction::OpenAddWord);
            Check(runtime.Input().ExportUncommittedRaw() == u"aa");
            Check(send(0xbb, u"down", true).action != OutputAction::OpenAddWord);
            send(0xbb, u"up", true);
            auto recent = send(0x4d, u"down", true);
            Check(recent.handled && recent.composing && !recent.inputBuffer.empty() && switched == 1);
            Check(send(0x4d, u"down", true).composing && switched == 1);
            send(0x4d, u"up", true); send(0xa2, u"up");
            auto ctrlDigit = send(0x31, u"down", true);
            Check(!ctrlDigit.handled && ctrlDigit.cancelCompositionBeforePass && adjusted == 0);
            Check(runtime.Input().ExportUncommittedRaw().empty());
            runtime.Input().ImportRaw(u"AA", 2);
            send(0xa2, u"down", true);
            auto toggle = send(0x20, u"down", true);
            Check(toggle.handled && toggle.text == u"AA" && !outer.IsChinese());
            send(0x20, u"up", true); send(0xa2, u"up");
            Check(!send(0x41).handled && runtime.Input().ExportUncommittedRaw().empty());
            outer.SetChinese(true);
            Check(send(0x41, u"down", false, true).composing && outer.Mode() == ChineseMode::UpperCase);
            Check(runtime.Input().ExportUncommittedRaw().empty());
            send(0x1b);
            Check(send(0xc0).composing && outer.Mode() == ChineseMode::Pinyin);
            Check(send(0x41).composing && runtime.Input().ExportUncommittedRaw().empty());
            runtime.CancelComposition(outer);
        }
        {
            auto settings = LoadSentenceSettings({{u"\u5141\u8bb8\u5355\u5b57\u91cd\u7801\u7ec4\u53e5", u"false"}});
            RuntimeSentenceDecoder firstRanks(sentenceSchema, modelFile, settings);
            Check(!firstRanks.HasCompleteCandidate(u"aabb", {}, std::u16string_view(u"ab"), false));
            RuntimeSentenceDecoder duplicates(sentenceSchema, modelFile, LoadSentenceSettings({}));
            Check(duplicates.HasCompleteCandidate(u"aabb", {}, std::u16string_view(u"ab"), false));
        }
        {
            RuntimeSentenceDecoder decoder(sentenceSchema, modelFile, sentenceSettings);
            auto decoded = decoder.DecodeFull(u"aabb");
            Check(!decoded.candidates.empty() && decoded.candidates[0].text == u"ab");
            Check(decoded.candidates[0].supplementScore == 9);
            Check(std::abs(decoded.candidates[0].score - decoded.candidates[0].logMass - 9) < 1e-12);
            auto whole = decoder.DecodeFull(u"aa");
            Check(whole.candidates.size() == 2 && whole.candidates[1].maxRank == 2); // commit-text dedup reassigns rank
            auto cachedDecode = decoder.Decode(u"aaaaaa");
            Check(decoder.Decode(u" AA AA AA ") == cachedDecode);
            auto appendedDecode = decoder.Decode(u"aaaaaabb");
            Check(cachedDecode->raw == u"aaaaaa" && appendedDecode->raw == u"aaaaaabb");
            Check(appendedDecode->candidates[0].text == decoder.DecodeFull(u"aaaaaabb").candidates[0].text);
            auto deletedDecode = decoder.Decode(u"aaaaaa");
            Check(deletedDecode->expanded == 0);
            decoder.ResetCache();
            Check(decoder.Decode(u"aaaaaa") != deletedDecode);
            auto withEvidence = decoder.DecodeResult(u"aaaaaab", true);
            Check(withEvidence->evidence.mergedIncompleteTail && withEvidence->evidence.neutralIncompleteTail);
            Check(!withEvidence->evidence.prefixes.empty());
            Check(decoder.DecodeResult(u"AAAAAAB", true) == withEvidence);
            auto impossiblePrefix = decoder.DecodeResult(u"aaaaaab", true, u"unavailable");
            Check(impossiblePrefix != withEvidence && impossiblePrefix->evidence.prefixes.empty());
            Check(impossiblePrefix->lattice->expanded == 0 && !withEvidence->evidence.prefixes.empty());
            auto noEvidence = decoder.DecodeResult(u"aaaaaab");
            Check(noEvidence->evidence.prefixes.empty() && noEvidence->lattice->expanded == 0);
            auto evidenceAgain = decoder.DecodeResult(u"aaaaaab", true);
            Check(evidenceAgain->evidence.prefixes.size() == withEvidence->evidence.prefixes.size());
            decoder.ResetCache();
            Check(decoder.DecodeResult(u"aaaaaab", true) != evidenceAgain);
            sentenceSchema.table = CompactLexicon::Build({}); // owner is independent of source schema storage
            std::atomic<bool> consistent{true};
            std::vector<std::thread> workers;
            for (int i = 0; i < 4; ++i) workers.emplace_back([&]
            {
                try
                {
                    for (int j = 0; j < 20; ++j)
                    {
                        auto result = decoder.DecodeFull(u"aabb");
                        if (result.candidates.empty() || result.candidates[0].text != u"ab" ||
                            result.candidates[0].score != decoded.candidates[0].score) consistent = false;
                    }
                }
                catch (...) { consistent = false; }
            });
            for (auto& worker : workers) worker.join();
            Check(consistent);
            {
                SentenceDecodeWorker worker([&](auto raw) { return decoder.Decode(raw); });
                auto generation = worker.Submit(u"aabb");
                Check(worker.WaitIdle(std::chrono::seconds(2)));
                auto ready = worker.TakeCompleted();
                Check(ready && ready->generation == generation && !ready->error &&
                    ready->result->candidates[0].text == u"ab");
            } // worker joins before decoder/mapping destruction
            {
                BasicSentenceDecodeWorker<RuntimeSentenceResult> worker([&](auto raw) { return decoder.DecodeResult(raw, true); });
                auto generation = worker.Submit(u"aaaaaab");
                Check(worker.WaitIdle(std::chrono::seconds(2)));
                auto ready = worker.TakeCompleted();
                Check(ready && ready->generation == generation && !ready->error &&
                    ready->result->lattice->raw == u"aaaaaab" && ready->result->evidence.mergedIncompleteTail);
                Check(!worker.TakeCompleted());
            }
            {
                SentenceCompositionSession session;
                for (auto key : std::u16string_view(u"AABB")) session.Append(key);
                BasicSentenceDecodeWorker<RuntimeSentenceResult, SentenceCompositionRequest> worker(
                    [&](const SentenceCompositionRequest& request)
                    {
                        return decoder.DecodeResult(request.raw, true, request.requiredPrefix);
                    });
                worker.Submit(session.Request());
                Check(worker.WaitIdle(std::chrono::seconds(2)));
                auto ready = worker.TakeCompleted();
                Check(ready && !ready->error && ready->raw.raw == u"AABB");
                Check(session.Apply(ready->raw, *ready->result->lattice, ready->result->evidence));
                Check(session.Candidates()[0].text == u"ab");
                Check(!session.Apply(ready->raw, *ready->result->lattice, ready->result->evidence));
                // A completion already taken from the worker can still become
                // stale before host application; the session is the final guard.
                session.Append(u'A'); worker.Submit(session.Request());
                Check(worker.WaitIdle(std::chrono::seconds(2)));
                auto stale = worker.TakeCompleted(); Check(stale && !stale->error);
                session.Append(u'A');
                Check(!session.Apply(stale->raw, *stale->result->lattice, stale->result->evidence));
                worker.Submit(session.Request());
                Check(worker.WaitIdle(std::chrono::seconds(2)));
                auto latest = worker.TakeCompleted(); Check(latest && !latest->error);
                Check(session.Apply(latest->raw, *latest->result->lattice, latest->result->evidence));
                Check(session.Context().Raw() == u"AABBAA");
            }
            {
                RuntimeSentenceInput input(decoder);
                for (int vk : {0x41, 0x41, 0x42, 0x42}) Check(input.KeyDown(vk).handled);
                // Space must synchronize with the pending decode, not consume
                // whichever shorter candidate happened to finish first.
                auto space = input.KeyDown(0x20);
                Check(space.commit == std::optional<std::u16string>(u"ab"));
                Check(input.Session().Context().Raw().empty());
                Check(input.KeyDown(0x41).handled && input.KeyDown(0x41).handled);
                Check(input.KeyDown(0x09).handled && input.Session().SelectedIndex() == 1);
                Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                Check(input.Session().SelectedIndex() == 1); // late duplicate must not reset navigation
                Check(input.KeyDown(0x20).commit == std::optional<std::u16string>(u"b"));
                Check(input.KeyDown(0x41).handled && input.KeyDown(0x41).handled);
                Check(input.FinishWithPunctuation(u"!") == u"a!");
                Check(input.KeyDown(0x41, true).handled && input.Session().Context().Raw() == u"a");
                Check(input.KeyDown(0x62).handled && input.Session().Context().Raw() == u"a2");
                Check(input.KeyDown(0xba).handled && input.KeyDown(0xde).handled);
                Check(input.Session().Context().Raw() == u"a2;'" );
                Check(!input.KeyDown(0x31, true).handled);
                input.Cancel(); Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                Check(input.Session().Context().Raw().empty() && input.Session().Candidates().empty());
                input.ImportRaw(u"AA", 19);
                Check(input.ExportUncommittedRaw() == u"AA");
                Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                Check(input.Session().Request().lexiconVersion == 19 && !input.Session().Candidates().empty());
                input.ImportRaw(u"Bb", 20); // pending old work cannot restore AA
                Check(input.KeyDown(0x0d).commit == std::optional<std::u16string>(u"Bb"));
                Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                Check(input.ExportUncommittedRaw().empty());
            }
            {
                std::binary_semaphore scoring(0), finishScoring(0);
                std::vector<std::u16string> submitted;
                RuntimeSentenceInput input(decoder, {}, [&](const SentenceNeuralRequest& request)
                {
                    if (request.composition.raw == u"aaaa")
                    {
                        submitted = request.candidates;
                        scoring.release(); finishScoring.acquire();
                    }
                    std::vector<double> scores(request.candidates.size(), -10000);
                    if (scores.size() > 1) scores[1] = 10000;
                    return scores;
                });
                for (int round = 0; round < 2; ++round)
                {
                    for (int i = 0; i < 4; ++i) Check(input.KeyDown(0x41).handled);
                    Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                    bool started = scoring.try_acquire_for(std::chrono::seconds(2));
                    if (!started) finishScoring.release();
                    Check(started);
                    bool enoughCandidates = submitted.size() > 1;
                    if (round == 1) input.KeyDown(0x28); // selection freezes the pending score result
                    finishScoring.release();
                    Check(input.WaitNeuralIdle(std::chrono::seconds(2))); input.Pump();
                    Check(enoughCandidates);
                    if (round == 0) Check(input.Session().Candidates()[0].text == submitted[1]);
                    else Check(input.Session().SelectedIndex() == 1 && input.Session().Candidates()[0].text == submitted[0]);
                    input.Cancel();
                }
            }
            {
                std::binary_semaphore scoring(0), finishScoring(0);
                std::vector<std::u16string> submitted;
                int loads = 0, releases = 0;
                SentenceServiceLifecycle service([&](std::stop_token) { ++loads; }, [&] { ++releases; },
                    [&](const SentenceNeuralRequest& request, std::stop_token stop)
                    {
                        if (request.composition.raw == u"aaaa")
                        {
                            submitted = request.candidates;
                            std::stop_callback cancel(stop, [&] { finishScoring.release(); });
                            scoring.release(); finishScoring.acquire();
                        }
                        std::vector<double> scores(request.candidates.size(), -10000);
                        if (scores.size() > 1) scores[1] = 10000;
                        return scores;
                    });
                service.SetEnabled(true);
                RuntimeSentenceInput input(decoder, {}, service);
                for (int round = 0; round < 3; ++round)
                {
                    for (int i = 0; i < 4; ++i) input.KeyDown(0x41);
                    Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                    Check(scoring.try_acquire_for(std::chrono::seconds(2)));
                    Check(submitted.size() > 1);
                    if (round == 2) input.Cancel(); // cancellation wakes scorer; no residency release
                    else
                    {
                        if (round == 1) input.KeyDown(0x28);
                        finishScoring.release();
                    }
                    Check(input.WaitNeuralIdle(std::chrono::seconds(2))); input.Pump();
                    if (round == 0) Check(input.Session().Candidates()[0].text == submitted[1]);
                    else if (round == 1)
                        Check(input.Session().SelectedIndex() == 1 && input.Session().Candidates()[0].text == submitted[0]);
                    else Check(input.Session().Context().Raw().empty() && input.Session().Candidates().empty());
                    Check(loads == 1 && releases == 0);
                    input.Cancel();
                }
            }
            {
                int calls = 0;
                std::binary_semaphore secondScore(0), finishSecond(0);
                SentenceServiceLifecycle service([](std::stop_token) {}, [] {},
                    [&](const SentenceNeuralRequest& request, std::stop_token token)
                    {
                        if (++calls == 2)
                        {
                            std::stop_callback cancel(token, [&] { finishSecond.release(); });
                            secondScore.release(); finishSecond.acquire();
                        }
                        return std::vector<double>(request.candidates.size(), -1);
                    });
                RuntimeSentenceInput input(decoder, {}, service);
                input.ImportRaw(u"aa", 1);
                Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                Check(service.WaitIdle(std::chrono::seconds(2)) && calls == 0);
                service.SetEnabled(true); input.Pump();
                Check(service.WaitIdle(std::chrono::seconds(2)) && calls == 1);
                input.Pump(); input.Pump();
                Check(service.WaitIdle(std::chrono::seconds(2)) && calls == 1);
                input.ImportRaw(u"aa", 2);
                Check(input.WaitIdle(std::chrono::seconds(2))); input.Pump();
                Check(secondScore.try_acquire_for(std::chrono::seconds(2))); finishSecond.release();
                Check(service.WaitIdle(std::chrono::seconds(2)) && calls == 2);
                // Discard the completed score before input consumes it; a new
                // lifecycle must retry the same composition without an edit.
                service.SetEnabled(false); service.SetEnabled(true); input.Pump();
                Check(service.WaitIdle(std::chrono::seconds(2)) && calls == 3);
                input.Pump(); input.Pump();
                Check(service.WaitIdle(std::chrono::seconds(2)) && calls == 3);
            }
            {
                RuntimeSentenceInput defaults(decoder, LoadSentenceInputSettings({}));
                defaults.ImportRaw(u"zz", 0);
                Check(defaults.KeyDown(0x09).handled && defaults.ExportUncommittedRaw().empty());
                RuntimeSentenceInput configured(decoder, LoadSentenceInputSettings({
                    {u"TAB\u6e05\u5c4f", u"false"}, {u"\u56de\u8f66\u6e05\u5c4f", u"true"},
                    {u"\u5206\u53f7\u6b21\u9009", u"false"}, {u"\u5f15\u53f7\u4e09\u9009", u"false"}}));
                configured.ImportRaw(u"zz", 0);
                Check(configured.KeyDown(0x09).handled && configured.ExportUncommittedRaw() == u"zz");
                Check(!configured.KeyDown(0xba).handled && !configured.KeyDown(0xde).handled);
                Check(configured.ExportUncommittedRaw() == u"zz");
                auto cleared = configured.KeyDown(0x0d);
                Check(cleared.handled && !cleared.commit && configured.ExportUncommittedRaw().empty());
            }
            {
                RuntimeSentenceInput failingNeural(decoder, {}, [](const SentenceNeuralRequest&) -> std::vector<double>
                { throw std::runtime_error("injected neural scorer failure"); });
                failingNeural.KeyDown(0x41); failingNeural.KeyDown(0x41);
                Check(failingNeural.WaitIdle(std::chrono::seconds(2))); failingNeural.Pump();
                Check(failingNeural.WaitNeuralIdle(std::chrono::seconds(2))); failingNeural.Pump();
                Check(failingNeural.LastNeuralError() && failingNeural.Session().Candidates()[0].text == u"a");
                Check(failingNeural.KeyDown(0x20).commit == std::optional<std::u16string>(u"a"));
            }
        }
        Write(modelFile, modelImage); // decoder destruction releases its owned mapping
        auto editDirectory = root / "edit-tables/Edit";
        std::filesystem::create_directories(editDirectory);
        Write(editDirectory / "Edit.txt", u8"a first\na second\na third\n");
        Write(editDirectory / u"\u8865\u5145\u8bed\u6599.txt", u8"first 1000\nsecond 2000\nfirst 3000\ninvalid 0\n");
        auto editConfig = root / "edit-config.txt";
        Write(editConfig, u8"\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e\tedit-tables\n\u5f53\u524d\u7801\u8868\tEdit\n");
        RuntimeLexicons edits(root);
        edits.Reload(editConfig);
        auto beforeEdit = edits.Read();
        Check(beforeEdit->schema->supplements->size() == 2);
        Check(beforeEdit->schema->supplements->at(0).text == u"first" && beforeEdit->schema->supplements->at(0).weight == 3000);
        Check(edits.AdjustCandidate(CandidateAdjustment::Top, u" A ", u"third"));
        Check(First(edits.Read()->schema->table, u"a") == u"third");
        Check(First(beforeEdit->schema->table, u"a") == u"first");
        Check(edits.Read()->pinyin == beforeEdit->pinyin);
        Check(edits.Read()->schema->supplements == beforeEdit->schema->supplements);
        auto editedGeneration = edits.Read()->generation;
        Check(!edits.AdjustCandidate(CandidateAdjustment::Top, u"a", u"third"));
        Check(edits.Read()->generation == editedGeneration);
        Check(edits.AdjustCandidate(CandidateAdjustment::Advance, u"a", u"second"));
        Check(edits.AdjustCandidate(CandidateAdjustment::Delete, u"a", u"third"));
        Check(First(edits.Read()->schema->table, u"a") == u"second");
        edits.Reload(editConfig);
        Check(First(edits.Read()->schema->table, u"a") == u"second");
        Check(edits.AdjustCandidate(CandidateAdjustment::Top, u"new", u"new text"));
        edits.Reload(editConfig);
        Check(First(edits.Read()->schema->table, u"new") == u"new text");
        Check(edits.AdjustCandidate(CandidateAdjustment::Delete, u"new", u"new text"));
        Check(edits.Read()->schema->table.CandidateCount(*edits.Read()->schema->table.Find(u"new")) == 0);
        auto adjustFile = editDirectory / u"\u7528\u6237\u8c03\u6574.txt";
        HANDLE lockedAdjust = CreateFileW(adjustFile.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Check(lockedAdjust != INVALID_HANDLE_VALUE);
        bool changedWithoutLog = edits.AdjustCandidate(CandidateAdjustment::Top, u"a", u"first");
        CloseHandle(lockedAdjust);
        Check(changedWithoutLog && First(edits.Read()->schema->table, u"a") == u"first");
        edits.Reload(editConfig);
        Check(First(edits.Read()->schema->table, u"a") == u"second"); // failed append was best effort
        ChineseInputSession editSession(MakeUpperCaseServices([](std::u16string_view) {}));
        OrdinarySettings editSettings;
        editSettings.maxCodeAutoCommit = false;
        auto editEvent = [&](int vk, bool ctrl)
        {
            auto snapshot = edits.Read();
            InputKeyEvent event; event.vk = vk; event.ctrl = ctrl;
            return editSession.ProcessKey(event, *snapshot->schema, *snapshot->pinyin, editSettings,
                true, true, true, snapshot->selectionKeys, snapshot->actionBindings, {},
                [&](CandidateAdjustment op, std::u16string_view code, std::u16string_view text)
                { return edits.AdjustCandidate(op, code, text); }, CtrlSpaceState::Time{});
        };
        Check(editEvent(65, false).composing);
        Check(editEvent(50, true).handled);
        Check(First(edits.Read()->schema->table, u"a") == u"first");
        edits.Reload(editConfig);
        Check(First(edits.Read()->schema->table, u"a") == u"first");
        for (auto name : {"A", "B", "C"}) std::filesystem::create_directories(root / "tables" / name);
        auto aFile = root / "tables/A/A.txt";
        auto bFile = root / "tables/B/B.txt";
        auto cFile = root / "tables/C/C.txt";
        Write(aFile, u8"aa alpha\n"); Write(bFile, u8"aa beta\n"); Write(cFile, u8"aa gamma\n");
        auto pinyinRoot = root / u"\u62fc\u97f3\u53cd\u67e5\u7801\u8868";
        std::filesystem::create_directory(pinyinRoot);
        auto pinyinFile = pinyinRoot / "pinyin.txt";
        Write(pinyinFile, u8"aa original\n");
        auto configFile = root / "config.txt";
        Write(configFile, u8"\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e\ttables\n\u5f53\u524d\u7801\u8868\tA\n");
        auto startupConfig = root / "first-run/nested/config.txt";
        RuntimeLexicons startup(root);
        startup.Initialize(startupConfig);
        Check(std::filesystem::is_regular_file(startupConfig));
        Check(ReadConfigFile(startupConfig) == ParseConfigLines({}));
        Check(startup.Read()->schema->table.Count() == 0); // default table root is absent
        Check(First(*startup.Read()->pinyin, u"aa") == u"original");
        auto initialBindings = startup.Read();
        RuntimeLexicons hostRuntime(root);
        hostRuntime.Reload(configFile);
        RuntimeInput host(hostRuntime, MakeUpperCaseServices([](std::u16string_view) {}));
        auto hostKey = [&](int vk, bool ctrl = false)
        {
            InputKeyEvent event; event.vk = vk; event.ctrl = ctrl;
            return host.Process(event, CtrlSpaceState::Time{});
        };
        Check(hostKey(65).composing && hostKey(65).composing);
        Check(host.Page().entries.front() == u"alpha");
        Check(hostRuntime.SwitchSchema(u"B"));
        Check(host.Page().entries.front() == u"beta" && host.Session().Raw() == u"aa");
        Check(hostKey(32).text == u"beta");
        hostKey(65); hostKey(65);
        host.FocusChanged();
        Check(host.Session().Raw() == u"aa");
        host.Cancel();
        Check(host.Session().Raw().empty());
        host.ImportRaw(u"AA");
        Check(host.Page().entries.front() == u"beta" && host.Session().Raw() == u"AA");
        Check(hostKey(13).text == u"AA");
        auto callbackRoot = root / "callback-tables";
        {
            auto integratedRoot = root / "integrated-tables";
            std::filesystem::create_directories(integratedRoot / "Plain");
            std::filesystem::create_directories(integratedRoot / u"test\u6574\u53e5");
            Write(integratedRoot / "Plain/Plain.txt", u8"aa plain\naa other\n");
            Write(integratedRoot / u"test\u6574\u53e5/table.txt", u8"aa a\naa b\nbb b\n");
            auto integratedConfig = root / "integrated-config.txt";
            Write(integratedConfig, u8"\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e\tintegrated-tables\n"
                u8"\u5f53\u524d\u7801\u8868\tPlain\n\u6700\u8fd1\u7801\u8868\u5bf9\tPlain|test\u6574\u53e5\n"
                u8"Ctrl+m\u5207\u6362\u6700\u8fd1\u7801\u8868\ttrue\n");
            RuntimeLexicons integratedTables(root); integratedTables.Reload(integratedConfig);
            RuntimeInput integrated(integratedTables, MakeUpperCaseServices([](std::u16string_view) {}), modelFile);
            auto event = [&](int vk, bool ctrl = false, bool up = false)
            {
                InputKeyEvent key; key.vk = vk; key.ctrl = ctrl; key.action = up ? u"up" : u"down";
                return integrated.Process(key, CtrlSpaceState::Time{});
            };
            auto switchMode = [&]
            {
                auto result = event(0x4d, true);
                event(0x4d, true, true); event(0xa2, false, true);
                return result;
            };
            integrated.ImportRaw(u"AA");
            Check(integrated.Page().entries.front() == u"plain");
            auto intoSentence = switchMode();
            Check(intoSentence.handled && intoSentence.composing && integrated.Raw() == u"AA");
            Check(integrated.Session().Raw().empty());
            Check(event(0x28).handled && integrated.SelectedCandidateIndex() == 1);
            Check(integrated.Page().entries.size() == 2 && integrated.Page().entries[1] == u"b");
            integrated.FocusChanged(); Check(integrated.Raw() == u"AA" && integrated.SelectedCandidateIndex() == 1);
            auto intoPlain = switchMode();
            Check(intoPlain.handled && intoPlain.composing && integrated.Raw() == u"AA");
            Check(integrated.SelectedCandidateIndex() == 0 && integrated.Page().entries.front() == u"plain");
            Check(event(0x20).text == u"plain");
            switchMode(); event(0x41); event(0x41);
            Check(event(0x20).text == u"a" && integrated.Raw().empty());
            integrated.ImportRaw(u"AA"); integrated.Cancel();
            Check(integrated.Raw().empty() && integrated.Page().entries.empty());
            Check(integratedTables.SwitchSchema(u"Plain")); integrated.Page();
            RuntimeInput missingModel(integratedTables, MakeUpperCaseServices([](std::u16string_view) {}), root / "missing-model.bin");
            missingModel.ImportRaw(u"AA");
            Check(integratedTables.SwitchSchema(u"test\u6574\u53e5"));
            bool rejected = false;
            try { missingModel.Page(); } catch (const std::exception&) { rejected = true; }
            Check(rejected && missingModel.Raw() == u"AA" && missingModel.Session().Raw() == u"AA");
            Check(integratedTables.SwitchSchema(u"Plain"));
            Check(missingModel.Page().entries.front() == u"plain");
        }
        std::filesystem::create_directories(callbackRoot / "Old");
        std::filesystem::create_directories(callbackRoot / "New");
        Write(callbackRoot / "Old/Old.txt", u8"aa old\naa alternate\n");
        Write(callbackRoot / "New/New.txt", u8"aa new\naa alternate\n");
        auto callbackConfig = root / "callback-config.txt";
        Write(callbackConfig, u8"\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e\tcallback-tables\n"
            u8"\u5f53\u524d\u7801\u8868\tOld\n\u6700\u8fd1\u7801\u8868\u5bf9\tOld|New\n"
            u8"Ctrl+m\u5207\u6362\u6700\u8fd1\u7801\u8868\ttrue\n"
            u8"\u4e2d\u82f1\u6587\u4e0d\u9650\u957f\u6df7\u5408\u8f93\u5165\ttrue\n\u6700\u5927\u7801\u957f\t2\n");
        RuntimeLexicons callbackRuntime(root);
        callbackRuntime.Reload(callbackConfig);
        RuntimeInput callbackHost(callbackRuntime, MakeUpperCaseServices([](std::u16string_view) {}));
        auto callbackKey = [&](int vk, bool ctrl = false, bool up = false)
        {
            InputKeyEvent event; event.vk = vk; event.ctrl = ctrl; event.action = up ? u"up" : u"down";
            return callbackHost.Process(event, CtrlSpaceState::Time{});
        };
        for (int i = 0; i < 4; ++i) callbackKey(65);
        Check(callbackHost.Session().Surface() == u"oldaa");
        auto switched = callbackKey(77, true);
        Check(switched.handled && switched.inputBuffer == u"aa" && switched.action == OutputAction::Text);
        Check(callbackRuntime.Read()->schemaName == u"New" && callbackHost.Session().Raw() == u"aaaa");
        Check(callbackHost.Session().Surface() == u"newaa");
        Check(callbackKey(77, true).handled && callbackRuntime.Read()->schemaName == u"New"); // held M is one-shot
        callbackKey(77, true, true); callbackKey(17, false, true);
        Check(callbackKey(32).text == u"newnew");
        for (int i = 0; i < 4; ++i) callbackKey(65);
        Check(callbackKey(50, true).handled); // real runtime Ctrl+2 top callback
        Check(First(callbackRuntime.Read()->schema->table, u"aa") == u"alternate");
        Check(callbackHost.Session().Raw() == u"aaaa");
        // The completed segment retained its explicit page-first preference;
        // only the active segment reflects the new table rank.
        Check(callbackKey(32).text == u"newalternate");
        callbackRuntime.Reload(callbackConfig);
        Check(callbackRuntime.SwitchSchema(u"New"));
        Check(First(callbackRuntime.Read()->schema->table, u"aa") == u"alternate"); // adjustment persisted
        callbackHost.Cancel();
        for (int i = 0; i < 4; ++i) callbackKey(65);
        auto beforeFailedMigration = callbackHost.Session().Surface();
        std::filesystem::create_directories(callbackRoot / "Macro");
        Write(callbackRoot / "Macro/Macro.txt", u8"aa {\u65e5\u671f-}\n");
        Check(callbackRuntime.SwitchSchema(u"Macro"));
        for (int attempt = 0; attempt < 2; ++attempt)
        {
            bool failed = false;
            try { callbackHost.Page(); }
            catch (const std::bad_function_call&) { failed = true; }
            Check(failed && callbackHost.Session().Raw() == u"aaaa");
            Check(callbackHost.Session().Surface() == beforeFailedMigration);
        }
        OutputContext recoveryContext;
        recoveryContext.clock = [] { return OutputClock{2026, 9, 9, 12, 0, 0, 3, u"Wednesday"}; };
        InputKeyEvent recoveryEvent; recoveryEvent.action = u"up";
        callbackHost.Process(recoveryEvent, CtrlSpaceState::Time{}, recoveryContext);
        Check(callbackHost.Session().Raw() == u"aaaa" && callbackHost.Session().Surface() == u"2026-09-09aa");
        Check(initialBindings->actionBindings.addWord->ConfigString() == u"Ctrl+VK_OEM_PLUS");
        Check(!initialBindings->actionBindings.recentSchema);
        auto bindingConfig = root / "bindings-config.txt";
        auto bindingValues = ParseConfigLines({});
        for (auto& [key, value] : bindingValues)
        {
            if (key == u"\u624b\u52a8\u52a0\u8bcd\u5feb\u6377\u952e") value = u"Alt+VK_A";
            if (key == u"Ctrl+m\u5207\u6362\u6700\u8fd1\u7801\u8868") value = u"true";
        }
        WriteConfigFile(bindingConfig, bindingValues);
        RuntimeLexicons bindingsRuntime(root);
        bindingsRuntime.Reload(bindingConfig);
        auto firstBindings = bindingsRuntime.Read();
        Check(firstBindings->actionBindings.addWord->ConfigString() == u"Alt+VK_A");
        Check(firstBindings->actionBindings.recentSchema->ConfigString() == u"Ctrl+VK_M");
        for (auto& [key, value] : bindingValues)
            if (key == u"\u624b\u52a8\u52a0\u8bcd\u5feb\u6377\u952e") value = u"Ctrl+VK_M";
        WriteConfigFile(bindingConfig, bindingValues);
        bindingsRuntime.Reload(bindingConfig);
        Check(!bindingsRuntime.Read()->actionBindings.addWord && !bindingsRuntime.Read()->actionBindings.recentSchema);
        Check(firstBindings->actionBindings.addWord->ConfigString() == u"Alt+VK_A"); // old reader immutable
        auto selectionFile = root / u"\u81ea\u5b9a\u4e49\u9009\u91cd\u952e.txt";
        startup.ReloadSelectionBindings();
        Check(!std::filesystem::exists(selectionFile)); // read-only loader
        Check(startup.Read()->selectionBindings == DefaultSelectionBindings());
        Write(selectionFile, u8"\ufeff" u8"1\u9009 VK_A\r\n2\u9009 VK_A VK_B\r\n3\u9009\r\n");
        startup.ReloadSelectionBindings();
        auto customSelection = startup.Read();
        Check(customSelection->selectionKeys.Number(0x41) == 1);
        Check(customSelection->selectionKeys.Number(0x42) == 2);
        Check(!customSelection->selectionKeys.Number(0x33));
        startup.Reload(startupConfig);
        Check(startup.Read()->selectionBindings == customSelection->selectionBindings);
        Write(selectionFile, u8"1\u9009 VK_Z\n2\u9009 INVALID\n");
        auto beforeSelectionReload = startup.Read();
        startup.ReloadSelectionBindings();
        Check(startup.Read()->selectionBindings == DefaultSelectionBindings());
        Check(startup.Read()->schema == beforeSelectionReload->schema);
        Check(startup.Read()->pinyin == beforeSelectionReload->pinyin);
        Check(startup.Read()->generation == beforeSelectionReload->generation + 1);
        Check(customSelection->selectionKeys.Number(0x41) == 1); // old reader remains valid
        HANDLE selectionLock = CreateFileW(selectionFile.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        Check(selectionLock != INVALID_HANDLE_VALUE);
        auto unreadableSelection = ReadSelectionBindingsFile(selectionFile);
        CloseHandle(selectionLock);
        Check(unreadableSelection == DefaultSelectionBindings());
        // File.ReadAllLines(UTF8) detects UTF-32 LE; the main lexicon's legacy
        // pre-detection intentionally does not. Keep these policies separate.
        std::u8string utf32 = {char8_t(0xff), char8_t(0xfe), char8_t(0), char8_t(0)};
        for (char32_t cp : std::u32string_view(U"1\u9009 VK_Z\n"))
            for (int shift = 0; shift < 32; shift += 8)
                utf32.push_back(static_cast<char8_t>((cp >> shift) & 255));
        Write(selectionFile, utf32);
        startup.ReloadSelectionBindings();
        Check(startup.Read()->selectionKeys.Number(0x5a) == 1);
        auto utf32Size = std::filesystem::file_size(selectionFile);
        EnsureSelectionBindingsFile(selectionFile);
        Check(std::filesystem::file_size(selectionFile) == utf32Size);
        auto defaultSelectionFile = root / "default-selection.txt";
        EnsureSelectionBindingsFile(defaultSelectionFile);
        Check(ReadSelectionBindingsFile(defaultSelectionFile) == DefaultSelectionBindings());
        std::ifstream selectionBytes(defaultSelectionFile, std::ios::binary);
        std::vector<std::uint8_t> actualSelectionBytes{std::istreambuf_iterator<char>(selectionBytes), {}};
        selectionBytes.close();
        Check(actualSelectionBytes == EncodeUtf8Text(BuildSelectionFileText(DefaultSelectionBindings()), true));
        Check(actualSelectionBytes.size() > 3 && actualSelectionBytes[0] == 0xef && actualSelectionBytes[1] == 0xbb && actualSelectionBytes[2] == 0xbf);
        auto beforeInvalidSave = startup.Read();
        std::vector<std::u16string> selectionEdits{u"1\u9009 VK_B", u"2\u9009 INVALID"};
        Check(!startup.SaveSelectionBindings(selectionEdits).success);
        Check(startup.Read() == beforeInvalidSave && std::filesystem::file_size(selectionFile) == utf32Size);
        selectionEdits = {u"1\u9009 VK_B", u"2\u9009"};
        selectionLock = CreateFileW(selectionFile.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        Check(selectionLock != INVALID_HANDLE_VALUE);
        bool selectionSaveFailed = false;
        try { startup.SaveSelectionBindings(selectionEdits); } catch (...) { selectionSaveFailed = true; }
        CloseHandle(selectionLock);
        Check(selectionSaveFailed && startup.Read() == beforeInvalidSave);
        Check(startup.SaveSelectionBindings(selectionEdits).success);
        Check(startup.Read()->selectionKeys.Number(0x42) == 1);
        Check(!startup.Read()->selectionKeys.Number(0x32));
        Check(startup.Read()->schema == beforeInvalidSave->schema && startup.Read()->pinyin == beforeInvalidSave->pinyin);
        Check(ReadSelectionBindingsFile(selectionFile) == startup.Read()->selectionBindings);
        Check(beforeInvalidSave->selectionKeys.Number(0x5a) == 1);
        const std::u8string retainedPayload = u8"# retain this comment\n\u5f53\u524d\u7801\u8868\tcustom\n";
        Write(startupConfig, retainedPayload);
        auto retainedSize = std::filesystem::file_size(startupConfig);
        EnsureConfigFile(startupConfig);
        Check(std::filesystem::file_size(startupConfig) == retainedSize);
        std::ifstream retained(startupConfig, std::ios::binary);
        std::string retainedText((std::istreambuf_iterator<char>(retained)), {});
        retained.close();
        Check(retainedText == std::string(reinterpret_cast<const char*>(retainedPayload.data()), retainedPayload.size()));
        auto startupBeforeFailure = startup.Read();
        bool startupFailed = false;
        try { startup.Initialize(root / "first-run"); } catch (...) { startupFailed = true; }
        Check(startupFailed && startup.Read() == startupBeforeFailure);
        Write(root / "parent-is-file", u8"retain");
        startupFailed = false;
        try { startup.Initialize(root / "parent-is-file/config.txt"); } catch (...) { startupFailed = true; }
        Check(startupFailed && startup.Read() == startupBeforeFailure);
        RuntimeLexicons runtime(root);
        Check(!runtime.Read() && !runtime.SwitchSchema(u"A"));
        runtime.Reload(configFile);
        auto initial = runtime.Read();
        Check(initial->generation == 1 && initial->schemaName == u"A");
        Check(First(initial->schema->table, u"aa") == u"alpha");
        Check(First(*initial->pinyin, u"aa") == u"original");
        auto retainedPage = runtime.GetCandidatePage(u" AA ", 0, 5);
        Check(retainedPage.entries == std::vector<std::u16string>{u"alpha"});
        Check(runtime.GetCandidatePage(u"AA", 0, 5, true).entries == std::vector<std::u16string>{u"original"});
        Write(aFile, u8"aa updated\n"); Write(pinyinFile, u8"aa changed\n");
        Check(runtime.SwitchSchema(u"b"));
        auto b = runtime.Read();
        Check(b->schemaName == u"B" && b->pinyin == initial->pinyin);
        Check(runtime.GetCandidatePage(u"aa", 0, 5).entries == std::vector<std::u16string>{u"beta"});
        Check(retainedPage.entries == std::vector<std::u16string>{u"alpha"});
        Check(runtime.SwitchSchema(u"a"));
        Check(runtime.Read()->schema == initial->schema); // cached A, not edited disk A
        Check(!runtime.SwitchSchema(u"A") && !runtime.SwitchSchema(u"missing"));
        auto beforeFailure = runtime.Read();
        bool failed = false;
        try { runtime.Reload(root / "absent-config.txt"); } catch (...) { failed = true; }
        Check(failed && runtime.Read() == beforeFailure);
        HANDLE locked = CreateFileW(cFile.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Check(locked != INVALID_HANDLE_VALUE);
        failed = false;
        try { runtime.SwitchSchema(u"C"); } catch (...) { failed = true; }
        CloseHandle(locked);
        Check(failed && runtime.Read() == beforeFailure);
        locked = CreateFileW(pinyinFile.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Check(locked != INVALID_HANDLE_VALUE);
        failed = false;
        try { runtime.Reload(configFile); } catch (...) { failed = true; }
        CloseHandle(locked);
        Check(failed && runtime.Read() == beforeFailure); // main load succeeded, pinyin load failed
        Check(runtime.SwitchSchema(u"B"));
        Check(runtime.Read()->schema == b->schema); // failed switch did not destroy cache
        Check(runtime.SwitchSchema(u"C") && runtime.SwitchSchema(u"A"));
        Check(First(runtime.Read()->schema->table, u"aa") == u"updated"); // A was evicted
        Check(runtime.Read()->pinyin == initial->pinyin);
        runtime.Reload(configFile);
        auto reloaded = runtime.Read();
        Check(reloaded->pinyin != initial->pinyin && First(*reloaded->pinyin, u"aa") == u"changed");
        Check(First(*initial->pinyin, u"aa") == u"original"); // retained reader remains valid
        Check(First(initial->schema->table, u"aa") == u"alpha");
        std::atomic<bool> invalid = false;
        Check(runtime.SwitchRecentSchema());
        Check(runtime.Read()->schemaName == u"B");
        Check(runtime.SwitchRecentSchema());
        Check(runtime.Read()->schema == reloaded->schema && runtime.Read()->pinyin == reloaded->pinyin);
        Check(SerializeRecentSchemas(runtime.Read()->recentSchemas) == u"A|B");
        reloaded = runtime.Read();
        auto savedConfig = root / "saved-config.txt";
        runtime.SaveConfig(savedConfig);
        Check(ReadConfigFile(savedConfig) == runtime.Read()->config);
        auto bytes = SerializeConfig(runtime.Read()->config);
        std::ifstream saved(savedConfig, std::ios::binary);
        std::vector<std::uint8_t> disk((std::istreambuf_iterator<char>(saved)), {});
        saved.close();
        Check(disk == bytes);
        locked = CreateFileW(savedConfig.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Check(locked != INVALID_HANDLE_VALUE);
        failed = false;
        try { runtime.SaveConfig(savedConfig); } catch (...) { failed = true; }
        CloseHandle(locked);
        Check(failed && runtime.Read() == reloaded);
        Check(ReadConfigFile(savedConfig) == runtime.Read()->config);
        RuntimeLexicons restarted(root);
        restarted.Reload(savedConfig);
        Check(SerializeRecentSchemas(restarted.Read()->recentSchemas) == u"A|B");
        Check(restarted.SwitchRecentSchema() && restarted.Read()->schemaName == u"B");
        WriteConfigFile(savedConfig, {});
        Check(std::filesystem::file_size(savedConfig) == SerializeConfig({}).size());
        failed = false;
        try { runtime.SaveConfig(root / "missing-parent/config.txt"); } catch (...) { failed = true; }
        Check(failed && !std::filesystem::exists(root / "missing-parent"));
        std::latch entered(1);
        std::jthread reader([&](std::stop_token stop)
        {
            std::uint64_t last = 0;
            auto firstRead = runtime.Read();
            entered.count_down();
            try
            {
                Check(firstRead == reloaded);
                while (!stop.stop_requested())
                {
                    auto snapshot = runtime.Read();
                    auto word = First(snapshot->schema->table, u"aa");
                    if (snapshot->generation < last || !((snapshot->schemaName == u"A" && word == u"updated") ||
                        (snapshot->schemaName == u"B" && word == u"beta"))) invalid = true;
                    last = snapshot->generation;
                }
            }
            catch (...) { invalid = true; }
        });
        entered.wait();
        for (int index = 0; index < 100; ++index) Check(runtime.SwitchSchema(index % 2 == 0 ? u"B" : u"A"));
        reader.request_stop(); reader.join(); Check(!invalid);
        std::cout << "Runtime loading, cache ownership, failure preservation and concurrent snapshots passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
