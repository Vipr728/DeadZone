using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Buffers one episode's telemetry and flushes it once, at episode end, under
    /// <see cref="PlaytestPaths.RunDir"/>. Fed from <see cref="EpisodeRunner"/>'s per-tick onStep callback:
    /// every tick appends an action line; keyframes are sampled every <see cref="KeyframeInterval"/> ticks and on
    /// any event tick; full frame records are buffered only when <c>recordFullTrajectory</c> is set. Memory is
    /// bounded by episode length; no writes ever land under Assets/.
    /// </summary>
    public sealed class TelemetryRecorder
    {
        public const int KeyframeInterval = 30;

        readonly string runId;
        readonly string episodeId;
        readonly bool recordFullTrajectory;

        readonly StringBuilder actionBuffer = new StringBuilder(4096);
        readonly List<FrameRecord> frames;
        readonly List<KeyframeRecord> keyframes = new List<KeyframeRecord>();

        bool flushed;

        public TelemetryRecorder(string runId, string episodeId, bool recordFullTrajectory)
        {
            this.runId = runId;
            this.episodeId = episodeId;
            this.recordFullTrajectory = recordFullTrajectory;
            frames = recordFullTrajectory ? new List<FrameRecord>(512) : null;
        }

        /// <summary>Keyframes captured this episode. Also consumed in-memory by <see cref="ReplayVerifier"/>.</summary>
        public IReadOnlyList<KeyframeRecord> Keyframes => keyframes;

        /// <summary>Record one tick. Pass a non-null <paramref name="eventLabel"/> (e.g. "Death") to force a keyframe.</summary>
        public void OnStep(int tick, in PlayerAction action, Observation obs, string eventLabel = null)
        {
            if (actionBuffer.Length > 0)
                actionBuffer.Append('\n');
            ActionStreamIO.WriteLine(actionBuffer, tick, in action);

            int flags = StateFlags.From(obs);

            if (recordFullTrajectory)
            {
                frames.Add(new FrameRecord
                {
                    t = tick,
                    px = obs.Position.x, py = obs.Position.y,
                    vx = obs.Velocity.x, vy = obs.Velocity.y,
                    flags = flags,
                    dashes = obs.DashesRemaining,
                    stamina = obs.Stamina,
                    progress = obs.Progress,
                    section = obs.SectionIndex,
                    ev = eventLabel
                });
            }

            bool isEvent = eventLabel != null;
            if (tick % KeyframeInterval == 0 || isEvent)
            {
                keyframes.Add(new KeyframeRecord
                {
                    t = tick,
                    px = obs.Position.x, py = obs.Position.y,
                    vx = obs.Velocity.x, vy = obs.Velocity.y,
                    flags = flags,
                    dashes = obs.DashesRemaining,
                    stateHash = StateHash.Compute(obs.Position, obs.Velocity, flags, obs.DashesRemaining)
                });
            }
        }

        /// <summary>Write buffered files once. Idempotent — a second call is a no-op.</summary>
        public void Flush()
        {
            if (flushed)
                return;
            flushed = true;

            string dir = PlaytestPaths.RunDir(runId);
            File.WriteAllText(Path.Combine(dir, $"ep_{episodeId}.actions.jsonl"), actionBuffer.ToString());
            File.WriteAllText(Path.Combine(dir, $"ep_{episodeId}.keyframes.jsonl"), SerializeKeyframes());
            if (recordFullTrajectory)
                File.WriteAllText(Path.Combine(dir, $"ep_{episodeId}.frames.jsonl"), SerializeFrames());
        }

        string SerializeKeyframes()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(keyframes.Count * 96);
            for (int i = 0; i < keyframes.Count; i++)
            {
                KeyframeRecord k = keyframes[i];
                if (i > 0) sb.Append('\n');
                sb.Append("{\"t\":").Append(k.t.ToString(inv))
                  .Append(",\"px\":").Append(k.px.ToString("G9", inv))
                  .Append(",\"py\":").Append(k.py.ToString("G9", inv))
                  .Append(",\"vx\":").Append(k.vx.ToString("G9", inv))
                  .Append(",\"vy\":").Append(k.vy.ToString("G9", inv))
                  .Append(",\"flags\":").Append(k.flags.ToString(inv))
                  .Append(",\"dashes\":").Append(k.dashes.ToString(inv))
                  .Append(",\"stateHash\":").Append(k.stateHash.ToString(inv))
                  .Append(",\"kf\":true}");
            }
            return sb.ToString();
        }

        string SerializeFrames()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(frames.Count * 112);
            for (int i = 0; i < frames.Count; i++)
            {
                FrameRecord f = frames[i];
                if (i > 0) sb.Append('\n');
                sb.Append("{\"t\":").Append(f.t.ToString(inv))
                  .Append(",\"px\":").Append(f.px.ToString("G9", inv))
                  .Append(",\"py\":").Append(f.py.ToString("G9", inv))
                  .Append(",\"vx\":").Append(f.vx.ToString("G9", inv))
                  .Append(",\"vy\":").Append(f.vy.ToString("G9", inv))
                  .Append(",\"flags\":").Append(f.flags.ToString(inv))
                  .Append(",\"dashes\":").Append(f.dashes.ToString(inv))
                  .Append(",\"stamina\":").Append(f.stamina.ToString("G9", inv))
                  .Append(",\"progress\":").Append(f.progress.ToString("G9", inv))
                  .Append(",\"section\":").Append(f.section.ToString(inv));
                if (f.ev != null)
                    sb.Append(",\"ev\":\"").Append(f.ev).Append('"');
                sb.Append('}');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Run-level writer: emits run.json once and appends one line per episode to episodes.jsonl, both under
    /// <see cref="PlaytestPaths.RunDir"/>. Uses <see cref="UnityEngine.JsonUtility"/> for the header/summary rows.
    /// </summary>
    public sealed class RunWriter
    {
        readonly string runId;

        public RunWriter(string runId)
        {
            this.runId = runId;
        }

        /// <summary>Write (overwrite) run.json.</summary>
        public void WriteHeader(RunHeader header)
        {
            string dir = PlaytestPaths.RunDir(runId);
            File.WriteAllText(Path.Combine(dir, "run.json"), UnityEngine.JsonUtility.ToJson(header));
        }

        /// <summary>Append one episode summary line to episodes.jsonl.</summary>
        public void AppendEpisode(EpisodeSummary summary)
        {
            string dir = PlaytestPaths.RunDir(runId);
            File.AppendAllText(Path.Combine(dir, "episodes.jsonl"), UnityEngine.JsonUtility.ToJson(summary) + "\n");
        }
    }
}
