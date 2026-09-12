using System;
using System.Collections.Generic;

namespace TigerClaw.Core
{
    internal sealed class SmartSentenceSegment
    {
        public int Start;
        public int CodeEnd;
        public int End;
        public int Rank;
        public bool Closed;
    }

    internal static class SmartSentenceSegmentation
    {
        internal static bool IsLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

        internal static int CountLetters(string raw, int start = 0, int end = int.MaxValue)
        {
            int count = 0;
            for (int i = Math.Max(0, start); i < Math.Min(raw.Length, end); i++)
            {
                if (IsLetter(raw[i])) count++;
            }
            return count;
        }

        internal static bool TryParse(string raw, int maximum, out List<SmartSentenceSegment> segments, int selectionMask = 3)
        {
            segments = new List<SmartSentenceSegment>();
            maximum = Math.Max(1, maximum);
            int position = 0;
            while (position < raw.Length)
            {
                if (!IsLetter(raw[position])) return false;
                var segment = new SmartSentenceSegment { Start = position };
                while (position < raw.Length && IsLetter(raw[position]) && position - segment.Start < maximum)
                {
                    position++;
                }
                segment.CodeEnd = position;
                if (position < raw.Length)
                {
                    char mark = raw[position];
                    if ((mark >= '0' && mark <= '9') || mark == ';' || mark == '\'')
                    {
                        if ((mark == ';' && (selectionMask & 1) == 0) ||
                            (mark == '\'' && (selectionMask & 2) == 0)) return false;
                        segment.Rank = mark == ';' ? 2 : mark == '\'' ? 3 : mark == '0' ? 10 : mark - '0';
                        position++;
                        segment.Closed = true;
                    }
                    if (position < raw.Length && raw[position] == ' ')
                    {
                        position++;
                        segment.Closed = true;
                    }
                    if (position < raw.Length && IsLetter(raw[position])) segment.Closed = true;
                }
                segment.End = position;
                segments.Add(segment);
            }
            return segments.Count > 0;
        }
    }
}
