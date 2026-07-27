using UnityEngine;

namespace CelesteBenchmark
{
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class BenchmarkMovingPlatform : MonoBehaviour
    {
        public Vector2 localPointA = Vector2.zero;
        public Vector2 localPointB = new Vector2(4f, 0f);
        public float speed = 2.5f;
        public float waitAtEnds = 0.25f;

        public Vector2 Velocity { get; private set; }

        Rigidbody2D rb;
        Vector2 origin;
        Vector2 previousPosition;
        float waitTimer;
        int direction = 1;
        bool tickDriven;
        bool resetTeleportPending;

        /// <summary>When true, FixedUpdate is inert and motion is driven by manual Tick(dt) (arena stepping).</summary>
        public void SetTickDriven(bool value)
        {
            tickDriven = value;
        }

        void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            origin = transform.position;
            previousPosition = rb.position;
        }

        void FixedUpdate()
        {
            if (tickDriven)
                return;

            Tick(Time.fixedDeltaTime);
        }

        /// <summary>FixedUpdate body, callable manually under arena stepping. Platform MovePosition happens here;
        /// the caller runs the physics Simulate afterwards (matching the old FixedUpdate to simulation ordering).</summary>
        public void Tick(float dt)
        {
            if (waitTimer > 0f)
            {
                waitTimer -= dt;
                Velocity = Vector2.zero;
                return;
            }

            Vector2 target = origin + (direction > 0 ? localPointB : localPointA);
            Vector2 next = Vector2.MoveTowards(rb.position, target, speed * dt);

            // Determinism (T11): ResetState's `rb.position = origin` queues a teleport that the next physics step
            // applies INSTEAD of a MovePosition issued in the same step, so the platform silently lost its first
            // step after every reset — episode 1 on a freshly loaded scene ran one tick ahead of episode 2 forever
            // (measured: player traces diverged at tick 596 of SampleScene, when the offset platform edge moved).
            // On the reset tick we therefore write the position directly, overriding that pending teleport.
            // Safe by construction: at reset the player is at spawn, never riding a platform.
            if (resetTeleportPending)
            {
                resetTeleportPending = false;
                rb.position = next;
                transform.position = next;
            }
            else
            {
                rb.MovePosition(next);
            }

            Velocity = (next - previousPosition) / dt;
            previousPosition = next;

            if ((next - target).sqrMagnitude <= 0.0001f)
            {
                direction *= -1;
                waitTimer = waitAtEnds;
            }
        }

        /// <summary>Restores the platform to its Awake-captured origin/state. Used by episode resets.</summary>
        public void ResetState()
        {
            rb.position = origin;
            transform.position = origin;
            previousPosition = origin;
            direction = 1;
            waitTimer = 0f;
            Velocity = Vector2.zero;
            resetTeleportPending = true;
        }

        void OnDrawGizmosSelected()
        {
            Vector2 originPoint = Application.isPlaying ? origin : (Vector2)transform.position;
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(originPoint + localPointA, originPoint + localPointB);
            Gizmos.DrawWireSphere(originPoint + localPointA, 0.15f);
            Gizmos.DrawWireSphere(originPoint + localPointB, 0.15f);
        }
    }
}
