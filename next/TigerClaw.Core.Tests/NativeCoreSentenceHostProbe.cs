using System;
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
        private static int RunNativeCoreSentenceHostProbe(string executable, string root, string model, string table)
        {
            if (string.IsNullOrWhiteSpace(root) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
                throw new ArgumentException("A new isolated fixture directory is required");
            string schema = Path.Combine(root, "tables", "Test整句");
            Directory.CreateDirectory(schema); Directory.CreateDirectory(Path.Combine(root, "Models"));
            File.Copy(table, Path.Combine(schema, "table.txt"));
            File.Copy(model, Path.Combine(root, "Models", "sentence-fivegram-mobile.bin"));
            File.WriteAllText(Path.Combine(root,"config.txt"),
                "码表存储位置\ttables\n当前码表\tTest整句\n整句神经重排\t否\n整句自动提前上屏\t否\n高频字仅使用最优码组句\t0\n整句Tab自学习\t是\n",
                new UTF8Encoding(false));
            string pipeName = "TigerClaw.Core.Native.Test.Sentence." + Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(executable) { UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            foreach (var arg in new[] { "--serve-isolated", root, pipeName }) start.ArgumentList.Add(arg);
            using var host = Process.Start(start);
            var stderr = host.StandardError.ReadToEndAsync();
            using var trace = new StreamWriter(Path.Combine(root,"wire.jsonl"),false,new UTF8Encoding(false)) { AutoFlush=true };
            try
            {
                string ready=host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                if (ready==null || !ready.Contains("ready")) throw new InvalidOperationException("Sentence host did not start");
                NamedPipeClientStream pipe=null; StreamReader reader=null; StreamWriter writer=null;
                void Connect()
                {
                    writer?.Dispose(); reader?.Dispose(); pipe?.Dispose();
                    pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous); pipe.Connect(5000);
                    reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
                    writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) { AutoFlush=true };
                }
                long sequence=0;
                JsonElement Send(object request, bool notification=false)
                {
                    string json=JsonSerializer.Serialize(request); trace.WriteLine("{\"request\":"+json+"}"); writer.WriteLine(json);
                    if (notification) return default;
                    string response=reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    trace.WriteLine("{\"response\":"+response+"}");
                    using var document=JsonDocument.Parse(response); return document.RootElement.Clone();
                }
                JsonElement Key(int vk,string id=null) => Send(new { type="key", vk, action="down", seq=++sequence,
                    client_session="sentence-host",event_id=id ?? sequence.ToString(), learning_ack_version=1, caret_x=10, caret_y=20 });
                Connect();
                using var map=MemoryMappedFile.OpenExisting(@"Local\"+pipeName+".UiState.v1.Snapshot.v2");
                using var view=map.CreateViewAccessor(); using var gate=Mutex.OpenExisting(@"Local\"+pipeName+".UiState.v1.Snapshot.v2.Lock");
                JsonElement Ui()
                {
                    if (!gate.WaitOne(2000)) throw new TimeoutException("UI mutex");
                    try
                    {
                        int length=view.ReadInt32(16); if(length<=0 || length>131052) throw new InvalidOperationException("UI frame size");
                        byte[] bytes=new byte[length];view.ReadArray(20,bytes,0,length);
                        using var json=JsonDocument.Parse(bytes);return json.RootElement.Clone();
                    }
                    finally { gate.ReleaseMutex(); }
                }
                JsonElement WaitUi(Func<JsonElement,bool> accept)
                {
                    var timeout=Stopwatch.StartNew();
                    while (timeout.ElapsedMilliseconds<15000) { var state=Ui(); if(accept(state)) return state; Thread.Sleep(10); }
                    File.WriteAllText(Path.Combine(root,"timeout-ui.json"),Ui().GetRawText());
                    throw new TimeoutException("Expected sentence UI not published");
                }
                string journal=Path.Combine(schema,"自学习-虎爪.txt");
                int JournalCount()
                {
                    try { return File.Exists(journal) ? File.ReadAllLines(journal).Count(line=>line.StartsWith("学习\t",StringComparison.Ordinal)) : 0; }
                    catch (IOException) { return -1; }
                }
                void Ack(string receipt,bool applied,string client="sentence-host") => Send(new {type="learning_commit",client_session=client,learning_receipt=receipt,applied},true);
                string CommitCorrection(string id)
                {
                    string idleFrame=Ui().GetProperty("CandidateFrameSession").GetString();
                    foreach(char c in "tlleo") Key(char.ToUpperInvariant(c));
                    var state=WaitUi(s=>s.GetProperty("InputCode").GetString().Replace(" ","")=="tlleo" && s.GetProperty("Candidates").GetArrayLength()>1);
                    if(state.GetProperty("CompositionState").GetInt32()!=5) throw new InvalidOperationException("Not using sentence decoder");
                    string frame=state.GetProperty("CandidateFrameSession").GetString();
                    if(frame==idleFrame) throw new InvalidOperationException("New sentence retained old frame identity");
                    Key(9); WaitUi(s=>s.GetProperty("SelectedCandidateIndex").GetInt32()==1);
                    var commit=Key(32,id);
                    if(!commit.TryGetProperty("learning_receipt",out var receipt) || string.IsNullOrEmpty(receipt.GetString()))
                        throw new InvalidOperationException("Correction did not issue receipt");
                    Connect(); // Retry after a real disconnect; same event must not apply twice.
                    var retry=Key(32,id);
                    if(retry.GetProperty("learning_receipt").GetString()!=receipt.GetString() || retry.GetProperty("commit_text").GetString()!=commit.GetProperty("commit_text").GetString())
                        throw new InvalidOperationException("Receipt changed on reconnect replay");
                    WaitUi(s=>s.GetProperty("InputCode").GetString()=="" && s.GetProperty("CandidateFrameSession").GetString()!=frame);
                    return receipt.GetString();
                }
                try
                {
                    string receipt=CommitCorrection("confirmed");
                    Ack(receipt,true,"wrong-client"); Send(new {type="query_state"}); Thread.Sleep(100);
                    if(JournalCount()!=0) throw new InvalidOperationException("Wrong client learned");
                    Ack(receipt,true);
                    var wait=Stopwatch.StartNew(); int learned;
                    while((learned=JournalCount())<=0 && wait.ElapsedMilliseconds<5000) Thread.Sleep(20);
                    if(learned<=0) throw new InvalidOperationException("Confirmed receipt did not persist");
                    Ack(receipt,true); Send(new {type="query_state"}); Thread.Sleep(100);
                    if(JournalCount()!=learned) throw new InvalidOperationException("Duplicate receipt learned twice");
                    receipt=CommitCorrection("failed"); Ack(receipt,false); Ack(receipt,true);
                    Send(new {type="query_state"}); Thread.Sleep(100);
                    if(JournalCount()!=learned) throw new InvalidOperationException("Failed commit learned");
                    receipt=CommitCorrection("focus-canceled"); Send(new {type="focus",hwnd=123,processId=123},true); Ack(receipt,true);
                    Send(new {type="query_state"}); Thread.Sleep(100);
                    if(JournalCount()!=learned) throw new InvalidOperationException("Focus-canceled receipt learned");
                    // Async results from canceled input must not resurrect an old frame.
                    foreach(char c in "tlleotlleo") Key(char.ToUpperInvariant(c));
                    Send(new {type="composition_canceled"},true);
                    WaitUi(s=>s.GetProperty("InputCode").GetString()==""); Thread.Sleep(200);
                    if(Ui().GetProperty("InputCode").GetString()!="") throw new InvalidOperationException("Stale decode resurrected composition");
                    // A committed prefix remains decoder context, never candidate UI text.
                    Send(new {type="set_config",key="整句自动提前上屏",value="是"});
                    var earlyOutput=new StringBuilder();
                    foreach(char c in "tuja")
                    {
                        var response=Key(char.ToUpperInvariant(c));
                        if(response.TryGetProperty("commit_text",out var part)) earlyOutput.Append(part.GetString());
                        Thread.Sleep(150); // Exercise normal paced input and the async UI pump.
                    }
                    if(earlyOutput.ToString()!="我") throw new InvalidOperationException("tuja did not early-commit 我: "+earlyOutput);
                    var suffixUi=WaitUi(s=>s.GetProperty("InputCode").GetString().Replace(" ","")=="ja" && s.GetProperty("Candidates").GetArrayLength()>0);
                    if(suffixUi.GetProperty("Candidates")[0].GetString()!="们")
                        throw new InvalidOperationException("Committed prefix leaked into candidate UI: "+suffixUi.GetRawText());
                    var suffixCommit=Key(32);
                    if(suffixCommit.GetProperty("commit_text").GetString()!="们") throw new InvalidOperationException("Candidate suffix and space commit differ");
                    WaitUi(s=>s.GetProperty("InputCode").GetString()=="");
                    Send(new {type="set_config",key="整句自动提前上屏",value="否"});
                    // Bounded real-pipe reconnect soak with resource samples.
                    host.Refresh(); long memoryBefore=host.PrivateMemorySize64; int handlesBefore=host.HandleCount;
                    for(int i=0;i<200;++i) { Connect(); var state=Send(new {type="query_state",seq=i}); if(state.GetProperty("seq").GetInt32()!=i) throw new InvalidOperationException("Reconnect response mismatch"); }
                    Thread.Sleep(100); host.Refresh();
                    long growth=host.PrivateMemorySize64-memoryBefore; int handles=host.HandleCount-handlesBefore;
                    if(handles>16 || growth>64L*1024*1024) throw new InvalidOperationException("Reconnect resources did not remain bounded");
                    File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new {learned, reconnects=200, handleGrowth=handles, privateBytesGrowth=growth, actualModel=true, earlyPrefixTuja=true, physicalTyping=false}));
                    Send(new {type="exit_core"});
                    if(!host.WaitForExit(5000) || host.ExitCode!=0) throw new InvalidOperationException("Sentence host did not stop");
                    Console.WriteLine("Native actual-model pipe receipt, reconnect replay, journal, cancellation and bounded reconnect checks passed");
                    return 0;
                }
                finally { writer?.Dispose();reader?.Dispose();pipe?.Dispose(); }
            }
            finally
            {
                if(!host.HasExited) { host.Kill(true);host.WaitForExit(); }
                File.WriteAllText(Path.Combine(root,"stderr.log"),stderr.GetAwaiter().GetResult());
            }
        }
    }
}
