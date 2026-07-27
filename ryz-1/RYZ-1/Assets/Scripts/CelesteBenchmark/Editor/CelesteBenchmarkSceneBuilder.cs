using System.IO;
using CelesteBenchmark;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace CelesteBenchmark.Editor
{
    [InitializeOnLoad]
    static class CelesteBenchmarkAutoBuild
    {
        const string AutoBuildKey = "CelesteBenchmark.AutoBuildSampleSceneV2";

        static CelesteBenchmarkAutoBuild()
        {
            EditorApplication.delayCall += TryAutoBuild;
            EditorApplication.delayCall += CelesteBenchmarkSceneBuilder.EnsureRandomLevelGeneratorInOpenScene;
        }

        static void TryAutoBuild()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryAutoBuild;
                return;
            }

            if (SessionState.GetBool(AutoBuildKey, false))
                return;

            if (SampleSceneExists())
            {
                SessionState.SetBool(AutoBuildKey, true);
                return;
            }

            CelesteBenchmarkSceneBuilder.BuildScene();
            SessionState.SetBool(AutoBuildKey, true);
        }

        static bool SampleSceneExists()
        {
            string scenePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", CelesteBenchmarkSceneBuilder.ScenePath));
            return File.Exists(scenePath);
        }
    }

    public static class CelesteBenchmarkSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string AssetRoot = "Assets/CelesteBenchmark";
        const int PlayerLayer = 6;
        const int GroundLayer = 7;
        const int MovingPlatformLayer = 8;
        const int OneWayLayer = 9;
        const int HazardLayer = 10;
        const int TriggerLayer = 11;

        static Sprite squareSprite;
        static Material fallbackSpriteMaterial;

        [MenuItem("Celeste Benchmark/Build Tile Benchmark Scene")]
        public static void BuildScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play Mode before rebuilding the Celeste benchmark scene.");
                return;
            }

            EnsureLayers();
            EnsureAssetFolders();
            squareSprite = EnsureSprite("BenchmarkSquare", new Color32(255, 255, 255, 255));

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "CelesteBenchmark";

            CreateLighting();
            GameObject player = CreatePlayer();
            CreateCamera(player.transform);
            CreateTileLevel();
            CreateMechanics();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Celeste benchmark scene built at {ScenePath}");
        }

        [MenuItem("Celeste Benchmark/Attach Random Level Generator to SampleScene")]
        public static void EnsureRandomLevelGeneratorInOpenScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != ScenePath)
                return;

            GameObject gridObject = null;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == "Tile Grid")
                {
                    gridObject = roots[i];
                    break;
                }
            }

            if (!gridObject)
            {
                Debug.LogWarning("Could not attach RandomLevelGenerator: SampleScene has no root named 'Tile Grid'.");
                return;
            }

            RandomLevelGenerator generator = gridObject.GetComponent<RandomLevelGenerator>();
            if (!generator)
            {
                generator = Undo.AddComponent<RandomLevelGenerator>(gridObject);
                generator.PlatformSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{AssetRoot}/Art/BenchmarkSquare.png");
                EditorUtility.SetDirty(generator);
                EditorSceneManager.MarkSceneDirty(scene);
                Debug.Log("Attached RandomLevelGenerator to SampleScene/Tile Grid.");
            }

            Selection.activeGameObject = gridObject;
            EditorGUIUtility.PingObject(gridObject);
        }

        static void CreateLighting()
        {
            GameObject lightObject = new GameObject("Global Light 2D");
            Light2D light = lightObject.AddComponent<Light2D>();
            light.lightType = Light2D.LightType.Global;
            light.intensity = 0.9f;
        }

        static GameObject CreatePlayer()
        {
            GameObject player = CreateSpriteBox("Player", new Vector2(-8f, -3.1f), new Vector2(0.8f, 1.1f), new Color(0.2f, 0.62f, 1f, 1f), PlayerLayer, false);
            Rigidbody2D rb = player.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Dynamic;
            rb.gravityScale = 0f;
            rb.freezeRotation = true;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;

            CapsuleCollider2D capsule = player.GetComponent<CapsuleCollider2D>();
            if (!capsule)
            {
                Object.DestroyImmediate(player.GetComponent<BoxCollider2D>());
                capsule = player.AddComponent<CapsuleCollider2D>();
            }

            capsule.size = new Vector2(0.72f, 1.05f);
            capsule.direction = CapsuleDirection2D.Vertical;

            CelesteBenchmarkPlayer controller = player.AddComponent<CelesteBenchmarkPlayer>();
            controller.startPosition = player.transform.position;
            controller.groundMask = LayerMask.GetMask("Ground", "MovingPlatform", "Crumble");
            controller.wallMask = LayerMask.GetMask("Ground", "MovingPlatform", "Crumble");
            controller.oneWayPlatformMask = LayerMask.GetMask("OneWay");
            player.AddComponent<PlayerActionTrail>();
            return player;
        }

        static void CreateCamera(Transform target)
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.1f, 0.13f, 1f);

            BenchmarkCameraFollow follow = cameraObject.AddComponent<BenchmarkCameraFollow>();
            follow.target = target;
            follow.offset = new Vector3(1.5f, 1.3f, -10f);
            follow.orthographicSize = 6f;
            cameraObject.transform.position = target.position + follow.offset;
        }

        static void CreateTileLevel()
        {
            GameObject gridObject = new GameObject("Tile Grid");
            Grid grid = gridObject.AddComponent<Grid>();
            grid.cellSize = Vector3.one;
            RandomLevelGenerator randomLevelGenerator = gridObject.AddComponent<RandomLevelGenerator>();
            randomLevelGenerator.PlatformSprite = squareSprite;

            GameObject solidTiles = new GameObject("Solid Ground Tilemap");
            solidTiles.transform.SetParent(gridObject.transform);
            FillSolidRect(solidTiles.transform, -12, -5, 26, 1);
            FillSolidRect(solidTiles.transform, 18, -5, 10, 1);
            FillSolidRect(solidTiles.transform, 30, -5, 4, 1);
            FillSolidRect(solidTiles.transform, 36, 2, 8, 1);
            FillSolidRect(solidTiles.transform, 47, -5, 10, 1);
            FillSolidRect(solidTiles.transform, 64, -5, 28, 1);
            FillSolidRect(solidTiles.transform, 96, -5, 16, 1);

            FillSolidRect(solidTiles.transform, 15, -5, 1, 5);
            FillSolidRect(solidTiles.transform, 22, -5, 1, 4);
            FillSolidRect(solidTiles.transform, 30, -4, 1, 8);
            FillSolidRect(solidTiles.transform, 35, -4, 1, 7);
            FillSolidRect(solidTiles.transform, 44, -4, 1, 11);
            FillSolidRect(solidTiles.transform, 50, -4, 1, 6);
            FillSolidRect(solidTiles.transform, 96, -4, 1, 8);

            FillSolidRect(solidTiles.transform, -14, -8, 130, 1);

            GameObject oneWayTiles = new GameObject("One-Way Platform Tilemap");
            oneWayTiles.transform.SetParent(gridObject.transform);
            FillOneWayRect(oneWayTiles.transform, 6, -2, 4, 1);
            FillOneWayRect(oneWayTiles.transform, 12, 0, 4, 1);
            FillOneWayRect(oneWayTiles.transform, 52, -2, 5, 1);
            FillOneWayRect(oneWayTiles.transform, 57, 1, 5, 1);
            FillOneWayRect(oneWayTiles.transform, 101, -1, 6, 1);
        }

        static void CreateMechanics()
        {
            CreateCheckpoint(new Vector2(-6f, -3.4f));
            CreateCheckpoint(new Vector2(19.5f, -3.4f));
            CreateCheckpoint(new Vector2(37.5f, 3.6f));
            CreateCheckpoint(new Vector2(66.5f, -3.4f));
            CreateCheckpoint(new Vector2(98.5f, -3.4f));

            CreateSpikeRow("Gap Spikes", 14.5f, -7.25f, 12);
            CreateSpikeRow("Moving Platform Spikes", 58.5f, -4.45f, 9);
            CreateSpikeRow("Late Spikes", 78.5f, -4.45f, 9);

            CreateSpring("Vertical Spring", new Vector2(10.5f, -3.55f), Vector2.up, 18f);
            CreateSpring("Diagonal Spring", new Vector2(90f, -3.55f), new Vector2(1f, 1.35f), 19f);

            CreateDashRefill(new Vector2(25.4f, -1.4f));
            CreateDashRefill(new Vector2(61.5f, 2.2f));
            CreateDashRefill(new Vector2(73f, -1f));
            CreateDashRefill(new Vector2(103.5f, 1.3f));

            CreateMovingPlatform("Horizontal Moving Platform", new Vector2(58f, -2.2f), new Vector2(0f, 0f), new Vector2(7f, 0f), 2.8f);
            CreateMovingPlatform("Vertical Moving Platform", new Vector2(71f, -3.2f), new Vector2(0f, 0f), new Vector2(0f, 4f), 2.3f);

            CreateCrumblingPlatform("Crumble 1", new Vector2(82f, -2.1f), 3);
            CreateCrumblingPlatform("Crumble 2", new Vector2(87f, -0.7f), 3);
            CreateCrumblingPlatform("Crumble 3", new Vector2(92f, 0.8f), 3);

            CreateFlag("Finish", new Vector2(107f, 0.1f));
        }

        static void CreateCheckpoint(Vector2 position)
        {
            GameObject checkpoint = CreateSpriteBox("Checkpoint Square", SnapToCellCenter(position), Vector2.one, new Color(0.25f, 0.6f, 1f, 1f), TriggerLayer, true);
            checkpoint.AddComponent<BenchmarkCheckpoint>();
        }

        static void CreateSpring(string name, Vector2 position, Vector2 direction, float speed)
        {
            GameObject spring = CreateSpriteBox($"{name} Square", SnapToCellCenter(position), Vector2.one, new Color(0.3f, 1f, 0.38f, 1f), TriggerLayer, true);
            BenchmarkSpring springComponent = spring.AddComponent<BenchmarkSpring>();
            springComponent.launchDirection = direction.normalized;
            springComponent.launchSpeed = speed;
            springComponent.dashVelocityCarryMultiplier = 1f;
            springComponent.regularVelocityCarryMultiplier = 0.15f;
            springComponent.maxCarriedSpeed = 24f;
        }

        static void CreateDashRefill(Vector2 position)
        {
            GameObject refill = CreateSpriteBox("Dash Refill Square", SnapToCellCenter(position), Vector2.one, new Color(0.1f, 0.95f, 1f, 1f), TriggerLayer, true);
            Object.DestroyImmediate(refill.GetComponent<BoxCollider2D>());
            CircleCollider2D circle = refill.AddComponent<CircleCollider2D>();
            circle.isTrigger = true;
            circle.radius = 0.48f;
            refill.AddComponent<BenchmarkDashRefill>();
        }

        static void CreateSpikeRow(string name, float startX, float y, int count)
        {
            GameObject root = new GameObject(name);
            root.layer = HazardLayer;
            for (int i = 0; i < count; i++)
            {
                GameObject spike = CreateSpriteBox("Spike Square", SnapToCellCenter(new Vector2(startX + i, y)), Vector2.one, new Color(1f, 0.22f, 0.22f, 1f), HazardLayer, true);
                spike.transform.SetParent(root.transform);
                spike.AddComponent<BenchmarkSpike>();
            }
        }

        static void CreateMovingPlatform(string name, Vector2 position, Vector2 pointA, Vector2 pointB, float speed)
        {
            GameObject platform = CreateSpriteBox($"{name} Collider", position, new Vector2(3f, 1f), new Color(1f, 0.75f, 0.25f, 0.2f), MovingPlatformLayer, false);
            Rigidbody2D rb = platform.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;

            for (int i = -1; i <= 1; i++)
            {
                GameObject tile = CreateSpriteBox("Moving Platform Square", new Vector2(position.x + i, position.y), Vector2.one, new Color(1f, 0.75f, 0.25f, 1f), MovingPlatformLayer, false);
                Object.DestroyImmediate(tile.GetComponent<BoxCollider2D>());
                tile.transform.SetParent(platform.transform);
            }

            BenchmarkMovingPlatform movingPlatform = platform.AddComponent<BenchmarkMovingPlatform>();
            movingPlatform.localPointA = pointA;
            movingPlatform.localPointB = pointB;
            movingPlatform.speed = speed;
        }

        static void CreateCrumblingPlatform(string name, Vector2 position, int tileCount)
        {
            GameObject root = new GameObject(name);
            root.layer = LayerMask.NameToLayer("Crumble");
            root.transform.position = position;

            BoxCollider2D collider = root.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(tileCount, 0.55f);

            Rigidbody2D rb = root.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Static;

            for (int i = 0; i < tileCount; i++)
            {
                GameObject tile = CreateSpriteBox("Crumble Square", new Vector2(position.x - (tileCount - 1) * 0.5f + i, position.y), Vector2.one, new Color(0.78f, 0.47f, 0.25f, 1f), root.layer, false);
                Object.DestroyImmediate(tile.GetComponent<BoxCollider2D>());
                tile.transform.SetParent(root.transform);
            }

            root.AddComponent<BenchmarkCrumblingPlatform>();
        }

        static void CreateFlag(string name, Vector2 position)
        {
            Vector2 snapped = SnapToCellCenter(position);
            GameObject pole = CreateSpriteBox($"{name} Square", snapped, Vector2.one, new Color(0.94f, 0.94f, 0.9f, 1f), TriggerLayer, true);
            pole.AddComponent<BenchmarkGoal>();
            GameObject banner = CreateSpriteBox("Flag Square", snapped + Vector2.up, Vector2.one, new Color(1f, 0.9f, 0.22f, 1f), TriggerLayer, true);
            banner.transform.SetParent(pole.transform);
        }

        static GameObject CreateSpriteBox(string name, Vector2 position, Vector2 size, Color color, int layer, bool trigger)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.layer = layer;
            gameObject.transform.position = position;
            gameObject.transform.localScale = new Vector3(size.x, size.y, 1f);

            SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
            renderer.sprite = squareSprite;
            renderer.sharedMaterial = GetFallbackSpriteMaterial();
            renderer.color = color;

            BoxCollider2D collider = gameObject.AddComponent<BoxCollider2D>();
            collider.isTrigger = trigger;
            return gameObject;
        }

        static void FillSolidRect(Transform parent, int x, int y, int width, int height)
        {
            FillSpriteTileRect(parent, x, y, width, height, "Solid Square", new Color(0.2f, 0.24f, 0.29f, 1f), GroundLayer, false);
        }

        static void FillOneWayRect(Transform parent, int x, int y, int width, int height)
        {
            FillSpriteTileRect(parent, x, y, width, height, "One-Way Square", new Color(0.25f, 0.52f, 0.78f, 1f), OneWayLayer, true);
        }

        static void FillSpriteTileRect(Transform parent, int x, int y, int width, int height, string name, Color color, int layer, bool oneWay)
        {
            for (int ix = x; ix < x + width; ix++)
            {
                for (int iy = y; iy < y + height; iy++)
                {
                    GameObject tile = CreateSpriteBox(name, new Vector2(ix + 0.5f, iy + 0.5f), Vector2.one, color, layer, false);
                    tile.transform.SetParent(parent);

                    if (oneWay)
                    {
                        Rigidbody2D rb = tile.AddComponent<Rigidbody2D>();
                        rb.bodyType = RigidbodyType2D.Static;

                        PlatformEffector2D effector = tile.AddComponent<PlatformEffector2D>();
                        effector.useOneWay = true;
                        tile.GetComponent<BoxCollider2D>().usedByEffector = true;
                    }
                }
            }
        }

        static Vector2 SnapToCellCenter(Vector2 position)
        {
            return new Vector2(Mathf.Floor(position.x) + 0.5f, Mathf.Floor(position.y) + 0.5f);
        }

        static Tile EnsureTile(string name, Color color)
        {
            string path = $"{AssetRoot}/Tiles/{name}.asset";
            Tile existing = AssetDatabase.LoadAssetAtPath<Tile>(path);
            if (existing)
                return existing;

            Tile tile = ScriptableObject.CreateInstance<Tile>();
            tile.sprite = squareSprite;
            tile.color = color;
            tile.colliderType = Tile.ColliderType.Grid;
            AssetDatabase.CreateAsset(tile, path);
            return tile;
        }

        static Sprite EnsureSprite(string name, Color32 color)
        {
            string pngPath = $"{AssetRoot}/Art/{name}.png";
            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
            if (!existing)
            {
                Texture2D texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                Color32[] pixels = new Color32[16 * 16];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = color;
                texture.SetPixels32(pixels);
                texture.Apply();

                File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(pngPath);
            }

            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(pngPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spritePixelsPerUnit = 16f;
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Standalone",
                overridden = true,
                maxTextureSize = 2048,
                format = TextureImporterFormat.RGBA32,
                textureCompression = TextureImporterCompression.Uncompressed
            });
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "WebGL",
                overridden = true,
                maxTextureSize = 2048,
                format = TextureImporterFormat.RGBA32,
                textureCompression = TextureImporterCompression.Uncompressed
            });
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
        }

        static Material GetFallbackSpriteMaterial()
        {
            if (fallbackSpriteMaterial)
                return fallbackSpriteMaterial;

            Shader shader = Shader.Find("Sprites/Default");
            if (!shader)
                return null;

            fallbackSpriteMaterial = new Material(shader)
            {
                name = "Celeste Benchmark Sprite Fallback"
            };
            return fallbackSpriteMaterial;
        }

        static void EnsureAssetFolders()
        {
            EnsureFolder("Assets", "CelesteBenchmark");
            EnsureFolder(AssetRoot, "Art");
            EnsureFolder(AssetRoot, "Tiles");
        }

        static void EnsureFolder(string parent, string child)
        {
            string path = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        static void EnsureLayers()
        {
            SetLayer(PlayerLayer, "Player");
            SetLayer(GroundLayer, "Ground");
            SetLayer(MovingPlatformLayer, "MovingPlatform");
            SetLayer(OneWayLayer, "OneWay");
            SetLayer(HazardLayer, "Hazard");
            SetLayer(TriggerLayer, "Trigger");
            SetLayer(12, "Crumble");
        }

        static void SetLayer(int index, string layerName)
        {
            SerializedObject tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            SerializedProperty layer = layers.GetArrayElementAtIndex(index);
            layer.stringValue = layerName;
            tagManager.ApplyModifiedProperties();
        }
    }
}
