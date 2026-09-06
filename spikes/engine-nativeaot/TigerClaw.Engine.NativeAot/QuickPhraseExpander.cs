using System.Globalization;

namespace TigerClaw.Engine.NativeAot;

internal static class QuickPhraseExpander
{
    public static string Expand(string text, string repeatBuffer)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (text.Length > 3 && text[0] == '{' && text[^1] == '}' && text.IndexOf('|') > 0)
        {
            string[] choices = text[1..^1].Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (choices.Length > 0)
            {
                return choices[Random.Shared.Next(choices.Length)];
            }
        }

        return text switch
        {
            "{重复上屏}" => repeatBuffer,
            "{日期}" => DateTime.Now.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture),
            "{日期.}" => DateTime.Now.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            "{日期-}" => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "{日期/}" => DateTime.Now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            "{时分秒}" => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "{时分}" => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            "{星期}" => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DateTime.Now.DayOfWeek),
            "{周}" => new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)DateTime.Now.DayOfWeek],
            _ => text,
        };
    }
}
