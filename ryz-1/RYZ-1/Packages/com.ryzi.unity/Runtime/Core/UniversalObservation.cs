using System;
using UnityEngine;

namespace Ryzi
{
    [Serializable]
    public struct NumericChannel
    {
        public string id;
        public float value;
    }

    [Serializable]
    public struct BooleanChannel
    {
        public string id;
        public bool value;
    }

    [Serializable]
    public struct SurfaceSegment
    {
        public Vector2 start;
        public Vector2 end;
        public Vector2 normal;
        public bool oneWay;
        public bool hazard;
        public bool climbable;
    }

    [Serializable]
    public struct DynamicEntityObservation
    {
        public string runtimeTypeId;
        public Vector2 relativePosition;
        public Vector2 velocity;
        public Vector2 size;
        public NumericChannel[] numericState;
        public BooleanChannel[] stateFlags;
        public string[] relations;
    }

    [Serializable]
    public sealed class UniversalObservation
    {
        public Vector2 position;
        public Vector2 velocity;
        public int facing;
        public bool grounded;
        public bool wallLeft;
        public bool wallRight;
        public string movementStateId;
        public NumericChannel[] healthChannels = Array.Empty<NumericChannel>();
        public NumericChannel[] resourceChannels = Array.Empty<NumericChannel>();
        public NumericChannel[] cooldownChannels = Array.Empty<NumericChannel>();
        public BooleanChannel[] stateFlags = Array.Empty<BooleanChannel>();
        public string regionId;
        public float progress;
        public string[] recentActionIds = Array.Empty<string>();
        public string[] recentEventIds = Array.Empty<string>();
        public byte[] localOccupancy = Array.Empty<byte>();
        public SurfaceSegment[] surfaces = Array.Empty<SurfaceSegment>();
        public Vector2[] collisionNormals = Array.Empty<Vector2>();
        public string[] navigableRegionIds = Array.Empty<string>();
        public DynamicEntityObservation[] dynamicEntities = Array.Empty<DynamicEntityObservation>();
    }
}
