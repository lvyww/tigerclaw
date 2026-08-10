using System;
using System.Collections.Generic;
using System.Text;

namespace TigerClaw.Core
{
    internal interface IMixedInputDecoder
    {
        MixedInputDecodeResult Decode(MixedInputDecodeRequest request);

        void ClearCache();
    }

    internal sealed class MixedInputDecodeRequest
    {
        public string RawCode { get; set; }
        public int MaxCodeLength { get; set; }
        public int LexiconVersion { get; set; }
        public IReadOnlyDictionary<int, string> PreferredCandidateTextByStart { get; set; }
    }

    internal sealed class MixedInputDecodeSegment
    {
        public int Start { get; set; }
        public string Code { get; set; }
        public string CandidateText { get; set; }

        public bool HasCandidate => !string.IsNullOrEmpty(CandidateText);
    }

    internal sealed class MixedInputDecodeResult
    {
        public static readonly MixedInputDecodeResult Empty = new MixedInputDecodeResult
        {
            RawCode = string.Empty,
            Segments = Array.Empty<MixedInputDecodeSegment>(),
            ResolvedPrefixText = string.Empty,
            ActiveCode = string.Empty,
            SurfaceText = string.Empty
        };

        public string RawCode { get; set; }
        public MixedInputDecodeSegment[] Segments { get; set; }
        public string ResolvedPrefixText { get; set; }
        public string ActiveCode { get; set; }
        public string SurfaceText { get; set; }
    }

    internal static class MixedInputCommitComposer
    {
        public static string ComposeChinese(MixedInputDecodeResult decodeResult, string activeOutput, string suffix = null)
        {
            string prefix = decodeResult?.ResolvedPrefixText ?? string.Empty;
            return prefix + (activeOutput ?? string.Empty) + (suffix ?? string.Empty);
        }

        public static string ComposeRaw(MixedInputDecodeResult decodeResult)
        {
            return decodeResult?.RawCode ?? string.Empty;
        }
    }

    /// <summary>
    /// Version-one full-string decoder. Every call derives the complete surface from RawCode.
    /// Its fixed-length segmentation can later be replaced by a sentence/LM decoder without
    /// changing the engine's authoritative raw-code state.
    /// </summary>
    internal sealed class FixedLengthMixedInputDecoder : IMixedInputDecoder
    {
        private readonly Func<string, List<string>> _candidateResolver;
        private readonly Func<string, string> _candidateOutputResolver;
        private readonly Dictionary<string, string> _firstCandidateCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int _cachedLexiconVersion = -1;

        public FixedLengthMixedInputDecoder(
            Func<string, List<string>> candidateResolver,
            Func<string, string> candidateOutputResolver)
        {
            _candidateResolver = candidateResolver ?? throw new ArgumentNullException(nameof(candidateResolver));
            _candidateOutputResolver = candidateOutputResolver ?? throw new ArgumentNullException(nameof(candidateOutputResolver));
        }

        public MixedInputDecodeResult Decode(MixedInputDecodeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string rawCode = request.RawCode ?? string.Empty;
            int maxCodeLength = Math.Max(1, request.MaxCodeLength);
            EnsureLexiconVersion(request.LexiconVersion);

            if (rawCode.Length == 0)
            {
                return MixedInputDecodeResult.Empty;
            }

            // A segment becomes provisional only after the next character arrives.
            // Therefore N == M remains one active segment; N == M + 1 seals the first M chars.
            int completedSegmentCount = (rawCode.Length - 1) / maxCodeLength;
            var segments = new MixedInputDecodeSegment[completedSegmentCount];
            var resolvedPrefixText = new StringBuilder(completedSegmentCount * maxCodeLength);

            for (int i = 0; i < completedSegmentCount; i++)
            {
                int start = i * maxCodeLength;
                string code = rawCode.Substring(start, maxCodeLength);
                string candidateText = ResolvePreferredCandidate(request.PreferredCandidateTextByStart, start);
                if (string.IsNullOrEmpty(candidateText))
                {
                    candidateText = ResolveFirstCandidate(code);
                }

                segments[i] = new MixedInputDecodeSegment
                {
                    Start = start,
                    Code = code,
                    CandidateText = candidateText ?? string.Empty
                };

                // A completed segment without a candidate remains visible and is committed as
                // its original English code. Candidate segments use their resolved output text.
                resolvedPrefixText.Append(!string.IsNullOrEmpty(candidateText) ? candidateText : code);
            }

            int activeStart = completedSegmentCount * maxCodeLength;
            string activeCode = rawCode.Substring(activeStart);
            string prefix = resolvedPrefixText.ToString();
            return new MixedInputDecodeResult
            {
                RawCode = rawCode,
                Segments = segments,
                ResolvedPrefixText = prefix,
                ActiveCode = activeCode,
                SurfaceText = prefix + activeCode
            };
        }

        public void ClearCache()
        {
            _firstCandidateCache.Clear();
            _cachedLexiconVersion = -1;
        }

        private void EnsureLexiconVersion(int lexiconVersion)
        {
            if (_cachedLexiconVersion == lexiconVersion)
            {
                return;
            }

            _firstCandidateCache.Clear();
            _cachedLexiconVersion = lexiconVersion;
        }

        private string ResolveFirstCandidate(string code)
        {
            if (_firstCandidateCache.TryGetValue(code, out string cached))
            {
                return cached;
            }

            string candidateText = string.Empty;
            List<string> candidates = _candidateResolver(code);
            if (candidates != null && candidates.Count > 0 && !string.IsNullOrEmpty(candidates[0]))
            {
                candidateText = _candidateOutputResolver(candidates[0]) ?? string.Empty;
            }

            _firstCandidateCache[code] = candidateText;
            return candidateText;
        }

        private static string ResolvePreferredCandidate(
            IReadOnlyDictionary<int, string> preferredCandidateTextByStart,
            int start)
        {
            if (preferredCandidateTextByStart != null &&
                preferredCandidateTextByStart.TryGetValue(start, out string candidateText))
            {
                return candidateText ?? string.Empty;
            }

            return string.Empty;
        }
    }
}
