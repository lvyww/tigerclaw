using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayFontResolver
    {
        private readonly Dictionary<string, FontFamily> _cache = new Dictionary<string, FontFamily>(StringComparer.Ordinal);

        public FontFamily Resolve(string fontName)
        {
            if (string.IsNullOrWhiteSpace(fontName))
            {
                return null;
            }

            string key = fontName.Trim();
            if (_cache.TryGetValue(key, out FontFamily family))
            {
                return family;
            }

            family = CreateFontFamily(key);
            if (family != null)
            {
                _cache[key] = family;
            }

            return family;
        }

        private static FontFamily CreateFontFamily(string fontName)
        {
            try
            {
                if (fontName.StartsWith("#", StringComparison.Ordinal))
                {
                    string currentPath = AppDomain.CurrentDomain.BaseDirectory;
                    string fontRoot = Path.Combine(currentPath, "\u5b57\u4f53");
                    if (!Directory.Exists(fontRoot))
                    {
                        return null;
                    }

                    return new FontFamily(new Uri(fontRoot + "\\", UriKind.Absolute), "./" + fontName);
                }

                return new FontFamily(fontName);
            }
            catch
            {
                return null;
            }
        }
    }
}
