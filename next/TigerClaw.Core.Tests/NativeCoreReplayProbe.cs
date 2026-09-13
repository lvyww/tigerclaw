using System;
using System.Linq;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunNativeCoreReplayProbe()
        {
            var cache = new KeyRequestReplayCache();
            int executions = 0;
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                using var document = JsonDocument.Parse(line);
                var input = document.RootElement;
                string Units(string field) => !input.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null
                    ? null : new string(value.EnumerateArray().Select(unit => checked((char)unit.GetInt32())).ToArray());
                int Int(string field, int fallback) => input.TryGetProperty(field, out var value) ? value.GetInt32() : fallback;
                string Text(string field) => input.TryGetProperty(field, out var value) ? value.GetString() : string.Empty;
                string key = KeyRequestReplayCache.BuildKey(Units("session"), Units("event"));
                object output;
                switch (input.GetProperty("op").GetString())
                {
                    case "reset":
                        cache = new KeyRequestReplayCache(Int("capacity", 512));
                        executions = 0;
                        output = new { ok = true };
                        break;
                    case "build":
                        output = new { key = key?.Select(c => (int)c).ToArray() };
                        break;
                    case "store":
                        cache.Store(key, Text("response"));
                        output = new { ok = true };
                        break;
                    case "get":
                        bool hit = cache.TryGet(key, Int("seq", 0), out string response);
                        output = new { hit, response };
                        break;
                    case "execute":
                        if (!cache.TryGet(key, Int("seq", 0), out string result))
                        {
                            ++executions;
                            result = Text("response");
                            cache.Store(key, result);
                        }
                        output = new { response = result, executions };
                        break;
                    default: throw new ArgumentException("Unknown replay probe operation");
                }
                Console.WriteLine(JsonSerializer.Serialize(output));
            }
            return 0;
        }
    }
}
