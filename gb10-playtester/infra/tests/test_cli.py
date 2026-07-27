from __future__ import annotations

import json
import threading
from collections.abc import Iterator
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

import yaml

from playtester_infra.cli import main


def _valid_report() -> dict[str, Any]:
    return {
        "level_id": "level_b",
        "overall_difficulty": "too_hard",
        "difficulty_rationale": "Repeated deaths cluster at one jump.",
        "problem_points": [
            {
                "location": {
                    "piece_id": "gap_extreme_01",
                    "x": 18.1,
                    "y": -0.3,
                },
                "issue": "The extreme gap repeatedly kills the agent.",
                "severity": "high",
                "evidence": "Both episodes died near x=18 after many attempts.",
            }
        ],
        "teachability_assessment": (
            "The trained mechanic had no earlier level-local precedent."
        ),
        "planted_issue_detected": {
            "detected": True,
            "description": "Death cluster at the planted extreme gap.",
        },
    }


@contextmanager
def _ollama_server(
    report: dict[str, Any],
) -> Iterator[tuple[str, list[dict[str, Any]]]]:
    requests: list[dict[str, Any]] = []

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            if self.path != "/api/tags":
                self.send_error(404)
                return
            encoded = b'{"models":[{"name":"llama3.2:1b"}]}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)

        def do_POST(self) -> None:
            if self.path != "/api/generate":
                self.send_error(404)
                return
            length = int(self.headers["Content-Length"])
            requests.append(json.loads(self.rfile.read(length)))
            encoded = json.dumps(
                {"response": json.dumps(report)}
            ).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)

        def log_message(self, format: str, *args: object) -> None:
            return

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        host, port = server.server_address
        yield f"http://{host}:{port}", requests
    finally:
        server.shutdown()
        thread.join()
        server.server_close()


def _write_config(tmp_path: Path, llm_host: str) -> Path:
    source = Path(__file__).parents[1] / "config.yaml"
    document = yaml.safe_load(source.read_text(encoding="utf-8"))
    contracts_dir = Path(__file__).parents[2] / "contracts"
    fixture_dir = Path(__file__).parent / "fixtures"
    reports_dir = tmp_path / "reports"
    document["paths"].update(
        {
            "watched_levels_dir": str(tmp_path / "exports"),
            "builds_dir": str(tmp_path / "builds"),
            "telemetry_dir": str(fixture_dir),
            "reports_dir": str(reports_dir),
            "checkpoints_dir": str(tmp_path / "checkpoints"),
            "checkpoint_manifest": str(tmp_path / "manifest.json"),
            "contracts_dir": str(contracts_dir),
        }
    )
    document["llm"]["host"] = llm_host
    host_port = llm_host.removeprefix("http://")
    document["sandbox"].update(
        {
            "allowed_read_paths": [str(fixture_dir), str(contracts_dir)],
            "allowed_write_paths": [str(reports_dir)],
            "llm_allowlist": [host_port],
        }
    )
    config_path = tmp_path / "config.yaml"
    config_path.write_text(yaml.safe_dump(document), encoding="utf-8")
    return config_path


def test_report_command_runs_local_guarded_pipeline_end_to_end(
    tmp_path: Path, capsys: Any
) -> None:
    with _ollama_server(_valid_report()) as (llm_host, requests):
        config_path = _write_config(tmp_path, llm_host)
        fixture = Path(__file__).parent / "fixtures/planted_issue.telemetry.json"

        exit_code = main(
            ["report", str(fixture), "--config", str(config_path)]
        )

    assert exit_code == 0
    guarded = json.loads(capsys.readouterr().out)
    assert guarded["planted_issue_detected"]["detected"] is True
    assert "2 deaths" in guarded["planted_issue_detected"]["description"]
    assert guarded["overall_difficulty"] == "too_hard"
    planted = next(
        point
        for point in guarded["problem_points"]
        if point["location"]["piece_id"] == "gap_extreme_01"
    )
    assert planted["severity"] == "high"
    assert "2 recorded deaths" in planted["evidence"]
    assert len(requests) == 1
    assert requests[0]["format"]["title"] == "Playtester design report"
    assert (
        tmp_path
        / "reports"
        / "level_b_b8fe2b3d-cac3-496c-b8e2-a68221d18d94.json"
    ).is_file()


def test_egress_proof_command_requires_external_block_and_local_llm(
    tmp_path: Path, capsys: Any
) -> None:
    with _ollama_server(_valid_report()) as (llm_host, _):
        config_path = _write_config(tmp_path, llm_host)

        exit_code = main(
            ["egress-proof", "--config", str(config_path)]
        )

    assert exit_code == 0
    assert "PASS" in capsys.readouterr().out
