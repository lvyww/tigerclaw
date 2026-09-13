#pragma once
#include <chrono>
#include <optional>

namespace tiger::core
{
    // Host supplies UTC time (matching the C# wall-clock grace policy), and
    // applies language/commit changes iff Process returns true. Call before
    // modifier selection and ordinary dispatch, including non-Ctrl/Space keys.
    class CtrlSpaceState
    {
    public:
        using Time = std::chrono::system_clock::time_point;
        void Reset()
        {
            _ctrlDown = _spaceDown = _armed = _switched = false;
            _lastCtrlUp.reset();
        }
        bool Process(int vk, bool down, bool up, int repeat, bool shift,
            bool alt, bool win, bool enabled, Time now)
        {
            bool ctrl = vk == 0x11 || vk == 0xa2 || vk == 0xa3;
            bool space = vk == 32;
            if (!ctrl && !space)
            {
                if (down && _ctrlDown) { Reset(); return false; }
                Cleanup(now);
                return false;
            }
            if (!enabled) { Reset(); return false; }
            bool handled = false;
            if (down || up)
            {
                if (shift || alt || win) { Reset(); return false; }
                if (down)
                {
                    if (ctrl)
                    {
                        _ctrlDown = true;
                        _armed = !_spaceDown;
                        _switched = false;
                        _lastCtrlUp.reset();
                    }
                    else
                    {
                        _spaceDown = true;
                        if (_ctrlDown && repeat <= 1) handled = Toggle();
                    }
                }
                else if (ctrl)
                {
                    _ctrlDown = false;
                    _lastCtrlUp = now;
                    if (_spaceDown) handled = Toggle();
                }
                else
                {
                    _spaceDown = false;
                    bool withinGrace = _lastCtrlUp && now - *_lastCtrlUp <= Grace;
                    if (_ctrlDown || withinGrace) handled = Toggle();
                }
            }
            Cleanup(now);
            return handled;
        }
    private:
        static constexpr auto Grace = std::chrono::milliseconds(250);
        bool Toggle()
        {
            if (!_armed || _switched) return false;
            _switched = true;
            return true;
        }
        void Cleanup(Time now)
        {
            if (_ctrlDown || _spaceDown) return;
            if (_switched || (_armed && _lastCtrlUp && now - *_lastCtrlUp > Grace)) Reset();
        }
        bool _ctrlDown = false, _spaceDown = false, _armed = false, _switched = false;
        std::optional<Time> _lastCtrlUp;
    };
}
