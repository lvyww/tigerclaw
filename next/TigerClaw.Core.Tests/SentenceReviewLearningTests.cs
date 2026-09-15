using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void ReviewLearningAccumulator()
        {
            const long start = 1700000000;
            var accumulator = new SentenceLearningSnapshot.Accumulator();
            var events = new List<SentenceLearningEvent>();
            var random = new Random(31589);
            string[] texts = { "甲乙", "甲国", "乙", "\U00020000甲", "a\u0301", "国中" };
            string[] contexts = { "", "甲", "乙中", "\U00020000甲" };
            for (int step = 0; step < 180; step++)
            {
                long now = start + step / 13 * 10;
                if (step % 37 == 0) now -= 40;
                if (events.Count > 4 && step % 29 == 0) events.RemoveAt(0);
                string code = "aa" + (char)('a' + random.Next(8));
                events.Add(new SentenceLearningEvent { Id = "event-" + step, Time = start + (step % 23 == 0 ? 1000 : -random.Next(1000)),
                    Mode = "m" + step % 2, Code = code, Text = texts[random.Next(texts.Length)], Context = contexts[random.Next(contexts.Length)] });
                var actual = accumulator.Update(events, now);
                var expected = SentenceLearningSnapshot.Build(events, now);
                foreach (var e in events.Where((_, i) => i % 3 == 0))
                foreach (string context in contexts)
                {
                    ReviewNumber(actual.Score(e.Mode, e.Code, e.Text, context), expected.Score(e.Mode, e.Code, e.Text, context), "aggregate exact score");
                    for (int n = 1; n < e.Text.Length; n++)
                        ReviewNumber(actual.PrefixScore(e.Mode, "aa", e.Text[..n], context), expected.PrefixScore(e.Mode, "aa", e.Text[..n], context), "aggregate prefix score");
                }
                Review(ReferenceEquals(actual, accumulator.Update(events, now)), "unchanged scoring snapshot reused");
            }
            var appendOnly = new SentenceLearningSnapshot.Accumulator();
            events.Clear();
            for (int i = 0; i < 500; i++)
            {
                events.Add(new SentenceLearningEvent { Id = "append-" + i, Time = start, Mode = "m", Code = "aa" + i, Text = "甲乙" });
                appendOnly.Update(events, start);
            }
            Review(appendOnly.ReplayedEvents == 500, "normal append replays only new events");
            bool failed = false;
            try { appendOnly.Update(new[] { events[0], new SentenceLearningEvent { Mode = null } }, start); }
            catch (NullReferenceException) { failed = true; }
            Review(failed, "invalid caller update rejected");
            var recovered = appendOnly.Update(events, start);
            var reference = SentenceLearningSnapshot.Build(events, start);
            ReviewNumber(recovered.Score("m", events[0].Code, "甲乙", ""), reference.Score("m", events[0].Code, "甲乙", ""), "failed aggregate update recovered by full replay");
            foreach (string text in new[] { "", "甲", "甲乙丙", "a\u0301\U00020000甲", "\ud800x\udc00" })
                for (int end = 0; end <= text.Length; end++)
                    Review(SentenceLearning.Context(text, end) == SentenceLearning.Context(text[..end]), "offset context preserves UTF-16 semantics");
        }

        private static string ReviewSeal(string record)
        {
            uint crc = 0xffffffff;
            foreach (byte value in Encoding.ASCII.GetBytes(record))
            {
                crc ^= value;
                for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            return record + "\t" + (~crc).ToString(CultureInfo.InvariantCulture) + "\n";
        }
        private static SentenceLearningEvent ReviewEvent(string id, string code = "aa", string text = "甲乙") =>
            new() { Id = id, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Mode = "review", Code = code, Text = text };

        private static void ReviewJournal(string folder)
        {
            string path = Path.Combine(folder, "learning.log");
            var store = new SentenceLearningStore(path);
            store.Confirm(new[] { ReviewEvent("first") });
            long parsed = store.ParsedCharacters, bytes = new FileInfo(path).Length;
            store.Confirm(new[] { ReviewEvent("second", "bb", "乙国") });
            Review(store.ParsedCharacters - parsed == new FileInfo(path).Length - bytes, "append parses only added bytes");
            var entries = store.Entries(); entries[0].Text = "corrupted caller copy";
            Review(store.Entries()[0].Text == "甲乙", "caller cannot mutate cached journal");
            var snapshot = store.Snapshot;
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            bool writeFailed = false;
            try { store.Confirm(new[] { ReviewEvent("retry") }); }
            catch (UnauthorizedAccessException) { writeFailed = true; }
            finally { File.SetAttributes(path, attributes); }
            Review(writeFailed && ReferenceEquals(snapshot, store.Snapshot), "failed append does not publish score");
            store.Confirm(new[] { ReviewEvent("retry") });
            Review(store.Entries().Any(e => e.Id == "retry"), "failed append does not consume event ID");
            File.AppendAllText(path, "TCL1\tE\tbroken-tail", Encoding.ASCII);
            store.Confirm(new[] { ReviewEvent("after-tear") });
            Review(store.Entries().Length == 4, "torn tail recovered");
            store.Clear();
            File.AppendAllText(path, ReviewSeal("TCL1\tU\tunknown-undo\t0\tfuture-event"), Encoding.ASCII);
            var many = Enumerable.Range(0, 10005).Select(i => ReviewEvent("bulk-" + i, "aa" + i)).ToList();
            many.Add(ReviewEvent("future-event"));
            store.Confirm(many);
            Review(store.Entries().Length == 10000 && store.Entries()[0].Id == "bulk-5" &&
                store.Entries()[^1].Id == "bulk-10004", "tombstones applied before combined window limit");
            Review(store.UndoLast() && store.Entries()[0].Id == "bulk-4", "undo restores record that fell outside window");
            store.Clear();
            var invalid = ReviewEvent("bad-utf16"); invalid.Mode = "\ud800";
            store.Confirm(new[] { invalid });
            Review(store.Entries().Length == 0 && store.Snapshot.IsEmpty, "invalid UTF-16 never enters fast append snapshot");
            var original = ReviewEvent("queued", "aa", "甲乙");
            using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Review(store.ConfirmAsync(new[] { original }), "confirmation queued");
                original.Text = "乙国";
            }
            Review(store.FlushAsync().Wait(TimeSpan.FromSeconds(5)), "confirmation drained");
            Review(store.Entries().Single().Text == "甲乙", "queued events detached from caller");
            string smallPath = Path.Combine(folder, "same-size.log");
            var small = new SentenceLearningStore(smallPath);
            small.Confirm(new[] { ReviewEvent("stable") });
            var stamp = File.GetLastWriteTimeUtc(smallPath);
            string line = File.ReadAllText(smallPath, Encoding.ASCII).TrimEnd('\n');
            line = line[..line.LastIndexOf('\t')].Replace("75324e59", "4e594e2d", StringComparison.Ordinal);
            File.WriteAllText(smallPath, ReviewSeal(line), Encoding.ASCII);
            File.SetLastWriteTimeUtc(smallPath, stamp);
            small.Confirm(new[] { ReviewEvent("next", "bb") });
            Review(small.Entries()[0].Text == "乙中" && small.Snapshot.Score("review", "aa", "甲乙", "") == 0,
                "actual journal content verified despite reused metadata");
            var lastRead = typeof(SentenceLearningStore).GetField("_lastRead", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            lastRead.SetValue(small, DateTime.UtcNow.AddMinutes(5));
            small.Refresh();
            Review((DateTime)lastRead.GetValue(small) <= DateTime.UtcNow, "clock rollback does not suppress required journal refresh");
        }
    }
}
