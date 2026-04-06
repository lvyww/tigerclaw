using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class CoreRequestResult
    {
        public bool Success { get; set; }
        public string ResponseLine { get; set; }
        public string Error { get; set; }
    }

    internal sealed class CoreSchemaListResult
    {
        public bool Success { get; set; }
        public string[] SchemaList { get; set; }
        public string CurrentSchema { get; set; }
        public string Error { get; set; }
    }

    internal sealed class CoreThemeResult
    {
        public bool Success { get; set; }
        public string Theme { get; set; }
        public string Error { get; set; }
    }

    internal sealed class CorePathResult
    {
        public bool Success { get; set; }
        public string Path { get; set; }
        public string Error { get; set; }
    }

    internal static class CorePipeClient
    {
        private const int DefaultSeq = 1;
        private const string KeyTheme = "\u4e3b\u9898";

        public static bool TrySetConfigValue(string key, string value, int timeoutMs, out string error)
        {
            error = string.Empty;
            string request = "{\"type\":\"set_config\",\"seq\":" + DefaultSeq +
                             ",\"key\":\"" + Escape(key) +
                             "\",\"value\":\"" + Escape(value) + "\"}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryShowConfigWin(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"show_config\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryShowAddCi(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"show_addci\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryReloadMb(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"reload_mb\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryGetMbFolderPath(int timeoutMs, out string path, out string error)
        {
            path = string.Empty;
            string request = "{\"type\":\"open_mb_folder\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            path = ExtractJsonField(responseLine, "path");
            return !string.IsNullOrWhiteSpace(path);
        }

        public static bool TryExportMb(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"export_mb\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryOpenOfficial(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"open_official\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryExitCore(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"exit_core\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryGetSchemaList(int timeoutMs, out string[] schemaList, out string currentSchema, out string error)
        {
            schemaList = Array.Empty<string>();
            currentSchema = string.Empty;
            error = string.Empty;

            string request = "{\"type\":\"get_schema_list\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            string text = ExtractJsonField(responseLine, "schema_list");
            currentSchema = ExtractJsonField(responseLine, "current_schema");
            schemaList = string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : text.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return true;
        }

        public static bool TryGetCurrentTheme(int timeoutMs, out string theme, out string error)
        {
            theme = string.Empty;
            error = string.Empty;
            string request = "{\"type\":\"get_config\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            string configText = ExtractJsonField(responseLine, "config_text");
            theme = ExtractConfigValue(configText, KeyTheme);
            return true;
        }

        public static Task<CoreRequestResult> SetConfigValueAsync(string key, string value, int timeoutMs)
        {
            return Task.Run(() =>
            {
                bool ok = TrySetConfigValue(key, value, timeoutMs, out string error);
                return new CoreRequestResult
                {
                    Success = ok,
                    ResponseLine = string.Empty,
                    Error = error ?? string.Empty
                };
            });
        }

        public static Task<CoreRequestResult> ShowConfigWinAsync(int timeoutMs)
        {
            return RunSimpleAsync(TryShowConfigWin, timeoutMs);
        }

        public static Task<CoreRequestResult> ShowAddCiAsync(int timeoutMs)
        {
            return RunSimpleAsync(TryShowAddCi, timeoutMs);
        }

        public static Task<CoreRequestResult> ReloadMbAsync(int timeoutMs)
        {
            return RunSimpleAsync(TryReloadMb, timeoutMs);
        }

        public static Task<CorePathResult> GetMbFolderPathAsync(int timeoutMs)
        {
            return Task.Run(() =>
            {
                bool ok = TryGetMbFolderPath(timeoutMs, out string path, out string error);
                return new CorePathResult
                {
                    Success = ok,
                    Path = path ?? string.Empty,
                    Error = error ?? string.Empty
                };
            });
        }

        public static Task<CoreRequestResult> ExportMbAsync(int timeoutMs)
        {
            return RunSimpleAsync(TryExportMb, timeoutMs);
        }

        public static Task<CoreRequestResult> OpenOfficialAsync(int timeoutMs)
        {
            return RunSimpleAsync(TryOpenOfficial, timeoutMs);
        }

        public static Task<CoreRequestResult> ExitCoreAsync(int timeoutMs)
        {
            return RunSimpleAsync(TryExitCore, timeoutMs);
        }

        public static Task<CoreSchemaListResult> GetSchemaListAsync(int timeoutMs)
        {
            return Task.Run(() =>
            {
                bool ok = TryGetSchemaList(timeoutMs, out string[] schemaList, out string currentSchema, out string error);
                return new CoreSchemaListResult
                {
                    Success = ok,
                    SchemaList = schemaList ?? Array.Empty<string>(),
                    CurrentSchema = currentSchema ?? string.Empty,
                    Error = error ?? string.Empty
                };
            });
        }

        public static Task<CoreThemeResult> GetCurrentThemeAsync(int timeoutMs)
        {
            return Task.Run(() =>
            {
                bool ok = TryGetCurrentTheme(timeoutMs, out string theme, out string error);
                return new CoreThemeResult
                {
                    Success = ok,
                    Theme = theme ?? string.Empty,
                    Error = error ?? string.Empty
                };
            });
        }

        private static bool TrySendRequest(string requestJson, int timeoutMs, out string responseLine, out string error)
        {
            responseLine = string.Empty;
            error = string.Empty;

            try
            {
                using (var pipe = new NamedPipeClientStream(".", RuntimeConstants.TsfPipeShortName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMs);
                    pipe.ReadMode = PipeTransmissionMode.Message;

                    byte[] data = Encoding.UTF8.GetBytes(requestJson + "\n");
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();

                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    {
                        Task<string> readTask = reader.ReadLineAsync();
                        if (!readTask.Wait(timeoutMs))
                        {
                            error = "Core response timeout.";
                            return false;
                        }

                        responseLine = readTask.Result ?? string.Empty;
                    }
                }

                if (responseLine.IndexOf("\"success\":true", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                error = ExtractJsonField(responseLine, "error");
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "Core returned failure.";
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string ExtractJsonField(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field))
            {
                return string.Empty;
            }

            string key = "\"" + field + "\":\"";
            int start = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += key.Length;
            int i = start;
            bool escaped = false;
            while (i < json.Length)
            {
                char ch = json[i];
                if (!escaped && ch == '"')
                {
                    break;
                }

                if (ch == '\\' && !escaped)
                {
                    escaped = true;
                }
                else
                {
                    escaped = false;
                }

                i++;
            }

            if (i <= start || i >= json.Length)
            {
                return string.Empty;
            }

            return Unescape(json.Substring(start, i - start));
        }

        private static string ExtractConfigValue(string configText, string key)
        {
            if (string.IsNullOrWhiteSpace(configText) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            string[] lines = configText.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int sep = line.IndexOf('\t');
                if (sep < 0)
                {
                    sep = line.IndexOf(' ');
                }

                if (sep <= 0)
                {
                    continue;
                }

                string k = line.Substring(0, sep).Trim();
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(sep + 1).Trim();
                }
            }

            return string.Empty;
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    switch (next)
                    {
                        case 'r':
                            sb.Append('\r');
                            i++;
                            continue;
                        case 'n':
                            sb.Append('\n');
                            i++;
                            continue;
                        case 't':
                            sb.Append('\t');
                            i++;
                            continue;
                        case '"':
                            sb.Append('"');
                            i++;
                            continue;
                        case '\\':
                            sb.Append('\\');
                            i++;
                            continue;
                    }
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        private delegate bool CoreCall(int timeoutMs, out string error);

        private static Task<CoreRequestResult> RunSimpleAsync(CoreCall action, int timeoutMs)
        {
            return Task.Run(() =>
            {
                bool ok = action(timeoutMs, out string error);
                return new CoreRequestResult
                {
                    Success = ok,
                    ResponseLine = string.Empty,
                    Error = error ?? string.Empty
                };
            });
        }
    }
}
