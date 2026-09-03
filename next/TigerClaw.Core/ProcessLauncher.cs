using System;
using System.Diagnostics;
using System.IO;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal sealed class ProcessLauncher
    {
        private readonly string _baseDir;

        public ProcessLauncher()
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            _baseDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        }

        public bool TryLaunchOverlay()
        {
            if (IsProcessRunning(RuntimeConstants.OverlayProcessName))
            {
                return true;
            }

            string exePath = ResolveSiblingExe(RuntimeConstants.OverlayProcessName + ".exe");
            return Start(exePath, null);
        }

        public bool TryLaunchDialog(bool addCi)
        {
            string args = addCi ? "--addci" : null;
            string exePath = ResolveSiblingExe(RuntimeConstants.DialogProcessName + ".exe");
            return Start(exePath, args);
        }

        public bool TryLaunchSentence(string arguments)
        {
            if (IsProcessRunning(RuntimeConstants.SentenceProcessName))
            {
                return true;
            }

            string exePath = ResolveSiblingExe(RuntimeConstants.SentenceProcessName + ".exe");
            return Start(exePath, arguments, createNoWindow: true);
        }

        public bool HasPublishedNativeHook()
        {
            string exePath = ResolveSiblingExe(RuntimeConstants.HookNativePublishedProcessName + ".exe");
            return !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
        }

        public bool TryLaunchPublishedNativeHook()
        {
            if (IsProcessRunning(RuntimeConstants.HookNativePublishedProcessName))
            {
                return true;
            }

            string exePath = ResolveSiblingExe(RuntimeConstants.HookNativePublishedProcessName + ".exe");
            return Start(exePath, null);
        }

        private static bool IsProcessRunning(string processName)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                return processes != null && processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private string ResolveSiblingExe(string fileName)
        {
            string sameDir = Path.Combine(_baseDir, fileName);
            if (File.Exists(sameDir))
            {
                return sameDir;
            }

            string sentenceSubdirectory = Path.Combine(_baseDir, "sentence", fileName);
            if (File.Exists(sentenceSubdirectory))
            {
                return sentenceSubdirectory;
            }

            // Development and publish layouts may place sidecars above or beside Core.
            string dir = _baseDir;
            for (int up = 0; up < 6 && !string.IsNullOrEmpty(dir); up++)
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }


                string nestedCandidate = Path.Combine(dir, "sentence", fileName);
                if (File.Exists(nestedCandidate))
                {
                    return nestedCandidate;
                }

                string debugSentenceCandidate = Path.Combine(dir, "_run", "Debug", "sentence", fileName);
                if (File.Exists(debugSentenceCandidate))
                {
                    return debugSentenceCandidate;
                }

                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                dir = parent;
            }

            // Typical debug layout fallback.
            string repoFallback = Path.GetFullPath(Path.Combine(_baseDir, "..", "..", "..", ".."));
            string overlayFallback = Path.Combine(repoFallback, RuntimeConstants.OverlayProcessName, "bin", "Debug", "net48", fileName);
            if (File.Exists(overlayFallback))
            {
                return overlayFallback;
            }

            string dialogFallback = Path.Combine(repoFallback, RuntimeConstants.DialogProcessName, "bin", "Debug", "net48", fileName);
            if (File.Exists(dialogFallback))
            {
                return dialogFallback;
            }

            return sameDir;
        }

        private static bool Start(string exePath, string arguments, bool createNoWindow = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments ?? string.Empty,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = !createNoWindow,
                    CreateNoWindow = createNoWindow
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
