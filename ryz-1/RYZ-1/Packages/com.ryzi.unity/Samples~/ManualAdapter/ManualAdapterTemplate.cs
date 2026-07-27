using System;
using System.Collections.Generic;
using Ryzi;
using UnityEngine;

namespace Ryzi.Samples
{
    /// <summary>
    /// Compile-time template for Tier 3 integrations. Subclasses bind these methods to their game's explicit API.
    /// This abstract type is intentionally not a compatibility implementation and cannot be attached directly.
    /// </summary>
    public abstract class ManualAdapterTemplate : MonoBehaviour, IPlaytestGameAdapter
    {
        readonly List<PlaytestEvent> events = new List<PlaytestEvent>();

        public abstract UniversalObservation Observe();
        public abstract void ApplyAction(in UniversalAction action);
        public abstract void ResetEpisode(in EpisodeResetContext context);
        public abstract EpisodeStatus GetEpisodeStatus();
        public abstract float GetProgress();

        public IReadOnlyList<PlaytestEvent> DrainEvents()
        {
            PlaytestEvent[] snapshot = events.ToArray();
            events.Clear();
            return snapshot;
        }

        protected void RecordEvent(PlaytestEvent value) => events.Add(value);
    }
}
