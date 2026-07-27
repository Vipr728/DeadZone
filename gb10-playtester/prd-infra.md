# PRD — Infra/Backend Lead (Abhi)

Read `PRD.md` first for shared contracts, repo layout, modularity rules, and timeline. This file is your file-by-file build list. Every task is tagged `[laptop-ok]` or `[gb10-only]`.

Per your answer, OpenClaw/NemoClaw/OpenShell are sponsor-provided tools whose exact APIs are TBD — everything below is spec'd behind an abstraction layer so a real SDK can be swapped in at any point (even mid-event, if sponsor docs/onboarding become available) without touching the report-pipeline logic. Build the honest-local-inference version first; treat the "real" sponsor tool as a pluggable backend, not a blocker.

---

## 1. `infra/config.yaml` — single source of truth `[laptop-ok]`

```yaml
paths:
  watched_levels_dir: "../unity/PlaytesterProject/Exports"   # LOCKED — OpenClaw watches for level_export.json markers here, see §4
  builds_dir: "../unity/PlaytesterProject/Builds"             # LOCKED — matching executables live here, written BEFORE their marker
  telemetry_dir: "../unity/PlaytesterProject/Telemetry"       # where TelemetryRecorder.cs writes
  reports_dir: "../unity/PlaytesterProject/Reports"           # where ReportPanel.cs reads from
  checkpoint_manifest: "../rl/checkpoint_manifest.json"

llm:
  backend: ollama              # TUNABLE — "ollama" | "nim" | "nemoclaw" (once real SDK available)
  model: "llama3.1:8b"          # TUNABLE — [laptop-ok] default; swap to real 70B-class model for [gb10-only] demo runs
  gb10_model: "llama3.3:70b"    # TUNABLE — used when backend config flag `use_gb10_model: true`
  host: "http://localhost:11434"  # LOCKED — the one address every egress-blocking mechanism below must allow through

sandbox:
  allowed_read_paths: ["../unity/PlaytesterProject/Exports", "../unity/PlaytesterProject/Telemetry"]
  allowed_write_paths: ["../unity/PlaytesterProject/Reports", "../rl/checkpoints"]
  egress_policy: block_all      # TUNABLE — "block_all" | "allow_list" (real OpenShell may impose its own scheme)
  llm_allowlist: ["localhost:11434"]  # LOCKED — parsed from llm.host; see §6, this is what keeps Ollama reachable under block_all
```

Every one of these fields is what changes when swapping laptop→GB10, or fake-scaffolding→real-sponsor-SDK — never a code change, per the modularity rule in `PRD.md` §1. **These paths are config-authoritative, full stop** — per `PRD.md`'s async-independence guarantee, none of them need confirming with Rahul or anyone else. `watched_levels_dir` is locked precisely because Unity's export step (`prd-unity.md` §4) writes its marker file there in a fixed, agreed order relative to `builds_dir` — see §4 below for exactly what that ordering guarantees.

---

## 2. `infra/src/playtester_infra/llm_client.py` `[laptop-ok]`

```python
class ILLMClient(Protocol):
    def generate_structured(self, prompt: str, schema: dict) -> dict:
        """Returns a dict conforming to `schema` (contracts/report.schema.json).
        Implementations enforce structured output via the backend's native mechanism
        (JSON mode, function-calling, or grammar-constrained decoding) — never regex-parsed
        free text, since a malformed report breaks the demo silently."""

class OllamaClient(ILLMClient):
    """Default. Talks to a local Ollama server (http://localhost:11434) via its
    /api/generate or /api/chat endpoint with format="json" and the schema embedded
    in the system prompt. Model name read from infra/config.yaml."""

class NimClient(ILLMClient):
    """Stub for a NIM/TensorRT-LLM OpenAI-compatible local endpoint — same interface,
    fill in once/if a real local NIM container is confirmed available. Not required
    to be built unless time allows; the interface existing is what matters."""
```

**Setup task, hour 0–4 `[laptop-ok]`:** confirm Ollama is installable/runnable on the actual GB10 image ahead of time if possible; if GB10 access isn't available until later, this is exactly why `OllamaClient` is the dev-path default — build and test the whole pipeline against a small local model first, swap the model name for the real one once on the GB10.

## 3. `infra/src/playtester_infra/report_pipeline.py` `[laptop-ok]`

```python
def compute_level_local_precedent(piece_results: list[dict]) -> list[bool]:
    """The OTHER half of spec §4.1's teachability heuristic — NOT the same
    signal as telemetry's `seen_in_stage1_range` (PRD.md §3.1 is explicit
    about this distinction; do not conflate them). For each piece_result at
    index i, returns True if any piece_result at index j < i in the SAME
    list has the same piece_type (a same-skill precedent already appeared
    earlier in this level's own playthrough). piece_results is already
    ordered by traversal — both the RL-side telemetry writer and the real
    Unity TelemetryRecorder append in play order — so this needs no new
    telemetry field, only this one pure function over data already on the
    wire. Called once per episode_summary before prompt construction."""

def generate_report(telemetry_path: str, llm_client: ILLMClient) -> dict:
    """1. Load + validate telemetry JSON against contracts/telemetry.schema.json
          (reuse rl/src/playtester_rl/telemetry_writer.validate_telemetry — import
          across the /rl and /infra package boundary, or duplicate the schema check
          if keeping the two packages fully independent is preferred; decide based
          on whether uv workspace sharing is set up, don't block on this decision).
       2. For each episode_summary, call compute_level_local_precedent(piece_results)
          and attach the result alongside seen_in_stage1_range in the prompt context —
          two distinct, clearly-labeled booleans per piece, not one conflated signal.
       3. Render a prompt from infra/src/playtester_infra/prompts/report_prompt.md.j2
          (Jinja2 template, NOT an inlined f-string — versioned, editable without
          touching pipeline code, per PRD.md modularity rule).
       4. Call llm_client.generate_structured(prompt, report.schema.json).
       5. Validate the response against report.schema.json before writing.
       6. Write to infra/config.yaml's reports_dir, filename = f"{level_id}_{run_id}.json".
    """
```

`prompts/report_prompt.md.j2` — the template receives the full telemetry doc as context, PLUS the `compute_level_local_precedent` result per piece, and instructs the model to reason over both signals explicitly labeled (e.g. `seen_in_stage1_range: was this difficulty ever inside Stage 1's trained range` vs. `taught_earlier_in_this_level: has an equivalent piece appeared earlier in THIS level's own layout`) to produce the difficulty/problem-points/teachability/planted-issue fields — never hand the model `seen_in_stage1_range` alone and ask it to infer the level-local half, since that field cannot answer that question (PRD.md §3.1). Write this prompt iteratively against **fixture telemetry** (hand-authored JSON matching the schema, simulating a run with an obvious problem point) before any real training run exists — this is explicitly independent of RL training outcome per spec §11 hours 0-4, so start immediately.

## 4. `infra/src/playtester_infra/openclaw_skill.py` `[laptop-ok]`

```python
class LevelWatcher:
    """Watches infra/config.yaml's watched_levels_dir for new
    <level_id>/level_export.json marker files (via watchdog, or polling if
    watchdog unavailable) — NOT raw scene/tilemap assets. A marker's mere
    presence is a hard guarantee, owned by Unity's export step
    (prd-unity.md §4): that step writes the build to builds_dir FIRST and
    the marker to watched_levels_dir only AFTER the build succeeds. So
    LevelWatcher never needs to poll/wait for a build to appear — by the
    time on_new_level() fires, contracts/checkpoint_manifest or the raw
    Builds/<level_id>/ path (read from the marker itself, see schema below)
    is already populated. On a new marker, triggers: Stage 2 fine-tune
    (calls into /rl/scripts/finetune_stage2.sh as a subprocess) -> playtest
    run -> generate_report(). This IS the 'OpenClaw agent skill' — if the
    real OpenClaw SDK/onboarding becomes available, this class's run loop
    gets replaced by registering the same trigger logic as an OpenClaw
    skill definition; the trigger logic itself (subprocess calls +
    generate_report) doesn't change.

    level_export.json shape (written by Unity, read here):
        {"level_id": str, "build_path": str, "scene_path": str, "exported_at": str}
    """

    def run(self) -> None:
        """Blocking watch loop — acceptable for a demo, no need for production-grade
        service management given the 29-hour window."""
```

Keep the actual trigger logic (what happens on a new level) as a standalone function separate from the watch-loop mechanics, specifically so swapping "polling loop" for "real OpenClaw skill registration" is a thin wrapper change, not a rewrite — same modularity principle as everywhere else in this PRD.

**This is the resolution to the Exports/-vs-Builds/ transition** flagged as underspecified: the two directories serve two different jobs (Exports/ is a trigger signal, Builds/ is the actual executable), reconciled by a single ordering guarantee owned entirely by Unity's export step, not by anything infra needs to poll, retry, or coordinate live about. See `PRD.md`'s async-independence section and `prd-unity.md` §4 for the Unity-side half of this contract.

## 5. `infra/nemoclaw_setup.sh` `[gb10-only, best-effort]`

Skeleton onboarding script — since NemoClaw's real one-command install (`curl -fsSL https://www.nvidia.com/nemoclaw.sh | bash` per the source spec) is unconfirmed/TBD, this script's job is:
1. Check whether the real NemoClaw installer URL is reachable/documented once you have GB10 access — if yes, follow it for real and drop this stub.
2. If not available in time, this script instead does the honest equivalent manually: ensures Ollama is installed and running, pulls the configured model, confirms `infra/config.yaml`'s paths exist. **Do not claim "NemoClaw" in the pitch if this fallback path is what's actually running** — the pitch's honest-framing rule (spec §6) applies to internal tooling claims too, not just the RL claims.

## 6. `infra/src/playtester_infra/openshell_policy.py` `[laptop-ok for structure, gb10-only for the real proof]`

```python
class IEgressPolicy(Protocol):
    def is_write_allowed(self, path: str) -> bool: ...
    def is_read_allowed(self, path: str) -> bool: ...
    def block_egress(self) -> None:
        """Applies the actual network block WHILE keeping infra/config.yaml's
        llm.host (default http://localhost:11434, where Ollama listens)
        reachable — a block that also kills the local model server isn't a
        no-egress proof, it's just breakage. Two ranked options, both must
        satisfy this constraint:

        1. RECOMMENDED DEFAULT — application-level allow-list: wrap/monkeypatch
           the HTTP client library used anywhere in /infra so it only permits
           requests whose host:port matches config.yaml's sandbox.llm_allowlist
           (parsed from llm.host), raising for everything else. This sidesteps
           OS-level networking entirely — Ollama keeps running as a normal local
           process, nothing needs to share a namespace with it, and the
           mechanism is exactly as easy to demo (attempt a real external call,
           watch it raise; call Ollama, watch it succeed).
        2. IF OS-level defense-in-depth is wanted: a bare `unshare --net`
           namespace gets its OWN isolated loopback — it will NOT reach a
           localhost:11434 Ollama server running in the host's namespace, this
           is not a corner case, it is how network namespaces work. If this
           route is taken, Ollama's server process must be started INSIDE the
           same namespace as the sandboxed report-generation process (so they
           share one loopback), with that namespace's only route being to
           nothing — never try to block from outside a fully-isolated
           namespace and then poke a hole in from the outside for Ollama.
           iptables/nftables DROP rules scoped to the process's user/cgroup are
           a further alternative, with the same loopback-sharing requirement
           and the same sudo-availability risk already noted below.
        - Real OpenShell, if its actual policy schema becomes documented in time —
          swap this implementation, keep the IEgressPolicy interface call sites unchanged."""

def demo_egress_proof() -> bool:
    """CLI entry point for the live demo: attempts an outbound HTTP call
    (e.g. to a real external URL) from inside the sandboxed process/environment,
    catches the failure, THEN makes a real call to infra/config.yaml's llm.host
    and confirms it succeeds — the proof isn't complete without showing Ollama
    still works under the same block, not just that the internet is blocked.
    Prints PASS/FAIL for both halves. This is the literal script run live
    on stage per spec §9 — test this for real well before hours 23-27, don't
    discover sudo/permission issues during rehearsal."""
```

Given the loopback-sharing gotcha above, option 1 (application-level allow-list) is the recommended starting point, not just a fallback — it has no OS-permission risk, no namespace/Ollama interaction to get wrong, and satisfies the demo requirement (spec §9) exactly as convincingly. Reach for option 2 only if there's genuine slack and a specific reason to want OS-level enforcement too. Either way, decide and lock this by hour 8 — debugging real OS sandboxing under time pressure late in the schedule is exactly the kind of thing spec §7's gate discipline warns against for the RL side, and the same discipline applies here. If option 2 is chosen, confirm the demo machine actually allows `unshare --net`/iptables (may need elevated permissions not available on a shared/managed GB10 unit) before committing to it.

---

## 7. Shared contract ownership

`contracts/telemetry.schema.json` (`PRD.md` §3.1) is already locked, in writing, in this PRD — it does not need a real-time conversation with ML/RL lead to confirm, per `PRD.md`'s async-independence guarantee. Every field `report_pipeline.py` consumes is already specified there: `seen_in_stage1_range` (Stage-1-training-range fact, RL-computed) and `death_position` are both required schema fields, and `compute_level_local_precedent` (§3 above) derives the level-local teachability signal from `piece_results`' existing ordering — no additional field negotiation needed. If a genuine new field turns out to be needed once real telemetry exists, resolve it the same way as any other mid-build ambiguity (`PRD.md`'s async-independence section): add it to `contracts/telemetry.schema.json` and commit — the commit is the message, not a synchronous conversation.

---

## 8. Timeline mapping

- **Hours 0–4:** `infra/config.yaml`, `ILLMClient`/`OllamaClient`, start `report_prompt.md.j2` against hand-authored fixture telemetry — all independent of RL training outcome. Lock telemetry schema with ML/RL lead.
- **Hours 4–8:** continue report pipeline; scaffold `LevelWatcher` and `openshell_policy.py` structure.
- **Hours 8–14 (sleep window, on-call for silent failures):** overnight Claude Code sessions on report-generation prompt refinement, `LevelWatcher` event-trigger wiring, `nemoclaw_setup.sh`, `openshell_policy.py` — all well-specified, low-ambiguity per spec §11.
- **Hours 14–18:** review overnight output, fix issues.
- **Hours 18–23:** full pipeline integration test (real telemetry from a real playtest run → real report); confirm/lock the egress-block mechanism.
- **Hours 23–27:** run `demo_egress_proof()` live repeatedly, confirm reliability.

## 9. Testing

- `infra/tests/test_report_pipeline.py` — fixture telemetry (including one with an obvious planted-issue-shaped death cluster) → `generate_report()` → assert output validates against `report.schema.json` and `planted_issue_detected.detected == True` for the planted fixture.
- `infra/tests/test_llm_client.py` — `OllamaClient` against a running local Ollama instance (skip if unavailable in CI/dev env, mark accordingly) — assert malformed-schema responses raise rather than silently passing through.
- `infra/tests/test_openshell_policy.py` — `demo_egress_proof()` returns `False`/raises when egress is correctly blocked, sanity-check against a control case with no block applied (should succeed) so the test itself isn't vacuous.
