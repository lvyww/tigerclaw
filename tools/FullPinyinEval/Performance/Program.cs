using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TigerClaw.Pinyin;
using Decoder=TigerClaw.Pinyin.Decoder;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string mode=args[0];
using var writer=new StreamWriter(new FileStream(args[2],FileMode.CreateNew,FileAccess.Write)){AutoFlush=true};
var clock=Stopwatch.StartNew();
using var resources=new PinyinResources(args[1]);
writer.WriteLine($"# load_ms={clock.Elapsed.TotalMilliseconds:F3},entries={resources.Lexicon.Count}");
writer.WriteLine("# "+resources.LoadTimings);
var options=PinyinSpellingOptions.Abbreviations|PinyinSpellingOptions.Aliases;
using var decoder=new Decoder(resources.Lexicon,resources.Model,200,spellingOptions:options);
var session=new PinyinSession();
if(mode=="profile")decoder.EnableProfiling();
string[] examples=["shiyixia","nihaoma","changyongzi","yaoyouhuahaojiu","jintianxiawuwomenyiqiquchifan","woxiangceshiyixiapinyinshurufa","nh","windowsbanben"];
void Menu(string raw, DecodeResult decoded){session.Reset(raw);session.Apply(session.Generation,decoded,lexicon:resources.Lexicon,spelling:options,preferences:resources.Preferences,deferMenu:true);var extras=resources.English.Candidates(raw,null).ToList();string? emoji=session.Sentences.Length>0?PinyinTools.Emoji(session.Sentences[0].Text):null;if(emoji!=null)extras.Add(new(new(emoji,0,0,[]),emoji,raw.Length,true,Literal:true,Annotation:"表情"));session.SetExtras(extras,!session.Sentences.Any(c=>c.Segments.All(s=>s.SpellingPenalty==0&&!s.Incomplete)));}
string Digest(DecodeResult decoded){using var memory=new MemoryStream();using(var b=new BinaryWriter(memory,Encoding.UTF8,true)){b.Write(decoded.Consumed);b.Write(decoded.Tail);b.Write(decoded.PreferExactSpelling);b.Write(decoded.Candidates.Length);foreach(var c in decoded.Candidates){b.Write(c.Text);b.Write(c.Score);b.Write(c.Frequency);b.Write(c.Segments.Length);foreach(var s in c.Segments){b.Write(s.Code);b.Write(s.Text);b.Write(s.Start);b.Write(s.End);b.Write(s.SpellingPenalty);b.Write(s.Incomplete);b.Write(s.WordBonus);b.Write(s.Tokens?.Length??-1);foreach(var t in s.Tokens??[])b.Write(t);b.Write(s.RawEnds?.Length??-1);foreach(int end in s.RawEnds??[])b.Write(end);}}b.Write(session.Choices.Length);foreach(var c in session.Choices){b.Write(c.Text);b.Write(c.Consumed);b.Write(c.Whole);b.Write(c.Literal);b.Write(c.Annotation);}}return Convert.ToHexString(SHA256.HashData(memory.ToArray()));}
if(mode=="sharing"){
 using var other=new PinyinResources(args[3]);
 if(!ReferenceEquals(resources.Baseline,other.Baseline)||!ReferenceEquals(resources.Model,other.Model)||!ReferenceEquals(resources.Reranker,other.Reranker))throw new Exception("Physical resources were not shared");
 using var another=new Decoder(other.Lexicon,other.Model,200,spellingOptions:options);
 var jobs=new[]{Task.Run(()=>resources.Rank(decoder.Decode("nihao"))),Task.Run(()=>other.Rank(another.Decode("nihao")))};
 Task.WaitAll(jobs);
 string Signature(DecodeResult value)=>string.Join('|',value.Candidates.Select(c=>c.Text+":"+c.Score.ToString("R",CultureInfo.InvariantCulture)));
 if(Signature(jobs[0].Result)!=Signature(jobs[1].Result))throw new Exception("Concurrent shared query mismatch");
 resources.Dispose();
 if(other.Rank(another.Decode("nihaoma")).Candidates.Length==0)throw new Exception("Shared resources prematurely released");
 other.Dispose();
 if(decoder.Decode("shiyixia").Candidates.Length==0)throw new Exception("Query mapping lease did not survive owner disposal");
 writer.WriteLine("shared_bundle=true,concurrent_queries=true,lease_after_owner_disposal=true");
}else if(mode=="scores"){
 var model=(Kenlm)resources.Model;
 var ids=resources.Lexicon.Entries.SelectMany(e=>e.Tokens??e.Characters).Distinct().Take(2500).Select(model.Index).Distinct().ToArray();
 var random=new Random(20260922);long checks=0;
 for(int round=0;round<100;round++){
  var contexts=Enumerable.Range(0,200).Select(_=>(ids[random.Next(ids.Length)],ids[random.Next(ids.Length)])).Append((model.Index("\u0002"),model.Index("\u0002"))).ToArray();
  using var batch=model.CreateContexts(contexts);var row=new double[contexts.Length];
  foreach(uint target in ids.Take(200)){batch.Score(target,row);for(int i=0;i<contexts.Length;i++){
    double expected=model.LogProbability(contexts[i].Item1,contexts[i].Item2,target)+2.0;
    if(BitConverter.DoubleToInt64Bits(expected)!=BitConverter.DoubleToInt64Bits(row[i]))throw new Exception($"Native batch mismatch {contexts[i]} {target}: {expected:R}/{row[i]:R}");checks++;
  }}
 }
 writer.WriteLine($"exact_scores={checks}");
}else if(mode=="golden"){
 int n=0;foreach(string line in File.ReadLines(args[3])){using var json=JsonDocument.Parse(line);var r=json.RootElement;if(r.GetProperty("split").GetString()!="test")continue;string raw=r.GetProperty("code").GetString()!,id=r.GetProperty("id").GetString()!;decoder.Reset();var decoded=resources.Rank(decoder.Decode(raw,50,completeLastSyllable:true));Menu(raw,decoded);writer.WriteLine($"full\t{id}\t{raw}\t{Digest(decoded)}");if(++n%100==0)Console.WriteLine(n);}
 foreach(string input in examples){decoder.Reset();for(int i=1;i<=input.Length;i++){string raw=input[..i];var decoded=resources.Rank(decoder.Decode(raw,50,completeLastSyllable:true));Menu(raw,decoded);writer.WriteLine($"append\t{input}\t{raw}\t{Digest(decoded)}");}for(int i=input.Length-1;i>0;i--){string raw=input[..i];var decoded=resources.Rank(decoder.Decode(raw,50,completeLastSyllable:true));Menu(raw,decoded);writer.WriteLine($"erase\t{input}\t{raw}\t{Digest(decoded)}");}}
}else{
 writer.WriteLine("round,input,raw,decode_ms,rank_ms,menu_ms,allocated_bytes,expansions,top,lookup_ms,score_ms,loop_ms,score_calls,native_misses,native_ms_sampled");for(int round=0;round<3;round++)foreach(string input in examples){decoder.Reset();for(int i=1;i<=input.Length;i++){string raw=input[..i];long before=GC.GetAllocatedBytesForCurrentThread();clock.Restart();var decoded=decoder.Decode(raw,50,completeLastSyllable:true);double decode=clock.Elapsed.TotalMilliseconds;clock.Restart();decoded=resources.Rank(decoded);double rank=clock.Elapsed.TotalMilliseconds;clock.Restart();Menu(raw,decoded);writer.WriteLine($"{round},{input},{raw},{decode:F3},{rank:F3},{clock.Elapsed.TotalMilliseconds:F3},{GC.GetAllocatedBytesForCurrentThread()-before},{decoded.Expansions},{session.Choices.FirstOrDefault()?.Text},{decoder.ProfileMetrics.LookupMs:F3},{decoder.ProfileMetrics.ScoreMs:F3},{decoder.ProfileMetrics.LoopMs:F3},{decoder.ProfileMetrics.Calls},{decoder.ProfileMetrics.Misses},{decoder.ProfileMetrics.NativeMs:F3}");}}}
GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var process=Process.GetCurrentProcess();writer.WriteLine($"# private_bytes={process.PrivateMemorySize64},working_set={process.WorkingSet64},peak_working_set={process.PeakWorkingSet64}");
