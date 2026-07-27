using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using PlatformerPlaytest.Analysis;

namespace PlatformerPlaytest.Tests.EditMode
{
    public class CounterfactualEditModeTests
    {
        // ---- edge-location + edge-shift pure functions --------------------------------------------------------

        static PlayerAction Move(float x) { PlayerAction a = PlayerAction.Neutral; a.MoveX = x; return a; }
        static PlayerAction JumpPress(float x)
        {
            PlayerAction a = PlayerAction.Neutral; a.MoveX = x; a.JumpPressed = true; a.JumpHeld = true; return a;
        }
        static PlayerAction JumpHold(float x)
        {
            PlayerAction a = PlayerAction.Neutral; a.MoveX = x; a.JumpHeld = true; return a;
        }

        static List<PlayerAction> Stream()
        {
            // ticks: 0,1 run right; 2 jump press; 3,4 jump held; 5 run right
            return new List<PlayerAction> { Move(1), Move(1), JumpPress(1), JumpHold(1), JumpHold(1), Move(1) };
        }

        [Test]
        public void FindJumpEdge_ReturnsFirstPressAtOrAfterHint()
        {
            Assert.AreEqual(2, Perturbation.FindJumpEdge(Stream(), 0));
            Assert.AreEqual(2, Perturbation.FindJumpEdge(Stream(), 2));
            Assert.AreEqual(-1, Perturbation.FindJumpEdge(Stream(), 3), "no press at/after tick 3");
        }

        [Test]
        public void ShiftJumpEdge_MovesPressAndHeldRun_PreservesMoveInputs()
        {
            List<PlayerAction> shifted = Perturbation.ShiftJumpEdge(Stream(), 2, +2);

            // Old-span ticks that the shifted span no longer covers (2,3) are cleared. Tick 4 is reused by the
            // new span (4,5,6), so it is not expected clear.
            for (int i = 2; i <= 3; i++)
            {
                Assert.IsFalse(shifted[i].JumpPressed, $"stale press at {i}");
                Assert.IsFalse(shifted[i].JumpHeld, $"stale held at {i}");
            }
            // New span starts at 4 (2+2), length 3 -> ticks 4,5,6.
            Assert.IsTrue(shifted[4].JumpPressed);
            Assert.IsTrue(shifted[4].JumpHeld && shifted[5].JumpHeld && shifted[6].JumpHeld);
            Assert.IsFalse(shifted[5].JumpPressed);
            // MoveX preserved on original ticks.
            Assert.AreEqual(1f, shifted[0].MoveX);
            Assert.AreEqual(1f, shifted[5].MoveX);
        }

        [Test]
        public void ShiftJumpEdge_NegativeDeltaClampsToZero_DoesNotThrow()
        {
            List<PlayerAction> shifted = Perturbation.ShiftJumpEdge(Stream(), 2, -5);
            Assert.IsTrue(shifted[0].JumpPressed, "edge clamped to tick 0");
        }

        [Test]
        public void ShiftJumpEdge_DoesNotMutateInput()
        {
            List<PlayerAction> original = Stream();
            Perturbation.ShiftJumpEdge(original, 2, +3);
            Assert.IsTrue(original[2].JumpPressed, "input stream must be untouched");
        }

        // ---- counterfactual variant aggregation on fixtures ---------------------------------------------------

        static EpisodeSummary Ep(string profile, string outcome, int deaths, float progress)
        {
            return new EpisodeSummary
            {
                episodeId = $"{profile}-{deaths}-{progress}",
                agentId = "profiled",
                profileId = profile,
                outcome = outcome,
                deaths = deaths,
                furthestProgress = progress,
                completionTick = outcome == "Completed" ? 100 : -1
            };
        }

        [Test]
        public void AggregateVariant_ComputesOverallAndPerProfile()
        {
            List<EpisodeSummary> eps = new List<EpisodeSummary>
            {
                Ep("Beginner", "StepBudgetExceeded", 5, 0.4f),
                Ep("Beginner", "Completed", 2, 1f),
                Ep("Expert", "Completed", 0, 1f),
                Ep("Expert", "Completed", 1, 1f),
            };

            VariantResult vr = CounterfactualRunner.AggregateVariant(13.5f, solved: true, eps);

            Assert.AreEqual(13.5f, vr.Value);
            Assert.IsTrue(vr.Solved);
            Assert.AreEqual(0.75f, vr.CompletionRate, 1e-4, "3 of 4 completed");
            Assert.AreEqual(4, vr.EpisodeIds.Count);
            Assert.AreEqual(2, vr.Profiles.Count, "one breakdown per profile");

            ProfileOutcome expert = FindProfile(vr, "Expert");
            Assert.AreEqual(1f, expert.CompletionRate, 1e-4);
            ProfileOutcome beginner = FindProfile(vr, "Beginner");
            Assert.AreEqual(0.5f, beginner.CompletionRate, 1e-4);
            Assert.Greater(beginner.MedianDeaths, expert.MedianDeaths, "beginner dies more than expert");
        }

        [Test]
        public void AggregateVariant_UnsolvedVariant_HasNoEpisodes()
        {
            VariantResult vr = CounterfactualRunner.AggregateVariant(9f, solved: false, new List<EpisodeSummary>());
            Assert.IsFalse(vr.Solved);
            Assert.AreEqual(0, vr.EpisodeIds.Count);
            Assert.AreEqual(0f, vr.CompletionRate);
        }

        static ProfileOutcome FindProfile(VariantResult vr, string id)
        {
            for (int i = 0; i < vr.Profiles.Count; i++)
                if (vr.Profiles[i].ProfileId == id) return vr.Profiles[i];
            Assert.Fail($"profile {id} not found");
            return default;
        }
    }
}
