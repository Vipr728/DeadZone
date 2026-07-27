namespace PlatformerPlaytest
{
    /// <summary>A decision-maker over a game-owned observation/action schema, never Unity scene objects.</summary>
    public interface IAgent<TAction, TObservation>
    {
        /// <summary>Called once per episode before the first tick.</summary>
        void OnEpisodeStart(int seed);

        /// <summary>Produce this tick's action from the current observation.</summary>
        TAction Act(TObservation obs, int tick);
    }

    /// <summary>Bundled CelesteBenchmark agent profile.</summary>
    public interface IAgent : IAgent<PlayerAction, Observation> { }
}
