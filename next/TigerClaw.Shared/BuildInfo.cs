namespace TigerClaw.Shared
{
    public static class BuildInfo
    {
        public const string VersionLabel = "2026.08.14-1852";
        public const string Commit = "bb814739";
        public const string BuildUtc = "2026-08-14T18:52:38Z";
        public const string TrialExpireUtc = "2027-01-01T16:00:00Z";

        internal static byte[] GetModelProtectionKey()
        {
            return new byte[32];
        }
    }
}