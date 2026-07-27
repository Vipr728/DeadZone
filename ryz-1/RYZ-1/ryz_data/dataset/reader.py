from __future__ import annotations
import json
from pathlib import Path
from typing import Any, Iterator

class DatasetReader:
    def __init__(self, root: Path): self.root = root
    def rows(self, table: str = "transitions") -> Iterator[dict[str, Any]]:
        try:
            import pyarrow.parquet as pq
        except ImportError as exc: raise RuntimeError("PyArrow is required to read this dataset") from exc
        for path in sorted((self.root / table).glob("*.parquet")):
            for row in pq.read_table(path).to_pylist():
                yield {k: self._decode(v) for k, v in row.items()}
    @staticmethod
    def _decode(value: Any) -> Any:
        if isinstance(value, str):
            try: return json.loads(value)
            except json.JSONDecodeError: return value
        return value
