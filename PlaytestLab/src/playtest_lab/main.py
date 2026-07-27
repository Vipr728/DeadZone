from __future__ import annotations

import asyncio
import json
import os
import uuid
from contextlib import asynccontextmanager
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Annotated, Any

import uvicorn
from fastapi import Depends, FastAPI, Header, HTTPException, Request
from fastapi.responses import FileResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles

from . import __version__
from .engines import execute
from .qwen import MODEL_ID, answer_question, synthesize
from .registry import load_registry
from .schemas import ChatRequest, RunRecord, RunRequest
from .store import Store


ROOT = Path(__file__).resolve().parents[2]
DATA_DIR = Path(os.getenv("PLAYTEST_LAB_DATA_DIR", str(ROOT / "data"))).expanduser()
store = Store(DATA_DIR / "playtest-lab.sqlite3")
executor = ThreadPoolExecutor(max_workers=int(os.getenv("PLAYTEST_LAB_WORKERS", "2")))


@asynccontextmanager
async def lifespan(_: FastAPI):
    seed_demo_runs()
    yield


app = FastAPI(title="RYZ Playtest Lab", version=__version__, lifespan=lifespan)


def _role(
    request: Request,
    authorization: Annotated[str | None, Header()] = None,
) -> str:
    viewer = os.getenv("PLAYTEST_LAB_VIEWER_TOKEN", "")
    operator = os.getenv("PLAYTEST_LAB_OPERATOR_TOKEN", "")
    if not viewer and not operator:
        if request.client and request.client.host not in {"127.0.0.1", "::1", "testclient"}:
            raise HTTPException(403, "Tokens are required for non-loopback access")
        return "operator"
    token = (authorization or "").removeprefix("Bearer ").strip()
    if operator and token == operator:
        return "operator"
    if viewer and token == viewer:
        return "viewer"
    raise HTTPException(401, "Invalid Playtest Lab token")


def require_operator(role: Annotated[str, Depends(_role)]) -> str:
    if role != "operator":
        raise HTTPException(403, "Operator token required")
    return role


def _run_job(run_id: str, request: RunRequest) -> None:
    store.update(run_id, status="running")
    store.event(run_id, "runner.started", "Deterministic analysis started.", {})

    def progress(event_type: str, summary: str, payload: dict[str, Any] | None = None) -> None:
        store.event(run_id, event_type, summary, payload or {})

    try:
        report = execute(request, progress)
        if request.use_qwen and report.get("models"):
            store.event(run_id, "qwen.started", f"Requesting grounded synthesis from {MODEL_ID}.", {})
            try:
                report["qwen"] = synthesize(report)
                report["qwen"]["evidence_tier"] = "llm_inferred"
                store.event(run_id, "qwen.complete", "Grounded Qwen synthesis completed.", {})
            except Exception as error:
                report["qwen"] = {
                    "evidence_tier": "llm_inferred",
                    "available": False,
                    "error": str(error),
                    "executive_summary": report["summary"],
                    "limitations": ["Deterministic metrics remain valid; Qwen synthesis was unavailable."],
                }
                store.event(run_id, "qwen.fallback", "Qwen unavailable; deterministic report preserved.", {})
        current = store.get(run_id)
        if current and current["status"] == "canceled":
            return
        store.update(run_id, status="complete", report=report)
        store.event(run_id, "runner.complete", "Analysis completed.", {})
    except Exception as error:
        store.update(run_id, status="failed", error=str(error))
        store.event(run_id, "runner.failed", f"Analysis failed: {error}", {})


def seed_demo_runs() -> None:
    if os.getenv("PLAYTEST_LAB_SEED_DEMO", "1").lower() in {"0", "false", "off"}:
        return
    if store.list(limit=1):
        return
    demo_requests = [
        RunRequest(
            kind="compare",
            title="GB10 PPO checkpoint comparison",
            domain="platformer",
            engine="gb10_proxy",
            model_ids=[
                "gb10-ppo-seed42-step14978",
                "gb10-ppo-seed43-step14977",
                "gb10-ppo-seed44-step14995",
            ],
            episodes=12,
            seed=42,
            source={"stress": 0.65},
            use_qwen=True,
        ),
        RunRequest(
            kind="generate",
            title="RYZ-1 mixed symbolic puzzle audit",
            domain="symbolic_puzzle",
            engine="ryz_simcore",
            model_ids=["ryz1-mixed-v1-20260726"],
            episodes=18,
            seed=20260726,
            source={"stress": 0.55},
            use_qwen=True,
        ),
    ]
    for request in demo_requests:
        run_id = str(uuid.uuid4())
        store.create(run_id, request.title, request.model_dump(mode="json"))
        executor.submit(_run_job, run_id, request)


@app.get("/api/v1/health")
def health() -> dict[str, Any]:
    registry = load_registry(verify=False)
    return {
        "ok": True,
        "version": __version__,
        "service": "ryz-playtest-lab",
        "qwen": {"model": MODEL_ID, "mode": os.getenv("PLAYTEST_LAB_QWEN_MODE", "nemoclaw")},
        "models_available": sum(
            bool(model.get("onnx", {}).get("available")) for model in registry["models"]
        ),
        "evidence_policy": "dashboard metrics require Unity validation before release sign-off",
    }


@app.get("/api/v1/models")
def models(_: Annotated[str, Depends(_role)]) -> dict[str, Any]:
    return load_registry(verify=False)


@app.get("/api/v1/runs", response_model=list[RunRecord])
def list_runs(_: Annotated[str, Depends(_role)]) -> list[dict[str, Any]]:
    return store.list()


@app.post("/api/v1/runs", response_model=RunRecord, status_code=202)
def create_run(
    body: RunRequest,
    _: Annotated[str, Depends(require_operator)],
) -> dict[str, Any]:
    run_id = str(uuid.uuid4())
    store.create(run_id, body.title, body.model_dump(mode="json"))
    executor.submit(_run_job, run_id, body)
    record = store.get(run_id)
    assert record is not None
    return record


@app.get("/api/v1/runs/{run_id}", response_model=RunRecord)
def get_run(run_id: str, _: Annotated[str, Depends(_role)]) -> dict[str, Any]:
    record = store.get(run_id)
    if record is None:
        raise HTTPException(404, "Unknown run id")
    return record


@app.post("/api/v1/runs/{run_id}/cancel", response_model=RunRecord)
def cancel_run(
    run_id: str,
    _: Annotated[str, Depends(require_operator)],
) -> dict[str, Any]:
    record = store.get(run_id)
    if record is None:
        raise HTTPException(404, "Unknown run id")
    if record["status"] in {"queued", "running"}:
        store.update(run_id, status="canceled")
        store.event(run_id, "runner.canceled", "Cancellation requested.", {})
    return store.get(run_id)  # type: ignore[return-value]


@app.get("/api/v1/runs/{run_id}/report")
def report(run_id: str, _: Annotated[str, Depends(_role)]) -> dict[str, Any]:
    record = store.get(run_id)
    if record is None:
        raise HTTPException(404, "Unknown run id")
    if record["report"] is None:
        raise HTTPException(409, "Report is not ready")
    return record["report"]


@app.post("/api/v1/chat")
def chat(
    body: ChatRequest,
    _: Annotated[str, Depends(require_operator)],
) -> dict[str, Any]:
    report_data: dict[str, Any] | None = None
    if body.run_id:
        record = store.get(body.run_id)
        if record is None:
            raise HTTPException(404, "Unknown run id")
        report_data = record["report"]
    try:
        result = answer_question(
            body.question,
            report=report_data,
            model_ids=body.model_ids,
            history=[message.model_dump() for message in body.history],
        )
    except Exception as error:
        raise HTTPException(503, f"Qwen inference failed: {error}") from error
    return {
        **result,
        "run_id": body.run_id,
        "grounded": report_data is not None,
    }


@app.get("/api/v1/runs/{run_id}/events")
async def events(run_id: str, _: Annotated[str, Depends(_role)]) -> StreamingResponse:
    if store.get(run_id) is None:
        raise HTTPException(404, "Unknown run id")

    async def stream():
        sent = 0
        while True:
            record = store.get(run_id)
            if record is None:
                return
            for event in record["events"][sent:]:
                yield f"data: {json.dumps(event)}\n\n"
            sent = len(record["events"])
            if record["status"] in {"complete", "failed", "canceled"}:
                return
            await asyncio.sleep(0.5)

    return StreamingResponse(stream(), media_type="text/event-stream")


FRONTEND = ROOT / "frontend" / "dist"
if FRONTEND.is_dir():
    app.mount("/assets", StaticFiles(directory=FRONTEND / "assets"), name="assets")

    @app.get("/{path:path}", include_in_schema=False)
    def frontend(path: str) -> FileResponse:
        candidate = FRONTEND / path
        return FileResponse(candidate if candidate.is_file() else FRONTEND / "index.html")


def run() -> None:
    uvicorn.run(
        "playtest_lab.main:app",
        host=os.getenv("PLAYTEST_LAB_HOST", "127.0.0.1"),
        port=int(os.getenv("PLAYTEST_LAB_PORT", "8788")),
        reload=False,
    )
