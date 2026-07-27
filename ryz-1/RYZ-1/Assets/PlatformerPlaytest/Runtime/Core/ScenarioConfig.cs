using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PlatformerPlaytest
{
    /// <summary>A deferred (T8) counterfactual tunable override; application logic lands later.</summary>
    [Serializable]
    public class TunableOverride
    {
        public string targetId;
        public string field;
        public float value;
    }

    /// <summary>
    /// Runtime scenario snapshot produced by an IScenarioProvider. The bundled Celeste profile uses the spatial
    /// fields below; custom adapters may treat them as optional and keep game-specific metadata in their provider.
    /// </summary>
    [CreateAssetMenu(fileName = "ScenarioConfig", menuName = "PlatformerPlaytest/Scenario Config")]
    public class ScenarioConfig : ScriptableObject
    {
        public string scenarioId;
        public int layoutSeed;
        public Vector2 spawnPosition;
        public Rect goalRect;
        public float[] sectionBoundariesX = Array.Empty<float>();
        /// <summary>
        /// Optional intended standing height for each x boundary. When this array has the same length as
        /// sectionBoundariesX, segmented solving requires both coordinates. Keeping x and y in parallel arrays
        /// preserves existing ScenarioConfig assets and x-only scenarios while letting a spatial provider prevent
        /// falling or off-route states from satisfying checkpoints through horizontal drift alone.
        /// </summary>
        public float[] sectionBoundariesY = Array.Empty<float>();
        public float sectionBoundaryYTolerance = 1.25f;
        public int stepBudget = 6000;
        public float fixedDeltaTime = 0.02f;
        public List<TunableOverride> overrides = new List<TunableOverride>();

        /// <summary>
        /// Stable cache identity derived from discovered level metadata. A changed goal, spawn, checkpoint route, or
        /// procedural seed cannot accidentally reuse an action stream solved against different geometry.
        /// </summary>
        public string CacheIdentity(string fallbackId = null)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder value = new StringBuilder(128);
            value.Append(string.IsNullOrWhiteSpace(scenarioId) ? fallbackId ?? "scenario" : scenarioId);
            value.Append("|seed=").Append(layoutSeed);
            Append(value, spawnPosition.x, inv);
            Append(value, spawnPosition.y, inv);
            Append(value, goalRect.x, inv);
            Append(value, goalRect.y, inv);
            Append(value, goalRect.width, inv);
            Append(value, goalRect.height, inv);

            int count = sectionBoundariesX?.Length ?? 0;
            value.Append("|sections=").Append(count);
            for (int i = 0; i < count; i++)
            {
                Append(value, sectionBoundariesX[i], inv);
                if (sectionBoundariesY != null && i < sectionBoundariesY.Length)
                    Append(value, sectionBoundariesY[i], inv);
            }
            return value.ToString();
        }

        static void Append(StringBuilder into, float value, CultureInfo culture) =>
            into.Append('|').Append(value.ToString("R", culture));
    }
}
