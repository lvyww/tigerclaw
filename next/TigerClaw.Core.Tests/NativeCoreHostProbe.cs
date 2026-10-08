using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Linq;
namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
        private static string NativeHostImagePath(Process process)
        {
            var path=new StringBuilder(32768); uint size=(uint)path.Capacity;
            try { return QueryFullProcessImageNameW(process.Handle,0,path,ref size) ? path.ToString() : null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        private static int RunNativeCoreHostProbe(string executable, string root, string frontendSource = null, string nativeOverlay = null, bool detached = false)
        {
            if(string.IsNullOrWhiteSpace(root) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
                throw new ArgumentException("A new isolated fixture directory is required");
            Directory.CreateDirectory(Path.Combine(root,"tables","Plain"));
            File.WriteAllText(Path.Combine(root,"tables","Plain","table.txt"),"aa 甲\naa 乙\n",new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root,"config.txt"),"码表存储位置\ttables\n当前码表\tPlain\n",new UTF8Encoding(false));
            if (frontendSource != null)
                foreach (string component in new[] { "Dialog", "Overlay" })
                {
                    string source = Path.Combine(frontendSource, "TigerClaw." + component, "bin", "Release", "net48");
                    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        string destination = Path.Combine(root, Path.GetRelativePath(source, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        File.Copy(file, destination, true);
                    }
                }
            if (nativeOverlay != null) File.Copy(nativeOverlay,Path.Combine(root,"TigerClaw.Overlay.exe"),true);
            string pipeName="TigerClaw.Core.Native.Test.Host."+Guid.NewGuid().ToString("N");
            var start=new ProcessStartInfo(executable) { UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
            start.ArgumentList.Add(detached ? "--serve-isolated-detached" : frontendSource == null ? "--serve-isolated" : "--serve-isolated-ui"); start.ArgumentList.Add(root); start.ArgumentList.Add(pipeName);
            using var process=Process.Start(start);
            var frontendIds = new System.Collections.Generic.List<int>();
            try
            {
                string ready=process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                if (ready==null || !ready.Contains("ready")) throw new InvalidOperationException("Native host did not start");
                if(detached) { process.StandardInput.Close(); Thread.Sleep(100); if(process.HasExited) throw new InvalidOperationException("Detached Core stopped on stdin EOF"); }
                using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
                pipe.Connect(5000);
                using var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
                using var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) { AutoFlush=true };
                JsonDocument Send(string request)
                {
                    writer.WriteLine(request);
                    return JsonDocument.Parse(reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                }
                int WaitFrontend(string component, bool window)
                {
                    var timeout = Stopwatch.StartNew();
                    while (timeout.ElapsedMilliseconds < 22000)
                    {
                        foreach (var child in Process.GetProcessesByName("TigerClaw." + component))
                            using (child)
                            {
                                string expected = Path.GetFullPath(Path.Combine(root, "TigerClaw." + component + ".exe"));
                                if (string.Equals(NativeHostImagePath(child), expected, StringComparison.OrdinalIgnoreCase) &&
                                    (!window || child.MainWindowHandle != IntPtr.Zero))
                                { frontendIds.Add(child.Id); return child.Id; }
                            }
                        Thread.Sleep(50);
                    }
                    throw new InvalidOperationException("Isolated " + component + " not ready");
                }
                if (frontendSource == null)
                {
                    using var unsupported = Send("{\"type\":\"show_config\"}");
                    if (unsupported.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Headless host falsely acknowledged window launch");
                }
                if (frontendSource != null)
                {
                    int overlayId=WaitFrontend("Overlay", false);
                    using var show = Send("{\"type\":\"show_config\"}");
                    if (!show.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Settings launch failed");
                    int settings = WaitFrontend("Dialog", true);
                    using var again = Send("{\"type\":\"show_config\"}");
                    if (WaitFrontend("Dialog", true) != settings) throw new InvalidOperationException("Duplicate settings process");
                    using var add = Send("{\"type\":\"show_addci\"}");
                    if (!add.RootElement.GetProperty("success").GetBoolean() || WaitFrontend("Dialog", true) == settings)
                        throw new InvalidOperationException("Add-word window did not replace owned settings window");
                    using var menu = Send("{\"type\":\"show_menu\"}");
                    if (!menu.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Menu signal failed");
                    using var overlayHeartbeat = MemoryMappedFile.OpenExisting(@"Local\" + pipeName + ".OverlayHeartbeat.v1");
                    using var overlayView = overlayHeartbeat.CreateViewAccessor();
                    if (overlayView.ReadInt64(0) < 1) throw new InvalidOperationException("Real Overlay did not publish isolated heartbeat");
                    // Only the isolated, path-verified child is killed. The host
                    // must recreate it from its retained explicit root.
                    using (var child=Process.GetProcessById(overlayId)) { child.Kill(); child.WaitForExit(); }
                    if (WaitFrontend("Overlay",false)==overlayId) throw new InvalidOperationException("Overlay supervisor did not restart child");
                }
                using var first=Send("{\"type\":\"key\",\"vk\":65,\"action\":\"down\",\"client_session\":\"host\",\"event_id\":\"a1\",\"caret_x\":12,\"caret_y\":34}");
                using var second=Send("{\"type\":\"key\",\"vk\":65,\"action\":\"down\",\"client_session\":\"host\",\"event_id\":\"a2\",\"caret_x\":12,\"caret_y\":34}");
                if (second.RootElement.GetProperty("input_buffer").GetString()!="aa") throw new InvalidOperationException("Native pipe composition differs");
                string mapName=@"Local\"+pipeName+".UiState.v1.Snapshot.v2";
                using var map=MemoryMappedFile.OpenExisting(mapName);
                using var view=map.CreateViewAccessor();
                using var gate=Mutex.OpenExisting(mapName+".Lock");
                bool found=false; var timer=Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds<5000)
                {
                    if (gate.WaitOne(1000))
                    {
                        try
                        {
                            long seq=view.ReadInt64(0); int length=view.ReadInt32(16);
                            if (seq>0 && length>0 && length<=131052)
                            {
                                byte[] payload=new byte[length];view.ReadArray(20,payload,0,length);
                                using var ui=JsonDocument.Parse(payload); var value=ui.RootElement;
                                found=value.GetProperty("InputCode").GetString()=="aa" && value.GetProperty("CaretX").GetInt32()==12 &&
                                    value.GetProperty("CandidateVisible").GetBoolean() && value.GetProperty("Candidates")[0].GetString()=="甲";
                            }
                        }
                        finally { gate.ReleaseMutex(); }
                    }
                    if(found) break; Thread.Sleep(10);
                }
                if(!found) throw new InvalidOperationException("Native v2 UI snapshot not observed by managed reader");
                using var heartbeat=MemoryMappedFile.OpenExisting(@"Local\"+pipeName+".Heartbeat.v1");
                using var heartbeatView=heartbeat.CreateViewAccessor();
                if(heartbeatView.ReadInt64(0)<1 || heartbeatView.ReadInt64(8)<=0) throw new InvalidOperationException("Native heartbeat missing");
                using var commit=Send("{\"type\":\"key\",\"vk\":32,\"action\":\"down\",\"client_session\":\"host\",\"event_id\":\"commit\"}");
                using var retry=Send("{\"type\":\"key\",\"seq\":99,\"vk\":32,\"action\":\"down\",\"client_session\":\"host\",\"event_id\":\"commit\"}");
                if(commit.RootElement.GetProperty("commit_text").GetString()!="甲" || retry.RootElement.GetProperty("commit_text").GetString()!="甲" || retry.RootElement.GetProperty("seq").GetInt32()!=99)
                    throw new InvalidOperationException("Native commit/replay differs");
                using var exit=Send("{\"type\":\"exit_core\"}");
                if(!exit.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Native protocol shutdown rejected");
                if(!process.WaitForExit(5000) || process.ExitCode!=0) throw new InvalidOperationException("Native host did not drain and stop");
                foreach (int id in frontendIds.Distinct())
                {
                    try { using var child = Process.GetProcessById(id); if (!child.HasExited) throw new InvalidOperationException("Owned frontend survived host exit"); }
                    catch (ArgumentException) { }
                }
                Console.WriteLine("Native host: managed pipe client, v2 UI reader, commit/replay and graceful shutdown passed");
                return 0;
            }
            finally { if(!process.HasExited) { process.Kill(true);process.WaitForExit(); } }
        }
    }
}
