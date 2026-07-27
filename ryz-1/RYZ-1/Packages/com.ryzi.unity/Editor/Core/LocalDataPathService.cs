using System;
using System.IO;

namespace Ryzi.Editor
{
    public static class LocalDataPathService
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
        public static string Root => Guard(Path.Combine(ProjectRoot, "Library", "Ryzi"));
        public static string RunsRoot => Guard(Path.Combine(Root, "runs"));
        public static string CacheRoot => Guard(Path.Combine(Root, "cache"));
        public static string RecoveryRoot => Guard(Path.Combine(Root, "recovery"));

        public static string CreateRunDirectory(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("A non-empty run ID is required.", nameof(runId));

            string path = Guard(Path.Combine(RunsRoot, Sanitize(runId)));
            Directory.CreateDirectory(path);
            return path;
        }

        public static string EnsureDirectory(string path)
        {
            string safe = Guard(path);
            Directory.CreateDirectory(safe);
            return safe;
        }

        public static string Guard(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A path is required.", nameof(path));

            string full = Path.GetFullPath(path);
            string assets = Path.GetFullPath(UnityEngine.Application.dataPath);
            StringComparison comparison = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(full, assets, comparison) ||
                full.StartsWith(assets + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException($"Ryzi refuses to write generated data under Assets: {full}");
            return full;
        }

        static string Sanitize(string value)
        {
            char[] chars = value.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }
            return new string(chars);
        }
    }
}
