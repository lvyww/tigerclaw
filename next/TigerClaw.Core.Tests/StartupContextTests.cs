using System;
using System.Security.Principal;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void RunStartupContextTests()
        {
            var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
            void Check(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException("Startup context: " + name);
            }
            Check(CoreStartupContext.IsAllowed(1, user, "WinSta0", "Default"), "interactive user");
            Check(CoreStartupContext.IsAllowed(3, user, "winsta0", "default"), "RDP user session");
            foreach (string sid in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-20" })
            {
                Check(!CoreStartupContext.IsAllowed(1, new SecurityIdentifier(sid), "WinSta0", "Default"),
                    "service identity cannot claim even the user desktop");
            }
            Check(!CoreStartupContext.IsAllowed(0, user, "WinSta0", "Default"), "session zero");
            Check(!CoreStartupContext.IsAllowed(1, user, "WinSta0", "Winlogon"), "logon desktop");
            Check(!CoreStartupContext.IsAllowed(1, user, "Service-0x0-3e7$", "Default"), "service station");
            Check(!CoreStartupContext.IsAllowed(1, null, "WinSta0", "Default"), "unknown user");
            Check(!CoreStartupContext.IsAllowed(1, user, null, null), "unreadable desktop");
            Check(CoreStartupContext.CanStart(), "current interactive process native query");
            Console.WriteLine("Core startup context tests passed");
        }
    }
}
