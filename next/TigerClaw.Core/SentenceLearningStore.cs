using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TigerClaw.Core
{
    // ASCII journal with UTF-16 hex fields and CRC32, compatible with Tigirl V1.
    // Product basenames differ deliberately: no implicit cross-product writes.
    internal sealed class SentenceLearningStore
    {
        internal const int MaximumBytes = 16 * 1024 * 1024, MaximumEvents = 10000;
        private readonly object _gate = new();
        private readonly string _path;
        private SentenceLearningSnapshot _snapshot = SentenceLearningSnapshot.Empty;
        private Task _writes = Task.CompletedTask;
        private int _queued;
        private long _lastRefreshTick = long.MinValue / 2;
        private long _size = -1;
        private DateTime _stamp;
        internal string LastError { get; private set; }
        internal string Path => _path;
        internal SentenceLearningSnapshot Snapshot => Volatile.Read(ref _snapshot);
        internal SentenceLearningStore(string path) { _path = path; }

        private bool Queue(Action action)
        {
            lock (_gate)
            {
                if (_queued >= 256) { LastError = "Learning write queue full"; return false; }
                _queued++;
                _writes = _writes.ContinueWith(_ =>
                {
                    try { action(); LastError = null; }
                    catch (Exception e) { LastError = e.Message; }
                    finally { lock (_gate) _queued--; }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return true;
            }
        }
        internal bool ConfirmAsync(SentenceLearningEvent[] events) => events.Length == 0 || Queue(() => Confirm(events));
        internal void RefreshAsync()
        {
            lock (_gate)
            {
                long tick = Environment.TickCount64;
                if (tick - _lastRefreshTick < 1000) return;
                _lastRefreshTick = tick; Queue(Refresh);
            }
        }
        // Worker/test/clean shutdown only; never called by a key handler.
        internal Task FlushAsync() { lock (_gate) return _writes; }
        private FileStream Acquire()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
            long deadline = Environment.TickCount64 + 10000;
            for (;;)
            {
                try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(10); }
            }
        }
        private string ReadBytes()
        {
            if (!File.Exists(_path)) return "";
            if (new FileInfo(_path).Length > MaximumBytes) throw new IOException("Learning journal exceeds 16 MiB");
            return File.ReadAllText(_path, Encoding.ASCII);
        }
        private static string Hex(string text)
        {
            var b = new StringBuilder(text.Length * 4);
            foreach (char c in text) b.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            return b.ToString();
        }
        private static bool Unhex(string text, out string value)
        {
            value = ""; if (text.Length % 4 != 0 || text.Length > 2048) return false;
            var b = new StringBuilder(text.Length / 4);
            for (int i = 0; i < text.Length; i += 4)
            {
                if (!ushort.TryParse(text.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var c)) return false;
                b.Append((char)c);
            }
            value = b.ToString(); return value.Length == 0 || SentenceLearning.Characters(value) > 0;
        }
        private static uint Checksum(string s)
        {
            uint crc = 0xffffffff;
            foreach (char c in s) { crc ^= (byte)c; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320 : 0); }
            return ~crc;
        }
        private static string Seal(string s) => s + "\t" + Checksum(s).ToString(CultureInfo.InvariantCulture) + "\n";
        private sealed class Journal
        {
            internal List<SentenceLearningEvent> Events = new();
            internal HashSet<string> Seen = new(StringComparer.Ordinal);
        }
        private static Journal Parse(string data)
        {
            var journal = new Journal(); var removed = new HashSet<string>(StringComparer.Ordinal);
            int start = 0;
            for (;;)
            {
                int end = data.IndexOf('\n', start); if (end < 0) break;
                string line = data.Substring(start, end - start); start = end + 1;
                if (line.Length > 8192) continue;
                int crc = line.LastIndexOf('\t');
                if (crc < 0 || !uint.TryParse(line.AsSpan(crc + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint expected) ||
                    expected != Checksum(line.Substring(0, crc))) continue;
                string[] f = line.Substring(0, crc).Split('\t');
                if (f.Length < 4 || f[0] != "TCL1" || f[2].Length == 0 || f[2].Length > 128 || journal.Seen.Contains(f[2]) ||
                    !long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out long time)) continue;
                if (f[1] == "E" && f.Length == 8)
                {
                    if (!Unhex(f[4], out string mode) || !Unhex(f[5], out string code) || !Unhex(f[6], out string text) || !Unhex(f[7], out string context) ||
                        mode.Length == 0 || code.Length == 0 || code.Length > 128 || !SentenceLearning.StaticText(text) || SentenceLearning.Characters(context) > 2) continue;
                    journal.Seen.Add(f[2]); journal.Events.Add(new SentenceLearningEvent { Id = f[2], Time = time, Mode = mode, Code = code, Text = text, Context = context });
                }
                else if (f[1] == "U" && f.Length == 5 && f[4].Length <= 128) { journal.Seen.Add(f[2]); removed.Add(f[4]); }
                else if (f[1] == "C" && f.Length == 4) { journal.Seen.Add(f[2]); journal.Events.Clear(); removed.Clear(); }
            }
            journal.Events.RemoveAll(e => removed.Contains(e.Id));
            if (journal.Events.Count > MaximumEvents) journal.Events.RemoveRange(0, journal.Events.Count - MaximumEvents);
            return journal;
        }
        private void Publish(List<SentenceLearningEvent> events)
        {
            Volatile.Write(ref _snapshot, SentenceLearningSnapshot.Build(events));
            var info = new FileInfo(_path); _size = info.Exists ? info.Length : 0; _stamp = info.Exists ? info.LastWriteTimeUtc : default;
        }
        internal void Refresh()
        {
            if (!File.Exists(_path)) { Publish(new()); return; }
            var info = new FileInfo(_path);
            // Refresh decay even when the file is unchanged (one-minute bound).
            if (info.Length == _size && info.LastWriteTimeUtc == _stamp && DateTime.UtcNow - _lastRead < TimeSpan.FromMinutes(1)) return;
            using var guard = Acquire(); Publish(Parse(ReadBytes()).Events); _lastRead = DateTime.UtcNow;
        }
        private DateTime _lastRead;
        private void Append(string data, string addition)
        {
            if (data.Length > 0 && data[^1] != '\n')
            {
                int end = data.LastIndexOf('\n'); data = end < 0 ? "" : data.Substring(0, end + 1);
                using var repair = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read);
                repair.SetLength(data.Length);
            }
            if (data.Length + addition.Length > MaximumBytes) throw new IOException("Learning journal full; normal input remains available");
            using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(addition); stream.Write(bytes); stream.Flush(true);
            }
            Publish(Parse(data + addition).Events);
        }
        internal void Confirm(IEnumerable<SentenceLearningEvent> events)
        {
            using var guard = Acquire(); string data = ReadBytes(); var journal = Parse(data); var addition = new StringBuilder();
            foreach (var e in events)
            {
                if (e.Id.Length == 0 || e.Id.Length > 128 || e.Id.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0 || journal.Seen.Contains(e.Id) ||
                    e.Time < 0 || e.Mode.Length == 0 || e.Mode.Length > 512 || e.Code.Length == 0 || e.Code.Length > 128 || !SentenceLearning.StaticText(e.Text) ||
                    (e.Context.Length > 0 && SentenceLearning.Characters(e.Context) == 0) || SentenceLearning.Characters(e.Context) > 2) continue;
                journal.Seen.Add(e.Id);
                addition.Append(Seal("TCL1\tE\t" + e.Id + "\t" + e.Time.ToString(CultureInfo.InvariantCulture) + "\t" + Hex(e.Mode) + "\t" + Hex(e.Code) + "\t" + Hex(e.Text) + "\t" + Hex(e.Context)));
            }
            if (addition.Length == 0) Publish(journal.Events); else Append(data, addition.ToString());
        }
        internal SentenceLearningEvent[] Entries() { if (!File.Exists(_path)) return Array.Empty<SentenceLearningEvent>(); using var guard = Acquire(); return Parse(ReadBytes()).Events.ToArray(); }
        internal bool UndoLast()
        {
            if (!File.Exists(_path)) return false;
            using var guard = Acquire(); string data = ReadBytes(); var entries = Parse(data).Events; if (entries.Count == 0) return false;
            Append(data, Seal("TCL1\tU\t" + Guid.NewGuid().ToString("N") + "\t" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\t" + entries[^1].Id)); return true;
        }
        internal void Clear()
        {
            using var guard = Acquire(); string data = ReadBytes();
            Append(data, Seal("TCL1\tC\t" + Guid.NewGuid().ToString("N") + "\t" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
    }
}
