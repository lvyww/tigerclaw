#include "Model.h"
#include "Placement.h"
#include "MenuDismiss.h"
#include "FrameTransition.h"
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
        State pinyin;
        Check(ParseState(R"({"CompositionState":6,"CandidateSelectionToken":"0123456789abcdef0123456789abcdef","CandidateVisible":true,"Candidates":["你好","你"],"InputCode":"nihao","ShowInputCodeInCandidateWindow":true})", pinyin), "pinyin UI parse");
        auto pinyinDisplay = Format(pinyin);
        Check(pinyin.candidateSelectionToken.size() == 32 && pinyinDisplay.candidateRanges.size() == 2, "pinyin click token and hit ranges");
        for (const auto& range : pinyinDisplay.candidateRanges)
            Check(range.first >= 0 && range.second > 0 && range.first + range.second <= static_cast<int>(pinyinDisplay.text.size()), "candidate hit range bound");
        FrameTransition transition;
        RefreshRateCache rates;
        unsigned queries=0, rate=144;
        auto query=[&] { ++queries; return rate; };
        Check(rates.Get(1,0,query)==144 && queries==1, "initial display query");
        rate=240;
        Check(rates.Get(1,1999,query)==144 && queries==1, "refresh cache TTL");
        Check(rates.Get(1,2000,query)==240 && queries==2, "same display rate changed");
        rate=75;
        Check(rates.Get(2,2001,query)==75 && queries==3, "cross display immediate query");
        rates.Invalidate(); rate=0;
        Check(rates.Get(2,2002,query)==60 && queries==4, "display notification and query failure fallback");
        for(unsigned invalid:{1,29,1001}) { rates.Invalidate();rate=invalid;
            Check(rates.Get(2,2003,query)==60,"invalid display rate fallback"); }
        State animated;
        Check(ParseState(R"({})", animated) && animated.animationEnabled &&
            animated.animationDurationMs == 200, "default geometry animation duration");
        Check(ParseState(R"({"CandidateAnimationEnabled":false,"CandidateAnimationDurationMs":350})", animated) &&
            !animated.animationEnabled && animated.animationDurationMs == 350, "animation settings parse");
        Check(ParseState(R"({})", animated) && animated.residenceDurationMs == 0, "old Core defaults residence off");
        Check(ParseState(R"({"CandidateResidenceDurationMs":3000})", animated) && animated.residenceDurationMs == 3000, "custom residence duration");
        Check(ParseState(R"({"CandidateResidenceDurationMs":-1})", animated) && animated.residenceDurationMs == 0, "negative residence disabled");
        Check(ParseState(R"({"CandidateResidenceDurationMs":60001})", animated) && animated.residenceDurationMs == 60000, "residence duration capped");
        FrameRect from{100, 200, 400, 200};
        for (FrameRect target : {FrameRect{200,300,400,200}, FrameRect{100,200,600,300}, FrameRect{100,200,200,100}})
        {
            transition.Start(from, target, 1000, 60);
            Check(transition.Duration() == 200 && transition.Interval() == 1000.0/60, "all geometry shares default duration");
            Check(transition.Sample(1000) == from, "starts at displayed rectangle");
            auto middle = transition.Sample(1100);
            Check(middle != from && middle != target, "intermediate geometry");
            Check(transition.Sample(1200) == target && !transition.Active(), "deadline reaches target");
            transition.Start(middle, from, 2000, 144, 350);
            Check(transition.Duration() == 350 && transition.Interval() == 1000.0/144, "custom duration preserves cadence");
            Check(transition.Sample(2000) == middle && transition.Sample(2350) == from, "interruption starts from current rectangle");
        }
        transition.Start(from, {0,0,10,10}, 0, 60, 0);
        Check(!transition.Active() && transition.Sample(0).width == 10, "zero duration is immediate");
        transition.Cancel();
        Check(!transition.Active(), "cancel clears animation");
        for (unsigned hz : {30,60,75,120,144,165,240,360,1000})
        {
            transition.Start(from, {1000,2000,800,600}, 123.25, hz, 60000);
            for (int i=0;i<10000;++i)
            {
                double now = 123.25 + i*5.91;
                double next = transition.Next(now,hz);
                Check(next > now && next-now <= 1000.0/hz + 1e-8, "future deadline skips missed frames");
                double index = (next-123.25)*hz/1000;
                Check(std::abs(index-std::round(index)) < 1e-7, "no accumulated cadence drift");
            }
            auto before = transition.Sample(30123.25);
            transition.Next(30123.25, hz==60?144:60);
            Check(transition.Sample(30123.25)==before, "rate change preserves progress");
            Check(transition.Sample(70000)==transition.Target() && !transition.Active(), "blocked UI lands at final geometry");
        }
        MenuDismiss dismiss{false, true, false};
        Check(!dismiss.Update(false, false, true, false), "opening right-button hold does not dismiss");
        Check(!dismiss.Update(false, false, false, false), "button release does not dismiss");
        Check(!dismiss.Update(true, true, false, false), "menu or submenu click stays open");
        Check(!dismiss.Update(false, true, false, false), "dragging a held button outside is not a new click");
        Check(!dismiss.Update(false, false, false, false), "outside release stays open");
        Check(dismiss.Update(false, true, false, false), "outside left click dismisses without foreground ownership");
        dismiss = {};
        Check(dismiss.Update(false, false, true, false), "outside right click dismisses");
        dismiss = {};
        Check(dismiss.Update(true, false, false, true), "escape dismisses even over a menu");
        State state;
        for (int dpi : {96, 120, 144, 192})
        {
            int width = (26 * dpi + 95) / 96, height = (50 * dpi + 95) / 96;
            for (WorkArea work : {WorkArea{0, 0, 1920, 1040}, WorkArea{-1920, -1080, 0, -40},
                WorkArea{60, 40, 1920, 1080}})
            {
                auto start = StatusPosition({}, width, height, work, true);
                Check(start.x + width == work.right - 2 && start.y + height == work.bottom - 2,
                    "status uses actual scaled size within work area");
                auto clamp = StatusPosition({work.right, work.bottom}, width, height, work, false);
                Check(clamp.x == start.x && clamp.y == start.y, "work area change clamps status");
                auto keep = StatusPosition({work.left + 10, work.top + 10}, width, height, work, false);
                Check(keep.x == work.left + 10 && keep.y == work.top + 10, "valid dragged position retained");
            }
        }
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
        Check(position.IsAbove() && point.x == 1618 && point.y == 935, "anchor refresh permits a required above placement");
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
