using System;
using System.IO;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Single source of truth for run-output paths (ADR-007). All generated data lives under
    /// <c>Library/PlatformerPlaytest/runs/&lt;runId&gt;/</c>. Library/ is Unity-generated and git-ignored,
    /// so nothing here must ever resolve under Assets/ — that is asserted, not assumed.
    /// </summary>
    public static class PlaytestPaths
    {
        /// <summary>Root of all playtest output: &lt;projectRoot&gt;/Library/PlatformerPlaytest.</summary>
        public static string Root => Path.Combine(Directory.GetCurrentDirectory(), "Library", "PlatformerPlaytest");

        /// <summary>Parent directory for per-run folders.</summary>
        public static string RunsRoot => Path.Combine(Root, "runs");

        /// <summary>Directory for a single run; created if missing. Throws if it would resolve under Assets/.</summary>
        public static string RunDir(string runId)
        {
            if (string.IsNullOrEmpty(runId))
                throw new ArgumentException("runId must be non-empty.", nameof(runId));

            string dir = Path.Combine(RunsRoot, Sanitize(runId));
            GuardNotUnderAssets(dir);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Guard invariant: no playtest output may ever land under an Assets/ folder (ADR-007).</summary>
        public static void GuardNotUnderAssets(string path)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            if (full.Contains("/Assets/") || full.EndsWith("/Assets"))
                throw new InvalidOperationException($"Refusing to write playtest data under Assets/: {full}");
        }

        static string Sanitize(string runId)
        {
            char[] chars = runId.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok)
                    chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
