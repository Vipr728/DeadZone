using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.Tilemaps;
using Playtester.Gym;

namespace Playtester.Agent
{
    public sealed class GridObservationEncoder : MonoBehaviour, IObservationEncoder
    {
        [SerializeField] private ObservationConfigAsset observationConfig = null!;

        public void Encode(VectorSensor sensor, Rigidbody2D playerBody, PlayerController playerController, Tilemap tilemap, Transform currentGoal)
        {
            int radius = observationConfig.GridSize / 2;
            Vector3Int center = tilemap.WorldToCell(playerBody.position);
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    TileBase tile = tilemap.GetTile(center + new Vector3Int(x, y, 0));
                    sensor.AddOneHotObservation(TileChannel(tile), 4);
                }
            }
            Vector2 goalDelta = currentGoal == null
                ? Vector2.zero
                : (Vector2)currentGoal.position - playerBody.position;
            float normalizationScale = Mathf.Max(1f, observationConfig.GridSize);
            Vector2 normalizedGoalDelta = goalDelta / normalizationScale;
            sensor.AddObservation(normalizedGoalDelta);
            if (observationConfig.IncludeVelocity)
            {
                sensor.AddObservation(playerBody.linearVelocity);
            }
            if (observationConfig.IncludeGroundedFlag)
            {
                sensor.AddObservation(playerController.IsGrounded());
            }
            sensor.AddObservation(goalDelta.magnitude / normalizationScale);
            sensor.AddObservation(goalDelta.sqrMagnitude > 0f ? goalDelta.normalized.x : 0f);
        }

        private static int TileChannel(TileBase tile)
        {
            if (tile == null) return 0;
            if (tile is HazardTile) return 2;
            if (tile.name.Contains("Goal")) return 3;
            return 1;
        }
    }
}
