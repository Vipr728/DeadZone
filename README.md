# Ark (RYZ-1)

Ark is a GB10-focused platformer playtesting showcase with two independently
runnable tracks.

## RYZ-1 native neural playtesting

`ryz-1/RYZ-1` pairs Unity authoring with a native ARM64 SimCore runtime,
deterministic replay verification, sequence training, and a Unity neural
bridge. The included curriculum checkpoint supports the bridge workflow.

```bash
cd ryz-1/RYZ-1
scripts/verify_gb10_runtime.sh
scripts/run_hackathon_demo.sh
```

For the Mac-to-GB10 Unity bridge, configure the environment variables described
in `scripts/run_unity_bridge.sh` and run that script from the same directory.

## GB10 compositional playtester

`gb10-playtester` contains the Unity ML-Agents playtester, RL training and
playback pipeline, telemetry/reporting infrastructure, and GB10 remote
execution path.

```bash
cd gb10-playtester/rl
uv sync
uv run pytest -v

cd ../infra
uv sync
uv run pytest
```

See `gb10-playtester/rl/README.md` for local, real, and remote training modes,
and `gb10-playtester/infra/README.md` for the reporting and orchestration flow.
