# Playtester Unity assets

This folder is intentionally organized around the locked MVP seams:

- `Scripts/Player` — Input System movement and jump adapter.
- `Scripts/Gym` — configurable Stage 1 composition pieces.
- `Scripts/Agent` — ML-Agents observations and rewards.
- `Scripts/EditorTool` — Unity Editor workflow.
- `Scripts/Inference` — Sentis ONNX playback.
- `Scripts/Telemetry` — telemetry-schema writer.
- `Configs`, `Tilemaps`, and `Scenes` — generated config caches and demo assets.

Add the ML-Agents and Sentis packages only after selecting versions compatible
with the installed Unity editor and Python ML-Agents runtime. They are not
included in the base manifest because a mismatched package pair would make a
new project fail to resolve on first open.
