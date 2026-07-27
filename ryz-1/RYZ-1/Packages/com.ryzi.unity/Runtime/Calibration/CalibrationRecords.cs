using System;

namespace Ryzi
{
    [Serializable]
    public sealed class CalibrationProbeResult
    {
        public string probeId;
        public bool completed;
        public UniversalObservation before;
        public UniversalAction[] actions = Array.Empty<UniversalAction>();
        public UniversalObservation after;
        public PlaytestEvent[] events = Array.Empty<PlaytestEvent>();
        public float confidence;
        public string[] warnings = Array.Empty<string>();
    }

    [Serializable]
    public sealed class CalibrationReport
    {
        public bool completed;
        public bool cancelled;
        public bool stateRestored;
        public bool deterministicRepeatability;
        public long elapsedMilliseconds;
        public CalibrationProbeResult[] probes = Array.Empty<CalibrationProbeResult>();
        public string[] warnings = Array.Empty<string>();
    }
}
