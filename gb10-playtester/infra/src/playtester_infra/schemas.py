"""Shared JSON Schema loading and validation helpers."""

from __future__ import annotations

import json
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import SchemaError, ValidationError

from playtester_infra.config import REPO_ROOT

CONTRACTS_DIR = REPO_ROOT / "contracts"


class DocumentValidationError(ValueError):
    """A document does not conform to one of the locked shared contracts."""


def load_schema(name: str) -> dict[str, Any]:
    path = CONTRACTS_DIR / name
    try:
        schema = json.loads(path.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
    except (OSError, json.JSONDecodeError, SchemaError) as exc:
        raise DocumentValidationError(f"Invalid or unavailable schema {path}: {exc}") from exc
    return schema


def validate_document(document: Any, schema: dict[str, Any], label: str) -> None:
    try:
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(document)
    except ValidationError as exc:
        location = ".".join(str(part) for part in exc.absolute_path) or "<root>"
        raise DocumentValidationError(f"{label} failed validation at {location}: {exc.message}") from exc
