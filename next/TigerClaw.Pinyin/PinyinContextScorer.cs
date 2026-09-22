using System.Buffers;
using System.Numerics;
namespace TigerClaw.Pinyin;

// Many lexical entries share their first model token (especially OOV readings).
// Score that token for the current beam contexts once, without merging states
// or changing lexical visitation/pruning order.
internal sealed class PinyinContextScorer : IDisposable
{
    private readonly IIndexedPinyinLanguageModel model;
    private readonly (uint A,uint B)[] contexts;
    private CancellationToken cancellation;
    private readonly double[] best;
    private readonly Kenlm.ContextBatch? native;
    private readonly Dictionary<uint,Kenlm.ContextBatch> secondContexts=new();
    private readonly Dictionary<uint,double[]> first=new();
    private readonly Dictionary<(uint,uint),double[]> second=new();
    internal PinyinContextScorer(IIndexedPinyinLanguageModel model,(uint A,uint B)[] contexts,CancellationToken cancellation)
    {this.model=model;this.contexts=contexts;this.cancellation=cancellation;best=ArrayPool<double>.Shared.Rent(contexts.Length);if(model is Kenlm kenlm)native=kenlm.TryCreateContexts(contexts);}
    internal long RetainedBytes => (long)(1+first.Count+second.Count)*best.Length*sizeof(double)+
        (long)(1+secondContexts.Count)*contexts.Length*64;
    internal void SetCancellation(CancellationToken value)=>cancellation=value;
    internal bool Matches((uint A,uint B)[] value)=>contexts.AsSpan().SequenceEqual(value);
    internal double[] First(uint token)
    {
        if(first.TryGetValue(token,out var row))return row;
        row=ArrayPool<double>.Shared.Rent(contexts.Length);
        try{cancellation.ThrowIfCancellationRequested();if(native!=null)native.Score(token,row);else for(int i=0;i<contexts.Length;i++){cancellation.ThrowIfCancellationRequested();row[i]=model.LogProbability(contexts[i].A,contexts[i].B,token)+2.0;}}
        catch{ArrayPool<double>.Shared.Return(row);throw;}
        first.Add(token,row);return row;
    }
    internal double[] Second(uint previous,uint token)
    {
        if(second.TryGetValue((previous,token),out var row))return row;
        row=ArrayPool<double>.Shared.Rent(contexts.Length);
        try{cancellation.ThrowIfCancellationRequested();
            if(native!=null&&model is Kenlm kenlm){if(!secondContexts.TryGetValue(previous,out var batch))secondContexts[previous]=batch=kenlm.CreateContexts(contexts.Select(c=>(c.B,previous)).ToArray());batch.Score(token,row);}
            else for(int i=0;i<contexts.Length;i++){cancellation.ThrowIfCancellationRequested();row[i]=model.LogProbability(contexts[i].B,previous,token)+2.0;}}
        catch{ArrayPool<double>.Shared.Return(row);throw;}
        second.Add((previous,token),row);return row;
    }
    internal double[] Best(double[] bases,double bonus,double penalty,double[] firstRow,double[]? secondRow,double[] suffix)
    {
        int i=0,count=contexts.Length,width=Vector<double>.Count;
        var add=new Vector<double>(bonus);var subtract=new Vector<double>(penalty);
        for(;i+width<=count;i+=width)
        {
            var value=new Vector<double>(bases,i)+add;
            value-=subtract;value+=new Vector<double>(firstRow,i);
            if(secondRow!=null)value+=new Vector<double>(secondRow,i);
            foreach(double increment in suffix)value+=new Vector<double>(increment);
            value.CopyTo(best,i);
        }
        for(;i<count;i++)
        {
            double value=bases[i]+bonus-penalty;value+=firstRow[i];
            if(secondRow!=null)value+=secondRow[i];
            foreach(double increment in suffix)value+=increment;
            best[i]=value;
        }
        return best;
    }
    public void Dispose(){native?.Dispose();foreach(var batch in secondContexts.Values)batch.Dispose();ArrayPool<double>.Shared.Return(best);foreach(var row in first.Values)ArrayPool<double>.Shared.Return(row);foreach(var row in second.Values)ArrayPool<double>.Shared.Return(row);}
}

// Retain exact score rows for recurring contexts across edits/compositions. A
// completed row is immutable even if the surrounding decode is later canceled.
// Bound both cardinality and retained buffers; an active position may temporarily
// exceed the byte budget until the next acquisition, then is evicted as well.
internal sealed class PinyinContextCache : IDisposable
{
    private readonly LinkedList<PinyinContextScorer> recent=new();
    private const long Budget=32L*1024*1024;
    internal PinyinContextScorer Get(IIndexedPinyinLanguageModel model,(uint A,uint B)[] contexts,CancellationToken cancellation)
    {
        long bytes=0;foreach(var item in recent)bytes+=item.RetainedBytes;
        while(recent.Count>0&&(bytes>Budget||recent.Count>128))
        {var old=recent.Last!.Value;bytes-=old.RetainedBytes;recent.RemoveLast();old.Dispose();}
        for(var node=recent.First;node!=null;node=node.Next)
        {
            if(!node.Value.Matches(contexts))continue;
            recent.Remove(node);recent.AddFirst(node);node.Value.SetCancellation(cancellation);return node.Value;
        }
        var value=new PinyinContextScorer(model,contexts,cancellation);recent.AddFirst(value);return value;
    }
    public void Dispose(){foreach(var item in recent)item.Dispose();recent.Clear();}
}
