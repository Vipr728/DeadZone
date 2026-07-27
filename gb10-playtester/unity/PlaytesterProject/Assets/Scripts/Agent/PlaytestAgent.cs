using Playtester.Gym;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.Tilemaps;
using Playtester.Telemetry;
using MLAgent = Unity.MLAgents.Agent;

namespace Playtester.Agent
{
    [RequireComponent(typeof(BehaviorParameters))]
    public sealed class PlaytestAgent : MLAgent
    {
        [SerializeField] private PlayerInputAdapter playerInput = null!;
        [SerializeField] private PlayerController playerController = null!;
        [SerializeField] private Rigidbody2D playerBody = null!;
        [SerializeField] private Tilemap tilemap = null!;
        [SerializeField] private GridObservationEncoder observationEncoder = null!;
        [SerializeField] private RewardConfigAsset rewardConfig = null!;
        [SerializeField] private PieceComposer pieceComposer = null!;
        [SerializeField] private StartMarker levelStart = null!;
        [SerializeField] private GoalMarker currentGoal = null!;
        [SerializeField] private TelemetryRecorder telemetryRecorder = null!;
        [SerializeField] private string levelId = "level_a";
        [SerializeField] private string checkpointPath = string.Empty;
        [SerializeField] private bool stageOne;

        private IRewardStrategy rewardStrategy = null!;
        private GoalMarker initialGoal = null!;
        private float previousGoalDistance;
        private int completedPieces;
        private bool episodeActive;
        private float episodeStartTime;
        private bool inferenceSmoke;
        private bool scriptedSmoke;
        private int inferenceSmokeActionCount;

        public override void Initialize()
        {
            BehaviorParameters behavior = GetComponent<BehaviorParameters>();
            behavior.BehaviorName = "PlaytestAgent";
            inferenceSmoke = HasCommandLineFlag("--inference-smoke");
            scriptedSmoke = HasCommandLineFlag("--scripted-smoke");
            checkpointPath = ReadCommandLineValue("--checkpoint") ?? checkpointPath;
            if (inferenceSmoke)
            {
                if (behavior.BehaviorType != BehaviorType.InferenceOnly || behavior.Model == null)
                    throw new System.InvalidOperationException(
                        "Inference smoke requires an assigned model and BehaviorType.InferenceOnly.");
                Debug.Log(
                    "PLAYTESTER_INFERENCE_POLICY_READY " +
                    $"behavior={behavior.BehaviorType} model={behavior.Model.name} device={behavior.InferenceDevice}");
            }
            rewardStrategy = rewardConfig.ActiveStrategy == "single_gym_fallback"
                ? new SingleGymFallbackStrategy(rewardConfig)
                : new CompositionalRewardStrategy(rewardConfig);
            initialGoal = currentGoal;
            MaxStep = ReadMaxStepsOverride() ?? rewardConfig.MaxSteps;
            playerController.SetAgentControlEnabled(true);
            if (telemetryRecorder != null)
                telemetryRecorder.BeginRun(levelId, stageOne ? "stage1" : "stage2", checkpointPath);
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            observationEncoder.Encode(
                sensor,
                playerBody,
                playerController,
                tilemap,
                currentGoal == null ? null! : currentGoal.transform);
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discreteActions = actionsOut.DiscreteActions;
            if (discreteActions.Length > 0)
                discreteActions[0] = 0;
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (actions.DiscreteActions.Length == 0)
            {
                return;
            }
            int action = actions.DiscreteActions[0];
            if (scriptedSmoke)
                action = 2;
            if (inferenceSmoke)
            {
                inferenceSmokeActionCount++;
                if (inferenceSmokeActionCount == 1)
                    Debug.Log($"PLAYTESTER_INFERENCE_ACTION_PASS action={action}");
            }
            playerInput.SetMove(action == 1 ? -1f : action == 2 ? 1f : 0f);
            playerInput.SetJump(action == 3);
            float currentDistance = DistanceToCurrentGoal();
            AddReward(rewardStrategy.PieceProgressReward(previousGoalDistance - currentDistance));
            AddReward(rewardStrategy.StepTimePenalty());
            previousGoalDistance = currentDistance;
        }

        public override void OnEpisodeBegin()
        {
            if (episodeActive)
            {
                // ML-Agents invokes OnEpisodeBegin after MaxStep without
                // calling Die/CompletePiece. Record that automatic boundary
                // so standalone playtests never silently lose timeout data.
                telemetryRecorder?.EndEpisode("timeout", GetCumulativeReward(), null);
            }
            completedPieces = 0;
            if (stageOne)
            {
                pieceComposer.Recompose();
            }
            else
            {
                currentGoal = initialGoal;
                playerBody.position = levelStart.transform.position;
                playerBody.linearVelocity = Vector2.zero;
                foreach (AgentTriggerRelay relay in FindObjectsByType<AgentTriggerRelay>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None))
                {
                    relay.ResetForEpisode();
                }
            }
            previousGoalDistance = DistanceToCurrentGoal();
            if (telemetryRecorder != null)
                telemetryRecorder.BeginEpisode(CompletedEpisodes);
            episodeStartTime = Time.time;
            episodeActive = true;
        }

        private void FixedUpdate()
        {
            if (telemetryRecorder != null)
                telemetryRecorder.RecordPosition(Time.time, playerBody.position);
        }

        public void CompletePiece(
            Transform nextGoal,
            string pieceId = "piece_1",
            string pieceType = "move_to_goal",
            PieceParams parameters = default,
            bool seenInStageOneRange = true)
        {
            telemetryRecorder?.RecordPieceResult(
                pieceId,
                pieceType,
                parameters,
                1,
                Time.time - episodeStartTime,
                null,
                seenInStageOneRange);
            completedPieces++;
            AddReward(rewardStrategy.PieceCompletionBonus());
            if (completedPieces >= 3)
            {
                AddReward(rewardStrategy.FinalSequenceBonus());
                telemetryRecorder?.EndEpisode("success", GetCumulativeReward(), Time.time);
                episodeActive = false;
                EndEpisode();
                return;
            }
            currentGoal = nextGoal != null ? nextGoal.GetComponent<GoalMarker>() : currentGoal;
            previousGoalDistance = DistanceToCurrentGoal();
            playerBody.linearVelocity = Vector2.zero;
        }

        public void Die(
            string pieceId = "piece_1",
            string pieceType = "move_to_goal",
            PieceParams parameters = default,
            bool seenInStageOneRange = true)
        {
            telemetryRecorder?.RecordPieceResult(
                pieceId,
                pieceType,
                parameters,
                1,
                null,
                playerBody.position,
                seenInStageOneRange);
            AddReward(rewardStrategy.DeathPenalty());
            telemetryRecorder?.EndEpisode("death", GetCumulativeReward(), null);
            episodeActive = false;
            EndEpisode();
        }

        /// <summary>Records a bounded standalone smoke run when no policy requests decisions.</summary>
        public void RecordStandaloneTimeout()
        {
            if (!episodeActive)
            {
                OnEpisodeBegin();
            }
            telemetryRecorder?.EndEpisode("timeout", GetCumulativeReward(), null);
            episodeActive = false;
        }

        public void SetCurrentGoal(Transform goal)
        {
            currentGoal = goal != null ? goal.GetComponent<GoalMarker>() : null!;
            previousGoalDistance = DistanceToCurrentGoal();
        }

        private float DistanceToCurrentGoal()
        {
            return currentGoal == null ? 0f : Vector2.Distance(playerBody.position, currentGoal.transform.position);
        }

        // Kept opt-in so production episode settings remain entirely YAML-driven.
        // This is useful for a bounded Unity-to-trainer compatibility smoke run.
        private static int? ReadMaxStepsOverride()
        {
            string[] arguments = System.Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index] == "--mlagents-max-steps" &&
                    int.TryParse(arguments[index + 1], out int maxSteps) &&
                    maxSteps > 0)
                {
                    return maxSteps;
                }
            }

            return null;
        }

        private static bool HasCommandLineFlag(string flag)
        {
            foreach (string argument in System.Environment.GetCommandLineArgs())
                if (argument == flag)
                    return true;
            return false;
        }

        private static string? ReadCommandLineValue(string flag)
        {
            string[] arguments = System.Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < arguments.Length; index++)
                if (arguments[index] == flag)
                    return arguments[index + 1];
            return null;
        }
    }
}
