# Neural-Guided Search

The search score is:

```text
verified progress and terminal outcome
+ configured neural policy prior
+ configured neural value
- death
- macro tick penalty
```

The model ranks expansions only. A trajectory is accepted only after deterministic replay verification.

Current implementation:

- `INeuralGuide` interface in SimCore.
- `NullNeuralGuide` fallback.
- Neural weights in `SimSearchConfig`.

Pending:

- Local Python inference process or ONNX-backed guide connected to `Ryz1.Runner`.
