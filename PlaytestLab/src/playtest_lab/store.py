from __future__ import annotations

import json
import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


class Store:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self.connection = sqlite3.connect(path, check_same_thread=False)
        self.connection.row_factory = sqlite3.Row
        self.lock = threading.RLock()
        with self.connection:
            self.connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS runs (
                    id TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    title TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    report_json TEXT,
                    error TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS events (
                    run_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    type TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    PRIMARY KEY(run_id, sequence)
                );
                """
            )
            self.connection.execute(
                "UPDATE runs SET status='failed', error='Service restarted during run', updated_at=? "
                "WHERE status IN ('queued','running')",
                (now(),),
            )

    def create(self, run_id: str, title: str, request: dict[str, Any]) -> None:
        timestamp = now()
        with self.lock, self.connection:
            self.connection.execute(
                "INSERT INTO runs VALUES (?, 'queued', ?, ?, NULL, '', ?, ?)",
                (run_id, title, json.dumps(request), timestamp, timestamp),
            )
        self.event(run_id, "runner.created", "Analysis queued.", {})

    def update(
        self,
        run_id: str,
        *,
        status: str,
        report: dict[str, Any] | None = None,
        error: str = "",
    ) -> None:
        with self.lock, self.connection:
            self.connection.execute(
                "UPDATE runs SET status=?, report_json=?, error=?, updated_at=? WHERE id=?",
                (status, json.dumps(report) if report is not None else None, error, now(), run_id),
            )

    def event(self, run_id: str, event_type: str, summary: str, payload: dict[str, Any]) -> None:
        with self.lock, self.connection:
            sequence = self.connection.execute(
                "SELECT COALESCE(MAX(sequence), 0) + 1 FROM events WHERE run_id=?", (run_id,)
            ).fetchone()[0]
            timestamp = now()
            self.connection.execute(
                "INSERT INTO events VALUES (?, ?, ?, ?, ?, ?)",
                (run_id, sequence, event_type, summary, json.dumps(payload), timestamp),
            )
            self.connection.execute("UPDATE runs SET updated_at=? WHERE id=?", (timestamp, run_id))

    def get(self, run_id: str) -> dict[str, Any] | None:
        with self.lock:
            row = self.connection.execute("SELECT * FROM runs WHERE id=?", (run_id,)).fetchone()
            if row is None:
                return None
            events = self.connection.execute(
                "SELECT * FROM events WHERE run_id=? ORDER BY sequence", (run_id,)
            ).fetchall()
        return {
            "ok": row["status"] != "failed",
            "run_id": row["id"],
            "status": row["status"],
            "title": row["title"],
            "created_at": row["created_at"],
            "updated_at": row["updated_at"],
            "request": json.loads(row["request_json"]),
            "report": json.loads(row["report_json"]) if row["report_json"] else None,
            "error": row["error"],
            "events": [
                {
                    "sequence": event["sequence"],
                    "type": event["type"],
                    "summary": event["summary"],
                    "payload": json.loads(event["payload_json"]),
                    "timestamp": event["timestamp"],
                }
                for event in events
            ],
        }

    def list(self, limit: int = 100) -> list[dict[str, Any]]:
        with self.lock:
            ids = [
                row[0]
                for row in self.connection.execute(
                    "SELECT id FROM runs ORDER BY created_at DESC LIMIT ?", (limit,)
                ).fetchall()
            ]
        return [record for run_id in ids if (record := self.get(run_id))]

