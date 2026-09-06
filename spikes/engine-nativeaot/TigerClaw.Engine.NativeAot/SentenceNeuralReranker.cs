using System.Runtime.InteropServices;
using System.Text;

namespace TigerClaw.Engine.NativeAot;

internal unsafe sealed class SentenceNeuralReranker : IDisposable
{
    private readonly object _lock = new();
    private readonly string _nativeLibraryPath;
    private readonly string _modelPath;
    private readonly Thread _worker;
    private WorkItem? _pending;
    private bool _stopping;
    private bool _disabled;

    public SentenceNeuralReranker(string nativeLibraryPath, string modelPath)
    {
        _nativeLibraryPath = nativeLibraryPath;
        _modelPath = modelPath;
        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "TigerClaw macOS sentence reranker",
            // Qwen ranking is a refinement, never a reason to deprioritize
            // interactive input or candidate-window updates.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    public bool Request(long generation, string rawCode, string[] candidates, Action<long, string, double[]?> completed)
    {
        if (candidates.Length == 0 || !File.Exists(_nativeLibraryPath) || !File.Exists(_modelPath))
        {
            return false;
        }

        lock (_lock)
        {
            if (_stopping || _disabled)
            {
                return false;
            }

            _pending = new WorkItem(generation, rawCode, candidates, completed);
            Monitor.PulseAll(_lock);
            return true;
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

        _worker.Join();
    }

    private void WorkerMain()
    {
        nint library = 0;
        nint scorer = 0;
        try
        {
            while (true)
            {
                WorkItem request;
                lock (_lock)
                {
                    while (!_stopping && _pending is null)
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_stopping)
                    {
                        return;
                    }

                    request = _pending!;
                    _pending = null;
                }

                try
                {
                    if (scorer == 0)
                    {
                        library = NativeLibrary.Load(_nativeLibraryPath);
                        scorer = CreateScorer(library, _modelPath);
                    }

                    request.Completed(request.Generation, request.RawCode, Score(library, scorer, request.Candidates));
                }
                catch
                {
                    lock (_lock)
                    {
                        _disabled = true;
                    }
                    request.Completed(request.Generation, request.RawCode, null);
                }
            }
        }
        finally
        {
            if (scorer != 0 && library != 0)
            {
                var destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "tcs_destroy");
                destroy(scorer);
            }
            if (library != 0)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    private static nint CreateScorer(nint library, string modelPath)
    {
        var create = (delegate* unmanaged[Cdecl]<byte*, nint*, int>)NativeLibrary.GetExport(library, "tcs_create_from_file");
        byte[] encodedPath = Encoding.UTF8.GetBytes(modelPath + '\0');
        nint scorer = 0;
        fixed (byte* path = encodedPath)
        {
            if (create(path, &scorer) != 0 || scorer == 0)
            {
                throw new InvalidOperationException("Cannot load the sentence Qwen model.");
            }
        }
        return scorer;
    }

    private static double[] Score(nint library, nint scorer, string[] candidates)
    {
        var score = (delegate* unmanaged[Cdecl]<nint, byte**, int, double*, int>)NativeLibrary.GetExport(library, "tcs_score");
        nint[] strings = new nint[candidates.Length];
        try
        {
            byte** pointers = stackalloc byte*[candidates.Length];
            for (int index = 0; index < candidates.Length; index++)
            {
                strings[index] = Marshal.StringToCoTaskMemUTF8(candidates[index]);
                pointers[index] = (byte*)strings[index];
            }

            double[] scores = new double[candidates.Length];
            fixed (double* values = scores)
            {
                if (score(scorer, pointers, candidates.Length, values) != 0)
                {
                    throw new InvalidOperationException("Sentence Qwen scoring failed.");
                }
            }
            return scores;
        }
        finally
        {
            foreach (nint value in strings)
            {
                if (value != 0)
                {
                    Marshal.FreeCoTaskMem(value);
                }
            }
        }
    }

    private sealed record WorkItem(
        long Generation,
        string RawCode,
        string[] Candidates,
        Action<long, string, double[]?> Completed);
}
