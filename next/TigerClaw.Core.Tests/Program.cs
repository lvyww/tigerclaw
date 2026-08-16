using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 && string.Equals(args[0], "--sentence-smoke", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceSmoke(args[1], args[2], args[3]);
                }
                if (args.Length == 1 && string.Equals(args[0], "--sentence-client-smoke", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceClientSmoke();
                }

                KeepsMaxLengthAsActiveCode();
                SealsSegmentOnlyAfterOverflow();
                PreservesCompletedSegmentWithoutCandidate();
                DecodesMultipleCompletedSegments();
                PreservesCandidateAndLiteralSegmentsInOrder();
                DecodesCandidateSegmentsCaseInsensitively();
                UsesCurrentPageCandidateAsSoftHint();
                BackspaceRebuildsFromRawCode();
                InvalidatesLookupCacheByLexiconVersion();
                ComposesChineseAndRawCommitTextSeparately();
                EngineKeepsRawCodeSeparateFromSurface();
                EngineCommitsRawCodeWhenSwitchingToEnglish();
                EngineStillCommitsPunctuationWithoutCandidates();
                EnginePreservesShiftedLetterInMixedInput();
                SentenceDecoderSupportsWordAndSelectionSuffix();
                SentenceDecoderRejectsEmbeddedBareOneKeyCharacter();
                SentenceDecoderRequiresExplicitSelectionForEveryCode();
                SentenceDecoderUsesOnlyTheOptimalCharacterCode();
                SentenceDecoderIncrementalMatchesFullRebuild();
                SentenceNgramV2LoadsFromMappedFile();
                SentenceEngineCommitsDecodedCandidate();
                SentenceEngineDisplaysPrimarySegmentation();
                SentenceEngineHonorsSelectionSymbolSettings();
                SentenceEngineKeepsArrowSelectionInPlace();
                SentenceEngineUsesTabToTraverseCandidates();
                SentenceEnginePassesCtrlNumberShortcut();
                SentenceEngineCommitsSmartQuoteAfterCandidate();
                SentenceEngineRejectsStaleNeuralResult();
                SentenceEngineReranksOnlyTopFive();
                KeyReplayCacheReturnsOriginalResultWithCurrentSequence();
                SentenceEngineDecodesLongWorkOffTheKeyPath();
                SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending();
                SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending();
                Console.WriteLine("TigerClaw.Core.Tests: all tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("TigerClaw.Core.Tests: " + ex.Message);
                return 1;
            }
        }

        private static void KeepsMaxLengthAsActiveCode()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "你" }
            });

            MixedInputDecodeResult result = Decode(decoder, "abcd", 4, 1);
            Equal(string.Empty, result.ResolvedPrefixText, nameof(KeepsMaxLengthAsActiveCode));
            Equal("abcd", result.ActiveCode, nameof(KeepsMaxLengthAsActiveCode));
            Equal("abcd", result.SurfaceText, nameof(KeepsMaxLengthAsActiveCode));
        }

        private static void SealsSegmentOnlyAfterOverflow()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "你" }
            });

            MixedInputDecodeResult result = Decode(decoder, "abcde", 4, 1);
            Equal("你", result.ResolvedPrefixText, nameof(SealsSegmentOnlyAfterOverflow));
            Equal("e", result.ActiveCode, nameof(SealsSegmentOnlyAfterOverflow));
            Equal("你e", result.SurfaceText, nameof(SealsSegmentOnlyAfterOverflow));
        }

        private static void PreservesCompletedSegmentWithoutCandidate()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>());
            MixedInputDecodeResult result = Decode(decoder, "abcde", 4, 1);
            Equal("abcd", result.ResolvedPrefixText, nameof(PreservesCompletedSegmentWithoutCandidate));
            Equal("abcde", result.SurfaceText, nameof(PreservesCompletedSegmentWithoutCandidate));
            True(result.Segments.Length == 1 && !result.Segments[0].HasCandidate, nameof(PreservesCompletedSegmentWithoutCandidate));
        }

        private static void DecodesMultipleCompletedSegments()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "你" },
                ["efgh"] = new List<string> { "好" }
            });

            MixedInputDecodeResult result = Decode(decoder, "abcdefghi", 4, 2);
            Equal("你好", result.ResolvedPrefixText, nameof(DecodesMultipleCompletedSegments));
            Equal("i", result.ActiveCode, nameof(DecodesMultipleCompletedSegments));
            Equal("你好i", result.SurfaceText, nameof(DecodesMultipleCompletedSegments));
        }

        private static void PreservesCandidateAndLiteralSegmentsInOrder()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "你" }
            });

            MixedInputDecodeResult result = Decode(decoder, "abcdefghi", 4, 2);
            Equal("你efgh", result.ResolvedPrefixText, nameof(PreservesCandidateAndLiteralSegmentsInOrder));
            Equal("你efghi", result.SurfaceText, nameof(PreservesCandidateAndLiteralSegmentsInOrder));
        }

        private static void DecodesCandidateSegmentsCaseInsensitively()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["abcd"] = new List<string> { "你" }
                });

            MixedInputDecodeResult result = Decode(decoder, "abCde", 4, 1);
            Equal("你e", result.SurfaceText, nameof(DecodesCandidateSegmentsCaseInsensitively));
            Equal("abCde", result.RawCode, nameof(DecodesCandidateSegmentsCaseInsensitively));
        }

        private static void UsesCurrentPageCandidateAsSoftHint()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "你", "您" }
            });
            var preferred = new Dictionary<int, string> { [0] = "您" };

            MixedInputDecodeResult result = Decode(decoder, "abcde", 4, 1, preferred);
            Equal("您e", result.SurfaceText, nameof(UsesCurrentPageCandidateAsSoftHint));
        }

        private static void BackspaceRebuildsFromRawCode()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "你" }
            });

            Decode(decoder, "abcde", 4, 1);
            MixedInputDecodeResult result = Decode(decoder, "abcd", 4, 1);
            Equal(string.Empty, result.ResolvedPrefixText, nameof(BackspaceRebuildsFromRawCode));
            Equal("abcd", result.ActiveCode, nameof(BackspaceRebuildsFromRawCode));
        }

        private static void InvalidatesLookupCacheByLexiconVersion()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "旧" }
            };
            FixedLengthMixedInputDecoder decoder = CreateDecoder(lexicon);
            Equal("旧e", Decode(decoder, "abcde", 4, 1).SurfaceText, nameof(InvalidatesLookupCacheByLexiconVersion));

            lexicon["abcd"] = new List<string> { "新" };
            Equal("旧e", Decode(decoder, "abcde", 4, 1).SurfaceText, nameof(InvalidatesLookupCacheByLexiconVersion));
            Equal("新e", Decode(decoder, "abcde", 4, 2).SurfaceText, nameof(InvalidatesLookupCacheByLexiconVersion));
        }

        private static void ComposesChineseAndRawCommitTextSeparately()
        {
            FixedLengthMixedInputDecoder decoder = CreateDecoder(new Dictionary<string, List<string>>
            {
                ["abcd"] = new List<string> { "你" }
            });
            MixedInputDecodeResult result = Decode(decoder, "abcde", 4, 1);

            Equal("你好！", MixedInputCommitComposer.ComposeChinese(result, "好", "！"), nameof(ComposesChineseAndRawCommitTextSeparately));
            Equal("abcde", MixedInputCommitComposer.ComposeRaw(result), nameof(ComposesChineseAndRawCommitTextSeparately));

            FixedLengthMixedInputDecoder literalDecoder = CreateDecoder(new Dictionary<string, List<string>>());
            MixedInputDecodeResult literalResult = Decode(literalDecoder, "abcde", 4, 1);
            Equal("abcd好", MixedInputCommitComposer.ComposeChinese(literalResult, "好"), nameof(ComposesChineseAndRawCommitTextSeparately));
        }

        private static void EngineKeepsRawCodeSeparateFromSurface()
        {
            InputMethodEngine engine = CreateMixedEngine();
            TypeLetters(engine, "abcde");

            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            Equal("abcde", snapshot.InputCode, nameof(EngineKeepsRawCodeSeparateFromSurface));
            Equal("e", snapshot.ActiveInputCode, nameof(EngineKeepsRawCodeSeparateFromSurface));

            KeyEngineResult enter = Press(engine, 0x0D);
            Equal("abcde", enter.TextToOutput, nameof(EngineKeepsRawCodeSeparateFromSurface));
            True(!engine.GetUiSnapshot(5).IsComposing, nameof(EngineKeepsRawCodeSeparateFromSurface));
        }

        private static void EngineCommitsRawCodeWhenSwitchingToEnglish()
        {
            InputMethodEngine engine = CreateMixedEngine();
            TypeLetters(engine, "abcde");

            engine.ToggleChinese(out string committed);
            Equal("abcde", committed, nameof(EngineCommitsRawCodeWhenSwitchingToEnglish));
            True(!engine.IsChinese, nameof(EngineCommitsRawCodeWhenSwitchingToEnglish));
        }

        private static void EngineStillCommitsPunctuationWithoutCandidates()
        {
            InputMethodEngine engine = CreateMixedEngine();
            TypeLetters(engine, "abcde");

            KeyEngineResult punctuation = Press(engine, 0xBE);
            Equal("abcd。", punctuation.TextToOutput, nameof(EngineStillCommitsPunctuationWithoutCandidates));
            True(!engine.GetUiSnapshot(5).IsComposing, nameof(EngineStillCommitsPunctuationWithoutCandidates));
        }

        private static void EnginePreservesShiftedLetterInMixedInput()
        {
            InputMethodEngine enterEngine = CreateMixedEngine();
            TypeMixedCaseLetters(enterEngine);

            EngineUiSnapshot snapshot = enterEngine.GetUiSnapshot(5);
            Equal("abCde", snapshot.InputCode, nameof(EnginePreservesShiftedLetterInMixedInput));
            Equal("e", snapshot.ActiveInputCode, nameof(EnginePreservesShiftedLetterInMixedInput));
            True(enterEngine.IsChinese, nameof(EnginePreservesShiftedLetterInMixedInput));

            KeyEngineResult enter = Press(enterEngine, 0x0D);
            Equal("abCde", enter.TextToOutput, nameof(EnginePreservesShiftedLetterInMixedInput));

            InputMethodEngine punctuationEngine = CreateMixedEngine();
            TypeMixedCaseLetters(punctuationEngine);
            KeyEngineResult punctuation = Press(punctuationEngine, 0xBE);
            Equal("abCd。", punctuation.TextToOutput, nameof(EnginePreservesShiftedLetterInMixedInput));
        }

        private static int RunSentenceSmoke(string lexiconPath, string modelPath, string code)
        {
            var source = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadLines(lexiconPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                string text = parts[0];
                string key = parts[1].ToLowerInvariant();
                if (!source.TryGetValue(key, out List<string> values))
                {
                    values = new List<string>();
                    source[key] = values;
                }
                if (!values.Contains(text))
                {
                    values.Add(text);
                }
            }

            Stopwatch watch = Stopwatch.StartNew();
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(source);
            ISentenceLanguageModel model = SentenceNgramModel.Load(modelPath);
            long loadMilliseconds = watch.ElapsedMilliseconds;
            var decoder = new SentenceInputDecoder(index, model);
            watch.Restart();
            SentenceDecodeResult result = decoder.Decode(code, 20);
            watch.Stop();

            Console.WriteLine("load_ms=" + loadMilliseconds + ", decode_ms=" + watch.ElapsedMilliseconds +
                              ", expanded=" + result.ExpandedStates + ", candidates=" + result.Candidates.Length);
            for (int indexValue = 0; indexValue < result.Candidates.Length; indexValue++)
            {
                SentenceCandidate candidate = result.Candidates[indexValue];
                Console.WriteLine((indexValue + 1) + "\t" + candidate.Text + "\t" + candidate.FinalScore.ToString("F4"));
            }
            return result.Candidates.Length > 0 ? 0 : 2;
        }

        private static void SentenceNgramV2LoadsFromMappedFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "tigerclaw-ngram-v2-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                using (var writer = new BinaryWriter(File.Create(path), Encoding.UTF8, false))
                {
                    writer.Write(Encoding.ASCII.GetBytes("TCSKNM01"));
                    writer.Write(1);
                    writer.Write(2);
                    writer.Write(0);
                    writer.Write(0.1f);
                    writer.Write((int)'a');
                    writer.Write(0.9f);
                    writer.Write((long)0);
                    writer.Write(0);
                    writer.Write((long)0);
                    writer.Write((long)0);
                }

                using (SentenceNgramModel model = SentenceNgramModel.Load(path))
                {
                    double known = Math.Exp(model.LogProbability("\x02", "\x02", "a"));
                    double unknown = Math.Exp(model.LogProbability("\x02", "\x02", "b"));
                    True(Math.Abs(known - 0.9) < 1e-6, nameof(SentenceNgramV2LoadsFromMappedFile) + ".known");
                    True(Math.Abs(unknown - 0.1) < 1e-6, nameof(SentenceNgramV2LoadsFromMappedFile) + ".unknown");
                }
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static int RunSentenceClientSmoke()
        {
            var state = new CoreRuntimeState();
            state.TrySetConfigValue("整句输入", "是", out _, out _);
            state.TrySetConfigValue("整句神经重排", "是", out _, out _);
            using (var completed = new ManualResetEvent(false))
            {
                double[] receivedScores = null;
                using (var client = new SentenceRerankClient(
                    state,
                    new ProcessLauncher(),
                    (generation, rawCode, scores) =>
                    {
                        if (generation == 7 && rawCode == "smoke")
                        {
                            receivedScores = scores;
                            completed.Set();
                        }
                    }))
                {
                    client.Request(new SentenceRerankRequest
                    {
                        Generation = 7,
                        RawCode = "smoke",
                        Candidates = new[]
                        {
                            "今天早上我吃了两个面包三根油条",
                            "今天早上我吃了两个面有三根油条",
                            "今天早上我呆了两个面包三根油条"
                        }
                    });

                    if (!completed.WaitOne(TimeSpan.FromSeconds(30)))
                    {
                        Console.Error.WriteLine("sentence client timed out");
                        return 2;
                    }
                }

                Console.WriteLine("scores=" + string.Join(",", receivedScores ?? Array.Empty<double>()));
                return receivedScores != null && receivedScores.Length == 3 ? 0 : 3;
            }
        }

        private static void SentenceDecoderSupportsWordAndSelectionSuffix()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ot"] = new List<string> { "是" },
                ["j"] = new List<string> { "人", "什么", "怎样" }
            });

            SentenceDecodeResult numeric = decoder.Decode("otj2");
            Equal("是什么", numeric.Candidates[0].Text, nameof(SentenceDecoderSupportsWordAndSelectionSuffix));

            SentenceDecodeResult semicolon = decoder.Decode("j;");
            Equal("什么", semicolon.Candidates[0].Text, nameof(SentenceDecoderSupportsWordAndSelectionSuffix));

            SentenceDecodeResult quote = decoder.Decode("j'");
            Equal("怎样", quote.Candidates[0].Text, nameof(SentenceDecoderSupportsWordAndSelectionSuffix));
            Equal("j'", quote.Candidates[0].SegmentedCode, nameof(SentenceDecoderSupportsWordAndSelectionSuffix));

            SentenceDecodeResult numericThird = decoder.Decode("j3");
            Equal("怎样", numericThird.Candidates[0].Text, nameof(SentenceDecoderSupportsWordAndSelectionSuffix));
        }

        private static void SentenceDecoderRejectsEmbeddedBareOneKeyCharacter()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ot"] = new List<string> { "是" },
                ["j"] = new List<string> { "人", "什么" }
            });

            True(decoder.Decode("otj").Candidates.Length == 0, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
            SentenceDecodeResult standalone = decoder.Decode("j");
            Equal("人", standalone.Candidates[0].Text, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
            True(standalone.Candidates.Length == 1, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
            True(
                Array.TrueForAll(standalone.Candidates, candidate => candidate.Text != "什么"),
                nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));

            SentenceDecodeResult numeric = decoder.Decode("j2");
            Equal("什么", numeric.Candidates[0].Text, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
            True(numeric.Candidates.Length == 1, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));

            SentenceDecodeResult semicolon = decoder.Decode("j;");
            Equal("什么", semicolon.Candidates[0].Text, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
            True(semicolon.Candidates.Length == 1, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
        }

        private static void SentenceDecoderIncrementalMatchesFullRebuild()
        {
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ot"] = new List<string> { "是" },
                    ["ue"] = new List<string> { "的" },
                    ["tu"] = new List<string> { "我" },
                    ["j"] = new List<string> { "人", "什么", "怎样" },
                    ["jq"] = new List<string> { "件" },
                    ["fi"] = new List<string> { "一", "一般" }
                }),
                new DistinctSentenceLanguageModel(),
                beamWidth: 20);

            string[] samples =
            {
                "ot",
                "ueot",
                "ueottu",
                "otj2",
                "ueot;",
                "j'",
                "fiot",
                "jqtusotu"
            };

            foreach (string sample in samples)
            {
                decoder.ResetDecodeCache();
                SentenceDecodeResult grown = SentenceDecodeResult.Empty;
                for (int length = 1; length <= sample.Length; length++)
                {
                    grown = decoder.Decode(sample.Substring(0, length));
                }

                AssertSentenceResultsEqual(
                    grown,
                    decoder.DecodeFull(sample),
                    nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".grow." + sample);

                SentenceDecodeResult same = decoder.Decode(sample);
                AssertSentenceResultsEqual(
                    same,
                    decoder.DecodeFull(sample),
                    nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".same." + sample);
            }

            decoder.ResetDecodeCache();
            const string longCode = "jqtusotu";
            decoder.Decode(longCode);
            for (int length = longCode.Length - 1; length >= 1; length--)
            {
                string prefix = longCode.Substring(0, length);
                AssertSentenceResultsEqual(
                    decoder.Decode(prefix),
                    decoder.DecodeFull(prefix),
                    nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".back." + prefix);
            }

            decoder.ResetDecodeCache();
            decoder.Decode("u");
            AssertSentenceResultsEqual(
                decoder.Decode("ue"),
                decoder.DecodeFull("ue"),
                nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".one_key_to_two");
            AssertSentenceResultsEqual(
                decoder.Decode("u"),
                decoder.DecodeFull("u"),
                nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".two_back_to_one");

            decoder.ResetDecodeCache();
            for (int length = 1; length <= 4; length++)
            {
                decoder.Decode("ueot".Substring(0, length));
            }

            AssertSentenceResultsEqual(
                decoder.Decode("ueot;"),
                decoder.DecodeFull("ueot;"),
                nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".append_selector");
            AssertSentenceResultsEqual(
                decoder.Decode("ueot"),
                decoder.DecodeFull("ueot"),
                nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".backspace_selector");
            AssertSentenceResultsEqual(
                decoder.Decode("ueottu"),
                decoder.DecodeFull("ueottu"),
                nameof(SentenceDecoderIncrementalMatchesFullRebuild) + ".retype_after_selector");
        }

        private static void AssertSentenceResultsEqual(
            SentenceDecodeResult left,
            SentenceDecodeResult right,
            string name)
        {
            SentenceCandidate[] leftCandidates = left.Candidates ?? Array.Empty<SentenceCandidate>();
            SentenceCandidate[] rightCandidates = right.Candidates ?? Array.Empty<SentenceCandidate>();
            True(leftCandidates.Length == rightCandidates.Length, name + ".count");
            for (int index = 0; index < leftCandidates.Length; index++)
            {
                Equal(rightCandidates[index].Text, leftCandidates[index].Text, name + ".text." + index);
                Equal(
                    rightCandidates[index].SegmentedCode,
                    leftCandidates[index].SegmentedCode,
                    name + ".seg." + index);
                True(
                    leftCandidates[index].FinalScore == rightCandidates[index].FinalScore,
                    name + ".score." + index);
            }
        }

        private sealed class DistinctSentenceLanguageModel : ISentenceLanguageModel
        {
            public double LogProbability(string previous2, string previous1, string target)
            {
                return -(
                    HashToken(previous2) * 0.031 +
                    HashToken(previous1) * 0.017 +
                    HashToken(target) * 0.011);
            }

            private static int HashToken(string token)
            {
                if (string.IsNullOrEmpty(token))
                {
                    return 1;
                }

                int hash = 17;
                for (int index = 0; index < token.Length; index++)
                {
                    hash = unchecked(hash * 31 + token[index]);
                }

                return Math.Abs(hash % 997) + 1;
            }
        }

        private static void SentenceDecoderUsesOnlyTheOptimalCharacterCode()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "乙", "甲" },
                ["ac"] = new List<string> { "甲", "丙" }
            });

            True(
                Array.TrueForAll(decoder.Decode("ab").Candidates, candidate => candidate.Text != "甲"),
                nameof(SentenceDecoderUsesOnlyTheOptimalCharacterCode));
            Equal("甲", decoder.Decode("ac").Candidates[0].Text, nameof(SentenceDecoderUsesOnlyTheOptimalCharacterCode));
        }

        private static void SentenceDecoderRequiresExplicitSelectionForEveryCode()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["fi"] = new List<string> { "一", "一般" },
                ["ot"] = new List<string> { "是" }
            });

            SentenceDecodeResult bare = decoder.Decode("fi");
            Equal("一", bare.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.TrueForAll(bare.Candidates, candidate => candidate.Text != "一般"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));

            SentenceDecodeResult prefix = decoder.Decode("fiot");
            Equal("一是", prefix.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            Equal("fi ot", prefix.Candidates[0].SegmentedCode,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.TrueForAll(prefix.Candidates, candidate => candidate.Text != "一般是"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));

            SentenceDecodeResult suffix = decoder.Decode("otfi");
            Equal("是一", suffix.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.TrueForAll(suffix.Candidates, candidate => candidate.Text != "是一般"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));

            Equal("一般", decoder.Decode("fi2").Candidates[0].Text,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            Equal("一般", decoder.Decode("fi;").Candidates[0].Text,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            Equal("fi;", decoder.Decode("fi;").Candidates[0].SegmentedCode,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
        }

        private static void SentenceEngineCommitsDecodedCandidate()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string reason),
                nameof(SentenceEngineCommitsDecodedCandidate) + ": " + reason);
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ot"] = new List<string> { "是" },
                ["j"] = new List<string> { "人", "什么" }
            });
            var engine = new InputMethodEngine(state, decoder);

            TypeLetters(engine, "otj");
            Press(engine, 0x32);
            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            Equal("ot j2", snapshot.InputCode, nameof(SentenceEngineCommitsDecodedCandidate));
            Equal("是什么", snapshot.Candidates[0], nameof(SentenceEngineCommitsDecodedCandidate));
            Equal("是什么", Press(engine, 0x20).TextToOutput, nameof(SentenceEngineCommitsDecodedCandidate));
        }

        private static void SentenceEngineKeepsArrowSelectionInPlace()
        {
            InputMethodEngine engine = CreateSentenceEngine(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["abcd"] = new List<string> { "丙" }
            });
            TypeLetters(engine, "abcd");
            EngineUiSnapshot before = engine.GetUiSnapshot(5);
            True(before.Candidates.Length >= 2, nameof(SentenceEngineKeepsArrowSelectionInPlace));
            string first = before.Candidates[0];
            string second = before.Candidates[1];
            string secondSegmentedCode = string.Equals(second, "丙", StringComparison.Ordinal) ? "abcd" : "ab cd";
            Press(engine, 0x28);
            EngineUiSnapshot selected = engine.GetUiSnapshot(5);
            Equal(first, selected.Candidates[0], nameof(SentenceEngineKeepsArrowSelectionInPlace) + ".stable_order");
            True(selected.SelectedCandidateIndex == 1,
                nameof(SentenceEngineKeepsArrowSelectionInPlace) + ".selected_index");
            Equal(secondSegmentedCode, selected.ActiveInputCode, nameof(SentenceEngineKeepsArrowSelectionInPlace) + ".segmentation");
            Equal(second, Press(engine, 0x20).TextToOutput, nameof(SentenceEngineKeepsArrowSelectionInPlace) + ".commit");
        }

        private static void SentenceEngineUsesTabToTraverseCandidates()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string sentenceReason),
                nameof(SentenceEngineUsesTabToTraverseCandidates) + ": " + sentenceReason);
            True(state.TrySetConfigValue("TAB清屏", "否", out _, out string tabReason),
                nameof(SentenceEngineUsesTabToTraverseCandidates) + ": " + tabReason);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["abcd"] = new List<string> { "丙" }
            }));

            TypeLetters(engine, "abcd");
            EngineUiSnapshot before = engine.GetUiSnapshot(5);
            True(before.Candidates.Length >= 2, nameof(SentenceEngineUsesTabToTraverseCandidates));
            Press(engine, 0x09);
            True(engine.GetUiSnapshot(5).SelectedCandidateIndex == 1,
                nameof(SentenceEngineUsesTabToTraverseCandidates) + ".forward");
            Press(engine, 0x09, shift: true);
            True(engine.GetUiSnapshot(5).SelectedCandidateIndex == 0,
                nameof(SentenceEngineUsesTabToTraverseCandidates) + ".backward");
        }

        private static void SentenceEnginePassesCtrlNumberShortcut()
        {
            InputMethodEngine engine = CreateSentenceEngine(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" }
            });
            TypeLetters(engine, "ab");

            KeyEngineResult result = PressWithCtrl(engine, 0x31);
            True(!result.Handled, nameof(SentenceEnginePassesCtrlNumberShortcut) + ".passed");
            True(result.CancelComposition, nameof(SentenceEnginePassesCtrlNumberShortcut) + ".canceled_composition");
        }

        private static void SentenceEngineHonorsSelectionSymbolSettings()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["fi"] = new List<string> { "一", "一般", "一起" }
            };

            InputMethodEngine semicolonEnabled = CreateSentenceEngine(lexicon);
            TypeLetters(semicolonEnabled, "fi");
            Press(semicolonEnabled, 0xBA);
            EngineUiSnapshot semicolonSnapshot = semicolonEnabled.GetUiSnapshot(5);
            Equal("fi;", semicolonSnapshot.InputCode, nameof(SentenceEngineHonorsSelectionSymbolSettings));
            Equal("一般", semicolonSnapshot.Candidates[0], nameof(SentenceEngineHonorsSelectionSymbolSettings));

            InputMethodEngine quoteEnabled = CreateSentenceEngine(lexicon);
            TypeLetters(quoteEnabled, "fi");
            Press(quoteEnabled, 0xDE);
            EngineUiSnapshot quoteSnapshot = quoteEnabled.GetUiSnapshot(5);
            Equal("fi'", quoteSnapshot.InputCode, nameof(SentenceEngineHonorsSelectionSymbolSettings));
            Equal("一起", quoteSnapshot.Candidates[0], nameof(SentenceEngineHonorsSelectionSymbolSettings));

            var disabledState = new CoreRuntimeState();
            True(disabledState.TrySetConfigValue("整句输入", "是", out _, out string sentenceReason),
                nameof(SentenceEngineHonorsSelectionSymbolSettings) + ": " + sentenceReason);
            True(disabledState.TrySetConfigValue("分号次选", "否", out _, out string semicolonReason),
                nameof(SentenceEngineHonorsSelectionSymbolSettings) + ": " + semicolonReason);
            True(disabledState.TrySetConfigValue("引号三选", "否", out _, out string quoteReason),
                nameof(SentenceEngineHonorsSelectionSymbolSettings) + ": " + quoteReason);

            var semicolonDisabled = new InputMethodEngine(disabledState, CreateSentenceDecoder(lexicon));
            TypeLetters(semicolonDisabled, "fi");
            Equal("一；", Press(semicolonDisabled, 0xBA).TextToOutput,
                nameof(SentenceEngineHonorsSelectionSymbolSettings));

            var quoteDisabled = new InputMethodEngine(disabledState, CreateSentenceDecoder(lexicon));
            TypeLetters(quoteDisabled, "fi");
            Equal("一‘", Press(quoteDisabled, 0xDE).TextToOutput,
                nameof(SentenceEngineHonorsSelectionSymbolSettings));
        }

        private static void SentenceEngineDisplaysPrimarySegmentation()
        {
            InputMethodEngine engine = CreateSentenceEngine(new Dictionary<string, List<string>>
            {
                ["fi"] = new List<string> { "一", "一般" },
                ["ot"] = new List<string> { "是" }
            });
            TypeLetters(engine, "fiot");

            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            Equal("fi ot", snapshot.InputCode, nameof(SentenceEngineDisplaysPrimarySegmentation));
            Equal("fi ot", snapshot.ActiveInputCode, nameof(SentenceEngineDisplaysPrimarySegmentation));
            engine.GetCompositionDisplayParts(out string prefix, out string activeCode);
            Equal(string.Empty, prefix, nameof(SentenceEngineDisplaysPrimarySegmentation));
            Equal("fi ot", activeCode, nameof(SentenceEngineDisplaysPrimarySegmentation));
        }

        private static void SentenceEngineCommitsSmartQuoteAfterCandidate()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string sentenceReason),
                nameof(SentenceEngineCommitsSmartQuoteAfterCandidate) + ": " + sentenceReason);
            True(state.TrySetConfigValue("引号三选", "否", out _, out string quoteReason),
                nameof(SentenceEngineCommitsSmartQuoteAfterCandidate) + ": " + quoteReason);
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" }
            });
            var engine = new InputMethodEngine(state, decoder);
            TypeLetters(engine, "ab");
            Equal("甲‘", Press(engine, 0xDE).TextToOutput, nameof(SentenceEngineCommitsSmartQuoteAfterCandidate));
        }

        private static void SentenceEngineRejectsStaleNeuralResult()
        {
            InputMethodEngine engine = CreateSentenceEngine(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["abcd"] = new List<string> { "丙" }
            });
            TypeLetters(engine, "abcd");
            EngineUiSnapshot before = engine.GetUiSnapshot(5);
            True(before.Candidates.Length == 2, nameof(SentenceEngineRejectsStaleNeuralResult));
            string originalFirst = before.Candidates[0];
            string originalSecond = before.Candidates[1];

            True(!engine.ApplySentenceNeuralScores(4, "abcd", new[] { -100.0, 0.0 }),
                nameof(SentenceEngineRejectsStaleNeuralResult));
            Equal(originalFirst, engine.GetUiSnapshot(5).Candidates[0], nameof(SentenceEngineRejectsStaleNeuralResult));
            True(engine.ApplySentenceNeuralScores(5, "abcd", new[] { -100.0, 0.0 }),
                nameof(SentenceEngineRejectsStaleNeuralResult));
            EngineUiSnapshot reranked = engine.GetUiSnapshot(5);
            Equal(originalSecond, reranked.Candidates[0], nameof(SentenceEngineRejectsStaleNeuralResult));
            Equal(
                string.Equals(originalSecond, "丙", StringComparison.Ordinal) ? "abcd" : "ab cd",
                reranked.ActiveInputCode,
                nameof(SentenceEngineRejectsStaleNeuralResult));
        }

        private static void SentenceEngineReranksOnlyTopFive()
        {
            const string rawCode = "abcdefgh";
            var lexicon = new Dictionary<string, List<string>>();
            int textValue = 0x4E00;
            for (int start = 0; start < rawCode.Length - 1; start++)
            {
                for (int length = 2; start + length <= rawCode.Length; length++)
                {
                    lexicon[rawCode.Substring(start, length)] =
                        new List<string> { char.ConvertFromUtf32(textValue++) };
                }
            }

            InputMethodEngine engine = CreateSentenceEngine(lexicon);
            var reranker = new RecordingSentenceRerankService();
            engine.SetSentenceRerankService(reranker);
            TypeLetters(engine, rawCode);

            EngineUiSnapshot before = engine.GetUiSnapshot(20);
            True(before.Candidates.Length > 5, nameof(SentenceEngineReranksOnlyTopFive) + ".candidate_count");
            True(reranker.LastRequest != null && reranker.LastRequest.Candidates.Length == 5,
                nameof(SentenceEngineReranksOnlyTopFive) + ".request_count");
            string promoted = before.Candidates[4];
            var preservedTail = new string[before.Candidates.Length - 5];
            Array.Copy(before.Candidates, 5, preservedTail, 0, preservedTail.Length);

            True(!engine.ApplySentenceNeuralScores(
                    reranker.LastRequest.Generation,
                    rawCode,
                    new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }),
                nameof(SentenceEngineReranksOnlyTopFive) + ".reject_six_scores");
            True(engine.ApplySentenceNeuralScores(
                    reranker.LastRequest.Generation,
                    rawCode,
                    new[] { -100.0, -100.0, -100.0, -100.0, 0.0 }),
                nameof(SentenceEngineReranksOnlyTopFive) + ".accept_five_scores");

            EngineUiSnapshot after = engine.GetUiSnapshot(20);
            Equal(promoted, after.Candidates[0], nameof(SentenceEngineReranksOnlyTopFive) + ".promoted");
            for (int index = 0; index < preservedTail.Length; index++)
            {
                Equal(
                    preservedTail[index],
                    after.Candidates[index + 5],
                    nameof(SentenceEngineReranksOnlyTopFive) + ".tail_" + index);
            }
        }

        private static InputMethodEngine CreateMixedEngine()
        {
            var state = new CoreRuntimeState();
            True(
                state.TrySetConfigValue("中英文不限长混合输入", "是", out _, out string reason),
                nameof(CreateMixedEngine) + ": " + reason);
            return new InputMethodEngine(state);
        }

        private static InputMethodEngine CreateSentenceEngine(Dictionary<string, List<string>> lexicon)
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string reason),
                nameof(CreateSentenceEngine) + ": " + reason);
            return new InputMethodEngine(state, CreateSentenceDecoder(lexicon));
        }

        private static void TypeLetters(InputMethodEngine engine, string text)
        {
            foreach (char c in text)
            {
                Press(engine, char.ToUpperInvariant(c));
            }
        }

        private static void TypeMixedCaseLetters(InputMethodEngine engine)
        {
            TypeLetters(engine, "ab");
            SendKey(engine, 0x10, "down", true);
            SendKey(engine, 0x43, "down", true);
            SendKey(engine, 0x43, "up", true);
            SendKey(engine, 0x10, "up", false);
            TypeLetters(engine, "de");
        }

        private static KeyEngineResult Press(InputMethodEngine engine, int vk, bool shift = false)
        {
            return SendKey(engine, vk, "down", shift);
        }

        private static KeyEngineResult PressWithCtrl(InputMethodEngine engine, int vk)
        {
            KeyEngineResult result = engine.ProcessKey(
                vk,
                0,
                "down",
                shift: false,
                ctrl: true,
                alt: false,
                win: false,
                capsLock: false,
                numLock: false,
                repeat: 1,
                extended: false);
            engine.PostProcessKey(vk, "down", result, false, true, false, false, false);
            return result;
        }

        private static KeyEngineResult SendKey(InputMethodEngine engine, int vk, string action, bool shift)
        {
            KeyEngineResult result = engine.ProcessKey(
                vk,
                0,
                action,
                shift: shift,
                ctrl: false,
                alt: false,
                win: false,
                capsLock: false,
                numLock: false,
                repeat: 1,
                extended: false);
            engine.PostProcessKey(vk, action, result, shift, false, false, false, false);
            return result;
        }

        private static FixedLengthMixedInputDecoder CreateDecoder(Dictionary<string, List<string>> lexicon)
        {
            return new FixedLengthMixedInputDecoder(
                code => lexicon.TryGetValue(code, out List<string> candidates) ? new List<string>(candidates) : null,
                candidate => candidate);
        }

        private static SentenceInputDecoder CreateSentenceDecoder(Dictionary<string, List<string>> lexicon)
        {
            return new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);
        }

        private static void KeyReplayCacheReturnsOriginalResultWithCurrentSequence()
        {
            var cache = new KeyRequestReplayCache(16);
            string key = KeyRequestReplayCache.BuildKey("frontend-a", "17");
            cache.Store(key, "{\"type\":\"response\",\"seq\":101,\"success\":true,\"handled\":true,\"commit_text\":\"一\"}");

            True(cache.TryGet(key, 202, out string replayed), nameof(KeyReplayCacheReturnsOriginalResultWithCurrentSequence));
            True(replayed.Contains("\"seq\":202,"), nameof(KeyReplayCacheReturnsOriginalResultWithCurrentSequence) + ".seq");
            True(replayed.Contains("\"commit_text\":\"一\""), nameof(KeyReplayCacheReturnsOriginalResultWithCurrentSequence) + ".commit");
            True(!cache.TryGet(KeyRequestReplayCache.BuildKey("frontend-a", "18"), 203, out _), nameof(KeyReplayCacheReturnsOriginalResultWithCurrentSequence) + ".different_event");
            True(!cache.TryGet(KeyRequestReplayCache.BuildKey("frontend-b", "17"), 204, out _), nameof(KeyReplayCacheReturnsOriginalResultWithCurrentSequence) + ".different_frontend");
        }

        private static void SentenceEngineDecodesLongWorkOffTheKeyPath()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string reason),
                nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ": " + reason);
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "你" }
                }),
                new SlowSentenceLanguageModel(),
                beamWidth: 10);
            var engine = new InputMethodEngine(state, decoder, sentenceDecodeSynchronously: false);
            using (var completed = new ManualResetEvent(false))
            {
                engine.SetSentenceDecodeCompletedCallback(() => completed.Set());
                var stopwatch = Stopwatch.StartNew();
                Press(engine, 0x41);
                Press(engine, 0x42);
                stopwatch.Stop();
                True(stopwatch.ElapsedMilliseconds < 60, nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".key_latency");
                True(engine.IsSentenceCompositionActive, nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".tracking");
                True(engine.IsSentenceDecodePending, nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".pending");
                engine.GetCompositionDisplayParts(out _, out string pendingDisplay);
                Equal("ab", pendingDisplay, nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".pending_raw_display");
                True(completed.WaitOne(3000), nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".decode_completed");
                True(!engine.IsSentenceDecodePending, nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".completed");

                EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
                Equal("ab", snapshot.ActiveInputCode, nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".segmented_display");
                True(snapshot.Candidates.Length > 0 && snapshot.Candidates[0] == "你",
                    nameof(SentenceEngineDecodesLongWorkOffTheKeyPath) + ".candidate");
            }
        }

        private static void SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string reason),
                nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ": " + reason);
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ue"] = new List<string> { "的" },
                    ["ot"] = new List<string> { "是" },
                    ["tu"] = new List<string> { "我" }
                }),
                new SlowSentenceLanguageModel(),
                beamWidth: 10);
            var engine = new InputMethodEngine(state, decoder, sentenceDecodeSynchronously: false);
            using (var completed = new ManualResetEvent(false))
            {
                engine.SetSentenceDecodeCompletedCallback(() => completed.Set());
                TypeLetters(engine, "ueot");
                True(completed.WaitOne(3000), nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".prefix_ready");
                Equal("ue ot", engine.GetUiSnapshot(5).ActiveInputCode,
                    nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".prefix_seg");

                completed.Reset();
                Press(engine, 0x54);
                True(engine.IsSentenceDecodePending, nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".append_pending");
                EngineUiSnapshot appended = engine.GetUiSnapshot(5);
                Equal("ue ott", appended.ActiveInputCode,
                    nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".append_display");
                engine.GetCompositionDisplayParts(out _, out string appendedComposition);
                Equal("ue ott", appendedComposition,
                    nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".append_composition");

                True(completed.WaitOne(3000), nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".append_ready");

                completed.Reset();
                TypeLetters(engine, "u");
                True(completed.WaitOne(3000), nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".full_ready");
                Equal("ue ot tu", engine.GetUiSnapshot(5).ActiveInputCode,
                    nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".full_seg");

                completed.Reset();
                Press(engine, 0x08);
                if (engine.IsSentenceDecodePending)
                {
                    Equal("ue ot t", engine.GetUiSnapshot(5).ActiveInputCode,
                        nameof(SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending) + ".back_display");
                }
            }
        }

        private static void SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string reason),
                nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ": " + reason);
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ot"] = new List<string> { "是" },
                    ["ue"] = new List<string> { "的" }
                }),
                new SlowSentenceLanguageModel(),
                beamWidth: 10);
            var engine = new InputMethodEngine(state, decoder, sentenceDecodeSynchronously: false);
            using (var completed = new ManualResetEvent(false))
            {
                engine.SetSentenceDecodeCompletedCallback(() => completed.Set());
                TypeLetters(engine, "ot");
                True(completed.WaitOne(3000), nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".prefix_ready");
                Equal("是", engine.GetUiSnapshot(5).Candidates[0], nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".prefix");

                completed.Reset();
                Press(engine, 0x55);
                True(engine.IsSentenceDecodePending, nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".pending");
                EngineUiSnapshot held = engine.GetUiSnapshot(5);
                Equal("otu", held.ActiveInputCode, nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".live_code");
                True(held.Candidates.Length > 0 && held.Candidates[0] == "是",
                    nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".held");

                True(completed.WaitOne(3000), nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".suffix_ready");
                True(!engine.IsSentenceDecodePending, nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".caught_up");
                True(engine.GetUiSnapshot(5).Candidates.Length == 0,
                    nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".empty_when_done");

                Press(engine, 0x1B);
                EngineUiSnapshot cleared = engine.GetUiSnapshot(5);
                True(!cleared.IsComposing && cleared.Candidates.Length == 0,
                    nameof(SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending) + ".cleared");
            }
        }

        private sealed class SlowSentenceLanguageModel : ISentenceLanguageModel
        {
            public double LogProbability(string previous2, string previous1, string target)
            {
                Thread.Sleep(75);
                return 0.0;
            }
        }

        private sealed class RecordingSentenceRerankService : ISentenceRerankService
        {
            public SentenceRerankRequest LastRequest { get; private set; }

            public void Request(SentenceRerankRequest request)
            {
                LastRequest = request;
            }

            public void Dispose()
            {
            }
        }

        private static MixedInputDecodeResult Decode(
            FixedLengthMixedInputDecoder decoder,
            string rawCode,
            int maxCodeLength,
            int lexiconVersion,
            IReadOnlyDictionary<int, string> preferred = null)
        {
            return decoder.Decode(new MixedInputDecodeRequest
            {
                RawCode = rawCode,
                MaxCodeLength = maxCodeLength,
                LexiconVersion = lexiconVersion,
                PreferredCandidateTextByStart = preferred
            });
        }

        private static void Equal(string expected, string actual, string testName)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(testName + ": expected '" + expected + "', actual '" + actual + "'.");
            }
        }

        private static void True(bool condition, string testName)
        {
            if (!condition)
            {
                throw new InvalidOperationException(testName + ": assertion failed.");
            }
        }
    }
}
