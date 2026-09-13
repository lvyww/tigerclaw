using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TigerClaw.Core;
namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int ExportNativeCoreConfigDefaults(string path)
        {
            if (File.Exists(path)) throw new IOException("Config-default output already exists");
            var pairs = (KeyValuePair<string, string>[])typeof(CoreRuntimeState).GetField("DefaultConfigPairs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            string Literal(string value)
            {
                var result = new StringBuilder("u\"");
                foreach (char c in value) result.Append("\\x").Append(((int)c).ToString("x4"));
                return result.Append('"').ToString();
            }
            var output = new StringBuilder("// Generated from CoreRuntimeState.DefaultConfigPairs; do not hand-edit.\n#pragma once\n#include <string_view>\n#include <utility>\nnamespace tiger::core {\ninline constexpr std::pair<std::u16string_view, std::u16string_view> ConfigDefaults[] = {\n");
            foreach (var pair in pairs) output.Append("    {").Append(Literal(pair.Key)).Append(", ").Append(Literal(pair.Value)).Append("},\n");
            output.Append("};\n}\n");
            File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
            Console.WriteLine("Exported " + pairs.Length + " configuration defaults");
            return 0;
        }
    }
}
