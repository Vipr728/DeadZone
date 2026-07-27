using UnityEngine;

namespace PlatformerPlaytest.Live
{
    /// <summary>
    /// Minimal follow camera for T10 watch mode. CelesteBenchmark already has BenchmarkCameraFollow that does the
    /// same thing, but Runtime/ may only reference CelesteBenchmark from Runtime/Adapter/ (module-boundaries.md),
    /// so this is a tiny standalone duplicate rather than a boundary violation.
    /// </summary>
    public sealed class LiveFollowCamera : MonoBehaviour
    {
        public Transform Target;
        public Vector3 Offset = new Vector3(0f, 1.5f, -10f);
        public float FollowSpeed = 8f;

        void LateUpdate()
        {
            if (!Target)
                return;
            Vector3 desired = Target.position + Offset;
            transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-FollowSpeed * Time.deltaTime));
        }
    }
}
