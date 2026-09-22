using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TigerClaw.Pinyin;

namespace TigerClaw.Core
{
    // Offline Core scheduling/confirmation benchmark. Never owns production IPC,
    // registration or UI channels; all configuration belongs to the marked fixture.
    internal static class FullPinyinPerformanceProbe
    {
        internal static int Run(string root,string output)
        {
            if(!File.Exists(Path.Combine(root,"full-pinyin-test-fixture")))throw new InvalidOperationException("Expected isolated fixture");
            using var writer=new StreamWriter(new FileStream(output,FileMode.CreateNew,FileAccess.Write)){AutoFlush=true};
            var state=new CoreRuntimeState(root);state.Initialize();
            state.TrySetConfigValue("当前码表","虎爪全拼",out _,out _);
            var load=Stopwatch.StartNew();var resources=new PinyinResources(state.GetFullPinyinDirectory());
            writer.WriteLine(FormattableString.Invariant($"# load_ms={load.Elapsed.TotalMilliseconds:F3},{resources.LoadTimings}"));
            using var engine=new InputMethodEngine(state,fullPinyinResources:resources);
            var samples=new ConcurrentQueue<PinyinPerformanceSample>();
            engine.SetPinyinPerformanceCallback(samples.Enqueue);
            KeyEngineResult Key(int vk)=>engine.ProcessKey(vk,0,"down",false,false,false,false,false,true,1,false);
            writer.WriteLine("interval,input,key_return_max_ms,published,canceled,response_p95_ms,commit_ms");
            foreach(int interval in new[]{100,60,30})
                foreach(string input in new[]{"shiyixia","nihaoma","changyongzi","yaoyouhuahaojiu","jintianxiawuwomenyiqiquchifan","woxiangceshiyixiapinyinshurufa","nh","windowsbanben"})
                {
                    samples.Clear();double keyMaximum=0;var clock=Stopwatch.StartNew();int count=0;
                    foreach(char c in input)
                    {
                        long start=Stopwatch.GetTimestamp();Key(char.ToUpperInvariant(c));keyMaximum=Math.Max(keyMaximum,Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        int wait=(int)(++count*interval-clock.Elapsed.TotalMilliseconds);if(wait>0)Thread.Sleep(wait);
                    }
                    if(!SpinWait.SpinUntil(()=>!engine.IsSentenceDecodePending,30000))throw new TimeoutException("Core generation stalled");
                    var menu=engine.GetUiSnapshot(9);if(menu.Candidates.Length==0)throw new InvalidOperationException("Missing candidates");
                    string expected=menu.Candidates[0];long confirm=Stopwatch.GetTimestamp();var commit=Key(0x20);double commitMs=Stopwatch.GetElapsedTime(confirm).TotalMilliseconds;
                    if(commit.TextToOutput!=expected)throw new InvalidOperationException("Commit did not match visible first candidate");
                    var all=samples.ToArray();var times=all.Where(s=>!s.Canceled).Select(s=>(s.Published-s.Submitted)*1000.0/Stopwatch.Frequency).Order().ToArray();
                    double p95=times.Length==0?0:times[(int)((times.Length-1)*.95)];
                    writer.WriteLine(FormattableString.Invariant($"{interval},{input},{keyMaximum:F3},{times.Length},{all.Count(s=>s.Canceled)},{p95:F3},{commitMs:F3}"));
                }
            engine.SetPinyinPerformanceCallback(null);
            writer.WriteLine("# isolated_core=true,physical_tsf=false");
            return 0;
        }
    }
}
