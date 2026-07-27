using System.Collections.Generic;
using UnityEngine;

namespace PlatformerPlaytest.Analysis
{
    /// <summary>Grid bucket counts of death positions over a world-space rect, for editor overlay + tests.</summary>
    public struct HeatmapGrid
    {
        public int[,] Counts;
        public Vector2 Origin;
        public float CellSize;
        public int Width;
        public int Height;
    }

    public static class HeatmapData
    {
        /// <summary>
        /// Buckets death positions into a Width x Height grid of cellSize cells covering worldRect. Points outside
        /// worldRect are dropped. Boundary points (exactly on a cell edge) fall into the cell whose min corner they
        /// touch, i.e. bucket = floor((pos - origin) / cellSize), clamped to the last row/column at the rect's max edge.
        /// </summary>
        public static HeatmapGrid Build(List<DeathRecord> deaths, Rect worldRect, float cellSize)
        {
            int width = Mathf.Max(1, Mathf.CeilToInt(worldRect.width / cellSize));
            int height = Mathf.Max(1, Mathf.CeilToInt(worldRect.height / cellSize));
            Vector2 origin = worldRect.min;

            HeatmapGrid grid = new HeatmapGrid
            {
                Counts = new int[width, height],
                Origin = origin,
                CellSize = cellSize,
                Width = width,
                Height = height
            };

            if (deaths == null)
                return grid;

            for (int i = 0; i < deaths.Count; i++)
            {
                float x = deaths[i].X;
                float y = deaths[i].Y;
                if (x < worldRect.xMin || x > worldRect.xMax || y < worldRect.yMin || y > worldRect.yMax)
                    continue;

                int cx = Mathf.FloorToInt((x - origin.x) / cellSize);
                int cy = Mathf.FloorToInt((y - origin.y) / cellSize);
                if (cx >= width) cx = width - 1;
                if (cy >= height) cy = height - 1;
                if (cx < 0) cx = 0;
                if (cy < 0) cy = 0;

                grid.Counts[cx, cy]++;
            }

            return grid;
        }
    }
}
