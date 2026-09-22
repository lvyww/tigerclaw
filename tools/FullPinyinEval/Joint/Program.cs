using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using TigerClaw.Core;
using FullPinyinEval;
using Decoder=TigerClaw.Pinyin.Decoder;
record Row(string Id,string Text,string Code,string Split,string Source);
record TokenRow(string Text,string Code,string[] Tokens);
static class Program
{
    static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    static string Dump(object v)=>JsonSerializer.Serialize(v,Json);
    static string Hash(string p){using var f=File.OpenRead(p);return Convert.ToHexString(SHA256.HashData(f));}
    static int Main(string[] args){try{Run(args);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
    static void Run(string[] args)
    {
        if(args[0]=="verify-reranker"){RerankerVerification.Run(args);return;}
        if(args[0]=="test"){Tests.Run();JointTests.Run(args[1]);return;}
        if(args[0]=="scores")
        {
            using var lm=new Kenlm(args[1]);using var output=new StreamWriter(args[3]);
            foreach(var line in File.ReadLines(args[2]))
            {
                var f=line.Split('\t');if(f[1]!="sentence")continue;
                string a="\u0002",b=a;double score=0;int oov=0;
                foreach(var c in f[2].Split(' ')){score+=lm.LogProbability(a,b,c);oov+=lm.Index(c)==0?1:0;a=b;b=c;}
                score+=lm.LogProbability(a,b,"\u0003");output.WriteLine(Dump(new{id=f[0],score,oov}));
            }
            return;
        }
        // decode|bench type model table token-map cases output [worker workers] [--beam N]
        int beam=200;
        string spellingName = "exact";
        if(args.Length>=2 && args[^2]=="--spelling") { spellingName=args[^1]; args=args[..^2]; }
        var spelling = spellingName switch {
            "exact" => TigerClaw.Pinyin.PinyinSpellingOptions.None,
            "full" => TigerClaw.Pinyin.PinyinSpellingOptions.Abbreviations | TigerClaw.Pinyin.PinyinSpellingOptions.Aliases,
            "tolerant" => TigerClaw.Pinyin.PinyinSpellingOptions.Abbreviations | TigerClaw.Pinyin.PinyinSpellingOptions.Aliases | TigerClaw.Pinyin.PinyinSpellingOptions.Typos,
            "xiaohe" => TigerClaw.Pinyin.PinyinSpellingOptions.DoublePinyin,
            _ => throw new ArgumentException("unknown spelling")
        };
        if(args.Length>=2 && args[^2]=="--beam")
        {
            beam=int.Parse(args[^1]);args=args[..^2];
            if(beam<1)throw new ArgumentOutOfRangeException(nameof(beam));
        }
        string mode=args[0],type=args[1];
        if(mode!="decode"&&mode!="bench")throw new ArgumentException("unknown mode");
        if(type!="m5"&&type!="char"&&type!="joint")throw new ArgumentException("unknown model");
        IPinyinLanguageModel model=type=="m5"?SentenceNgramModel.Load(args[2]):new Kenlm(args[2]);
        using var owned=(IDisposable)model;
        var tokens=type=="joint"?JsonSerializer.Deserialize<TokenRow[]>(File.ReadAllText(args[4]),Json)!.ToDictionary(r=>(r.Text,r.Code),r=>r.Tokens):null;
        var lexicon=Lexicon.Load(args[3],tokens:tokens);
        using var decoder=new Decoder(lexicon,model,beam,spellingOptions:spelling);
        var all=File.ReadLines(args[5]).Select(l=>JsonSerializer.Deserialize<Row>(l,Json)!).ToArray();
        string outputPath=args[6];
        if(File.Exists(outputPath))throw new IOException("Use fresh output; never overwrite evidence");
        File.WriteAllText(outputPath+".manifest.json",Dump(new{type,spelling=spellingName,model=Hash(args[2]),table=Hash(args[3]),tokenMap=Hash(args[4]),cases=Hash(args[5]),executable=Hash(typeof(Program).Assembly.Location),decoderAssembly=Hash(typeof(Decoder).Assembly.Location),beam,reward=2,naturalLog=true}));
        using var writer=new StreamWriter(outputPath);
        if(mode=="bench")
        {
            foreach(var c in all.OrderBy(c=>c.Id).Take(100))
            {
                decoder.Reset();
                for(int i=1;i<=c.Code.Length;i++){var watch=Stopwatch.StartNew();var r=decoder.Decode(c.Code[..i]);writer.WriteLine(Dump(new{c.Id,direction="append",keys=i,ms=watch.Elapsed.TotalMilliseconds,r.Expansions}));}
                for(int i=c.Code.Length-1;i>=0;i--){var watch=Stopwatch.StartNew();var r=decoder.Decode(c.Code[..i]);writer.WriteLine(Dump(new{c.Id,direction="backspace",keys=i,ms=watch.Elapsed.TotalMilliseconds,r.Expansions}));}
            }
        }
        else
        {
            int worker=int.Parse(args[7]),workers=int.Parse(args[8]),count=0;
            foreach(var c in all.Where(c=>c.Split=="test").Where((c,i)=>i%workers==worker))
            {
                var watch=Stopwatch.StartNew();var r=decoder.Decode(c.Code,50,false);
                writer.WriteLine(Dump(new{c.Id,c.Text,c.Code,c.Split,c.Source,type,ms=watch.Elapsed.TotalMilliseconds,r.Consumed,r.Tail,r.Expansions,candidates=r.Candidates,rank=Array.FindIndex(r.Candidates,x=>x.Text==c.Text)+1}));
                if(++count%100==0){writer.Flush();Console.WriteLine($"{type} worker={worker} rows={count}");}
            }
        }
        Console.WriteLine($"complete type={type} peakBytes={Process.GetCurrentProcess().PeakWorkingSet64}");
    }
}
