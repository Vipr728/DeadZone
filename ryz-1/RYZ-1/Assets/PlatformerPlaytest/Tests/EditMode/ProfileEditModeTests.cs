using System;
using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;

namespace PlatformerPlaytest.Tests.EditMode
{
    public class ProfileEditModeTests
    {
        // ---- spec table values ---------------------------------------------------------------------------------

        [Test]
        public void Beginner_MatchesSpecTable()
        {
            ProfileParams p = ProfileParams.Beginner;
            Assert.AreEqual(12, p.reactionDelayTicks);
            Assert.AreEqual(4f, p.timingVarianceTicksSigma);
            Assert.AreEqual(0.15f, p.directionErrorProb);
            Assert.AreEqual(ProfileParams.HorizonShort, p.planningHorizon);
            Assert.IsFalse(p.knowsWallJumpChains);
            Assert.AreEqual(0.5f, p.repeatFailedStrategyProb);
            Assert.AreEqual(8, p.persistenceRetries);
        }

        [Test]
        public void Intermediate_MatchesSpecTable()
        {
            ProfileParams p = ProfileParams.Intermediate;
            Assert.AreEqual(6, p.reactionDelayTicks);
            Assert.AreEqual(2f, p.timingVarianceTicksSigma);
            Assert.AreEqual(0.05f, p.directionErrorProb);
            Assert.AreEqual(ProfileParams.HorizonMedium, p.planningHorizon);
            Assert.IsTrue(p.knowsWallJumpChains);
            Assert.AreEqual(0.2f, p.repeatFailedStrategyProb);
            Assert.AreEqual(15, p.persistenceRetries);
        }

        [Test]
        public void Expert_MatchesSpecTable()
        {
            ProfileParams p = ProfileParams.Expert;
            Assert.AreEqual(2, p.reactionDelayTicks);
            Assert.AreEqual(0.5f, p.timingVarianceTicksSigma);
            Assert.AreEqual(0.01f, p.directionErrorProb);
            Assert.AreEqual(ProfileParams.HorizonFull, p.planningHorizon);
            Assert.IsTrue(p.knowsWallJumpChains);
            Assert.AreEqual(0.05f, p.repeatFailedStrategyProb);
            Assert.AreEqual(40, p.persistenceRetries);
        }

        // ---- reaction delay --------------------------------------------------------------------------------

        static List<PlayerAction> MakePlan(int length)
        {
            List<PlayerAction> plan = new List<PlayerAction>(length);
            for (int i = 0; i < length; i++)
            {
                PlayerAction a = PlayerAction.Neutral;
                a.MoveX = (i / 5) % 2 == 0 ? 1f : -1f;
                if (i == 10) { a.JumpPressed = true; a.JumpHeld = true; }
                if (i == 11) a.JumpHeld = true;
                if (i == 20) a.DashPressed = true;
                plan.Add(a);
            }
            return plan;
        }

        [Test]
        public void ReactionDelay_ShiftsPlanLate_LeadingTicksNeutral()
        {
            ProfileParams profile = new ProfileParams
            {
                reactionDelayTicks = 5,
                timingVarianceTicksSigma = 0f,
                directionErrorProb = 0f,
                planningHorizon = 60,
                knowsWallJumpChains = true,
                repeatFailedStrategyProb = 0f,
                persistenceRetries = 1
            };
            List<PlayerAction> plan = MakePlan(30);
            ProfiledAgent agent = new ProfiledAgent(plan, profile);
            agent.OnEpisodeStart(seed: 1);

            for (int i = 0; i < profile.reactionDelayTicks; i++)
            {
                PlayerAction a = agent.Act(null, i);
                Assert.IsFalse(a.JumpPressed);
                Assert.IsFalse(a.DashPressed);
                Assert.AreEqual(0f, a.MoveX);
            }

            // Plan content intact, just shifted by the delay (no jitter/direction error configured).
            for (int i = 0; i < plan.Count; i++)
            {
                PlayerAction expected = plan[i];
                PlayerAction actual = agent.Act(null, i + profile.reactionDelayTicks);
                Assert.AreEqual(expected.MoveX, actual.MoveX);
                Assert.AreEqual(expected.JumpPressed, actual.JumpPressed);
                Assert.AreEqual(expected.DashPressed, actual.DashPressed);
            }
        }

        // ---- jitter determinism -----------------------------------------------------------------------------

        static List<PlayerAction> CollectMutatedPlan(List<PlayerAction> basePlan, ProfileParams profile, int seed, int lookaheadTicks)
        {
            ProfiledAgent agent = new ProfiledAgent(new List<PlayerAction>(basePlan), profile);
            agent.OnEpisodeStart(seed);
            List<PlayerAction> result = new List<PlayerAction>(lookaheadTicks);
            for (int i = 0; i < lookaheadTicks; i++)
                result.Add(agent.Act(null, i));
            return result;
        }

        [Test]
        public void Jitter_SameSeed_IdenticalMutatedPlan()
        {
            ProfileParams profile = ProfileParams.Beginner;
            List<PlayerAction> plan = MakePlan(30);

            List<PlayerAction> a = CollectMutatedPlan(plan, profile, seed: 42, 40);
            List<PlayerAction> b = CollectMutatedPlan(plan, profile, seed: 42, 40);

            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].MoveX, b[i].MoveX, $"tick {i} MoveX diverged");
                Assert.AreEqual(a[i].JumpPressed, b[i].JumpPressed, $"tick {i} JumpPressed diverged");
                Assert.AreEqual(a[i].DashPressed, b[i].DashPressed, $"tick {i} DashPressed diverged");
            }
        }

        [Test]
        public void Jitter_DifferentSeed_ProducesDifferentPlan()
        {
            ProfileParams profile = ProfileParams.Beginner;
            List<PlayerAction> plan = MakePlan(30);

            List<PlayerAction> a = CollectMutatedPlan(plan, profile, seed: 1, 40);
            List<PlayerAction> b = CollectMutatedPlan(plan, profile, seed: 999, 40);

            bool anyDifferent = false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].MoveX != b[i].MoveX || a[i].JumpPressed != b[i].JumpPressed || a[i].DashPressed != b[i].DashPressed)
                {
                    anyDifferent = true;
                    break;
                }
            }
            Assert.IsTrue(anyDifferent, "different seeds produced identical mutated plans");
        }

        [Test]
        public void Jitter_ExtremeSigma_PreservesEdgeOrder()
        {
            ProfileParams profile = new ProfileParams
            {
                reactionDelayTicks = 0,
                timingVarianceTicksSigma = 50f, // extreme
                directionErrorProb = 0f,
                planningHorizon = 60,
                knowsWallJumpChains = true,
                repeatFailedStrategyProb = 0f,
                persistenceRetries = 1
            };

            List<PlayerAction> plan = new List<PlayerAction>();
            for (int i = 0; i < 5; i++)
            {
                PlayerAction a = PlayerAction.Neutral;
                a.JumpPressed = true;
                a.JumpHeld = true;
                plan.Add(a);
                plan.Add(PlayerAction.Neutral);
                plan.Add(PlayerAction.Neutral);
                PlayerAction dash = PlayerAction.Neutral;
                dash.DashPressed = true;
                dash.MoveX = 1f;
                plan.Add(dash);
                plan.Add(PlayerAction.Neutral);
                plan.Add(PlayerAction.Neutral);
            }

            ProfiledAgent agent = new ProfiledAgent(plan, profile);
            agent.OnEpisodeStart(seed: 7);

            int lastEdgeTick = -1;
            int totalEdgeTicksSeen = 0;
            for (int i = 0; i < plan.Count + 200; i++)
            {
                PlayerAction a = agent.Act(null, i);
                if (a.JumpPressed || a.DashPressed)
                {
                    Assert.Greater(i, lastEdgeTick, "edge order not preserved under extreme jitter");
                    lastEdgeTick = i;
                    totalEdgeTicksSeen++;
                }
            }
            Assert.AreEqual(10, totalEdgeTicksSeen, "expected 5 jump + 5 dash edges to survive jitter");
        }

        // ---- direction error rate --------------------------------------------------------------------------

        [Test]
        public void DirectionError_DashRotationRate_MatchesConfiguredProbWithinTolerance()
        {
            float directionErrorProb = 0.2f;
            ProfileParams profile = new ProfileParams
            {
                reactionDelayTicks = 0,
                timingVarianceTicksSigma = 0f,
                directionErrorProb = directionErrorProb,
                planningHorizon = 60,
                knowsWallJumpChains = true,
                repeatFailedStrategyProb = 0f,
                persistenceRetries = 1
            };

            int trials = 1000;
            int rotated = 0;
            for (int seed = 0; seed < trials; seed++)
            {
                List<PlayerAction> plan = new List<PlayerAction>();
                PlayerAction dash = PlayerAction.Neutral;
                dash.DashPressed = true;
                dash.MoveX = 1f;
                dash.MoveY = 0f;
                plan.Add(dash);

                ProfiledAgent agent = new ProfiledAgent(plan, profile);
                agent.OnEpisodeStart(seed);
                PlayerAction result = agent.Act(null, 0);
                if (result.MoveX != 1f || result.MoveY != 0f)
                    rotated++;
            }

            float observedRate = rotated / (float)trials;
            float tolerance = directionErrorProb * 0.30f;
            Assert.That(observedRate, Is.EqualTo(directionErrorProb).Within(tolerance),
                $"observed dash rotation rate {observedRate} outside ±30% of configured {directionErrorProb}");
        }

        // ---- Act is lookup-only ---------------------------------------------------------------------------

        [Test]
        public void Act_IsPureLookup_PlanReferenceStableAcrossCalls()
        {
            ProfileParams profile = ProfileParams.Intermediate;
            List<PlayerAction> plan = MakePlan(20);
            ProfiledAgent agent = new ProfiledAgent(plan, profile);
            agent.OnEpisodeStart(seed: 3);

            PlayerAction first = agent.Act(null, 5);
            for (int i = 0; i < 100; i++)
            {
                PlayerAction repeat = agent.Act(null, 5);
                Assert.AreEqual(first.MoveX, repeat.MoveX);
                Assert.AreEqual(first.JumpPressed, repeat.JumpPressed);
                Assert.AreEqual(first.DashPressed, repeat.DashPressed);
            }
        }

        // ---- solver config mapping -------------------------------------------------------------------------

        [Test]
        public void MakeSolverConfig_CapsDepthAtPlanningHorizon()
        {
            SolverConfig baseConfig = SolverConfig.Default;
            baseConfig.MaxMacrosDepth = 60;

            SolverConfig beginnerConfig = ProfiledAgent.MakeSolverConfig(ProfileParams.Beginner, baseConfig);
            Assert.AreEqual(ProfileParams.HorizonShort, beginnerConfig.MaxMacrosDepth);

            SolverConfig expertConfig = ProfiledAgent.MakeSolverConfig(ProfileParams.Expert, baseConfig);
            Assert.AreEqual(ProfileParams.HorizonFull, expertConfig.MaxMacrosDepth);
        }

        // ---- persistence helpers ---------------------------------------------------------------------------

        [Test]
        public void ShouldRetry_RespectsPersistenceBudget()
        {
            ProfileParams profile = ProfileParams.Beginner; // persistenceRetries = 8
            Assert.IsTrue(ProfiledAgent.ShouldRetry(profile, 0));
            Assert.IsTrue(ProfiledAgent.ShouldRetry(profile, 7));
            Assert.IsFalse(ProfiledAgent.ShouldRetry(profile, 8));
        }
    }
}
