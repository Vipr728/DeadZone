using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ryzi.Editor
{
    /// <summary>
    /// High-contrast SceneView rendering for recorded replays. It intentionally renders above scene geometry so
    /// an incomplete trace remains visible in dense levels and while playback advances through keyframes.
    /// </summary>
    public static class TrajectoryOverlay
    {
        static Vector2[] path = Array.Empty<Vector2>();
        static Vector2[] failures = Array.Empty<Vector2>();
        static int currentFrame = -1;
        static string terminalStatus = "Unknown";

        public static bool Enabled { get; private set; }

        public static void Show(Vector2[] recordedPath, Vector2[] failurePositions, int selectedFrame,
            string terminal)
        {
            path = recordedPath ?? Array.Empty<Vector2>();
            failures = failurePositions ?? Array.Empty<Vector2>();
            currentFrame = Mathf.Clamp(selectedFrame, -1, path.Length - 1);
            terminalStatus = string.IsNullOrEmpty(terminal) ? "Unknown" : terminal;
            if (!Enabled)
            {
                Enabled = true;
                SceneView.duringSceneGui += Draw;
            }
            Focus();
            SceneView.RepaintAll();
        }

        public static void SetCurrentFrame(int frame)
        {
            if (!Enabled)
                return;
            currentFrame = Mathf.Clamp(frame, -1, path.Length - 1);
            SceneView.RepaintAll();
        }

        public static void Focus()
        {
            if (path.Length == 0 && failures.Length == 0)
                return;

            bool initialized = false;
            Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
            AddPoints(path, ref bounds, ref initialized);
            AddPoints(failures, ref bounds, ref initialized);
            if (!initialized)
                return;

            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
                return;
            float size = Mathf.Max(12f, Mathf.Max(bounds.size.x, bounds.size.y) * 1.35f);
            view.LookAt(bounds.center, Quaternion.identity, size);
        }

        public static void Hide()
        {
            if (!Enabled)
                return;
            Enabled = false;
            SceneView.duringSceneGui -= Draw;
            SceneView.RepaintAll();
        }

        static void Draw(SceneView sceneView)
        {
            CompareFunction previousZTest = Handles.zTest;
            Color previousColor = Handles.color;
            Handles.zTest = CompareFunction.Always;
            try
            {
                DrawPath();
                DrawTerminalMarkers();
                DrawCurrentMarker();
                DrawLegend();
            }
            finally
            {
                Handles.zTest = previousZTest;
                Handles.color = previousColor;
            }
        }

        static void DrawPath()
        {
            if (path.Length == 0)
                return;

            Vector3[] points = new Vector3[path.Length];
            for (int i = 0; i < path.Length; i++)
                points[i] = path[i];

            if (points.Length > 1)
            {
                Handles.color = new Color(0.02f, 0.04f, 0.06f, 0.98f);
                Handles.DrawAAPolyLine(9f, points);
                Handles.color = new Color(0.08f, 0.88f, 1f, 1f);
                Handles.DrawAAPolyLine(5f, points);
                Handles.color = new Color(0.94f, 0.98f, 1f, 0.95f);
                Handles.DrawAAPolyLine(1.5f, points);
            }

            DrawDisc(path[0], new Color(0.15f, 1f, 0.45f, 1f), "START", 0.13f);
            if (path.Length == 1)
                DrawDisc(path[0], new Color(1f, 0.75f, 0.1f, 1f), "ONLY RECORDED FRAME", 0.18f);
        }

        static void DrawTerminalMarkers()
        {
            if (path.Length > 0)
            {
                Vector2 terminal = path[path.Length - 1];
                Color color = terminalStatus == "Completed"
                    ? new Color(0.2f, 1f, 0.4f, 1f)
                    : new Color(1f, 0.72f, 0.08f, 1f);
                DrawDisc(terminal, color, terminalStatus == "Completed" ? "COMPLETE" : "TRACE ENDS", 0.18f);
            }

            for (int i = 0; i < failures.Length; i++)
                DrawFailure(failures[i]);
        }

        static void DrawCurrentMarker()
        {
            if (currentFrame < 0 || currentFrame >= path.Length)
                return;
            Vector3 point = path[currentFrame];
            float size = HandleUtility.GetHandleSize(point) * 0.16f;
            Handles.color = new Color(1f, 1f, 1f, 1f);
            Handles.DrawWireDisc(point, Vector3.forward, size);
            Handles.color = new Color(0.05f, 0.25f, 0.95f, 1f);
            Handles.DrawWireDisc(point, Vector3.forward, size * 0.68f);
            Handles.Label(point + Vector3.up * (size * 1.15f), $"REPLAY FRAME {currentFrame + 1}/{path.Length}", LabelStyle());
        }

        static void DrawDisc(Vector2 position, Color color, string label, float scale)
        {
            Vector3 point = position;
            float size = HandleUtility.GetHandleSize(point) * scale;
            Handles.color = new Color(0f, 0f, 0f, 0.9f);
            Handles.DrawSolidDisc(point, Vector3.forward, size * 1.3f);
            Handles.color = color;
            Handles.DrawSolidDisc(point, Vector3.forward, size);
            Handles.Label(point + Vector3.up * (size * 1.35f), label, LabelStyle());
        }

        static void DrawFailure(Vector2 position)
        {
            Vector3 point = position;
            float size = HandleUtility.GetHandleSize(point) * 0.20f;
            Handles.color = new Color(0f, 0f, 0f, 0.92f);
            Handles.DrawSolidDisc(point, Vector3.forward, size * 1.35f);
            Handles.color = new Color(1f, 0.12f, 0.12f, 1f);
            Handles.DrawSolidDisc(point, Vector3.forward, size);
            Handles.color = Color.white;
            Handles.DrawAAPolyLine(4f, point + new Vector3(-size, -size), point + new Vector3(size, size));
            Handles.DrawAAPolyLine(4f, point + new Vector3(-size, size), point + new Vector3(size, -size));
            Handles.Label(point + Vector3.up * (size * 1.45f), "SEARCH STOP / FAILURE", LabelStyle());
        }

        static void DrawLegend()
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 12f, 310f, 72f), EditorStyles.helpBox);
            GUILayout.Label("RYZI RECORDED REPLAY", EditorStyles.boldLabel);
            GUILayout.Label($"{path.Length} keyframes | terminal: {terminalStatus}");
            GUILayout.Label("Cyan: path   Blue ring: current frame   Red X: failure/search limit");
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        static GUIStyle LabelStyle()
        {
            GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = Color.white;
            style.fontSize = 11;
            return style;
        }

        static void AddPoints(Vector2[] points, ref Bounds bounds, ref bool initialized)
        {
            for (int i = 0; i < points.Length; i++)
            {
                if (!initialized)
                {
                    bounds = new Bounds(points[i], Vector3.zero);
                    initialized = true;
                }
                else
                    bounds.Encapsulate(points[i]);
            }
        }
    }
}
