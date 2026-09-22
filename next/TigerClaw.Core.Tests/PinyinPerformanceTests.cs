using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Pinyin;
using Decoder=TigerClaw.Pinyin.Decoder;

namespace TigerClaw.Core.Tests;
internal static partial class Program
{
    private sealed class IndexedTestPinyinModel : IPinyinLanguageModel,IIndexedPinyinLanguageModel
    {
        internal int StringCalls,IntegerCalls;
        public uint Index(string token)=>token switch {"\u0002"=>1,"\u0003"=>2,_=>unchecked((uint)token.Sum(c=>(int)c)%7)};
        public double LogProbability(string a,string b,string c){StringCalls++;return Value(Index(a),Index(b),Index(c));}
        public double LogProbability(uint a,uint b,uint c){IntegerCalls++;return Value(a,b,c);}
        internal static double Value(uint a,uint b,uint c)=>-(a*13+b*3+c+1)*0.03;
    }
    private sealed class StringTestPinyinModel(IndexedTestPinyinModel inner) : IPinyinLanguageModel
    {public double LogProbability(string a,string b,string c)=>inner.LogProbability(a,b,c);}
    private sealed class CancelTestPinyinModel : IPinyinLanguageModel
    {
        internal CancellationTokenSource Cancel;
        internal int Remaining=int.MaxValue;
        public double LogProbability(string a,string b,string c){if(--Remaining==0)Cancel?.Cancel();return c=="泥/ni"?-1:-0.1;}
    }
    private static string PinyinSignature(DecodeResult value)=>value.Consumed+"/"+value.Tail+"/"+value.PreferExactSpelling+"/"+
        string.Join("|",value.Candidates.Select(c=>c.Text+":"+c.Score.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+c.Frequency.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+
            string.Join(";",c.Segments.Select(s=>$"{s.Code},{s.Text},{s.Start},{s.End},{s.SpellingPenalty},{s.Incomplete},{s.WordBonus},{string.Join(',',s.Tokens??Array.Empty<string>())},{string.Join(',',s.RawEnds??Array.Empty<int>())}"))));
    private static void RunPinyinPerformanceTests()
    {
        var lexicon=PinyinTestLexicon();
        var indexed=new IndexedTestPinyinModel();var plain=new StringTestPinyinModel(indexed);
        foreach(var options in new[]{PinyinSpellingOptions.None,PinyinSpellingOptions.Abbreviations|PinyinSpellingOptions.Aliases,PinyinSpellingOptions.DoublePinyin})
        {
            using var fast=new Decoder(lexicon,indexed,200,spellingOptions:options);
            using var slow=new Decoder(lexicon,plain,200,spellingOptions:options);
            foreach(string raw in new[]{"n","ni","nih","niha","nihao","nihaom","nihaoma","nihao","nihaoma","xi'an","nh","nihc"})
                Equal(PinyinSignature(slow.Decode(raw,completeLastSyllable:true)),PinyinSignature(fast.Decode(raw,completeLastSyllable:true)),"indexed scorer exact equivalence including colliding token IDs "+raw);
        }
        True(indexed.IntegerCalls>0,"integer scoring path used");
        var words=new[]{PinyinUserWords.Validate("你好","ni hao"),PinyinUserWords.Validate("泥你","ni ni")};
        var overlay=PinyinUserWords.Merge(lexicon,words);
        var rows=lexicon.Entries.Select(e=>(e.Text,e.Code,e.Frequency)).Concat(new[]{("你好","nihao",1000),("泥你","nini",1000)});
        var tokens=lexicon.Entries.ToDictionary(e=>(e.Text,e.Code),e=>e.Tokens);
        tokens[("泥你","nini")]=new[]{"泥/ni","你/ni"};
        var rebuilt=new Lexicon(rows,tokens:tokens,userEntries:new HashSet<(string,string)>{("你好","nihao"),("泥你","nini")});
        Equal(string.Join('|',rebuilt.Entries.Select(e=>e.Text+e.Code+e.Frequency+e.Bonus)),string.Join('|',overlay.Entries.Select(e=>e.Text+e.Code+e.Frequency+e.Bonus)),"overlay preserves trie traversal and replacement");
        using(var a=new Decoder(overlay,plain,200,spellingOptions:PinyinSpellingOptions.Abbreviations))
        using(var b=new Decoder(rebuilt,plain,200,spellingOptions:PinyinSpellingOptions.Abbreviations))
            foreach(string raw in new[]{"ni","nih","nihao","nini","nn","nihaoma"})Equal(PinyinSignature(b.Decode(raw,completeLastSyllable:true)),PinyinSignature(a.Decode(raw,completeLastSyllable:true)),"overlay decode "+raw);
        foreach(var dictionary in new[]{lexicon,overlay})foreach(string raw in new[]{"n","ni","nih","x","xi","xi'","nv"})
        {
            var expected=dictionary.Entries.Where(e=>!raw.Contains('\'')&&e.Code.StartsWith(raw,StringComparison.Ordinal)&&e.Code.Length>raw.Length&&e.Tokens!=null&&raw.Length>e.Code.Length-Lexicon.RawReading(e.Tokens[^1]).Length)
                .OrderByDescending(e=>e.Frequency).ThenBy(e=>e.Code,StringComparer.Ordinal).ThenBy(e=>e.Text,StringComparer.Ordinal).Take(2000).Select(e=>e.Text+e.Code);
            Equal(string.Join('|',expected),string.Join('|',dictionary.Completions(raw,0).Select(e=>e.Text+e.Code)),"pruned completion exact "+raw);
        }
        var cancelModel=new CancelTestPinyinModel();using var canceled=new Decoder(lexicon,cancelModel,200);
        canceled.Decode("ni");using var source=new CancellationTokenSource();cancelModel.Cancel=source;cancelModel.Remaining=2;
        bool caught=false;try{canceled.Decode("nihaoma",cancellation:source.Token);}catch(OperationCanceledException){caught=true;}
        True(caught,"cancel occurs during expansion");cancelModel.Remaining=int.MaxValue;cancelModel.Cancel=null;
        using var reference=new Decoder(lexicon,new CancelTestPinyinModel(),200);reference.Decode("ni");
        Equal(PinyinSignature(reference.Decode("nihao")),PinyinSignature(canceled.Decode("nihao")),"cancel transaction restores previous complete buckets");
        var worker=new LatestPinyinWorker();var entered=new ManualResetEventSlim();var release=new TaskCompletionSource();var done=new ManualResetEventSlim();var ran=new List<int>();
        worker.Submit(async()=>{lock(ran)ran.Add(0);entered.Set();await release.Task;});True(entered.Wait(5000),"latest worker entered");
        for(int i=1;i<=50;i++){int id=i;worker.Submit(()=>{lock(ran)ran.Add(id);if(id==50)done.Set();return Task.CompletedTask;});}
        release.SetResult();True(done.Wait(5000),"latest worker completed");worker.Stop();Equal("0,50",string.Join(',',ran),"pending requests coalesce");
        string file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try
        {
            File.WriteAllText(file,"[{\"text\":\"你\",\"code\":\"ni\",\"tokens\":[\"你/ni\"]},{\"text\":\"你你\",\"code\":\"nini\",\"tokens\":[\"你/ni\",\"你/ni\"]}]",new System.Text.UTF8Encoding(true));
            var map=PinyinTokenReader.Read(file);True(ReferenceEquals(map[("你","ni")][0],map[("你你","nini")][1]),"streamed BOM token table pools tokens");
        }finally{File.Delete(file);}
        Console.WriteLine("Pinyin integer scoring/compact lookup/user overlay/cancellation/latest-worker tests passed.");
    }
}
