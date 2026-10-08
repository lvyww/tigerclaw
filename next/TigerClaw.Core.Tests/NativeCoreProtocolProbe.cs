using System.IO;
using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Win32;
using TigerClaw.Core;
using TigerClaw.Shared;
namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunNativeCoreProtocolProbe(string root, string input, string output)
        {
            if(string.IsNullOrWhiteSpace(root)) throw new ArgumentException("An explicit isolated root is required");
            string StartupIdentity()
            {
                using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",false);
                return JsonSerializer.Serialize(new[] { key?.GetValue("TigerClawCore"),key?.GetValue("TigerClaw") });
            }
            string startupBefore=StartupIdentity();
            var state = new CoreRuntimeState(root);
            state.Initialize();
            string mapName = @"Local\TigerClaw.Core.Native.Test." + Guid.NewGuid().ToString("N");
            using var publisher = new UiStatePublisher(mapName);
            using var map = MemoryMappedFile.OpenExisting(mapName);
            using var view = map.CreateViewAccessor();
            var commands=new List<int>();
            using var protocol = new ProtocolHandler(command => commands.Add((int)command), state, publisher, null, true, false);
            using var writer = new StreamWriter(output);
            using var uiWriter = new StreamWriter(output + ".ui.jsonl");
            using var commandWriter=new StreamWriter(output+".commands.jsonl");
            foreach (string line in File.ReadLines(input))
            {
                writer.WriteLine(protocol.Handle(line) ?? "null");
                int length = view.ReadInt32(16);
                if (length <= 0 || length > 131052) throw new InvalidDataException("Missing isolated UI publication");
                byte[] bytes = new byte[length]; view.ReadArray(20, bytes, 0, length);
                uiWriter.WriteLine(Encoding.UTF8.GetString(bytes));
                commandWriter.WriteLine(JsonSerializer.Serialize(commands)); commands.Clear();
            }
            string startupAfter=StartupIdentity();
            File.WriteAllText(output+".startup.json",JsonSerializer.Serialize(new { before=startupBefore,after=startupAfter,equal=startupBefore==startupAfter }));
            if(startupBefore!=startupAfter) throw new InvalidOperationException("Isolated Core fixture changed desktop startup registry");
            return 0;
        }
    }
}
