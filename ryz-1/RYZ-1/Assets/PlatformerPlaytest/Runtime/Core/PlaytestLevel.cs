namespace PlatformerPlaytest
{
    /// <summary>T12: which level a Watch/Run action targets. SampleScene is the real authored level (Play Mode
    /// only, loaded via ArenaManager.LoadSceneArena); Demo is the small in-code level used since T8/T10.</summary>
    public enum PlaytestLevel
    {
        SampleScene,
        Demo
    }

    public static class PlaytestLevelExtensions
    {
        /// <summary>run.json / episode scenarioId for this level — Results/Watch use this string to tell real and
        /// demo runs apart, so it must match SampleSceneScenario's identity exactly ("sample-scene").</summary>
        public static string ScenarioId(this PlaytestLevel level) =>
            level == PlaytestLevel.SampleScene ? "sample-scene" : "demo-level";

        public static PlaytestLevel FromScenarioId(string scenarioId) =>
            scenarioId != null && scenarioId.StartsWith("sample-scene")
                ? PlaytestLevel.SampleScene
                : PlaytestLevel.Demo;
    }
}
