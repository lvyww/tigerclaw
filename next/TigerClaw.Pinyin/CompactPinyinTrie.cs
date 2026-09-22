namespace TigerClaw.Pinyin;

// Immutable pooled arcs and candidate ranges. Lookup arcs are sorted; traversal
// arcs retain insertion order because lexical source order breaks some ties.
internal sealed class CompactPinyinTrie<TKey> where TKey : notnull
{
    private readonly record struct Range(int Arc, int Arcs, int Value, int Values);
    private readonly Range[] nodes;
    private readonly TKey[] keys;
    private readonly int[] children, traversal;
    private readonly Entry[] values;
    private readonly int[] minimumCompletionPrefix;
    private readonly IComparer<TKey> comparer;
    private CompactPinyinTrie(Range[] nodes, TKey[] keys, int[] children, int[] traversal, Entry[] values, IComparer<TKey> comparer, bool completion)
    {
        this.nodes=nodes;this.keys=keys;this.children=children;this.traversal=traversal;this.values=values;this.comparer=comparer;
        minimumCompletionPrefix=new int[nodes.Length];
        int Visit(int node)
        {
            int minimum=int.MaxValue;var range=nodes[node];
            for(int i=range.Value;i<range.Value+range.Values;i++)
            {
                var entry=values[i];
                if(entry.Tokens is {Length:>0}) minimum=Math.Min(minimum,entry.CompletionPrefix);
            }
            for(int i=range.Arc;i<range.Arc+range.Arcs;i++) minimum=Math.Min(minimum,Visit(children[i]));
            return minimumCompletionPrefix[node]=minimum;
        }
        if(completion)Visit(0);
    }
    internal Entry[] OrderedValues()
    {
        var result=new Entry[values.Length];int offset=0;
        var stack=new Stack<int>();stack.Push(0);
        while(stack.Count!=0)
        {
            var range=nodes[stack.Pop()];
            Array.Copy(values,range.Value,result,offset,range.Values);offset+=range.Values;
            for(int i=range.Arc+range.Arcs-1;i>=range.Arc;i--)stack.Push(traversal[i]);
        }
        return result;
    }
    internal bool TryNext(int node,TKey key,out int child)
    {
        var range=nodes[node];int lo=range.Arc,hi=lo+range.Arcs-1;
        while(lo<=hi){int mid=(lo+hi)>>1;int c=comparer.Compare(keys[mid],key);if(c==0){child=children[mid];return true;}if(c<0)lo=mid+1;else hi=mid-1;}
        child=0;return false;
    }
    internal IEnumerable<(TKey Key,int Child)> OrderedChildren(int node)
    {
        var range=nodes[node];
        for(int i=range.Arc;i<range.Arc+range.Arcs;i++)
        {
            int child=traversal[i];
            for(int j=range.Arc;j<range.Arc+range.Arcs;j++)
                if(children[j]==child){yield return(keys[j],child);break;}
        }
    }
    internal bool HasChildren(int node)=>nodes[node].Arcs!=0;
    internal ReadOnlySpan<Entry> Values(int node){var range=nodes[node];return values.AsSpan(range.Value,range.Values);}
    internal IEnumerable<Entry> Walk(int node=0,int completionPrefix=int.MaxValue,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(completionPrefix<minimumCompletionPrefix[node])yield break;
        var range=nodes[node];
        for(int i=range.Value;i<range.Value+range.Values;i++)yield return values[i];
        for(int i=range.Arc;i<range.Arc+range.Arcs;i++)
            foreach(var entry in Walk(traversal[i],completionPrefix,cancellation))yield return entry;
    }
    internal sealed class Builder
    {
        private readonly Dictionary<(int Node,TKey Key),int> arcs=new();
        private readonly List<int> valueNodes=new();
        private readonly List<Entry> values=new();
        private int count=1;
        private readonly List<(int Node,TKey Key,int Child)> sortedArcs=new();
        private readonly List<int> sourceOrders=new(),sortedPath=new(){0};
        private TKey[] previous=[];
        private int previousLength;
        private bool sorted;
        internal void AddSorted(ReadOnlySpan<TKey> code,Entry entry,int sourceOrder)
        {
            sorted=true;
            int common=0;
            while(common<code.Length&&common<previousLength&&EqualityComparer<TKey>.Default.Equals(code[common],previous[common]))common++;
            if(sortedPath.Count>common+1)sortedPath.RemoveRange(common+1,sortedPath.Count-common-1);
            int node=sortedPath[^1];
            for(int i=common;i<code.Length;i++)
            {int next=count++;sortedArcs.Add((node,code[i],next));sortedPath.Add(next);node=next;}
            valueNodes.Add(node);values.Add(entry);sourceOrders.Add(sourceOrder);
            if(previous.Length<code.Length)Array.Resize(ref previous,Math.Max(code.Length,previous.Length*2));
            code.CopyTo(previous);previousLength=code.Length;
        }
        internal void Add(ReadOnlySpan<TKey> code,Entry entry)
        {
            int node=0;
            foreach(var key in code){if(!arcs.TryGetValue((node,key),out int next))arcs[(node,key)]=next=count++;node=next;}
            valueNodes.Add(node);values.Add(entry);
        }
        internal void AddTransformed<T>(T[] code,Func<T,TKey> transform,Entry entry)
        {
            int node=0;
            foreach(var item in code){var key=transform(item);if(!arcs.TryGetValue((node,key),out int next))arcs[(node,key)]=next=count++;node=next;}
            valueNodes.Add(node);values.Add(entry);
        }
        internal void Add(IEnumerable<TKey> code,Entry entry)
        {
            int node=0;
            foreach(var key in code){if(!arcs.TryGetValue((node,key),out int next))arcs[(node,key)]=next=count++;node=next;}
            valueNodes.Add(node);values.Add(entry);
        }
        internal CompactPinyinTrie<TKey> Build(IComparer<TKey>? comparer=null,bool completion=false)
        {
            comparer??=Comparer<TKey>.Default;
            var allArcs=sorted?sortedArcs.ToArray():arcs.Select(a=>(Node:a.Key.Node,Key:a.Key.Key,Child:a.Value)).ToArray();
            var arcCounts=new int[count];var valueCounts=new int[count];
            foreach(var arc in allArcs)arcCounts[arc.Node]++;
            foreach(int node in valueNodes)valueCounts[node]++;
            var ranges=new Range[count];int a=0,v=0;
            for(int n=0;n<count;n++){ranges[n]=new(a,arcCounts[n],v,valueCounts[n]);a+=arcCounts[n];v+=valueCounts[n];}
            var keys=new TKey[a];var children=new int[a];var ordered=new int[a];var entries=new Entry[v];
            Array.Clear(arcCounts);Array.Clear(valueCounts);
            foreach(var arc in allArcs){int i=ranges[arc.Node].Arc+arcCounts[arc.Node]++;keys[i]=arc.Key;children[i]=ordered[i]=arc.Child;}
            for(int i=0;i<values.Count;i++){int node=valueNodes[i];entries[ranges[node].Value+valueCounts[node]++]=values[i];}
            if(sorted)
            {
                var minimum=new int[count];Array.Fill(minimum,int.MaxValue);
                for(int i=0;i<values.Count;i++)minimum[valueNodes[i]]=Math.Min(minimum[valueNodes[i]],sourceOrders[i]);
                for(int i=allArcs.Length-1;i>=0;i--){var arc=allArcs[i];minimum[arc.Node]=Math.Min(minimum[arc.Node],minimum[arc.Child]);}
                var sourceComparer=Comparer<int>.Create((a,b)=>minimum[a].CompareTo(minimum[b]));
                foreach(var range in ranges)if(range.Arcs>1)Array.Sort(ordered,range.Arc,range.Arcs,sourceComparer);
            }
            foreach(var range in ranges)if(range.Arcs>1)Array.Sort(keys,children,range.Arc,range.Arcs,comparer);
            return new(ranges,keys,children,ordered,entries,comparer,completion);
        }
    }
}
