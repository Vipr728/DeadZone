using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PlatformerPlaytest.Solver;

namespace PlatformerPlaytest.Tests.EditMode
{
    /// <summary>T12: SolutionCache key derivation and disk round-trip. The golden values below pin the FNV-1a
    /// construction (see SolutionCache.MakeKey) so a future accidental switch back to string.GetHashCode(), or any
    /// other change to key derivation, fails loudly instead of silently missing the cache forever.</summary>
    public class SolutionCacheEditModeTests
    {
        static SolverConfig SampleSceneConfig => new SolverConfig
        {
            BeamWidth = 20,
            MaxMacrosDepth = 50,
            Seed = 0,
            TickMenu = new[] { 4, 8, 16, 32 }
        };

        [Test]
        public void MakeKey_KnownInput_MatchesGoldenValue()
        {
            string key = SolutionCache.MakeKey("sample-scene", SampleSceneConfig);
            Assert.AreEqual("sample-scene-a9b72605459a7236", key);
        }

        [Test]
        public void MakeKey_DifferentBeamWidth_DifferentKey()
        {
            SolverConfig config = SampleSceneConfig;
            config.BeamWidth = 21;
            string key = SolutionCache.MakeKey("sample-scene", config);
            Assert.AreEqual("sample-scene-d59a46bbcfc038d7", key);
            Assert.AreNotEqual(SolutionCache.MakeKey("sample-scene", SampleSceneConfig), key);
        }

        [Test]
        public void MakeKey_IsDeterministicAcrossCalls()
        {
            string a = SolutionCache.MakeKey("sample-scene", SampleSceneConfig);
            string b = SolutionCache.MakeKey("sample-scene", SampleSceneConfig);
            Assert.AreEqual(a, b);
        }

        [Test]
        public void RoundTrip_SaveThenLoad_EqualStream()
        {
            string key = "edit-mode-roundtrip-test";
            SolutionCache.Clear(key);
            try
            {
                List<PlayerAction> stream = new List<PlayerAction>
                {
                    PlayerAction.Neutral,
                    new PlayerAction { MoveX = 1f, JumpPressed = true },
                    new PlayerAction { MoveX = -1f, DashPressed = true }
                };

                SolutionCache.Save(key, stream);
                bool hit = SolutionCache.TryLoad(key, out List<PlayerAction> loaded);

                Assert.IsTrue(hit);
                Assert.AreEqual(stream.Count, loaded.Count);
                for (int i = 0; i < stream.Count; i++)
                {
                    Assert.AreEqual(stream[i].MoveX, loaded[i].MoveX);
                    Assert.AreEqual(stream[i].JumpPressed, loaded[i].JumpPressed);
                    Assert.AreEqual(stream[i].DashPressed, loaded[i].DashPressed);
                }
            }
            finally
            {
                SolutionCache.Clear(key);
            }
        }

        [Test]
        public void TryLoad_MissingKey_ReturnsFalse()
        {
            bool hit = SolutionCache.TryLoad("no-such-cache-entry-ever", out List<PlayerAction> stream);
            Assert.IsFalse(hit);
            Assert.IsNull(stream);
        }

        [Test]
        public void Save_WritesUnderLibrary_NeverUnderAssets()
        {
            string key = "edit-mode-path-test";
            SolutionCache.Clear(key);
            try
            {
                SolutionCache.Save(key, new List<PlayerAction> { PlayerAction.Neutral });
                string expectedDir = Path.Combine(Directory.GetCurrentDirectory(), "Library", "PlatformerPlaytest", "solutions");
                string path = Path.Combine(expectedDir, key + ".actions.jsonl");
                Assert.IsTrue(File.Exists(path), $"expected cache file at {path}");
                Assert.IsFalse(path.Replace('\\', '/').Contains("/Assets/"));
            }
            finally
            {
                SolutionCache.Clear(key);
            }
        }
    }
}
