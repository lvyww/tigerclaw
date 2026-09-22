using System.Runtime.InteropServices;
using TigerClaw.Pinyin;

// Offline combined latency experiment. No production host/IPC integration.
internal sealed class BudgetReranker : IDisposable
{
    [DllImport("higherorder")] static extern IntPtr ho_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("higherorder")] static extern double ho_score(IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string tokens, out uint oov);
    [DllImport("higherorder")] static extern void ho_free(IntPtr model);
    readonly IntPtr model;
    readonly double alpha;
    public BudgetReranker(string path)
    {
        alpha = double.Parse(Environment.GetEnvironmentVariable("BUDGET_RERANK_ALPHA") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(alpha) || alpha < 0 || alpha > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        model = ho_load(path);
        if (model == IntPtr.Zero) throw new InvalidOperationException("Rerank model load failed");
    }
    public DecodeResult Rerank(DecodeResult result)
    {
        var scored = result.Candidates.Select(c => {
            var tokens = c.Segments.SelectMany(s => s.Tokens ?? throw new InvalidOperationException("Missing tokens")).ToArray();
            var value = ho_score(model, string.Join(' ', tokens), out _) * Math.Log(10) + 2 * tokens.Length;
            if (!double.IsFinite(value)) throw new InvalidOperationException("Nonfinite rerank score");
            return c with { Score = c.Score + alpha * (value-c.Score) };
        }).OrderByDescending(c => c.Score).ThenByDescending(c => c.Frequency).ThenBy(c => c.Text, StringComparer.Ordinal).ToArray();
        return result with { Candidates = scored };
    }
    public void Dispose() => ho_free(model);
}
