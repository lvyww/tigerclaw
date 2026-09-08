#include "Model.h"
#include "Placement.h"
#include <iostream>
#include <stdexcept>
using namespace tiger::overlay;
static void Check(bool ok, const char* message)
{
    if (!ok) throw std::runtime_error(message);
}
int main()
{
    try
    {
        State state;
        Placement position;
        WorkArea area{0, 0, 1920, 1080};
        Check(!position.Acquire(0, 0, 20), "unknown caret rejected");
        Check(position.Acquire(100, 200, 20), "valid caret");
        auto point = position.Resolve(300, 60, area);
        Check(point.x == 100 && point.y == 205, "below caret");
        position.Acquire(400, 500, 20);
        Check(position.Anchor().x == 100, "composition anchor pinned");
        Check(!position.Acquire(0, 0, 20) && position.Anchor().x == 100, "invalid caret preserves anchor");
        position.RefreshAnchor(); position.Acquire(1800, 1060, 20);
        point = position.Resolve(300, 100, area);
        Check(!position.IsAbove() && point.x == 1618 && point.y == 978, "explicit anchor refresh preserves below placement");
        position.Reset(); position.Acquire(1800, 1060, 20);
        point = position.Resolve(300, 100, area);
        Check(position.IsAbove() && point.y == 935, "bottom edge flips above");
        position.Resolve(300, 20, area);
        Check(position.IsAbove(), "shrinking candidate keeps above lock");
        point = position.Resolve(300, 100, {-1920, -1080, 0, 0}, true);
        Check(point.x == -1910 && point.y == -1070, "start-menu offset supports negative monitors");
        point = position.Resolve(3000, 2000, area);
        Check(point.x == 0 && point.y == 0, "oversize window clamps to work area origin");
        Check(ParseState(R"({"CandidateVisible":true,"InputCode":"ab","Candidates":["\u53ef","\ud840\udc00"],"CandidateAnnotations":["x",null],"SelectedCandidateIndex":1,"SoundSeq":9007199254740993})", state), "JSON state");
        Check(state.soundSequence == 9007199254740993LL, "64-bit sequences remain exact");
        Check(state.candidates[1].size() == 2, "non-BMP UTF-16");
        Check(ToUtf8(FromUtf8("A\xf0\xa0\x80\x80")) == "A\xf0\xa0\x80\x80", "UTF round trip");
        Check(!ParseState(R"({"Candidates":123})", state), "reject invalid arrays");
        Check(state.input == u"ab", "bad snapshot preserves previous state");
        state.nativeHook = true;
        auto display = Format(state);
        Check(display.text == u"ab     \u53ef\u3014x\u3015  \U00020000", "hook always shows code and annotations");
        Check(display.selectionStart == 13 && display.selectionLength == 2, "UTF-16 selection offsets");
        state.vertical = true; state.showIndex = true;
        Check(Format(state).text == u"ab\n1 \u53ef\u3014x\u3015\n2 \U00020000", "vertical text");
        state.composition = 5; state.selected = 0;
        Check(Format(state).selectionStart == -1, "sentence first candidate has no highlight");
        state.hideCandidates = true;
        Check(Format(state).mode == DisplayMode::CodeOnly, "hidden candidates with hook");
        state.nativeHook = false;
        Check(Format(state).mode == DisplayMode::Hidden, "hidden candidates without code");
        state.hideCandidates = false; state.candidates.clear();
        state.input = u"a\r\nb\rc\td";
        Check(Format(state).text == u"a\\nb\\nc\\td", "literal escaping");
        Check(Format(state).mode == DisplayMode::InputOnly, "unmatched code stays visible");
        state.candidates = {u"x"}; state.annotations = {u"y"}; state.input = u"ab";
        state.showCode = true; state.candidateDelay = 100; state.annotationDelay = 200;
        Reveal reveal;
        Check(reveal.Update(state, 1000).mode == DisplayMode::CodeOnly, "delayed candidates");
        Check(reveal.Update(state, 1100).text == u"ab\n1 x", "candidate deadline");
        Check(reveal.Update(state, 1200).text == u"ab\n1 x\u3014y\u3015", "annotation deadline");
        state.input = u"abc";
        Check(reveal.Update(state, 1201).text == u"abc\n1 x\u3014y\u3015", "editing does not restart reveal");
        state.candidateVisible = false; reveal.Update(state, 1202);
        state.candidateVisible = true;
        Check(reveal.Update(state, 1300).mode == DisplayMode::CodeOnly, "new composition resets reveal");
        state.fontSize = 17;
        Check(MeasureStyle(state, DisplayMode::CandidatesOnly).minWidth == 79, "vertical width coefficient 3.76");
        Check(Theme(u"unknown").background == 0xfffff8f3, "default theme fallback");
        auto original = state;
        auto checkChanged = [&](auto change)
        {
            auto changed = original; change(changed);
            Check(!SameDisplayContent(original, changed), "display edit invalidates formatting");
        };
        checkChanged([](State& s) { s.candidateVisible = !s.candidateVisible; });
        checkChanged([](State& s) { s.vertical = !s.vertical; });
        checkChanged([](State& s) { s.showIndex = !s.showIndex; });
        checkChanged([](State& s) { s.hideCandidates = !s.hideCandidates; });
        checkChanged([](State& s) { s.showCode = !s.showCode; });
        checkChanged([](State& s) { s.nativeHook = !s.nativeHook; });
        checkChanged([](State& s) { s.input += u'x'; });
        checkChanged([](State& s) { s.codeMask += u'*'; });
        checkChanged([](State& s) { s.candidates.push_back(u"new"); });
        checkChanged([](State& s) { s.annotations.push_back(u"new"); });
        checkChanged([](State& s) { ++s.selected; });
        checkChanged([](State& s) { ++s.composition; });
        checkChanged([](State& s) { ++s.candidateDelay; });
        checkChanged([](State& s) { ++s.annotationDelay; });
        auto unrelated = original;
        ++unrelated.caretX; ++unrelated.caretY; ++unrelated.caretHeight; ++unrelated.anchorRevision;
        ++unrelated.soundSequence; ++unrelated.soundVk; ++unrelated.soundVolume;
        unrelated.isOff = !unrelated.isOff; unrelated.isChinese = !unrelated.isChinese;
        unrelated.hideStatus = !unrelated.hideStatus; unrelated.status = u"new";
        unrelated.font = u"Consolas"; unrelated.fontSize = 33; unrelated.theme = u"new";
        Check(SameDisplayContent(original, unrelated), "non-text state reuses formatted display");
        for (unsigned bits = 0; bits < 128; ++bits)
        {
            auto variant = original;
            variant.candidateVisible = bits & 1; variant.hideCandidates = bits & 2;
            variant.showCode = bits & 4; variant.nativeHook = bits & 8;
            if (bits & 16) variant.input.clear();
            if (bits & 32) variant.candidates.clear();
            bool expanded = (bits & 64) != 0;
            Check(ModeFor(variant, expanded) == Format(variant, expanded).mode, "mode-only query parity");
        }
        Check(reveal.NextDelay(state, 1501) != 0, "overdue reveal still forces formatting");
        reveal.Update(state, 1501);
        Check(reveal.NextDelay(state, 1501) == 0, "completed reveal allows formatting reuse");
        std::cout << "Overlay model tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
