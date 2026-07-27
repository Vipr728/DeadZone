using UnityEngine;

namespace CelesteBenchmark
{
    public sealed class BenchmarkSpike : MonoBehaviour
    {
        public bool respawnOnTouch = true;

        void OnTriggerEnter2D(Collider2D other)
        {
            if (respawnOnTouch && other.TryGetComponent(out CelesteBenchmarkPlayer player))
                player.Respawn();
        }
    }
}
