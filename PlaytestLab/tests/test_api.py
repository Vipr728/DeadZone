import os
import time

os.environ["PLAYTEST_LAB_QWEN_MODE"] = "off"
os.environ["PLAYTEST_LAB_SEED_DEMO"] = "0"
os.environ.pop("PLAYTEST_LAB_VIEWER_TOKEN", None)
os.environ.pop("PLAYTEST_LAB_OPERATOR_TOKEN", None)

from fastapi.testclient import TestClient

from playtest_lab.main import app
import playtest_lab.main as main_module


client = TestClient(app)


def test_health_and_model_registry():
    assert client.get("/api/v1/health").status_code == 200
    response = client.get("/api/v1/models")
    assert response.status_code == 200
    assert len(response.json()["models"]) >= 4


def test_create_synthetic_run():
    response = client.post(
        "/api/v1/runs",
        json={
            "kind": "generate",
            "title": "API smoke",
            "domain": "platformer",
            "engine": "mock",
            "model_ids": ["gb10-ppo-seed42-step14978"],
            "episodes": 4,
            "use_qwen": False,
        },
    )
    assert response.status_code == 202
    run_id = response.json()["run_id"]
    for _ in range(100):
        record = client.get(f"/api/v1/runs/{run_id}").json()
        if record["status"] in {"complete", "failed"}:
            break
        time.sleep(0.02)
    assert record["status"] == "complete"
    assert record["report"]["evidence_tier"] == "synthetic"
    assert record["report"]["synthetic"] is True


def test_rejects_gb10_for_ark_topdown():
    response = client.post(
        "/api/v1/runs",
        json={"domain": "ark_topdown", "engine": "gb10_proxy"},
    )
    assert response.status_code == 422


def test_chat_calls_qwen_with_selected_run(monkeypatch):
    monkeypatch.setattr(
        main_module,
        "answer_question",
        lambda question, **kwargs: {
            "answer": f"Qwen answered: {question}",
            "model": "qwen3.6-35b-a3b",
            "mode": "nemoclaw",
            "prompt_sha256": "abc",
        },
    )
    response = client.post(
        "/api/v1/chat",
        json={
            "question": "Compare the checkpoints.",
            "model_ids": ["gb10-ppo-seed42-step14978"],
        },
    )
    assert response.status_code == 200
    assert response.json()["answer"] == "Qwen answered: Compare the checkpoints."
    assert response.json()["model"] == "qwen3.6-35b-a3b"
