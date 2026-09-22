using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TigerClaw.Core
{
    // Most recent token first. A fivegram needs four tokens, including BOS until
    // it leaves the window. Value states survive incremental/locked-prefix reuse.
    internal readonly record struct SentenceLmHistory(uint A, uint B, uint C, uint D, int Count)
    {
        public SentenceLmHistory Append(uint token) => new(token, A, B, C, Math.Min(4, Count + 1));
    }

    internal interface ISentenceHistoryLanguageModel : ISentenceLanguageModel
    {
        SentenceLmHistory BeginHistory { get; }
        double Step(SentenceLmHistory history, string target, out SentenceLmHistory next);
    }

    internal sealed class SentenceFivegramModel : ISentenceLanguageModel, IDisposable
    {
        internal const string FileName = "sentence-fivegram.klm";
        private readonly ModelHandle _handle;
        private readonly SentenceNgramModel _prior;
        private readonly object _gate = new();
        private bool _disposed;

        internal sealed class ModelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal ModelHandle(IntPtr pointer) : base(true) { SetHandle(pointer); }
            protected override bool ReleaseHandle() { Native.joint_free(handle); return true; }
        }
        private static class Native
        {
            [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr joint_load_fivegram([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
            [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void joint_free(IntPtr model);
            [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr joint_error();
            [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint joint_index(IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string token);
            [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
            internal static extern double joint_score_history4(IntPtr model, uint a, uint b, uint c, uint d, uint count, uint target);
        }

        // Takes ownership of prior only on success. The threegram is used solely
        // for the existing observed-bigram isolation prior, never search scoring.
        internal SentenceFivegramModel(string path, SentenceNgramModel prior)
        {
            _prior = prior ?? throw new ArgumentNullException(nameof(prior));
            _handle = new ModelHandle(Native.joint_load_fivegram(path));
            if (_handle.IsInvalid) throw new InvalidDataException(Marshal.PtrToStringUTF8(Native.joint_error()));
            try
            {
                // Probe the new ABI before selecting this model; older DLLs fall back.
                uint bos = Native.joint_index(_handle.DangerousGetHandle(), "<s>");
                uint eos = Native.joint_index(_handle.DangerousGetHandle(), "</s>");
                double score = Native.joint_score_history4(_handle.DangerousGetHandle(), bos, 0, 0, 0, 1, eos);
                if (!double.IsFinite(score)) throw new InvalidDataException("Invalid fivegram boundary score");
            }
            catch { _handle.Dispose(); throw; }
        }

        internal static ISentenceLanguageModel LoadAvailable(string baseDirectory)
        {
            var prior = SentenceNgramModel.LoadAvailable(baseDirectory);
            if (prior == null) return null;
            string root = string.IsNullOrEmpty(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
            foreach (string path in new[] { Path.Combine(root, "Models", FileName), Path.Combine(root, FileName) })
            {
                if (!File.Exists(path)) continue;
                try { return new SentenceFivegramModel(path, prior); }
                catch (Exception ex)
                {
                    Trace.TraceError("Failed to load sentence fivegram '{0}', retaining trigram fallback: {1}", path, ex.Message);
                }
            }
            return prior;
        }

        internal QuerySession CreateQuerySession()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return new QuerySession(_handle, _prior.CreateQuerySession());
            }
        }
        public double LogProbability(string a, string b, string c) => throw new InvalidOperationException("Fivegram search requires full history");
        public bool HasObservedBigram(string a, string b) => _prior.HasObservedBigram(a, b);
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _handle.Dispose();
                _prior.Dispose();
            }
        }

        internal sealed class QuerySession : ISentenceHistoryLanguageModel, IDisposable
        {
            private readonly object _gate = new();
            private ModelHandle _owner;
            private readonly IntPtr _pointer;
            private readonly SentenceNgramModel.QuerySession _prior;
            private readonly Dictionary<string, uint> _tokens = new(StringComparer.Ordinal);
            private readonly Dictionary<(SentenceLmHistory, uint), double> _scores = new();
            public SentenceLmHistory BeginHistory { get; }
            internal QuerySession(ModelHandle owner, SentenceNgramModel.QuerySession prior)
            {
                _prior = prior;
                bool retained = false;
                try
                {
                    owner.DangerousAddRef(ref retained);
                    _pointer = owner.DangerousGetHandle();
                    BeginHistory = new(Native.joint_index(_pointer, "<s>"), 0, 0, 0, 1);
                    _owner = owner;
                }
                catch { if (retained) owner.DangerousRelease(); prior.Dispose(); throw; }
            }
            ~QuerySession() { Dispose(); }
            public double Step(SentenceLmHistory history, string target, out SentenceLmHistory next)
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_owner == null, this);
                    if (!_tokens.TryGetValue(target, out uint token))
                    {
                        if (_tokens.Count >= 32768) _tokens.Clear();
                        token = Native.joint_index(_pointer, target == "\u0003" ? "</s>" : target);
                        _tokens.Add(target, token);
                    }
                    next = history.Append(token);
                    var key = (history, token);
                    if (_scores.TryGetValue(key, out double score)) return score;
                    score = Native.joint_score_history4(_pointer, history.A, history.B, history.C, history.D, (uint)history.Count, token) * Math.Log(10.0);
                    if (!double.IsFinite(score)) throw new InvalidDataException("Non-finite fivegram transition");
                    if (_scores.Count >= 8192) _scores.Clear();
                    _scores.Add(key, score);
                    return score;
                }
            }
            public double LogProbability(string a, string b, string c) => throw new InvalidOperationException("Fivegram search requires full history");
            public bool HasObservedBigram(string a, string b) => _prior.HasObservedBigram(a, b);
            public void Dispose()
            {
                lock (_gate)
                {
                    if (_owner == null) return;
                    _owner.DangerousRelease();
                    _owner = null;
                    _prior.Dispose();
                    _tokens.Clear(); _scores.Clear();
                }
                GC.SuppressFinalize(this);
            }
        }
    }
}
