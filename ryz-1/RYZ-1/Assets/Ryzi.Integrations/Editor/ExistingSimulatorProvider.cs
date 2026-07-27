using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using CelesteBenchmark;
using PlatformerPlaytest;
using PlatformerPlaytest.Live;
using PlatformerPlaytest.Solver;
using Ryzi.Editor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ryzi.Integrations.ExistingSimulator
{
    public sealed class ExistingSimulatorProvider : IPlaytestIntegrationProvider, IGameViewReplayProvider
    {
        const string ProviderId = "celeste-benchmark-explicit-api";
        const string PackageVersion = "0.1.0";
        const string JumpChannel = "button.0";
        const string DashChannel = "button.1";
        const string ClimbChannel = "button.2";

        ArenaManager gameReplayArenaManager;
        LivePlaybackDriver gameReplayDriver;

        public string Id => ProviderId;
        public string DisplayName => "Existing deterministic simulator";
        public bool RequiresPlayMode => true;
        public bool IsGameViewReplayActive => gameReplayDriver != null;
        public bool IsGameViewReplayComplete => gameReplayDriver != null && gameReplayDriver.IsComplete;
        public int GameViewReplayTick => gameReplayDriver != null ? gameReplayDriver.CurrentTick : 0;

        public bool CanHandle(SceneDiscoveryResult discovery, out string reason)
        {
            if (discovery?.SelectedPlayer?.Value == null)
            {
                reason = "No selected player.";
                return false;
            }
            if (discovery.SelectedPlayer.Value.GetComponent<CelesteBenchmarkPlayer>() == null)
            {
                reason = "Selected player is not a CelesteBenchmarkPlayer.";
                return false;
            }
            if (string.IsNullOrEmpty(discovery.ScenePath))
            {
                reason = "The active scene must be saved before isolated simulation.";
                return false;
            }
            reason = "Explicit simulator API and deterministic adapter are available.";
            return true;
        }

        public IEnumerator Calibrate(
            SceneDiscoveryResult discovery,
            CancellationToken cancellationToken,
            Action<CalibrationReport> completed)
        {
            EnsurePlayMode();
            Stopwatch watch = Stopwatch.StartNew();
            ArenaManager manager = new ArenaManager();
            Arena arena = null;
            List<CalibrationProbeResult> probes = new List<CalibrationProbeResult>();
            List<string> warnings = new List<string>
            {
                "Airborne, wall-contact, and resource-empty preconditions are not synthesized in this first provider."
            };
            bool restored = false;

            yield return manager.LoadSceneArena(discovery.ScenePath, value => arena = value);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                BoundSimulator bound = Bind(arena);

                probes.Add(RunProbe(bound, "baseline.no-input", Repeat(PlayerAction.Neutral, 20), cancellationToken));
                probes.Add(RunProbe(bound, "axis.move.negative", Move(-1f, 24), cancellationToken));
                probes.Add(RunProbe(bound, "axis.move.positive", Move(1f, 24), cancellationToken));
                probes.Add(RunProbe(bound, "button.0.short", Jump(1, 20), cancellationToken));
                probes.Add(RunProbe(bound, "button.0.long", Jump(12, 24), cancellationToken));
                probes.Add(RunProbe(bound, "button.1.directional", Dash(1f, 1f, 20), cancellationToken));

                UniversalObservation repeatA = RunEnd(bound, Move(1f, 30), cancellationToken);
                UniversalObservation repeatB = RunEnd(bound, Move(1f, 30), cancellationToken);
                bool deterministic = Vector2.Distance(repeatA.position, repeatB.position) <= 0.0001f &&
                                     Vector2.Distance(repeatA.velocity, repeatB.velocity) <= 0.0001f;
                probes.Add(new CalibrationProbeResult
                {
                    probeId = "reset.deterministic-repeatability",
                    completed = true,
                    before = repeatA,
                    after = repeatB,
                    confidence = deterministic ? 1f : 0f,
                    warnings = deterministic
                        ? Array.Empty<string>()
                        : new[] { "Matched reset/action streams diverged; deterministic replay must not be claimed." }
                });

                bound.Adapter.RestoreOverrides();
                restored = true;
                CalibrationReport report = new CalibrationReport
                {
                    completed = true,
                    stateRestored = true,
                    deterministicRepeatability = deterministic,
                    probes = probes.ToArray(),
                    warnings = warnings.ToArray()
                };
                ReconcileManifest(discovery.Manifest, report);
                watch.Stop();
                report.elapsedMilliseconds = watch.ElapsedMilliseconds;
                completed?.Invoke(report);
            }
            catch (OperationCanceledException)
            {
                watch.Stop();
                completed?.Invoke(new CalibrationReport
                {
                    cancelled = true,
                    stateRestored = restored,
                    elapsedMilliseconds = watch.ElapsedMilliseconds,
                    probes = probes.ToArray(),
                    warnings = warnings.ToArray()
                });
            }
            finally
            {
                manager.UnloadAll();
            }
        }

        public IEnumerator RunTest(
            SceneDiscoveryResult discovery,
            MechanicsManifest manifest,
            PlayerProfile profile,
            CancellationToken cancellationToken,
            Action<SimulationRunReport> completed)
        {
            EnsurePlayMode();
            ArenaManager manager = new ArenaManager();
            Arena arena = null;
            yield return manager.LoadSceneArena(discovery.ScenePath, value => arena = value);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                BoundSimulator bound = Bind(arena);

                SolverConfig config = ConfigFor(profile);
                string cacheKey = SolutionCache.MakeKey(
                    bound.Scenario.CacheIdentity(bound.Scenario.scenarioId), config);
                bool cacheHit = false;
                string cacheStatus = "cache miss";
                SegmentedSolveResult solve;

                if (TryLoadVerifiedCachedSolve(bound, cacheKey, cancellationToken, out solve, out string cacheWarning))
                {
                    cacheHit = true;
                    cacheStatus = "cache hit; clean-reset replay verified";
                }
                else
                {
                    if (!string.IsNullOrEmpty(cacheWarning))
                        cacheStatus = "cache invalidated: " + cacheWarning;

                    SegmentedSolver solver = new SegmentedSolver();
                    solver.OnSegment = (_, __) => cancellationToken.ThrowIfCancellationRequested();
                    solve = solver.Solve(bound.Adapter, bound.Scenario, config);
                    if (solve.Solved)
                    {
                        SolutionCache.Save(cacheKey, solve.ActionStream);
                        cacheStatus += "; fresh verified solution saved";
                    }
                    else
                        cacheStatus += "; no incomplete solution was cached";
                }

                SimulationRunReport report = BuildRunReport(
                    discovery, manifest, profile, bound, solve, cacheHit, cacheKey, cacheStatus, cancellationToken);
                completed?.Invoke(report);
            }
            catch (OperationCanceledException)
            {
                completed?.Invoke(new SimulationRunReport
                {
                    scenarioId = discovery.ScenePath,
                    manifestVersion = manifest.manifestVersion,
                    packageVersion = PackageVersion,
                    unityVersion = Application.unityVersion,
                    profileId = profile?.id ?? "standard",
                    solverDiagnostic = "Cancelled at a safe solver boundary."
                });
            }
            finally
            {
                manager.UnloadAll();
            }
        }

        public IEnumerator StartGameViewReplay(
            SceneDiscoveryResult discovery,
            ReplayRecord replay,
            CancellationToken cancellationToken,
            Action<string> completed)
        {
            EnsurePlayMode();
            if (discovery == null || string.IsNullOrEmpty(discovery.ScenePath))
                throw new InvalidOperationException("Scan a saved scene before starting a Game View replay.");
            if (replay == null || replay.actions == null || replay.actions.Length == 0)
                throw new InvalidOperationException("This run has no recorded actions to replay in Game View.");

            StopGameViewReplay();
            gameReplayArenaManager = new ArenaManager();
            Arena arena = null;
            yield return gameReplayArenaManager.LoadSceneArena(discovery.ScenePath, value => arena = value);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                BoundSimulator bound = Bind(arena);
                List<PlayerAction> actions = ToPlayerActions(replay.actions);
                if (actions.Count == 0)
                    throw new InvalidOperationException("The replay action stream could not be mapped to this simulator.");

                Transform playerTransform = FindPlayer(arena)?.transform;
                if (playerTransform == null)
                    throw new InvalidOperationException("The replay arena does not contain a player transform.");

                VisibleLevelBuilder.AddFollowCamera(arena.Scene, playerTransform);
                GameObject host = new GameObject("RyziGameViewReplay");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, arena.Scene);
                gameReplayDriver = host.AddComponent<LivePlaybackDriver>();
                gameReplayDriver.Adapter = bound.Adapter;
                gameReplayDriver.Agent = new ReplayAgent(actions);
                gameReplayDriver.Scenario = bound.Scenario;
                gameReplayDriver.Seed = replay.seed;
                gameReplayDriver.PlaybackTickLimit = actions.Count;
                SimulationDiagnosticsOverlay diagnostics = host.AddComponent<SimulationDiagnosticsOverlay>();
                diagnostics.Bind(gameReplayDriver, playerTransform);
                gameReplayDriver.Restart();
                gameReplayDriver.Pause();

                completed?.Invoke(
                    $"Game View replay ready: {actions.Count} recorded ticks. Use Play, Step, or Jump to Failure.");
            }
            catch
            {
                StopGameViewReplay();
                throw;
            }
        }

        public void PlayGameViewReplay() => gameReplayDriver?.Play();

        public void PauseGameViewReplay() => gameReplayDriver?.Pause();

        public void StepGameViewReplay() => gameReplayDriver?.StepOnce();

        public void RestartGameViewReplay()
        {
            if (gameReplayDriver == null)
                return;
            gameReplayDriver.Restart();
            gameReplayDriver.Pause();
        }

        public void JumpGameViewReplayToTick(int tick)
        {
            if (gameReplayDriver == null)
                return;
            gameReplayDriver.Restart();
            gameReplayDriver.Pause();
            int targetTick = Mathf.Clamp(tick, 0, gameReplayDriver.PlaybackTickLimit);
            while (gameReplayDriver.CurrentTick < targetTick && !gameReplayDriver.IsComplete)
                gameReplayDriver.StepOnce();
        }

        public void StopGameViewReplay()
        {
            if (gameReplayDriver != null)
                UnityEngine.Object.Destroy(gameReplayDriver.gameObject);
            gameReplayDriver = null;
            if (gameReplayArenaManager != null)
            {
                gameReplayArenaManager.UnloadAll();
                gameReplayArenaManager = null;
            }
        }

        public IEnumerator RunCounterfactual(
            SceneDiscoveryResult discovery,
            MechanicsManifest manifest,
            SimulationRunReport baseline,
            CancellationToken cancellationToken,
            Action<CounterfactualReport> completed)
        {
            EnsurePlayMode();
            if (baseline == null || string.IsNullOrEmpty(baseline.replayPath) || !File.Exists(baseline.replayPath))
                throw new InvalidOperationException("Run a successful baseline test before the counterfactual.");

            ReplayRecord replay = JsonUtility.FromJson<ReplayRecord>(File.ReadAllText(baseline.replayPath));
            List<PlayerAction> actions = ToPlayerActions(replay.actions);
            float original = FindOriginalJump(discovery);
            float[] candidates = { original * 0.85f, original, original * 1.15f };
            CounterfactualVariantResult[] variants = new CounterfactualVariantResult[candidates.Length];
            Stopwatch watch = Stopwatch.StartNew();
            ArenaManager manager = new ArenaManager();
            string recoveryPath = WriteRecoveryMarker(original);
            bool restored = true;
            bool cancelled = false;

            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    Arena arena = null;
                    yield return manager.LoadSceneArena(discovery.ScenePath, value => arena = value);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    BoundSimulator bound = Bind(arena, candidates[i]);
                    try
                    {
                        variants[i] = ReplayVariant(bound, actions, replay.seed, candidates[i], cancellationToken);
                    }
                    finally
                    {
                        bound.Adapter.RestoreOverrides();
                        CelesteBenchmarkPlayer player = FindPlayer(arena);
                        if (player == null || Mathf.Abs(player.jumpVelocity - original) > 0.0001f)
                            restored = false;
                    }
                }
            }
            finally
            {
                manager.UnloadAll();
                if (File.Exists(recoveryPath))
                    File.Delete(recoveryPath);
            }

            watch.Stop();
            completed?.Invoke(new CounterfactualReport
            {
                tunableId = "player.jumpVelocity",
                displayName = "Jump Velocity",
                originalValue = original,
                originalRestored = restored,
                cancelled = cancelled,
                elapsedMilliseconds = watch.ElapsedMilliseconds,
                variants = variants,
                warnings = new[]
                {
                    "Variants replay the same baseline action stream and seed; they measure robustness, not re-optimized solutions."
                }
            });
        }

        static BoundSimulator Bind(Arena arena, float? jumpOverride = null)
        {
            if (arena == null)
                throw new InvalidOperationException("The isolated scene arena did not load.");
            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            if (jumpOverride.HasValue)
            {
                scenario.overrides.Add(new TunableOverride
                {
                    targetId = "player",
                    field = "jumpVelocity",
                    value = jumpOverride.Value
                });
            }
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);
            return new BoundSimulator(adapter, scenario);
        }

        static CalibrationProbeResult RunProbe(
            BoundSimulator bound,
            string id,
            ProbeStep[] steps,
            CancellationToken cancellationToken)
        {
            bound.Adapter.ResetEpisode(0);
            Observation observation = new Observation();
            bound.Adapter.ReadObservation(observation);
            UniversalObservation before = Convert(observation);
            List<PlaytestEvent> events = new List<PlaytestEvent>();
            UniversalAction[] actions = new UniversalAction[steps.Length];

            for (int i = 0; i < steps.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                actions[i] = steps[i].Universal;
                PlayerAction action = steps[i].Player;
                bound.Adapter.ApplyAction(in action);
                bound.Adapter.TickSimulation(bound.Scenario.fixedDeltaTime);
                bound.Adapter.AfterStep(i);
                List<TimedGameEvent> drained = new List<TimedGameEvent>();
                bound.Adapter.DrainEvents(drained);
                for (int e = 0; e < drained.Count; e++)
                {
                    events.Add(new PlaytestEvent
                    {
                        tick = drained[e].Tick,
                        eventId = drained[e].Kind.ToString()
                    });
                }
            }

            bound.Adapter.ReadObservation(observation);
            UniversalObservation after = Convert(observation);
            float magnitude = Vector2.Distance(before.position, after.position) +
                              Vector2.Distance(before.velocity, after.velocity) * 0.1f;
            return new CalibrationProbeResult
            {
                probeId = id,
                completed = true,
                before = before,
                actions = actions,
                after = after,
                events = events.ToArray(),
                confidence = id == "baseline.no-input" ? 0.9f : Mathf.Clamp01(magnitude / 2f)
            };
        }

        static UniversalObservation RunEnd(
            BoundSimulator bound,
            ProbeStep[] steps,
            CancellationToken cancellationToken)
        {
            CalibrationProbeResult probe = RunProbe(bound, "repeat", steps, cancellationToken);
            return probe.after;
        }

        /// <summary>
        /// A cache entry is an optimization, never proof. Re-execute it through the bound Unity simulator before
        /// exposing it to reports or replay. Invalid/corrupt entries are removed so the next run falls back to
        /// a fresh segmented solve instead of repeatedly trusting stale data.
        /// </summary>
        static bool TryLoadVerifiedCachedSolve(
            BoundSimulator bound,
            string cacheKey,
            CancellationToken cancellationToken,
            out SegmentedSolveResult solve,
            out string warning)
        {
            solve = new SegmentedSolveResult
            {
                ActionStream = new List<PlayerAction>(),
                BestEffortActionStream = new List<PlayerAction>(),
                FailedSegment = -1,
                FurthestX = float.NegativeInfinity
            };
            warning = null;

            List<PlayerAction> cached;
            try
            {
                if (!SolutionCache.TryLoad(cacheKey, out cached))
                    return false;
            }
            catch (Exception ex)
            {
                SolutionCache.Clear(cacheKey);
                warning = "cache could not be read (" + ex.Message + ")";
                return false;
            }

            if (cached == null || cached.Count == 0)
            {
                SolutionCache.Clear(cacheKey);
                warning = "cache contained no actions";
                return false;
            }

            Stopwatch watch = Stopwatch.StartNew();
            Observation observation = new Observation();
            bound.Adapter.ResetEpisode(0);
            bound.Adapter.ReadObservation(observation);
            float furthestX = observation.Position.x;

            for (int i = 0; i < cached.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlayerAction action = cached[i];
                bound.Adapter.ApplyAction(in action);
                bound.Adapter.TickSimulation(bound.Scenario.fixedDeltaTime);
                bound.Adapter.AfterStep(i);
                bound.Adapter.ReadObservation(observation);
                furthestX = Mathf.Max(furthestX, observation.Position.x);
                if (!bound.Adapter.IsComplete)
                    continue;

                watch.Stop();
                List<PlayerAction> verified = new List<PlayerAction>(i + 1);
                for (int a = 0; a <= i; a++)
                    verified.Add(cached[a]);
                solve = new SegmentedSolveResult
                {
                    Solved = true,
                    ActionStream = verified,
                    BestEffortActionStream = new List<PlayerAction>(verified),
                    SegmentsSolved = (bound.Scenario.sectionBoundariesX?.Length ?? 0) + 1,
                    SegmentCount = (bound.Scenario.sectionBoundariesX?.Length ?? 0) + 1,
                    FailedSegment = -1,
                    FurthestX = furthestX,
                    TicksSimulated = i + 1,
                    ElapsedMs = watch.ElapsedMilliseconds,
                    Diagnostic = "verified cached solution"
                };
                return true;
            }

            SolutionCache.Clear(cacheKey);
            warning = "clean-reset replay did not complete";
            return false;
        }

        static SimulationRunReport BuildRunReport(
            SceneDiscoveryResult discovery,
            MechanicsManifest manifest,
            PlayerProfile profile,
            BoundSimulator bound,
            SegmentedSolveResult solve,
            bool cacheHit,
            string cacheKey,
            string cacheStatus,
            CancellationToken cancellationToken)
        {
            Stopwatch telemetryWatch = Stopwatch.StartNew();
            List<ReplayKeyframe> keyframes = new List<ReplayKeyframe>();
            List<SerializedUniversalAction> actions = new List<SerializedUniversalAction>();
            List<Vector2> failures = new List<Vector2>();
            int completionTick = -1;
            int failureTick = -1;
            bool sawDeath = false;
            float furthest = 0f;
            Observation observation = new Observation();
            List<PlayerAction> trace = solve.Solved
                ? solve.ActionStream
                : solve.BestEffortActionStream;

            if (trace != null)
            {
                bound.Adapter.ResetEpisode(0);
                bound.Adapter.ReadObservation(observation);
                if (trace.Count == 0)
                    keyframes.Add(Keyframe(0, observation));

                for (int i = 0; i < trace.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PlayerAction playerAction = trace[i];
                    UniversalAction universal = ToUniversal(in playerAction);
                    actions.Add(SerializedUniversalAction.From(in universal));

                    bound.Adapter.ApplyAction(in playerAction);
                    bound.Adapter.TickSimulation(bound.Scenario.fixedDeltaTime);
                    bound.Adapter.AfterStep(i);
                    bound.Adapter.ReadObservation(observation);
                    furthest = Mathf.Max(furthest, observation.Progress);
                    if (bound.Adapter.IsDead)
                    {
                        sawDeath = true;
                        failures.Add(observation.Position);
                        if (failureTick < 0)
                            failureTick = i;
                    }
                    if (i % 5 == 0 || bound.Adapter.IsComplete || i == trace.Count - 1)
                        keyframes.Add(Keyframe(i, observation));
                    if (bound.Adapter.IsComplete)
                    {
                        completionTick = i;
                        break;
                    }
                }

                // A search-limit failure does not necessarily kill the player. Mark the terminal observed state
                // so the SceneView overlay can show the exact point represented by this partial trace.
                if (!solve.Solved && failureTick < 0 && keyframes.Count > 0)
                {
                    failureTick = keyframes[keyframes.Count - 1].tick;
                    failures.Add(keyframes[keyframes.Count - 1].position);
                }
            }

            string runId = "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string directory = LocalDataPathService.CreateRunDirectory(runId);
            string replayPath = LocalDataPathService.Guard(Path.Combine(directory, "replay.json"));
            ReplayRecord replay = new ReplayRecord
            {
                scenarioId = bound.Scenario.scenarioId,
                seed = 0,
                packageVersion = PackageVersion,
                manifestVersion = manifest.manifestVersion,
                fixedDeltaTime = bound.Scenario.fixedDeltaTime,
                failureTick = failureTick,
                isPartial = !solve.Solved,
                terminalStatus = solve.Solved && completionTick >= 0
                    ? "Completed"
                    : failureTick >= 0 ? (sawDeath ? "Death" : "SearchLimit") : "Empty",
                actions = actions.ToArray(),
                keyframes = keyframes.ToArray(),
                deterministicVerificationPassed = solve.Solved && completionTick >= 0,
                verificationMessage = solve.Solved && completionTick >= 0
                    ? "Fresh replay reached the discovered goal."
                    : trace != null && trace.Count > 0
                        ? "Partial best-effort trace from a clean reset; it did not reach the target."
                        : "No replayable candidate trace was produced."
            };
            File.WriteAllText(replayPath, JsonUtility.ToJson(replay, true));

            SimulationRunReport report = new SimulationRunReport
            {
                runId = runId,
                scenarioId = bound.Scenario.scenarioId,
                manifestVersion = manifest.manifestVersion,
                packageVersion = PackageVersion,
                unityVersion = Application.unityVersion,
                projectRevision = TryGitRevision(),
                agentVersion = "segmented-beam-baseline@1",
                profileId = profile?.id ?? "standard",
                seed = 0,
                fixedDeltaTime = bound.Scenario.fixedDeltaTime,
                settingsHash = bound.Scenario.CacheIdentity(),
                runCount = 1,
                completedRuns = completionTick >= 0 ? 1 : 0,
                completionRate = completionTick >= 0 ? 1f : 0f,
                completionTick = completionTick,
                failureCount = solve.Solved ? failures.Count : Mathf.Max(1, failures.Count),
                failurePositions = failures.ToArray(),
                furthestProgress = trace != null && trace.Count > 0
                    ? furthest
                    : ProgressFromX(solve.FurthestX, bound.Scenario),
                solverExpansions = solve.NodesExpanded,
                simulationTicks = solve.TicksSimulated,
                solverMilliseconds = solve.ElapsedMs,
                solverCacheHit = cacheHit,
                solverCacheKey = cacheKey,
                solverCacheStatus = cacheStatus,
                solverSucceeded = solve.Solved,
                solverDiagnostic = solve.Diagnostic,
                discoveredMechanics = MechanicNames(manifest),
                unresolvedIssues = IssueNames(manifest),
                replayPath = replayPath,
                runDirectory = directory
            };
            telemetryWatch.Stop();
            report.telemetryMilliseconds = telemetryWatch.ElapsedMilliseconds;
            string reportPath = LocalDataPathService.Guard(Path.Combine(directory, "run.json"));
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
            return report;
        }

        static CounterfactualVariantResult ReplayVariant(
            BoundSimulator bound,
            List<PlayerAction> actions,
            int seed,
            float candidate,
            CancellationToken cancellationToken)
        {
            bound.Adapter.ResetEpisode(seed);
            Observation observation = new Observation();
            int deaths = 0;
            float furthest = 0f;
            for (int i = 0; i < actions.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new CounterfactualVariantResult
                    {
                        candidateValue = candidate,
                        seed = seed,
                        deaths = deaths,
                        furthestProgress = furthest,
                        diagnostic = "Cancelled before replay completed."
                    };
                }
                PlayerAction action = actions[i];
                bound.Adapter.ApplyAction(in action);
                bound.Adapter.TickSimulation(bound.Scenario.fixedDeltaTime);
                bound.Adapter.AfterStep(i);
                bound.Adapter.ReadObservation(observation);
                furthest = Mathf.Max(furthest, observation.Progress);
                if (bound.Adapter.IsDead)
                    deaths++;
                if (bound.Adapter.IsComplete)
                {
                    return new CounterfactualVariantResult
                    {
                        candidateValue = candidate,
                        seed = seed,
                        completed = true,
                        completionTick = i,
                        deaths = deaths,
                        furthestProgress = furthest
                    };
                }
            }
            return new CounterfactualVariantResult
            {
                candidateValue = candidate,
                seed = seed,
                deaths = deaths,
                furthestProgress = furthest,
                diagnostic = "Matched action stream did not reach the goal."
            };
        }

        static SolverConfig ConfigFor(PlayerProfile profile)
        {
            SolverConfig config = SampleSceneScenario.SolverConfig;
            if (profile == null)
                return config;
            if (profile.id == "constrained")
            {
                config.BeamWidth = 12;
                config.MaxMacrosDepth = Mathf.Min(38, profile.searchDepth);
                config.MaxTicksSimulated = 1_500_000;
            }
            else if (profile.id == "precision")
            {
                config.BeamWidth = 24;
                config.MaxMacrosDepth = Mathf.Max(50, profile.searchDepth);
            }
            return config;
        }

        static void ReconcileManifest(MechanicsManifest manifest, CalibrationReport report)
        {
            if (manifest == null)
                return;
            for (int m = 0; m < manifest.mechanics.Length; m++)
            {
                MechanicDefinition mechanic = manifest.mechanics[m];
                List<RuntimeEvidence> evidence = new List<RuntimeEvidence>();
                float confidence = 0f;
                for (int p = 0; p < report.probes.Length; p++)
                {
                    CalibrationProbeResult probe = report.probes[p];
                    if (mechanic.trigger.channelIds.Length == 0 ||
                        !probe.probeId.Contains(mechanic.trigger.channelIds[0]))
                        continue;
                    float magnitude = probe.after == null || probe.before == null
                        ? 0f
                        : Vector2.Distance(probe.before.position, probe.after.position);
                    evidence.Add(new RuntimeEvidence
                    {
                        probeId = probe.probeId,
                        summary = $"Observed position delta {magnitude:F3}.",
                        observedMagnitude = magnitude,
                        level = EvidenceLevel.RuntimeObserved
                    });
                    confidence = Mathf.Max(confidence, probe.confidence);
                }
                mechanic.runtimeEvidence = evidence.ToArray();
                mechanic.runtimeConfidence = confidence;
            }
        }

        static ProbeStep[] Repeat(PlayerAction action, int ticks)
        {
            ProbeStep[] result = new ProbeStep[ticks];
            for (int i = 0; i < ticks; i++)
                result[i] = new ProbeStep(action, ToUniversal(in action));
            return result;
        }

        static ProbeStep[] Move(float x, int ticks)
        {
            PlayerAction action = PlayerAction.Neutral;
            action.MoveX = x;
            return Repeat(action, ticks);
        }

        static ProbeStep[] Jump(int heldTicks, int totalTicks)
        {
            ProbeStep[] result = new ProbeStep[totalTicks];
            for (int i = 0; i < totalTicks; i++)
            {
                PlayerAction action = PlayerAction.Neutral;
                action.JumpPressed = i == 0;
                action.JumpHeld = i < heldTicks;
                result[i] = new ProbeStep(action, ToUniversal(in action, i == heldTicks));
            }
            return result;
        }

        static ProbeStep[] Dash(float x, float y, int totalTicks)
        {
            ProbeStep[] result = new ProbeStep[totalTicks];
            for (int i = 0; i < totalTicks; i++)
            {
                PlayerAction action = PlayerAction.Neutral;
                action.MoveX = x;
                action.MoveY = y;
                action.DashPressed = i == 0;
                result[i] = new ProbeStep(action, ToUniversal(in action));
            }
            return result;
        }

        static UniversalAction ToUniversal(in PlayerAction action, bool jumpReleased = false)
        {
            List<ButtonActionState> buttons = new List<ButtonActionState>(3);
            if (action.JumpPressed || action.JumpHeld || jumpReleased)
                buttons.Add(new ButtonActionState(JumpChannel, action.JumpPressed, action.JumpHeld, jumpReleased));
            if (action.DashPressed)
                buttons.Add(new ButtonActionState(DashChannel, action.DashPressed, action.DashPressed, false));
            if (action.ClimbHeld)
                buttons.Add(new ButtonActionState(ClimbChannel, false, true, false));
            return new UniversalAction(
                new Vector2(action.MoveX, action.MoveY),
                Vector2.zero,
                buttons);
        }

        static List<PlayerAction> ToPlayerActions(SerializedUniversalAction[] serialized)
        {
            List<PlayerAction> result = new List<PlayerAction>(serialized?.Length ?? 0);
            if (serialized == null)
                return result;
            for (int i = 0; i < serialized.Length; i++)
            {
                UniversalAction universal = serialized[i].ToAction();
                PlayerAction action = PlayerAction.Neutral;
                action.MoveX = universal.MoveAxis.x;
                action.MoveY = universal.MoveAxis.y;
                if (universal.TryGetButton(JumpChannel, out ButtonActionState jump))
                {
                    action.JumpPressed = jump.PressedThisTick;
                    action.JumpHeld = jump.Held;
                }
                if (universal.TryGetButton(DashChannel, out ButtonActionState dash))
                    action.DashPressed = dash.PressedThisTick;
                if (universal.TryGetButton(ClimbChannel, out ButtonActionState climb))
                    action.ClimbHeld = climb.Held;
                result.Add(action);
            }
            return result;
        }

        static UniversalObservation Convert(Observation source)
        {
            return new UniversalObservation
            {
                position = source.Position,
                velocity = source.Velocity,
                facing = source.Velocity.x < -0.01f ? -1 : source.Velocity.x > 0.01f ? 1 : 0,
                grounded = source.IsGrounded,
                wallLeft = source.OnLeftWall,
                wallRight = source.OnRightWall,
                movementStateId = source.IsDashing ? "state.3" : source.IsClimbing ? "state.4" : "state.0",
                resourceChannels = new[]
                {
                    new NumericChannel { id = "resource.0", value = source.DashesRemaining },
                    new NumericChannel { id = "resource.1", value = source.Stamina }
                },
                regionId = source.SectionIndex.ToString(),
                progress = source.Progress
            };
        }

        static ReplayKeyframe Keyframe(int tick, Observation observation)
        {
            int flags = StateFlags.From(observation);
            return new ReplayKeyframe
            {
                tick = tick,
                position = observation.Position,
                velocity = observation.Velocity,
                progress = observation.Progress,
                stateFlags = flags,
                stateHash = StateHash.Compute(
                    observation.Position, observation.Velocity, flags, observation.DashesRemaining).ToString("X16")
            };
        }

        static float FindOriginalJump(SceneDiscoveryResult discovery)
        {
            CelesteBenchmarkPlayer player = discovery.SelectedPlayer.Value.GetComponent<CelesteBenchmarkPlayer>();
            if (player == null)
                throw new InvalidOperationException("The selected player no longer has the expected controller.");
            return player.jumpVelocity;
        }

        static CelesteBenchmarkPlayer FindPlayer(Arena arena)
        {
            GameObject[] roots = arena.Scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                CelesteBenchmarkPlayer player = roots[i].GetComponentInChildren<CelesteBenchmarkPlayer>(true);
                if (player != null)
                    return player;
            }
            return null;
        }

        static string WriteRecoveryMarker(float original)
        {
            string directory = LocalDataPathService.EnsureDirectory(LocalDataPathService.RecoveryRoot);
            string path = LocalDataPathService.Guard(Path.Combine(directory, "counterfactual-active.json"));
            File.WriteAllText(path, JsonUtility.ToJson(new RecoveryMarker
            {
                target = "isolated-arena:player.jumpVelocity",
                originalValue = original
            }, true));
            return path;
        }

        static float ProgressFromX(float x, ScenarioConfig scenario)
        {
            if (float.IsNegativeInfinity(x))
                return 0f;
            return Mathf.InverseLerp(scenario.spawnPosition.x, scenario.goalRect.center.x, x);
        }

        static string[] MechanicNames(MechanicsManifest manifest)
        {
            string[] values = new string[manifest.mechanics.Length];
            for (int i = 0; i < values.Length; i++)
                values[i] = manifest.mechanics[i].suggestedName;
            return values;
        }

        static string[] IssueNames(MechanicsManifest manifest)
        {
            string[] values = new string[manifest.issues.Length];
            for (int i = 0; i < values.Length; i++)
                values[i] = manifest.issues[i].summary;
            return values;
        }

        static string TryGitRevision()
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    WorkingDirectory = LocalDataPathService.ProjectRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                        return string.Empty;
                    string value = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(1000);
                    return process.ExitCode == 0 ? value : string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Ryzi could not read the project revision: " + ex.Message);
                return string.Empty;
            }
        }

        static void EnsurePlayMode()
        {
            if (!Application.isPlaying)
                throw new InvalidOperationException(
                    "This integration requires Play Mode because Unity local 2D physics scenes cannot be created in Edit Mode.");
        }

        readonly struct BoundSimulator
        {
            public readonly CelesteBenchmarkAdapter Adapter;
            public readonly ScenarioConfig Scenario;

            public BoundSimulator(CelesteBenchmarkAdapter adapter, ScenarioConfig scenario)
            {
                Adapter = adapter;
                Scenario = scenario;
            }
        }

        readonly struct ProbeStep
        {
            public readonly PlayerAction Player;
            public readonly UniversalAction Universal;

            public ProbeStep(PlayerAction player, UniversalAction universal)
            {
                Player = player;
                Universal = universal;
            }
        }

        [Serializable]
        sealed class RecoveryMarker
        {
            public string target;
            public float originalValue;
        }
    }
}
