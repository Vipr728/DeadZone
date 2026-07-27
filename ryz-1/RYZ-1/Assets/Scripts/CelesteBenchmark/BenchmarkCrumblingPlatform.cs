using System.Collections;
using UnityEngine;

namespace CelesteBenchmark
{
    [RequireComponent(typeof(Collider2D))]
    public sealed class BenchmarkCrumblingPlatform : MonoBehaviour
    {
        public float crumbleDelay = 0.28f;
        public float respawnDelay = 2.2f;
        public bool respawn = true;

        Collider2D platformCollider;
        SpriteRenderer[] renderers;
        bool crumbling;
        bool tickDriven;
        float crumbleRemaining;
        float respawnRemaining;

        /// <summary>When true, the crumble/respawn delays run off manual Tick(dt) instead of WaitForSeconds
        /// (arena stepping). Keyboard mode keeps the coroutine path.</summary>
        public void SetTickDriven(bool value)
        {
            tickDriven = value;
        }

        void Awake()
        {
            platformCollider = GetComponent<Collider2D>();
            renderers = GetComponentsInChildren<SpriteRenderer>();
        }

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (crumbling || !collision.collider.TryGetComponent(out CelesteBenchmarkPlayer _))
                return;

            crumbling = true;
            if (tickDriven)
                crumbleRemaining = crumbleDelay;
            else
                StartCoroutine(CrumbleRoutine());
        }

        /// <summary>Coroutine-free countdown, callable manually under arena stepping. Inert unless tick-driven.</summary>
        public void Tick(float dt)
        {
            if (!tickDriven)
                return;

            if (crumbleRemaining > 0f)
            {
                crumbleRemaining -= dt;
                if (crumbleRemaining <= 0f)
                {
                    crumbleRemaining = 0f;
                    Collapse();
                    respawnRemaining = respawn ? respawnDelay : 0f;
                }
                return;
            }

            if (respawnRemaining > 0f)
            {
                respawnRemaining -= dt;
                if (respawnRemaining <= 0f)
                {
                    respawnRemaining = 0f;
                    Restore();
                    crumbling = false;
                }
            }
        }

        IEnumerator CrumbleRoutine()
        {
            yield return new WaitForSeconds(crumbleDelay);

            Collapse();

            if (respawn)
            {
                yield return new WaitForSeconds(respawnDelay);
                Restore();
                crumbling = false;
            }
        }

        void Collapse()
        {
            platformCollider.enabled = false;
            SetRenderers(false);
        }

        void Restore()
        {
            platformCollider.enabled = true;
            SetRenderers(true);
        }

        void SetRenderers(bool enabled)
        {
            foreach (SpriteRenderer spriteRenderer in renderers)
            {
                if (spriteRenderer)
                    spriteRenderer.enabled = enabled;
            }
        }

        /// <summary>Stops any pending crumble/respawn and restores the platform to its intact state. Used by episode resets.</summary>
        public void ResetState()
        {
            StopAllCoroutines();
            crumbling = false;
            crumbleRemaining = 0f;
            respawnRemaining = 0f;
            Restore();
        }
    }
}
