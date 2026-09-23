using System;

namespace VibeDesk.Update
{
    public static class AppVersion
    {
        public const string Current = "1.2.5";
        public static string FullTitle => $"v{Current}";

        /// <summary>
        /// Compares remote version tag (e.g. "v1.2.0" or "1.2.0") against current version.
        /// Returns true if remote version is strictly newer.
        /// </summary>
        public static bool IsNewer(string? remoteTag, string currentVersion = Current)
        {
            if (string.IsNullOrWhiteSpace(remoteTag)) return false;

            var remote = NormalizeVersion(remoteTag);
            var current = NormalizeVersion(currentVersion);

            if (remote == null || current == null) return false;

            return remote > current;
        }

        public static Version? NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Strip leading 'v' or 'V'
            string cleaned = raw.Trim();
            if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(1).Trim();
            }

            // Remove any git hash or prerelease suffix like "-beta" or "+sha"
            int dashIdx = cleaned.IndexOf('-');
            if (dashIdx > 0)
            {
                cleaned = cleaned.Substring(0, dashIdx);
            }
            int plusIdx = cleaned.IndexOf('+');
            if (plusIdx > 0)
            {
                cleaned = cleaned.Substring(0, plusIdx);
            }

            // Ensure format Major.Minor or Major.Minor.Build
            var parts = cleaned.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int major))
            {
                return new Version(major, 0, 0);
            }
            if (parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out int minor))
            {
                return new Version(major, minor, 0);
            }

            if (Version.TryParse(cleaned, out var v))
            {
                return v;
            }

            return null;
        }
    }
}
