using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-eval", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceEval(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null,
                        sweep: false);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-tune", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceEval(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null,
                        sweep: true);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-compare", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceCompare(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-onekey-eval", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceOneKeyEval(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null);
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
                SentenceDecoderKeepsFirstChoiceAheadOnShortCodes();
                SentenceDecoderUsesOnlyTheOptimalCharacterCode();
                SentenceDecoderAllowsNonPrimaryCodesForRareCharacters();
                SentenceDecoderIncrementalMatchesFullRebuild();
                SentenceNgramV2LoadsFromMappedFile();
                SentenceEngineCommitsDecodedCandidate();
                SentenceEngineDisplaysPrimarySegmentation();
                SentenceEngineHonorsSelectionSymbolSettings();
                SentenceDecoderAcceptsSpacedOneKeyWhenEnabled();
                SentenceEngineUsesSpacedOneKeyAndEnterCommit();
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
                SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch();
                Console.WriteLine("TigerClaw.Core.Tests: all tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("TigerClaw.Core.Tests: " + ex.Message);
                Exception inner = ex.InnerException;
                while (inner != null)
                {
                    Console.Error.WriteLine("inner: " + inner.Message);
                    inner = inner.InnerException;
                }

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

        private static Dictionary<string, List<string>> LoadSentenceLexiconSource(string lexiconPath)
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

            return source;
        }

        private static int RunSentenceSmoke(string lexiconPath, string modelPath, string code)
        {
            Dictionary<string, List<string>> source = LoadSentenceLexiconSource(lexiconPath);

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

        private static int RunSentenceEval(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath,
            bool sweep)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            if (!sweep)
            {
                AppendHandcraftedEvalCases(cases);
            }

            Dictionary<string, List<string>> source = LoadSentenceLexiconSource(lexiconPath);
            Stopwatch watch = Stopwatch.StartNew();
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(source);
            ISentenceLanguageModel model = SentenceNgramModel.Load(modelPath);
            var decoder = new SentenceInputDecoder(
                index,
                model,
                isolationPenalty: sweep ? SentenceIsolationPenalty.None : null);
            long loadMilliseconds = watch.ElapsedMilliseconds;
            watch.Restart();

            List<SentenceIsolationPenalty> configs = sweep
                ? BuildIsolationSweep()
                : new List<SentenceIsolationPenalty> { SentenceIsolationPenalty.CreateDefault() };
            var tallies = new EvalTally[configs.Count];
            for (int c = 0; c < configs.Count; c++)
            {
                tallies[c] = new EvalTally();
            }

            var rows = new List<string>(cases.Count);
            for (int i = 0; i < cases.Count; i++)
            {
                EvalCaseDto item = cases[i];
                SentenceDecodeResult decoded = decoder.Decode(item.Code, 20);
                int baselineRank = RankOf(decoded, item.Text);
                string topText = decoded.Candidates != null && decoded.Candidates.Length > 0
                    ? decoded.Candidates[0].Text
                    : string.Empty;
                AccumulateTally(tallies[0], baselineRank);
                if (!sweep)
                {
                    rows.Add(FormatEvalRow(item, baselineRank, topText));
                    if (string.Equals(item.Source, "handcrafted-regression", StringComparison.Ordinal))
                    {
                        Console.WriteLine("handcrafted\trank=" + FormatRank(baselineRank) +
                                          "\tgold=" + item.Text + "\ttop=" + topText);
                    }
                }
                else
                {
                    for (int c = 1; c < configs.Count; c++)
                    {
                        AccumulateTally(tallies[c], RankAfterIsolationPenalty(decoded, item.Text, model, configs[c]));
                    }
                }

                if ((i + 1) % 100 == 0)
                {
                    Console.Error.WriteLine("eval " + (i + 1) + "/" + cases.Count);
                }
            }

            watch.Stop();
            if (sweep)
            {
                ReportHandcraftedIsolationSweep(decoder, model, configs);
            }

            var summaries = new List<string>();
            for (int c = 0; c < configs.Count; c++)
            {
                string label = configs[c] == null || !configs[c].Enabled ? "core-ngram-no-penalty" : configs[c].Label();
                string summary = FormatEvalSummary(label, tallies[c], cases.Count, loadMilliseconds, watch.Elapsed.TotalSeconds);
                summaries.Add(summary);
                Console.WriteLine(summary);
            }

            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string payload = sweep
                    ? "{\"summaries\":[" + string.Join(",", summaries.ToArray()) + "]}"
                    : "{\"summary\":" + summaries[0] + ",\"cases\":[" + string.Join(",", rows.ToArray()) + "]}";
                File.WriteAllText(outputPath, payload, new UTF8Encoding(false));
            }

            return 0;
        }

        private static int RunSentenceCompare(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            Dictionary<string, List<string>> source = LoadSentenceLexiconSource(lexiconPath);
            Stopwatch watch = Stopwatch.StartNew();
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(source);
            SentenceIsolationPenalty afterPenalty = SentenceIsolationPenalty.CreateDefault();
            long loadMilliseconds = watch.ElapsedMilliseconds;
            watch.Restart();

            int workers = Math.Min(8, Math.Max(1, Environment.ProcessorCount));
            var ownedModels = new ConcurrentBag<SentenceNgramModel>();
            var locals = new ThreadLocal<CompareLocal>(() =>
            {
                SentenceNgramModel localModel = SentenceNgramModel.Load(modelPath);
                ownedModels.Add(localModel);
                return new CompareLocal
                {
                    Model = localModel,
                    Decoder = new SentenceInputDecoder(
                        index,
                        localModel,
                        isolationPenalty: SentenceIsolationPenalty.None)
                };
            });

            var beforeTally = new EvalTally();
            var afterTally = new EvalTally();
            var gained = new ConcurrentBag<string>();
            var lost = new ConcurrentBag<string>();
            var recallLost = new ConcurrentBag<string>();
            int done = 0;
            try
            {
                Parallel.For(
                    0,
                    cases.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    i =>
                    {
                        EvalCaseDto item = cases[i];
                        CompareLocal local = locals.Value;
                        SentenceDecodeResult decoded = local.Decoder.Decode(item.Code, 20);
                        int beforeRank;
                        string beforeTop;
                        ScoreIsolation(decoded, item.Text, local.Model, null, out beforeRank, out beforeTop);
                        int afterRank;
                        string afterTop;
                        ScoreIsolation(decoded, item.Text, local.Model, afterPenalty, out afterRank, out afterTop);
                        lock (beforeTally)
                        {
                            AccumulateTally(beforeTally, beforeRank);
                            AccumulateTally(afterTally, afterRank);
                        }

                        if (beforeRank != 1 && afterRank == 1)
                        {
                            gained.Add(FormatCompareFlip(item, beforeRank, afterRank, beforeTop, afterTop));
                        }
                        else if (beforeRank == 1 && afterRank != 1)
                        {
                            lost.Add(FormatCompareFlip(item, beforeRank, afterRank, beforeTop, afterTop));
                        }

                        if (beforeRank > 0 && afterRank <= 0)
                        {
                            recallLost.Add(FormatCompareFlip(item, beforeRank, afterRank, beforeTop, afterTop));
                        }

                        int finished = Interlocked.Increment(ref done);
                        if (finished % 200 == 0)
                        {
                            Console.Error.WriteLine("compare " + finished + "/" + cases.Count);
                        }
                    });

                watch.Stop();
                string beforeSummary = FormatEvalSummary(
                    "core-ngram-no-penalty",
                    beforeTally,
                    cases.Count,
                    loadMilliseconds,
                    watch.Elapsed.TotalSeconds);
                string afterSummary = FormatEvalSummary(
                    afterPenalty.Label(),
                    afterTally,
                    cases.Count,
                    loadMilliseconds,
                    watch.Elapsed.TotalSeconds);
                Console.WriteLine(beforeSummary);
                Console.WriteLine(afterSummary);
                Console.WriteLine(
                    "workers=" + workers +
                    " gained_top1=" + gained.Count +
                    " lost_top1=" + lost.Count +
                    " recall_lost=" + recallLost.Count);

                ReportCompareBag("gained-top1", gained);
                ReportCompareBag("lost-top1", lost);
                ReportCompareBag("recall-lost", recallLost);

                List<EvalCaseDto> extras = new List<EvalCaseDto>();
                AppendHandcraftedEvalCases(extras);
                SentenceNgramModel extraModel = SentenceNgramModel.Load(modelPath);
                ownedModels.Add(extraModel);
                var extraDecoder = new SentenceInputDecoder(
                    index,
                    extraModel,
                    isolationPenalty: SentenceIsolationPenalty.None);
                for (int i = 0; i < extras.Count; i++)
                {
                    EvalCaseDto item = extras[i];
                    SentenceDecodeResult decoded = extraDecoder.Decode(item.Code, 20);
                    int beforeRank;
                    string beforeTop;
                    ScoreIsolation(decoded, item.Text, extraModel, null, out beforeRank, out beforeTop);
                    int afterRank;
                    string afterTop;
                    ScoreIsolation(decoded, item.Text, extraModel, afterPenalty, out afterRank, out afterTop);
                    Console.WriteLine(
                        "handcrafted\tgold=" + item.Text +
                        "\tbefore=" + FormatRank(beforeRank) +
                        "\tafter=" + FormatRank(afterRank) +
                        "\tbefore_top=" + beforeTop +
                        "\tafter_top=" + afterTop);
                }

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string payload = "{\"before\":" + beforeSummary +
                                     ",\"after\":" + afterSummary +
                                     ",\"workers\":" + workers +
                                     ",\"gained_top1\":" + gained.Count +
                                     ",\"lost_top1\":" + lost.Count +
                                     ",\"recall_lost\":" + recallLost.Count +
                                     ",\"gained\":[" + string.Join(",", gained.ToArray()) + "]" +
                                     ",\"lost\":[" + string.Join(",", lost.ToArray()) + "]" +
                                     ",\"recall_lost_cases\":[" + string.Join(",", recallLost.ToArray()) + "]}";
                    File.WriteAllText(outputPath, payload, new UTF8Encoding(false));
                }
            }
            finally
            {
                locals.Dispose();
                foreach (SentenceNgramModel owned in ownedModels)
                {
                    owned.Dispose();
                }
            }

            return 0;
        }

        private static int RunSentenceOneKeyEval(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            Dictionary<string, List<string>> source = LoadSentenceLexiconSource(lexiconPath);
            OneKeySentenceEncoder encoder = OneKeySentenceEncoder.Build(source);
            Stopwatch watch = Stopwatch.StartNew();
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(source);
            long loadMilliseconds = watch.ElapsedMilliseconds;
            watch.Restart();

            int workers = Math.Min(8, Math.Max(1, Environment.ProcessorCount));
            var ownedModels = new ConcurrentBag<SentenceNgramModel>();
            var locals = new ThreadLocal<OneKeyEvalLocal>(() =>
            {
                SentenceNgramModel localModel = SentenceNgramModel.Load(modelPath);
                ownedModels.Add(localModel);
                return new OneKeyEvalLocal
                {
                    Baseline = new SentenceInputDecoder(
                        index,
                        localModel,
                        isolationPenalty: SentenceIsolationPenalty.CreateDefault(),
                        allowSpacedOneKey: false),
                    Challenger = new SentenceInputDecoder(
                        index,
                        localModel,
                        isolationPenalty: SentenceIsolationPenalty.CreateDefault(),
                        allowSpacedOneKey: true)
                };
            });

            var beforeTally = new EvalTally();
            var afterTally = new EvalTally();
            var gained = new ConcurrentBag<string>();
            var lost = new ConcurrentBag<string>();
            var encodeFailed = new ConcurrentBag<string>();
            int oneKeyCharacters = 0;
            int totalCharacters = 0;
            int oneKeyCases = 0;
            int beforeCodeLength = 0;
            int afterCodeLength = 0;
            int done = 0;
            try
            {
                Parallel.For(
                    0,
                    cases.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    i =>
                    {
                        EvalCaseDto item = cases[i];
                        OneKeyEvalLocal local = locals.Value;
                        SentenceDecodeResult beforeDecoded = local.Baseline.Decode(item.Code, 20);
                        int beforeRank;
                        string beforeTop;
                        ScoreIsolation(beforeDecoded, item.Text, null, null, out beforeRank, out beforeTop);

                        string encoded;
                        int usedOneKey;
                        int characterCount;
                        if (!string.IsNullOrEmpty(item.OneKeyCode))
                        {
                            encoded = item.OneKeyCode;
                            encoder.CountUsage(item.Text, out usedOneKey, out characterCount);
                        }
                        else if (!encoder.TryEncode(item.Text, out encoded, out usedOneKey, out characterCount))
                        {
                            encodeFailed.Add(JsonString(item.Text));
                            lock (beforeTally)
                            {
                                AccumulateTally(beforeTally, beforeRank);
                            }

                            Interlocked.Increment(ref done);
                            return;
                        }

                        SentenceDecodeResult afterDecoded = local.Challenger.Decode(encoded, 20);
                        int afterRank;
                        string afterTop;
                        ScoreIsolation(afterDecoded, item.Text, null, null, out afterRank, out afterTop);
                        lock (beforeTally)
                        {
                            AccumulateTally(beforeTally, beforeRank);
                            AccumulateTally(afterTally, afterRank);
                            oneKeyCharacters += usedOneKey;
                            totalCharacters += characterCount;
                            beforeCodeLength += item.Code == null ? 0 : item.Code.Length;
                            afterCodeLength += encoded.Length;
                            if (usedOneKey > 0)
                            {
                                oneKeyCases++;
                            }
                        }

                        if (beforeRank != 1 && afterRank == 1)
                        {
                            gained.Add(FormatOneKeyFlip(item, encoded, beforeRank, afterRank, beforeTop, afterTop));
                        }
                        else if (beforeRank == 1 && afterRank != 1)
                        {
                            lost.Add(FormatOneKeyFlip(item, encoded, beforeRank, afterRank, beforeTop, afterTop));
                        }

                        int finished = Interlocked.Increment(ref done);
                        if (finished % 200 == 0)
                        {
                            Console.Error.WriteLine("onekey " + finished + "/" + cases.Count);
                        }
                    });

                watch.Stop();
                string beforeSummary = FormatEvalSummary(
                    "current-optimal",
                    beforeTally,
                    cases.Count,
                    loadMilliseconds,
                    watch.Elapsed.TotalSeconds);
                string afterSummary = FormatEvalSummary(
                    "onekey-space-optimal",
                    afterTally,
                    cases.Count,
                    loadMilliseconds,
                    watch.Elapsed.TotalSeconds);
                Console.WriteLine(beforeSummary);
                Console.WriteLine(afterSummary);
                Console.WriteLine(
                    "workers=" + workers +
                    " onekey_chars=" + oneKeyCharacters + "/" + totalCharacters +
                    " onekey_cases=" + oneKeyCases +
                    " encode_failed=" + encodeFailed.Count +
                    " mean_code_len_before=" + (cases.Count == 0 ? 0 : beforeCodeLength / (double)cases.Count).ToString("0.###") +
                    " mean_code_len_after=" + (cases.Count == 0 ? 0 : afterCodeLength / (double)cases.Count).ToString("0.###") +
                    " gained_top1=" + gained.Count +
                    " lost_top1=" + lost.Count);
                Console.WriteLine("onekey_letters\t" + encoder.DescribeOneKeyLetters());

                ReportCompareBag("gained-top1", gained);
                ReportCompareBag("lost-top1", lost);

                var extras = new List<EvalCaseDto>();
                AppendHandcraftedEvalCases(extras);
                using (SentenceNgramModel extraModel = SentenceNgramModel.Load(modelPath))
                {
                    var baseline = new SentenceInputDecoder(
                        index,
                        extraModel,
                        isolationPenalty: SentenceIsolationPenalty.CreateDefault(),
                        allowSpacedOneKey: false);
                    var challenger = new SentenceInputDecoder(
                        index,
                        extraModel,
                        isolationPenalty: SentenceIsolationPenalty.CreateDefault(),
                        allowSpacedOneKey: true);
                    for (int i = 0; i < extras.Count; i++)
                    {
                        EvalCaseDto item = extras[i];
                        SentenceDecodeResult beforeDecoded = baseline.Decode(item.Code, 20);
                        int beforeRank;
                        string beforeTop;
                        ScoreIsolation(beforeDecoded, item.Text, null, null, out beforeRank, out beforeTop);
                        string encoded;
                        int usedOneKey;
                        int characterCount;
                        encoder.TryEncode(item.Text, out encoded, out usedOneKey, out characterCount);
                        SentenceDecodeResult afterDecoded = challenger.Decode(encoded ?? string.Empty, 20);
                        int afterRank;
                        string afterTop;
                        ScoreIsolation(afterDecoded, item.Text, extraModel, null, out afterRank, out afterTop);
                        Console.WriteLine(
                            "handcrafted\tgold=" + item.Text +
                            "\told=" + item.Code +
                            "\tnew=" + encoded +
                            "\tbefore=" + FormatRank(beforeRank) +
                            "\tafter=" + FormatRank(afterRank) +
                            "\tbefore_top=" + beforeTop +
                            "\tafter_top=" + afterTop);
                    }
                }

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string payload = "{\"before\":" + beforeSummary +
                                     ",\"after\":" + afterSummary +
                                     ",\"onekey_chars\":" + oneKeyCharacters +
                                     ",\"total_chars\":" + totalCharacters +
                                     ",\"onekey_cases\":" + oneKeyCases +
                                     ",\"encode_failed\":" + encodeFailed.Count +
                                     ",\"gained_top1\":" + gained.Count +
                                     ",\"lost_top1\":" + lost.Count +
                                     ",\"gained\":[" + string.Join(",", gained.ToArray()) + "]" +
                                     ",\"lost\":[" + string.Join(",", lost.ToArray()) + "]}";
                    File.WriteAllText(outputPath, payload, new UTF8Encoding(false));
                }
            }
            finally
            {
                locals.Dispose();
                foreach (SentenceNgramModel owned in ownedModels)
                {
                    owned.Dispose();
                }
            }

            return 0;
        }

        private sealed class OneKeyEvalLocal
        {
            public SentenceInputDecoder Baseline;
            public SentenceInputDecoder Challenger;
        }

        private static string FormatOneKeyFlip(
            EvalCaseDto item,
            string encoded,
            int beforeRank,
            int afterRank,
            string beforeTop,
            string afterTop)
        {
            return "{\"text\":" + JsonString(item.Text) +
                   ",\"old_code\":" + JsonString(item.Code) +
                   ",\"new_code\":" + JsonString(encoded) +
                   ",\"before_rank\":" + (beforeRank > 0 ? beforeRank.ToString() : "null") +
                   ",\"after_rank\":" + (afterRank > 0 ? afterRank.ToString() : "null") +
                   ",\"before_top\":" + JsonString(beforeTop) +
                   ",\"after_top\":" + JsonString(afterTop) + "}";
        }

        private sealed class OneKeySentenceEncoder
        {
            private readonly Dictionary<string, string> _oneKeyByCharacter;
            private readonly Dictionary<string, string> _sentenceCodeByCharacter;

            private OneKeySentenceEncoder(
                Dictionary<string, string> oneKeyByCharacter,
                Dictionary<string, string> sentenceCodeByCharacter)
            {
                _oneKeyByCharacter = oneKeyByCharacter;
                _sentenceCodeByCharacter = sentenceCodeByCharacter;
            }

            public static OneKeySentenceEncoder Build(Dictionary<string, List<string>> source)
            {
                var oneKey = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<string>> pair in source)
                {
                    if (pair.Key.Length != 1 || pair.Value == null || pair.Value.Count == 0)
                    {
                        continue;
                    }

                    string first = pair.Value[0];
                    if (new StringInfo(first).LengthInTextElements == 1 && !oneKey.ContainsKey(first))
                    {
                        oneKey[first] = pair.Key;
                    }
                }

                var codesByCharacter = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<string>> pair in source)
                {
                    if (pair.Value == null)
                    {
                        continue;
                    }

                    foreach (string text in pair.Value)
                    {
                        if (new StringInfo(text).LengthInTextElements != 1)
                        {
                            continue;
                        }

                        List<string> codes;
                        if (!codesByCharacter.TryGetValue(text, out codes))
                        {
                            codes = new List<string>();
                            codesByCharacter[text] = codes;
                        }

                        if (!codes.Contains(pair.Key))
                        {
                            codes.Add(pair.Key);
                        }
                    }
                }

                var sentence = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<string>> pair in codesByCharacter)
                {
                    string code = ChooseSentenceCode(pair.Key, pair.Value, source);
                    if (!string.IsNullOrEmpty(code))
                    {
                        sentence[pair.Key] = code;
                    }
                }

                return new OneKeySentenceEncoder(oneKey, sentence);
            }

            public void CountUsage(string text, out int usedOneKey, out int characterCount)
            {
                string ignored;
                TryEncode(text, out ignored, out usedOneKey, out characterCount);
            }

            public bool TryEncode(string text, out string encoded, out int usedOneKey, out int characterCount)
            {
                encoded = null;
                usedOneKey = 0;
                characterCount = 0;
                if (string.IsNullOrEmpty(text))
                {
                    return false;
                }

                var builder = new StringBuilder();
                TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
                while (enumerator.MoveNext())
                {
                    string character = enumerator.GetTextElement();
                    characterCount++;
                    string oneKey;
                    if (_oneKeyByCharacter.TryGetValue(character, out oneKey))
                    {
                        builder.Append(oneKey);
                        builder.Append(' ');
                        usedOneKey++;
                        continue;
                    }

                    string sentenceCode;
                    if (!_sentenceCodeByCharacter.TryGetValue(character, out sentenceCode))
                    {
                        return false;
                    }

                    builder.Append(sentenceCode);
                }

                encoded = builder.ToString();
                return characterCount > 0;
            }

            public string DescribeOneKeyLetters()
            {
                var items = new List<string>();
                foreach (KeyValuePair<string, string> pair in _oneKeyByCharacter)
                {
                    items.Add(pair.Value + "=" + pair.Key);
                }

                items.Sort(StringComparer.Ordinal);
                return string.Join(" ", items.ToArray());
            }

            private static string ChooseSentenceCode(
                string character,
                List<string> codes,
                Dictionary<string, List<string>> source)
            {
                string bestFirst = null;
                string bestAny = null;
                foreach (string code in codes)
                {
                    if (string.IsNullOrEmpty(code) || code.Length < 2)
                    {
                        continue;
                    }

                    List<string> candidates;
                    if (!source.TryGetValue(code, out candidates) || candidates.Count == 0)
                    {
                        continue;
                    }

                    bestAny = Shorter(bestAny, code);
                    if (string.Equals(candidates[0], character, StringComparison.Ordinal))
                    {
                        bestFirst = Shorter(bestFirst, code);
                    }
                }

                string chosen = bestFirst ?? bestAny;
                if (string.IsNullOrEmpty(chosen))
                {
                    return null;
                }

                int rank = source[chosen].IndexOf(character) + 1;
                if (rank <= 1)
                {
                    return chosen;
                }

                if (rank == 2)
                {
                    return chosen + ";";
                }

                if (rank == 3)
                {
                    return chosen + "'";
                }

                return chosen + (rank == 10 ? "0" : rank.ToString(CultureInfo.InvariantCulture));
            }

            private static string Shorter(string current, string candidate)
            {
                if (string.IsNullOrEmpty(current) ||
                    candidate.Length < current.Length ||
                    (candidate.Length == current.Length && string.CompareOrdinal(candidate, current) < 0))
                {
                    return candidate;
                }

                return current;
            }
        }

        private sealed class CompareLocal
        {
            public ISentenceLanguageModel Model;
            public SentenceInputDecoder Decoder;
        }

        private static void ReportCompareBag(string label, ConcurrentBag<string> rows)
        {
            string[] items = rows.ToArray();
            Array.Sort(items, StringComparer.Ordinal);
            int limit = Math.Min(items.Length, 20);
            for (int i = 0; i < limit; i++)
            {
                Console.WriteLine(label + "\t" + items[i]);
            }

            if (items.Length > limit)
            {
                Console.WriteLine(label + "\t... " + (items.Length - limit) + " more");
            }
        }

        private static string FormatCompareFlip(
            EvalCaseDto item,
            int beforeRank,
            int afterRank,
            string beforeTop,
            string afterTop)
        {
            return "{\"text\":" + JsonString(item.Text) +
                   ",\"code\":" + JsonString(item.Code) +
                   ",\"before_rank\":" + (beforeRank > 0 ? beforeRank.ToString() : "null") +
                   ",\"after_rank\":" + (afterRank > 0 ? afterRank.ToString() : "null") +
                   ",\"before_top\":" + JsonString(beforeTop) +
                   ",\"after_top\":" + JsonString(afterTop) + "}";
        }

        private static void ScoreIsolation(
            SentenceDecodeResult decoded,
            string gold,
            ISentenceLanguageModel model,
            SentenceIsolationPenalty penalty,
            out int rank,
            out string top)
        {
            rank = 0;
            top = string.Empty;
            if (decoded == null || decoded.Candidates == null || decoded.Candidates.Length == 0)
            {
                return;
            }

            if (penalty == null || !penalty.Enabled)
            {
                top = decoded.Candidates[0].Text;
                rank = RankOf(decoded, gold);
                return;
            }

            var scored = new List<KeyValuePair<double, string>>(decoded.Candidates.Length);
            for (int i = 0; i < decoded.Candidates.Length; i++)
            {
                SentenceCandidate candidate = decoded.Candidates[i];
                scored.Add(new KeyValuePair<double, string>(
                    candidate.BaseScore - penalty.Apply(candidate.Text, model),
                    candidate.Text));
            }

            scored.Sort((left, right) =>
            {
                int compared = right.Key.CompareTo(left.Key);
                return compared != 0 ? compared : string.CompareOrdinal(left.Value, right.Value);
            });
            top = scored[0].Value;
            for (int i = 0; i < scored.Count; i++)
            {
                if (string.Equals(scored[i].Value, gold, StringComparison.Ordinal))
                {
                    rank = i + 1;
                    return;
                }
            }
        }

        private static List<SentenceIsolationPenalty> BuildIsolationSweep()
        {
            var configs = new List<SentenceIsolationPenalty>();
            configs.Add(null);
            int[] thresholds = { 3000, 4000, 5000, 8000 };
            double[] lambdas = { 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            bool[] modes = { false, true };
            for (int t = 0; t < thresholds.Length; t++)
            {
                for (int l = 0; l < lambdas.Length; l++)
                {
                    for (int m = 0; m < modes.Length; m++)
                    {
                        configs.Add(new SentenceIsolationPenalty
                        {
                            RankThreshold = thresholds[t],
                            Lambda = lambdas[l],
                            UseLogRank = modes[m]
                        });
                    }
                }
            }

            return configs;
        }

        private static void ReportHandcraftedIsolationSweep(
            SentenceInputDecoder decoder,
            ISentenceLanguageModel model,
            List<SentenceIsolationPenalty> configs)
        {
            var extras = new List<EvalCaseDto>();
            AppendHandcraftedEvalCases(extras);
            for (int i = 0; i < extras.Count; i++)
            {
                EvalCaseDto item = extras[i];
                SentenceDecodeResult decoded = decoder.Decode(item.Code, 20);
                Console.WriteLine("handcrafted-gold\t" + item.Text);
                for (int c = 0; c < configs.Count; c++)
                {
                    int rank = c == 0
                        ? RankOf(decoded, item.Text)
                        : RankAfterIsolationPenalty(decoded, item.Text, model, configs[c]);
                    string label = configs[c] == null || !configs[c].Enabled ? "none" : configs[c].Label();
                    Console.WriteLine("handcrafted\t" + label + "\trank=" + FormatRank(rank));
                }
            }
        }

        private static int RankAfterIsolationPenalty(
            SentenceDecodeResult decoded,
            string gold,
            ISentenceLanguageModel model,
            SentenceIsolationPenalty penalty)
        {
            if (decoded == null || decoded.Candidates == null || decoded.Candidates.Length == 0)
            {
                return 0;
            }

            var scored = new List<KeyValuePair<double, string>>(decoded.Candidates.Length);
            for (int i = 0; i < decoded.Candidates.Length; i++)
            {
                SentenceCandidate candidate = decoded.Candidates[i];
                double score = candidate.BaseScore - penalty.Apply(candidate.Text, model);
                scored.Add(new KeyValuePair<double, string>(score, candidate.Text));
            }

            scored.Sort((left, right) =>
            {
                int compared = right.Key.CompareTo(left.Key);
                return compared != 0 ? compared : string.CompareOrdinal(left.Value, right.Value);
            });
            for (int i = 0; i < scored.Count; i++)
            {
                if (string.Equals(scored[i].Value, gold, StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }

            return 0;
        }

        private static void AccumulateTally(EvalTally tally, int rank)
        {
            if (rank <= 0)
            {
                return;
            }

            tally.Recalled++;
            tally.Mrr += 1.0 / rank;
            if (rank <= 1)
            {
                tally.Top1++;
            }

            if (rank <= 5)
            {
                tally.Top5++;
            }

            if (rank <= 10)
            {
                tally.Top10++;
            }
        }

        private static string FormatEvalRow(EvalCaseDto item, int rank, string topText)
        {
            return "{\"source\":" + JsonString(item.Source) +
                   ",\"text\":" + JsonString(item.Text) +
                   ",\"code\":" + JsonString(item.Code) +
                   ",\"rank\":" + (rank > 0 ? rank.ToString() : "null") +
                   ",\"top\":" + JsonString(topText) +
                   ",\"hit\":" + (rank == 1 ? "true" : "false") + "}";
        }

        private static string FormatEvalSummary(string label, EvalTally tally, int cases, long loadMilliseconds, double seconds)
        {
            int total = Math.Max(cases, 1);
            return "{\"model\":" + JsonString(label) +
                   ",\"cases\":" + cases +
                   ",\"recalled\":" + tally.Recalled +
                   ",\"recall\":" + (tally.Recalled / (double)total).ToString("G9") +
                   ",\"top_1\":" + (tally.Top1 / (double)total).ToString("G9") +
                   ",\"top_5\":" + (tally.Top5 / (double)total).ToString("G9") +
                   ",\"top_10\":" + (tally.Top10 / (double)total).ToString("G9") +
                   ",\"mrr\":" + (tally.Mrr / total).ToString("G9") +
                   ",\"load_ms\":" + loadMilliseconds +
                   ",\"seconds\":" + seconds.ToString("G9") +
                   ",\"milliseconds_per_case\":" + (seconds * 1000.0 / total).ToString("G9") + "}";
        }

        private static string FormatRank(int rank)
        {
            return rank > 0 ? rank.ToString() : "miss";
        }

        private sealed class EvalTally
        {
            public int Recalled;
            public int Top1;
            public int Top5;
            public int Top10;
            public double Mrr;
        }

        private static void AppendHandcraftedEvalCases(List<EvalCaseDto> cases)
        {
            AddEvalCaseIfMissing(cases, "好了不用全记最简码了", "bhrlcbtyjnsvjoqramnrl");
            AddEvalCaseIfMissing(cases, "一百五十艘战船的明国大船", "fifuwunsipgydpiodueovrnmdiod");
        }

        private static void AddEvalCaseIfMissing(List<EvalCaseDto> cases, string text, string code)
        {
            for (int i = 0; i < cases.Count; i++)
            {
                if (string.Equals(cases[i].Text, text, StringComparison.Ordinal))
                {
                    return;
                }
            }

            cases.Add(new EvalCaseDto
            {
                Text = text,
                Code = code,
                Source = "handcrafted-regression"
            });
        }

        private static int RankOf(SentenceDecodeResult decoded, string gold)
        {
            if (decoded == null || decoded.Candidates == null)
            {
                return 0;
            }

            for (int i = 0; i < decoded.Candidates.Length; i++)
            {
                if (string.Equals(decoded.Candidates[i].Text, gold, StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }

            return 0;
        }

        private static List<EvalCaseDto> LoadEvalCases(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(EvalCaseDto[]));
                var loaded = serializer.ReadObject(stream) as EvalCaseDto[];
                var result = new List<EvalCaseDto>();
                if (loaded == null)
                {
                    return result;
                }

                for (int i = 0; i < loaded.Length; i++)
                {
                    EvalCaseDto item = loaded[i];
                    if (item == null || string.IsNullOrEmpty(item.Text) || string.IsNullOrEmpty(item.Code))
                    {
                        continue;
                    }

                    result.Add(item);
                }

                return result;
            }
        }

        private static string JsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch == '"' || ch == '\\')
                {
                    sb.Append('\\');
                    sb.Append(ch);
                }
                else if (ch == '\n')
                {
                    sb.Append("\\n");
                }
                else if (ch == '\r')
                {
                    sb.Append("\\r");
                }
                else
                {
                    sb.Append(ch);
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        [DataContract]
        private sealed class EvalCaseDto
        {
            [DataMember(Name = "text")]
            public string Text { get; set; }

            [DataMember(Name = "code")]
            public string Code { get; set; }

            [DataMember(Name = "source")]
            public string Source { get; set; }

            [DataMember(Name = "onekey_code")]
            public string OneKeyCode { get; set; }
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

        private static void SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "否", out _, out string offReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + offReason);
            True(state.TrySetConfigValue("自动启用整句模式", "是", out _, out string autoReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + autoReason);
            True(state.TrySetConfigValue("当前码表", "虎码", out _, out string schemaReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + schemaReason);
            True(!state.GetSentenceInputEnabled(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".switch_off");
            True(!state.IsSentenceInputActive(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".plain_schema");

            True(state.TrySetConfigValue("当前码表", "虎整句", out _, out string sentenceSchemaReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + sentenceSchemaReason);
            True(!state.GetSentenceInputEnabled(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".switch_still_off");
            True(state.IsSentenceInputActive(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".named_schema");

            True(state.TrySetConfigValue("自动启用整句模式", "否", out _, out string autoOffReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + autoOffReason);
            True(!state.IsSentenceInputActive(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".auto_off");

            True(state.TrySetConfigValue("整句输入", "是", out _, out string onReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + onReason);
            True(state.TrySetConfigValue("当前码表", "虎码", out _, out string plainAgainReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + plainAgainReason);
            True(state.IsSentenceInputActive(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".manual_on");
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
            True(
                Array.Exists(standalone.Candidates, candidate => candidate.Text == "什么"),
                nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter) + ".short_all_ranks");

            SentenceDecodeResult numeric = decoder.Decode("j2");
            Equal("什么", numeric.Candidates[0].Text, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
            True(numeric.Candidates.Length == 1, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));

            SentenceDecodeResult semicolon = decoder.Decode("j;");
            Equal("什么", semicolon.Candidates[0].Text, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
            True(semicolon.Candidates.Length == 1, nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter));
        }

        private static void SentenceDecoderAcceptsSpacedOneKeyWhenEnabled()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["u"] = new List<string> { "的" },
                ["ue"] = new List<string> { "的" },
                ["ot"] = new List<string> { "是" }
            };
            SentenceInputDecoder allowed = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 20,
                isolationPenalty: SentenceIsolationPenalty.None,
                allowSpacedOneKey: true);
            Equal("的是", allowed.Decode("u ot").Candidates[0].Text, nameof(SentenceDecoderAcceptsSpacedOneKeyWhenEnabled));
            Equal("u ot", allowed.Decode("u ot").Candidates[0].SegmentedCode,
                nameof(SentenceDecoderAcceptsSpacedOneKeyWhenEnabled) + ".seg");
            Equal("的", allowed.Decode("u").Candidates[0].Text, nameof(SentenceDecoderAcceptsSpacedOneKeyWhenEnabled) + ".standalone");
            True(allowed.Decode("uot").Candidates.Length == 0, nameof(SentenceDecoderAcceptsSpacedOneKeyWhenEnabled) + ".bare");

            SentenceInputDecoder disabled = CreateSentenceDecoder(lexicon);
            True(disabled.Decode("u ot").Candidates.Length == 0, nameof(SentenceDecoderAcceptsSpacedOneKeyWhenEnabled) + ".off");
        }

        private static void SentenceEngineUsesSpacedOneKeyAndEnterCommit()
        {
            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句输入", "是", out _, out string sentenceReason),
                nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ": " + sentenceReason);
            True(state.TrySetConfigValue("允许一简组整句", "是", out _, out string oneKeyReason),
                nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ": " + oneKeyReason);
            var engine = new InputMethodEngine(
                state,
                CreateSentenceDecoder(
                    new Dictionary<string, List<string>>
                    {
                        ["u"] = new List<string> { "的" },
                        ["ot"] = new List<string> { "是" }
                    },
                    allowSpacedOneKey: true));

            TypeLetters(engine, "u ot");
            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            Equal("u ot", snapshot.InputCode, nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ".raw");
            Equal("u ot", snapshot.ActiveInputCode, nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ".display");
            True(snapshot.Candidates.Length > 0 && snapshot.Candidates[0] == "的是",
                nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ".candidate");
            True(engine.IsSentenceCompositionActive, nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ".still_composing");
            Equal("的是", Press(engine, 0x0D).TextToOutput, nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ".enter");
            True(!engine.GetUiSnapshot(5).IsComposing, nameof(SentenceEngineUsesSpacedOneKeyAndEnterCommit) + ".committed");
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

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
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
            SentenceInputDecoder decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(
                    new Dictionary<string, List<string>>
            {
                        ["ab"] = new List<string> { "甲" },
                ["ac"] = new List<string> { "甲", "丙" }
                    },
                    new HashSet<string>(StringComparer.Ordinal) { "甲" }),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);

            Equal("甲", decoder.Decode("ab").Candidates[0].Text, nameof(SentenceDecoderUsesOnlyTheOptimalCharacterCode));
            True(
                Array.TrueForAll(decoder.Decode("ac").Candidates, candidate => candidate.Text != "甲"),
                nameof(SentenceDecoderUsesOnlyTheOptimalCharacterCode));
        }

        private static void SentenceDecoderAllowsNonPrimaryCodesForRareCharacters()
        {
            SentenceInputDecoder decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(
                    new Dictionary<string, List<string>>
                    {
                        ["ab"] = new List<string> { "甲" },
                        ["ac"] = new List<string> { "甲", "丙" }
                    },
                    new HashSet<string>(StringComparer.Ordinal) { "乙" }),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);

            True(
                Array.Exists(decoder.Decode("ac").Candidates, candidate => candidate.Text == "甲"),
                nameof(SentenceDecoderAllowsNonPrimaryCodesForRareCharacters));
        }

        private static void SentenceDecoderRequiresExplicitSelectionForEveryCode()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["fi"] = new List<string> { "一", "一般" },
                ["ot"] = new List<string> { "是" },
                ["ab"] = new List<string> { "甲" }
            });

            SentenceDecodeResult bare = decoder.Decode("fi");
            Equal("一", bare.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.Exists(bare.Candidates, candidate => candidate.Text == "一般"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".short_all_ranks");
            True(
                IndexOfCandidate(bare, "一") < IndexOfCandidate(bare, "一般"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".first_before_second");

            SentenceDecodeResult prefix = decoder.Decode("fiot");
            Equal("一是", prefix.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            Equal("fi ot", prefix.Candidates[0].SegmentedCode,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.Exists(prefix.Candidates, candidate => candidate.Text == "一般是"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".len4_all_ranks");
            True(
                IndexOfCandidate(prefix, "一是") < IndexOfCandidate(prefix, "一般是"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".first_path_before_second");

            SentenceDecodeResult suffix = decoder.Decode("otfi");
            Equal("是一", suffix.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.Exists(suffix.Candidates, candidate => candidate.Text == "是一般"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".len4_suffix");

            SentenceDecodeResult longer = decoder.Decode("fiotab");
            Equal("一是甲", longer.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".long");
            True(
                Array.TrueForAll(longer.Candidates, candidate => candidate.Text != "一般是甲"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".long_needs_selector");

            Equal("一般", decoder.Decode("fi2").Candidates[0].Text,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            Equal("一般", decoder.Decode("fi;").Candidates[0].Text,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            Equal("fi;", decoder.Decode("fi;").Candidates[0].SegmentedCode,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
        }

        private static void SentenceDecoderKeepsFirstChoiceAheadOnShortCodes()
        {
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["fi"] = new List<string> { "一", "一般", "一起" }
                }),
                new PrefersLaterCharactersLanguageModel(),
                beamWidth: 20,
                isolationPenalty: SentenceIsolationPenalty.None);

            SentenceDecodeResult decoded = decoder.Decode("fi");
            Equal("一", decoded.Candidates[0].Text, nameof(SentenceDecoderKeepsFirstChoiceAheadOnShortCodes));
            True(
                IndexOfCandidate(decoded, "一") < IndexOfCandidate(decoded, "一般") &&
                IndexOfCandidate(decoded, "一般") < IndexOfCandidate(decoded, "一起"),
                nameof(SentenceDecoderKeepsFirstChoiceAheadOnShortCodes) + ".rank_order");
        }

        private static int IndexOfCandidate(SentenceDecodeResult decoded, string text)
        {
            if (decoded == null || decoded.Candidates == null)
            {
                return int.MaxValue;
            }

            for (int index = 0; index < decoded.Candidates.Length; index++)
            {
                if (string.Equals(decoded.Candidates[index].Text, text, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

        private sealed class PrefersLaterCharactersLanguageModel : ISentenceLanguageModel
        {
            public double LogProbability(string previous2, string previous1, string target)
            {
                if (string.Equals(target, "般", StringComparison.Ordinal) ||
                    string.Equals(target, "起", StringComparison.Ordinal))
                {
                    return 20.0;
                }

                return 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
            }
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

        private static SentenceInputDecoder CreateSentenceDecoder(
            Dictionary<string, List<string>> lexicon,
            bool allowSpacedOneKey = false)
        {
            return new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100,
                isolationPenalty: allowSpacedOneKey ? SentenceIsolationPenalty.None : null,
                allowSpacedOneKey: allowSpacedOneKey);
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

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
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
