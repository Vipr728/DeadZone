using Playtester.Gym;
using UnityEngine;

namespace Playtester.Agent
{
    [RequireComponent(typeof(Collider2D))]
    public sealed class AgentTriggerRelay : MonoBehaviour
    {
        public enum TriggerKind { PieceGoal, Hazard }

        [SerializeField] private TriggerKind triggerKind;
        // These references must survive the editor wiring scene save. Without
        // serialization every Level A/B trigger reloads with a null agent.
        [SerializeField] private PlaytestAgent agent = null!;
        [SerializeField] private Transform nextGoal = null!;
        [SerializeField] private string pieceId = "piece_1";
        [SerializeField] private string pieceType = "move_to_goal";
        [SerializeField] private PieceParams pieceParameters;
        [SerializeField] private bool seenInStageOneRange = true;
        private bool consumed;

        public void Configure(
            PlaytestAgent target,
            TriggerKind kind,
            Transform next = null!,
            string configuredPieceId = "piece_1",
            string configuredPieceType = "move_to_goal",
            PieceParams configuredParameters = default,
            bool configuredSeenInStageOneRange = true)
        {
            agent = target;
            triggerKind = kind;
            nextGoal = next;
            pieceId = configuredPieceId;
            pieceType = configuredPieceType;
            pieceParameters = configuredParameters;
            seenInStageOneRange = configuredSeenInStageOneRange;
            consumed = false;
            GetComponent<Collider2D>().isTrigger = true;
        }

        public void ResetForEpisode()
        {
            consumed = false;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (consumed || agent == null || other.attachedRigidbody == null)
                return;
            if (other.attachedRigidbody.gameObject != agent.gameObject)
                return;

            consumed = true;
            if (triggerKind == TriggerKind.Hazard)
                agent.Die(pieceId, pieceType, pieceParameters, seenInStageOneRange);
            else
                agent.CompletePiece(
                    nextGoal,
                    pieceId,
                    pieceType,
                    pieceParameters,
                    seenInStageOneRange);
        }
    }
}
