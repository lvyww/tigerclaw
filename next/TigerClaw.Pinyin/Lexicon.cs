using System.Globalization;
using System.Text;

namespace TigerClaw.Pinyin;

internal sealed partial class Lexicon
{
    private readonly CompactPinyinTrie<char> codes;
    private readonly Entry[] ordered = [];
    private readonly Lexicon? baseline;
    private readonly Dictionary<(string Text,string Code),Entry>? overrides;
    internal IEnumerable<Entry> Entries => baseline==null?ordered:MergedEntries(0,0);
    private IEnumerable<Entry> MergedEntries(int original,int delta)
    {
        if(delta<0){foreach(var entry in baseline!.codes.Walk(original))yield return entry;yield break;}
        if(original<0){foreach(var entry in codes.Walk(delta))yield return entry;yield break;}
        var seen=new HashSet<(string,string)>();
        if(original>=0)
            foreach(var entry in baseline!.NodeEntries(original))
            {seen.Add((entry.Text,entry.Code));yield return overrides!.GetValueOrDefault((entry.Text,entry.Code),entry);}
        if(delta>=0)
            foreach(var entry in NodeEntries(delta))if(!seen.Contains((entry.Text,entry.Code)))yield return entry;
        if(original>=0)
            foreach(var edge in baseline!.codes.OrderedChildren(original))
            {
                int next=-1;if(delta>=0&&!codes.TryNext(delta,edge.Key,out next))next=-1;
                foreach(var entry in MergedEntries(edge.Child,next))yield return entry;
            }
        if(delta>=0)
            foreach(var edge in codes.OrderedChildren(delta))
                if(original<0||!baseline!.codes.TryNext(original,edge.Key,out _))
                    foreach(var entry in MergedEntries(-1,edge.Child))yield return entry;
    }
    internal Lexicon(Lexicon baseline,IEnumerable<Entry> additions)
    {
        this.baseline=baseline;overrides=additions.ToDictionary(e=>(e.Text,e.Code));
        var builder=new CompactPinyinTrie<char>.Builder();
        MaximumLength=baseline.MaximumLength;Count=baseline.Count;
        foreach(var entry in overrides.Values)
        {
            builder.Add(entry.Code.AsSpan(),entry);MaximumLength=Math.Max(MaximumLength,entry.Code.Length);
            if(!baseline.Edges(entry.Code,0).Any(e=>e.End==entry.Code.Length&&e.Entry.Text==entry.Text))Count++;
        }
        codes=builder.Build(completion:true);spelling=new(()=>new SpellingIndex(Entries));
    }
    internal int MaximumLength { get; private set; }
    internal int Count { get; private set; }
    internal void Warm() => _ = spelling.Value;
    internal static string NormalizeCode(string raw) => raw.ToLowerInvariant();
    internal static string RawReading(string token)
    {
        string reading=token[(token.LastIndexOf('/')+1)..];
        return reading=="nve"?"nue":reading=="lve"?"lue":reading;
    }
    internal Lexicon(IEnumerable<(string Text,string Code,int Frequency)> rows,bool words=true,
        IReadOnlyDictionary<(string Text,string Code),string[]>? tokens=null,
        IReadOnlySet<(string Text,string Code)>? userEntries=null)
    {
        spelling=new(()=>new SpellingIndex(Entries));
        var builder=new CompactPinyinTrie<char>.Builder();
        var characters=new Dictionary<int,string>();
        // Dictionary preserves first occurrence; duplicate frequencies keep the
        // same maximum and source ordering as the original GroupBy/MaxBy.
        var unique=new Dictionary<(string Text,string Code),int>(tokens?.Count??0);
        foreach(var row in rows){var key=(row.Text,row.Code);if(!unique.TryGetValue(key,out int f)||row.Frequency>f)unique[key]=row.Frequency;}
        var source=new List<(Entry Entry,int Order)>(unique.Count);
        foreach(var row in unique)
        {
            int characterCount=0;foreach(var rune in row.Key.Text.EnumerateRunes())characterCount++;
            if(characterCount==0||(!words&&characterCount!=1))continue;
            var chars=new string[characterCount];int characterIndex=0;
            foreach(var rune in row.Key.Text.EnumerateRunes())
            {if(!characters.TryGetValue(rune.Value,out var c))characters[rune.Value]=c=rune.ToString();chars[characterIndex++]=c;}
            string code=row.Key.Code.ToLowerInvariant();
            if(code.Length==0)throw new InvalidDataException("Invalid table code");
            foreach(char c in code)if(c<'a'||c>'z')throw new InvalidDataException("Invalid table code");
            var modelTokens=tokens==null?null:tokens[(row.Key.Text,code)];
            if(modelTokens!=null&&modelTokens.Length!=chars.Length)throw new InvalidDataException("Token alignment mismatch");
            var entry=new Entry(row.Key.Text,code,Math.Max(0,row.Value),chars,modelTokens,userEntries?.Contains((row.Key.Text,code))==true?9:0);
            source.Add((entry,source.Count));MaximumLength=Math.Max(MaximumLength,code.Length);Count++;
        }
        source.Sort(static (a,b)=>{int c=StringComparer.Ordinal.Compare(a.Entry.Code,b.Entry.Code);return c!=0?c:a.Order.CompareTo(b.Order);});
        foreach(var item in source)builder.AddSorted(item.Entry.Code.AsSpan(),item.Entry,item.Order);
        codes=builder.Build(completion:true);ordered=codes.OrderedValues();
    }
    internal static Lexicon Load(string path,bool words=true,IReadOnlyDictionary<(string Text,string Code),string[]>? tokens=null)=>new(ReadRows(path),words,tokens);
    private static IEnumerable<(string Text,string Code,int Frequency)> ReadRows(string path)
    {
        foreach(string line in File.ReadLines(path))
        {
            if(line.Length==0||line.StartsWith('#'))continue;
            yield return ParseRow(line);
        }
    }
    private static (string Text,string Code,int Frequency) ParseRow(string line)
    {
        Span<Range> fields=stackalloc Range[3];int count=0,position=0;
        while(position<line.Length)
        {
            if(char.IsWhiteSpace(line[position])){position++;continue;}
            int start=position;while(position<line.Length&&!char.IsWhiteSpace(line[position]))position++;
            if(count==3)throw new InvalidDataException("Invalid pinyin table row");fields[count++]=start..position;
        }
        if(count!=3)throw new InvalidDataException("Invalid pinyin table row");
        return(line[fields[0]],line[fields[1]],int.Parse(line.AsSpan()[fields[2]],CultureInfo.InvariantCulture));
    }
    internal IEnumerable<(Entry Entry,int End)> Edges(string raw,int start)
    {
        if(baseline!=null)
        {
            var original=baseline.Edges(raw,start).Select(e=>(Entry:overrides!.GetValueOrDefault((e.Entry.Text,e.Entry.Code),e.Entry),e.End));
            var extra=OwnEdges(raw,start).Where(e=>!baseline.Edges(e.Entry.Code,0).Any(b=>b.End==e.Entry.Code.Length&&b.Entry.Text==e.Entry.Text));
            foreach(var edge in original.Concat(extra).OrderBy(e=>e.End))yield return edge;
            yield break;
        }
        foreach(var edge in OwnEdges(raw,start))yield return edge;
    }
    private IEnumerable<(Entry Entry,int End)> OwnEdges(string raw,int start)
    {
        int node=0;
        for(int i=start;i<raw.Length&&codes.TryNext(node,raw[i],out node);i++)
        {
            // Span cannot survive a yield; enumerate indices through a helper.
            foreach(var entry in NodeEntries(node))yield return(entry,i+1);
        }
    }
    private IEnumerable<Entry> NodeEntries(int node)
    {
        int count=codes.Values(node).Length;
        for(int i=0;i<count;i++)yield return codes.Values(node)[i];
    }
    internal bool ProperPrefix(string raw)
    {
        if(baseline?.ProperPrefix(raw)==true)return true;
        int node=0;foreach(char c in raw)if(!codes.TryNext(node,c,out node))return false;
        return codes.HasChildren(node);
    }
    private IEnumerable<Entry> OwnCompletions(string tail,CancellationToken cancellation)
    {
        int node=0;foreach(char c in tail)if(!codes.TryNext(node,c,out node))return [];
        return codes.Walk(node,tail.Length,cancellation).Where(e=>e.Code.Length>tail.Length&&e.Tokens!=null&&tail.Length>=e.CompletionPrefix);
    }
    internal IEnumerable<Entry> Completions(string raw,int start,CancellationToken cancellation=default)
    {
        string tail=raw[start..];if(tail.Length==0||tail.Contains('\''))return [];
        var own=OwnCompletions(tail,cancellation);
        var all=baseline==null?own:baseline.OwnCompletions(tail,cancellation).Where(e=>!overrides!.ContainsKey((e.Text,e.Code))).Concat(own);
        return all.OrderByDescending(e=>e.Frequency).ThenBy(e=>e.Code,StringComparer.Ordinal).ThenBy(e=>e.Text,StringComparer.Ordinal).Take(2000);
    }
}
