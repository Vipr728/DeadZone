using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ryzi.Editor
{
    public sealed class RyziWindow : EditorWindow
    {
        const string PackageVersion = "0.1.0";

        readonly List<IPlaytestIntegrationProvider> providers = new List<IPlaytestIntegrationProvider>();
        SceneDiscoveryResult discovery;
        IPlaytestIntegrationProvider provider;
        CalibrationReport calibration;
        SimulationRunReport run;
        CounterfactualReport counterfactual;
        ReplayRecord replay;
        CancellationTokenSource cancellation;
        EditorOperationPump operation;
        Label setupStatus;
        Label discoveryStatus;
        VisualElement evidenceList;
        Label calibrationStatus;
        Label simulationStatus;
        Label resultsStatus;
        Label replayStatus;
        Label diagnosticsStatus;
        DropdownField profileField;
        SliderInt replaySlider;
        Button replayPlayButton;
        Button gameViewReplayButton;
        Button overlayButton;
        bool replayPlaying;
        double replayNextStepAt;

        [MenuItem("Tools/Ryzi")]
        public static void Open()
        {
            RyziWindow window = GetWindow<RyziWindow>();
            window.titleContent = new GUIContent("Ryzi");
            window.minSize = new Vector2(520f, 620f);
            window.Show();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexGrow = 1f;

            ScrollView scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1f;
            scrollView.style.paddingLeft = 12;
            scrollView.style.paddingRight = 12;
            scrollView.style.paddingTop = 10;
            scrollView.style.paddingBottom = 14;
            scrollView.contentContainer.style.flexGrow = 1f;
            scrollView.contentContainer.style.minWidth = 0f;
            rootVisualElement.Add(scrollView);

            VisualElement masthead = new VisualElement();
            masthead.style.borderTopWidth = 4;
            masthead.style.borderTopColor = new Color(0.95f, 0.42f, 0.12f);
            masthead.style.paddingTop = 10;
            masthead.style.width = Length.Percent(100);
            Label title = new Label("RYZI");
            title.style.fontSize = 26;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            masthead.Add(title);
            masthead.Add(new Label("Local, evidence-backed platformer simulation"));
            scrollView.Add(masthead);

            scrollView.Add(BuildSetup());
            scrollView.Add(BuildDiscovery());
            scrollView.Add(BuildCalibration());
            scrollView.Add(BuildSimulation());
            scrollView.Add(BuildResults());
            scrollView.Add(BuildReplay());
            scrollView.Add(BuildDiagnostics());
            RefreshDiagnostics();
        }

        void OnDisable()
        {
            StopOperation();
            StopReplay();
            (provider as IGameViewReplayProvider)?.StopGameViewReplay();
            TrajectoryOverlay.Hide();
        }

        VisualElement BuildSetup()
        {
            Foldout section = Section("1. Setup", true);
            setupStatus = new Label(
                "Local Basic entitlement. No account, network connection, project setting, or scene object is required.");
            setupStatus.style.whiteSpace = WhiteSpace.Normal;
            section.Add(setupStatus);
            return section;
        }

        VisualElement BuildDiscovery()
        {
            Foldout section = Section("2. Discovery", true);
            Button scan = new Button(Scan) { text = "Scan Current Scene" };
            scan.style.height = 32;
            scan.style.width = Length.Percent(100);
            section.Add(scan);
            discoveryStatus = Wrapped("No scan has run.");
            section.Add(discoveryStatus);
            evidenceList = new VisualElement();
            evidenceList.style.width = Length.Percent(100);
            evidenceList.style.minWidth = 0f;
            section.Add(evidenceList);
            return section;
        }

        VisualElement BuildCalibration()
        {
            Foldout section = Section("3. Calibration", true);
            VisualElement row = Row();
            row.Add(RowButton(RunCalibration, "Run Calibration"));
            row.Add(RowButton(CancelOperation, "Cancel"));
            section.Add(row);
            calibrationStatus = Wrapped("Scan first. This provider calibrates in an isolated scene during Play Mode.");
            section.Add(calibrationStatus);
            return section;
        }

        VisualElement BuildSimulation()
        {
            Foldout section = Section("4. Simulation", true);
            profileField = new DropdownField(
                "Synthetic search profile",
                new List<string> { "Constrained", "Standard", "Precision" },
                1);
            section.Add(profileField);
            VisualElement row = Row();
            row.Add(RowButton(RunTest, "Run Test"));
            row.Add(RowButton(RunCounterfactual, "Run 3-Variant Counterfactual"));
            row.Add(RowButton(CancelOperation, "Cancel"));
            section.Add(row);
            simulationStatus = Wrapped(
                "Profiles configure solver budget only; they are not calibrated human-player models.");
            section.Add(simulationStatus);
            return section;
        }

        VisualElement BuildResults()
        {
            Foldout section = Section("5. Results", true);
            resultsStatus = Wrapped("No measured run is loaded.");
            section.Add(resultsStatus);
            overlayButton = new Button(ToggleOverlay) { text = "Show Scene Path / Failure Overlay" };
            overlayButton.style.width = Length.Percent(100);
            section.Add(overlayButton);
            return section;
        }

        VisualElement BuildReplay()
        {
            Foldout section = Section("6. Replay", true);
            VisualElement row = Row();
            replayPlayButton = RowButton(ToggleReplay, "Play");
            row.Add(replayPlayButton);
            row.Add(RowButton(PauseReplay, "Pause"));
            row.Add(RowButton(RestartReplay, "Restart"));
            row.Add(RowButton(StepReplay, "Step"));
            row.Add(RowButton(JumpToFailure, "Jump to Failure"));
            section.Add(row);
            gameViewReplayButton = new Button(ToggleGameViewReplay) { text = "Open Game View Replay" };
            gameViewReplayButton.style.width = Length.Percent(100);
            section.Add(gameViewReplayButton);
            replaySlider = new SliderInt("Recorded keyframe", 0, 0);
            replaySlider.style.width = Length.Percent(100);
            replaySlider.RegisterValueChangedCallback(evt => ShowReplayFrame(evt.newValue));
            section.Add(replaySlider);
            replayStatus = Wrapped(
                "Run Test records a verified complete trace or a best-effort diagnostic trace when search stops early.");
            section.Add(replayStatus);
            return section;
        }

        VisualElement BuildDiagnostics()
        {
            Foldout section = Section("7. Diagnostics", false);
            diagnosticsStatus = Wrapped(string.Empty);
            section.Add(diagnosticsStatus);
            section.Add(new Button(RefreshDiagnostics) { text = "Refresh Diagnostics" });
            return section;
        }

        void Scan()
        {
            try
            {
                discovery = new ProjectScanner().ScanCurrentScene();
                DiscoverProvider();
                RenderDiscovery();
                RefreshDiagnostics();
            }
            catch (Exception ex)
            {
                discoveryStatus.text = "Scan failed: " + ex.Message;
            }
        }

        void DiscoverProvider()
        {
            providers.Clear();
            provider = null;
            foreach (Type type in TypeCache.GetTypesDerivedFrom<IPlaytestIntegrationProvider>())
            {
                if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null)
                    continue;
                try
                {
                    IPlaytestIntegrationProvider candidate =
                        (IPlaytestIntegrationProvider)Activator.CreateInstance(type);
                    providers.Add(candidate);
                    if (provider == null && candidate.CanHandle(discovery, out _))
                        provider = candidate;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Ryzi could not initialize provider {type.FullName}: {ex.Message}");
                }
            }
            discovery.SelectedProviderId = provider?.Id;
        }

        void RenderDiscovery()
        {
            evidenceList.Clear();
            DiscoveryCandidate<GameObject> player = discovery.SelectedPlayer;
            if (player == null)
            {
                discoveryStatus.text =
                    $"Scanned {discovery.SceneName} in {discovery.DurationMilliseconds} ms. No probable player found.";
                return;
            }

            discoveryStatus.text =
                $"Scene: {discovery.SceneName}\n" +
                $"Selected player: {ProjectScanner.HierarchyPath(player.Value)} ({player.Confidence:P0})\n" +
                $"Provider: {(provider == null ? "none" : provider.DisplayName)}\n" +
                $"Actions: {discovery.Manifest.actions.Length}; issues: {discovery.Manifest.issues.Length}; " +
                $"scan: {discovery.DurationMilliseconds} ms";

            Label playerHeader = Strong("Player evidence");
            evidenceList.Add(playerHeader);
            for (int i = 0; i < player.Evidence.Count; i++)
            {
                DiscoveryEvidence evidence = player.Evidence[i];
                evidenceList.Add(Wrapped(
                    $"{evidence.level} +{evidence.weight:P0}: {evidence.summary} [{evidence.source}]"));
            }

            evidenceList.Add(Strong("Discovered actions"));
            for (int i = 0; i < discovery.Manifest.actions.Length; i++)
            {
                ActionChannelDefinition action = discovery.Manifest.actions[i];
                evidenceList.Add(Wrapped(
                    $"{action.id} -> {action.suggestedName} ({action.confidence:P0}, {action.evidenceLevel}; " +
                    $"edge P/H/R={action.supportsPressed}/{action.supportsHeld}/{action.supportsReleased})"));
            }

            if (discovery.Manifest.issues.Length > 0)
            {
                evidenceList.Add(Strong("Unresolved issues"));
                for (int i = 0; i < discovery.Manifest.issues.Length; i++)
                {
                    DiscoveryIssue issue = discovery.Manifest.issues[i];
                    evidenceList.Add(Wrapped($"{issue.severity}: {issue.summary} {issue.resolution}"));
                }
            }
        }

        void RunCalibration()
        {
            if (!ValidateOperation("calibration"))
                return;
            StartOperation(
                provider.Calibrate(discovery, cancellation.Token, result =>
                {
                    calibration = result;
                    calibrationStatus.text = FormatCalibration(result);
                    RenderDiscovery();
                }),
                "Calibration running...",
                value => calibrationStatus.text = value);
        }

        void RunTest()
        {
            if (!ValidateOperation("simulation"))
                return;
            PlayerProfile profile = SelectedProfile();
            StartOperation(
                provider.RunTest(discovery, discovery.Manifest, profile, cancellation.Token, result =>
                {
                    run = result;
                    simulationStatus.text = FormatRun(result);
                    resultsStatus.text = FormatRun(result);
                    LoadReplay(result.replayPath);
                }),
                "Solver running. Cancellation is checked between segments...",
                value => simulationStatus.text = value);
        }

        void RunCounterfactual()
        {
            if (!ValidateOperation("counterfactual"))
                return;
            if (run == null)
            {
                simulationStatus.text = "Run a baseline test before the counterfactual.";
                return;
            }
            StartOperation(
                provider.RunCounterfactual(
                    discovery, discovery.Manifest, run, cancellation.Token, result =>
                    {
                        counterfactual = result;
                        simulationStatus.text = FormatCounterfactual(result);
                        resultsStatus.text = FormatRun(run) + "\n\n" + FormatCounterfactual(result);
                    }),
                "Three matched-seed variants running...",
                value => simulationStatus.text = value);
        }

        bool ValidateOperation(string name)
        {
            if (operation != null)
            {
                simulationStatus.text = "Another Ryzi operation is already running.";
                return false;
            }
            if (discovery == null)
            {
                discoveryStatus.text = $"Scan Current Scene before {name}.";
                return false;
            }
            if (provider == null)
            {
                discoveryStatus.text = "No compatible integration provider was found. Use an adapter.";
                return false;
            }
            if (provider.RequiresPlayMode && !Application.isPlaying)
            {
                string message =
                    $"{provider.DisplayName} requires Play Mode for isolated PhysicsScene2D simulation. " +
                    "Enter Play Mode, scan again, then retry.";
                calibrationStatus.text = message;
                simulationStatus.text = message;
                return false;
            }
            cancellation = new CancellationTokenSource();
            return true;
        }

        void StartOperation(
            System.Collections.IEnumerator routine,
            string runningMessage,
            Action<string> status)
        {
            status(runningMessage);
            operation = EditorOperationPump.Start(
                routine,
                () =>
                {
                    operation = null;
                    cancellation?.Dispose();
                    cancellation = null;
                    Repaint();
                },
                ex =>
                {
                    operation = null;
                    cancellation?.Dispose();
                    cancellation = null;
                    status("Operation failed: " + ex.Message);
                    Debug.LogException(ex);
                });
        }

        void CancelOperation()
        {
            if (cancellation == null)
                return;
            cancellation.Cancel();
            simulationStatus.text = "Cancellation requested; waiting for the current safe boundary.";
            calibrationStatus.text = simulationStatus.text;
        }

        void StopOperation()
        {
            cancellation?.Cancel();
            operation?.Cancel();
            operation = null;
            cancellation?.Dispose();
            cancellation = null;
        }

        void LoadReplay(string path)
        {
            replay = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                replayStatus.text = "The run did not produce a replay file.";
                return;
            }
            replay = JsonUtility.FromJson<ReplayRecord>(File.ReadAllText(path));
            if (replay == null || replay.keyframes == null || replay.keyframes.Length == 0)
            {
                replay = null;
                replayStatus.text =
                    "This run contains no recorded trajectory. Re-run the test after updating the replay exporter.";
                return;
            }
            int max = Mathf.Max(0, replay.keyframes.Length - 1);
            replaySlider.highValue = max;
            replaySlider.SetValueWithoutNotify(0);
            ShowReplayFrame(0);
            ShowReplayOverlay();
        }

        void ToggleReplay()
        {
            if (replay == null || replay.keyframes.Length == 0)
                return;
            ShowReplayOverlay();
            replayPlaying = !replayPlaying;
            replayPlayButton.text = replayPlaying ? "Playing" : "Play";
            IGameViewReplayProvider gameView = provider as IGameViewReplayProvider;
            if (gameView != null && gameView.IsGameViewReplayActive)
            {
                if (replayPlaying)
                    gameView.PlayGameViewReplay();
                else
                    gameView.PauseGameViewReplay();
            }
            if (replayPlaying)
            {
                replayNextStepAt = EditorApplication.timeSinceStartup;
                EditorApplication.update -= ReplayTick;
                EditorApplication.update += ReplayTick;
            }
            else
                EditorApplication.update -= ReplayTick;
        }

        void PauseReplay()
        {
            replayPlaying = false;
            replayPlayButton.text = "Play";
            EditorApplication.update -= ReplayTick;
            (provider as IGameViewReplayProvider)?.PauseGameViewReplay();
        }

        void RestartReplay()
        {
            if (replay == null || replay.keyframes == null || replay.keyframes.Length == 0)
            {
                replayStatus.text = "Load a recorded replay before restarting it.";
                return;
            }

            PauseReplay();
            replaySlider.SetValueWithoutNotify(0);
            ShowReplayFrame(0);
            ShowReplayOverlay();
            (provider as IGameViewReplayProvider)?.RestartGameViewReplay();
            replayStatus.text = "Replay restarted at tick 0.";
        }

        void StopReplay() => PauseReplay();

        void ReplayTick()
        {
            if (!replayPlaying || EditorApplication.timeSinceStartup < replayNextStepAt)
                return;
            replayNextStepAt = EditorApplication.timeSinceStartup + 0.05;

            IGameViewReplayProvider gameView = provider as IGameViewReplayProvider;
            if (gameView != null && gameView.IsGameViewReplayActive)
            {
                SyncRecordedPreviewToGameView(gameView.GameViewReplayTick);
                if (gameView.IsGameViewReplayComplete)
                    PauseReplay();
                return;
            }

            if (replaySlider.value >= replaySlider.highValue)
            {
                PauseReplay();
                return;
            }
            replaySlider.value++;
        }

        void StepReplay()
        {
            PauseReplay();
            if (replay != null)
            {
                ShowReplayOverlay();
                IGameViewReplayProvider gameView = provider as IGameViewReplayProvider;
                if (gameView != null && gameView.IsGameViewReplayActive)
                {
                    gameView.StepGameViewReplay();
                    SyncRecordedPreviewToGameView(gameView.GameViewReplayTick);
                }
                else
                    replaySlider.value = Mathf.Min(replaySlider.value + 1, replaySlider.highValue);
            }
        }

        void JumpToFailure()
        {
            if (replay == null || replay.failureTick < 0)
            {
                replayStatus.text = "This replay has no recorded failure tick.";
                return;
            }
            int index = 0;
            for (int i = 0; i < replay.keyframes.Length; i++)
            {
                if (replay.keyframes[i].tick > replay.failureTick)
                    break;
                index = i;
            }
            ShowReplayOverlay();
            replaySlider.value = index;
            ShowReplayFrame(index);
            (provider as IGameViewReplayProvider)?.JumpGameViewReplayToTick(replay.failureTick);
        }

        void ShowReplayFrame(int index)
        {
            SetReplayPreviewFrame(index, true);
        }

        void SetReplayPreviewFrame(int index, bool focusScene)
        {
            if (replay == null || replay.keyframes.Length == 0)
                return;
            index = Mathf.Clamp(index, 0, replay.keyframes.Length - 1);
            ReplayKeyframe frame = replay.keyframes[index];
            replayStatus.text =
                $"Recorded trajectory preview: tick {frame.tick}, position {frame.position}, " +
                $"progress {frame.progress:P1}, state {frame.stateHash}\n" +
                $"Verification: {replay.verificationMessage}";
            TrajectoryOverlay.SetCurrentFrame(index);
            if (focusScene)
                SceneView.lastActiveSceneView?.LookAt(frame.position, Quaternion.identity, 6f);
        }

        void SyncRecordedPreviewToGameView(int tick)
        {
            if (replay == null || replay.keyframes == null || replay.keyframes.Length == 0)
                return;
            int index = 0;
            for (int i = 0; i < replay.keyframes.Length; i++)
            {
                if (replay.keyframes[i].tick > tick)
                    break;
                index = i;
            }
            replaySlider.SetValueWithoutNotify(index);
            SetReplayPreviewFrame(index, false);
        }

        void ToggleOverlay()
        {
            if (TrajectoryOverlay.Enabled)
            {
                TrajectoryOverlay.Hide();
                UpdateOverlayButton();
                return;
            }
            if (replay == null)
            {
                resultsStatus.text = "Run or load a replay before enabling the overlay.";
                return;
            }
            ShowReplayOverlay();
        }

        void ShowReplayOverlay()
        {
            if (replay == null || replay.keyframes == null || replay.keyframes.Length == 0)
                return;
            Vector2[] path = new Vector2[replay.keyframes.Length];
            for (int i = 0; i < path.Length; i++)
                path[i] = replay.keyframes[i].position;
            Vector2[] failures = run?.failurePositions;
            if ((failures == null || failures.Length == 0) && replay.failureTick >= 0)
            {
                int failureIndex = 0;
                for (int i = 0; i < replay.keyframes.Length; i++)
                {
                    if (replay.keyframes[i].tick > replay.failureTick)
                        break;
                    failureIndex = i;
                }
                failures = new[] { replay.keyframes[failureIndex].position };
            }
            TrajectoryOverlay.Show(path, failures, replaySlider.value, replay.terminalStatus);
            UpdateOverlayButton();
        }

        void ToggleGameViewReplay()
        {
            if (replay == null || replay.actions == null || replay.actions.Length == 0)
            {
                replayStatus.text = "Run a replay with recorded actions before opening Game View playback.";
                return;
            }
            if (!(provider is IGameViewReplayProvider gameView))
            {
                replayStatus.text = "The selected simulator integration does not support Game View replay.";
                return;
            }
            if (gameView.IsGameViewReplayActive)
            {
                gameView.StopGameViewReplay();
                UpdateGameViewReplayButton();
                replayStatus.text = "Game View replay closed.";
                return;
            }
            if (!ValidateOperation("Game View replay"))
                return;

            StartOperation(
                gameView.StartGameViewReplay(discovery, replay, cancellation.Token, status =>
                {
                    replayStatus.text = status;
                    UpdateGameViewReplayButton();
                }),
                "Loading visible replay scene...",
                value => replayStatus.text = value);
        }

        void UpdateGameViewReplayButton()
        {
            if (gameViewReplayButton != null)
            {
                bool active = (provider as IGameViewReplayProvider)?.IsGameViewReplayActive == true;
                gameViewReplayButton.text = active ? "Close Game View Replay" : "Open Game View Replay";
            }
        }

        void UpdateOverlayButton()
        {
            if (overlayButton != null)
                overlayButton.text = TrajectoryOverlay.Enabled
                    ? "Hide Scene Path / Failure Overlay"
                    : "Show Scene Path / Failure Overlay";
        }

        void RefreshDiagnostics()
        {
            if (diagnosticsStatus == null)
                return;
            string recovery = LocalDataPathService.Guard(
                Path.Combine(LocalDataPathService.RecoveryRoot, "counterfactual-active.json"));
            diagnosticsStatus.text =
                $"Ryzi {PackageVersion}\n" +
                $"Unity {Application.unityVersion}\n" +
                $"Local data: {LocalDataPathService.Root}\n" +
                $"Active scene dirty: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty}\n" +
                $"Compatible provider: {(provider == null ? "not selected" : provider.DisplayName)}\n" +
                $"Input System dependency in base package: none\n" +
                $"Network/upload: disabled\n" +
                $"Pending restoration marker: {(File.Exists(recovery) ? recovery : "none")}";
        }

        PlayerProfile SelectedProfile()
        {
            switch (profileField.value)
            {
                case "Constrained": return PlayerProfile.Constrained;
                case "Precision": return PlayerProfile.Precision;
                default: return PlayerProfile.Standard;
            }
        }

        static Foldout Section(string title, bool open)
        {
            Foldout foldout = new Foldout { text = title, value = open };
            foldout.style.width = Length.Percent(100);
            foldout.style.minWidth = 0f;
            foldout.style.marginTop = 8;
            foldout.style.paddingBottom = 5;
            return foldout;
        }

        static VisualElement Row()
        {
            VisualElement row = new VisualElement();
            row.style.width = Length.Percent(100);
            row.style.minWidth = 0f;
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            return row;
        }

        static Button RowButton(Action action, string text)
        {
            Button button = new Button(action) { text = text };
            button.style.marginRight = 6;
            button.style.marginBottom = 6;
            button.style.flexShrink = 0f;
            return button;
        }

        static Label Wrapped(string text)
        {
            Label label = new Label(text);
            label.style.width = Length.Percent(100);
            label.style.minWidth = 0f;
            label.style.flexShrink = 1f;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = 3;
            label.style.marginBottom = 3;
            return label;
        }

        static Label Strong(string text)
        {
            Label label = Wrapped(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 7;
            return label;
        }

        static string FormatCalibration(CalibrationReport value)
        {
            if (value == null)
                return "Calibration did not return a report.";
            if (value.cancelled)
                return $"Calibration cancelled after {value.probes.Length} probe(s); restored={value.stateRestored}.";
            return
                $"Calibration complete: {value.probes.Length} probes in {value.elapsedMilliseconds} ms; " +
                $"repeatable={value.deterministicRepeatability}; restored={value.stateRestored}.\n" +
                string.Join("\n", value.warnings ?? Array.Empty<string>());
        }

        static string FormatRun(SimulationRunReport value)
        {
            if (value == null)
                return "The provider returned no run report.";
            return
                $"Run {value.runId}: {(value.solverSucceeded ? "completed" : "failed")}\n" +
                $"Completion {value.completedRuns}/{value.runCount} ({value.completionRate:P0}); " +
                $"completion tick {value.completionTick}; failures {value.failureCount}; " +
                $"furthest progress {value.furthestProgress:P1}\n" +
                $"Solver: {value.solverExpansions} expansions, {value.simulationTicks} ticks, " +
                $"{value.solverMilliseconds} ms. Telemetry: {value.telemetryMilliseconds} ms.\n" +
                $"Solver cache: {(value.solverCacheHit ? "hit" : "miss")}; " +
                $"{value.solverCacheStatus ?? "not available"}\n" +
                $"Diagnostic: {value.solverDiagnostic ?? "verified"}\n" +
                $"Local evidence: {value.runDirectory}";
        }

        static string FormatCounterfactual(CounterfactualReport value)
        {
            if (value == null)
                return "The provider returned no counterfactual report.";
            string text =
                $"Counterfactual {value.displayName}; original {value.originalValue:F3}; " +
                $"restored={value.originalRestored}; cancelled={value.cancelled}; {value.elapsedMilliseconds} ms";
            for (int i = 0; i < value.variants.Length; i++)
            {
                CounterfactualVariantResult variant = value.variants[i];
                if (variant == null)
                    continue;
                text +=
                    $"\n{variant.candidateValue:F3}: completed={variant.completed}, " +
                    $"tick={variant.completionTick}, deaths={variant.deaths}, progress={variant.furthestProgress:P1}";
            }
            return text;
        }
    }
}
