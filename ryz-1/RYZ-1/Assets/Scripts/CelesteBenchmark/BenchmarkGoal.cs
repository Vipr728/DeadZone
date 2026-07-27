using UnityEngine;

namespace CelesteBenchmark
{
    /// <summary>
    /// Declares an actual level objective in world space. The playtest adapter discovers these markers from the
    /// loaded arena instead of assuming that a particular coordinate or GameObject name is the finish.
    ///
    /// Priority resolves intentionally layered objectives (for example, a generated route extending an authored
    /// route). The highest priority wins; equal-priority ambiguity is rejected by scenario discovery.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider2D))]
    public sealed class BenchmarkGoal : MonoBehaviour
    {
        [SerializeField] int priority;

        Collider2D cachedCollider;

        public int Priority
        {
            get => priority;
            set => priority = value;
        }

        public Collider2D Trigger
        {
            get
            {
                if (!cachedCollider)
                    cachedCollider = GetComponent<Collider2D>();
                return cachedCollider;
            }
        }

        public Rect WorldRect
        {
            get
            {
                Bounds bounds = Trigger.bounds;
                return new Rect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y);
            }
        }
    }
}
