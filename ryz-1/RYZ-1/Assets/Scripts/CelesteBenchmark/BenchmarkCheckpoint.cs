using UnityEngine;

namespace CelesteBenchmark
{
    public sealed class BenchmarkCheckpoint : MonoBehaviour
    {
        [Tooltip("Explicit route order. Leave negative to let the game-specific scenario provider project the " +
                 "checkpoint onto the spawn-to-goal route.")]
        public int sectionOrder = -1;
        public Vector2 respawnOffset = new Vector2(0f, 0.35f);
        public Color inactiveColor = new Color(0.25f, 0.6f, 1f, 1f);
        public Color activeColor = new Color(1f, 0.95f, 0.25f, 1f);

        SpriteRenderer spriteRenderer;

        public int SectionOrder => sectionOrder;

        void Awake()
        {
            spriteRenderer = GetComponent<SpriteRenderer>();
            if (spriteRenderer)
                spriteRenderer.color = inactiveColor;
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.TryGetComponent(out CelesteBenchmarkPlayer player))
                return;

            player.SetCheckpoint((Vector2)transform.position + respawnOffset);
            if (spriteRenderer)
                spriteRenderer.color = activeColor;
        }
    }
}
