using UnityEngine;

namespace CelesteBenchmark
{
    /// <summary>
    /// Displays a short trail while the player is rising from a jump or actively dashing.
    /// Ordinary running and falling do not emit a trail.
    /// </summary>
    [RequireComponent(typeof(CelesteBenchmarkPlayer))]
    public sealed class PlayerActionTrail : MonoBehaviour
    {
        [Header("Timing")]
        [Min(0.01f)] public float trailLifetime = 0.18f;
        [Min(0f)] public float minimumJumpVelocity = 0.1f;

        [Header("Shape")]
        [Min(0f)] public float startWidth = 0.5f;
        [Min(0f)] public float endWidth = 0.05f;
        [Min(0.001f)] public float minimumVertexDistance = 0.05f;

        [Header("Color")]
        public Color startColor = new Color(0.15f, 0.9f, 1f, 0.85f);
        public Color endColor = new Color(0.35f, 0.55f, 1f, 0f);

        CelesteBenchmarkPlayer player;
        TrailRenderer trail;
        Material runtimeMaterial;

        void Awake()
        {
            player = GetComponent<CelesteBenchmarkPlayer>();
            trail = GetComponent<TrailRenderer>();
            if (trail == null)
                trail = gameObject.AddComponent<TrailRenderer>();

            ConfigureTrail();
            trail.emitting = false;
            trail.Clear();
        }

        void LateUpdate()
        {
            bool jumpingUp = !player.IsGrounded && player.Velocity.y > minimumJumpVelocity;
            bool shouldEmit = player.IsDashing || jumpingUp;

            if (trail.emitting == shouldEmit)
                return;

            trail.emitting = shouldEmit;
            if (!shouldEmit)
                trail.Clear();
        }

        void OnDisable()
        {
            if (trail == null)
                return;

            trail.emitting = false;
            trail.Clear();
        }

        void OnDestroy()
        {
            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);
        }

        void ConfigureTrail()
        {
            trail.time = trailLifetime;
            trail.startWidth = startWidth;
            trail.endWidth = endWidth;
            trail.minVertexDistance = minimumVertexDistance;
            trail.alignment = LineAlignment.View;
            trail.textureMode = LineTextureMode.Stretch;
            trail.numCornerVertices = 2;
            trail.numCapVertices = 2;
            trail.sortingOrder = -1;

            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(startColor, 0f),
                    new GradientColorKey(endColor, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(startColor.a, 0f),
                    new GradientAlphaKey(endColor.a, 1f)
                });
            trail.colorGradient = gradient;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                runtimeMaterial = new Material(shader)
                {
                    name = "Player Action Trail (Runtime)",
                    hideFlags = HideFlags.DontSave
                };
                trail.sharedMaterial = runtimeMaterial;
            }
        }
    }
}
