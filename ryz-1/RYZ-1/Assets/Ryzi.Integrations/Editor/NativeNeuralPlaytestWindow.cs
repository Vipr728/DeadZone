using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using CelesteBenchmark;
using PlatformerPlaytest;
using PlatformerPlaytest.Live;
using Ryzi.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Ryzi.Integrations.ExistingSimulator
{
    /// <summary>
    /// User-facing bridge for a saved Unity scene on Rahul's Mac. Geometry is
    /// exported from an isolated copy, solved by RYZ-1 on the GB10 over
    /// Tailscale SSH, verified in a fresh Unity simulation, then shown in Game
    /// View. Neither the ONNX score nor SimCore completion is exposed as a pass
    /// until Unity replay also reaches the authored goal.
    /// </summary>
    public sealed class NativeNeuralPlaytestWindow : EditorWindow
    {
        const string HostPref = "RYZ1.NeuralPlaytest.Gb10Host";
        const string RepoPref = "RYZ1.NeuralPlaytest.Gb10Repo";
        const string DotnetPref = "RYZ1.NeuralPlaytest.Gb10Dotnet";
        const string ModelPref = "RYZ1.NeuralPlaytest.Model";
        const string BeamPref = "RYZ1.NeuralPlaytest.Beam";
        const string DepthPref = "RYZ1.NeuralPlaytest.Depth";
        const string SpeedPref = "RYZ1.NeuralPlaytest.PlaybackSpeed";

        const string DefaultHost = "dell@100.122.207.66";
        const string DefaultRepo = "/home/dell/Ryzi-labs/RYZ-1";
        const string DefaultDotnet = "/home/dell/.dotnet/dotnet";
        const string DefaultModel =
            "Library/RYZ1/models/curriculum-sequence-v3/ryz1-sequence.onnx";

        string gb10Host;
        string gb10Repo;
        string gb10Dotnet;
        string modelPath;
        int beamWidth;
        int maxDepth;
        float playbackSpeed;
        bool showConnection;
        bool busy;
        string stage = "Ready.";
        string details =
            "Open a saved compatible scene, enter Play Mode, then run the trained model.";
        string lastRunDirectory;
        string sourceScenePath;
        string runId;

        EditorOperationPump operation;
        ArenaManager workingArenaManager;
        ArenaManager replayArenaManager;
        Process remoteProcess;
        LivePlaybackDriver replayDriver;
        CelesteBenchmarkAdapter replayAdapter;
        GameObject replayHost;
        BridgeCompatibilityReport compatibility;
        BridgeReplayVerification verification;

        [MenuItem("Tools/RYZ-1 Neural Playtest")]
        public static void Open()
        {
            NativeNeuralPlaytestWindow window = GetWindow<NativeNeuralPlaytestWindow>();
            window.titleContent = new GUIContent("RYZ-1 Neural Playtest");
            window.minSize = new Vector2(540f, 560f);
            window.Show();
        }

        void OnEnable()
        {
            gb10Host = EditorPrefs.GetString(HostPref, DefaultHost);
            gb10Repo = EditorPrefs.GetString(RepoPref, DefaultRepo);
            gb10Dotnet = EditorPrefs.GetString(DotnetPref, DefaultDotnet);
            modelPath = EditorPrefs.GetString(ModelPref, DefaultModel);
            beamWidth = EditorPrefs.GetInt(BeamPref, 20);
            maxDepth = EditorPrefs.GetInt(DepthPref, 50);
            playbackSpeed = EditorPrefs.GetFloat(SpeedPref, 1f);
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            CancelOperation();
            StopReplay();
        }

        void OnGUI()
        {
            Scene active = SceneManager.GetActiveScene();
            string activePath = active.IsValid() ? active.path : string.Empty;
            bool saved = !string.IsNullOrWhiteSpace(activePath);
            bool inBuild = saved && IsSceneEnabledInBuildSettings(activePath);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("RYZ-1 Neural Playtest", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Runs the trained RYZ-1 ONNX guide and native search on the GB10, then authoritatively " +
                "replays the returned actions in Unity. The Mac does not need CUDA or Python.",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Current scene", saved ? activePath : "Unsaved scene");
            EditorGUILayout.LabelField("Mode", Application.isPlaying ? "Play Mode" : "Edit Mode");

            if (!saved)
            {
                EditorGUILayout.HelpBox("Save the scene before running RYZ-1.", MessageType.Warning);
                if (!Application.isPlaying && GUILayout.Button("Save Scene"))
                    EditorSceneManager.SaveOpenScenes();
            }
            else if (active.isDirty)
            {
                EditorGUILayout.HelpBox(
                    "The active scene has unsaved changes. RYZ-1 loads the saved scene into an isolated " +
                    "physics arena, so save before entering Play Mode.",
                    MessageType.Warning);
                if (!Application.isPlaying && GUILayout.Button("Save Open Scenes"))
                    EditorSceneManager.SaveOpenScenes();
            }

            if (saved && !inBuild)
            {
                EditorGUILayout.HelpBox(
                    "The scene must be enabled in Build Settings so Unity can load its isolated physics copy.",
                    MessageType.Warning);
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    if (GUILayout.Button("Add Current Scene To Build Settings"))
                        AddSceneToBuildSettings(activePath);
                }
            }

            if (!Application.isPlaying)
            {
                using (new EditorGUI.DisabledScope(!saved || active.isDirty || !inBuild))
                {
                    if (GUILayout.Button("Enter Play Mode"))
                        EditorApplication.isPlaying = true;
                }
            }

            EditorGUILayout.Space(8f);
            showConnection = EditorGUILayout.Foldout(
                showConnection,
                "GB10 connection and solver settings",
                true);
            if (showConnection)
            {
                EditorGUI.indentLevel++;
                gb10Host = EditorGUILayout.TextField("SSH host", gb10Host);
                gb10Repo = EditorGUILayout.TextField("Remote RYZ-1 repo", gb10Repo);
                gb10Dotnet = EditorGUILayout.TextField("Remote dotnet", gb10Dotnet);
                modelPath = EditorGUILayout.TextField("Remote model", modelPath);
                beamWidth = Mathf.Max(1, EditorGUILayout.IntField("Beam width", beamWidth));
                maxDepth = Mathf.Max(1, EditorGUILayout.IntField("Maximum depth", maxDepth));
                playbackSpeed = Mathf.Clamp(
                    EditorGUILayout.Slider("Replay speed", playbackSpeed, 0.25f, 8f),
                    0.25f,
                    8f);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(10f);
            using (new EditorGUI.DisabledScope(
                       busy ||
                       !Application.isPlaying ||
                       !saved ||
                       active.isDirty ||
                       !inBuild))
            {
                if (GUILayout.Button("Run Trained Model On Current Scene", GUILayout.Height(42f)))
                    StartRun(activePath);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!busy))
                {
                    if (GUILayout.Button("Cancel Run"))
                        CancelOperation();
                }
                using (new EditorGUI.DisabledScope(replayArenaManager == null))
                {
                    if (GUILayout.Button("Stop Replay"))
                        StopReplay();
                }
            }

            EditorGUILayout.Space(10f);
            MessageType statusType = stage.StartsWith("PASS", StringComparison.Ordinal)
                ? MessageType.Info
                : stage.StartsWith("FAILED", StringComparison.Ordinal) ||
                  stage.StartsWith("UNSUPPORTED", StringComparison.Ordinal)
                    ? MessageType.Error
                    : MessageType.None;
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(stage + "\n\n" + details, statusType);

            if (compatibility != null)
            {
                EditorGUILayout.LabelField("Scene compatibility", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    compatibility.Summary(),
                    compatibility.supported ? MessageType.Info : MessageType.Error);
            }

            if (verification != null)
            {
                EditorGUILayout.LabelField("Unity verification", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    $"{verification.diagnostic}\n" +
                    $"Executed {verification.executedTicks}/{verification.actionCount} actions; " +
                    $"furthest progress {verification.furthestProgress:P1}; " +
                    $"final ({verification.finalPosition.x:0.00}, {verification.finalPosition.y:0.00}).",
                    verification.completed && !verification.died
                        ? MessageType.Info
                        : MessageType.Error);
            }

            if (!string.IsNullOrWhiteSpace(lastRunDirectory) &&
                Directory.Exists(lastRunDirectory) &&
                GUILayout.Button("Reveal Run Artifacts"))
            {
                EditorUtility.RevealInFinder(lastRunDirectory);
            }
        }

        void StartRun(string scenePath)
        {
            if (busy)
                return;

            StopReplay();
            SavePreferences();
            compatibility = null;
            verification = null;
            sourceScenePath = scenePath;
            runId = "unity-gui-" +
                    Sanitize(Path.GetFileNameWithoutExtension(scenePath)) + "-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            lastRunDirectory = Path.Combine(
                projectRoot,
                "Library",
                "RYZ1",
                "gui-runs",
                runId);
            Directory.CreateDirectory(lastRunDirectory);

            busy = true;
            SetStatus("Preparing Unity scene...", "Loading an isolated copy of " + scenePath);
            operation = EditorOperationPump.Start(
                RunCurrentScene(),
                () =>
                {
                    operation = null;
                    busy = false;
                    Repaint();
                },
                ex =>
                {
                    operation = null;
                    busy = false;
                    CleanupWorkingArena();
                    DisposeRemoteProcess();
                    if (compatibility != null && !compatibility.supported)
                        SetStatus("UNSUPPORTED SCENE", compatibility.Summary());
                    else
                        SetStatus("FAILED", ex.Message);
                    Debug.LogException(ex);
                });
        }

        IEnumerator RunCurrentScene()
        {
            workingArenaManager = new ArenaManager();
            Arena exportArena = null;
            yield return workingArenaManager.LoadSceneArena(
                sourceScenePath,
                value => exportArena = value);
            if (exportArena == null)
                throw new InvalidOperationException("Unity did not load the isolated export arena.");

            ScenarioConfig exportScenario = CreateScenario(exportArena);
            compatibility = NativeSimCoreBridge.InspectCompatibility(exportArena, exportScenario);
            WriteJson("compatibility.json", compatibility);
            Repaint();
            if (!compatibility.supported)
                throw new InvalidOperationException(compatibility.Summary());

            string snapshotPath = Path.Combine(lastRunDirectory, "unity-snapshot.json");
            NativeSimCoreBridge.ExportSnapshot(
                exportArena,
                exportScenario,
                snapshotPath,
                runId);
            CleanupWorkingArena();

            SetStatus(
                "Running RYZ-1 on GB10...",
                "Uploading the current scene, running ONNX-guided native search, and downloading its replay.");
            yield return RunRemoteSolve(snapshotPath);

            string taskBundlePath = Path.Combine(lastRunDirectory, "task_bundle.json");
            string replayPath = Path.Combine(lastRunDirectory, "replay.json");
            var actions = NativeSimCoreBridge.LoadNativeReplay(
                taskBundlePath,
                replayPath,
                out NativeReplay nativeReplay);

            SetStatus(
                "Verifying in Unity...",
                $"SimCore returned {nativeReplay.macroIds.Length} macros. Replaying them through Unity physics.");
            workingArenaManager = new ArenaManager();
            Arena replayArena = null;
            yield return workingArenaManager.LoadSceneArena(
                sourceScenePath,
                value => replayArena = value);
            if (replayArena == null)
                throw new InvalidOperationException("Unity did not load the replay arena.");

            ScenarioConfig replayScenario = CreateScenario(replayArena);
            BridgeCompatibilityReport replayCompatibility =
                NativeSimCoreBridge.InspectCompatibility(replayArena, replayScenario);
            if (!replayCompatibility.supported)
                throw new InvalidOperationException(
                    "The replay copy no longer matches the exported supported scene:\n" +
                    replayCompatibility.Summary());

            verification = NativeSimCoreBridge.VerifyInUnity(
                replayArena,
                replayScenario,
                actions);
            WriteJson("unity-verification.json", verification);
            if (!verification.completed || verification.died)
                throw new InvalidOperationException(
                    "SimCore returned a candidate, but authoritative Unity replay rejected it: " +
                    verification.diagnostic);

            StartVisibleReplay(replayArena, replayScenario, actions);
            replayArenaManager = workingArenaManager;
            workingArenaManager = null;
            SetStatus(
                "PASS — Unity verified",
                $"The trained model participated in search and Unity reached the goal in " +
                $"{verification.executedTicks} ticks. The verified replay is now playing in Game View.");
        }

        ScenarioConfig CreateScenario(Arena arena)
        {
            string scenarioId = string.IsNullOrWhiteSpace(runId) ? "ryz1-unity-gui" : runId;
            return new CelesteBenchmarkScenarioProvider(scenarioId).CreateScenario(arena, 0);
        }

        IEnumerator RunRemoteSolve(string snapshotPath)
        {
            string scriptPath = Path.Combine(lastRunDirectory, "run-gb10.sh");
            string logPath = Path.Combine(lastRunDirectory, "transport.log");
            File.WriteAllText(scriptPath, BuildRemoteScript(snapshotPath, logPath));

            remoteProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = QuoteProcessArgument(scriptPath),
                    WorkingDirectory = lastRunDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = false
            };
            if (!remoteProcess.Start())
                throw new InvalidOperationException("Could not start the GB10 transport process.");

            while (!remoteProcess.HasExited)
                yield return null;

            int exitCode = remoteProcess.ExitCode;
            DisposeRemoteProcess();
            if (exitCode != 0)
            {
                string log = File.Exists(logPath) ? File.ReadAllText(logPath) : "No transport log was written.";
                throw new InvalidOperationException(
                    $"GB10 solve failed with exit code {exitCode}.\n{Tail(log, 3000)}");
            }

            string[] required = { "task_bundle.json", "replay.json", "result.json", "report.md" };
            for (int i = 0; i < required.Length; i++)
            {
                string path = Path.Combine(lastRunDirectory, required[i]);
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        "GB10 solve completed without a required artifact.",
                        path);
            }
        }

        string BuildRemoteScript(string snapshotPath, string logPath)
        {
            string remoteRoot = "/tmp/" + runId;
            string remoteSnapshot = remoteRoot + "/unity-snapshot.json";
            string remoteCommand =
                "set -euo pipefail; " +
                "cd " + ShellQuote(gb10Repo) + "; " +
                "test -x " + ShellQuote(gb10Dotnet) + "; " +
                "test -f " + ShellQuote(modelPath) + "; " +
                ShellQuote(gb10Dotnet) +
                " run --no-restore --project src/Ryz1.Runner/Ryz1.Runner.csproj -c Release -- " +
                "solve-unity-snapshot " +
                "--snapshot " + ShellQuote(remoteSnapshot) + " " +
                "--model " + ShellQuote(modelPath) + " " +
                "--neural-sequence-length 16 " +
                "--beam " + beamWidth + " " +
                "--depth " + maxDepth + " " +
                "--out " + ShellQuote(remoteRoot);

            var script = new StringBuilder();
            script.AppendLine("#!/bin/bash");
            script.AppendLine("set -euo pipefail");
            script.AppendLine("exec >" + ShellQuote(logPath) + " 2>&1");
            script.AppendLine(
                "/usr/bin/ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new " +
                ShellQuote(gb10Host) + " " +
                ShellQuote("mkdir -p " + ShellQuote(remoteRoot)));
            script.AppendLine(
                "/usr/bin/scp -q -o BatchMode=yes -o ConnectTimeout=10 " +
                ShellQuote(snapshotPath) + " " +
                ShellQuote(gb10Host + ":" + remoteSnapshot));
            script.AppendLine(
                "/usr/bin/ssh -o BatchMode=yes -o ConnectTimeout=10 " +
                ShellQuote(gb10Host) + " " +
                ShellQuote(remoteCommand));
            script.AppendLine(
                "/usr/bin/scp -q -o BatchMode=yes -o ConnectTimeout=10 " +
                ShellQuote(gb10Host + ":" + remoteRoot + "/task_bundle.json") + " " +
                ShellQuote(gb10Host + ":" + remoteRoot + "/replay.json") + " " +
                ShellQuote(gb10Host + ":" + remoteRoot + "/result.json") + " " +
                ShellQuote(gb10Host + ":" + remoteRoot + "/report.md") + " " +
                ShellQuote(lastRunDirectory + "/"));
            return script.ToString();
        }

        void StartVisibleReplay(
            Arena arena,
            ScenarioConfig scenario,
            System.Collections.Generic.IReadOnlyList<PlayerAction> actions)
        {
            replayAdapter = new CelesteBenchmarkAdapter();
            replayAdapter.Bind(arena, scenario);
            CelesteBenchmarkPlayer player = FindPlayer(arena.Scene);
            if (player == null)
                throw new InvalidOperationException("Replay arena has no CelesteBenchmarkPlayer.");

            VisibleLevelBuilder.AddVisuals(arena.Scene);
            VisibleLevelBuilder.AddFollowCamera(arena.Scene, player.transform);
            replayHost = new GameObject("RYZ1NeuralReplay");
            SceneManager.MoveGameObjectToScene(replayHost, arena.Scene);
            replayDriver = replayHost.AddComponent<LivePlaybackDriver>();
            replayDriver.Adapter = replayAdapter;
            replayDriver.Agent = new ReplayAgent(actions);
            replayDriver.Scenario = scenario;
            replayDriver.Seed = 0;
            replayDriver.PlaybackTickLimit = actions.Count;
            replayDriver.SetSpeed(playbackSpeed);
            SimulationDiagnosticsOverlay overlay =
                replayHost.AddComponent<SimulationDiagnosticsOverlay>();
            overlay.Bind(replayDriver, player.transform);
            replayDriver.Finished += OnVisibleReplayFinished;
            replayDriver.Restart();
            replayDriver.Play();
            EditorApplication.ExecuteMenuItem("Window/General/Game");
        }

        void OnVisibleReplayFinished()
        {
            if (replayDriver == null || replayAdapter == null)
                return;
            if (replayAdapter.IsComplete && !replayAdapter.IsDead)
            {
                SetStatus(
                    "PASS — replay complete",
                    "The visible Game View replay independently reached the Unity goal.");
            }
            else
            {
                SetStatus(
                    "FAILED — visible replay mismatch",
                    "The previously verified stream did not complete during visible replay. " +
                    "Keep the run artifacts for diagnosis.");
            }
        }

        void CancelOperation()
        {
            operation?.Cancel();
            operation = null;
            if (remoteProcess != null)
            {
                try
                {
                    if (!remoteProcess.HasExited)
                        remoteProcess.Kill();
                }
                catch (Exception)
                {
                    // Best effort: an SSH child may already have exited.
                }
            }
            DisposeRemoteProcess();
            CleanupWorkingArena();
            if (busy)
                SetStatus("Cancelled.", "The current GUI run was cancelled.");
            busy = false;
        }

        void CleanupWorkingArena()
        {
            workingArenaManager?.UnloadAll();
            workingArenaManager = null;
        }

        void StopReplay()
        {
            if (replayDriver != null)
                replayDriver.Finished -= OnVisibleReplayFinished;
            if (replayHost != null)
                Destroy(replayHost);
            replayHost = null;
            replayDriver = null;
            replayAdapter = null;
            replayArenaManager?.UnloadAll();
            replayArenaManager = null;
        }

        void DisposeRemoteProcess()
        {
            remoteProcess?.Dispose();
            remoteProcess = null;
        }

        void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode ||
                change == PlayModeStateChange.EnteredEditMode)
            {
                CancelOperation();
                StopReplay();
            }
            Repaint();
        }

        void SetStatus(string newStage, string newDetails)
        {
            stage = newStage;
            details = newDetails;
            Repaint();
        }

        void WriteJson(string fileName, object value)
        {
            File.WriteAllText(
                Path.Combine(lastRunDirectory, fileName),
                JsonUtility.ToJson(value, true));
        }

        void SavePreferences()
        {
            EditorPrefs.SetString(HostPref, gb10Host);
            EditorPrefs.SetString(RepoPref, gb10Repo);
            EditorPrefs.SetString(DotnetPref, gb10Dotnet);
            EditorPrefs.SetString(ModelPref, modelPath);
            EditorPrefs.SetInt(BeamPref, beamWidth);
            EditorPrefs.SetInt(DepthPref, maxDepth);
            EditorPrefs.SetFloat(SpeedPref, playbackSpeed);
        }

        static bool IsSceneEnabledInBuildSettings(string scenePath)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
                if (scenes[i].enabled && scenes[i].path == scenePath)
                    return true;
            return false;
        }

        static void AddSceneToBuildSettings(string scenePath)
        {
            EditorBuildSettingsScene[] existing = EditorBuildSettings.scenes;
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i].path != scenePath)
                    continue;
                existing[i].enabled = true;
                EditorBuildSettings.scenes = existing;
                return;
            }

            var updated = new EditorBuildSettingsScene[existing.Length + 1];
            Array.Copy(existing, updated, existing.Length);
            updated[existing.Length] = new EditorBuildSettingsScene(scenePath, true);
            EditorBuildSettings.scenes = updated;
        }

        static CelesteBenchmarkPlayer FindPlayer(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                CelesteBenchmarkPlayer player =
                    roots[i].GetComponentInChildren<CelesteBenchmarkPlayer>(true);
                if (player != null)
                    return player;
            }
            return null;
        }

        static string ShellQuote(string value) =>
            "'" + (value ?? string.Empty).Replace("'", "'\"'\"'") + "'";

        static string QuoteProcessArgument(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string Sanitize(string value)
        {
            var result = new StringBuilder(value?.Length ?? 0);
            string source = value ?? "scene";
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                result.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            }
            return result.Length == 0 ? "scene" : result.ToString();
        }

        static string Tail(string value, int maximumCharacters)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumCharacters)
                return value;
            return value.Substring(value.Length - maximumCharacters);
        }
    }
}
