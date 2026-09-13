#include "LexiconText.h"
#include "LexiconFile.h"
#include "LexiconAssembly.h"
#include "CodeCase.h"
#include "TextElements.h"
#include "WordConstruction.h"
#include "SchemaLexicon.h"
#include "ConfigParser.h"
#include "RecentSchemas.h"
#include "CandidatePage.h"
#include "OutputActions.h"
#include "OutputServices.h"
#include "OutputState.h"
#include "SendHistory.h"
#include "KeyPostProcessor.h"
#include "Punctuation.h"
#include "SelectionKeys.h"
#include "OrdinaryComposition.h"
#include "UpperCaseComposition.h"
#include "PinyinComposition.h"
#include "CandidateSymbolTransition.h"
#include "ChineseInputSession.h"
#include "ActionShortcuts.h"
#include "ShiftToggleState.h"
#include "CtrlSpaceState.h"
#include "MixedInputDecoder.h"
#include "MixedComposition.h"
#include "SentenceNgram.h"
#include "CachedSentenceNgram.h"
#include "SentenceNgramTransition.h"
#include "SentenceIsolation.h"
#include "SentenceCharacterRanks.h"
#include "SentenceSupplement.h"
#include "SentenceSupplementFile.h"
#include "RuntimeSentenceDecoder.h"
#include "SentencePrefixEvidence.h"
#include "SentenceEarlyEvidence.h"
#include "SentenceLexicon.h"
#include "SentenceEdges.h"
#include "SentenceBeam.h"
#include "SentenceLattice.h"
#include <iostream>
#include <stdexcept>
#include <source_location>

using namespace tiger::core;
static void Check(bool ok, std::source_location where = std::source_location::current())
{
    if (!ok) throw std::runtime_error("Lexicon text regression at line " + std::to_string(where.line()));
}
int main()
{
    try
    {
        Check(StripInlineComment(u"  x\\#y # tail ") == u"x#y");
        Check(StripInlineComment(u"x\\\\#y") == u"x\\\\");
        Check(StripInlineComment(u"x\\\\\\#y") == u"x\\\\#y");
        Check(StripInlineComment(u"\u3000x\u00a0") == u"x");
        Check(DecodeLexiconEscapes(u"\\t\\n\\s\\\\n") == u"\t\r\n \\n");
        Check(DecodeLexiconEscapes(u"Bime20231222BIME") == u"\\");
        Check(ParseLexiconEntryToken(u" a=>b ") == u"a\x001e" u"b");
        Check(ParseLexiconEntryToken(u"a=>a") == u"a");
        Check(ParseLexiconEntryToken(u"=>a") == u"=>a");
        Check(ParseLexiconEntryToken(u"a=>") == u"a=>");
        Check(ParseLexiconEntryToken(u"\\s=>\\t") == u" \x001e\t");
        std::u16string unusual{char16_t(0xd800), char16_t(0), char16_t(0xdc00)};
        Check(ParseLexiconEntryToken(unusual) == unusual);
        Check(StripInlineComment(unusual) == unusual);
        LexiconLineParser yaml(true);
        Check(yaml.Parse(u"name: test").empty());
        Check(yaml.Parse(u"... # comment").empty());
        Check(yaml.Parse(u"word\tab").empty());
        Check(yaml.Parse(u"...").empty());
        auto rows = yaml.Parse(u"AB first second -2147483648");
        Check(rows.size() == 2 && rows[0].code == u"ab" && rows[1].text == u"second" &&
            rows[0].frequency == (-2147483647 - 1));
        rows = yaml.Parse(u"word\t99\tAB");
        Check(rows.size() == 1 && rows[0].code == u"AB" && rows[0].frequency == 99);
        rows = yaml.Parse(u"word\t2147483648");
        Check(rows.size() == 1 && rows[0].code == u"2147483648" && rows[0].frequency == 0);
        rows = yaml.Parse(u"word\t99");
        Check(rows.size() == 1 && rows[0].code.empty() && rows[0].frequency == 99);
        Check(yaml.Parse(u"{\u6dfb\u52a0} ignored").empty());
        rows = yaml.Parse(u"{\u91cd\u590d\u4e0a\u5c4f}\tz");
        Check(rows.size() == 1 && rows[0].code == u"z");
        auto decode = [](std::initializer_list<std::uint8_t> bytes)
        { return DecodeLexiconBytes(std::span<const std::uint8_t>(bytes.begin(), bytes.size())); };
        Check(decode({}).empty());
        Check(decode({0xef, 0xbb, 0xbf, 0x61}) == u"a");
        Check(decode({0xff, 0xfe, 0x61, 0}) == u"a");
        Check(decode({0xfe, 0xff, 0, 0x61}) == u"a");
        Check(decode({0, 0, 0xfe, 0xff, 0, 0, 0, 0x61}) == u"a");
        Check(decode({0xff, 0xfe, 0, 0, 0x61, 0, 0, 0}) == std::u16string({0, u'a', 0}));
        Check(decode({0xf0, 0xa0, 0x80, 0x80}) == u"\U00020000");
        Check(decode({0xe0, 0x80, 0x80}) == u"\ufffd\ufffd\ufffd");
        Check(decode({0xe1, 0x80, 0x61}) == u"\ufffda");
        Check(decode({0xff, 0xfe, 0, 0xd8, 0x61, 0}) == u"\ufffda");
        Check(decode({0xff, 0xfe, 0, 0xd8, 0x61}) == u"\ufffd\ufffd");
        Check(NormalizeCode(u"\u3000ABC\u00a0") == u"abc");
        Check(NormalizeCode(u"\U00010400") == u"\U00010428");
        Check(FoldOrdinalCode(u"\u03c2") == FoldOrdinalCode(u"\u03c3"));
        Check(FoldOrdinalCode(u"\u0131") != FoldOrdinalCode(u"i"));
        std::vector<LexiconRow> source{{u" Z ", u"first", 1}, {u"z", u"second", 9},
            {u"a", u"other", 9}, {u"Z", u"second", 0}, {u"z", u"SECOND", 0}, {u" ", u"skip", 99}};
        auto assembled = AssembleCodedRows(source);
        Check(assembled.size() == 2 && assembled[0].first == u"z" && assembled[1].first == u"a");
        Check(assembled[0].second == std::vector<std::u16string>({u"second", u"first", u"SECOND"}));
        auto snapshot = tiger::core::CompactLexicon::Build(assembled);
        source.clear(); assembled.clear();
        Check(snapshot.Find(u"Z") == 0u && snapshot.Candidate(0, 0) == u"second");
        assembled = {{u"z", {u"old\x001e" u"commit", u"other", u"new\x001e" u"commit"}}};
        std::vector<std::u16string> adjustments{u"{\u6dfb\u52a0}Z\tcommit"};
        ApplyAdjustments(assembled, adjustments);
        Check(assembled[0].second == std::vector<std::u16string>({u"other", u"new\x001e" u"commit", u"old\x001e" u"commit"}));
        adjustments = {u"{\u524d\u79fb}z\tcommit"};
        ApplyAdjustments(assembled, adjustments);
        Check(assembled[0].second[0] == u"new\x001e" u"commit");
        adjustments = {u"{\u5220\u9664}z\tcommit", u"{\u5220\u9664}z\tother"};
        ApplyAdjustments(assembled, adjustments);
        Check(assembled.size() == 1 && assembled[0].second.empty());
        adjustments = {u"{\u7f6e\u9876}new\t#literal"};
        ApplyAdjustments(assembled, adjustments);
        Check(assembled[1].first == u"new" && assembled[1].second[0] == u"#literal");
        Check(CandidateCommitText(u"display\x001e") == u"display");
        Check(CandidateCommitText(u"\x001e" u"commit") == u"commit");
        assembled = {{u"z", {u"old\x001e" u"commit", u"other", u"duplicate\x001e" u"commit"}}};
        std::vector<std::u16string> custom{u"#comment\tignored", u"Z\tnew=>commit", u"new\t#literal", u"bad no-tab"};
        ApplyCustomRows(assembled, custom);
        Check(assembled.size() == 2);
        Check(assembled[0].second == std::vector<std::u16string>({u"new\x001e" u"commit", u"other"}));
        Check(assembled[1].second == std::vector<std::u16string>({u"#literal"}));
        Check(TextElementStarts(u"a\u0301b") == std::vector<std::size_t>({0, 2}));
        Check(TextElementStarts(u"\r\na") == std::vector<std::size_t>({0, 2}));
        Check(TextElementStarts(u"\U0001f469\u200d\U0001f4bb").size() == 1);
        Check(TextElementStarts(u"\U0001f1e6\U0001f1e7\U0001f1e8").size() == 2);
        ConstructCodeMap construct{{u"\u7532", u"abcd"}, {u"\u4e59", u"efgh"}, {u"\u4e19", u"ijkl"}, {u"\u4e01", u"mnop"}};
        Check(ConstructWordCode(u"\u7532\u4e59", construct) == u"abef");
        Check(ConstructWordCode(u"\u7532\u4e59\u4e19", construct) == u"aeij");
        Check(ConstructWordCode(u"\u7532\u4e59\u4e19\u4e01", construct) == u"aeim");
        Check(ConstructWordCode(u"A?Z", {}) == u"aazz");
        Check(ConstructWordCode(u"\u7532?\u4e59", construct) == u"abef");
        std::vector<LexiconRow> explicitCodes{{u"x", u"\u7532", 0}, {u"yyyy", u"\u7532", 999}};
        std::vector<CompactLexicon::Entry> constructEntries{{u"oabc", {u"\u4e59"}}, {u"zabc", {u"\u4e59"}},
            {u"abcd", {u"\u4e59"}}, {u"aabc", {u"\u4e59"}}, {u"q", {u"\u4e19"}}};
        auto constructionMap = BuildConstructCodeMap(explicitCodes, constructEntries);
        Check(constructionMap.at(u"\u7532") == u"x" && constructionMap.at(u"\u4e59") == u"aabc");
        Check(!constructionMap.contains(u"\u4e19"));
        std::vector<LexiconRow> noCode{{{}, u"\u4e59A", 7}, {{}, u"\u7532\u4e59", 9}}, inferredRows;
        AppendInferredRows(noCode, constructionMap, inferredRows);
        Check(inferredRows.size() == 1 && inferredRows[0].code == u"aaaa" && inferredRows[0].frequency == 7);
        Check(LoadSchemaLexicon({}).table.Count() == 0);
        std::vector<CompactLexicon::Entry> metadataEntries{{u"a", {u"word"}}, {u"ab", {u"word"}},
            {u"cd", {u"display\x001e" u"word"}}, {u";", {u"symbol"}}, {u";a", {u"detail"}}, {u"[", {}}, {u"z", {u"quick"}}};
        auto metadata = BuildLexiconMetadata(metadataEntries);
        Check(metadata.fullCodes.at(u"word") == u"ab");
        Check(metadata.nonTerminal.Contains(u"A") && !metadata.unique.Contains(u"a") && metadata.unique.Contains(u"AB"));
        Check(metadata.shortSemicolon && metadata.shortBracket && metadata.shortZ && !metadata.shortSlash);
        Check(!metadata.autoShort.Contains(u";") && metadata.autoShort.Contains(u";A") && metadata.autoShort.Contains(u"Z"));
        metadataEntries.emplace_back(u"xz", std::vector<std::u16string>{u"other"});
        Check(!BuildLexiconMetadata(metadataEntries).shortZ);
        Check(NormalizeCodeRoot(u" C:\\ ") == u"C:\\");
        Check(NormalizeCodeRoot(u"relative///") == u"relative");
        Check(NormalizeCodeRoot(u" ") == u"\u7801\u8868");
        Check(ParseConfigBool(u" ON ", false) && !ParseConfigBool(u"\u5426", true));
        Check(!ParseConfigBool(u"yes", false) && ParseConfigBool(u"yes", true));
        std::vector<std::u16string> configLines{u"\u5f53\u524d\u7801\u8868\tfirst", u"\u5f53\u524d\u7801\u8868 ", u"unknown\tignored"};
        auto settings = ParseConfigLines(configLines);
        Check(settings.size() == 50);
        auto history = SeedRecentSchemas(u" A |a| B |C");
        Check(SerializeRecentSchemas(history) == u"A|B");
        RecordRecentSchema(history, history.back());
        Check(SerializeRecentSchemas(history) == u"B|A");
        std::vector<std::u16string> candidates;
        for (int index = 0; index < 107; ++index) candidates.push_back(std::u16string(index + 1, u'x'));
        std::vector<CompactLexicon::Entry> pageEntries{{u"ab", candidates}, {u"empty", {}}, {u"", {u"not exposed"}}};
        auto pageTable = CompactLexicon::Build(pageEntries);
        Check(!ReadCandidatePage(pageTable, u" ", 0, 5).found);
        Check(!ReadCandidatePage(pageTable, u"missing", 0, 5).found);
        auto emptyPage = ReadCandidatePage(pageTable, u"empty", 100, 5);
        Check(emptyPage.found && emptyPage.total == 0 && emptyPage.pageIndex == 0 && emptyPage.entries.empty());
        for (int size = -2; size <= 12; ++size)
            for (int requested = -2; requested <= 120; ++requested)
            {
                auto page = ReadCandidatePage(pageTable, u" AB\u3000", requested, size);
                int actualSize = size < 1 ? 1 : size > 10 ? 10 : size;
                int maxPage = 106 / actualSize;
                int expectedPage = requested < 0 ? 0 : requested > maxPage ? maxPage : requested;
                Check(page.found && page.total == 107 && page.pageIndex == static_cast<unsigned>(expectedPage));
                int start = expectedPage * actualSize;
                int count = start + actualSize <= 107 ? actualSize : 107 - start;
                Check(page.entries.size() == static_cast<unsigned>(count));
                for (int index = 0; index < count; ++index) Check(page.entries[index] == candidates[start + index]);
            }
        Check(ReadCandidatePage(pageTable, u"ab", INT64_MAX, 10).pageIndex == 10);
        Check(ReadCandidatePage(pageTable, u"ab", INT64_MIN, 10).pageIndex == 0);
        CandidatePageTracker tracker;
        Check(tracker.Resolve(u"ab", 1, 107, 5) == 0);
        tracker.Move(u"ab", 1, 107, 5, 1); Check(tracker.Index() == 1);
        tracker.Move(u"ab", 1, 107, 5, 100); Check(tracker.Index() == 21);
        tracker.Move(u"ab", 1, 107, 5, 1); Check(tracker.Index() == 21); // no wrap
        Check(tracker.Resolve(u"ab", 1, 12, 5) == 2);
        Check(tracker.Resolve(u"ab", 1, 12, 10) == 1);
        Check(tracker.Resolve(u"AB", 1, 12, 5) == 0); // lookup equal, raw identity differs
        tracker.Move(u"AB", 1, 12, 5, 1);
        tracker.Move(u"other", 2, 0, 5, 0); Check(tracker.Index() == 1);
        Check(tracker.Resolve(u"AB", 2, 12, 5) == 0);
        tracker.Move(u"AB", 2, 12, 5, -1); Check(tracker.Index() == 0);
        tracker.Move(u"AB", 2, 12, 5, 1);
        Check(tracker.Resolve(u"AB", 2, 0, 5) == 0);
        tracker.Reset(); Check(tracker.Index() == 0);
        Check(CandidatePageKeyDelta(u"\u3000[ ]\u3000", 0xdd, false) == 1);
        Check(CandidatePageKeyDelta(u"Shift Tab/Tab", 9, true) == -1);
        Check(CandidatePageKeyDelta(u"shift tab/tab", 9, true) == 0);
        Check(CandidatePageKeyDelta(u"unknown", 0xbb, false) == 1);
        Check(!tracker.MoveKey(u"ab", 2, 20, 5, u"[ ]", 0x41, false));
        Check(tracker.MoveKey(u"ab", 2, 20, 5, u"[ ]", 0xdd, false) && tracker.Index() == 1);
        Check(CandidateDisplayText(u"shown\x1e" u"commit") == u"shown");
        Check(CandidateDisplayText(u"\x1e" u"commit") == u"commit");
        Check(CandidateDisplayText(u"shown\x1e") == u"shown");
        Check(CandidateDisplayText(u"\x1e").empty());
        OutputContext context;
        context.repeatBuffer = u"previous";
        int clockCalls = 0, randomCalls = 0;
        context.clock = [&] { ++clockCalls; return OutputClock{2026, 9, 9, 3, 4, 5, 3, u"Wednesday"}; };
        context.randomIndex = [&](std::size_t count) { ++randomCalls; return count - 1; };
        Check(NormalizeOutputAction(u"ordinary", context).text == u"ordinary");
        Check(NormalizeOutputAction(u"", context).text.empty());
        Check(NormalizeOutputAction(u"{\u6dfb\u52a0}", context).action == OutputAction::OpenAddWord);
        Check(NormalizeOutputAction(u"{\u52a0\u8bcd}", context).action == OutputAction::OpenAddWord);
        Check(NormalizeOutputAction(u"{\u9690\u85cf\u5019\u9009}", context).action == OutputAction::ToggleHideCandidates);
        Check(NormalizeOutputAction(u"{\u91cd\u590d\u4e0a\u5c4f}", context).text == u"previous");
        Check(NormalizeOutputAction(u"\u3002", context).text == u"\u3002");
        context.dotAfterDigit = true;
        Check(NormalizeOutputAction(u"\u3002", context).text == u".");
        Check(NormalizeOutputAction(u"x\u3002", context).text == u"x\u3002");
        Check(NormalizeOutputAction(u"{||a||b|}", context).text == u"b");
        Check(NormalizeOutputAction(u"{a| }", context).text == u" ");
        Check(NormalizeOutputAction(u"{|||}", context).text == u"{|||}");
        Check(NormalizeOutputAction(u"{a|{\u6dfb\u52a0}}", context).action == OutputAction::Text);
        Check(clockCalls == 0 && randomCalls == 3);
        Check(NormalizeOutputAction(u"{\u65e5\u671f}", context).text == u"2026\u5e7409\u670809\u65e5");
        Check(NormalizeOutputAction(u"{\u65e5\u671f.}", context).text == u"2026.09.09");
        Check(NormalizeOutputAction(u"{\u65e5\u671f-}", context).text == u"2026-09-09");
        Check(NormalizeOutputAction(u"{\u65e5\u671f/}", context).text == u"2026/09/09");
        Check(NormalizeOutputAction(u"{\u65f6\u5206\u79d2}", context).text == u"03:04:05");
        Check(NormalizeOutputAction(u"{\u65f6\u5206}", context).text == u"03:04");
        Check(NormalizeOutputAction(u"{\u661f\u671f}", context).text == u"Wednesday");
        Check(NormalizeOutputAction(u"{\u5468}", context).text == u"\u5468\u4e09");
        OutputRandom random;
        Check(random.Choose(1) == 0);
        for (std::size_t count = 1; count < 100; ++count)
            for (int trial = 0; trial < 100; ++trial) Check(random.Choose(count) < count);
        bool rejectedRandom = false;
        try { random.Choose(0); } catch (const std::invalid_argument&) { rejectedRandom = true; }
        Check(rejectedRandom);
        OutputState outputState;
        Check(outputState.RepeatBuffer() == u"\u91cd\u590d\u4e0a\u5c4f");
        outputState.Observe(0x31, true, false, {});
        Check(outputState.DecimalArmed());
        outputState.Observe(0x11, true, false, {}, false, true);
        Check(outputState.DecimalArmed()); // modifiers preserve the arm
        Check(outputState.Normalize(u"\u3002", {}).text == u".");
        outputState.Observe(0xbe, true, true, u".");
        Check(!outputState.DecimalArmed() && outputState.RepeatBuffer() == u".");
        outputState.Observe(0x41, false, true, u"word7");
        Check(!outputState.DecimalArmed() && outputState.RepeatBuffer() == u"word7"); // keyup still records handled output
        outputState.Observe(0x41, true, false, u"\uff19");
        Check(outputState.DecimalArmed() && outputState.RepeatBuffer() == u"word7");
        outputState.ClearDecimalArm(); Check(!outputState.DecimalArmed());
        Check(outputState.Normalize(u"{\u91cd\u590d\u4e0a\u5c4f}", {}).text == u"word7");
        Check(IsOutputEndingWithDigit(u"x9") && IsOutputEndingWithDigit(u"x\uff19"));
        Check(!IsOutputEndingWithDigit(u"9\u0301") && !IsOutputEndingWithDigit(u"\u06009"));
        Check(!IsOutputEndingWithDigit(u"\u0661") && !IsOutputEndingWithDigit(u"1\ufe0f\u20e3"));
        SendHistory historyState;
        Check(historyState.VisibleCount() == 0 && historyState.LastWord(10).empty());
        historyState.Backspace();
        historyState.Append(u"ab\u0301\U0001f600");
        Check(historyState.VisibleCount() == 3 && historyState.LastWord(2) == u"b\u0301\U0001f600");
        historyState.Backspace(); Check(historyState.LastWord(1) == u"b\u0301");
        Check(historyState.EmitQuote(true) == u"\u201c"); historyState.Append(u"\u201c");
        Check(historyState.EmitQuote(true) == u"\u201d"); historyState.Append(u"\u201d");
        historyState.Backspace(); Check(historyState.EmitQuote(true) == u"\u201c");
        historyState.Append(u":"); Check(historyState.EmitQuote(true) == u"\u201c");
        Check(historyState.EmitQuote(false) == u"\u2018");
        historyState.Append(u"x"); Check(historyState.EmitQuote(false) == u"\u2019");
        historyState.Append(std::u16string(30, u'z'));
        Check(historyState.VisibleCount() == 20 && historyState.LastWord(100) == std::u16string(20, u'z'));
        Check(historyState.LastWord(-1).empty());
        Check(GuessPassThroughText(0x41, true, true) == u"a");
        Check(GuessPassThroughText(0x30, true, false) == u")");
        Check(GuessPassThroughText(0xba, true, false) == u":");
        Check(!GuessPassThroughText(0x08, false, false));
        KeyPostProcessor post;
        PostProcessResult passed;
        post.Process(0x31, true, false, false, false, false, false, passed);
        Check(post.History().LastWord(20) == u"1" && post.Output().DecimalArmed());
        PostProcessResult dot{true, u"\u3002", {}, false};
        post.Process(0xbe, true, false, false, false, false, false, dot);
        Check(dot.text == u"." && post.History().LastWord(20) == u"1.");
        PostProcessResult repeat{true, u"{\u91cd\u590d\u4e0a\u5c4f}", {}, false};
        post.Process(0x20, true, false, false, false, false, false, repeat);
        Check(repeat.text == u"." && post.History().LastWord(20) == u"1..");
        post.Process(0x41, true, false, true, false, false, false, passed);
        Check(post.History().LastWord(20) == u"1.."); // Ctrl+A is not guessed text
        post.Process(0x08, true, false, false, false, false, false, passed);
        Check(post.History().LastWord(20) == u"1.");
        PostProcessResult add{true, u"{\u6dfb\u52a0}", u"ab", true};
        post.Process(0x20, true, false, false, false, false, false, add);
        Check(add.action == OutputAction::OpenAddWord && add.text.empty() && !add.composing && add.inputBuffer.empty());
        Check(post.Output().RepeatBuffer() == u"." && post.History().LastWord(20) == u"1.");
        OutputState punctuation;
        Check(ResolveChineseSymbol(0xbe, false, true, punctuation) == u"\u3002");
        punctuation.Observe(0x31, true, false, {});
        Check(ResolveChineseSymbol(0xbc, false, true, punctuation) == u"\uff0c" && punctuation.DecimalArmed());
        Check(ResolveChineseSymbol(0xbe, false, true, punctuation) == u"." && !punctuation.DecimalArmed());
        Check(ResolveChineseSymbol(0xbf, false, false, punctuation) == u"/");
        Check(ResolveChineseSymbol(0xbf, false, true, punctuation) == u"\u3001");
        Check(!ResolveChineseSymbol(0xde, true, true, punctuation));
        Check(ResolveShiftChineseSymbol(0x36, false) == u"\u2026\u2026");
        Check(ResolveShiftChineseSymbol(0x36, true) == u"^");
        Check(ResolveShiftChineseSymbol(0xbd, false) == u"\u2014\u2014");
        Check(!ResolveShiftChineseSymbol(0xde, false));
        SelectionKeys selection;
        std::vector<std::u16string> selectPage{u"shown\x1e" u"commit", u"second"};
        Check(selection.Select(0x31, selectPage).text == u"commit");
        Check(selection.Select(0x32, selectPage).text == u"second");
        Check(selection.Select(0x33, selectPage).text == u"commit3");
        Check(selection.Select(0x30, selectPage).text == u"commit0");
        Check(!selection.Select(0x61, selectPage).recognized); // numpad is not bound by default
        Check(selection.Select(0x31, {}).recognized && selection.Select(0x31, {}).text.empty());
        SelectionKeys customSelection({{2, {0x10, 0xba}}, {3, {0xa1}}, {5, {0xba, 0x61}}, {8, {0xde}}});
        Check(customSelection.Number(0xa0) == 2 && customSelection.Number(0xa1) == 3);
        Check(customSelection.Number(0xba) == 2); // smaller selection number wins duplicate binding
        Check(customSelection.Select(0xde, selectPage).recognized && customSelection.Select(0xde, selectPage).text.empty());
        Check(customSelection.Select(0x61, selectPage).text == u"commit5"); // append mapped rank, not physical key digit
        std::vector<std::u16string> macroPage{u"{\u91cd\u590d\u4e0a\u5c4f}"};
        OutputContext selectionContext; selectionContext.repeatBuffer = u"previous";
        Check(selection.Select(0x33, macroPage, selectionContext).text == u"previous3");
        macroPage[0] = u"{\u6dfb\u52a0}";
        Check(selection.Select(0x31, macroPage).text == u"{\u6dfb\u52a0}");
        Check(selection.Select(0x33, macroPage).text == u"{\u6dfb\u52a0}3");
        Check(ResolveSelectionVirtualKey(0x10, 0x36, false) == 0xa1);
        Check(ResolveSelectionVirtualKey(0x10, 0, false) == 0x10);
        Check(ResolveSelectionVirtualKey(0x11, 0, true) == 0xa3);
        std::vector<std::u16string> selectionLines{u"# comment", u"1\u9009", u"2\u9009 vk_lshift 160 0xA0 VK_F24", u"10\u9009 -1 0xffffffff"};
        auto parsedSelection = ParseSelectionBindings(selectionLines);
        Check(parsedSelection.success && parsedSelection.bindings.at(1).empty());
        Check(parsedSelection.bindings.at(2) == std::vector<int>({0xa0, 0x87}));
        Check(parsedSelection.bindings.at(10) == std::vector<int>({-1}));
        Check(SelectionKeys(parsedSelection.bindings).Number(0xa0) == 2);
        selectionLines = {u"2\u9009 VK_A", u"3\u9009 VK_OEM_PLUS"}; // name deliberately absent in C# map
        parsedSelection = ParseSelectionBindings(selectionLines);
        Check(!parsedSelection.success && parsedSelection.bindings.at(2) == std::vector<int>({0x41}));
        Check(parsedSelection.bindings.at(3) == std::vector<int>({0x33}));
        for (auto invalid : {u"0\u9009 VK_A", u"11\u9009 VK_A", u"1\u9009 0x100000000", u"1\u9009 2147483648", u"1\u9009 VK_F01", u"1\u9009 VK_A #not-inline-comment"})
        {
            selectionLines = {invalid}; Check(!ParseSelectionBindings(selectionLines).success);
        }
        selectionLines = {u"+1\u9009 0x80000000 -2147483648", u"2\u9009 VK_0 VK_Z VK_F1"};
        parsedSelection = ParseSelectionBindings(selectionLines);
        Check(parsedSelection.success && parsedSelection.bindings.at(1) == std::vector<int>({INT32_MIN}));
        Check(parsedSelection.bindings.at(2) == std::vector<int>({0x30, 0x5a, 0x70}));
        SelectionBindings formatted{{1, {0, -1, 0, INT32_MIN}}, {10, {256}}, {11, {99}}};
        auto bindingText = BuildSelectionBindingsText(formatted);
        Check(bindingText.starts_with(u"1\u9009 0x00 0xFFFFFFFF 0x80000000\r\n2\u9009\r\n"));
        Check(bindingText.ends_with(u"10\u9009 0x100"));
        Check(bindingText.find(u"11\u9009") == bindingText.npos);
        auto selectionFileText = BuildSelectionFileText(formatted);
        Check(selectionFileText.ends_with(bindingText));
        selectionLines.clear();
        std::size_t lineStart = 0;
        while (true)
        {
            auto lineEnd = selectionFileText.find(u"\r\n", lineStart);
            selectionLines.push_back(selectionFileText.substr(lineStart, lineEnd == selectionFileText.npos ? lineEnd : lineEnd - lineStart));
            if (lineEnd == selectionFileText.npos) break;
            lineStart = lineEnd + 2;
        }
        auto roundTripSelection = ParseSelectionBindings(selectionLines);
        Check(roundTripSelection.success);
        Check(roundTripSelection.bindings.at(1) == std::vector<int>({0, -1, INT32_MIN}));
        Check(roundTripSelection.bindings.at(2).empty());
        Check(BuildSelectionFileText(roundTripSelection.bindings) == selectionFileText);
        std::vector<CompactLexicon::Entry> ordinaryEntries{
            {u"ab", {u"first", u"second", u"third"}}, {u"abc", {u"long"}}, {u"xy", {u"unique"}}, {u"abd", {}}};
        SchemaLexicon ordinarySchema{CompactLexicon::Build(ordinaryEntries), {}, BuildLexiconMetadata(ordinaryEntries)};
        OrdinaryComposition ordinary;
        OrdinarySettings ordinarySettings;
        ordinarySettings.maxCodeAutoCommit = false;
        ordinarySettings.clearOnNoCode = false;
        ordinarySettings.tabClear = false;
        ordinarySettings.maxCodeLength = 2;
        ordinarySettings.pageSize = 2;
        auto typed = ordinary.AppendLetter(u'a', ordinarySchema, ordinarySettings);
        Check(typed.handled && typed.composing && typed.inputBuffer == u"a" && typed.text.empty());
        ordinary.AppendLetter(u'b', ordinarySchema, ordinarySettings);
        Check(ordinary.Page(ordinarySchema, ordinarySettings).entries.size() == 2);
        Check(ordinary.EditKey(0xbb, false, ordinarySchema, ordinarySettings, selection)->composing);
        Check(ordinary.Page(ordinarySchema, ordinarySettings).entries == std::vector<std::u16string>{u"third"});
        auto picked = ordinary.EditKey(32, false, ordinarySchema, ordinarySettings, selection);
        Check(picked->text == u"third" && !picked->composing && !ordinary.Active());
        ordinary.AppendLetter(u'a', ordinarySchema, ordinarySettings);
        ordinary.AppendLetter(u'b', ordinarySchema, ordinarySettings);
        typed = ordinary.AppendLetter(u'c', ordinarySchema, ordinarySettings);
        Check(typed.text.empty() && ordinary.Raw() == u"abc"); // known codes may exceed max
        typed = ordinary.AppendLetter(u'd', ordinarySchema, ordinarySettings);
        Check(typed.text == u"long" && ordinary.Raw() == u"d");
        auto literal = ordinary.EditKey(13, false, ordinarySchema, ordinarySettings, selection);
        Check(literal->text == u"d" && !ordinary.Active());
        ordinarySettings.maxCodeAutoCommit = true;
        ordinary.AppendLetter(u'x', ordinarySchema, ordinarySettings);
        typed = ordinary.AppendLetter(u'y', ordinarySchema, ordinarySettings);
        Check(typed.text == u"unique" && !ordinary.Active());
        ordinary.AppendLetter(u'q', ordinarySchema, ordinarySettings);
        ordinary.AppendLetter(u'q', ordinarySchema, ordinarySettings);
        ordinary.AppendLetter(u'q', ordinarySchema, ordinarySettings);
        Check(ordinary.Raw() == u"qqq"); // unknown code retained when clearing disabled
        ordinarySettings.clearOnNoCode = true;
        ordinary.AppendLetter(u'a', ordinarySchema, ordinarySettings);
        Check(ordinary.Raw() == u"a");
        auto tabPass = ordinary.EditKey(9, false, ordinarySchema, ordinarySettings, selection);
        Check(tabPass.has_value() && !tabPass->handled && ordinary.Raw() == u"a");
        Check(!ordinary.EditKey(0x70, false, ordinarySchema, ordinarySettings, selection));
        auto erased = ordinary.EditKey(8, false, ordinarySchema, ordinarySettings, selection);
        Check(!erased->composing && ordinary.Raw().empty());
        ordinary.AppendLetter(u'a', ordinarySchema, ordinarySettings);
        ordinary.AppendLetter(u'b', ordinarySchema, ordinarySettings);
        typed = ordinary.AppendLetter(u'd', ordinarySchema, ordinarySettings);
        Check(typed.text == u"first" && ordinary.Raw() == u"d"); // empty bucket is not HasCode
        OutputState ordinaryOutput;
        SendHistory ordinaryHistory;
        auto prepareAb = [&]
        {
            ordinary.Clear();
            ordinary.AppendLetter(u'a', ordinarySchema, ordinarySettings);
            ordinary.AppendLetter(u'b', ordinarySchema, ordinarySettings);
        };
        prepareAb();
        auto punctuated = ordinary.KeyDown(0xbc, false, ordinarySchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory);
        Check(punctuated->text == u"first\uff0c" && !ordinary.Active());
        prepareAb();
        Check(ordinary.KeyDown(0xba, false, ordinarySchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory)->text == u"second");
        prepareAb();
        Check(ordinary.KeyDown(0xde, false, ordinarySchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory)->text == u"first\u2018"); // page has only two entries
        prepareAb();
        SelectionKeys symbolSelection(SelectionBindings{{2, {0xbc}}});
        Check(ordinary.KeyDown(0xbc, false, ordinarySchema, ordinarySettings, symbolSelection, ordinaryOutput, ordinaryHistory)->text == u"second");
        prepareAb();
        Check(ordinary.KeyDown(0xbc, true, ordinarySchema, ordinarySettings, symbolSelection, ordinaryOutput, ordinaryHistory)->text == u"first\u300a"); // Shift punctuation precedes selection
        ordinary.AppendLetter(u'q', ordinarySchema, ordinarySettings);
        Check(ordinary.KeyDown(0xbc, true, ordinarySchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory)->text.empty());
        std::vector<CompactLexicon::Entry> shortEntries{{u"ab", {u"word"}}, {u";a", {u"short"}}, {u"/a", {u"slash"}}, {u"[a", {u"bracket"}}};
        SchemaLexicon shortSchema{CompactLexicon::Build(shortEntries), {}, BuildLexiconMetadata(shortEntries)};
        ordinarySettings.maxCodeAutoCommit = false;
        ordinarySettings.secondSemicolon = false;
        auto shortStart = ordinary.KeyDown(0xba, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory);
        Check(shortStart->composing && ordinary.Raw() == u";");
        Check(ordinary.KeyDown(0xba, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory)->text == u"\uff1b");
        ordinary.KeyDown(0xdb, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory);
        Check(ordinary.KeyDown(32, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory)->text == u"\u3010");
        ordinary.KeyDown(0x41, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory);
        ordinary.KeyDown(0x42, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory);
        auto reentered = ordinary.KeyDown(0xba, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory);
        Check(reentered->text == u"word" && !reentered->composing && reentered->inputBuffer.empty());
        Check(ordinary.Active() && ordinary.Raw() == u";"); // hidden response is not cleared state
        Check(ordinary.KeyDown(0x41, false, shortSchema, ordinarySettings, selection, ordinaryOutput, ordinaryHistory)->text == u"short");
        Check(!ordinary.Active());
        int upperCommits = 0;
        UpperCaseComposition upper({[&](std::u16string_view raw)
            { ++upperCommits; return std::u16string(raw); },
            [](std::u16string_view raw) { return raw == u"S1"; }});
        Check(upper.Start(u'A').inputBuffer == u"A");
        Check(upper.KeyDown(0x42, false, true).inputBuffer == u"Ab");
        Check(upper.KeyDown(0x43, true, true).inputBuffer == u"AbC");
        Check(upper.KeyDown(0x31, false, true).inputBuffer == u"AbC1");
        Check(!upper.KeyDown(9, false, false).handled && upper.Raw() == u"AbC1");
        Check(!upper.KeyDown(0x61, false, false).handled && upper.Raw() == u"AbC1"); // numpad is not top-row input
        Check(upper.KeyDown(0xde, true, true).text == u"AbC1\"" && !upper.Active());
        Check(upperCommits == 1);
        upper.Start(u'S'); upper.KeyDown(0x31, false, true);
        Check(upper.KeyDown(0xbe, false, true).inputBuffer == u"S1.");
        Check(upperCommits == 1); // separator appends without committing
        Check(upper.KeyDown(13, false, true).text == u"S1." && upperCommits == 2);
        upper.Start(u'A');
        Check(!upper.KeyDown(8, false, true).composing && !upper.Active());
        upper.Start(u'A'); upper.KeyDown(27, false, true);
        Check(upperCommits == 2 && !upper.Active());
        bool missingUpperService = false;
        try { UpperCaseComposition invalid({}); } catch (const std::invalid_argument&) { missingUpperService = true; }
        Check(missingUpperService);
        for (auto raw : {u"D1.", u"d.5e-400", u"S1e99999", u"S+NaN", u"D\u3000Infinity\u3000"})
            Check(IsUpperCaseNumericPrefix(raw));
        for (auto raw : {u"s1", u"DS1", u"S1,000", u"D1e+", u"S\u30001\u3000", u"D."})
            Check(!IsUpperCaseNumericPrefix(raw));
        UpperCaseComposition numericUpper({[](std::u16string_view raw) { return std::u16string(raw); }});
        numericUpper.Start(u'S'); numericUpper.KeyDown(0x31, false, true);
        Check(numericUpper.KeyDown(0xbc, false, true).inputBuffer == u"S1,");
        Check(numericUpper.KeyDown(0xbe, false, true).text == u"S1,."); // comma invalidates the next numeric-prefix check
        Check(ClassifyUpperCaseCommand(u"Ds1\n") == UpperCaseCommand::Timer);
        Check(ClassifyUpperCaseCommand(u"Ds1\r\n") == UpperCaseCommand::Literal);
        Check(ClassifyUpperCaseCommand(u"S,") == UpperCaseCommand::Currency);
        Check(ClassifyUpperCaseCommand(u"S1e2") == UpperCaseCommand::Literal);
        Check(IsUpperCaseNumericPrefix(u"S1e2")); // recognition and separator rules intentionally differ
        std::vector<std::u16string> requestedTimers, requestedCurrency;
        auto commandServices = MakeUpperCaseServices(
            [&](std::u16string_view code) { requestedCurrency.emplace_back(code); return u"converted"; },
            [&](std::u16string_view code) { requestedTimers.emplace_back(code); });
        UpperCaseComposition commandUpper(commandServices);
        commandUpper.Start(u'D'); commandUpper.KeyDown(0x53, false, true); commandUpper.KeyDown(0x31, false, true);
        Check(commandUpper.KeyDown(32, false, true).text.empty());
        Check(!commandUpper.Active() && requestedTimers == std::vector<std::u16string>{u"Ds1"});
        commandUpper.Start(u'S'); commandUpper.KeyDown(0x31, false, true);
        Check(commandUpper.KeyDown(0xba, false, true).text == u"converted;");
        Check(requestedCurrency == std::vector<std::u16string>{u"S1"});
        Check(commandServices.commit(u"S,") == u"converted" && requestedCurrency.back() == u"S,");
        Check(commandServices.commit(u"Abc") == u"Abc" && requestedTimers.size() == 1 && requestedCurrency.size() == 2);
        Check(ConvertUpperCaseCurrency(u"S0").empty());
        Check(ConvertUpperCaseCurrency(u"S0.005") == u"\u58f9\u5206");
        Check(ConvertUpperCaseCurrency(u"S1.005") == u"\u58f9\u5143\u96f6\u58f9\u5206");
        Check(ConvertUpperCaseCurrency(u"S100000001") == u"\u58f9\u4ebf\u96f6\u58f9\u5143\u6574");
        Check(ConvertUpperCaseCurrency(u"S10001000") == u"\u58f9\u4edf\u4e07\u58f9\u4edf\u5143\u6574");
        Check(ConvertUpperCaseCurrency(u"S79228162514264337593543950336") == u"\u6570\u5b57\u683c\u5f0f\u9519\u8bef!");
        UpperCaseComposition currencyUpper(MakeUpperCaseServices([](std::u16string_view) {}));
        currencyUpper.Start(u'S'); currencyUpper.KeyDown(0x31, false, true);
        Check(currencyUpper.KeyDown(13, false, true).text == u"\u58f9\u5143\u6574");
        PinyinComposition py;
        std::vector<CompactLexicon::Entry> pyEntries{{u"a", {u"one", u"two", u"three"}}};
        auto pyTable = CompactLexicon::Build(pyEntries);
        OrdinarySettings pySettings;
        pySettings.pageSize = 2;
        Check(py.Start().inputBuffer == u"\u00b7");
        Check(py.KeyDown(0x41, true, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {}).inputBuffer == u"\u00b7a");
        py.KeyDown(0xbb, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {});
        Check(py.Page(pyTable, pySettings).entries == std::vector<std::u16string>{u"three"});
        Check(py.KeyDown(32, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {}).text == u"three");
        py.Start();
        Check(py.KeyDown(0xc0, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {}).text == u"\u00b7");
        py.Start(); py.KeyDown(0x41, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {});
        Check(!py.KeyDown(8, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {}).composing);
        Check(!py.Active());
        py.Start(); py.KeyDown(0x41, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {});
        Check(py.KeyDown(13, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {}).text == u"\u00b7a");
        py.Start(); py.KeyDown(0x41, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {});
        SelectionKeys pyRebound(SelectionBindings{{1, {0xba, 0xbb}}});
        Check(py.KeyDown(0xba, false, true, pyTable, pySettings, pyRebound, ordinaryOutput, ordinaryHistory, {}).text == u"two"); // built-in pinyin second selection precedes custom
        py.Start(); py.KeyDown(0x41, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {});
        Check(py.KeyDown(0xbb, false, true, pyTable, pySettings, pyRebound, ordinaryOutput, ordinaryHistory, {}).composing); // paging precedes custom selection
        Check(py.Page(pyTable, pySettings).pageIndex == 1);
        bool missingPyTransition = false;
        try { py.KeyDown(0xbc, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, {}); }
        catch (const std::logic_error&) { missingPyTransition = true; }
        Check(missingPyTransition && py.Raw() == u"\u00b7a");
        std::u16string delegatedCandidate;
        auto delegated = py.KeyDown(0xbc, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory,
            [&](int vk, bool shift, std::u16string candidate)
            {
                Check(vk == 0xbc && !shift);
                delegatedCandidate = candidate;
                return PostProcessResult{true, candidate + u"!", {}, false};
            });
        Check(delegatedCandidate == u"three" && delegated.text == u"three!" && !py.Active());
        OrdinaryComposition transitionOrdinary;
        SchemaLexicon transitionSchema{CompactLexicon::Build({}), {}, {}};
        auto transition = [&](int vk, bool shift, std::u16string candidate)
        {
            return CommitCandidateThenSymbol(vk, shift, candidate, transitionOrdinary,
                py, transitionSchema, pyTable, pySettings, true, ordinaryOutput);
        };
        py.Start(); py.KeyDown(0x41, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, transition);
        Check(py.KeyDown(0xbc, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, transition).text == u"one\uff0c");
        Check(!py.Active() && !transitionOrdinary.Active());
        py.Start();
        Check(py.KeyDown(0xbc, false, true, pyTable, pySettings, selection, ordinaryOutput, ordinaryHistory, transition).text.empty());
        Check(!py.Active());
        Check(transition(0xbe, false, u"42").text == u"42.");
        auto restarted = transition(0xc0, false, u"one");
        Check(restarted.text == u"one" && !restarted.composing && restarted.inputBuffer.empty());
        Check(py.Raw() == u"\u00b7"); // Hidden response echo must not clear new mode.
        transitionSchema.metadata.shortSlash = true;
        auto shortRestart = transition(0xbf, false, u"one");
        Check(shortRestart.text == u"one" && !shortRestart.composing && shortRestart.inputBuffer.empty());
        Check(!py.Active() && transitionOrdinary.Raw() == u"/");
        Check(transitionOrdinary.KeyDown(32, false, transitionSchema, pySettings, selection, ordinaryOutput, ordinaryHistory)->text == u"\u3001");
        bool invalidTransition = false;
        py.Start();
        try { transition(0x41, false, u"one"); }
        catch (const std::invalid_argument&) { invalidTransition = true; }
        Check(invalidTransition && py.Active());
        ChineseInputSession session(MakeUpperCaseServices([](std::u16string_view) {}));
        std::vector<CompactLexicon::Entry> sessionEntries{{u"a", {u"hello"}}, {u"r", {u"{\u91cd\u590d\u4e0a\u5c4f}"}}};
        SchemaLexicon sessionSchema{CompactLexicon::Build(sessionEntries), {}, BuildLexiconMetadata(sessionEntries)};
        OrdinarySettings sessionSettings;
        sessionSettings.maxCodeAutoCommit = false;
        auto sessionKey = [&](int vk, bool shift = false)
        {
            return session.KeyDown(vk, shift, sessionSchema, pyTable, sessionSettings, true, selection);
        };
        sessionKey(65); Check(sessionKey(32).text == u"hello");
        sessionKey(82); Check(sessionKey(32).text == u"hello");
        Check(session.PostProcessor().History().VisibleCount() == 10);
        Check(!sessionKey(49).handled);
        Check(sessionKey(0xbe).text == u".");
        auto quote = sessionKey(0xde).text;
        sessionKey(8);
        Check(quote == u"\u2018");
        Check(sessionKey(0xde).text == u"\u2019"); // Preserve C# deleted-quote inversion.
        auto addBinding = ShortcutGesture::Create(0xbb, true, false, false);
        auto recentBinding = ShortcutGesture::Create(0x4d, true, false, false);
        Check(addBinding && recentBinding && !ShortcutGesture::Create(0x4d, false, false, true));
        Check(!ShortcutGesture::Create(0xa2, true, false, false));
        Check(ShortcutGesture::Create(0x14, true, false, false).has_value()); // CapsLock is gesture-eligible.
        Check(!addBinding->Matches(0xbb, true, true, false, false));
        ActionShortcuts actions;
        int switched = 0;
        auto actionKey = [&](int vk)
        {
            return actions.KeyDown(vk, false, true, false, false, addBinding, recentBinding,
                [&] { ++switched; return true; });
        };
        Check(actionKey(0xbb) == ShortcutOutcome::AddWord);
        Check(actionKey(0xbb) == ShortcutOutcome::SuppressedAddRepeat);
        actions.ReleaseKey(0xbb);
        Check(actionKey(0xbb) == ShortcutOutcome::AddWord);
        Check(actionKey(0x4d) == ShortcutOutcome::SchemaSwitched && switched == 1);
        actions.ReleaseKey(0x4d);
        Check(actionKey(0xbb) == ShortcutOutcome::SuppressedRollover);
        actions.ReleaseModifiers(0xa2, false, true, false, false);
        Check(actionKey(0x4d) == ShortcutOutcome::SuppressedRollover);
        actions.ReleaseModifiers(0xa2, false, false, false, false);
        Check(actionKey(0x4d) == ShortcutOutcome::SchemaSwitched && switched == 2);
        actions.Reset();
        Check(actions.KeyDown(0x4d, false, true, false, false, addBinding, recentBinding, [] { return false; }) == ShortcutOutcome::None);
        Check(actionKey(0x4d) == ShortcutOutcome::SchemaSwitched && switched == 3);
        auto duplicate = addBinding;
        FilterActionBindings(addBinding, duplicate, true, true, true, true);
        Check(!addBinding && !duplicate);
        Check(ShortcutGesture::Parse(u" Ctrl + vk_m ")->ConfigString() == u"Ctrl+VK_M");
        Check(ShortcutGesture::Parse(u"Ctrl+0x0+VK_M")->ConfigString() == u"Ctrl+VK_M");
        Check(!ShortcutGesture::Parse(u"Ctrl+Ctrl+VK_M"));
        Check(!ShortcutGesture::Parse(u"Shift+VK_A"));
        Check(ShortcutGesture::Parse(u"Alt+0x14")->ConfigString() == u"Alt+0x14");
        Check(LoadActionBindings({}).addWord->ConfigString() == u"Ctrl+VK_OEM_PLUS");
        Check(!LoadActionBindings({}).recentSchema);
        Check(LoadActionBindings({{u"\u624b\u52a8\u52a0\u8bcd\u5feb\u6377\u952e", u""}}).addWord->ConfigString() == u"Ctrl+VK_OEM_PLUS");
        Check(!LoadActionBindings({{u"Ctrl+\u7b49\u53f7\u624b\u52a8\u52a0\u8bcd", u"false"}}).addWord);
        Check(!LoadActionBindings({{u"\u624b\u52a8\u52a0\u8bcd\u5feb\u6377\u952e", u"Ctrl+VK_SPACE"}}).addWord);
        ShiftToggleState shiftState;
        auto shiftEvent = [&](int vk, bool down, bool ctrl = false, bool enabled = true)
        {
            return shiftState.Process(vk, down, !down, ctrl, false, false, enabled);
        };
        Check(!shiftEvent(65, true).intercepted);
        Check(!shiftEvent(0xa0, false).toggle); // stray release
        Check(shiftEvent(0xa0, true).intercepted && shiftState.Down());
        Check(!shiftEvent(0xa0, true).toggle); // repeat
        Check(shiftEvent(0xa0, false).toggle && !shiftState.Down());
        shiftEvent(0xa0, true); shiftState.ObserveOtherKeyDown(65);
        Check(!shiftEvent(0xa0, false).toggle);
        CtrlSpaceState ctrlSpace;
        auto ctrlEvent = [&](int vk, bool down, int milliseconds, int repeat = 1, bool shift = false, bool enabled = true)
        {
            return ctrlSpace.Process(vk, down, !down, repeat, shift, false, false, enabled,
                CtrlSpaceState::Time{} + std::chrono::milliseconds(milliseconds));
        };
        Check(!ctrlEvent(0xa2, true, 0));
        Check(ctrlEvent(32, true, 1));
        Check(!ctrlEvent(32, true, 2, 2));
        Check(!ctrlEvent(32, false, 3));
        Check(!ctrlEvent(0xa2, false, 4));
        Check(!ctrlEvent(0xa2, true, 5));
        Check(ctrlEvent(32, true, 6));
        ctrlSpace.Reset();
        ctrlEvent(0xa2, true, 0); ctrlEvent(0xa2, false, 1);
        Check(ctrlEvent(32, false, 251)); // inclusive grace, missed Space-down allowed
        ctrlSpace.Reset();
        ctrlEvent(0xa2, true, 0); ctrlEvent(0xa2, false, 1);
        Check(!ctrlEvent(32, false, 252));
        ctrlSpace.Reset();
        ctrlEvent(0xa2, true, 0); ctrlEvent(0x4d, true, 1); ctrlEvent(0xa2, false, 2);
        Check(!ctrlEvent(32, true, 3) && !ctrlEvent(32, false, 4));
        ctrlSpace.Reset();
        ctrlEvent(32, true, 0); ctrlEvent(0xa2, true, 1);
        Check(!ctrlEvent(32, false, 2)); // reverse key order does not arm
        ctrlSpace.Reset();
        ctrlEvent(0xa2, true, 0);
        Check(!ctrlEvent(32, true, 1, 2));
        Check(ctrlEvent(0xa2, false, 2)); // release fallback after repeat-only Space
        ctrlSpace.Reset();
        ctrlEvent(0xa2, true, 0);
        Check(!ctrlEvent(32, true, 1, 1, true));
        Check(!ctrlEvent(32, false, 2));
        ctrlEvent(0xa2, true, 3);
        Check(!ctrlEvent(32, true, 4, 1, false, false));
        ChineseInputSession languageSession(MakeUpperCaseServices([](std::u16string_view)
            { throw std::logic_error("Language switch must not schedule timers"); }));
        auto languageKey = [&](int vk, bool shift = false)
        {
            return languageSession.KeyDown(vk, shift, sessionSchema, pyTable, sessionSettings, true, selection);
        };
        languageKey(65);
        Check(languageSession.SetChinese(true).empty() && languageSession.Raw() == u"a");
        Check(languageSession.SetChinese(false) == u"a");
        Check(!languageSession.IsChinese() && languageSession.Raw().empty());
        Check(languageSession.PostProcessor().History().LastWord(1) == u"a");
        Check(!languageKey(65).handled && languageSession.Raw().empty());
        Check(languageSession.ToggleChinese().empty() && languageSession.IsChinese());
        languageKey(0xc0); languageKey(65);
        Check(languageSession.ToggleChinese() == u"\u00b7a"); // marker is part of literal code
        languageSession.SetChinese(true);
        languageKey(83, true); languageKey(49);
        Check(languageSession.ToggleChinese() == u"S1"); // do not run currency conversion
        languageKey(49);
        Check(languageSession.PostProcessor().Output().DecimalArmed());
        languageSession.SetChinese(false); // redundant change is a no-op
        Check(languageSession.PostProcessor().Output().DecimalArmed());
        languageSession.SetChinese(true);
        Check(!languageSession.PostProcessor().Output().DecimalArmed());
        languageKey(65);
        auto shiftDown = languageSession.ShiftKey(0xa0, true, false, true, false, false, false, false, true);
        Check(shiftDown && !shiftDown->handled && languageSession.IsChinese());
        auto shiftUp = languageSession.ShiftKey(0xa0, false, true, false, false, false, false, false, true);
        Check(shiftUp && shiftUp->handled && shiftUp->text == u"a" && !languageSession.IsChinese());
        Check(languageSession.PostProcessor().Output().RepeatBuffer() == u"a");
        // Reference SetChinese records the literal, then key postprocessing
        // records the response too; retain this existing history contract.
        Check(languageSession.PostProcessor().History().LastWord(2) == u"aa");
        languageSession.ShiftKey(0xa0, true, false, true, false, false, false, false, true);
        auto emptyShiftUp = languageSession.ShiftKey(0xa0, false, true, false, false, false, false, false, true);
        Check(emptyShiftUp && !emptyShiftUp->handled && languageSession.IsChinese());
        languageKey(0xc0); languageKey(65);
        auto toggleAt = [&](int vk, bool down, int ms)
        {
            return languageSession.CtrlSpaceKey(vk, down, !down, 1, false, down, false, false, false, true,
                CtrlSpaceState::Time{} + std::chrono::milliseconds(ms));
        };
        Check(!toggleAt(0xa2, true, 0));
        auto ctrlSpaceCommit = toggleAt(32, true, 1);
        Check(ctrlSpaceCommit && ctrlSpaceCommit->handled && ctrlSpaceCommit->text == u"\u00b7a");
        Check(!languageSession.IsChinese() && languageSession.Raw().empty());
        Check(!toggleAt(32, false, 2) && !toggleAt(0xa2, false, 3));
        Check(!toggleAt(0xa2, true, 4));
        auto emptyCtrlSpace = toggleAt(32, true, 5);
        Check(emptyCtrlSpace && emptyCtrlSpace->handled && emptyCtrlSpace->text.empty() && languageSession.IsChinese());
        languageSession.ResetLanguageToggleState();
        Check(!toggleAt(32, false, 6));
        languageSession.ShiftKey(0xa0, true, false, true, false, false, false, false, true);
        Check(!languageSession.ShiftKey(65, true, false, true, false, false, false, false, true));
        Check(!languageSession.ShiftKey(0xa0, false, true, false, false, false, false, false, true)->handled);
        Check(languageSession.IsChinese());
        ChineseInputSession modifierSession(MakeUpperCaseServices([](std::u16string_view) {}));
        SelectionKeys modifierKeys(SelectionBindings{{1, {0x10}}, {2, {0xa1}}, {3, {0xa2}}});
        auto modLetter = [&](int vk)
        {
            return modifierSession.KeyDown(vk, false, sessionSchema, pyTable, sessionSettings, true, modifierKeys);
        };
        auto modEvent = [&](int vk, bool down)
        {
            return modifierSession.ModifierSelectionKey(vk, down, !down,
                down && ShiftToggleState::IsShift(vk), down && vk == 0xa2, false, false, false,
                sessionSchema, pyTable, sessionSettings, modifierKeys);
        };
        modLetter(65);
        auto selectedShift = modEvent(0xa0, true);
        Check(selectedShift && selectedShift->text == u"hello" && !selectedShift->composing);
        Check(modifierSession.IsChinese() && modifierSession.Raw().empty());
        Check(!modEvent(0xa1, false)); // other side's release is not consumed
        Check(modEvent(0xa0, false)->handled);
        Check(!modEvent(0xa0, false));
        modLetter(0xc0); modLetter(65);
        Check(modEvent(0xa1, true)->text == u"two"); // explicit right Shift overrides generic
        Check(modEvent(0xa1, false)->handled);
        modLetter(65);
        auto beyondPage = modEvent(0xa2, true);
        Check(beyondPage && beyondPage->text.empty() && modifierSession.Raw().empty());
        modifierSession.ResetModifierSelectionState();
        Check(!modEvent(0xa2, false));
        modifierSession.KeyDown(65, true, sessionSchema, pyTable, sessionSettings, true, modifierKeys);
        Check(!modEvent(0xa0, true) && modifierSession.Raw() == u"A"); // uppercase is not selectable
        ChineseInputSession lifecycle(MakeUpperCaseServices([](std::u16string_view) {}));
        ActionBindings lifecycleBindings{ShortcutGesture::Parse(u"Ctrl+VK_OEM_PLUS"), ShortcutGesture::Parse(u"Ctrl+VK_M")};
        auto lifecycleAction = [&](int vk)
        {
            return lifecycle.ActionKeyDown(vk, false, true, false, false, false, lifecycleBindings, [] { return true; });
        };
        auto lifecycleKey = [&](int vk)
        {
            return lifecycle.KeyDown(vk, false, sessionSchema, pyTable, pySettings, true, selection);
        };
        lifecycleKey(0xc0); lifecycleKey(65); lifecycleKey(0xbb);
        Check(lifecycle.Page(sessionSchema, pyTable, pySettings).pageIndex == 1);
        auto beforeFocusRaw = lifecycle.Raw();
        Check(lifecycleAction(0xbb)->action == OutputAction::OpenAddWord);
        Check(lifecycle.Raw() == beforeFocusRaw);
        Check(lifecycleAction(0xbb)->action == OutputAction::Text);
        lifecycle.OnFocusChanged();
        Check(lifecycle.Raw() == beforeFocusRaw && lifecycle.Page(sessionSchema, pyTable, pySettings).pageIndex == 0);
        Check(lifecycleAction(0xbb)->action == OutputAction::OpenAddWord);
        auto switchResponse = lifecycleAction(0x4d);
        Check(switchResponse->composing && switchResponse->inputBuffer == beforeFocusRaw);
        auto rolloverResponse = lifecycleAction(0xbb);
        Check(rolloverResponse->action == OutputAction::Text && rolloverResponse->composing);
        lifecycle.ActionKeyRelease(0x4d);
        lifecycle.ActionModifierRelease(0xa2, false, false, false, false);
        Check(lifecycleAction(0xbb)->action == OutputAction::OpenAddWord);
        lifecycle.OnExternalCompositionCanceled();
        Check(lifecycle.Raw().empty() && lifecycle.IsChinese());
        Check(!lifecycle.ResetCompositionForConfigChange());
        lifecycleKey(65);
        Check(lifecycle.ResetCompositionForConfigChange() && lifecycle.Raw().empty());
        ChineseInputSession quoteSession(MakeUpperCaseServices([](std::u16string_view) {}));
        auto quoteEvent = [&](bool down, bool shift = false, bool ctrl = false)
        {
            return quoteSession.QuoteKey(0xde, down, !down, shift, ctrl, false, false, false,
                sessionSchema, pyTable, pySettings);
        };
        Check(!quoteEvent(true));
        auto normalQuote = quoteSession.KeyDown(0xde, false, sessionSchema, pyTable, pySettings, true, selection);
        Check(normalQuote.text == u"\u2018" && !quoteEvent(false));
        Check(quoteEvent(false)->text == u"\u2019");
        Check(!quoteEvent(false, false, true));
        quoteSession.KeyDown(0xc0, false, sessionSchema, pyTable, pySettings, true, selection);
        quoteSession.KeyDown(65, false, sessionSchema, pyTable, pySettings, true, selection);
        quoteSession.KeyDown(0xbb, false, sessionSchema, pyTable, pySettings, true, selection);
        Check(quoteEvent(false, true)->text == u"three\u201c"); // current page, no rank selection
        quoteSession.KeyDown(0xc0, false, sessionSchema, pyTable, pySettings, true, selection);
        auto emptyFallback = quoteEvent(false);
        Check(emptyFallback && emptyFallback->text.empty() && quoteSession.Raw().empty());
        quoteSession.KeyDown(65, true, sessionSchema, pyTable, pySettings, true, selection);
        Check(!quoteEvent(false) && quoteSession.Raw() == u"A");
        quoteSession.OnExternalCompositionCanceled();
        quoteEvent(true); quoteSession.OnFocusChanged();
        Check(quoteEvent(false).has_value()); // focus forgets unmatched down
        ChineseInputSession capsSession(MakeUpperCaseServices([](std::u16string_view)
            { throw std::logic_error("CapsLock must not schedule a timer"); }));
        auto capsKey = [&](int vk, bool shift = false)
        {
            return capsSession.KeyDown(vk, shift, sessionSchema, pyTable, pySettings, true, selection);
        };
        auto caps = [&] { return capsSession.CapsLockKey(false, false, false, false, false, sessionSchema, pyTable); };
        capsKey(0xc0); capsKey(65); capsKey(0xbb);
        Check(capsSession.Page(sessionSchema, pyTable, pySettings).pageIndex == 1);
        auto capsPinyin = caps();
        Check(!capsPinyin.handled && capsPinyin.text == u"one" && capsSession.Raw().empty());
        Check(capsSession.IsChinese() && capsSession.PostProcessor().History().LastWord(3) == u"one");
        Check(capsSession.PostProcessor().Output().RepeatBuffer() != u"one"); // pass-through commit does not replace repeat
        capsKey(0xc0); capsKey(90);
        Check(caps().text == u"\u00b7z");
        capsKey(65, true);
        Check(caps().text == u"hello"); // uppercase A performs case-insensitive main-table lookup
        capsKey(83, true); capsKey(49);
        Check(caps().text == u"S1"); // no currency command conversion
        capsSession.SetChinese(false);
        Check(!caps().handled && !capsSession.IsChinese());
        ChineseInputSession adjustmentSession(MakeUpperCaseServices([](std::u16string_view) {}));
        int adjustmentCalls = 0;
        CandidateAdjustment lastAdjustment = CandidateAdjustment::Advance;
        auto adjustmentKey = [&](int vk, bool ctrl, bool alt, bool shift, bool change)
        {
            return adjustmentSession.ModifiedKeyDown(vk, shift, ctrl, alt, false, false,
                sessionSchema, pyTable, sessionSettings,
                [&](CandidateAdjustment operation, std::u16string_view raw, std::u16string_view candidate)
                {
                    ++adjustmentCalls; lastAdjustment = operation;
                    Check(raw == u"a" && candidate == u"hello");
                    return change;
                });
        };
        auto startAdjustment = [&] { adjustmentSession.KeyDown(65, false, sessionSchema, pyTable, sessionSettings, true, selection); };
        startAdjustment();
        Check(adjustmentKey(0xa2, true, false, false, false)->cancelCompositionBeforePass == false);
        Check(adjustmentSession.Raw() == u"a");
        Check(adjustmentKey(49, false, true, false, false)->handled); // no-op advance consumed
        Check(lastAdjustment == CandidateAdjustment::Advance && adjustmentCalls == 1);
        Check(adjustmentKey(49, false, true, false, true)->handled && adjustmentCalls == 1);
        adjustmentSession.ActionKeyRelease(49);
        auto failedTop = adjustmentKey(49, true, false, false, false);
        Check(!failedTop->handled && failedTop->cancelCompositionBeforePass && adjustmentSession.Raw().empty());
        startAdjustment();
        Check(adjustmentKey(49, true, false, true, true)->handled && lastAdjustment == CandidateAdjustment::Delete);
        adjustmentSession.ActionKeyRelease(49);
        Check(adjustmentKey(49, true, false, false, true)->handled && lastAdjustment == CandidateAdjustment::Top);
        Check(adjustmentKey(67, true, false, false, true)->cancelCompositionBeforePass);
        Check(adjustmentSession.Raw().empty());
        ChineseInputSession unified(MakeUpperCaseServices([](std::u16string_view) {}));
        InputKeyEvent event;
        auto unifiedKey = [&]
        {
            return unified.ProcessKey(event, sessionSchema, pyTable, sessionSettings, true, true, true,
                modifierKeys, LoadActionBindings({}), {}, {}, CtrlSpaceState::Time{});
        };
        event.vk = 65; Check(unifiedKey().composing);
        event.vk = 0x10; event.scan = 0x2a; event.shift = true;
        Check(unifiedKey().text == u"hello"); // resolve generic Shift to left, select before toggle
        event.action = u"up"; event.shift = false;
        Check(unifiedKey().handled && unified.IsChinese());
        event = {}; event.vk = 65; unifiedKey();
        event.vk = 0x11; event.ctrl = true;
        Check(unifiedKey().handled); // generic Ctrl resolves left and selects out-of-range rank
        event.vk = 32;
        Check(unifiedKey().handled && !unified.IsChinese()); // CtrlSpace was armed before selection
        unified.OnFocusChanged();
        event = {}; event.vk = 0xde; event.action = u"UP";
        Check(!unifiedKey().handled); // English has no missing-down quote fallback
        unified.SetChinese(true);
        Check(unifiedKey().text == u"\u2018");
        event.action = u"invalid";
        Check(!unifiedKey().handled && unified.IsChinese());
        int mixedLookups = 0, mixedOutputs = 0;
        std::u16string mixedFirst = u"word";
        FixedLengthMixedDecoder mixed([&](std::u16string_view code)
            { ++mixedLookups; return FoldOrdinalCode(code) == u"AB" ? mixedFirst : std::u16string{}; },
            [&](std::u16string_view text) { ++mixedOutputs; return std::u16string(text); });
        Check(mixed.Decode(u"Ab", 2, 1).active == u"Ab" && mixedLookups == 0);
        auto mixedResult = mixed.Decode(u"AbCDx", 2, 1);
        Check(mixedResult.raw == u"AbCDx" && mixedResult.prefix == u"wordCD" && mixedResult.active == u"x");
        Check(mixedResult.surface == u"wordCDx" && mixedResult.ComposeChinese(u"end", u"!") == u"wordCDend!");
        Check(mixedLookups == 2 && mixedOutputs == 1);
        mixedFirst = u"changed";
        Check(mixed.Decode(u"abCDx", 2, 1).prefix == u"wordCD" && mixedLookups == 2);
        Check(mixed.Decode(u"abCDx", 2, 2).prefix == u"changedCD" && mixedLookups == 4);
        Check(mixed.Decode(u"abCDx", 2, 2, {{0, u"preferred"}}).prefix == u"preferredCD");
        Check(mixed.Decode(u"abCDx", 2, 2, {{0, u""}}).prefix == u"changedCD");
        mixed.ClearCache();
        Check(mixed.Decode(u"abx", 2, 2).prefix == u"changed" && mixedLookups == 5);
        Check(mixed.Decode(u"XY", 0, 2).prefix == u"X");
        MixedComposition mixedComposition(FixedLengthMixedDecoder(
            [](std::u16string_view code) { return FoldOrdinalCode(code) == u"AB" ? u"first" : std::u16string{}; },
            [](std::u16string_view text) { return std::u16string(text); }));
        mixedComposition.Start(u'A', 2, 1);
        mixedComposition.Append(u'b', 2, 1);
        Check(mixedComposition.Result().active == u"Ab");
        mixedComposition.Append(u'x', 2, 1, u"page-two");
        Check(mixedComposition.Raw() == u"Abx" && mixedComposition.Result().prefix == u"page-two");
        mixedComposition.Backspace(2, 1);
        Check(mixedComposition.Result().active == u"Ab" && mixedComposition.Result().prefix.empty());
        mixedComposition.Append(u'y', 2, 1);
        Check(mixedComposition.Result().prefix == u"first"); // reopened segment forgot old preference
        mixedComposition.Import(u"AbCdE", 2, 2);
        Check(mixedComposition.Result().prefix == u"firstCd" && mixedComposition.Result().active == u"E");
        auto revision = mixedComposition.Revision();
        mixedComposition.Rebuild(4, 2);
        Check(mixedComposition.Result().prefix == u"AbCd" && mixedComposition.Revision() > revision);
        mixedComposition.Clear();
        mixedComposition.Backspace(4, 2);
        Check(!mixedComposition.Active() && mixedComposition.Result().surface.empty());
        OrdinarySettings mixedSettings;
        mixedSettings.maxCodeLength = 2;
        mixedSettings.pageSize = 2;
        SelectionKeys mixedSelection;
        auto mixedEdit = [&](int vk, bool shift = false)
        {
            return mixedComposition.EditKey(vk, shift, ordinarySchema.table, mixedSettings, 3, mixedSelection);
        };
        mixedComposition.Import(u"Ab", 2, 3);
        Check(mixedEdit(0xbb)->composing); // next page
        Check(mixedComposition.Page(ordinarySchema.table, mixedSettings).entries.front() == u"third");
        mixedComposition.AppendLetter(u'x', ordinarySchema.table, mixedSettings, 3);
        Check(mixedComposition.Result().prefix == u"third");
        Check(mixedEdit(8)->inputBuffer == u"Ab");
        Check(mixedComposition.Page(ordinarySchema.table, mixedSettings).pageIndex == 0);
        mixedComposition.AppendLetter(u'x', ordinarySchema.table, mixedSettings, 3);
        Check(mixedComposition.Result().prefix == u"first");
        mixedComposition.AppendLetter(u'y', ordinarySchema.table, mixedSettings, 3);
        Check(mixedEdit(0x31)->text == u"firstunique" && !mixedComposition.Active());
        mixedComposition.Import(u"Abxy", 2, 3);
        Check(mixedEdit(0x39)->text == u"firstunique9"); // out-of-range digit retains prefix
        mixedComposition.Import(u"AbQ", 2, 3);
        Check(mixedEdit(32)->text == u"first"); // missing active candidate still commits prefix
        mixedComposition.Import(u"Abxy", 2, 3);
        Check(mixedEdit(13)->text == u"Abxy"); // Enter is complete raw, not surface
        mixedComposition.Import(u"Abxy", 2, 3);
        mixedSettings.enterClear = true;
        Check(mixedEdit(13)->text.empty() && !mixedComposition.Active());
        mixedComposition.Import(u"Abxy", 2, 3);
        Check(mixedEdit(27)->text.empty() && !mixedComposition.Active());
        auto mixedKey = [&](int vk, bool shift = false)
        {
            return mixedComposition.KeyDown(vk, shift, ordinarySchema, pyTable, mixedSettings, 3,
                mixedSelection, transitionOrdinary, py, true, ordinaryOutput, ordinaryHistory);
        };
        mixedSettings.pageSize = 5;
        mixedComposition.Import(u"Abab", 2, 3);
        Check(mixedKey(0xba)->text == u"firstsecond");
        mixedComposition.Import(u"Abab", 2, 3);
        Check(mixedKey(0xde)->text == u"firstthird");
        mixedComposition.Import(u"AbQ", 2, 3);
        Check(mixedKey(0xbc)->text == u"first\uff0c");
        mixedComposition.Import(u"Q", 2, 3);
        Check(mixedKey(0xbc)->text == u"\uff0c"); // mixed empty prefix still emits symbol
        mixedComposition.Import(u"AbQ", 2, 3);
        auto mixedPinyin = mixedKey(0xc0);
        Check(mixedPinyin->text == u"first" && !mixedPinyin->composing && py.Active());
        Check(!mixedComposition.Active());
        py.Clear();
        mixedComposition.Import(u"AbQ", 2, 3);
        Check(mixedKey(0x31, true)->text == u"first\uff01");
        mixedComposition.Import(u"Abx", 2, 3);
        Check(mixedKey(0x59, true)->inputBuffer == u"xY" && mixedComposition.Raw() == u"AbxY");
        mixedComposition.Import(u"AbQ", 2, 3);
        Check(mixedKey(0xde)->text.starts_with(u"first") && !mixedComposition.Active());
        ChineseComposition mixedDispatcher(MakeUpperCaseServices([](std::u16string_view) {}),
            FixedLengthMixedDecoder([&](std::u16string_view code)
                {
                    auto page = ReadCandidatePage(ordinarySchema.table, code, 0, 1);
                    return page.entries.empty() ? std::u16string{} : page.entries.front();
                }, [](std::u16string_view text) { return std::u16string(text); }));
        mixedSettings.mixedInput = true;
        mixedSettings.enterClear = false;
        auto dispatchMixed = [&](int vk, bool shift = false)
        {
            return mixedDispatcher.KeyDown(vk, shift, ordinarySchema, pyTable, mixedSettings,
                true, mixedSelection, ordinaryOutput, ordinaryHistory);
        };
        dispatchMixed(0x41); dispatchMixed(0x42); dispatchMixed(0x58); dispatchMixed(0x59, true);
        Check(mixedDispatcher.Raw() == u"abxY" && mixedDispatcher.ActiveCode() == u"xY");
        Check(mixedDispatcher.Surface() == u"firstxY" && mixedDispatcher.Mode() == ChineseMode::Ordinary);
        Check(dispatchMixed(32).text == u"firstunique" && mixedDispatcher.Mode() == ChineseMode::Idle);
        dispatchMixed(0x41); dispatchMixed(0x42); dispatchMixed(0x58);
        Check(dispatchMixed(0xc0).text == u"first" && mixedDispatcher.Mode() == ChineseMode::Pinyin);
        mixedDispatcher.Clear();
        dispatchMixed(0x41, true);
        Check(mixedDispatcher.Mode() == ChineseMode::UpperCase); // shift entry is not mixed
        mixedDispatcher.Clear();
        ChineseInputSession mixedSession(MakeUpperCaseServices([](std::u16string_view) {}),
            FixedLengthMixedDecoder([&](std::u16string_view code)
                {
                    auto page = ReadCandidatePage(ordinarySchema.table, code, 0, 1);
                    return page.entries.empty() ? std::u16string{} : page.entries.front();
                }, [](std::u16string_view text) { return std::u16string(text); }));
        auto sessionMixedKey = [&](int vk)
        {
            return mixedSession.KeyDown(vk, false, ordinarySchema, pyTable, mixedSettings,
                true, mixedSelection);
        };
        auto startMixedSession = [&]
        {
            mixedSession.ClearComposition();
            mixedSession.SetChinese(true);
            sessionMixedKey(0x41); sessionMixedKey(0x42); sessionMixedKey(0x58); sessionMixedKey(0x59);
        };
        startMixedSession();
        Check(mixedSession.Raw() == u"abxy" && mixedSession.ActiveCode() == u"xy");
        auto mixedCaps = mixedSession.CapsLockKey(false, false, false, false, false, ordinarySchema, pyTable);
        Check(!mixedCaps.handled && mixedCaps.text == u"firstunique" && mixedSession.Raw().empty());
        startMixedSession();
        Check(mixedSession.SetChinese(false) == u"abxy"); // language switch retains literal raw
        startMixedSession();
        SelectionKeys mixedModifier(SelectionBindings{{1, {0xa0}}});
        auto mixedSelected = mixedSession.ModifierSelectionKey(0xa0, true, false, true, false, false, false, false,
            ordinarySchema, pyTable, mixedSettings, mixedModifier);
        Check(mixedSelected && mixedSelected->text == u"firstunique" && mixedSession.Raw().empty());
        startMixedSession();
        std::u16string adjustedMixedCode;
        auto mixedAdjustment = mixedSession.ModifiedKeyDown(0x31, false, true, false, false, false,
            ordinarySchema, pyTable, mixedSettings,
            [&](CandidateAdjustment, std::u16string_view code, std::u16string_view)
            { adjustedMixedCode = code; return true; });
        Check(adjustedMixedCode == u"xy" && mixedAdjustment->inputBuffer == u"xy" && mixedSession.Raw() == u"abxy");
        mixedSettings.mixedInput = false;
        mixedSettings.maxCodeLength = 2;
        mixedSession.RefreshAfterSchemaSwitch(mixedSettings);
        Check(mixedSession.Raw() == u"abxy" && mixedSession.Surface() == u"firstxy"); // temporary mixed
        mixedSettings.maxCodeLength = 4;
        mixedSession.RefreshAfterSchemaSwitch(mixedSettings);
        Check(mixedSession.Raw() == u"abxy" && mixedSession.ActiveCode() == u"abxy" && mixedSession.Surface() == u"abxy");
        mixedSettings.mixedInput = true;
        mixedSettings.maxCodeLength = 2;
        mixedSession.RefreshAfterSchemaSwitch(mixedSettings);
        Check(mixedSession.Surface() == u"firstxy" && mixedSession.ActiveCode() == u"xy");
        Check(sessionMixedKey(13).text == u"abxy");
        mixedSettings.mixedInput = false; mixedSettings.maxCodeLength = 2;
        mixedSession.ImportRaw(u"AbXY", mixedSettings);
        Check(mixedSession.Raw() == u"AbXY" && mixedSession.ActiveCode() == u"XY");
        Check(sessionMixedKey(13).text == u"AbXY");
        mixedSession.ImportRaw(u"Ab", mixedSettings);
        Check(mixedSession.ActiveCode() == u"Ab" && mixedSession.Surface() == u"Ab");
        mixedSession.ImportRaw(mixedSession.Raw(), mixedSettings);
        Check(mixedSession.Raw() == u"Ab");
        mixedSession.ImportRaw(u"", mixedSettings); Check(mixedSession.Mode() == ChineseMode::Idle);
        ChineseComposition noMixed(MakeUpperCaseServices([](std::u16string_view) {}));
        noMixed.ImportRaw(u"Ab", mixedSettings);
        bool migrationRejected = false;
        try { noMixed.ImportRaw(u"AbXY", mixedSettings); } catch (const std::logic_error&) { migrationRejected = true; }
        Check(migrationRejected && noMixed.Raw() == u"Ab");
        // Cache invalidation is required even if a host reuses a version value.
        std::u16string migrationFirst = u"old";
        auto inputDefaults = LoadOrdinarySettings({}, 17);
        Check(inputDefaults.maxCodeLength == 4 && inputDefaults.pageSize == 5 && inputDefaults.lexiconVersion == 17);
        Check(inputDefaults.pageKeys == u"- =" && inputDefaults.secondSemicolon && !inputDefaults.mixedInput);
        auto inputConfigured = LoadOrdinarySettings({
            {u"\u6700\u5927\u7801\u957f", u"999"},
            {u"\u6bcf\u9875\u5019\u9009\u4e2a\u6570", u"-2"},
            {u"\u7ffb\u9875\u952e", u"\u3000[ ]\u3000"},
            {u"\u4e2d\u82f1\u6587\u4e0d\u9650\u957f\u6df7\u5408\u8f93\u5165", u"true"},
            {u"\u5206\u53f7\u6b21\u9009", u"false"}});
        Check(inputConfigured.maxCodeLength == 16 && inputConfigured.pageSize == 1 && inputConfigured.pageKeys == u"[ ]");
        Check(inputConfigured.mixedInput && !inputConfigured.secondSemicolon);
        Check(LoadOrdinarySettings({{u"\u6700\u5927\u7801\u957f", u"2147483648"}}).maxCodeLength == 4);
        Check(LoadOrdinarySettings({{u"\u6700\u5927\u7801\u957f", u"plus7"}}, 0, u"plus", u"minus").maxCodeLength == 7);
        MixedComposition migrating(FixedLengthMixedDecoder(
            [&](std::u16string_view) { return migrationFirst; },
            [](std::u16string_view text) { return std::u16string(text); }));
        migrating.Import(u"AbC", 2, 1);
        Check(migrating.Result().prefix == u"old");
        migrationFirst = u"new";
        migrating.Import(migrating.Raw(), 2, 1);
        Check(migrating.Raw() == u"AbC" && migrating.Result().prefix == u"new");
        shiftEvent(0xa0, true); shiftEvent(0xa1, true);
        Check(shiftEvent(0xa0, false).toggle && shiftState.Down());
        Check(shiftEvent(0xa1, false).toggle); // reference toggles on each matched release
        shiftEvent(0xa1, true);
        Check(!shiftEvent(0xa1, false, true).toggle);
        shiftEvent(0xa1, true);
        Check(!shiftEvent(0xa1, false, false, false).toggle);
        shiftState.SkipAfterSelection(); shiftEvent(0xa0, true);
        Check(!shiftEvent(0xa0, false).toggle);
        shiftEvent(0xa0, true); Check(shiftEvent(0xa0, false).toggle);
        shiftEvent(0xa0, true); shiftState.Reset();
        Check(!shiftEvent(0xa0, false).toggle);
        for (const auto& [key, value] : settings) if (key == u"\u5f53\u524d\u7801\u8868") Check(value.empty());
        std::cout << "Lexicon text tests passed\n";
        std::vector<std::uint8_t> ngramBytes;
        auto number = [&](std::uint64_t value, unsigned size)
        {
            for (unsigned i = 0; i < size; ++i) ngramBytes.push_back(static_cast<std::uint8_t>(value >> (8 * i)));
        };
        auto probability = [&](float value) { number(std::bit_cast<std::uint32_t>(value), 4); };
        for (char c : std::string_view("TCSKNM01")) number(static_cast<unsigned char>(c), 1);
        number(1, 4);
        number(3, 4); // unigram records
        number(0, 4); probability(0.01f);
        number(97, 4); probability(0.2f);
        number(98, 4); probability(0.3f);
        number(1, 8); number(SentenceNgram::Pair(97, 98), 8); probability(0.4f);
        number(1, 4); number(97, 4); probability(0.5f);
        number(1, 8); number(SentenceNgram::Triple(97, 97, 98), 8); probability(0.1f);
        number(1, 8); number(SentenceNgram::Pair(97, 97), 8); probability(0.25f);
        SentenceNgram ngram(ngramBytes);
        std::vector<CompactLexicon::Entry> sentenceEntries{
            {u"a", {u"X"}}, {u"ba", {u"Y", u"X", u"word"}},
            {u"ab", {u"X"}}, {u"xy", {u"X", u"Y"}}, {u"xyz", {u"Y"}},
            {u"zz", {u"e\u0301", u"\U00020000"}}};
        auto sentenceIndex = SentenceLexicon::Build(sentenceEntries, {u"X", u"Y"});
        Check(sentenceIndex.Candidates(u"A")->front().optimalSingle);
        Check(sentenceIndex.Candidates(u"BA")->size() == 2);
        Check(sentenceIndex.Candidates(u"ba")->at(1).text == u"word" && sentenceIndex.Candidates(u"ba")->at(1).rank == 3);
        Check(!sentenceIndex.Candidates(u"ab")->front().optimalSingle);
        Check(sentenceIndex.Candidates(u"xy") == nullptr && sentenceIndex.Candidates(u"xyz") == nullptr);
        Check(sentenceIndex.Candidates(u"zz")->front().elements.size() == 1);
        Check(sentenceIndex.Candidates(u"zz")->at(1).optimalSingle);
        Check(sentenceIndex.IsProperPrefix(u"B") && !sentenceIndex.IsProperPrefix(u"xy"));
        Check(sentenceIndex.CodeLengths() == std::vector<std::size_t>({1, 2}));
        auto whitelistedIndex = SentenceLexicon::Build(sentenceEntries, {u"X", u"Y"}, {u"X"});
        Check(whitelistedIndex.Candidates(u"xy")->size() == 1);
        auto edgeIndex = SentenceLexicon::Build(std::vector<CompactLexicon::Entry>{
            {u"a", {u"X", u"Y"}}, {u"aa", {u"X", u"Y", u"word"}}, {u"/a", {u"symbol"}}});
        Check(SentenceEdges(edgeIndex, u"a", 0, false).size() == 2);
        Check(SentenceEdges(edgeIndex, u"aaa", 0, false).size() == 1); // no unselected single-key edge
        Check(SentenceEdges(edgeIndex, u"aaa", 0, true).size() == 2); // implicit non-first single only
        Check(SentenceEdges(edgeIndex, u"aa", 0, false).size() == 3); // whole edge allows later word ranks
        auto explicitEdge = SentenceEdges(edgeIndex, u"aa'aa", 0, false);
        Check(explicitEdge.size() == 1 && explicitEdge[0].candidate->text == u"word" && explicitEdge[0].end == 3);
        Check(SentenceEdges(edgeIndex, u"a;aa", 0, false).front().candidate->text == u"Y");
        Check(SentenceEdges(edgeIndex, u"aa/a", 2, true).empty());
        Check(SentenceEdges(edgeIndex, u"/a", 0, false).size() == 1);
        Check(SentenceEdges(edgeIndex, u"aa", 0, true, 2).empty());
        Check(ReadSentenceCodeSuffix(u"aa0", 2).rank == 10);
        Check(ReadSentenceCodeSuffix(u"aa00", 2).rank == 0);
        Check(ReadSentenceCodeSuffix(u"aa0002", 2).rank == 2);
        bool rankOverflow = false, rankUnicode = false;
        try { ReadSentenceCodeSuffix(u"aa2147483648", 2); } catch (const std::overflow_error&) { rankOverflow = true; }
        try { ReadSentenceCodeSuffix(u"aa\uff12", 2); } catch (const std::invalid_argument&) { rankUnicode = true; }
        Check(rankOverflow && rankUnicode);
        auto singleEdges = SentenceEdges(edgeIndex, u"aa", 0, true);
        SentenceBeamState initialBeam;
        initialBeam.previous2 = initialBeam.previous1 = u"\x02";
        auto scoredBeam = AdvanceSentenceBeam(initialBeam, singleEdges[1],
            [](auto, auto, auto) { return -3.0; }, 0.03, 2.0, 5.0,
            [](int state, auto) { return std::pair{state + 1, 4.0}; });
        Check(std::abs(scoredBeam.logMass - (-1.0 - 0.03 * std::log(2.0))) < 1e-12);
        Check(scoredBeam.supplementScore == 4.0 && scoredBeam.boundary->rawLength == 2);
        auto optimalCandidate = *singleEdges[1].candidate;
        optimalCandidate.optimalSingle = true;
        auto optimalEdge = singleEdges[1]; optimalEdge.candidate = &optimalCandidate;
        auto rewardedBeam = AdvanceSentenceBeam(initialBeam, optimalEdge,
            [](auto, auto, auto) { return -3.0; }, 0.03, 2.0, 5.0);
        Check(std::abs(rewardedBeam.score - rewardedBeam.logMass - 5.0) < 1e-12);
        optimalEdge.selectedRank = 2;
        auto selectedBeam = AdvanceSentenceBeam(initialBeam, optimalEdge,
            [](auto, auto, auto) { return -3.0; }, 0.03, 2.0, 5.0);
        Check(selectedBeam.score == -1 && selectedBeam.logMass == -1);
        SentenceBeamBucket beamBucket;
        auto lowRank = scoredBeam; lowRank.text = u"same"; lowRank.maxRank = 1; lowRank.score = -20; lowRank.logMass = -2;
        auto highRank = lowRank; highRank.maxRank = 2; highRank.score = 20; highRank.logMass = -3;
        beamBucket.Add(lowRank); beamBucket.Add(highRank);
        auto aggregated = beamBucket.Limit(2, true);
        Check(aggregated.size() == 1 && aggregated[0].score == -20);
        Check(std::abs(aggregated[0].logMass - std::log(std::exp(-2.0) + std::exp(-3.0))) < 1e-12);
        auto otherBeam = lowRank; otherBeam.text = u"other"; otherBeam.score = 0;
        beamBucket.Add(otherBeam);
        Check(beamBucket.Limit(1, true).front().text == u"other" && beamBucket.Truncated());
        auto lattice = DecodeSentenceLattice(edgeIndex, u" AA AA ", [](auto, auto, auto) { return 0.0; });
        Check(lattice.raw == u"aaaa" && !lattice.candidates.empty());
        Check(lattice.candidates.front().text == u"XX");
        Check(SegmentedSentenceCode(lattice.raw, lattice.candidates.front().boundary) == u"aa aa");
        auto explicitLattice = DecodeSentenceLattice(edgeIndex, u"aa'aa", [](auto, auto, auto) { return 0.0; });
        Check(explicitLattice.candidates.front().text == u"wordX");
        Check(SegmentedSentenceCode(explicitLattice.raw, explicitLattice.candidates.front().boundary) == u"aa' aa");
        auto wholeLattice = DecodeSentenceLattice(edgeIndex, u"aa", [](auto, auto, auto) { return 0.0; });
        Check(wholeLattice.candidates.front().text == u"X"); // whole-only set keeps rank first even if a word scores more
        Check(DecodeSentenceLattice(edgeIndex, u"q", [](auto, auto, auto) { return 0.0; }).candidates.empty());
        Check(DecodeSentenceLattice(edgeIndex, u"\t ", [](auto, auto, auto) { return 0.0; }).states.empty());
        auto symbolIndex = SentenceLexicon::Build(std::vector<CompactLexicon::Entry>{{u";", {u"X"}}, {u"\U00010400", {u"Y"}}});
        for (auto raw : {u";", u"00", u"/ [", u"\U00010400"})
        {
            auto rejected = DecodeSentenceLattice(symbolIndex, raw, [](auto, auto, auto) { return 0.0; });
            Check(rejected.raw.empty() && rejected.states.empty() && rejected.candidates.empty() && rejected.expanded == 0);
        }
        FixedScoreCache<double> collisionCache(1);
        double cacheValue = 0;
        Check(!collisionCache.Get(0, cacheValue));
        collisionCache.Set(0, -123.0);
        Check(collisionCache.Get(0, cacheValue) && cacheValue == -123.0);
        collisionCache.Set(1, -456.0);
        Check(!collisionCache.Get(0, cacheValue) && collisionCache.Get(1, cacheValue) && cacheValue == -456.0);
        collisionCache.Clear();
        Check(!collisionCache.Get(1, cacheValue) && collisionCache.Capacity() == 1);
        CachedSentenceNgram cachedNgram(ngram);
        Check(SentenceSupplementEntry::Create(u"a", -1).reward == 0);
        Check(SentenceSupplementEntry::Create(u"a", 1000).reward == 9);
        Check(SentenceSupplementEntry::Create(u"a", 10000000000LL).reward == 16);
        Check(SentenceSupplementEntry::Create(u"a", 10000000000LL).weight == 1000000000LL);
        auto supplementMatcher = SentenceSupplementMatcher::Build(std::vector<SentenceSupplementEntry>{
            {u"ab", 1000, 9}, {u"b", 1000, 12}, {u"ab", 1000, 3}, {u"ba", 1000, 7},
            {u"a\u0301\U00020000", 1000, 5}, {u"ignored", 1, 0}});
        auto [suppState, suppReward] = supplementMatcher.Advance(-1, u"a");
        Check(suppReward == 0);
        auto suppNext = supplementMatcher.Advance(suppState, u"b");
        Check(suppNext.second == 12); // longest and suffix rewards use max, not sum
        Check(supplementMatcher.Advance(suppNext.first, u"a").second == 7);
        Check(supplementMatcher.Advance(suppNext.first, u"") == std::pair<int, double>(0, 0));
        auto combinedState = supplementMatcher.Advance(999999, u"a\u0301").first;
        Check(supplementMatcher.Advance(combinedState, u"\U00020000").second == 5);
        Check(SentenceSupplementMatcher{}.IsEmpty());
        RuntimeSentenceSettings extractionSettings;
        extractionSettings.optimalCodeHighFrequencyLimit = 0;
        auto extracted = RuntimeSentenceDecoder::BuildLexicon(CompactLexicon::Build(std::vector<CompactLexicon::Entry>{
            {u"aa", {u"display\x1e" u"X", u"alias\x1e" u"X", u"Y", u""}}}), extractionSettings);
        auto extractedCandidates = extracted.Candidates(u"aa");
        Check(extractedCandidates && extractedCandidates->size() == 2);
        Check((*extractedCandidates)[0].text == u"X" && (*extractedCandidates)[1].rank == 2);
        auto exactPaths = SentenceLexicon::Build(std::vector<CompactLexicon::Entry>{{u"aa", {u"X"}}, {u"aaaa", {u"XX"}}});
        Check(HasCompleteSentenceCandidate(exactPaths, u"AAAA", true));
        Check(!HasCompleteSentenceCandidate(exactPaths, u"aaaa", true, {}, u"XX"));
        Check(HasCompleteSentenceCandidate(exactPaths, u"aaaa", true, u"X", u""));
        Check(!HasCompleteSentenceCandidate(exactPaths, u"aaaa", true, u"XXZ"));
        Check(!HasCompleteSentenceCandidate(exactPaths, u";", true));
        Check(HasCompleteSentenceCandidate(extracted, u"aa", true, {}, u"X"));
        Check(!HasCompleteSentenceCandidate(extracted, u"aa", true, {}, u"X", true));
        Check(HasCompleteSentenceCandidate(extracted, u"aa2", true, u"Y", {}, true));
        SentenceBeamState confidenceA, confidenceB;
        confidenceA.text = u"ab"; confidenceB.text = u"cd";
        confidenceA.score = 10000; // ranking score is deliberately irrelevant
        auto sharedBoundary = std::make_shared<SentenceBoundary>(SentenceBoundary{nullptr, 1, 2});
        confidenceA.boundary = confidenceB.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{sharedBoundary, 2, 4});
        auto prefixes = BuildSentencePrefixEvidence(std::vector<SentenceBeamState>{confidenceA, confidenceB});
        Check(prefixes.size() == 4);
        for (const auto& prefix : prefixes) Check(prefix.share == 0.5 && prefix.boundaryShare == 1 && prefix.boundaryClosed);
        SentenceBeamState crossing;
        crossing.text = u"z";
        crossing.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{nullptr, 1, 4});
        prefixes = BuildSentencePrefixEvidence(std::vector<SentenceBeamState>{confidenceA, confidenceB, crossing});
        for (const auto& prefix : prefixes) if (prefix.rawLength == 2) Check(!prefix.boundaryClosed);
        crossing.logMass = -20;
        prefixes = BuildSentencePrefixEvidence(std::vector<SentenceBeamState>{confidenceA, confidenceB, crossing});
        for (const auto& prefix : prefixes) if (prefix.rawLength == 2) Check(prefix.boundaryClosed);
        Check(BuildSentencePrefixEvidence({}).empty());
        auto incompleteLattice = DecodeSentenceLattice(exactPaths, u"aaa", [](auto, auto, auto) { return 0.0; });
        auto tailEvidence = BuildSentenceEarlyEvidence(exactPaths, incompleteLattice, [](auto, auto, auto) { return 0.0; });
        Check(tailEvidence.neutralIncompleteTail && tailEvidence.mergedIncompleteTail && tailEvidence.proposal == u"X");
        Check(tailEvidence.proposalShare == 1 && tailEvidence.rawLengths.at(u"X") == 2);
        auto filteredEvidence = BuildSentenceEarlyEvidence(exactPaths, incompleteLattice, [](auto, auto, auto) { return 0.0; }, {}, {}, u"Y");
        Check(filteredEvidence.prefixes.empty() && !filteredEvidence.mergedIncompleteTail);
        auto completeLattice = DecodeSentenceLattice(exactPaths, u"aaaa", [](auto, auto, auto) { return 0.0; });
        auto completeEvidence = BuildSentenceEarlyEvidence(exactPaths, completeLattice, [](auto, auto, auto) { return 0.0; });
        Check(!completeEvidence.mergedIncompleteTail && !completeEvidence.neutralLowConfidence && completeEvidence.proposal == u"XX");
        SentenceSupplementParser supplementParser;
        for (auto line : {u"a", u"b 9223372036854775807", u"a 2500", u"a 0", u"a 9223372036854775808",
            u"bad -1", u"bad 1 2", u"# ignored", u"c +0123"}) supplementParser.Consume(line);
        Check(supplementParser.Entries().size() == 3);
        Check(supplementParser.Entries()[0].text == u"a" && supplementParser.Entries()[0].weight == 2500);
        Check(supplementParser.Entries()[1].weight == 1000000000 && supplementParser.Entries()[2].weight == 123);
        auto rankFixture = SentenceCharacterRanks::FromText(u" # comment\r\n b \rA\n\u2003b\t\nA\n\U00020000\na#b");
        Check(rankFixture.Size() == 4 && rankFixture.GetRank(u"b") == 1 && rankFixture.GetRank(u"A") == 2);
        Check(rankFixture.GetRank(u"\U00020000") == 3 && rankFixture.GetRank(u"a#b") == 4);
        Check(rankFixture.GetRank(u"a") == SentenceCharacterRanks::UnknownRank && rankFixture.GetRank(u"") == 20001);
        Check(rankFixture.TakeTop(0).empty() && rankFixture.TakeTop(-1).empty());
        Check(rankFixture.TakeTop(2) == std::unordered_set<std::u16string>({u"b", u"A"}));
        Check(rankFixture.TakeTop(99).size() == 4);
        auto unknownRank = [](auto) { return 20001; };
        auto unseen = [](auto, auto) { return false; };
        auto seenAB = [](auto a, auto b) { return a == u"a" && b == u"b"; };
        Check(SentenceIsolationPenalty(u"ab", unknownRank, unseen) == 4);
        Check(SentenceIsolationPenalty(u"ab", unknownRank, seenAB) == 0);
        Check(SentenceIsolationPenalty(u"abc", unknownRank, seenAB) == 2);
        Check(SentenceIsolationPenalty(u"a\u0301\U00020000", unknownRank, unseen) == 4);
        Check(SentenceIsolationPenalty(u"ab", unknownRank, unseen, {0, 2, true}) == 0);
        Check(SentenceIsolationPenalty(u"ab", [](auto) { return 3000; }, unseen) == 0);
        Check(std::abs(SentenceIsolationPenalty(u"a", unknownRank, unseen, {3000, 2, true}) -
            2 * std::log(20001.0 / 3000)) < 1e-14);
        for (auto previous2 : {u"a", u"\x02"})
        for (auto previous1 : {u"a", u"\x02"})
        for (auto target : {u"b", u"\x03"})
        for (bool boundaries : {false, true})
        {
            bool includeUnigram = boundaries || (std::u16string_view(previous2) != u"\x02" &&
                std::u16string_view(previous1) != u"\x02" && std::u16string_view(target) != u"\x03");
            Check(ScoreSentenceNgramTransition(cachedNgram, previous2, previous1, target, boundaries) ==
                ngram.LogProbability(previous2, previous1, target, includeUnigram));
        }
        for (int cachePass = 0; cachePass < 3; ++cachePass)
        {
            for (bool unigram : {true, false})
                Check(cachedNgram.LogProbability(u"a", u"a", u"b", unigram) == ngram.LogProbability(u"a", u"a", u"b", unigram));
            Check(cachedNgram.HasObservedBigram(u"a", u"b") && !cachedNgram.HasObservedBigram(u"b", u"a"));
        }
        cachedNgram.Clear();
        Check(cachedNgram.LogProbability(u"a", u"a", u"b") == ngram.LogProbability(u"a", u"a", u"b"));
        auto expectedProbability = double(0.1f) + double(0.25f) * (double(0.4f) + double(0.5f) * double(0.3f));
        Check(std::abs(ngram.LogProbability(u"a", u"a", u"b") - std::log(expectedProbability)) < 1e-14);
        Check(std::abs(ngram.LogProbability(u"a", u"a", u"b", false) - std::log(double(0.1f) + double(0.25f) * double(0.4f))) < 1e-14);
        Check(ngram.HasObservedBigram(u"a", u"b") && !ngram.HasObservedBigram(u"b", u"a"));
        Check(ngram.LogProbability(u"", u"", u"unknown") == std::log(double(0.01f)));
        Check(ngram.LogProbability(u"", u"", u"b", false) == std::log(1e-300));
        Check(SentenceNgram::Scalar(u"\U00020000") == 0x20000 && SentenceNgram::Scalar(u"ab") == 0);
        for (std::size_t length = 0; length < ngramBytes.size(); ++length)
        {
            bool rejected = false;
            try { SentenceNgram truncated(std::span<const std::uint8_t>(ngramBytes).first(length)); }
            catch (const std::invalid_argument&) { rejected = true; }
            Check(rejected);
        }
        auto invalidNgram = ngramBytes;
        invalidNgram.push_back(0);
        bool trailingRejected = false;
        try { SentenceNgram trailing(invalidNgram); }
        catch (const std::invalid_argument&) { trailingRejected = true; }
        Check(trailingRejected);
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
