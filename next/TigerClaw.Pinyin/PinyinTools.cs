using System.Globalization;
using System.Text;

namespace TigerClaw.Pinyin;

internal static class PinyinTools
{
    internal static string[] Candidates(string raw, DateTime now)
    {
        string command = raw.ToLowerInvariant();
        if (command == "/rq") return [now.ToString("yyyy-MM-dd"), now.ToString("yyyy年M月d日"), now.ToString("yyyy/MM/dd")];
        if (command == "/sj") return [now.ToString("HH:mm:ss"), now.ToString("HH:mm")];
        if (command == "/xq") return ["星期" + "日一二三四五六"[(int)now.DayOfWeek]];
        if (command == "/dt") return [now.ToString("yyyy-MM-dd HH:mm:ss"), now.ToString("yyyy-MM-ddTHH:mm:sszzz")];
        if (command == "/fh") return ["——", "……", "·", "±", "×", "÷", "℃", "㎡", "√", "×", "©", "®"];
        if (command == "/jt") return ["→", "←", "↑", "↓", "↔", "⇒", "⇔", "↗", "↘"];
        if (command == "/emoji") return ["😀", "😂", "😊", "👍", "❤️", "🎉", "🙏", "😭", "🤔", "✅"];
        if (command.StartsWith("/r") && decimal.TryParse(command.AsSpan(2), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount))
            return Math.Abs(amount) < 10000000000000m && decimal.Round(amount, 2) == amount ? [Money(amount)] : [];
        if (command.StartsWith("/v") && command.Length <= 130)
        {
            try { string value = new Calculator(command[2..]).Read().ToString("G29", CultureInfo.InvariantCulture); return [value, raw[2..] + "=" + value]; }
            catch (Exception e) when (e is ArgumentException or OverflowException or DivideByZeroException) { return []; }
        }
        return [];
    }
    internal static string? Emoji(string text) => text switch
    { "笑脸" => "😊", "大笑" => "😂", "赞" or "点赞" => "👍", "爱心" => "❤️", "庆祝" => "🎉", "谢谢" => "🙏", "哭" => "😭", "思考" => "🤔", _ => null };
    internal static string Money(decimal number)
    {
        bool negative = number < 0; number = Math.Abs(number);
        long whole = (long)decimal.Truncate(number); int cents = (int)((number - whole) * 100);
        string Group(int value)
        {
            string digits = "零壹贰叁肆伍陆柒捌玖", units = "仟佰拾"; var b = new StringBuilder(); bool zero = false;
            int divisor = 1000;
            for (int i = 0; i < 4; i++, divisor /= 10)
            {
                int d = value / divisor % 10;
                if (d == 0) { if (b.Length > 0) zero = true; continue; }
                if (zero) b.Append('零'); zero = false;
                b.Append(digits[d]); if (i < 3) b.Append(units[i]);
            }
            return b.ToString();
        }
        var output = new StringBuilder(negative ? "负" : "");
        bool gap = false;
        for (int group = 3; group >= 0; group--)
        {
            long divisor = group == 3 ? 1000000000000L : group == 2 ? 100000000L : group == 1 ? 10000 : 1;
            int part = (int)(whole / divisor % 10000);
            if (part == 0) { if (output.Length > (negative ? 1 : 0)) gap = true; continue; }
            if (output.Length > (negative ? 1 : 0) && (gap || part < 1000)) output.Append('零');
            output.Append(Group(part)); output.Append(group == 3 ? "万亿" : group == 2 ? "亿" : group == 1 ? "万" : ""); gap = false;
        }
        if (whole == 0) output.Append('零'); output.Append('元');
        if (cents == 0) output.Append('整');
        else
        {
            if (cents / 10 > 0) output.Append("零壹贰叁肆伍陆柒捌玖"[cents / 10]).Append('角');
            else if (whole > 0) output.Append('零');
            if (cents % 10 > 0) output.Append("零壹贰叁肆伍陆柒捌玖"[cents % 10]).Append('分');
        }
        return output.ToString();
    }
    // Arithmetic grammar only: never evaluates code, functions or file names.
    private sealed class Calculator(string input)
    {
        private int at, depth;
        internal decimal Read() { decimal result = Sum(); if (at != input.Length) throw new ArgumentException(); return result; }
        private decimal Sum()
        {
            decimal value = Product();
            while (at < input.Length && input[at] is '+' or '-') { char op = input[at++]; decimal rhs = Product(); value = op == '+' ? value + rhs : value - rhs; }
            return value;
        }
        private decimal Product()
        {
            decimal value = Value();
            while (at < input.Length && input[at] is '*' or '/' or '%') { char op = input[at++]; decimal rhs = Value(); value = op == '*' ? value * rhs : op == '/' ? value / rhs : value % rhs; }
            return value;
        }
        private decimal Value()
        {
            if (++depth > 16) throw new ArgumentException();
            decimal result;
            if (at < input.Length && input[at] is '+' or '-') { char sign = input[at++]; result = Value() * (sign == '-' ? -1 : 1); }
            else if (at < input.Length && input[at] == '(')
            { at++; result = Sum(); if (at >= input.Length || input[at++] != ')') throw new ArgumentException(); }
            else
            {
                int start = at;
                while (at < input.Length && (char.IsAsciiDigit(input[at]) || input[at] == '.')) at++;
                if (!decimal.TryParse(input.AsSpan(start, at - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result)) throw new ArgumentException();
            }
            depth--; return result;
        }
    }
}
