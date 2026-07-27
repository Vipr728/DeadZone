using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PlatformerPlaytest.Tests.EditMode
{
    public class StateHashTests
    {
        [Test]
        public void GoldenVector_MatchesKnownHash()
        {
            ulong h = StateHash.Compute(new Vector2(1f, 2f), new Vector2(3f, 4f), 5, 1);
            Assert.AreEqual(18071725713166954657UL, h);
        }

        [Test]
        public void ZeroState_MatchesKnownHash()
        {
            ulong h = StateHash.Compute(Vector2.zero, Vector2.zero, 0, 0);
            Assert.AreEqual(11573569732163983077UL, h);
        }

        [Test]
        public void Deterministic_SameInputSameHash()
        {
            ulong a = StateHash.Compute(new Vector2(1.2345f, -6.789f), new Vector2(0.5f, -0.5f), 3, 2);
            ulong b = StateHash.Compute(new Vector2(1.2345f, -6.789f), new Vector2(0.5f, -0.5f), 3, 2);
            Assert.AreEqual(a, b);
        }

        [Test]
        public void QuantizationBoundary_BelowGridHashesEqual()
        {
            // Both round to 12345 on the 1e-4 grid.
            ulong a = StateHash.Compute(new Vector2(1.23453f, 0f), Vector2.zero, 0, 0);
            ulong b = StateHash.Compute(new Vector2(1.23448f, 0f), Vector2.zero, 0, 0);
            Assert.AreEqual(a, b);
        }

        [Test]
        public void QuantizationBoundary_AcrossGridHashesDiffer()
        {
            ulong a = StateHash.Compute(new Vector2(1.23455f, 0f), Vector2.zero, 0, 0); // -> 12346 (away-from-zero)
            ulong b = StateHash.Compute(new Vector2(1.23445f, 0f), Vector2.zero, 0, 0); // -> 12344/12345
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void NaNAndInfinity_GuardToZero()
        {
            ulong nan = StateHash.Compute(new Vector2(float.NaN, float.PositiveInfinity), new Vector2(float.NegativeInfinity, 0f), 0, 0);
            ulong zero = StateHash.Compute(Vector2.zero, Vector2.zero, 0, 0);
            Assert.AreEqual(zero, nan);
        }

        [Test]
        public void SignedZero_HashesEqual()
        {
            Assert.AreEqual(0L, StateHash.Quantize(-0f));
            ulong neg = StateHash.Compute(new Vector2(-0f, -0f), new Vector2(-0f, -0f), 0, 0);
            ulong pos = StateHash.Compute(Vector2.zero, Vector2.zero, 0, 0);
            Assert.AreEqual(pos, neg);
        }
    }

    public class ActionStreamIOTests
    {
        static void AssertRoundTrip(PlayerAction a)
        {
            string line = ActionStreamIO.WriteLine(7, in a);
            PlayerAction parsed = ActionStreamIO.ParseLine(line, out int tick);
            Assert.AreEqual(7, tick, line);
            Assert.AreEqual(a.MoveX, parsed.MoveX, line);
            Assert.AreEqual(a.MoveY, parsed.MoveY, line);
            Assert.AreEqual(a.JumpPressed, parsed.JumpPressed, line);
            Assert.AreEqual(a.JumpHeld, parsed.JumpHeld, line);
            Assert.AreEqual(a.DashPressed, parsed.DashPressed, line);
            Assert.AreEqual(a.ClimbHeld, parsed.ClimbHeld, line);
        }

        [Test]
        public void RoundTrip_NeutralAndFullPress()
        {
            AssertRoundTrip(PlayerAction.Neutral);
            AssertRoundTrip(new PlayerAction { MoveX = 1f, MoveY = -1f, JumpPressed = true, JumpHeld = true, DashPressed = true, ClimbHeld = true });
        }

        [Test]
        public void RoundTrip_NegativeAndFractionalFloats_ExactEquality()
        {
            AssertRoundTrip(new PlayerAction { MoveX = -0.3333333f, MoveY = 0.6666667f });
            AssertRoundTrip(new PlayerAction { MoveX = -1.2345678e-3f, MoveY = 987.654f });
            AssertRoundTrip(new PlayerAction { MoveX = float.Epsilon, MoveY = -float.MaxValue });
        }

        [Test]
        public void ParseIndexed_FillsGapsWithNeutral()
        {
            List<string> lines = new List<string>
            {
                ActionStreamIO.WriteLine(0, new PlayerAction { MoveX = 1f }),
                ActionStreamIO.WriteLine(2, new PlayerAction { JumpPressed = true })
            };
            List<PlayerAction> byTick = ActionStreamIO.ParseIndexed(lines);
            Assert.AreEqual(3, byTick.Count);
            Assert.AreEqual(1f, byTick[0].MoveX);
            Assert.AreEqual(0f, byTick[1].MoveX);
            Assert.IsFalse(byTick[1].JumpPressed);
            Assert.IsTrue(byTick[2].JumpPressed);
        }
    }

    public class PlaytestPathsTests
    {
        [Test]
        public void RunDir_UnderLibrary_NotUnderAssets()
        {
            string dir = PlaytestPaths.RunDir("edit-test-run");
            string full = System.IO.Path.GetFullPath(dir).Replace('\\', '/');
            StringAssert.Contains("/Library/PlatformerPlaytest/", full);
            Assert.IsFalse(full.Contains("/Assets/"), full);
        }

        [Test]
        public void Guard_ThrowsWhenUnderAssets()
        {
            Assert.Throws<System.InvalidOperationException>(
                () => PlaytestPaths.GuardNotUnderAssets("/some/project/Assets/whatever"));
        }
    }

    public class SchemaRoundTripTests
    {
        [Test]
        public void RunHeader_JsonUtilityRoundTrip()
        {
            RunHeader h = new RunHeader
            {
                runId = "r1", createdUtc = "2026-07-23T00:00:00Z", scenarioId = "s1",
                unityVersion = "6000.3.6f1", packageVersion = "0.1.0",
                agentVersions = new[] { "scripted@1" }, fixedDeltaTime = 0.02f,
                seeds = new[] { 1, 2, 3 }, profileIds = new[] { "default" }, settingsHash = "abc123"
            };
            RunHeader back = JsonUtility.FromJson<RunHeader>(JsonUtility.ToJson(h));
            Assert.AreEqual(h.runId, back.runId);
            Assert.AreEqual(h.fixedDeltaTime, back.fixedDeltaTime);
            Assert.AreEqual(h.seeds, back.seeds);
            Assert.AreEqual(h.agentVersions, back.agentVersions);
            Assert.AreEqual(h.settingsHash, back.settingsHash);
        }

        [Test]
        public void EpisodeSummary_JsonUtilityRoundTrip()
        {
            EpisodeSummary e = new EpisodeSummary
            {
                episodeId = "e1", scenarioId = "s1", agentId = "scripted", profileId = "default",
                seed = 42, outcome = "Completed", steps = 123, deaths = 2,
                furthestProgress = 0.87f, completionTick = 120, hasFullTrajectory = true
            };
            EpisodeSummary back = JsonUtility.FromJson<EpisodeSummary>(JsonUtility.ToJson(e));
            Assert.AreEqual(e.episodeId, back.episodeId);
            Assert.AreEqual(e.outcome, back.outcome);
            Assert.AreEqual(e.steps, back.steps);
            Assert.AreEqual(e.furthestProgress, back.furthestProgress);
            Assert.AreEqual(e.hasFullTrajectory, back.hasFullTrajectory);
        }

        // ---- T14: cross-process replay tolerance -------------------------------------------------------------

        static List<KeyframeRecord> OneKeyframe(float px)
        {
            Observation o = new Observation { Position = new Vector2(px, 1f), Velocity = Vector2.zero, DashesRemaining = 1 };
            return new List<KeyframeRecord>
            {
                new KeyframeRecord
                {
                    t = 0, px = px, py = 1f, vx = 0f, vy = 0f,
                    flags = StateFlags.From(o), dashes = 1,
                    stateHash = StateHash.Compute(o.Position, o.Velocity, StateFlags.From(o), 1)
                }
            };
        }

        static DesyncReport Verify(List<KeyframeRecord> recordedFrames, float livePx, float tolerance)
        {
            ReplayVerifier v = new ReplayVerifier(recordedFrames, tolerance);
            v.OnStep(0, new Observation { Position = new Vector2(livePx, 1f), Velocity = Vector2.zero, DashesRemaining = 1 });
            return v.Report;
        }

        [Test]
        public void ReplayVerifier_ZeroTolerance_IsExactOnQuantizationGrid()
        {
            Assert.AreEqual(-1, Verify(OneKeyframe(5f), 5f, 0f).FirstDesyncTick, "identical state must not desync");
            Assert.AreEqual(0, Verify(OneKeyframe(5f), 5.01f, 0f).FirstDesyncTick,
                "0.01-unit drift must desync at the default exact tolerance");
        }

        [Test]
        public void ReplayVerifier_CrossProcessTolerance_AbsorbsSubSlopDrift()
        {
            // 0.0125 = the measured worst-case cross-process divergence on SampleScene over 400 ticks (T14).
            Assert.AreEqual(-1, Verify(OneKeyframe(5f), 5.0125f, ReplayVerifier.CrossProcessTolerance).FirstDesyncTick,
                "measured cross-process drift must be absorbed by CrossProcessTolerance");
            Assert.AreEqual(0, Verify(OneKeyframe(5f), 6f, ReplayVerifier.CrossProcessTolerance).FirstDesyncTick,
                "a whole-tile divergence must still be reported as desync");
        }

        [Test]
        public void ReplayVerifier_Tolerance_StillRequiresExactFlagsAndDashes()
        {
            List<KeyframeRecord> frames = OneKeyframe(5f);
            ReplayVerifier v = new ReplayVerifier(frames, ReplayVerifier.CrossProcessTolerance);
            v.OnStep(0, new Observation { Position = new Vector2(5f, 1f), Velocity = Vector2.zero, DashesRemaining = 0 });
            Assert.AreEqual(0, v.Report.FirstDesyncTick, "a dash-count mismatch must desync regardless of tolerance");
        }
    }
}
