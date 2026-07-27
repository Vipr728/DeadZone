from __future__ import annotations
import json, os, uuid
from collections import defaultdict
from pathlib import Path
from typing import Any
from .schema import SCHEMA_VERSION, TransitionRecord

TABLES = ("tasks", "trials", "trajectories", "transitions", "calibrations", "candidate_actions")

class DatasetWriter:
    """Single-process atomic writer. Workers return records; only this writer owns shards."""
    def __init__(self, root: Path, shard_size: int = 25_000) -> None:
        self.root, self.shard_size = root, shard_size; self.buffers: dict[str, list[dict[str, Any]]] = defaultdict(list)
        self.counts: dict[str, int] = defaultdict(int); self.shard_indices: dict[str, int] = defaultdict(int); self.ids: set[str] = set(); root.mkdir(parents=True, exist_ok=True)
        for name in TABLES: (root / name).mkdir(exist_ok=True)
        self._load_checkpoint()
        for name in TABLES:
            self.shard_indices[name] = len(list((root / name).glob("*.parquet")))
    def _load_checkpoint(self) -> None:
        path = self.root / "generation_state" / "checkpoint.json"
        if path.exists():
            payload = json.loads(path.read_text()); self.counts.update(payload.get("counts", {})); self.ids.update(payload.get("ids", []))
    def add(self, table: str, record: dict[str, Any] | TransitionRecord) -> None:
        row = record.to_dict() if isinstance(record, TransitionRecord) else record
        row.setdefault("schema_version", SCHEMA_VERSION)
        key = row.get("transition_id") or row.get("task_id") or str(uuid.uuid4())
        if table == "transitions" and key in self.ids: return
        if table == "transitions": self.ids.add(key)
        self.buffers[table].append(row)
        if len(self.buffers[table]) >= self.shard_size: self.flush(table)
    def _write_rows(self, table: str, rows: list[dict[str, Any]], index: int) -> None:
        # Storing complex fields as JSON keeps schema evolution safe while task/trial data stays normalized.
        # JSON-encode all non-string scalars as well: probe values can legitimately be
        # bool, int, float, or null in one column as mechanics vary by task.
        encoded = [{k: json.dumps(v, sort_keys=True, separators=(",", ":")) if not isinstance(v, str) and v is not None else v for k, v in row.items()} for row in rows]
        final = self.root / table / f"{table}-{index:05d}.parquet"; temporary = final.with_suffix(".parquet.tmp")
        try:
            import pyarrow as pa
            import pyarrow.parquet as pq
            pq.write_table(pa.Table.from_pylist(encoded), temporary, compression="zstd")
        except ImportError as exc:
            raise RuntimeError("PyArrow is required for production Parquet dataset writing; install ryz-data dependencies") from exc
        os.replace(temporary, final)
    def flush(self, table: str | None = None) -> None:
        tables = (table,) if table else tuple(self.buffers)
        for name in tables:
            rows = self.buffers[name]
            if not rows: continue
            self._write_rows(name, rows, self.shard_indices[name])
            self.shard_indices[name] += 1
            self.counts[name] += len(rows); self.buffers[name] = []
        self.checkpoint()
    def checkpoint(self) -> None:
        state = self.root / "generation_state"; state.mkdir(exist_ok=True)
        tmp = state / "checkpoint.tmp"; final = state / "checkpoint.json"
        tmp.write_text(json.dumps({"schema_version": SCHEMA_VERSION, "counts": dict(self.counts), "ids": sorted(self.ids)}, indent=2)); os.replace(tmp, final)
    def close(self, metadata: dict[str, Any] | None = None) -> None:
        self.flush(); (self.root / "metadata.json").write_text(json.dumps({"schema_version": SCHEMA_VERSION, **(metadata or {})}, indent=2))
