from __future__ import annotations

import argparse
from pathlib import Path

from ryz1.models.policy_value import ModelConfig, RyzPolicyValueModel


def exportable_model(model):
    import torch

    class ExportableModel(torch.nn.Module):
        def __init__(self, wrapped):
            super().__init__()
            self.wrapped = wrapped

        def forward(self, player, mechanics, history):
            outputs = self.wrapped(player, mechanics, history)
            return outputs["policy_logits"], outputs["value"]

    return ExportableModel(model)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=Path("Library/RYZ1/models/ryz1.onnx"))
    args = parser.parse_args()

    import torch

    ckpt = torch.load(args.checkpoint, map_location="cpu")
    model = RyzPolicyValueModel(ModelConfig(**ckpt["model_config"]))
    model.load_state_dict(ckpt["model_state"])
    model.eval()
    cfg = model.config
    sequence_length = int(ckpt.get("sequence_length", 1))
    player = torch.zeros(1, sequence_length, cfg.player_vector_size)
    mechanics = torch.zeros(1, sequence_length, cfg.mechanics_vector_size)
    history = torch.zeros(1, sequence_length, 4)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    export_model = exportable_model(model).eval()
    torch.onnx.export(
        export_model,
        (player, mechanics, history),
        str(args.out),
        input_names=["player", "mechanics", "history"],
        output_names=["policy_logits", "value"],
        dynamic_axes={
            "player": {0: "batch", 1: "seq"},
            "mechanics": {0: "batch", 1: "seq"},
            "history": {0: "batch", 1: "seq"},
            "policy_logits": {0: "batch", 1: "seq"},
            "value": {0: "batch", 1: "seq"},
        },
        dynamo=False,
        opset_version=17,
    )
    print(args.out)


if __name__ == "__main__":
    main()
