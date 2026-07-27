using System.Collections.Generic;
using UnityEngine;

namespace PlatformerPlaytest.Analysis
{
    /// <summary>One recorded death: which episode, when, where, and in which section.</summary>
    public struct DeathRecord
    {
        public string EpisodeId;
        public int Tick;
        public float X;
        public float Y;
        public int SectionIndex;
    }

    /// <summary>Per-section death aggregate.</summary>
    public struct SectionStats
    {
        public int SectionIndex;
        public int DeathCount;
        /// <summary>DeathCount / total deaths across all sections (0 if there are none).</summary>
        public float FailureShare;
    }

    public static class SectionStatsCalculator
    {
        /// <summary>Buckets death records by section index. sectionCount must cover the highest SectionIndex present.</summary>
        public static List<SectionStats> Compute(List<DeathRecord> deaths, int sectionCount)
        {
            List<SectionStats> result = new List<SectionStats>(sectionCount);
            int[] counts = new int[sectionCount];
            int total = 0;

            if (deaths != null)
            {
                for (int i = 0; i < deaths.Count; i++)
                {
                    int s = deaths[i].SectionIndex;
                    if (s >= 0 && s < sectionCount)
                    {
                        counts[s]++;
                        total++;
                    }
                }
            }

            for (int s = 0; s < sectionCount; s++)
            {
                result.Add(new SectionStats
                {
                    SectionIndex = s,
                    DeathCount = counts[s],
                    FailureShare = total > 0 ? (float)counts[s] / total : 0f
                });
            }

            return result;
        }
    }

    /// <summary>
    /// Runtime capture path (MVP): subscribes to <see cref="EpisodeRunner"/>'s onStep callback and, driven by
    /// <see cref="IGameAdapter.IsDead"/> transitions, records one <see cref="DeathRecord"/> per death. Caller wires
    /// this in via the onStep delegate passed to <see cref="EpisodeRunner.Run"/>; DeathLogger itself has no
    /// dependency on EpisodeRunner beyond the callback signature, so it stays test-friendly with any fake adapter.
    /// </summary>
    public sealed class DeathLogger
    {
        readonly IGameAdapter adapter;
        readonly string episodeId;
        readonly List<DeathRecord> records = new List<DeathRecord>();

        bool wasDead;

        public DeathLogger(IGameAdapter adapter, string episodeId)
        {
            this.adapter = adapter;
            this.episodeId = episodeId;
        }

        public IReadOnlyList<DeathRecord> Records => records;

        /// <summary>Call once per tick, after TickSimulation/AfterStep, with the tick's observation (for position/section).</summary>
        public void OnStep(int tick, PlayerAction action, Observation obs)
        {
            bool isDead = adapter.IsDead;
            if (isDead && !wasDead)
            {
                records.Add(new DeathRecord
                {
                    EpisodeId = episodeId,
                    Tick = tick,
                    X = obs.Position.x,
                    Y = obs.Position.y,
                    SectionIndex = obs.SectionIndex
                });
            }
            wasDead = isDead;
        }
    }
}
