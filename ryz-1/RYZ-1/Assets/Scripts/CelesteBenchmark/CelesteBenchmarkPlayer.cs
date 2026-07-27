using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CelesteBenchmark
{
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public sealed class CelesteBenchmarkPlayer : MonoBehaviour
    {
        [Header("Collision")]
        public LayerMask groundMask;
        public LayerMask wallMask;
        public LayerMask oneWayPlatformMask;
        public Vector2 groundCheckSize = new Vector2(0.74f, 0.08f);
        public Vector2 wallCheckSize = new Vector2(0.08f, 0.82f);
        public float groundCheckDistance = 0.04f;
        public float wallCheckDistance = 0.05f;

        [Header("Running")]
        public float movementSpeed = 8.5f;
        public float groundAcceleration = 90f;
        public float groundDeceleration = 110f;
        public float airAcceleration = 65f;
        public float airDeceleration = 45f;

        [Header("Gravity")]
        public float normalGravity = 34f;
        public float fallGravity = 52f;
        public float jumpCutGravity = 70f;
        public float maxFallSpeed = 24f;
        public float wallSlideMaxFallSpeed = 2.5f;

        [Header("Jumping")]
        public float jumpVelocity = 13.5f;
        public float coyoteTime = 0.11f;
        public float jumpBufferTime = 0.12f;
        [Range(0.05f, 1f)] public float jumpReleaseVelocityMultiplier = 0.48f;

        [Header("Wall Jumping")]
        public float wallJumpHorizontalSpeed = 10.5f;
        public float wallJumpVerticalSpeed = 12.5f;
        public float wallJumpControlLockTime = 0.13f;

        [Header("Climbing")]
        public float climbSpeed = 3.8f;
        public float climbStaminaMax = 100f;
        public float climbStaminaDrainPerSecond = 26f;
        public float climbStaminaRegenPerSecond = 80f;
        public float climbExhaustedThreshold = 1f;

        [Header("Dashing")]
        public int maxDashes = 1;
        public float dashSpeed = 18f;
        public float dashLength = 3.2f;
        public float dashEndSpeed = 5.5f;
        public float dashBufferTime = 0.12f;
        public float dashRefillCooldown = 0.04f;

        [Header("One-Way Platforms")]
        public float dropThroughDuration = 0.22f;

        [Header("Respawn")]
        public Vector2 startPosition;
        public float respawnFreezeSeconds = 0.05f;

        public int DashesRemaining => dashesRemaining;
        public float ClimbStamina => climbStamina;
        public bool IsGrounded => grounded;
        public bool IsDashing => dashing;
        public Vector2 Velocity => rb ? rb.linearVelocity : Vector2.zero;
        public bool IsControlsFrozen => controlsFrozen;
        public bool IsClimbing => isClimbing;
        public bool TouchingLeftWall => leftWall;
        public bool TouchingRightWall => rightWall;

        public event Action Died;

        Rigidbody2D rb;
        Collider2D bodyCollider;
        Vector2 moveInput;
        Vector2 checkpointPosition;
        float coyoteTimer;
        float jumpBufferTimer;
        float dashBufferTimer;
        float wallJumpLockTimer;
        float dashTimer;
        float dashCooldownTimer;
        float climbStamina;
        int dashesRemaining;
        int facingDirection = 1;
        int wallDirection;
        bool grounded;
        bool touchingWall;
        bool leftWall;
        bool rightWall;
        bool jumpHeld;
        bool jumpReleasedThisFrame;
        bool climbHeld;
        bool dashing;
        bool controlsFrozen;
        bool ignoringOneWay;
        bool isClimbing;
        Vector2 dashDirection;

        // Tick-driven seam
        bool tickDriven;
        IVirtualInput virtualInput;
        float respawnFreezeRemaining;
        float dropThroughRemaining;

        // Drop-through per-collider ignore tracking (avoids global layer-collision state)
        ContactFilter2D oneWayContactFilter;
        readonly Collider2D[] overlapResults = new Collider2D[8];
        readonly Collider2D[] ignoredColliders = new Collider2D[8];
        int ignoredCount;

        // Physics scene routing: defaults to the global scene (keyboard-mode parity); arenas
        // running in an isolated LocalPhysicsMode.Physics2D scene must call SetPhysicsScene.
        PhysicsScene2D physicsScene;
        ContactFilter2D groundContactFilter;
        ContactFilter2D wallContactFilter;
        readonly Collider2D[] singleResult = new Collider2D[1];

        void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            bodyCollider = GetComponent<Collider2D>();
            rb.gravityScale = 0f;
            rb.freezeRotation = true;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;

            checkpointPosition = startPosition == Vector2.zero ? (Vector2)transform.position : startPosition;
            climbStamina = climbStaminaMax;
            dashesRemaining = maxDashes;

            physicsScene = Physics2D.defaultPhysicsScene;

            oneWayContactFilter = new ContactFilter2D();
            oneWayContactFilter.SetLayerMask(oneWayPlatformMask);
            oneWayContactFilter.useTriggers = Physics2D.queriesHitTriggers;

            // Layer masks are re-applied every check (cheap struct field writes, no allocation) rather than
            // baked once here, since some callers (e.g. test level builders) assign groundMask/wallMask on
            // the component after Awake has already run.
            groundContactFilter = new ContactFilter2D();
            groundContactFilter.useTriggers = Physics2D.queriesHitTriggers;

            wallContactFilter = new ContactFilter2D();
            wallContactFilter.useTriggers = Physics2D.queriesHitTriggers;
        }

        public void SetPhysicsScene(PhysicsScene2D scene)
        {
            physicsScene = scene;
        }

        public void SetVirtualInput(IVirtualInput source)
        {
            virtualInput = source;
        }

        public void SetTickDriven(bool value)
        {
            tickDriven = value;
        }

        void Update()
        {
            if (tickDriven)
                return;

            if (controlsFrozen)
                return;

            ReadInput();

            if (WasJumpPressed())
                jumpBufferTimer = jumpBufferTime;
            else
                jumpBufferTimer -= Time.deltaTime;

            if (WasDashPressed())
                dashBufferTimer = dashBufferTime;
            else
                dashBufferTimer -= Time.deltaTime;

            if (WasJumpReleased())
                jumpReleasedThisFrame = true;
        }

        void FixedUpdate()
        {
            if (tickDriven)
                return;

            Tick(Time.fixedDeltaTime);
        }

        public void Tick(float dt)
        {
            if (tickDriven && virtualInput != null)
                ReadVirtualInput(dt);

            UpdateCollisionChecks();
            UpdateTimers(dt);

            if (tickDriven && ignoringOneWay)
            {
                dropThroughRemaining -= dt;
                if (dropThroughRemaining <= 0f)
                    RestoreOneWayColliders();
            }

            if (tickDriven && controlsFrozen)
            {
                rb.linearVelocity = Vector2.zero;
                if (respawnFreezeRemaining > 0f)
                {
                    respawnFreezeRemaining -= dt;
                    if (respawnFreezeRemaining <= 0f)
                    {
                        transform.position = checkpointPosition;
                        RefillDashAndStamina();
                        controlsFrozen = false;
                    }
                }
                return;
            }

            if (controlsFrozen)
            {
                rb.linearVelocity = Vector2.zero;
                return;
            }

            if (dashBufferTimer > 0f && dashesRemaining > 0 && dashCooldownTimer <= 0f)
                StartDash();

            if (dashing)
            {
                isClimbing = false;
                UpdateDash(dt);
                return;
            }

            Vector2 velocity = rb.linearVelocity;

            if (TryDropThrough(dt))
            {
                jumpBufferTimer = 0f;
                isClimbing = false;
                return;
            }

            bool climbing = TryApplyClimb(ref velocity, dt);
            isClimbing = climbing;
            if (!climbing)
            {
                TryApplyJump(ref velocity);
                ApplyWallSlide(ref velocity);
                ApplyGravity(ref velocity, dt);
            }

            ApplyHorizontalMovement(ref velocity, dt);

            rb.linearVelocity = velocity;
            jumpReleasedThisFrame = false;
        }

        void ReadVirtualInput(float dt)
        {
            Vector2 v = new Vector2(Mathf.Clamp(virtualInput.MoveX, -1f, 1f), Mathf.Clamp(virtualInput.MoveY, -1f, 1f));
            moveInput = v;
            if (Mathf.Abs(v.x) > 0.1f)
                facingDirection = v.x > 0f ? 1 : -1;

            jumpHeld = virtualInput.JumpHeld;
            climbHeld = virtualInput.ClimbHeld;

            if (virtualInput.JumpPressedEdge)
                jumpBufferTimer = jumpBufferTime;
            else
                jumpBufferTimer -= dt;

            if (virtualInput.DashPressedEdge)
                dashBufferTimer = dashBufferTime;
            else
                dashBufferTimer -= dt;

            if (virtualInput.JumpReleasedEdge)
                jumpReleasedThisFrame = true;
        }

        void ReadInput()
        {
            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;

            Vector2 keyboardInput = Vector2.zero;
            if (keyboard != null)
            {
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                    keyboardInput.x -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                    keyboardInput.x += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                    keyboardInput.y -= 1f;
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                    keyboardInput.y += 1f;

                jumpHeld = keyboard.spaceKey.isPressed || keyboard.zKey.isPressed;
                climbHeld = keyboard.rightShiftKey.isPressed || keyboard.cKey.isPressed || keyboard.leftCtrlKey.isPressed;
            }

            Vector2 padInput = Vector2.zero;
            if (gamepad != null)
            {
                padInput = gamepad.leftStick.ReadValue();
                if (padInput.magnitude < 0.2f)
                    padInput = gamepad.dpad.ReadValue();

                jumpHeld |= gamepad.buttonSouth.isPressed;
                climbHeld |= gamepad.leftShoulder.isPressed || gamepad.rightShoulder.isPressed || gamepad.leftTrigger.ReadValue() > 0.4f || gamepad.rightTrigger.ReadValue() > 0.4f;
            }

            moveInput = padInput.sqrMagnitude > keyboardInput.sqrMagnitude ? padInput : keyboardInput;
            moveInput = Vector2.ClampMagnitude(moveInput, 1f);
            if (Mathf.Abs(moveInput.x) > 0.1f)
                facingDirection = moveInput.x > 0f ? 1 : -1;
        }

        bool WasJumpPressed()
        {
            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;
            return (keyboard != null && (keyboard.spaceKey.wasPressedThisFrame || keyboard.zKey.wasPressedThisFrame))
                || (gamepad != null && gamepad.buttonSouth.wasPressedThisFrame);
        }

        bool WasJumpReleased()
        {
            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;
            return (keyboard != null && (keyboard.spaceKey.wasReleasedThisFrame || keyboard.zKey.wasReleasedThisFrame))
                || (gamepad != null && gamepad.buttonSouth.wasReleasedThisFrame);
        }

        bool WasDashPressed()
        {
            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;
            return (keyboard != null && (keyboard.leftShiftKey.wasPressedThisFrame || keyboard.xKey.wasPressedThisFrame || keyboard.jKey.wasPressedThisFrame))
                || (gamepad != null && gamepad.buttonEast.wasPressedThisFrame);
        }

        void UpdateCollisionChecks()
        {
            Bounds bounds = bodyCollider.bounds;
            Vector2 groundOrigin = new Vector2(bounds.center.x, bounds.min.y - groundCheckDistance);
            Vector2 leftOrigin = new Vector2(bounds.min.x - wallCheckDistance, bounds.center.y);
            Vector2 rightOrigin = new Vector2(bounds.max.x + wallCheckDistance, bounds.center.y);

            groundContactFilter.SetLayerMask(groundMask | oneWayPlatformMask);
            wallContactFilter.SetLayerMask(wallMask);

            grounded = physicsScene.OverlapBox(groundOrigin, groundCheckSize, 0f, groundContactFilter, singleResult) > 0;
            leftWall = physicsScene.OverlapBox(leftOrigin, wallCheckSize, 0f, wallContactFilter, singleResult) > 0;
            rightWall = physicsScene.OverlapBox(rightOrigin, wallCheckSize, 0f, wallContactFilter, singleResult) > 0;

            touchingWall = leftWall || rightWall;
            wallDirection = rightWall ? 1 : leftWall ? -1 : 0;
        }

        void UpdateTimers(float dt)
        {
            coyoteTimer = grounded ? coyoteTime : coyoteTimer - dt;
            wallJumpLockTimer -= dt;
            dashCooldownTimer -= dt;

            if (grounded)
            {
                dashesRemaining = maxDashes;
                climbStamina = Mathf.MoveTowards(climbStamina, climbStaminaMax, climbStaminaRegenPerSecond * dt);
            }
        }

        void ApplyHorizontalMovement(ref Vector2 velocity, float dt)
        {
            if (wallJumpLockTimer > 0f)
                return;

            float target = moveInput.x * movementSpeed;
            float acceleration;
            if (Mathf.Abs(target) > 0.01f)
                acceleration = grounded ? groundAcceleration : airAcceleration;
            else
                acceleration = grounded ? groundDeceleration : airDeceleration;

            velocity.x = Mathf.MoveTowards(velocity.x, target, acceleration * dt);
        }

        void TryApplyJump(ref Vector2 velocity)
        {
            if (jumpBufferTimer <= 0f)
                return;

            if (coyoteTimer > 0f)
            {
                velocity.y = jumpVelocity;
                coyoteTimer = 0f;
                jumpBufferTimer = 0f;
                return;
            }

            if (touchingWall && !grounded)
            {
                int awayFromWall = -wallDirection;
                velocity.x = awayFromWall * wallJumpHorizontalSpeed;
                velocity.y = wallJumpVerticalSpeed;
                facingDirection = awayFromWall;
                wallJumpLockTimer = wallJumpControlLockTime;
                jumpBufferTimer = 0f;
            }
        }

        void ApplyGravity(ref Vector2 velocity, float dt)
        {
            if (grounded && velocity.y <= 0f)
            {
                velocity.y = -1f;
                return;
            }

            if (jumpReleasedThisFrame && velocity.y > 0f)
                velocity.y *= jumpReleaseVelocityMultiplier;

            float gravity = normalGravity;
            if (velocity.y < 0f)
                gravity = fallGravity;
            else if (!jumpHeld && velocity.y > 0f)
                gravity = jumpCutGravity;

            velocity.y = Mathf.MoveTowards(velocity.y, -maxFallSpeed, gravity * dt);
        }

        void ApplyWallSlide(ref Vector2 velocity)
        {
            if (!touchingWall || grounded || velocity.y >= 0f)
                return;

            bool pressingIntoWall = Mathf.Abs(moveInput.x) > 0.1f && Mathf.Sign(moveInput.x) == wallDirection;
            if (pressingIntoWall || climbHeld)
                velocity.y = Mathf.Max(velocity.y, -wallSlideMaxFallSpeed);
        }

        bool TryApplyClimb(ref Vector2 velocity, float dt)
        {
            if (!climbHeld || !touchingWall || grounded || climbStamina <= climbExhaustedThreshold)
                return false;

            climbStamina = Mathf.Max(0f, climbStamina - climbStaminaDrainPerSecond * dt);
            velocity.x = 0f;
            velocity.y = moveInput.y * climbSpeed;
            return true;
        }

        void StartDash()
        {
            dashDirection = moveInput.sqrMagnitude > 0.05f ? moveInput.normalized : new Vector2(facingDirection, 0f);
            dashTimer = dashLength / Mathf.Max(0.01f, dashSpeed);
            dashing = true;
            dashesRemaining--;
            dashBufferTimer = 0f;
            dashCooldownTimer = dashRefillCooldown;
            rb.linearVelocity = dashDirection * dashSpeed;
        }

        void UpdateDash(float dt)
        {
            dashTimer -= dt;
            rb.linearVelocity = dashDirection * dashSpeed;
            if (dashTimer > 0f)
                return;

            dashing = false;
            rb.linearVelocity = dashDirection * dashEndSpeed;
        }

        bool TryDropThrough(float dt)
        {
            if (!grounded || jumpBufferTimer <= 0f || moveInput.y > -0.5f || ignoringOneWay)
                return false;

            // Re-apply the mask each check (like ground/wall filters): callers assigning oneWayPlatformMask
            // after Awake would otherwise leave this filter baked with a stale/empty mask.
            oneWayContactFilter.SetLayerMask(oneWayPlatformMask);
            Bounds bounds = bodyCollider.bounds;
            Vector2 groundOrigin = new Vector2(bounds.center.x, bounds.min.y - groundCheckDistance);
            int count = physicsScene.OverlapBox(groundOrigin, groundCheckSize, 0f, oneWayContactFilter, overlapResults);
            if (count <= 0)
                return false;

            ignoringOneWay = true;
            ignoredCount = 0;
            int limit = Mathf.Min(count, overlapResults.Length);
            for (int i = 0; i < limit; i++)
            {
                Collider2D col = overlapResults[i];
                if (!col)
                    continue;
                Physics2D.IgnoreCollision(bodyCollider, col, true);
                ignoredColliders[ignoredCount] = col;
                ignoredCount++;
            }

            if (tickDriven)
                dropThroughRemaining = dropThroughDuration;
            else
                StartCoroutine(RestoreOneWayAfterDelay());

            return true;
        }

        IEnumerator RestoreOneWayAfterDelay()
        {
            yield return new WaitForSeconds(dropThroughDuration);
            RestoreOneWayColliders();
        }

        void RestoreOneWayColliders()
        {
            for (int i = 0; i < ignoredCount; i++)
            {
                if (ignoredColliders[i])
                    Physics2D.IgnoreCollision(bodyCollider, ignoredColliders[i], false);
                ignoredColliders[i] = null;
            }

            ignoredCount = 0;
            ignoringOneWay = false;
            dropThroughRemaining = 0f;
        }

        public void RefillDashAndStamina()
        {
            dashesRemaining = maxDashes;
            climbStamina = climbStaminaMax;
            dashCooldownTimer = 0f;
        }

        public void Bounce(Vector2 direction, float speed, bool refillDash)
        {
            Bounce(direction, speed, refillDash, 0f, 0f, 0f);
        }

        public void Bounce(Vector2 direction, float speed, bool refillDash, float dashVelocityCarryMultiplier, float regularVelocityCarryMultiplier, float maxCarriedSpeed)
        {
            Vector2 launchDirection = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.up;
            Vector2 incomingVelocity = rb.linearVelocity;
            float carryMultiplier = dashing ? dashVelocityCarryMultiplier : regularVelocityCarryMultiplier;
            Vector2 carriedVelocity = Vector2.ClampMagnitude(incomingVelocity, maxCarriedSpeed) * carryMultiplier;
            Vector2 launchVelocity = launchDirection * speed + carriedVelocity;
            float launchComponent = Vector2.Dot(launchVelocity, launchDirection);
            if (launchComponent < speed)
                launchVelocity += launchDirection * (speed - launchComponent);

            dashing = false;
            rb.linearVelocity = launchVelocity;
            if (refillDash)
                RefillDashAndStamina();
        }

        public void SetCheckpoint(Vector2 position)
        {
            checkpointPosition = position;
        }

        public void Respawn()
        {
            Died?.Invoke();

            if (tickDriven)
            {
                controlsFrozen = true;
                rb.linearVelocity = Vector2.zero;
                respawnFreezeRemaining = respawnFreezeSeconds;
                return;
            }

            StartCoroutine(RespawnRoutine());
        }

        IEnumerator RespawnRoutine()
        {
            controlsFrozen = true;
            rb.linearVelocity = Vector2.zero;
            yield return new WaitForSeconds(respawnFreezeSeconds);
            transform.position = checkpointPosition;
            RefillDashAndStamina();
            controlsFrozen = false;
        }

        public void ResetForEpisode(Vector2 spawnPosition)
        {
            StopAllCoroutines();
            RestoreOneWayColliders();

            dashing = false;
            controlsFrozen = false;
            isClimbing = false;
            jumpReleasedThisFrame = false;

            coyoteTimer = 0f;
            jumpBufferTimer = 0f;
            dashBufferTimer = 0f;
            wallJumpLockTimer = 0f;
            dashTimer = 0f;
            dashCooldownTimer = 0f;
            respawnFreezeRemaining = 0f;
            dropThroughRemaining = 0f;

            // Sensed state must match a fresh component (all false) so episode N+1
            // observes the same tick-0 state as episode 1 — determinism gate.
            grounded = false;
            touchingWall = false;
            leftWall = false;
            rightWall = false;
            wallDirection = 0;
            moveInput = Vector2.zero;
            jumpHeld = false;
            climbHeld = false;

            transform.position = spawnPosition;
            rb.position = spawnPosition;
            rb.linearVelocity = Vector2.zero;
            checkpointPosition = spawnPosition;
            RefillDashAndStamina();
            facingDirection = 1;
        }

        void OnDrawGizmosSelected()
        {
            if (!bodyCollider)
                bodyCollider = GetComponent<Collider2D>();
            if (!bodyCollider)
                return;

            Bounds bounds = bodyCollider.bounds;
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(new Vector2(bounds.center.x, bounds.min.y - groundCheckDistance), groundCheckSize);
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(new Vector2(bounds.min.x - wallCheckDistance, bounds.center.y), wallCheckSize);
            Gizmos.DrawWireCube(new Vector2(bounds.max.x + wallCheckDistance, bounds.center.y), wallCheckSize);
        }
    }
}
