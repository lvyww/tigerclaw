using System;
using System.IO;
using Microsoft.Win32;

namespace TigerClaw.Shared
{
    public static class TsfRegistrationGuard
    {
        private const string TipClsid = "{14493D3C-2059-41C0-805A-1F7841DE206B}";
        private const string InprocSubKey = @"CLSID\" + TipClsid + @"\InprocServer32";

        private static readonly string[] InstallScriptNames =
        {
            "\u5b89\u88c5.bat",
            "install.bat",
            "dist_install.bat"
        };

        public static bool IsTsfRegistered(out string dllPath)
        {
            dllPath = string.Empty;

            if (TryReadRegisteredDllPath(RegistryView.Registry64, out dllPath))
            {
                return true;
            }

            if (TryReadRegisteredDllPath(RegistryView.Registry32, out dllPath))
            {
                return true;
            }

            return false;
        }

        public static bool ShouldBypassRegistrationCheck(string startDirectory)
        {
            string current = startDirectory;
            if (string.IsNullOrWhiteSpace(current))
            {
                current = AppDomain.CurrentDomain.BaseDirectory;
            }

            try
            {
                current = Path.GetFullPath(current);
                return File.Exists(Path.Combine(current, "TigerClaw.exe"));
            }
            catch
            {
                return false;
            }
        }

        public static string FindInstallScriptPath(string startDirectory)
        {
            string current = startDirectory;
            if (string.IsNullOrWhiteSpace(current))
            {
                current = AppDomain.CurrentDomain.BaseDirectory;
            }

            try
            {
                current = Path.GetFullPath(current);
            }
            catch
            {
                return string.Empty;
            }

            for (int i = 0; i < 8 && !string.IsNullOrEmpty(current); i++)
            {
                foreach (string scriptName in InstallScriptNames)
                {
                    string direct = Path.Combine(current, scriptName);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }

                    string releaseSub = Path.Combine(current, "release", scriptName);
                    if (File.Exists(releaseSub))
                    {
                        return releaseSub;
                    }
                }

                DirectoryInfo parent = null;
                try
                {
                    parent = Directory.GetParent(current);
                }
                catch
                {
                    parent = null;
                }

                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }

            return string.Empty;
        }

        public static string BuildNotRegisteredMessage(string installScriptPath)
        {
            if (!string.IsNullOrWhiteSpace(installScriptPath))
            {
                return "\u672a\u68c0\u6d4b\u5230 TigerClaw \u8f93\u5165\u6cd5\u672a\u6ce8\u518c\u3002\r\n\r\n" +
                       "\u8bf7\u5148\u8fd0\u884c\u4ee5\u4e0b\u811a\u672c\uff1a\r\n" +
                       installScriptPath +
                       "\r\n\r\n\u7a0b\u5e8f\u5c06\u9000\u51fa\u3002";
            }

            return "\u672a\u68c0\u6d4b\u5230 TigerClaw \u8f93\u5165\u6cd5\u672a\u6ce8\u518c\u3002\r\n\r\n" +
                   "\u8bf7\u5148\u8fd0\u884c\u300c\u5b89\u88c5.bat\u300d\u5b8c\u6210\u6ce8\u518c\u540e\u518d\u542f\u52a8\u3002\r\n\r\n" +
                   "\u7a0b\u5e8f\u5c06\u9000\u51fa\u3002";
        }

        private static bool TryReadRegisteredDllPath(RegistryView view, out string dllPath)
        {
            dllPath = string.Empty;

            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
                using (RegistryKey inproc = baseKey.OpenSubKey(InprocSubKey, false))
                {
                    if (inproc == null)
                    {
                        return false;
                    }

                    string value = inproc.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return false;
                    }

                    string normalized = value.Trim().Trim('"');
                    try
                    {
                        normalized = Environment.ExpandEnvironmentVariables(normalized);
                    }
                    catch
                    {
                    }

                    if (!File.Exists(normalized))
                    {
                        return false;
                    }

                    dllPath = normalized;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
