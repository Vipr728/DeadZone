# Integration contracts

This repository uses the names and paths in `NAMING_CONVENTIONS.md`. The
following version resolution is intentionally explicit because Unity package
version numbers do not track the Editor version:

| Component | Locked value | Verification source |
| --- | --- | --- |
| Unity Editor | `6000.3.6f1` | `ProjectVersion.txt` |
| ML-Agents | `com.unity.ml-agents` `4.0.1` | Unity 6.3's installed package documentation lists 4.0.0 and 4.0.1 as released. |
| Inference | `com.unity.sentis` `2.2.0` | Unity 6.3 ships Sentis 2.2.0 as a shim to `com.unity.ai.inference` 2.2.1. |
| Agent behavior | `PlaytestAgent` | `rl/configs/training_config.yaml` and Unity agent component |
| Human/agent controls | `SetMove(float)`, `SetJump(bool)` | `PlayerInputAdapter` |
| Export trigger | `Exports/<level_id>/level_export.json` | marker is written after `Builds/<level_id>/<level_id>.<ext>` |
| Simulation host | Mac running the Unity build | Unity remains local so the Editor and 2D simulation stay visible. |
| Policy host | GB10 running ML-Agents/PyTorch | Training and primary `--resume --inference` execute remotely. |
| Trainer transport | Tailscale DNS + SSH local forwarding | `rl/configs/remote_execution.yaml`; IP addresses are rejected. |
| Trainer ports | `5004` base, unique allocation per run | Each concurrent Unity/trainer pair receives its own forwarded port. |
| ONNX/Sentis | compatibility proof and emergency fallback | Primary policy inference remains on the GB10. |

`infra/tests/test_cross_workstream_contracts.py` is the automated drift check
for these interfaces, file locations, and training CLI flags.
