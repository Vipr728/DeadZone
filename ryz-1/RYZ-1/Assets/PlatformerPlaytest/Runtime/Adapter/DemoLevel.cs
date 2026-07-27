using CelesteBenchmark;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Shared in-code demo level (T8 batch runner + T10 live watch). Lives under Runtime/Adapter/ — the one place
    /// besides VirtualInputSource allowed to reference CelesteBenchmark types (module-boundaries.md) — so both the
    /// Editor batch button and the Editor live-watch panel build the exact same geometry instead of drifting
    /// copies. Ground strip rising to a 2-unit ledge at x=6, a dash refill just before it, plus a spike pit
    /// (T10: added so deaths actually happen and the death heatmap has data) placed where an under-timed jump
    /// at the ledge lands short.
    /// </summary>
    public static class DemoLevel
    {
        const int GroundLayer = 7;
        const int HazardLayer = 10;
        static readonly Vector2 Spawn = new Vector2(0f, 1f);
        static readonly Rect Goal = new Rect(9.5f, 2.5f, 1f, 1f);
        const float LedgeX = 6f;
        const float LedgeHeight = 2f;

        public static void Build(Scene scene)
        {
            GameObject groundRoot = new GameObject("Ground");
            SceneManager.MoveGameObjectToScene(groundRoot, scene);
            for (int i = -2; i < 20; i++)
            {
                bool raised = i + 0.5f >= LedgeX;
                // Spike pit: skip the ground tile right before the ledge (x in [5,6)) so a short/mistimed jump
                // falls into the gap and touches the spike below instead of just landing early on flat ground.
                bool pit = !raised && i + 0.5f >= LedgeX - 1f && i + 0.5f < LedgeX;
                if (!pit)
                    CreateGroundTile(groundRoot.transform, new Vector2(i + 0.5f, raised ? LedgeHeight : 0f));
                if (raised)
                    CreateGroundTile(groundRoot.transform, new Vector2(i + 0.5f, LedgeHeight - 1f));
            }

            GameObject spike = new GameObject("Spike");
            spike.layer = HazardLayer;
            spike.transform.position = new Vector2(LedgeX - 0.5f, -1f);
            BoxCollider2D spikeCollider = spike.AddComponent<BoxCollider2D>();
            spikeCollider.isTrigger = true;
            spike.AddComponent<BenchmarkSpike>();
            SceneManager.MoveGameObjectToScene(spike, scene);

            GameObject refill = new GameObject("DashRefill");
            refill.transform.position = new Vector2(LedgeX - 1.5f, 1f);
            BoxCollider2D refillCollider = refill.AddComponent<BoxCollider2D>();
            refillCollider.isTrigger = true;
            refill.AddComponent<BenchmarkDashRefill>();
            SceneManager.MoveGameObjectToScene(refill, scene);

            GameObject player = new GameObject("Player");
            player.layer = 6;
            player.transform.position = Spawn;
            Rigidbody2D rb = player.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Dynamic;
            rb.gravityScale = 0f;
            rb.freezeRotation = true;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            CapsuleCollider2D capsule = player.AddComponent<CapsuleCollider2D>();
            capsule.size = new Vector2(0.72f, 1.05f);
            capsule.direction = CapsuleDirection2D.Vertical;
            CelesteBenchmarkPlayer controller = player.AddComponent<CelesteBenchmarkPlayer>();
            controller.startPosition = Spawn;
            controller.groundMask = LayerMask.GetMask("Ground", "MovingPlatform", "Crumble");
            controller.wallMask = LayerMask.GetMask("Ground", "MovingPlatform", "Crumble");
            controller.oneWayPlatformMask = LayerMask.GetMask("OneWay");
            SceneManager.MoveGameObjectToScene(player, scene);
        }

        static void CreateGroundTile(Transform parent, Vector2 position)
        {
            GameObject tile = new GameObject("Ground Tile");
            tile.layer = GroundLayer;
            tile.transform.SetParent(parent);
            tile.transform.position = position;
            tile.AddComponent<BoxCollider2D>();
        }

        public static ScenarioConfig MakeScenario()
        {
            ScenarioConfig scenario = ScriptableObject.CreateInstance<ScenarioConfig>();
            scenario.spawnPosition = Spawn;
            scenario.goalRect = Goal;
            scenario.sectionBoundariesX = new float[0];
            scenario.stepBudget = 2000;
            scenario.fixedDeltaTime = 0.02f;
            return scenario;
        }
    }
}
