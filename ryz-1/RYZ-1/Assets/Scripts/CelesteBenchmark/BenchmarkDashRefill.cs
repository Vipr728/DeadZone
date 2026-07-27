using System.Collections;
using UnityEngine;

namespace CelesteBenchmark
{
    [RequireComponent(typeof(Collider2D))]
    public sealed class BenchmarkDashRefill : MonoBehaviour
    {
        public float respawnTime = 2.5f;

        Collider2D triggerCollider;
        SpriteRenderer spriteRenderer;
        bool tickDriven;
        float respawnRemaining;

        /// <summary>When true, the respawn delay runs off manual Tick(dt) instead of WaitForSeconds
        /// (arena stepping). Keyboard mode keeps the coroutine path.</summary>
        public void SetTickDriven(bool value)
        {
            tickDriven = value;
        }

        void Awake()
        {
            triggerCollider = GetComponent<Collider2D>();
            triggerCollider.isTrigger = true;
            spriteRenderer = GetComponent<SpriteRenderer>();
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.TryGetComponent(out CelesteBenchmarkPlayer player))
                return;

            player.RefillDashAndStamina();
            SetAvailable(false);

            if (tickDriven)
                respawnRemaining = respawnTime;
            else
                StartCoroutine(RespawnRoutine());
        }

        /// <summary>Coroutine-free countdown, callable manually under arena stepping. Inert unless tick-driven.</summary>
        public void Tick(float dt)
        {
            if (!tickDriven || respawnRemaining <= 0f)
                return;

            respawnRemaining -= dt;
            if (respawnRemaining <= 0f)
            {
                respawnRemaining = 0f;
                SetAvailable(true);
            }
        }

        IEnumerator RespawnRoutine()
        {
            yield return new WaitForSeconds(respawnTime);
            SetAvailable(true);
        }

        void SetAvailable(bool available)
        {
            triggerCollider.enabled = available;
            if (spriteRenderer)
                spriteRenderer.enabled = available;
        }

        /// <summary>Stops any pending respawn and restores the refill to its available state. Used by episode resets.</summary>
        public void ResetState()
        {
            StopAllCoroutines();
            respawnRemaining = 0f;
            SetAvailable(true);
        }
    }
}
