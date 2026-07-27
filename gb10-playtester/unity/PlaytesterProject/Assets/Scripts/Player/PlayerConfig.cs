using UnityEngine;

namespace Playtester
{
    [CreateAssetMenu(fileName = "PlayerConfig", menuName = "Playtester/Player Config")]
    public sealed class PlayerConfig : ScriptableObject
    {
        [field: SerializeField, Min(0.01f)] public float MoveSpeed { get; private set; }
        [field: SerializeField, Min(0.01f)] public float JumpImpulse { get; private set; }
        [field: SerializeField, Min(0.01f)] public float GroundedCheckDistance { get; private set; }
        [field: SerializeField] public LayerMask GroundLayers { get; private set; }
    }
}
