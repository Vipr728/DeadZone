using System.Collections.Generic;
using System.Diagnostics;

namespace PlatformerPlaytest.Solver
{
    /// <summary>Outcome of a segmented solve: the validated full-level stream (if any) plus per-segment diagnostics.</summary>
    public struct SegmentedSolveResult
    {
        public bool Solved;
        public List<PlayerAction> ActionStream;   // full stream from spawn; empty unless Solved
        /// <summary>Best clean-reset-replayable partial stream when a segment cannot be solved.</summary>
        public List<PlayerAction> BestEffortActionStream;
        public int SegmentsSolved;
        public int SegmentCount;
        public int FailedSegment;                 // -1 when all segments solved
        public float FurthestX;
        public long NodesExpanded;
        public long TicksSimulated;
        public long ElapsedMs;
        public string Diagnostic;
    }

    /// <summary>
    /// Checkpoint-segmented beam search (T11). A single beam search over the full 115-unit SampleScene blows the
    /// tick budget because re-simulation is quadratic in depth; splitting at the authored checkpoints turns one
    /// deep search into N shallow ones.
    ///
    /// Segment k's success condition is "player x >= sectionBoundariesX[k]" rather than IsComplete. Scenarios
    /// with an aligned sectionBoundariesY array also require the player to be near the checkpoint's authored
    /// standing height, so horizontal movement below the level cannot satisfy a checkpoint. The last segment
    /// solves for the real goal rect.
    ///
    /// State continuity without snapshots: segment k+1 starts from the exact state segment k ended in by
    /// CONCATENATION — the cumulative action stream is passed as SolverConfig.PrefixActions and every node in
    /// segment k+1 replays that whole prefix before its own macros. Honest cost: O(prefix) simulator ticks per
    /// node, so late segments are the expensive ones. MaxTicksSimulated is applied PER SEGMENT to bound it.
    ///
    /// Nothing is claimed solved without proof: the concatenated stream is replayed once from a fresh
    /// ResetEpisode and must report IsComplete, or Solved stays false with the offending segment index.
    /// A segment that cannot be cleared within budget is reported (index + furthest x) rather than skipped —
    /// "the solver cannot pass section 3" is a product finding, not a bug to hide.
    /// </summary>
    public sealed class SegmentedSolver
    {
        readonly BeamSearchSolver inner = new BeamSearchSolver();
        readonly Observation obs = new Observation();

        /// <summary>Optional progress hook: invoked (segmentIndex, segmentCount) right before each segment solves.
        /// Editor callers use this to drive a progress bar; not called on the final validation replay.</summary>
        public System.Action<int, int> OnSegment;

        public SegmentedSolveResult Solve(IGameAdapter adapter, ScenarioConfig scenario, SolverConfig config)
        {
            Stopwatch sw = Stopwatch.StartNew();
            float dt = config.FixedDeltaTime > 0f ? config.FixedDeltaTime : scenario.fixedDeltaTime;

            // Targets: every section boundary strictly ahead of spawn, then the goal itself (NaN = IsComplete).
            List<float> targets = new List<float>();
            List<float> targetYs = new List<float>();
            float[] boundaries = scenario.sectionBoundariesX ?? System.Array.Empty<float>();
            float[] boundaryYs = scenario.sectionBoundariesY ?? System.Array.Empty<float>();
            bool useBoundaryY = boundaryYs.Length == boundaries.Length;
            for (int i = 0; i < boundaries.Length; i++)
            {
                if (boundaries[i] > scenario.spawnPosition.x)
                {
                    targets.Add(boundaries[i]);
                    targetYs.Add(useBoundaryY ? boundaryYs[i] : float.NaN);
                }
            }
            targets.Add(float.NaN);
            targetYs.Add(float.NaN);

            SegmentedSolveResult result = new SegmentedSolveResult
            {
                ActionStream = new List<PlayerAction>(),
                BestEffortActionStream = new List<PlayerAction>(),
                SegmentCount = targets.Count,
                FailedSegment = -1,
                FurthestX = float.NegativeInfinity
            };

            List<PlayerAction> accumulated = new List<PlayerAction>();

            for (int s = 0; s < targets.Count; s++)
            {
                OnSegment?.Invoke(s, targets.Count);
                SolverConfig segmentConfig = config;
                segmentConfig.TargetX = targets[s];
                segmentConfig.TargetY = targetYs[s];
                segmentConfig.TargetYTolerance = scenario.sectionBoundaryYTolerance;
                segmentConfig.PrefixActions = accumulated;

                SolveResult segment = inner.Solve(adapter, scenario, segmentConfig);
                result.NodesExpanded += segment.NodesExpanded;
                result.TicksSimulated += segment.TicksSimulated;
                if (segment.FurthestX > result.FurthestX)
                    result.FurthestX = segment.FurthestX;

                if (!segment.Solved)
                {
                    sw.Stop();
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    result.FailedSegment = s;
                    result.SegmentsSolved = s;
                    string targetLabel = float.IsNaN(targets[s])
                        ? "x=goal"
                        : float.IsNaN(targetYs[s])
                            ? $"x={targets[s]}"
                            : $"x={targets[s]}, y={targetYs[s]}±{segmentConfig.TargetYTolerance}";
                    result.Diagnostic =
                        $"segment {s} (target {targetLabel}) unsolved: {segment.Diagnostic}; " +
                        $"furthest x={result.FurthestX:F2}";
                    result.BestEffortActionStream = segment.BestEffortActionStream != null &&
                                                     segment.BestEffortActionStream.Count > 0
                        ? new List<PlayerAction>(segment.BestEffortActionStream)
                        : new List<PlayerAction>(accumulated);
                    return result;
                }

                accumulated = segment.ActionStream;
                result.BestEffortActionStream = new List<PlayerAction>(accumulated);
                result.SegmentsSolved = s + 1;
            }

            // Final proof: replay the concatenated stream once from a fresh reset.
            int failTick = ReplayToCompletion(adapter, dt, config.Seed, accumulated);
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;

            if (failTick >= 0)
            {
                result.FailedSegment = result.SegmentCount - 1;
                result.Diagnostic =
                    $"validation failed: concatenated {accumulated.Count}-tick stream did not reach the goal " +
                    "(segment concatenation is not state-exact)";
                return result;
            }

            result.Solved = true;
            result.ActionStream = accumulated;
            result.BestEffortActionStream = new List<PlayerAction>(accumulated);
            return result;
        }

        /// <summary>Replays the stream from a fresh reset. Returns -1 on completion, else the tick it ran out at.</summary>
        int ReplayToCompletion(IGameAdapter adapter, float dt, int seed, List<PlayerAction> stream)
        {
            adapter.ResetEpisode(seed);
            for (int i = 0; i < stream.Count; i++)
            {
                adapter.ReadObservation(obs);
                PlayerAction a = stream[i];
                adapter.ApplyAction(in a);
                adapter.TickSimulation(dt);
                adapter.AfterStep(i);
                if (adapter.IsComplete)
                    return -1;
            }
            return stream.Count;
        }
    }
}
