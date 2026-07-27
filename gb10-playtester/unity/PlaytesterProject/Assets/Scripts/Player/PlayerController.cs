using UnityEngine;
using UnityEngine.InputSystem;

namespace Playtester
{
    [RequireComponent(typeof(PlayerInputAdapter), typeof(Rigidbody2D), typeof(Collider2D))]
    public sealed class PlayerController : MonoBehaviour
    {
        [SerializeField] private PlayerConfig playerConfig = null!;
        [SerializeField] private InputActionAsset playerControls = null!;

        private PlayerInputAdapter inputAdapter = null!;
        private Rigidbody2D body = null!;
        private InputAction? moveAction;
        private InputAction? jumpAction;
        private bool agentControlEnabled;

        private void Awake()
        {
            inputAdapter = GetComponent<PlayerInputAdapter>();
            body = GetComponent<Rigidbody2D>();
            ConfigureActions();
        }

        private void OnEnable()
        {
            moveAction?.Enable();
            jumpAction?.Enable();
        }

        private void OnDisable()
        {
            moveAction?.Disable();
            jumpAction?.Disable();
        }

        private void Update()
        {
            if (agentControlEnabled || moveAction == null || jumpAction == null)
            {
                return;
            }

            inputAdapter.SetMove(moveAction.ReadValue<float>());
            if (jumpAction.WasPressedThisFrame())
            {
                inputAdapter.SetJump(true);
            }
        }

        private void FixedUpdate()
        {
            if (playerConfig == null)
            {
                return;
            }

            body.linearVelocity = new Vector2(
                inputAdapter.Move * playerConfig.MoveSpeed,
                body.linearVelocity.y);
            if (!inputAdapter.ConsumeJump() || !IsGrounded())
            {
                return;
            }

            body.AddForce(Vector2.up * playerConfig.JumpImpulse, ForceMode2D.Impulse);
        }

        public void Configure(PlayerConfig config, InputActionAsset controls)
        {
            playerConfig = config;
            playerControls = controls;
            ConfigureActions();
            if (isActiveAndEnabled)
            {
                moveAction?.Enable();
                jumpAction?.Enable();
            }
        }

        public void SetAgentControlEnabled(bool enabled)
        {
            agentControlEnabled = enabled;
            if (!enabled)
            {
                inputAdapter.Clear();
            }
        }

        public bool IsGrounded()
        {
            if (playerConfig == null)
            {
                return false;
            }
            return Physics2D.Raycast(
                body.position,
                Vector2.down,
                playerConfig.GroundedCheckDistance,
                playerConfig.GroundLayers);
        }

        private void ConfigureActions()
        {
            if (playerControls == null)
            {
                return;
            }
            moveAction = playerControls.FindAction("Player/Move", throwIfNotFound: true);
            jumpAction = playerControls.FindAction("Player/Jump", throwIfNotFound: true);
        }
    }
}
