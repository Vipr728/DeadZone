using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryzi.Editor
{
    public sealed class DiscoveryCandidate<T>
    {
        public T Value { get; }
        public float Confidence { get; }
        public IReadOnlyList<DiscoveryEvidence> Evidence { get; }

        public DiscoveryCandidate(T value, float confidence, IReadOnlyList<DiscoveryEvidence> evidence)
        {
            Value = value;
            Confidence = Mathf.Clamp01(confidence);
            Evidence = evidence ?? Array.Empty<DiscoveryEvidence>();
        }
    }

    public sealed class SceneDiscoveryResult
    {
        public string ScenePath { get; internal set; }
        public string SceneName { get; internal set; }
        public string Fingerprint { get; internal set; }
        public long DurationMilliseconds { get; internal set; }
        public bool SceneWasDirty { get; internal set; }
        public DiscoveryCandidate<GameObject>[] PlayerCandidates { get; internal set; } =
            Array.Empty<DiscoveryCandidate<GameObject>>();
        public DiscoveryCandidate<MonoBehaviour>[] MovementCandidates { get; internal set; } =
            Array.Empty<DiscoveryCandidate<MonoBehaviour>>();
        public DiscoveryCandidate<MonoBehaviour>[] ResetCandidates { get; internal set; } =
            Array.Empty<DiscoveryCandidate<MonoBehaviour>>();
        public DiscoveryCandidate<MonoBehaviour>[] DeathCandidates { get; internal set; } =
            Array.Empty<DiscoveryCandidate<MonoBehaviour>>();
        public DiscoveryCandidate<MonoBehaviour>[] CompletionCandidates { get; internal set; } =
            Array.Empty<DiscoveryCandidate<MonoBehaviour>>();
        public MechanicsManifest Manifest { get; internal set; }
        public string SelectedProviderId { get; internal set; }

        public DiscoveryCandidate<GameObject> SelectedPlayer =>
            PlayerCandidates.Length == 0 ? null : PlayerCandidates[0];
    }
}
