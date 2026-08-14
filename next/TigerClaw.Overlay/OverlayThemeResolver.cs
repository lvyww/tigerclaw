using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayThemeResolver
    {
        private readonly Dictionary<string, OverlayThemePalette> _themeMap =
            new Dictionary<string, OverlayThemePalette>(StringComparer.Ordinal)
            {
                { "\u9ed8\u8ba4", new OverlayThemePalette("FF000000", "FFFFF8F3", "FF1A7B6B", "48000000", 1.25, new CornerRadius(5)) },
                { "\u901a\u900f", new OverlayThemePalette("FF2277EE", "00000000", "00000000", "482277EE", 1.25, new CornerRadius(5)) },
                { "\u4e00\u822c\u901a\u900f", new OverlayThemePalette("FF2277EE", "1A000000", "00000000", "482277EE", 1.25, new CornerRadius(5)) },
                { "\u8ff7\u96fe", new OverlayThemePalette("FFD9D9D9", "FF2F2F2F", "FF5A5A5A", "48D9D9D9", 1.25, new CornerRadius(5)) },
                { "\u661f\u591c", new OverlayThemePalette("FFFFDC6A", "FF232B39", "FF3A6B9B", "48FFDC6A", 1.25, new CornerRadius(5)) },
                { "\u7eb8", new OverlayThemePalette("FF111111", "FFF5F2E8", "FFA8A09D", "48111111", 1.3, new CornerRadius(5)) },
                { "\u7c89", new OverlayThemePalette("FF000000", "FFFDF9F5", "FFDEACAC", "48000000", 1.25, new CornerRadius(5)) },
                { "\u8d5b\u535a\u670b\u514b", new OverlayThemePalette("FF71E4FD", "88001122", "FFF651FC", "4871E4FD", 1.5, new CornerRadius(10, 0, 10, 0)) },
                { "\u6e05\u6668", new OverlayThemePalette("FF303030", "FFFDFDFF", "FF56A1DD", "48303030", 1.25, new CornerRadius(5)) }
            };

        public OverlayThemePalette Resolve(string themeName)
        {
            if (_themeMap.TryGetValue(themeName ?? string.Empty, out OverlayThemePalette palette))
            {
                return palette;
            }

            return _themeMap["\u9ed8\u8ba4"];
        }
    }

    internal sealed class OverlayThemePalette
    {
        public OverlayThemePalette(
            string foreground,
            string background,
            string border,
            string candidateSelectionBackground,
            double borderWidth,
            CornerRadius corner)
        {
            Foreground = CreateBrush(foreground);
            Background = CreateBrush(background);
            Border = CreateBrush(border);
            CandidateSelectionBackground = CreateBrush(candidateSelectionBackground);
            BorderWidth = borderWidth;
            Corner = corner;
        }

        public SolidColorBrush Foreground { get; }
        public SolidColorBrush Background { get; }
        public SolidColorBrush Border { get; }
        public SolidColorBrush CandidateSelectionBackground { get; }
        public double BorderWidth { get; }
        public CornerRadius Corner { get; }

        private static SolidColorBrush CreateBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex));
            brush.Freeze();
            return brush;
        }
    }
}
