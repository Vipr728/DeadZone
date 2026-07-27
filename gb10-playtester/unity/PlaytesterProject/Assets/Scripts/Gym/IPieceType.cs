using UnityEngine;

namespace Playtester.Gym
{
    public interface IPieceType
    {
        void Configure(PieceParams parameters);
        Bounds GetLocalBounds();
        Transform GetLocalGoal();
    }
}
