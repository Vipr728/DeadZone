# GB10 project naming conventions

These names are integration contracts. Do not casually normalize one naming
layer into another: Unity asset names are human-facing, while automation IDs
are filesystem- and CLI-safe.

| Domain | Convention | Canonical values |
| --- | --- | --- |
| Unity project | PascalCase | `PlaytesterProject` |
| Unity scenes | PascalCase | `GymScene`, `LevelA`, `LevelB` |
| C# files/types | PascalCase | `PlaytestAgent`, `PlayerInputAdapter`, `TelemetryRecorder` |
| C# methods | PascalCase | `SetMove(float)`, `SetJump(bool)` |
| Python packages/modules | snake_case | `playtester_rl`, `telemetry_writer.py` |
| Level IDs / CLIs / build folders | snake_case | `gym`, `level_a`, `level_b` |
| YAML and JSON contracts | snake_case | `piece_config.yaml`, `checkpoint_manifest.json` |

## Scene-to-level mapping

| Unity scene | `--level-id` | Required build path |
| --- | --- | --- |
| `GymScene` | `gym` | `unity/PlaytesterProject/Builds/gym/gym.<platform-extension>` |
| `LevelA` | `level_a` | `unity/PlaytesterProject/Builds/level_a/level_a.<platform-extension>` |
| `LevelB` | `level_b` | `unity/PlaytesterProject/Builds/level_b/level_b.<platform-extension>` |

The ML-Agents behavior name is exactly `PlaytestAgent`. The Unity controller
boundary is exactly `PlayerInputAdapter.SetMove(float)` and
`PlayerInputAdapter.SetJump(bool)`.
