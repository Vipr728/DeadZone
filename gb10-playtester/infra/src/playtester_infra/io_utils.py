"""Crash-safe JSON file operations used by reports and pipeline state."""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path
from typing import Any


class FileAlreadyExistsError(FileExistsError):
    """Raised when an immutable output would otherwise be overwritten."""


def _encoded_json(document: Any) -> bytes:
    return (json.dumps(document, indent=2, sort_keys=True) + "\n").encode("utf-8")


def atomic_create_json(path: Path, document: Any) -> None:
    """Publish a complete JSON file atomically and fail if the target exists."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temp_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False
        ) as handle:
            temp_path = Path(handle.name)
            handle.write(_encoded_json(document))
            handle.flush()
            os.fsync(handle.fileno())
        try:
            os.link(temp_path, path)
        except FileExistsError as exc:
            raise FileAlreadyExistsError(f"Refusing to overwrite existing file: {path}") from exc
    finally:
        if temp_path is not None:
            temp_path.unlink(missing_ok=True)

def atomic_replace_json(path: Path, document: Any) -> None:
    """Atomically replace mutable internal state with a complete JSON file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temp_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False
        ) as handle:
            temp_path = Path(handle.name)
            handle.write(_encoded_json(document))
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_path, path)
        temp_path = None
    finally:
        if temp_path is not None:
            temp_path.unlink(missing_ok=True)
