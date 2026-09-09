using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Core;
using TigerClaw.Overlay;
using TigerClaw.Shared;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--ui-publish-stdio")
                    return RunUiPublisherProbe(args[1]);
                if (args.Length == 1 && args[0] == "--ui-snapshot-tests")
                {
                    RunUiSnapshotTests();
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--overlay-format-stdio")
                {
                    return RunOverlayFormatProbe();
                }
                if (args.Length == 2 &&
                    string.Equals(args[0], "--core-diff-stdio", StringComparison.OrdinalIgnoreCase))
                {
                    return RunCoreDifferentialAdapter(args[1]);
                }
                if (args.Length >= 1 &&
                    string.Equals(args[0], "--sentence-golden-export", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceGoldenExport(args.Length > 1 ? args[1] : null);
                }
                if (args.Length >= 3 &&
                    string.Equals(args[0], "--sentence-kn-probe", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceKnProbe(args[1], args[2]);
                }
                if (args.Length == 4 && string.Equals(args[0], "--sentence-smoke", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceSmoke(args[1], args[2], args[3]);
                }
                if (args.Length >= 5 && string.Equals(args[0], "--sentence-export-pools", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentencePoolExport(
                        args[1], args[2], args[3], args[4],
                        args.Length > 5 ? ParseInt(args[5], 0) : 0,
                        args.Length > 6 ? ParseDouble(args[6], 2.0) : 2.0);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-stream-eval", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceStreamEval(
                        args[1], args[2], args[3],
                        args.Length > 4 ? args[4] : null,
                        args.Length > 5 ? ParseDouble(args[5], 0.98) : 0.98,
                        args.Length > 6 ? ParseInt(args[6], 2) : 2,
                        args.Length > 7 ? ParseDouble(args[7], 0.0) : 0.0);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-early-commit-eval", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceEarlyCommitEval(
                        args[1], args[2], args[3],
                        args.Length > 4 ? args[4] : null,
                        args.Length > 5 ? ParseInt(args[5], 0) : 0);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-early-commit-policy-eval", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceEarlyCommitPolicyEval(
                        args[1], args[2], args[3],
                        args.Length > 4 ? args[4] : null,
                        args.Length > 5 ? ParseInt(args[5], 0) : 0,
                        args.Length > 6 ? args[6] : null);
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
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-boundary-compare", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceBoundaryCompare(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-softpin-compare", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceSoftPinCompare(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-length-compare", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceLengthCompare(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null,
                        null);
                }
                if (args.Length >= 4 && string.Equals(args[0], "--sentence-length-validate", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSentenceLengthCompare(
                        args[1],
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : null,
                        new[] { args.Length > 5 ? ParseDouble(args[5], 2.0) : 2.0 });
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
                LexiconFilesPrioritizeSchemaAndUseCurrentCulture();
                ComposesChineseAndRawCommitTextSeparately();
                EngineKeepsRawCodeSeparateFromSurface();
                EngineCommitsRawCodeWhenSwitchingToEnglish();
                EngineStillCommitsPunctuationWithoutCandidates();
                EnginePreservesShiftedLetterInMixedInput();
                SentenceDecoderSupportsWordAndSelectionSuffix();
                SentenceDecoderRejectsEmbeddedBareOneKeyCharacter();
                SentenceDecoderAllowsLeadingShortSymbolOnly();
                SentenceDecoderRequiresExplicitSelectionForEveryCode();
                SentenceDecoderAllowsImplicitNonFirstOnlyForWholeInputEdge();
                SentenceDecoderKeepsFirstChoiceAheadForWholeInputEdge();
                SentenceAllowDuplicateSingleCharactersDefaultsOn();
                SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled();
                SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled();
                SentenceDecoderAppliesCharacterRewardInsideBeam();
                SentenceDecoderRewardsOptimalWholeInputSingleCharacter();
                SentenceSupplementParsesPerSchemaFile();
                SentenceSupplementMatchesOverlapsAndRepeatedSingleCharacters();
                SentenceSupplementRewardsInsideBeamWithoutChangingConfidence();
                SentenceSupplementIncrementalMatchesFullRebuild();
                SentenceSupplementSurvivesNeuralRerank();
                SentenceDecoderUsesOnlyTheOptimalCharacterCode();
                SentenceDecoderUsesSourcePriorityForEqualLengthCodes();
                SentenceDecoderAllowsNonPrimaryCodesForRareCharacters();
                SentenceDecoderAllowsNonPrimaryCodesWhenHighFreqLimitIsZero();
                SentenceCharacterRanksTakeTopSelectsFrequencyPrefix();
                SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled();
                SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters();
                SentenceDecoderAllowsWhitelistedFullCodesUnderHighFreqLimit();
                SentenceDecoderIncrementalMatchesFullRebuild();
                SentenceDecoderReportsTruncatedConfidenceMass();
                SentenceDecoderMergesIncompleteTailIntoPrefixEvidence();
                SentenceDecoderKeepsDroppedTailRawEndpointsDistinct();
                SentenceDecoderConditionsEvidenceOnCommittedPrefix();
                SentenceDecoderMarksIncompleteTailAsNeutralOnly();
                SentenceDecoderSelectsExactVisibleTopK();
                SentenceIsolationPenaltyCachesCandidateText();
                SentenceDecoderAggregatesCompositionPerformance();
                SentencePerformanceCompletionDoesNotWaitForDecode();
                SentenceNgramV2LoadsFromMappedFile();
                SentenceEngineCommitsDecodedCandidate();
                SentenceEngineDisplaysPrimarySegmentation();
                SentenceEngineHonorsSelectionSymbolSettings();
                SentenceEngineKeepsArrowSelectionInPlace();
                SentenceEngineUsesTabToTraverseCandidates();
                SentenceTabSelectionLocksOnContinuedInput();
                SentenceTabLocksStackAndPreserveSelectors();
                SentenceEmptyCodeCountsDuplicateSingles();
                SentenceEnginePassesCtrlNumberShortcut();
                SentenceEngineCommitsSmartQuoteAfterCandidate();
                SentenceMinRetainedRawLengthDefaultsToZeroAndClamps();
                SentenceEmptyCodeAutoCommitDefaultsOnAndRetainsNewKey();
                SentenceEmptyCodeAutoCommitPreservesSentenceContext();
                SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards();
                SentenceEmptyCodeAutoCommitAcceptsStrongPrimaryCandidate();
                SentenceEmptyCodeAutoCommitDefersCompletableLastSegment();
                SentenceEmptyCodeAutoCommitWorksWithAsyncDecode();
                SentenceAutoCommitContinuationUsesFirstRanksOnly();
                EngineLightweightKeyStateMatchesUiSnapshot();
                SentenceEngineRejectsStaleNeuralResult();
                SentenceManualSelectionRejectsLateRerank();
                SentencePathCheckMatchesFullDecode();
                SentenceEmptyCodeDoesNotTreatPrunedCandidateAsUnique();
                SentenceAutoCommitExitKeepsOnlyLiveTail();
                SmartSentenceFixedSegmentation();
                SmartSentenceClosedSegmentDisplay();
                SmartSentenceKeysAndExits();
                SmartSentenceEarlyCommitConsumesSeparators();
                TemporaryPinyinAlwaysShowsReverseLookupAnnotations();
                SettingsOrderIsIndependentOfConfigurationOrder();
                SentenceNeuralWeightPreservesShortNgramWinner();
                SentenceEngineReranksOnlyTopFive();
                SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete();
                SentenceAutoCommitRequiresConsecutiveAppendEvidence();
                SentenceAutoCommitSuspendsAfterManualNavigation();
                SentenceAutoCommitRetainsAtLeastThreeRawCodes();
                SentenceProbabilisticAutoCommitRetainsDuplicateSingleContinuation();
                SentenceAutoCommitHonorsConfiguredMinRetainedRawLength();
                SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength();
                SentenceAutoCommitNeutralTailCountsMergedEvidence();
                SentenceAutoCommitMergedTailCountsAsEvidence();
                SentenceAutoCommitDoesNotCountMergedEvidenceAcrossLowConfidenceGap();
                SentenceAutoCommitDroppedTailContradictsShorterCompetitor();
                SentenceAutoCommitStopsBeforeExtendableSegment();
                SentenceAutoCommitUsesTwoStrongGenerationCommonPrefix();
                SentenceAutoCommitKeepsThreeGenerationWindowForWeakEvidence();
                SentenceEmptyCodeContinuationPreservesDuplicateSingles();
                SentenceAutoCommitTracksPrefixesIndependently();
                SentencePrefixEvidenceWeightsBoundaryDisagreement();
                SentencePrefixEvidenceKeepsRawBoundariesDistinct();
                SentencePrefixEvidenceLookupRequiresExactRawBoundary();
                SentenceAutoCommitAcceptsProbabilisticAlternatingSegmentation();
                SentenceAutoCommitReplayPreservesOriginalCommit();
                KeyReplayCacheReturnsOriginalResultWithCurrentSequence();
                ShortcutGesturesParseAndMatch();
                CustomActionShortcutsTriggerAndRearm();
                CustomRecentSchemaShortcutSwitchesSchemas();
                SchemaSwitchPreservesOnlyLiveRaw();
                KeyResponsesDeclareExpectedKeyUp();
                CtrlSpaceStillTogglesWithExpectedKeyUpResponses();
                KeyUpExpectationCoversReleaseDependentState();
                TransportDefersOnlyKeyUiPublication();
                NativeHookAlwaysShowsInputCodeInCandidateWindow();
                SentenceEngineDecodesLongWorkOffTheKeyPath();
                SentenceAutoCommitStaysOffTheKeyPath();
                SentenceEngineHoldsPreviousCandidatesWhileDecodeIsPending();
                SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending();
                SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch();
                SentenceGoldenExportIsDeterministic();
                SentenceGoldenCasesIncrementalMatchesFull();
                RunUiSnapshotTests();
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

        private static void NativeHookAlwaysShowsInputCodeInCandidateWindow()
        {
            var formatter = new CandidateTextFormatter();
            var nativeState = new OverlayUiState
            {
                IsNativeHook = true,
                CandidateVisible = true,
                InputCode = "abcd",
                Candidates = new[] { "candidate" },
                ShowInputCodeInCandidateWindow = false
            };

            CandidateWindowViewModel nativeView = formatter.BuildViewModel(nativeState, showCandidates: true, includeAnnotations: false);
            True(nativeView.Mode == CandidateDisplayMode.CodeAndCandidates,
                nameof(NativeHookAlwaysShowsInputCodeInCandidateWindow) + ".native_mode");
            True(nativeView.DisplayText.StartsWith("abcd", StringComparison.Ordinal),
                nameof(NativeHookAlwaysShowsInputCodeInCandidateWindow) + ".native_text");

            nativeState.HideCandidateItems = true;
            CandidateWindowViewModel nativeCodeOnlyView = formatter.BuildViewModel(nativeState, showCandidates: false, includeAnnotations: false);
            True(nativeCodeOnlyView.Mode == CandidateDisplayMode.CodeOnly,
                nameof(NativeHookAlwaysShowsInputCodeInCandidateWindow) + ".native_code_only");
            Equal("abcd", nativeCodeOnlyView.DisplayText,
                nameof(NativeHookAlwaysShowsInputCodeInCandidateWindow) + ".native_code_only_text");

            nativeState.IsNativeHook = false;
            nativeState.HideCandidateItems = false;
            CandidateWindowViewModel tsfView = formatter.BuildViewModel(nativeState, showCandidates: true, includeAnnotations: false);
            True(tsfView.Mode == CandidateDisplayMode.CandidatesOnly,
                nameof(NativeHookAlwaysShowsInputCodeInCandidateWindow) + ".tsf_mode");
            True(!tsfView.DisplayText.Contains("abcd", StringComparison.Ordinal),
                nameof(NativeHookAlwaysShowsInputCodeInCandidateWindow) + ".tsf_text");
        }

        private static void LexiconFilesPrioritizeSchemaAndUseCurrentCulture()
        {
            string root = Path.Combine(Path.GetTempPath(), "TigerClaw.Core.Tests", Guid.NewGuid().ToString("N"));
            string schemaDir = Path.Combine(root, "虎整句");
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                Directory.CreateDirectory(schemaDir);
                File.WriteAllText(Path.Combine(schemaDir, "快符.txt"), string.Empty);
                File.WriteAllText(Path.Combine(schemaDir, "虎补充.txt"), string.Empty);
                File.WriteAllText(Path.Combine(schemaDir, "虎整句.txt"), string.Empty);

                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
                string[] ordered = CoreRuntimeState.GetOrderedLexiconFiles(schemaDir)
                    .Select(Path.GetFileName)
                    .ToArray();

                True(ordered.Length == 3,
                    nameof(LexiconFilesPrioritizeSchemaAndUseCurrentCulture) + ".count");
                Equal("虎整句.txt", ordered[0],
                    nameof(LexiconFilesPrioritizeSchemaAndUseCurrentCulture) + ".schema_first");
                Equal("虎补充.txt", ordered[1],
                    nameof(LexiconFilesPrioritizeSchemaAndUseCurrentCulture) + ".culture_first");
                Equal("快符.txt", ordered[2],
                    nameof(LexiconFilesPrioritizeSchemaAndUseCurrentCulture) + ".culture_second");

                string yamlSchemaDir = Path.Combine(root, "yaml方案");
                Directory.CreateDirectory(yamlSchemaDir);
                File.WriteAllText(Path.Combine(yamlSchemaDir, "普通.txt"), string.Empty);
                File.WriteAllText(Path.Combine(yamlSchemaDir, "yaml方案.dict.yaml"), string.Empty);
                string[] yamlOrdered = CoreRuntimeState.GetOrderedLexiconFiles(yamlSchemaDir)
                    .Select(Path.GetFileName)
                    .ToArray();
                Equal("yaml方案.dict.yaml", yamlOrdered[0],
                    nameof(LexiconFilesPrioritizeSchemaAndUseCurrentCulture) + ".yaml_schema_first");
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        private static int RunCoreDifferentialAdapter(string root)
        {
            Directory.CreateDirectory(root);
            var state = new CoreRuntimeState(root);
            state.Initialize();
            string lastUiCommand = string.Empty;
            using (var handler = new ProtocolHandler(command => lastUiCommand = command.ToString(), state, null))
            {
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    string response = null;
                    if (line.IndexOf("\"_diff\"", StringComparison.Ordinal) >= 0 &&
                        line.IndexOf("wait_idle", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!handler.WaitForDifferentialIdle(10000))
                        {
                            Console.Error.WriteLine("C# differential adapter timed out waiting for sentence decode");
                            return 2;
                        }
                    }
                    else if (line.IndexOf("\"_diff\"", StringComparison.Ordinal) < 0)
                    {
                        response = handler.Handle(line);
                    }

                    bool? pendingAtResponse = null;
                    if (!string.IsNullOrEmpty(response))
                    {
                        if (response.IndexOf("\"composition_pending\":true", StringComparison.Ordinal) >= 0)
                        {
                            pendingAtResponse = true;
                        }
                        else if (response.IndexOf("\"composition_pending\":false", StringComparison.Ordinal) >= 0)
                        {
                            pendingAtResponse = false;
                        }
                    }
                    string snapshot = handler.BuildDifferentialSnapshotJson(pendingAtResponse);
                    Console.WriteLine("{\"response\":" + (string.IsNullOrEmpty(response) ? "null" : response) +
                                      ",\"snapshot\":" + snapshot +
                                      ",\"ui_command\":" + QuoteJson(lastUiCommand) + "}");
                    Console.Out.Flush();
                    lastUiCommand = string.Empty;
                }
            }
            return 0;
        }

        private static string QuoteJson(string value)
        {
            string text = value ?? string.Empty;
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
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
            SentenceSupplementMatcher supplementMatcher = SentenceSupplementMatcher.Build(
                CoreRuntimeState.LoadSentenceSupplements(Path.GetDirectoryName(lexiconPath)));
            var decoder = new SentenceInputDecoder(
                index,
                model,
                emittedCharacterReward: 2.0,
                supplementMatcher: supplementMatcher);
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

        private static int RunSentencePoolExport(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath,
            int maximumCases,
            double emittedCharacterReward)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            if (maximumCases > 0 && cases.Count > maximumCases)
            {
                cases = cases.Take(maximumCases).ToList();
            }

            SentenceLexiconIndex index = SentenceLexiconIndex.Build(
                LoadSentenceLexiconSource(lexiconPath));
            using (SentenceNgramModel model = SentenceNgramModel.Load(modelPath))
            {
                var decoder = new SentenceInputDecoder(
                    index,
                    model,
                    emittedCharacterReward: emittedCharacterReward);
                var rows = new List<string>(cases.Count);
                for (int caseIndex = 0; caseIndex < cases.Count; caseIndex++)
                {
                    EvalCaseDto item = cases[caseIndex];
                    SentenceDecodeResult decoded = decoder.Decode(item.Code, 5);
                    string candidates = string.Join(
                        ",",
                        decoded.Candidates.Select(candidate =>
                            "{\"text\":" + JsonString(candidate.Text) +
                            ",\"base_score\":" + candidate.BaseScore.ToString(
                                "G17",
                                CultureInfo.InvariantCulture) + "}"));
                    rows.Add(
                        "{\"text\":" + JsonString(item.Text) +
                        ",\"code\":" + JsonString(item.Code) +
                        ",\"source\":" + JsonString(item.Source) +
                        ",\"candidates\":[" + candidates + "]}");
                    if ((caseIndex + 1) % 200 == 0)
                    {
                        Console.Error.WriteLine(
                            "pool-export " + (caseIndex + 1) + "/" + cases.Count);
                    }
                }

                File.WriteAllText(
                    outputPath,
                    "[" + string.Join(",", rows.ToArray()) + "]",
                    new UTF8Encoding(false));
            }

            return 0;
        }

        private static int RunSentenceStreamEval(
            string casesPath, string modelPath, string lexiconPath, string outputPath,
            double threshold, int stabilityRequired, double emittedCharacterReward)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            AppendHandcraftedEvalCases(cases);
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(LoadSentenceLexiconSource(lexiconPath)),
                SentenceNgramModel.Load(modelPath),
                emittedCharacterReward: emittedCharacterReward);
            threshold = Math.Max(0.0, Math.Min(1.0, threshold));
            stabilityRequired = Math.Max(1, stabilityRequired);

            int committedSamples = 0, safeSamples = 0, exactSamples = 0;
            long committedChars = 0, safeChars = 0, wrongChars = 0;
            var rows = new List<string> { "text,code,committed_chars,safe_chars,wrong_chars,final_top1_correct" };
            foreach (EvalCaseDto item in cases)
            {
                string committed = string.Empty, proposed = string.Empty;
                int stable = 0;
                for (int length = 1; length <= item.Code.Length; length++)
                {
                    SentenceDecodeResult decoded = decoder.Decode(item.Code.Substring(0, length), 20);
                    string next = ChooseConfidentPrefix(decoded, threshold);
                    if (next == proposed && next.Length > committed.Length)
                    {
                        stable++;
                    }
                    else if (next != proposed)
                    {
                        proposed = next;
                        stable = 1;
                    }

                    if (proposed.Length > committed.Length && stable >= stabilityRequired)
                    {
                        committed = proposed;
                    }
                }

                int safe = CommonPrefixLength(committed, item.Text);
                int wrong = committed.Length - safe;
                bool finalCorrect = string.Equals(decoder.Decode(item.Code, 20).Candidates.FirstOrDefault()?.Text, item.Text, StringComparison.Ordinal);
                if (committed.Length > 0) committedSamples++;
                if (safe == committed.Length) safeSamples++;
                if (committed.Length == item.Text.Length && safe == committed.Length) exactSamples++;
                committedChars += committed.Length;
                safeChars += safe;
                wrongChars += Math.Max(0, wrong);
                rows.Add(JsonString(item.Text) + "," + JsonString(item.Code) + "," + committed.Length + "," + safe + "," + Math.Max(0, wrong) + "," + finalCorrect.ToString().ToLowerInvariant());
            }

            string summary = "samples=" + cases.Count +
                ",threshold=" + threshold.ToString("F4", CultureInfo.InvariantCulture) +
                ",stability=" + stabilityRequired +
                ",early_sample_rate=" + Ratio(committedSamples, cases.Count).ToString("F4", CultureInfo.InvariantCulture) +
                ",safe_sample_rate=" + Ratio(safeSamples, cases.Count).ToString("F4", CultureInfo.InvariantCulture) +
                ",exact_sample_rate=" + Ratio(exactSamples, cases.Count).ToString("F4", CultureInfo.InvariantCulture) +
                ",committed_chars=" + committedChars +
                ",safe_chars=" + safeChars +
                ",wrong_chars=" + wrongChars;
            Console.WriteLine(summary);
            if (!string.IsNullOrEmpty(outputPath)) File.WriteAllLines(outputPath, rows, new UTF8Encoding(false));
            return 0;
        }

        private static int RunSentenceEarlyCommitEval(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath,
            int caseLimit)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            AppendEarlyCommitRegressionCases(cases);
            if (caseLimit > 0 && cases.Count > caseLimit)
            {
                cases = cases.Take(caseLimit).ToList();
            }

            SentenceLexiconIndex index = SentenceLexiconIndex.Build(
                LoadSentenceLexiconSource(lexiconPath));
            using (SentenceNgramModel model = SentenceNgramModel.Load(modelPath))
            {
                var baselineCorrect = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (EvalCaseDto item in cases)
                {
                    var decoder = new SentenceInputDecoder(
                        index,
                        model,
                        emittedCharacterReward: 2.0);
                    SentenceCandidate top = decoder.DecodeFull(item.Code, 20)
                        .Candidates.FirstOrDefault();
                    baselineCorrect[item.Code] = top != null &&
                        string.Equals(top.Text, item.Text, StringComparison.Ordinal);
                }

                var rows = new List<string>
                {
                    "mode,text,code,source,baseline_correct,early_events,early_committed_text," +
                    "safe,final_text,final_exact,first_commit_key,retained_before_final," +
                    "mean_retained_codes,mean_eligible_retained_codes"
                };
                var summaries = new List<string>();
                EvaluateSentenceEarlyCommitMode(
                    cases, index, model, baselineCorrect,
                    mode: "early-commit",
                    rows: rows,
                    summaries: summaries);

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.WriteAllLines(outputPath, rows, new UTF8Encoding(false));
                    File.WriteAllLines(
                        Path.ChangeExtension(outputPath, ".summary.txt"),
                        summaries,
                        new UTF8Encoding(false));
                }
            }

            return 0;
        }

        private static int RunSentenceEarlyCommitPolicyEval(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath,
            int caseLimit,
            string selectedMode)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            AppendEarlyCommitRegressionCases(cases);
            if (caseLimit > 0 && cases.Count > caseLimit)
            {
                cases = cases.Take(caseLimit).ToList();
            }

            SentenceLexiconIndex index = SentenceLexiconIndex.Build(
                LoadSentenceLexiconSource(lexiconPath));
            using (SentenceNgramModel model = SentenceNgramModel.Load(modelPath))
            {
                var baselineCorrect = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (EvalCaseDto item in cases)
                {
                    var decoder = new SentenceInputDecoder(
                        index,
                        model,
                        emittedCharacterReward: 2.0);
                    SentenceCandidate top = decoder.DecodeFull(item.Code, 20)
                        .Candidates.FirstOrDefault();
                    baselineCorrect[item.Code] = top != null &&
                        string.Equals(top.Text, item.Text, StringComparison.Ordinal);
                }

                var rows = new List<string>
                {
                    "mode,text,code,source,baseline_correct,early_events,early_committed_text," +
                    "safe,final_text,final_exact,first_commit_key,retained_before_final," +
                    "mean_retained_codes,mean_eligible_retained_codes"
                };
                var summaries = new List<string>();
                if (ShouldRunSentenceEarlyCommitPolicyMode(selectedMode, "current"))
                {
                    EvaluateSentenceEarlyCommitMode(
                        cases, index, model, baselineCorrect,
                        mode: "current",
                        rows: rows,
                        summaries: summaries);
                }
                if (ShouldRunSentenceEarlyCommitPolicyMode(selectedMode, "weak-evidence-2"))
                {
                    EvaluateSentenceEarlyCommitMode(
                        cases, index, model, baselineCorrect,
                        mode: "weak-evidence-2",
                        rows: rows,
                        summaries: summaries,
                        configureEngine: engine =>
                            engine.SentenceEarlyCommitRequiredEvidenceCount = 2);
                }
                if (ShouldRunSentenceEarlyCommitPolicyMode(selectedMode, "retain-raw-0"))
                {
                    EvaluateSentenceEarlyCommitMode(
                        cases, index, model, baselineCorrect,
                        mode: "retain-raw-0",
                        rows: rows,
                        summaries: summaries,
                        configureEngine: engine =>
                            engine.SentenceEarlyCommitMinimumRetainedRawLength = 0);
                }
                if (ShouldRunSentenceEarlyCommitPolicyMode(selectedMode, "count-merged-tail"))
                {
                    EvaluateSentenceEarlyCommitMode(
                        cases, index, model, baselineCorrect,
                        mode: "count-merged-tail",
                        rows: rows,
                        summaries: summaries,
                        configureEngine: engine =>
                            engine.SentenceEarlyCommitCountMergedTailEvidence = true);
                }
                if (ShouldRunSentenceEarlyCommitPolicyMode(selectedMode, "count-merged-tail-weak2"))
                {
                    EvaluateSentenceEarlyCommitMode(
                        cases, index, model, baselineCorrect,
                        mode: "count-merged-tail-weak2",
                        rows: rows,
                        summaries: summaries,
                        configureEngine: engine =>
                        {
                            engine.SentenceEarlyCommitRequiredEvidenceCount = 2;
                            engine.SentenceEarlyCommitCountMergedTailEvidence = true;
                        });
                }
                if (ShouldRunSentenceEarlyCommitPolicyMode(selectedMode, "count-merged-tail-retain0"))
                {
                    EvaluateSentenceEarlyCommitMode(
                        cases, index, model, baselineCorrect,
                        mode: "count-merged-tail-retain0",
                        rows: rows,
                        summaries: summaries,
                        configureEngine: engine =>
                        {
                            engine.SentenceEarlyCommitMinimumRetainedRawLength = 0;
                            engine.SentenceEarlyCommitCountMergedTailEvidence = true;
                        });
                }
                if (ShouldRunSentenceEarlyCommitPolicyMode(selectedMode, "combined-relaxed"))
                {
                    EvaluateSentenceEarlyCommitMode(
                        cases, index, model, baselineCorrect,
                        mode: "combined-relaxed",
                        rows: rows,
                        summaries: summaries,
                        configureEngine: engine =>
                        {
                            engine.SentenceEarlyCommitRequiredEvidenceCount = 2;
                            engine.SentenceEarlyCommitMinimumRetainedRawLength = 0;
                            engine.SentenceEarlyCommitCountMergedTailEvidence = true;
                        });
                }

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.WriteAllLines(outputPath, rows, new UTF8Encoding(false));
                    File.WriteAllLines(
                        Path.ChangeExtension(outputPath, ".summary.txt"),
                        summaries,
                        new UTF8Encoding(false));
                }
            }

            return 0;
        }

        private static bool ShouldRunSentenceEarlyCommitPolicyMode(
            string selectedMode,
            string mode)
        {
            return string.IsNullOrEmpty(selectedMode) ||
                string.Equals(selectedMode, mode, StringComparison.OrdinalIgnoreCase);
        }

        private static void EvaluateSentenceEarlyCommitMode(
            List<EvalCaseDto> cases,
            SentenceLexiconIndex index,
            ISentenceLanguageModel model,
            Dictionary<string, bool> baselineCorrect,
            string mode,
            List<string> rows,
            List<string> summaries,
            Action<InputMethodEngine> configureEngine = null)
        {
            var tally = new SentenceEarlyCommitEvalTally();
            var bucketTallies = new Dictionary<string, SentenceEarlyCommitEvalTally>(
                StringComparer.Ordinal)
            {
                ["5-8"] = new SentenceEarlyCommitEvalTally(),
                ["9-12"] = new SentenceEarlyCommitEvalTally(),
                ["13-18"] = new SentenceEarlyCommitEvalTally(),
                ["19-30"] = new SentenceEarlyCommitEvalTally(),
                ["31+"] = new SentenceEarlyCommitEvalTally()
            };
            var bucketCounts = bucketTallies.Keys.ToDictionary(
                key => key,
                key => 0,
                StringComparer.Ordinal);
            string stateRoot = Path.Combine(
                Path.GetTempPath(),
                "tigerclaw-early-commit-eval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stateRoot);
            try
            {
                var state = new CoreRuntimeState(stateRoot);
                EnableSentenceMode(state);
                state.TrySetConfigValue("整句神经重排", "否", out _, out _);
                state.TrySetConfigValue("整句自动提前上屏", "是", out _, out _);
                state.TrySetConfigValue("保留最少编码数量", "0", out _, out _);

                foreach (EvalCaseDto item in cases)
                {
                    var decoder = new SentenceInputDecoder(
                        index,
                        model,
                        emittedCharacterReward: 2.0);
                    var engine = new InputMethodEngine(
                        state,
                        decoder,
                        sentenceDecodeSynchronously: true);
                    configureEngine?.Invoke(engine);
                    SentenceEarlyCommitCaseResult result = EvaluateSentenceEarlyCommitCase(
                        engine,
                        item,
                        baselineCorrect.TryGetValue(item.Code, out bool correct) && correct);
                    tally.Add(result, item.Code.Length);
                    string bucket = SentenceEarlyCommitLengthBucket(item.Code.Length);
                    bucketTallies[bucket].Add(result, item.Code.Length);
                    bucketCounts[bucket]++;
                    rows.Add(FormatSentenceEarlyCommitCsvRow(mode, item, result));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(stateRoot, true);
                }
                catch
                {
                }
            }

            string overall = tally.Format(mode, cases.Count);
            summaries.Add(overall);
            Console.WriteLine(overall);
            foreach (string bucket in new[] { "5-8", "9-12", "13-18", "19-30", "31+" })
            {
                if (bucketCounts[bucket] == 0)
                {
                    continue;
                }
                string summary = bucketTallies[bucket].Format(
                    mode + ":length-" + bucket,
                    bucketCounts[bucket]);
                summaries.Add(summary);
                Console.WriteLine(summary);
            }
        }

        private static string SentenceEarlyCommitLengthBucket(int codeLength)
        {
            if (codeLength <= 8) return "5-8";
            if (codeLength <= 12) return "9-12";
            if (codeLength <= 18) return "13-18";
            if (codeLength <= 30) return "19-30";
            return "31+";
        }

        private static SentenceEarlyCommitCaseResult EvaluateSentenceEarlyCommitCase(
            InputMethodEngine engine,
            EvalCaseDto item,
            bool baselineCorrect)
        {
            var result = new SentenceEarlyCommitCaseResult
            {
                BaselineCorrect = baselineCorrect,
                Safe = true,
                FirstCommitKey = 0
            };
            var committed = new StringBuilder();
            for (int index = 0; index < item.Code.Length; index++)
            {
                char code = item.Code[index];
                KeyEngineResult keyResult = Press(engine, VirtualKeyForSentenceCode(code));
                string output = keyResult?.TextToOutput ?? string.Empty;
                EngineUiSnapshot snapshot = engine.GetUiSnapshot(20);
                int retained = CountRawCodeCharacters(snapshot.ActiveInputCode);
                result.RetentionSamples.Add(retained);
                if (index + 1 > 4)
                {
                    result.EligibleRetentionSamples.Add(retained);
                }

                if (output.Length == 0)
                {
                    continue;
                }

                result.EarlyEvents++;
                if (result.FirstCommitKey == 0)
                {
                    result.FirstCommitKey = index + 1;
                }
                committed.Append(output);
                result.PostCommitRetained.Add(retained);
                if (!item.Text.StartsWith(committed.ToString(), StringComparison.Ordinal))
                {
                    result.Safe = false;
                    result.UnsafeEvents++;
                }
            }

            EngineUiSnapshot beforeFinal = engine.GetUiSnapshot(20);
            result.RetainedBeforeFinal = CountRawCodeCharacters(beforeFinal.ActiveInputCode);
            result.EarlyCommittedText = committed.ToString();
            KeyEngineResult final = Press(engine, 0x20);
            committed.Append(final?.TextToOutput ?? string.Empty);
            result.FinalText = committed.ToString();
            result.FinalExact = string.Equals(result.FinalText, item.Text, StringComparison.Ordinal);
            return result;
        }

        private static int VirtualKeyForSentenceCode(char value)
        {
            if (value >= 'a' && value <= 'z')
            {
                return char.ToUpperInvariant(value);
            }
            if (value >= 'A' && value <= 'Z')
            {
                return value;
            }
            if (value >= '0' && value <= '9')
            {
                return value;
            }
            if (value == ';')
            {
                return 0xBA;
            }
            if (value == '\'')
            {
                return 0xDE;
            }
            return value;
        }

        private static int CountRawCodeCharacters(string displayCode)
        {
            if (string.IsNullOrEmpty(displayCode))
            {
                return 0;
            }

            int count = 0;
            foreach (char value in displayCode)
            {
                if (!char.IsWhiteSpace(value))
                {
                    count++;
                }
            }
            return count;
        }

        private static string FormatSentenceEarlyCommitCsvRow(
            string mode,
            EvalCaseDto item,
            SentenceEarlyCommitCaseResult result)
        {
            double meanRetained = result.RetentionSamples.Count == 0
                ? 0.0
                : result.RetentionSamples.Average();
            double meanEligibleRetained = result.EligibleRetentionSamples.Count == 0
                ? 0.0
                : result.EligibleRetentionSamples.Average();
            return JsonString(mode) + "," +
                JsonString(item.Text) + "," +
                JsonString(item.Code) + "," +
                JsonString(item.Source) + "," +
                result.BaselineCorrect.ToString().ToLowerInvariant() + "," +
                result.EarlyEvents + "," +
                JsonString(result.EarlyCommittedText) + "," +
                result.Safe.ToString().ToLowerInvariant() + "," +
                JsonString(result.FinalText) + "," +
                result.FinalExact.ToString().ToLowerInvariant() + "," +
                result.FirstCommitKey + "," +
                result.RetainedBeforeFinal + "," +
                meanRetained.ToString("F3", CultureInfo.InvariantCulture) + "," +
                meanEligibleRetained.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static void AppendEarlyCommitRegressionCases(List<EvalCaseDto> cases)
        {
            AddEvalCaseIfMissing(cases, "新人上午来面试", "iejryfenahbmsp");
            AddEvalCaseIfMissing(cases, "左手匕首", "nuusvbbhoi");
            AddEvalCaseIfMissing(cases, "有一些人在这里看东西", "nvfisvmjrngvduqryxvx");
        }

        private sealed class SentenceEarlyCommitCaseResult
        {
            public bool BaselineCorrect;
            public bool Safe;
            public bool FinalExact;
            public int EarlyEvents;
            public int UnsafeEvents;
            public int FirstCommitKey;
            public int RetainedBeforeFinal;
            public string EarlyCommittedText = string.Empty;
            public string FinalText = string.Empty;
            public readonly List<int> RetentionSamples = new List<int>();
            public readonly List<int> EligibleRetentionSamples = new List<int>();
            public readonly List<int> PostCommitRetained = new List<int>();
        }

        private sealed class SentenceEarlyCommitEvalTally
        {
            private int _baselineCorrect;
            private int _baselineCorrectFinalExact;
            private int _baselineCorrectEarlyCases;
            private int _baselineCorrectSafeEarlyCases;
            private int _baselineCorrectUnsafeCases;
            private int _earlyCases;
            private int _safeEarlyCases;
            private int _unsafeCases;
            private int _finalExact;
            private int _earlyEvents;
            private int _unsafeEvents;
            private long _totalCodeLength;
            private long _retainedBeforeFinal;
            private readonly List<int> _retentionSamples = new List<int>();
            private readonly List<int> _eligibleRetentionSamples = new List<int>();
            private readonly List<int> _postCommitRetained = new List<int>();
            private readonly List<int> _firstCommitKeys = new List<int>();

            public void Add(SentenceEarlyCommitCaseResult result, int codeLength)
            {
                if (result.BaselineCorrect)
                {
                    _baselineCorrect++;
                    if (result.FinalExact)
                    {
                        _baselineCorrectFinalExact++;
                    }
                    if (result.EarlyEvents > 0)
                    {
                        _baselineCorrectEarlyCases++;
                        if (result.Safe)
                        {
                            _baselineCorrectSafeEarlyCases++;
                        }
                    }
                    if (!result.Safe)
                    {
                        _baselineCorrectUnsafeCases++;
                    }
                }
                if (result.EarlyEvents > 0)
                {
                    _earlyCases++;
                    if (result.Safe)
                    {
                        _safeEarlyCases++;
                    }
                    _firstCommitKeys.Add(result.FirstCommitKey);
                }
                if (!result.Safe)
                {
                    _unsafeCases++;
                }
                if (result.FinalExact)
                {
                    _finalExact++;
                }
                _earlyEvents += result.EarlyEvents;
                _unsafeEvents += result.UnsafeEvents;
                _totalCodeLength += codeLength;
                _retainedBeforeFinal += result.RetainedBeforeFinal;
                _retentionSamples.AddRange(result.RetentionSamples);
                _eligibleRetentionSamples.AddRange(result.EligibleRetentionSamples);
                _postCommitRetained.AddRange(result.PostCommitRetained);
            }

            public string Format(string mode, int cases)
            {
                return "mode=" + mode +
                    ",cases=" + cases +
                    ",baseline_top1_rate=" + FormatRatio(_baselineCorrect, cases) +
                    ",early_case_rate=" + FormatRatio(_earlyCases, cases) +
                    ",safe_early_case_rate=" + FormatRatio(_safeEarlyCases, _earlyCases) +
                    ",unsafe_case_rate=" + FormatRatio(_unsafeCases, cases) +
                    ",baseline_correct_safe_early_case_rate=" +
                        FormatRatio(_baselineCorrectSafeEarlyCases, _baselineCorrectEarlyCases) +
                    ",baseline_correct_unsafe_case_rate=" +
                        FormatRatio(_baselineCorrectUnsafeCases, _baselineCorrect) +
                    ",unsafe_events=" + _unsafeEvents +
                    ",early_events=" + _earlyEvents +
                    ",final_exact_rate=" + FormatRatio(_finalExact, cases) +
                    ",baseline_correct_final_exact_rate=" +
                        FormatRatio(_baselineCorrectFinalExact, _baselineCorrect) +
                    ",mean_retained_codes=" + FormatMean(_retentionSamples) +
                    ",p50_retained_codes=" + FormatPercentile(_retentionSamples, 0.50) +
                    ",p90_retained_codes=" + FormatPercentile(_retentionSamples, 0.90) +
                    ",mean_eligible_retained_codes=" + FormatMean(_eligibleRetentionSamples) +
                    ",p50_eligible_retained_codes=" +
                        FormatPercentile(_eligibleRetentionSamples, 0.50) +
                    ",p90_eligible_retained_codes=" +
                        FormatPercentile(_eligibleRetentionSamples, 0.90) +
                    ",mean_post_commit_retained_codes=" + FormatMean(_postCommitRetained) +
                    ",mean_first_commit_key=" + FormatMean(_firstCommitKeys) +
                    ",raw_commit_coverage_before_final=" +
                        (1.0 - Ratio(_retainedBeforeFinal, _totalCodeLength))
                            .ToString("F4", CultureInfo.InvariantCulture);
            }

            private static string FormatRatio(long value, long total)
            {
                return Ratio(value, total).ToString("F4", CultureInfo.InvariantCulture);
            }

            private static string FormatMean(List<int> values)
            {
                return values.Count == 0
                    ? "0.000"
                    : values.Average().ToString("F3", CultureInfo.InvariantCulture);
            }

            private static string FormatPercentile(List<int> values, double percentile)
            {
                if (values.Count == 0)
                {
                    return "0";
                }
                int[] ordered = values.OrderBy(value => value).ToArray();
                int index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
                index = Math.Max(0, Math.Min(ordered.Length - 1, index));
                return ordered[index].ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string ChooseConfidentPrefix(SentenceDecodeResult decoded, double threshold)
        {
            if (decoded == null || decoded.Candidates == null || decoded.Candidates.Length == 0) return string.Empty;
            double max = decoded.Candidates.Max(candidate => candidate.FinalScore);
            double total = decoded.Candidates.Sum(candidate => Math.Exp(candidate.FinalScore - max));
            string best = string.Empty;
            foreach (SentenceCandidate candidate in decoded.Candidates)
            {
                string prefix = candidate.Text;
                while (prefix.Length > 0)
                {
                    double mass = decoded.Candidates.Where(item => item.Text.StartsWith(prefix, StringComparison.Ordinal))
                        .Sum(item => Math.Exp(item.FinalScore - max)) / total;
                    if (mass >= threshold && prefix.Length < candidate.Text.Length)
                    {
                        best = prefix;
                        break;
                    }
                    prefix = prefix.Substring(0, prefix.Length - 1);
                }
            }
            return best;
        }

        private static int CommonPrefixLength(string left, string right)
        {
            int length = Math.Min(left.Length, right.Length), index = 0;
            while (index < length && left[index] == right[index]) index++;
            return index;
        }

        private static double Ratio(long value, long total) { return total <= 0 ? 0.0 : (double)value / total; }
        private static double ParseDouble(string value, double fallback) { double parsed; return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback; }
        private static int ParseInt(string value, int fallback) { int parsed; return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback; }

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
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(
                source,
                SentenceCharacterRanks.TakeTop(CoreRuntimeState.DefaultSentenceOptimalCodeHighFreqLimit),
                CoreRuntimeState.ParseCharacterSet(CoreRuntimeState.DefaultSentenceFullCodeWhitelist));
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

        private static int RunSentenceLengthCompare(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath,
            double[] requestedWeights)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            Dictionary<string, List<string>> source = LoadSentenceLexiconSource(lexiconPath);
            Stopwatch watch = Stopwatch.StartNew();
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(source);
            long loadMilliseconds = watch.ElapsedMilliseconds;
            watch.Restart();

            var configs = new List<LengthBiasConfig>
            {
                new LengthBiasConfig { Label = "current-no-length-bias", Weight = 0.0 }
            };
            double[] weights = requestedWeights ?? new[] { 1.5, 1.8, 2.0, 2.2, 2.4, 2.6 };
            for (int i = 0; i < weights.Length; i++)
            {
                double weight = Math.Max(0.0, weights[i]);
                configs.Add(new LengthBiasConfig
                {
                    Label = "add-" + weight.ToString("0.0###", CultureInfo.InvariantCulture),
                    Weight = weight
                });
            }

            int workers = 8;
            var ownedModels = new ConcurrentBag<SentenceNgramModel>();
            var locals = new ThreadLocal<LengthBiasLocal>(() =>
            {
                SentenceNgramModel localModel = SentenceNgramModel.Load(modelPath);
                ownedModels.Add(localModel);
                return new LengthBiasLocal
                {
                    Decoders = configs
                        .Select(config => new SentenceInputDecoder(
                            index,
                            localModel,
                            emittedCharacterReward: config.Weight))
                        .ToArray()
                };
            });

            var tallies = new EvalTally[configs.Count];
            var gained = new ConcurrentBag<string>[configs.Count];
            var lost = new ConcurrentBag<string>[configs.Count];
            for (int c = 0; c < configs.Count; c++)
            {
                tallies[c] = new EvalTally();
                gained[c] = new ConcurrentBag<string>();
                lost[c] = new ConcurrentBag<string>();
            }

            int done = 0;
            try
            {
                ReportLengthBiasHandcrafted(locals.Value.Decoders, configs);

                Parallel.For(
                    0,
                    cases.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    i =>
                    {
                        EvalCaseDto item = cases[i];
                        int beforeRank = 0;
                        string beforeTop = string.Empty;
                        for (int c = 0; c < configs.Count; c++)
                        {
                            SentenceDecodeResult decoded = locals.Value.Decoders[c].Decode(item.Code, 20);
                            int afterRank;
                            string afterTop;
                            RankDecoded(decoded, item.Text, out afterRank, out afterTop);
                            lock (tallies[c])
                            {
                                AccumulateTally(tallies[c], afterRank);
                            }

                            if (c == 0)
                            {
                                beforeRank = afterRank;
                                beforeTop = afterTop;
                            }
                            else if (beforeRank != 1 && afterRank == 1)
                            {
                                gained[c].Add(FormatCompareFlip(item, beforeRank, afterRank, beforeTop, afterTop));
                            }
                            else if (beforeRank == 1 && afterRank != 1)
                            {
                                lost[c].Add(FormatCompareFlip(item, beforeRank, afterRank, beforeTop, afterTop));
                            }
                        }

                        int finished = Interlocked.Increment(ref done);
                        if (finished % 200 == 0)
                        {
                            Console.Error.WriteLine("length-compare " + finished + "/" + cases.Count);
                        }
                    });

                watch.Stop();
                var summaries = new List<string>();
                for (int c = 0; c < configs.Count; c++)
                {
                    string summary = FormatEvalSummary(
                        configs[c].Label,
                        tallies[c],
                        cases.Count,
                        loadMilliseconds,
                        watch.Elapsed.TotalSeconds);
                    summaries.Add(summary);
                    Console.WriteLine(summary);
                    if (c > 0)
                    {
                        Console.WriteLine(
                            "workers=" + workers +
                            " " + configs[c].Label +
                            " gained_top1=" + gained[c].Count +
                            " lost_top1=" + lost[c].Count);
                        ReportCompareBag("gained-top1@" + configs[c].Label, gained[c]);
                        ReportCompareBag("lost-top1@" + configs[c].Label, lost[c]);
                    }
                }

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(
                        outputPath,
                        "{\"summaries\":[" + string.Join(",", summaries.ToArray()) + "]}",
                        new UTF8Encoding(false));
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

        private sealed class LengthBiasConfig
        {
            public string Label;
            public double Weight;
        }

        private sealed class LengthBiasLocal
        {
            public SentenceInputDecoder[] Decoders;
        }

        private static void RankDecoded(
            SentenceDecodeResult decoded,
            string gold,
            out int rank,
            out string top)
        {
            rank = 0;
            top = string.Empty;
            if (decoded == null || decoded.Candidates == null || decoded.Candidates.Length == 0)
            {
                return;
            }

            top = decoded.Candidates[0].Text;
            for (int i = 0; i < decoded.Candidates.Length; i++)
            {
                if (string.Equals(decoded.Candidates[i].Text, gold, StringComparison.Ordinal))
                {
                    rank = i + 1;
                    return;
                }
            }
        }

        private static void ReportLengthBiasHandcrafted(
            SentenceInputDecoder[] decoders,
            List<LengthBiasConfig> configs)
        {
            var extras = new[]
            {
                new EvalCaseDto { Text = "那依你之见", Code = "aujtijxriej" },
                new EvalCaseDto { Text = "坐皮艇", Code = "jjgrpitgu" },
                new EvalCaseDto { Text = "坐皮艇划回去", Code = "jjgrpitgupprdgk" }
            };
            for (int i = 0; i < extras.Length; i++)
            {
                for (int c = 0; c < configs.Count; c++)
                {
                    SentenceDecodeResult decoded = decoders[c].Decode(extras[i].Code, 20);
                    int rank;
                    string top;
                    RankDecoded(decoded, extras[i].Text, out rank, out top);
                    Console.WriteLine(
                        "handcrafted\t" + configs[c].Label +
                        "\tgold=" + extras[i].Text +
                        "\tlen=" + CountTextElements(extras[i].Text) +
                        "\ttop=" + top +
                        "\ttoplen=" + CountTextElements(top) +
                        "\trank=" + FormatRank(rank));
                }
            }
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

        private static int RunSentenceBoundaryCompare(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            Dictionary<string, List<string>> source = LoadSentenceLexiconSource(lexiconPath);
            Stopwatch watch = Stopwatch.StartNew();
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(source);
            long loadMilliseconds = watch.ElapsedMilliseconds;
            watch.Restart();

            int workers = 12;
            var ownedModels = new ConcurrentBag<SentenceNgramModel>();
            var locals = new ThreadLocal<BoundaryCompareLocal>(() =>
            {
                SentenceNgramModel localModel = SentenceNgramModel.Load(modelPath);
                ownedModels.Add(localModel);
                return new BoundaryCompareLocal
                {
                    WithBoundary = new SentenceInputDecoder(index, localModel),
                    WithoutBoundary = new SentenceInputDecoder(
                        index,
                        localModel,
                        isolationPenalty: SentenceIsolationPenalty.CreateDefault(),
                        scoreSentenceBoundaries: false)
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
                        BoundaryCompareLocal local = locals.Value;
                        SentenceDecodeResult beforeDecoded = local.WithBoundary.Decode(item.Code, 20);
                        SentenceDecodeResult afterDecoded = local.WithoutBoundary.Decode(item.Code, 20);
                        int beforeRank = RankOf(beforeDecoded, item.Text);
                        int afterRank = RankOf(afterDecoded, item.Text);
                        string beforeTop = beforeDecoded.Candidates != null && beforeDecoded.Candidates.Length > 0
                            ? beforeDecoded.Candidates[0].Text
                            : string.Empty;
                        string afterTop = afterDecoded.Candidates != null && afterDecoded.Candidates.Length > 0
                            ? afterDecoded.Candidates[0].Text
                            : string.Empty;
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
                            Console.Error.WriteLine("boundary-compare " + finished + "/" + cases.Count);
                        }
                    });

                watch.Stop();
                string beforeSummary = FormatEvalSummary(
                    "current-bos-eos",
                    beforeTally,
                    cases.Count,
                    loadMilliseconds,
                    watch.Elapsed.TotalSeconds);
                string afterSummary = FormatEvalSummary(
                    "bos-eos-bigram-no-unigram",
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

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(
                        outputPath,
                        "{\"before\":" + beforeSummary +
                        ",\"after\":" + afterSummary +
                        ",\"gained_top1\":[" + string.Join(",", gained.ToArray()) +
                        "],\"lost_top1\":[" + string.Join(",", lost.ToArray()) +
                        "],\"recall_lost\":[" + string.Join(",", recallLost.ToArray()) + "]}",
                        new UTF8Encoding(false));
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

        private static int RunSentenceSoftPinCompare(
            string casesPath,
            string modelPath,
            string lexiconPath,
            string outputPath)
        {
            List<EvalCaseDto> cases = LoadEvalCases(casesPath);
            Dictionary<string, List<string>> source = LoadSentenceLexiconSource(lexiconPath);
            Stopwatch watch = Stopwatch.StartNew();
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(source);
            long loadMilliseconds = watch.ElapsedMilliseconds;
            watch.Restart();

            double[] bonuses = { 0.5, 1.0, 2.0, 3.0 };
            int workers = 8;
            var ownedModels = new ConcurrentBag<SentenceNgramModel>();
            var locals = new ThreadLocal<SentenceInputDecoder>(() =>
            {
                SentenceNgramModel localModel = SentenceNgramModel.Load(modelPath);
                ownedModels.Add(localModel);
                return new SentenceInputDecoder(index, localModel);
            });

            var baselineTally = new EvalTally();
            var tallies = new EvalTally[bonuses.Length];
            var gained = new ConcurrentBag<string>[bonuses.Length];
            var lost = new ConcurrentBag<string>[bonuses.Length];
            for (int b = 0; b < bonuses.Length; b++)
            {
                tallies[b] = new EvalTally();
                gained[b] = new ConcurrentBag<string>();
                lost[b] = new ConcurrentBag<string>();
            }

            int done = 0;
            try
            {
                ReportSoftPinHandcrafted(locals.Value, bonuses);

                Parallel.For(
                    0,
                    cases.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    i =>
                    {
                        EvalCaseDto item = cases[i];
                        SentenceInputDecoder decoder = locals.Value;
                        List<SoftPinSnapshot> snapshots = CollectSoftPinSnapshots(decoder, item.Code, 50);
                        int beforeRank;
                        string beforeTop;
                        ReplaySoftPin(snapshots, 0.0, item.Text, out beforeRank, out beforeTop);
                        lock (baselineTally)
                        {
                            AccumulateTally(baselineTally, beforeRank);
                        }

                        for (int b = 0; b < bonuses.Length; b++)
                        {
                            int afterRank;
                            string afterTop;
                            ReplaySoftPin(snapshots, bonuses[b], item.Text, out afterRank, out afterTop);
                            lock (baselineTally)
                            {
                                AccumulateTally(tallies[b], afterRank);
                            }

                            if (beforeRank != 1 && afterRank == 1)
                            {
                                gained[b].Add(FormatCompareFlip(item, beforeRank, afterRank, beforeTop, afterTop));
                            }
                            else if (beforeRank == 1 && afterRank != 1)
                            {
                                lost[b].Add(FormatCompareFlip(item, beforeRank, afterRank, beforeTop, afterTop));
                            }
                        }

                        int finished = Interlocked.Increment(ref done);
                        if (finished % 200 == 0)
                        {
                            Console.Error.WriteLine("softpin-compare " + finished + "/" + cases.Count);
                        }
                    });

                watch.Stop();
                var summaries = new List<string>();
                string baselineSummary = FormatEvalSummary(
                    "current-no-softpin",
                    baselineTally,
                    cases.Count,
                    loadMilliseconds,
                    watch.Elapsed.TotalSeconds);
                summaries.Add(baselineSummary);
                Console.WriteLine(baselineSummary);
                for (int b = 0; b < bonuses.Length; b++)
                {
                    string label = "softpin-" + bonuses[b].ToString("0.#", CultureInfo.InvariantCulture);
                    string summary = FormatEvalSummary(
                        label,
                        tallies[b],
                        cases.Count,
                        loadMilliseconds,
                        watch.Elapsed.TotalSeconds);
                    summaries.Add(summary);
                    Console.WriteLine(summary);
                    Console.WriteLine(
                        "workers=" + workers +
                        " bonus=" + bonuses[b].ToString("0.#", CultureInfo.InvariantCulture) +
                        " gained_top1=" + gained[b].Count +
                        " lost_top1=" + lost[b].Count);
                    ReportCompareBag("gained-top1@" + bonuses[b].ToString("0.#", CultureInfo.InvariantCulture), gained[b]);
                    ReportCompareBag("lost-top1@" + bonuses[b].ToString("0.#", CultureInfo.InvariantCulture), lost[b]);
                }

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var payload = new StringBuilder();
                    payload.Append("{\"summaries\":[");
                    payload.Append(string.Join(",", summaries.ToArray()));
                    payload.Append("],\"sweeps\":[");
                    for (int b = 0; b < bonuses.Length; b++)
                    {
                        if (b > 0)
                        {
                            payload.Append(',');
                        }

                        payload.Append("{\"bonus\":");
                        payload.Append(bonuses[b].ToString("G9", CultureInfo.InvariantCulture));
                        payload.Append(",\"gained_top1\":[");
                        payload.Append(string.Join(",", gained[b].ToArray()));
                        payload.Append("],\"lost_top1\":[");
                        payload.Append(string.Join(",", lost[b].ToArray()));
                        payload.Append("]}");
                    }

                    payload.Append("]}");
                    File.WriteAllText(outputPath, payload.ToString(), new UTF8Encoding(false));
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

        private sealed class SoftPinSnapshot
        {
            public string[] Texts;
            public double[] Scores;
            public int[] Ranks;
        }

        private static List<SoftPinSnapshot> CollectSoftPinSnapshots(
            SentenceInputDecoder decoder,
            string code,
            int candidateLimit)
        {
            decoder.ResetDecodeCache();
            var snapshots = new List<SoftPinSnapshot>();
            if (string.IsNullOrEmpty(code))
            {
                return snapshots;
            }

            for (int length = 1; length <= code.Length; length++)
            {
                SentenceDecodeResult decoded = decoder.Decode(code.Substring(0, length), candidateLimit);
                snapshots.Add(CopySoftPinSnapshot(decoded));
            }

            return snapshots;
        }

        private static SoftPinSnapshot CopySoftPinSnapshot(SentenceDecodeResult decoded)
        {
            if (decoded == null || decoded.Candidates == null || decoded.Candidates.Length == 0)
            {
                return new SoftPinSnapshot
                {
                    Texts = Array.Empty<string>(),
                    Scores = Array.Empty<double>(),
                    Ranks = Array.Empty<int>()
                };
            }

            var snap = new SoftPinSnapshot
            {
                Texts = new string[decoded.Candidates.Length],
                Scores = new double[decoded.Candidates.Length],
                Ranks = new int[decoded.Candidates.Length]
            };
            for (int i = 0; i < decoded.Candidates.Length; i++)
            {
                snap.Texts[i] = decoded.Candidates[i].Text ?? string.Empty;
                snap.Scores[i] = decoded.Candidates[i].FinalScore;
                snap.Ranks[i] = decoded.Candidates[i].MaxLexiconRank;
            }

            return snap;
        }

        private static void ReplaySoftPin(
            List<SoftPinSnapshot> snapshots,
            double bonus,
            string gold,
            out int rank,
            out string top)
        {
            rank = 0;
            top = string.Empty;
            if (snapshots == null || snapshots.Count == 0)
            {
                return;
            }

            SoftPinSnapshot last = snapshots[snapshots.Count - 1];
            if (last.Texts == null || last.Texts.Length == 0)
            {
                return;
            }

            string unpinnedTop = last.Texts[0];
            string pin = string.Empty;
            if (bonus > 0.0)
            {
                for (int s = snapshots.Count - 2; s >= 0; s--)
                {
                    SoftPinSnapshot snap = snapshots[s];
                    if (snap.Texts == null || snap.Texts.Length == 0)
                    {
                        continue;
                    }

                    string prefixTop = snap.Texts[0];
                    if (CountTextElements(prefixTop) < 2)
                    {
                        continue;
                    }

                    if (unpinnedTop.StartsWith(prefixTop, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool survives = false;
                    int surviveLimit = Math.Min(3, last.Texts.Length);
                    for (int i = 0; i < surviveLimit; i++)
                    {
                        if (last.Texts[i].StartsWith(prefixTop, StringComparison.Ordinal))
                        {
                            survives = true;
                            break;
                        }
                    }

                    if (survives)
                    {
                        pin = prefixTop;
                        break;
                    }
                }

                if (gold != null && gold.IndexOf("坐皮艇划回去", StringComparison.Ordinal) >= 0)
                {
                    Console.WriteLine(
                        "softpin-pin\tbonus=" + bonus.ToString("0.#", CultureInfo.InvariantCulture) +
                        "\tpin=" + pin +
                        "\trank0=" + last.Ranks[0] +
                        "\trank1=" + (last.Ranks.Length > 1 ? last.Ranks[1].ToString() : "") +
                        "\ttext0=" + last.Texts[0] +
                        "\ttext1=" + (last.Texts.Length > 1 ? last.Texts[1] : ""));
                }
            }

            var order = new int[last.Texts.Length];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (left, right) =>
            {
                int rankCompare = last.Ranks[left].CompareTo(last.Ranks[right]);
                if (rankCompare != 0)
                {
                    return rankCompare;
                }

                double leftScore = last.Scores[left];
                double rightScore = last.Scores[right];
                if (pin.Length > 0)
                {
                    if (last.Texts[left].StartsWith(pin, StringComparison.Ordinal))
                    {
                        leftScore += bonus;
                    }

                    if (last.Texts[right].StartsWith(pin, StringComparison.Ordinal))
                    {
                        rightScore += bonus;
                    }
                }

                int scoreCompare = rightScore.CompareTo(leftScore);
                return scoreCompare != 0
                    ? scoreCompare
                    : string.CompareOrdinal(last.Texts[left], last.Texts[right]);
            });

            top = last.Texts[order[0]];
            for (int i = 0; i < order.Length; i++)
            {
                if (string.Equals(last.Texts[order[i]], gold, StringComparison.Ordinal))
                {
                    rank = i + 1;
                    return;
                }
            }
        }

        private static int CountTextElements(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return new StringInfo(text).LengthInTextElements;
        }

        private static void ReportSoftPinHandcrafted(SentenceInputDecoder decoder, double[] bonuses)
        {
            var extras = new[]
            {
                new EvalCaseDto { Text = "坐皮艇", Code = "jjgrpitgu" },
                new EvalCaseDto { Text = "坐皮艇划回去", Code = "jjgrpitgupprdgk" },
                new EvalCaseDto { Text = "从坡艇划回去", Code = "jjgrpitgupprdgk" }
            };
            for (int i = 0; i < extras.Length; i++)
            {
                EvalCaseDto item = extras[i];
                List<SoftPinSnapshot> snapshots = CollectSoftPinSnapshots(decoder, item.Code, 50);
                if (string.Equals(item.Text, "坐皮艇划回去", StringComparison.Ordinal))
                {
                    for (int s = 0; s < snapshots.Count; s++)
                    {
                        SoftPinSnapshot snap = snapshots[s];
                        if (snap.Texts == null || snap.Texts.Length == 0)
                        {
                            continue;
                        }

                        Console.WriteLine(
                            "softpin-trace\tlen=" + (s + 1) +
                            "\ttop=" + snap.Texts[0] +
                            "\tsecond=" + (snap.Texts.Length > 1 ? snap.Texts[1] : "") +
                            "\tscore0=" + snap.Scores[0].ToString("F3", CultureInfo.InvariantCulture) +
                            "\tscore1=" + (snap.Texts.Length > 1 ? snap.Scores[1].ToString("F3", CultureInfo.InvariantCulture) : ""));
                    }
                }

                int beforeRank;
                string beforeTop;
                ReplaySoftPin(snapshots, 0.0, item.Text, out beforeRank, out beforeTop);
                Console.WriteLine(
                    "handcrafted\tbonus=0\tgold=" + item.Text +
                    "\trank=" + FormatRank(beforeRank) + "\ttop=" + beforeTop);
                for (int b = 0; b < bonuses.Length; b++)
                {
                    int afterRank;
                    string afterTop;
                    ReplaySoftPin(snapshots, bonuses[b], item.Text, out afterRank, out afterTop);
                    Console.WriteLine(
                        "handcrafted\tbonus=" + bonuses[b].ToString("0.#", CultureInfo.InvariantCulture) +
                        "\tgold=" + item.Text +
                        "\trank=" + FormatRank(afterRank) + "\ttop=" + afterTop);
                }
            }
        }

        private sealed class BoundaryCompareLocal
        {
            public SentenceInputDecoder WithBoundary;
            public SentenceInputDecoder WithoutBoundary;
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
                    writer.Write((long)1);
                    writer.Write(((ulong)'a' << 21) | (uint)'b');
                    writer.Write(0.5f);
                    writer.Write(0);
                    writer.Write((long)0);
                    writer.Write((long)0);
                }

                using (SentenceNgramModel model = SentenceNgramModel.Load(path))
                {
                    double known = Math.Exp(model.LogProbability("\x02", "\x02", "a"));
                    double unknown = Math.Exp(model.LogProbability("\x02", "\x02", "b"));
                    double knownCached = Math.Exp(model.LogProbability("\x02", "\x02", "a"));
                    double withoutUnigram = Math.Exp(model.LogProbability("\x02", "\x02", "a", false));
                    double withoutUnigramCached = Math.Exp(model.LogProbability("\x02", "\x02", "a", false));
                    True(Math.Abs(known - 0.9) < 1e-6, nameof(SentenceNgramV2LoadsFromMappedFile) + ".known");
                    True(Math.Abs(unknown - 0.1) < 1e-6, nameof(SentenceNgramV2LoadsFromMappedFile) + ".unknown");
                    True(knownCached == known, nameof(SentenceNgramV2LoadsFromMappedFile) + ".known_cached");
                    True(
                        withoutUnigramCached == withoutUnigram && withoutUnigram < 1e-299,
                        nameof(SentenceNgramV2LoadsFromMappedFile) + ".without_unigram_cached");
                    True(model.HasObservedBigram("a", "b"), nameof(SentenceNgramV2LoadsFromMappedFile) + ".bigram");
                    True(model.HasObservedBigram("a", "b"), nameof(SentenceNgramV2LoadsFromMappedFile) + ".bigram_cached");
                    True(!model.HasObservedBigram("b", "a"), nameof(SentenceNgramV2LoadsFromMappedFile) + ".missing_bigram");
                    True(!model.HasObservedBigram("b", "a"), nameof(SentenceNgramV2LoadsFromMappedFile) + ".missing_bigram_cached");
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
            EnableSentenceMode(state);
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
            True(state.TrySetConfigValue("自动启用整句模式", "是", out _, out string autoReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + autoReason);
            True(state.TrySetConfigValue("当前码表", "虎码", out _, out string schemaReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + schemaReason);
            True(!state.IsSentenceInputActive(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".plain_schema");

            True(state.TrySetConfigValue("当前码表", "虎整句", out _, out string sentenceSchemaReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + sentenceSchemaReason);
            True(state.IsSentenceInputActive(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".named_schema");

            True(state.TrySetConfigValue("自动启用整句模式", "否", out _, out string autoOffReason),
                nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ": " + autoOffReason);
            True(!state.IsSentenceInputActive(), nameof(SentenceAutoEnableUsesSchemaNameWithoutChangingSwitch) + ".auto_off");
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
                nameof(SentenceDecoderRejectsEmbeddedBareOneKeyCharacter) + ".whole_input_all_ranks");

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
                "fiota",
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

        private static string DumpSentencePrefixes(SentenceDecodeResult result)
        {
            SentenceCandidate[] candidates = result.Candidates ?? Array.Empty<SentenceCandidate>();
            SentencePrefixEvidence[] prefixes = result.EarlyCommitEvidence == null
                ? Array.Empty<SentencePrefixEvidence>()
                : result.EarlyCommitEvidence.Prefixes ?? Array.Empty<SentencePrefixEvidence>();
            return "visible=" + string.Join("/", candidates.Select(item => item.Text)) +
                " prefixes=" + string.Join("/", prefixes.Select(prefix =>
                    prefix.Text + "@" + prefix.RawLength.ToString(CultureInfo.InvariantCulture) +
                    ":" + prefix.Share.ToString("0.000", CultureInfo.InvariantCulture) +
                    (prefix.BoundaryClosed ? "c" : "o")));
        }

        private static void AssertSentenceResultsEqual(
            SentenceDecodeResult left,
            SentenceDecodeResult right,
            string name)
        {
            SentenceCandidate[] leftCandidates = left.Candidates ?? Array.Empty<SentenceCandidate>();
            SentenceCandidate[] rightCandidates = right.Candidates ?? Array.Empty<SentenceCandidate>();
            AssertSentenceCandidateArraysEqual(leftCandidates, rightCandidates, name + ".visible");
            SentenceEarlyCommitEvidence leftEvidence =
                left.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
            SentenceEarlyCommitEvidence rightEvidence =
                right.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
            Equal(rightEvidence.Proposal, leftEvidence.Proposal, name + ".early.proposal");
            True(
                leftEvidence.ProposalShare == rightEvidence.ProposalShare,
                name + ".early.proposal_share");
            True(
                leftEvidence.ConfidenceTruncated == rightEvidence.ConfidenceTruncated,
                name + ".early.confidence_truncated");
            True(
                leftEvidence.NeutralIncompleteTail == rightEvidence.NeutralIncompleteTail,
                name + ".early.neutral_tail");
            True(
                leftEvidence.MergedIncompleteTail == rightEvidence.MergedIncompleteTail,
                name + ".early.merged_tail");
            True(
                leftEvidence.NeutralLowConfidence == rightEvidence.NeutralLowConfidence,
                name + ".early.neutral_low_confidence");
            True(
                leftEvidence.IgnoreNeuralConstraint == rightEvidence.IgnoreNeuralConstraint,
                name + ".early.ignore_neural");
            Dictionary<string, int> leftRawLengths = leftEvidence.RawLengths ??
                new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> rightRawLengths = rightEvidence.RawLengths ??
                new Dictionary<string, int>(StringComparer.Ordinal);
            True(leftRawLengths.Count == rightRawLengths.Count, name + ".early.raw_count");
            foreach (KeyValuePair<string, int> item in leftRawLengths)
            {
                True(
                    rightRawLengths.TryGetValue(item.Key, out int rightRawLength) &&
                    item.Value == rightRawLength,
                    name + ".early.raw." + item.Key);
            }
            SentencePrefixEvidence[] leftPrefixes = leftEvidence.Prefixes ??
                Array.Empty<SentencePrefixEvidence>();
            SentencePrefixEvidence[] rightPrefixes = rightEvidence.Prefixes ??
                Array.Empty<SentencePrefixEvidence>();
            True(leftPrefixes.Length == rightPrefixes.Length, name + ".early.prefix_count");
            foreach (SentencePrefixEvidence prefix in leftPrefixes)
            {
                True(rightPrefixes.Any(item =>
                        item.Text == prefix.Text &&
                        item.RawLength == prefix.RawLength &&
                        item.Share == prefix.Share &&
                        item.BoundaryShare == prefix.BoundaryShare &&
                        item.BoundaryClosed == prefix.BoundaryClosed),
                    name + ".early.prefix." + prefix.Text + "@" + prefix.RawLength);
            }
        }

        private static void AssertSentenceCandidateArraysEqual(
            SentenceCandidate[] leftCandidates,
            SentenceCandidate[] rightCandidates,
            string name)
        {
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
                    name + ".final_score." + index);
                True(
                    leftCandidates[index].BaseScore == rightCandidates[index].BaseScore,
                    name + ".base_score." + index);
                True(
                    leftCandidates[index].ConfidenceScore == rightCandidates[index].ConfidenceScore,
                    name + ".confidence_score." + index);
                True(
                    leftCandidates[index].SupplementScore == rightCandidates[index].SupplementScore,
                    name + ".supplement_score." + index);
                True(
                    leftCandidates[index].MaxLexiconRank == rightCandidates[index].MaxLexiconRank,
                    name + ".max_rank." + index);
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

        private static void SentenceIsolationPenaltyCachesCandidateText()
        {
            var model = new CountingIsolationLanguageModel();
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "龘龘" }
                }),
                model,
                isolationPenalty: SentenceIsolationPenalty.CreateDefault());

            decoder.DecodeFull("ab", 20);
            int firstObservedCalls = model.ObservedBigramCalls;
            True(firstObservedCalls > 0,
                nameof(SentenceIsolationPenaltyCachesCandidateText) + ".first_scan");
            decoder.DecodeFull("ab", 20);
            True(firstObservedCalls == model.ObservedBigramCalls,
                nameof(SentenceIsolationPenaltyCachesCandidateText) + ".cached");
        }

        private static void SentenceDecoderAggregatesCompositionPerformance()
        {
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "龘龘" }
                }),
                new CountingIsolationLanguageModel(),
                isolationPenalty: SentenceIsolationPenalty.CreateDefault());

            decoder.Decode("ab", 20);
            SentenceDecodePerformanceSample current = decoder.GetCurrentPerformanceSample();
            True(current.DecodeCalls == 1 && current.DecodeTotalMilliseconds >= 0.0,
                nameof(SentenceDecoderAggregatesCompositionPerformance) + ".current");
            True(current.IsolationCacheMisses == 1,
                nameof(SentenceDecoderAggregatesCompositionPerformance) + ".miss");

            decoder.CompleteComposition();
            SentenceDecodePerformanceSample last = decoder.GetLastPerformanceSample();
            True(last != null && last.DecodeCalls == 1,
                nameof(SentenceDecoderAggregatesCompositionPerformance) + ".last");
            True(decoder.GetCurrentPerformanceSample().DecodeCalls == 0,
                nameof(SentenceDecoderAggregatesCompositionPerformance) + ".reset");
        }

        private static void SentencePerformanceCompletionDoesNotWaitForDecode()
        {
            var model = new BlockingSentenceLanguageModel();
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" }
                }),
                model,
                isolationPenalty: SentenceIsolationPenalty.None);

            Task<SentenceDecodeResult> decode = Task.Run(() => decoder.Decode("ab", 20));
            True(model.Entered.WaitOne(3000),
                nameof(SentencePerformanceCompletionDoesNotWaitForDecode) + ".entered");
            var stopwatch = Stopwatch.StartNew();
            decoder.CompleteComposition();
            stopwatch.Stop();
            True(stopwatch.ElapsedMilliseconds < 60,
                nameof(SentencePerformanceCompletionDoesNotWaitForDecode) + ".latency");
            model.Release.Set();
            True(decode.Wait(3000),
                nameof(SentencePerformanceCompletionDoesNotWaitForDecode) + ".completed");
        }

        private sealed class CountingIsolationLanguageModel : ISentenceLanguageModel
        {
            public int ObservedBigramCalls { get; private set; }

            public double LogProbability(string previous2, string previous1, string target)
            {
                return 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                ObservedBigramCalls++;
                return false;
            }
        }

        private sealed class BlockingSentenceLanguageModel : ISentenceLanguageModel
        {
            public readonly ManualResetEvent Entered = new ManualResetEvent(false);
            public readonly ManualResetEvent Release = new ManualResetEvent(false);

            public double LogProbability(string previous2, string previous1, string target)
            {
                Entered.Set();
                Release.WaitOne(3000);
                return 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
            }
        }

        private sealed class SupplementPreferenceLanguageModel : ISentenceLanguageModel
        {
            public double LogProbability(string previous2, string previous1, string target)
            {
                return string.Equals(target, "茧", StringComparison.Ordinal) ||
                       string.Equals(target, "师", StringComparison.Ordinal)
                    ? -1.7
                    : 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
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

        private static void SentenceDecoderAllowsNonPrimaryCodesWhenHighFreqLimitIsZero()
        {
            var source = new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "的" },
                ["ac"] = new List<string> { "的", "丙" }
            };
            SentenceInputDecoder unrestricted = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(source),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);
            True(
                Array.Exists(unrestricted.Decode("ac").Candidates, candidate => candidate.Text == "的"),
                nameof(SentenceDecoderAllowsNonPrimaryCodesWhenHighFreqLimitIsZero) + ".unrestricted");

            SentenceInputDecoder restricted = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(source, SentenceCharacterRanks.TakeTop(1)),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);
            Equal("的", restricted.Decode("ab").Candidates[0].Text,
                nameof(SentenceDecoderAllowsNonPrimaryCodesWhenHighFreqLimitIsZero) + ".primary");
            True(
                Array.TrueForAll(restricted.Decode("ac").Candidates, candidate => candidate.Text != "的"),
                nameof(SentenceDecoderAllowsNonPrimaryCodesWhenHighFreqLimitIsZero) + ".filtered");
        }

        private static void SentenceCharacterRanksTakeTopSelectsFrequencyPrefix()
        {
            True(SentenceCharacterRanks.TakeTop(0).Count == 0,
                nameof(SentenceCharacterRanksTakeTopSelectsFrequencyPrefix) + ".zero");
            True(SentenceCharacterRanks.TakeTop(-3).Count == 0,
                nameof(SentenceCharacterRanksTakeTopSelectsFrequencyPrefix) + ".negative");
            ISet<string> top1 = SentenceCharacterRanks.TakeTop(1);
            True(top1.Count == 1 && top1.Contains("的"),
                nameof(SentenceCharacterRanksTakeTopSelectsFrequencyPrefix) + ".top1");
            ISet<string> top1500 = SentenceCharacterRanks.TakeTop(1500);
            True(top1500.Count == 1500 && top1500.Contains("的") && top1500.Contains("赤"),
                nameof(SentenceCharacterRanksTakeTopSelectsFrequencyPrefix) + ".top1500");
        }

        private static void SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled()
        {
            var state = new CoreRuntimeState();
            True(state.GetSentenceOptimalCodeHighFreqLimit() == 1500,
                nameof(SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled) + ".default");
            True(state.TrySetConfigValue("高频字仅使用最优码组句", "500", out _, out _),
                nameof(SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled) + ".set");
            True(state.GetSentenceOptimalCodeHighFreqLimit() == 500,
                nameof(SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled) + ".500");
            state.TrySetConfigValue("高频字仅使用最优码组句", "", out _, out _);
            True(state.GetSentenceOptimalCodeHighFreqLimit() == 0,
                nameof(SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled) + ".empty");
            state.TrySetConfigValue("高频字仅使用最优码组句", "0", out _, out _);
            True(state.GetSentenceOptimalCodeHighFreqLimit() == 0,
                nameof(SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled) + ".zero");
            state.TrySetConfigValue("高频字仅使用最优码组句", "-8", out _, out _);
            True(state.GetSentenceOptimalCodeHighFreqLimit() == 0,
                nameof(SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled) + ".negative");
            state.TrySetConfigValue("高频字仅使用最优码组句", "abc", out _, out _);
            True(state.GetSentenceOptimalCodeHighFreqLimit() == 0,
                nameof(SentenceOptimalCodeHighFreqLimitDefaultsTo1500AndTreatsEmptyAsDisabled) + ".invalid");
            state.TrySetConfigValue("高频字仅使用最优码组句", "1500", out _, out _);
        }

        private static void SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters()
        {
            ISet<string> parsed = CoreRuntimeState.ParseCharacterSet(
                CoreRuntimeState.DefaultSentenceFullCodeWhitelist);
            True(parsed.Count == 119,
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".count");
            True(parsed.Contains("便") && parsed.Contains("绕") && !parsed.Contains("撞"),
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".bounds");
            True(CoreRuntimeState.ParseCharacterSet(string.Empty).Count == 0,
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".empty");

            var state = new CoreRuntimeState();
            True(state.TrySetConfigValue("整句允许全码组句白名单", "便甲", out _, out _),
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".set");
            ISet<string> custom = state.GetSentenceFullCodeWhitelist();
            True(custom.Count == 2 && custom.Contains("便") && custom.Contains("甲"),
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".custom");
            True(state.TrySetConfigValue("整句允许全码组句白名单", "", out _, out _),
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".clear");
            Equal(string.Empty, state.GetSentenceFullCodeWhitelistText(),
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".empty_text");
            True(state.GetSentenceFullCodeWhitelist().Count == 0,
                nameof(SentenceFullCodeWhitelistDefaultsToFourRootSanJianCharacters) + ".cleared");
            state.TrySetConfigValue(
                "整句允许全码组句白名单",
                CoreRuntimeState.DefaultSentenceFullCodeWhitelist,
                out _,
                out _);
        }

        private static void SentenceDecoderAllowsWhitelistedFullCodesUnderHighFreqLimit()
        {
            var source = new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "的" },
                ["ac"] = new List<string> { "的", "丙" }
            };
            ISet<string> top1 = SentenceCharacterRanks.TakeTop(1);
            SentenceInputDecoder blocked = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(source, top1),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);
            True(
                Array.TrueForAll(blocked.Decode("ac").Candidates, candidate => candidate.Text != "的"),
                nameof(SentenceDecoderAllowsWhitelistedFullCodesUnderHighFreqLimit) + ".blocked");

            SentenceInputDecoder allowed = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(
                    source,
                    top1,
                    new HashSet<string>(StringComparer.Ordinal) { "的" }),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);
            True(
                Array.Exists(allowed.Decode("ac").Candidates, candidate => candidate.Text == "的"),
                nameof(SentenceDecoderAllowsWhitelistedFullCodesUnderHighFreqLimit) + ".whitelisted");

            SentenceInputDecoder duplicatesStillFiltered = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(source, top1),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100,
                allowDuplicateSingleCharacters: true);
            True(
                Array.TrueForAll(
                    duplicatesStillFiltered.Decode("ac").Candidates,
                    candidate => candidate.Text != "的"),
                nameof(SentenceDecoderAllowsWhitelistedFullCodesUnderHighFreqLimit) +
                    ".duplicate_option_does_not_bypass_optimal_code");
        }

        private static void SentenceDecoderAllowsLeadingShortSymbolOnly()
        {
            SentenceInputDecoder decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    [";a"] = new List<string> { "甲" },
                    ["ab"] = new List<string> { "乙" },
                    [";b"] = new List<string> { "丙" }
                }),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100,
                isolationPenalty: SentenceIsolationPenalty.None);

            SentenceDecodeResult leading = decoder.Decode(";aab");
            True(
                Array.Exists(leading.Candidates, candidate => candidate.Text == "甲乙"),
                nameof(SentenceDecoderAllowsLeadingShortSymbolOnly) + ".leading_allowed");

            SentenceDecodeResult embedded = decoder.Decode("ab;b");
            True(
                Array.TrueForAll(embedded.Candidates, candidate => candidate.Text != "乙丙"),
                nameof(SentenceDecoderAllowsLeadingShortSymbolOnly) + ".embedded_rejected");
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
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".whole_input_all_ranks");
            True(
                IndexOfCandidate(bare, "一") < IndexOfCandidate(bare, "一般"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".first_before_second");

            SentenceDecodeResult prefix = decoder.Decode("fiot");
            Equal("一是", prefix.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            Equal("fi ot", prefix.Candidates[0].SegmentedCode,
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.TrueForAll(prefix.Candidates, candidate => candidate.Text != "一般是"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".segmented_first_rank_only");

            SentenceDecodeResult suffix = decoder.Decode("otfi");
            Equal("是一", suffix.Candidates[0].Text, nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode));
            True(
                Array.TrueForAll(suffix.Candidates, candidate => candidate.Text != "是一般"),
                nameof(SentenceDecoderRequiresExplicitSelectionForEveryCode) + ".segmented_suffix_first_rank_only");

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

        private static void SentenceDecoderKeepsFirstChoiceAheadForWholeInputEdge()
        {
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲", "乙", "丙" }
                }),
                new PrefersLaterCharactersLanguageModel(),
                beamWidth: 20,
                isolationPenalty: SentenceIsolationPenalty.None);

            SentenceDecodeResult decoded = decoder.Decode("ab");
            Equal("甲", decoded.Candidates[0].Text, nameof(SentenceDecoderKeepsFirstChoiceAheadForWholeInputEdge));
            True(
                IndexOfCandidate(decoded, "甲") < IndexOfCandidate(decoded, "乙") &&
                IndexOfCandidate(decoded, "乙") < IndexOfCandidate(decoded, "丙"),
                nameof(SentenceDecoderKeepsFirstChoiceAheadForWholeInputEdge) + ".rank_order");
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
            EnableSentenceMode(state);
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

        private static void SentenceTabSelectionLocksOnContinuedInput()
        {
            foreach (bool autoCommit in new[] { false, true })
            foreach (bool synchronous in new[] { false, true })
            {
                var state = new CoreRuntimeState();
                EnableSentenceMode(state);
                state.TrySetConfigValue("整句自动提前上屏", autoCommit ? "是" : "否", out _, out _);
                using (var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                    new Dictionary<string, List<string>>
                    {
                        ["ab"] = new List<string> { "甲", "乙乙" },
                        ["cd"] = new List<string> { "丙", "戊戊" },
                        ["abcd"] = new List<string> { "丁" }
                    }), sentenceDecodeSynchronously: synchronous))
                {
                    void WaitForDecode() => True(SpinWait.SpinUntil(() => !engine.IsSentenceDecodePending, 3000),
                        "tab_lock.decode_completed");
                    TypeLetters(engine, "ab");
                    WaitForDecode();
                    Equal(null, Press(engine, 0x09).TextToOutput, "tab_lock.no_immediate_commit");
                    True(engine.GetUiSnapshot(5).SelectedCandidateIndex == 1, "tab_lock.highlight");
                    long oldGeneration = engine.GetDifferentialSnapshot(5).SentenceGeneration;
                    Equal(autoCommit ? "乙乙" : null, Press(engine, 0x43).TextToOutput, "tab_lock.next_key_commit");
                    True(!engine.ApplySentenceNeuralScores(oldGeneration, "ab", new[] { 0.0, -100.0 }),
                        "tab_lock.old_rerank_rejected");
                    Press(engine, 0x44);
                    WaitForDecode();
                    Equal(autoCommit ? "丙" : "乙乙丙", engine.GetUiSnapshot(5).Candidates[0], "tab_lock.fixed_boundary");
                    Equal(autoCommit ? "丙" : "乙乙丙", Press(engine, 0x20).TextToOutput, "tab_lock.no_duplicate_commit");

                    TypeLetters(engine, "ab"); Press(engine, 0x09); Press(engine, 0x43); Press(engine, 0x44);
                    Press(engine, 0x08); Press(engine, 0x08);
                    WaitForDecode();
                    if (autoCommit)
                        True(!engine.GetUiSnapshot(5).IsComposing, "tab_lock.committed_not_erasable");
                    else
                    {
                        Equal("甲", engine.GetUiSnapshot(5).Candidates[0], "tab_lock.backspace_unlock");
                        Equal("ab", engine.GetUiSnapshot(5).ActiveInputCode, "tab_lock.raw_restored");
                    }
                    Press(engine, 0x1b);
                    TypeLetters(engine, "ab"); Press(engine, 0x09); Press(engine, 0x43); Press(engine, 0x44);
                    Equal(autoCommit ? "cd" : "abcd", Press(engine, 0x0d).TextToOutput, "tab_lock.literal_exit");
                }
            }
        }

        private static void SentenceTabLocksStackAndPreserveSelectors()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
            var decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲", "乙乙" },
                ["cd"] = new List<string> { "丙" },
                ["ef"] = new List<string> { "丁" },
                ["cdef"] = new List<string> { "戊" },
                ["abcd"] = new List<string> { "己" }
            });
            using (var engine = new InputMethodEngine(state, decoder))
            {
                TypeLetters(engine, "ab");
                Press(engine, 0x09);
                TypeLetters(engine, "cd");
                Press(engine, 0x09); // Even the only candidate can be confirmed.
                TypeLetters(engine, "ef");
                Equal("乙乙丙丁", engine.GetUiSnapshot(5).Candidates[0], "tab_lock.second_boundary");
                Press(engine, 0x08); Press(engine, 0x08);
                Equal("乙乙丙", engine.GetUiSnapshot(5).Candidates[0], "tab_lock.previous_lock_retained");
                TypeLetters(engine, "ef");
                True(engine.GetUiSnapshot(5).Candidates.Contains("乙乙戊"), "tab_lock.second_boundary_released");
                Press(engine, 0x1b);
                TypeLetters(engine, "abcd");
                True(engine.GetUiSnapshot(5).Candidates.Contains("己"), "tab_lock.clear_resets_boundary");
                Press(engine, 0x1b);
                TypeLetters(engine, "ab");
                Press(engine, 0x09);
                Press(engine, 0x32);
                Equal("乙乙", engine.GetUiSnapshot(5).Candidates[0], "tab_lock.selector_edits_current_segment");
            }

            SentenceCandidate selected = decoder.Decode("ab").Candidates.Single(candidate => candidate.Text == "乙乙");
            var prefix = new SentenceLockedPrefix("ab", selected.Text, selected.Boundary);
            True(decoder.HasCompleteCandidate("abcd", groupEligibleOnly: true, lockedPrefix: prefix),
                "tab_lock.explicit_prefix_is_group_eligible");
            True(!decoder.HasCompleteCandidate("abcd", excludedText: "乙乙丙", groupEligibleOnly: true, lockedPrefix: prefix),
                "tab_lock.exact_query_excludes_crossing_path");
            True(!decoder.HasCompleteCandidate("ab", excludedText: "乙乙", lockedPrefix: prefix),
                "tab_lock.exact_query_excludes_fixed_text");
            True(decoder.Decode("abcd", lockedPrefix: prefix).Candidates.All(candidate => candidate.Text == "乙乙丙"),
                "tab_lock.decoder_fixed_text_and_boundary");
        }

        private static void SentenceEmptyCodeCountsDuplicateSingles()
        {
            foreach (bool duplicates in new[] { false, true })
            foreach (int beam in new[] { 1, 100 })
            {
                var state = new CoreRuntimeState();
                EnableSentenceEarlyCommit(state);
                state.TrySetConfigValue("高频字仅使用最优码组句", "0", out _, out _);
                state.TrySetConfigValue("允许单字重码组句", duplicates ? "是" : "否", out _, out _);
                var decoder = new SentenceInputDecoder(SentenceLexiconIndex.Build(
                    new Dictionary<string, List<string>>
                    {
                        ["ot"] = new List<string> { "是", "题", "多字词" },
                        ["qm"] = new List<string> { "目" }
                    }), new PrefersCharactersLanguageModel("题", "目"), beamWidth: beam,
                    allowDuplicateSingleCharacters: duplicates);
                True(decoder.HasCompleteCandidate("ot", excludedText: "是", groupEligibleOnly: true) == duplicates,
                    "empty_code.duplicate_exact_ambiguity");
                using (var engine = new InputMethodEngine(state, decoder))
                {
                    TypeLetters(engine, "ot");
                    Equal(duplicates ? null : "是", Press(engine, 0x51).TextToOutput,
                        "empty_code.otq_does_not_discard_ti");
                    Press(engine, 0x4d);
                    if (duplicates && beam == 100)
                    {
                        Equal("题目", engine.GetUiSnapshot(5).Candidates[0], "empty_code.otqm");
                    }
                }
            }
        }

        private static void SentenceEngineUsesTabToTraverseCandidates()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
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
            EnableSentenceMode(disabledState);
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
            EnableSentenceMode(state);
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

        private static void SentenceNeuralWeightPreservesShortNgramWinner()
        {
            double shortWeight = InputMethodEngine.GetSentenceNeuralWeight(2);
            double expectedScore = InputMethodEngine.CombineSentenceNeuralScore(
                -10.0606,
                -51.20660248470333,
                shortWeight);
            double competingScore = InputMethodEngine.CombineSentenceNeuralScore(
                -23.2096,
                -32.833989807356645,
                shortWeight);

            True(
                expectedScore > competingScore,
                nameof(SentenceNeuralWeightPreservesShortNgramWinner));
            True(
                Math.Abs(shortWeight - 0.30) < 1e-9 &&
                Math.Abs(InputMethodEngine.GetSentenceNeuralWeight(3) - 0.84) < 1e-9,
                nameof(SentenceNeuralWeightPreservesShortNgramWinner) + ".dynamic_weight");
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

        private static void SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["gyy"] = new List<string> { "羊" },
                ["gch"] = new List<string> { "赤", "羔" }
            };
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
            True(state.TrySetConfigValue("允许单字重码组句", "是", out _, out _),
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".setting");
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 20,
                isolationPenalty: SentenceIsolationPenalty.None,
                allowDuplicateSingleCharacters: true);
            var engine = new InputMethodEngine(state, decoder);
            var reranker = new RecordingSentenceRerankService();
            engine.SetSentenceRerankService(reranker);

            TypeLetters(engine, "gyygch");
            True(reranker.LastRequest != null && reranker.LastRequest.Candidates.Length == 2,
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".segmented_request");
            int lambIndex = Array.IndexOf(reranker.LastRequest.Candidates, "羊羔");
            int redIndex = Array.IndexOf(reranker.LastRequest.Candidates, "羊赤");
            True(lambIndex >= 0 && redIndex >= 0,
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".segmented_candidates");
            var scores = new double[reranker.LastRequest.Candidates.Length];
            scores[lambIndex] = 100.0;
            scores[redIndex] = -100.0;
            True(engine.ApplySentenceNeuralScores(
                    reranker.LastRequest.Generation,
                    "gyygch",
                    scores),
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".segmented_apply");
            Equal("羊羔", engine.GetUiSnapshot(5).Candidates[0],
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".segmented_score_wins");

            var wholeEngine = new InputMethodEngine(state, decoder);
            var wholeReranker = new RecordingSentenceRerankService();
            wholeEngine.SetSentenceRerankService(wholeReranker);
            TypeLetters(wholeEngine, "gch");
            True(wholeReranker.LastRequest != null && wholeReranker.LastRequest.Candidates.Length == 2,
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".whole_request");
            int goatIndex = Array.IndexOf(wholeReranker.LastRequest.Candidates, "羔");
            int firstIndex = Array.IndexOf(wholeReranker.LastRequest.Candidates, "赤");
            var wholeScores = new double[wholeReranker.LastRequest.Candidates.Length];
            wholeScores[goatIndex] = 100.0;
            wholeScores[firstIndex] = -100.0;
            True(wholeEngine.ApplySentenceNeuralScores(
                    wholeReranker.LastRequest.Generation,
                    "gch",
                    wholeScores),
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".whole_apply");
            Equal("赤", wholeEngine.GetUiSnapshot(5).Candidates[0],
                nameof(SentenceNeuralRerankLetsSegmentedSingleDuplicatesCompete) + ".whole_rank_stays_first");
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
            EnableSentenceMode(state);
            return new InputMethodEngine(state, CreateSentenceDecoder(lexicon));
        }

        private static void SentenceDecoderAppliesCharacterRewardInsideBeam()
        {
            SentenceLexiconIndex lexicon = SentenceLexiconIndex.Build(
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" },
                    ["abcd"] = new List<string> { "丙" },
                    ["ef"] = new List<string> { "丁" }
                });
            var baseline = new SentenceInputDecoder(
                lexicon,
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 1);
            var rewarded = new SentenceInputDecoder(
                lexicon,
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 1,
                emittedCharacterReward: 2.0);

            Equal(
                "丙丁",
                baseline.Decode("abcdef").Candidates[0].Text,
                nameof(SentenceDecoderAppliesCharacterRewardInsideBeam) + ".baseline");
            Equal(
                "甲乙丁",
                rewarded.Decode("abcdef").Candidates[0].Text,
                nameof(SentenceDecoderAppliesCharacterRewardInsideBeam) + ".rewarded");
        }

        private static void SentenceDecoderRewardsOptimalWholeInputSingleCharacter()
        {
            SentenceLexiconIndex lexicon = SentenceLexiconIndex.Build(
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" },
                    ["abcd"] = new List<string> { "词语", "丙" },
                    ["x"] = new List<string> { "丁" },
                    ["ef"] = new List<string> { "戊" },
                    ["gh"] = new List<string> { "己" },
                    ["efgh"] = new List<string> { "丁" }
                });
            var baseline = new SentenceInputDecoder(
                lexicon,
                NeutralSentenceLanguageModel.Instance,
                emittedCharacterReward: 2.0,
                isolationPenalty: SentenceIsolationPenalty.None,
                allowDuplicateSingleCharacters: true);
            var rewarded = new SentenceInputDecoder(
                lexicon,
                NeutralSentenceLanguageModel.Instance,
                emittedCharacterReward: 2.0,
                wholeInputSingleCharacterReward: 5.0,
                isolationPenalty: SentenceIsolationPenalty.None,
                allowDuplicateSingleCharacters: true);

            SentenceDecodeResult baselineResult = baseline.DecodeFull("abcd");
            SentenceDecodeResult rewardedResult = rewarded.DecodeFull("abcd");
            SentenceCandidate baselineSingle = baselineResult.Candidates.Single(candidate => candidate.Text == "丙");
            SentenceCandidate rewardedSingle = rewardedResult.Candidates.Single(candidate => candidate.Text == "丙");
            True(baselineResult.Candidates[0].Text != "丙",
                nameof(SentenceDecoderRewardsOptimalWholeInputSingleCharacter) + ".baseline_loses");
            Equal("丙", rewardedResult.Candidates[0].Text,
                nameof(SentenceDecoderRewardsOptimalWholeInputSingleCharacter) + ".later_rank_rewarded");
            True(Math.Abs((rewardedSingle.FinalScore - baselineSingle.FinalScore) - 5.0) < 1e-9,
                nameof(SentenceDecoderRewardsOptimalWholeInputSingleCharacter) + ".reward_value");
            True(Math.Abs(rewardedSingle.ConfidenceScore - baselineSingle.ConfidenceScore) < 1e-9,
                nameof(SentenceDecoderRewardsOptimalWholeInputSingleCharacter) + ".confidence_unchanged");

            SentenceDecodeResult nonOptimal = rewarded.DecodeFull("efgh");
            Equal("戊己", nonOptimal.Candidates[0].Text,
                nameof(SentenceDecoderRewardsOptimalWholeInputSingleCharacter) + ".non_optimal_not_rewarded");

            rewarded.Decode("abcd");
            AssertSentenceResultsEqual(
                rewarded.Decode("abcdef"),
                rewarded.DecodeFull("abcdef"),
                nameof(SentenceDecoderRewardsOptimalWholeInputSingleCharacter) + ".incremental_append");
        }

        private static void SentenceSupplementParsesPerSchemaFile()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "tigerclaw-supplement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "补充语料.txt");
                File.WriteAllText(
                    path,
                    "# comment\r\n茧师\t2000\r\n流行词\r\n单字   500\r\n坏权重 nope\r\n零 0\r\n茧师 3000\r\n",
                    new UnicodeEncoding(false, true));

                SentenceSupplementEntry[] entries = CoreRuntimeState.LoadSentenceSupplements(directory);
                True(entries.Length == 3, nameof(SentenceSupplementParsesPerSchemaFile) + ".count");
                SentenceSupplementEntry cocoon = entries.Single(entry => entry.Text == "茧师");
                True(cocoon.Weight == 3000, nameof(SentenceSupplementParsesPerSchemaFile) + ".last_wins");
                True(entries.Single(entry => entry.Text == "流行词").Weight == 1000,
                    nameof(SentenceSupplementParsesPerSchemaFile) + ".default_weight");
                True(entries.Single(entry => entry.Text == "单字").Weight == 500,
                    nameof(SentenceSupplementParsesPerSchemaFile) + ".spaces");
                True(CoreRuntimeState.IsSentenceSupplementFile(path),
                    nameof(SentenceSupplementParsesPerSchemaFile) + ".skip_exact");
                True(CoreRuntimeState.IsSentenceSupplementFile(Path.Combine(directory, "补充语料.TXT")),
                    nameof(SentenceSupplementParsesPerSchemaFile) + ".skip_case");
                True(!CoreRuntimeState.IsSentenceSupplementFile(Path.Combine(directory, "普通码表.txt")),
                    nameof(SentenceSupplementParsesPerSchemaFile) + ".keep_lexicon");

                File.WriteAllText(path, "无签名 1000\n", new UTF8Encoding(false));
                entries = CoreRuntimeState.LoadSentenceSupplements(directory);
                Equal("无签名", entries.Single().Text,
                    nameof(SentenceSupplementParsesPerSchemaFile) + ".utf8_no_bom");

                File.WriteAllText(path, "大端 1000\n", new UnicodeEncoding(true, true));
                entries = CoreRuntimeState.LoadSentenceSupplements(directory);
                Equal("大端", entries.Single().Text,
                    nameof(SentenceSupplementParsesPerSchemaFile) + ".utf16_be");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void SentenceSupplementMatchesOverlapsAndRepeatedSingleCharacters()
        {
            SentenceSupplementMatcher matcher = SentenceSupplementMatcher.Build(new[]
            {
                SentenceSupplementEntry.Create("师", 1000),
                SentenceSupplementEntry.Create("茧师", 2000)
            });

            int state = matcher.Advance(0, "茧", out double first);
            state = matcher.Advance(state, "师", out double overlap);
            state = matcher.Advance(state, "师", out double repeatedSingle);
            True(first == 0.0,
                nameof(SentenceSupplementMatchesOverlapsAndRepeatedSingleCharacters) + ".prefix");
            True(overlap > 10.3 && overlap < 10.4,
                nameof(SentenceSupplementMatchesOverlapsAndRepeatedSingleCharacters) + ".max_overlap");
            True(repeatedSingle == 9.0,
                nameof(SentenceSupplementMatchesOverlapsAndRepeatedSingleCharacters) + ".single_repeat");
        }

        private static void SentenceSupplementRewardsInsideBeamWithoutChangingConfidence()
        {
            SentenceInputDecoder baseline = CreateSupplementDecoder(SentenceSupplementMatcher.Empty);
            SentenceInputDecoder rewarded = CreateSupplementDecoder(SentenceSupplementMatcher.Build(new[]
            {
                SentenceSupplementEntry.Create("茧师", 1000)
            }));

            SentenceDecodeResult before = baseline.Decode("abcdef", 20);
            SentenceDecodeResult after = rewarded.Decode("abcdef", 20);
            Equal("齿烧", before.Candidates[0].Text,
                nameof(SentenceSupplementRewardsInsideBeamWithoutChangingConfidence) + ".baseline");
            Equal("茧师", after.Candidates[0].Text,
                nameof(SentenceSupplementRewardsInsideBeamWithoutChangingConfidence) + ".rewarded");

            SentenceCandidate beforeTarget = before.Candidates.Single(candidate => candidate.Text == "茧师");
            SentenceCandidate afterTarget = after.Candidates.Single(candidate => candidate.Text == "茧师");
            True(Math.Abs(afterTarget.SupplementScore - 9.0) < 1e-9,
                nameof(SentenceSupplementRewardsInsideBeamWithoutChangingConfidence) + ".reward");
            True(Math.Abs(afterTarget.ConfidenceScore - beforeTarget.ConfidenceScore) < 1e-12,
                nameof(SentenceSupplementRewardsInsideBeamWithoutChangingConfidence) + ".confidence");
            True(Math.Abs((afterTarget.BaseScore - beforeTarget.BaseScore) - 9.0) < 1e-9,
                nameof(SentenceSupplementRewardsInsideBeamWithoutChangingConfidence) + ".base_score");
        }

        private static void SentenceSupplementIncrementalMatchesFullRebuild()
        {
            SentenceSupplementMatcher matcher = SentenceSupplementMatcher.Build(new[]
            {
                SentenceSupplementEntry.Create("茧师", 1000),
                SentenceSupplementEntry.Create("师", 500)
            });
            SentenceInputDecoder decoder = CreateSupplementDecoder(matcher);
            decoder.Decode("abcde", 20);
            AssertSentenceResultsEqual(
                decoder.Decode("abcdef", 20),
                decoder.DecodeFull("abcdef", 20),
                nameof(SentenceSupplementIncrementalMatchesFullRebuild) + ".append");
            decoder.Decode("abcd", 20);
            AssertSentenceResultsEqual(
                decoder.Decode("abcdef", 20),
                decoder.DecodeFull("abcdef", 20),
                nameof(SentenceSupplementIncrementalMatchesFullRebuild) + ".backspace_append");
        }

        private static void SentenceSupplementSurvivesNeuralRerank()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
            SentenceSupplementMatcher matcher = SentenceSupplementMatcher.Build(new[]
            {
                SentenceSupplementEntry.Create("茧师", 1000)
            });
            var engine = new InputMethodEngine(state, CreateSupplementDecoder(matcher));
            var reranker = new RecordingSentenceRerankService();
            engine.SetSentenceRerankService(reranker);
            TypeLetters(engine, "abcdef");

            True(reranker.LastRequest != null && reranker.LastRequest.Candidates.Length == 2,
                nameof(SentenceSupplementSurvivesNeuralRerank) + ".request");
            var scores = new double[reranker.LastRequest.Candidates.Length];
            for (int index = 0; index < scores.Length; index++)
            {
                scores[index] = reranker.LastRequest.Candidates[index] == "茧师"
                    ? -37.90426359899996
                    : -35.9985714209704;
            }
            True(engine.ApplySentenceNeuralScores(
                    reranker.LastRequest.Generation,
                    "abcdef",
                    scores),
                nameof(SentenceSupplementSurvivesNeuralRerank) + ".accepted");
            Equal("茧师", engine.GetUiSnapshot(5).Candidates[0],
                nameof(SentenceSupplementSurvivesNeuralRerank) + ".top");
        }

        private static SentenceInputDecoder CreateSupplementDecoder(SentenceSupplementMatcher matcher)
        {
            return new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "齿" },
                    ["cdef"] = new List<string> { "烧" },
                    ["abc"] = new List<string> { "茧" },
                    ["def"] = new List<string> { "师" }
                }),
                new SupplementPreferenceLanguageModel(),
                beamWidth: 100,
                isolationPenalty: SentenceIsolationPenalty.None,
                supplementMatcher: matcher);
        }

        private static void SentenceDecoderReportsTruncatedConfidenceMass()
        {
            var lexicon = new Dictionary<string, List<string>>();
            const string raw = "abcdef";
            int text = 0x4E00;
            for (int start = 0; start < raw.Length - 1; start++)
            {
                for (int length = 2; start + length <= raw.Length; length++)
                {
                    lexicon[raw.Substring(start, length)] =
                        new List<string> { char.ConvertFromUtf32(text++) };
                }
            }

            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 1);
            SentenceDecodeResult result = decoder.Decode(raw, 20, includeEarlyCommitEvidence: true);
            True(result.EarlyCommitEvidence.ConfidenceTruncated,
                nameof(SentenceDecoderReportsTruncatedConfidenceMass));
        }

        private static void SentenceDecoderMergesIncompleteTailIntoPrefixEvidence()
        {
            var languageModel = new PrefersIncompleteTailSentenceLanguageModel();
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲乙" },
                    ["abc"] = new List<string> { "丁戊" },
                    ["cd"] = new List<string> { "丙" }
                }),
                languageModel,
                beamWidth: 100);

            SentenceDecodeResult result = decoder.Decode("abc", 20, includeEarlyCommitEvidence: true);
            AssertSentenceResultsEqual(
                result,
                decoder.DecodeFull("abc", 20, includeEarlyCommitEvidence: true),
                nameof(SentenceDecoderMergesIncompleteTailIntoPrefixEvidence) + ".incremental");
            True(result.Candidates.Length == 1 && result.Candidates[0].Text == "丁戊",
                nameof(SentenceDecoderMergesIncompleteTailIntoPrefixEvidence) + ".visible");
            True(!result.EarlyCommitEvidence.IgnoreNeuralConstraint,
                nameof(SentenceDecoderMergesIncompleteTailIntoPrefixEvidence) + ".neural");
            True(!result.EarlyCommitEvidence.NeutralIncompleteTail,
                nameof(SentenceDecoderMergesIncompleteTailIntoPrefixEvidence) + ".complete");
            True(result.EarlyCommitEvidence.MergedIncompleteTail,
                nameof(SentenceDecoderMergesIncompleteTailIntoPrefixEvidence) + ".merged");
            True(result.EarlyCommitEvidence.Prefixes.Any(prefix => prefix.Text == "甲乙"),
                nameof(SentenceDecoderMergesIncompleteTailIntoPrefixEvidence) + ".dropped_tail");
            True(result.EarlyCommitEvidence.Prefixes.Any(prefix => prefix.Text == "丁戊"),
                nameof(SentenceDecoderMergesIncompleteTailIntoPrefixEvidence) + ".visible_prefix");
        }

        private static void SentenceDecoderKeepsDroppedTailRawEndpointsDistinct()
        {
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲乙" },
                    ["abc"] = new List<string> { "甲乙" },
                    ["cd"] = new List<string> { "丙" }
                }),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);

            SentenceDecodeResult result = decoder.DecodeFull(
                "abc",
                20,
                includeEarlyCommitEvidence: true);
            SentencePrefixEvidence[] matching = result.EarlyCommitEvidence.Prefixes
                .Where(prefix => prefix.Text == "甲乙")
                .ToArray();
            True(result.EarlyCommitEvidence.MergedIncompleteTail,
                nameof(SentenceDecoderKeepsDroppedTailRawEndpointsDistinct) + ".merged");
            True(matching.Any(prefix => prefix.RawLength == 2),
                nameof(SentenceDecoderKeepsDroppedTailRawEndpointsDistinct) + ".dropped_endpoint");
            True(matching.Any(prefix => prefix.RawLength == 3),
                nameof(SentenceDecoderKeepsDroppedTailRawEndpointsDistinct) + ".visible_endpoint");
        }

        private static void SentenceDecoderConditionsEvidenceOnCommittedPrefix()
        {
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲乙", "丁戊" },
                    ["xy"] = new List<string> { "丙" }
                }),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);

            SentenceDecodeResult unconditioned = decoder.Decode(
                "ab",
                20,
                includeEarlyCommitEvidence: true);
            Equal(string.Empty, unconditioned.EarlyCommitEvidence.Proposal,
                nameof(SentenceDecoderConditionsEvidenceOnCommittedPrefix) + ".unconditioned");

            SentenceDecodeResult conditioned = decoder.Decode(
                "ab",
                20,
                includeEarlyCommitEvidence: true,
                requiredTextPrefix: "甲");
            AssertSentenceResultsEqual(
                conditioned,
                decoder.DecodeFull(
                    "ab",
                    20,
                    includeEarlyCommitEvidence: true,
                    requiredTextPrefix: "甲"),
                nameof(SentenceDecoderConditionsEvidenceOnCommittedPrefix) + ".incremental");
            Equal("甲乙", conditioned.EarlyCommitEvidence.Proposal,
                nameof(SentenceDecoderConditionsEvidenceOnCommittedPrefix) + ".conditioned");
            SentenceDecodeResult matchingTail = decoder.DecodeFull(
                "abx",
                20,
                includeEarlyCommitEvidence: true,
                requiredTextPrefix: "甲");
            SentenceDecodeResult mismatchedTail = decoder.DecodeFull(
                "abx",
                20,
                includeEarlyCommitEvidence: true,
                requiredTextPrefix: "丁");
            True(matchingTail.EarlyCommitEvidence.NeutralIncompleteTail,
                nameof(SentenceDecoderConditionsEvidenceOnCommittedPrefix) + ".matching_tail");
            True(!mismatchedTail.EarlyCommitEvidence.NeutralIncompleteTail,
                nameof(SentenceDecoderConditionsEvidenceOnCommittedPrefix) + ".mismatched_tail");
        }

        private static void SentenceDecoderMarksIncompleteTailAsNeutralOnly()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["xy"] = new List<string> { "丙" }
            });

            SentenceDecodeResult result = decoder.Decode(
                "abcdx",
                20,
                includeEarlyCommitEvidence: true);
            True(result.Candidates.Length == 0,
                nameof(SentenceDecoderMarksIncompleteTailAsNeutralOnly) + ".visible");
            True(result.EarlyCommitEvidence.NeutralIncompleteTail,
                nameof(SentenceDecoderMarksIncompleteTailAsNeutralOnly) + ".neutral");
            True(result.EarlyCommitEvidence.MergedIncompleteTail,
                nameof(SentenceDecoderMarksIncompleteTailAsNeutralOnly) + ".merged");
            True(result.EarlyCommitEvidence.Prefixes.Any(prefix => prefix.Text == "甲乙"),
                nameof(SentenceDecoderMarksIncompleteTailAsNeutralOnly) + ".dropped_tail");
        }

        private static void SentenceDecoderAllowsImplicitNonFirstOnlyForWholeInputEdge()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["rl"] = new List<string> { "了", "对方" },
                ["rlrl"] = new List<string> { "特例", "整边非首选" }
            });

            SentenceDecodeResult single = decoder.Decode("rl");
            True(Array.Exists(single.Candidates, candidate => candidate.Text == "对方"),
                nameof(SentenceDecoderAllowsImplicitNonFirstOnlyForWholeInputEdge) + ".single_edge");

            SentenceDecodeResult segmented = decoder.Decode("rlrl");
            True(Array.Exists(segmented.Candidates, candidate => candidate.Text == "整边非首选"),
                nameof(SentenceDecoderAllowsImplicitNonFirstOnlyForWholeInputEdge) + ".whole_edge_non_first");
            True(Array.TrueForAll(segmented.Candidates, candidate => candidate.Text != "对方对方"),
                nameof(SentenceDecoderAllowsImplicitNonFirstOnlyForWholeInputEdge) + ".segmented_non_first_rejected");
        }

        private static void SentenceAllowDuplicateSingleCharactersDefaultsOn()
        {
            var state = new CoreRuntimeState();
            True(state.GetSentenceAllowDuplicateSingleCharacters(),
                nameof(SentenceAllowDuplicateSingleCharactersDefaultsOn) + ".default");
            True(state.TrySetConfigValue("允许单字重码组句", "否", out _, out _),
                nameof(SentenceAllowDuplicateSingleCharactersDefaultsOn) + ".off");
            True(!state.GetSentenceAllowDuplicateSingleCharacters(),
                nameof(SentenceAllowDuplicateSingleCharactersDefaultsOn) + ".off_value");
            True(state.TrySetConfigValue("允许单字重码组句", "是", out _, out _),
                nameof(SentenceAllowDuplicateSingleCharactersDefaultsOn) + ".on");
            True(state.GetSentenceAllowDuplicateSingleCharacters(),
                nameof(SentenceAllowDuplicateSingleCharactersDefaultsOn) + ".on_value");
        }

        private static void SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲", "乙" },
                ["ot"] = new List<string> { "是" },
                ["fi"] = new List<string> { "一", "一般" },
                ["rl"] = new List<string> { "了", "对方" }
            };
            SentenceInputDecoder off = CreateSentenceDecoder(lexicon);
            SentenceInputDecoder on = CreateSentenceDecoder(lexicon, allowDuplicateSingleCharacters: true);

            True(
                Array.TrueForAll(off.Decode("abot").Candidates, candidate => candidate.Text != "乙是"),
                nameof(SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled) + ".off_segmented");
            True(
                Array.Exists(on.Decode("abot").Candidates, candidate => candidate.Text == "乙是"),
                nameof(SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled) + ".on_segmented_single");
            True(
                Array.TrueForAll(on.Decode("fiot").Candidates, candidate => candidate.Text != "一般是"),
                nameof(SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled) + ".on_segmented_word_rejected");
            Equal("乙", on.Decode("ab2").Candidates[0].Text,
                nameof(SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled) + ".explicit_digit");
            Equal("乙", on.Decode("ab;").Candidates[0].Text,
                nameof(SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled) + ".explicit_semicolon");
            True(
                Array.TrueForAll(on.Decode("rlot").Candidates, candidate => candidate.Text != "对方是"),
                nameof(SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled) + ".on_segmented_word_still_needs_selector");
            Equal("对方是", on.Decode("rl2ot").Candidates[0].Text,
                nameof(SentenceDecoderAllowsSegmentedSingleDuplicatesWhenEnabled) + ".word_with_selector");
        }

        private static void SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["gyy"] = new List<string> { "羊" },
                ["gch"] = new List<string> { "赤", "羔" }
            };
            var prefersLamb = new PrefersCharactersLanguageModel("羔");
            var off = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                prefersLamb,
                beamWidth: 20,
                isolationPenalty: SentenceIsolationPenalty.None);
            var on = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                prefersLamb,
                beamWidth: 20,
                isolationPenalty: SentenceIsolationPenalty.None,
                allowDuplicateSingleCharacters: true);

            SentenceDecodeResult offDecoded = off.Decode("gyygch");
            Equal("羊赤", offDecoded.Candidates[0].Text,
                nameof(SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled) + ".off_keeps_first_rank");
            True(
                Array.TrueForAll(offDecoded.Candidates, candidate => candidate.Text != "羊羔"),
                nameof(SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled) + ".off_hides_non_first");

            SentenceDecodeResult onDecoded = on.Decode("gyygch");
            Equal("羊羔", onDecoded.Candidates[0].Text,
                nameof(SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled) + ".on_score_can_win");
            True(
                IndexOfCandidate(onDecoded, "羊羔") < IndexOfCandidate(onDecoded, "羊赤"),
                nameof(SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled) + ".on_competes");

            SentenceDecodeResult wholeInput = on.Decode("gch");
            Equal("赤", wholeInput.Candidates[0].Text,
                nameof(SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled) + ".whole_input_keeps_first_rank");
            True(
                IndexOfCandidate(wholeInput, "赤") < IndexOfCandidate(wholeInput, "羔"),
                nameof(SentenceDecoderLetsSegmentedSingleDuplicatesCompeteWhenEnabled) + ".whole_input_rank_order");
        }

        private sealed class PrefersCharactersLanguageModel : ISentenceLanguageModel
        {
            private readonly HashSet<string> _preferred;

            public PrefersCharactersLanguageModel(params string[] preferred)
            {
                _preferred = new HashSet<string>(preferred ?? Array.Empty<string>(), StringComparer.Ordinal);
            }

            public double LogProbability(string previous2, string previous1, string target)
            {
                return _preferred.Contains(target) ? 20.0 : 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
            }
        }

        private static void SentenceDecoderSelectsExactVisibleTopK()
        {
            var candidates = new List<string>();
            for (int index = 0; index < 30; index++)
            {
                candidates.Add(char.ConvertFromUtf32(0x4E00 + index));
            }
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = candidates
                }),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100);

            SentenceDecodeResult result = decoder.Decode("ab", 20);
            True(result.Candidates.Length == 20,
                nameof(SentenceDecoderSelectsExactVisibleTopK) + ".count");
            for (int index = 0; index < result.Candidates.Length; index++)
            {
                Equal(candidates[index], result.Candidates[index].Text,
                    nameof(SentenceDecoderSelectsExactVisibleTopK) + ".rank." + index);
            }
        }

        private static InputMethodEngine CreateAutoCommitSentenceEngine()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["abc"] = new List<string> { "甲乙" },
                ["de"] = new List<string> { "丙" },
                ["def"] = new List<string> { "丁" },
                ["defg"] = new List<string> { "戊" }
            }));
            engine.SentenceEmptyCodeAutoCommitOverride = false;
            return engine;
        }

        private static void SentenceAutoCommitRequiresConsecutiveAppendEvidence()
        {
            InputMethodEngine engine = CreateAutoCommitSentenceEngine();
            TypeLetters(engine, "abcde");
            Press(engine, 0x08);
            KeyEngineResult result = Press(engine, 0x45);
            Equal(null, result.TextToOutput,
                nameof(SentenceAutoCommitRequiresConsecutiveAppendEvidence));
        }

        private static void SentenceAutoCommitSuspendsAfterManualNavigation()
        {
            InputMethodEngine engine = CreateAutoCommitSentenceEngine();
            TypeLetters(engine, "abcde");
            Press(engine, 0x28);
            KeyEngineResult result = Press(engine, 0x46);
            Equal(null, result.TextToOutput,
                nameof(SentenceAutoCommitSuspendsAfterManualNavigation));
        }

        private static void SentenceAutoCommitRetainsAtLeastThreeRawCodes()
        {
            InputMethodEngine engine = CreateAutoCommitSentenceEngine();
            TypeLetters(engine, "abcde");
            KeyEngineResult result = Press(engine, 0x46);
            Equal("甲乙", result.TextToOutput,
                nameof(SentenceAutoCommitRetainsAtLeastThreeRawCodes));
            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            True(snapshot.ActiveInputCode.Replace(" ", string.Empty).Length >= 3,
                nameof(SentenceAutoCommitRetainsAtLeastThreeRawCodes));
        }

        private static void SentenceProbabilisticAutoCommitRetainsDuplicateSingleContinuation()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            True(state.TrySetConfigValue("允许单字重码组句", "是", out _, out _),
                nameof(SentenceProbabilisticAutoCommitRetainsDuplicateSingleContinuation) + ".setting");
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["aw"] = new List<string> { "买" },
                    ["me"] = new List<string> { "窦", "椟" },
                    ["namc"] = new List<string> { "还" },
                    ["unta"] = new List<string> { "珠" }
                }),
                new PrefersCharactersLanguageModel("椟"),
                beamWidth: 100,
                isolationPenalty: SentenceIsolationPenalty.None,
                allowDuplicateSingleCharacters: true);
            var engine = new InputMethodEngine(state, decoder);
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "awmen");
            Equal("买", Press(engine, 0x41).TextToOutput,
                nameof(SentenceProbabilisticAutoCommitRetainsDuplicateSingleContinuation) + ".commit");

            // Freeze further early commits so the retained suffix can be
            // inspected after another decode generation.
            True(state.TrySetConfigValue("整句自动提前上屏", "否", out _, out _),
                nameof(SentenceProbabilisticAutoCommitRetainsDuplicateSingleContinuation) + ".disable");
            TypeLetters(engine, "mcunta");
            EngineUiSnapshot continuation = engine.GetUiSnapshot(5);
            Equal("椟还珠", continuation.Candidates[0],
                nameof(SentenceProbabilisticAutoCommitRetainsDuplicateSingleContinuation) + ".top");
            True(Array.Exists(continuation.Candidates, candidate => candidate == "椟还珠"),
                nameof(SentenceProbabilisticAutoCommitRetainsDuplicateSingleContinuation) + ".retained");
        }

        private static void SentenceMinRetainedRawLengthDefaultsToZeroAndClamps()
        {
            var state = new CoreRuntimeState();
            True(state.GetSentenceMinRetainedRawLength() == 0,
                nameof(SentenceMinRetainedRawLengthDefaultsToZeroAndClamps) + ".default");
            True(state.TrySetConfigValue("保留最少编码数量", "5", out _, out _),
                nameof(SentenceMinRetainedRawLengthDefaultsToZeroAndClamps) + ".set");
            True(state.GetSentenceMinRetainedRawLength() == 5,
                nameof(SentenceMinRetainedRawLengthDefaultsToZeroAndClamps) + ".five");
            state.TrySetConfigValue("保留最少编码数量", "-2", out _, out _);
            True(state.GetSentenceMinRetainedRawLength() == 0,
                nameof(SentenceMinRetainedRawLengthDefaultsToZeroAndClamps) + ".negative");
            state.TrySetConfigValue("保留最少编码数量", "99", out _, out _);
            True(state.GetSentenceMinRetainedRawLength() == 32,
                nameof(SentenceMinRetainedRawLengthDefaultsToZeroAndClamps) + ".clamp");
            state.TrySetConfigValue("保留最少编码数量", "abc", out _, out _);
            True(state.GetSentenceMinRetainedRawLength() == 0,
                nameof(SentenceMinRetainedRawLengthDefaultsToZeroAndClamps) + ".invalid");
        }

        private static void SentenceAutoCommitHonorsConfiguredMinRetainedRawLength()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            True(state.TrySetConfigValue("保留最少编码数量", "5", out _, out _),
                nameof(SentenceAutoCommitHonorsConfiguredMinRetainedRawLength) + ".set");
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["abc"] = new List<string> { "甲乙" },
                    ["de"] = new List<string> { "丙" },
                    ["def"] = new List<string> { "丁" },
                    ["defg"] = new List<string> { "戊" },
                    ["hi"] = new List<string> { "己" }
                }));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcde");
            Equal(null, Press(engine, 0x46).TextToOutput,
                nameof(SentenceAutoCommitHonorsConfiguredMinRetainedRawLength) + ".three_not_enough");
            Equal(null, Press(engine, 0x47).TextToOutput,
                nameof(SentenceAutoCommitHonorsConfiguredMinRetainedRawLength) + ".four_not_enough");
            Equal("甲乙", Press(engine, 0x48).TextToOutput,
                nameof(SentenceAutoCommitHonorsConfiguredMinRetainedRawLength) + ".five_commits");
            True(engine.GetUiSnapshot(5).ActiveInputCode.Replace(" ", string.Empty).Length >= 5,
                nameof(SentenceAutoCommitHonorsConfiguredMinRetainedRawLength) + ".retained");
        }

        private static void SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            True(state.TrySetConfigValue("保留最少编码数量", "4", out _, out _),
                nameof(SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength) + ".set");
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" }
                }));

            TypeLetters(engine, "ab");
            Equal(null, Press(engine, 0x43).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength) + ".one");
            Equal(null, Press(engine, 0x44).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength) + ".two");
            Equal(null, Press(engine, 0x45).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength) + ".three");
            Equal("甲", Press(engine, 0x46).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength) + ".four");
            Equal("cdef", engine.GetUiSnapshot(5).ActiveInputCode.Replace(" ", string.Empty),
                nameof(SentenceEmptyCodeAutoCommitHonorsConfiguredMinRetainedRawLength) + ".retained");
        }

        private static void SentenceAutoCommitNeutralTailCountsMergedEvidence()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cde"] = new List<string> { "乙" },
                    ["xyz"] = new List<string> { "丙" }
                }));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcde");
            Equal("甲", Press(engine, 0x58).TextToOutput,
                nameof(SentenceAutoCommitNeutralTailCountsMergedEvidence) + ".merged_counts");
            Equal("cdex", engine.GetUiSnapshot(5).ActiveInputCode.Replace(" ", string.Empty),
                nameof(SentenceAutoCommitNeutralTailCountsMergedEvidence) + ".three_raw_retained");
        }

        private static void SentenceAutoCommitMergedTailCountsAsEvidence()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cde"] = new List<string> { "乙" },
                    ["cdef"] = new List<string> { "乙" },
                    ["fg"] = new List<string> { "丙" }
                }));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcde");
            Equal("甲", Press(engine, 0x46).TextToOutput,
                nameof(SentenceAutoCommitMergedTailCountsAsEvidence) + ".merged_counts");
            Equal("cdef", engine.GetUiSnapshot(5).ActiveInputCode.Replace(" ", string.Empty),
                nameof(SentenceAutoCommitMergedTailCountsAsEvidence) + ".retained");
        }

        private static void SentenceAutoCommitStopsBeforeExtendableSegment()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" },
                    ["nv"] = new List<string> { "有" },
                    ["nvt"] = new List<string> { "郁" },
                    ["xy"] = new List<string> { "闷" }
                }));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcdn");
            Equal("甲", Press(engine, 0x56).TextToOutput,
                nameof(SentenceAutoCommitStopsBeforeExtendableSegment) + ".safe_prefix");
            Equal(null, Press(engine, 0x54).TextToOutput,
                nameof(SentenceAutoCommitStopsBeforeExtendableSegment) + ".extendable_tail");
            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            True(snapshot.Candidates.Length > 0 && snapshot.Candidates[0].Contains("郁"),
                nameof(SentenceAutoCommitStopsBeforeExtendableSegment) + ".candidate");
        }

        private static void SentenceAutoCommitDoesNotCountMergedEvidenceAcrossLowConfidenceGap()
        {
            var lexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cdef"] = new List<string> { "乙" },
                ["gh"] = new List<string> { "丙" },
                ["abc"] = new List<string> { "丁" },
                ["defg"] = new List<string> { "戊" },
                ["abcd"] = new List<string> { "己" },
                ["efg"] = new List<string> { "庚" }
            });
            var decoder = new SentenceInputDecoder(
                lexicon,
                new LowConfidenceGapSentenceLanguageModel(),
                beamWidth: 100);
            SentencePrefixEvidence firstStrong = decoder.DecodeFull(
                    "abcdef", 20, includeEarlyCommitEvidence: true)
                .EarlyCommitEvidence.Prefixes
                .Single(prefix => prefix.Text == "甲" && prefix.RawLength == 2);
            SentenceDecodeResult gap = decoder.DecodeFull(
                "abcdefg", 20, includeEarlyCommitEvidence: true);
            SentencePrefixEvidence secondStrong = decoder.DecodeFull(
                    "abcdefgh", 20, includeEarlyCommitEvidence: true)
                .EarlyCommitEvidence.Prefixes
                .Single(prefix => prefix.Text == "甲" && prefix.RawLength == 2);
            True(firstStrong.Share >= 0.99999 && firstStrong.BoundaryClosed &&
                 secondStrong.Share >= 0.99999 && secondStrong.BoundaryClosed,
                nameof(SentenceAutoCommitDoesNotCountMergedEvidenceAcrossLowConfidenceGap) +
                ".strong_ends");
            True(gap.Candidates.Length >= 2 &&
                 gap.EarlyCommitEvidence.NeutralLowConfidence,
                nameof(SentenceAutoCommitDoesNotCountMergedEvidenceAcrossLowConfidenceGap) +
                ".neutral_gap");

            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, new SentenceInputDecoder(
                lexicon,
                new LowConfidenceGapSentenceLanguageModel(),
                beamWidth: 100));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcdef");
            Equal(null, Press(engine, 0x47).TextToOutput,
                nameof(SentenceAutoCommitDoesNotCountMergedEvidenceAcrossLowConfidenceGap) +
                ".gap_does_not_commit");
            Equal(null, Press(engine, 0x48).TextToOutput,
                nameof(SentenceAutoCommitDoesNotCountMergedEvidenceAcrossLowConfidenceGap) +
                ".merged_strong_does_not_commit");
            Equal(null, Press(engine, 0x5A).TextToOutput,
                nameof(SentenceAutoCommitDoesNotCountMergedEvidenceAcrossLowConfidenceGap) +
                ".later_key_does_not_commit_yet");
        }

        private static void SentenceAutoCommitDroppedTailContradictsShorterCompetitor()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["efgh"] = new List<string> { "丙丁" },
                ["efghi"] = new List<string> { "丙戊" },
                ["ix"] = new List<string> { "庚" },
                ["xyz"] = new List<string> { "己" }
            };
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                new PrefersAfternoonOverCaveSentenceLanguageModel(),
                beamWidth: 100);
            SentenceDecodeResult shorter = decoder.DecodeFull(
                "abcdefgh", 20, includeEarlyCommitEvidence: true);
            SentenceDecodeResult longer = decoder.DecodeFull(
                "abcdefghi", 20, includeEarlyCommitEvidence: true);
            string shorterDump = DumpSentencePrefixes(shorter);
            string longerDump = DumpSentencePrefixes(longer);
            SentencePrefixEvidence cave = (shorter.EarlyCommitEvidence.Prefixes ??
                Array.Empty<SentencePrefixEvidence>())
                .FirstOrDefault(prefix => prefix.Text == "甲乙丙丁" && prefix.RawLength == 8);
            SentencePrefixEvidence afternoon = (longer.EarlyCommitEvidence.Prefixes ??
                Array.Empty<SentencePrefixEvidence>())
                .FirstOrDefault(prefix => prefix.Text == "甲乙丙戊" && prefix.RawLength == 9);
            True(cave != null, nameof(SentenceAutoCommitDroppedTailContradictsShorterCompetitor) +
                ".cave_missing " + shorterDump);
            True(afternoon != null, nameof(SentenceAutoCommitDroppedTailContradictsShorterCompetitor) +
                ".afternoon_missing " + longerDump);
            True(cave.Share >= 0.995 && cave.BoundaryClosed,
                nameof(SentenceAutoCommitDroppedTailContradictsShorterCompetitor) + ".cave " +
                shorterDump);
            True(afternoon.Share >= 0.995 && afternoon.BoundaryClosed &&
                 longer.EarlyCommitEvidence.MergedIncompleteTail,
                nameof(SentenceAutoCommitDroppedTailContradictsShorterCompetitor) + ".afternoon " +
                longerDump);
            True(!longer.EarlyCommitEvidence.Prefixes.Any(prefix =>
                    prefix.Text == "甲乙丙丁" && prefix.Share >= 0.995),
                nameof(SentenceAutoCommitDroppedTailContradictsShorterCompetitor) +
                ".cave_diluted " + longerDump);

            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                new PrefersAfternoonOverCaveSentenceLanguageModel(),
                beamWidth: 100));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcdefgh");
            Equal(null, Press(engine, 0x49).TextToOutput,
                nameof(SentenceAutoCommitDroppedTailContradictsShorterCompetitor) +
                ".stronger_competitor_prevents_commit");
            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            True(snapshot.Candidates.Length > 0 && snapshot.Candidates[0].Contains("丙戊") &&
                 !snapshot.Candidates[0].Contains("丙丁"),
                nameof(SentenceAutoCommitDroppedTailContradictsShorterCompetitor) +
                ".afternoon_visible");
        }

        private static void SentenceAutoCommitUsesTwoStrongGenerationCommonPrefix()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
            True(state.TrySetConfigValue("整句自动提前上屏", "是", out _, out string commitReason),
                nameof(SentenceAutoCommitUsesTwoStrongGenerationCommonPrefix) + ": " + commitReason);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["abc"] = new List<string> { "甲" },
                ["de"] = new List<string> { "丙" },
                ["def"] = new List<string> { "丁" },
                ["defg"] = new List<string> { "乙戊" }
            }));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcde");
            KeyEngineResult committed = Press(engine, 0x46);
            Equal("甲", committed.TextToOutput,
                nameof(SentenceAutoCommitUsesTwoStrongGenerationCommonPrefix) + ".single_character");
            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            True(snapshot.Candidates.Length > 0 && snapshot.Candidates[0] == "丁",
                nameof(SentenceAutoCommitUsesTwoStrongGenerationCommonPrefix) + ".unstable_suffix_retained");
        }

        private static void SentenceAutoCommitKeepsThreeGenerationWindowForWeakEvidence()
        {
            SentenceInputDecoder decoder = CreateWeakEvidenceSentenceDecoder();
            SentenceDecodeResult evidence = decoder.DecodeFull(
                "abcdef",
                20,
                includeEarlyCommitEvidence: true);
            Equal("甲乙丙", evidence.EarlyCommitEvidence.Proposal,
                nameof(SentenceAutoCommitKeepsThreeGenerationWindowForWeakEvidence) + ".proposal");
            True(evidence.EarlyCommitEvidence.ProposalShare >= 0.995 &&
                evidence.EarlyCommitEvidence.ProposalShare < 0.99999,
                nameof(SentenceAutoCommitKeepsThreeGenerationWindowForWeakEvidence) +
                ".share=" + evidence.EarlyCommitEvidence.ProposalShare.ToString(
                    "G17",
                    CultureInfo.InvariantCulture));

            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
            True(state.TrySetConfigValue("整句自动提前上屏", "是", out _, out string commitReason),
                nameof(SentenceAutoCommitKeepsThreeGenerationWindowForWeakEvidence) + ": " + commitReason);
            var engine = new InputMethodEngine(state, CreateWeakEvidenceSentenceDecoder());
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcdef");
            Equal("甲乙", Press(engine, 0x47).TextToOutput,
                nameof(SentenceAutoCommitKeepsThreeGenerationWindowForWeakEvidence) +
                ".merged_third_evidence");
        }

        private static void SentenceAutoCommitTracksPrefixesIndependently()
        {
            var lexicon = new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["ef"] = new List<string> { "丙" },
                ["efg"] = new List<string> { "丙" },
                ["efgh"] = new List<string> { "丙" },
                ["cdef"] = new List<string> { "丁" },
                ["cdefg"] = new List<string> { "丁" },
                ["cdefgh"] = new List<string> { "丁" }
            };
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                new WeakAlternativeSentenceLanguageModel(),
                beamWidth: 100);
            SentenceDecodeResult evidence = decoder.DecodeFull(
                "abcdef", 20, includeEarlyCommitEvidence: true);
            SentencePrefixEvidence stableShort = evidence.EarlyCommitEvidence.Prefixes
                .Single(prefix => prefix.Text == "甲" && prefix.RawLength == 2);
            SentencePrefixEvidence weakerLong = evidence.EarlyCommitEvidence.Prefixes
                .Single(prefix => prefix.Text == "甲乙" && prefix.RawLength == 4);
            True(stableShort.Share >= 0.99999,
                nameof(SentenceAutoCommitTracksPrefixesIndependently) + ".short_strong");
            True(weakerLong.Share >= 0.995 && weakerLong.Share < 0.99999,
                nameof(SentenceAutoCommitTracksPrefixesIndependently) + ".long_weak");
            True(stableShort.BoundaryClosed && !weakerLong.BoundaryClosed,
                nameof(SentenceAutoCommitTracksPrefixesIndependently) + ".closed_boundary");

            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                new WeakAlternativeSentenceLanguageModel(),
                beamWidth: 100));
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcde");
            Equal("甲", Press(engine, 0x46).TextToOutput,
                nameof(SentenceAutoCommitTracksPrefixesIndependently) + ".short_commits_first");
            Equal(null, Press(engine, 0x47).TextToOutput,
                nameof(SentenceAutoCommitTracksPrefixesIndependently) + ".continuation");
            Equal("cdefg", engine.GetUiSnapshot(5).ActiveInputCode.Replace(" ", string.Empty),
                nameof(SentenceAutoCommitTracksPrefixesIndependently) + ".suffix_retained");
        }

        private static void SentencePrefixEvidenceKeepsRawBoundariesDistinct()
        {
            SentenceInputDecoder decoder = CreateSentenceDecoder(
                new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲乙" },
                ["abc"] = new List<string> { "甲乙" },
            });
            SentenceDecodeResult shortCode = decoder.DecodeFull(
                "ab", 20, includeEarlyCommitEvidence: true);
            SentenceDecodeResult longCode = decoder.DecodeFull(
                "abc", 20, includeEarlyCommitEvidence: true);
            True(shortCode.EarlyCommitEvidence.Prefixes.Any(prefix =>
                    prefix.Text == "甲乙" && prefix.RawLength == 2),
                nameof(SentencePrefixEvidenceKeepsRawBoundariesDistinct) + ".short");
            True(longCode.EarlyCommitEvidence.Prefixes.Any(prefix =>
                    prefix.Text == "甲乙" && prefix.RawLength == 3),
                nameof(SentencePrefixEvidenceKeepsRawBoundariesDistinct) + ".long");
        }

        private static void SentencePrefixEvidenceLookupRequiresExactRawBoundary()
        {
            var prefixes = new[]
            {
                new SentencePrefixEvidence
                {
                    Text = "甲乙",
                    RawLength = 3,
                    Share = 1.0,
                    BoundaryShare = 1.0,
                    BoundaryClosed = true
                }
            };

            True(InputMethodEngine.FindSentencePrefixEvidence(prefixes, "甲乙", 2) == null,
                nameof(SentencePrefixEvidenceLookupRequiresExactRawBoundary) + ".different_boundary");
            True(InputMethodEngine.FindSentencePrefixEvidence(prefixes, "甲乙", 3) != null,
                nameof(SentencePrefixEvidenceLookupRequiresExactRawBoundary) + ".exact_boundary");
        }

        private static void SentencePrefixEvidenceWeightsBoundaryDisagreement()
        {
            var lexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["ef"] = new List<string> { "丙" },
                ["abc"] = new List<string> { "丁" },
                ["def"] = new List<string> { "戊" }
            });
            var materialDecoder = new SentenceInputDecoder(
                lexicon,
                new BoundaryAlternativeSentenceLanguageModel(-8.0),
                beamWidth: 100);
            var negligibleDecoder = new SentenceInputDecoder(
                lexicon,
                new BoundaryAlternativeSentenceLanguageModel(-20.0),
                beamWidth: 100);

            SentencePrefixEvidence material = materialDecoder.DecodeFull(
                    "abcdef", 20, includeEarlyCommitEvidence: true)
                .EarlyCommitEvidence.Prefixes
                .Single(prefix => prefix.Text == "甲乙" && prefix.RawLength == 4);
            SentencePrefixEvidence negligible = negligibleDecoder.DecodeFull(
                    "abcdef", 20, includeEarlyCommitEvidence: true)
                .EarlyCommitEvidence.Prefixes
                .Single(prefix => prefix.Text == "甲乙" && prefix.RawLength == 4);

            True(material.BoundaryShare >= 0.995 && material.BoundaryShare < 0.99999 &&
                 !material.BoundaryClosed,
                nameof(SentencePrefixEvidenceWeightsBoundaryDisagreement) + ".material");
            True(negligible.BoundaryShare >= 0.99999 && negligible.BoundaryClosed,
                nameof(SentencePrefixEvidenceWeightsBoundaryDisagreement) + ".negligible");
        }

        private static void SentenceAutoCommitAcceptsProbabilisticAlternatingSegmentation()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
            True(state.TrySetConfigValue("整句自动提前上屏", "是", out _, out string commitReason),
                nameof(SentenceAutoCommitAcceptsProbabilisticAlternatingSegmentation) + ": " + commitReason);
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" },
                    ["ef"] = new List<string> { "丙" },
                    ["gh"] = new List<string> { "辛" },
                    ["abc"] = new List<string> { "丁" },
                    ["de"] = new List<string> { "戊" },
                    ["fg"] = new List<string> { "己" }
                }),
                new PrefersIncompleteTailSentenceLanguageModel(),
                beamWidth: 100);
            var engine = new InputMethodEngine(state, decoder);
            engine.SentenceEmptyCodeAutoCommitOverride = false;

            TypeLetters(engine, "abcde");
            Equal("甲", Press(engine, 0x46).TextToOutput,
                nameof(SentenceAutoCommitAcceptsProbabilisticAlternatingSegmentation) +
                ".probabilistic_commit");
        }

        private static void SentenceAutoCommitReplayPreservesOriginalCommit()
        {
            InputMethodEngine engine = CreateAutoCommitSentenceEngine();
            TypeLetters(engine, "abcde");
            KeyEngineResult committed = Press(engine, 0x46);
            Equal("甲乙", committed.TextToOutput,
                nameof(SentenceAutoCommitReplayPreservesOriginalCommit) + ".initial");

            var cache = new KeyRequestReplayCache(16);
            string key = KeyRequestReplayCache.BuildKey("frontend-auto", "commit-event");
            cache.Store(key,
                "{\"type\":\"response\",\"seq\":10,\"success\":true,\"handled\":true," +
                "\"commit_text\":\"甲乙\"}");
            string displayBeforeReplay = engine.GetUiSnapshot(5).ActiveInputCode;
            True(cache.TryGet(key, 11, out string replayed),
                nameof(SentenceAutoCommitReplayPreservesOriginalCommit) + ".cache_hit");
            True(replayed.Contains("\"seq\":11,") && replayed.Contains("\"commit_text\":\"甲乙\""),
                nameof(SentenceAutoCommitReplayPreservesOriginalCommit) + ".same_commit");
            Equal(displayBeforeReplay, engine.GetUiSnapshot(5).ActiveInputCode,
                nameof(SentenceAutoCommitReplayPreservesOriginalCommit) + ".state_unchanged");
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

        private static KeyEngineResult PressChord(
            InputMethodEngine engine,
            int vk,
            bool shift = false,
            bool ctrl = false,
            bool alt = false,
            bool win = false)
        {
            KeyEngineResult result = engine.ProcessKey(
                vk, 0, "down", shift, ctrl, alt, win, false, false, 1, false);
            engine.PostProcessKey(vk, "down", result, shift, ctrl, alt, win, false);
            return result;
        }

        private static void ReleaseChord(
            InputMethodEngine engine,
            int vk,
            bool shift = false,
            bool ctrl = false,
            bool alt = false,
            bool win = false)
        {
            KeyEngineResult result = engine.ProcessKey(
                vk, 0, "up", shift, ctrl, alt, win, false, false, 1, false);
            engine.PostProcessKey(vk, "up", result, shift, ctrl, alt, win, false);
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


        private static void EnableSentenceMode(CoreRuntimeState state)
        {
            state.TrySetConfigValue("自动启用整句模式", "是", out _, out _);
            state.TrySetConfigValue("当前码表", "虎整句", out _, out _);
        }

        private static void EnableSentenceEarlyCommit(CoreRuntimeState state)
        {
            EnableSentenceMode(state);
            state.TrySetConfigValue("整句自动提前上屏", "是", out _, out _);
        }

        private static SentenceInputDecoder CreateSentenceDecoder(
            Dictionary<string, List<string>> lexicon,
            bool allowDuplicateSingleCharacters = false)
        {
            return new SentenceInputDecoder(
                SentenceLexiconIndex.Build(lexicon),
                NeutralSentenceLanguageModel.Instance,
                beamWidth: 100,
                allowDuplicateSingleCharacters: allowDuplicateSingleCharacters);
        }

        private static SentenceInputDecoder CreateWeakEvidenceSentenceDecoder()
        {
            return new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" },
                    ["abcd"] = new List<string> { "丁戊" },
                    ["ef"] = new List<string> { "丙" },
                    ["efg"] = new List<string> { "丙" },
                    ["efgh"] = new List<string> { "丙" }
                }),
                new WeakAlternativeSentenceLanguageModel(),
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

        private static void SentenceEmptyCodeAutoCommitDefaultsOnAndRetainsNewKey()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" }
            }));

            TypeLetters(engine, "ab");
            KeyEngineResult result = Press(engine, 0x43);
            Equal("甲", result.TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitDefaultsOnAndRetainsNewKey) + ".commit");
            EngineUiSnapshot snapshot = engine.GetUiSnapshot(5);
            Equal("c", snapshot.ActiveInputCode,
                nameof(SentenceEmptyCodeAutoCommitDefaultsOnAndRetainsNewKey) + ".retained_code");
            True(snapshot.Candidates.Length == 0,
                nameof(SentenceEmptyCodeAutoCommitDefaultsOnAndRetainsNewKey) + ".incomplete_tail");
            Press(engine, 0x44);
            snapshot = engine.GetUiSnapshot(5);
            True(snapshot.Candidates.Length == 1 && snapshot.Candidates[0] == "乙",
                nameof(SentenceEmptyCodeAutoCommitDefaultsOnAndRetainsNewKey) + ".continued_candidate");
        }

        private static void SentenceEmptyCodeAutoCommitPreservesSentenceContext()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" }
                }));

            TypeLetters(engine, "ab");
            Equal("甲", Press(engine, 0x43).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitPreservesSentenceContext) + ".commit");
            EngineDifferentialSnapshot committed = engine.GetDifferentialSnapshot(5);
            Equal("abc", committed.RawInput,
                nameof(SentenceEmptyCodeAutoCommitPreservesSentenceContext) + ".full_raw");
            Equal("甲", committed.SentenceCommittedText,
                nameof(SentenceEmptyCodeAutoCommitPreservesSentenceContext) + ".committed_text");
            True(committed.SentenceCommittedRawLength == 2,
                nameof(SentenceEmptyCodeAutoCommitPreservesSentenceContext) + ".raw_boundary");
            Equal("c", committed.Ui.ActiveInputCode,
                nameof(SentenceEmptyCodeAutoCommitPreservesSentenceContext) + ".visible_tail");

            Press(engine, 0x44);
            EngineUiSnapshot continued = engine.GetUiSnapshot(5);
            True(continued.Candidates.Length > 0 && continued.Candidates[0] == "乙",
                nameof(SentenceEmptyCodeAutoCommitPreservesSentenceContext) + ".continued_candidate");
        }

        private static void SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards()
        {
            var disabledState = new CoreRuntimeState();
            EnableSentenceMode(disabledState);
            var disabled = new InputMethodEngine(disabledState, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" }
                }));
            TypeLetters(disabled, "ab");
            Equal(null, Press(disabled, 0x43).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards) + ".disabled");
            Equal("abc", disabled.GetUiSnapshot(5).ActiveInputCode,
                nameof(SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards) + ".disabled_code");

            var multipleState = new CoreRuntimeState();
            EnableSentenceEarlyCommit(multipleState);
            multipleState.TrySetConfigValue("允许单字重码组句", "否", out _, out _);
            var multiple = new InputMethodEngine(multipleState, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲", "乙" }
                }));
            TypeLetters(multiple, "ab");
            Equal("甲", Press(multiple, 0x43).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards) +
                ".implicit_nonfirst_does_not_block");

            var ambiguousState = new CoreRuntimeState();
            EnableSentenceEarlyCommit(ambiguousState);
            var ambiguous = new InputMethodEngine(ambiguousState, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" },
                    ["abcd"] = new List<string> { "整" }
                }));
            TypeLetters(ambiguous, "abcd");
            Equal(null, Press(ambiguous, 0x45).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards) +
                ".primary_path_ambiguity_blocks");

            var nonemptyState = new CoreRuntimeState();
            EnableSentenceEarlyCommit(nonemptyState);
            var nonempty = new InputMethodEngine(nonemptyState, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["abc"] = new List<string> { "丙" }
                }));
            TypeLetters(nonempty, "ab");
            Equal(null, Press(nonempty, 0x43).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards) + ".new_code_nonempty");
            True(nonempty.GetUiSnapshot(5).Candidates.Length > 0,
                nameof(SentenceEmptyCodeAutoCommitHonorsDisabledAndCandidateGuards) + ".candidate_preserved");
        }

        private static void SentenceEmptyCodeAutoCommitAcceptsStrongPrimaryCandidate()
        {
            var lexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["abcd"] = new List<string> { "丁" }
            });

            var strongState = new CoreRuntimeState();
            EnableSentenceEarlyCommit(strongState);
            var strong = new InputMethodEngine(strongState, new SentenceInputDecoder(
                lexicon,
                new BoundaryAlternativeSentenceLanguageModel(-20.0),
                beamWidth: 100));
            TypeLetters(strong, "abcd");
            KeyEngineResult committed = Press(strong, 0x45);
            Equal("甲乙", committed.TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitAcceptsStrongPrimaryCandidate) + ".commit");
            Equal("e", strong.GetUiSnapshot(5).ActiveInputCode,
                nameof(SentenceEmptyCodeAutoCommitAcceptsStrongPrimaryCandidate) + ".retained");

            var weakState = new CoreRuntimeState();
            EnableSentenceEarlyCommit(weakState);
            var weak = new InputMethodEngine(weakState, new SentenceInputDecoder(
                lexicon,
                new BoundaryAlternativeSentenceLanguageModel(-8.0),
                beamWidth: 100));
            TypeLetters(weak, "abcd");
            Equal(null, Press(weak, 0x45).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitAcceptsStrongPrimaryCandidate) + ".weak_blocked");
            Equal("abcde", weak.GetUiSnapshot(5).ActiveInputCode.Replace(" ", string.Empty),
                nameof(SentenceEmptyCodeAutoCommitAcceptsStrongPrimaryCandidate) + ".weak_raw");
        }

        private static void SentenceEmptyCodeAutoCommitWorksWithAsyncDecode()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(
                state,
                new SentenceInputDecoder(
                    SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                    {
                        ["ab"] = new List<string> { "甲" },
                        ["abcd"] = new List<string> { "乙" }
                    }),
                    new SlowSentenceLanguageModel(),
                    beamWidth: 10),
                sentenceDecodeSynchronously: false);

            using (var completed = new ManualResetEvent(false))
            {
                engine.SetSentenceDecodeCompletedCallback(() => completed.Set());
                TypeLetters(engine, "ab");
                True(completed.WaitOne(3000),
                    nameof(SentenceEmptyCodeAutoCommitWorksWithAsyncDecode) + ".prefix_ready");
                while (engine.IsSentenceDecodePending)
                {
                    Thread.Sleep(5);
                }

                completed.Reset();
                var stopwatch = Stopwatch.StartNew();
                KeyEngineResult result = Press(engine, 0x43);
                stopwatch.Stop();
                Equal(null, result.TextToOutput,
                    nameof(SentenceEmptyCodeAutoCommitWorksWithAsyncDecode) + ".deferred");
                True(stopwatch.ElapsedMilliseconds < 60,
                    nameof(SentenceEmptyCodeAutoCommitWorksWithAsyncDecode) + ".key_latency");
                Equal("abc", engine.GetUiSnapshot(5).ActiveInputCode,
                    nameof(SentenceEmptyCodeAutoCommitWorksWithAsyncDecode) + ".pending_code");

                Equal("甲", Press(engine, 0x45).TextToOutput,
                    nameof(SentenceEmptyCodeAutoCommitWorksWithAsyncDecode) + ".dead_commit");
                Equal("ce", engine.GetUiSnapshot(5).ActiveInputCode,
                    nameof(SentenceEmptyCodeAutoCommitWorksWithAsyncDecode) + ".retained_code");
                True(completed.WaitOne(3000),
                    nameof(SentenceEmptyCodeAutoCommitWorksWithAsyncDecode) + ".suffix_ready");
            }
        }

        private static void SentenceEmptyCodeContinuationPreservesDuplicateSingles()
        {
            foreach (string prefixCode in new[] { "xr", "xry" })
            foreach (bool autoCommit in new[] { false, true })
            foreach (bool duplicates in new[] { false, true })
            {
                var state = new CoreRuntimeState();
                EnableSentenceMode(state);
                state.TrySetConfigValue("整句自动提前上屏", autoCommit ? "是" : "否", out _, out _);
                state.TrySetConfigValue("允许单字重码组句", duplicates ? "是" : "否", out _, out _);
                var decoder = new SentenceInputDecoder(
                    SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                    {
                        ["xr"] = new List<string> { "反" },
                        ["xry"] = new List<string> { "反" },
                        ["xbj"] = new List<string> { "秉", "刍", "多字词" }
                    }), new PrefersCharactersLanguageModel("刍", "多", "字", "词"),
                    beamWidth: 100, allowDuplicateSingleCharacters: duplicates);
                var engine = new InputMethodEngine(state, decoder);
                TypeLetters(engine, prefixCode);
                string prefix = Press(engine, 0x58).TextToOutput ?? string.Empty;
                Equal(autoCommit ? "反" : string.Empty, prefix, "rumination.prefix");
                TypeLetters(engine, "bj");
                var view = engine.GetUiSnapshot(5);
                Equal(duplicates ? "反刍" : "反秉", prefix + view.Candidates[0], "rumination.first");
                True(!view.Candidates.Any(text => text.Contains("多字词")), "rumination.implicit_word_hidden");
                Equal(duplicates ? "反刍" : "反秉", prefix + Press(engine, 0x20).TextToOutput, "rumination.commit");
            }
        }

        private static void SentenceAutoCommitContinuationUsesFirstRanksOnly()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["rl"] = new List<string> { "了", "对方" }
                }));

            TypeLetters(engine, "ab");
            Equal("甲", Press(engine, 0x52).TextToOutput,
                nameof(SentenceAutoCommitContinuationUsesFirstRanksOnly) + ".auto_commit");
            Press(engine, 0x4C);
            EngineUiSnapshot continuation = engine.GetUiSnapshot(5);
            True(continuation.Candidates.Length == 1 && continuation.Candidates[0] == "了",
                nameof(SentenceAutoCommitContinuationUsesFirstRanksOnly) + ".implicit_non_first_hidden");

            Press(engine, 0x1B);
            TypeLetters(engine, "rl");
            EngineUiSnapshot standalone = engine.GetUiSnapshot(5);
            True(Array.Exists(standalone.Candidates, candidate => candidate == "对方"),
                nameof(SentenceAutoCommitContinuationUsesFirstRanksOnly) + ".standalone_non_first_visible");
        }

        private static void EngineLightweightKeyStateMatchesUiSnapshot()
        {
            var state = new CoreRuntimeState();
            var engine = new InputMethodEngine(state);

            engine.GetKeyState(out bool idleChinese, out bool idleComposing);
            EngineUiSnapshot idle = engine.GetUiSnapshot(5);
            True(idleChinese == idle.IsChinese && idleComposing == idle.IsComposing,
                nameof(EngineLightweightKeyStateMatchesUiSnapshot) + ".idle");

            Press(engine, 0x41);
            engine.GetKeyState(out bool activeChinese, out bool activeComposing);
            EngineUiSnapshot active = engine.GetUiSnapshot(5);
            True(activeChinese == active.IsChinese && activeComposing == active.IsComposing,
                nameof(EngineLightweightKeyStateMatchesUiSnapshot) + ".active");
        }

        private static void SentenceEmptyCodeAutoCommitDefersCompletableLastSegment()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var engine = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["abcd"] = new List<string> { "乙" }
                }));

            TypeLetters(engine, "ab");
            Equal(null, Press(engine, 0x43).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".deferred");
            Equal("abc", engine.GetUiSnapshot(5).ActiveInputCode,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".deferred_raw");
            Equal(null, Press(engine, 0x44).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".matched");
            True(engine.GetUiSnapshot(5).Candidates.Length == 1 &&
                engine.GetUiSnapshot(5).Candidates[0] == "乙",
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".matched_candidate");

            var dead = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["abcd"] = new List<string> { "乙" }
                }));
            TypeLetters(dead, "ab");
            Equal(null, Press(dead, 0x43).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".dead_deferred");
            Equal("甲", Press(dead, 0x45).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".dead_commit");
            Equal("ce", dead.GetUiSnapshot(5).ActiveInputCode,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".dead_tail_retained");

            var canceled = new InputMethodEngine(state, CreateSentenceDecoder(
                new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["abcd"] = new List<string> { "乙" }
                }));
            TypeLetters(canceled, "ab");
            Press(canceled, 0x43);
            Press(canceled, 0x20);
            Equal(null, Press(canceled, 0x45).TextToOutput,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".manual_cancel");
            Equal("abce", canceled.GetUiSnapshot(5).ActiveInputCode,
                nameof(SentenceEmptyCodeAutoCommitDefersCompletableLastSegment) + ".manual_raw");
        }

        private static void SentenceDecoderUsesSourcePriorityForEqualLengthCodes()
        {
            var source = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ldac"] = new List<string> { "燕", "㷼" },
                ["ladc"] = new List<string> { "燕" }
            };
            SentenceLexiconIndex index = SentenceLexiconIndex.Build(
                source,
                new HashSet<string>(StringComparer.Ordinal) { "燕" });

            True(
                Array.Exists(index.GetCandidates("ldac"), candidate => candidate.Text == "燕"),
                nameof(SentenceDecoderUsesSourcePriorityForEqualLengthCodes) + ".preferred");
            True(
                index.GetCandidates("ladc") == null ||
                Array.TrueForAll(index.GetCandidates("ladc"), candidate => candidate.Text != "燕"),
                nameof(SentenceDecoderUsesSourcePriorityForEqualLengthCodes) + ".alternate_filtered");
        }

        private static void KeyResponsesDeclareExpectedKeyUp()
        {
            var state = new CoreRuntimeState();
            using (var handler = new ProtocolHandler(_ => { }, state, null))
            {
                string letterResponse = handler.Handle(
                    "{\"type\":\"key\",\"seq\":1,\"client_session\":\"keyup-test\"," +
                    "\"event_id\":\"letter\",\"action\":\"down\",\"vk\":65}");
                True(letterResponse.Contains("\"expect_keyup\":false"),
                    nameof(KeyResponsesDeclareExpectedKeyUp) + ".letter");

                string shiftResponse = handler.Handle(
                    "{\"type\":\"key\",\"seq\":2,\"client_session\":\"keyup-test\"," +
                    "\"event_id\":\"shift\",\"action\":\"down\",\"vk\":16,\"scan\":42}");
                True(shiftResponse.Contains("\"expect_keyup\":true"),
                    nameof(KeyResponsesDeclareExpectedKeyUp) + ".shift");

                string replayedShift = handler.Handle(
                    "{\"type\":\"key\",\"seq\":3,\"client_session\":\"keyup-test\"," +
                    "\"event_id\":\"shift\",\"action\":\"down\",\"vk\":16,\"scan\":42}");
                True(replayedShift.Contains("\"seq\":3,") &&
                     replayedShift.Contains("\"expect_keyup\":true"),
                    nameof(KeyResponsesDeclareExpectedKeyUp) + ".replay");
            }
        }

        private static void ShortcutGesturesParseAndMatch()
        {
            True(
                ShortcutGesture.TryParse("shift+ctrl+alt+vk_oem_plus", out ShortcutGesture gesture),
                nameof(ShortcutGesturesParseAndMatch) + ".parse");
            Equal("Ctrl+Alt+Shift+VK_OEM_PLUS", gesture.ToConfigString(),
                nameof(ShortcutGesturesParseAndMatch) + ".canonical");
            Equal("Ctrl+Alt+Shift+=", gesture.ToDisplayString(),
                nameof(ShortcutGesturesParseAndMatch) + ".display");
            True(gesture.Matches(0xBB, true, true, true, false),
                nameof(ShortcutGesturesParseAndMatch) + ".match");
            True(!gesture.Matches(0xBB, false, true, true, false),
                nameof(ShortcutGesturesParseAndMatch) + ".exact_modifiers");
            True(!ShortcutGesture.TryParse("VK_K", out _),
                nameof(ShortcutGesturesParseAndMatch) + ".reject_bare");
            True(!ShortcutGesture.TryParse("Win+VK_K", out _),
                nameof(ShortcutGesturesParseAndMatch) + ".reject_win");
            True(!ShortcutGesture.TryParse("Ctrl+VK_CONTROL", out _),
                nameof(ShortcutGesturesParseAndMatch) + ".reject_modifier_key");

            ShortcutGesture.TryParse("Ctrl+VK_SPACE", out ShortcutGesture ctrlSpace);
            True(
                ShortcutBindingRules.GetReservedConflict(ctrlSpace, true, true) == ShortcutConflictKind.CtrlSpace,
                nameof(ShortcutGesturesParseAndMatch) + ".ctrl_space_conflict");
            True(
                ShortcutBindingRules.GetReservedConflict(ctrlSpace, false, true) == ShortcutConflictKind.None,
                nameof(ShortcutGesturesParseAndMatch) + ".disabled_ctrl_space_available");
            ShortcutGesture.TryParse("Alt+VK_OEM_5", out ShortcutGesture altBackslash);
            True(
                ShortcutBindingRules.GetReservedConflict(altBackslash, true, true) == ShortcutConflictKind.NativeHookAltBackslash,
                nameof(ShortcutGesturesParseAndMatch) + ".native_toggle_conflict");
            ShortcutGesture.TryParse("Ctrl+VK_3", out ShortcutGesture ctrlDigit);
            True(
                ShortcutBindingRules.GetReservedConflict(ctrlDigit, true, true) == ShortcutConflictKind.CtrlDigitReorder,
                nameof(ShortcutGesturesParseAndMatch) + ".ctrl_digit_conflict");
            ShortcutGesture.TryParse("Ctrl+Shift+VK_3", out ShortcutGesture ctrlShiftDigit);
            True(
                ShortcutBindingRules.GetReservedConflict(ctrlShiftDigit, true, true) == ShortcutConflictKind.CtrlDigitReorder,
                nameof(ShortcutGesturesParseAndMatch) + ".ctrl_shift_digit_conflict");
        }

        private static void CustomActionShortcutsTriggerAndRearm()
        {
            string root = Path.Combine(Path.GetTempPath(), "TigerClaw.Core.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var state = new CoreRuntimeState(root);
                state.Initialize();
                True(state.TrySetConfigValue(
                    "手动加词快捷键", "Ctrl+Alt+Shift+VK_K", out _, out _),
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".configure");
                var engine = new InputMethodEngine(state);

                KeyEngineResult oldDefault = PressChord(engine, 0xBB, ctrl: true);
                True(!oldDefault.OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".old_default_released");
                KeyEngineResult wrongModifiers = PressChord(engine, 0x4B, ctrl: true, alt: true);
                True(!wrongModifiers.OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".exact_modifiers");

                KeyEngineResult first = PressChord(engine, 0x4B, shift: true, ctrl: true, alt: true);
                True(first.Handled && first.OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".first");
                KeyEngineResult repeated = PressChord(engine, 0x4B, shift: true, ctrl: true, alt: true);
                True(repeated.Handled && !repeated.OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".repeat_once");
                engine.OnFocusChanged();
                True(PressChord(engine, 0x4B, shift: true, ctrl: true, alt: true).OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".focus_rearms");
                ReleaseChord(engine, 0x4B, shift: true, ctrl: true, alt: true);
                True(PressChord(engine, 0x4B, shift: true, ctrl: true, alt: true).OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".rearmed");

                state.TrySetConfigValue("Ctrl+等号手动加词", "否", out _, out _);
                ReleaseChord(engine, 0x4B, shift: true, ctrl: true, alt: true);
                True(!PressChord(engine, 0x4B, shift: true, ctrl: true, alt: true).OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".disabled");

                state.TrySetConfigValue("Ctrl+等号手动加词", "是", out _, out _);
                state.TrySetConfigValue("手动加词快捷键", "invalid", out _, out _);
                True(PressChord(engine, 0xBB, ctrl: true).OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".invalid_falls_back");

                ReleaseChord(engine, 0xBB, ctrl: true);
                state.TrySetConfigValue("手动加词快捷键", "Ctrl+VK_J", out _, out _);
                state.TrySetConfigValue("Ctrl+m切换最近码表", "是", out _, out _);
                state.TrySetConfigValue("切换最近码表快捷键", "Ctrl+VK_J", out _, out _);
                KeyEngineResult ambiguous = PressChord(engine, 0x4A, ctrl: true);
                True(!ambiguous.OpenAddCiWindow,
                    nameof(CustomActionShortcutsTriggerAndRearm) + ".ambiguous_never_adds");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void CustomRecentSchemaShortcutSwitchesSchemas()
        {
            string root = Path.Combine(Path.GetTempPath(), "TigerClaw.Core.Tests", Guid.NewGuid().ToString("N"));
            string codeRoot = Path.Combine(root, "码表");
            Directory.CreateDirectory(Path.Combine(codeRoot, "虎码字词"));
            Directory.CreateDirectory(Path.Combine(codeRoot, "虎整句"));
            File.WriteAllText(
                Path.Combine(codeRoot, "虎码字词", "虎码字词.txt"),
                "甲\tab\r\n乙\tcd\r\n{加词}\tef\r\n");
            File.WriteAllText(
                Path.Combine(codeRoot, "虎整句", "虎整句.txt"),
                "你\tab\r\n好\tcd\r\n{加词}\tef\r\n");
            try
            {
                var state = new CoreRuntimeState(root);
                state.Initialize();
                state.TrySetConfigValue("当前码表", "虎整句", out _, out _);
                state.ReloadLexicon();
                state.TrySetConfigValue("当前码表", "虎码字词", out _, out _);
                state.ReloadLexicon();
                state.TrySetConfigValue("最大码长", "2", out _, out _);
                state.TrySetConfigValue("中英文不限长混合输入", "是", out _, out _);
                state.TrySetConfigValue("最大码长无重自动上屏", "否", out _, out _);
                state.TrySetConfigValue("手动加词快捷键", "Ctrl+VK_E", out _, out _);
                state.TrySetConfigValue("Ctrl+m切换最近码表", "是", out _, out _);
                state.TrySetConfigValue("切换最近码表快捷键", "Ctrl+VK_J", out _, out _);
                var decoder = new SentenceInputDecoder(
                    SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                    {
                        ["ab"] = new List<string> { "你" },
                        ["cd"] = new List<string> { "好" }
                    }),
                    NeutralSentenceLanguageModel.Instance,
                    beamWidth: 20);
                var engine = new InputMethodEngine(state, decoder, sentenceDecodeSynchronously: true);

                TypeLetters(engine, "ab");
                EngineUiSnapshot before = engine.GetUiSnapshot(5);
                Equal("虎码字词", state.GetCurrentSchema(),
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".before");
                Equal("甲", before.Candidates[0],
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".before_candidate");

                KeyEngineResult switched = PressChord(engine, 0x4A, ctrl: true);
                True(switched.Handled,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".handled");
                True(!switched.OpenAddCiWindow,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".never_opens_add_word");
                Equal("虎整句", state.GetCurrentSchema(),
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".sentence_schema");
                True(engine.IsSentenceCompositionActive,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".sentence_state");
                EngineUiSnapshot sentence = engine.GetUiSnapshot(5);
                Equal("ab", sentence.InputCode,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".sentence_raw");
                Equal("你", sentence.Candidates[0],
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".sentence_candidate");

                KeyEngineResult rolledIntoAddWord = PressChord(engine, 0x45, ctrl: true);
                True(rolledIntoAddWord.Handled && !rolledIntoAddWord.OpenAddCiWindow,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".modifier_rollover_not_add_word");
                Equal("虎整句", state.GetCurrentSchema(),
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".modifier_rollover_not_switch");

                ReleaseChord(engine, 0x4A, ctrl: true);
                ReleaseChord(engine, 0x11);
                KeyEngineResult switchedBack = PressChord(engine, 0x4A, ctrl: true);
                True(switchedBack.Handled,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".back_handled");
                True(!switchedBack.OpenAddCiWindow,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".back_never_opens_add_word");
                Equal("虎码字词", state.GetCurrentSchema(),
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".normal_schema");
                True(!engine.IsSentenceCompositionActive,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".normal_state");
                EngineUiSnapshot normal = engine.GetUiSnapshot(5);
                Equal("ab", normal.InputCode,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".normal_raw");
                Equal("甲", normal.Candidates[0],
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".normal_candidate");

                TypeLetters(engine, "cd");
                EngineUiSnapshot continued = engine.GetUiSnapshot(5);
                Equal("abcd", engine.GetDifferentialSnapshot(5).RawInput,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".continued_raw");
                Equal("乙", continued.Candidates[0],
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".continued_candidate");

                Press(engine, 0x1B);
                TypeLetters(engine, "ef");
                EngineUiSnapshot actionCandidate = engine.GetUiSnapshot(5);
                True(actionCandidate.Candidates.Length > 0,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".action_candidate_exists");
                Equal("{加词}", actionCandidate.Candidates[0],
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".action_candidate_before_switch");
                ReleaseChord(engine, 0x4A, ctrl: true);
                ReleaseChord(engine, 0x11);
                KeyEngineResult switchedWithActionCandidate = PressChord(engine, 0x4A, ctrl: true);
                True(switchedWithActionCandidate.Handled && !switchedWithActionCandidate.OpenAddCiWindow,
                    nameof(CustomRecentSchemaShortcutSwitchesSchemas) + ".action_candidate_not_committed");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void KeyUpExpectationCoversReleaseDependentState()
        {
            var state = new CoreRuntimeState();
            var engine = new InputMethodEngine(state);

            Press(engine, 0x41);
            True(!engine.ShouldExpectKeyUp(0x41, 0, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".letter");
            True(engine.ShouldExpectKeyUp(0x10, 0x2A, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".left_shift");
            True(engine.ShouldExpectKeyUp(0x10, 0x36, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".right_shift");
            True(engine.ShouldExpectKeyUp(0x11, 0, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".control");
            True(engine.ShouldExpectKeyUp(0x20, 0, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".space");
            True(engine.ShouldExpectKeyUp(0xDE, 0, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".quote");

            PressWithCtrl(engine, 0xBB);
            True(engine.ShouldExpectKeyUp(0xBB, 0, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".one_shot");
            KeyEngineResult keyUp = engine.ProcessKey(
                0xBB, 0, "up", false, true, false, false, false, false, 1, false);
            engine.PostProcessKey(0xBB, "up", keyUp, false, true, false, false, false);
            True(!engine.ShouldExpectKeyUp(0xBB, 0, false),
                nameof(KeyUpExpectationCoversReleaseDependentState) + ".one_shot_rearmed");
        }

        private static void CtrlSpaceStillTogglesWithExpectedKeyUpResponses()
        {
            var state = new CoreRuntimeState();
            using (var handler = new ProtocolHandler(_ => { }, state, null))
            {
                string ctrlDown = handler.Handle(
                    "{\"type\":\"key\",\"seq\":1,\"client_session\":\"ctrl-space-test\"," +
                    "\"event_id\":\"ctrl-down\",\"action\":\"down\",\"vk\":17,\"scan\":29,\"ctrl\":true}");
                True(ctrlDown.Contains("\"expect_keyup\":true"),
                    nameof(CtrlSpaceStillTogglesWithExpectedKeyUpResponses) + ".ctrl_down");

                string orphanSpaceUp = handler.Handle(
                    "{\"type\":\"key\",\"seq\":2,\"client_session\":\"ctrl-space-test\"," +
                    "\"event_id\":\"space-up\",\"action\":\"up\",\"vk\":32,\"ctrl\":true}");
                True(orphanSpaceUp.Contains("\"handled\":true") &&
                     orphanSpaceUp.Contains("\"keyboard_open\":false"),
                    nameof(CtrlSpaceStillTogglesWithExpectedKeyUpResponses) + ".orphan_space_up");
            }


            var ctrlFirstState = new CoreRuntimeState();
            using (var ctrlFirstHandler = new ProtocolHandler(_ => { }, ctrlFirstState, null))
            {
                ctrlFirstHandler.Handle(
                    "{\"type\":\"key\",\"seq\":1,\"client_session\":\"ctrl-first-test\"," +
                    "\"event_id\":\"ctrl-down\",\"action\":\"down\",\"vk\":17,\"scan\":29,\"ctrl\":true}");
                string ctrlUp = ctrlFirstHandler.Handle(
                    "{\"type\":\"key\",\"seq\":2,\"client_session\":\"ctrl-first-test\"," +
                    "\"event_id\":\"ctrl-up\",\"action\":\"up\",\"vk\":17,\"scan\":29}");
                True(ctrlUp.Contains("\"keyboard_open\":true"),
                    nameof(CtrlSpaceStillTogglesWithExpectedKeyUpResponses) + ".ctrl_first_not_yet");

                string delayedSpaceUp = ctrlFirstHandler.Handle(
                    "{\"type\":\"key\",\"seq\":3,\"client_session\":\"ctrl-first-test\"," +
                    "\"event_id\":\"space-up\",\"action\":\"up\",\"vk\":32}");
                True(delayedSpaceUp.Contains("\"handled\":true") &&
                     delayedSpaceUp.Contains("\"keyboard_open\":false"),
                    nameof(CtrlSpaceStillTogglesWithExpectedKeyUpResponses) + ".ctrl_first_space_up");
            }
        }

        private static void TransportDefersOnlyKeyUiPublication()
        {
            var state = new CoreRuntimeState();
            using (var handler = new ProtocolHandler(_ => { }, state, null))
            {
                string response = handler.HandleTransport(
                    "{\"type\":\"key\",\"seq\":1,\"client_session\":\"test\",\"event_id\":\"1\",\"action\":\"down\",\"vk\":65}",
                    out bool publishKeyUiAfterResponse);
                True(!string.IsNullOrEmpty(response), nameof(TransportDefersOnlyKeyUiPublication) + ".response");
                True(publishKeyUiAfterResponse, nameof(TransportDefersOnlyKeyUiPublication) + ".key");

                handler.HandleTransport(
                    "{\"type\":\"query_state\",\"seq\":2}",
                    out bool publishQueryUiAfterResponse);
                True(!publishQueryUiAfterResponse, nameof(TransportDefersOnlyKeyUiPublication) + ".query");
            }
        }

        private static void SentenceEngineDecodesLongWorkOffTheKeyPath()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
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

        private static void SentenceAutoCommitStaysOffTheKeyPath()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
            True(state.TrySetConfigValue("整句自动提前上屏", "是", out _, out string commitReason),
                nameof(SentenceAutoCommitStaysOffTheKeyPath) + ": " + commitReason);
            var decoder = new SentenceInputDecoder(
                SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                {
                    ["abc"] = new List<string> { "甲乙" },
                    ["de"] = new List<string> { "丙" },
                    ["def"] = new List<string> { "丁" },
                    ["defg"] = new List<string> { "戊" }
                }),
                new SlowSentenceLanguageModel(),
                beamWidth: 100);
            var engine = new InputMethodEngine(state, decoder, sentenceDecodeSynchronously: false);
            using (var completed = new ManualResetEvent(false))
            {
                engine.SetSentenceDecodeCompletedCallback(() => completed.Set());
                TypeLetters(engine, "abcde");
                True(completed.WaitOne(5000), nameof(SentenceAutoCommitStaysOffTheKeyPath) + ".five_ready");
                while (engine.IsSentenceDecodePending)
                {
                    Thread.Sleep(5);
                }

                completed.Reset();
                var stopwatch = Stopwatch.StartNew();
                KeyEngineResult sixth = Press(engine, 0x46);
                stopwatch.Stop();
                True(stopwatch.ElapsedMilliseconds < 60,
                    nameof(SentenceAutoCommitStaysOffTheKeyPath) + ".sixth_latency");
                Equal(null, sixth.TextToOutput,
                    nameof(SentenceAutoCommitStaysOffTheKeyPath) + ".first_evidence");
                True(completed.WaitOne(5000), nameof(SentenceAutoCommitStaysOffTheKeyPath) + ".six_ready");
                while (engine.IsSentenceDecodePending)
                {
                    Thread.Sleep(5);
                }

                completed.Reset();
                stopwatch.Restart();
                KeyEngineResult seventh = Press(engine, 0x47);
                stopwatch.Stop();
                True(stopwatch.ElapsedMilliseconds < 60,
                    nameof(SentenceAutoCommitStaysOffTheKeyPath) + ".seventh_latency");
                Equal("甲乙", seventh.TextToOutput,
                    nameof(SentenceAutoCommitStaysOffTheKeyPath) + ".commit");
                True(completed.WaitOne(5000), nameof(SentenceAutoCommitStaysOffTheKeyPath) + ".seven_ready");
                while (engine.IsSentenceDecodePending)
                {
                    Thread.Sleep(5);
                }
            }
        }

        private static void SentenceEngineKeepsPreviousSegmentationWhileDecodeIsPending()
        {
            var state = new CoreRuntimeState();
            EnableSentenceMode(state);
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
            EnableSentenceMode(state);
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

        private sealed class PrefersAfternoonOverCaveSentenceLanguageModel : ISentenceLanguageModel
        {
            public double LogProbability(string previous2, string previous1, string target)
            {
                return string.Equals(target, "丁", StringComparison.Ordinal) ? -100.0 : 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
            }
        }

        private sealed class PrefersIncompleteTailSentenceLanguageModel : ISentenceLanguageModel
        {
            public int EosCalls { get; private set; }

            public double LogProbability(string previous2, string previous1, string target)
            {
                if (string.Equals(target, "\x03", StringComparison.Ordinal))
                {
                    EosCalls++;
                }
                return string.Equals(target, "甲", StringComparison.Ordinal) ||
                       string.Equals(target, "乙", StringComparison.Ordinal) ||
                       string.Equals(target, "丙", StringComparison.Ordinal) ||
                       string.Equals(target, "辛", StringComparison.Ordinal)
                    ? 0.0
                    : -10.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
            }
        }

        private sealed class WeakAlternativeSentenceLanguageModel : ISentenceLanguageModel
        {
            public double LogProbability(string previous2, string previous1, string target)
            {
                return string.Equals(target, "丁", StringComparison.Ordinal) ? -8.0 : 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
            }
        }

        private sealed class BoundaryAlternativeSentenceLanguageModel : ISentenceLanguageModel
        {
            private readonly double _alternativePenalty;

            public BoundaryAlternativeSentenceLanguageModel(double alternativePenalty)
            {
                _alternativePenalty = alternativePenalty;
            }

            public double LogProbability(string previous2, string previous1, string target)
            {
                return string.Equals(target, "丁", StringComparison.Ordinal)
                    ? _alternativePenalty
                    : 0.0;
            }

            public bool HasObservedBigram(string previous, string target)
            {
                return false;
            }
        }

        private sealed class LowConfidenceGapSentenceLanguageModel : ISentenceLanguageModel
        {
            public double LogProbability(string previous2, string previous1, string target)
            {
                if (string.Equals(target, "庚", StringComparison.Ordinal) &&
                    string.Equals(previous1, "乙", StringComparison.Ordinal))
                {
                    return -40.0;
                }
                return string.Equals(target, "丁", StringComparison.Ordinal) ||
                       string.Equals(target, "己", StringComparison.Ordinal)
                    ? -20.0
                    : 0.0;
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
