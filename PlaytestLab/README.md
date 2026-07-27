# DeadZone Hackathon Playtest Lab

This is the localhost hackathon demo: a Qwen-backed, chat-style game QA
dashboard that lets a developer select RYZ-1 or GB10 checkpoints, simulate a
training/evaluation run, and present seeded bug, deadzone, impossibility, and
difficulty findings. It remains useful without Unity. Demo-fidelity results are
always labeled synthetic.

## Run

```bash
cd PlaytestLab
uv sync --dev
uv run playtest-lab
```

Open `http://127.0.0.1:8788`. The frontend build is produced with:

```bash
cd frontend
npm install
npm run build
```

Useful environment variables:

- `PLAYTEST_LAB_HOST` / `PLAYTEST_LAB_PORT`
- `PLAYTEST_LAB_DATA_DIR`
- `PLAYTEST_LAB_VIEWER_TOKEN` / `PLAYTEST_LAB_OPERATOR_TOKEN`
- `GB10_PROJECT_ROOT` and `RYZ1_PROJECT_ROOT`
- `PLAYTEST_LAB_QWEN_MODE=nemoclaw|direct|off`
- `PLAYTEST_LAB_QWEN_BASE_URL` for the loopback-only direct development mode

Qwen narrative generation is downstream of deterministic metrics. If Qwen or
OpenClaw is unavailable, the run still completes with a deterministic report.

## Local service and Tailscale

```bash
systemctl --user link "$PWD/deploy/playtest-lab.service"
systemctl --user enable --now playtest-lab.service
tailscale serve --bg 8788
```

The application remains bound to loopback. Tailscale Serve exposes only the
dashboard to authenticated tailnet members; the vLLM endpoint stays private.
Set viewer/operator tokens in `~/.config/ryz-playtest-lab/env` when application
roles are needed:

```text
PLAYTEST_LAB_VIEWER_TOKEN=...
PLAYTEST_LAB_OPERATOR_TOKEN=...
```
