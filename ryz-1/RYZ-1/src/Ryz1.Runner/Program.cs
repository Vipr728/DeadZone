using Ryz1.Contracts;
using Ryz1.SimCore;

namespace Ryz1.Runner;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string command = args.Length > 0 ? args[0] : "demo";
            string outDir = GetOption(args, "--out", Path.Combine("Library", "RYZ1", "runs", DateTime.UtcNow.ToString("yyyyMMddHHmmss")));
            Directory.CreateDirectory(outDir);

            if (command == "create-demo-bundle")
            {
                int seed = int.Parse(GetOption(args, "--seed", "0"));
                var demoBundle = Ryz1.SimCore.TaskFactory.CreateDemoBundle(seed);
                string path = Path.Combine(outDir, "task_bundle.json");
                Json.Write(path, demoBundle);
                Console.WriteLine(path);
                return 0;
            }

            if (command == "generate-curriculum")
                return GenerateCurriculum(args, outDir);

            string bundlePath = GetOption(args, "--bundle", "");
            RyzTaskBundleDto bundle;
            if (command == "solve-unity-snapshot")
            {
                string snapshotPath = GetOption(args, "--snapshot", "");
                if (string.IsNullOrWhiteSpace(snapshotPath))
                    throw new ArgumentException("solve-unity-snapshot requires --snapshot <path>.");
                UnityTaskSnapshotDto snapshot = Json.Read<UnityTaskSnapshotDto>(snapshotPath);
                bundle = Ryz1.SimCore.TaskFactory.CreateUnitySnapshotBundle(snapshot);
            }
            else
            {
                bundle = string.IsNullOrWhiteSpace(bundlePath)
                    ? Ryz1.SimCore.TaskFactory.CreateDemoBundle(int.Parse(GetOption(args, "--seed", "0")))
                    : Json.Read<RyzTaskBundleDto>(bundlePath);
            }
            bundle.Validate();

            int beam = int.Parse(GetOption(args, "--beam", "12"));
            int depth = int.Parse(GetOption(args, "--depth", "24"));
            string modelPath = GetOption(args, "--model", "");
            float neuralPolicyWeight = float.Parse(
                GetOption(args, "--neural-policy-weight", "0.1"),
                System.Globalization.CultureInfo.InvariantCulture);
            float neuralValueWeight = float.Parse(
                GetOption(args, "--neural-value-weight", "0"),
                System.Globalization.CultureInfo.InvariantCulture);
            int neuralSequenceLength = int.Parse(GetOption(args, "--neural-sequence-length", "16"));
            var solver = new SimBeamSearch();
            using OnnxNeuralGuide? guide = string.IsNullOrWhiteSpace(modelPath)
                ? null
                : new OnnxNeuralGuide(
                    modelPath,
                    bundle.Task.MechanicsVector.Values,
                    bundle.Task.ObservationSchema.PlayerVectorSize,
                    bundle.Task.ActionSchema.Macros.Max(macro => macro.Id) + 1,
                    neuralSequenceLength);
            SimSolveResult result = solver.Solve(
                bundle,
                new SimSearchConfig
                {
                    BeamWidth = beam,
                    MaxDepth = depth,
                    NeuralPolicyWeight = guide == null ? 0f : neuralPolicyWeight,
                    NeuralValueWeight = guide == null ? 0f : neuralValueWeight,
                },
                guide);

            Json.Write(Path.Combine(outDir, "task_bundle.json"), bundle);
            Json.Write(Path.Combine(outDir, "dataset.json"), result.Dataset);
            Json.Write(Path.Combine(outDir, "replay.json"), result.Replay);
            Json.Write(Path.Combine(outDir, "result.json"), result);
            File.WriteAllText(Path.Combine(outDir, "report.md"),
                $"# RYZ-1 SimCore Report\n\nTask: `{bundle.Task.TaskId}`\n\nSolved: {result.Solved}\n\n" +
                $"Nodes expanded: {result.NodesExpanded}\n\nTicks simulated: {result.TicksSimulated}\n\n" +
                $"Furthest progress: {result.FurthestProgress:0.000}\n\nReplay: {result.Replay.Diagnostic}\n");
            Console.WriteLine($"RYZ-1 SimCore run complete: {outDir}");
            Console.WriteLine($"solved={result.Solved} nodes={result.NodesExpanded} ticks={result.TicksSimulated}");
            Console.WriteLine(
                guide == null
                    ? "guide=none"
                    : $"guide=onnx model={Path.GetFullPath(modelPath)} " +
                      $"policy_weight={neuralPolicyWeight} value_weight={neuralValueWeight}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RYZ-1 runner failed: " + ex.Message);
            return 1;
        }
    }

    private static int GenerateCurriculum(string[] args, string outDir)
    {
        int baseSeed = int.Parse(GetOption(args, "--seed", "100"));
        int repetitions = int.Parse(GetOption(args, "--repetitions", "2"));
        int beam = int.Parse(GetOption(args, "--beam", "16"));
        int depth = int.Parse(GetOption(args, "--depth", "32"));
        int maxTicks = int.Parse(GetOption(args, "--max-search-ticks", "500000"));
        IReadOnlyList<RyzTaskBundleDto> bundles = Ryz1.SimCore.TaskFactory.CreateCurriculumBundles(baseSeed, repetitions);
        var solver = new SimBeamSearch();
        var transitions = new List<DatasetTransitionDto>();
        var summaries = new List<CurriculumTaskRunDto>();

        for (int index = 0; index < bundles.Count; index++)
        {
            RyzTaskBundleDto bundle = bundles[index];
            bundle.Validate();
            string taskDir = Path.Combine(outDir, "tasks", bundle.Task.TaskId);
            Directory.CreateDirectory(taskDir);
            SimSolveResult result = solver.Solve(
                bundle,
                new SimSearchConfig
                {
                    BeamWidth = beam,
                    MaxDepth = depth,
                    MaxTicksSimulated = maxTicks,
                },
                trialId: index);
            transitions.AddRange(result.Dataset.Transitions);

            string bundlePath = Path.Combine(taskDir, "task_bundle.json");
            string replayPath = Path.Combine(taskDir, "replay.json");
            Json.Write(bundlePath, bundle);
            Json.Write(Path.Combine(taskDir, "dataset.json"), result.Dataset);
            Json.Write(replayPath, result.Replay);
            Json.Write(Path.Combine(taskDir, "result.json"), result);
            summaries.Add(new CurriculumTaskRunDto
            {
                TaskId = bundle.Task.TaskId,
                Archetype = bundle.Task.CurriculumArchetype,
                StageCount = bundle.Task.StageCount,
                FeatureFlags = bundle.Task.FeatureFlags,
                Solved = result.Solved,
                NodesExpanded = result.NodesExpanded,
                DeathsPruned = result.DeathsPruned,
                TicksSimulated = result.TicksSimulated,
                TransitionCount = result.Dataset.Transitions.Count,
                FurthestProgress = result.FurthestProgress,
                BundlePath = Path.GetRelativePath(outDir, bundlePath),
                ReplayPath = Path.GetRelativePath(outDir, replayPath),
            });
            Console.WriteLine(
                $"[{index + 1}/{bundles.Count}] {bundle.Task.TaskId} " +
                $"solved={result.Solved} transitions={result.Dataset.Transitions.Count} " +
                $"features={string.Join(',', bundle.Task.FeatureFlags)}");
        }

        string[] taskIds = bundles.Select(bundle => bundle.Task.TaskId).ToArray();
        var dataset = new DatasetFileDto
        {
            DatasetId = $"curriculum-{baseSeed}-r{repetitions}",
            Split = "train",
            TaskIds = taskIds,
            Transitions = transitions,
        };
        var manifest = new CurriculumRunDto
        {
            DatasetId = dataset.DatasetId,
            BaseSeed = baseSeed,
            Repetitions = repetitions,
            BeamWidth = beam,
            MaxDepth = depth,
            TaskCount = bundles.Count,
            SolvedTaskCount = summaries.Count(summary => summary.Solved),
            TransitionCount = transitions.Count,
            Tasks = summaries,
        };
        Json.Write(Path.Combine(outDir, "dataset.json"), dataset);
        Json.Write(Path.Combine(outDir, "curriculum_manifest.json"), manifest);
        File.WriteAllText(
            Path.Combine(outDir, "report.md"),
            $"# RYZ-1 Curriculum Generation Report\n\n" +
            $"Dataset: `{dataset.DatasetId}`\n\n" +
            $"Tasks solved: {manifest.SolvedTaskCount}/{manifest.TaskCount}\n\n" +
            $"Transitions: {manifest.TransitionCount}\n\n" +
            $"Beam width: {beam}\n\nMaximum search depth: {depth}\n");
        Console.WriteLine(
            $"RYZ-1 curriculum complete: tasks={manifest.TaskCount} " +
            $"solved={manifest.SolvedTaskCount} transitions={manifest.TransitionCount}");
        Console.WriteLine($"dataset={Path.Combine(outDir, "dataset.json")}");
        return 0;
    }

    private static string GetOption(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return fallback;
    }
}

internal sealed record CurriculumTaskRunDto
{
    public string TaskId { get; init; } = "";
    public string Archetype { get; init; } = "";
    public int StageCount { get; init; }
    public IReadOnlyList<string> FeatureFlags { get; init; } = Array.Empty<string>();
    public bool Solved { get; init; }
    public int NodesExpanded { get; init; }
    public int DeathsPruned { get; init; }
    public int TicksSimulated { get; init; }
    public int TransitionCount { get; init; }
    public float FurthestProgress { get; init; }
    public string BundlePath { get; init; } = "";
    public string ReplayPath { get; init; } = "";
}

internal sealed record CurriculumRunDto
{
    public string DatasetId { get; init; } = "";
    public int BaseSeed { get; init; }
    public int Repetitions { get; init; }
    public int BeamWidth { get; init; }
    public int MaxDepth { get; init; }
    public int TaskCount { get; init; }
    public int SolvedTaskCount { get; init; }
    public int TransitionCount { get; init; }
    public IReadOnlyList<CurriculumTaskRunDto> Tasks { get; init; } = Array.Empty<CurriculumTaskRunDto>();
}
