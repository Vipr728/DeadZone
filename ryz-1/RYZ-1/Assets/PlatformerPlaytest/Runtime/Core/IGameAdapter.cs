using System.Collections.Generic;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Mechanics-agnostic simulator contract. A game chooses its own action and observation types; the core tick
    /// loop only owns lifecycle, cancellation, outcomes, and event collection.
    /// </summary>
    public interface IGameAdapter<TAction, TObservation>
    {
        /// <summary>Bind to an arena's contents (find player/level actors) and remember the scenario.</summary>
        void Bind(Arena arena, ScenarioConfig scenario);

        /// <summary>Reset player/dynamics to spawn state for a fresh episode. Seed is stored, not yet used for RNG.</summary>
        void ResetEpisode(int seed);

        /// <summary>Apply this tick's agent action to the simulator, before physics.</summary>
        void ApplyAction(in TAction action);

        /// <summary>Advance simulator logic (player Tick) then physics by dt. Call instead of Arena.Step directly.</summary>
        void TickSimulation(float dt);

        /// <summary>Fill the observation from current simulator state. Call after TickSimulation.</summary>
        void ReadObservation(TObservation target);

        /// <summary>Drain and clear queued events, run edge-only input cleanup, update per-tick death/complete flags.</summary>
        void AfterStep(int tick);

        /// <summary>True if a death event fired within the current AfterStep window.</summary>
        bool IsDead { get; }

        /// <summary>True once the game-specific completion objective has been satisfied.</summary>
        bool IsComplete { get; }

        /// <summary>Game-defined normalized progress used for reporting; it need not be horizontal or linear.</summary>
        float Progress { get; }

        /// <summary>Append all events queued since the last drain into the given list, then clear the queue.</summary>
        void DrainEvents(List<TimedGameEvent> into);

        /// <summary>Undo any tunable overrides applied for this episode/run. No-op until T8.</summary>
        void RestoreOverrides();
    }

    /// <summary>
    /// Built-in action/observation profile used by the CelesteBenchmark adapter and bundled solvers. Platformers
    /// with different mechanics implement IGameAdapter&lt;TAction,TObservation&gt; and supply matching agents.
    /// </summary>
    public interface IGameAdapter : IGameAdapter<PlayerAction, Observation> { }
}
