using System.Collections.Generic;
using System.Reflection;
using CelesteBenchmark;
using UnityEngine;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Only file (besides VirtualInputSource) allowed to reference CelesteBenchmark types (module-boundaries.md).
    /// Drives a CelesteBenchmarkPlayer via the virtual-input seam and reads its public state into Observation.
    ///
    /// Event coverage (MVP): only Death (via CelesteBenchmarkPlayer.Died) and GoalReached (goal-rect containment)
    /// are detected. Checkpoint/DashRefill/Spring events have no simulator-side hook yet; adding adapter-owned
    /// sensor triggers for them is deferred to a later seam task.
    ///
    /// Moving-platform tick ownership (T4): cached BenchmarkMovingPlatforms are switched to tick-driven in Bind
    /// and advanced manually in TickSimulation (platforms MovePosition, then player Tick, then physics Simulate),
    /// so each arena's platforms step on that arena's manual clock rather than Unity's global FixedUpdate.
    ///
    /// T11 extends the same seam to BenchmarkCrumblingPlatform and BenchmarkDashRefill (their WaitForSeconds
    /// countdowns became float timers), because those fire constantly in SampleScene and an Update-clock timer
    /// breaks tick-exact replay.
    /// </summary>
    public sealed class CelesteBenchmarkAdapter : IGameAdapter
    {
        const int GroundLayer = 7;
        const int MovingPlatformLayer = 8;
        const int OneWayLayer = 9;
        const int HazardLayer = 10;
        const int CrumbleLayer = 12;
        const int GridQueryMask = (1 << GroundLayer) | (1 << MovingPlatformLayer) | (1 << OneWayLayer) | (1 << HazardLayer) | (1 << CrumbleLayer);
        const float EntityRadius = 12f;

        Arena arena;
        ScenarioConfig scenario;
        CelesteBenchmarkPlayer player;
        readonly VirtualInputSource virtualInput = new VirtualInputSource();

        BenchmarkMovingPlatform[] movingPlatforms = System.Array.Empty<BenchmarkMovingPlatform>();
        BenchmarkSpring[] springs = System.Array.Empty<BenchmarkSpring>();
        BenchmarkDashRefill[] dashRefills = System.Array.Empty<BenchmarkDashRefill>();
        BenchmarkCrumblingPlatform[] crumblingPlatforms = System.Array.Empty<BenchmarkCrumblingPlatform>();
        BenchmarkGoal[] goals = System.Array.Empty<BenchmarkGoal>();

        // ADR-008 tunable overrides: captured at Bind, restored (idempotently) at teardown. Whitelist below.
        struct AppliedOverride { public object Target; public FieldInfo Field; public float Original; }
        readonly List<AppliedOverride> appliedOverrides = new List<AppliedOverride>();
        static readonly HashSet<string> PlayerFields = new HashSet<string>
        { "coyoteTime", "jumpBufferTime", "dashSpeed", "jumpVelocity", "movementSpeed" };

        readonly List<TimedGameEvent> pendingEvents = new List<TimedGameEvent>();
        readonly Collider2D[] gridQueryBuffer = new Collider2D[4];
        readonly ContactFilter2D gridContactFilter;

        bool diedThisStep;
        bool prevJumpHeld;
        int currentTick;

        public bool IsDead { get; private set; }
        public bool IsComplete { get; private set; }
        public float Progress { get; private set; }

        public CelesteBenchmarkAdapter()
        {
            gridContactFilter = new ContactFilter2D();
            gridContactFilter.SetLayerMask(GridQueryMask);
            gridContactFilter.useTriggers = true;
        }

        public void Bind(Arena boundArena, ScenarioConfig boundScenario)
        {
            arena = boundArena;
            scenario = boundScenario;

            player = null;
            GameObject[] roots = arena.Scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length && player == null; i++)
                player = roots[i].GetComponentInChildren<CelesteBenchmarkPlayer>(true);

            if (player == null)
            {
                Debug.LogError("CelesteBenchmarkAdapter.Bind: no CelesteBenchmarkPlayer found in arena scene.");
                return;
            }

            player.SetTickDriven(true);
            player.SetVirtualInput(virtualInput);
            player.SetPhysicsScene(arena.PhysicsScene2D);
            player.Died += OnPlayerDied;

            List<BenchmarkMovingPlatform> platformList = new List<BenchmarkMovingPlatform>();
            List<BenchmarkSpring> springList = new List<BenchmarkSpring>();
            List<BenchmarkDashRefill> refillList = new List<BenchmarkDashRefill>();
            List<BenchmarkCrumblingPlatform> crumblingList = new List<BenchmarkCrumblingPlatform>();
            List<BenchmarkGoal> goalList = new List<BenchmarkGoal>();
            for (int i = 0; i < roots.Length; i++)
            {
                platformList.AddRange(roots[i].GetComponentsInChildren<BenchmarkMovingPlatform>(true));
                springList.AddRange(roots[i].GetComponentsInChildren<BenchmarkSpring>(true));
                refillList.AddRange(roots[i].GetComponentsInChildren<BenchmarkDashRefill>(true));
                crumblingList.AddRange(roots[i].GetComponentsInChildren<BenchmarkCrumblingPlatform>(true));
                BenchmarkGoal[] foundGoals = roots[i].GetComponentsInChildren<BenchmarkGoal>(true);
                for (int g = 0; g < foundGoals.Length; g++)
                {
                    if (foundGoals[g].gameObject.activeInHierarchy)
                        goalList.Add(foundGoals[g]);
                }
            }
            movingPlatforms = platformList.ToArray();
            springs = springList.ToArray();
            dashRefills = refillList.ToArray();
            crumblingPlatforms = crumblingList.ToArray();
            goals = goalList.ToArray();

            for (int i = 0; i < movingPlatforms.Length; i++)
            {
                if (movingPlatforms[i])
                    movingPlatforms[i].SetTickDriven(true);
            }
            for (int i = 0; i < crumblingPlatforms.Length; i++)
            {
                if (crumblingPlatforms[i])
                    crumblingPlatforms[i].SetTickDriven(true);
            }
            for (int i = 0; i < dashRefills.Length; i++)
            {
                if (dashRefills[i])
                    dashRefills[i].SetTickDriven(true);
            }

            ApplyOverrides();
        }

        // ADR-008: apply whitelisted tunable overrides once at Bind, capturing originals for exact restoration.
        // Unknown targetId or field is a hard error — the whitelist keeps the surface honest and small.
        void ApplyOverrides()
        {
            // Re-binding with un-restored overrides would capture already-overridden values
            // as "originals" and corrupt restoration — restore first, defensively.
            if (appliedOverrides.Count > 0)
                RestoreOverrides();
            appliedOverrides.Clear();
            if (scenario == null || scenario.overrides == null)
                return;

            for (int i = 0; i < scenario.overrides.Count; i++)
            {
                TunableOverride ov = scenario.overrides[i];
                object target;
                if (ov.targetId == "player")
                {
                    if (player == null)
                        throw new System.InvalidOperationException("TunableOverride targets 'player' but no player was bound.");
                    if (!PlayerFields.Contains(ov.field))
                        throw new System.ArgumentException($"TunableOverride: field '{ov.field}' is not whitelisted for target 'player'.");
                    target = player;
                }
                else if (ov.targetId != null && ov.targetId.StartsWith("platform:"))
                {
                    if (ov.field != "speed")
                        throw new System.ArgumentException($"TunableOverride: field '{ov.field}' is not whitelisted for a platform target (only 'speed').");
                    string name = ov.targetId.Substring("platform:".Length);
                    target = FindPlatform(name);
                    if (target == null)
                        throw new System.ArgumentException($"TunableOverride: no BenchmarkMovingPlatform named '{name}' in the arena.");
                }
                else
                {
                    throw new System.ArgumentException($"TunableOverride: unknown targetId '{ov.targetId}' (expected 'player' or 'platform:<name>').");
                }

                FieldInfo field = target.GetType().GetField(ov.field, BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.FieldType != typeof(float))
                    throw new System.ArgumentException($"TunableOverride: field '{ov.field}' not found as a public float on {target.GetType().Name}.");

                appliedOverrides.Add(new AppliedOverride { Target = target, Field = field, Original = (float)field.GetValue(target) });
                field.SetValue(target, ov.value);
            }
        }

        BenchmarkMovingPlatform FindPlatform(string name)
        {
            for (int i = 0; i < movingPlatforms.Length; i++)
            {
                if (movingPlatforms[i] && movingPlatforms[i].gameObject.name == name)
                    return movingPlatforms[i];
            }
            return null;
        }

        public void ResetEpisode(int seed)
        {
            pendingEvents.Clear();
            IsDead = false;
            IsComplete = false;
            Progress = 0f;
            diedThisStep = false;
            currentTick = 0;
            prevJumpHeld = false;

            if (player != null)
                player.ResetForEpisode(scenario.spawnPosition);

            for (int i = 0; i < dashRefills.Length; i++)
            {
                if (dashRefills[i])
                    dashRefills[i].ResetState();
            }

            for (int i = 0; i < crumblingPlatforms.Length; i++)
            {
                if (crumblingPlatforms[i])
                    crumblingPlatforms[i].ResetState();
            }

            for (int i = 0; i < movingPlatforms.Length; i++)
            {
                if (movingPlatforms[i])
                    movingPlatforms[i].ResetState();
            }
        }

        public void ApplyAction(in PlayerAction action)
        {
            virtualInput.MoveX = action.MoveX;
            virtualInput.MoveY = action.MoveY;
            virtualInput.JumpHeld = action.JumpHeld;
            virtualInput.ClimbHeld = action.ClimbHeld;
            virtualInput.JumpPressedEdge = action.JumpPressed;
            virtualInput.DashPressedEdge = action.DashPressed;
            virtualInput.JumpReleasedEdge = prevJumpHeld && !action.JumpHeld;
            prevJumpHeld = action.JumpHeld;
        }

        public void TickSimulation(float dt)
        {
            // World objects first (platforms MovePosition, crumble/refill countdowns), then player logic, then a
            // single physics step — preserves the old FixedUpdate (world + player) then Simulate ordering, now on
            // this arena's manual clock. Crumble/refill timers are advanced BEFORE the player so a platform that
            // expires this tick is already non-colliding when the player's move resolves, matching the coroutine
            // ordering (coroutines run before FixedUpdate physics).
            for (int i = 0; i < movingPlatforms.Length; i++)
            {
                if (movingPlatforms[i])
                    movingPlatforms[i].Tick(dt);
            }
            for (int i = 0; i < crumblingPlatforms.Length; i++)
            {
                if (crumblingPlatforms[i])
                    crumblingPlatforms[i].Tick(dt);
            }
            for (int i = 0; i < dashRefills.Length; i++)
            {
                if (dashRefills[i])
                    dashRefills[i].Tick(dt);
            }
            if (player != null)
                player.Tick(dt);
            arena.Step(dt);
        }

        public void ReadObservation(Observation target)
        {
            if (player == null)
                return;

            Vector2 pos = player.transform.position;
            target.Position = pos;
            target.Velocity = player.Velocity;
            target.IsGrounded = player.IsGrounded;
            target.OnLeftWall = player.TouchingLeftWall;
            target.OnRightWall = player.TouchingRightWall;
            target.IsDashing = player.IsDashing;
            target.IsClimbing = player.IsClimbing;
            target.DashesRemaining = player.DashesRemaining;
            target.Stamina = player.ClimbStamina;

            float goalX = scenario.goalRect.center.x;
            target.Progress = ProgressMath.Progress(pos.x, scenario.spawnPosition.x, goalX);
            target.SectionIndex = ProgressMath.SectionIndexFor(pos.x, scenario.sectionBoundariesX);
            Progress = target.Progress;

            FillGrid(target, pos);
            FillEntities(target, pos);

            IsComplete = ContainsGoal(pos);
        }

        bool ContainsGoal(Vector2 position)
        {
            if (goals.Length == 0)
                return scenario.goalRect.Contains(position);

            int highestPriority = int.MinValue;
            for (int i = 0; i < goals.Length; i++)
            {
                if (goals[i] && goals[i].Priority > highestPriority)
                    highestPriority = goals[i].Priority;
            }
            for (int i = 0; i < goals.Length; i++)
            {
                BenchmarkGoal goal = goals[i];
                if (goal && goal.Priority == highestPriority && goal.Trigger.OverlapPoint(position))
                    return true;
            }
            return false;
        }

        void FillGrid(Observation target, Vector2 center)
        {
            target.ClearGrid();

            int originX = Mathf.FloorToInt(center.x) - Observation.GridWidth / 2;
            int originY = Mathf.FloorToInt(center.y) - Observation.GridHeight / 2;

            for (int gx = 0; gx < Observation.GridWidth; gx++)
            {
                for (int gy = 0; gy < Observation.GridHeight; gy++)
                {
                    Vector2 cellCenter = new Vector2(originX + gx + 0.5f, originY + gy + 0.5f);
                    int count = arena.PhysicsScene2D.OverlapBox(cellCenter, new Vector2(0.95f, 0.95f), 0f, gridContactFilter, gridQueryBuffer);
                    target.Grid[gx, gy] = ClassifyCell(count);
                }
            }
        }

        CellKind ClassifyCell(int count)
        {
            CellKind best = CellKind.Empty;
            for (int i = 0; i < count; i++)
            {
                Collider2D col = gridQueryBuffer[i];
                if (!col)
                    continue;

                int layer = col.gameObject.layer;
                if (layer == HazardLayer)
                    return CellKind.Hazard;
                if (layer == OneWayLayer && best != CellKind.Solid)
                    best = CellKind.OneWay;
                else if (layer == GroundLayer || layer == MovingPlatformLayer || layer == CrumbleLayer)
                    best = CellKind.Solid;
            }
            return best;
        }

        void FillEntities(Observation target, Vector2 center)
        {
            target.Entities.Clear();
            float radiusSq = EntityRadius * EntityRadius;

            for (int i = 0; i < movingPlatforms.Length; i++)
            {
                BenchmarkMovingPlatform platform = movingPlatforms[i];
                if (!platform)
                    continue;
                Vector2 p = platform.transform.position;
                if ((p - center).sqrMagnitude > radiusSq)
                    continue;
                target.Entities.Add(new DynamicEntity
                {
                    Kind = DynamicEntityKind.MovingPlatform,
                    Position = p,
                    Velocity = platform.Velocity,
                    Size = Vector2.one
                });
            }

            for (int i = 0; i < springs.Length; i++)
            {
                BenchmarkSpring spring = springs[i];
                if (!spring)
                    continue;
                Vector2 p = spring.transform.position;
                if ((p - center).sqrMagnitude > radiusSq)
                    continue;
                target.Entities.Add(new DynamicEntity
                {
                    Kind = DynamicEntityKind.Spring,
                    Position = p,
                    Velocity = Vector2.zero,
                    Size = Vector2.one
                });
            }

            for (int i = 0; i < dashRefills.Length; i++)
            {
                BenchmarkDashRefill refill = dashRefills[i];
                if (!refill)
                    continue;
                Vector2 p = refill.transform.position;
                if ((p - center).sqrMagnitude > radiusSq)
                    continue;
                target.Entities.Add(new DynamicEntity
                {
                    Kind = DynamicEntityKind.DashRefill,
                    Position = p,
                    Velocity = Vector2.zero,
                    Size = Vector2.one
                });
            }

            target.Entities.Add(new DynamicEntity
            {
                Kind = DynamicEntityKind.Goal,
                Position = scenario.goalRect.center,
                Velocity = Vector2.zero,
                Size = scenario.goalRect.size
            });
        }

        public void AfterStep(int tick)
        {
            currentTick = tick;
            virtualInput.ClearEdges();

            IsDead = diedThisStep;
            diedThisStep = false;

            if (IsComplete)
                pendingEvents.Add(new TimedGameEvent { Tick = tick, Kind = GameEventKind.GoalReached });
        }

        public void DrainEvents(List<TimedGameEvent> into)
        {
            for (int i = 0; i < pendingEvents.Count; i++)
                into.Add(pendingEvents[i]);
            pendingEvents.Clear();
        }

        public void RestoreOverrides()
        {
            // Restore exact captured originals, then clear so a second call is a no-op (idempotent).
            for (int i = 0; i < appliedOverrides.Count; i++)
            {
                AppliedOverride a = appliedOverrides[i];
                if (a.Target != null && a.Field != null)
                    a.Field.SetValue(a.Target, a.Original);
            }
            appliedOverrides.Clear();
        }

        void OnPlayerDied()
        {
            diedThisStep = true;
            pendingEvents.Add(new TimedGameEvent { Tick = currentTick, Kind = GameEventKind.Death });
        }
    }
}
