using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
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

    internal sealed class SentenceRerankClient : ISentenceRerankService
    {
        private readonly object _lock = new object();
        private readonly CoreRuntimeState _state;
        private readonly ProcessLauncher _launcher;
        private readonly Action<long, string, double[]> _resultCallback;
        private readonly Thread _worker;
        private SentenceRerankRequest _pending;
        private bool _stopping;
        private long _seq;

        public SentenceRerankClient(
            CoreRuntimeState state,
            ProcessLauncher launcher,
            Action<long, string, double[]> resultCallback)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _resultCallback = resultCallback;
            _worker = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "TigerClaw sentence reranker"
            };
            _worker.Start();
        }

        public void Request(SentenceRerankRequest request)
        {
            if (request?.Candidates == null || request.Candidates.Length == 0 ||
                !_state.IsSentenceInputActive() || !_state.GetSentenceNeuralRerankEnabled())
            {
                return;
            }

            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }

                _pending = request;
                Monitor.PulseAll(_lock);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _stopping = true;
                _pending = null;
                Monitor.PulseAll(_lock);
            }

            if (_worker.IsAlive)
            {
                _worker.Join(1500);
            }
        }

        private void WorkerMain()
        {
            while (true)
            {
                SentenceRerankRequest request;
                lock (_lock)
                {
                    while (!_stopping && _pending == null)
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_stopping)
                    {
                        return;
                    }

                    request = _pending;
                    _pending = null;
                }

                TryProcess(request);
            }
        }

        private void TryProcess(SentenceRerankRequest request)
        {
            try
            {
                string modelPath = ResolveModelPath();
                if (!File.Exists(modelPath))
                {
                    return;
                }

                string arguments =
                    "--parent-pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                    " --pipe " + QuoteArgument(RuntimeConstants.SentencePipeShortName) +
                    " --model " + QuoteArgument(modelPath);
                if (!_launcher.TryLaunchSentence(arguments))
                {
                    return;
                }

                using (var pipe = new NamedPipeClientStream(
                    ".",
                    RuntimeConstants.SentencePipeShortName,
                    PipeDirection.InOut,
                    PipeOptions.None))
                {
                    pipe.Connect(10000);
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                    {
                        AutoFlush = true,
                        NewLine = "\n"
                    })
                    {
                        long seq = Interlocked.Increment(ref _seq);
                        var message = new SentencePipeRequest
                        {
                            Type = "rerank",
                            Seq = seq,
                            Generation = request.Generation,
                            RawCode = request.RawCode ?? string.Empty,
                            Candidates = request.Candidates
                        };
                        writer.WriteLine(Serialize(message));
                        Task<string> readTask = Task.Factory.StartNew(reader.ReadLine);
                        if (!readTask.Wait(TimeSpan.FromSeconds(5)))
                        {
                            return;
                        }
                        string line = readTask.Result;
                        SentencePipeResponse response = Deserialize<SentencePipeResponse>(line);
                        if (response != null && response.Success && response.Seq == seq &&
                            response.Generation == request.Generation && response.Scores != null)
                        {
                            _resultCallback?.Invoke(response.Generation, response.RawCode, response.Scores);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Neural reranking is optional. The n-gram order remains usable on every failure path.
                Debug.WriteLine("[Sentence] rerank failed: " + ex.Message);
            }
        }

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

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(stream) as T;
            }
        }

        [DataContract]
        private sealed class SentencePipeRequest
        {
            [DataMember(Name = "type")] public string Type { get; set; }
            [DataMember(Name = "seq")] public long Seq { get; set; }
            [DataMember(Name = "generation")] public long Generation { get; set; }
            [DataMember(Name = "raw_code")] public string RawCode { get; set; }
            [DataMember(Name = "candidates")] public string[] Candidates { get; set; }
        }

        [DataContract]
        private sealed class SentencePipeResponse
        {
            [DataMember(Name = "seq")] public long Seq { get; set; }
            [DataMember(Name = "generation")] public long Generation { get; set; }
            [DataMember(Name = "raw_code")] public string RawCode { get; set; }
            [DataMember(Name = "success")] public bool Success { get; set; }
            [DataMember(Name = "scores")] public double[] Scores { get; set; }
        }
    }
}
