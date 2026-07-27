namespace PlatformerPlaytest
{
    /// <summary>
    /// Kinds of discrete gameplay events an adapter can report. MVP only ever emits Death and GoalReached
    /// (see CelesteBenchmarkAdapter); CheckpointReached/DashRefillCollected/SpringBounced are reserved for a
    /// later seam task that adds adapter-owned sensors.
    /// </summary>
    public enum GameEventKind
    {
        Death,
        CheckpointReached,
        DashRefillCollected,
        SpringBounced,
        GoalReached
    }

    /// <summary>A gameplay event tagged with the tick it occurred on.</summary>
    public struct TimedGameEvent
    {
        public int Tick;
        public GameEventKind Kind;
    }
}
