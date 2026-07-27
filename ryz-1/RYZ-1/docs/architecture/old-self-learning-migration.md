# Old Self-Learning Migration Inventory

Audit date: 2026-07-25.

The active repository does not contain a completed Python or neural self-learning pipeline. Existing architecture
docs explicitly describe the current MVP as Unity/C# search-based with no trained models. Prior-agent worktrees
under `.claude/worktrees/` contain mechanic discovery/probe experiments, but they are not part of the active
Unity project and are not compiled.

| File path | Current responsibility | Dependencies | Used now | Tests | RYZ-1 compatibility | Migration action | Risk |
|---|---|---|---:|---|---|---|---|
| `Assets/PlatformerPlaytest/Runtime/Agents/Solver/BeamSearchSolver.cs` | Deterministic macro beam search through real Unity simulation | `IGameAdapter`, `ScenarioConfig`, `MovementMacro`, `StateHash` | Yes | EditMode/PlayMode solver tests | High as teacher/fallback/search backend | Keep but adapt later behind neural ranking interface | High |
| `Assets/PlatformerPlaytest/Runtime/Agents/Solver/SegmentedSolver.cs` | Checkpoint-based long-level solving and clean replay | Beam solver, scenario checkpoints | Yes | Solver PlayMode tests | High | Keep unchanged until native parity exists | High |
| `Assets/PlatformerPlaytest/Runtime/Agents/Profiles/ProfiledAgent.cs` | Synthetic profile perturbations over solver plans | `PlayerProfile`, action streams | Yes | Profile tests | Medium; useful for synthetic profiles but not human-calibrated | Keep but adapt reporting labels | Medium |
| `Packages/com.ryzi.unity/Runtime/Mechanics/MechanicsManifest.cs` | Unity manifest IR with evidence/confidence | Unity serialization | Yes | `RyziContractTests` | Medium; version exists but no native DTO/fingerprint | Keep but adapt to export `Ryz1.Contracts` bundle | Medium |
| `Packages/com.ryzi.unity/Editor/Discovery/ProjectScanner.cs` | Unity scene/source scan and manifest generation | Unity Editor APIs | Yes | Contract tests | High for authoring layer | Keep and adapt to export task bundles | Medium |
| `Assets/Ryzi.Integrations/Editor/ExistingSimulatorProvider.cs` | Calibration, run, report, counterfactual integration for current simulator | Unity Editor, PlatformerPlaytest, CelesteBenchmark | Yes | Ryzi integration PlayMode tests | Medium; Unity-only authoring/reference path | Keep but adapt as Unity authoring bridge | High |
| `Assets/Scripts/CelesteBenchmark/RandomLevelGenerator.cs` | Procedural Unity level variations | UnityEngine | Yes | Random generator tests | Medium; source for SimCore level generator behavior | Keep; port subset to SimCore | Medium |
| `Packages/com.ryzi.unity/Editor/Commercial/CommercialServices.cs` | Placeholder local model distribution interfaces | Editor async tasks | No production model use | None specific | Low for P0; subscription/model distribution is P2 | Deprecate for P0, keep isolated | Low |
| `.claude/worktrees/*/Assets/PlatformerPlaytest/Runtime/Agents/Discovery/*` | Stranded mechanic detector/probe experiments | Unity prototype code in worktrees | No | Worktree-only tests | Unknown | Do not copy blindly; mine concepts only after active tests exist | Medium |

Compatibility strategy:

- Preserve Unity solver and reports while adding native SimCore in parallel.
- Use versioned DTOs as the boundary between Unity and native runtime.
- Do not remove old or stranded components until SimCore parity and dataset generation are tested.
- Label synthetic profiles as synthetic, not human-calibrated.
