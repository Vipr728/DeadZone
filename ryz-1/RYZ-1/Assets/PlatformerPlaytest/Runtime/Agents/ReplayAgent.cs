using System.Collections.Generic;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Deterministic playback agent: replays a recorded action stream, indexed by tick. Any tick beyond the
    /// recorded stream (or a negative tick) yields <see cref="PlayerAction.Neutral"/>, matching replay.md's
    /// "missing tick = neutral action" rule. Ignores the observation entirely — replay is input-driven.
    /// </summary>
    public sealed class ReplayAgent : IAgent
    {
        readonly IReadOnlyList<PlayerAction> actionsByTick;

        public ReplayAgent(IReadOnlyList<PlayerAction> actionsByTick)
        {
            this.actionsByTick = actionsByTick ?? new List<PlayerAction>();
        }

        public void OnEpisodeStart(int seed) { }

        public PlayerAction Act(Observation obs, int tick)
        {
            if (tick >= 0 && tick < actionsByTick.Count)
                return actionsByTick[tick];
            return PlayerAction.Neutral;
        }
    }
}
