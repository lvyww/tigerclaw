#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using TigerClaw.Pinyin;

namespace TigerClaw.Core.Tests;

internal static partial class Program
{
    private static void RunPinyinMenuTests()
    {
        var rows = new[] { ("你", "ni", 1000), ("泥", "ni", 500), ("妮", "ni", 1),
            ("好", "hao", 100), ("你好", "nihao", 10), ("泥好", "nihao", 20), ("吗", "ma", 100),
            ("西", "xi", 100), ("安", "an", 100), ("先", "xian", 100) };
        var tokens = rows.ToDictionary(r => (r.Item1, r.Item2), r => r.Item1.Length == 2
            ? new[] { r.Item1[..1] + "/ni", "好/hao" } : new[] { r.Item1 + "/" + r.Item2 });
        var lexicon = new Lexicon(rows, tokens: tokens);
        Candidate Sentence(string first, double score) => new(first + "好吗", score, 0,
            [new("ni", first, 0, 2, [first + "/ni"]), new("hao", "好", 2, 5, ["好/hao"]), new("ma", "吗", 5, 7, ["吗/ma"])]);
        var session = new PinyinSession();
        void Apply(double second, IReadOnlySet<string>? filter = null)
        {
            session.Reset("nihaoma");
            session.Apply(session.Generation, new([Sentence("你", 0), Sentence("泥", second)], 7, "", 0, true),
                lexicon: lexicon, spelling: PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Aliases, firstCharacters: filter);
        }
        Apply(-5);
        True(session.SentenceConfidence >= .9, "clear sentence confidence");
        Equal("你好吗|泥好|你好|你|泥|妮", string.Join('|', session.Choices.Select(c => c.Text)),
            "one sentence then word length and actual dictionary frequency, including off-beam homophone");
        True(session.Choices.Where(c => c.Text is "你" or "泥" or "妮").All(c => c.Consumed == 2), "no one-key abbreviation of full ni");
        Apply(-.1);
        Equal("你好吗|泥好吗|泥好|你好|你|泥|妮", string.Join('|', session.Choices.Select(c => c.Text)), "uncertain shows exactly two sentences before words");
        int rare = Array.FindIndex(session.Choices, c => c.Text == "妮");
        True(session.Confirm(rare, true) == null && session.LockedEnd == 2, "off-beam word can lock full spelling");
        True(session.Corrections.Count == 0, "single-character prefix never learns");
        using var decoder = new Decoder(lexicon, new PinyinTestModel(), 200);
        session.Apply(session.Generation, decoder.Decode(session.Raw, prefix: session.Prefix), lexicon: lexicon);
        Equal("妮好吗", session.Confirm(0, false), "off-beam prefix continues and commits");
        Apply(-5, new HashSet<string> { "你" });
        True(session.Choices.Skip(1).All(c => c.Text.StartsWith("你", StringComparison.Ordinal)), "prefix lookup respects auxiliary filtering");
        session.Reset("xi'anma");
        session.Apply(session.Generation, decoder.Decode(session.Raw), lexicon: lexicon);
        True(session.Choices.All(c => c.Text != "先"), "prefix dictionary never crosses forced syllable boundary");
        // Many plausible alternatives reduce mass even when the second alone is weak.
        session.Reset("nihaoma");
        var alternatives = Enumerable.Range(0, 20).Select(i => Sentence(((char)(0x4e00 + i)).ToString(), -4)).Prepend(Sentence("你", 0)).ToArray();
        session.Apply(session.Generation, new(alternatives, 7, "", 0, true), lexicon: lexicon);
        True(session.SentenceConfidence < .9 && session.Choices[1].Text == alternatives[1].Text, "confidence considers retained pool, not only top2 gap");
        var boundaryRows = new[] { ("常", "chang", 10), ("长", "chang", 100), ("常用", "changyong", 5),
            ("用", "yong", 10), ("字", "zi", 10), ("差", "cha", 999), ("查", "cha", 888), ("婵", "chan", 777), ("嗯", "ng", 1000) };
        var boundaryTokens = boundaryRows.ToDictionary(r => (r.Item1, r.Item2), r => r.Item1 == "常用"
            ? new[] { "常/chang", "用/yong" } : new[] { r.Item1 + "/" + r.Item2 });
        var boundaryLexicon = new Lexicon(boundaryRows, tokens: boundaryTokens);
        foreach (string raw in new[] { "changyongzi", "changyongz" })
        {
            session.Reset(raw);
            Candidate best = new("常用字", 0, 0, [new("changyong", "常用", 0, 9, ["常/chang", "用/yong"], RawEnds: [5, 9]),
                new("zi", "字", 9, raw.Length, ["字/zi"], Incomplete: raw.EndsWith('z'))]);
            session.Apply(session.Generation, new([best], raw.Length, "", 0, !raw.EndsWith('z')), lexicon: boundaryLexicon,
                spelling: PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Aliases);
            session.SetExtras(new PinyinEnglish(new[] { ("chan", "chan", 999), ("chang", "chang", 1) }).Candidates(raw, null));
            True(session.Choices.All(c => c.Text is not ("差" or "查" or "婵" or "chan" or "chang")), "no cha/chan cuts in chang " + raw);
            True(session.Choices.Any(c => c.Text == "常用" && c.Consumed == 9) && session.Choices.Any(c => c.Text == "长" && c.Consumed == 5), "legal Chinese cuts remain " + raw);
        }
        session.Reset("chang");
        session.Apply(session.Generation, new([new("常", 0, 0, [new("chang", "常", 0, 5, ["常/chang"])])], 5, "", 0, true),
            lexicon: boundaryLexicon, spelling: PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Aliases);
        True(session.Choices.All(c => c.Text is not ("差" or "查" or "婵")), "complete chang cannot leave incomplete g/ng suffix");
        session.Reset("xian");
        session.Apply(session.Generation, decoder.Decode(session.Raw), lexicon: lexicon);
        True(session.Choices.Any(c => c.Text == "先") && session.Choices.Any(c => c.Text == "西"), "both xian and xi/an are legal alternatives");
        Console.WriteLine("Pinyin sentence confidence/prefix length/frequency/off-beam/boundary tests passed.");
    }
}
