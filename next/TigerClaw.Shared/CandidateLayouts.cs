using System.Globalization;

namespace TigerClaw.Shared
{
    public static class CandidateLayouts
    {
        public const string ConfigKey = "候选布局";
        public const int HorizontalWithCode = 1;
        public const int VerticalWithCode = 2;
        public const int HorizontalWithoutCode = 3;
        public const int VerticalWithoutCode = 4;
        public const int CodeOnly = 5;
        public const int Default = VerticalWithoutCode;

        public static int Normalize(int value) => value >= 1 && value <= 5 ? value : Default;
        public static bool IsValid(string value) => value != null && value.Length == 1 && value[0] >= '1' && value[0] <= '5';
        public static int Parse(string value) => IsValid(value) ? value[0] - '0' : Default;
        public static string Format(int value) => Normalize(value).ToString(CultureInfo.InvariantCulture);
        public static int Next(int value) => Normalize(value) % 5 + 1;
        public static bool IsVertical(int value) => Normalize(value) == 2 || Normalize(value) == 4;
        public static bool ShowsCode(int value) => Normalize(value) == 1 || Normalize(value) == 2 || Normalize(value) == 5;
        public static bool HidesCandidates(int value) => Normalize(value) == 5;
    }
}
