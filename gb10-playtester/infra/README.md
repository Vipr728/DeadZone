# Playtester infra

Laptop-ready local report generation, orchestration, level watching, and
application-boundary egress controls for the GB10 playtester MVP.

```bash
cd infra
uv sync
uv run pytest
uv run playtester-report tests/fixtures/level_a_normal.json
uv run playtester-watch --once
uv run playtester-egress-proof
```

`config.yaml` is the single source of truth. Relative paths resolve from that
file, not from the caller's working directory. Ollama is the default local
backend. NIM and NemoClaw adapters expect local OpenAI-compatible endpoints.

The laptop egress proof is explicitly an application-level socket policy. A
real GB10 claim still requires the `network_namespace` backend or verified
OpenShell integration on the target machine.

Report output is first validated as raw LLM structured output, then checked
against deterministic aggregates using the thresholds in `reporting`. This
keeps narrative generation model-driven while preventing a small model from
miscounting failures or suppressing a telemetry-proven planted issue.

Live Ollama tests are opt-in and require a pulled model:

```bash
RUN_OLLAMA_TESTS=1 PLAYTESTER_RUN_OLLAMA_INTEGRATION=1 \
  OLLAMA_TEST_MODEL=deepseek-r1:8b \
  uv run pytest tests/test_live_ollama.py tests/test_ollama_integration.py
```

The default orchestrator consumes the shared Stage 1 checkpoint at
`rl/checkpoints/stage1/generalizer.ckpt`, requests the configured non-fake
Stage 2 mode, replays the exact exported checkpoint through ML-Agents
inference, validates telemetry, and atomically publishes the report.
`playtester-watch --once` processes new export markers idempotently; repeated
unchanged filesystem events do not launch duplicate training jobs.

The repository configuration now selects `orchestration.execution_mode:
remote`. In that mode the RL layer runs policy training/inference on the GB10,
then infra copies Mac-produced telemetry to the same GB10, invokes the local
report model there, validates the returned report, and copies it back into the
Mac reports directory. Set `PLAYTESTER_GB10_USER` before invoking the watcher;
the Tailscale hostname comes from `rl/configs/remote_execution.yaml`.
