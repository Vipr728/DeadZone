using System.Collections.Generic;

namespace PlatformerPlaytest.Analysis
{
    /// <summary>
    /// A difficulty-spike finding. Wording rule: findings carry only numbers and episode ids — never a subjective
    /// claim ("too hard", "broken") — so report surfaces can quote them verbatim.
    /// </summary>
    public struct Finding
    {
        public int SectionIndex;
        public int DeathCount;
        public float NeighborBaseline;
        public List<string> EvidenceEpisodeIds;

        /// <summary>Numbers-only sentence, e.g. "Section 3: 12 deaths vs neighbor baseline 4.0 (episodes: e1, e2, e3)."</summary>
        public string ToEvidenceText()
        {
            string ids = EvidenceEpisodeIds != null ? string.Join(", ", EvidenceEpisodeIds) : "";
            return $"Section {SectionIndex}: {DeathCount} deaths vs neighbor baseline {NeighborBaseline:F1} (episodes: {ids}).";
        }
    }

    public static class DifficultyFlags
    {
        /// <summary>
        /// Flags sections whose deathCount exceeds threshold * max(neighborMedian, 1). Neighbors are the immediate
        /// left/right sections (edge sections use whichever single neighbor exists; a section with no neighbors
        /// never flags).
        /// </summary>
        public static List<Finding> FlagDifficultySpikes(List<SectionStats> sections, List<DeathRecord> deaths, float threshold = 2.0f)
        {
            List<Finding> findings = new List<Finding>();
            if (sections == null || sections.Count == 0)
                return findings;

            for (int i = 0; i < sections.Count; i++)
            {
                List<int> neighborCounts = new List<int>();
                if (i > 0)
                    neighborCounts.Add(sections[i - 1].DeathCount);
                if (i < sections.Count - 1)
                    neighborCounts.Add(sections[i + 1].DeathCount);

                if (neighborCounts.Count == 0)
                    continue;

                float neighborMedian = MedianInt(neighborCounts);
                float baseline = neighborMedian > 1f ? neighborMedian : 1f;

                if (sections[i].DeathCount > threshold * baseline)
                {
                    findings.Add(new Finding
                    {
                        SectionIndex = sections[i].SectionIndex,
                        DeathCount = sections[i].DeathCount,
                        NeighborBaseline = baseline,
                        EvidenceEpisodeIds = CollectEvidence(deaths, sections[i].SectionIndex)
                    });
                }
            }

            return findings;
        }

        static List<string> CollectEvidence(List<DeathRecord> deaths, int sectionIndex)
        {
            List<string> ids = new List<string>();
            if (deaths == null)
                return ids;
            for (int i = 0; i < deaths.Count; i++)
            {
                if (deaths[i].SectionIndex == sectionIndex && !ids.Contains(deaths[i].EpisodeId))
                    ids.Add(deaths[i].EpisodeId);
            }
            return ids;
        }

        static float MedianInt(List<int> values)
        {
            List<int> sorted = new List<int>(values);
            sorted.Sort();
            int n = sorted.Count;
            if (n % 2 == 1)
                return sorted[n / 2];
            return (sorted[n / 2 - 1] + sorted[n / 2]) / 2f;
        }
    }
}
