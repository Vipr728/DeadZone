using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlatformerPlaytest.Live
{
    /// <summary>
    /// Makes an arena watchable (T10). Batch-run arenas have colliders but no renderers/camera by design (fast,
    /// headless); this adds a cheap sprite per collider and a follow camera without touching the arena's
    /// transforms/colliders (each visual is a child "Visual" GameObject, scaled to the collider's local bounds).
    /// </summary>
    public static class VisibleLevelBuilder
    {
        const int GroundLayer = 7;
        const int MovingPlatformLayer = 8;
        const int OneWayLayer = 9;
        const int HazardLayer = 10;
        const int CrumbleLayer = 12;
        const int PlayerLayer = 6;

        static Sprite whiteSprite;

        // One immutable 1x1-white sprite, created once and reused for every visual — not mutable shared state,
        // just an asset cache (a fresh Texture2D/Sprite per collider would be wasteful and leak in edit/play mode).
        static Sprite WhiteSprite
        {
            get
            {
                if (whiteSprite != null)
                    return whiteSprite;
                Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.DontSave;
                Color32[] pixels = new Color32[16];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(pixels);
                tex.Apply();
                whiteSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                whiteSprite.hideFlags = HideFlags.DontSave;
                return whiteSprite;
            }
        }

        public static void AddVisuals(Scene arenaScene)
        {
            GameObject[] roots = arenaScene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                AddVisualsRecursive(roots[i]);
        }

        static void AddVisualsRecursive(GameObject go)
        {
            if (go.GetComponent<SpriteRenderer>() == null && go.transform.Find("Visual") == null)
            {
                Collider2D col = go.GetComponent<Collider2D>();
                if (col != null)
                    AddVisual(go, col);
            }

            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                AddVisualsRecursive(t.GetChild(i).gameObject);
        }

        static void AddVisual(GameObject owner, Collider2D col)
        {
            Bounds localBounds = ColliderLocalBounds(col);
            if (localBounds.size.x <= 0f || localBounds.size.y <= 0f)
                return;

            GameObject visual = new GameObject("Visual");
            visual.transform.SetParent(owner.transform, false);
            visual.transform.localPosition = localBounds.center;
            visual.transform.localScale = new Vector3(localBounds.size.x, localBounds.size.y, 1f);

            SpriteRenderer renderer = visual.AddComponent<SpriteRenderer>();
            renderer.sprite = WhiteSprite;
            renderer.color = ColorFor(owner.layer, col.isTrigger);
            renderer.sortingOrder = col.isTrigger ? 1 : 0;
        }

        static Bounds ColliderLocalBounds(Collider2D col)
        {
            switch (col)
            {
                case BoxCollider2D box:
                    return new Bounds(box.offset, box.size);
                case CapsuleCollider2D cap:
                    return new Bounds(cap.offset, cap.size);
                case CircleCollider2D circle:
                    return new Bounds(circle.offset, Vector2.one * circle.radius * 2f);
                default:
                    // Fallback: world bounds converted to local scale (approximate, good enough for a debug visual).
                    Bounds world = col.bounds;
                    return new Bounds(Vector3.zero, world.size);
            }
        }

        static Color ColorFor(int layer, bool isTrigger)
        {
            if (layer == PlayerLayer) return new Color(0.2f, 0.62f, 1f, 1f);   // cyan-ish, matches CelesteBenchmark palette
            if (layer == HazardLayer) return new Color(1f, 0.22f, 0.22f, 1f); // red
            if (layer == OneWayLayer) return new Color(0.25f, 0.52f, 0.78f, 1f); // blue
            if (layer == GroundLayer || layer == CrumbleLayer) return new Color(0.2f, 0.24f, 0.29f, 1f); // grey-blue
            if (layer == MovingPlatformLayer) return new Color(1f, 0.75f, 0.25f, 1f);
            if (isTrigger) return new Color(1f, 0.9f, 0.22f, 1f); // yellow: dash refill / checkpoint / other triggers
            return Color.grey;
        }

        public static Camera AddFollowCamera(Scene arenaScene, Transform target, float orthoSize = 7f, int depth = 10)
        {
            GameObject camGo = new GameObject("LiveWatchCamera");
            SceneManager.MoveGameObjectToScene(camGo, arenaScene);
            Camera cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = orthoSize;
            cam.depth = depth;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.1f, 0.13f, 1f);

            LiveFollowCamera follow = camGo.AddComponent<LiveFollowCamera>();
            follow.Target = target;
            if (target != null)
                camGo.transform.position = target.position + follow.Offset;

            return cam;
        }
    }
}
