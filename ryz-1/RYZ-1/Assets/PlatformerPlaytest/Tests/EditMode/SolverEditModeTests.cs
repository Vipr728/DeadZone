using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using PlatformerPlaytest.Solver;
using UnityEngine;

namespace PlatformerPlaytest.Tests.EditMode
{
    public class SolverEditModeTests
    {
        // ---- macro expansion determinism + edge semantics -----------------------------------------------------

        [Test]
        public void HoldDirection_ExpandsToHeldMoveNoEdges()
        {
            List<PlayerAction> stream = new List<PlayerAction>();
            MovementMacro.HoldDirection(1, 3).Expand(stream);

            Assert.AreEqual(3, stream.Count);
            for (int i = 0; i < stream.Count; i++)
            {
                Assert.AreEqual(1f, stream[i].MoveX);
                Assert.IsFalse(stream[i].JumpPressed);
                Assert.IsFalse(stream[i].JumpHeld);
                Assert.IsFalse(stream[i].DashPressed);
            }
        }

        [Test]
        public void JumpAndHold_JumpPressedOnlyFirstTick_HeldThroughout()
        {
            List<PlayerAction> stream = new List<PlayerAction>();
            MovementMacro.JumpAndHold(1, 4).Expand(stream);

            Assert.AreEqual(4, stream.Count);
            Assert.IsTrue(stream[0].JumpPressed, "JumpPressed must be an edge on tick 0");
            for (int i = 1; i < stream.Count; i++)
                Assert.IsFalse(stream[i].JumpPressed, $"JumpPressed leaked onto tick {i}");
            for (int i = 0; i < stream.Count; i++)
            {
                Assert.IsTrue(stream[i].JumpHeld, $"JumpHeld must persist on tick {i}");
                Assert.AreEqual(1f, stream[i].MoveX);
            }
        }

        [Test]
        public void DashDirection_DashPressedOnlyFirstTick()
        {
            List<PlayerAction> stream = new List<PlayerAction>();
            MovementMacro.DashDirection(1, 1, 5).Expand(stream);

            Assert.AreEqual(5, stream.Count);
            Assert.IsTrue(stream[0].DashPressed);
            Assert.AreEqual(1f, stream[0].MoveX);
            Assert.AreEqual(1f, stream[0].MoveY);
            for (int i = 1; i < stream.Count; i++)
                Assert.IsFalse(stream[i].DashPressed, $"DashPressed leaked onto tick {i}");
        }

        [Test]
        public void ReleaseAll_And_Wait_AreAllNeutral()
        {
            List<PlayerAction> stream = new List<PlayerAction>();
            MovementMacro.ReleaseAll(2).Expand(stream);
            MovementMacro.Wait(2).Expand(stream);

            Assert.AreEqual(4, stream.Count);
            for (int i = 0; i < stream.Count; i++)
            {
                Assert.AreEqual(0f, stream[i].MoveX);
                Assert.AreEqual(0f, stream[i].MoveY);
                Assert.IsFalse(stream[i].JumpPressed);
                Assert.IsFalse(stream[i].JumpHeld);
                Assert.IsFalse(stream[i].DashPressed);
            }
        }

        [Test]
        public void Expand_IsDeterministic()
        {
            List<PlayerAction> a = new List<PlayerAction>();
            List<PlayerAction> b = new List<PlayerAction>();
            MovementMacro.JumpAndHold(-1, 10).Expand(a);
            MovementMacro.JumpAndHold(-1, 10).Expand(b);

            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].MoveX, b[i].MoveX);
                Assert.AreEqual(a[i].JumpPressed, b[i].JumpPressed);
                Assert.AreEqual(a[i].JumpHeld, b[i].JumpHeld);
            }
        }

        // ---- solver: duplicate elimination + determinism against a fake adapter --------------------------------

        /// <summary>
        /// Tiny deterministic in-memory adapter: player x advances by MoveX each tick, y unaffected. Complete when
        /// x >= 10. State is a pure function of the applied action stream, so the beam search's hashing/dedup and
        /// determinism can be checked with zero Unity/physics involvement.
        /// </summary>
        sealed class LinearAdapter : IGameAdapter
        {
            float x;
            public int Ticks;

            public void Bind(Arena arena, ScenarioConfig scenario) { }
            public void ResetEpisode(int seed) { x = 0f; }
            public void ApplyAction(in PlayerAction action) { pendingMove = action.MoveX; }
            float pendingMove;
            public void TickSimulation(float dt) { x += pendingMove; Ticks++; }
            public void ReadObservation(Observation target)
            {
                target.Position = new Vector2(x, 0f);
                target.Velocity = new Vector2(pendingMove, 0f);
                target.IsGrounded = true;
                target.OnLeftWall = false;
                target.OnRightWall = false;
                target.IsDashing = false;
                target.IsClimbing = false;
                target.DashesRemaining = 1;
                target.Progress = Mathf.Clamp01(x / 10f);
            }
            public void AfterStep(int tick) { }
            public bool IsDead => false;
            public bool IsComplete => x >= 10f;
            public float Progress => Mathf.Clamp01(x / 10f);
            public void DrainEvents(List<TimedGameEvent> into) { }
            public void RestoreOverrides() { }
        }

        /// <summary>
        /// Moving right without holding jump permanently drops below the target height. With BeamWidth=1 this
        /// catches both parts of the regression: x alone must not complete the target, and scoring must retain
        /// the valid jump-held branch instead of the faster-enumerated falling branch.
        /// </summary>
        sealed class FallingShortcutAdapter : IGameAdapter
        {
            float x;
            bool fell;
            PlayerAction pending;

            public void Bind(Arena arena, ScenarioConfig scenario) { }
            public void ResetEpisode(int seed)
            {
                x = 0f;
                fell = false;
                pending = PlayerAction.Neutral;
            }
            public void ApplyAction(in PlayerAction action) { pending = action; }
            public void TickSimulation(float dt)
            {
                if (pending.MoveX > 0f)
                {
                    x += 1f;
                    if (!pending.JumpHeld)
                        fell = true;
                }
            }
            public void ReadObservation(Observation target)
            {
                target.Position = new Vector2(x, fell ? -5f : 0f);
                target.Velocity = new Vector2(pending.MoveX, 0f);
                target.IsGrounded = false;
                target.OnLeftWall = false;
                target.OnRightWall = false;
                target.IsDashing = false;
                target.IsClimbing = false;
                target.DashesRemaining = 1;
                target.Progress = Mathf.Clamp01(x / 10f);
            }
            public void AfterStep(int tick) { }
            public bool IsDead => false;
            public bool IsComplete => false;
            public float Progress => Mathf.Clamp01(x / 10f);
            public void DrainEvents(List<TimedGameEvent> into) { }
            public void RestoreOverrides() { }
        }

        /// <summary>
        /// Same world-state transitions with a configurable full-level Progress scale. Segment planning must not
        /// change when unrelated geometry moves the final goal farther away.
        /// </summary>
        sealed class ProgressScaledBranchAdapter : IGameAdapter
        {
            readonly float progressSpan;
            float x;
            bool grounded;
            PlayerAction pending;

            public ProgressScaledBranchAdapter(float progressSpan)
            {
                this.progressSpan = progressSpan;
            }

            public void Bind(Arena arena, ScenarioConfig scenario) { }
            public void ResetEpisode(int seed)
            {
                x = 0f;
                grounded = true;
                pending = PlayerAction.Neutral;
            }
            public void ApplyAction(in PlayerAction action) { pending = action; }
            public void TickSimulation(float dt)
            {
                if (pending.MoveX <= 0f)
                    return;

                grounded = !pending.JumpHeld;
                x += grounded ? 1f : 1.2f;
            }
            public void ReadObservation(Observation target)
            {
                target.Position = new Vector2(x, 0f);
                target.Velocity = new Vector2(pending.MoveX, 0f);
                target.IsGrounded = grounded;
                target.OnLeftWall = false;
                target.OnRightWall = false;
                target.IsDashing = false;
                target.IsClimbing = false;
                target.DashesRemaining = 1;
                target.Progress = Mathf.Clamp01(x / progressSpan);
            }
            public void AfterStep(int tick) { }
            public bool IsDead => false;
            public bool IsComplete => false;
            public float Progress => Mathf.Clamp01(x / progressSpan);
            public void DrainEvents(List<TimedGameEvent> into) { }
            public void RestoreOverrides() { }
        }

        static SolverConfig FakeConfig()
        {
            SolverConfig c = SolverConfig.Default;
            c.TickMenu = new[] { 5, 10 };
            c.BeamWidth = 8;
            c.MaxMacrosDepth = 20;
            c.FixedDeltaTime = 0.02f;
            return c;
        }

        static ScenarioConfig FakeScenario()
        {
            ScenarioConfig s = ScriptableObject.CreateInstance<ScenarioConfig>();
            s.spawnPosition = Vector2.zero;
            s.goalRect = new Rect(10f, -1f, 2f, 2f);
            s.fixedDeltaTime = 0.02f;
            s.stepBudget = 2000;
            return s;
        }

        [Test]
        public void Solver_FindsLinearSolution()
        {
            BeamSearchSolver solver = new BeamSearchSolver();
            SolveResult r = solver.Solve(new LinearAdapter(), FakeScenario(), FakeConfig());

            Assert.IsTrue(r.Solved, "solver should reach x>=10 by moving right");
            Assert.Greater(r.ActionStream.Count, 0);
        }

        [Test]
        public void Solver_FailedSearch_ExportsBestEffortReplayableTrace()
        {
            SolverConfig config = FakeConfig();
            config.TickMenu = new[] { 5 };
            config.BeamWidth = 1;
            config.MaxMacrosDepth = 1;

            SolveResult result = new BeamSearchSolver().Solve(new LinearAdapter(), FakeScenario(), config);

            Assert.IsFalse(result.Solved);
            Assert.That(result.ActionStream, Is.Empty, "only verified completions belong in ActionStream");
            Assert.That(result.BestEffortActionStream, Is.Not.Empty,
                "failed search must preserve a clean-reset-replayable diagnostic trace");
            Assert.That(result.BestEffortActionStream[0].MoveX, Is.GreaterThan(0f));
        }

        [Test]
        public void Solver_VisitsEachHashedStateOnce()
        {
            // Every HoldDirection(-1) / Wait / ReleaseAll from spawn lands on x<=0 states that collide in hash
            // space; the solver must record duplicates rather than re-expand them.
            BeamSearchSolver solver = new BeamSearchSolver();
            SolveResult r = solver.Solve(new LinearAdapter(), FakeScenario(), FakeConfig());

            Assert.Greater(r.DuplicatesPruned, 0, "distinct macros collapsing to the same state must be deduped");
        }

        [Test]
        public void Solver_IsDeterministicAcrossRuns()
        {
            BeamSearchSolver solver = new BeamSearchSolver();
            SolveResult a = solver.Solve(new LinearAdapter(), FakeScenario(), FakeConfig());
            SolveResult b = solver.Solve(new LinearAdapter(), FakeScenario(), FakeConfig());

            Assert.AreEqual(a.Solved, b.Solved);
            Assert.AreEqual(a.ActionStream.Count, b.ActionStream.Count);
            Assert.AreEqual(a.NodesExpanded, b.NodesExpanded);
            Assert.AreEqual(a.DuplicatesPruned, b.DuplicatesPruned);
            for (int i = 0; i < a.ActionStream.Count; i++)
            {
                Assert.AreEqual(a.ActionStream[i].MoveX, b.ActionStream[i].MoveX, $"MoveX diverged at {i}");
                Assert.AreEqual(a.ActionStream[i].JumpPressed, b.ActionStream[i].JumpPressed, $"JumpPressed diverged at {i}");
            }
        }

        [Test]
        public void Solver_TargetYRejectsFallingShortcutAndRetainsValidBranch()
        {
            SolverConfig config = SolverConfig.Default;
            config.TickMenu = new[] { 5 };
            config.BeamWidth = 1;
            config.MaxMacrosDepth = 2;
            config.MaxTicksSimulated = 10000;
            config.FixedDeltaTime = 0.02f;
            config.TargetX = 10f;
            config.TargetY = 0f;
            config.TargetYTolerance = 0.1f;

            SolveResult result = new BeamSearchSolver().Solve(
                new FallingShortcutAdapter(), FakeScenario(), config);

            Assert.IsTrue(result.Solved, result.Diagnostic);
            Assert.AreEqual(10, result.ActionStream.Count);
            for (int i = 0; i < result.ActionStream.Count; i++)
                Assert.IsTrue(result.ActionStream[i].JumpHeld, $"falling shortcut retained at tick {i}");
        }

        [Test]
        public void SegmentedSolver_BeamOrderingIsInvariantToFinalGoalDistance()
        {
            SolverConfig config = SolverConfig.Default;
            config.TickMenu = new[] { 5 };
            config.BeamWidth = 1;
            config.MaxMacrosDepth = 2;
            config.MaxTicksSimulated = 10000;
            config.FixedDeltaTime = 0.02f;
            config.TargetX = 11.5f;

            BeamSearchSolver solver = new BeamSearchSolver();
            SolveResult nearbyGoal = solver.Solve(
                new ProgressScaledBranchAdapter(20f), FakeScenario(), config);
            SolveResult distantGoal = solver.Solve(
                new ProgressScaledBranchAdapter(2000f), FakeScenario(), config);

            Assert.IsTrue(nearbyGoal.Solved, nearbyGoal.Diagnostic);
            Assert.IsTrue(distantGoal.Solved, distantGoal.Diagnostic);
            Assert.AreEqual(nearbyGoal.ActionStream.Count, distantGoal.ActionStream.Count);
            for (int i = 0; i < nearbyGoal.ActionStream.Count; i++)
            {
                Assert.AreEqual(nearbyGoal.ActionStream[i].MoveX, distantGoal.ActionStream[i].MoveX);
                Assert.AreEqual(nearbyGoal.ActionStream[i].JumpHeld, distantGoal.ActionStream[i].JumpHeld);
                Assert.AreEqual(nearbyGoal.ActionStream[i].DashPressed, distantGoal.ActionStream[i].DashPressed);
            }
        }
    }
}
