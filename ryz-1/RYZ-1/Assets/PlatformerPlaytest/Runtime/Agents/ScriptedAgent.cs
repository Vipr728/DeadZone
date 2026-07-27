namespace PlatformerPlaytest
{
    /// <summary>
    /// Deterministic, no-frills agent: holds right, jumps against a right wall, and jumps periodically or
    /// whenever grounded horizontal velocity has stalled for a few ticks (stuck-on-ground recovery). Good enough
    /// to traverse flat ground and small gaps in the MVP test level.
    /// </summary>
    public sealed class ScriptedAgent : IAgent
    {
        readonly int periodicJumpInterval;
        readonly int stallTicksBeforeJump;
        readonly int jumpHoldTicks;

        int stalledTicks;
        int heldTicksRemaining;

        public ScriptedAgent(int periodicJumpInterval = 40, int stallTicksBeforeJump = 8, int jumpHoldTicks = 15)
        {
            this.periodicJumpInterval = periodicJumpInterval;
            this.stallTicksBeforeJump = stallTicksBeforeJump;
            this.jumpHoldTicks = jumpHoldTicks;
        }

        public void OnEpisodeStart(int seed)
        {
            stalledTicks = 0;
            heldTicksRemaining = 0;
        }

        public PlayerAction Act(Observation obs, int tick)
        {
            bool stalled = obs.IsGrounded && obs.Velocity.x > -0.1f && obs.Velocity.x < 0.1f;
            stalledTicks = stalled ? stalledTicks + 1 : 0;

            // No jump on OnRightWall: airborne wall contact would trigger a wall jump,
            // which kicks the agent AWAY from the ledge it is trying to climb.
            bool press = (obs.IsGrounded && stalledTicks >= stallTicksBeforeJump)
                || (periodicJumpInterval > 0 && obs.IsGrounded && tick % periodicJumpInterval == 0);

            if (press)
                heldTicksRemaining = jumpHoldTicks;

            // Keep jump held after the press so the jump-release velocity cut
            // (jumpReleaseVelocityMultiplier) doesn't turn every jump into a tap-jump.
            bool held = heldTicksRemaining > 0;
            if (held)
                heldTicksRemaining--;

            return new PlayerAction
            {
                MoveX = 1f,
                MoveY = 0f,
                JumpPressed = press,
                JumpHeld = held,
                DashPressed = false,
                ClimbHeld = false
            };
        }
    }
}
