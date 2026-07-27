using System.Collections.Generic;
using PlatformerPlaytest.Analysis;
using UnityEditor;
using UnityEngine;

namespace PlatformerPlaytest.Editor
{
    /// <summary>
    /// SceneView death-position overlay. Explicit opt-in only (no [InitializeOnLoad]): the Results tab toggles
    /// <see cref="SetEnabled"/>, which subscribes/unsubscribes <see cref="Draw"/> from
    /// <see cref="SceneView.duringSceneGui"/>. <see cref="PlaytestWindow"/> also unsubscribes on window close so no
    /// handler is ever left attached after the window that owns the data disappears.
    /// </summary>
    public static class DeathHeatmapOverlay
    {
        const float CellSize = 1f;
        const float BaseRadius = 0.35f;

        static List<DeathRecord> currentDeaths;

        public static bool IsEnabled { get; private set; }

        public static void SetEnabled(bool enabled, List<DeathRecord> deaths)
        {
            currentDeaths = deaths;
            if (enabled == IsEnabled)
                return;

            IsEnabled = enabled;
            if (enabled)
                SceneView.duringSceneGui += Draw;
            else
                SceneView.duringSceneGui -= Draw;
        }

        public static void Disable()
        {
            if (!IsEnabled)
                return;
            IsEnabled = false;
            SceneView.duringSceneGui -= Draw;
        }

        static void Draw(SceneView sceneView)
        {
            if (currentDeaths == null || currentDeaths.Count == 0)
                return;

            Rect bounds = ComputeBounds(currentDeaths);
            HeatmapGrid grid = HeatmapData.Build(currentDeaths, bounds, CellSize);

            int maxCount = 1;
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    if (grid.Counts[x, y] > maxCount) maxCount = grid.Counts[x, y];

            for (int x = 0; x < grid.Width; x++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    int count = grid.Counts[x, y];
                    if (count == 0)
                        continue;

                    float alpha = Mathf.Clamp01((float)count / maxCount);
                    Vector3 center = new Vector3(
                        grid.Origin.x + (x + 0.5f) * CellSize,
                        grid.Origin.y + (y + 0.5f) * CellSize,
                        0f);

                    Handles.color = new Color(1f, 0f, 0f, 0.15f + 0.65f * alpha);
                    Handles.DrawSolidDisc(center, Vector3.forward, BaseRadius + BaseRadius * alpha);
                }
            }
        }

        static Rect ComputeBounds(List<DeathRecord> deaths)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < deaths.Count; i++)
            {
                DeathRecord d = deaths[i];
                if (d.X < minX) minX = d.X;
                if (d.X > maxX) maxX = d.X;
                if (d.Y < minY) minY = d.Y;
                if (d.Y > maxY) maxY = d.Y;
            }
            // Pad by one cell so single-point/edge deaths still get a bucket.
            return new Rect(minX - CellSize, minY - CellSize, (maxX - minX) + 2f * CellSize, (maxY - minY) + 2f * CellSize);
        }
    }
}
