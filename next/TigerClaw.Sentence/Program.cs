using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
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
                NeuralSentenceScorer scorer;
                try
                {
                    scorer = new NeuralSentenceScorer(options.ModelPath, options.VocabularyPath);
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
                return @"Local\TigerClaw.Sentence.Test." + BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }
        }

        private static void ServeClient(Stream stream, NeuralSentenceScorer scorer)
        {
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
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

        private static SentenceResponse HandleRequest(SentenceRequest request, NeuralSentenceScorer scorer)
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
                return new SentenceResponse { Type = "response", Seq = request.Seq, Success = true };
            }

            if (type != "rerank")
            {
                throw new InvalidDataException("Unsupported request type: " + type);
            }

            string[] candidates = request.Candidates ?? new string[0];
            if (candidates.Length == 0 || candidates.Length > 20)
            {
                throw new InvalidDataException("rerank candidates must contain 1 to 20 items.");
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

    internal sealed class NeuralSentenceScorer : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly Dictionary<string, int> _vocabulary;
        private readonly int _bos;
        private readonly int _eos;
        private readonly int _pad;
        private readonly int _unknown;
        private readonly int _contextLength;

        public NeuralSentenceScorer(string modelPath, string vocabularyPath)
        {
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("Sentence ONNX model was not found.", modelPath);
            }
            if (!File.Exists(vocabularyPath))
            {
                throw new FileNotFoundException("Sentence vocabulary was not found.", vocabularyPath);
            }

            string vocabularyJson = ReadVocabulary(vocabularyPath);
            _vocabulary = new JavaScriptSerializer().Deserialize<Dictionary<string, int>>(vocabularyJson)
                ?? throw new InvalidDataException("Invalid sentence vocabulary.");
            _bos = ResolveRequired("<bos>");
            _eos = ResolveRequired("<eos>");
            _pad = ResolveRequired("<pad>");
            _unknown = ResolveRequired("<unk>");
            _contextLength = LoadContextLength(modelPath);
            if (string.Equals(Path.GetExtension(modelPath), ".tcmodel", StringComparison.OrdinalIgnoreCase))
            {
                byte[] modelBytes = EncryptedModelReader.ReadAllBytes(
                    modelPath,
                    EncryptedModelKind.SentenceTransformer);
                try
                {
                    _session = new InferenceSession(modelBytes);
                }
                finally
                {
                    Array.Clear(modelBytes, 0, modelBytes.Length);
                }
            }
            else
            {
                _session = new InferenceSession(modelPath);
            }
        }

        public string Provider => "cpu";

        public double[] Score(IReadOnlyList<string> texts)
        {
            List<int[]> encoded = texts.Select(Encode).ToList();
            int width = encoded.Max(values => values.Length) - 1;
            var inputValues = new long[encoded.Count * width];
            for (int row = 0; row < encoded.Count; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    inputValues[row * width + column] = _pad;
                }
                int[] values = encoded[row];
                for (int column = 0; column < values.Length - 1; column++)
                {
                    inputValues[row * width + column] = values[column];
                }
            }

            var tensor = new DenseTensor<long>(inputValues, new[] { encoded.Count, width });
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(
                new[] { NamedOnnxValue.CreateFromTensor("token_ids", tensor) }))
            {
                Tensor<float> logits = results.First().AsTensor<float>();
                int vocabularySize = logits.Dimensions[2];
                var scores = new double[encoded.Count];
                for (int row = 0; row < encoded.Count; row++)
                {
                    int[] sequence = encoded[row];
                    double score = 0.0;
                    for (int position = 0; position < sequence.Length - 1; position++)
                    {
                        int target = sequence[position + 1];
                        float maximum = float.NegativeInfinity;
                        for (int token = 0; token < vocabularySize; token++)
                        {
                            maximum = Math.Max(maximum, logits[row, position, token]);
                        }

                        double sum = 0.0;
                        for (int token = 0; token < vocabularySize; token++)
                        {
                            sum += Math.Exp(logits[row, position, token] - maximum);
                        }
                        score += logits[row, position, target] - maximum - Math.Log(sum);
                    }
                    scores[row] = score;
                }
                return scores;
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }

        private int[] Encode(string text)
        {
            var values = new List<int> { _bos };
            string source = text ?? string.Empty;
            for (int index = 0; index < source.Length; index++)
            {
                string token;
                if (char.IsHighSurrogate(source[index]) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]))
                {
                    token = source.Substring(index, 2);
                    index++;
                }
                else
                {
                    token = source[index].ToString();
                }
                values.Add(_vocabulary.TryGetValue(token, out int id) ? id : _unknown);
            }
            values.Add(_eos);
            if (values.Count - 1 > _contextLength)
            {
                values = values.Skip(values.Count - (_contextLength + 1)).ToList();
                values[0] = _bos;
            }
            return values.ToArray();
        }

        private int ResolveRequired(string token)
        {
            if (_vocabulary.TryGetValue(token, out int id))
            {
                return id;
            }
            throw new InvalidDataException("Vocabulary is missing " + token + ".");
        }

        private static string ReadVocabulary(string vocabularyPath)
        {
            if (!string.Equals(Path.GetExtension(vocabularyPath), ".tcmodel", StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadAllText(vocabularyPath);
            }

            byte[] vocabularyBytes = EncryptedModelReader.ReadAllBytes(
                vocabularyPath,
                EncryptedModelKind.SentenceVocabulary);
            try
            {
                return Encoding.UTF8.GetString(vocabularyBytes);
            }
            finally
            {
                Array.Clear(vocabularyBytes, 0, vocabularyBytes.Length);
            }
        }

        private static int LoadContextLength(string modelPath)
        {
            string metadataPath = Path.ChangeExtension(modelPath, ".json");
            if (!File.Exists(metadataPath))
            {
                return 64;
            }
            ModelMetadata metadata = JsonCodec.Deserialize<ModelMetadata>(File.ReadAllText(metadataPath));
            return metadata != null && metadata.ContextLength > 0 ? metadata.ContextLength : 64;
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

    [DataContract]
    internal sealed class ModelMetadata
    {
        [DataMember(Name = "context_length")] public int ContextLength { get; set; }
    }

    internal sealed class Options
    {
        public string PipeName { get; private set; } = "TigerClaw.Sentence.v1";
        public string ModelPath { get; private set; } = string.Empty;
        public string VocabularyPath { get; private set; } = string.Empty;
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
                    case "--pipe": result.PipeName = next; break;
                    case "--model": result.ModelPath = Path.GetFullPath(next); break;
                    case "--vocabulary": result.VocabularyPath = Path.GetFullPath(next); break;
                    case "--parent-pid": result.ParentPid = int.Parse(next, CultureInfo.InvariantCulture); break;
                }
            }

            if (string.IsNullOrWhiteSpace(result.ModelPath) || string.IsNullOrWhiteSpace(result.VocabularyPath))
            {
                throw new ArgumentException("--model and --vocabulary are required.");
            }
            return result;
        }
    }
}
