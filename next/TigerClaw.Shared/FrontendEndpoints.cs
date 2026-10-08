using System;

namespace TigerClaw.Shared
{
    // Only frontend consumers use this override. Production Core/TSF identities
    // remain constants. An invalid explicit override fails closed, never falling
    // back to the daily Core.
    public static class FrontendEndpoints
    {
        private static readonly string TestPipe = ReadTestPipe();
        private static string ReadTestPipe()
        {
            string value = Environment.GetEnvironmentVariable("TIGERCLAW_TEST_PIPE");
            if (value == null) return null;
            if (!value.StartsWith("TigerClaw.Core.Native.Test.", StringComparison.Ordinal) || value.Length > 180)
                throw new InvalidOperationException("Invalid isolated frontend pipe");
            foreach (char c in value)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_'))
                    throw new InvalidOperationException("Invalid isolated frontend pipe");
            return value;
        }
        public static bool IsIsolated => TestPipe != null;
        public static string Pipe => TestPipe ?? RuntimeConstants.TsfPipeShortName;
        private static string Map(string suffix, string normal) => IsIsolated ? @"Local\" + TestPipe + suffix : normal;
        public static string UiState => Map(".UiState.v1", RuntimeConstants.UiStateMmfName);
        public static string Heartbeat => Map(".Heartbeat.v1", RuntimeConstants.HeartbeatMmfName);
        public static string OverlayHeartbeat => Map(".OverlayHeartbeat.v1", RuntimeConstants.OverlayHeartbeatMmfName);
        public static string ShowMenu => Map(".ShowMenu.v1", RuntimeConstants.ShowMenuEventName);
    }
}
