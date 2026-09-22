using System.Runtime.InteropServices;

namespace TigerClaw.Pinyin;

// Called under the host's decode gate, before learning/pins and generation publication.
internal sealed class PinyinReranker : IDisposable
{
    [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr joint_load_fivegram([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
    private static extern double joint_sentence_score(IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string tokens);
    [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr joint_error();
    [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
    private static extern void joint_free(IntPtr model);

    private IntPtr handle;
    private readonly Func<string[], double> score;
    internal string? Error { get; private set; }
    internal PinyinReranker(string path)
    {
        handle = joint_load_fivegram(path);
        if (handle == IntPtr.Zero) throw new InvalidDataException(Marshal.PtrToStringUTF8(joint_error()));
        score = tokens => joint_sentence_score(handle, string.Join(' ', tokens)) * Math.Log(10);
    }
    internal PinyinReranker(Func<string[], double> score) => this.score = score;

    internal DecodeResult Rank(DecodeResult result, CancellationToken cancellation = default)
    {
        if (Error != null) return result;
        try
        {
            var candidates = new Candidate[result.Candidates.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var candidate = result.Candidates[i];
                var tokens = candidate.Segments.SelectMany(s => s.Tokens ??
                    throw new InvalidDataException("Five-gram reranking requires joint tokens")).ToArray();
                // Keep non-LM terms explicit to avoid cancellation roundoff
                // breaking exact score ties. Include locked segments once.
                double value = score(tokens) + 2.0 * tokens.Length +
                    candidate.Segments.Sum(s => s.WordBonus - s.SpellingPenalty);
                if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite five-gram score");
                candidates[i] = candidate with { Score = value };
            }
            cancellation.ThrowIfCancellationRequested();
            return result with { Candidates = candidates.OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Frequency).ThenBy(c => c.Text, StringComparer.Ordinal).ToArray() };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // An entire generation falls back; never mix score scales in one menu.
            Error = e.Message;
            return result;
        }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { joint_free(handle); handle = IntPtr.Zero; } }
}
