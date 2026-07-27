using NUnit.Framework;
using Ryz1.Contracts;
using Ryz1.SimCore;

namespace Ryz1.SimCore.Tests;

public sealed class SimCoreTests
{
    [Test]
    public void DemoBundle_ValidatesAndFingerprintIsStable()
    {
        RyzTaskBundleDto a = TaskFactory.CreateDemoBundle(7);
        RyzTaskBundleDto b = TaskFactory.CreateDemoBundle(7);

        Assert.DoesNotThrow(a.Validate);
        Assert.That(a.Task.ManifestFingerprint, Is.EqualTo(b.Task.ManifestFingerprint));
        Assert.That(a.Task.LevelFingerprint, Is.EqualTo(b.Task.LevelFingerprint));
    }

    [Test]
    public void TrialMemoryBoundary_IsRepresentedByTaskAndTrialIdsInDataset()
    {
        RyzTaskBundleDto bundle = TaskFactory.CreateDemoBundle(1);
        SimSolveResult result = new SimBeamSearch().Solve(bundle, new SimSearchConfig { BeamWidth = 2, MaxDepth = 1 }, trialId: 3);

        Assert.That(result.Dataset.Transitions, Is.Not.Empty);
        Assert.That(result.Dataset.Transitions.All(t => t.TaskId == bundle.Task.TaskId), Is.True);
        Assert.That(result.Dataset.Transitions.All(t => t.TrialId == 3), Is.True);
    }

    [Test]
    public void ReplayVerification_RejectsEmptyIncompletePlan()
    {
        RyzTaskBundleDto bundle = TaskFactory.CreateDemoBundle(0);
        ReplayRecordDto replay = SimBeamSearch.Verify(bundle, Array.Empty<int>());

        Assert.That(replay.Verified, Is.False);
        Assert.That(replay.Completed, Is.False);
    }

    [Test]
    public void DeterministicTick_SameActionsProduceSameObservation()
    {
        RyzTaskBundleDto bundle = TaskFactory.CreateDemoBundle(0);
        var a = new PlatformerSim(bundle.Task);
        var b = new PlatformerSim(bundle.Task);
        var action = new RyzAction(1f, 0f, false, false, false, false);

        for (int i = 0; i < 30; i++)
        {
            a.Tick(action);
            b.Tick(action);
        }

        Assert.That(a.Observe().Position, Is.EqualTo(b.Observe().Position));
        Assert.That(a.Observe().Velocity, Is.EqualTo(b.Observe().Velocity));
    }

    [Test]
    public void Curriculum_IsDeterministicAndCoversFeatureCombinations()
    {
        IReadOnlyList<RyzTaskBundleDto> a = TaskFactory.CreateCurriculumBundles(100, repetitions: 2);
        IReadOnlyList<RyzTaskBundleDto> b = TaskFactory.CreateCurriculumBundles(100, repetitions: 2);

        Assert.That(a, Has.Count.EqualTo(TaskFactory.DefaultCurriculum.Length * 2));
        Assert.That(a.Select(x => x.Task.LevelFingerprint), Is.EqualTo(b.Select(x => x.Task.LevelFingerprint)));
        Assert.That(a.Any(x => x.Task.FeatureFlags.Contains("dash")), Is.True);
        Assert.That(a.Any(x => x.Task.FeatureFlags.Contains("hazards")), Is.True);
        Assert.That(a.Any(x => x.Task.FeatureFlags.Contains("elevation")), Is.True);
        Assert.That(a.Any(x => !x.Task.FeatureFlags.Contains("jump")), Is.True);
        Assert.That(a.All(x => x.Task.StageCount >= 3), Is.True);
        Assert.That(a.All(x =>
        {
            x.Validate();
            return true;
        }), Is.True);
    }

    [Test]
    public void Curriculum_FeatureTogglesDisableUnavailableMacros()
    {
        RyzTaskBundleDto runOnly = TaskFactory.CreateCurriculumBundle(
            TaskFactory.DefaultCurriculum.Single(profile => profile.Archetype == "flat-run"),
            seed: 5);
        RyzTaskBundleDto mixed = TaskFactory.CreateCurriculumBundle(
            TaskFactory.DefaultCurriculum.Single(profile => profile.Archetype == "mixed-course"),
            seed: 5);

        Assert.That(runOnly.Task.ActionSchema.Macros.Single(m => m.Id == 3).IsValid, Is.False);
        Assert.That(runOnly.Task.ActionSchema.Macros.Single(m => m.Id == 6).IsValid, Is.False);
        Assert.That(mixed.Task.ActionSchema.Macros.Single(m => m.Id == 3).IsValid, Is.True);
        Assert.That(mixed.Task.ActionSchema.Macros.Single(m => m.Id == 6).IsValid, Is.True);
        Assert.That(mixed.Task.MechanicsVector.Values[4], Is.EqualTo(1f));
        Assert.That(mixed.Task.MechanicsVector.Values[5], Is.EqualTo(1f));
    }

    [Test]
    public void SearchDataset_CarriesTaskMechanicsVector()
    {
        RyzTaskBundleDto bundle = TaskFactory.CreateCurriculumBundle(
            TaskFactory.DefaultCurriculum.Single(profile => profile.Archetype == "mixed-course"),
            seed: 7);
        SimSolveResult result = new SimBeamSearch().Solve(
            bundle,
            new SimSearchConfig { BeamWidth = 2, MaxDepth = 1 });

        Assert.That(result.Dataset.Transitions, Is.Not.Empty);
        Assert.That(result.Dataset.Transitions.All(transition =>
            transition.MechanicsVector.SequenceEqual(bundle.Task.MechanicsVector.Values)), Is.True);
    }

    [Test]
    public void GroundedRun_IsNotBlockedByFloorContact()
    {
        RyzTaskBundleDto bundle = TaskFactory.CreateCurriculumBundle(
            TaskFactory.DefaultCurriculum.Single(profile => profile.Archetype == "flat-run"),
            seed: 11);
        var sim = new PlatformerSim(bundle.Task);
        float startX = sim.Observe().Position.X;

        for (int tick = 0; tick < 30; tick++)
            sim.Tick(new RyzAction(1f, 0f, false, false, false, false));

        Assert.That(sim.Observe().Position.X, Is.GreaterThan(startX + 1f));
        Assert.That(sim.Observe().IsDead, Is.False);
    }

    [Test]
    public void UnitySnapshot_ConvertsToValidatedNativeBundle()
    {
        var snapshot = new UnityTaskSnapshotDto
        {
            TaskId = "unity-fixture",
            Spawn = new Vec2(0f, 1f),
            Goal = new Rect2(9f, 0f, 2f, 3f),
            Platforms = new[]
            {
                new PlatformDto { Rect = new Rect2(-2f, -0.5f, 14f, 1f) },
            },
            Movement = new MovementParametersDto(),
        };

        RyzTaskBundleDto bundle = TaskFactory.CreateUnitySnapshotBundle(snapshot);

        Assert.DoesNotThrow(bundle.Validate);
        Assert.That(bundle.Task.CurriculumArchetype, Is.EqualTo("unity-export"));
        Assert.That(bundle.Task.Level.Platforms, Has.Count.EqualTo(1));
        Assert.That(bundle.Task.FeatureFlags, Does.Contain("jump"));
    }

    [Test]
    public void NeuralGuide_CannotRemoveDeterministicBaselineCandidate()
    {
        RyzTaskBundleDto bundle = TaskFactory.CreateCurriculumBundle(
            TaskFactory.DefaultCurriculum.Single(profile => profile.Archetype == "flat-run"),
            seed: 91);
        SimSearchConfig config = new()
        {
            BeamWidth = 1,
            MaxDepth = 50,
            NeuralPolicyWeight = 100f,
        };

        SimSolveResult baseline = new SimBeamSearch().Solve(bundle, config);
        SimSolveResult adversarial = new SimBeamSearch().Solve(bundle, config, new PreferLeftGuide());

        Assert.That(baseline.Replay.Verified, Is.True);
        Assert.That(adversarial.Replay.Verified, Is.True);
        Assert.That(adversarial.MacroIds, Is.EqualTo(baseline.MacroIds));
    }

    sealed class PreferLeftGuide : INeuralGuide
    {
        public NeuralGuideOutput Evaluate(IReadOnlyList<NeuralGuideStep> sequence, int trialId)
        {
            float[] logits = new float[9];
            logits[2] = 100f;
            return new NeuralGuideOutput(logits, 1f);
        }
    }
}
