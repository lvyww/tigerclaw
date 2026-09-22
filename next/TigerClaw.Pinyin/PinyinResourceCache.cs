using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TigerClaw.Pinyin;

internal sealed class PinyinResourceCache : IDisposable
{
    private static readonly object gate=new();
    private static readonly Dictionary<string,PinyinResourceCache> cache=new(StringComparer.Ordinal);
    private readonly string key;
    private int references=1;
    internal Lexicon Lexicon {get;}
    internal Kenlm Model {get;}
    internal PinyinReranker? Reranker {get;}
    internal string? RerankError {get;}
    internal string LoadTimings {get;}
    private PinyinResourceCache(string key,string table,string tokens,string model,string? rerank)
    {
        this.key=key;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var map=PinyinTokenReader.Read(tokens);double tokenMs=watch.Elapsed.TotalMilliseconds;watch.Restart();
        Lexicon=Lexicon.Load(table,tokens:map);map.Clear();double lexiconMs=watch.Elapsed.TotalMilliseconds;watch.Restart();
        Lexicon.Warm();double spellingMs=watch.Elapsed.TotalMilliseconds;watch.Restart();
        Model=new Kenlm(model);
        LoadTimings=FormattableString.Invariant($"tokens_ms={tokenMs:F3},lexicon_ms={lexiconMs:F3},spelling_ms={spellingMs:F3},model_ms={watch.Elapsed.TotalMilliseconds:F3}");
        if(rerank!=null)try{Reranker=new(rerank);}catch(Exception e){RerankError=e.Message;}
    }
    internal static PinyinResourceCache Acquire(string table,string tokens,string model,string? rerank)
    {
        string key=string.Join('|',new[]{Identity(table),Identity(tokens),Identity(model),rerank==null?"":Identity(rerank)});
        lock(gate)
        {
            if(cache.TryGetValue(key,out var value)){value.references++;return value;}
            value=new(key,table,tokens,model,rerank);cache.Add(key,value);return value;
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(--references!=0)return;
            cache.Remove(key);Reranker?.Dispose();Model.Dispose();
        }
    }
    private static string Identity(string path)
    {
        var info=new FileInfo(path);
        if(!info.Exists)return Path.GetFullPath(path)+":missing";
        if(OperatingSystem.IsWindows())
        {
            using var file=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(GetFileInformationByHandle(file,out var id))return $"{id.Volume:X}:{id.IndexHigh:X}:{id.IndexLow:X}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        return $"{Path.GetFullPath(path)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileIdentity information);
}
