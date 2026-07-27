using System.Collections.Generic;
using System.IO;

namespace PlatformerPlaytest.Solver
{
    /// <summary>
    /// Disk cache for solved action streams, keyed by scenario id + the solver-config fields that change the
    /// answer. Solving SampleScene takes ~116s (see SegmentedSolver); paying that once and reusing the stream is
    /// the point. Storage: Library/PlatformerPlaytest/solutions/&lt;key&gt;.actions.jsonl via ActionStreamIO.
    ///
    /// Callers pass ScenarioConfig.CacheIdentity, which includes discovered spawn/goal/checkpoint geometry and the
    /// procedural layout seed. Solver-policy fields are mixed below. A changed runtime scenario therefore misses
    /// the old cache automatically; the editor's clear action remains available for manual cleanup.
    /// </summary>
    public static class SolutionCache
    {
        static string Dir => Path.Combine(PlaytestPaths.Root, "solutions");

        static string PathFor(string key) => Path.Combine(Dir, key + ".actions.jsonl");

        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        // Stable, process-independent FNV-1a (same construction as Telemetry/StateHash.Mix). string.GetHashCode()
        // is randomized per-process on .NET, so it must never be used for an on-disk cache key.
        static ulong FnvByte(ulong hash, byte b)
        {
            hash ^= b;
            hash *= FnvPrime;
            return hash;
        }

        static ulong MixString(ulong hash, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                hash = FnvByte(hash, (byte)(c & 0xFF));
                hash = FnvByte(hash, (byte)((c >> 8) & 0xFF));
            }
            return hash;
        }

        static ulong MixInt(ulong hash, int v)
        {
            uint u = unchecked((uint)v);
            hash = FnvByte(hash, (byte)(u & 0xFF));
            hash = FnvByte(hash, (byte)((u >> 8) & 0xFF));
            hash = FnvByte(hash, (byte)((u >> 16) & 0xFF));
            hash = FnvByte(hash, (byte)((u >> 24) & 0xFF));
            return hash;
        }

        /// <summary>Stable key from scenario id + the config fields that affect the solved stream. Deterministic
        /// across processes/machines (FNV-1a over explicit bytes) — do NOT swap in string.GetHashCode, which is
        /// randomized per-process on .NET and would silently never hit the cache.</summary>
        public static string MakeKey(string scenarioId, SolverConfig config)
        {
            ulong h = FnvOffset;
            h = MixString(h, scenarioId ?? "");
            h = MixInt(h, config.BeamWidth);
            h = MixInt(h, config.MaxMacrosDepth);
            h = MixInt(h, config.Seed);
            int menuLen = config.TickMenu?.Length ?? 0;
            h = MixInt(h, menuLen);
            for (int i = 0; i < menuLen; i++)
                h = MixInt(h, config.TickMenu[i]);
            return $"{Sanitize(scenarioId)}-{h:x16}";
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "scenario";
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (!((c[i] >= 'a' && c[i] <= 'z') || (c[i] >= 'A' && c[i] <= 'Z') || (c[i] >= '0' && c[i] <= '9') || c[i] == '-' || c[i] == '_'))
                    c[i] = '_';
            return new string(c);
        }

        public static bool TryLoad(string key, out List<PlayerAction> stream)
        {
            string path = PathFor(key);
            if (!File.Exists(path))
            {
                stream = null;
                return false;
            }
            stream = ActionStreamIO.ParseIndexed(File.ReadAllLines(path));
            return true;
        }

        public static void Save(string key, List<PlayerAction> stream)
        {
            PlaytestPaths.GuardNotUnderAssets(Dir);
            Directory.CreateDirectory(Dir);
            System.Text.StringBuilder sb = new System.Text.StringBuilder(stream.Count * 24);
            for (int i = 0; i < stream.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                PlayerAction a = stream[i];
                ActionStreamIO.WriteLine(sb, i, in a);
            }
            File.WriteAllText(PathFor(key), sb.ToString());
        }

        /// <summary>Deletes the cache entry for a key, if any. Returns true if a file was deleted.</summary>
        public static bool Clear(string key)
        {
            string path = PathFor(key);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }

        /// <summary>
        /// Deletes every geometry/config revision for one scenario. This is used only by the editor's explicit
        /// "Clear cached solution" action; runtime discovery otherwise invalidates stale entries automatically.
        /// </summary>
        public static bool ClearScenario(string scenarioId)
        {
            if (!Directory.Exists(Dir))
                return false;

            string prefix = Sanitize(scenarioId);
            bool deleted = false;
            string[] files = Directory.GetFiles(Dir, "*.actions.jsonl");
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(files[i]));
                if (!name.StartsWith(prefix))
                    continue;
                File.Delete(files[i]);
                deleted = true;
            }
            return deleted;
        }
    }
}
