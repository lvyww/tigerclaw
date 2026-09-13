using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void RunCompactLexiconTests()
        {
            var source = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["z"] = new() { "可", "可以", "可" },
                ["lets"] = new() { "旋", "𠀀", "e\u0301", "\ud800", "" },
                ["AB"] = new() { "可", "可以", "a\0b" },
                ["空"] = new(),
                ["verylongcode12345"] = new() { new string('文', 1000) }
            };
            var compact = CompactLexicon.Build(source);
            void Compare(CompactLexicon value)
            {
                Equal(string.Join("|", source.Keys), string.Join("|", value.Keys), "compact.insertion_order");
                foreach (var pair in source)
                {
                    True(value.TryGetValue(pair.Key.ToUpperInvariant(), out var actual), "compact.lookup");
                    True(pair.Value.SequenceEqual(actual), "compact.candidates_utf16_order");
                }
                True(!value.ContainsKey("absent"), "compact.missing");
            }
            Compare(compact);
            using var stream = new MemoryStream();
            compact.WriteTo(stream);
            byte[] image = stream.ToArray();
            var roundtrip = CompactLexicon.FromBinary(image);
            Compare(roundtrip);
            image[0] = 0;
            Compare(roundtrip); // Loading never retains a caller-owned mutable buffer.
            var changed = compact.WithCandidates("Z", new[] { "新" });
            Equal("新", changed["z"][0], "compact.edit");
            Equal("可", compact["z"][0], "compact.old_snapshot_immutable");
            Equal("z", changed.Keys.First(), "compact.edit_keeps_key_order_and_case");
            Equal("new", compact.WithCandidates("new", new[] { "新" }).Keys.Last(), "compact.append_order");
            var random = new Random(73421);
            var mutated = compact;
            var expected = source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 400; i++)
            {
                string key = "r" + random.Next(40);
                var values = Enumerable.Range(0, random.Next(5)).Select(n => new string((char)('一' + random.Next(200)), random.Next(1, 1000))).ToList();
                expected[key] = values;
                mutated = mutated.WithCandidates(key, values);
                True(expected.Keys.SequenceEqual(mutated.Keys), "compact.random_edit_code_order");
                foreach (var pair in expected) True(pair.Value.SequenceEqual(mutated[pair.Key]), "compact.random_edit_values");
            }
            var huge = compact.WithCandidates("huge", new[] { new string('文', 100000) });
            var shrunk = huge.WithCandidates("huge", Array.Empty<string>());
            True(shrunk.BinarySize < huge.BinarySize / 2, "compact.large_delete_releases_text");
            True(mutated.BinarySize <= CompactLexicon.Build(expected).BinarySize * 2 + 65536, "compact.dead_text_bounded");
            Parallel.For(0, 1000, _ => Compare(roundtrip));
            image = stream.ToArray();
            for (int length = 0; length < image.Length; length++)
            {
                bool rejected = false;
                try { CompactLexicon.FromBinary(image.AsSpan(0, length)); }
                catch (InvalidDataException) { rejected = true; }
                True(rejected, "compact.reject_truncated");
            }
            foreach (int offset in new[] { 0, 4, 8, 12, 16, 20, 20 + source.Count * 4 })
            {
                byte[] bad = (byte[])image.Clone();
                Array.Fill(bad, (byte)255, offset, 4);
                bool rejected = false;
                try { CompactLexicon.FromBinary(bad); }
                catch (InvalidDataException) { rejected = true; }
                True(rejected, "compact.reject_corrupt_header_index");
            }
            CompactLexiconRuntimeTests();
            Console.WriteLine("Compact lexicon tests passed.");
        }

        private static void CompactLexiconRuntimeTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "TigerClaw.Core.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                foreach (string schema in new[] { "普通A", "普通B" })
                {
                    string dir = Path.Combine(root, "码表", schema);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, schema + ".txt"), "可\tz\n可以\tz\n甲\tab\n");
                }
                string pyDir = Path.Combine(root, "拼音反查码表");
                Directory.CreateDirectory(pyDir);
                string pyFile = Path.Combine(pyDir, "py.txt");
                var pagedWords = Enumerable.Range(0, 23).Select(i => "词" + i).ToArray();
                File.WriteAllText(pyFile, "可\tke\n科\tke\n𠀀\the\n" + string.Join("\n", pagedWords.Select(word => word + "\tma")));
                var state = new CoreRuntimeState(root);
                state.Initialize();
                state.TrySetConfigValue("当前码表", "普通A", out _, out _);
                True(state.ReloadLexicon(), "compact.runtime_reload");
                object PinyinStorage() => typeof(CoreRuntimeState).GetField("_pinyinLexicon", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(state);
                var shared = PinyinStorage();
                state.TrySetConfigValue("每页候选个数", "5", out _, out _);
                state.TrySetConfigValue("翻页键", "PageUp/PageDown", out _, out _);
                using (var engine = new InputMethodEngine(state))
                {
                    Press(engine, 0xC0);
                    TypeLetters(engine, "ma");
                    for (int page = 0; page < 4; page++) Press(engine, 0x22);
                    Equal(pagedWords[20], Press(engine, 0x20).TextToOutput, "compact.pinyin_last_page_space");
                    Press(engine, 0xC0);
                    TypeLetters(engine, "ma");
                    Press(engine, 0x22);
                    Equal(pagedWords[6], Press(engine, 0x32).TextToOutput, "compact.pinyin_second_page_rank");
                }
                var oldCandidates = state.GetPinyinCandidates("KE");
                True(oldCandidates.SequenceEqual(new[] { "可", "科" }), "compact.pinyin_order");
                for (int i = 0; i < 4; i++)
                {
                    True(state.TrySwitchRecentSchema(out _), "compact.switch");
                    True(ReferenceEquals(shared, PinyinStorage()), "compact.pinyin_not_owned_by_schema");
                }
                True(state.TryAddCi("z", "新", out _), "compact.add");
                True(state.TryUserTop("z", "新", out _), "compact.top");
                Equal("新", state.GetCandidates("z")[0], "compact.top_order");
                True(state.TryUserAdvance("z", "可以", out _), "compact.advance");
                True(state.TryUserDelete("z", "可", out _), "compact.delete");
                string before = string.Join("|", state.GetCandidates("z"));
                True(state.ReloadLexicon(), "compact.reload_adjustments");
                Equal(before, string.Join("|", state.GetCandidates("z")), "compact.persisted_order");
                File.WriteAllText(pyFile, "刻\tke\n");
                True(state.ReloadLexicon(), "compact.pinyin_reload");
                Equal("刻", state.GetPinyinCandidates("ke")[0], "compact.pinyin_updated");
                Equal("可", oldCandidates[0], "compact.pinyin_old_view_stable");
                True(state.TrySwitchRecentSchema(out _), "compact.switch_after_reload");
                Equal("刻", state.GetPinyinCandidates("ke")[0], "compact.no_stale_pinyin_snapshot");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
