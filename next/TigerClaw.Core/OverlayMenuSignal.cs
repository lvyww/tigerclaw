using System;
using System.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal static class OverlayMenuSignal
    {
        public static void Trigger()
        {
            try
            {
                using (var evt = new EventWaitHandle(false, EventResetMode.AutoReset, RuntimeConstants.ShowMenuEventName))
                {
                    evt.Set();
                }
            }
            catch
            {
            }
        }
    }
}
