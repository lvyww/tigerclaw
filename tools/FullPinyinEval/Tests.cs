using TigerClaw.Core;

namespace FullPinyinEval;
internal static class Tests
{
    internal static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); }
    private sealed class Model : ISentenceLanguageModel
    {
        public double LogProbability(string a, string b, string c) => -((a.Sum(x=>(int)x)*3L+b.Sum(x=>(int)x)*5L+c.Sum(x=>(int)x)*7L)%23)/5.0;
        public bool HasObservedBigram(string a, string b) => false;
    }
    internal static void Run()
    {
        var lexicon = new Lexicon(new[] { ("你","ni",100), ("尼","ni",2), ("好","hao",10), ("号","hao",1),
            ("你好","nihao",1000), ("西","xi",3), ("安","an",1), ("先","xian",4), ("啊","a",5), ("嗯","n",1), ("吕","lv",1) });
        var model = new Model(); using var decoder = new Decoder(lexicon,model,100000);
        int checks = 0;
        // Independent unpruned DFS oracle, deduplicated by output text.
        Candidate[] Oracle(string raw)
        {
            var all = new List<Candidate>();
            void Visit(int pos,string text,string a,string b,double score,double frequency,List<Segment> path)
            {
                if (pos==raw.Length) { all.Add(new(text,score+model.LogProbability(a,b,"\u0003"),frequency,path.ToArray()));return; }
                foreach(var edge in lexicon.Edges(raw,pos))
                {
                    double next=score;string x=a,y=b;
                    foreach(string z in edge.Entry.Characters){next+=model.LogProbability(x,y,z)+2;x=y;y=z;}
                    Visit(edge.End,text+edge.Entry.Text,x,y,next,frequency+Math.Log(1+edge.Entry.Frequency),
                        path.Append(new(edge.Entry.Code,edge.Entry.Text,pos,edge.End)).ToList());
                }
            }
            Visit(0,"","\u0002","\u0002",0,0,new());
            return all.GroupBy(x=>x.Text).Select(g=>g.OrderByDescending(x=>x.Score).ThenByDescending(x=>x.Frequency).First())
                .OrderByDescending(x=>x.Score).ThenByDescending(x=>x.Frequency).ThenBy(x=>x.Text,StringComparer.Ordinal).Take(50).ToArray();
        }
        var random=new Random(42); var codes=new[]{"ni","hao","xian","a","lv","n"};
        for(int n=0;n<100;n++)
        {
            string raw=string.Concat(Enumerable.Range(0,random.Next(1,5)).Select(_=>codes[random.Next(codes.Length)]));
            var expected=Oracle(raw);var actual=decoder.Decode(raw,50,false).Candidates;
            Check(expected.Length==actual.Length,"exhaustive cardinality");
            for(int i=0;i<expected.Length;i++){Check(expected[i].Text==actual[i].Text && Math.Abs(expected[i].Score-actual[i].Score)<1e-9,"exhaustive rank/score");checks++;}
            foreach(int width in new[]{1,5,200})
            {
                using var incremental=new Decoder(lexicon,model,width);using var fresh=new Decoder(lexicon,model,width);
                foreach(int len in Enumerable.Range(1,raw.Length).Concat(Enumerable.Range(0,raw.Length).Reverse()).Concat(Enumerable.Range(1,raw.Length)))
                {
                    var a=incremental.Decode(raw[..len]);var b=fresh.Decode(raw[..len],50,false);
                    Check(a.Consumed==b.Consumed && a.Tail==b.Tail && a.Candidates.Select(x=>(x.Text,x.Score)).SequenceEqual(b.Candidates.Select(x=>(x.Text,x.Score))),"incremental/backspace equivalence");checks++;
                }
            }
        }
        var ambiguous=decoder.Decode("xian",50,false).Candidates;
        Check(ambiguous.Any(x=>x.Text=="西安") && ambiguous.Any(x=>x.Text=="先"),"segmentation competition");
        var forced=decoder.Decode("xi'an",50,false).Candidates;
        Check(forced.Any(x=>x.Text=="西安") && !forced.Any(x=>x.Text=="先"),"forced boundary");
        Check(decoder.Decode("nihaoxi'an",50,false).Candidates.Any(x=>x.Text=="你好西安"),"separator preserves preceding words");
        Check(decoder.Decode("nihao",50,false).Candidates.Count(c=>c.Text=="你好")==1,"word/single dedup");
        Check(decoder.Decode("ani",50,false).Candidates.Any(c=>c.Text=="啊你"),"single-letter syllable inside sentence");
        Check(decoder.Decode("nih",50,false).Tail=="h","incomplete tail");
        Check(decoder.Decode("h",50,false).Tail=="h","initial incomplete syllable remains raw");
        var unicodeLexicon = new Lexicon(new[]{("𠀀", "he", 1), ("啊", "a", 1)});
        using var unicodeDecoder = new Decoder(unicodeLexicon,model,200);
        Check(unicodeDecoder.Decode("hea").Candidates[0].Segments.Length == 2,"Unicode scalar segmentation");
        Check(decoder.Decode("nh",50,false).Candidates.All(c=>c.Text!="你好"),"no initials expansion");
        Check(decoder.Decode("NIHAO",50,false).Candidates.Select(x=>x.Text).SequenceEqual(decoder.Decode("nihao",50,false).Candidates.Select(x=>x.Text)),"case normalization");
        foreach(string raw in new[]{"ni3hao","ni hao","'ni","ni''hao","nǐ"})
        {
            bool rejected=false;try{decoder.Decode(raw);}catch(ArgumentException){rejected=true;}Check(rejected,"invalid input");
        }
        Console.WriteLine($"FullPinyinEval tests: PASS ({checks} oracle/cache comparisons plus boundary fixtures)");
    }
}
