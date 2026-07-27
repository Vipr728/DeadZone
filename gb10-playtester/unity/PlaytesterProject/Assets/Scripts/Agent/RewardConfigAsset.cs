using UnityEngine;

namespace Playtester.Agent
{
    [CreateAssetMenu(fileName = "RewardConfig", menuName = "Playtester/Reward Config")]
    public sealed class RewardConfigAsset : ScriptableObject
    {
        [field: SerializeField] public string ActiveStrategy { get; private set; } = string.Empty;
        [field: SerializeField] public float ProgressRewardScale { get; private set; }
        [field: SerializeField] public float TimePenalty { get; private set; }
        [field: SerializeField] public float PieceCompletionBonus { get; private set; }
        [field: SerializeField] public float FinalSequenceBonus { get; private set; }
        [field: SerializeField] public float DeathPenalty { get; private set; }
        [field: SerializeField, Min(1)] public int MaxSteps { get; private set; }

#if UNITY_EDITOR
        public void SetGeneratedValues(
            string activeStrategy,
            float progressRewardScale,
            float timePenalty,
            float pieceCompletionBonus,
            float finalSequenceBonus,
            float deathPenalty,
            int maxSteps)
        {
            ActiveStrategy = activeStrategy;
            ProgressRewardScale = progressRewardScale;
            TimePenalty = timePenalty;
            PieceCompletionBonus = pieceCompletionBonus;
            FinalSequenceBonus = finalSequenceBonus;
            DeathPenalty = deathPenalty;
            MaxSteps = maxSteps;
        }
#endif
    }
}
