using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using PlatformerPlaytest;
using Ryzi.Integrations.ExistingSimulator;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ryzi.Integrations.Tests.PlayMode
{
    public sealed class NativeSimCoreBridgeTests
    {
        static readonly Vector2 UnseenSpawn = new Vector2(0f, 1f);

        ArenaManager arenas;

        [SetUp]
        public void SetUp() => arenas = new ArenaManager();

        [TearDown]
        public void TearDown() => arenas.UnloadAll();

        [UnityTest]
        public IEnumerator UnityFixture_ExportsNativeSnapshot()
        {
            string output = Environment.GetEnvironmentVariable("RYZ1_UNITY_SNAPSHOT_OUT");
            if (string.IsNullOrWhiteSpace(output))
                Assert.Ignore("RYZ1_UNITY_SNAPSHOT_OUT not set — bridge export probe skipped.");

            Arena arena = arenas.CreateArena("native-bridge-export", DemoLevel.Build);
            yield return null;
            UnityTaskSnapshot snapshot = NativeSimCoreBridge.ExportSnapshot(
                arena,
                DemoLevel.MakeScenario(),
                output,
                "unity-demo-bridge");

            Assert.That(File.Exists(output), Is.True);
            Assert.That(snapshot.platforms, Is.Not.Empty);
            Assert.That(snapshot.hazards, Is.Not.Empty);
            Assert.That(snapshot.movement.jumpVelocity, Is.GreaterThan(0f));
        }

        [UnityTest]
        public IEnumerator UnseenStaticFixture_ExportsCurrentSceneCompatibleSnapshot()
        {
            string output = Environment.GetEnvironmentVariable("RYZ1_UNSEEN_SNAPSHOT_OUT");
            if (string.IsNullOrWhiteSpace(output))
                Assert.Ignore("RYZ1_UNSEEN_SNAPSHOT_OUT not set — unseen-level export probe skipped.");

            Arena arena = arenas.CreateArena("native-bridge-unseen-export", BuildUnseenStaticLevel);
            yield return null;
            ScenarioConfig scenario =
                new CelesteBenchmarkScenarioProvider("unseen-static-gui-smoke").CreateScenario(arena, 2718);
            BridgeCompatibilityReport report =
                NativeSimCoreBridge.InspectCompatibility(arena, scenario);
            Assert.That(report.supported, Is.True, report.Summary());

            UnityTaskSnapshot snapshot = NativeSimCoreBridge.ExportSnapshot(
                arena,
                scenario,
                output,
                "unseen-static-gui-smoke");

            Assert.That(File.Exists(output), Is.True);
            Assert.That(snapshot.platforms.Length, Is.EqualTo(20));
            Assert.That(snapshot.hazards.Length, Is.EqualTo(1));
            Assert.That(snapshot.goal.x, Is.EqualTo(10.5f).Within(0.01f));
        }

        [Test]
        public void Compatibility_AcceptsStaticDemoSubset()
        {
            Arena arena = arenas.CreateArena("native-bridge-compatible", DemoLevel.Build);
            BridgeCompatibilityReport report =
                NativeSimCoreBridge.InspectCompatibility(arena, DemoLevel.MakeScenario());

            Assert.That(report.supported, Is.True, report.Summary());
            Assert.That(report.platformCount, Is.GreaterThan(0));
            Assert.That(report.warnings, Is.Not.Empty, "DemoLevel's unmodeled dash refill should be disclosed.");
        }

        [Test]
        public void Compatibility_RejectsDynamicPlatformLayer()
        {
            Arena arena = arenas.CreateArena("native-bridge-dynamic", scene =>
            {
                DemoLevel.Build(scene);
                GameObject moving = new GameObject("Unsupported Moving Platform");
                moving.layer = LayerMask.NameToLayer("MovingPlatform");
                moving.AddComponent<BoxCollider2D>();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(moving, scene);
            });

            BridgeCompatibilityReport report =
                NativeSimCoreBridge.InspectCompatibility(arena, DemoLevel.MakeScenario());

            Assert.That(report.supported, Is.False);
            Assert.That(
                string.Join("\n", report.errors),
                Does.Contain("unsupported dynamic platform layer"));
        }

        [Test]
        public void Compatibility_RejectsRotatedPlatform()
        {
            Arena arena = arenas.CreateArena("native-bridge-rotated", scene =>
            {
                DemoLevel.Build(scene);
                GameObject rotated = new GameObject("Unsupported Rotated Platform");
                rotated.layer = LayerMask.NameToLayer("Ground");
                rotated.transform.rotation = Quaternion.Euler(0f, 0f, 12f);
                rotated.AddComponent<BoxCollider2D>();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(rotated, scene);
            });

            BridgeCompatibilityReport report =
                NativeSimCoreBridge.InspectCompatibility(arena, DemoLevel.MakeScenario());

            Assert.That(report.supported, Is.False);
            Assert.That(string.Join("\n", report.errors), Does.Contain("only axis-aligned"));
        }

        [UnityTest]
        public IEnumerator NativeReplay_CompletesInAuthoritativeUnityArena()
        {
            string directory = Environment.GetEnvironmentVariable("RYZ1_NATIVE_BRIDGE_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                Assert.Ignore("RYZ1_NATIVE_BRIDGE_DIR not set — native replay probe skipped.");

            string taskBundle = Path.Combine(directory, "task_bundle.json");
            string replayPath = Path.Combine(directory, "replay.json");
            string resultPath = Path.Combine(directory, "unity-verification.json");
            var actions = NativeSimCoreBridge.LoadNativeReplay(taskBundle, replayPath, out NativeReplay replay);

            Arena arena = arenas.CreateArena("native-bridge-replay", DemoLevel.Build);
            yield return null;
            BridgeReplayVerification result = NativeSimCoreBridge.VerifyInUnity(
                arena,
                DemoLevel.MakeScenario(),
                actions);
            File.WriteAllText(resultPath, JsonUtility.ToJson(result, true));

            Assert.That(replay.verified, Is.True, replay.diagnostic);
            Assert.That(result.completed, Is.True, result.diagnostic);
            Assert.That(result.died, Is.False, result.diagnostic);
        }

        [UnityTest]
        public IEnumerator UnseenStaticNativeReplay_CompletesInAuthoritativeUnityArena()
        {
            string directory = Environment.GetEnvironmentVariable("RYZ1_UNSEEN_BRIDGE_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                Assert.Ignore("RYZ1_UNSEEN_BRIDGE_DIR not set — unseen-level replay probe skipped.");

            string taskBundle = Path.Combine(directory, "task_bundle.json");
            string replayPath = Path.Combine(directory, "replay.json");
            string resultPath = Path.Combine(directory, "unity-verification.json");
            var actions = NativeSimCoreBridge.LoadNativeReplay(
                taskBundle,
                replayPath,
                out NativeReplay replay);

            Arena arena = arenas.CreateArena("native-bridge-unseen-replay", BuildUnseenStaticLevel);
            yield return null;
            ScenarioConfig scenario =
                new CelesteBenchmarkScenarioProvider("unseen-static-gui-smoke").CreateScenario(arena, 2718);
            BridgeReplayVerification result =
                NativeSimCoreBridge.VerifyInUnity(arena, scenario, actions, 2718);
            File.WriteAllText(resultPath, JsonUtility.ToJson(result, true));

            Assert.That(replay.verified, Is.True, replay.diagnostic);
            Assert.That(result.completed, Is.True, result.diagnostic);
            Assert.That(result.died, Is.False, result.diagnostic);
        }

        static void BuildUnseenStaticLevel(UnityEngine.SceneManagement.Scene scene)
        {
            int groundLayer = LayerMask.NameToLayer("Ground");
            int hazardLayer = LayerMask.NameToLayer("Hazard");
            int triggerLayer = LayerMask.NameToLayer("Trigger");

            GameObject groundRoot = new GameObject("Unseen Ground");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(groundRoot, scene);
            for (int tile = -3; tile < 17; tile++)
            {
                GameObject ground = new GameObject("Ground " + tile);
                ground.layer = groundLayer;
                ground.transform.SetParent(groundRoot.transform);
                ground.transform.position = new Vector2(tile + 0.5f, 0f);
                ground.AddComponent<BoxCollider2D>();
            }

            GameObject hazard = new GameObject("Unseen Jump Hazard");
            hazard.layer = hazardLayer;
            hazard.transform.position = new Vector2(5.5f, 0.75f);
            BoxCollider2D hazardCollider = hazard.AddComponent<BoxCollider2D>();
            hazardCollider.size = new Vector2(1f, 0.5f);
            hazardCollider.isTrigger = true;
            hazard.AddComponent<CelesteBenchmark.BenchmarkSpike>();
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(hazard, scene);

            GameObject goal = new GameObject("Unseen Goal");
            goal.layer = triggerLayer;
            goal.transform.position = new Vector2(11f, 1f);
            BoxCollider2D goalCollider = goal.AddComponent<BoxCollider2D>();
            goalCollider.size = Vector2.one;
            goalCollider.isTrigger = true;
            goal.AddComponent<CelesteBenchmark.BenchmarkGoal>();
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(goal, scene);

            GameObject player = new GameObject("Unseen Player");
            player.layer = LayerMask.NameToLayer("Player");
            player.transform.position = UnseenSpawn;
            Rigidbody2D body = player.AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Dynamic;
            body.gravityScale = 0f;
            body.freezeRotation = true;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            CapsuleCollider2D capsule = player.AddComponent<CapsuleCollider2D>();
            capsule.size = new Vector2(0.72f, 1.05f);
            capsule.direction = CapsuleDirection2D.Vertical;
            CelesteBenchmark.CelesteBenchmarkPlayer controller =
                player.AddComponent<CelesteBenchmark.CelesteBenchmarkPlayer>();
            controller.startPosition = UnseenSpawn;
            controller.groundMask = LayerMask.GetMask("Ground");
            controller.wallMask = LayerMask.GetMask("Ground");
            controller.oneWayPlatformMask = LayerMask.GetMask("OneWay");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(player, scene);
        }
    }
}
