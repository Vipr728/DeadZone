using UnityEngine;

namespace Playtester.Gym
{
    public sealed class ElevationPiece : MonoBehaviour, IPieceType
    {
        [SerializeField] private Transform localGoal = null!;
        private PieceParams parameters;

        public void Configure(PieceParams value)
        {
            parameters = value;
            localGoal.localPosition = new Vector3(0f, value.Height, 0f);
        }

        public Bounds GetLocalBounds() => new(Vector3.up * parameters.Height * 0.5f, new Vector3(1f, parameters.Height, 1f));
        public Transform GetLocalGoal() => localGoal;
    }
}
