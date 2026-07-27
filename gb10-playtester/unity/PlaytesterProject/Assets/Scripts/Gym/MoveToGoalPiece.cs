using UnityEngine;

namespace Playtester.Gym
{
    public sealed class MoveToGoalPiece : MonoBehaviour, IPieceType
    {
        [SerializeField] private Transform localGoal = null!;
        private PieceParams parameters;

        public void Configure(PieceParams value)
        {
            parameters = value;
            localGoal.localPosition = new Vector3(value.Distance, 0f, 0f);
        }

        public Bounds GetLocalBounds() => new(Vector3.right * parameters.Distance * 0.5f, new Vector3(parameters.Distance, 1f, 1f));
        public Transform GetLocalGoal() => localGoal;
    }
}
