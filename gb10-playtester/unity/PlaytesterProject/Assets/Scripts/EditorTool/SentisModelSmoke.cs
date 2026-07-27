#if UNITY_EDITOR
using System;
using System.IO;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Playtester.EditorTool
{
    /// <summary>
    /// Runs one CPU Inference Engine decision against a trainer-exported ML-Agents model.
    /// Invoke in batch mode with --sentis-model-asset Assets/path/to/model.onnx.
    /// </summary>
    public static class SentisModelSmoke
    {
        private const string ModelPathArgument = "--sentis-model-asset";
        private const string ModelSourceArgument = "--sentis-model-source";
        private const string SourceScenePath = "Assets/Scenes/LevelA.unity";
        private const string GeneratedRoot = "Assets/GeneratedInferenceSmoke";
        private const string GeneratedModelPath = GeneratedRoot + "/PlaytestAgent.onnx";
        private const string GeneratedScenePath = GeneratedRoot + "/LevelAInferenceSmoke.unity";

        public static void Run()
        {
            string assetPath = ReadModelAssetPath();
            ModelAsset modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(assetPath);
            if (modelAsset == null)
                throw new InvalidOperationException($"No Inference Engine model asset was imported at {assetPath}.");

            Model model = ModelLoader.Load(modelAsset);
            using var worker = new Worker(model, BackendType.CPU);
            using var observations = new Tensor<float>(new TensorShape(1, 203));
            using var actionMask = new Tensor<float>(new TensorShape(1, 4), new[] { 1f, 1f, 1f, 1f });

            worker.SetInput("obs_0", observations);
            worker.SetInput("action_masks", actionMask);
            worker.Schedule();

            var actionTensor = worker.PeekOutput("discrete_actions") as Tensor<int>;
            if (actionTensor == null)
                throw new InvalidOperationException("Model output 'discrete_actions' was not an integer action tensor.");
            using Tensor<int> action = actionTensor.ReadbackAndClone();
            int selectedAction = action[0];
            if (selectedAction < 0 || selectedAction > 3)
                throw new InvalidOperationException($"Model returned out-of-contract action {selectedAction}.");

            Debug.Log($"PLAYTESTER_SENTIS_SMOKE_PASS action={selectedAction} asset={assetPath}");
        }

        /// <summary>
        /// Builds a disposable Level A player with a trainer-exported ONNX assigned
        /// to BehaviorParameters in InferenceOnly mode. The source scene is never saved.
        /// </summary>
        public static void BuildInferencePlayback()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
                throw new InvalidOperationException("Run with -buildTarget StandaloneOSX for the laptop smoke build.");

            string sourceModelPath = ReadArgument(ModelSourceArgument);
            if (!File.Exists(sourceModelPath))
                throw new FileNotFoundException("The trainer-exported ONNX model does not exist.", sourceModelPath);

            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string buildPath = Path.Combine(
                projectRoot,
                "Builds",
                "inference_smoke",
                "level_a_inference_smoke.app");

            try
            {
                Directory.CreateDirectory(GeneratedRoot);
                File.Copy(sourceModelPath, GeneratedModelPath, true);
                AssetDatabase.ImportAsset(
                    GeneratedModelPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);
                BehaviorParameters behavior = UnityEngine.Object.FindFirstObjectByType<BehaviorParameters>(
                    FindObjectsInactive.Include);
                if (behavior == null)
                    throw new InvalidOperationException("Level A has no BehaviorParameters component.");

                ModelAsset modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(GeneratedModelPath);
                if (modelAsset == null)
                    throw new InvalidOperationException($"Unity did not import {GeneratedModelPath} as a ModelAsset.");
                behavior.Model = modelAsset;
                behavior.BehaviorType = BehaviorType.InferenceOnly;
                behavior.InferenceDevice = InferenceDevice.Burst;
                behavior.DeterministicInference = true;
                EditorUtility.SetDirty(behavior);
                string assignedModelName = modelAsset.name;
                string assignedBehaviorType = behavior.BehaviorType.ToString();
                string assignedInferenceDevice = behavior.InferenceDevice.ToString();

                if (!EditorSceneManager.SaveScene(scene, GeneratedScenePath, true))
                    throw new InvalidOperationException("Could not save the disposable inference smoke scene.");
                AssetDatabase.ImportAsset(GeneratedScenePath, ImportAssetOptions.ForceSynchronousImport);

                Directory.CreateDirectory(Path.GetDirectoryName(buildPath)!);
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { GeneratedScenePath },
                    locationPathName = buildPath,
                    target = BuildTarget.StandaloneOSX,
                    options = BuildOptions.None,
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException($"Inference smoke build failed: {report.summary.result}.");

                Debug.Log(
                    "PLAYTESTER_INFERENCE_BUILD_PASS " +
                    $"behavior={assignedBehaviorType} model={assignedModelName} " +
                    $"device={assignedInferenceDevice} build={buildPath}");
            }
            finally
            {
                AssetDatabase.DeleteAsset(GeneratedRoot);
                if (File.Exists(SourceScenePath))
                    EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);
            }
        }

        private static string ReadModelAssetPath()
        {
            return ReadArgument(ModelPathArgument);
        }

        private static string ReadArgument(string argumentName)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length - 1; index++)
                if (arguments[index] == argumentName)
                    return arguments[index + 1];
            throw new InvalidOperationException($"Pass {argumentName} followed by its value.");
        }
    }
}
#endif
