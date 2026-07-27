using UnityEngine;

namespace Playtester
{
    /// <summary>
    /// The locked player-control seam for both a human and an ML-Agents policy.
    /// The agent calls SetMove and SetJump; keyboard input is only a local
    /// development fallback.
    /// </summary>
    public sealed class PlayerInputAdapter : MonoBehaviour
    {
        private float moveInput;
        private bool jumpRequested;

        public float Move => moveInput;

        public void SetMove(float direction)
        {
            moveInput = Mathf.Clamp(direction, -1f, 1f);
        }

        public void SetJump(bool pressed)
        {
            jumpRequested |= pressed;
        }

        public bool ConsumeJump()
        {
            bool requested = jumpRequested;
            jumpRequested = false;
            return requested;
        }

        public void Clear()
        {
            moveInput = 0f;
            jumpRequested = false;
        }
    }
}
