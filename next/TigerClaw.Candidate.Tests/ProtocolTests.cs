#if CANDIDATE_PROTOCOL_TESTS
using System;
using System.IO;
using TigerClaw.Core;

internal static class ProtocolTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "TigerClaw.Candidate.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new CoreRuntimeState(root); state.Initialize();
            using (var handler = new ProtocolHandler(_ => { }, state, null))
            {
                handler.Handle("{\"type\":\"ime_active\",\"active\":true}");
                handler.Handle("{\"type\":\"focus\",\"hwnd\":1234,\"processId\":5,\"processName\":\"host\",\"className\":\"edit\",\"windowTitle\":\"chat\"}");
                long epoch = handler.CandidateEnvironmentRevision;
                handler.Handle("{\"type\":\"focus\",\"hwnd\":1234,\"processId\":5,\"processName\":\"host\",\"className\":\"edit\",\"windowTitle\":\"chat\"}");
                handler.Handle("{\"type\":\"caret\",\"x\":100,\"y\":1000,\"width\":2,\"height\":20}");
                handler.Handle("{\"type\":\"composition_canceled\"}");
                Program.Require(handler.CandidateEnvironmentRevision == epoch, "caret/cancel/duplicate focus changed epoch");
                handler.Handle("{\"type\":\"focus\",\"hwnd\":1234,\"processId\":5,\"candidate_environment_changed\":true}");
                Program.Require(handler.CandidateEnvironmentRevision > epoch, "same-HWND TSF context hint ignored");
                state.GetFocus(out _, out _, out string name, out string className, out string title);
                Program.Require(name == "host" && className == "edit" && title == "chat", "context hint destroyed focus metadata");
                epoch = handler.CandidateEnvironmentRevision;
                handler.Handle("{\"type\":\"ime_active\",\"active\":false}");
                handler.Handle("{\"type\":\"ime_active\",\"active\":true}");
                Program.Require(handler.CandidateEnvironmentRevision > epoch, "rapid inactive/active transition lost");
                epoch = handler.CandidateEnvironmentRevision;
                handler.Handle("{\"type\":\"focus\",\"hwnd\":4567,\"processId\":5}");
                handler.Handle("{\"type\":\"focus\",\"hwnd\":1234,\"processId\":5,\"processName\":\"host\",\"className\":\"edit\",\"windowTitle\":\"chat\"}");
                Program.Require(handler.CandidateEnvironmentRevision > epoch, "focus away/back transition lost");
                epoch = handler.CandidateEnvironmentRevision;
                handler.Handle("{\"type\":\"hook_native_disabled\",\"disabled\":true}");
                handler.Handle("{\"type\":\"hook_native_disabled\",\"disabled\":false}");
                Program.Require(handler.CandidateEnvironmentRevision > epoch, "native hook disable/re-enable lost");
                epoch = handler.CandidateEnvironmentRevision;
                handler.Handle("{\"type\":\"hello\"}");
                Program.Require(handler.CandidateEnvironmentRevision > epoch, "reconnect did not invalidate");
                // A raw-code composition and normal Enter commit may finish and
                // start a new word without any environment-generation change.
                handler.Handle("{\"type\":\"ime_active\",\"active\":true}");
                epoch = handler.CandidateEnvironmentRevision;
                handler.Handle("{\"type\":\"key\",\"action\":\"down\",\"vk\":65,\"caret_x\":100,\"caret_y\":1000}");
                handler.Handle("{\"type\":\"key\",\"action\":\"up\",\"vk\":65}");
                Program.Require(handler.BuildDifferentialSnapshotJson().Contains("\"is_composing\":true"), "commit fixture never started composing");
                string committed = handler.Handle("{\"type\":\"key\",\"action\":\"down\",\"vk\":13}");
                Program.Require(committed.Contains("\"commit_text\":\"a\"") &&
                    handler.BuildDifferentialSnapshotJson().Contains("\"is_composing\":false"), "normal commit fixture did not commit");
                handler.Handle("{\"type\":\"key\",\"action\":\"up\",\"vk\":13}");
                Program.Require(handler.CandidateEnvironmentRevision == epoch, "ordinary commit changed environment");
            }
            Console.WriteLine("{\"protocol_environment\":\"passed\",\"real_core_handler\":true,\"tsf_input_injected\":false}");
        }
        finally
        {
            // Non-fatal scratch cleanup, not swallowing a real test exception.
            try { Directory.Delete(root, true); }
            catch (IOException e) { Console.Error.WriteLine("Cleanup warning: " + root + ": " + e.Message); }
            catch (UnauthorizedAccessException e) { Console.Error.WriteLine("Cleanup warning: " + root + ": " + e.Message); }
        }
    }
}
#endif
