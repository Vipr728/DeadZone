using System.Collections.Generic;
using UnityEngine;

namespace PlatformerPlaytest
{
    /// <summary>Classification of a single occupancy-grid cell.</summary>
    public enum CellKind
    {
        Empty,
        Solid,
        OneWay,
        Hazard
    }

    /// <summary>Kind of a dynamic entity reported in the observation's nearby-entity list.</summary>
    public enum DynamicEntityKind
    {
        MovingPlatform,
        Spring,
        DashRefill,
        Goal,
        Checkpoint
    }

    /// <summary>A single nearby dynamic object relevant to agent decision-making.</summary>
    public struct DynamicEntity
    {
        public DynamicEntityKind Kind;
        public Vector2 Position;
        public Vector2 Velocity;
        public Vector2 Size;
    }

    /// <summary>
    /// Allocated-once, mutated-per-tick snapshot of player state, local occupancy grid, and nearby dynamic entities.
    /// Grid is 16x12 cells (1 unit each), centered on the player.
    /// </summary>
    public sealed class Observation
    {
        public const int GridWidth = 16;
        public const int GridHeight = 12;

        // Player
        public Vector2 Position;
        public Vector2 Velocity;
        public bool IsGrounded;
        public bool OnLeftWall;
        public bool OnRightWall;
        public bool IsDashing;
        public bool IsClimbing;
        public int DashesRemaining;
        public float Stamina;
        public float Progress;
        public int SectionIndex;

        // World
        public readonly CellKind[,] Grid = new CellKind[GridWidth, GridHeight];
        public readonly List<DynamicEntity> Entities = new List<DynamicEntity>(16);

        public void ClearGrid()
        {
            for (int x = 0; x < GridWidth; x++)
                for (int y = 0; y < GridHeight; y++)
                    Grid[x, y] = CellKind.Empty;
        }
    }
}
