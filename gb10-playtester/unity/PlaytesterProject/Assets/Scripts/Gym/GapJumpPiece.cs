using UnityEngine;

namespace Playtester.Gym
{
    public sealed class GapJumpPiece : MonoBehaviour, IPieceType
    {
        [SerializeField] private Transform localGoal = null!;
        private PieceParams parameters;

        public void Configure(PieceParams value)
        {
            parameters = value;
            localGoal.localPosition = new Vector3(value.Width, 0f, 0f);
        }

        public Bounds GetLocalBounds() => new(Vector3.right * parameters.Width * 0.5f, new Vector3(parameters.Width, 1f, 1f));
        public Transform GetLocalGoal() => localGoal;
    }
}
