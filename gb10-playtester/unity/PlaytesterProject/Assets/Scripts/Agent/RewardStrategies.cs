namespace Playtester.Agent
{
    public interface IRewardStrategy
    {
        float PieceProgressReward(float deltaProgress);
        float StepTimePenalty();
        float PieceCompletionBonus();
        float FinalSequenceBonus();
        float DeathPenalty();
    }

    public sealed class CompositionalRewardStrategy : IRewardStrategy
    {
        private readonly RewardConfigAsset config;
        public CompositionalRewardStrategy(RewardConfigAsset config) => this.config = config;
        public float PieceProgressReward(float deltaProgress) => config.ProgressRewardScale * System.MathF.Max(0f, deltaProgress);
        public float StepTimePenalty() => config.TimePenalty;
        public float PieceCompletionBonus() => config.PieceCompletionBonus;
        public float FinalSequenceBonus() => config.FinalSequenceBonus;
        public float DeathPenalty() => config.DeathPenalty;
    }

    public sealed class SingleGymFallbackStrategy : IRewardStrategy
    {
        private readonly RewardConfigAsset config;
        public SingleGymFallbackStrategy(RewardConfigAsset config) => this.config = config;
        public float PieceProgressReward(float deltaProgress) => config.ProgressRewardScale * System.MathF.Max(0f, deltaProgress);
        public float StepTimePenalty() => config.TimePenalty;
        public float PieceCompletionBonus() => config.PieceCompletionBonus;
        public float FinalSequenceBonus() => 0f;
        public float DeathPenalty() => config.DeathPenalty;
    }
}
