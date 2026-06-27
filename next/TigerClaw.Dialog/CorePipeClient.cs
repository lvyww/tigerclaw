using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using TigerClaw.Shared;

namespace TigerClaw.Dialog
{
    internal static class CorePipeClient
    {
        private const int DefaultSeq = 1;

        public static bool TryGetConfigText(int timeoutMs, out string configText, out string error)
        {
            configText = string.Empty;
            error = string.Empty;

            string request = "{\"type\":\"get_config\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            configText = ExtractJsonField(responseLine, "config_text");
            return true;
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

        public static bool TrySetConfigValue(string key, string value, int timeoutMs, out bool changed, out string error)
        {
            changed = false;
            error = string.Empty;

            string request = "{\"type\":\"set_config\",\"seq\":" + DefaultSeq +
                             ",\"key\":\"" + Escape(key) +
                             "\",\"value\":\"" + Escape(value) + "\"}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            changed = ExtractJsonBool(responseLine, "changed", false);
            return true;
        }

        public static bool TrySendReloadConfig(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"reload_config\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryGetMbFolderPath(int timeoutMs, out string path, out string error)
        {
            path = string.Empty;
            error = string.Empty;

            string request = "{\"type\":\"open_mb_folder\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            path = ExtractJsonField(responseLine, "path");
            return true;
        }

        public static bool TryOpenOfficial(int timeoutMs, out string error)
        {
            string request = "{\"type\":\"open_official\",\"seq\":" + DefaultSeq + "}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryAddCi(string code, string text, int timeoutMs, out string error)
        {
            string request = "{\"type\":\"add_ci\",\"seq\":" + DefaultSeq + ",\"code\":\"" + Escape(code) + "\",\"text\":\"" + Escape(text) + "\"}";
            return TrySendRequest(request, timeoutMs, out _, out error);
        }

        public static bool TryConstructCi(string text, int timeoutMs, out string code, out string error)
        {
            code = string.Empty;
            error = string.Empty;
            string request = "{\"type\":\"construct_ci\",\"seq\":" + DefaultSeq + ",\"text\":\"" + Escape(text ?? string.Empty) + "\"}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            code = ExtractJsonField(responseLine, "code");
            return true;
        }

        public static bool TryGetLastCi(int historyLen, int timeoutMs, out string text, out string error)
        {
            text = string.Empty;
            error = string.Empty;
            if (historyLen < 0)
            {
                historyLen = 0;
            }

            string request = "{\"type\":\"get_last_ci\",\"seq\":" + DefaultSeq + ",\"history_len\":" + historyLen + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            text = ExtractJsonField(responseLine, "text");
            return true;
        }

        public static bool TryGetSendHistoryCount(int timeoutMs, out int count, out string error)
        {
            count = 0;
            error = string.Empty;
            string request = "{\"type\":\"get_send_history_count\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            count = ExtractJsonInt(responseLine, "count", 0);
            if (count < 0)
            {
                count = 0;
            }
            return true;
        }

        public static bool TryGetSelectionKeyConfig(int timeoutMs, out string configText, out string defaultText, out string configPath, out string error)
        {
            configText = string.Empty;
            defaultText = string.Empty;
            configPath = string.Empty;
            error = string.Empty;

            string request = "{\"type\":\"get_selection_key_config\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            configText = ExtractJsonField(responseLine, "config_text");
            defaultText = ExtractJsonField(responseLine, "default_text");
            configPath = ExtractJsonField(responseLine, "config_path");
            return true;
        }

        public static bool TrySetSelectionKeyConfig(string configText, int timeoutMs, out string savedConfigText, out string error)
        {
            savedConfigText = string.Empty;
            error = string.Empty;
            string request = "{\"type\":\"set_selection_key_config\",\"seq\":" + DefaultSeq +
                             ",\"config_text\":\"" + Escape(configText) + "\"}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            savedConfigText = ExtractJsonField(responseLine, "config_text");
            return true;
        }

        public static bool TryResetSelectionKeyConfig(int timeoutMs, out string configText, out string error)
        {
            configText = string.Empty;
            error = string.Empty;
            string request = "{\"type\":\"reset_selection_key_config\",\"seq\":" + DefaultSeq + "}";
            if (!TrySendRequest(request, timeoutMs, out string responseLine, out error))
            {
                return false;
            }

            configText = ExtractJsonField(responseLine, "config_text");
            return true;
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

        private static bool ExtractJsonBool(string json, string field, bool fallback)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field))
            {
                return fallback;
            }

            string key = "\"" + field + "\":";
            int start = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return fallback;
            }

            start += key.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
            {
                start++;
            }

            if (start + 4 <= json.Length && string.Compare(json, start, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }

            if (start + 5 <= json.Length && string.Compare(json, start, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return false;
            }

            return fallback;
        }

        private static int ExtractJsonInt(string json, string field, int fallback)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field))
            {
                return fallback;
            }

            string key = "\"" + field + "\":";
            int start = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return fallback;
            }

            start += key.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
            {
                start++;
            }

            int end = start;
            while (end < json.Length && (json[end] == '-' || char.IsDigit(json[end])))
            {
                end++;
            }

            if (end <= start)
            {
                return fallback;
            }

            if (int.TryParse(json.Substring(start, end - start), out int parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
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
    }
}
