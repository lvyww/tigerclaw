using System.Text.Json;
using System.Runtime.InteropServices;
using TigerClaw.Pinyin;
record TokenRow(string Text,string Code,string[] Tokens);
record Row(string Id,string Text,string Code,string Split,string Source);
static class Program{
 [DllImport("higherorder")] static extern IntPtr ho_load([MarshalAs(UnmanagedType.LPUTF8Str)]string path);
 [DllImport("higherorder")] static extern double ho_score(IntPtr p,[MarshalAs(UnmanagedType.LPUTF8Str)]string text,out uint oov);
 [DllImport("higherorder")] static extern void ho_free(IntPtr p);
 static void Main(string[] args){
  var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
  using var lm=new Kenlm(args[0]);
  var tokens=JsonSerializer.Deserialize<TokenRow[]>(File.ReadAllText(args[2]),json)!.ToDictionary(r=>(r.Text,r.Code),r=>r.Tokens);
  var lex=Lexicon.Load(args[1],tokens:tokens);
  using var inc=new Decoder(lex,lm,200);using var fresh=new Decoder(lex,lm,200);
  int count=0;
  void Check(string code){var a=inc.Decode(code);var b=fresh.Decode(code,incremental:false);
   if(a.Consumed!=b.Consumed||a.Candidates.Length!=b.Candidates.Length)throw new Exception("count/consumption");
   for(int i=0;i<a.Candidates.Length;i++)if(a.Candidates[i].Text!=b.Candidates[i].Text||Math.Abs(a.Candidates[i].Score-b.Candidates[i].Score)>1e-8)throw new Exception("incremental mismatch");count++;
  }
  foreach(var line in File.ReadLines(args[3]).Take(8)){
   var row=JsonSerializer.Deserialize<Row>(line,json)!;inc.Reset();
   for(int i=1;i<=row.Code.Length;i++)Check(row.Code[..i]);
   for(int i=row.Code.Length-1;i>=0;i--)Check(row.Code[..i]);
   Check(row.Code);Check(row.Code); // retype and same-input cache
  }
  foreach(var s in new[]{"yaoyouhuahaojiu","yao'you'hua'hao'jiu","xianzaiyouhuameidifangshuoqu","nihaoshijie",""})Check(s);
  var native=ho_load(args[0]);if(native==IntPtr.Zero)throw new Exception("native load");
  int prefixChecks=0;
  try{foreach(var line in File.ReadLines(args[3]).Take(8)){
   var row=JsonSerializer.Deserialize<Row>(line,json)!;var original=fresh.Decode(row.Code,incremental:false).Candidates[0];
   var segments=original.Segments.Take(4).ToArray();var prefix=new Candidate(string.Concat(segments.Select(x=>x.Text)),0,0,segments);
   foreach(var candidate in fresh.Decode(row.Code,incremental:false,prefix:prefix).Candidates){
    if(!candidate.Text.StartsWith(prefix.Text,StringComparison.Ordinal))throw new Exception("prefix mismatch");
    var ts=candidate.Segments.SelectMany(x=>x.Tokens!).ToArray();var score=ho_score(native,string.Join(' ',ts),out _)*Math.Log(10)+2*ts.Length;
    if(Math.Abs(score-candidate.Score)>1e-8)throw new Exception("prefix context scoring mismatch");prefixChecks++;
   }
  }}finally{ho_free(native);}
  Console.WriteLine($"PASS {prefixChecks} locked-prefix candidates vs independent native score");
  Console.WriteLine($"PASS {count} incremental/full Top50 comparisons; order={lm.Order}");
 }
}
