using UnityEngine;

namespace Playtester.Agent
{
    [CreateAssetMenu(fileName = "ObservationConfig", menuName = "Playtester/Observation Config")]
    public sealed class ObservationConfigAsset : ScriptableObject
    {
        [field: SerializeField, Min(1)] public int GridSize { get; private set; }
        [field: SerializeField] public bool IncludeVelocity { get; private set; }
        [field: SerializeField] public bool IncludeGroundedFlag { get; private set; }

#if UNITY_EDITOR
        public void SetGeneratedValues(int gridSize, bool includeVelocity, bool includeGroundedFlag)
        {
            GridSize = gridSize;
            IncludeVelocity = includeVelocity;
            IncludeGroundedFlag = includeGroundedFlag;
        }
#endif
    }
}
