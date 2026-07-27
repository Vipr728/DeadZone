namespace PlatformerPlaytest
{
    /// <summary>Agent-issued input for a single tick. Press fields are edges, true only on the tick they occur.</summary>
    public struct PlayerAction
    {
        public float MoveX;
        public float MoveY;
        public bool JumpPressed;
        public bool JumpHeld;
        public bool DashPressed;
        public bool ClimbHeld;

        public static readonly PlayerAction Neutral = new PlayerAction
        {
            MoveX = 0f,
            MoveY = 0f,
            JumpPressed = false,
            JumpHeld = false,
            DashPressed = false,
            ClimbHeld = false
        };
    }
}
