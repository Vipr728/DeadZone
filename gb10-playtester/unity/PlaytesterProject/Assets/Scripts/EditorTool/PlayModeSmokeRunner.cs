#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Playtester.EditorTool
{
    /// <summary>Batch-safe Play Mode smoke runner for the wired Stage 1 gym.</summary>
    [InitializeOnLoad]
    public static class PlayModeSmokeRunner
    {
        private const string PendingKey = "Playtester.GymSmoke.Pending";
        private const string DeadlineKey = "Playtester.GymSmoke.Deadline";

        static PlayModeSmokeRunner()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        [MenuItem("Playtester/Run Gym Smoke Test")]
        public static void RunGymSmoke()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/GymScene.unity", OpenSceneMode.Single);
            if (GameObject.FindFirstObjectByType<Playtester.Agent.PlaytestAgent>() == null)
                throw new InvalidOperationException("GymScene has no PlaytestAgent.");
            if (GameObject.FindFirstObjectByType<Playtester.Gym.PieceComposer>() == null)
                throw new InvalidOperationException("GymScene has no PieceComposer.");
            SessionState.SetBool(PendingKey, true);
            SessionState.SetString(DeadlineKey, (EditorApplication.timeSinceStartup + 5d).ToString(System.Globalization.CultureInfo.InvariantCulture));
            EditorApplication.isPlaying = true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(PendingKey, false))
            {
                SessionState.EraseBool(PendingKey);
                SessionState.EraseString(DeadlineKey);
                Debug.Log("PLAYTESTER_GYM_SMOKE_PASS");
                EditorApplication.Exit(0);
            }
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
                return;
            if (!double.TryParse(SessionState.GetString(DeadlineKey, "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double deadline))
                throw new InvalidOperationException("Gym smoke test deadline was not persisted.");
            if (EditorApplication.timeSinceStartup < deadline) return;
            EditorApplication.isPlaying = false;
        }
    }
}
#endif
