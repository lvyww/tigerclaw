using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using TigerClaw.Core;
using TigerClaw.Shared;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string why)
    {
        ++checks;
        if (!condition) throw new Exception(why);
    }
    private static object Field(object owner, string name) => owner.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

    private sealed class Fixture : IDisposable
    {
        public readonly CoreRuntimeState State;
        private readonly string root = Path.Combine(Path.GetTempPath(), "tigerclaw-frame-" + Guid.NewGuid().ToString("N"));
        public readonly ProtocolHandler Handler;
        public readonly InputMethodEngine Engine;
        public readonly object EngineGate, PublishGate;
        private readonly UiStatePublisher publisher;
        private readonly MemoryMappedFile map;
        private readonly MemoryMappedViewAccessor view;
        private readonly Mutex gate;
        public Fixture()
        {
            Directory.CreateDirectory(root);
            State = new CoreRuntimeState(root);
            State.TrySetConfigValue("自动启用整句模式", "是", out _, out _);
            State.TrySetConfigValue("当前码表", "虎整句", out _, out _);
            State.TrySetConfigValue("整句自动提前上屏", "是", out _, out _);
            State.TrySetConfigValue("按键音", "否", out _, out _);
            var decoder = new SentenceInputDecoder(SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
            {
                ["vu"] = new List<string> { "这" },
                ["jw"] = new List<string> { "人" }
            }), NeutralSentenceLanguageModel.Instance, beamWidth: 100);
            string name = @"Local\TigerClaw.CandidateFrame.Test." + Guid.NewGuid().ToString("N");
            publisher = new UiStatePublisher(name);
            map = MemoryMappedFile.OpenExisting(name + ".Snapshot.v2");
            view = map.CreateViewAccessor();
            gate = Mutex.OpenExisting(name + ".Snapshot.v2.Lock");
            Handler = new ProtocolHandler(_ => { }, State, publisher, decoder, false);
            Engine = (InputMethodEngine)Field(Handler, "_engine");
            EngineGate = Field(Engine, "_lock");
            PublishGate = Field(Handler, "_publishLock");
            Handler.Handle("{\"type\":\"ime_active\",\"active\":true}");
            Handler.Handle("{\"type\":\"focus\",\"hwnd\":1,\"processId\":1}");
        }
        public string Key(char key, bool caret = true) => Handler.Handle(
            "{\"type\":\"key\",\"action\":\"down\",\"vk\":" + (int)char.ToUpperInvariant(key) +
            (caret ? ",\"caret_x\":200,\"caret_y\":500,\"height\":20" : "") + "}");
        public void Idle()
        {
            Check(SpinWait.SpinUntil(() => !Engine.IsSentenceDecodePending, 5000), "Decode did not finish");
        }
        public OverlayUiState Read()
        {
            // Synchronous publication avoids waiting on/depending on callback scheduling.
            Handler.Handle("{\"type\":\"hello\"}");
            Check(gate.WaitOne(2000), "Isolated snapshot gate unavailable");
            byte[] bytes;
            try
            {
                int length = view.ReadInt32(16);
                Check(length > 0 && length < 128 * 1024 - 20, "Invalid snapshot length");
                bytes = new byte[length];view.ReadArray(20, bytes, 0, length);
            }
            finally { gate.ReleaseMutex(); }
            return JsonSerializer.Deserialize<OverlayUiState>(bytes);
        }
        public void Ready()
        {
            Key('v');Idle();Key('u');Idle();
            var state = Read();
            Check(state.CandidateVisible && state.Candidates.Length == 1 && state.Candidates[0] == "这",
                "Real engine prefix fixture did not produce vu -> 这");
            Check(!state.CandidateHoldWhilePending, "Ready candidates requested a hold");
        }
        public OverlayUiState CommitPrefix()
        {
            // Caller owns the real engine Monitor. ProcessKey reenters it, but
            // Task.Run's decoder cannot acquire it until the assertions finish.
            // No candidate/result arrays or private pending fields are fabricated.
            Check(Monitor.IsEntered(EngineGate), "Pending fixture requires engine gate");
            string response = Key('j');
            using (var json = JsonDocument.Parse(response))
                Check(json.RootElement.GetProperty("commit_text").GetString() == "这", "Real key did not commit the prefix");
            var raw = Engine.GetDifferentialSnapshot(5);
            Check(raw.SentenceCommittedText == "这" && raw.Ui.ActiveInputCode == "j",
                "Auto-commit did not retain the live suffix j");
            var captured = Engine.GetUiSnapshot(5, out bool pending);
            Check(pending && captured.IsComposing && captured.Candidates.Length == 0,
                "Real engine did not reach empty pending suffix");
            var state = Read();
            Check(!state.CandidateVisible && state.CandidateHoldWhilePending && state.Candidates.Length == 0,
                "Pending suffix was not marked for frame retention");
            return state;
        }
        public void Dispose()
        {
            Idle();Handler.Dispose();gate.Dispose();view.Dispose();map.Dispose();publisher.Dispose();
            try { Directory.Delete(root, true); }
            catch (IOException e) { Console.Error.WriteLine("Temporary config cleanup: " + e.Message); }
            catch (UnauthorizedAccessException e) { Console.Error.WriteLine("Temporary config cleanup: " + e.Message); }
        }
    }
    private static int Main(string[] args)
    {
        try
        {
            Check(args.Length == 1, "Provide a new isolated JSONL trace path");
            Check(!File.Exists(args[0]), "Refusing to overwrite an existing trace");
            using var trace = new StreamWriter(args[0], false, new UTF8Encoding(false));
            void Record(string phase, OverlayUiState state) => trace.WriteLine(JsonSerializer.Serialize(new { phase, state }));
            using (var f = new Fixture())
            {
                lock (f.PublishGate) lock (f.EngineGate)
                {
                    f.Key('v');var first = f.Read();
                    Check(!first.CandidateVisible && first.Candidates.Length == 0, "Initial pending result became visible");
                    Record("initial_pending", first);
                }
                f.Idle();f.Key('u');f.Idle();
                var ready = f.Read();Check(ready.Candidates.Length == 1 && ready.Candidates[0] == "这", "Prefix missing");
                Check(!string.IsNullOrEmpty(ready.CandidateFrameSession), "Ready frame missing session token");
                Check(f.Read().CandidateFrameSession == ready.CandidateFrameSession, "Snapshot polling renewed session");
                Record("ready", ready);
                lock (f.PublishGate) lock (f.EngineGate)
                {
                    var pending = f.CommitPrefix();
                    Check(pending.CandidateFrameSession == ready.CandidateFrameSession,
                        "Automatic prefix commit broke display continuity");
                    Record("pending", pending);
                }
                f.Idle();var empty = f.Read();
                Check(!empty.CandidateHoldWhilePending && empty.CandidateVisible && empty.Candidates.Length == 0,
                    "Completed empty result still requested a hold");
                Record("completed_empty", empty);
                f.Key('w');f.Idle();var completed = f.Read();
                Check(completed.Candidates.Length > 0 && completed.Candidates[0] == "人" && !completed.CandidateHoldWhilePending,
                    "Suffix completion lost/duplicated the committed prefix");
                Check(empty.CandidateFrameSession == ready.CandidateFrameSession &&
                      completed.CandidateFrameSession == ready.CandidateFrameSession,
                    "Decode completion renewed the display session");
                Record("completed_candidates", completed);
            }
            // A real end/new-input sequence, with precisely the same input and
            // geometry. Native tests intentionally never consume the hidden state.
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (string ending in new[] { "commit", "cancel", "escape", "backspace" })
            {
                using var f = new Fixture();f.Ready();var before = f.Read();
                Check(tokens.Add(before.CandidateFrameSession), "Different engines reused a display token");
                Record("previous_" + ending, before);
                string nextSession;
                lock (f.PublishGate) lock (f.EngineGate)
                {
                    switch (ending)
                    {
                        case "commit": f.Key(' '); break;
                        case "cancel": f.Handler.Handle("{\"type\":\"composition_canceled\"}"); break;
                        case "escape": f.Key((char)27); break;
                        default: f.Key((char)8);f.Key((char)8);break;
                    }
                    var hidden = f.Read();
                    Check(!hidden.CandidateVisible && !hidden.CandidateHoldWhilePending,
                        "Input did not end: " + ending);
                    // Only the final MMF snapshot will be delivered to Native.
                    f.Key('v');f.Key('u');var next = f.Read();nextSession = next.CandidateFrameSession;
                    Check(next.CandidateHoldWhilePending && !next.CandidateVisible && next.Candidates.Length == 0,
                        "New input was not pending: " + ending);
                    Check(!string.IsNullOrEmpty(nextSession) && nextSession != before.CandidateFrameSession,
                        "New input reused the old display session: " + ending);
                    Check(next.InputCode == before.InputCode && next.CaretX == before.CaretX && next.CaretY == before.CaretY,
                        "Coalesced regression must keep identical input and coordinates");
                    Record("new_" + ending + "_pending", next);
                }
                f.Idle();var ready = f.Read();
                Check(ready.CandidateVisible && ready.CandidateFrameSession == nextSession, "New input completion changed session");
                Record("new_" + ending + "_ready", ready);
                lock (f.PublishGate) lock (f.EngineGate)
                {
                    var next = f.CommitPrefix();
                    Check(next.CandidateFrameSession == nextSession, "New input continuation changed session");
                    Record("new_" + ending + "_continuation", next);
                }
            }
            foreach (string action in new[] { "cancel", "focus", "inactive", "english", "disabled" })
            {
                using var f = new Fixture();f.Ready();
                lock (f.PublishGate) lock (f.EngineGate)
                {
                    var pending = f.CommitPrefix();
                    string message = action switch
                    {
                        "cancel" => "{\"type\":\"composition_canceled\"}",
                        "focus" => "{\"type\":\"focus\",\"hwnd\":2,\"processId\":1}",
                        "inactive" => "{\"type\":\"ime_active\",\"active\":false}",
                        "english" => "{\"type\":\"ctrl_space\"}",
                        _ => "{\"type\":\"hook_native_disabled\",\"disabled\":true}"
                    };
                    f.Handler.Handle(message);var ended = f.Read();
                    Check(!ended.CandidateHoldWhilePending && !ended.CandidateVisible,
                        "Pending hold bypassed " + action);
                    Check(ended.CandidateFrameSession != pending.CandidateFrameSession,
                        "Boundary retained frame identity: " + action);
                    Record(action, ended);
                }
            }
            using (var f = new Fixture())
            {
                lock (f.PublishGate) lock (f.EngineGate)
                {
                    f.Key('v', false);
                    // Freeze the existing 30-ms gate, not the decoder or production clock.
                    typeof(ProtocolHandler).GetField("_awaitingFreshCaretDeadlineTick", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(f.Handler, long.MaxValue);
                    var first = f.Read();
                    Check(!first.CandidateVisible && !first.CandidateHoldWhilePending, "Hold bypassed fresh-caret gate");
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { probe = "candidate_frame_core", status = "passed", checks,
                real_engine_auto_commit = true, real_async_worker = true, real_isolated_mmf = true,
                controlled_engine_lock = true, physical_input_tested = false }));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e);return 1; }
    }
}
