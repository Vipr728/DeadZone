using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PlatformerPlaytest.Live
{
    /// <summary>
    /// Visual-only instrumentation for a live simulation. It never writes inputs or game state: the HUD shows the
    /// action issued for the most recent tick, while Scene-view gizmos retain markers where input transitions
    /// occurred for the entire watch run.
    /// </summary>
    public sealed class SimulationDiagnosticsOverlay : MonoBehaviour
    {
        struct Marker
        {
            public Vector3 Position;
            public string Label;
            public Color Color;
        }

        const float InputEpsilon = 0.01f;

        readonly List<Marker> markers = new List<Marker>(16);
        LivePlaybackDriver driver;
        Transform player;
        PlayerAction previousAction;
        bool hasPreviousAction;

        public void Bind(LivePlaybackDriver source, Transform playerTransform)
        {
            if (driver != null)
                driver.Ticked -= OnSimulationTick;

            driver = source;
            player = playerTransform;
            hasPreviousAction = false;
            markers.Clear();

            if (driver != null)
                driver.Ticked += OnSimulationTick;
        }

        void OnDestroy()
        {
            if (driver != null)
                driver.Ticked -= OnSimulationTick;
        }

        void OnSimulationTick(int tick)
        {
            if (driver == null || player == null)
                return;

            PlayerAction action = driver.LastAction;
            if (!hasPreviousAction)
            {
                previousAction = PlayerAction.Neutral;
                hasPreviousAction = true;
            }

            if (Changed(action.MoveX, previousAction.MoveX))
                AddMarker(action.MoveX > InputEpsilon ? "RIGHT" : action.MoveX < -InputEpsilon ? "LEFT" : "HORIZONTAL STOP",
                    action.MoveX == 0f ? Color.gray : new Color(0.25f, 0.75f, 1f));
            if (Changed(action.MoveY, previousAction.MoveY))
                AddMarker(action.MoveY > InputEpsilon ? "UP" : action.MoveY < -InputEpsilon ? "DOWN" : "VERTICAL STOP",
                    new Color(0.65f, 0.45f, 1f));
            if (action.JumpPressed)
                AddMarker(driver.LastJumpWasWallJump ? "WALL JUMP" : "JUMP", new Color(0.4f, 1f, 0.45f));
            if (action.DashPressed)
                AddMarker("DASH", new Color(1f, 0.72f, 0.2f));
            if (action.ClimbHeld != previousAction.ClimbHeld)
                AddMarker(action.ClimbHeld ? "CLIMB" : "CLIMB RELEASE", new Color(1f, 0.4f, 0.8f));

            previousAction = action;
        }

        static bool Changed(float a, float b) => Mathf.Abs(a - b) > InputEpsilon;

        void AddMarker(string label, Color color)
        {
            markers.Add(new Marker
            {
                Position = player.position + Vector3.up * 1.1f,
                Label = label,
                Color = color
            });
        }

        void OnGUI()
        {
            if (!Application.isPlaying || driver == null)
                return;

            PlayerAction action = driver.LastAction;
            // The simulation reads this state immediately before issuing LastAction. Do not trigger an additional
            // adapter observation here: observations also update completion state, which must stay on the same
            // cadence as headless replay.
            Observation state = driver.LastObservation;
            const float statusHeight = 54f;
            const float panelWidth = 340f;
            const float panelHeight = 238f;
            Rect panel = new Rect(Screen.width - panelWidth - 12f, Screen.height - statusHeight - panelHeight - 12f,
                panelWidth, panelHeight);

            DrawPanel(panel, action);
            DrawStatusBar(new Rect(0f, Screen.height - statusHeight, Screen.width, statusHeight), state,
                driver);
        }

        static void DrawPanel(Rect rect, PlayerAction action)
        {
            GUI.Box(rect, "SIM INPUT", PanelStyle());
            float x = rect.x + 16f;
            float y = rect.y + 42f;
            DrawKey(new Rect(x, y, 94f, 38f), "←", action.MoveX < -InputEpsilon, new Color(0.25f, 0.75f, 1f));
            DrawKey(new Rect(x + 106f, y, 94f, 38f), "→", action.MoveX > InputEpsilon, new Color(0.25f, 0.75f, 1f));
            DrawKey(new Rect(x + 212f, y, 94f, 38f), "↑", action.MoveY > InputEpsilon, new Color(0.65f, 0.45f, 1f));
            DrawKey(new Rect(x + 212f, y + 48f, 94f, 38f), "↓", action.MoveY < -InputEpsilon, new Color(0.65f, 0.45f, 1f));
            DrawKey(new Rect(x, y + 102f, 147f, 38f), "JUMP", action.JumpPressed || action.JumpHeld, new Color(0.4f, 1f, 0.45f));
            DrawKey(new Rect(x + 159f, y + 102f, 147f, 38f), "DASH", action.DashPressed, new Color(1f, 0.72f, 0.2f));
            DrawKey(new Rect(x, y + 150f, 306f, 38f), "CLIMB", action.ClimbHeld, new Color(1f, 0.4f, 0.8f));
            GUI.Label(new Rect(x, y + 197f, 306f, 24f), "edge presses flash for one simulation tick", HintStyle());
        }

        static void DrawKey(Rect rect, string label, bool active, Color activeColor)
        {
            Color previous = GUI.color;
            GUI.color = active ? activeColor : new Color(0.55f, 0.58f, 0.63f, 0.8f);
            GUI.Box(rect, label, KeyStyle());
            GUI.color = previous;
        }

        static void DrawStatusBar(Rect rect, Observation state, LivePlaybackDriver driver)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0.04f, 0.06f, 0.09f, 0.92f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = Color.white;

            string wall = state.OnLeftWall ? "WALL ←" : state.OnRightWall ? "WALL →" : "WALL —";
            int replayLimit = driver.PlaybackTickLimit > 0
                ? driver.PlaybackTickLimit
                : driver.Scenario != null ? driver.Scenario.stepBudget : 0;
            string firstLine = $"REPLAY {driver.CurrentTick}/{replayLimit} {(driver.IsPaused ? "PAUSED" : "PLAYING")}    " +
                               $"GROUNDED {(state.IsGrounded ? "YES" : "NO")}    " +
                               $"DASHING {(state.IsDashing ? "YES" : "NO")}    " +
                               $"{wall}    " +
                               $"WALL JUMP {(driver.LastJumpWasWallJump ? "YES" : "NO")}";
            string secondLine = $"CLIMBING {(state.IsClimbing ? "YES" : "NO")}    " +
                                $"DASHES {state.DashesRemaining}    " +
                                $"STAMINA {state.Stamina:0}    " +
                                $"VEL ({state.Velocity.x:0.0}, {state.Velocity.y:0.0})";
            GUI.Label(new Rect(rect.x + 14f, rect.y + 5f, rect.width - 28f, 22f), firstLine, StatusStyle());
            GUI.Label(new Rect(rect.x + 14f, rect.y + 28f, rect.width - 28f, 22f), secondLine, StatusStyle());
            GUI.color = previous;
        }

        static GUIStyle PanelStyle() => new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize = 19,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(8, 8, 9, 8)
        };

        static GUIStyle KeyStyle() => new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold
        };

        static GUIStyle HintStyle() => new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12
        };

        static GUIStyle StatusStyle() => new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 16,
            fontStyle = FontStyle.Bold
        };

        void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;

            for (int i = 0; i < markers.Count; i++)
            {
                Marker marker = markers[i];
                Color color = marker.Color;
                Gizmos.color = color;
                Gizmos.DrawWireSphere(marker.Position, 0.28f);
                Gizmos.DrawLine(marker.Position, marker.Position + Vector3.up * 0.45f);

#if UNITY_EDITOR
                Handles.color = color;
                Handles.Label(marker.Position + Vector3.up * 0.5f, marker.Label);
#endif
            }
        }
    }
}
