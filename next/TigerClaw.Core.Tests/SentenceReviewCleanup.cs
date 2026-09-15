using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void ReviewCleanupDirectory(string folder)
        {
            if (!Directory.Exists(folder)) return;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try { Directory.Delete(folder, true); return; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    if (attempt < 5) { Thread.Sleep(100 << attempt); continue; }
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { phase = "cleanup", status = "warning",
                        directory = folder, error = error.Message, test_result_unchanged = true }));
                }
            }
        }
    }
}
