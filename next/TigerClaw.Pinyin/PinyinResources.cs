using System.Text.Json;
using System.Text;

namespace TigerClaw.Pinyin;

internal sealed class PinyinResources : IDisposable
{
    private readonly PinyinResourceCache? shared;
    private bool disposed;
    internal string LoadTimings => shared?.LoadTimings ?? "injected";
    internal Lexicon Lexicon { get; private set; }
    internal Lexicon Baseline { get; }
    internal IPinyinLanguageModel Model { get; }
    internal PinyinReranker? Reranker { get; }
    internal string? RerankError { get; }
    internal int CandidateLimit => Reranker == null ? 200 : 50;
    internal DecodeResult Rank(DecodeResult result, CancellationToken cancellation = default) =>
        Reranker?.Rank(result, cancellation) ?? result;
    internal string Directory { get; }
    internal PinyinTigerAux Auxiliary { get; private set; } = new([]);
    internal PinyinEnglish English { get; private set; } = PinyinEnglish.Empty;
    internal PinyinPreferences Preferences { get; private set; } = PinyinPreferences.Empty;
    internal void ReplacePreferences(PinyinPreferences preferences) => Preferences = preferences;
    internal PinyinResources(string directory)
    {
        Directory = directory;
        using var descriptor = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "schema.json")));
        var root = descriptor.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("engine").GetString() != "full_pinyin")
            throw new InvalidDataException("Unsupported full-pinyin scheme");
        string Resource(string name)
        {
            string relative = root.GetProperty(name).GetString() ?? "";
            string full = Path.GetFullPath(Path.Combine(directory, relative));
            if (!full.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pinyin resources must belong to their scheme");
            return full;
        }
        shared = PinyinResourceCache.Acquire(Resource("lexicon"), Resource("tokens"), Resource("model"),
            root.TryGetProperty("rerank_model", out _) ? Resource("rerank_model") : null);
        try
        {
            Baseline = shared.Lexicon;
            Lexicon = PinyinUserWords.Merge(Baseline, PinyinUserWords.Load(directory));
            Lexicon.Warm();
            Preferences = PinyinPreferences.Load(directory);
            English = PinyinEnglish.Load(directory);
            Auxiliary = PinyinTigerAux.Load(directory);
            Model = shared.Model; Reranker = shared.Reranker; RerankError = shared.RerankError;
        }
        catch { shared.Dispose(); throw; }
    }
    internal PinyinResources(Lexicon lexicon, IPinyinLanguageModel model, string directory)
    { Baseline = Lexicon = lexicon; Model = model; Directory = directory; Preferences = PinyinPreferences.Load(directory); English = PinyinEnglish.Load(directory); Auxiliary = PinyinTigerAux.Load(directory); }
    internal void ReplaceLexicon(Lexicon lexicon) => Lexicon = lexicon;
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(shared!=null)shared.Dispose();else{Reranker?.Dispose();(Model as IDisposable)?.Dispose();}
    }
}
