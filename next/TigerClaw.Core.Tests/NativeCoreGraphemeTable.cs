using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int ExportNativeCoreGraphemeTable(string path)
        {
            if (File.Exists(path)) throw new IOException("Grapheme-table output already exists");
            var method = typeof(CharUnicodeInfo).GetMethod("GetGraphemeClusterBreakType", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(Rune) }, null) ?? throw new InvalidOperationException("Reference grapheme API changed");
            var enumType = method.ReturnType;
            var output = new StringBuilder("// Generated from .NET " + Environment.Version + "; do not hand-edit.\n#pragma once\n#include <cstdint>\nnamespace tiger::core {\nenum class GraphemeType {\n");
            foreach (var name in Enum.GetNames(enumType)) output.Append("    ").Append(name).Append(",\n");
            output.Append("};\nstruct GraphemeRange { std::uint32_t first, last; GraphemeType type; };\ninline constexpr GraphemeRange GraphemeRanges[] = {\n");
            string previous = null;
            int start = 0;
            void Flush(int last)
            {
                if (previous != null && previous != "Other") output.Append("    {0x").Append(start.ToString("x")).Append(", 0x").Append(last.ToString("x"))
                    .Append(", GraphemeType::").Append(previous).Append("},\n");
            }
            for (int scalar = 0; scalar <= 0x10ffff; scalar++)
            {
                string type = scalar >= 0xd800 && scalar <= 0xdfff ? "Other" : method.Invoke(null, new object[] { new Rune(scalar) }).ToString();
                if (type == previous) continue;
                Flush(scalar - 1); previous = type; start = scalar;
            }
            Flush(0x10ffff);
            output.Append("};\n}\n");
            File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
            Console.WriteLine("Exported grapheme properties from .NET " + Environment.Version);
            return 0;
        }
    }
}
