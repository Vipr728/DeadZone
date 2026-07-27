using UnityEngine;

namespace CelesteBenchmark
{
    public sealed class BenchmarkSpring : MonoBehaviour
    {
        public Vector2 launchDirection = Vector2.up;
        public float launchSpeed = 18f;
        public bool refillDash = true;
        public float dashVelocityCarryMultiplier = 1f;
        public float regularVelocityCarryMultiplier = 0.15f;
        public float maxCarriedSpeed = 24f;

        void OnTriggerEnter2D(Collider2D other)
        {
            if (other.TryGetComponent(out CelesteBenchmarkPlayer player))
                player.Bounce(launchDirection, launchSpeed, refillDash, dashVelocityCarryMultiplier, regularVelocityCarryMultiplier, maxCarriedSpeed);
        }
    }
}
