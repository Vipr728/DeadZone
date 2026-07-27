using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryzi
{
    public enum EpisodeStatus
    {
        Running,
        Completed,
        Failed,
        Cancelled,
        Error
    }

    [Serializable]
    public struct EpisodeResetContext
    {
        public string scenarioId;
        public int seed;
        public bool restoreDynamicEntities;
    }

    [Serializable]
    public struct PlaytestEvent
    {
        public int tick;
        public string eventId;
        public Vector2 position;
        public string details;
    }

    public interface IPlaytestGameAdapter
    {
        UniversalObservation Observe();
        void ApplyAction(in UniversalAction action);
        void ResetEpisode(in EpisodeResetContext context);
        EpisodeStatus GetEpisodeStatus();
        float GetProgress();
        IReadOnlyList<PlaytestEvent> DrainEvents();
    }

    public interface IPlaytestTunable
    {
        string Id { get; }
        string DisplayName { get; }
        object CaptureOriginalValue();
        void ApplyCandidate(object value);
        void RestoreOriginal(object original);
    }
}
