using UnityEngine;

namespace Playtester.Gym
{
    [CreateAssetMenu(fileName = "PieceLibraryConfig", menuName = "Playtester/Piece Library Config")]
    public sealed class PieceLibraryConfig : ScriptableObject
    {
        [field: SerializeField] public GameObject GapJumpPrefab { get; private set; } = null!;
        [field: SerializeField] public GameObject MoveToGoalPrefab { get; private set; } = null!;
        [field: SerializeField] public GameObject ElevationPrefab { get; private set; } = null!;
        [field: SerializeField] public bool EnableElevationPiece { get; private set; }
        [field: SerializeField, Min(1)] public int PiecesPerEpisode { get; private set; }
        [field: SerializeField] public bool BoundaryVelocityReset { get; private set; }
        [field: SerializeField] public Vector2 GapWidthRange { get; private set; }
        [field: SerializeField] public Vector2 MoveDistanceRange { get; private set; }
        [field: SerializeField] public Vector2 ElevationHeightRange { get; private set; }

#if UNITY_EDITOR
        public void SetGeneratedValues(
            bool elevationEnabled,
            int piecesPerEpisode,
            bool boundaryVelocityReset,
            Vector2 gapWidthRange,
            Vector2 moveDistanceRange,
            Vector2 elevationHeightRange)
        {
            EnableElevationPiece = elevationEnabled;
            PiecesPerEpisode = piecesPerEpisode;
            BoundaryVelocityReset = boundaryVelocityReset;
            GapWidthRange = gapWidthRange;
            MoveDistanceRange = moveDistanceRange;
            ElevationHeightRange = elevationHeightRange;
        }

        public void SetPrefabReferences(GameObject gapJump, GameObject moveToGoal, GameObject elevation)
        {
            GapJumpPrefab = gapJump;
            MoveToGoalPrefab = moveToGoal;
            ElevationPrefab = elevation;
        }
#endif
    }
}
