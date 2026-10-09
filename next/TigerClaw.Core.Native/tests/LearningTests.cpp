#include "SentenceLearningWorker.h"
#include "SentenceLearningSelection.h"
#include "SentenceCompositionSession.h"
#include <iostream>
#include <source_location>
using namespace tiger::core;
namespace
{
    void Check(bool value, std::source_location at = std::source_location::current())
    { if (!value) throw std::runtime_error("Learning regression line " + std::to_string(at.line())); }
    SentenceLearningEvent Event(std::string id, int levels = 1)
    {
        SentenceLearningEvent e; e.id = std::move(id); e.time = 1720000000; e.mode = u"test";
        e.code = u"aabb"; e.text = u"甲乙"; e.levels = levels; return e;
    }
    SentenceBeamState Candidate(std::u16string text, double score, unsigned source = 2)
    {
        SentenceBeamState c; c.text = std::move(text); c.score = score; c.source = source;
        c.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{
            std::make_shared<SentenceBoundary>(SentenceBoundary{nullptr, 1, 2}), 2, 4});
        return c;
    }
}
int main()
{
    auto directory = std::filesystem::temp_directory_path() / ("tiger-native-learning-" + learningId());
    try
    {
        auto path = directory / "journal.txt";
        auto worker = std::make_shared<SentenceLearningWorker>(path);
        std::int64_t clock = 1000;
        SentenceLearningReceipts receipts([&] { return clock; });
        auto token = receipts.Issue(u"TSF", worker, {Event("one", 3)});
        Check(!token.empty()); Check(!std::filesystem::exists(path));
        Check(!receipts.Acknowledge(u"Hook", token, true));
        Check(receipts.Acknowledge(u"TSF", token, true));
        Check(!receipts.Acknowledge(u"TSF", token, true));
        Check(worker->WaitIdle(std::chrono::seconds(10))); Check(!worker->LastError());
        Check(worker->Snapshot()->score(u"test", u"aabb", u"甲乙", u"") == 13);
        Check(worker->Snapshot()->confidenceScore(u"test", u"aabb", u"甲乙", u"") == 9);
        token = receipts.Issue(u"Hook", worker, {Event("failed")});
        Check(!receipts.Acknowledge(u"Hook", token, false)); Check(!receipts.Acknowledge(u"Hook", token, true));
        token = receipts.Issue(u"Hook", worker, {Event("expired")}); clock += 30001;
        Check(!receipts.Acknowledge(u"Hook", token, true));
        token = receipts.Issue(u"Hook", worker, {Event("clock-reset")}); clock = 1;
        Check(!receipts.Acknowledge(u"Hook", token, true));
        for (int i = 0; i < 128; ++i) Check(!receipts.Issue(u"TSF", worker, {Event("capacity")}).empty());
        Check(receipts.Issue(u"TSF", worker, {Event("overflow")}).empty()); receipts.Cancel();
        SentenceLearningStore reader(path); reader.refresh();
        Check(reader.entries().size() == 1); reader.confirm({Event("one", 3)}); Check(reader.entries().size() == 1);
        reader.confirm({Event("two", 3)}); Check(reader.snapshot()->score(u"test", u"aabb", u"甲乙", u"") == 19);
        Check(reader.undoLast()); Check(reader.snapshot()->score(u"test", u"aabb", u"甲乙", u"") == 13);
        reader.forget(u"test", u"aabb", u"甲乙"); Check(reader.snapshot()->empty());
        reader.confirm({Event("after-forget")}); Check(!reader.snapshot()->empty());
        reader.clear(); Check(reader.snapshot()->empty());
        auto before = std::filesystem::file_size(path);
        { std::ofstream out(path, std::ios::app | std::ios::binary); out << "malformed row\n"; }
        bool rejected = false; try { reader.confirm({Event("three")}); } catch (...) { rejected = true; }
        Check(rejected && std::filesystem::file_size(path) == before + 14);
        // A failed write must not publish a ranking event.
        Check(reader.snapshot()->empty());

        SentenceCompositionSession session;
        session.ConfigureLearning(SentenceLearningSnapshot::build({}), u"test");
        session.ReplaceRaw(u"aabb", 1);
        SentenceLatticeResult lattice; lattice.raw = u"aabb";
        lattice.candidates = {Candidate(u"甲丙", 18), Candidate(u"甲乙", 0)};
        Check(session.Apply(session.Request(), lattice, {}));
        Check(session.Select(1)); auto output = session.FinishSelected(); Check(output && *output == u"甲乙");
        auto events = session.TakeLearning(*output); Check(events.size() == 1);
        Check(events[0].levels == 3 && events[0].code == u"aabb" && events[0].text == u"甲乙" && events[0].context.empty());
        Check(session.TakeLearning(*output).empty());
        session.ReplaceRaw(u"aabb", 1); Check(session.Apply(session.Request(), lattice, {}));
        session.Select(1); session.FinishSelected(); Check(session.TakeLearning(u"different output").empty());
        session.ReplaceRaw(u"aabb", 1); Check(session.Apply(session.Request(), lattice, {}));
        session.FinishSelected(); Check(session.TakeLearning(u"甲丙").empty());
        session.ReplaceRaw(u"aabb", 1); lattice.candidates[0].source = 1;
        Check(session.Apply(session.Request(), lattice, {})); session.Select(1); session.FinishSelected();
        events = session.TakeLearning(u"甲乙"); Check(events.size() == 1 && events[0].mode == u"test" && events[0].code == u"aabb" && events[0].text == u"甲乙");
        Check(SentenceFusionPreference::signedScore(SentenceLearningSnapshot::build(events), u"test", u"aabb", u"甲丙", u"甲乙") == 0);
        session.ReplaceRaw(u"aabb", 1); lattice.candidates[0].source = 2;
        Check(session.Apply(session.Request(), lattice, {})); session.Select(1);
        auto never = [](auto&&...) { return false; };
        auto commit = session.AppendWithAutoCommit(u'c', false, 3, never, never);
        Check(!commit && session.Request().lockedPrefix && session.Request().lockedPrefix->text == u"甲乙");
        Check(session.Request().raw == u"aabbc");
        session.Backspace(); Check(!session.Request().lockedPrefix);
        session.ReplaceRaw(u"aabb", 1); Check(session.Apply(session.Request(), lattice, {})); session.Select(1);
        commit = session.AppendWithAutoCommit(u'c', true, 3, never, never);
        Check(commit && *commit == u"甲乙" && session.Context().UncommittedRaw() == u"c");
        Check(session.TakeLearning(*commit).size() == 1);
        session.Cancel(); Check(!session.Request().lockedPrefix);
        worker.reset(); std::filesystem::remove_all(directory);
        std::cout << "Learning snapshot, adaptive levels, journal, worker, selection and receipt regressions passed\n";
        return 0;
    }
    catch (const std::exception& e) { std::cerr << e.what() << " (evidence: " << directory << ")\n"; return 1; }
}
