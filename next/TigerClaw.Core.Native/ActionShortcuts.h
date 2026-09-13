#pragma once
#include <optional>
#include <functional>
#include <string>
#include <string_view>
#include "ConfigParser.h"

namespace tiger::core
{
    struct ShortcutGesture
    {
        int key;
        bool control, alt, shift;
        static std::optional<ShortcutGesture> Create(int key, bool control, bool alt, bool shift, bool win = false);
        static std::optional<ShortcutGesture> Parse(std::u16string_view value);
        std::u16string ConfigString() const;
        bool Matches(int vk, bool shifted, bool ctrl, bool alternate, bool win) const;
        bool operator==(const ShortcutGesture&) const = default;
    };
    enum class ShortcutConflict { None, CtrlSpace, NativeHookAltBackslash, CtrlDigitReorder, AltDigitReorder };
    ShortcutConflict ReservedShortcutConflict(const ShortcutGesture& gesture,
        bool ctrlSpaceEnabled, bool nativeHookAltBackslashEnabled);
    void FilterActionBindings(std::optional<ShortcutGesture>& add, std::optional<ShortcutGesture>& recent,
        bool addEnabled, bool recentEnabled, bool ctrlSpaceEnabled, bool nativeHookAltBackslashEnabled);
    struct ActionBindings
    {
        std::optional<ShortcutGesture> addWord, recentSchema;
    };
    ActionBindings LoadActionBindings(const ConfigValues& config);
    enum class ShortcutOutcome { None, AddWord, SchemaSwitched, SuppressedAddRepeat, SuppressedSwitchRepeat, SuppressedRollover };
    class ActionShortcuts
    {
    public:
        // Call on physical release, after modifier selection/Shift interception
        // when integrating the outer dispatcher. No synthetic repeat release.
        void ReleaseKey(int vk);
        bool IsHeldAction(int vk) const { return _oneShotKey == vk; }
        void MarkHandledAction(int vk) { _oneShotKey = vk; }
        void Reset() { _oneShotKey = 0; _awaitingModifierRelease = false; }
        void ReleaseModifiers(int vk, bool shift, bool ctrl, bool alt, bool win);
        // Bindings passed here have already been filtered for enable flags and
        // reserved conflicts. The switch callback includes schema migration.
        ShortcutOutcome KeyDown(int vk, bool shift, bool ctrl, bool alt, bool win,
            const std::optional<ShortcutGesture>& add,
            const std::optional<ShortcutGesture>& recent,
            const std::function<bool()>& switchRecent);
    private:
        int _oneShotKey = 0;
        bool _awaitingModifierRelease = false;
    };
}
