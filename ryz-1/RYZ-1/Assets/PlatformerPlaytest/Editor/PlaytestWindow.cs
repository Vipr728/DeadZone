using System;
using System.Collections.Generic;
using System.IO;
using PlatformerPlaytest.Analysis;
using PlatformerPlaytest.Live;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PlatformerPlaytest.Editor
{
    /// <summary>
    /// MVP entry hook for the Run tab's batch button. Real batch execution (T8/final integration) registers
    /// <see cref="Execute"/>; until then the button reports the honest "not wired up yet" state instead of faking
    /// a run. See DECISION note in the T7 task: editor-driven batch runs need a level factory that isn't owned by
    /// this file (ArenaManager/Arena/CelesteBenchmarkAdapter belong to a concurrent task), so this stays a seam.
    /// </summary>
    public static class DemoBatchRunner
    {
        /// <summary>Register the real implementation here. Signature: (episodeCount, profileIds, level) -> status text.</summary>
        public static Func<int, string[], PlaytestLevel, string> Execute;

        public static string Run(int episodes, string[] profiles, PlaytestLevel level)
        {
            if (Execute != null)
                return Execute(episodes, profiles, level);
            throw new NotImplementedException(
                "DemoBatchRunner.Execute is not registered. In-editor batch runs need a level factory owned by " +
                "the Core/Adapter task (T8) to wire an ephemeral arena; until that lands, run batches via " +
                "PlayMode tests (see Tests/PlayMode/EpisodeRunnerPlayModeTests.cs) and load the resulting run " +
                "with the Results tab.");
        }
    }

    /// <summary>
    /// Tools/Platformer Playtest window. Three tabs: Run (batch trigger seam), Results (load + analyze a run),
    /// Replay (episode picker + metadata; playback deferred post-MVP). UI Toolkit only.
    /// </summary>
    public sealed class PlaytestWindow : EditorWindow
    {
        // ---- loaded run state -----------------------------------------------------------------------------
        string loadedRunDir;
        RunHeader header;
        readonly List<EpisodeSummary> episodes = new List<EpisodeSummary>();
        readonly List<DeathRecord> deaths = new List<DeathRecord>();
        List<SectionStats> sectionStats = new List<SectionStats>();
        List<Finding> findings = new List<Finding>();

        VisualElement runPane, resultsPane, replayPane, watchPane;
        Label statusLabel;

        // ---- watch state (T10) -----------------------------------------------------------------------------
        ArenaManager watchArenaManager;
        LivePlaybackDriver watchDriver;
        Label watchStatusLabel;
        ProgressBar watchProgressBar;
        int watchTotalTicks;
        DropdownField watchLevelDropdown;
        EditorCoroutinePump watchPump;

        // ---- run tab state (T12) ----------------------------------------------------------------------------
        DropdownField runLevelDropdown;
        ArenaManager runArenaManager;
        EditorCoroutinePump runPump;

        static readonly List<string> LevelChoices = new List<string> { "SampleScene (real level)", "Demo level" };

        static PlaytestLevel LevelFromChoice(string choice) =>
            choice == "Demo level" ? PlaytestLevel.Demo : PlaytestLevel.SampleScene;

        [MenuItem("Tools/Platformer Playtest")]
        public static void Open()
        {
            PlaytestWindow wnd = GetWindow<PlaytestWindow>();
            wnd.titleContent = new GUIContent("Platformer Playtest");
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;

            VisualElement tabs = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            Button runTabBtn = new Button(() => ShowPane(runPane)) { text = "Run" };
            Button resultsTabBtn = new Button(() => ShowPane(resultsPane)) { text = "Results" };
            Button replayTabBtn = new Button(() => ShowPane(replayPane)) { text = "Replay" };
            Button watchTabBtn = new Button(() => ShowPane(watchPane)) { text = "Watch" };
            tabs.Add(runTabBtn);
            tabs.Add(resultsTabBtn);
            tabs.Add(replayTabBtn);
            tabs.Add(watchTabBtn);
            root.Add(tabs);

            runPane = BuildRunTab();
            resultsPane = BuildResultsTab();
            replayPane = BuildReplayTab();
            watchPane = BuildWatchTab();
            root.Add(runPane);
            root.Add(resultsPane);
            root.Add(replayPane);
            root.Add(watchPane);

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += RefreshWatchStatus;

            ShowPane(runPane);
        }

        void ShowPane(VisualElement pane)
        {
            runPane.style.display = pane == runPane ? DisplayStyle.Flex : DisplayStyle.None;
            resultsPane.style.display = pane == resultsPane ? DisplayStyle.Flex : DisplayStyle.None;
            replayPane.style.display = pane == replayPane ? DisplayStyle.Flex : DisplayStyle.None;
            watchPane.style.display = pane == watchPane ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---- Run tab ----------------------------------------------------------------------------------------

        VisualElement BuildRunTab()
        {
            VisualElement pane = new VisualElement();
            pane.Add(new HelpBox(
                "Runs an ephemeral in-code demo level: plans one expert solution, then runs N episodes per " +
                "selected profile through the synthetic ProfiledAgent and records telemetry under " +
                "Library/PlatformerPlaytest/runs/. If manual physics does not step in edit mode, the status line " +
                "says so and directs you to the PlayMode tests instead of writing empty data.",
                HelpBoxMessageType.Info));

            DropdownField levelDropdown = new DropdownField("Level", LevelChoices, 0);
            IntegerField episodesField = new IntegerField("Episodes") { value = 10 };
            Toggle beginnerToggle = new Toggle("Beginner") { value = true };
            Toggle intermediateToggle = new Toggle("Intermediate") { value = true };
            Toggle expertToggle = new Toggle("Expert") { value = true };
            Label runStatus = new Label();

            Button runButton = new Button(() =>
            {
                List<string> profiles = new List<string>();
                if (beginnerToggle.value) profiles.Add("Beginner");
                if (intermediateToggle.value) profiles.Add("Intermediate");
                if (expertToggle.value) profiles.Add("Expert");
                RunBatchClicked(LevelFromChoice(levelDropdown.value), episodesField.value, profiles.ToArray(), runStatus);
            })
            { text = "Run Batch" };

            runLevelDropdown = levelDropdown;
            pane.Add(levelDropdown);
            pane.Add(episodesField);
            pane.Add(beginnerToggle);
            pane.Add(intermediateToggle);
            pane.Add(expertToggle);
            pane.Add(runButton);
            pane.Add(runStatus);
            return pane;
        }

        void RunBatchClicked(PlaytestLevel level, int episodes, string[] profiles, Label runStatus)
        {
            if (level == PlaytestLevel.Demo)
            {
                try
                {
                    runStatus.text = DemoBatchRunner.Run(episodes, profiles, PlaytestLevel.Demo);
                }
                catch (NotImplementedException ex)
                {
                    runStatus.text = ex.Message;
                }
                return;
            }

            // SampleScene: needs an async ArenaManager.LoadSceneArena load before the batch can run.
            if (!Application.isPlaying)
            {
                runStatus.text = "Enter Play Mode to run a SampleScene batch (LoadSceneArena is Play-Mode-only).";
                return;
            }

            runPump?.Cancel();
            runArenaManager?.UnloadAll();
            runArenaManager = new ArenaManager();
            runStatus.text = "Loading SampleScene...";

            runPump = EditorCoroutinePump.Run(
                runArenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, arena =>
                {
                    runStatus.text = "SampleScene loaded. Solving/running batch (may take up to ~2 min on a cache miss)...";
                    string result = DemoBatchRunnerImpl.RunSampleSceneBatch(arena, episodes, profiles);
                    runStatus.text = result;
                    runArenaManager.UnloadAll();
                    runArenaManager = null;
                }),
                onComplete: null,
                onError: ex =>
                {
                    runStatus.text = "SampleScene batch failed: " + ex.Message;
                    runArenaManager?.UnloadAll();
                    runArenaManager = null;
                });
        }

        // ---- Results tab --------------------------------------------------------------------------------------

        VisualElement BuildResultsTab()
        {
            VisualElement pane = new VisualElement();
            pane.Add(new HelpBox(
                "Profile results below come from SYNTHETIC player profiles: parameterized degradations of solver " +
                "plans, NOT validated human player models. Do not read them as human completion rates.",
                HelpBoxMessageType.Warning));
            statusLabel = new Label("No run loaded.");

            Button loadButton = new Button(LoadRunViaDialog) { text = "Load Run" };
            Button overlayToggle = new Button(ToggleHeatmapOverlay) { text = "Toggle Death Heatmap Overlay" };

            ScrollView completionTable = new ScrollView();
            ScrollView findingsList = new ScrollView();
            ScrollView sectionBars = new ScrollView();

            pane.Add(loadButton);
            pane.Add(overlayToggle);
            pane.Add(statusLabel);
            pane.Add(new Label("Completion by (agent, profile)") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            pane.Add(completionTable);
            pane.Add(new Label("Findings") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            pane.Add(findingsList);
            pane.Add(new Label("Deaths per section") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            pane.Add(sectionBars);

            resultsCompletionTable = completionTable;
            resultsFindingsList = findingsList;
            resultsSectionBars = sectionBars;

            return pane;
        }

        ScrollView resultsCompletionTable, resultsFindingsList, resultsSectionBars;

        void LoadRunViaDialog()
        {
            string runsRoot = PlaytestPaths.RunsRoot;
            string defaultDir = Directory.Exists(runsRoot) ? NewestRunDir(runsRoot) : runsRoot;
            string picked = EditorUtility.OpenFolderPanel("Load Playtest Run", defaultDir ?? runsRoot, "");
            if (string.IsNullOrEmpty(picked))
                return;

            LoadRun(picked);
        }

        static string NewestRunDir(string runsRoot)
        {
            string[] dirs = Directory.GetDirectories(runsRoot);
            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            for (int i = 0; i < dirs.Length; i++)
            {
                DateTime t = Directory.GetLastWriteTimeUtc(dirs[i]);
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = dirs[i];
                }
            }
            return newest ?? runsRoot;
        }

        void LoadRun(string dir)
        {
            loadedRunDir = dir;
            episodes.Clear();
            deaths.Clear();

            string headerPath = Path.Combine(dir, "run.json");
            header = File.Exists(headerPath) ? JsonUtility.FromJson<RunHeader>(File.ReadAllText(headerPath)) : null;

            string episodesPath = Path.Combine(dir, "episodes.jsonl");
            if (File.Exists(episodesPath))
            {
                string[] lines = File.ReadAllLines(episodesPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                        continue;
                    episodes.Add(JsonUtility.FromJson<EpisodeSummary>(lines[i]));
                }
            }

            // Death positions for section/heatmap analysis come from frames.jsonl of episodes that recorded a
            // full trajectory (failures + representative successes, per telemetry.md); summary-only episodes
            // carry no per-tick position data, so they simply contribute no death records here.
            for (int i = 0; i < episodes.Count; i++)
            {
                if (!episodes[i].hasFullTrajectory)
                    continue;
                string framesPath = Path.Combine(dir, $"ep_{episodes[i].episodeId}.frames.jsonl");
                if (!File.Exists(framesPath))
                    continue;
                CollectDeathsFromFrames(framesPath, episodes[i].episodeId);
            }

            int sectionCount = MaxSectionIndex() + 1;
            sectionStats = SectionStatsCalculator.Compute(deaths, Mathf.Max(1, sectionCount));
            findings = DifficultyFlags.FlagDifficultySpikes(sectionStats, deaths);

            statusLabel.text = $"Loaded: {dir} ({episodes.Count} episodes, {deaths.Count} deaths)";
            RefreshResultsViews();
            RefreshReplayEpisodeList();
        }

        int MaxSectionIndex()
        {
            int max = 0;
            for (int i = 0; i < deaths.Count; i++)
                if (deaths[i].SectionIndex > max) max = deaths[i].SectionIndex;
            return max;
        }

        // Manual scan for "ev":"Death" lines rather than a full JSON parser (frames.jsonl is hand-serialized,
        // not JsonUtility output, see TelemetryRecorder.SerializeFrames).
        void CollectDeathsFromFrames(string path, string episodeId)
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.IndexOf("\"ev\":\"Death\"", StringComparison.Ordinal) < 0)
                    continue;

                deaths.Add(new DeathRecord
                {
                    EpisodeId = episodeId,
                    Tick = ExtractInt(line, "\"t\":"),
                    X = ExtractFloat(line, "\"px\":"),
                    Y = ExtractFloat(line, "\"py\":"),
                    SectionIndex = ExtractInt(line, "\"section\":")
                });
            }
        }

        static int ExtractInt(string line, string key)
        {
            float f = ExtractFloat(line, key);
            return Mathf.RoundToInt(f);
        }

        static float ExtractFloat(string line, string key)
        {
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return 0f;
            int start = idx + key.Length;
            int end = start;
            while (end < line.Length && line[end] != ',' && line[end] != '}')
                end++;
            string token = line.Substring(start, end - start);
            return float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        void RefreshResultsViews()
        {
            resultsCompletionTable.Clear();
            List<EpisodeStats> stats = EpisodeStatsCalculator.Compute(episodes);
            for (int i = 0; i < stats.Count; i++)
            {
                EpisodeStats s = stats[i];
                resultsCompletionTable.Add(new Label(
                    $"{s.AgentId} / {s.ProfileId}: {s.CompletionRate * 100f:F0}% completion, " +
                    $"{s.Attempts} attempts, median completion ticks {(s.MedianCompletionTicks < 0 ? "n/a" : s.MedianCompletionTicks.ToString("F0"))}, " +
                    $"median deaths {s.MedianDeaths:F1}, mean furthest progress {s.MeanFurthestProgress:F2}"));
            }

            resultsFindingsList.Clear();
            if (findings.Count == 0)
            {
                resultsFindingsList.Add(new Label("No difficulty spikes flagged."));
            }
            else
            {
                for (int i = 0; i < findings.Count; i++)
                    resultsFindingsList.Add(new Label(findings[i].ToEvidenceText()));
            }

            resultsSectionBars.Clear();
            int maxCount = 1;
            for (int i = 0; i < sectionStats.Count; i++)
                if (sectionStats[i].DeathCount > maxCount) maxCount = sectionStats[i].DeathCount;

            for (int i = 0; i < sectionStats.Count; i++)
            {
                SectionStats s = sectionStats[i];
                VisualElement row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 2 } };
                row.Add(new Label($"Section {s.SectionIndex} ({s.DeathCount})") { style = { width = 140 } });
                VisualElement bar = new VisualElement
                {
                    style =
                    {
                        width = Mathf.Max(2, 200f * s.DeathCount / maxCount),
                        height = 14,
                        backgroundColor = new Color(0.8f, 0.2f, 0.2f)
                    }
                };
                row.Add(bar);
                resultsSectionBars.Add(row);
            }
        }

        void ToggleHeatmapOverlay()
        {
            DeathHeatmapOverlay.SetEnabled(!DeathHeatmapOverlay.IsEnabled, deaths);
        }

        // ---- Replay tab ---------------------------------------------------------------------------------------

        VisualElement BuildReplayTab()
        {
            VisualElement pane = new VisualElement();
            pane.Add(new HelpBox(
                "MVP replay execution lives in tests/API (see EpisodeRunnerPlayModeTests / ReplayVerifier). " +
                "Editor scene playback is post-MVP and is not implemented here — this tab only surfaces episode " +
                "metadata and a copyable replay command.",
                HelpBoxMessageType.Info));
            pane.Add(new HelpBox(
                "Episodes were played by SYNTHETIC profiles (solver-derived, not validated human models).",
                HelpBoxMessageType.Warning));

            DropdownField episodePicker = new DropdownField("Episode", new List<string>(), 0);
            Label metaLabel = new Label();
            Label desyncLabel = new Label("Desync status: not checked (playback not implemented).");
            Button copyButton = new Button(() =>
            {
                if (episodePicker.index < 0 || episodePicker.index >= episodes.Count)
                    return;
                EpisodeSummary ep = episodes[episodePicker.index];
                string cmd = $"replay --run {loadedRunDir} --episode {ep.episodeId} --seed {ep.seed}";
                EditorGUIUtility.systemCopyBuffer = cmd;
                metaLabel.text = $"Copied: {cmd}";
            })
            { text = "Copy Replay Command" };

            episodePicker.RegisterValueChangedCallback(_ =>
            {
                if (episodePicker.index < 0 || episodePicker.index >= episodes.Count)
                    return;
                EpisodeSummary ep = episodes[episodePicker.index];
                metaLabel.text = $"episodeId={ep.episodeId} outcome={ep.outcome} steps={ep.steps} deaths={ep.deaths} " +
                                  $"furthestProgress={ep.furthestProgress:F2} hasFullTrajectory={ep.hasFullTrajectory}";
            });

            pane.Add(episodePicker);
            pane.Add(copyButton);
            pane.Add(metaLabel);
            pane.Add(desyncLabel);

            replayEpisodePicker = episodePicker;
            return pane;
        }

        DropdownField replayEpisodePicker;

        void OnDisable()
        {
            DeathHeatmapOverlay.Disable();
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= RefreshWatchStatus;
            StopWatch();
            StopRunBatch();
        }

        void OnDestroy()
        {
            StopWatch();
            StopRunBatch();
        }

        // ---- Watch tab (T10) --------------------------------------------------------------------------------

        VisualElement BuildWatchTab()
        {
            VisualElement pane = new VisualElement();
            pane.Add(new HelpBox(
                "Episodes shown here (profile modes) come from SYNTHETIC player profiles — parameterized " +
                "degradations of a solver plan, not validated human models.", HelpBoxMessageType.Warning));

            HelpBox playModeWarning = new HelpBox(
                "Enter Play Mode to watch. Arena scenes (SceneManager.CreateScene with a local Physics2D world) " +
                "only exist during Play Mode.", HelpBoxMessageType.Info);
            pane.Add(playModeWarning);

            // -- level selection --
            DropdownField levelDropdown = new DropdownField("Level", LevelChoices, 0);
            pane.Add(levelDropdown);
            watchLevelDropdown = levelDropdown;

            // -- live agent source --
            pane.Add(new Label("Watch live agent") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 6 } });
            DropdownField profileDropdown = new DropdownField("Profile",
                new List<string> { "Solver (expert plan)", "Beginner", "Intermediate", "Expert" }, 0);
            IntegerField seedField = new IntegerField("Seed") { value = 1 };
            Button watchLiveButton = new Button(() => WatchLive(LevelFromChoice(levelDropdown.value), profileDropdown.value, seedField.value)) { text = "Watch" };
            Button clearCacheButton = new Button(() =>
            {
                PlaytestLevel level = LevelFromChoice(levelDropdown.value);
                bool cleared = CachedSolver.Clear(level.ScenarioId(), SampleSceneScenario.SolverConfig);
                watchStatusLabel.text = cleared
                    ? $"Cleared cached solution for '{level.ScenarioId()}'."
                    : $"No cached solution for '{level.ScenarioId()}' to clear.";
            })
            { text = "Clear cached solution" };
            pane.Add(profileDropdown);
            pane.Add(seedField);
            pane.Add(watchLiveButton);
            pane.Add(clearCacheButton);

            // -- recorded episode source --
            pane.Add(new Label("Watch recorded episode") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 10 } });
            pane.Add(new HelpBox("Uses the episode selected in the Replay tab's picker and the currently loaded run.",
                HelpBoxMessageType.Info));
            Button watchRecordedButton = new Button(WatchRecordedEpisode) { text = "Watch Selected Recorded Episode" };
            Button jumpToFailureButton = new Button(JumpToFailure) { text = "Jump to Failure" };
            pane.Add(watchRecordedButton);
            pane.Add(jumpToFailureButton);

            // -- transport controls --
            pane.Add(new Label("Transport") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 10 } });
            VisualElement transportRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            Button playPauseButton = new Button(TogglePlayPause) { text = "Play/Pause" };
            Button stepButton = new Button(() => watchDriver?.StepOnce()) { text = "Step" };
            Button restartButton = new Button(() => watchDriver?.Restart()) { text = "Restart" };
            Button stopButton = new Button(StopWatch) { text = "Stop" };
            transportRow.Add(playPauseButton);
            transportRow.Add(stepButton);
            transportRow.Add(restartButton);
            transportRow.Add(stopButton);
            pane.Add(transportRow);

            Slider speedSlider = new Slider("Speed", 0.25f, 8f) { value = 1f };
            speedSlider.RegisterValueChangedCallback(evt => watchDriver?.SetSpeed(evt.newValue));
            pane.Add(speedSlider);

            watchProgressBar = new ProgressBar
            {
                lowValue = 0f,
                highValue = 100f,
                value = 0f,
                title = "Playthrough 0%"
            };
            watchProgressBar.style.marginTop = 4;
            pane.Add(watchProgressBar);

            watchStatusLabel = new Label("Not watching.");
            pane.Add(watchStatusLabel);

            watchPlayModeWarning = playModeWarning;
            return pane;
        }

        HelpBox watchPlayModeWarning;

        void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                StopWatch();
                StopRunBatch();
            }
        }

        /// <summary>Cancels any in-flight editor-pumped SampleScene load/batch and unloads its arena. Idempotent.</summary>
        void StopRunBatch()
        {
            runPump?.Cancel();
            runPump = null;
            if (runArenaManager != null)
            {
                runArenaManager.UnloadAll();
                runArenaManager = null;
            }
        }

        void TogglePlayPause()
        {
            if (watchDriver == null)
                return;
            if (watchDriver.IsPaused)
                watchDriver.Play();
            else
                watchDriver.Pause();
        }

        static IAgent AgentFor(string profileChoice, List<PlayerAction> basePlan,
            IGameAdapter adapter, ScenarioConfig scenario)
        {
            if (profileChoice == "Solver (expert plan)")
                return new ReplayAgent(basePlan);
            ProfileParams profile = profileChoice switch
            {
                "Beginner" => ProfileParams.Beginner,
                "Intermediate" => ProfileParams.Intermediate,
                _ => ProfileParams.Expert
            };
            ProfiledAgent agent = new ProfiledAgent(basePlan, profile, profileSalt: 0);
            // Closed loop: the watched agent needs the plan's reference trajectory to re-anchor after drift.
            agent.SetReferenceTrajectory(ProfiledAgent.RecordTrajectory(adapter, scenario, basePlan));
            return agent;
        }

        void WatchLive(PlaytestLevel level, string profileChoice, int seed)
        {
            if (!Application.isPlaying)
            {
                watchStatusLabel.text = "Enter Play Mode to watch.";
                return;
            }

            StopWatch();

            if (level == PlaytestLevel.Demo)
            {
                watchArenaManager = new ArenaManager();
                Arena arena = watchArenaManager.CreateArena("watch-live", DemoLevel.Build);
                CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
                ScenarioConfig scenario = DemoLevel.MakeScenario();
                adapter.Bind(arena, scenario);

                SolverConfig solverConfig = SolverConfig.Default;
                solverConfig.Seed = 1;
                solverConfig.MaxMacrosDepth = 40;
                SolveResult solve = new BeamSearchSolver().Solve(adapter, scenario, solverConfig);
                if (!solve.Solved)
                {
                    watchStatusLabel.text = $"Solver failed to plan the demo level: {solve.Diagnostic}";
                    watchArenaManager.UnloadAll();
                    watchArenaManager = null;
                    return;
                }

                Transform playerTransform = FindPlayerTransform(arena.Scene);
                VisibleLevelBuilder.AddVisuals(arena.Scene);
                VisibleLevelBuilder.AddFollowCamera(arena.Scene, playerTransform);
                StartWatchDriver(arena, adapter, scenario, AgentFor(profileChoice, solve.ActionStream, adapter, scenario), seed);
                watchStatusLabel.text = "Watching live agent (demo level)...";
                return;
            }

            // SampleScene: LoadSceneArena is async, so load first, then solve-or-load-cache (progress bar on a
            // miss), then start the driver — all from the coroutine pump's completion callback.
            watchArenaManager = new ArenaManager();
            watchStatusLabel.text = "Loading SampleScene...";
            watchPump = EditorCoroutinePump.Run(
                watchArenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, arena =>
                {
                    CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
                    ScenarioConfig scenario = SampleSceneScenario.Create(arena, seed);
                    adapter.Bind(arena, scenario);

                    if (!CachedSolver.TrySolve(adapter, scenario, PlaytestLevel.SampleScene.ScenarioId(),
                            SampleSceneScenario.SolverConfig, out List<PlayerAction> basePlan, out string status))
                    {
                        watchStatusLabel.text = status;
                        watchArenaManager.UnloadAll();
                        watchArenaManager = null;
                        return;
                    }

                    // The real scene already has authored sprites; only the follow camera is needed.
                    Transform playerTransform = FindPlayerTransform(arena.Scene);
                    VisibleLevelBuilder.AddFollowCamera(arena.Scene, playerTransform);
                    StartWatchDriver(arena, adapter, scenario, AgentFor(profileChoice, basePlan, adapter, scenario), seed);
                    watchStatusLabel.text = status + " Watching live agent (SampleScene)...";
                }),
                onComplete: null,
                onError: ex =>
                {
                    watchStatusLabel.text = "SampleScene watch failed: " + ex.Message;
                    watchArenaManager?.UnloadAll();
                    watchArenaManager = null;
                });
        }

        void StartWatchDriver(Arena arena, IGameAdapter adapter, ScenarioConfig scenario, IAgent agent, int seed,
            int totalTicks = 0)
        {
            GameObject driverHost = new GameObject("LivePlaybackDriver");
            SceneManager.MoveGameObjectToScene(driverHost, arena.Scene);
            watchDriver = driverHost.AddComponent<LivePlaybackDriver>();
            watchDriver.Adapter = adapter;
            watchDriver.Agent = agent;
            watchDriver.Scenario = scenario;
            watchDriver.Seed = seed;
            SimulationDiagnosticsOverlay diagnostics = driverHost.AddComponent<SimulationDiagnosticsOverlay>();
            diagnostics.Bind(watchDriver, FindPlayerTransform(arena.Scene));
            watchTotalTicks = Mathf.Max(1, totalTicks > 0 ? totalTicks : scenario.stepBudget);
            watchDriver.Play();
        }

        void WatchRecordedEpisode()
        {
            if (!Application.isPlaying)
            {
                watchStatusLabel.text = "Enter Play Mode to watch.";
                return;
            }
            if (loadedRunDir == null || replayEpisodePicker == null || replayEpisodePicker.index < 0 ||
                replayEpisodePicker.index >= episodes.Count)
            {
                watchStatusLabel.text = "Load a run and pick an episode in the Replay tab first.";
                return;
            }

            EpisodeSummary ep = episodes[replayEpisodePicker.index];
            string actionsPath = Path.Combine(loadedRunDir, $"ep_{ep.episodeId}.actions.jsonl");
            if (!File.Exists(actionsPath))
            {
                watchStatusLabel.text = $"No action stream found: {actionsPath}";
                return;
            }

            // Level comes from the run's scenarioId when present; falls back to the Watch tab's Level dropdown
            // for older runs recorded before scenarioId distinguished real vs demo levels.
            string scenarioId = header != null && !string.IsNullOrEmpty(header.scenarioId)
                ? header.scenarioId
                : (ep.scenarioId ?? "");
            PlaytestLevel level = string.IsNullOrEmpty(scenarioId)
                ? LevelFromChoice(watchLevelDropdown?.value)
                : PlaytestLevelExtensions.FromScenarioId(scenarioId);

            StopWatch();

            List<PlayerAction> actionsByTick = ActionStreamIO.ParseIndexed(File.ReadAllLines(actionsPath));
            ReplayAgent agent = new ReplayAgent(actionsByTick);
            int recordedTotalTicks = Mathf.Max(ep.steps, actionsByTick.Count);

            if (level == PlaytestLevel.Demo)
            {
                watchArenaManager = new ArenaManager();
                Arena arena = watchArenaManager.CreateArena("watch-recorded", DemoLevel.Build);
                CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
                ScenarioConfig scenario = DemoLevel.MakeScenario();
                adapter.Bind(arena, scenario);

                Transform playerTransform = FindPlayerTransform(arena.Scene);
                VisibleLevelBuilder.AddVisuals(arena.Scene);
                VisibleLevelBuilder.AddFollowCamera(arena.Scene, playerTransform);
                StartWatchDriver(arena, adapter, scenario, agent, ep.seed, recordedTotalTicks);
                watchStatusLabel.text = $"Watching recorded episode {ep.episodeId} (demo level)...";
                return;
            }

            watchArenaManager = new ArenaManager();
            watchStatusLabel.text = "Loading SampleScene...";
            watchPump = EditorCoroutinePump.Run(
                watchArenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, arena =>
                {
                    CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
                    int layoutSeed = header != null ? header.layoutSeed : 0;
                    ScenarioConfig scenario = SampleSceneScenario.Create(arena, layoutSeed);
                    adapter.Bind(arena, scenario);

                    Transform playerTransform = FindPlayerTransform(arena.Scene);
                    VisibleLevelBuilder.AddFollowCamera(arena.Scene, playerTransform);
                    StartWatchDriver(arena, adapter, scenario, agent, ep.seed, recordedTotalTicks);
                    watchStatusLabel.text = $"Watching recorded episode {ep.episodeId} (SampleScene)...";
                }),
                onComplete: null,
                onError: ex =>
                {
                    watchStatusLabel.text = "SampleScene watch failed: " + ex.Message;
                    watchArenaManager?.UnloadAll();
                    watchArenaManager = null;
                });
        }

        // Reuses the death records the Replay/Results tabs already parsed from frames.jsonl (see CollectDeathsFromFrames)
        // to find the failure tick, then fast-forwards uncapped (StepOnce in a tight loop, no per-frame waiting)
        // to max(0, failureTick - 60) before resuming normal-speed playback.
        void JumpToFailure()
        {
            if (watchDriver == null)
            {
                watchStatusLabel.text = "Nothing is being watched.";
                return;
            }
            if (replayEpisodePicker == null || replayEpisodePicker.index < 0 || replayEpisodePicker.index >= episodes.Count)
            {
                watchStatusLabel.text = "Pick a recorded episode in the Replay tab first.";
                return;
            }

            EpisodeSummary ep = episodes[replayEpisodePicker.index];
            int failureTick = -1;
            for (int i = 0; i < deaths.Count; i++)
            {
                if (deaths[i].EpisodeId == ep.episodeId)
                {
                    failureTick = deaths[i].Tick; // first death event found for this episode
                    break;
                }
            }
            if (failureTick < 0)
            {
                watchStatusLabel.text = $"No recorded Death event for episode {ep.episodeId}.";
                return;
            }

            int targetTick = Mathf.Max(0, failureTick - 60);
            watchDriver.Restart();
            watchDriver.Pause();
            while (watchDriver.CurrentTick < targetTick && !watchDriver.IsComplete)
                watchDriver.StepOnce();
            watchDriver.Play();
            watchStatusLabel.text = $"Jumped to tick {targetTick} (failure at {failureTick}).";
        }

        static Transform FindPlayerTransform(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Transform found = roots[i].transform.Find("Player");
                if (found != null)
                    return found;
                if (roots[i].name == "Player")
                    return roots[i].transform;
            }
            return null;
        }

        void RefreshWatchStatus()
        {
            if (watchPlayModeWarning != null)
                watchPlayModeWarning.style.display = Application.isPlaying ? DisplayStyle.None : DisplayStyle.Flex;

            if (watchDriver == null || watchStatusLabel == null)
                return;

            string state = watchDriver.IsComplete ? "complete" : watchDriver.IsPaused ? "paused" : "playing";
            int totalTicks = Mathf.Max(1, watchTotalTicks);
            int displayTick = Mathf.Min(watchDriver.CurrentTick, totalTicks);
            float playbackPercent = watchDriver.IsComplete
                ? 100f
                : Mathf.Clamp01(displayTick / (float)totalTicks) * 100f;
            if (watchProgressBar != null)
            {
                watchProgressBar.value = playbackPercent;
                watchProgressBar.title = $"Playthrough {playbackPercent:F0}% ({displayTick}/{totalTicks} ticks)";
            }
            watchStatusLabel.text =
                $"tick {watchDriver.CurrentTick} | deaths {watchDriver.Deaths} | " +
                $"level progress {watchDriver.LastObservation.Progress:F2} | {state}";
        }

        /// <summary>Idempotent teardown: unloads the watch arena (destroying the driver/camera with it). Safe to
        /// call repeatedly (Stop button, window close, Play Mode exit) — a second call is a no-op.</summary>
        void StopWatch()
        {
            watchPump?.Cancel();
            watchPump = null;
            watchDriver = null;
            watchTotalTicks = 0;
            if (watchArenaManager != null)
            {
                watchArenaManager.UnloadAll();
                watchArenaManager = null;
            }
            if (watchStatusLabel != null)
                watchStatusLabel.text = "Stopped.";
            if (watchProgressBar != null)
            {
                watchProgressBar.value = 0f;
                watchProgressBar.title = "Playthrough 0%";
            }
        }

        void RefreshReplayEpisodeList()
        {
            if (replayEpisodePicker == null)
                return;
            List<string> choices = new List<string>();
            for (int i = 0; i < episodes.Count; i++)
                choices.Add(episodes[i].episodeId);
            replayEpisodePicker.choices = choices;
            replayEpisodePicker.index = choices.Count > 0 ? 0 : -1;
        }
    }
}
