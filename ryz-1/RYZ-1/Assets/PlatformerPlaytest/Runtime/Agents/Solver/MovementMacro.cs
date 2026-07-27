using System.Collections.Generic;

namespace PlatformerPlaytest.Solver
{
    /// <summary>The small enumerable macro alphabet the beam search plans over (ADR-004).</summary>
    public enum MacroKind
    {
        HoldDirection,
        JumpAndHold,
        ReleaseAll,
        DashDirection,
        Wait
    }

    /// <summary>
    /// A movement macro: a compact, deterministic recipe that expands to a fixed-length PlayerAction sequence.
    /// Press fields are edges (true only on the macro's first tick); held fields persist for the requested span.
    ///
    /// Determinism invariant: Expand(macro) is a pure function of the macro's fields — no RNG, no clock, no state.
    /// The same macro always appends the identical action sequence.
    /// </summary>
    public struct MovementMacro
    {
        public MacroKind Kind;
        public int DirX;   // -1, 0, +1 for HoldDirection / JumpAndHold
        public int DashX;  // dash vector x component (-1..+1)
        public int DashY;  // dash vector y component (-1..+1)
        public int Ticks;  // total ticks the macro spans

        public static MovementMacro HoldDirection(int dirX, int ticks) =>
            new MovementMacro { Kind = MacroKind.HoldDirection, DirX = dirX, Ticks = ticks };

        public static MovementMacro JumpAndHold(int dirX, int holdTicks) =>
            new MovementMacro { Kind = MacroKind.JumpAndHold, DirX = dirX, Ticks = holdTicks };

        public static MovementMacro ReleaseAll(int ticks) =>
            new MovementMacro { Kind = MacroKind.ReleaseAll, Ticks = ticks };

        public static MovementMacro DashDirection(int dx, int dy, int ticks) =>
            new MovementMacro { Kind = MacroKind.DashDirection, DashX = dx, DashY = dy, Ticks = ticks };

        public static MovementMacro Wait(int ticks) =>
            new MovementMacro { Kind = MacroKind.Wait, Ticks = ticks };

        /// <summary>
        /// Append this macro's per-tick actions to <paramref name="into"/>. Edges (JumpPressed / DashPressed)
        /// fire only on the first appended tick; held flags persist for the whole span.
        /// </summary>
        public void Expand(List<PlayerAction> into)
        {
            int n = Ticks < 0 ? 0 : Ticks;
            for (int i = 0; i < n; i++)
            {
                bool first = i == 0;
                PlayerAction a = PlayerAction.Neutral;
                switch (Kind)
                {
                    case MacroKind.HoldDirection:
                        a.MoveX = DirX;
                        break;
                    case MacroKind.JumpAndHold:
                        a.MoveX = DirX;
                        a.JumpPressed = first;   // edge: only tick 0
                        a.JumpHeld = true;       // held for holdTicks
                        break;
                    case MacroKind.ReleaseAll:
                        // all neutral — releases jump/dir, lets momentum carry
                        break;
                    case MacroKind.DashDirection:
                        a.MoveX = DashX;
                        a.MoveY = DashY;
                        a.DashPressed = first;   // edge: only tick 0
                        break;
                    case MacroKind.Wait:
                        break;
                }
                into.Add(a);
            }
        }
    }
}
