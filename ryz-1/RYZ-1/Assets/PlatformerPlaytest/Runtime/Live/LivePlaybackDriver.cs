using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlatformerPlaytest.Live
{
    /// <summary>
    /// Drives one arena at real-time pacing (T10) instead of the tight synchronous loop EpisodeRunner uses for
    /// headless batches. Same per-tick sequence as EpisodeRunner (ReadObservation → Act → ApplyAction →
    /// TickSimulation → AfterStep → DrainEvents), just spread across FixedUpdate calls so a human can watch it.
    ///
    /// Determinism: dt passed to TickSimulation is ALWAYS scenario.fixedDeltaTime (the same value a headless run
    /// uses), never Time.fixedDeltaTime — physics is bit-identical to a headless run of the same seed/agent.
    /// Time.fixedDeltaTime only controls how many ticks fire per real second (pacing), via ticksPerFixedUpdate =
    /// round(speedMultiplier). If the project's fixed timestep differs from scenario.fixedDeltaTime, the only
    /// consequence is that "1x" playback isn't exactly wall-clock real-time — simulation content is unaffected.
    /// </summary>
    public sealed class LivePlaybackDriver : MonoBehaviour
    {
        public IGameAdapter Adapter;
        public IAgent Agent;
        public ScenarioConfig Scenario;
        public int Seed;

        /// <summary>
        /// Optional action-stream limit for recorded replays. Zero preserves the scenario step budget behavior.
        /// </summary>
        public int PlaybackTickLimit;

        [SerializeField] float speedMultiplier = 1f;
        [SerializeField] bool paused;

        readonly Observation obs = new Observation();
        readonly List<TimedGameEvent> eventScratch = new List<TimedGameEvent>();

        public int CurrentTick { get; private set; }
        public bool IsComplete { get; private set; }
        public int Deaths { get; private set; }
        public Observation LastObservation => obs;
        public PlayerAction LastAction { get; private set; }
        public bool LastJumpWasWallJump { get; private set; }

        public event Action<int> Ticked;
        public event Action Finished;

        bool finishedRaised;
        bool started;

        public float SpeedMultiplier => speedMultiplier;
        public bool IsPaused => paused;

        int TicksPerFixedUpdate => Mathf.Max(1, Mathf.RoundToInt(speedMultiplier));

        public void Play()
        {
            EnsureStarted();
            paused = false;
        }

        public void Pause() => paused = true;

        public void SetSpeed(float speed) => speedMultiplier = Mathf.Max(0.01f, speed);

        /// <summary>Advances exactly one tick, regardless of pause state. No-op once finished.</summary>
        public void StepOnce()
        {
            EnsureStarted();
            if (!IsComplete)
                RunOneTick();
        }

        public void Restart()
        {
            if (Adapter == null || Scenario == null)
                return;
            Adapter.ResetEpisode(Seed);
            Agent?.OnEpisodeStart(Seed);
            CurrentTick = 0;
            IsComplete = false;
            Deaths = 0;
            LastAction = PlayerAction.Neutral;
            LastJumpWasWallJump = false;
            finishedRaised = false;
            started = true;
        }

        void EnsureStarted()
        {
            if (!started)
                Restart();
        }

        void FixedUpdate()
        {
            if (paused || IsComplete || Adapter == null || Agent == null || Scenario == null)
                return;
            EnsureStarted();

            int ticks = TicksPerFixedUpdate;
            for (int i = 0; i < ticks && !IsComplete; i++)
                RunOneTick();
        }

        void RunOneTick()
        {
            Adapter.ReadObservation(obs);
            PlayerAction action = Agent.Act(obs, CurrentTick);
            LastAction = action;
            LastJumpWasWallJump = action.JumpPressed && !obs.IsGrounded && (obs.OnLeftWall || obs.OnRightWall);
            Adapter.ApplyAction(in action);
            Adapter.TickSimulation(Scenario.fixedDeltaTime);
            Adapter.AfterStep(CurrentTick);

            eventScratch.Clear();
            Adapter.DrainEvents(eventScratch);
            if (Adapter.IsDead)
                Deaths++;

            Ticked?.Invoke(CurrentTick);
            CurrentTick++;

            int tickLimit = PlaybackTickLimit > 0 ? PlaybackTickLimit : Scenario.stepBudget;
            if (Adapter.IsComplete || CurrentTick >= tickLimit)
            {
                IsComplete = true;
                if (!finishedRaised)
                {
                    finishedRaised = true;
                    Finished?.Invoke();
                }
            }
        }

        void OnDestroy()
        {
            Adapter?.RestoreOverrides();
        }
    }
}
