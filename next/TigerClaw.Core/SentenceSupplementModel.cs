using System;
using System.Collections.Generic;
using System.Globalization;

namespace TigerClaw.Core
{
    internal sealed class SentenceSupplementEntry
    {
        private const double BaselineReward = 9.0;
        private const double WeightScale = 2.0;
        private const double BaselineWeight = 1000.0;
        private const double MaximumReward = 16.0;

        public string Text { get; set; }
        public long Weight { get; set; }
        public double Reward { get; set; }

        public static SentenceSupplementEntry Create(string text, long weight)
        {
            long boundedWeight = Math.Max(1L, Math.Min(1000000000L, weight));
            double reward = BaselineReward + WeightScale * Math.Log(boundedWeight / BaselineWeight);
            return new SentenceSupplementEntry
            {
                Text = text ?? string.Empty,
                Weight = boundedWeight,
                Reward = Math.Max(0.0, Math.Min(MaximumReward, reward))
            };
        }

        public override string ToString()
        {
            return Text + " " + Weight.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal sealed class SentenceSupplementMatcher
    {
        private sealed class BuildNode
        {
            public readonly Dictionary<string, int> Transitions =
                new Dictionary<string, int>(StringComparer.Ordinal);
            public int Failure;
            public double Reward;
        }

        private sealed class Node
        {
            public Dictionary<string, int> Transitions;
            public int Failure;
            public double Reward;
        }

        public static readonly SentenceSupplementMatcher Empty =
            new SentenceSupplementMatcher(new[]
            {
                new Node
                {
                    Transitions = new Dictionary<string, int>(StringComparer.Ordinal)
                }
            });

        private readonly Node[] _nodes;

        private SentenceSupplementMatcher(Node[] nodes)
        {
            _nodes = nodes;
        }

        public bool IsEmpty => _nodes.Length <= 1;

        public static SentenceSupplementMatcher Build(IEnumerable<SentenceSupplementEntry> entries)
        {
            var nodes = new List<BuildNode> { new BuildNode() };
            if (entries != null)
            {
                foreach (SentenceSupplementEntry entry in entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Text) || entry.Reward <= 0.0)
                    {
                        continue;
                    }

                    int state = 0;
                    TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(entry.Text);
                    while (elements.MoveNext())
                    {
                        string element = elements.GetTextElement();
                        if (!nodes[state].Transitions.TryGetValue(element, out int next))
                        {
                            next = nodes.Count;
                            nodes[state].Transitions[element] = next;
                            nodes.Add(new BuildNode());
                        }
                        state = next;
                    }

                    nodes[state].Reward = Math.Max(nodes[state].Reward, entry.Reward);
                }
            }

            if (nodes.Count == 1)
            {
                return Empty;
            }

            var queue = new Queue<int>();
            foreach (int child in nodes[0].Transitions.Values)
            {
                nodes[child].Failure = 0;
                queue.Enqueue(child);
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (KeyValuePair<string, int> transition in nodes[current].Transitions)
                {
                    int fallback = nodes[current].Failure;
                    while (fallback != 0 && !nodes[fallback].Transitions.ContainsKey(transition.Key))
                    {
                        fallback = nodes[fallback].Failure;
                    }

                    if (nodes[fallback].Transitions.TryGetValue(transition.Key, out int failureTarget) &&
                        failureTarget != transition.Value)
                    {
                        nodes[transition.Value].Failure = failureTarget;
                    }
                    else
                    {
                        nodes[transition.Value].Failure = 0;
                    }

                    nodes[transition.Value].Reward = Math.Max(
                        nodes[transition.Value].Reward,
                        nodes[nodes[transition.Value].Failure].Reward);
                    queue.Enqueue(transition.Value);
                }
            }

            var frozen = new Node[nodes.Count];
            for (int index = 0; index < nodes.Count; index++)
            {
                frozen[index] = new Node
                {
                    Transitions = nodes[index].Transitions,
                    Failure = nodes[index].Failure,
                    Reward = nodes[index].Reward
                };
            }
            return new SentenceSupplementMatcher(frozen);
        }

        public int Advance(int state, string textElement, out double reward)
        {
            if (IsEmpty || string.IsNullOrEmpty(textElement))
            {
                reward = 0.0;
                return 0;
            }

            int current = state >= 0 && state < _nodes.Length ? state : 0;
            while (current != 0 && !_nodes[current].Transitions.ContainsKey(textElement))
            {
                current = _nodes[current].Failure;
            }

            if (_nodes[current].Transitions.TryGetValue(textElement, out int next))
            {
                current = next;
            }

            reward = _nodes[current].Reward;
            return current;
        }
    }
}
