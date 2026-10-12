using System;
using System.Linq;
using System.Text.Json;
using TigerClaw.Overlay;
using TigerClaw.Shared;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        // Uses the actual linked WPF formatter; no windows, IPC or processes.
        private static int RunOverlayFormatProbe()
        {
            var formatter = new CandidateTextFormatter();
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var state = JsonSerializer.Deserialize<OverlayUiState>(root.GetProperty("state").GetRawText());
                var model = formatter.BuildViewModel(state,
                    root.GetProperty("expanded").GetBoolean(), root.GetProperty("annotations").GetBoolean(),
                    root.TryGetProperty("above", out var above) && above.GetBoolean());
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    mode = model.Mode.ToString(), text = model.DisplayText ?? string.Empty,
                    start = model.SelectionStart, length = model.SelectionLength,
                    annotations = model.AnnotationRanges.OrderBy(span => span.Start).Select(span => new[] { span.Start, span.Length }),
                    codeStart = model.CodeStart, codeLength = model.CodeLength
                }));
            }
            return 0;
        }
    }
}
