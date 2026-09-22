using TigerClaw.Core;
namespace FullPinyinEval;
static class JointTests
{
    sealed class Adversarial : ISentenceLanguageModel
    {
        public bool HasObservedBigram(string a,string b)=>false;
        public double LogProbability(string a,string b,string c)=>c switch{
            "甲/aa"=>-2,"乙/bc"=>-2,"丙/de" when b=="乙/abc"=>-100,_=>0};
    }
    internal static void Run(string path)
    {
        var tokens=new Dictionary<(string,string),string[]>{
            [("甲乙","aabc")]=["甲/a","乙/abc"],[("甲","aa")]=["甲/aa"],
            [("乙","bc")]=["乙/bc"],[("丙","de")]=["丙/de"]};
        var lex=new Lexicon(new[]{("甲乙","aabc",1),("甲","aa",1),("乙","bc",1),("丙","de",1)},tokens:tokens);
        using var d=new Decoder(lex,new Adversarial(),200);
        var first=d.Decode("aabc").Candidates;
        Tests.Check(first.Length==1 && first[0].Segments[0].Tokens![0]=="甲/a","display deduplicates joint paths");
        var next=d.Decode("aabcde").Candidates[0];
        Tests.Check(next.Text=="甲乙丙" && next.Segments[0].Tokens![0]=="甲/aa","lower-ranked alternate reading state survives");
        Tests.Check(Math.Abs(next.Score-2)<1e-9,"joint state exhaustive score");
        Tests.Check(d.Decode("aabc").Candidates[0].Score==first[0].Score,"backspace restores reading state");
        Tests.Check(d.Decode("aabcde").Candidates[0].Score==next.Score,"reappend restores reading state");
        using var lm=new Kenlm(path);
        double Score(params string[] ts){string a="\u0002",b=a;double s=0;foreach(var c in ts){s+=lm.LogProbability(a,b,c);a=b;b=c;}return s+lm.LogProbability(a,b,"\u0003");}
        Tests.Check(lm.Index("行/hang")!=lm.Index("行/xing"),"distinct reading vocabulary ids");
        Tests.Check(lm.Index("行/invalidreading")==0,"explicit OOV mapping");
        Tests.Check(Math.Abs(Score("银/yin","行/hang")-(-4.30864182115*Math.Log(10)))<1e-5,"native incremental vs direct KenLM BOS/EOS and log base");
        Tests.Check(Score("银/yin","行/hang")>Score("银/yin","行/xing"),"bank reading");
        Tests.Check(Score("行/xing","走/zou")>Score("行/hang","走/zou"),"walking reading");
        Console.WriteLine("Joint read/state/cache/log-base/BOS/EOS tests: PASS");
    }
}
