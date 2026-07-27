# Model Architecture

The implemented P0 model is `python/ryz1/src/ryz1/models/policy_value.py`.

Inputs:

- Player vector from SimCore observations.
- Fixed mechanics vector from the task bundle.
- History vector containing previous-action/reward/trial metadata placeholders.

Architecture:

- Player MLP.
- Mechanics MLP.
- History MLP.
- One-layer GRU memory.
- Policy head over macro IDs.
- Sigmoid value head predicting completion probability.

Memory behavior:

- `initial_memory(batch)` returns zero memory for a new task.
- The returned GRU state is reused across trials of the same task by evaluation code.
- Callers must reset memory when task ID, manifest fingerprint, level fingerprint, or goal changes.

The smoke config uses hidden size 64. The hackathon config uses hidden size 256.
