from __future__ import annotations


def policy_value_loss(
    outputs,
    actions,
    values,
    policy_weight: float = 1.0,
    value_weight: float = 1.0,
    class_weights=None,
):
    import torch.nn.functional as F

    logits = outputs["policy_logits"]
    pred_values = outputs["value"]
    if logits.dim() == 3:
        logits = logits[:, -1, :]
    if pred_values.dim() == 2:
        pred_values = pred_values[:, -1]
    policy = F.cross_entropy(logits, actions, weight=class_weights)
    value = F.mse_loss(pred_values, values)
    total = policy_weight * policy + value_weight * value
    return total, {"policy_loss": float(policy.detach().cpu()), "value_loss": float(value.detach().cpu())}
