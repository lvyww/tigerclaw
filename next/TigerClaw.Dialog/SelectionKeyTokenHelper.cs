using System.Collections.Generic;
using System.Globalization;

namespace TigerClaw.Dialog
{
    internal static class SelectionKeyTokenHelper
    {
        private static readonly Dictionary<int, string> VkToToken = new Dictionary<int, string>
        {
            { 0x10, "VK_SHIFT" },
            { 0xA0, "VK_LSHIFT" },
            { 0xA1, "VK_RSHIFT" },
            { 0x11, "VK_CONTROL" },
            { 0xA2, "VK_LCONTROL" },
            { 0xA3, "VK_RCONTROL" },
            { 0x12, "VK_MENU" },
            { 0xA4, "VK_LMENU" },
            { 0xA5, "VK_RMENU" },
            { 0x5B, "VK_LWIN" },
            { 0x5C, "VK_RWIN" },
            { 0x14, "VK_CAPITAL" },
            { 0x20, "VK_SPACE" },
            { 0x08, "VK_BACK" },
            { 0x0D, "VK_RETURN" },
            { 0x09, "VK_TAB" },
            { 0x1B, "VK_ESCAPE" },
            { 0xBA, "VK_OEM_1" },
            { 0xBF, "VK_OEM_2" },
            { 0xDB, "VK_OEM_4" },
            { 0xDE, "VK_OEM_7" },
            { 0xBC, "VK_OEM_COMMA" },
            { 0xBE, "VK_OEM_PERIOD" }
        };

        private static readonly Dictionary<int, string> VkToDisplay = new Dictionary<int, string>
        {
            { 0x10, "Shift" },
            { 0xA0, "左 Shift" },
            { 0xA1, "右 Shift" },
            { 0x11, "Ctrl" },
            { 0xA2, "左 Ctrl" },
            { 0xA3, "右 Ctrl" },
            { 0x12, "Alt" },
            { 0xA4, "左 Alt" },
            { 0xA5, "右 Alt" },
            { 0x5B, "左 Win" },
            { 0x5C, "右 Win" },
            { 0x14, "CapsLock" },
            { 0x20, "空格" },
            { 0x08, "Backspace" },
            { 0x0D, "Enter" },
            { 0x09, "Tab" },
            { 0x1B, "Esc" },
            { 0xBA, "; :" },
            { 0xBF, "/ ?" },
            { 0xDB, "[ {" },
            { 0xDE, "' \"" },
            { 0xBC, ", <" },
            { 0xBE, ". >" }
        };

        static SelectionKeyTokenHelper()
        {
            for (int i = 0; i <= 9; i++)
            {
                int vk = 0x30 + i;
                VkToToken[vk] = "VK_" + i.ToString(CultureInfo.InvariantCulture);
                VkToDisplay[vk] = i.ToString(CultureInfo.InvariantCulture);
            }

            for (int i = 0; i < 26; i++)
            {
                int vk = 0x41 + i;
                char ch = (char)('A' + i);
                VkToToken[vk] = "VK_" + ch.ToString(CultureInfo.InvariantCulture);
                VkToDisplay[vk] = ch.ToString(CultureInfo.InvariantCulture);
            }

            for (int i = 1; i <= 24; i++)
            {
                int vk = 0x6F + i;
                string token = "VK_F" + i.ToString(CultureInfo.InvariantCulture);
                VkToToken[vk] = token;
                VkToDisplay[vk] = "F" + i.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static string GetTokenForVirtualKey(int vk)
        {
            if (VkToToken.TryGetValue(vk, out string token))
            {
                return token;
            }

            return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
        }

        public static string GetDisplayNameForVirtualKey(int vk)
        {
            if (VkToDisplay.TryGetValue(vk, out string name))
            {
                return name;
            }

            return GetTokenForVirtualKey(vk);
        }
    }
}
