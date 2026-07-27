using System;
using System.IO;
using NUnit.Framework;
using Ryzi.Editor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryzi.Tests.EditMode
{
    public sealed class RyziContractTests
    {
        [Test]
        public void ButtonEdges_PreservePressedHeldReleasedSemantics()
        {
            ButtonActionState press = new ButtonActionState("button.0", true, true, false);
            ButtonActionState hold = new ButtonActionState("button.0", false, true, false);
            ButtonActionState release = new ButtonActionState("button.0", false, false, true);

            Assert.That(press.PressedThisTick, Is.True);
            Assert.That(press.Held, Is.True);
            Assert.That(hold.PressedThisTick, Is.False);
            Assert.That(hold.Held, Is.True);
            Assert.That(release.ReleasedThisTick, Is.True);
            Assert.Throws<ArgumentException>(() => new ButtonActionState("button.0", true, false, true));
        }

        [Test]
        public void UniversalAction_DefensivelyCopiesButtons()
        {
            ButtonActionState[] buttons =
            {
                new ButtonActionState("button.0", true, true, false)
            };
            UniversalAction action = new UniversalAction(Vector2.right, Vector2.zero, buttons);
            buttons[0] = new ButtonActionState("button.changed", false, true, false);

            Assert.That(action.TryGetButton("button.0", out ButtonActionState state), Is.True);
            Assert.That(state.PressedThisTick, Is.True);
            Assert.That(action.TryGetButton("button.changed", out _), Is.False);
        }

        [Test]
        public void Manifest_JsonRoundTrip_PreservesEvidenceAndVersion()
        {
            MechanicsManifest manifest = new MechanicsManifest
            {
                scenarioId = "fixture",
                sourceFingerprint = "abc",
                actions = new[]
                {
                    new ActionChannelDefinition
                    {
                        id = "button.0",
                        suggestedName = "Unverified action",
                        confidence = 0.6f,
                        evidenceLevel = EvidenceLevel.SourceCandidate,
                        evidence = new[]
                        {
                            new DiscoveryEvidence
                            {
                                id = "fixture",
                                summary = "Fixture evidence",
                                source = "test",
                                level = EvidenceLevel.SourceCandidate,
                                weight = 0.6f
                            }
                        }
                    }
                }
            };

            MechanicsManifest copy = JsonUtility.FromJson<MechanicsManifest>(JsonUtility.ToJson(manifest));
            Assert.That(copy.manifestVersion, Is.EqualTo(MechanicsManifest.CurrentVersion));
            Assert.That(copy.actions[0].id, Is.EqualTo("button.0"));
            Assert.That(copy.actions[0].evidence[0].summary, Is.EqualTo("Fixture evidence"));
        }

        [Test]
        public void ReplayAction_JsonRoundTrip_PreservesEdges()
        {
            UniversalAction action = new UniversalAction(
                new Vector2(0.5f, -0.25f),
                Vector2.up,
                new[] { new ButtonActionState("button.1", true, true, false) });
            ReplayRecord record = new ReplayRecord
            {
                actions = new[] { SerializedUniversalAction.From(in action) }
            };

            ReplayRecord copy = JsonUtility.FromJson<ReplayRecord>(JsonUtility.ToJson(record));
            UniversalAction restored = copy.actions[0].ToAction();
            Assert.That(restored.MoveAxis, Is.EqualTo(new Vector2(0.5f, -0.25f)));
            Assert.That(restored.TryGetButton("button.1", out ButtonActionState button), Is.True);
            Assert.That(button.PressedThisTick, Is.True);
            Assert.That(button.Held, Is.True);
        }

        [Test]
        public void PartialReplay_JsonRoundTrip_PreservesTerminalStatus()
        {
            ReplayRecord record = new ReplayRecord
            {
                isPartial = true,
                terminalStatus = "SearchLimit",
                failureTick = 15,
                keyframes = new[] { new ReplayKeyframe { tick = 15, position = Vector2.right } }
            };

            ReplayRecord copy = JsonUtility.FromJson<ReplayRecord>(JsonUtility.ToJson(record));

            Assert.That(copy.isPartial, Is.True);
            Assert.That(copy.terminalStatus, Is.EqualTo("SearchLimit"));
            Assert.That(copy.failureTick, Is.EqualTo(15));
            Assert.That(copy.keyframes, Has.Length.EqualTo(1));
        }

        [Test]
        public void RunReport_JsonRoundTrip_PreservesSolverCacheEvidence()
        {
            SimulationRunReport report = new SimulationRunReport
            {
                solverCacheHit = true,
                solverCacheKey = "fixture-cache-key",
                solverCacheStatus = "cache hit; clean-reset replay verified"
            };

            SimulationRunReport copy = JsonUtility.FromJson<SimulationRunReport>(JsonUtility.ToJson(report));

            Assert.That(copy.solverCacheHit, Is.True);
            Assert.That(copy.solverCacheKey, Is.EqualTo("fixture-cache-key"));
            Assert.That(copy.solverCacheStatus, Does.Contain("clean-reset replay verified"));
        }

        [Test]
        public void LocalPaths_StayOutsideAssets()
        {
            string directory = LocalDataPathService.CreateRunDirectory("path-safety-test");
            StringAssert.Contains(
                Path.Combine("Library", "Ryzi"),
                directory);
            StringAssert.DoesNotContain(
                Path.DirectorySeparatorChar + "Assets" + Path.DirectorySeparatorChar,
                directory);
            Assert.Throws<InvalidOperationException>(
                () => LocalDataPathService.Guard(Path.Combine(Application.dataPath, "RyziGenerated")));
        }

        [Test]
        public void TunableRestoration_RunsAfterExceptionAndCancellation()
        {
            FakeTunable tunable = new FakeTunable(13.5f);
            object original = tunable.CaptureOriginalValue();
            try
            {
                tunable.ApplyCandidate(8f);
                throw new OperationCanceledException();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                tunable.RestoreOriginal(original);
            }
            Assert.That(tunable.Value, Is.EqualTo(13.5f));
        }

        [Test]
        public void Scanner_RanksTaggedRigidbodyControllerAndKeepsDirtyState()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject player = new GameObject("Fixture Player");
            player.tag = "Player";
            player.AddComponent<Rigidbody2D>();
            player.AddComponent<BoxCollider2D>();
            player.AddComponent<FixtureMovementController>();
            bool dirtyBefore = scene.isDirty;

            try
            {
                SceneDiscoveryResult result = new ProjectScanner().ScanCurrentScene();
                Assert.That(result.SelectedPlayer, Is.Not.Null);
                Assert.That(result.SelectedPlayer.Value, Is.SameAs(player));
                Assert.That(result.SelectedPlayer.Confidence, Is.GreaterThan(0.7f));
                Assert.That(result.SelectedPlayer.Evidence.Count, Is.GreaterThanOrEqualTo(3));
                Assert.That(scene.isDirty, Is.EqualTo(dirtyBefore));
                Assert.That(result.Manifest.manifestVersion, Is.EqualTo(MechanicsManifest.CurrentVersion));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void OpeningWindow_DoesNotChangeSceneDirtyState()
        {
            Scene scene = SceneManager.GetActiveScene();
            bool dirtyBefore = scene.isDirty;
            RyziWindow window = ScriptableObject.CreateInstance<RyziWindow>();
            try
            {
                window.CreateGUI();
                Assert.That(scene.isDirty, Is.EqualTo(dirtyBefore));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        sealed class FakeTunable : IPlaytestTunable
        {
            public string Id => "fixture";
            public string DisplayName => "Fixture";
            public float Value { get; private set; }

            public FakeTunable(float value) => Value = value;
            public object CaptureOriginalValue() => Value;
            public void ApplyCandidate(object value) => Value = Convert.ToSingle(value);
            public void RestoreOriginal(object original) => Value = Convert.ToSingle(original);
        }
    }

    public sealed class FixtureMovementController : MonoBehaviour
    {
        public float movementSpeed = 5f;
        public float jumpVelocity = 10f;

        public void ApplyMovement() { }
        public void ReadInput() { }
        public void ResetEpisode() { }
        public void Respawn() { }
    }
}
