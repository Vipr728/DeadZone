using System.Collections.Generic;
using System.Linq;
using Playtester.Agent;
using UnityEngine;

namespace Playtester.Gym
{
    public sealed class PieceComposer : MonoBehaviour
    {
        [SerializeField] private PieceLibraryConfig pieceLibrary = null!;
        [SerializeField] private Transform player = null!;
        [SerializeField] private Rigidbody2D playerBody = null!;
        [SerializeField] private Transform compositionRoot = null!;
        [SerializeField] private PlaytestAgent agent = null!;

        private readonly List<GameObject> composedPieces = new();

        public void Recompose()
        {
            ClearComposition();
            if (pieceLibrary == null || compositionRoot == null)
            {
                Debug.LogError("PieceComposer needs a generated PieceLibraryConfig and composition root.");
                return;
            }
            float cursor = 0f;
            List<IPieceType> pieces = new();
            List<PieceParams> pieceParameters = new();
            for (int index = 0; index < pieceLibrary.PiecesPerEpisode; index++)
            {
                GameObject prefab = SelectPrefab();
                if (prefab == null)
                {
                    return;
                }
                GameObject instance = Instantiate(prefab, compositionRoot);
                instance.transform.localPosition = Vector3.right * cursor;
                IPieceType? piece = instance.GetComponents<MonoBehaviour>().OfType<IPieceType>().FirstOrDefault();
                if (piece == null)
                {
                    Debug.LogError($"Piece prefab {prefab.name} does not implement IPieceType.");
                    Destroy(instance);
                    return;
                }
                PieceParams parameters = SampleParameters(piece);
                piece.Configure(parameters);
                cursor += piece.GetLocalBounds().size.x;
                composedPieces.Add(instance);
                pieces.Add(piece);
                pieceParameters.Add(parameters);
            }
            for (int index = 0; index < pieces.Count; index++)
            {
                Transform nextGoal = index + 1 < pieces.Count ? pieces[index + 1].GetLocalGoal() : null!;
                AgentTriggerRelay relay = pieces[index].GetLocalGoal().GetComponent<AgentTriggerRelay>();
                if (relay != null && agent != null)
                    relay.Configure(
                        agent,
                        AgentTriggerRelay.TriggerKind.PieceGoal,
                        nextGoal,
                        $"piece_{index + 1}",
                        PieceTypeName(pieces[index]),
                        pieceParameters[index],
                        true);
            }
            if (pieces.Count > 0 && agent != null)
                agent.SetCurrentGoal(pieces[0].GetLocalGoal());
            ResetPlayerAtBoundary();
        }

        public void ResetPlayerAtBoundary()
        {
            if (player == null || playerBody == null)
            {
                return;
            }
            player.position = compositionRoot.position;
            if (pieceLibrary.BoundaryVelocityReset)
            {
                playerBody.linearVelocity = Vector2.zero;
                playerBody.angularVelocity = 0f;
            }
        }

        private GameObject SelectPrefab()
        {
            List<GameObject> candidates = new();
            if (pieceLibrary.GapJumpPrefab != null) candidates.Add(pieceLibrary.GapJumpPrefab);
            if (pieceLibrary.MoveToGoalPrefab != null) candidates.Add(pieceLibrary.MoveToGoalPrefab);
            if (pieceLibrary.EnableElevationPiece && pieceLibrary.ElevationPrefab != null) candidates.Add(pieceLibrary.ElevationPrefab);
            if (candidates.Count == 0)
            {
                Debug.LogError("PieceLibraryConfig has no enabled piece prefabs.");
                return null!;
            }
            return candidates[Random.Range(0, candidates.Count)];
        }

        private PieceParams SampleParameters(IPieceType piece)
        {
            if (piece is GapJumpPiece)
                return new PieceParams { Width = Random.Range(pieceLibrary.GapWidthRange.x, pieceLibrary.GapWidthRange.y) };
            if (piece is MoveToGoalPiece)
                return new PieceParams { Distance = Random.Range(pieceLibrary.MoveDistanceRange.x, pieceLibrary.MoveDistanceRange.y) };
            return new PieceParams { Height = Random.Range(pieceLibrary.ElevationHeightRange.x, pieceLibrary.ElevationHeightRange.y) };
        }

        private static string PieceTypeName(IPieceType piece)
        {
            if (piece is GapJumpPiece) return "gap_jump";
            if (piece is ElevationPiece) return "elevation";
            return "move_to_goal";
        }

        private void ClearComposition()
        {
            foreach (GameObject piece in composedPieces)
            {
                Destroy(piece);
            }
            composedPieces.Clear();
        }
    }
}
