namespace TigerClaw.Shared
{
    public static class BuildInfo
    {
        public const string VersionLabel = "2026.08.14-1518";
        public const string Commit = "966637b2";
        public const string BuildUtc = "2026-08-14T15:18:51Z";
        public const string TrialExpireUtc = "2027-01-01T16:00:00Z";

        internal static byte[] GetModelProtectionKey()
        {
            return new byte[32];
        }
    }
}