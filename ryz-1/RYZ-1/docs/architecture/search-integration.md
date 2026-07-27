# Search Integration

Existing Unity search remains in `BeamSearchSolver` and `SegmentedSolver`.

Native GB10 search is implemented in `Ryz1.SimCore/BeamSearch.cs`. It:

- Expands fixed macro IDs from the task bundle.
- Replays candidates through SimCore from reset.
- Records successful, failed, and pruned transitions.
- Applies optional `INeuralGuide` policy/value outputs to candidate scoring.
- Verifies final macro streams through a clean SimCore replay.

The neural guide cannot approve trajectories. Completion requires deterministic replay verification.
