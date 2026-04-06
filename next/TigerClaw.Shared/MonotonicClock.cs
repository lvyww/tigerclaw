using System.Runtime.InteropServices;

namespace TigerClaw.Shared
{
    public static class MonotonicClock
    {
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public static long GetMilliseconds()
        {
            return (long)GetTickCount64();
        }
    }
}
