from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

import numpy as np

from ryz1.data.torch_dataset import RyzSequenceDataset
from ryz1.models.policy_value import ModelConfig, RyzPolicyValueModel, count_parameters
from ryz1.training.losses import policy_value_loss


def choose_device(requested: str):
    import torch

    if requested != "auto":
        return torch.device(requested)
    return torch.device("cuda" if torch.cuda.is_available() else "cpu")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", type=Path, required=True)
    parser.add_argument("--config", type=Path, default=Path("python/ryz1/configs/smoke.json"))
    parser.add_argument("--out", type=Path, default=Path("Library/RYZ1/models/smoke"))
    args = parser.parse_args()

    import torch
    from torch.utils.data import DataLoader
    from torch.utils.tensorboard import SummaryWriter

    cfg = json.loads(args.config.read_text(encoding="utf-8"))
    seed = int(cfg.get("seed", 7))
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)

    sequence_length = int(cfg.get("sequence_length", 1))
    dataset = RyzSequenceDataset(args.dataset, sequence_length)
    device = choose_device(str(cfg.get("device", "auto")))
    if device.type == "cuda":
        torch.set_float32_matmul_precision("high")
    model_cfg = ModelConfig(
        player_vector_size=dataset.shape.player_vector_size,
        mechanics_vector_size=dataset.shape.mechanics_vector_size,
        macro_count=dataset.shape.macro_count,
        hidden_size=int(cfg.get("hidden_size", 256)),
    )
    model = RyzPolicyValueModel(model_cfg).to(device)
    class_balance = str(cfg.get("class_balance", "sqrt")).lower()
    class_weights = None
    if class_balance not in {"off", "false", "none"}:
        counts = np.bincount(dataset.arrays["actions"], minlength=model_cfg.macro_count).astype(np.float32)
        nonzero = counts > 0
        weights = np.ones_like(counts)
        if class_balance == "inverse":
            weights[nonzero] = counts[nonzero].sum() / counts[nonzero]
        else:
            weights[nonzero] = np.sqrt(counts[nonzero].sum() / counts[nonzero])
        weights[nonzero] /= weights[nonzero].mean()
        weights = np.clip(weights, 0.25, 4.0)
        class_weights = torch.tensor(weights, dtype=torch.float32, device=device)
    workers = int(cfg.get("num_workers", 0))
    loader = DataLoader(
        dataset,
        batch_size=int(cfg.get("batch_size", 16)),
        shuffle=True,
        num_workers=workers,
        pin_memory=device.type == "cuda",
        persistent_workers=workers > 0,
    )
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(cfg.get("learning_rate", 3e-4)))
    args.out.mkdir(parents=True, exist_ok=True)
    writer = SummaryWriter(log_dir=str(args.out / "tb"))

    mixed_precision = str(cfg.get("mixed_precision", "auto")).lower()
    use_amp = device.type == "cuda" and mixed_precision not in {"off", "false", "none"}
    amp_dtype = torch.bfloat16 if device.type == "cuda" and torch.cuda.is_bf16_supported() else torch.float16
    steps = int(cfg.get("steps", 5))
    iterator = iter(loader)
    last_metrics = {}
    for step in range(1, steps + 1):
        try:
            batch = next(iterator)
        except StopIteration:
            iterator = iter(loader)
            batch = next(iterator)
        player = batch["player"].to(device, non_blocking=True)
        mechanics = batch["mechanics"].to(device, non_blocking=True)
        history = batch["history"].to(device, non_blocking=True)
        actions = batch["action"].to(device, non_blocking=True)
        values = batch["value"].to(device, non_blocking=True)
        with torch.autocast(
            device_type=device.type,
            dtype=amp_dtype,
            enabled=use_amp,
        ):
            outputs = model(player, mechanics, history)
            loss, metrics = policy_value_loss(
                outputs,
                actions,
                values,
                class_weights=class_weights,
            )
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        optimizer.step()
        last_metrics = metrics
        writer.add_scalar("loss/total", float(loss.detach().cpu()), step)
        writer.add_scalar("loss/policy", metrics["policy_loss"], step)
        writer.add_scalar("loss/value", metrics["value_loss"], step)

    checkpoint = {
        "model_config": model_cfg.__dict__,
        "model_state": model.state_dict(),
        "optimizer_state": optimizer.state_dict(),
        "steps": steps,
        "sequence_length": sequence_length,
        "parameter_count": count_parameters(model),
        "last_metrics": last_metrics,
    }
    torch.save(checkpoint, args.out / "checkpoint.pt")
    (args.out / "training_summary.json").write_text(json.dumps({
        "steps": steps,
        "parameter_count": checkpoint["parameter_count"],
        "last_metrics": last_metrics,
        "device": str(device),
        "sequence_length": sequence_length,
        "sequence_examples": len(dataset),
        "mixed_precision": str(amp_dtype).split(".")[-1] if use_amp else "off",
        "class_balance": class_balance,
        "class_weights": class_weights.detach().cpu().tolist() if class_weights is not None else None,
    }, indent=2), encoding="utf-8")
    print(json.dumps({"checkpoint": str(args.out / "checkpoint.pt"), **checkpoint["last_metrics"], "parameters": checkpoint["parameter_count"]}))


if __name__ == "__main__":
    main()
