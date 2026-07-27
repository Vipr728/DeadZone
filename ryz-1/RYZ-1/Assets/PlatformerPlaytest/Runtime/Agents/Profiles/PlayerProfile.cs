using UnityEngine;

namespace PlatformerPlaytest.Profiles
{
    /// <summary>
    /// Plain, serializable copy of a profile's tunables — exists so tests and runtime code can build/compare
    /// profiles without needing a ScriptableObject asset. See <see cref="PlayerProfile"/> for the disclaimer
    /// that applies to every value here.
    /// </summary>
    [System.Serializable]
    public struct ProfileParams
    {
        public int reactionDelayTicks;
        public float timingVarianceTicksSigma;
        public float directionErrorProb;
        public int planningHorizon;         // solver MaxMacrosDepth cap
        public bool knowsWallJumpChains;     // MVP boolean stand-in for mechanicKnowledge
        public float repeatFailedStrategyProb;
        public int persistenceRetries;

        /// <summary>How far (world units) the agent may drift from its reference trajectory before it notices and
        /// re-anchors. Sloppier profiles notice later. 0 falls back to <see cref="DefaultDeviationTolerance"/>.</summary>
        public float deviationToleranceUnits;

        public const float DefaultDeviationTolerance = 1.5f;

        // Horizon buckets from the spec table (short/medium/full), fixed by ADR-level agreement in the T6 task.
        public const int HorizonShort = 12;
        public const int HorizonMedium = 30;
        public const int HorizonFull = 60;

        public static ProfileParams Beginner => new ProfileParams
        {
            reactionDelayTicks = 12,
            timingVarianceTicksSigma = 4f,
            directionErrorProb = 0.15f,
            planningHorizon = HorizonShort,
            knowsWallJumpChains = false,
            repeatFailedStrategyProb = 0.5f,
            persistenceRetries = 8,
            deviationToleranceUnits = 2.5f
        };

        public static ProfileParams Intermediate => new ProfileParams
        {
            reactionDelayTicks = 6,
            timingVarianceTicksSigma = 2f,
            directionErrorProb = 0.05f,
            planningHorizon = HorizonMedium,
            knowsWallJumpChains = true, // "all, unreliable" in spec; MVP has no reliability dial beyond this bool
            repeatFailedStrategyProb = 0.2f,
            persistenceRetries = 15,
            deviationToleranceUnits = 1.5f
        };

        public static ProfileParams Expert => new ProfileParams
        {
            reactionDelayTicks = 2,
            timingVarianceTicksSigma = 0.5f,
            directionErrorProb = 0.01f,
            planningHorizon = HorizonFull,
            knowsWallJumpChains = true,
            repeatFailedStrategyProb = 0.05f,
            persistenceRetries = 40,
            deviationToleranceUnits = 0.8f
        };
    }

    /// <summary>
    /// SYNTHETIC PROFILE — DISCLAIMER: this is a parameterized degradation of a solver-produced plan, NOT a
    /// validated model of a real human player. It exists to stress-test the tool's reporting surfaces before
    /// human demonstration data is available. Every report surface that consumes profile-labeled runs must carry
    /// this disclaimer forward until profiles are calibrated against real demos (see docs/specifications/player-profiles.md).
    /// </summary>
    [CreateAssetMenu(menuName = "PlatformerPlaytest/Player Profile", fileName = "NewPlayerProfile")]
    public sealed class PlayerProfile : ScriptableObject
    {
        [SerializeField] ProfileParams parameters;

        public ProfileParams Parameters => parameters;

        public static PlayerProfile CreateRuntime(ProfileParams parameters)
        {
            PlayerProfile profile = CreateInstance<PlayerProfile>();
            profile.parameters = parameters;
            return profile;
        }
    }
}
