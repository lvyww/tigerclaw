using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TigerClaw.Core
{
    internal sealed class SimpleJsonObject
    {
        private readonly Dictionary<string, object> _data;

        public SimpleJsonObject(Dictionary<string, object> data)
        {
            _data = data ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public object GetValue(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            return _data.TryGetValue(key, out object value) ? value : null;
        }
    }

    internal static class SimpleJson
    {
        public static SimpleJsonObject Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            json = json.Trim();
            if (!json.StartsWith("{", StringComparison.Ordinal) || !json.EndsWith("}", StringComparison.Ordinal))
            {
                return null;
            }

            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            json = json.Substring(1, json.Length - 2).Trim();

            int i = 0;
            while (i < json.Length)
            {
                i = SkipWhitespace(json, i);
                if (i >= json.Length)
                {
                    break;
                }

                if (json[i] != '"')
                {
                    i++;
                    continue;
                }

                int keyStart = ++i;
                while (i < json.Length && json[i] != '"')
                {
                    if (json[i] == '\\')
                    {
                        i++;
                    }
                    i++;
                }

                string key = UnescapeString(json.Substring(keyStart, i - keyStart));
                i++;
                i = SkipWhitespace(json, i);
                if (i >= json.Length || json[i] != ':')
                {
                    i++;
                    continue;
                }

                i++;
                i = SkipWhitespace(json, i);
                if (i >= json.Length)
                {
                    break;
                }

                object value;
                int valueEnd;
                if (json[i] == '"')
                {
                    int strStart = ++i;
                    while (i < json.Length && json[i] != '"')
                    {
                        if (json[i] == '\\')
                        {
                            i++;
                        }
                        i++;
                    }
                    value = UnescapeString(json.Substring(strStart, i - strStart));
                    valueEnd = i + 1;
                }
                else if (char.ToLowerInvariant(json[i]) == 't' || char.ToLowerInvariant(json[i]) == 'f')
                {
                    bool boolVal = char.ToLowerInvariant(json[i]) == 't';
                    value = boolVal;
                    valueEnd = i + (boolVal ? 4 : 5);
                }
                else if (char.ToLowerInvariant(json[i]) == 'n')
                {
                    value = null;
                    valueEnd = i + 4;
                }
                else
                {
                    int numStart = i;
                    while (i < json.Length &&
                           (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-' || json[i] == '+' || json[i] == 'e' || json[i] == 'E'))
                    {
                        i++;
                    }

                    string numStr = json.Substring(numStart, i - numStart);
                    if (numStr.Contains(".") || numStr.Contains("e") || numStr.Contains("E"))
                    {
                        if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        {
                            value = d;
                        }
                        else
                        {
                            value = numStr;
                        }
                    }
                    else if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    {
                        value = l;
                    }
                    else
                    {
                        value = numStr;
                    }

                    valueEnd = i;
                }

                dict[key] = value;
                i = valueEnd;
                i = SkipWhitespace(json, i);
                if (i < json.Length && json[i] == ',')
                {
                    i++;
                }
            }

            return new SimpleJsonObject(dict);
        }

        public static string EscapeString(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s ?? string.Empty;
            }

            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static int SkipWhitespace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
            return i;
        }

        private static string UnescapeString(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    switch (next)
                    {
                        case '"':
                            sb.Append('"');
                            i++;
                            break;
                        case '\\':
                            sb.Append('\\');
                            i++;
                            break;
                        case 'n':
                            sb.Append('\n');
                            i++;
                            break;
                        case 'r':
                            sb.Append('\r');
                            i++;
                            break;
                        case 't':
                            sb.Append('\t');
                            i++;
                            break;
                        default:
                            sb.Append(s[i]);
                            break;
                    }
                }
                else
                {
                    sb.Append(s[i]);
                }
            }

            return sb.ToString();
        }
    }
}
