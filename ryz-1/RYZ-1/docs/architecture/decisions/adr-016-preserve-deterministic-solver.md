# ADR-016: Preserve The Deterministic Solver

**Decision:** Wrap `BeamSearchSolver` and `SegmentedSolver`; retain macros, prefix replay, and fresh-reset final
verification.

**Consequence:** The proven baseline remains a verifier and fallback while universal contracts evolve.
