#include "SentenceCommitTracker.h"
#include "SentenceCompositionContext.h"
#include "SentenceCompositionSession.h"
#include "SentenceEmptyCodeTracker.h"
#include "SentencePathQuery.h"
#include <iostream>
#include <source_location>
using namespace tiger::core;
namespace { void Check(bool value, std::source_location source = std::source_location::current())
    { if (!value) throw std::runtime_error("Sentence commit tracker regression at line " + std::to_string(source.line())); } }
int main()
{
    try
    {
        SentenceCommitTracker tracker;
        SentenceCommitContext context; context.enabled = true;
        SentenceLatticeResult lattice;
        SentenceEarlyEvidence evidence;
        evidence.prefixes.push_back({u"XY", 2, 1, 1, true});
        SentenceBeamState candidate; candidate.text = u"XYZ";
        candidate.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{nullptr, 2, 2});
        lattice.candidates.push_back(candidate);
        auto observe = [&](std::size_t length, double share = 1.0)
        {
            lattice.raw.assign(length, u'a'); context.fullRaw = lattice.raw;
            evidence.prefixes[0].share = share;
            return tracker.Observe(lattice, evidence, context);
        };
        Check(!observe(5)); Check(!observe(5)); // same generation is not new evidence
        auto strong = observe(6); Check(strong && strong->text == u"XY" && strong->rawLength == 2);
        Check(!observe(5, .999)); Check(!observe(6, .999)); Check(observe(7, .999).has_value());
        Check(!observe(5, .999));
        evidence.neutralLowConfidence = true;
        Check(!observe(6, .4)); Check(!observe(7, .4)); Check(!observe(8, .4));
        evidence.neutralLowConfidence = false;
        Check(!observe(9, .999)); Check(observe(10, .999).has_value());
        Check(!observe(5)); context.suspended = true; Check(!observe(6)); context.suspended = false;
        Check(!observe(7)); Check(observe(8).has_value());
        Check(!observe(5)); evidence.confidenceTruncated = true; Check(!observe(6)); evidence.confidenceTruncated = false;
        Check(!observe(7)); Check(observe(8).has_value());
        context.minimumRetained = 10;
        Check(!observe(5)); Check(!observe(6)); Check(!observe(7));
        tracker.Reset(); context.acceptedNeuralTop.reset(); context.minimumRetained = 3;
        context.committedText = u"X"; context.committedRaw = 1;
        Check(!observe(5));
        auto extension = observe(6);
        Check(extension && extension->text == u"XY" && extension->rawLength == 2);
        // Proposal owns the entire prefix; the host must commit only the Y suffix.
        context.committedText = u"Q";
        Check(!observe(7)); Check(!observe(8));
        context.committedText = {}; context.committedRaw = 0; tracker.Reset();
        lattice.candidates[0].boundary.reset();
        Check(!observe(5)); Check(!observe(6)); // text match alone cannot prove a raw boundary
        evidence.mergedIncompleteTail = true;
        Check(!observe(7)); Check(observe(8).has_value()); // dropped-tail evidence has its own boundary
        evidence.mergedIncompleteTail = false;
        lattice.candidates[0].boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{nullptr, 2, 2});
        tracker.Reset(); context.lastCommitRaw = 4;
        Check(!observe(5)); Check(!observe(6)); // mature, but too close to the last commit
        Check(observe(7).has_value()); context.lastCommitRaw = 0;
        tracker.Reset();
        Check(!observe(5));
        std::u16string precedingRaw(7, u'a');
        lattice.raw.assign(6, u'a'); context.fullRaw = precedingRaw;
        Check(tracker.Observe(lattice, evidence, context).has_value()); // one-key preceding decode is allowed
        tracker.Reset(); context.acceptedNeuralTop = u"other";
        lattice.candidates[0].supplementScore = 1;
        Check(!observe(5)); Check(observe(6).has_value()); // supplemental winner pins the accepted prefix
        lattice.candidates[0].supplementScore = 0; context.acceptedNeuralTop.reset();
        Check(!observe(5)); context.lexiconCurrent = false; Check(!observe(6));
        context.lexiconCurrent = true; Check(!observe(7)); Check(observe(8).has_value());
        context.minimumRetained = 3; context.acceptedNeuralTop.reset(); tracker.Reset();
        Check(!observe(5, .999)); evidence.neutralLowConfidence = true;
        for (std::size_t length = 6; length <= 9; ++length) Check(!observe(length, .4));
        evidence.neutralLowConfidence = false;
        Check(!observe(10, .999)); Check(!observe(11, .999)); Check(observe(12, .999).has_value());
        Check(!observe(5, .999)); evidence.neutralLowConfidence = true;
        evidence.prefixes.push_back({u"XZ", 2, .7, 1, true});
        Check(!observe(6, .4)); // stronger fork with shared stem drops XY
        evidence.prefixes.pop_back(); evidence.neutralLowConfidence = false;
        Check(!observe(7, .999)); Check(!observe(8, .999)); Check(observe(9, .999).has_value());
        Check(!observe(5));
        std::u16string newerRaw(7, u'a'); context.fullRaw = newerRaw;
        Check(!tracker.Observe(lattice, evidence, context)); // two generations stale resets
        Check(!observe(6)); Check(observe(7).has_value());
        tracker.Reset(); context.minimumRetained = 3;
        Check(!observe(5)); Check(!observe(7)); // skipped generation restarts evidence
        Check(observe(8).has_value());
        tracker.Reset(); context.acceptedNeuralTop = u"other";
        Check(!observe(5)); Check(!observe(6)); Check(!observe(7));
        std::cout << "Sentence commit tracker evidence/gap/reset/constraint tests passed\n";
        SentenceCompositionContext composition;
        for (auto key : std::u16string_view(u"AaBbCc")) composition.Append(key);
        Check(composition.ApplyPrefix({u"XY", 2, 1}) == u"XY");
        Check(composition.Raw() == u"AaBbCc" && composition.UncommittedRaw() == u"BbCc");
        Check(composition.CandidateSuffix(u"XYZ") == u"Z");
        Check(!composition.AcceptsCandidate(u"Q") && composition.CandidateSuffix(u"Q") == u"BbCc");
        Check(composition.ApplyPrefix({u"XYZ", 4, 1}) == u"Z");
        Check(composition.CommitContext(true).lastCommitRaw == 4);
        bool rejected = false;
        try { composition.ApplyPrefix({u"Q", 5, 1}); } catch (const std::invalid_argument&) { rejected = true; }
        Check(rejected && composition.CommittedText() == u"XYZ" && composition.UncommittedRaw() == u"Cc");
        composition.Backspace(); Check(composition.UncommittedRaw() == u"C");
        composition.Backspace(); Check(composition.Raw().empty() && composition.CommittedText().empty());
        for (auto key : std::u16string_view(u"AaBb")) composition.Append(key);
        composition.ApplyPrefix({u"X", 2, 1});
        Check(composition.FinishLiteral() == u"Bb" && composition.Raw().empty());
        for (auto key : std::u16string_view(u"AaBb")) composition.Append(key);
        composition.ApplyPrefix({u"X", 2, 1});
        Check(composition.Finish(u"XY", u"!") == u"Y!" && composition.Raw().empty());
        for (auto key : std::u16string_view(u"AaBb")) composition.Append(key);
        composition.ApplyPrefix({u"X", 2, 1});
        Check(composition.Finish(std::nullopt, u"!") == u"Bb!");
        std::cout << "Sentence composition retained-context/literal/backspace tests passed\n";
        SentenceCompositionSession session;
        for (auto key : std::u16string_view(u"AAAAA")) session.Append(key);
        auto request5 = session.Request();
        lattice.raw = u"aaaaa"; evidence.prefixes = {{u"XY", 2, 1, 1, true}};
        Check(session.Apply(request5, lattice, evidence));
        Check(!session.TryAutoCommit(true));
        Check(!session.Apply(request5, lattice, evidence));
        session.Append(u'A'); auto request6 = session.Request();
        Check(!session.Apply(request5, lattice, evidence));
        lattice.raw = u"aaaaaa"; Check(session.Apply(request6, lattice, evidence));
        auto committed = session.TryAutoCommit(true);
        Check(committed && *committed == u"XY" && session.Context().UncommittedRaw() == u"AAAA");
        Check(!session.Apply(request6, lattice, evidence));
        auto filteredRequest = session.Request();
        SentenceBeamState wrong; wrong.text = u"Q"; lattice.candidates.push_back(wrong);
        Check(session.Apply(filteredRequest, lattice, evidence) && session.Candidates().size() == 1);
        Check(session.Select(0)); Check(!session.Apply(filteredRequest, lattice, evidence));
        Check(!session.TryAutoCommit(true));
        session.InvalidateLexicon(1); Check(!session.Apply(filteredRequest, lattice, evidence));
        Check(session.Candidates().empty() && session.FinishLiteral() == u"AAAA");
        Check(!session.Apply(filteredRequest, lattice, evidence));
        std::cout << "Sentence composition session generation/prefix/selection tests passed\n";
        SentenceCompositionSession displaySession;
        for (auto key : std::u16string_view(u"AaBb")) displaySession.Append(key);
        SentenceLatticeResult displayLattice; displayLattice.raw = u"aabb";
        auto firstBoundary = std::make_shared<SentenceBoundary>(SentenceBoundary{nullptr, 1, 2});
        SentenceBeamState displayCandidate; displayCandidate.text = u"XY";
        displayCandidate.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{firstBoundary, 2, 4});
        displayLattice.candidates.push_back(displayCandidate);
        Check(displaySession.DisplayCode() == u"AaBb" && !displaySession.FinishSelected());
        Check(displaySession.Apply(displaySession.Request(), displayLattice, {}));
        Check(displaySession.DisplayCode() == u"Aa Bb");
        displaySession.Append(u'C');
        Check(displaySession.DisplayCode() == u"Aa BbC" && !displaySession.FinishSelected(u"!"));
        displaySession.Backspace(); displaySession.Backspace();
        Check(displaySession.DisplayCode() == u"Aa B");
        displaySession.Append(u'D'); Check(displaySession.DisplayCode() == u"AaBD");
        displayLattice.raw = u"aabd";
        SentenceBeamState alternate = displayCandidate; alternate.text = u"XZ";
        displayLattice.candidates.push_back(alternate);
        Check(displaySession.Apply(displaySession.Request(), displayLattice, {}));
        Check(displaySession.Select(1));
        Check(displaySession.FinishSelected(u"!") == std::optional<std::u16string>(u"XZ!"));
        Check(displaySession.Context().Raw().empty() && displaySession.DisplayCode().empty());
        displaySession.Append(u'Q'); displayLattice.raw = u"q"; displayLattice.candidates.clear();
        Check(displaySession.Apply(displaySession.Request(), displayLattice, {}));
        Check(displaySession.FinishSelected(u"!") == std::optional<std::u16string>(u"Q!"));
        std::cout << "Sentence session pending-display/selected-commit tests passed\n";
        SentenceCompositionSession controls;
        controls.Append(u'A');
        auto pendingSpace = controls.ControlKey(0x20, false, 5, false, false);
        Check(pendingSpace.handled && pendingSpace.needsDecode && controls.Context().Raw() == u"A");
        SentenceLatticeResult controlLattice; controlLattice.raw = u"a";
        for (auto text : {u"X", u"Y", u"Z"})
        {
            SentenceBeamState item; item.text = text; controlLattice.candidates.push_back(item);
        }
        Check(controls.Apply(controls.Request(), controlLattice, {}));
        Check(controls.ControlKey(0x26, false, 2, false, false).handled && controls.SelectedIndex() == 1);
        Check(controls.ControlKey(0x28, false, 2, false, false).handled && controls.SelectedIndex() == 0);
        Check(controls.ControlKey(0x09, true, 2, false, false).handled && controls.SelectedIndex() == 1);
        auto space = controls.ControlKey(0x20, false, 2, false, false);
        Check(space.commit == std::optional<std::u16string>(u"Y") && controls.Context().Raw().empty());
        controls.Append(u'B');
        auto beforeCancel = controls.Request();
        Check(controls.ControlKey(0x1b, false, 5, false, false).handled);
        controlLattice.raw = u"b";
        Check(!controls.Apply(beforeCancel, controlLattice, {}));
        controls.Append(u'C');
        Check(controls.ControlKey(0x0d, false, 5, true, false).commit == std::nullopt);
        Check(controls.Context().Raw().empty()); controls.Append(u'D');
        Check(controls.ControlKey(0x0d, false, 5, false, false).commit == std::optional<std::u16string>(u"D"));
        controls.Append(u'E'); controlLattice.raw = u"e"; controlLattice.candidates.clear();
        Check(controls.Apply(controls.Request(), controlLattice, {}));
        Check(!controls.ControlKey(0x20, false, 5, false, false).commit && controls.Context().Raw() == u"E");
        Check(controls.ControlKey(0x09, false, 5, false, true).handled && controls.Context().Raw().empty());
        Check(!controls.ControlKey(0x41, false, 5, false, false).handled);
        std::cout << "Sentence control-key navigation/empty/clear/decode-barrier tests passed\n";
        SentenceEmptyCodeTracker emptyTracker;
        SentenceLatticeResult emptyBase; emptyBase.raw = u"aa";
        SentenceBeamState emptyCandidate; emptyCandidate.text = u"X";
        emptyCandidate.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{nullptr, 1, 2});
        emptyBase.candidates.push_back(emptyCandidate);
        SentenceCommitContext emptyContext; emptyContext.enabled = true; emptyContext.fullRaw = u"aa";
        bool otherOutput = false; int uniqueChecks = 0;
        auto exact = [&](auto, auto, std::optional<std::u16string_view> excluded, bool group)
        {
            if (excluded) { Check(group && *excluded == u"X"); ++uniqueChecks; return otherOutput; }
            return false;
        };
        auto proper = [](std::u16string_view code) { return code == u"aab"; };
        Check(!emptyTracker.Append(emptyBase, {}, emptyContext, u"aab", 0, exact, proper));
        Check(emptyTracker.PendingCommit() && uniqueChecks == 0);
        emptyContext.fullRaw = u"aab";
        auto emptyCommit = emptyTracker.Append(emptyBase, {}, emptyContext, u"aabc", 0, exact, proper);
        Check(emptyCommit && emptyCommit->text == u"X" && emptyCommit->rawLength == 2 && uniqueChecks == 1);
        emptyContext.fullRaw = u"aa"; otherOutput = true;
        Check(!emptyTracker.Append(emptyBase, {}, emptyContext, u"aax", 0, exact, proper));
        Check(!emptyTracker.PendingCommit()); otherOutput = false;
        Check(!emptyTracker.Append(emptyBase, {}, emptyContext, u"aax", 2, exact, proper));
        emptyContext.fullRaw = u"aax";
        Check(emptyTracker.Append(emptyBase, {}, emptyContext, u"aaxy", 2, exact, proper).has_value());
        emptyContext.fullRaw = u"aa";
        Check(!emptyTracker.Append(emptyBase, {}, emptyContext, u"aab", 0, exact, proper));
        emptyContext.fullRaw = u"aab";
        Check(!emptyTracker.Append(emptyBase, {}, emptyContext, u"aab2", 0, exact, proper));
        Check(!emptyTracker.PendingCommit());
        std::cout << "Sentence empty-code defer/uniqueness/retention/selector tests passed\n";
        std::vector<CompactLexicon::Entry> emptyEntries{{u"aa", {u"X"}}, {u"aabz", {u"Y"}}};
        auto emptyLexicon = SentenceLexicon::Build(emptyEntries);
        auto completePath = [&](auto raw, auto required, std::optional<std::u16string_view> excluded, bool group)
        { return HasCompleteSentenceCandidate(emptyLexicon, raw, true, required, excluded, group); };
        auto properPath = [&](auto raw) { return emptyLexicon.IsProperPrefix(raw); };
        SentenceCompositionSession emptySession;
        emptySession.Append(u'a'); emptySession.Append(u'a');
        Check(emptySession.Apply(emptySession.Request(), emptyBase, {}));
        Check(!emptySession.AppendWithAutoCommit(u'b', true, 0, completePath, properPath));
        auto actualCommit = emptySession.AppendWithAutoCommit(u'c', true, 0, completePath, properPath);
        Check(actualCommit == std::optional<std::u16string>(u"X"));
        Check(emptySession.Context().Raw() == u"aabc" && emptySession.Context().UncommittedRaw() == u"bc");
        SentenceLatticeResult continuationResult; continuationResult.raw = u"aabc";
        SentenceBeamState firstRank; firstRank.text = u"XY"; firstRank.maxRank = 1;
        SentenceBeamState laterRank = firstRank; laterRank.text = u"XZ"; laterRank.maxRank = 2;
        continuationResult.candidates = {firstRank, laterRank};
        Check(emptySession.Apply(emptySession.Request(), continuationResult, {}));
        Check(emptySession.Candidates().size() == 1 && emptySession.Candidates()[0].text == u"XY");
        Check(!emptySession.AppendWithAutoCommit(u'2', false, 0, completePath, properPath));
        continuationResult.raw = u"aabc2";
        Check(emptySession.Apply(emptySession.Request(), continuationResult, {}));
        Check(emptySession.Candidates().size() == 2);
        auto migration = emptySession;
        auto oldMigrationRequest = migration.Request();
        Check(migration.Select(1));
        migration.ReplaceRaw(migration.Context().UncommittedRaw(), 17);
        Check(migration.Context().Raw() == u"bc2" && migration.Context().CommittedText().empty());
        Check(migration.Request().requiredPrefix.empty() && migration.Request().lexiconVersion == 17);
        Check(migration.Candidates().empty() && migration.SelectedIndex() == 0);
        Check(!migration.Apply(oldMigrationRequest, continuationResult, {}));
        migration.ReplaceRaw(u"AbC2;'", 18);
        Check(migration.Context().Raw() == u"AbC2;'" && migration.FinishLiteral() == u"AbC2;'");
        Check(emptySession.FinishLiteral() == u"bc2");
        for (int i = 0; i < 128; ++i) emptySession.Append(u'a');
        auto fullRequest = emptySession.Request();
        Check(!emptySession.AppendWithAutoCommit(u'b', true, 0, completePath, properPath));
        Check(emptySession.Request().generation == fullRequest.generation && emptySession.Context().Raw().size() == 128);
        std::cout << "Sentence empty-code session exact-path/continuation/limit tests passed\n";
        Check(std::abs(CombineSentenceNeuralScore(10, -10, 2, 2) - 7) < 1e-12);
        Check(std::abs(CombineSentenceNeuralScore(10, -10, 3, 3) - 4) < 1e-12);
        Check(std::abs(CombineSentenceNeuralScore(10, -10, 1, 3) - 7) < 1e-12);
        Check(std::abs(CombineSentenceNeuralScore(10, -10, 7, 2) - 1.6) < 1e-12);
        SentenceCompositionSession neuralSession;
        neuralSession.Append(u'a'); neuralSession.Append(u'a');
        SentenceLatticeResult neuralLattice; neuralLattice.raw = u"aa";
        SentenceBeamState shortText; shortText.text = u"XY"; shortText.score = 10; shortText.maxRank = 1;
        SentenceBeamState longerText = shortText; longerText.text = u"XYZ"; longerText.score = 9;
        neuralLattice.candidates = {shortText, longerText};
        Check(neuralSession.Apply(neuralSession.Request(), neuralLattice, {}));
        auto neuralRequest = neuralSession.NeuralRequest(); Check(neuralRequest.has_value());
        std::vector<double> neuralScores{-10, 10};
        auto invalidRequest = *neuralRequest; invalidRequest.candidates[0] = u"wrong";
        Check(!neuralSession.ApplyNeural(invalidRequest, neuralScores, true));
        Check(neuralSession.ApplyNeural(*neuralRequest, neuralScores, true));
        Check(neuralSession.Candidates()[0].text == u"XYZ" && neuralSession.Candidates()[0].score == 9);
        Check(!neuralSession.ApplyNeural(*neuralRequest, neuralScores, true));
        neuralSession.Append(u'a'); Check(!neuralSession.ApplyNeural(*neuralRequest, neuralScores, true));
        neuralLattice.raw = u"aaa"; Check(neuralSession.Apply(neuralSession.Request(), neuralLattice, {}));
        auto manualRequest = neuralSession.NeuralRequest(); Check(manualRequest.has_value());
        Check(neuralSession.Select(1)); Check(!neuralSession.ApplyNeural(*manualRequest, neuralScores, true));
        neuralSession.Cancel(); Check(!neuralSession.ApplyNeural(*manualRequest, neuralScores, true));
        std::cout << "Sentence neural mixed-length/identity/freeze/base-preservation tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
