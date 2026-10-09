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
    // Readable UTF-8 correction journal. New format only; old files are not loaded.
    // Product basenames differ deliberately: no implicit cross-product writes.
    internal sealed partial class SentenceLearningStore
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
        internal bool ConfirmAsync(SentenceLearningEvent[] events)
        {
            if (events.Length == 0) return true;
            var copy = events.Select(e => e.Copy()).ToArray();
            return Queue(() => Confirm(copy));
        }
        internal bool ForgetAsync(string mode, string code, string text)
        {
            return Queue(() =>
            {
                lock (_ioGate)
                {
                    using var guard = Acquire(); string data = ReadBytes();
                    var entries = ReadJournal(data).VisibleEvents;
                    var addition = new StringBuilder();
                    foreach (var entry in entries.Where(e => SentenceLearning.EffectiveMode(e.Mode, e.Text) == SentenceLearning.EffectiveMode(mode, text) && e.Code == code && e.Text == text))
                        addition.Append(Row("撤销", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), target: entry.Id));
                    if (addition.Length != 0) Append(data, addition.ToString());
                    else Publish(entries);
                }
            });
        }
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
            return File.ReadAllText(_path, new UTF8Encoding(false, true));
        }
        private const string Header = "# 虎整句自学习记录（每条学习行代表一次人工纠正；等级上限10）\n" +
            "# 操作\t时间(UTC)\t片段\t编码\t前文\t本次升级\t模式\t记录编号\t撤销目标\n";
        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        private static string Unescape(string s)
        {
            var b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { b.Append(s[i]); continue; }
                if (++i == s.Length) throw new IOException("自学习文件有未完成的转义");
                b.Append(s[i] switch { 't' => '\t', 'r' => '\r', 'n' => '\n', '\\' => '\\', _ => throw new IOException("自学习文件有未知转义") });
            }
            return b.ToString();
        }
        private static string Row(string action, string id, long time, string text = "", string code = "", string context = "", int levels = 0, string mode = "", string target = "") =>
            string.Join("\t", new[] { action, DateTimeOffset.FromUnixTimeSeconds(time).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Escape(text), Escape(code), Escape(context), levels.ToString(CultureInfo.InvariantCulture), Escape(mode), id, target }) + "\n";
        private sealed class Journal
        {
            internal List<SentenceLearningEvent> Events = new();
            internal HashSet<string> Seen = new(StringComparer.Ordinal);
            internal HashSet<string> Removed = new(StringComparer.Ordinal);
            internal SentenceLearningEvent[] Visible;
            internal SentenceLearningEvent[] VisibleEvents => Visible ??=
                Events.Where(e => !Removed.Contains(e.Id)).TakeLast(MaximumEvents).ToArray();
        }
        private static Journal Parse(string data, Journal journal = null)
        {
            journal ??= new Journal(); journal.Visible = null;
            foreach (string source in data.TrimStart('\ufeff').Split('\n'))
            {
                string line = source.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.Length > 8192) throw new IOException("自学习文件行过长");
                string[] f = line.Split('\t');
                if (f.Length != 9 || f[7].Length == 0 || f[7].Length > 128 ||
                    !DateTimeOffset.TryParseExact(f[1], "yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) throw new IOException("自学习文件格式错误");
                long time = date.ToUnixTimeSeconds();
                if (time < 0) throw new IOException("自学习时间无效");
                if (journal.Seen.Contains(f[7])) continue;
                if (f[0] == "学习")
                {
                    string text = Unescape(f[2]), code = Unescape(f[3]), context = Unescape(f[4]), mode = Unescape(f[6]);
                    if (!int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out int levels) || levels < 1 || levels > 3 ||
                        mode.Length == 0 || mode.Length > 512 || SentenceLearning.Characters(mode) == 0 || code.Length == 0 || code.Length > 128 || SentenceLearning.Characters(code) == 0 || !SentenceLearning.StaticText(text) ||
                        (context.Length > 0 && SentenceLearning.Characters(context) == 0) || SentenceLearning.Characters(context) > 2 || f[8].Length != 0)
                        throw new IOException("自学习片段、编码、前文或升级值无效");
                    journal.Seen.Add(f[7]);
                    if (!SentenceLearning.LegacyPairMode(mode))
                        journal.Events.Add(new SentenceLearningEvent { Id = f[7], Time = time, Mode = mode, Code = code, Text = text, Context = context, Levels = levels });
                }
                else if (f[0] == "撤销" && f[8].Length > 0 && f[8].Length <= 128)
                { journal.Seen.Add(f[7]); journal.Removed.Add(f[8]); }
                else if (f[0] == "清空" && f[8].Length == 0)
                { journal.Seen.Add(f[7]); journal.Events.Clear(); journal.Removed.Clear(); }
                else throw new IOException("未知的自学习操作");
            }
            return journal;
        }
        private void Publish(IReadOnlyList<SentenceLearningEvent> events)
        {
            Volatile.Write(ref _snapshot, _accumulator.Update(events, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            _lastRead = DateTime.UtcNow;
            var info = new FileInfo(_path); _size = info.Exists ? info.Length : 0; _stamp = info.Exists ? info.LastWriteTimeUtc : default;
        }
        private void RefreshCore()
        {
            if (!File.Exists(_path)) { Publish(Array.Empty<SentenceLearningEvent>()); return; }
            var info = new FileInfo(_path);
            // Periodically check externally edited data; timestamps never decay scores.
            DateTime now = DateTime.UtcNow;
            if (info.Length == _size && info.LastWriteTimeUtc == _stamp && now >= _lastRead && now - _lastRead < TimeSpan.FromMinutes(1)) return;
            using var guard = Acquire(); Publish(ReadJournal(ReadBytes()).VisibleEvents); _lastRead = DateTime.UtcNow;
        }
        private DateTime _lastRead;
        private void Append(string data, string addition)
        {
            // Validate before writing; never truncate a human-edited file.
            ReadJournal(data);
            if (data.Length == 0) addition = Header + addition;
            else if (data[^1] != '\n') addition = "\n" + addition;
            if (Encoding.UTF8.GetByteCount(data) + Encoding.UTF8.GetByteCount(addition) > MaximumBytes) throw new IOException("Learning journal full; normal input remains available");
            using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(addition); stream.Write(bytes); stream.Flush(true);
            }
            var journal = ReadJournal(data);
            _parsedData = null; _parsedJournal = null; // No half-updated cache survives failure.
            Parse(addition, journal);
            _parsedCharacters += addition.Length;
            RememberJournal(data, addition, journal);
            Publish(journal.VisibleEvents);
        }
        private void ConfirmCore(IEnumerable<SentenceLearningEvent> events)
        {
            using var guard = Acquire(); string data = ReadBytes(); var journal = ReadJournal(data); var accepted = new HashSet<string>(StringComparer.Ordinal); var addition = new StringBuilder();
            foreach (var e in events)
            {
                if (SentenceLearning.LegacyPairMode(e.Mode) || e.Id.Length == 0 || e.Id.Length > 128 || e.Id.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0 || journal.Seen.Contains(e.Id) || accepted.Contains(e.Id) ||
                    e.Time < 0 || e.Mode.Length == 0 || e.Mode.Length > 512 || SentenceLearning.Characters(e.Mode) == 0 || e.Code.Length == 0 || e.Code.Length > 128 || SentenceLearning.Characters(e.Code) == 0 || !SentenceLearning.StaticText(e.Text) ||
                    (e.Context.Length > 0 && SentenceLearning.Characters(e.Context) == 0) || SentenceLearning.Characters(e.Context) > 2 || e.Levels < 1 || e.Levels > 3) continue;
                accepted.Add(e.Id);
                addition.Append(Row("学习", e.Id, e.Time, e.Text, e.Code, e.Context, e.Levels, e.Mode));
            }
            if (addition.Length == 0) Publish(journal.VisibleEvents); else Append(data, addition.ToString());
        }
        private SentenceLearningEvent[] EntriesCore() { if (!File.Exists(_path)) return Array.Empty<SentenceLearningEvent>(); using var guard = Acquire(); return ReadJournal(ReadBytes()).VisibleEvents.Select(e => e.Copy()).ToArray(); }
        private bool UndoLastCore()
        {
            if (!File.Exists(_path)) return false;
            using var guard = Acquire(); string data = ReadBytes(); var entries = ReadJournal(data).VisibleEvents; if (entries.Length == 0) return false;
            Append(data, Row("撤销", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), target: entries[^1].Id)); return true;
        }
        private void ClearCore()
        {
            using var guard = Acquire(); string data = ReadBytes();
            Append(data, Row("清空", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
    }
}
