using System;
using System.Threading;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Generic episode lifecycle for platformers with custom movement mechanics. Game-specific solvers, live
    /// viewers, and telemetry codecs can build on this without translating their action schema into PlayerAction.
    /// </summary>
    public sealed class EpisodeRunner<TAction, TObservation> where TObservation : new()
    {
        readonly IGameAdapter<TAction, TObservation> adapter;
        readonly IAgent<TAction, TObservation> agent;
        readonly ScenarioConfig scenario;
        readonly int seed;
        readonly CancellationToken cancellationToken;

        public EpisodeRunner(
            IGameAdapter<TAction, TObservation> adapter,
            IAgent<TAction, TObservation> agent,
            ScenarioConfig scenario,
            int seed,
            CancellationToken cancellationToken = default)
        {
            this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
            this.scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            this.seed = seed;
            this.cancellationToken = cancellationToken;
        }

        public EpisodeResult Run(Action<int, TAction, TObservation> onStep = null)
        {
            adapter.ResetEpisode(seed);
            agent.OnEpisodeStart(seed);

            EpisodeResult result = new EpisodeResult();
            TObservation observation = new TObservation();
            IAbandonable abandonable = agent as IAbandonable;

            int step;
            for (step = 0; step < scenario.stepBudget; step++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Outcome = Outcome.Cancelled;
                    break;
                }

                adapter.ReadObservation(observation);
                TAction action = agent.Act(observation, step);
                adapter.ApplyAction(in action);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(step);
                adapter.DrainEvents(result.Events);

                if (adapter.IsDead)
                    result.Deaths++;
                if (adapter.Progress > result.FurthestProgress)
                    result.FurthestProgress = adapter.Progress;

                onStep?.Invoke(step, action, observation);

                if (adapter.IsComplete)
                {
                    result.Outcome = Outcome.Completed;
                    result.CompletionTick = step;
                    step++;
                    break;
                }

                if (abandonable != null && abandonable.Abandoned)
                {
                    result.Outcome = Outcome.Abandoned;
                    step++;
                    break;
                }
            }

            result.Steps = step;
            return result;
        }
    }

    /// <summary>
    /// Drives one episode's synchronous tick loop: observe → act → apply → simulate → drain events, until
    /// completion, step budget, or cancellation. The caller decides real-time pacing (or none, for batch runs).
    /// </summary>
    public sealed class EpisodeRunner
    {
        readonly EpisodeRunner<PlayerAction, Observation> inner;

        public EpisodeRunner(IGameAdapter adapter, IAgent agent, ScenarioConfig scenario, int seed, CancellationToken cancellationToken = default)
        {
            inner = new EpisodeRunner<PlayerAction, Observation>(
                adapter, agent, scenario, seed, cancellationToken);
        }

        /// <summary>Runs the full episode. Optional callback fires after each tick with (tick, action, observation).</summary>
        public EpisodeResult Run(Action<int, PlayerAction, Observation> onStep = null) => inner.Run(onStep);
    }
}
