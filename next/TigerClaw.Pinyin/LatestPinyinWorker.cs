namespace TigerClaw.Pinyin;

// One executing request and one replaceable pending request. The host cancels
// replaced request tokens (including their completion promises).
internal sealed class LatestPinyinWorker
{
    private readonly object gate=new();
    private Func<Task>? pending;
    private bool running,stopped;
    internal void Submit(Func<Task> work)
    {
        lock(gate)
        {
            if(stopped)return;
            pending=work;
            if(running)return;
            running=true;_ = Task.Run(Run);
        }
    }
    private async Task Run()
    {
        while(true)
        {
            Func<Task>? work;
            lock(gate){work=pending;pending=null;if(work==null||stopped){running=false;return;}}
            try{await work().ConfigureAwait(false);}
            catch(Exception e){System.Diagnostics.Debug.WriteLine(e);}
        }
    }
    internal void Stop(){lock(gate){stopped=true;pending=null;}}
}
