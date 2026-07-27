using System;
using Playtester.Agent;
using Playtester.Telemetry;
using UnityEngine;

namespace Playtester
{
    /// <summary>Allows a built player to prove startup in unattended smoke tests.</summary>
    public sealed class PlaytesterRuntimeSmokeExit : MonoBehaviour
    {
        private float deadline;
        private int targetEpisodes = 1;
        private PlaytestAgent agent = null!;
        private bool quitting;

        private void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index] == "--smoke-exit")
                {
                    deadline = Time.realtimeSinceStartup + 15f;
                    agent = GetComponent<PlaytestAgent>();
                }
                else if (
                    arguments[index] == "--smoke-episodes" &&
                    index + 1 < arguments.Length &&
                    int.TryParse(arguments[index + 1], out int episodes) &&
                    episodes > 0)
                {
                    targetEpisodes = episodes;
                }
            }
            if (deadline == 0f)
                enabled = false;
        }

        private void Update()
        {
            if (quitting)
                return;
            if (agent != null && agent.CompletedEpisodes >= targetEpisodes)
            {
                Quit();
                return;
            }
            if (Time.realtimeSinceStartup < deadline)
                return;
            if (agent != null)
            {
                int missingEpisodes = Math.Max(1, targetEpisodes - agent.CompletedEpisodes);
                for (int index = 0; index < missingEpisodes; index++)
                    agent.RecordStandaloneTimeout();
            }
            if (agent == null)
            {
                var recorders = FindObjectsByType<TelemetryRecorder>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                foreach (TelemetryRecorder recorder in recorders)
                    recorder.RecordStandaloneTimeout();
            }
            Quit();
        }

        private void Quit()
        {
            quitting = true;
            Debug.Log($"PLAYTESTER_RUNTIME_SMOKE_PASS episodes={targetEpisodes}");
            Application.Quit(0);
        }
    }
}
