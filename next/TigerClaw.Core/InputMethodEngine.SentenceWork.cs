using System;
using System.Threading;

namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        private CancellationTokenSource _sentenceDecodeCancellation;
        private bool _engineDisposed;

        // Called under Engine's lock. Cancellation never waits for the Beam lock.
        private void CancelSentenceWork()
        {
            _sentenceDecodeCancellation?.Cancel();
            (_sentenceRerankService as ICancelableSentenceRerankService)?.CancelPending();
        }

        private void ReplaceSentenceDecoder(SentenceInputDecoder replacement)
        {
            CancelSentenceWork();
            var previous = _sentenceInputDecoder;
            _sentenceInputDecoder = replacement;
            // Raw text and candidate count can be identical across resource
            // replacement. Retire the generation as well as the decoder so a
            // late Qwen response cannot rerank the replacement's candidates.
            if (!ReferenceEquals(previous, replacement) &&
                _compositionState == CompositionState.CnSentence && _sentenceRawBuffer.Length > 0)
            {
                _sentenceGeneration++;
            }
            _sentenceNeuralAcceptedRaw = _sentenceNeuralTopText = string.Empty;
            ResetSentenceAutoCommitEvidence();
            ResetSentenceEmptyCodePending();
            _sentenceAppliedGeneration = -1;
            _sentenceResultLexiconVersion = -1;
            if (!_sentenceDecoderExternallyProvided) previous?.Dispose();
        }

        private void RunSentenceDecodeWorker()
        {
            while (true)
            {
                long generation;
                string raw, required;
                int version;
                bool evidence;
                SentenceLockedPrefix locked;
                SentenceInputDecoder decoder;
                CancellationTokenSource cancellation;
                lock (_lock)
                {
                    if (_engineDisposed || _compositionState != CompositionState.CnSentence ||
                        _sentenceRawBuffer.Length == 0 ||
                        (_sentenceAppliedGeneration == _sentenceGeneration &&
                         _sentenceResultLexiconVersion == _state.LexiconVersion))
                    {
                        _sentenceDecodeWorkerRunning = false;
                        return;
                    }
                    generation = _sentenceGeneration;
                    raw = _sentenceRawBuffer.ToString();
                    version = _state.LexiconVersion;
                    decoder = _sentenceInputDecoder;
                    required = _sentenceCommittedText;
                    locked = ActiveSentenceLockedPrefix;
                    evidence = ShouldCollectSentenceCommitEvidence();
                    cancellation = new CancellationTokenSource();
                    _sentenceDecodeCancellation = cancellation;
                }
                SentenceDecodeResult result;
                bool canceled = false;
                try
                {
                    result = decoder?.Decode(raw, 20, evidence, required, locked, cancellation.Token)
                        ?? SentenceDecodeResult.Empty;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    result = SentenceDecodeResult.Empty;
                }
                catch (ObjectDisposedException)
                {
                    canceled = true;
                    result = SentenceDecodeResult.Empty;
                }
                catch { result = SentenceDecodeResult.Empty; }
                Action completed = null;
                bool finished;
                lock (_lock)
                {
                    canceled |= cancellation.IsCancellationRequested;
                    if (ReferenceEquals(_sentenceDecodeCancellation, cancellation))
                        _sentenceDecodeCancellation = null;
                    cancellation.Dispose();
                    if (!canceled && !_engineDisposed && ReferenceEquals(decoder, _sentenceInputDecoder) &&
                        _compositionState == CompositionState.CnSentence && generation == _sentenceGeneration &&
                        raw == _sentenceRawBuffer.ToString() && version == _state.LexiconVersion)
                    {
                        ApplySentenceDecodeResult(generation, raw, version, result);
                        completed = _sentenceDecodeCompletedCallback;
                    }
                    finished = _engineDisposed || _compositionState != CompositionState.CnSentence ||
                        (_sentenceAppliedGeneration == _sentenceGeneration && _sentenceResultLexiconVersion == _state.LexiconVersion) ||
                        (!canceled && generation == _sentenceGeneration && ReferenceEquals(decoder, _sentenceInputDecoder));
                    if (finished) _sentenceDecodeWorkerRunning = false;
                }
                try { completed?.Invoke(); }
                catch { }
                if (finished) return;
            }
        }
    }
}
