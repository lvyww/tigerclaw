namespace TigerClaw.Engine.Experimental.Input;

public static class WindowsInputEventMapper
{
    public static InputEvent Map(
        int vk,
        int scanCode,
        string? action,
        bool shift,
        bool ctrl,
        bool alt,
        bool win,
        bool capsLock,
        bool numLock,
        int repeat,
        bool extended)
    {
        int resolvedVk = ResolvePhysicalModifierKey(vk, scanCode, extended);
        return new InputEvent(
            MapKey(resolvedVk),
            MapText(resolvedVk, shift, capsLock),
            BuildModifiers(shift, ctrl, alt, win, capsLock, numLock),
            IsKeyUp(action) ? KeyAction.KeyUp : KeyAction.KeyDown,
            "VK_" + resolvedVk.ToString("X2"),
            scanCode,
            extended,
            repeat > 1,
            repeat < 1 ? 1 : repeat);
    }

    private static bool IsKeyUp(string? action)
    {
        return string.Equals(action, "up", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(action, "key_up", StringComparison.OrdinalIgnoreCase);
    }

    private static InputModifiers BuildModifiers(
        bool shift,
        bool ctrl,
        bool alt,
        bool win,
        bool capsLock,
        bool numLock)
    {
        InputModifiers modifiers = InputModifiers.None;
        if (shift) { modifiers |= InputModifiers.Shift; }
        if (ctrl) { modifiers |= InputModifiers.Ctrl; }
        if (alt) { modifiers |= InputModifiers.Option; }
        if (win) { modifiers |= InputModifiers.Command; }
        if (capsLock) { modifiers |= InputModifiers.CapsLock; }
        if (numLock) { modifiers |= InputModifiers.NumLock; }
        return modifiers;
    }

    private static int ResolvePhysicalModifierKey(int vk, int scanCode, bool extended)
    {
        if (vk == WindowsVirtualKey.Shift)
        {
            return scanCode == 0x36 ? WindowsVirtualKey.RightShift : WindowsVirtualKey.LeftShift;
        }

        if (vk == WindowsVirtualKey.Control)
        {
            return extended ? WindowsVirtualKey.RightControl : WindowsVirtualKey.LeftControl;
        }

        if (vk == WindowsVirtualKey.Menu)
        {
            return extended ? WindowsVirtualKey.RightMenu : WindowsVirtualKey.LeftMenu;
        }

        return vk;
    }

    private static InputKey MapKey(int vk)
    {
        if (vk >= WindowsVirtualKey.A && vk <= WindowsVirtualKey.Z)
        {
            return InputKey.Character;
        }

        if (vk >= WindowsVirtualKey.Digit0 && vk <= WindowsVirtualKey.Digit9)
        {
            return InputKey.Digit;
        }

        return vk switch
        {
            WindowsVirtualKey.Space => InputKey.Space,
            WindowsVirtualKey.Back => InputKey.Backspace,
            WindowsVirtualKey.Return => InputKey.Enter,
            WindowsVirtualKey.Escape => InputKey.Escape,
            WindowsVirtualKey.Tab => InputKey.Tab,
            WindowsVirtualKey.Up => InputKey.ArrowUp,
            WindowsVirtualKey.Down => InputKey.ArrowDown,
            WindowsVirtualKey.Left => InputKey.ArrowLeft,
            WindowsVirtualKey.Right => InputKey.ArrowRight,
            WindowsVirtualKey.PageUp => InputKey.ArrowUp,
            WindowsVirtualKey.PageDown => InputKey.ArrowDown,
            WindowsVirtualKey.Oem1 => InputKey.Semicolon,
            WindowsVirtualKey.Oem7 => InputKey.Quote,
            _ => InputKey.Unknown,
        };
    }

    private static string? MapText(int vk, bool shift, bool capsLock)
    {
        if (vk >= WindowsVirtualKey.A && vk <= WindowsVirtualKey.Z)
        {
            bool upper = shift ^ capsLock;
            char baseChar = upper ? 'A' : 'a';
            return ((char)(baseChar + (vk - WindowsVirtualKey.A))).ToString();
        }

        if (vk >= WindowsVirtualKey.Digit0 && vk <= WindowsVirtualKey.Digit9)
        {
            return ((char)('0' + (vk - WindowsVirtualKey.Digit0))).ToString();
        }

        return vk switch
        {
            WindowsVirtualKey.Oem1 => shift ? ":" : ";",
            WindowsVirtualKey.Oem7 => shift ? "\"" : "'",
            _ => null,
        };
    }
}
