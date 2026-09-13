#pragma once

namespace tiger::core
{
    struct ShiftToggleDecision
    {
        bool intercepted = false;
        bool toggle = false;
    };
    // Resolved virtual keys, serialized by the input host. This tracker decides
    // when to toggle; the host owns language state, literal commits and history.
    class ShiftToggleState
    {
    public:
        static bool IsShift(int vk) { return vk == 0x10 || vk == 0xa0 || vk == 0xa1; }
        bool Down() const { return _left || _right; }
        void Reset() { _left = _right = _chordUsed = _skipOnce = false; }
        void SkipAfterSelection() { _skipOnce = true; }
        // Matches the separate handled-modifier-selection release branch.
        void SelectionReleased(int vk)
        {
            if (IsShift(vk) && !Down()) _skipOnce = false;
        }
        // Only call after higher-priority key handling has declined the event.
        void ObserveOtherKeyDown(int vk)
        {
            if (!IsShift(vk) && Down()) _chordUsed = true;
        }
        ShiftToggleDecision Process(int vk, bool down, bool up, bool ctrl,
            bool alt, bool win, bool enabled)
        {
            if (!IsShift(vk)) return {};
            bool& side = vk == 0xa1 ? _right : _left;
            if (down)
            {
                bool wasDown = Down();
                side = true;
                if (!wasDown) _chordUsed = false;
                return {true, false};
            }
            if (up)
            {
                bool matchingDown = side;
                side = false;
                bool toggle = matchingDown && enabled && !_chordUsed && !ctrl && !alt && !win && !_skipOnce;
                if (!Down()) { _chordUsed = false; _skipOnce = false; }
                return {true, toggle};
            }
            return {true, false};
        }
    private:
        bool _left = false, _right = false, _chordUsed = false, _skipOnce = false;
    };
}
