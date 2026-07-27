using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using PlatformerPlaytest.Analysis;
using UnityEngine;

namespace PlatformerPlaytest.Tests.EditMode
{
    public class AnalysisEditModeTests
    {
        // ---- EpisodeStats -------------------------------------------------------------------------------------

        static EpisodeSummary MakeSummary(string agent, string profile, string outcome, int deaths, int completionTick, float progress)
        {
            return new EpisodeSummary
            {
                episodeId = $"{agent}-{profile}-{completionTick}-{deaths}",
                agentId = agent,
                profileId = profile,
                outcome = outcome,
                deaths = deaths,
                completionTick = completionTick,
                furthestProgress = progress
            };
        }

        [Test]
        public void EpisodeStats_MedianOddCount_IsMiddleValue()
        {
            List<EpisodeSummary> eps = new List<EpisodeSummary>
            {
                MakeSummary("a", "Beginner", "Completed", 1, 100, 1f),
                MakeSummary("a", "Beginner", "Completed", 5, 300, 1f),
                MakeSummary("a", "Beginner", "Completed", 3, 200, 1f),
            };

            List<EpisodeStats> stats = EpisodeStatsCalculator.Compute(eps);
            Assert.AreEqual(1, stats.Count);
            Assert.AreEqual(3f, stats[0].MedianDeaths);
            Assert.AreEqual(200f, stats[0].MedianCompletionTicks);
            Assert.AreEqual(1f, stats[0].CompletionRate);
        }

        [Test]
        public void EpisodeStats_MedianEvenCount_AveragesMiddleTwo()
        {
            List<EpisodeSummary> eps = new List<EpisodeSummary>
            {
                MakeSummary("a", "Beginner", "Completed", 1, 100, 1f),
                MakeSummary("a", "Beginner", "Completed", 5, 300, 1f),
                MakeSummary("a", "Beginner", "Completed", 3, 200, 1f),
                MakeSummary("a", "Beginner", "Completed", 7, 400, 1f),
            };

            List<EpisodeStats> stats = EpisodeStatsCalculator.Compute(eps);
            Assert.AreEqual(4f, stats[0].MedianDeaths); // (3+5)/2
            Assert.AreEqual(250f, stats[0].MedianCompletionTicks); // (200+300)/2
        }

        [Test]
        public void EpisodeStats_ZeroCompleted_MedianCompletionTicksIsMinusOne()
        {
            List<EpisodeSummary> eps = new List<EpisodeSummary>
            {
                MakeSummary("a", "Beginner", "StepBudgetExceeded", 2, -1, 0.4f),
                MakeSummary("a", "Beginner", "StepBudgetExceeded", 4, -1, 0.6f),
            };

            List<EpisodeStats> stats = EpisodeStatsCalculator.Compute(eps);
            Assert.AreEqual(0f, stats[0].CompletionRate);
            Assert.AreEqual(-1f, stats[0].MedianCompletionTicks);
            Assert.AreEqual(0.5f, stats[0].MeanFurthestProgress, 1e-5f);
        }

        [Test]
        public void EpisodeStats_GroupsSeparatelyByAgentAndProfile()
        {
            List<EpisodeSummary> eps = new List<EpisodeSummary>
            {
                MakeSummary("a", "Beginner", "Completed", 1, 100, 1f),
                MakeSummary("a", "Expert", "Completed", 0, 50, 1f),
                MakeSummary("b", "Beginner", "Completed", 2, 120, 1f),
            };

            List<EpisodeStats> stats = EpisodeStatsCalculator.Compute(eps);
            Assert.AreEqual(3, stats.Count);
        }

        // ---- DifficultyFlags ----------------------------------------------------------------------------------

        static List<SectionStats> FlatSections(int count, int deathsEach)
        {
            List<SectionStats> sections = new List<SectionStats>();
            for (int i = 0; i < count; i++)
                sections.Add(new SectionStats { SectionIndex = i, DeathCount = deathsEach });
            return sections;
        }

        [Test]
        public void DifficultyFlags_FlatSections_NoFlags()
        {
            List<SectionStats> sections = FlatSections(5, 3);
            List<Finding> findings = DifficultyFlags.FlagDifficultySpikes(sections, new List<DeathRecord>());
            Assert.AreEqual(0, findings.Count);
        }

        [Test]
        public void DifficultyFlags_OneSpike_FlaggedWithEvidence()
        {
            List<SectionStats> sections = FlatSections(5, 2);
            sections[2] = new SectionStats { SectionIndex = 2, DeathCount = 20 };

            List<DeathRecord> deaths = new List<DeathRecord>
            {
                new DeathRecord { EpisodeId = "e1", SectionIndex = 2 },
                new DeathRecord { EpisodeId = "e2", SectionIndex = 2 },
                new DeathRecord { EpisodeId = "e1", SectionIndex = 0 }, // different section, must not leak in
            };

            List<Finding> findings = DifficultyFlags.FlagDifficultySpikes(sections, deaths);

            Assert.AreEqual(1, findings.Count);
            Assert.AreEqual(2, findings[0].SectionIndex);
            Assert.AreEqual(20, findings[0].DeathCount);
            Assert.AreEqual(2f, findings[0].NeighborBaseline); // neighbors both 2 deaths
            CollectionAssert.AreEquivalent(new[] { "e1", "e2" }, findings[0].EvidenceEpisodeIds);
        }

        [Test]
        public void DifficultyFlags_EdgeSections_UseSingleNeighbor()
        {
            // First section (index 0) has only a right neighbor; last section has only a left neighbor.
            List<SectionStats> sections = new List<SectionStats>
            {
                new SectionStats { SectionIndex = 0, DeathCount = 10 },
                new SectionStats { SectionIndex = 1, DeathCount = 1 },
                new SectionStats { SectionIndex = 2, DeathCount = 10 },
            };

            List<Finding> findings = DifficultyFlags.FlagDifficultySpikes(sections, new List<DeathRecord>());

            // Section 0: neighbor median = 1 (section 1) -> baseline max(1,1)=1, 10 > 2*1 -> flagged.
            // Section 2: same reasoning -> flagged.
            Assert.AreEqual(2, findings.Count);
            Assert.AreEqual(0, findings[0].SectionIndex);
            Assert.AreEqual(2, findings[1].SectionIndex);
        }

        [Test]
        public void DifficultyFlags_ZeroMedianNeighbors_GuardedToBaselineOne()
        {
            List<SectionStats> sections = new List<SectionStats>
            {
                new SectionStats { SectionIndex = 0, DeathCount = 0 },
                new SectionStats { SectionIndex = 1, DeathCount = 3 }, // > 2 * max(0,1) = 2
                new SectionStats { SectionIndex = 2, DeathCount = 0 },
            };

            List<Finding> findings = DifficultyFlags.FlagDifficultySpikes(sections, new List<DeathRecord>());
            Assert.AreEqual(1, findings.Count);
            Assert.AreEqual(1f, findings[0].NeighborBaseline);
        }

        // ---- HeatmapData ---------------------------------------------------------------------------------------

        [Test]
        public void HeatmapData_BucketsKnownPoints_ExactCounts()
        {
            List<DeathRecord> deaths = new List<DeathRecord>
            {
                new DeathRecord { X = 0.5f, Y = 0.5f },
                new DeathRecord { X = 0.9f, Y = 0.1f },
                new DeathRecord { X = 1.5f, Y = 0.5f },
            };

            Rect rect = new Rect(0f, 0f, 2f, 1f);
            HeatmapGrid grid = HeatmapData.Build(deaths, rect, 1f);

            Assert.AreEqual(2, grid.Width);
            Assert.AreEqual(1, grid.Height);
            Assert.AreEqual(2, grid.Counts[0, 0]); // both points in [0,1)x[0,1)
            Assert.AreEqual(1, grid.Counts[1, 0]); // 1.5 in [1,2)
        }

        [Test]
        public void HeatmapData_BoundaryPoint_FallsIntoLowerCell()
        {
            List<DeathRecord> deaths = new List<DeathRecord> { new DeathRecord { X = 1f, Y = 0f } };
            Rect rect = new Rect(0f, 0f, 2f, 1f);
            HeatmapGrid grid = HeatmapData.Build(deaths, rect, 1f);

            // x==1.0 is the shared edge between cell 0 and cell 1: floor((1-0)/1)=1 -> cell index 1.
            Assert.AreEqual(1, grid.Counts[1, 0]);
            Assert.AreEqual(0, grid.Counts[0, 0]);
        }

        [Test]
        public void HeatmapData_PointAtRectMaxEdge_ClampsToLastCell()
        {
            List<DeathRecord> deaths = new List<DeathRecord> { new DeathRecord { X = 2f, Y = 0.5f } };
            Rect rect = new Rect(0f, 0f, 2f, 1f);
            HeatmapGrid grid = HeatmapData.Build(deaths, rect, 1f);

            // x==2.0 is rect.xMax; floor((2-0)/1)=2 which is out of range for Width=2 -> clamp to last column (1).
            Assert.AreEqual(1, grid.Counts[1, 0]);
        }

        [Test]
        public void HeatmapData_PointOutsideRect_Dropped()
        {
            List<DeathRecord> deaths = new List<DeathRecord> { new DeathRecord { X = 5f, Y = 5f } };
            Rect rect = new Rect(0f, 0f, 2f, 1f);
            HeatmapGrid grid = HeatmapData.Build(deaths, rect, 1f);

            int total = 0;
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    total += grid.Counts[x, y];
            Assert.AreEqual(0, total);
        }

        // ---- DeathLogger ---------------------------------------------------------------------------------------

        sealed class FakeDeathAdapter : IGameAdapter
        {
            public bool DeadNow;
            public void Bind(Arena arena, ScenarioConfig scenario) { }
            public void ResetEpisode(int seed) { }
            public void ApplyAction(in PlayerAction action) { }
            public void TickSimulation(float dt) { }
            public void ReadObservation(Observation target) { }
            public void AfterStep(int tick) { }
            public bool IsDead => DeadNow;
            public bool IsComplete => false;
            public float Progress => 0f;
            public void DrainEvents(List<TimedGameEvent> into) { }
            public void RestoreOverrides() { }
        }

        [Test]
        public void DeathLogger_RecordsOnce_OnDeadTransition_NotEveryDeadTick()
        {
            FakeDeathAdapter adapter = new FakeDeathAdapter();
            DeathLogger logger = new DeathLogger(adapter, "ep1");
            Observation obs = new Observation { Position = new Vector2(3f, 4f), SectionIndex = 2 };

            adapter.DeadNow = false;
            logger.OnStep(0, PlayerAction.Neutral, obs);
            adapter.DeadNow = true;
            logger.OnStep(1, PlayerAction.Neutral, obs);
            logger.OnStep(2, PlayerAction.Neutral, obs); // still dead, must not double-record
            adapter.DeadNow = false;
            logger.OnStep(3, PlayerAction.Neutral, obs);
            adapter.DeadNow = true;
            logger.OnStep(4, PlayerAction.Neutral, obs); // second death

            Assert.AreEqual(2, logger.Records.Count);
            Assert.AreEqual(1, logger.Records[0].Tick);
            Assert.AreEqual(3f, logger.Records[0].X);
            Assert.AreEqual(4f, logger.Records[0].Y);
            Assert.AreEqual(2, logger.Records[0].SectionIndex);
            Assert.AreEqual(4, logger.Records[1].Tick);
        }
    }
}
