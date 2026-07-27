using System;
using UnityEngine;

namespace CelesteBenchmark
{
    /// <summary>
    /// Builds a deterministic, left-to-right platforming route. Generated objects live under a dedicated child,
    /// so regenerating never edits or deletes authored scene geometry.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RandomLevelGenerator : MonoBehaviour
    {
        public const string GeneratedRootName = "Generated Random Level";

        const int GroundLayer = 7;
        const int OneWayLayer = 9;
        const int HazardLayer = 10;
        const int TriggerLayer = 11;

        [Header("Generation")]
        [SerializeField] bool generateOnStart = true;
        [SerializeField] bool randomizeSeed = true;
        [SerializeField] int seed = 12345;
        [Min(2)]
        [SerializeField] int platformCount = 18;
        [Tooltip("Local-space centre of the first generated platform.")]
        [SerializeField] Vector2 origin = new Vector2(114.5f, -4.5f);

        [Header("Jumpable Layout")]
        [SerializeField] Vector2 platformWidthRange = new Vector2(3f, 6f);
        [Min(0.25f)]
        [SerializeField] float platformHeight = 0.75f;
        [SerializeField] Vector2 gapRange = new Vector2(1f, 2.75f);
        [SerializeField] Vector2 heightStepRange = new Vector2(-1.5f, 1.5f);
        [Tooltip("Minimum and maximum height relative to Origin.")]
        [SerializeField] Vector2 heightLimits = new Vector2(-1f, 5f);
        [Range(0f, 1f)]
        [SerializeField] float oneWayChance = 0.25f;

        [Header("Gameplay")]
        [Range(0f, 1f)]
        [SerializeField] float hazardChance = 0.25f;
        [Range(0f, 1f)]
        [SerializeField] float refillChance = 0.18f;
        [Min(0)]
        [SerializeField] int checkpointInterval = 5;
        [SerializeField] Sprite platformSprite;

        [Header("Objective")]
        [Tooltip("World-space size of the generated completion volume.")]
        [SerializeField] Vector2 goalSize = new Vector2(2f, 6f);
        [Tooltip("Offset from the final platform's finish marker to the completion volume centre.")]
        [SerializeField] Vector2 goalOffset = new Vector2(0.5f, 1.5f);

        [SerializeField, HideInInspector] int lastGeneratedSeed;

        Transform generatedRoot;
        Sprite fallbackSprite;
        Texture2D fallbackTexture;
        int generatedPlatformCount;
        Vector2 generatedEndPosition;
        BenchmarkGoal generatedGoal;

        public bool GenerateOnStart
        {
            get => generateOnStart;
            set => generateOnStart = value;
        }

        public bool RandomizeSeed
        {
            get => randomizeSeed;
            set => randomizeSeed = value;
        }

        public int Seed
        {
            get => seed;
            set => seed = value;
        }

        public int PlatformCount
        {
            get => platformCount;
            set => platformCount = Mathf.Max(2, value);
        }

        public Vector2 Origin
        {
            get => origin;
            set => origin = value;
        }

        public Sprite PlatformSprite
        {
            get => platformSprite;
            set => platformSprite = value;
        }

        public int LastGeneratedSeed => lastGeneratedSeed;
        public int GeneratedPlatformCount => generatedPlatformCount;
        public Vector2 GeneratedEndPosition => generatedEndPosition;
        public BenchmarkGoal GeneratedGoal => generatedGoal;

        public Transform GeneratedRoot
        {
            get
            {
                if (!generatedRoot)
                    generatedRoot = transform.Find(GeneratedRootName);
                return generatedRoot;
            }
        }

        void Start()
        {
            if (generateOnStart)
                GenerateNewLevel();
        }

        [ContextMenu("Generate New Random Level")]
        public void GenerateNewLevel()
        {
            int selectedSeed = randomizeSeed ? CreateRandomSeed() : seed;
            GenerateFromSeed(selectedSeed);
        }

        /// <summary>Regenerates the same route for the same seed and settings without touching Unity's global RNG.</summary>
        public void GenerateFromSeed(int selectedSeed)
        {
            SanitizeSettings();
            ClearGeneratedLevel();

            lastGeneratedSeed = selectedSeed;
            System.Random random = new System.Random(selectedSeed);

            GameObject rootObject = new GameObject(GeneratedRootName);
            rootObject.transform.SetParent(transform, false);
            generatedRoot = rootObject.transform;

            float width = RandomSnapped(random, platformWidthRange.x, platformWidthRange.y, 0.5f);
            Vector2 centre = origin;
            CreatePlatform(0, centre, width, false);

            for (int i = 1; i < platformCount; i++)
            {
                float nextWidth = RandomSnapped(random, platformWidthRange.x, platformWidthRange.y, 0.5f);
                float gap = RandomSnapped(random, gapRange.x, gapRange.y, 0.25f);
                float heightStep = RandomSnapped(random, heightStepRange.x, heightStepRange.y, 0.5f);

                centre.x += width * 0.5f + gap + nextWidth * 0.5f;
                centre.y = origin.y + Mathf.Clamp(
                    centre.y - origin.y + heightStep,
                    heightLimits.x,
                    heightLimits.y);

                bool isLast = i == platformCount - 1;
                bool isCheckpoint = checkpointInterval > 0 && i % checkpointInterval == 0;
                bool oneWay = !isLast && !isCheckpoint && random.NextDouble() < oneWayChance;

                CreatePlatform(i, centre, nextWidth, oneWay);

                if (!isLast && !isCheckpoint && random.NextDouble() < hazardChance)
                    CreateHazard(centre, nextWidth, random);

                if (!isLast && random.NextDouble() < refillChance)
                    CreateDashRefill(centre);

                if (isCheckpoint)
                    CreateCheckpoint(centre, nextWidth, i);

                width = nextWidth;
            }

            generatedPlatformCount = platformCount;
            generatedEndPosition = new Vector2(
                centre.x + width * 0.5f - 0.75f,
                centre.y + platformHeight * 0.5f);
            CreateFinishMarker(generatedEndPosition);
        }

        [ContextMenu("Clear Generated Level")]
        public void ClearGeneratedLevel()
        {
            Transform root = GeneratedRoot;
            if (root)
            {
                root.gameObject.SetActive(false);
                if (Application.isPlaying)
                    Destroy(root.gameObject);
                else
                    DestroyImmediate(root.gameObject);
            }

            generatedRoot = null;
            generatedPlatformCount = 0;
            generatedEndPosition = Vector2.zero;
            generatedGoal = null;
        }

        void CreatePlatform(int index, Vector2 centre, float width, bool oneWay)
        {
            GameObject platform = CreateBox(
                $"Platform {index + 1:00}",
                centre,
                new Vector2(width, platformHeight),
                oneWay ? new Color(0.25f, 0.52f, 0.78f, 1f) : new Color(0.2f, 0.24f, 0.29f, 1f),
                oneWay ? OneWayLayer : GroundLayer,
                false);

            if (!oneWay)
                return;

            Rigidbody2D body = platform.AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Static;

            PlatformEffector2D effector = platform.AddComponent<PlatformEffector2D>();
            effector.useOneWay = true;
            platform.GetComponent<BoxCollider2D>().usedByEffector = true;
        }

        void CreateHazard(Vector2 platformCentre, float platformWidth, System.Random random)
        {
            float safeOffset = Mathf.Max(0f, platformWidth * 0.5f - 1.25f);
            float xOffset = RandomSnapped(random, -safeOffset, safeOffset, 0.25f);
            Vector2 position = platformCentre + new Vector2(
                xOffset,
                platformHeight * 0.5f + 0.325f);
            GameObject hazard = CreateBox(
                "Random Spike",
                position,
                new Vector2(0.65f, 0.65f),
                new Color(1f, 0.22f, 0.22f, 1f),
                HazardLayer,
                true);
            hazard.AddComponent<BenchmarkSpike>();
        }

        void CreateDashRefill(Vector2 platformCentre)
        {
            Vector2 position = platformCentre + Vector2.up * (platformHeight * 0.5f + 1.25f);
            GameObject refill = CreateBox(
                "Random Dash Refill",
                position,
                Vector2.one * 0.75f,
                new Color(0.1f, 0.95f, 1f, 1f),
                TriggerLayer,
                true);
            refill.AddComponent<BenchmarkDashRefill>();
        }

        void CreateCheckpoint(Vector2 platformCentre, float platformWidth, int routeOrder)
        {
            Vector2 position = platformCentre + new Vector2(
                -platformWidth * 0.5f + 0.75f,
                platformHeight * 0.5f + 0.375f);
            GameObject checkpoint = CreateBox(
                "Random Checkpoint",
                position,
                Vector2.one * 0.75f,
                new Color(0.25f, 0.6f, 1f, 1f),
                TriggerLayer,
                true);
            BenchmarkCheckpoint marker = checkpoint.AddComponent<BenchmarkCheckpoint>();
            marker.sectionOrder = routeOrder;
        }

        void CreateFinishMarker(Vector2 basePosition)
        {
            GameObject pole = CreateBox(
                "Random Finish",
                basePosition + Vector2.up * 0.5f,
                new Vector2(0.35f, 1f),
                new Color(0.94f, 0.94f, 0.9f, 1f),
                TriggerLayer,
                true);
            GameObject banner = CreateBox(
                "Random Finish Flag",
                basePosition + new Vector2(0.45f, 1f),
                new Vector2(0.9f, 0.65f),
                new Color(1f, 0.9f, 0.22f, 1f),
                TriggerLayer,
                true);
            banner.transform.SetParent(pole.transform, true);

            GameObject goalObject = new GameObject("Generated Goal");
            goalObject.layer = TriggerLayer;
            goalObject.transform.SetParent(generatedRoot, false);
            goalObject.transform.localPosition = basePosition + goalOffset;
            BoxCollider2D goalTrigger = goalObject.AddComponent<BoxCollider2D>();
            goalTrigger.isTrigger = true;
            goalTrigger.size = goalSize;
            generatedGoal = goalObject.AddComponent<BenchmarkGoal>();
            generatedGoal.Priority = 100;
        }

        GameObject CreateBox(string objectName, Vector2 position, Vector2 size, Color color, int layer, bool trigger)
        {
            GameObject result = new GameObject(objectName);
            result.layer = layer;
            result.transform.SetParent(generatedRoot, false);
            result.transform.localPosition = position;
            result.transform.localScale = new Vector3(size.x, size.y, 1f);

            SpriteRenderer renderer = result.AddComponent<SpriteRenderer>();
            renderer.sprite = GetSprite();
            renderer.color = color;

            BoxCollider2D collider = result.AddComponent<BoxCollider2D>();
            collider.isTrigger = trigger;
            return result;
        }

        Sprite GetSprite()
        {
            if (platformSprite)
                return platformSprite;
            if (fallbackSprite)
                return fallbackSprite;

            fallbackTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "RandomLevelGenerator White Texture",
                hideFlags = HideFlags.HideAndDontSave
            };
            fallbackTexture.SetPixel(0, 0, Color.white);
            fallbackTexture.Apply();
            fallbackSprite = Sprite.Create(
                fallbackTexture,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f);
            fallbackSprite.name = "RandomLevelGenerator White Sprite";
            fallbackSprite.hideFlags = HideFlags.HideAndDontSave;
            return fallbackSprite;
        }

        void OnDestroy()
        {
            if (fallbackSprite)
                DestroyGeneratedObject(fallbackSprite);
            if (fallbackTexture)
                DestroyGeneratedObject(fallbackTexture);
        }

        void OnValidate()
        {
            SanitizeSettings();
        }

        void SanitizeSettings()
        {
            platformCount = Mathf.Max(2, platformCount);
            platformHeight = Mathf.Max(0.25f, platformHeight);
            platformWidthRange = OrderedRange(platformWidthRange, 1.5f);
            gapRange = OrderedRange(gapRange, 0f);
            heightStepRange = OrderedRange(heightStepRange, -10f);
            heightLimits = OrderedRange(heightLimits, -20f);
            checkpointInterval = Mathf.Max(0, checkpointInterval);
            goalSize.x = Mathf.Max(0.1f, goalSize.x);
            goalSize.y = Mathf.Max(0.1f, goalSize.y);
        }

        static Vector2 OrderedRange(Vector2 range, float minimum)
        {
            float low = Mathf.Max(minimum, Mathf.Min(range.x, range.y));
            float high = Mathf.Max(low, Mathf.Max(range.x, range.y));
            return new Vector2(low, high);
        }

        static float RandomSnapped(System.Random random, float minimum, float maximum, float step)
        {
            if (maximum <= minimum)
                return minimum;
            float value = Mathf.Lerp(minimum, maximum, (float)random.NextDouble());
            return Mathf.Clamp(Mathf.Round(value / step) * step, minimum, maximum);
        }

        static int CreateRandomSeed()
        {
            unchecked
            {
                return (int)DateTime.UtcNow.Ticks ^ (Time.frameCount * 397);
            }
        }

        static void DestroyGeneratedObject(UnityEngine.Object target)
        {
            if (!target)
                return;
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }
    }
}
