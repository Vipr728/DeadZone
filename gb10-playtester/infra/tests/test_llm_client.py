from __future__ import annotations

import json

import httpx
import pytest

from conftest import make_report
from playtester_infra.llm_client import (
    LLMClientError,
    NemoClawClient,
    NimClient,
    OllamaClient,
)
from playtester_infra.schemas import load_schema


def test_ollama_uses_native_schema_and_validates_response():
    expected = make_report("level_a")

    def handler(request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        assert request.url.path == "/api/generate"
        assert body["format"]["title"] == "PlaytestReport"
        assert body["stream"] is False
        return httpx.Response(200, json={"response": json.dumps(expected)})

    client = OllamaClient(
        "test-model",
        transport=httpx.MockTransport(handler),
    )
    assert client.generate_structured("prompt", load_schema("report.schema.json")) == expected


@pytest.mark.parametrize("client_type", [NimClient, NemoClawClient])
def test_openai_compatible_adapters_use_json_schema(client_type):
    expected = make_report("level_a")

    def handler(request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        assert request.url.path == "/v1/chat/completions"
        assert body["response_format"]["type"] == "json_schema"
        assert body["response_format"]["json_schema"]["strict"] is True
        return httpx.Response(
            200,
            json={"choices": [{"message": {"content": json.dumps(expected)}}]},
        )

    client = client_type(
        "test-model",
        "http://127.0.0.1:8000",
        transport=httpx.MockTransport(handler),
    )
    assert client.generate_structured("prompt", load_schema("report.schema.json")) == expected


@pytest.mark.parametrize(
    "response_value",
    ["not-json", json.dumps({"level_id": "level_a"}), "[]"],
)
def test_malformed_model_output_fails_explicitly(response_value):
    transport = httpx.MockTransport(
        lambda request: httpx.Response(200, json={"response": response_value})
    )
    client = OllamaClient("test-model", transport=transport)
    with pytest.raises(LLMClientError):
        client.generate_structured("prompt", load_schema("report.schema.json"))
