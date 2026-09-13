using System;
using System.IO;
using System.Text;
namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int ExportNativeCoreLetterTable(string path, bool digits = false)
        {
            if (File.Exists(path)) throw new IOException("Letter-table output already exists");
            var output = new StringBuilder("// Generated from .NET " + Environment.Version + "; do not hand-edit.\n#pragma once\n#include <cstdint>\nnamespace tiger::core {\nstruct LetterRange { std::uint16_t first, last; };\ninline constexpr LetterRange LetterRanges[] = {\n");
            for (int start = 0; start < 65536; start++)
            {
                if (!(digits ? char.IsDigit((char)start) : char.IsLetter((char)start))) continue;
                int end = start;
                while (end < 65535 && (digits ? char.IsDigit((char)(end+1)) : char.IsLetter((char)(end+1)))) end++;
                output.Append("    {0x").Append(start.ToString("x")).Append(", 0x").Append(end.ToString("x")).Append("},\n");
                start = end;
            }
            output.Append("};\n}\n");
            File.WriteAllText(path, digits ? output.ToString().Replace("Letter", "Digit") : output.ToString(), new UTF8Encoding(false));
            Console.WriteLine("Exported UTF-16 letter properties");
            return 0;
        }
    }
}
