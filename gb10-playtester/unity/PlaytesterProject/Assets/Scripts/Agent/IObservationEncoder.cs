using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Playtester.Agent
{
    public interface IObservationEncoder
    {
        void Encode(VectorSensor sensor, Rigidbody2D playerBody, PlayerController playerController, Tilemap tilemap, Transform currentGoal);
    }
}
