namespace Ryz1.Contracts;

public readonly record struct RyzAction(
    float MoveX,
    float MoveY,
    bool Button0Pressed,
    bool Button0Held,
    bool Button1Pressed,
    bool Button2Held)
{
    public static readonly RyzAction Neutral = new(0f, 0f, false, false, false, false);
}

public sealed record RyzObservationDto
{
    public int Tick { get; init; }
    public Vec2 Position { get; init; }
    public Vec2 Velocity { get; init; }
    public bool IsGrounded { get; init; }
    public bool OnLeftWall { get; init; }
    public bool OnRightWall { get; init; }
    public bool IsDashing { get; init; }
    public int DashesRemaining { get; init; }
    public float Progress { get; init; }
    public bool IsDead { get; init; }
    public bool IsComplete { get; init; }
    public float[] PlayerVector { get; init; } = Array.Empty<float>();
}

public sealed record ReplayRecordDto
{
    public string SchemaVersion { get; init; } = RyzSchemaVersions.Replay;
    public string TaskId { get; init; } = "";
    public string ManifestFingerprint { get; init; } = "";
    public IReadOnlyList<int> MacroIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<RyzObservationDto> Keyframes { get; init; } = Array.Empty<RyzObservationDto>();
    public bool Completed { get; init; }
    public bool Verified { get; init; }
    public string Diagnostic { get; init; } = "";
}

public sealed record DatasetTransitionDto
{
    public string TaskId { get; init; } = "";
    public int TrialId { get; init; }
    public int NodeId { get; init; }
    public int ParentId { get; init; }
    public int SearchDepth { get; init; }
    public int MacroId { get; init; }
    public float Reward { get; init; }
    public float Progress { get; init; }
    public bool Death { get; init; }
    public bool Completion { get; init; }
    public bool TeacherSelected { get; init; }
    public bool SurvivedPruning { get; init; }
    public bool EventuallyCompleted { get; init; }
    public float CandidateScore { get; init; }
    public float[] MechanicsVector { get; init; } = new float[32];
    public RyzObservationDto Observation { get; init; } = new();
    public RyzObservationDto NextObservation { get; init; } = new();
}

public sealed record DatasetFileDto
{
    public string SchemaVersion { get; init; } = RyzSchemaVersions.Dataset;
    public string DatasetId { get; init; } = "";
    public string Split { get; init; } = "train";
    public IReadOnlyList<string> TaskIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DatasetTransitionDto> Transitions { get; init; } = Array.Empty<DatasetTransitionDto>();
}
