"""Structured local-model clients behind the locked ILLMClient interface."""

from __future__ import annotations

import json
from typing import Any, Protocol, runtime_checkable

import httpx

from playtester_infra.config import AppConfig
from playtester_infra.schemas import DocumentValidationError, validate_document


class LLMClientError(RuntimeError):
    """The configured inference backend failed or returned unusable output."""


@runtime_checkable
class ILLMClient(Protocol):
    def generate_structured(self, prompt: str, schema: dict[str, Any]) -> dict[str, Any]:
        """Return a dictionary conforming to the supplied JSON Schema."""


def _decode_json(value: Any, backend: str) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if not isinstance(value, str):
        raise LLMClientError(f"{backend} returned structured content of type {type(value).__name__}")
    try:
        decoded = json.loads(value)
    except json.JSONDecodeError as exc:
        raise LLMClientError(f"{backend} returned malformed JSON: {exc}") from exc
    if not isinstance(decoded, dict):
        raise LLMClientError(f"{backend} returned JSON {type(decoded).__name__}; expected object")
    return decoded


def _validate_response(document: dict[str, Any], schema: dict[str, Any], backend: str) -> dict[str, Any]:
    try:
        validate_document(document, schema, f"{backend} response")
    except DocumentValidationError as exc:
        raise LLMClientError(str(exc)) from exc
    return document


class OllamaClient:
    """Ollama `/api/generate` client using native schema-constrained output."""

    def __init__(
        self,
        model: str,
        base_url: str = "http://127.0.0.1:11434",
        timeout_seconds: float = 120,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        self.model = model
        self.base_url = base_url.rstrip("/")
        self._client = httpx.Client(
            timeout=timeout_seconds,
            transport=transport,
            trust_env=False,
        )

    def generate_structured(self, prompt: str, schema: dict[str, Any]) -> dict[str, Any]:
        payload = {
            "model": self.model,
            "prompt": prompt,
            "stream": False,
            "format": schema,
            "options": {"temperature": 0},
        }
        try:
            response = self._client.post(f"{self.base_url}/api/generate", json=payload)
            response.raise_for_status()
            body = response.json()
        except (httpx.HTTPError, ValueError) as exc:
            raise LLMClientError(f"Ollama request failed: {exc}") from exc
        if not isinstance(body, dict) or "response" not in body:
            raise LLMClientError("Ollama response is missing the `response` field")
        return _validate_response(_decode_json(body["response"], "Ollama"), schema, "Ollama")


class _OpenAICompatibleClient:
    """Shared local OpenAI-compatible adapter for NIM and NemoClaw routing."""

    backend_name = "OpenAI-compatible backend"

    def __init__(
        self,
        model: str,
        base_url: str,
        timeout_seconds: float = 120,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        self.model = model
        self.base_url = base_url.rstrip("/")
        self._client = httpx.Client(
            timeout=timeout_seconds,
            transport=transport,
            trust_env=False,
        )

    def generate_structured(self, prompt: str, schema: dict[str, Any]) -> dict[str, Any]:
        payload = {
            "model": self.model,
            "messages": [{"role": "user", "content": prompt}],
            "temperature": 0,
            "response_format": {
                "type": "json_schema",
                "json_schema": {
                    "name": "playtest_report",
                    "strict": True,
                    "schema": schema,
                },
            },
        }
        try:
            response = self._client.post(f"{self.base_url}/v1/chat/completions", json=payload)
            response.raise_for_status()
            body = response.json()
            content = body["choices"][0]["message"]["content"]
        except (httpx.HTTPError, ValueError, KeyError, IndexError, TypeError) as exc:
            raise LLMClientError(f"{self.backend_name} request failed: {exc}") from exc
        return _validate_response(
            _decode_json(content, self.backend_name), schema, self.backend_name
        )


class NimClient(_OpenAICompatibleClient):
    """Adapter for a local NVIDIA NIM OpenAI-compatible endpoint."""

    backend_name = "NIM"


class NemoClawClient(_OpenAICompatibleClient):
    """Adapter for a verified NemoClaw local OpenAI-compatible route."""

    backend_name = "NemoClaw"


def create_llm_client(config: AppConfig) -> ILLMClient:
    llm = config.llm
    common = {"model": llm.selected_model, "timeout_seconds": llm.timeout_seconds}
    if llm.backend == "ollama":
        return OllamaClient(base_url=llm.ollama_base_url, **common)
    if llm.backend == "nim":
        return NimClient(base_url=llm.nim_base_url, **common)
    if llm.backend == "nemoclaw":
        return NemoClawClient(base_url=llm.nemoclaw_base_url, **common)
    raise LLMClientError(f"Unsupported LLM backend: {llm.backend}")
