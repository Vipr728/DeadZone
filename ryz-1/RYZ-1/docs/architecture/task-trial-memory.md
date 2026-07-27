# Task, Trial, and Memory

Task:

- Mechanics configuration
- Action mapping
- Level layout
- Entity rules
- Goal
- Initial resources
- Testing objective

Trial:

- One attempt within the same task after spawn/checkpoint reset.
- Actions, death, timeout, checkpoint, or completion are recorded.

Meta-episode:

- Several trials of the same task.
- World resets between trials.
- Model recurrent memory persists.

New task:

- A material mechanics, level, or goal change.
- World and model memory reset.
- Model weights remain frozen during deployment.

Current code records `taskId` and `trialId` on every dataset transition. The model exposes explicit memory
initialization. Full multi-trial evaluation metrics are the next integration step.
