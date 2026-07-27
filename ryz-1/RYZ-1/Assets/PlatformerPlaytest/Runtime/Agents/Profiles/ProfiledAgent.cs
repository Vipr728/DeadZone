using System;
using System.Collections.Generic;
using PlatformerPlaytest.Solver;
using Vector2 = UnityEngine.Vector2;

namespace PlatformerPlaytest.Profiles
{
    /// <summary>
    /// SYNTHETIC PROFILE AGENT — see <see cref="PlayerProfile"/> for the disclaimer: this degrades a plan
    /// (typically a <see cref="BeamSearchSolver"/> solution) through a parameterized limitation model, it is not
    /// a validated human model.
    ///
    /// All plan mutation (reaction delay, timing jitter, direction error) happens exactly once, in
    /// <see cref="OnEpisodeStart"/>, from one <see cref="Random"/> seeded with <c>seed ^ profileSalt</c>. This
    /// makes an episode fully deterministic given (basePlan, profile, seed, profileSalt), and keeps
    /// <see cref="Act"/> a pure array lookup with no per-tick allocation or randomness.
    ///
    /// CLOSED LOOP (T13). Open-loop replay of a jittered stream survives a 10-unit demo level but not the
    /// 115-unit SampleScene: a 2-tick jitter early desyncs the agent from moving platforms / crumble blocks and
    /// the remaining ~700 ticks are garbage. So <see cref="Act"/> now USES the observation, provided the caller
    /// supplied a reference trajectory (<see cref="SetReferenceTrajectory"/>; build one with
    /// <see cref="RecordTrajectory"/>). Without one the agent stays in the legacy open-loop mode.
    ///
    /// The loop is deliberately search-free (option (b) of the T13 brief): no second arena, no solver re-entry,
    /// no re-entrancy hazard against the live episode's adapter. Two mechanisms:
    ///  - RE-ANCHOR: when the live position drifts past the profile's tolerance, find the nearest point on the
    ///    reference trajectory and continue the plan from THAT index. This is the whole fix for timing desync —
    ///    a player who is 6 ticks behind just keeps playing from where they actually are.
    ///  - IMPROVISE: if no reference point is within tolerance (genuinely off-route, e.g. just respawned mid-air
    ///    or knocked off a platform), run a cheap reactive controller toward a lookahead waypoint until a
    ///    reference point comes back into tolerance.
    /// Both are gated by <see cref="ProfileParams.reactionDelayTicks"/> — a beginner takes 240 ms to notice it is
    /// off route, which is where most of the profile separation on the real level comes from.
    ///
    /// Persistence is wired here (not in a batch loop): a respawn is detected as a position teleport, counted,
    /// and once <see cref="ProfileParams.persistenceRetries"/> deaths are exceeded the agent sets
    /// <see cref="Abandoned"/> and EpisodeRunner ends the episode as <see cref="Outcome.Abandoned"/>.
    /// <see cref="ShouldRepeatFailedPlan"/> decides whether the retry re-anchors immediately (repeat the same
    /// plan) or improvises for a while first (try something different).
    /// </summary>
    public sealed class ProfiledAgent : IAgent, IAbandonable
    {
        /// <summary>Ticks of improvisation forced after a death when the profile does NOT repeat the failed plan.</summary>
        const int ImproviseAfterDeathTicks = 40;
        /// <summary>Reference indices to look ahead when improvising toward the route.</summary>
        const int WaypointLookahead = 10;
        /// <summary>A single-tick position jump this large can only be a respawn teleport.</summary>
        // ponytail: teleport heuristic instead of an adapter death callback — IAgent only ever sees Observation,
        // and adding a death channel to IAgent would touch every agent. Swap to a real signal if IAgent grows one.
        const float TeleportSq = 9f;

        readonly List<PlayerAction> basePlan;
        readonly ProfileParams profile;
        readonly int profileSalt;

        List<PlayerAction> mutatedPlan;
        List<Vector2> reference;      // caller-supplied, aligned to basePlan indices
        List<Vector2> alignedRef;     // reference shifted/padded to mutatedPlan indices

        Random rng;
        int planIndex;
        int noticeTick = -1;          // tick at which the current deviation was first seen (-1 = on route)
        int improviseUntilTick = -1;
        int nearestIndex;
        int stalledTicks;
        int jumpHoldRemaining;
        int deaths;
        Vector2 prevPosition;
        bool hasPrevPosition;
        bool improvising;

        /// <summary>True once persistence retries are exhausted; EpisodeRunner turns this into Outcome.Abandoned.</summary>
        public bool Abandoned { get; private set; }

        /// <summary>Deaths (respawn teleports) observed in the current episode.</summary>
        public int ObservedDeaths => deaths;

        /// <summary>Number of retries this agent instance has consumed against the current obstacle (batch-runner bookkeeping).</summary>
        public int ConsumedRetries { get; set; }

        public ProfiledAgent(List<PlayerAction> basePlan, ProfileParams profile, int profileSalt = 0)
        {
            this.basePlan = basePlan ?? new List<PlayerAction>();
            this.profile = profile;
            this.profileSalt = profileSalt;
        }

        /// <summary>
        /// Supplies the positions the base plan is expected to pass through — reference[i] is the position the
        /// player is at when basePlan[i] is issued (exactly what <see cref="RecordTrajectory"/> produces).
        /// Supplying it switches <see cref="Act"/> from open-loop replay to the closed loop. Call before
        /// <see cref="OnEpisodeStart"/>.
        /// </summary>
        public void SetReferenceTrajectory(List<Vector2> positions) => reference = positions;

        /// <summary>
        /// Replays a plan once on the adapter and records the position each action was issued from. Costs one
        /// episode of simulation (~750 ticks on SampleScene) versus the ~116 s solve, so callers pay it per plan,
        /// not per episode. Leaves the adapter reset — run it before the real episodes, not during one.
        /// </summary>
        public static List<Vector2> RecordTrajectory(IGameAdapter adapter, ScenarioConfig scenario,
            List<PlayerAction> plan, int seed = 0)
        {
            List<Vector2> positions = new List<Vector2>(plan.Count);
            Observation obs = new Observation();
            adapter.ResetEpisode(seed);
            for (int i = 0; i < plan.Count; i++)
            {
                adapter.ReadObservation(obs);
                positions.Add(obs.Position);
                PlayerAction a = plan[i];
                adapter.ApplyAction(in a);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(i);
                if (adapter.IsComplete)
                    break;
            }
            adapter.ResetEpisode(seed);
            return positions;
        }

        public void OnEpisodeStart(int seed)
        {
            rng = new Random(seed ^ profileSalt);
            mutatedPlan = ApplyReactionDelay(basePlan, profile.reactionDelayTicks);
            ApplyTimingJitter(mutatedPlan, profile.timingVarianceTicksSigma, rng);
            ApplyDirectionError(mutatedPlan, profile.directionErrorProb, rng);

            alignedRef = BuildAlignedReference(reference, profile.reactionDelayTicks, mutatedPlan.Count);

            planIndex = 0;
            noticeTick = -1;
            improviseUntilTick = -1;
            nearestIndex = 0;
            stalledTicks = 0;
            jumpHoldRemaining = 0;
            deaths = 0;
            hasPrevPosition = false;
            improvising = false;
            Abandoned = false;
        }

        /// <summary>Shifts the reference by the reaction delay (the agent stands at spawn during it) and pads the
        /// tail so it can be indexed with the same index as the jitter-lengthened plan.</summary>
        static List<Vector2> BuildAlignedReference(List<Vector2> source, int delayTicks, int planLength)
        {
            if (source == null || source.Count == 0)
                return null;

            List<Vector2> aligned = new List<Vector2>(planLength + delayTicks + 1);
            for (int i = 0; i < delayTicks; i++)
                aligned.Add(source[0]);
            aligned.AddRange(source);
            while (aligned.Count < planLength)
                aligned.Add(source[source.Count - 1]);
            return aligned;
        }

        public PlayerAction Act(Observation obs, int tick)
        {
            if (alignedRef == null)
            {
                // Legacy open-loop mode: no reference trajectory supplied.
                if (mutatedPlan != null && tick >= 0 && tick < mutatedPlan.Count)
                    return mutatedPlan[tick];
                return PlayerAction.Neutral;
            }

            if (Abandoned)
                return PlayerAction.Neutral;

            float tol = profile.deviationToleranceUnits > 0f
                ? profile.deviationToleranceUnits
                : ProfileParams.DefaultDeviationTolerance;
            float tolSq = tol * tol;

            // ---- respawn detection + persistence -------------------------------------------------------------
            if (hasPrevPosition && (obs.Position - prevPosition).sqrMagnitude > TeleportSq)
            {
                deaths++;
                if (deaths > profile.persistenceRetries)
                {
                    Abandoned = true;
                    return PlayerAction.Neutral;
                }
                // Repeat the identical failed plan, or improvise for a while first ("try something else").
                improviseUntilTick = ShouldRepeatFailedPlan(profile, rng) ? -1 : tick + ImproviseAfterDeathTicks;
                noticeTick = -1;
            }
            stalledTicks = hasPrevPosition && (obs.Position - prevPosition).sqrMagnitude < 0.0004f
                ? stalledTicks + 1
                : 0;
            prevPosition = obs.Position;
            hasPrevPosition = true;

            // ---- deviation detection / re-anchor -------------------------------------------------------------
            bool offRoute = planIndex >= alignedRef.Count ||
                            (obs.Position - alignedRef[planIndex]).sqrMagnitude > tolSq;

            if (offRoute)
            {
                if (noticeTick < 0)
                    noticeTick = tick;

                bool noticed = tick - noticeTick >= profile.reactionDelayTicks;
                if (noticed && tick >= improviseUntilTick)
                {
                    nearestIndex = NearestReferenceIndex(obs.Position);
                    if ((obs.Position - alignedRef[nearestIndex]).sqrMagnitude <= tolSq &&
                        nearestIndex < mutatedPlan.Count)
                    {
                        planIndex = nearestIndex;
                        improvising = false;
                        noticeTick = -1;
                    }
                    else
                    {
                        improvising = true;
                    }
                }
                else if (improvising)
                {
                    nearestIndex = NearestReferenceIndex(obs.Position);
                }
            }
            else
            {
                noticeTick = -1;
                improvising = false;
            }

            if (improvising || planIndex >= mutatedPlan.Count)
                return Improvise(obs);

            return mutatedPlan[planIndex++];
        }

        /// <summary>Nearest reference index to a live position. Linear scan, no allocation; only runs while the
        /// agent is off route.</summary>
        // ponytail: O(n) scan (~750 entries) per off-route tick. A spatial index would be faster and pointless —
        // it only runs during recovery, and recovery is rare on a plan the agent is mostly following.
        int NearestReferenceIndex(Vector2 position)
        {
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < alignedRef.Count; i++)
            {
                float dx = alignedRef[i].x - position.x;
                float dy = alignedRef[i].y - position.y;
                float sq = dx * dx + dy * dy;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Search-free recovery controller: walk toward a lookahead waypoint on the reference route, jump when
        /// the waypoint is above or the agent is stalled against geometry, wall-jump when pinned, and spend a
        /// dash when falling short of a waypoint that is ahead and not below. Models a player improvising back
        /// onto their route — it is not a planner and makes no completeness claim.
        /// </summary>
        PlayerAction Improvise(Observation obs)
        {
            Vector2 target = alignedRef[Math.Min(nearestIndex + WaypointLookahead, alignedRef.Count - 1)];
            float dx = target.x - obs.Position.x;
            float dy = target.y - obs.Position.y;

            PlayerAction a = PlayerAction.Neutral;
            a.MoveX = dx > 0.2f ? 1f : dx < -0.2f ? -1f : 0f;
            a.MoveY = dy > 1f ? 1f : dy < -1f ? -1f : 0f;

            bool pinned = (obs.OnRightWall && dx > 0f) || (obs.OnLeftWall && dx < 0f);
            if (obs.IsGrounded && (dy > 0.5f || stalledTicks > 6))
            {
                a.JumpPressed = true;
                a.JumpHeld = true;
                jumpHoldRemaining = 8;
            }
            else if (!obs.IsGrounded && pinned && jumpHoldRemaining <= 0)
            {
                a.JumpPressed = true;
                a.JumpHeld = true;
                jumpHoldRemaining = 8;
            }
            else if (jumpHoldRemaining > 0)
            {
                a.JumpHeld = dy > 0f;
                jumpHoldRemaining--;
            }

            if (!obs.IsGrounded && !obs.IsDashing && obs.Velocity.y < 0f && obs.DashesRemaining > 0 &&
                dy > -1f && dx * dx + dy * dy > 4f)
            {
                a.DashPressed = true;
                a.MoveX = dx > 0.5f ? 1f : dx < -0.5f ? -1f : 0f;
                a.MoveY = dy > 0.5f ? 1f : dy < -0.5f ? -1f : 0f;
            }

            return a;
        }

        // ---- plan-time solver config -------------------------------------------------------------------------

        /// <summary>
        /// Applies the profile's plan-time limitations to a solver config: caps search depth at
        /// <see cref="ProfileParams.planningHorizon"/>. NOTE: the "no wall-jump chains" mechanic-knowledge
        /// limitation described in the spec (drop diagonal dash candidates for beginners) is NOT applied here —
        /// BeamSearchSolver enumerates its macro alphabet internally with no per-run filtering hook, and adding
        /// one is out of scope for this file. Depth capping is the only lever this MVP actually pulls; the
        /// mechanic-knowledge gap is currently represented only by <see cref="ProfileParams.knowsWallJumpChains"/>
        /// staying unused at plan time — a known, documented limitation, not a bug.
        /// </summary>
        public static SolverConfig MakeSolverConfig(ProfileParams profile, SolverConfig baseConfig)
        {
            SolverConfig config = baseConfig;
            if (profile.planningHorizon < config.MaxMacrosDepth)
                config.MaxMacrosDepth = profile.planningHorizon;
            return config;
        }

        // ---- persistence (batch-runner helpers, not used within a single episode) ----------------------------

        /// <summary>True while attemptIndex (0-based) is still within the profile's retry budget for one obstacle.</summary>
        public static bool ShouldRetry(ProfileParams profile, int attemptIndex) => attemptIndex < profile.persistenceRetries;

        /// <summary>Whether the next retry should replay the identical failed plan instead of replanning.</summary>
        public static bool ShouldRepeatFailedPlan(ProfileParams profile, Random rng) => rng.NextDouble() < profile.repeatFailedStrategyProb;

        // ---- reaction delay -----------------------------------------------------------------------------------

        static List<PlayerAction> ApplyReactionDelay(List<PlayerAction> plan, int delayTicks)
        {
            if (delayTicks <= 0)
                return new List<PlayerAction>(plan);

            List<PlayerAction> shifted = new List<PlayerAction>(plan.Count + delayTicks);
            for (int i = 0; i < delayTicks; i++)
                shifted.Add(PlayerAction.Neutral);
            shifted.AddRange(plan);
            return shifted;
        }

        // ---- timing jitter --------------------------------------------------------------------------------

        struct Edge
        {
            public int Start;
            public int Length; // contiguous span (jump-held run, or 1 for a dash)
            public List<PlayerAction> Values;
        }

        static void ApplyTimingJitter(List<PlayerAction> plan, float sigma, Random rng)
        {
            if (sigma <= 0f)
                return;

            List<Edge> edges = FindEdges(plan);
            if (edges.Count == 0)
                return;

            // Clear original span positions (drop the press/held flags but keep neutral placeholders).
            for (int e = 0; e < edges.Count; e++)
            {
                Edge edge = edges[e];
                for (int i = 0; i < edge.Length; i++)
                    plan[edge.Start + i] = PlayerAction.Neutral;
            }

            int minAllowedStart = 0;
            for (int e = 0; e < edges.Count; e++)
            {
                Edge edge = edges[e];
                int delta = RoundGaussian(rng, sigma);
                int newStart = edge.Start + delta;
                if (newStart < minAllowedStart)
                    newStart = minAllowedStart;

                while (plan.Count < newStart + edge.Length)
                    plan.Add(PlayerAction.Neutral);

                for (int i = 0; i < edge.Length; i++)
                    plan[newStart + i] = edge.Values[i];

                minAllowedStart = newStart + edge.Length; // next edge must start after this one: order preserved
            }
        }

        static List<Edge> FindEdges(List<PlayerAction> plan)
        {
            List<Edge> edges = new List<Edge>();
            int i = 0;
            while (i < plan.Count)
            {
                PlayerAction a = plan[i];
                if (a.JumpPressed)
                {
                    int len = 0;
                    while (i + len < plan.Count && plan[i + len].JumpHeld)
                        len++;
                    if (len == 0) len = 1;
                    edges.Add(MakeEdge(plan, i, len));
                    i += len;
                }
                else if (a.DashPressed)
                {
                    edges.Add(MakeEdge(plan, i, 1));
                    i += 1;
                }
                else
                {
                    i++;
                }
            }
            return edges;
        }

        static Edge MakeEdge(List<PlayerAction> plan, int start, int length)
        {
            List<PlayerAction> values = new List<PlayerAction>(length);
            for (int i = 0; i < length; i++)
                values.Add(plan[start + i]);
            return new Edge { Start = start, Length = length, Values = values };
        }

        /// <summary>Box-Muller sample from the seeded RNG, rounded to the nearest tick.</summary>
        static int RoundGaussian(Random rng, float sigma)
        {
            double u1 = 1.0 - rng.NextDouble(); // (0,1]
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return (int)Math.Round(z * sigma);
        }

        // ---- direction error ------------------------------------------------------------------------------

        // 8 compass directions in 45-degree steps, index order matches rotation direction.
        static readonly int[] DirLookupX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        static readonly int[] DirLookupY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        static void ApplyDirectionError(List<PlayerAction> plan, float directionErrorProb, Random rng)
        {
            if (directionErrorProb <= 0f)
                return;

            // Dash edges: rotate direction one 45-degree step, random sign.
            int i = 0;
            while (i < plan.Count)
            {
                if (plan[i].DashPressed)
                {
                    if (rng.NextDouble() < directionErrorProb)
                    {
                        int dashX = (int)Math.Round(plan[i].MoveX);
                        int dashY = (int)Math.Round(plan[i].MoveY);
                        int idx = FindDirIndex(dashX, dashY);
                        if (idx >= 0)
                        {
                            int sign = rng.NextDouble() < 0.5 ? -1 : 1;
                            int newIdx = ((idx + sign) % 8 + 8) % 8;
                            PlayerAction a = plan[i];
                            a.MoveX = DirLookupX[newIdx];
                            a.MoveY = DirLookupY[newIdx];
                            plan[i] = a;
                        }
                    }
                    i++;
                }
                else
                {
                    i++;
                }
            }

            // Movement segments (contiguous same MoveX): momentary wrong direction for the first 5 ticks.
            float segErrorProb = directionErrorProb / 4f;
            int segStart = 0;
            while (segStart < plan.Count)
            {
                float moveX = plan[segStart].MoveX;
                int segEnd = segStart;
                while (segEnd < plan.Count && plan[segEnd].MoveX == moveX)
                    segEnd++;

                if (moveX != 0f && rng.NextDouble() < segErrorProb)
                {
                    int flipCount = Math.Min(5, segEnd - segStart);
                    for (int t = 0; t < flipCount; t++)
                    {
                        PlayerAction a = plan[segStart + t];
                        a.MoveX = -a.MoveX;
                        plan[segStart + t] = a;
                    }
                }

                segStart = segEnd;
            }
        }

        static int FindDirIndex(int dx, int dy)
        {
            for (int i = 0; i < 8; i++)
            {
                if (DirLookupX[i] == dx && DirLookupY[i] == dy)
                    return i;
            }
            return -1; // e.g. dx==dy==0: no direction to rotate
        }
    }
}
