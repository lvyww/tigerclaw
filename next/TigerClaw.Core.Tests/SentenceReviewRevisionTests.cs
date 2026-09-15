using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static object RevisionCall(object value, string name, Dictionary<string, object> values)
        {
            MethodInfo method = value.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == name);
            return method.Invoke(value, method.GetParameters().Select(p => values.TryGetValue(p.Name, out var x) ? x :
                p.HasDefaultValue ? p.DefaultValue : null).ToArray());
        }
        private static object RevisionProperty(object value, string name) => value?.GetType().GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value);

        private static string RevisionSnapshot(object result)
        {
            var text = new StringBuilder();
            void Value(object value)
            {
                string rendered = value is double number ? BitConverter.DoubleToInt64Bits(number).ToString("X16") :
                    value?.ToString() ?? string.Empty;
                text.Append(rendered.Length).Append(':').Append(rendered).Append('|');
            }
            Value(RevisionProperty(result, "RawCode"));
            Value(RevisionProperty(result, "LearningAffected"));
            Value(RevisionProperty(result, "LearningMode"));
            foreach (object candidate in (IEnumerable)RevisionProperty(result, "Candidates"))
            {
                foreach (string field in new[] { "Text", "SegmentedCode", "BaseScore", "FinalScore", "ConfidenceScore", "SupplementScore", "LearningScore", "MaxLexiconRank" })
                    Value(RevisionProperty(candidate, field));
                object boundary = RevisionProperty(candidate, "Boundary");
                while (boundary != null)
                {
                    Value(RevisionProperty(boundary, "TextLength"));
                    Value(RevisionProperty(boundary, "RawLength"));
                    Value(RevisionProperty(boundary, "LearningScore"));
                    boundary = RevisionProperty(boundary, "Previous");
                }
                text.Append(';');
            }
            return text.ToString();
        }

        private static int RunSentenceRevisionReview(string baselineAssembly, string modelPath)
        {
            baselineAssembly = Path.GetFullPath(baselineAssembly);
            Review(File.Exists(baselineAssembly), "baseline assembly exists");
            var context = new AssemblyLoadContext("sentence-review-" + Guid.NewGuid().ToString("N"), true);
            context.Resolving += (owner, name) =>
            {
                string path = Path.Combine(Path.GetDirectoryName(baselineAssembly), name.Name + ".dll");
                return File.Exists(path) ? owner.LoadFromAssemblyPath(path) : null;
            };
            Assembly oldAssembly = context.LoadFromAssemblyPath(baselineAssembly);
            Type oldIndex = oldAssembly.GetType("TigerClaw.Core.SentenceLexiconIndex", true);
            Type oldDecoder = oldAssembly.GetType("TigerClaw.Core.SentenceInputDecoder", true);
            Type oldPenalty = oldAssembly.GetType("TigerClaw.Core.SentenceIsolationPenalty", true);
            Type oldNgram = oldAssembly.GetType("TigerClaw.Core.SentenceNgramModel", true);
            var lexicon = new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙", "\U00020000", "a\u0301" },
                ["bb"] = new() { "国", "中" }, ["ab"] = new() { "丙" }, ["ba"] = new() { "丁" },
                ["aab"] = new() { "甲乙" }, ["aabb"] = new() { "国中", "中" } };
            var build = oldIndex.GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var indexArguments = build.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            indexArguments[0] = lexicon;
            object index = build.Invoke(null, indexArguments);
            string temporary = Path.Combine(Path.GetTempPath(), "tigerclaw-revision-model-" + Guid.NewGuid().ToString("N") + ".bin");
            ReviewWriteModel(temporary);
            int snapshots = 0;
            try
            {
                foreach (string path in modelPath == null ? new[] { null, temporary } : new[] { null, temporary, modelPath })
                {
                    object oldModel = path == null ? null : oldNgram.GetMethod("Load", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { path });
                    using var newModel = path == null ? null : SentenceNgramModel.Load(path);
                    try
                    {
                        foreach (bool duplicate in new[] { false, true })
                        foreach (int beam in new[] { 1, 16, 128 })
                        {
                            var ctor = oldDecoder.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
                            var options = new Dictionary<string, object> { ["lexicon"] = index, ["languageModel"] = oldModel,
                                ["beamWidth"] = beam, ["emittedCharacterReward"] = 2.0, ["wholeInputSingleCharacterReward"] = 5.0,
                                ["isolationPenalty"] = oldPenalty.GetField("None", BindingFlags.Public | BindingFlags.Static).GetValue(null),
                                ["allowDuplicateSingleCharacters"] = duplicate };
                            object old = ctor.Invoke(ctor.GetParameters().Select(p => options.TryGetValue(p.Name, out var x) ? x : p.DefaultValue).ToArray());
                            using var current = ReviewDecoder(lexicon, newModel, beam, duplicate);
                            var random = new Random(8123 + beam);
                            string raw = "";
                            for (int i = 0; i < 160; i++)
                            {
                                if (raw.Length > 24 || (raw.Length > 2 && random.Next(3) == 0)) raw = raw[..random.Next(raw.Length)];
                                else raw += new[] { "aa", "bb", "ab", "ba", "aab", "a", "b", "12", ";" }[random.Next(9)];
                                var result = RevisionCall(old, "DecodeFull", new() { ["rawCode"] = raw, ["candidateLimit"] = 20 });
                                Review(RevisionSnapshot(result) == RevisionSnapshot(current.Decode(raw, 20)), "old/new exact snapshot " + raw);
                                snapshots++;
                            }
                            object oldSeed = ((IEnumerable)RevisionProperty(RevisionCall(old, "DecodeFull", new() { ["rawCode"] = "aa" }), "Candidates")).Cast<object>().First();
                            var seed = current.DecodeFull("aa").Candidates[0];
                            object oldLock = Activator.CreateInstance(oldAssembly.GetType("TigerClaw.Core.SentenceLockedPrefix", true),
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { (object)"aa", RevisionProperty(oldSeed, "Text"), RevisionProperty(oldSeed, "Boundary") }, CultureInfo.InvariantCulture);
                            var prefix = new SentenceLockedPrefix("aa", seed.Text, seed.Boundary);
                            for (int i = 0; i < 80; i++)
                            {
                                raw = "aa" + string.Concat(Enumerable.Repeat(i % 3 == 0 ? "ab" : "aa", 1 + i % 12));
                                var result = RevisionCall(old, "Decode", new() { ["rawCode"] = raw, ["candidateLimit"] = 20, ["lockedPrefix"] = oldLock });
                                Review(RevisionSnapshot(result) == RevisionSnapshot(current.Decode(raw, 20, lockedPrefix: prefix)), "old/new exact locked snapshot");
                                snapshots++;
                            }
                        }
                    }
                    finally { (oldModel as IDisposable)?.Dispose(); }
                }
                Console.WriteLine(JsonSerializer.Serialize(new { test = "independent_sentence_revisions", status = "passed", snapshots,
                    baseline_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(baselineAssembly))),
                    current_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SentenceInputDecoder).Assembly.Location))),
                    production_model = modelPath != null, old_confidence_policy_compared = false }));
                return 0;
            }
            finally
            {
                context.Unload();
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    try { File.Delete(temporary); break; }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        if (attempt < 5) { System.Threading.Thread.Sleep(100 << attempt); continue; }
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { phase = "cleanup", status = "warning",
                            path = temporary, error = error.Message, test_result_unchanged = true }));
                    }
                }
            }
        }
    }
}
