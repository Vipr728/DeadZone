using System;
using UnityEngine;

namespace PlatformerPlaytest
{
    /// <summary>Player state-flag bits packed into the keyframe/frame flags field.</summary>
    [Flags]
    public enum StateFlagBits
    {
        None = 0,
        Grounded = 1 << 0,
        WallLeft = 1 << 1,
        WallRight = 1 << 2,
        Dashing = 1 << 3,
        Climbing = 1 << 4
    }

    /// <summary>Packs the boolean player-state flags into the bitfield used by keyframes, frames, and StateHash.</summary>
    public static class StateFlags
    {
        public static int Pack(bool grounded, bool wallLeft, bool wallRight, bool dashing, bool climbing)
        {
            int f = 0;
            if (grounded) f |= (int)StateFlagBits.Grounded;
            if (wallLeft) f |= (int)StateFlagBits.WallLeft;
            if (wallRight) f |= (int)StateFlagBits.WallRight;
            if (dashing) f |= (int)StateFlagBits.Dashing;
            if (climbing) f |= (int)StateFlagBits.Climbing;
            return f;
        }

        public static int From(Observation obs)
        {
            return Pack(obs.IsGrounded, obs.OnLeftWall, obs.OnRightWall, obs.IsDashing, obs.IsClimbing);
        }
    }

    /// <summary>
    /// run.json header (ADR-005 / telemetry.md). Plain serializable class: public fields only, arrays not
    /// dictionaries, so <see cref="UnityEngine.JsonUtility"/> round-trips it one-object-per-file.
    /// </summary>
    [Serializable]
    public sealed class RunHeader
    {
        public string runId;
        public string createdUtc;
        public string scenarioId;
        public int layoutSeed;
        public string unityVersion;
        public string packageVersion;
        public string[] agentVersions = Array.Empty<string>();
        public float fixedDeltaTime;
        public int[] seeds = Array.Empty<int>();
        public string[] profileIds = Array.Empty<string>();
        /// <summary>Hash of player-controller tunables + fixed timestep + physics settings (build/settings hash).</summary>
        public string settingsHash;
    }

    /// <summary>One line of episodes.jsonl. JsonUtility-compatible (public fields, no dictionaries).</summary>
    [Serializable]
    public sealed class EpisodeSummary
    {
        public string episodeId;
        public string scenarioId;
        public int layoutSeed;
        public string agentId;
        public string profileId;
        public int seed;
        /// <summary>Outcome name: Completed / StepBudgetExceeded / Cancelled / Abandoned.</summary>
        public string outcome;
        public int steps;
        public int deaths;
        public float furthestProgress;
        public int completionTick = -1;
        public bool hasFullTrajectory;
    }

    /// <summary>Full-trajectory per-tick frame record (frames.jsonl). Serialized manually to allow the inline event tag.</summary>
    public struct FrameRecord
    {
        public int t;
        public float px, py, vx, vy;
        public int flags;
        public int dashes;
        public float stamina;
        public float progress;
        public int section;
        /// <summary>Non-null on a tick where an event fired (e.g. "Death", "GoalReached").</summary>
        public string ev;
    }

    /// <summary>Sampled keyframe (keyframes.jsonl). Carries the FNV-1a stateHash used for desync detection.</summary>
    public struct KeyframeRecord
    {
        public int t;
        public float px, py, vx, vy;
        public int flags;
        public int dashes;
        public ulong stateHash;
    }
}
