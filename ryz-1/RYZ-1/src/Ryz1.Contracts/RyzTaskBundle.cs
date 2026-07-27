using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ryz1.Contracts;

public static class RyzSchemaVersions
{
    public const string TaskBundle = "ryz-task-bundle/1.0";
    public const string MechanicsManifest = "mechanics-manifest/1.0";
    public const string Dataset = "ryz-search-dataset/1.0";
    public const string Replay = "ryz-replay/1.0";
    public const string UnitySnapshot = "ryz-unity-snapshot/1.0";
}

public sealed record MechanicsManifestDto
{
    public string SchemaVersion { get; init; } = RyzSchemaVersions.MechanicsManifest;
    public string ScenarioId { get; init; } = "unknown";
    public IReadOnlyList<ActionChannelDto> Actions { get; init; } = Array.Empty<ActionChannelDto>();
    public IReadOnlyList<MechanicDto> Mechanics { get; init; } = Array.Empty<MechanicDto>();
    public IReadOnlyList<MechanicParameterDto> Parameters { get; init; } = Array.Empty<MechanicParameterDto>();
    public IReadOnlyList<EvidenceDto> SourceEvidence { get; init; } = Array.Empty<EvidenceDto>();
    public IReadOnlyList<EvidenceDto> RuntimeEvidence { get; init; } = Array.Empty<EvidenceDto>();

    public string Fingerprint() => StableFingerprint(this);

    public static string StableFingerprint<T>(T value)
    {
        JsonSerializerOptions options = Json.Options;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record ActionChannelDto
{
    public string Id { get; init; } = "";
    public int Index { get; init; }
    public string ValueType { get; init; } = "button";
    public string SuggestedSemantic { get; init; } = "";
    public bool SupportsPressed { get; init; }
    public bool SupportsHeld { get; init; }
    public bool SupportsReleased { get; init; }
    public float Confidence { get; init; }
}

public sealed record MechanicDto
{
    public string Id { get; init; } = "";
    public string SuggestedName { get; init; } = "";
    public IReadOnlyList<int> ActionPattern { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> Preconditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Effects { get; init; } = Array.Empty<string>();
    public float StaticConfidence { get; init; }
    public float RuntimeConfidence { get; init; }
}

public sealed record MechanicParameterDto
{
    public string Id { get; init; } = "";
    public float Value { get; init; }
    public float Confidence { get; init; } = 1f;
}

public sealed record EvidenceDto
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Summary { get; init; } = "";
    public float Confidence { get; init; }
}

public sealed record RyzTaskSpecDto
{
    public string SchemaVersion { get; init; } = RyzSchemaVersions.TaskBundle;
    public string TaskId { get; init; } = "";
    public string ManifestFingerprint { get; init; } = "";
    public string LevelFingerprint { get; init; } = "";
    public int RandomizationSeed { get; init; }
    public int TrialCount { get; init; } = 4;
    public int MaxTicks { get; init; } = 1800;
    public float FixedDeltaTime { get; init; } = 0.02f;
    public string CurriculumArchetype { get; init; } = "custom";
    public int StageCount { get; init; } = 1;
    public IReadOnlyList<string> FeatureFlags { get; init; } = Array.Empty<string>();
    public ActionSchemaDto ActionSchema { get; init; } = new();
    public ObservationSchemaDto ObservationSchema { get; init; } = new();
    public MechanicsVectorDto MechanicsVector { get; init; } = new();
    public LevelSpecDto Level { get; init; } = new();
    public MovementParametersDto Movement { get; init; } = new();
    public RewardConfigDto Reward { get; init; } = new();
}

public sealed record ActionSchemaDto
{
    public IReadOnlyList<string> GenericChannels { get; init; } =
        new[] { "axis.move_x", "axis.move_y", "button.0", "button.1", "button.2" };

    public IReadOnlyList<MacroActionDto> Macros { get; init; } = MacroActionDto.DefaultVocabulary;
}

public sealed record MacroActionDto
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Ticks { get; init; }
    public float MoveX { get; init; }
    public float MoveY { get; init; }
    public bool Button0Pressed { get; init; }
    public bool Button0Held { get; init; }
    public bool Button1Pressed { get; init; }
    public bool Button2Held { get; init; }
    public bool IsValid { get; init; } = true;

    public static readonly MacroActionDto[] DefaultVocabulary =
    {
        new() { Id = 0, Name = "move_right_short", Ticks = 8, MoveX = 1 },
        new() { Id = 1, Name = "move_right_medium", Ticks = 16, MoveX = 1 },
        new() { Id = 2, Name = "move_left_short", Ticks = 8, MoveX = -1 },
        new() { Id = 3, Name = "jump_right", Ticks = 16, MoveX = 1, Button0Pressed = true, Button0Held = true },
        new() { Id = 4, Name = "jump_left", Ticks = 16, MoveX = -1, Button0Pressed = true, Button0Held = true },
        new() { Id = 5, Name = "jump_neutral", Ticks = 12, Button0Pressed = true, Button0Held = true },
        new() { Id = 6, Name = "dash_right", Ticks = 10, MoveX = 1, Button1Pressed = true },
        new() { Id = 7, Name = "dash_up_right", Ticks = 10, MoveX = 1, MoveY = 1, Button1Pressed = true },
        new() { Id = 8, Name = "wait", Ticks = 8 },
    };
}

public sealed record ObservationSchemaDto
{
    public int PlayerVectorSize { get; init; } = 16;
    public int GeometryChannels { get; init; } = 4;
    public int GeometryWidth { get; init; } = 16;
    public int GeometryHeight { get; init; } = 12;
    public int MaxEntities { get; init; } = 16;
    public int EntityVectorSize { get; init; } = 8;
}

public sealed record MechanicsVectorDto
{
    public int Size { get; init; } = 32;
    public float[] Values { get; init; } = new float[32];
}

public sealed record LevelSpecDto
{
    public Vec2 Spawn { get; init; } = new(-8f, -3.25f);
    public Rect2 Goal { get; init; } = new(32f, -2.5f, 1.5f, 3f);
    public IReadOnlyList<PlatformDto> Platforms { get; init; } = Array.Empty<PlatformDto>();
    public IReadOnlyList<Rect2> Hazards { get; init; } = Array.Empty<Rect2>();
    public IReadOnlyList<Vec2> Checkpoints { get; init; } = Array.Empty<Vec2>();
}

public sealed record PlatformDto
{
    public Rect2 Rect { get; init; }
    public string Kind { get; init; } = "solid";
}

public sealed record MovementParametersDto
{
    public float MovementSpeed { get; init; } = 8.5f;
    public float GroundAcceleration { get; init; } = 90f;
    public float GroundDeceleration { get; init; } = 110f;
    public float AirAcceleration { get; init; } = 65f;
    public float AirDeceleration { get; init; } = 45f;
    public float NormalGravity { get; init; } = 34f;
    public float FallGravity { get; init; } = 52f;
    public float JumpCutGravity { get; init; } = 70f;
    public float MaxFallSpeed { get; init; } = 24f;
    public float JumpVelocity { get; init; } = 13.5f;
    public float CoyoteTime { get; init; } = 0.11f;
    public float JumpBufferTime { get; init; } = 0.12f;
    public int MaxDashes { get; init; } = 1;
    public float DashSpeed { get; init; } = 18f;
    public float DashLength { get; init; } = 3.2f;
    public float DashEndSpeed { get; init; } = 5.5f;
    public float DashBufferTime { get; init; } = 0.12f;
    public float DashRefillCooldown { get; init; } = 0.04f;
}

public sealed record RewardConfigDto
{
    public float CompletionReward { get; init; } = 1f;
    public float DeathPenalty { get; init; } = -1f;
    public float ProgressScale { get; init; } = 0.01f;
    public float TickPenalty { get; init; } = -0.001f;
}

/// <summary>
/// Minimal Unity-authored scene snapshot accepted by the native GB10 runner.
/// Unity deliberately exports only the deterministic hackathon subset.
/// </summary>
public sealed record UnityTaskSnapshotDto
{
    public string SchemaVersion { get; init; } = RyzSchemaVersions.UnitySnapshot;
    public string TaskId { get; init; } = "";
    public int RandomizationSeed { get; init; }
    public int MaxTicks { get; init; } = 2400;
    public float FixedDeltaTime { get; init; } = 0.02f;
    public Vec2 Spawn { get; init; }
    public Rect2 Goal { get; init; }
    public IReadOnlyList<PlatformDto> Platforms { get; init; } = Array.Empty<PlatformDto>();
    public IReadOnlyList<Rect2> Hazards { get; init; } = Array.Empty<Rect2>();
    public IReadOnlyList<Vec2> Checkpoints { get; init; } = Array.Empty<Vec2>();
    public MovementParametersDto Movement { get; init; } = new();

    public void Validate()
    {
        if (SchemaVersion != RyzSchemaVersions.UnitySnapshot)
            throw new InvalidDataException($"Unsupported Unity snapshot schema '{SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(TaskId))
            throw new InvalidDataException("Unity snapshot TaskId is required.");
        if (Platforms.Count == 0)
            throw new InvalidDataException("Unity snapshot must contain at least one static platform.");
        if (FixedDeltaTime <= 0f)
            throw new InvalidDataException("Unity snapshot fixedDeltaTime must be positive.");
    }
}

public sealed record RyzTaskBundleDto
{
    public string SchemaVersion { get; init; } = RyzSchemaVersions.TaskBundle;
    public MechanicsManifestDto Manifest { get; init; } = new();
    public RyzTaskSpecDto Task { get; init; } = new();

    public void Validate()
    {
        if (SchemaVersion != RyzSchemaVersions.TaskBundle)
            throw new InvalidDataException($"Unsupported task bundle schema '{SchemaVersion}'.");
        if (Task.SchemaVersion != RyzSchemaVersions.TaskBundle)
            throw new InvalidDataException($"Unsupported task spec schema '{Task.SchemaVersion}'.");
        if (Manifest.SchemaVersion != RyzSchemaVersions.MechanicsManifest)
            throw new InvalidDataException($"Unsupported manifest schema '{Manifest.SchemaVersion}'.");
        string fingerprint = Manifest.Fingerprint();
        if (!string.Equals(Task.ManifestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException($"Manifest fingerprint mismatch. Expected {Task.ManifestFingerprint}, actual {fingerprint}.");
        if (string.IsNullOrWhiteSpace(Task.TaskId))
            throw new InvalidDataException("TaskId is required.");
        if (Task.Level.Platforms.Count == 0)
            throw new InvalidDataException("Task level must include at least one platform.");
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"Could not deserialize {typeof(T).Name} from {path}.");
    }

    public static void Write<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using FileStream stream = File.Create(path);
        JsonSerializer.Serialize(stream, value, Options);
    }
}
