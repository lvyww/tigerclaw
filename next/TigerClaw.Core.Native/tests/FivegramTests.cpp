#include "SentenceFivegramModel.h"
#include "SentenceLattice.h"
#include "SentenceEarlyEvidence.h"
#include "RuntimeSentenceDecoder.h"
#include "LexiconFile.h"
#include "RuntimeInput.h"
#include "OutputServices.h"
#include <fstream>
#include <iostream>
#include <iomanip>
#include <random>
#include <source_location>
#include <thread>

using namespace tiger::core;
namespace
{
    void Check(bool ok, std::source_location where = std::source_location::current())
    { if (!ok) throw std::runtime_error("Fivegram regression line " + std::to_string(where.line())); }
    void Near(double a, double b) { Check(std::abs(a - b) < 1e-10); }
    struct Fixture
    {
        std::filesystem::path root;
        Fixture()
        {
            for (int i = 0; i < 100; ++i)
            {
                root = std::filesystem::temp_directory_path() / ("core-fivegram-" + std::to_string(std::random_device{}()));
                if (std::filesystem::create_directory(root)) return;
            }
            throw std::runtime_error("Cannot create test directory");
        }
        ~Fixture() { std::error_code ignored; std::filesystem::remove_all(root, ignored); }
    };
    using Image = std::vector<std::uint8_t>;
    void Put(Image& data, std::size_t at, std::uint64_t value, unsigned width)
    { for (unsigned i = 0; i < width; ++i) data.at(at + i) = static_cast<std::uint8_t>(value >> (8 * i)); }
    void Add(Image& data, std::uint64_t value, unsigned width)
    { auto at = data.size(); data.resize(at + width); Put(data, at, value, width); }
    void Save(const std::filesystem::path& path, const Image& data)
    { std::ofstream out(path, std::ios::binary); out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size())); Check(bool(out)); }
    Image Model(int version)
    {
        Image data(256);
        std::copy_n("TCSKNM03", 8, data.begin());
        Put(data, 8, version, 4); Put(data, 12, 256, 4); Put(data, 24, 5, 4); Put(data, 28, 7, 4);
        Put(data, 32, 256, 4); Put(data, 36, 1, 4); Put(data, 40, 256, 8);
        Put(data, 56, 0, 2); Put(data, 58, 1, 2); Put(data, 60, 2, 2);
        unsigned q = version == 2 ? 1 : 2;
        // All orders: probability=-30+q*.001, backoff=0 for q=0,
        // otherwise -2+(q-1)*.001. Same numeric values in Q16 and Q8.
        for (int order = 0; order < 5; ++order)
        {
            auto at = 160 + order * 16;
            Put(data, at, std::uint32_t(-300000000), 4); Put(data, at + 4, version == 2 ? 1000000 : 1000000000, 4);
            Put(data, at + 8, std::uint32_t(-20000000), 4); Put(data, at + 12, version == 2 ? 1000000 : 1000000000, 4);
        }
        for (auto text : {u8"<unk>", u8"<s>", u8"</s>", u8"a", u8"b", u8"𠀀", u8"的人"})
        {
            auto bytes = std::u8string_view(text); Add(data, bytes.size(), 2);
            for (auto byte : bytes) data.push_back(static_cast<std::uint8_t>(byte));
            Add(data, 100, q); Add(data, 0, q);
        }
        Put(data, 48, data.size() - 256, 8);
        // Nested histories following BOS,a,b,a. Every backoff level has a
        // distinct observable successor; only order 5 has target b.
        for (int order = 2; order <= 5; ++order)
        {
            std::vector<int> context;
            int target, probability;
            if (order == 2) { context = {3}; target = 3; probability = 210; }
            else if (order == 3) { context = {4, 3}; target = 5; probability = 220; }
            else if (order == 4) { context = {3, 4, 3}; target = 6; probability = 230; }
            else { context = {1, 3, 4, 3}; target = 4; probability = 240; }
            auto directory = data.size(); data.resize(directory + 256 * 40);
            Put(data, 64 + (order - 2) * 24, directory, 8);
            Put(data, 72 + (order - 2) * 24, 1, 8); Put(data, 80 + (order - 2) * 24, 1, 8);
            auto block = data.size();
            for (int token : context) Add(data, token, 2);
            Add(data, 1, q); Add(data, 1, 2); Add(data, target, 2); Add(data, probability, q);
            auto index = data.size(); data.resize(index + 16);
            for (std::size_t i = 0; i < context.size(); ++i) Put(data, index + i * 2, context[i], 2);
            Put(data, index + 8, block, 8);
            auto meta = directory + context.front() * 40;
            Put(data, meta, block, 8); Put(data, meta + 8, index - block, 8); Put(data, meta + 16, index, 8);
            Put(data, meta + 24, 1, 4); Put(data, meta + 28, 1, 4); Put(data, meta + 32, 1, 8);
        }
        Put(data, 16, data.size(), 8); return data;
    }
    void HistoryDecoder()
    {
        auto lexicon = SentenceLexicon::Build(std::vector<CompactLexicon::Entry>{
            {u"aa", {u"甲", u"乙"}}, {u"bb", {u"一"}}, {u"cc", {u"二"}}, {u"dd", {u"三"}}, {u"ee", {u"四", u"五"}}});
        SentenceHistoryTransition history{{{{2, 0, 0, 0}}, 1}, [](auto& h, auto target)
        {
            double score = target == u"五" ? (h.count == 4 && h.tokens[3] == u'乙' ? 100.0 : -100.0) : 0;
            h = h.Append(target.front()); return score;
        }};
        SentenceTransition truncated = [](auto, auto, auto) -> double { throw std::runtime_error("History lost"); };
        SentenceLatticeResult previous;
        SentenceLatticeSettings historySettings; historySettings.autoSelectMinCodeLength = 2; // four-history ambiguity fixture
        std::u16string raw = u"aabbccddee";
        auto compare = [&](auto input)
        {
            auto incremental = DecodeSentenceLattice(lexicon, input, truncated, historySettings, {}, {}, &previous, &history);
            auto full = DecodeSentenceLattice(lexicon, input, truncated, historySettings, {}, {}, nullptr, &history);
            Check(incremental.candidates.size() == full.candidates.size());
            for (std::size_t i = 0; i < full.candidates.size(); ++i)
            {
                Check(incremental.candidates[i].text == full.candidates[i].text);
                Near(incremental.candidates[i].score, full.candidates[i].score);
                Check(incremental.candidates[i].history == full.candidates[i].history);
            }
            BuildSentenceEarlyEvidence(lexicon, incremental, truncated, historySettings, {}, {}, &history);
            previous = std::move(incremental);
        };
        for (std::size_t i = 1; i <= raw.size(); ++i) compare(std::u16string_view(raw).substr(0, i));
        Check(previous.candidates.front().text == u"乙一二三五");
        for (std::size_t i = raw.size(); i; --i) compare(std::u16string_view(raw).substr(0, i));
    }
    void ReaderTests()
    {
        Fixture fixture;
        for (int version : {1, 2})
        {
            auto image = Model(version); auto path = fixture.root / "model.bin"; Save(path, image);
            std::unique_ptr<SentenceFivegramModel::Query> query;
            { SentenceFivegramModel model(path); Check(model.Version() == version); query = model.CreateQuery(); }
            auto full = query->BeginHistory();
            query->Step(full, u"a"); query->Step(full, u"b"); query->Step(full, u"a");
            auto score = [&](auto target, double expected)
            { auto h = full; Near(query->Step(h, target), expected * std::log(10.0)); Check(h.count == 4); };
            score(u"b", -29.760); score(u"的人", -2 - 29.770); score(u"𠀀", -4 - 29.780);
            score(u"a", -6 - 29.790); score(u"missing", -8 - 29.900); score(u"\x03", -8 - 29.900);
            Check(query->HasObservedBigram(u"a", u"a") && !query->HasObservedBigram(u"missing", u"a"));
            auto h = query->BeginHistory(); Near(query->Step(h, u"\x02"), -29.900 * std::log(10.0));
            auto invalidHistory = SentenceLmHistory{}; bool rejected = false;
            try { query->Step(invalidHistory, u"a"); } catch (const std::invalid_argument&) { rejected = true; }
            Check(rejected);
            query.reset(); // final lease must release the mapped file
            auto bad = image; Put(bad, 8, 3, 4); Save(path, bad);
            rejected = false; try { SentenceFivegramModel invalid(path); } catch (const std::exception&) { rejected = true; } Check(rejected);
            bad = image; bad[258] = 0xff; Save(path, bad);
            rejected = false; try { SentenceFivegramModel invalid(path); } catch (const std::exception&) { rejected = true; } Check(rejected);
            bad = image; Put(bad, 64, bad.size() + 1, 8); Save(path, bad);
            rejected = false; try { SentenceFivegramModel invalid(path); } catch (const std::exception&) { rejected = true; } Check(rejected);
            Save(path, image);
            SchemaLexicon schema{CompactLexicon::Build(std::vector<CompactLexicon::Entry>{{u"aa", {u"a"}}, {u"bb", {u"b"}}}), {}, {}};
            RuntimeSentenceSettings settings; settings.optimalCodeHighFrequencyLimit = 0;
            RuntimeSentenceDecoder decoder(schema, path, settings);
            std::stop_source canceled; canceled.request_stop();
            bool canceledDecode = false;
            try { decoder.DecodeResult(u"aa", true, {}, {}, canceled.get_token()); }
            catch (const std::runtime_error&) { canceledDecode = true; }
            Check(canceledDecode);
            auto result = decoder.DecodeResult(u"aabbaa", true);
            Check(result->lattice->candidates.front().text == u"aba");
            Check(result->lattice->candidates.front().history.count == 4);
            settings.scoreSentenceBoundaries = false; rejected = false;
            try { RuntimeSentenceDecoder invalid(schema, path, settings); } catch (const std::invalid_argument&) { rejected = true; }
            Check(rejected);
        }
    }
    void ModelRepair()
    {
#ifdef _WIN32
        for (bool corrupt : {false,true})
        {
            Fixture fixture;
            auto directory=fixture.root/u"tables/test整句";
            std::filesystem::create_directories(directory);
            WriteUtf8TextFile(directory/"table.txt",u"aa a\naa b\n");
            auto config=fixture.root/"config.txt", model=fixture.root/"model.bin";
            WriteUtf8TextFile(config,u"码表存储位置\ttables\n当前码表\ttest整句\n整句神经重排\t否\n");
            if(corrupt) Save(model,Image{1,2,3});
            RuntimeLexicons runtime(fixture.root);runtime.Reload(config);
            RuntimeInput input(runtime,MakeUpperCaseServices([](auto){}),model,nullptr,true);
            input.ImportRaw(u"aa");
            Check(input.CaptureSnapshot().mode==RuntimeInputMode::Ordinary);
            Save(model,Model(2)); input.ReloadTables();
            auto until=std::chrono::steady_clock::now()+std::chrono::seconds(5);
            RuntimeInputSnapshot view;
            do { view=input.CaptureSnapshot(); if(!view.decodePending) break; std::this_thread::sleep_for(std::chrono::milliseconds(2)); }
            while(std::chrono::steady_clock::now()<until);
            Check(view.mode==RuntimeInputMode::Sentence && view.raw==u"aa" && view.candidatesCurrent && !view.page.entries.empty());
        }
#endif
    }
    int Scores(const char* model, const char* queries, const char* output)
    {
        auto path = [](const char* value) { auto text = std::string_view(value); return std::filesystem::path(std::u8string(text.begin(), text.end())); };
        SentenceFivegramModel reader(path(model)); auto query = reader.CreateQuery();
        std::ofstream writer(path(output)); writer << std::setprecision(17);
        ReadLexiconLines(path(queries), [&](auto line)
        {
            auto h = query->BeginHistory(); double score = 0; std::size_t start = 0;
            for (;;)
            {
                auto end = line.find(u'\t', start); score = query->Step(h, line.substr(start, end == std::u16string_view::npos ? end : end - start));
                if (end == std::u16string_view::npos) break;
                start = end + 1;
            }
            writer << score << '\n';
        });
        return writer ? 0 : 1;
    }
}
int main(int argc, char** argv)
{
    try
    {
        if (argc == 5 && std::string_view(argv[1]) == "--scores") return Scores(argv[2], argv[3], argv[4]);
        Check(argc == 1); HistoryDecoder(); ReaderTests(); ModelRepair();
        std::cout << "Fivegram Q8/Q16, full history, incremental/EOS evidence, lease and malformed-model tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
