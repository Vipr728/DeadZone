using System;
using UnityEngine;

namespace Ryzi
{
    [Serializable]
    public sealed class SimulationRunReport
    {
        public string runId;
        public string scenarioId;
        public string manifestVersion;
        public string packageVersion;
        public string unityVersion;
        public string projectRevision;
        public string agentVersion;
        public string profileId;
        public int seed;
        public float fixedDeltaTime;
        public string settingsHash;
        public int runCount;
        public int completedRuns;
        public float completionRate;
        public int completionTick = -1;
        public int failureCount;
        public Vector2[] failurePositions = Array.Empty<Vector2>();
        public float furthestProgress;
        public long solverExpansions;
        public long simulationTicks;
        public long solverMilliseconds;
        public long telemetryMilliseconds;
        public bool solverCacheHit;
        public string solverCacheKey;
        public string solverCacheStatus;
        public bool solverSucceeded;
        public string solverDiagnostic;
        public string[] discoveredMechanics = Array.Empty<string>();
        public string[] unresolvedIssues = Array.Empty<string>();
        public string calibrationSummary;
        public string replayPath;
        public string runDirectory;
    }

    [Serializable]
    public sealed class ReplayRecord
    {
        public string replayVersion = "1";
        public string scenarioId;
        public int seed;
        public string packageVersion;
        public string manifestVersion;
        public float fixedDeltaTime;
        public int failureTick = -1;
        /// <summary>True when this is a best-effort trace from an incomplete search, not a completing replay.</summary>
        public bool isPartial;
        /// <summary>Completed, Death, SearchLimit, or Empty.</summary>
        public string terminalStatus;
        public SerializedUniversalAction[] actions = Array.Empty<SerializedUniversalAction>();
        public ReplayKeyframe[] keyframes = Array.Empty<ReplayKeyframe>();
        public bool deterministicVerificationPassed;
        public string verificationMessage;
    }

    [Serializable]
    public sealed class SerializedUniversalAction
    {
        public Vector2 moveAxis;
        public Vector2 aimAxis;
        public SerializedButtonAction[] buttons = Array.Empty<SerializedButtonAction>();

        public static SerializedUniversalAction From(in UniversalAction action)
        {
            SerializedButtonAction[] serialized = new SerializedButtonAction[action.Buttons.Count];
            for (int i = 0; i < serialized.Length; i++)
            {
                ButtonActionState button = action.Buttons[i];
                serialized[i] = new SerializedButtonAction
                {
                    channelId = button.ChannelId,
                    pressed = button.PressedThisTick,
                    held = button.Held,
                    released = button.ReleasedThisTick
                };
            }
            return new SerializedUniversalAction
            {
                moveAxis = action.MoveAxis,
                aimAxis = action.AimAxis,
                buttons = serialized
            };
        }

        public UniversalAction ToAction()
        {
            ButtonActionState[] states = new ButtonActionState[buttons?.Length ?? 0];
            for (int i = 0; i < states.Length; i++)
                states[i] = buttons[i].ToState();
            return new UniversalAction(moveAxis, aimAxis, states);
        }
    }

    [Serializable]
    public sealed class SerializedButtonAction
    {
        public string channelId;
        public bool pressed;
        public bool held;
        public bool released;

        public ButtonActionState ToState() => new ButtonActionState(channelId, pressed, held, released);
    }

    [Serializable]
    public struct ReplayKeyframe
    {
        public int tick;
        public Vector2 position;
        public Vector2 velocity;
        public float progress;
        public int stateFlags;
        public string stateHash;
    }

    [Serializable]
    public sealed class CounterfactualVariantResult
    {
        public float candidateValue;
        public int seed;
        public bool completed;
        public int completionTick = -1;
        public int deaths;
        public float furthestProgress;
        public string diagnostic;
    }

    [Serializable]
    public sealed class CounterfactualReport
    {
        public string tunableId;
        public string displayName;
        public float originalValue;
        public bool originalRestored;
        public bool cancelled;
        public long elapsedMilliseconds;
        public CounterfactualVariantResult[] variants = Array.Empty<CounterfactualVariantResult>();
        public string[] warnings = Array.Empty<string>();
    }
}
