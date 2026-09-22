using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TigerClaw.Pinyin;

namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        private bool _pinyinLiteralMode;
        private string _pinyinAuxCode, _pinyinDecoderAux;
        private string PinyinLiveDisplay => PinyinOutput(_pinyinSession.LockedText) + _pinyinSession.Raw[_pinyinSession.LockedEnd..] + (_pinyinAuxCode == null ? "" : "`" + _pinyinAuxCode);
        private bool RebuildSpecialPinyin()
        {
            if (!_pinyinLiteralMode && !_pinyinSession.Raw.StartsWith("/", StringComparison.Ordinal)) return false;
            _pinyinSession.ApplySpecial(_pinyinLiteralMode ? new[] { _pinyinSession.Raw } : PinyinTools.Candidates(_pinyinSession.Raw, DateTime.Now),
                _pinyinLiteralMode ? "原文" : "工具 · 空格确认");
            return true;
        }
        private static void ApplyPinyinExtras(PinyinSession session, PinyinResources resources, string auxiliary, bool english, bool enableEmoji)
        {
            if (resources == null || auxiliary != null) { session.SetExtras(Array.Empty<PinyinChoice>()); return; }
            var extras = english
                ? resources.English.Candidates(session.Raw, session.Prefix).ToList()
                : new System.Collections.Generic.List<PinyinChoice>();
            if (enableEmoji && session.LockedEnd == 0 && session.Sentences.Length > 0)
            {
                string emoji = PinyinTools.Emoji(session.Sentences[0].Text);
                if (emoji != null) extras.Add(new(new(emoji, 0, 0, Array.Empty<Segment>()), emoji, session.Raw.Length, true, Literal: true, Annotation: "表情"));
            }
            bool exactPinyin = session.Sentences.Any(c => c.Segments.All(s => s.SpellingPenalty == 0 && !s.Incomplete));
            bool uppercase = session.Raw.Length > session.LockedEnd && char.IsUpper(session.Raw[session.LockedEnd]);
            session.SetExtras(extras, uppercase || !exactPinyin);
        }
        private bool ProcessPinyinExtraKey(int vk, bool shift, out KeyEngineResult result)
        {
            result = null;
            if (_pinyinAuxCode != null)
            {
                if (vk >= VK_A && vk <= VK_Z)
                { if (_pinyinAuxCode.Length < 8) _pinyinAuxCode += (char)('a' + vk - VK_A); }
                else if (vk == VK_BACK)
                    _pinyinAuxCode = _pinyinAuxCode.Length == 0 ? null : _pinyinAuxCode[..^1];
                else if (vk == VK_OEM_3 && !shift) _pinyinAuxCode = null;
                else if (vk == 0x25 || vk == 0x27 || vk == 0x24 || vk == 0x23)
                { _pinyinAuxCode = null; _pinyinSession.Invalidate(); RebuildFullPinyin(); return false; }
                else return false;
                _pinyinSession.Invalidate(); RebuildFullPinyin(); result = FullPinyinResult(); return true;
            }
            if (vk == VK_OEM_3 && !shift && !_pinyinLiteralMode && !_pinyinSession.Raw.StartsWith("/", StringComparison.Ordinal))
            { _pinyinAuxCode = ""; _pinyinSession.Invalidate(); RebuildFullPinyin(); result = FullPinyinResult(); return true; }
            bool special = _pinyinLiteralMode || _pinyinSession.Raw.StartsWith("/", StringComparison.Ordinal);
            if (!special && AsciiKey(vk, shift) is char punctuation && !char.IsAsciiLetterOrDigit(punctuation))
            {
                string raw = _pinyinSession.Raw;
                bool exactPinyin = _pinyinSession.Sentences.Any(c => c.Segments.All(s => s.SpellingPenalty == 0 && !s.Incomplete));
                bool start = punctuation == '@' || punctuation == ':' && (raw.Equals("http", StringComparison.OrdinalIgnoreCase) || raw.Equals("https", StringComparison.OrdinalIgnoreCase) || raw.Length == 1) ||
                    punctuation == '.' && (raw.Equals("www", StringComparison.OrdinalIgnoreCase) || !exactPinyin && _pinyinDecoderResources?.English.HasExact(raw) == true) ||
                    punctuation == '-' && raw.Equals("type", StringComparison.OrdinalIgnoreCase);
                if (start && _pinyinSession.LockedEnd == 0)
                { _pinyinLiteralMode = true; _pinyinSession.Corrections.Clear(); special = true; }
            }
            if (!special) return false;
            // Numeric expressions consume digits; completed named tools retain
            // the usual number-selection keys (date, symbols, emoji).
            bool namedTool = _pinyinSession.Raw is "/rq" or "/sj" or "/xq" or "/dt" or "/fh" or "/jt" or "/emoji";
            char? value = AsciiKey(vk, shift);
            if (value != null && !(namedTool && char.IsAsciiDigit(value.Value)))
            {
                _pinyinSession.Insert(value.Value); RebuildFullPinyin(); result = FullPinyinResult(); return true;
            }
            return false;
        }
        private static char? AsciiKey(int vk, bool shift)
        {
            if (vk >= 0x41 && vk <= 0x5a) return (char)((shift ? 'A' : 'a') + vk - 0x41);
            if (vk >= 0x30 && vk <= 0x39) return shift ? ")!@#$%^&*("[vk - 0x30] : (char)vk;
            if (vk >= 0x60 && vk <= 0x69) return (char)('0' + vk - 0x60);
            return vk switch
            {
                0xba => shift ? ':' : ';', 0xbb => shift ? '+' : '=', 0xbc => shift ? '<' : ',', 0xbd => shift ? '_' : '-',
                0xbe => shift ? '>' : '.', 0xbf => shift ? '?' : '/', 0xc0 => shift ? '~' : '`', 0xdb => shift ? '{' : '[',
                0xdc => shift ? '|' : '\\', 0xdd => shift ? '}' : ']', 0xde => shift ? '"' : '\'',
                0x6a => '*', 0x6b => '+', 0x6d => '-', 0x6e => '.', 0x6f => '/', _ => null
            };
        }
        private KeyEngineResult PinyinSelectCharacter(bool last)
        {
            if (!EnsureFullPinyinCurrent() || _pinyinSession.Choices.Length == 0) return FullPinyinResult();
            string selected = _pinyinSession.Choices[_pinyinSession.Selected].Text;
            var runes = selected.EnumerateRunes().ToArray();
            if (runes.Length == 0) return FullPinyinResult();
            // An explicit extraction commits only that character, discards the
            // composition and never reinforces a full-word correction.
            _pinyinSession.Corrections.Clear();
            return FinishFullPinyin(PinyinOutput((last ? runes[^1] : runes[0]).ToString()));
        }
        private string PinyinOutput(string text)
        {
            if (!_state.GetPinyinTraditionalEnabled() || string.IsNullOrEmpty(text)) return text;
            int length = LCMapStringEx("zh-CN", 0x04000000, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (length <= 0) return text;
            var output = new char[length];
            int written = LCMapStringEx("zh-CN", 0x04000000, text, text.Length, output, length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return written > 0 && written <= output.Length ? new string(output, 0, written) : text;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength, [Out] char[] destination,
            int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);
    }
}
