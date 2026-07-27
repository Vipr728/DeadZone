using UnityEngine;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Sample project wiring only. Geometry is discovered from the loaded arena by IScenarioProvider; this class
    /// no longer contains spawn, goal, or checkpoint coordinates.
    /// </summary>
    public static class SampleSceneScenario
    {
        public const string ScenePath = "Assets/Scenes/SampleScene.unity";

        /// <summary>Solver policy belongs to the selected adapter/plugin, not the level geometry.</summary>
        public static Solver.SolverConfig SolverConfig => new Solver.SolverConfig
        {
            BeamWidth = 20,
            MaxMacrosDepth = 50,
            Seed = 0,
            TickMenu = new[] { 4, 8, 16, 32 },
            MaxTicksSimulated = 4_000_000,
            FixedDeltaTime = 0f,
            TargetX = float.NaN,
            TargetY = float.NaN,
            TargetYTolerance = 1.25f,
            PrefixActions = null
        };

        public static IScenarioProvider Provider() =>
            new CelesteBenchmarkScenarioProvider(PlaytestLevel.SampleScene.ScenarioId());

        public static ScenarioConfig Create(Arena arena, int layoutSeed) =>
            Provider().CreateScenario(arena, layoutSeed);
    }
}
