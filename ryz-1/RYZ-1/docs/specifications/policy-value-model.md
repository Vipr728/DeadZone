# Policy-Value Model

Required outputs:

- Policy logits over macro IDs.
- Value prediction as probability of eventual completion.

Loss:

- Cross-entropy policy loss against teacher macro labels.
- Mean-squared value loss against verified completion targets.

Checkpoint:

- PyTorch `.pt` file.
- Contains `model_config`, `model_state`, `optimizer_state`, `steps`, `parameter_count`, and last metrics.

Action masking is supported by `masked_logits`; full task-derived masks are the next integration step.
