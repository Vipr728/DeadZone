using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using UnityEngine;

namespace PlatformerPlaytest.Tests.EditMode
{
    public class PlayerActionTests
    {
        [Test]
        public void Neutral_HasZeroedFields()
        {
            PlayerAction a = PlayerAction.Neutral;
            Assert.AreEqual(0f, a.MoveX);
            Assert.AreEqual(0f, a.MoveY);
            Assert.IsFalse(a.JumpPressed);
            Assert.IsFalse(a.JumpHeld);
            Assert.IsFalse(a.DashPressed);
            Assert.IsFalse(a.ClimbHeld);
        }
    }

    public class ProgressMathTests
    {
        [Test]
        public void Progress_ClampsBelowSpawn()
        {
            Assert.AreEqual(0f, ProgressMath.Progress(-5f, 0f, 10f));
        }

        [Test]
        public void Progress_ClampsAboveGoal()
        {
            Assert.AreEqual(1f, ProgressMath.Progress(20f, 0f, 10f));
        }

        [Test]
        public void Progress_MidpointIsHalf()
        {
            Assert.AreEqual(0.5f, ProgressMath.Progress(5f, 0f, 10f), 0.0001f);
        }

        [Test]
        public void Progress_ZeroSpanReturnsZero()
        {
            Assert.AreEqual(0f, ProgressMath.Progress(5f, 3f, 3f));
        }

        [Test]
        public void SectionIndexFor_EmptyBoundsIsZero()
        {
            Assert.AreEqual(0, ProgressMath.SectionIndexFor(50f, new float[0]));
        }

        [Test]
        public void SectionIndexFor_BeforeFirstBoundaryIsZero()
        {
            float[] bounds = { 10f, 20f, 30f };
            Assert.AreEqual(0, ProgressMath.SectionIndexFor(5f, bounds));
        }

        [Test]
        public void SectionIndexFor_ScansToCorrectSection()
        {
            float[] bounds = { 10f, 20f, 30f };
            Assert.AreEqual(1, ProgressMath.SectionIndexFor(15f, bounds));
            Assert.AreEqual(2, ProgressMath.SectionIndexFor(25f, bounds));
            Assert.AreEqual(3, ProgressMath.SectionIndexFor(35f, bounds));
        }

        [Test]
        public void SectionIndexFor_ExactBoundaryCountsAsPassed()
        {
            float[] bounds = { 10f, 20f };
            Assert.AreEqual(1, ProgressMath.SectionIndexFor(10f, bounds));
        }
    }

    public class EpisodeResultTests
    {
        [Test]
        public void Defaults_AreZeroedAndEventsEmpty()
        {
            EpisodeResult result = new EpisodeResult();
            Assert.AreEqual(0, result.Steps);
            Assert.AreEqual(0, result.Deaths);
            Assert.AreEqual(0f, result.FurthestProgress);
            Assert.AreEqual(-1, result.CompletionTick);
            Assert.AreEqual(0, result.CheckpointsReached);
            Assert.IsNotNull(result.Events);
            Assert.AreEqual(0, result.Events.Count);
        }
    }

    public class GenericEpisodeRunnerTests
    {
        struct GrappleAction
        {
            public bool FireGrapple;
            public float Reel;
        }

        sealed class GrappleObservation
        {
            public int AnchorsPassed;
        }

        sealed class GrappleAgent : IAgent<GrappleAction, GrappleObservation>
        {
            public void OnEpisodeStart(int seed) { }

            public GrappleAction Act(GrappleObservation observation, int tick) =>
                new GrappleAction { FireGrapple = true, Reel = 0.75f };
        }

        sealed class GrappleAdapter : IGameAdapter<GrappleAction, GrappleObservation>
        {
            int anchors;

            public bool IsDead => false;
            public bool IsComplete => anchors >= 3;
            public float Progress => anchors / 3f;

            public void Bind(Arena arena, ScenarioConfig scenario) { }
            public void ResetEpisode(int seed) => anchors = 0;
            public void ApplyAction(in GrappleAction action)
            {
                if (action.FireGrapple && action.Reel > 0f)
                    anchors++;
            }
            public void TickSimulation(float dt) { }
            public void ReadObservation(GrappleObservation target) => target.AnchorsPassed = anchors;
            public void AfterStep(int tick) { }
            public void DrainEvents(List<TimedGameEvent> into) { }
            public void RestoreOverrides() { }
        }

        [Test]
        public void CustomMechanics_RunWithoutPlayerActionOrObservation()
        {
            ScenarioConfig scenario = ScriptableObject.CreateInstance<ScenarioConfig>();
            scenario.stepBudget = 10;

            EpisodeResult result =
                new EpisodeRunner<GrappleAction, GrappleObservation>(
                    new GrappleAdapter(), new GrappleAgent(), scenario, seed: 4).Run();

            Assert.AreEqual(Outcome.Completed, result.Outcome);
            Assert.AreEqual(3, result.Steps);
            Assert.AreEqual(1f, result.FurthestProgress, 0.0001f);
        }
    }
}
