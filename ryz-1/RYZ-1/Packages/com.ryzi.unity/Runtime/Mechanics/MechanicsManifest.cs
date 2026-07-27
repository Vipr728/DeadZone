using System;
using UnityEngine;

namespace Ryzi
{
    public enum EvidenceLevel
    {
        DeveloperDefined,
        SourceVerified,
        RuntimeVerified,
        SourceCandidate,
        RuntimeObserved,
        ModelSuggested,
        Unknown
    }

    [Serializable]
    public sealed class DiscoveryEvidence
    {
        public string id;
        public string summary;
        public string source;
        public EvidenceLevel level;
        [Range(0f, 1f)] public float weight;
    }

    [Serializable]
    public sealed class SourceEvidence
    {
        public string assetPath;
        public int line;
        public string symbol;
        public string summary;
        public EvidenceLevel level;
    }

    [Serializable]
    public sealed class RuntimeEvidence
    {
        public string probeId;
        public string summary;
        public float observedMagnitude;
        public EvidenceLevel level;
    }

    [Serializable]
    public sealed class ActionChannelDefinition
    {
        public string id;
        public string suggestedName;
        public string valueType;
        public bool supportsPressed;
        public bool supportsHeld;
        public bool supportsReleased;
        public float confidence;
        public EvidenceLevel evidenceLevel;
        public DiscoveryEvidence[] evidence = Array.Empty<DiscoveryEvidence>();
    }

    [Serializable]
    public sealed class ObservationChannelDefinition
    {
        public string id;
        public string valueType;
        public float confidence;
        public EvidenceLevel evidenceLevel;
    }

    [Serializable]
    public sealed class ResourceDefinition
    {
        public string id;
        public string suggestedName;
        public float confidence;
        public EvidenceLevel evidenceLevel;
    }

    [Serializable]
    public sealed class StateDefinition
    {
        public string id;
        public string suggestedName;
        public float confidence;
        public EvidenceLevel evidenceLevel;
    }

    [Serializable]
    public sealed class ActionPattern
    {
        public string[] channelIds = Array.Empty<string>();
        public string description;
    }

    [Serializable]
    public sealed class StatePredicate
    {
        public string channelId;
        public string operation;
        public float numericValue;
        public bool booleanValue;
    }

    [Serializable]
    public sealed class MechanicEffect
    {
        public string observationChannelId;
        public string effect;
    }

    [Serializable]
    public sealed class MechanicDefinition
    {
        public string id;
        public string suggestedName;
        public ActionPattern trigger = new ActionPattern();
        public StatePredicate[] preconditions = Array.Empty<StatePredicate>();
        public MechanicEffect[] effects = Array.Empty<MechanicEffect>();
        public StatePredicate[] terminationConditions = Array.Empty<StatePredicate>();
        public float staticConfidence;
        public float runtimeConfidence;
        public bool developerConfirmed;
        public SourceEvidence[] sourceEvidence = Array.Empty<SourceEvidence>();
        public RuntimeEvidence[] runtimeEvidence = Array.Empty<RuntimeEvidence>();
    }

    [Serializable]
    public sealed class EntityAffordanceDefinition
    {
        public string runtimeTypeId;
        public string suggestedName;
        public string[] flags = Array.Empty<string>();
        public float confidence;
        public EvidenceLevel evidenceLevel;
    }

    [Serializable]
    public sealed class TunableDefinition
    {
        public string id;
        public string displayName;
        public string valueType;
        public float currentNumericValue;
        public float confidence;
        public EvidenceLevel evidenceLevel;
    }

    [Serializable]
    public sealed class DiscoveryIssue
    {
        public string id;
        public string severity;
        public string summary;
        public string resolution;
    }

    [Serializable]
    public sealed class MechanicsManifest
    {
        public const string CurrentVersion = "1.0";

        public string manifestVersion = CurrentVersion;
        public string scenarioId;
        public string sourceFingerprint;
        public ActionChannelDefinition[] actions = Array.Empty<ActionChannelDefinition>();
        public ObservationChannelDefinition[] observations = Array.Empty<ObservationChannelDefinition>();
        public ResourceDefinition[] resources = Array.Empty<ResourceDefinition>();
        public StateDefinition[] states = Array.Empty<StateDefinition>();
        public MechanicDefinition[] mechanics = Array.Empty<MechanicDefinition>();
        public EntityAffordanceDefinition[] affordances = Array.Empty<EntityAffordanceDefinition>();
        public TunableDefinition[] tunables = Array.Empty<TunableDefinition>();
        public DiscoveryIssue[] issues = Array.Empty<DiscoveryIssue>();
    }
}
