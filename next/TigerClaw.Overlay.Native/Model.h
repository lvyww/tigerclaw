#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace tiger::overlay
{
    // UTF-16 positions match WPF and DirectWrite on every test host.
    using Text = std::u16string;
    struct State
    {
        bool isOff = false, isChinese = false, candidateVisible = false;
        bool candidateHoldWhilePending = false;
        int candidateLayout = 4;
        bool vertical() const { return candidateLayout == 2 || candidateLayout == 4; }
        bool showCode() const { return candidateLayout == 1 || candidateLayout == 2 || candidateLayout == 5; }
        bool hideCandidates() const { return candidateLayout == 5; }
        bool showIndex = false;
        bool hideStatus = false, nativeHook = false;
        Text status, input, codeMask, theme, font;
        Text candidateFrameSession, candidateSelectionToken;
        std::vector<Text> candidates, annotations;
        int composition = 0, caretX = 0, caretY = 0, caretHeight = 0;
        int soundVk = 0, soundVolume = 0, candidateDelay = 0, annotationDelay = 0;
        int selected = -1;
        bool animationEnabled = true;
        int animationDurationMs = 200;
        int residenceDurationMs = 0;
        double fontSize = 0;
        std::int64_t soundSequence = 0, anchorRevision = 0;
        std::int64_t backgroundUntil = 0;
    };
    Text FromUtf8(std::string_view value);
    std::string ToUtf8(std::u16string_view value);
    bool ParseState(std::string_view json, State& state) noexcept;
    enum class DisplayMode { Hidden, CodeOnly, InputOnly, CodeAndCandidates, CandidatesOnly };
    struct Display
    {
        DisplayMode mode = DisplayMode::Hidden;
        Text text;
        int selectionStart = -1, selectionLength = 0;
        std::vector<std::pair<int, int>> candidateRanges;
    };
    Display Format(const State& state, bool expanded = true, bool annotations = true);
    DisplayMode ModeFor(const State& state, bool expanded = true);
    bool SameDisplayContent(const State& left, const State& right);
    // A hint is not permission to show a window: Application also requires an
    // already visible, successfully published candidate frame and valid geometry.
    inline bool PendingCandidateFrame(const State& state)
    {
        return state.candidateHoldWhilePending && !state.candidateVisible &&
            state.isChinese && !state.isOff && (state.composition == 5 || state.composition == 6) &&
            !state.input.empty() && state.candidates.empty() &&
            !state.hideCandidates();
    }
    class Reveal
    {
        bool active_ = false, candidates_ = false, annotations_ = false;
        std::uint64_t started_ = 0;
    public:
        Display Update(const State& state, std::uint64_t now);
        std::uint32_t NextDelay(const State& state, std::uint64_t now) const;
    };
    struct Metrics
    {
        double fontSize, lineHeight, minWidth, left, top, right, bottom;
    };
    Metrics MeasureStyle(const State& state, DisplayMode mode);
    struct Palette
    {
        std::uint32_t foreground, background, border, selection;
        double borderWidth;
        bool asymmetricCorners = false;
    };
    Palette Theme(std::u16string_view name);
}
