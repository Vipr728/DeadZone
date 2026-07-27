using System;

namespace Ryzi
{
    [Serializable]
    public sealed class PlayerProfile
    {
        public string id;
        public string displayName;
        public bool synthetic = true;
        public int reactionDelayTicks;
        public float timingVariance;
        public float directionVariance;
        public int planningBudget;
        public int searchDepth;
        public string[] mechanicChannelIds = Array.Empty<string>();
        public float riskPreference;
        public float explorationPreference;
        public int retryPersistence;

        public static PlayerProfile Constrained => Create("constrained", "Constrained", 8, 12);
        public static PlayerProfile Standard => Create("standard", "Standard", 20, 32);
        public static PlayerProfile Precision => Create("precision", "Precision", 32, 50);

        static PlayerProfile Create(string id, string name, int budget, int depth)
        {
            return new PlayerProfile
            {
                id = id,
                displayName = name,
                planningBudget = budget,
                searchDepth = depth,
                retryPersistence = 3
            };
        }
    }
}
