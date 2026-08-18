namespace TigerClaw.Shared
{
    public static class RuntimeConstants
    {
        public const string ProductName = "TigerClaw";

        public const string CoreProcessName = "TigerClaw.Core";
        public const string OverlayProcessName = "TigerClaw.Overlay";
        public const string DialogProcessName = "TigerClaw.Dialog";
        public const string SentenceProcessName = "TigerClaw.Sentence";
        public const string HookNativeProcessName = "TigerClaw.Hook.Native";
        public const string HookNativePublishedProcessName = "TigerClaw";

        public const string TsfPipeShortName = "BimeIPC";
        public const string TsfPipeName = @"\\.\pipe\BimeIPC";
        public const string SentencePipeShortName = "TigerClaw.Sentence.v1";
        public const string SentencePipeName = @"\\.\pipe\TigerClaw.Sentence.v1";

        public const string UiStateMmfName = @"Local\TigerClaw.UiState.v1";
        public const string HeartbeatMmfName = @"Local\TigerClaw.Heartbeat.v1";
        public const string OverlayHeartbeatMmfName = @"Local\TigerClaw.OverlayHeartbeat.v1";
        public const string ShowMenuEventName = @"Local\TigerClaw.ShowMenu.v1";
        public const string HookNativeExitEventName = @"Local\TigerClaw.Hook.Native.Exit.v1";
    }
}
