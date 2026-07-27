from __future__ import annotations

import argparse
import json
from pathlib import Path

from ryz1.data.torch_dataset import RyzSequenceDataset
from ryz1.models.policy_value import ModelConfig, RyzPolicyValueModel


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=Path("Library/RYZ1/reports/eval.json"))
    args = parser.parse_args()

    import torch
    from torch.utils.data import DataLoader

    ckpt = torch.load(args.checkpoint, map_location="cpu")
    sequence_length = int(ckpt.get("sequence_length", 1))
    dataset = RyzSequenceDataset(args.dataset, sequence_length)
    model = RyzPolicyValueModel(ModelConfig(**ckpt["model_config"]))
    model.load_state_dict(ckpt["model_state"])
    model.eval()

    correct = 0
    total = 0
    per_action_total = [0] * model.config.macro_count
    per_action_correct = [0] * model.config.macro_count
    confusion = [[0] * model.config.macro_count for _ in range(model.config.macro_count)]
    loader = DataLoader(dataset, batch_size=256, shuffle=False, num_workers=0)
    with torch.no_grad():
        for batch in loader:
            outputs = model(
                batch["player"],
                batch["mechanics"],
                batch["history"],
            )
            predictions = outputs["policy_logits"][:, -1, :].argmax(dim=-1)
            targets = batch["action"]
            correct += int((predictions == targets).sum().item())
            total += int(targets.numel())
            for target, prediction in zip(targets.tolist(), predictions.tolist()):
                per_action_total[target] += 1
                per_action_correct[target] += int(prediction == target)
                confusion[target][prediction] += 1
    report = {
        "transitions": total,
        "sequence_length": sequence_length,
        "teacher_action_accuracy": correct / max(1, total),
        "per_action_accuracy": {
            str(action): per_action_correct[action] / max(1, per_action_total[action])
            for action in range(model.config.macro_count)
            if per_action_total[action] > 0
        },
        "per_action_total": {
            str(action): per_action_total[action]
            for action in range(model.config.macro_count)
            if per_action_total[action] > 0
        },
        "confusion": confusion,
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
