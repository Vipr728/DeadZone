using CelesteBenchmark;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>Builds a minimal in-code level (ground strip + player, optional spike) for PlayMode tests, no scene asset.</summary>
    static class TestLevelBuilder
    {
        public const int GroundLayer = 7;
        public const int OneWayLayer = 9;
        public const int HazardLayer = 10;

        public static readonly Vector2 SpawnPosition = new Vector2(0f, 1f);
        public static readonly Rect GoalRect = new Rect(9.5f, 2.5f, 1f, 1f);

        // One-way isolation level: player rests on a one-way platform with empty space below, so a
        // successful drop-through is unambiguous (player Y falls well past the platform surface).
        public static readonly Vector2 OneWaySpawn = new Vector2(0f, 1f);
        public const float OneWayPlatformY = 0f;
        public const float OneWayPlatformTop = 0.5f; // box half-height 0.5 (mirrors ground-tile geometry)

        // Ledge requiring a real jump (grounded/coyote path), placed with a dash refill just before it.
        const float LedgeX = 6f;
        const float LedgeHeight = 2f;

        public static void Build(Scene scene, bool withSpike)
        {
            GameObject groundRoot = new GameObject("Ground");
            SceneManager.MoveGameObjectToScene(groundRoot, scene);
            for (int i = -2; i < 20; i++)
            {
                bool raised = i + 0.5f >= LedgeX;
                CreateGroundTile(groundRoot.transform, new Vector2(i + 0.5f, raised ? LedgeHeight : 0f));
                // Fill the column beneath the raised ledge so the wall face at x=LedgeX is solid from the
                // lower ground up. Without this, the floating ledge tile leaves a gap under it that the
                // player's head wedges into (blocking advance, wall-detection, and jump).
                if (raised)
                    CreateGroundTile(groundRoot.transform, new Vector2(i + 0.5f, LedgeHeight - 1f));
            }

            GameObject refill = new GameObject("DashRefill");
            refill.transform.position = new Vector2(LedgeX - 1.5f, 1f);
            BoxCollider2D refillCollider = refill.AddComponent<BoxCollider2D>();
            refillCollider.isTrigger = true;
            refill.AddComponent<BenchmarkDashRefill>();
            SceneManager.MoveGameObjectToScene(refill, scene);

            GameObject playerObject = CreatePlayer();
            SceneManager.MoveGameObjectToScene(playerObject, scene);

            if (withSpike)
            {
                GameObject spike = new GameObject("Spike");
                spike.layer = HazardLayer;
                spike.transform.position = new Vector2(4.5f, 0.5f);
                BoxCollider2D col = spike.AddComponent<BoxCollider2D>();
                col.isTrigger = true;
                spike.AddComponent<BenchmarkSpike>();
                SceneManager.MoveGameObjectToScene(spike, scene);
            }
        }

        /// <summary>Builds a lone one-way platform (PlatformEffector2D, layer OneWay) with the player resting on
        /// it and empty space beneath. Drop-through (down+jump) drops the player off the bottom.</summary>
        public static void BuildOneWay(Scene scene)
        {
            // Thin platform (like real one-way ledges) with its top at y=OneWayPlatformTop. Thickness matters:
            // during drop-through the grounded player only sinks at the ~1u/s "stick" velocity, so a thick slab
            // would never be cleared inside the drop window and the player would pop back on top.
            GameObject plat = new GameObject("OneWayPlatform");
            plat.layer = OneWayLayer;
            plat.transform.position = new Vector2(0f, OneWayPlatformTop - 0.1f);
            BoxCollider2D col = plat.AddComponent<BoxCollider2D>();
            col.size = new Vector2(20f, 0.2f);
            col.usedByEffector = true;
            PlatformEffector2D effector = plat.AddComponent<PlatformEffector2D>();
            effector.useOneWay = true;
            SceneManager.MoveGameObjectToScene(plat, scene);

            GameObject playerObject = CreatePlayer();
            playerObject.transform.position = OneWaySpawn;
            CelesteBenchmarkPlayer controller = playerObject.GetComponent<CelesteBenchmarkPlayer>();
            controller.startPosition = OneWaySpawn;
            // Grounded descent is clamped to ~1u/s, so the default 0.22s drop window barely clears a 0.2-thick
            // platform. Widen it so the drop-through unambiguously carries the player off the bottom.
            controller.dropThroughDuration = 1f;
            SceneManager.MoveGameObjectToScene(playerObject, scene);
        }

        public static ScenarioConfig MakeOneWayScenario()
        {
            ScenarioConfig scenario = ScriptableObject.CreateInstance<ScenarioConfig>();
            scenario.spawnPosition = OneWaySpawn;
            scenario.goalRect = new Rect(1000f, 1000f, 1f, 1f); // unreachable: keep episode running
            scenario.sectionBoundariesX = new float[0];
            scenario.stepBudget = 2000;
            scenario.fixedDeltaTime = 0.02f;
            return scenario;
        }

        static void CreateGroundTile(Transform parent, Vector2 position)
        {
            GameObject tile = new GameObject("Ground Tile");
            tile.layer = GroundLayer;
            tile.transform.SetParent(parent);
            tile.transform.position = position;
            tile.AddComponent<BoxCollider2D>();
        }

        static GameObject CreatePlayer()
        {
            GameObject player = new GameObject("Player");
            player.layer = 6;
            player.transform.position = SpawnPosition;

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
            controller.startPosition = player.transform.position;
            controller.groundMask = LayerMask.GetMask("Ground", "MovingPlatform", "Crumble");
            controller.wallMask = LayerMask.GetMask("Ground", "MovingPlatform", "Crumble");
            controller.oneWayPlatformMask = LayerMask.GetMask("OneWay");
            return player;
        }

        public static ScenarioConfig MakeScenario()
        {
            ScenarioConfig scenario = ScriptableObject.CreateInstance<ScenarioConfig>();
            scenario.spawnPosition = SpawnPosition;
            scenario.goalRect = GoalRect;
            scenario.sectionBoundariesX = new float[0];
            scenario.stepBudget = 2000;
            scenario.fixedDeltaTime = 0.02f;
            return scenario;
        }
    }
}
