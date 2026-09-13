#include "ActionShortcuts.h"
#include <stdexcept>

namespace tiger::core
{
    namespace
    {
        bool GestureModifier(int key)
        {
            return key == 0x10 || key == 0x11 || key == 0x12 || key == 0x5b || key == 0x5c ||
                (key >= 0xa0 && key <= 0xa5);
        }
    }
    std::optional<ShortcutGesture> ShortcutGesture::Create(int key, bool control, bool alt, bool shift, bool win)
    {
        if (win || (!control && !alt) || key <= 0 || key > 255 || GestureModifier(key)) return {};
        return ShortcutGesture{key, control, alt, shift};
    }
    bool ShortcutGesture::Matches(int vk, bool shifted, bool ctrl, bool alternate, bool win) const
    {
        return !win && vk == key && shifted == shift && ctrl == control && alternate == alt;
    }
    ShortcutConflict ReservedShortcutConflict(const ShortcutGesture& gesture, bool ctrlSpace, bool hookBackslash)
    {
        if (ctrlSpace && gesture.key == 32 && gesture.control && !gesture.alt && !gesture.shift) return ShortcutConflict::CtrlSpace;
        if (hookBackslash && gesture.key == 0xdc && gesture.alt && !gesture.control && !gesture.shift) return ShortcutConflict::NativeHookAltBackslash;
        if (gesture.key >= 0x31 && gesture.key <= 0x39)
        {
            if (gesture.control && !gesture.alt) return ShortcutConflict::CtrlDigitReorder;
            if (gesture.alt && !gesture.control && !gesture.shift) return ShortcutConflict::AltDigitReorder;
        }
        return ShortcutConflict::None;
    }
    void FilterActionBindings(std::optional<ShortcutGesture>& add, std::optional<ShortcutGesture>& recent,
        bool addEnabled, bool recentEnabled, bool ctrlSpace, bool hookBackslash)
    {
        if (!addEnabled || (add && ReservedShortcutConflict(*add, ctrlSpace, hookBackslash) != ShortcutConflict::None)) add.reset();
        if (!recentEnabled || (recent && ReservedShortcutConflict(*recent, ctrlSpace, hookBackslash) != ShortcutConflict::None)) recent.reset();
        if (add && recent && *add == *recent) { add.reset(); recent.reset(); }
    }
    void ActionShortcuts::ReleaseModifiers(int vk, bool shift, bool ctrl, bool alt, bool win)
    {
        // Engine IsModifierKey additionally includes CapsLock, unlike gesture validation.
        if ((GestureModifier(vk) || vk == 0x14) && !shift && !ctrl && !alt && !win) _awaitingModifierRelease = false;
    }
    void ActionShortcuts::ReleaseKey(int vk)
    {
        if (vk == _oneShotKey) _oneShotKey = 0;
    }
    ShortcutOutcome ActionShortcuts::KeyDown(int vk, bool shift, bool ctrl, bool alt, bool win,
        const std::optional<ShortcutGesture>& add, const std::optional<ShortcutGesture>& recent,
        const std::function<bool()>& switchRecent)
    {
        bool matchesAdd = add && add->Matches(vk, shift, ctrl, alt, win);
        bool matchesRecent = recent && recent->Matches(vk, shift, ctrl, alt, win);
        if (_awaitingModifierRelease && (matchesAdd || matchesRecent)) return ShortcutOutcome::SuppressedRollover;
        if (matchesAdd)
        {
            if (_oneShotKey == vk) return ShortcutOutcome::SuppressedAddRepeat;
            _oneShotKey = vk;
            return ShortcutOutcome::AddWord;
        }
        if (matchesRecent)
        {
            if (_oneShotKey == vk) return ShortcutOutcome::SuppressedSwitchRepeat;
            if (!switchRecent) throw std::logic_error("Recent-schema shortcut requires a switch handler");
            if (switchRecent())
            {
                _oneShotKey = vk;
                _awaitingModifierRelease = true;
                return ShortcutOutcome::SchemaSwitched;
            }
        }
        return ShortcutOutcome::None;
    }
}
