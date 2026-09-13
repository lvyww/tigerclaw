using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal sealed class SentenceRerankRequest
    {
        public long Generation { get; set; }
        public string RawCode { get; set; }
        public string[] Candidates { get; set; }
    }

    internal interface ISentenceRerankService : IDisposable
    {
        void Request(SentenceRerankRequest request);
    }

    internal sealed partial class SentenceRerankClient : ISentenceRerankService
    {
        private readonly CoreRuntimeState _state;
        private readonly ProcessLauncher _launcher;
        private readonly SentenceServiceLifecycle _lifecycle;
        private long _seq;

        public SentenceRerankClient(CoreRuntimeState state, ProcessLauncher launcher,
            Action<long, string, double[]> resultCallback)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _lifecycle = new SentenceServiceLifecycle(Preload, Release, Score, resultCallback);
            RefreshConfiguration();
        }

        internal void RefreshConfiguration()
        {
            _lifecycle.SetEnabled(IsEligible(_state));
        }

        internal static bool IsEligible(CoreRuntimeState state) =>
            state.IsSentenceInputActive() && state.GetSentenceNeuralRerankEnabled();

        public void Request(SentenceRerankRequest request)
        {
            RefreshConfiguration();
            if (request?.Candidates == null || request.Candidates.Length == 0) return;
            _lifecycle.Request(request);
        }

        public void Dispose()
        {
            _lifecycle.Dispose();
        }

        private bool EnsureStarted(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string modelPath = ResolveModelPath();
            if (!File.Exists(modelPath)) return false;
            string arguments = "--parent-pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
                " --pipe " + QuoteArgument(RuntimeConstants.SentencePipeShortName) +
                " --model " + QuoteArgument(modelPath);
            return _launcher.TryLaunchSentence(arguments);
        }

        private void Preload(CancellationToken token)
        {
            if (EnsureStarted(token))
                Exchange(new SentencePipeRequest { Type = "hello" }, token, 10000);
        }

        private double[] Score(SentenceRerankRequest request, CancellationToken token)
        {
            if (!EnsureStarted(token)) return null;
            var response = Exchange(new SentencePipeRequest
            {
                Type = "rerank", Generation = request.Generation,
                RawCode = request.RawCode ?? string.Empty, Candidates = request.Candidates
            }, token, 10000);
            return response != null && response.Generation == request.Generation &&
                string.Equals(response.RawCode, request.RawCode, StringComparison.Ordinal) &&
                response.Scores?.Length == request.Candidates.Length ? response.Scores : null;
        }

        private void Release()
        {
            // Only the process started by this launcher is eligible for release.
            if (!_launcher.HasOwnedSentence) return;
            try
            {
                Exchange(new SentencePipeRequest { Type = "shutdown" }, CancellationToken.None, 1000);
            }
            catch (Exception error)
            {
                Debug.WriteLine("[Sentence] shutdown: " + error.Message);
            }
            _launcher.StopOwnedSentence();
        }

        private SentencePipeResponse Exchange(SentencePipeRequest request, CancellationToken token, int timeoutMs)
        {
            return ExchangeAsync(request, token, timeoutMs).GetAwaiter().GetResult();
        }

        private async Task<SentencePipeResponse> ExchangeAsync(
            SentencePipeRequest request, CancellationToken token, int timeoutMs)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            using var pipe = new NamedPipeClientStream(".", RuntimeConstants.SentencePipeShortName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            // Preserve the existing scoring response budget after connection.
            if (request.Type == "rerank") deadline.CancelAfter(5000);
            if (request.Type == "shutdown" &&
                (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) ||
                 serverPid != _launcher.OwnedSentenceId))
            {
                throw new IOException("Sentence shutdown pipe is not owned by this launcher");
            }
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                { AutoFlush = true, NewLine = "\n" };
            request.Seq = Interlocked.Increment(ref _seq);
            await writer.WriteLineAsync(Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
            string line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            var response = DeserializeResponse(line);
            return response != null && response.Success && response.Seq == request.Seq ? response : null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

        private static string ResolveModelPath()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string modelPath = Path.Combine(baseDirectory, "Models", "sentence-qwen-q8.gguf");
            string directory = baseDirectory;
            for (int level = 0; level < 6 && !string.IsNullOrEmpty(directory); level++)
            {
                string[] roots =
                {
                    Path.Combine(directory, "Models"),
                    Path.Combine(directory, "sentence", "Models"),
                    Path.Combine(directory, "_run", "Debug", "sentence", "Models"),
                    directory
                };
                foreach (string root in roots)
                {
                    string[] fileNames =
                    {
                        "sentence-qwen-q8.gguf"
                    };
                    foreach (string fileName in fileNames)
                    {
                        string modelCandidate = Path.Combine(root, fileName);
                        if (File.Exists(modelCandidate))
                        {
                            return modelCandidate;
                        }
                    }
                }

                string parent = Path.GetDirectoryName(directory);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                directory = parent;
            }
            return modelPath;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string Serialize(SentencePipeRequest value)
        {
            return JsonSerializer.Serialize(
                value,
                SentencePipeJsonContext.Default.SentencePipeRequest);
        }

        private static SentencePipeResponse DeserializeResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                json,
                SentencePipeJsonContext.Default.SentencePipeResponse);
        }

        [JsonSourceGenerationOptions(
            GenerationMode = JsonSourceGenerationMode.Metadata,
            PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
        [JsonSerializable(typeof(SentencePipeRequest))]
        [JsonSerializable(typeof(SentencePipeResponse))]
        private partial class SentencePipeJsonContext : JsonSerializerContext
        {
        }

        private sealed class SentencePipeRequest
        {
            public string Type { get; set; }
            public long Seq { get; set; }
            public long Generation { get; set; }
            public string RawCode { get; set; }
            public string[] Candidates { get; set; }
        }

        private sealed class SentencePipeResponse
        {
            public long Seq { get; set; }
            public long Generation { get; set; }
            public string RawCode { get; set; }
            public bool Success { get; set; }
            public double[] Scores { get; set; }
        }
    }
}
