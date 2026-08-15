using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Sentence
{
    internal static class Program
    {
        private static volatile bool _stopping;

        private static int Main(string[] args)
        {
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, BuildMutexName(options.PipeName), out createdNew))
            {
                if (!createdNew)
                {
                    return 0;
                }

                StartParentWatcher(options.ParentPid);
                QwenSentenceScorer scorer;
                try
                {
                    scorer = new QwenSentenceScorer(options.ModelPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Failed to load sentence model: " + ex.Message);
                    return 3;
                }

                using (scorer)
                {
                    while (!_stopping)
                    {
                        try
                        {
                            using (var pipe = new NamedPipeServerStream(
                                options.PipeName,
                                PipeDirection.InOut,
                                1,
                                PipeTransmissionMode.Byte,
                                PipeOptions.None))
                            {
                                pipe.WaitForConnection();
                                ServeClient(pipe, scorer);
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }

            return 0;
        }

        private static string BuildMutexName(string pipeName)
        {
            if (string.Equals(pipeName, RuntimeConstants.SentencePipeShortName, StringComparison.Ordinal))
            {
                return @"Local\TigerClaw.Sentence.SingleInstance";
            }

            byte[] source = Encoding.UTF8.GetBytes(pipeName ?? string.Empty);
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(source);
                return @"Local\TigerClaw.Sentence.Test." +
                    BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }
        }

        private static void ServeClient(Stream stream, QwenSentenceScorer scorer)
        {
            using (var reader = new StreamReader(
                stream, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            using (var writer = new StreamWriter(
                stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            })
            {
                while (!_stopping)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                    {
                        return;
                    }

                    SentenceResponse response;
                    try
                    {
                        SentenceRequest request = JsonCodec.Deserialize<SentenceRequest>(line);
                        if (request == null)
                        {
                            throw new InvalidDataException("Empty request.");
                        }
                        response = HandleRequest(request, scorer);
                    }
                    catch (Exception ex)
                    {
                        response = new SentenceResponse
                        {
                            Type = "response",
                            Success = false,
                            Error = ex.Message
                        };
                    }

                    writer.WriteLine(JsonCodec.Serialize(response));
                }
            }
        }

        private static SentenceResponse HandleRequest(
            SentenceRequest request,
            QwenSentenceScorer scorer)
        {
            string type = request.Type ?? string.Empty;
            if (type == "hello" || type == "ping")
            {
                return new SentenceResponse
                {
                    Type = "response",
                    Seq = request.Seq,
                    Success = true,
                    Provider = scorer.Provider
                };
            }

            if (type == "shutdown")
            {
                _stopping = true;
                return new SentenceResponse
                {
                    Type = "response",
                    Seq = request.Seq,
                    Success = true
                };
            }

            if (type != "rerank")
            {
                throw new InvalidDataException("Unsupported request type: " + type);
            }

            string[] candidates = request.Candidates ?? new string[0];
            if (candidates.Length == 0 || candidates.Length > 5)
            {
                throw new InvalidDataException("rerank candidates must contain 1 to 5 items.");
            }

            return new SentenceResponse
            {
                Type = "response",
                Seq = request.Seq,
                Generation = request.Generation,
                RawCode = request.RawCode ?? string.Empty,
                Success = true,
                Provider = scorer.Provider,
                Scores = scorer.Score(candidates)
            };
        }

        private static void StartParentWatcher(int parentPid)
        {
            if (parentPid <= 0)
            {
                return;
            }

            var thread = new Thread(() =>
            {
                while (!_stopping)
                {
                    try
                    {
                        using (Process parent = Process.GetProcessById(parentPid))
                        {
                            if (parent.HasExited)
                            {
                                Environment.Exit(0);
                            }
                        }
                    }
                    catch
                    {
                        Environment.Exit(0);
                    }
                    Thread.Sleep(2000);
                }
            })
            {
                IsBackground = true,
                Name = "TigerClaw sentence parent watcher"
            };
            thread.Start();
        }
    }

    internal sealed class QwenSentenceScorer : IDisposable
    {
        private const string NativeLibrary = "TigerClaw.Sentence.Native.dll";
        private IntPtr _handle;

        public QwenSentenceScorer(string modelPath)
        {
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("Sentence Qwen model was not found.", modelPath);
            }

            using (PinnedUtf8 path = PinnedUtf8.Create(modelPath))
            {
                Check(NativeMethods.CreateFromFile(path.Pointer, out _handle));
            }

            if (_handle == IntPtr.Zero)
            {
                throw new InvalidDataException("Native Qwen loader returned an empty handle.");
            }
        }

        public string Provider => "llama.cpp-cpu-q8";

        public double[] Score(IReadOnlyList<string> texts)
        {
            if (_handle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(QwenSentenceScorer));
            }
            if (texts == null || texts.Count < 1 || texts.Count > 5)
            {
                throw new ArgumentException("Qwen reranking requires 1 to 5 candidates.", nameof(texts));
            }

            var pinned = new PinnedUtf8[texts.Count];
            var pointers = new IntPtr[texts.Count];
            try
            {
                for (int index = 0; index < texts.Count; index++)
                {
                    pinned[index] = PinnedUtf8.Create(texts[index] ?? string.Empty);
                    pointers[index] = pinned[index].Pointer;
                }
                var scores = new double[texts.Count];
                Check(NativeMethods.Score(_handle, pointers, pointers.Length, scores));
                return scores;
            }
            finally
            {
                for (int index = 0; index < pinned.Length; index++)
                {
                    pinned[index]?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.Destroy(handle);
            }
        }

        private static void Check(int result)
        {
            if (result == 0)
            {
                return;
            }
            IntPtr errorPointer = NativeMethods.LastError();
            string error = errorPointer == IntPtr.Zero
                ? null
                : Marshal.PtrToStringAnsi(errorPointer);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "Native Qwen operation failed." : error);
        }

        private sealed class PinnedUtf8 : IDisposable
        {
            private GCHandle _pin;

            private PinnedUtf8(byte[] bytes)
            {
                _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                Pointer = _pin.AddrOfPinnedObject();
            }

            public IntPtr Pointer { get; }

            public static PinnedUtf8 Create(string value)
            {
                byte[] source = Encoding.UTF8.GetBytes(value ?? string.Empty);
                var terminated = new byte[source.Length + 1];
                Buffer.BlockCopy(source, 0, terminated, 0, source.Length);
                return new PinnedUtf8(terminated);
            }

            public void Dispose()
            {
                if (_pin.IsAllocated)
                {
                    _pin.Free();
                }
            }
        }

        private static class NativeMethods
        {
            [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "tcs_create_from_file")]
            internal static extern int CreateFromFile(
                IntPtr modelPathUtf8,
                out IntPtr scorer);

            [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "tcs_score")]
            internal static extern int Score(
                IntPtr scorer,
                IntPtr[] candidatesUtf8,
                int candidateCount,
                [Out] double[] scores);

            [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "tcs_destroy")]
            internal static extern void Destroy(IntPtr scorer);

            [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl,
                EntryPoint = "tcs_last_error")]
            internal static extern IntPtr LastError();
        }
    }

    internal static class JsonCodec
    {
        public static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Deserialize<T>(string json) where T : class
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
    }

    [DataContract]
    internal sealed class SentenceRequest
    {
        [DataMember(Name = "type")] public string Type { get; set; }
        [DataMember(Name = "seq")] public long Seq { get; set; }
        [DataMember(Name = "generation")] public long Generation { get; set; }
        [DataMember(Name = "raw_code")] public string RawCode { get; set; }
        [DataMember(Name = "candidates")] public string[] Candidates { get; set; }
    }

    [DataContract]
    internal sealed class SentenceResponse
    {
        [DataMember(Name = "type")] public string Type { get; set; }
        [DataMember(Name = "seq")] public long Seq { get; set; }
        [DataMember(Name = "generation")] public long Generation { get; set; }
        [DataMember(Name = "raw_code")] public string RawCode { get; set; }
        [DataMember(Name = "success")] public bool Success { get; set; }
        [DataMember(Name = "provider")] public string Provider { get; set; }
        [DataMember(Name = "scores")] public double[] Scores { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
    }

    internal sealed class Options
    {
        public string PipeName { get; private set; } = "TigerClaw.Sentence.v1";
        public string ModelPath { get; private set; } = string.Empty;
        public int ParentPid { get; private set; }

        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (int index = 0; index < args.Length; index++)
            {
                string value = args[index];
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for " + value + ".");
                }
                string next = args[++index];
                switch (value)
                {
                    case "--pipe":
                        result.PipeName = next;
                        break;
                    case "--model":
                        result.ModelPath = Path.GetFullPath(next);
                        break;
                    case "--parent-pid":
                        result.ParentPid = int.Parse(next, CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ArgumentException("Unknown option: " + value + ".");
                }
            }

            if (string.IsNullOrWhiteSpace(result.ModelPath))
            {
                throw new ArgumentException("--model is required.");
            }
            return result;
        }
    }
}
