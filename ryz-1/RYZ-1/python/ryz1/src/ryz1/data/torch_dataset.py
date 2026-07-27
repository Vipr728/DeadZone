from __future__ import annotations

from pathlib import Path

from ryz1.data.schema import (
    load_json,
    teacher_sequences_to_arrays,
    transitions_to_arrays,
    validate_dataset,
)


class RyzTransitionDataset:
    def __init__(self, path: Path):
        payload = load_json(path)
        self.shape = validate_dataset(payload)
        self.arrays = transitions_to_arrays(payload, self.shape)

    def __len__(self) -> int:
        return len(self.arrays["actions"])

    def __getitem__(self, index: int):
        import torch

        return {
            "player": torch.from_numpy(self.arrays["player"][index]),
            "mechanics": torch.from_numpy(self.arrays["mechanics"][index]),
            "action": torch.tensor(self.arrays["actions"][index], dtype=torch.long),
            "value": torch.tensor(self.arrays["values"][index], dtype=torch.float32),
            "reward": torch.tensor(self.arrays["rewards"][index], dtype=torch.float32),
            "trial_id": torch.tensor(self.arrays["trial_ids"][index], dtype=torch.long),
        }


class RyzSequenceDataset:
    def __init__(self, path: Path, sequence_length: int):
        payload = load_json(path)
        self.shape = validate_dataset(payload)
        self.sequence_length = sequence_length
        self.arrays = teacher_sequences_to_arrays(payload, self.shape, sequence_length)

    def __len__(self) -> int:
        return len(self.arrays["actions"])

    def __getitem__(self, index: int):
        import torch

        return {
            "player": torch.from_numpy(self.arrays["player"][index]),
            "mechanics": torch.from_numpy(self.arrays["mechanics"][index]),
            "history": torch.from_numpy(self.arrays["history"][index]),
            "action": torch.tensor(self.arrays["actions"][index], dtype=torch.long),
            "value": torch.tensor(self.arrays["values"][index], dtype=torch.float32),
            "trial_id": torch.tensor(self.arrays["trial_ids"][index], dtype=torch.long),
            "length": torch.tensor(self.arrays["lengths"][index], dtype=torch.long),
        }
