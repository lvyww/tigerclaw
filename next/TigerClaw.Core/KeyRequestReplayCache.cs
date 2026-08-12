using System;
using System.Collections.Generic;

namespace TigerClaw.Core
{
    internal sealed class KeyRequestReplayCache
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _responses = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> _insertionOrder = new Queue<string>();
        private readonly int _capacity;

        public KeyRequestReplayCache(int capacity = 512)
        {
            _capacity = Math.Max(16, capacity);
        }

        public static string BuildKey(string clientSession, string eventId)
        {
            if (string.IsNullOrWhiteSpace(clientSession) || string.IsNullOrWhiteSpace(eventId))
            {
                return null;
            }

            return clientSession + "\n" + eventId;
        }

        public bool TryGet(string key, int responseSeq, out string response)
        {
            response = null;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            lock (_lock)
            {
                if (!_responses.TryGetValue(key, out string cached))
                {
                    return false;
                }

                response = RewriteResponseSeq(cached, responseSeq);
                return true;
            }
        }

        public void Store(string key, string response)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(response))
            {
                return;
            }

            lock (_lock)
            {
                if (_responses.ContainsKey(key))
                {
                    _responses[key] = response;
                    return;
                }

                _responses[key] = response;
                _insertionOrder.Enqueue(key);
                while (_responses.Count > _capacity && _insertionOrder.Count > 0)
                {
                    _responses.Remove(_insertionOrder.Dequeue());
                }
            }
        }

        private static string RewriteResponseSeq(string response, int responseSeq)
        {
            const string marker = "\"seq\":";
            int valueStart = response.IndexOf(marker, StringComparison.Ordinal);
            if (valueStart < 0)
            {
                return response;
            }

            valueStart += marker.Length;
            int valueEnd = response.IndexOf(',', valueStart);
            if (valueEnd < 0)
            {
                valueEnd = response.IndexOf('}', valueStart);
            }
            if (valueEnd < 0)
            {
                return response;
            }

            return response.Substring(0, valueStart) + responseSeq + response.Substring(valueEnd);
        }
    }
}
