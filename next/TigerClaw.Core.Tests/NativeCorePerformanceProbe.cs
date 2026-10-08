using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        // Explicit isolated fixture; never production endpoints or startup registry.
        private static int RunNativeCorePerformanceProbe(string executable, string root, string output)
        {
            string name="TigerClaw.Core.Native.Test.Perf."+Guid.NewGuid().ToString("N");
            var start=new ProcessStartInfo(executable) {UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            foreach(var arg in new[]{"--serve-isolated",root,name}) start.ArgumentList.Add(arg);
            var startup=Stopwatch.StartNew(); using var host=Process.Start(start);
            var stderr=host.StandardError.ReadToEndAsync();
            try
            {
                var ready=host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
                if(ready==null || !ready.Contains("ready")) throw new InvalidOperationException("Host not ready");
                double startupMs=startup.Elapsed.TotalMilliseconds;
                using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);pipe.Connect(5000);
                using var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
                using var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) {AutoFlush=true};
                using var map=MemoryMappedFile.OpenExisting(@"Local\"+name+".UiState.v1.Snapshot.v2");
                using var view=map.CreateViewAccessor();using var gate=Mutex.OpenExisting(@"Local\"+name+".UiState.v1.Snapshot.v2.Lock");
                JsonElement Send(object request)
                {
                    writer.WriteLine(JsonSerializer.Serialize(request));
                    var line=reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    using var json=JsonDocument.Parse(line);return json.RootElement.Clone();
                }
                long seq=0;
                JsonElement Key(int vk)=>Send(new{type="key",vk,action="down",seq=++seq,client_session="perf",event_id=seq.ToString(),caret_x=10,caret_y=20});
                object Idle()
                {
                    Thread.Sleep(300);host.Refresh();var cpu=host.TotalProcessorTime;long revision=view.ReadInt64(0);
                    var time=Stopwatch.StartNew();Thread.Sleep(3000);host.Refresh();
                    return new{wallMs=time.Elapsed.TotalMilliseconds,cpuMs=(host.TotalProcessorTime-cpu).TotalMilliseconds,uiPublications=view.ReadInt64(0)-revision,privateBytes=host.PrivateMemorySize64,workingSet=host.WorkingSet64};
                }
                var idle=Idle();
                foreach(char c in "tuja") Key(char.ToUpperInvariant(c));Thread.Sleep(500);
                var composingIdle=Idle();Key(32);
                var keys=new List<double>();var commits=new List<double>();var outputs=new List<string>();
                for(int round=0;round<70;round++)
                {
                    string code=new[]{"tuja","tlleo","tujatuja"}[round%3];
                    foreach(char c in code)
                    {
                        var time=Stopwatch.StartNew();Key(char.ToUpperInvariant(c));
                        if(round>=10) keys.Add(time.Elapsed.TotalMilliseconds);
                    }
                    var commitTime=Stopwatch.StartNew();var response=Key(32);
                    if(round>=10) {commits.Add(commitTime.Elapsed.TotalMilliseconds);outputs.Add(response.GetProperty("commit_text").GetString());}
                }
                object Stats(List<double> values)
                {
                    var a=values.OrderBy(x=>x).ToArray();return new{count=a.Length,p50=a[(int)Math.Floor((a.Length-1)*.50)],p95=a[(int)Math.Ceiling((a.Length-1)*.95)],max=a.Last()};
                }
                File.WriteAllText(output,JsonSerializer.Serialize(new{startupMs,idle,composingIdle,keyRoundtripMs=Stats(keys),spaceRoundtripMs=Stats(commits),outputs,scope="isolated Core pipe; no Overlay, no neural, no learning, warm fixed cases; not physical typing"},new JsonSerializerOptions{WriteIndented=true}));
                Send(new{type="exit_core"});if(!host.WaitForExit(5000)) throw new TimeoutException("Host shutdown");
                return host.ExitCode;
            }
            finally {if(!host.HasExited){host.Kill(true);host.WaitForExit();}File.WriteAllText(output+".stderr",stderr.GetAwaiter().GetResult());}
        }
    }
}
