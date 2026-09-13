using System;
using System.Collections.Generic;
using System.Globalization;

namespace TigerClaw.Shared
{
    public sealed class ShortcutGesture : IEquatable<ShortcutGesture>
    {
        private static readonly Dictionary<string, int> TokenToVirtualKey = BuildTokenMap();
        private static readonly Dictionary<int, string> VirtualKeyToToken = BuildReverseTokenMap();

        public int VirtualKey { get; }
        public bool Control { get; }
        public bool Alt { get; }
        public bool Shift { get; }

        private ShortcutGesture(int virtualKey, bool control, bool alt, bool shift)
        {
            VirtualKey = virtualKey;
            Control = control;
            Alt = alt;
            Shift = shift;
        }

        public static bool TryCreate(
            int virtualKey,
            bool control,
            bool alt,
            bool shift,
            bool win,
            out ShortcutGesture gesture)
        {
            gesture = null;
            if (win || (!control && !alt) || virtualKey <= 0 || virtualKey > 0xFF || IsModifierKey(virtualKey))
            {
                return false;
            }

            gesture = new ShortcutGesture(virtualKey, control, alt, shift);
            return true;
        }

        public static bool TryParse(string value, out ShortcutGesture gesture)
        {
            gesture = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            bool control = false;
            bool alt = false;
            bool shift = false;
            bool win = false;
            int virtualKey = 0;
            foreach (string rawPart in value.Split('+'))
            {
                string part = (rawPart ?? string.Empty).Trim();
                if (part.Length == 0)
                {
                    return false;
                }

                if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
                {
                    if (control) return false;
                    control = true;
                }
                else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
                {
                    if (alt) return false;
                    alt = true;
                }
                else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
                {
                    if (shift) return false;
                    shift = true;
                }
                else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase))
                {
                    if (win) return false;
                    win = true;
                }
                else
                {
                    if (virtualKey != 0 || !TryParseVirtualKey(part, out virtualKey))
                    {
                        return false;
                    }
                }
            }

            return TryCreate(virtualKey, control, alt, shift, win, out gesture);
        }

        public bool Matches(int virtualKey, bool shift, bool control, bool alt, bool win)
        {
            return !win &&
                   VirtualKey == virtualKey &&
                   Shift == shift &&
                   Control == control &&
                   Alt == alt;
        }

        public string ToConfigString()
        {
            var parts = new List<string>(4);
            if (Control) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(GetTokenForVirtualKey(VirtualKey));
            return string.Join("+", parts);
        }

        public string ToDisplayString()
        {
            var parts = new List<string>(4);
            if (Control) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(GetDisplayNameForVirtualKey(VirtualKey));
            return string.Join("+", parts);
        }

        public bool Equals(ShortcutGesture other)
        {
            return other != null &&
                   VirtualKey == other.VirtualKey &&
                   Control == other.Control &&
                   Alt == other.Alt &&
                   Shift == other.Shift;
        }

        public override bool Equals(object obj) => Equals(obj as ShortcutGesture);

        public override int GetHashCode()
        {
            int hash = VirtualKey;
            hash = (hash * 397) ^ (Control ? 1 : 0);
            hash = (hash * 397) ^ (Alt ? 1 : 0);
            hash = (hash * 397) ^ (Shift ? 1 : 0);
            return hash;
        }

        public static string GetTokenForVirtualKey(int virtualKey)
        {
            return VirtualKeyToToken.TryGetValue(virtualKey, out string token)
                ? token
                : "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);
        }

        public static string GetDisplayNameForVirtualKey(int virtualKey)
        {
            if (virtualKey >= 0x30 && virtualKey <= 0x39)
            {
                return ((char)virtualKey).ToString();
            }
            if (virtualKey >= 0x41 && virtualKey <= 0x5A)
            {
                return ((char)virtualKey).ToString();
            }
            if (virtualKey >= 0x70 && virtualKey <= 0x87)
            {
                return "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture);
            }

            switch (virtualKey)
            {
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x20: return "空格";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
                default: return GetTokenForVirtualKey(virtualKey);
            }
        }

        private static bool TryParseVirtualKey(string token, out int virtualKey)
        {
            virtualKey = 0;
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(
                    token.Substring(2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out virtualKey);
            }

            return TokenToVirtualKey.TryGetValue(token, out virtualKey);
        }

        private static bool IsModifierKey(int virtualKey)
        {
            return virtualKey == 0x10 || virtualKey == 0xA0 || virtualKey == 0xA1 ||
                   virtualKey == 0x11 || virtualKey == 0xA2 || virtualKey == 0xA3 ||
                   virtualKey == 0x12 || virtualKey == 0xA4 || virtualKey == 0xA5 ||
                   virtualKey == 0x5B || virtualKey == 0x5C;
        }

        private static Dictionary<string, int> BuildTokenMap()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "VK_BACK", 0x08 }, { "VK_TAB", 0x09 }, { "VK_RETURN", 0x0D },
                { "VK_ESCAPE", 0x1B }, { "VK_SPACE", 0x20 },
                { "VK_PRIOR", 0x21 }, { "VK_NEXT", 0x22 },
                { "VK_END", 0x23 }, { "VK_HOME", 0x24 },
                { "VK_LEFT", 0x25 }, { "VK_UP", 0x26 },
                { "VK_RIGHT", 0x27 }, { "VK_DOWN", 0x28 },
                { "VK_INSERT", 0x2D }, { "VK_DELETE", 0x2E },
                { "VK_OEM_1", 0xBA }, { "VK_OEM_PLUS", 0xBB },
                { "VK_OEM_COMMA", 0xBC }, { "VK_OEM_MINUS", 0xBD },
                { "VK_OEM_PERIOD", 0xBE }, { "VK_OEM_2", 0xBF },
                { "VK_OEM_3", 0xC0 }, { "VK_OEM_4", 0xDB },
                { "VK_OEM_5", 0xDC }, { "VK_OEM_6", 0xDD },
                { "VK_OEM_7", 0xDE }
            };
            for (int i = 0; i <= 9; i++) map["VK_" + i] = 0x30 + i;
            for (int i = 0; i < 26; i++) map["VK_" + (char)('A' + i)] = 0x41 + i;
            for (int i = 1; i <= 24; i++) map["VK_F" + i] = 0x6F + i;
            return map;
        }

        private static Dictionary<int, string> BuildReverseTokenMap()
        {
            var map = new Dictionary<int, string>();
            foreach (KeyValuePair<string, int> pair in TokenToVirtualKey)
            {
                map[pair.Value] = pair.Key;
            }
            return map;
        }
    }

    public enum ShortcutConflictKind
    {
        None = 0,
        CtrlSpace = 1,
        NativeHookAltBackslash = 2,
        CtrlDigitReorder = 3,
        AltDigitReorder = 4
    }

    public static class ShortcutBindingRules
    {
        public static ShortcutConflictKind GetReservedConflict(
            ShortcutGesture gesture,
            bool ctrlSpaceEnabled,
            bool nativeHookAltBackslashEnabled)
        {
            if (gesture == null)
            {
                return ShortcutConflictKind.None;
            }
            if (ctrlSpaceEnabled &&
                gesture.VirtualKey == 0x20 &&
                gesture.Control && !gesture.Alt && !gesture.Shift)
            {
                return ShortcutConflictKind.CtrlSpace;
            }
            if (nativeHookAltBackslashEnabled &&
                gesture.VirtualKey == 0xDC &&
                gesture.Alt && !gesture.Control && !gesture.Shift)
            {
                return ShortcutConflictKind.NativeHookAltBackslash;
            }
            if (gesture.VirtualKey >= 0x31 && gesture.VirtualKey <= 0x39)
            {
                if (gesture.Control && !gesture.Alt)
                {
                    return ShortcutConflictKind.CtrlDigitReorder;
                }
                if (gesture.Alt && !gesture.Control && !gesture.Shift)
                {
                    return ShortcutConflictKind.AltDigitReorder;
                }
            }
            return ShortcutConflictKind.None;
        }
    }
}
