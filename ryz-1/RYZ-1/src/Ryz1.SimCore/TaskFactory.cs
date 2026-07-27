using Ryz1.Contracts;

namespace Ryz1.SimCore;

[Flags]
public enum CurriculumFeatures
{
    None = 0,
    Jump = 1 << 0,
    Dash = 1 << 1,
    Hazards = 1 << 2,
    Elevation = 1 << 3,
    Checkpoints = 1 << 4,
    VariablePhysics = 1 << 5,
}

public sealed record CurriculumLevelDefinition(
    string Archetype,
    CurriculumFeatures Features,
    int StageCount = 4);

public static class TaskFactory
{
    public static readonly CurriculumLevelDefinition[] DefaultCurriculum =
    {
        new("flat-run", CurriculumFeatures.VariablePhysics, 3),
        new("jump-gaps", CurriculumFeatures.Jump | CurriculumFeatures.Checkpoints | CurriculumFeatures.VariablePhysics, 4),
        new("hazard-hops", CurriculumFeatures.Jump | CurriculumFeatures.Hazards | CurriculumFeatures.Checkpoints | CurriculumFeatures.VariablePhysics, 4),
        new("elevation", CurriculumFeatures.Jump | CurriculumFeatures.Elevation | CurriculumFeatures.Checkpoints | CurriculumFeatures.VariablePhysics, 4),
        new("dash-gaps", CurriculumFeatures.Jump | CurriculumFeatures.Dash | CurriculumFeatures.Checkpoints | CurriculumFeatures.VariablePhysics, 4),
        new("mixed-course", CurriculumFeatures.Jump | CurriculumFeatures.Dash | CurriculumFeatures.Hazards | CurriculumFeatures.Elevation | CurriculumFeatures.Checkpoints | CurriculumFeatures.VariablePhysics, 4),
    };

    public static RyzTaskBundleDto CreateDemoBundle(int seed = 0)
    {
        Random random = new(seed);
        float gravity = 30f + random.NextSingle() * 10f;
        float jump = 12f + random.NextSingle() * 3f;
        float speed = 7.5f + random.NextSingle() * 2f;
        var manifest = new MechanicsManifestDto
        {
            ScenarioId = $"simcore-demo-{seed}",
            Actions = new[]
            {
                new ActionChannelDto { Id = "axis.move_x", Index = 0, ValueType = "axis", SuggestedSemantic = "horizontal", Confidence = 1f },
                new ActionChannelDto { Id = "axis.move_y", Index = 1, ValueType = "axis", SuggestedSemantic = "vertical", Confidence = 1f },
                new ActionChannelDto { Id = "button.0", Index = 2, ValueType = "button", SuggestedSemantic = "jump", SupportsPressed = true, SupportsHeld = true, SupportsReleased = true, Confidence = 0.95f },
                new ActionChannelDto { Id = "button.1", Index = 3, ValueType = "button", SuggestedSemantic = "dash", SupportsPressed = true, Confidence = 0.9f },
            },
            Mechanics = new[]
            {
                new MechanicDto { Id = "movement.run", SuggestedName = "run", ActionPattern = new[] { 0 }, Effects = new[] { "velocity.x approaches target speed" }, StaticConfidence = 1f, RuntimeConfidence = 1f },
                new MechanicDto { Id = "movement.jump", SuggestedName = "jump", ActionPattern = new[] { 2 }, Preconditions = new[] { "grounded or coyote" }, Effects = new[] { "positive y velocity" }, StaticConfidence = 0.95f, RuntimeConfidence = 0.9f },
                new MechanicDto { Id = "movement.dash", SuggestedName = "dash", ActionPattern = new[] { 3 }, Preconditions = new[] { "dash resource > 0" }, Effects = new[] { "fixed dash velocity" }, StaticConfidence = 0.9f, RuntimeConfidence = 0.85f },
            },
            Parameters = new[]
            {
                new MechanicParameterDto { Id = "movement.speed", Value = speed },
                new MechanicParameterDto { Id = "gravity.normal", Value = gravity },
                new MechanicParameterDto { Id = "jump.velocity", Value = jump },
            },
        };
        string manifestFingerprint = manifest.Fingerprint();
        var level = new LevelSpecDto
        {
            Spawn = new Vec2(-8f, -2.2f),
            Goal = new Rect2(30f, -2f, 2f, 3f),
            Platforms = new[]
            {
                new PlatformDto { Rect = new Rect2(-12f, -4f, 18f, 1f) },
                new PlatformDto { Rect = new Rect2(8f, -2.7f, 7f, 1f) },
                new PlatformDto { Rect = new Rect2(18f, -1.8f, 6f, 1f) },
                new PlatformDto { Rect = new Rect2(29f, -3.2f, 6f, 1f) },
            },
            Hazards = new[] { new Rect2(6f, -4f, 2f, 1f), new Rect2(15.5f, -4f, 2f, 1f) },
            Checkpoints = new[] { new Vec2(8f, -1.5f), new Vec2(18f, -0.6f) },
        };
        string levelFingerprint = MechanicsManifestDto.StableFingerprint(level);
        var mechanicsVector = new float[32];
        mechanicsVector[0] = speed / 10f;
        mechanicsVector[1] = gravity / 60f;
        mechanicsVector[2] = jump / 20f;
        mechanicsVector[3] = 1f;
        var task = new RyzTaskSpecDto
        {
            TaskId = $"simcore-demo-{seed}",
            ManifestFingerprint = manifestFingerprint,
            LevelFingerprint = levelFingerprint,
            RandomizationSeed = seed,
            MechanicsVector = new MechanicsVectorDto { Size = 32, Values = mechanicsVector },
            Level = level,
            Movement = new MovementParametersDto { MovementSpeed = speed, NormalGravity = gravity, JumpVelocity = jump },
        };
        return new RyzTaskBundleDto { Manifest = manifest, Task = task };
    }

    public static RyzTaskBundleDto CreateUnitySnapshotBundle(UnityTaskSnapshotDto snapshot)
    {
        snapshot.Validate();
        bool jumpEnabled = snapshot.Movement.JumpVelocity > 0f;
        bool dashEnabled = snapshot.Movement.MaxDashes > 0 && snapshot.Movement.DashSpeed > 0f;
        bool hazardsEnabled = snapshot.Hazards.Count > 0;
        bool elevationEnabled = snapshot.Platforms
            .Select(platform => platform.Rect.Y)
            .Distinct()
            .Skip(1)
            .Any();
        bool checkpointsEnabled = snapshot.Checkpoints.Count > 0;

        var actions = new List<ActionChannelDto>
        {
            new() { Id = "axis.move_x", Index = 0, ValueType = "axis", SuggestedSemantic = "horizontal", Confidence = 1f },
            new() { Id = "axis.move_y", Index = 1, ValueType = "axis", SuggestedSemantic = "vertical", Confidence = 1f },
        };
        if (jumpEnabled)
            actions.Add(new ActionChannelDto { Id = "button.0", Index = 2, ValueType = "button", SuggestedSemantic = "jump", SupportsPressed = true, SupportsHeld = true, SupportsReleased = true, Confidence = 1f });
        if (dashEnabled)
            actions.Add(new ActionChannelDto { Id = "button.1", Index = 3, ValueType = "button", SuggestedSemantic = "dash", SupportsPressed = true, Confidence = 1f });

        var mechanics = new List<MechanicDto>
        {
            new() { Id = "movement.run", SuggestedName = "run", ActionPattern = new[] { 0 }, Effects = new[] { "horizontal movement" }, StaticConfidence = 1f, RuntimeConfidence = 1f },
        };
        if (jumpEnabled)
            mechanics.Add(new MechanicDto { Id = "movement.jump", SuggestedName = "jump", ActionPattern = new[] { 2 }, Effects = new[] { "positive y velocity" }, StaticConfidence = 1f, RuntimeConfidence = 1f });
        if (dashEnabled)
            mechanics.Add(new MechanicDto { Id = "movement.dash", SuggestedName = "dash", ActionPattern = new[] { 3 }, Effects = new[] { "fixed dash velocity" }, StaticConfidence = 1f, RuntimeConfidence = 1f });

        var manifest = new MechanicsManifestDto
        {
            ScenarioId = snapshot.TaskId,
            Actions = actions,
            Mechanics = mechanics,
            Parameters = new[]
            {
                new MechanicParameterDto { Id = "movement.speed", Value = snapshot.Movement.MovementSpeed },
                new MechanicParameterDto { Id = "gravity.normal", Value = snapshot.Movement.NormalGravity },
                new MechanicParameterDto { Id = "jump.velocity", Value = snapshot.Movement.JumpVelocity },
                new MechanicParameterDto { Id = "dash.speed", Value = snapshot.Movement.DashSpeed },
            },
            RuntimeEvidence = new[]
            {
                new EvidenceDto
                {
                    Id = "unity-snapshot",
                    Source = "Unity isolated physics arena",
                    Summary = "Static collider geometry and player movement parameters exported by the Unity bridge.",
                    Confidence = 1f,
                },
            },
        };
        float[] mechanicsVector = BuildMechanicsVector(
            snapshot.Movement.MovementSpeed,
            snapshot.Movement.NormalGravity,
            snapshot.Movement.JumpVelocity,
            snapshot.Movement.DashSpeed,
            Math.Max(1, snapshot.Checkpoints.Count + 1),
            jumpEnabled,
            dashEnabled,
            hazardsEnabled,
            elevationEnabled,
            checkpointsEnabled);
        MacroActionDto[] macros = MacroActionDto.DefaultVocabulary
            .Select(macro => macro with
            {
                IsValid = macro.IsValid
                    && (jumpEnabled || macro.Id is not (3 or 4 or 5))
                    && (dashEnabled || macro.Id is not (6 or 7)),
            })
            .ToArray();
        var level = new LevelSpecDto
        {
            Spawn = snapshot.Spawn,
            Goal = snapshot.Goal,
            Platforms = snapshot.Platforms,
            Hazards = snapshot.Hazards,
            Checkpoints = snapshot.Checkpoints,
        };
        var task = new RyzTaskSpecDto
        {
            TaskId = snapshot.TaskId,
            ManifestFingerprint = manifest.Fingerprint(),
            LevelFingerprint = MechanicsManifestDto.StableFingerprint(level),
            RandomizationSeed = snapshot.RandomizationSeed,
            TrialCount = 1,
            MaxTicks = snapshot.MaxTicks,
            FixedDeltaTime = snapshot.FixedDeltaTime,
            CurriculumArchetype = "unity-export",
            StageCount = Math.Max(1, snapshot.Checkpoints.Count + 1),
            FeatureFlags = new[]
            {
                jumpEnabled ? "jump" : "",
                dashEnabled ? "dash" : "",
                hazardsEnabled ? "hazards" : "",
                elevationEnabled ? "elevation" : "",
                checkpointsEnabled ? "checkpoints" : "",
            }.Where(value => value.Length > 0).ToArray(),
            ActionSchema = new ActionSchemaDto { Macros = macros },
            MechanicsVector = new MechanicsVectorDto { Size = mechanicsVector.Length, Values = mechanicsVector },
            Level = level,
            Movement = snapshot.Movement,
        };
        return new RyzTaskBundleDto { Manifest = manifest, Task = task };
    }

    public static IReadOnlyList<RyzTaskBundleDto> CreateCurriculumBundles(int baseSeed, int repetitions = 1)
    {
        if (repetitions <= 0)
            throw new ArgumentOutOfRangeException(nameof(repetitions), "Curriculum repetitions must be positive.");

        var bundles = new List<RyzTaskBundleDto>(DefaultCurriculum.Length * repetitions);
        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            for (int profileIndex = 0; profileIndex < DefaultCurriculum.Length; profileIndex++)
            {
                int seed = checked(baseSeed + repetition * 1000 + profileIndex);
                bundles.Add(CreateCurriculumBundle(DefaultCurriculum[profileIndex], seed));
            }
        }
        return bundles;
    }

    public static RyzTaskBundleDto CreateCurriculumBundle(CurriculumLevelDefinition definition, int seed)
    {
        Random random = new(seed);
        bool jumpEnabled = definition.Features.HasFlag(CurriculumFeatures.Jump);
        bool dashEnabled = definition.Features.HasFlag(CurriculumFeatures.Dash);
        bool hazardsEnabled = definition.Features.HasFlag(CurriculumFeatures.Hazards);
        bool elevationEnabled = definition.Features.HasFlag(CurriculumFeatures.Elevation);
        bool checkpointsEnabled = definition.Features.HasFlag(CurriculumFeatures.Checkpoints);

        float speed = 7.6f + random.NextSingle() * 1.8f;
        float gravity = 31f + random.NextSingle() * 7f;
        float jump = 12.8f + random.NextSingle() * 1.8f;
        float dashSpeed = 17.5f + random.NextSingle() * 2f;

        var actions = new List<ActionChannelDto>
        {
            new() { Id = "axis.move_x", Index = 0, ValueType = "axis", SuggestedSemantic = "horizontal", Confidence = 1f },
            new() { Id = "axis.move_y", Index = 1, ValueType = "axis", SuggestedSemantic = "vertical", Confidence = 1f },
        };
        if (jumpEnabled)
            actions.Add(new ActionChannelDto { Id = "button.0", Index = 2, ValueType = "button", SuggestedSemantic = "jump", SupportsPressed = true, SupportsHeld = true, SupportsReleased = true, Confidence = 0.95f });
        if (dashEnabled)
            actions.Add(new ActionChannelDto { Id = "button.1", Index = 3, ValueType = "button", SuggestedSemantic = "dash", SupportsPressed = true, Confidence = 0.9f });

        var mechanics = new List<MechanicDto>
        {
            new() { Id = "movement.run", SuggestedName = "run", ActionPattern = new[] { 0 }, Effects = new[] { "velocity.x approaches target speed" }, StaticConfidence = 1f, RuntimeConfidence = 1f },
        };
        if (jumpEnabled)
            mechanics.Add(new MechanicDto { Id = "movement.jump", SuggestedName = "jump", ActionPattern = new[] { 2 }, Preconditions = new[] { "grounded or coyote" }, Effects = new[] { "positive y velocity" }, StaticConfidence = 0.95f, RuntimeConfidence = 0.9f });
        if (dashEnabled)
            mechanics.Add(new MechanicDto { Id = "movement.dash", SuggestedName = "dash", ActionPattern = new[] { 3 }, Preconditions = new[] { "dash resource > 0" }, Effects = new[] { "fixed dash velocity" }, StaticConfidence = 0.9f, RuntimeConfidence = 0.85f });

        var manifest = new MechanicsManifestDto
        {
            ScenarioId = $"curriculum-{definition.Archetype}-{seed}",
            Actions = actions,
            Mechanics = mechanics,
            Parameters = new[]
            {
                new MechanicParameterDto { Id = "movement.speed", Value = speed },
                new MechanicParameterDto { Id = "gravity.normal", Value = gravity },
                new MechanicParameterDto { Id = "jump.velocity", Value = jumpEnabled ? jump : 0f },
                new MechanicParameterDto { Id = "dash.speed", Value = dashEnabled ? dashSpeed : 0f },
                new MechanicParameterDto { Id = "level.stage_count", Value = definition.StageCount },
            },
        };

        LevelSpecDto level = BuildCurriculumLevel(definition, hazardsEnabled, elevationEnabled, checkpointsEnabled);
        string manifestFingerprint = manifest.Fingerprint();
        string levelFingerprint = MechanicsManifestDto.StableFingerprint(level);
        float[] mechanicsVector = BuildMechanicsVector(
            speed,
            gravity,
            jump,
            dashSpeed,
            definition.StageCount,
            jumpEnabled,
            dashEnabled,
            hazardsEnabled,
            elevationEnabled,
            checkpointsEnabled);
        MacroActionDto[] macros = MacroActionDto.DefaultVocabulary
            .Select(macro => macro with
            {
                IsValid = macro.IsValid
                    && (jumpEnabled || macro.Id is not (3 or 4 or 5))
                    && (dashEnabled || macro.Id is not (6 or 7)),
            })
            .ToArray();
        string[] featureFlags = Enum.GetValues<CurriculumFeatures>()
            .Where(feature => feature is not CurriculumFeatures.None && definition.Features.HasFlag(feature))
            .Select(feature => feature.ToString().ToLowerInvariant())
            .ToArray();
        var task = new RyzTaskSpecDto
        {
            TaskId = $"curriculum-{definition.Archetype}-{seed}",
            ManifestFingerprint = manifestFingerprint,
            LevelFingerprint = levelFingerprint,
            RandomizationSeed = seed,
            TrialCount = 1,
            MaxTicks = 2400,
            CurriculumArchetype = definition.Archetype,
            StageCount = definition.StageCount,
            FeatureFlags = featureFlags,
            ActionSchema = new ActionSchemaDto { Macros = macros },
            MechanicsVector = new MechanicsVectorDto { Size = mechanicsVector.Length, Values = mechanicsVector },
            Level = level,
            Movement = new MovementParametersDto
            {
                MovementSpeed = speed,
                NormalGravity = gravity,
                JumpVelocity = jumpEnabled ? jump : 0f,
                MaxDashes = dashEnabled ? 1 : 0,
                DashSpeed = dashSpeed,
            },
        };
        return new RyzTaskBundleDto { Manifest = manifest, Task = task };
    }

    static float[] BuildMechanicsVector(
        float speed,
        float gravity,
        float jump,
        float dashSpeed,
        int stageCount,
        bool jumpEnabled,
        bool dashEnabled,
        bool hazardsEnabled,
        bool elevationEnabled,
        bool checkpointsEnabled)
    {
        var vector = new float[32];
        vector[0] = speed / 10f;
        vector[1] = gravity / 60f;
        vector[2] = jumpEnabled ? jump / 20f : 0f;
        vector[3] = jumpEnabled ? 1f : 0f;
        vector[4] = dashEnabled ? 1f : 0f;
        vector[5] = hazardsEnabled ? 1f : 0f;
        vector[6] = elevationEnabled ? 1f : 0f;
        vector[7] = checkpointsEnabled ? 1f : 0f;
        vector[8] = stageCount / 8f;
        vector[9] = dashEnabled ? dashSpeed / 25f : 0f;
        return vector;
    }

    static LevelSpecDto BuildCurriculumLevel(
        CurriculumLevelDefinition definition,
        bool hazardsEnabled,
        bool elevationEnabled,
        bool checkpointsEnabled)
    {
        if (definition.Archetype == "flat-run")
        {
            return new LevelSpecDto
            {
                Spawn = new Vec2(-8f, -2.2f),
                Goal = new Rect2(30f, -2f, 2f, 3f),
                Platforms = new[] { new PlatformDto { Rect = new Rect2(-12f, -4f, 48f, 1f) } },
            };
        }

        if (definition.Archetype == "hazard-hops")
        {
            return new LevelSpecDto
            {
                Spawn = new Vec2(-8f, -2.2f),
                Goal = new Rect2(30f, -2f, 2f, 3f),
                Platforms = new[] { new PlatformDto { Rect = new Rect2(-12f, -4f, 48f, 1f) } },
                Hazards = hazardsEnabled
                    ? new[] { new Rect2(3.5f, -4f, 1.4f, 1f), new Rect2(13f, -4f, 1.8f, 1f), new Rect2(23f, -4f, 1.5f, 1f) }
                    : Array.Empty<Rect2>(),
                Checkpoints = checkpointsEnabled
                    ? new[] { new Vec2(7f, -2.2f), new Vec2(18f, -2.2f), new Vec2(27f, -2.2f) }
                    : Array.Empty<Vec2>(),
            };
        }

        float secondY = elevationEnabled ? -2.7f : -4f;
        float thirdY = elevationEnabled ? -1.8f : -4f;
        float fourthY = elevationEnabled ? -3.2f : -4f;
        bool mixedSizedGaps = definition.Archetype == "mixed-course";
        bool dashSizedGaps = definition.Archetype == "dash-gaps";
        float secondX = mixedSizedGaps ? 8f : dashSizedGaps ? 7.8f : 7.2f;
        float secondWidth = mixedSizedGaps ? 7f : dashSizedGaps ? 7.4f : 8f;
        float thirdX = mixedSizedGaps ? 18f : dashSizedGaps ? 17.2f : 16.5f;
        float thirdWidth = mixedSizedGaps ? 6f : dashSizedGaps ? 7f : 8f;
        float fourthX = mixedSizedGaps ? 29f : dashSizedGaps ? 27.2f : 26f;
        float fourthWidth = mixedSizedGaps ? 6f : dashSizedGaps ? 7.8f : 9f;
        return new LevelSpecDto
        {
            Spawn = new Vec2(-8f, -2.2f),
            Goal = new Rect2(30f, -2f, 2f, 3f),
            Platforms = new[]
            {
                new PlatformDto { Rect = new Rect2(-12f, -4f, 18f, 1f) },
                new PlatformDto { Rect = new Rect2(secondX, secondY, secondWidth, 1f) },
                new PlatformDto { Rect = new Rect2(thirdX, thirdY, thirdWidth, 1f) },
                new PlatformDto { Rect = new Rect2(fourthX, fourthY, fourthWidth, 1f) },
            },
            Hazards = hazardsEnabled
                ? new[] { new Rect2(6f, -4f, 2f, 1f), new Rect2(15.5f, -4f, 2f, 1f), new Rect2(24.5f, -4f, 4f, 1f) }
                : Array.Empty<Rect2>(),
            Checkpoints = checkpointsEnabled
                ? new[] { new Vec2(secondX, secondY + 1.2f), new Vec2(thirdX, thirdY + 1.2f), new Vec2(fourthX, fourthY + 1.2f) }
                : Array.Empty<Vec2>(),
        };
    }
}
