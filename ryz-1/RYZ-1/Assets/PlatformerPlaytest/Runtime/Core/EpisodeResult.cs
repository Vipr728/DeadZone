using System.Collections.Generic;

namespace PlatformerPlaytest
{
    /// <summary>How an episode ended.</summary>
    public enum Outcome
    {
        /// <summary>Default: episode ran out of step budget without completing.</summary>
        StepBudgetExceeded = 0,
        Completed,
        Cancelled,

        /// <summary>The agent gave up (e.g. a profile exhausted its persistence retries) before the budget ran out.</summary>
        Abandoned
    }

    /// <summary>Optional agent capability: lets an agent end its own episode early instead of burning the budget.</summary>
    public interface IAbandonable
    {
        /// <summary>True once the agent has stopped trying; EpisodeRunner ends the episode as <see cref="Outcome.Abandoned"/>.</summary>
        bool Abandoned { get; }
    }

    /// <summary>Summary result of a single episode run.</summary>
    public class EpisodeResult
    {
        public Outcome Outcome;
        public int Steps;
        public int Deaths;
        public float FurthestProgress;
        public int CompletionTick = -1;
        public int CheckpointsReached;
        public readonly List<TimedGameEvent> Events = new List<TimedGameEvent>();
    }
}
