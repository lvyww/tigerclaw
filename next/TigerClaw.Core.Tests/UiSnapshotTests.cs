using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunUiPublisherProbe(string session)
        {
            if (string.IsNullOrEmpty(session) || session.Length > 64 ||
                System.Linq.Enumerable.Any(session, c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException("Invalid isolated publisher session");
            using var publisher = new UiStatePublisher(@"Local\TigerClaw.Overlay.Test." + session + ".Ui");
            Console.WriteLine("READY");
            string line;
            long sequence = 0;
            while ((line = Console.ReadLine()) != null)
            {
                var state = JsonSerializer.Deserialize<OverlayUiState>(line);
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                publisher.Publish(state);
                long done = System.Diagnostics.Stopwatch.GetTimestamp();
                Console.WriteLine((++sequence) + " " + start + " " + done);
            }
            return 0;
        }

        private static void RunUiSnapshotTests()
        {
            string name = @"Local\TigerClaw.UiSnapshot.Test." + Guid.NewGuid().ToString("N");
            using var publisher = new UiStatePublisher(name);
            using var legacy = MemoryMappedFile.OpenExisting(name);
            using var oldView = legacy.CreateViewAccessor();
            using var snapshot = MemoryMappedFile.OpenExisting(name + ".Snapshot.v2");
            using var view = snapshot.CreateViewAccessor();
            using var gate = Mutex.OpenExisting(name + ".Snapshot.v2.Lock");
            using var changed = EventWaitHandle.OpenExisting(name + ".Snapshot.v2.Changed");
            void Check(bool ok, string reason)
            {
                if (!ok) throw new Exception("UI snapshot: " + reason);
            }
            publisher.Publish(new OverlayUiState { InputCode = "first" });
            Check(changed.WaitOne(1000), "publication notification");
            Check(view.ReadInt64(0) == 1 && oldView.ReadInt64(0) == 1, "legacy and snapshot sequence");
            Check(view.ReadInt64(8) == oldView.ReadInt64(8), "same publication timestamp");
            byte[] bytes = new byte[view.ReadInt32(16)];
            view.ReadArray(20, bytes, 0, bytes.Length);
            using (var json = JsonDocument.Parse(bytes))
                Check(json.RootElement.GetProperty("InputCode").GetString() == "first", "complete JSON payload");

            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                gate.WaitOne(); held.Set(); release.Wait(); gate.ReleaseMutex();
            });
            holder.Start();
            try
            {
                Check(held.Wait(2000), "test reader holds gate");
                var started = System.Diagnostics.Stopwatch.StartNew();
                publisher.Publish(new OverlayUiState { InputCode = "fallback" });
                Check(started.ElapsedMilliseconds < 1000, "Core does not wait for suspended reader");
                Check(oldView.ReadInt64(0) == 2 && view.ReadInt64(0) == 1, "contended publication preserves v1 fallback");
                Check(changed.WaitOne(1000), "fallback wakes reader too");
            }
            finally { release.Set(); holder.Join(); }

            publisher.Publish(new OverlayUiState { InputCode = "recovered" });
            Check(view.ReadInt64(0) == 3, "next publication recovers fast channel");
            publisher.Publish(new OverlayUiState { InputCode = new string('x', 128 * 1024) });
            Check(view.ReadInt64(0) == 3 && oldView.ReadInt64(0) == 3, "oversized JSON not truncated or published");
            // Actual C# publisher under concurrent, gate-protected observations.
            Exception failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 2000; ++i)
                    {
                        string text = i.ToString() + new string('a', i % 512);
                        publisher.Publish(new OverlayUiState { InputCode = text, Candidates = new[] { text } });
                    }
                }
                catch (Exception e) { failure = e; }
            });
            writer.Start();
            try
            {
                while (writer.IsAlive)
                {
                    if (!gate.WaitOne(0)) { Thread.Yield(); continue; }
                    try
                    {
                        bytes = new byte[view.ReadInt32(16)];
                        view.ReadArray(20, bytes, 0, bytes.Length);
                    }
                    finally { gate.ReleaseMutex(); }
                    using var json = JsonDocument.Parse(bytes);
                    var element = json.RootElement;
                    if (element.GetProperty("InputCode").GetString() != "recovered")
                        Check(element.GetProperty("InputCode").GetString() == element.GetProperty("Candidates")[0].GetString(), "concurrent snapshot fields agree");
                }
            }
            finally { writer.Join(); }
            if (failure != null) throw failure;
            Console.WriteLine("UI snapshot publication/concurrency tests passed.");
        }
    }
}
