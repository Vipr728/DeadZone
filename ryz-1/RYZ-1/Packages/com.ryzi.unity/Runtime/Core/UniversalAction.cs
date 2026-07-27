using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryzi
{
    [Serializable]
    public struct ButtonActionState
    {
        [SerializeField] string channelId;
        [SerializeField] bool pressed;
        [SerializeField] bool held;
        [SerializeField] bool released;

        public string ChannelId => channelId;
        public bool PressedThisTick => pressed;
        public bool Held => held;
        public bool ReleasedThisTick => released;

        public ButtonActionState(string channelId, bool pressedThisTick, bool held, bool releasedThisTick)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("A button channel ID is required.", nameof(channelId));
            if (pressedThisTick && releasedThisTick)
                throw new ArgumentException("A button cannot be pressed and released on the same simulation tick.");

            this.channelId = channelId;
            pressed = pressedThisTick;
            this.held = held;
            released = releasedThisTick;
        }
    }

    [Serializable]
    public readonly struct UniversalAction
    {
        [SerializeField] readonly Vector2 moveAxis;
        [SerializeField] readonly Vector2 aimAxis;
        [SerializeField] readonly ButtonActionState[] buttons;

        public Vector2 MoveAxis => moveAxis;
        public Vector2 AimAxis => aimAxis;
        public IReadOnlyList<ButtonActionState> Buttons => buttons ?? Array.Empty<ButtonActionState>();

        public UniversalAction(
            Vector2 moveAxis,
            Vector2 aimAxis,
            IReadOnlyList<ButtonActionState> buttons = null)
        {
            this.moveAxis = Vector2.ClampMagnitude(moveAxis, 1f);
            this.aimAxis = Vector2.ClampMagnitude(aimAxis, 1f);
            if (buttons == null || buttons.Count == 0)
            {
                this.buttons = Array.Empty<ButtonActionState>();
                return;
            }

            this.buttons = new ButtonActionState[buttons.Count];
            for (int i = 0; i < buttons.Count; i++)
                this.buttons[i] = buttons[i];
        }

        public bool TryGetButton(string channelId, out ButtonActionState state)
        {
            ButtonActionState[] source = buttons;
            if (source != null)
            {
                for (int i = 0; i < source.Length; i++)
                {
                    if (string.Equals(source[i].ChannelId, channelId, StringComparison.Ordinal))
                    {
                        state = source[i];
                        return true;
                    }
                }
            }

            state = default;
            return false;
        }

        public static UniversalAction Neutral => new UniversalAction(Vector2.zero, Vector2.zero);
    }
}
