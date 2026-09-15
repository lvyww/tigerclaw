using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static async Task<object> NativeExchange(SentenceRerankClient client, string type,
            string[] candidates = null, CancellationToken token = default)
        {
            Type requestType = typeof(SentenceRerankClient).GetNestedType("SentencePipeRequest", BindingFlags.NonPublic);
            object request = Activator.CreateInstance(requestType, true);
            requestType.GetProperty("Type").SetValue(request, type);
            requestType.GetProperty("Generation").SetValue(request, 17L);
            requestType.GetProperty("RawCode").SetValue(request, "synthetic-test-code");
            requestType.GetProperty("Candidates").SetValue(request, candidates);
            var method = typeof(SentenceRerankClient).GetMethod("ExchangeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (Task)method.Invoke(client, new[] { request, (object)token, 30000 });
            await task;
            return task.GetType().GetProperty("Result").GetValue(task);
        }
        private static double[] NativeScores(object response) => response == null ? null :
            (double[])response.GetType().GetProperty("Scores").GetValue(response);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

        private static int RunNativeSentenceReview(string executable, string model)
        {
            Review(File.Exists(executable) && File.Exists(model), "explicit native host and GGUF exist");
            string name = "TigerClaw.Sentence.Review." + Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false };
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
            start.ArgumentList.Add("--model"); start.ArgumentList.Add(Path.GetFullPath(model));
            start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var process = Process.Start(start);
            string stateRoot = Path.Combine(Path.GetTempPath(), name);
            try
            {
                // No eligible schema: this client must not preload/adopt any runtime process.
                Directory.CreateDirectory(stateRoot);
                var state = new CoreRuntimeState(stateRoot);
                state.TrySetConfigValue("整句神经重排", "否", out _, out _);
                using var client = new SentenceRerankClient(state, new ProcessLauncher(), null, name);
                Review(NativeExchange(client, "hello").GetAwaiter().GetResult() != null, "isolated host ready");
                string[] shortTexts = { "今天我们测试输入法", "今天我们测试输出法", "今天我们测试输入吧" };
                double[] baseline = NativeScores(NativeExchange(client, "rerank", shortTexts).GetAwaiter().GetResult());
                Review(baseline?.Length == shortTexts.Length, "native baseline response");
                string stem = string.Concat(Enumerable.Repeat("今天我们讨论输入法算法和内存优化。", 12));
                var longTexts = new[] { "方案", "方法", "过程", "效果", "计划" }.Select(end => stem + end).ToArray();
                for (int i = 0; i < 3; i++)
                {
                    using var cancellation = new CancellationTokenSource(40);
                    bool canceled = false;
                    try { NativeExchange(client, "rerank", longTexts, cancellation.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { canceled = true; }
                    Review(canceled, "production client cancels read and closes isolated pipe");
                    Review(NativeExchange(client, "ping").GetAwaiter().GetResult() != null && !process.HasExited,
                        "host remains resident and accepts next connection after cancellation");
                    var recovered = NativeScores(NativeExchange(client, "rerank", shortTexts).GetAwaiter().GetResult());
                    Review(recovered?.Length == baseline.Length, "post-cancel response complete");
                    for (int j = 0; j < baseline.Length; j++) ReviewNumber(baseline[j], recovered[j], "post-cancel exact Qwen score");
                }
                Review(NativeExchange(client, "rerank", new string[] { null }).GetAwaiter().GetResult() == null,
                    "malformed rerank fails without partial scores");
                Review(NativeExchange(client, "ping").GetAwaiter().GetResult() != null, "host survives malformed request");
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(5000);
                    Review(GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) && pid == process.Id,
                        "shutdown targets only this test's owned process");
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    writer.WriteLine("{\"type\":\"shutdown\",\"seq\":99}");
                    using var deadline = new CancellationTokenSource(5000);
                    string reply = reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                    using var json = JsonDocument.Parse(reply);
                    Review(json.RootElement.GetProperty("success").GetBoolean(), "isolated shutdown acknowledged");
                }
                Review(process.WaitForExit(5000) && process.ExitCode == 0, "isolated host exits cleanly");
                Console.WriteLine(JsonSerializer.Serialize(new { test = "native_sentence_transport", status = "passed",
                    canceled_requests = 3, exact_recovery_scores = baseline.Length * 3, actual_qwen = true, production_pipe = false }));
                return 0;
            }
            finally
            {
                // This handle was returned by Process.Start above; never kill/adopt by name.
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
                finally { ReviewCleanupDirectory(stateRoot); }
            }
        }
    }
}
