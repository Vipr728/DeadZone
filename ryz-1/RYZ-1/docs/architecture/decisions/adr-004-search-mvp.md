# ADR-004: Search-based solver MVP, no trained policy

Status: Accepted

Context: MVP must be useful with zero training. Simulator is deterministic-enough, fully observable, resettable — ideal for search.

Decision: Beam search over movement macros (hold-direction-N-frames, jump-hold-N, dash-dir, wall-jump, wait) toward stable states (grounded, wall-grab, checkpoint, goal), with state hashing and duplicate elimination. Synthetic profiles derive from solver policies with modeled limitations. Learned policies come later behind the same IAgent interface.

Alternatives: RL/BC first — rejected: training infra, data needs, no determinism benefit for MVP metrics.

Consequences: Solver needs cheap state save/restore or re-simulation from action prefixes (re-simulation chosen first: no snapshot API needed; revisit if too slow). Profiles are honestly labeled synthetic.
