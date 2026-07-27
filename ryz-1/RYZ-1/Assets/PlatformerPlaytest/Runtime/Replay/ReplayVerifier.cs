using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PlatformerPlaytest
{
    /// <summary>Result of comparing a replay against a recording. FirstDesyncTick == -1 means the replay matched.</summary>
    public struct DesyncReport
    {
        /// <summary>Tick of the first keyframe whose quantized state diverged, or -1 if none.</summary>
        public int FirstDesyncTick;
        /// <summary>Human-readable per-field deltas at the first desync (empty when in sync).</summary>
        public string FieldDeltas;
        /// <summary>Recorded stateHash at the first desync tick (0 when in sync).</summary>
        public ulong RecordedHash;
        /// <summary>Live stateHash at the first desync tick (0 when in sync).</summary>
        public ulong ActualHash;
    }

    /// <summary>
    /// Compares a live replay run against recorded keyframes and reports the first divergence (ADR-006 / replay.md).
    /// Subscribe <see cref="OnStep"/> to the live <see cref="EpisodeRunner"/> callback; at each recorded keyframe
    /// tick it compares live vs recorded on quantized position/velocity, the flags bitfield, dashes, and the
    /// FNV-1a stateHash. Comparison defaults to exact on the 1e-4 quantization grid — no float tolerance beyond
    /// it. Pass the constructor's toleranceUnits (see <see cref="CrossProcessTolerance"/>) only when the
    /// recording came from a DIFFERENT Unity process, where physics is not bit-exact (limitations.md #14).
    ///
    /// Invariants:
    ///  - Never throws on mismatch: desync is data, not an exception. A desync is recorded, never silently dropped.
    ///  - First-write-wins: once <see cref="DesyncReport.FirstDesyncTick"/> is set it is never overwritten, so the
    ///    report always names the earliest divergence even though the run keeps going.
    ///  - Ticks in the live run with no matching recorded keyframe are skipped; recorded keyframe ticks never
    ///    observed live are ignored (they simply never compare).
    ///
    /// Complexity: O(1) amortized per tick — a single forward cursor over the sorted keyframe list; no per-tick
    /// allocation (the delta string is built only once, on the first mismatch).
    /// </summary>
    public sealed class ReplayVerifier
    {
        readonly IReadOnlyList<KeyframeRecord> recorded;
        readonly float tolerance;
        int cursor;
        DesyncReport report;

        /// <summary>
        /// Cross-process replay tolerance (T14). Unity Physics2D resolves the same contact to a slightly
        /// different rest separation in different processes (limitations.md #14): measured max |delta| between
        /// processes on SampleScene is 0.0125 units over 400 ticks, ~0.17 over 754. 0.25 units — a quarter of a
        /// tile — is comfortably above that and far below any gameplay-relevant distance (tile 1.0, player
        /// capsule ~0.6 wide). Use for verifying a replay recorded by a DIFFERENT process; same-process
        /// verification stays exact (tolerance 0).
        /// </summary>
        public const float CrossProcessTolerance = 0.25f;

        /// <param name="toleranceUnits">0 (default) = exact on the 1e-4 quantization grid, including the state
        /// hash. &gt;0 = position/velocity must agree within this many units and the hash is not compared; flags
        /// and dash count still must match exactly. See <see cref="CrossProcessTolerance"/>.</param>
        public ReplayVerifier(IReadOnlyList<KeyframeRecord> recordedKeyframes, float toleranceUnits = 0f)
        {
            recorded = recordedKeyframes ?? new List<KeyframeRecord>();
            tolerance = toleranceUnits;
            report.FirstDesyncTick = -1;
        }

        public DesyncReport Report => report;

        /// <summary>Live per-tick hook. Compares against the recorded keyframe for this tick, if one exists.</summary>
        public void OnStep(int tick, Observation obs)
        {
            // Advance past any recorded keyframe ticks the live run skipped over.
            while (cursor < recorded.Count && recorded[cursor].t < tick)
                cursor++;

            if (cursor >= recorded.Count || recorded[cursor].t != tick)
                return;

            KeyframeRecord rec = recorded[cursor];
            cursor++;

            int flags = StateFlags.From(obs);
            long px = StateHash.Quantize(obs.Position.x), py = StateHash.Quantize(obs.Position.y);
            long vx = StateHash.Quantize(obs.Velocity.x), vy = StateHash.Quantize(obs.Velocity.y);
            ulong actualHash = StateHash.Compute(obs.Position, obs.Velocity, flags, obs.DashesRemaining);

            bool stateMatch = tolerance > 0f
                ? Near(rec.px, obs.Position.x) && Near(rec.py, obs.Position.y)
                  && Near(rec.vx, obs.Velocity.x) && Near(rec.vy, obs.Velocity.y)
                : px == StateHash.Quantize(rec.px) && py == StateHash.Quantize(rec.py)
                  && vx == StateHash.Quantize(rec.vx) && vy == StateHash.Quantize(rec.vy)
                  && actualHash == rec.stateHash;

            bool match = stateMatch && flags == rec.flags && obs.DashesRemaining == rec.dashes;

            if (match || report.FirstDesyncTick != -1)
                return;

            report.FirstDesyncTick = tick;
            report.RecordedHash = rec.stateHash;
            report.ActualHash = actualHash;
            report.FieldDeltas = BuildDeltas(rec, px, py, vx, vy, flags, obs.DashesRemaining);
        }

        bool Near(float recorded, float actual) => Mathf.Abs(recorded - actual) <= tolerance;

        static string BuildDeltas(KeyframeRecord rec, long px, long py, long vx, long vy, int flags, int dashes)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(96);
            AppendIfDiff(sb, inv, "px", StateHash.Quantize(rec.px), px);
            AppendIfDiff(sb, inv, "py", StateHash.Quantize(rec.py), py);
            AppendIfDiff(sb, inv, "vx", StateHash.Quantize(rec.vx), vx);
            AppendIfDiff(sb, inv, "vy", StateHash.Quantize(rec.vy), vy);
            AppendIfDiff(sb, inv, "flags", rec.flags, flags);
            AppendIfDiff(sb, inv, "dashes", rec.dashes, dashes);
            return sb.ToString();
        }

        static void AppendIfDiff(StringBuilder sb, CultureInfo inv, string name, long rec, long act)
        {
            if (rec == act) return;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(name).Append(": rec=").Append(rec.ToString(inv)).Append(" act=").Append(act.ToString(inv));
        }
    }
}
