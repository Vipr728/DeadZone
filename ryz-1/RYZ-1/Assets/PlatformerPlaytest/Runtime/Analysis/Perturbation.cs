using System.Collections.Generic;

namespace PlatformerPlaytest.Analysis
{
    /// <summary>Result of an execution-tolerance sweep around one jump press edge.</summary>
    public struct ToleranceWindow
    {
        /// <summary>Tick of the unshifted jump press edge that was perturbed (-1 if no edge was found).</summary>
        public int EdgeTick;
        /// <summary>Applied shift range: deltas run from -MaxShift..+MaxShift, so index d+MaxShift holds delta d.</summary>
        public int MaxShift;
        /// <summary>successes[i] == did the episode complete with delta (i - MaxShift) applied to the edge.</summary>
        public bool[] Successes;
        /// <summary>Length of the contiguous run of successes that contains delta 0 (0 if delta 0 itself fails).</summary>
        public int WindowTicks;

        public bool SuccessAt(int delta)
        {
            int i = delta + MaxShift;
            return i >= 0 && i < Successes.Length && Successes[i];
        }
    }

    /// <summary>
    /// Execution-tolerance perturbation (T8): takes a solved action stream, shifts one jump press edge by
    /// delta ticks across [-maxShift..+maxShift], re-runs the episode per delta, and reports the contiguous
    /// success span around the nominal timing. Measures how forgiving an obstacle's jump timing is.
    ///
    /// Cost: O(shifts * ticks) — (2*maxShift+1) full episode re-runs, each O(stepBudget) simulator ticks.
    /// The edge-shift itself is a pure list transform (<see cref="ShiftJumpEdge"/>), isolated for EditMode testing.
    /// </summary>
    public static class Perturbation
    {
        public const int DefaultMaxShift = 8;

        /// <summary>
        /// Index of the first tick at or after <paramref name="fromTick"/> whose action carries a JumpPressed
        /// edge, or -1 if none. Pure; no simulation.
        /// </summary>
        public static int FindJumpEdge(IReadOnlyList<PlayerAction> stream, int fromTick)
        {
            if (stream == null)
                return -1;
            int start = fromTick < 0 ? 0 : fromTick;
            for (int i = start; i < stream.Count; i++)
                if (stream[i].JumpPressed)
                    return i;
            return -1;
        }

        /// <summary>
        /// Returns a copy of <paramref name="stream"/> with the jump edge at <paramref name="edgeTick"/> (its
        /// JumpPressed tick plus the contiguous JumpHeld run) moved by <paramref name="delta"/> ticks. Only jump
        /// flags move; MoveX/MoveY/Dash on every tick stay put (horizontal steering is unchanged). The list is
        /// extended with neutral ticks if the shift pushes the edge past the end; a negative target is clamped to 0.
        /// Pure — no simulation, no mutation of the input.
        /// </summary>
        public static List<PlayerAction> ShiftJumpEdge(IReadOnlyList<PlayerAction> stream, int edgeTick, int delta)
        {
            List<PlayerAction> copy = new List<PlayerAction>(stream.Count + (delta > 0 ? delta : 0));
            for (int i = 0; i < stream.Count; i++)
                copy.Add(stream[i]);

            if (edgeTick < 0 || edgeTick >= copy.Count || !copy[edgeTick].JumpPressed)
                return copy; // nothing to shift

            // Span = press tick + contiguous held run.
            int len = 1;
            while (edgeTick + len < copy.Count && copy[edgeTick + len].JumpHeld)
                len++;

            // Clear jump flags on the old span (keep the other input fields on those ticks).
            for (int i = 0; i < len; i++)
            {
                PlayerAction a = copy[edgeTick + i];
                a.JumpPressed = false;
                a.JumpHeld = false;
                copy[edgeTick + i] = a;
            }

            int newStart = edgeTick + delta;
            if (newStart < 0)
                newStart = 0;
            while (copy.Count < newStart + len)
                copy.Add(PlayerAction.Neutral);

            for (int i = 0; i < len; i++)
            {
                PlayerAction a = copy[newStart + i];
                a.JumpPressed = i == 0;
                a.JumpHeld = true;
                copy[newStart + i] = a;
            }

            return copy;
        }

        /// <summary>
        /// Sweep the jump edge at/after <paramref name="edgeTickHint"/> by [-maxShift..+maxShift] and measure the
        /// success window. Each delta re-runs a fresh, deterministic episode through the adapter (ResetEpisode +
        /// EpisodeRunner). Same seed every run, so differences are the timing shift alone.
        /// </summary>
        public static ToleranceWindow MeasureTolerance(
            IGameAdapter adapter, ScenarioConfig scenario, IReadOnlyList<PlayerAction> baseStream,
            int edgeTickHint, int seed = 0, int maxShift = DefaultMaxShift)
        {
            int edge = FindJumpEdge(baseStream, edgeTickHint);
            ToleranceWindow window = new ToleranceWindow
            {
                EdgeTick = edge,
                MaxShift = maxShift,
                Successes = new bool[2 * maxShift + 1],
                WindowTicks = 0
            };
            if (edge < 0)
                return window;

            for (int delta = -maxShift; delta <= maxShift; delta++)
            {
                List<PlayerAction> shifted = ShiftJumpEdge(baseStream, edge, delta);
                EpisodeRunner runner = new EpisodeRunner(adapter, new ReplayAgent(shifted), scenario, seed);
                EpisodeResult result = runner.Run();
                window.Successes[delta + maxShift] = result.Outcome == Outcome.Completed;
            }

            window.WindowTicks = ContiguousRunAround(window.Successes, maxShift);
            return window;
        }

        /// <summary>Length of the contiguous true-run that contains index <paramref name="center"/> (0 if false there).</summary>
        static int ContiguousRunAround(bool[] flags, int center)
        {
            if (center < 0 || center >= flags.Length || !flags[center])
                return 0;
            int lo = center;
            while (lo - 1 >= 0 && flags[lo - 1]) lo--;
            int hi = center;
            while (hi + 1 < flags.Length && flags[hi + 1]) hi++;
            return hi - lo + 1;
        }
    }
}
