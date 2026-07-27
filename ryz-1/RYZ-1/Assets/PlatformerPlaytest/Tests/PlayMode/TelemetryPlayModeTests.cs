using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    public class TelemetryPlayModeTests
    {
        ArenaManager arenaManager;

        [SetUp]
        public void SetUp()
        {
            arenaManager = new ArenaManager();
        }

        [TearDown]
        public void TearDown()
        {
            arenaManager.UnloadAll();
        }

        static string RunId() => "playmode-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // Records a scripted episode; captures per-tick actions and the recorder's keyframes.
        static void Record(CelesteBenchmarkAdapter adapter, ScenarioConfig scenario, int seed,
            TelemetryRecorder recorder, List<PlayerAction> actions)
        {
            EpisodeRunner runner = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed);
            runner.Run((tick, action, obs) =>
            {
                actions.Add(action);
                string ev = adapter.IsDead ? "Death" : (adapter.IsComplete ? "GoalReached" : null);
                recorder.OnStep(tick, in action, obs, ev);
            });
        }

        [UnityTest]
        public IEnumerator RecordThenReplay_ZeroDesync()
        {
            Arena arena = arenaManager.CreateArena("rec-replay", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            scenario.stepBudget = 400;
            adapter.Bind(arena, scenario);

            TelemetryRecorder recorder = new TelemetryRecorder(RunId(), "1", recordFullTrajectory: true);
            List<PlayerAction> actions = new List<PlayerAction>();
            Record(adapter, scenario, seed: 11, recorder, actions);

            ReplayVerifier verifier = new ReplayVerifier(recorder.Keyframes);
            EpisodeRunner replay = new EpisodeRunner(adapter, new ReplayAgent(actions), scenario, seed: 11);
            replay.Run((tick, action, obs) => verifier.OnStep(tick, obs));

            Assert.AreEqual(-1, verifier.Report.FirstDesyncTick,
                $"unexpected desync: {verifier.Report.FieldDeltas}");
        }

        [UnityTest]
        public IEnumerator TamperedReplay_DesyncDetected()
        {
            Arena arena = arenaManager.CreateArena("tamper", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            scenario.stepBudget = 400;
            adapter.Bind(arena, scenario);

            TelemetryRecorder recorder = new TelemetryRecorder(RunId(), "1", recordFullTrajectory: true);
            List<PlayerAction> actions = new List<PlayerAction>();
            Record(adapter, scenario, seed: 11, recorder, actions);

            // Tamper: remove the first jump (or flip movement if no jump was recorded).
            int tamperTick = -1;
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].JumpPressed)
                {
                    PlayerAction a = actions[i];
                    a.JumpPressed = false;
                    a.JumpHeld = false;
                    actions[i] = a;
                    tamperTick = i;
                    break;
                }
            }
            if (tamperTick < 0)
            {
                tamperTick = Math.Min(45, actions.Count - 1);
                PlayerAction a = actions[tamperTick];
                a.MoveX = -1f;
                actions[tamperTick] = a;
            }

            ReplayVerifier verifier = new ReplayVerifier(recorder.Keyframes);
            EpisodeRunner replay = new EpisodeRunner(adapter, new ReplayAgent(actions), scenario, seed: 11);
            replay.Run((tick, action, obs) => verifier.OnStep(tick, obs));

            Assert.AreNotEqual(-1, verifier.Report.FirstDesyncTick, "expected a desync but none detected");
            Assert.GreaterOrEqual(verifier.Report.FirstDesyncTick, tamperTick,
                "desync reported before the tampered tick");
        }

        [UnityTest]
        public IEnumerator RecorderWritesUnderLibrary()
        {
            Arena arena = arenaManager.CreateArena("writes", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            scenario.stepBudget = 120;
            adapter.Bind(arena, scenario);

            string runId = RunId();
            TelemetryRecorder recorder = new TelemetryRecorder(runId, "1", recordFullTrajectory: true);
            List<PlayerAction> actions = new List<PlayerAction>();
            Record(adapter, scenario, seed: 5, recorder, actions);
            recorder.Flush();
            recorder.Flush(); // idempotent

            RunWriter writer = new RunWriter(runId);
            writer.WriteHeader(new RunHeader { runId = runId, scenarioId = "test", fixedDeltaTime = 0.02f });
            writer.AppendEpisode(new EpisodeSummary { episodeId = "1", agentId = "scripted", seed = 5 });

            string dir = PlaytestPaths.RunDir(runId);
            string full = Path.GetFullPath(dir).Replace('\\', '/');
            StringAssert.Contains("/Library/PlatformerPlaytest/", full);
            Assert.IsFalse(full.Contains("/Assets/"), full);

            Assert.IsTrue(File.Exists(Path.Combine(dir, "ep_1.actions.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "ep_1.keyframes.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "ep_1.frames.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "run.json")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "episodes.jsonl")));

            Directory.Delete(dir, true);
        }
    }
}
