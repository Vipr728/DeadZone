using Ryz1.Contracts;

namespace Ryz1.SimCore;

public sealed class PlatformerSim
{
    const float PlayerWidth = 0.74f;
    const float PlayerHeight = 1.6f;
    const float Skin = 0.001f;

    readonly RyzTaskSpecDto task;
    readonly List<PlatformDto> solids;
    readonly List<Rect2> hazards;
    SimState state;

    public PlatformerSim(RyzTaskSpecDto task)
    {
        this.task = task;
        solids = task.Level.Platforms.Where(p => p.Kind is "solid" or "one-way").ToList();
        hazards = task.Level.Hazards.ToList();
        Reset();
    }

    public SimState State => state;

    public void Reset()
    {
        state = new SimState
        {
            Tick = 0,
            Position = task.Level.Spawn,
            Velocity = Vec2.Zero,
            DashesRemaining = task.Movement.MaxDashes,
            Facing = 1,
            CoyoteTimer = task.Movement.CoyoteTime,
            JumpBufferTimer = 0f,
            DashBufferTimer = 0f,
            DashCooldownTimer = 0f,
            DashTimer = 0f,
            IsDead = false,
            IsComplete = false,
        };
        RefreshContacts();
    }

    public RyzObservationDto Observe() => ToObservation();

    public RyzObservationDto Tick(RyzAction action)
    {
        if (state.IsComplete || state.IsDead)
            return ToObservation();

        float dt = task.FixedDeltaTime;
        Vec2 move = DeterministicMath.ClampMagnitude(new Vec2(
            DeterministicMath.Clamp(action.MoveX, -1f, 1f),
            DeterministicMath.Clamp(action.MoveY, -1f, 1f)), 1f);
        if (MathF.Abs(move.X) > 0.1f)
            state.Facing = move.X > 0f ? 1 : -1;

        state.JumpHeld = action.Button0Held;
        state.JumpBufferTimer = action.Button0Pressed
            ? task.Movement.JumpBufferTime
            : state.JumpBufferTimer - dt;
        state.DashBufferTimer = action.Button1Pressed
            ? task.Movement.DashBufferTime
            : state.DashBufferTimer - dt;

        RefreshContacts();
        state.CoyoteTimer = state.IsGrounded ? task.Movement.CoyoteTime : state.CoyoteTimer - dt;
        state.DashCooldownTimer -= dt;

        if (state.IsGrounded)
            state.DashesRemaining = task.Movement.MaxDashes;

        if (state.DashBufferTimer > 0f && state.DashesRemaining > 0 && state.DashCooldownTimer <= 0f)
            StartDash(move);

        if (state.IsDashing)
            UpdateDash(dt);
        else
            UpdateMovement(move, dt);

        Integrate(dt);
        state.Tick++;
        RefreshContacts();
        CheckTerminal();
        return ToObservation();
    }

    void UpdateMovement(Vec2 move, float dt)
    {
        Vec2 velocity = state.Velocity;
        TryApplyJump(ref velocity);
        ApplyGravity(ref velocity, dt);

        float target = move.X * task.Movement.MovementSpeed;
        float acceleration = MathF.Abs(target) > 0.01f
            ? (state.IsGrounded ? task.Movement.GroundAcceleration : task.Movement.AirAcceleration)
            : (state.IsGrounded ? task.Movement.GroundDeceleration : task.Movement.AirDeceleration);
        velocity = velocity with
        {
            X = DeterministicMath.MoveTowards(velocity.X, target, acceleration * dt)
        };
        state.Velocity = velocity;
    }

    void TryApplyJump(ref Vec2 velocity)
    {
        if (state.JumpBufferTimer <= 0f)
            return;
        if (state.CoyoteTimer > 0f)
        {
            velocity = velocity with { Y = task.Movement.JumpVelocity };
            state.CoyoteTimer = 0f;
            state.JumpBufferTimer = 0f;
        }
    }

    void ApplyGravity(ref Vec2 velocity, float dt)
    {
        if (state.IsGrounded && velocity.Y <= 0f)
        {
            velocity = velocity with { Y = -1f };
            return;
        }

        float gravity = task.Movement.NormalGravity;
        if (velocity.Y < 0f)
            gravity = task.Movement.FallGravity;
        else if (!state.JumpHeld && velocity.Y > 0f)
            gravity = task.Movement.JumpCutGravity;
        velocity = velocity with
        {
            Y = DeterministicMath.MoveTowards(velocity.Y, -task.Movement.MaxFallSpeed, gravity * dt)
        };
    }

    void StartDash(Vec2 move)
    {
        Vec2 direction = move.LengthSquared > 0.05f ? move.Normalized() : new Vec2(state.Facing, 0f);
        state.DashDirection = direction;
        state.DashTimer = task.Movement.DashLength / MathF.Max(0.01f, task.Movement.DashSpeed);
        state.IsDashing = true;
        state.DashesRemaining--;
        state.DashBufferTimer = 0f;
        state.DashCooldownTimer = task.Movement.DashRefillCooldown;
        state.Velocity = direction * task.Movement.DashSpeed;
    }

    void UpdateDash(float dt)
    {
        state.DashTimer -= dt;
        state.Velocity = state.DashDirection * task.Movement.DashSpeed;
        if (state.DashTimer > 0f)
            return;
        state.IsDashing = false;
        state.Velocity = state.DashDirection * task.Movement.DashEndSpeed;
    }

    void Integrate(float dt)
    {
        Vec2 old = state.Position;
        Vec2 target = old + state.Velocity * dt;
        Rect2 horizontalBody = BodyAt(new Vec2(target.X, old.Y));
        foreach (PlatformDto platform in solids)
        {
            if (!horizontalBody.Intersects(platform.Rect))
                continue;
            // Merely touching the top of a floor is a grounded contact, not a side collision.
            // Require material vertical overlap before blocking horizontal motion.
            if (horizontalBody.MinY >= platform.Rect.MaxY - Skin
                || horizontalBody.MaxY <= platform.Rect.MinY + Skin)
                continue;
            if (state.Velocity.X > 0f)
                target = target with { X = platform.Rect.MinX - PlayerWidth / 2f - Skin };
            else if (state.Velocity.X < 0f)
                target = target with { X = platform.Rect.MaxX + PlayerWidth / 2f + Skin };
            state.Velocity = state.Velocity with { X = 0f };
            horizontalBody = BodyAt(new Vec2(target.X, old.Y));
        }

        Rect2 verticalBody = BodyAt(target);
        foreach (PlatformDto platform in solids)
        {
            if (!verticalBody.Intersects(platform.Rect))
                continue;
            if (state.Velocity.Y > 0f)
                target = target with { Y = platform.Rect.MinY - PlayerHeight / 2f - Skin };
            else if (state.Velocity.Y < 0f)
                target = target with { Y = platform.Rect.MaxY + PlayerHeight / 2f + Skin };
            state.Velocity = state.Velocity with { Y = 0f };
            verticalBody = BodyAt(target);
        }

        state.Position = target;
    }

    void RefreshContacts()
    {
        Rect2 feet = new(state.Position.X - PlayerWidth * 0.45f, state.Position.Y - PlayerHeight / 2f - 0.05f, PlayerWidth * 0.9f, 0.08f);
        Rect2 left = new(state.Position.X - PlayerWidth / 2f - 0.05f, state.Position.Y - PlayerHeight * 0.4f, 0.08f, PlayerHeight * 0.8f);
        Rect2 right = new(state.Position.X + PlayerWidth / 2f - 0.03f, state.Position.Y - PlayerHeight * 0.4f, 0.08f, PlayerHeight * 0.8f);
        state.IsGrounded = solids.Any(p => feet.Intersects(p.Rect));
        state.OnLeftWall = solids.Any(p => left.Intersects(p.Rect));
        state.OnRightWall = solids.Any(p => right.Intersects(p.Rect));
    }

    void CheckTerminal()
    {
        Rect2 body = BodyAt(state.Position);
        if (task.Level.Goal.Intersects(body))
            state.IsComplete = true;
        if (hazards.Any(h => h.Intersects(body)) || state.Position.Y < -40f)
            state.IsDead = true;
    }

    Rect2 BodyAt(Vec2 position) =>
        new(position.X - PlayerWidth / 2f, position.Y - PlayerHeight / 2f, PlayerWidth, PlayerHeight);

    RyzObservationDto ToObservation()
    {
        float total = MathF.Max(0.001f, task.Level.Goal.X - task.Level.Spawn.X);
        float progress = DeterministicMath.Clamp((state.Position.X - task.Level.Spawn.X) / total, 0f, 1f);
        float[] player = new float[task.ObservationSchema.PlayerVectorSize];
        player[0] = state.Position.X - task.Level.Goal.X;
        player[1] = state.Position.Y - task.Level.Goal.Y;
        player[2] = state.Velocity.X;
        player[3] = state.Velocity.Y;
        player[4] = state.IsGrounded ? 1f : 0f;
        player[5] = state.OnLeftWall ? 1f : 0f;
        player[6] = state.OnRightWall ? 1f : 0f;
        player[7] = state.IsDashing ? 1f : 0f;
        player[8] = state.DashesRemaining;
        player[9] = progress;
        player[10] = state.Tick;
        return new RyzObservationDto
        {
            Tick = state.Tick,
            Position = state.Position,
            Velocity = state.Velocity,
            IsGrounded = state.IsGrounded,
            OnLeftWall = state.OnLeftWall,
            OnRightWall = state.OnRightWall,
            IsDashing = state.IsDashing,
            DashesRemaining = state.DashesRemaining,
            Progress = progress,
            IsDead = state.IsDead,
            IsComplete = state.IsComplete,
            PlayerVector = player,
        };
    }
}

public struct SimState
{
    public int Tick;
    public Vec2 Position;
    public Vec2 Velocity;
    public int Facing;
    public bool IsGrounded;
    public bool OnLeftWall;
    public bool OnRightWall;
    public bool IsDashing;
    public bool JumpHeld;
    public int DashesRemaining;
    public float CoyoteTimer;
    public float JumpBufferTimer;
    public float DashBufferTimer;
    public float DashCooldownTimer;
    public float DashTimer;
    public Vec2 DashDirection;
    public bool IsDead;
    public bool IsComplete;
}
