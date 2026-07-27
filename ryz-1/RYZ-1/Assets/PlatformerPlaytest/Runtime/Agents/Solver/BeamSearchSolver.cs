using System.Collections.Generic;
using System.Diagnostics;

namespace PlatformerPlaytest.Solver
{
    /// <summary>Tunables for one <see cref="BeamSearchSolver"/> run. All fields are deterministic inputs.</summary>
    public struct SolverConfig
    {
        public int BeamWidth;         // nodes kept per depth level
        public int MaxMacrosDepth;    // max macros chained end-to-end
        public int Seed;              // passed to adapter.ResetEpisode
        public int[] TickMenu;        // fixed macro span menu, e.g. {5,10,20,40}
        public long MaxTicksSimulated; // hard cap on total simulator ticks across the whole search
        public float FixedDeltaTime;   // dt per TickSimulation; falls back to scenario if <= 0

        /// <summary>Segmented solving (T11): when set, success is "player x >= TargetX" instead of
        /// adapter.IsComplete. NaN = solve for the real goal.</summary>
        public float TargetX;

        /// <summary>Segmented solving: when set (not NaN), TargetX success ALSO requires
        /// |player y - TargetY| <= TargetYTolerance. Without it the success test is x-only, so a player can
        /// satisfy every intermediate checkpoint while falling below the level as long as horizontal momentum
        /// continues, then fail only when the real goal checks both coordinates.
        /// NaN = y unconstrained (all pre-T15 callers).</summary>
        public float TargetY;

        /// <summary>Half-width of the TargetY acceptance band, in world units. Ignored when TargetY is NaN.</summary>
        public float TargetYTolerance;

        /// <summary>Segmented solving (T11): actions replayed before the searched suffix. The search's action
        /// streams INCLUDE this prefix, so the returned stream is always playable from a fresh ResetEpisode.
        /// Cost: every node re-simulates the whole prefix — O(prefix) per node. Budget with MaxTicksSimulated.</summary>
        public List<PlayerAction> PrefixActions;

        public static SolverConfig Default => new SolverConfig
        {
            BeamWidth = 24,
            MaxMacrosDepth = 60,
            Seed = 0,
            TickMenu = new[] { 5, 10, 20, 40 },
            MaxTicksSimulated = 2_000_000,
            FixedDeltaTime = 0f,
            TargetX = float.NaN,
            TargetY = float.NaN,
            TargetYTolerance = 0f,
            PrefixActions = null
        };
    }

    /// <summary>Outcome of a solve: the winning action stream (if any) plus search diagnostics.</summary>
    public struct SolveResult
    {
        public bool Solved;
        public List<PlayerAction> ActionStream;
        /// <summary>
        /// Deterministically replayable best-effort stream when a solve is incomplete. This is diagnostic
        /// evidence only: callers must never treat it as a verified solution.
        /// </summary>
        public List<PlayerAction> BestEffortActionStream;
        public int NodesExpanded;
        public int DuplicatesPruned;
        public int DeathsPruned;
        public long TicksSimulated;
        public int MaxDepthReached;
        public long ElapsedMs;
        public string Diagnostic;
        public float FurthestX;   // furthest player x observed across the whole search (progress diagnostic)
    }

    /// <summary>
    /// Offline beam-search planner over <see cref="MovementMacro"/> sequences (ADR-004). NOT an IAgent — it plans
    /// once, returns a replayable action stream, and does not participate in the live episode loop.
    ///
    /// Mechanism — re-simulation, no snapshots: a search node IS a macro sequence. To evaluate a node the solver
    /// resets the adapter to spawn (seed) and replays the node's full action stream tick-by-tick through the same
    /// ReadObservation → ApplyAction → TickSimulation → AfterStep sequence the EpisodeRunner uses. This relies on
    /// simulator determinism (proven by the isolation/determinism PlayMode tests).
    ///
    /// Complexity: evaluating a node at depth d replays O(d * meanMacroTicks) ticks. One beam level expands
    /// BeamWidth nodes against |candidates| macros, so a full search costs
    ///     O(MaxMacrosDepth * BeamWidth * |candidates| * MaxMacrosDepth * meanMacroTicks)  simulator ticks,
    /// i.e. quadratic in depth because there are no snapshots to resume from. This is why re-simulation is capped
    /// by SolverConfig.MaxTicksSimulated and short-circuited by early goal exit. Acceptable: offline, one-shot.
    ///
    /// Determinism invariants:
    ///  - Same (adapter, scenario, config) → same SolveResult. No Guid, no wall-clock, no RNG anywhere in ordering.
    ///  - Candidate macros are enumerated in a fixed order; ties in the beam sort break on insertion index (stable).
    ///  - Duplicate elimination uses StateHash over the quantized post-macro state; each hash is expanded at most once.
    ///  - Validation: a Solved stream is re-simulated once and asserted IsComplete before returning (guards scoring bugs).
    ///
    /// LINQ is banned by repo convention; all ordering is done with an explicit insertion-sort over small lists.
    /// </summary>
    public sealed class BeamSearchSolver
    {
        // Reused scratch buffers (offline code; kept for clarity + to avoid churn, not a per-tick hot path).
        readonly Observation obs = new Observation();

        sealed class Node
        {
            public List<PlayerAction> Stream; // full action stream from spawn
            public float Score;
            public ulong Hash;
            public int Depth;
        }

        readonly struct ScoreContext
        {
            public readonly bool HasSegmentTarget;
            public readonly float StartX;
            public readonly float TargetX;
            public readonly float HorizontalScale;

            public ScoreContext(float startX, float targetX)
            {
                HasSegmentTarget = !float.IsNaN(targetX);
                StartX = startX;
                TargetX = targetX;
                HorizontalScale = HasSegmentTarget
                    ? 1f / System.Math.Max(System.Math.Abs(targetX - startX), 0.001f)
                    : 0f;
            }
        }

        public SolveResult Solve(IGameAdapter adapter, ScenarioConfig scenario, SolverConfig config)
        {
            Stopwatch sw = Stopwatch.StartNew();
            float dt = config.FixedDeltaTime > 0f ? config.FixedDeltaTime : scenario.fixedDeltaTime;
            int[] tickMenu = (config.TickMenu != null && config.TickMenu.Length > 0)
                ? config.TickMenu
                : new[] { 5, 10, 20, 40 };

            SolveResult result = new SolveResult
            {
                Solved = false,
                ActionStream = new List<PlayerAction>(),
                BestEffortActionStream = new List<PlayerAction>(),
                NodesExpanded = 0,
                DuplicatesPruned = 0,
                DeathsPruned = 0,
                TicksSimulated = 0,
                MaxDepthReached = 0,
                ElapsedMs = 0,
                Diagnostic = null,
                FurthestX = float.NegativeInfinity
            };

            HashSet<ulong> seen = new HashSet<ulong>();

            // Root: the prefix (empty for an unsegmented solve) = the state the search starts from.
            List<PlayerAction> rootStream = config.PrefixActions != null
                ? new List<PlayerAction>(config.PrefixActions)
                : new List<PlayerAction>();
            SimOutcome rootSim = Simulate(adapter, scenario, dt, config, rootStream, rootStream.Count, ref result);
            ScoreContext scoreContext = new ScoreContext(rootSim.PositionX, config.TargetX);
            Node root = new Node
            {
                Stream = rootStream,
                Score = Score(rootSim, config, scoreContext),
                Hash = rootSim.Hash,
                Depth = 0
            };
            seen.Add(root.Hash);

            // Retain a real, clean-reset-replayable trace even if the search budget is exhausted. The
            // diagnostic/reporting layer uses this to show where search actually got, rather than emitting
            // an empty replay for every failed solve.
            List<PlayerAction> bestEffort = new List<PlayerAction>(rootStream);
            float bestEffortX = rootSim.PositionX;

            if (rootSim.Completed)
                return Finish(adapter, scenario, dt, config, root.Stream, sw, ref result, "already at goal on spawn");

            List<Node> frontier = new List<Node> { root };
            List<Node> candidates = new List<Node>();

            for (int depth = 0; depth < config.MaxMacrosDepth; depth++)
            {
                if (result.TicksSimulated >= config.MaxTicksSimulated)
                {
                    result.Diagnostic = "tick budget exhausted";
                    break;
                }

                candidates.Clear();

                for (int n = 0; n < frontier.Count; n++)
                {
                    Node parent = frontier[n];

                    for (int t = 0; t < tickMenu.Length; t++)
                    {
                        int ticks = tickMenu[t];
                        for (int m = 0; m < MacroCount; m++)
                        {
                            if (result.TicksSimulated >= config.MaxTicksSimulated)
                                break;

                            MovementMacro macro = MacroAt(m, ticks);

                            List<PlayerAction> childStream = new List<PlayerAction>(parent.Stream.Count + ticks);
                            childStream.AddRange(parent.Stream);
                            int macroStart = childStream.Count;
                            macro.Expand(childStream);

                            SimOutcome sim = Simulate(adapter, scenario, dt, config, childStream, macroStart, ref result);
                            result.NodesExpanded++;

                            if (sim.DiedInMacro)
                            {
                                result.DeathsPruned++;
                                continue;
                            }

                            if (sim.PositionX > bestEffortX)
                            {
                                bestEffortX = sim.PositionX;
                                bestEffort = new List<PlayerAction>(childStream);
                            }

                            if (sim.Completed)
                            {
                                // Truncate to the exact completing tick for a tight, exactly-replayable stream.
                                if (sim.CompletionTick + 1 < childStream.Count)
                                    childStream.RemoveRange(sim.CompletionTick + 1, childStream.Count - (sim.CompletionTick + 1));
                                result.MaxDepthReached = depth + 1;
                                return Finish(adapter, scenario, dt, config, childStream, sw, ref result, null);
                            }

                            if (seen.Contains(sim.Hash))
                            {
                                result.DuplicatesPruned++;
                                continue;
                            }
                            seen.Add(sim.Hash);

                            candidates.Add(new Node
                            {
                                Stream = childStream,
                                Score = Score(sim, config, scoreContext),
                                Hash = sim.Hash,
                                Depth = depth + 1
                            });
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    if (result.Diagnostic == null)
                        result.Diagnostic = "search exhausted: no new states to expand";
                    break;
                }

                result.MaxDepthReached = depth + 1;
                frontier = TopK(candidates, config.BeamWidth);
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.BestEffortActionStream = bestEffort;
            if (result.Diagnostic == null)
                result.Diagnostic = "depth limit reached without solution";
            return result;
        }

        // ---- candidate macro alphabet (fixed enumeration order) -----------------------------------------------

        const int MacroCount = 8;

        static MovementMacro MacroAt(int index, int ticks)
        {
            switch (index)
            {
                case 0: return MovementMacro.HoldDirection(1, ticks);
                case 1: return MovementMacro.HoldDirection(-1, ticks);
                case 2: return MovementMacro.JumpAndHold(1, ticks);
                case 3: return MovementMacro.JumpAndHold(-1, ticks);
                case 4: return MovementMacro.JumpAndHold(0, ticks);
                case 5: return MovementMacro.DashDirection(1, 0, ticks);
                case 6: return MovementMacro.DashDirection(1, 1, ticks);
                default: return MovementMacro.Wait(ticks);
            }
        }

        // ---- simulation ---------------------------------------------------------------------------------------

        struct SimOutcome
        {
            public bool Completed;
            public int CompletionTick;
            public bool DiedInMacro;
            public ulong Hash;
            public float Progress;
            public float PositionX;
            public float PositionY;
            public bool Grounded;
            public bool WallContact;
        }

        /// <summary>
        /// Reset to spawn and replay the whole action stream. Mirrors EpisodeRunner's per-tick ordering so
        /// completion timing matches live play. Death is only counted if it occurs at/after <paramref name="macroStart"/>
        /// (i.e. caused by the candidate macro, not inherited from the prefix — the prefix never died or it would
        /// not be in the frontier).
        /// </summary>
        SimOutcome Simulate(IGameAdapter adapter, ScenarioConfig scenario, float dt, SolverConfig config,
            List<PlayerAction> stream, int macroStart, ref SolveResult result)
        {
            adapter.ResetEpisode(config.Seed);

            SimOutcome outcome = new SimOutcome { CompletionTick = -1 };
            bool useTargetX = !float.IsNaN(config.TargetX);

            for (int i = 0; i < stream.Count; i++)
            {
                adapter.ReadObservation(obs);

                // Target-x success is checked on the observation the loop already reads, so segmented solving
                // costs no extra ReadObservation (the grid fill in there is the expensive part). obs reflects the
                // state AFTER tick i-1, hence CompletionTick = i - 1.
                if (useTargetX && ReachedTarget(obs, config) && i > 0)
                {
                    outcome.Completed = true;
                    outcome.CompletionTick = i - 1;
                    break;
                }

                PlayerAction a = stream[i];
                adapter.ApplyAction(in a);
                adapter.TickSimulation(dt);
                adapter.AfterStep(i);
                result.TicksSimulated++;

                if (adapter.IsDead && i >= macroStart)
                    outcome.DiedInMacro = true;

                if (adapter.IsComplete)
                {
                    outcome.Completed = true;
                    outcome.CompletionTick = i;
                    break;
                }
            }

            // Snapshot post-tick state for scoring + dedup hashing.
            adapter.ReadObservation(obs);
            if (obs.Position.x > result.FurthestX)
                result.FurthestX = obs.Position.x;
            if (!outcome.Completed && useTargetX && ReachedTarget(obs, config) && stream.Count > 0)
            {
                outcome.Completed = true;
                outcome.CompletionTick = stream.Count - 1;
            }
            outcome.Progress = obs.Progress;
            outcome.PositionX = obs.Position.x;
            outcome.PositionY = obs.Position.y;
            outcome.Grounded = obs.IsGrounded;
            outcome.WallContact = obs.OnLeftWall || obs.OnRightWall;

            int flags = 0;
            if (obs.IsGrounded) flags |= 1 << 0;
            if (obs.OnLeftWall) flags |= 1 << 1;
            if (obs.OnRightWall) flags |= 1 << 2;
            if (obs.IsDashing) flags |= 1 << 3;
            if (obs.IsClimbing) flags |= 1 << 4;
            outcome.Hash = StateHash.Compute(obs.Position, obs.Velocity, flags, obs.DashesRemaining);

            return outcome;
        }

        static bool ReachedTarget(Observation observation, SolverConfig config)
        {
            if (observation.Position.x < config.TargetX)
                return false;
            if (float.IsNaN(config.TargetY))
                return true;

            float tolerance = config.TargetYTolerance > 0f ? config.TargetYTolerance : 0f;
            return System.Math.Abs(observation.Position.y - config.TargetY) <= tolerance;
        }

        static float Score(SimOutcome sim, SolverConfig config, ScoreContext context)
        {
            // Intermediate segments must be ranked relative to their own discovered start and target. Using the
            // adapter's full-level normalized Progress here couples an early segment's beam ordering to wherever
            // the final goal happens to be: appending geometry after the segment rescales horizontal progress while
            // the fixed grounded/wall/Y terms stay unchanged. Local segment progress is invariant to later level
            // extensions and contains no scene-specific coordinates.
            float s = context.HasSegmentTarget
                ? (sim.PositionX - context.StartX) * context.HorizontalScale
                : sim.Progress;
            if (!float.IsNaN(config.TargetY))
            {
                // An x-only score ranks a player falling below the level as "best" as long as horizontal
                // momentum continues. Keep states near the authored checkpoint height in the beam instead.
                // Being above a checkpoint is often required (for example, climbing over SampleScene's x=15
                // pillar before descending to the x=19.5 checkpoint). Only rank states down when they are below
                // the authored height, which is the signature of the falling shortcut we need to exclude.
                float verticalShortfall = System.Math.Max(0f, config.TargetY - sim.PositionY);
                s -= verticalShortfall * 0.05f;
            }
            if (sim.Grounded) s += 0.02f;        // bonus: stable footing is a good stepping-off state
            if (sim.WallContact) s += 0.01f;     // bonus: wall contact enables wall-jumps
            return s;
        }

        // ---- deterministic top-K (insertion sort, stable on insertion index) ----------------------------------

        static List<Node> TopK(List<Node> candidates, int k)
        {
            if (k < 1) k = 1;
            // Selection of the k highest-scoring candidates, preserving insertion order among equal scores.
            List<Node> ordered = new List<Node>(candidates.Count);
            ordered.AddRange(candidates);
            // Stable insertion sort by descending score; equal scores keep original relative order.
            for (int i = 1; i < ordered.Count; i++)
            {
                Node key = ordered[i];
                int j = i - 1;
                while (j >= 0 && ordered[j].Score < key.Score)
                {
                    ordered[j + 1] = ordered[j];
                    j--;
                }
                ordered[j + 1] = key;
            }
            if (ordered.Count > k)
                ordered.RemoveRange(k, ordered.Count - k);
            return ordered;
        }

        // ---- solution validation ------------------------------------------------------------------------------

        SolveResult Finish(IGameAdapter adapter, ScenarioConfig scenario, float dt, SolverConfig config,
            List<PlayerAction> stream, Stopwatch sw, ref SolveResult result, string note)
        {
            // Re-simulate once and assert completion — guards against scoring/dedup bugs claiming a false win.
            SimOutcome check = Simulate(adapter, scenario, dt, config, stream, stream.Count, ref result);
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;

            if (!check.Completed)
            {
                result.Solved = false;
                result.ActionStream = new List<PlayerAction>();
                result.BestEffortActionStream = new List<PlayerAction>(stream);
                result.Diagnostic = "validation failed: winning stream did not reach goal on re-simulation";
                return result;
            }

            result.Solved = true;
            result.ActionStream = stream;
            result.BestEffortActionStream = new List<PlayerAction>(stream);
            result.Diagnostic = note;
            return result;
        }
    }
}
