namespace PlatformerPlaytest
{
    /// <summary>
    /// Game-owned scenario discovery seam. The reusable runner never infers spawn points, objectives, sections,
    /// procedural seeds, or timing from scene names or coordinates. An adapter package supplies that knowledge for
    /// its game and returns a runtime ScenarioConfig after the arena has loaded.
    /// </summary>
    public interface IScenarioProvider
    {
        string ScenarioId { get; }
        ScenarioConfig CreateScenario(Arena arena, int layoutSeed);
    }
}
