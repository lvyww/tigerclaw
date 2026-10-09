#include "SentenceCompositionSession.h"
#include "SentenceLearningStore.h"
#include <fstream>
#include <iostream>
#include <source_location>

using namespace tiger::core;
namespace
{
    int checks = 0;
    void Check(bool value, std::source_location where = std::source_location::current())
    {
        ++checks;
        if (!value) throw std::runtime_error("Fragment learning regression line " + std::to_string(where.line()));
    }
    SentenceLearningEvent Event(std::u16string code, std::u16string text, std::u16string context = {}, int levels = 1)
    {
        SentenceLearningEvent e; e.id = learningId(); e.time = 1720000000;
        e.mode = u"fragment-v1"; e.code = std::move(code); e.text = std::move(text);
        e.context = std::move(context); e.levels = levels; return e;
    }
    SentenceBeamState Candidate(std::u16string text, double score, unsigned source,
        std::initializer_list<SentenceLearningBoundary> points, int rank = 1)
    {
        SentenceBeamState c; c.text = std::move(text); c.score = score; c.logMass = score;
        c.source = source; c.directRank = (source & 1) ? rank : std::numeric_limits<int>::max(); c.maxRank = rank;
        for (auto p : points)
            c.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{c.boundary,
                static_cast<std::size_t>(p.text), static_cast<std::size_t>(p.raw)});
        return c;
    }
    auto Events(std::u16string_view raw, std::u16string_view before, std::u16string_view selected,
        const std::vector<SentenceLearningBoundary>& points,
        const std::shared_ptr<const SentenceLearningSnapshot>& snapshot = {}, int floor = 0,
        const std::function<bool(std::u16string_view)>& supplemental = {})
    {
        return sentenceLearningSelectionEvents(raw, before, selected, points, points, floor,
            u"fragment-v1", snapshot, supplemental);
    }
    void Extraction()
    {
        auto bare = Events(u"KZJUY", u"滤掉", u"淦掉", {{3, 1}, {5, 2}});
        Check(bare.size() == 1 && bare[0].code == u"kzjuy" && bare[0].text == u"淦掉");
        Check(bare[0].context.empty() && bare[0].rawStart == 0 && bare[0].rawEnd == 5);
        auto known = SentenceLearningSnapshot::build(bare);
        auto prefix = Events(u"aakzjuy", u"甲滤掉", u"甲淦掉", {{2, 1}, {5, 2}, {7, 3}}, known);
        Check(prefix.size() == 1 && prefix[0].code == u"kzjuy" && prefix[0].text == u"淦掉");
        Check(prefix[0].context == u"甲" && prefix[0].rawStart == 2 && prefix[0].rawEnd == 7);
        auto different = Events(u"bbkzjuy", u"乙滤掉", u"乙淦掉", {{2, 1}, {5, 2}, {7, 3}}, known);
        Check(different.size() == 1 && different[0].code == u"kzjuy" && different[0].context == u"乙");
        Check(known->score(u"fragment-v1", u"kzjuy", u"淦掉", u"乙") == 6);
        auto suffix = Events(u"aakzjuycc", u"甲滤掉丙", u"甲淦掉丙",
            {{2, 1}, {5, 2}, {7, 3}, {9, 4}}, known);
        Check(suffix.size() == 1 && suffix[0].code == u"kzjuy");
        auto noHistory = Events(u"aakzjuy", u"甲滤掉", u"甲淦掉", {{2, 1}, {5, 2}, {7, 3}});
        Check(noHistory.size() == 1 && noHistory[0].code == u"kzj" && noHistory[0].text == u"淦");
        auto supplemental = Events(u"aakzjuy", u"甲滤掉", u"甲淦掉", {{2, 1}, {5, 2}, {7, 3}}, {}, 0,
            [](auto text) { return text == u"淦掉"; });
        Check(supplemental.size() == 1 && supplemental[0].code == u"kzjuy");
        bare.push_back(Event(u"kzj", u"淦"));
        auto wholeAndSingle = SentenceLearningSnapshot::build(bare);
        auto longest = Events(u"aakzjuy", u"甲滤掉", u"甲淦掉", {{2, 1}, {5, 2}, {7, 3}}, wholeAndSingle);
        Check(longest.size() == 1 && longest[0].text == u"淦掉");
        auto twice = Events(u"aakzjuyccdd", u"甲滤掉丙戊", u"甲淦掉丙丁",
            {{2, 1}, {5, 2}, {7, 3}, {9, 4}, {11, 5}}, known);
        Check(twice.size() == 2 && twice[0].text == u"淦掉" && twice[1].text == u"丁");
        auto ambiguous = SentenceLearningSnapshot::build({Event(u"aabb", u"甲乙"), Event(u"bbcc", u"乙丙")});
        auto fallback = Events(u"aabbccdd", u"甲戊丙丁", u"甲乙丙丁",
            {{2, 1}, {4, 2}, {6, 3}, {8, 4}}, ambiguous);
        Check(fallback.size() == 1 && fallback[0].text == u"乙" && fallback[0].code == u"bb");
        Check(Events(u"aakzjuy", u"甲滤掉", u"甲淦掉", {{2, 1}, {5, 2}, {7, 3}}, known, 3).empty());
        Check(Events(u"aakzjuy", u"甲淦掉", u"甲淦掉", {{2, 1}, {5, 2}, {7, 3}}, known).empty());
        Check(Events(u"kzjuy", u"滤掉", u"淦掉", {{3, 1}, {4, 2}}).empty());
        Check(Events(u"aabb", u"甲乙", u"{x}", {{4, 3}}).empty());
        Check(Events(std::u16string(129, u'a'), u"甲乙", u"淦掉", {{129, 2}}).empty());
        auto unicode = Events(u"aabb", u"\U00020000乙", u"\U00020000丙", {{2, 2}, {4, 3}});
        Check(unicode.size() == 1 && unicode[0].text == u"\U00020000丙" && unicode[0].code == u"aabb");
        auto direct = sentenceLearningSelectionEvents(u"kzjuy", u"滤掉", u"淦掉",
            {{3, 1}, {5, 2}}, {{5, 2}}, 0, u"fragment-v1", {});
        Check(direct.size() == 1 && direct[0].code == u"kzjuy");
    }
    void CaptureAndScores()
    {
        const auto empty = SentenceLearningSnapshot::build({});
        auto composed = Candidate(u"拾滑", 4, 2, {{2, 1}, {4, 2}});
        auto direct = Candidate(u"捡", -3, 1, {{4, 1}});
        std::vector<SentenceBeamState> candidates{composed, direct};
        SentenceLearningSelection selection; selection.Configure(empty, u"fragment-v1");
        selection.Capture(u"ujkf", candidates, 1, 0); // A non-first tap, without Tab.
        selection.Release(u"捡", 4, u"捡");
        auto events = selection.Take(u"捡");
        Check(events.size() == 1 && events[0].mode == u"fragment-v1" && events[0].code == u"ujkf" && events[0].text == u"捡");
        Check(events[0].levels == 1);
        Check(selection.Take(u"捡").empty());
        selection.Capture(u"ujkf", candidates, 0, 0); selection.Release(u"拾滑", 4, u"拾滑");
        Check(selection.Take(u"拾滑").empty());
        selection.Begin(composed); selection.Capture(u"ujkf", candidates, 1, 0); selection.Clear();
        selection.Release(u"捡", 4, u"捡"); Check(selection.Take(u"捡").empty());
        selection.Configure(empty, u"fragment-v1");
        auto directFirst = Candidate(u"甲丙", 30, 1, {{4, 2}}, 1);
        auto directSecond = Candidate(u"甲乙", 0, 1, {{4, 2}}, 2);
        std::vector<SentenceBeamState> pure{directFirst, directSecond};
        selection.Capture(u"aabb", pure, 1, 0); selection.Release(u"甲乙", 4, u"甲乙");
        Check(selection.Take(u"甲乙").empty());
        std::vector<SentenceBeamState> middle{directFirst, Candidate(u"乙丙", 4, 2, {{2, 1}, {4, 2}}), directSecond};
        selection.Begin(directFirst); selection.Capture(u"aabb", middle, 2, 0); selection.Release(u"甲乙", 4, u"甲乙");
        auto mid = selection.Take(u"甲乙");
        Check(mid.size() == 1 && mid[0].text == u"甲乙" && mid[0].code == u"aabb" && mid[0].levels == 1);
        // Menu final scores, rather than the obsolete pre-neural gap, set levels.
        candidates[0].score = 100; candidates[0].finalScore = 4;
        candidates[1].score = 0; candidates[1].finalScore = 0;
        selection.Capture(u"ujkf", candidates, 1, 0); selection.Release(u"捡", 4, u"捡");
        Check(selection.Take(u"捡")[0].levels == 1);
        auto lexicon = SentenceLexicon::Build(std::vector<CompactLexicon::Entry>{
            {u"uj", {u"拾"}}, {u"kf", {u"滑"}}, {u"ujkf", {u"捡"}}, {u"aa", {u"甲", u"乙"}}});
        SentenceLatticeSettings settings; settings.autoSelectMinCodeLength = 1; settings.beamWidth = 100; settings.candidateLimit = 20;
        auto transition = [](auto, auto, auto target) { return target == u"捡" ? -10.0 : 0.0; };
        auto plain = DecodeSentenceLattice(lexicon, u"ujkf", transition, settings);
        SentenceLearningQuery query{SentenceLearningSnapshot::build(events), u"fragment-v1"};
        auto learned = DecodeSentenceLattice(lexicon, u"ujkf", transition, settings, {}, {}, nullptr, nullptr, &query);
        Check(plain.candidates[0].text == u"拾滑" && learned.candidates[0].text == u"捡");
        auto oldDirect = *std::find_if(plain.candidates.begin(), plain.candidates.end(), [](const auto& c) { return c.text == u"捡"; });
        Check(learned.candidates[0].learningScore == 9 && learned.candidates[0].logMass == oldDirect.logMass);
        Check(learned.candidates[0].earlyLogMass == oldDirect.earlyLogMass);
        query = {SentenceLearningSnapshot::build({Event(u"aa", u"乙")}), u"fragment-v1"};
        for (auto beam : {1u, 2u, 100u}) for (auto limit : {1u, 2u, 20u})
        {
            settings.beamWidth = beam; settings.candidateLimit = limit;
            auto ranked = DecodeSentenceLattice(lexicon, u"aa", transition, settings, {}, {}, nullptr, nullptr, &query);
            Check(ranked.candidates[0].text == u"甲");
        }
        auto d1 = Candidate(u"B", -20, 1, {{4, 1}}, 1), d2 = Candidate(u"C", 10, 1, {{4, 1}}, 2);
        auto c = Candidate(u"A", 5, 2, {{2, 1}, {4, 1}}); d2.learningScore = 9;
        std::vector<SentenceBeamState> chain{d2, c, d1};
        ApplySentenceFusionOrdering(chain, u"test");
        Check(chain[0].text == u"B" && chain[1].text == u"C" && chain[2].text == u"A");
        chain = {c, d1, d2}; chain[2].learningScore = 0;
        ApplySentenceFusionOrdering(chain, u"test");
        Check(chain[0].text == u"A" && chain[1].text == u"B" && chain[2].text == u"C");
        SentenceCompositionSession session;
        session.ConfigureLearning(query.snapshot, u"fragment-v1"); session.ReplaceRaw(u"ujkf", 1);
        SentenceLatticeResult lattice; lattice.raw = u"ujkf";
        auto nd = direct; nd.score = -3 + 9; nd.learningScore = 9;
        lattice.candidates = {nd, composed};
        Check(session.Apply(session.Request(), lattice, {}));
        auto request = session.NeuralRequest(); Check(request.has_value());
        Check(session.ApplyNeural(*request, std::vector<double>{-100, 100}, true));
        Check(session.Candidates()[0].text == u"拾滑");
        Check(session.Candidates()[0].score == 4 && SentenceFinalScore(session.Candidates()[0]) > 4);
    }
    void LegacyAndWindow()
    {
        auto ordinary = Event(u"aabb", u"甲乙");
        auto retired = SentenceFusionPreference::event(u"fragment-v1", u"aabb", u"甲", u"乙", true, 4);
        auto corrected = retired; corrected.id = learningId(); corrected.mode = u"exact-correction-v1|fragment-v1";
        auto legacy = SentenceLearningSnapshot::build({retired, corrected});
        Check(legacy->empty());
        SentenceLearningAccumulator replay; Check(replay.update({retired, corrected})->empty());
        auto mixed = replay.update({ordinary, retired, corrected});
        Check(mixed->score(u"fragment-v1", u"aabb", u"甲乙", u"") == 9);
        Check(mixed->prefixScore(retired.mode, u"~", u"D", u"") == 0);
        auto path = std::filesystem::temp_directory_path() / ("tiger-fragment-window-" + learningId()) / "learning.txt";
        std::filesystem::create_directories(path.parent_path());
        {
            std::ofstream out(path, std::ios::binary);
            out << "学习\t2024-07-03T09:46:40Z\t甲乙\taabb\t\t1\tfragment-v1\tordinary\t\n";
            for (int i = 0; i < 10010; ++i)
                out << "学习\t2024-07-03T09:46:40Z\tD\t~f123\t\t1\t"
                    << (i % 2 ? "fusion-v1|fragment-v1" : "exact-correction-v1|fragment-v1")
                    << "\tlegacy-" << i << "\t\n";
        }
        auto size = std::filesystem::file_size(path);
        SentenceLearningStore store(path); store.refresh();
        Check(store.entries().size() == 1 && store.snapshot()->score(u"fragment-v1", u"aabb", u"甲乙", u"") == 9);
        Check(std::filesystem::file_size(path) == size);
        store.confirm({retired, corrected}); Check(std::filesystem::file_size(path) == size);
        std::filesystem::remove_all(path.parent_path());
    }
}
int main()
{
    try
    {
        Extraction(); CaptureAndScores(); LegacyAndWindow();
        std::cout << "Fragment learning, Direct scores/rank chain, final menu scores and legacy window: " << checks << " checks passed\n";
        return 0;
    }
    catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
