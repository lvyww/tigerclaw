namespace TigerClaw.Shared
{
    public static class BuildInfo
    {
        public const string VersionLabel = "2026.08.14-1546";
        public const string Commit = "85f35ca2";
        public const string BuildUtc = "2026-08-14T15:46:56Z";
        public const string TrialExpireUtc = "2027-01-01T16:00:00Z";

        internal static byte[] GetModelProtectionKey()
        {
            return new byte[32];
        }
    }
}