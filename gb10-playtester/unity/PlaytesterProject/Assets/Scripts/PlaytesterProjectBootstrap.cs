using UnityEngine;
using UnityEngine.InputSystem;

namespace Playtester
{
    /// <summary>
    /// Builds the minimal playable 2D level at runtime. Authored Level A and
    /// Level B assets replace these primitives during the Unity workstream.
    /// </summary>
    public sealed class PlaytesterProjectBootstrap : MonoBehaviour
    {
        [SerializeField] private PlayerConfig playerConfig = null!;
        [SerializeField] private InputActionAsset playerControls = null!;
        private static readonly Color GroundColor = new(0.18f, 0.25f, 0.38f);
        private static readonly Color PlayerColor = new(0.2f, 0.75f, 1f);
        private static readonly Color GoalColor = new(0.35f, 0.9f, 0.45f);

        private void Awake()
        {
            ConfigureCamera();
            CreateGround();
            CreatePlayer();
            CreateGoal();
        }

        private static void ConfigureCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }

            camera.orthographic = true;
            camera.orthographicSize = 5f;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.backgroundColor = new Color(0.06f, 0.09f, 0.16f);
        }

        private static void CreateGround()
        {
            GameObject ground = CreateBox("Ground", new Vector2(0f, -3.5f), new Vector2(16f, 1f), GroundColor);
            ground.AddComponent<BoxCollider2D>();
        }

        private void CreatePlayer()
        {
            GameObject player = CreateBox("Player", new Vector2(-6f, -2f), new Vector2(0.8f, 1.2f), PlayerColor);
            player.AddComponent<BoxCollider2D>();
            player.AddComponent<Rigidbody2D>();
            player.AddComponent<PlayerInputAdapter>();
            PlayerController controller = player.AddComponent<PlayerController>();
            controller.Configure(playerConfig, playerControls);
        }

        private static void CreateGoal()
        {
            GameObject goal = CreateBox("Goal", new Vector2(6f, -2.5f), new Vector2(0.7f, 2f), GoalColor);
            BoxCollider2D collider = goal.AddComponent<BoxCollider2D>();
            collider.isTrigger = true;
        }

        private static GameObject CreateBox(string objectName, Vector2 position, Vector2 scale, Color color)
        {
            GameObject gameObject = new(objectName);
            gameObject.transform.position = position;
            gameObject.transform.localScale = scale;
            SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f);
            renderer.color = color;
            return gameObject;
        }
    }
}
