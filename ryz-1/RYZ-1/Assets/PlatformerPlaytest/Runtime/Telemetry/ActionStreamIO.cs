using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Writes and parses the action stream (ep_&lt;id&gt;.actions.jsonl) — the replay source of truth (ADR-005).
    /// One flat JSON object per tick, false/zero fields omitted. Floats use invariant-culture "G9", which
    /// round-trips a 32-bit float exactly, so parse(write(x)) == x bit-for-bit.
    /// </summary>
    public static class ActionStreamIO
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Append one tick's action as a JSON line to <paramref name="sb"/> (no trailing newline).</summary>
        public static void WriteLine(StringBuilder sb, int tick, in PlayerAction a)
        {
            sb.Append("{\"t\":").Append(tick.ToString(Inv));
            if (a.MoveX != 0f) sb.Append(",\"mx\":").Append(a.MoveX.ToString("G9", Inv));
            if (a.MoveY != 0f) sb.Append(",\"my\":").Append(a.MoveY.ToString("G9", Inv));
            if (a.JumpPressed) sb.Append(",\"jp\":true");
            if (a.JumpHeld) sb.Append(",\"jh\":true");
            if (a.DashPressed) sb.Append(",\"dp\":true");
            if (a.ClimbHeld) sb.Append(",\"ch\":true");
            sb.Append('}');
        }

        /// <summary>Serialize a single action line to a string.</summary>
        public static string WriteLine(int tick, in PlayerAction a)
        {
            StringBuilder sb = new StringBuilder(64);
            WriteLine(sb, tick, in a);
            return sb.ToString();
        }

        /// <summary>Parse one action line into its tick and action. Missing keys default to neutral (0/false).</summary>
        public static PlayerAction ParseLine(string line, out int tick)
        {
            tick = 0;
            PlayerAction a = PlayerAction.Neutral;

            int open = line.IndexOf('{');
            int close = line.LastIndexOf('}');
            if (open < 0 || close <= open)
                return a;

            string body = line.Substring(open + 1, close - open - 1);
            int i = 0;
            while (i < body.Length)
            {
                int keyStart = body.IndexOf('"', i);
                if (keyStart < 0) break;
                int keyEnd = body.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                string key = body.Substring(keyStart + 1, keyEnd - keyStart - 1);

                int colon = body.IndexOf(':', keyEnd + 1);
                if (colon < 0) break;
                int valStart = colon + 1;
                int comma = body.IndexOf(',', valStart);
                int valEnd = comma < 0 ? body.Length : comma;
                string val = body.Substring(valStart, valEnd - valStart).Trim();
                i = comma < 0 ? body.Length : comma + 1;

                switch (key)
                {
                    case "t": tick = int.Parse(val, NumberStyles.Integer, Inv); break;
                    case "mx": a.MoveX = float.Parse(val, NumberStyles.Float, Inv); break;
                    case "my": a.MoveY = float.Parse(val, NumberStyles.Float, Inv); break;
                    case "jp": a.JumpPressed = val == "true"; break;
                    case "jh": a.JumpHeld = val == "true"; break;
                    case "dp": a.DashPressed = val == "true"; break;
                    case "ch": a.ClimbHeld = val == "true"; break;
                }
            }
            return a;
        }

        /// <summary>
        /// Parse a stream of action lines into a list indexed by tick; ticks with no line become neutral.
        /// </summary>
        public static List<PlayerAction> ParseIndexed(IEnumerable<string> lines)
        {
            List<PlayerAction> byTick = new List<PlayerAction>();
            foreach (string raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                PlayerAction a = ParseLine(raw, out int tick);
                if (tick < 0) continue;
                while (byTick.Count <= tick)
                    byTick.Add(PlayerAction.Neutral);
                byTick[tick] = a;
            }
            return byTick;
        }
    }
}
