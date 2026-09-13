using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal sealed class ProcessLauncher
    {
        private readonly string _baseDir;
        // Accessed only by the Sentence lifecycle worker. Never adopt by name.
        private Process _ownedSentence;

        internal bool HasOwnedSentence => _ownedSentence != null;
        internal int OwnedSentenceId => _ownedSentence != null && !_ownedSentence.HasExited ? _ownedSentence.Id : 0;

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);

        public void AllowOverlayForeground()
        {
            // Only the sibling runtime receives the permission passed by TSF.
            string expected = Path.GetFullPath(ResolveSiblingExe(RuntimeConstants.OverlayProcessName + ".exe"));
            Process[] processes;
            try { processes = Process.GetProcessesByName(RuntimeConstants.OverlayProcessName); }
            catch { return; }
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                            AllowSetForegroundWindow((uint)process.Id);
                    }
                    catch { /* Exited or inaccessible: do not broaden permission. */ }
                }
            }
        }

        public bool TryLaunchSentence(string arguments)
        {
            if (_ownedSentence != null)
            {
                if (!_ownedSentence.HasExited) return true;
                _ownedSentence.Dispose();
                _ownedSentence = null;
            }
            if (IsProcessRunning(RuntimeConstants.SentenceProcessName))
            {
                return true;
            }

            string exePath = ResolveSiblingExe(RuntimeConstants.SentenceProcessName + ".exe");
            if (!File.Exists(exePath)) return false;
            try
            {
                _ownedSentence = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath, Arguments = arguments ?? string.Empty,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = false, CreateNoWindow = true
                });
                return _ownedSentence != null;
            }
            catch { return false; }
        }

        internal void StopOwnedSentence()
        {
            if (_ownedSentence == null) return;
            if (!_ownedSentence.WaitForExit(500))
            {
                _ownedSentence.Kill();
                if (!_ownedSentence.WaitForExit(1000))
                    throw new TimeoutException("Owned Sentence process has not exited");
            }
            _ownedSentence.Dispose();
            _ownedSentence = null;
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
                bool found = processes != null && processes.Length > 0;
                if (processes != null)
                    foreach (Process process in processes) process.Dispose();
                return found;
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
