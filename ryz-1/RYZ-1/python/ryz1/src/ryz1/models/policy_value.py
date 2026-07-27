from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ModelConfig:
    player_vector_size: int = 16
    mechanics_vector_size: int = 32
    macro_count: int = 9
    hidden_size: int = 256
    recurrent_layers: int = 1


def count_parameters(model) -> int:
    return sum(p.numel() for p in model.parameters() if p.requires_grad)


def masked_logits(logits, mask):
    import torch

    if mask is None:
        return logits
    return logits.masked_fill(~mask.bool(), torch.finfo(logits.dtype).min)


class RyzPolicyValueModel:
    """Mechanics-conditioned recurrent policy-value model.

    This class intentionally imports torch lazily so repository-level Python checks can run before
    GB10 dependencies are installed.
    """

    def __new__(cls, config: ModelConfig):
        import torch
        from torch import nn

        class _Model(nn.Module):
            def __init__(self, cfg: ModelConfig):
                super().__init__()
                self.config = cfg
                self.player = nn.Sequential(
                    nn.Linear(cfg.player_vector_size, cfg.hidden_size),
                    nn.ReLU(),
                    nn.Linear(cfg.hidden_size, cfg.hidden_size),
                    nn.ReLU(),
                )
                self.mechanics = nn.Sequential(
                    nn.Linear(cfg.mechanics_vector_size, cfg.hidden_size),
                    nn.ReLU(),
                )
                self.history = nn.Sequential(
                    nn.Linear(4, cfg.hidden_size // 2),
                    nn.ReLU(),
                )
                fused = cfg.hidden_size * 2 + cfg.hidden_size // 2
                self.gru = nn.GRU(fused, cfg.hidden_size, cfg.recurrent_layers, batch_first=True)
                self.policy = nn.Linear(cfg.hidden_size, cfg.macro_count)
                self.value = nn.Linear(cfg.hidden_size, 1)

            def initial_memory(self, batch_size: int, device=None):
                return torch.zeros(self.config.recurrent_layers, batch_size, self.config.hidden_size, device=device)

            def forward(self, player, mechanics, history, memory=None, action_mask=None):
                if player.dim() == 2:
                    player = player.unsqueeze(1)
                if mechanics.dim() == 2:
                    mechanics = mechanics.unsqueeze(1).expand(-1, player.shape[1], -1)
                if history.dim() == 2:
                    history = history.unsqueeze(1).expand(-1, player.shape[1], -1)

                batch, seq, _ = player.shape
                p = self.player(player.reshape(batch * seq, -1)).reshape(batch, seq, -1)
                m = self.mechanics(mechanics.reshape(batch * seq, -1)).reshape(batch, seq, -1)
                h = self.history(history.reshape(batch * seq, -1)).reshape(batch, seq, -1)
                fused = torch.cat([p, m, h], dim=-1)
                if memory is None:
                    memory = self.initial_memory(batch, player.device)
                output, next_memory = self.gru(fused, memory)
                logits = masked_logits(self.policy(output), action_mask)
                value = torch.sigmoid(self.value(output)).squeeze(-1)
                return {"policy_logits": logits, "value": value, "memory": next_memory}

        return _Model(config)
