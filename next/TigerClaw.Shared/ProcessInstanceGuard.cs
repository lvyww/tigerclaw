using System;
using System.Diagnostics;

namespace TigerClaw.Shared
{
    public static class ProcessInstanceGuard
    {
        public static bool EnsureNoOtherInstances(string processName, int currentProcessId, int waitMs = 2000)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process == null || process.Id == currentProcessId)
                        {
                            continue;
                        }

                        process.Kill();
                        if (!process.WaitForExit(waitMs))
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                    finally
                    {
                        try
                        {
                            process?.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void TryTerminateOtherInstances(string processName, int currentProcessId, int waitMs = 1000)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return;
            }

            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process == null || process.Id == currentProcessId)
                        {
                            continue;
                        }

                        process.Kill();
                        process.WaitForExit(waitMs);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try
                        {
                            process?.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
