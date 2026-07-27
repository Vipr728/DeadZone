from __future__ import annotations

from enum import Enum
from typing import Any, Literal

from pydantic import BaseModel, Field, model_validator


class EvidenceTier(str, Enum):
    UNITY_VERIFIED = "unity_verified"
    HEADLESS = "headless"
    SYNTHETIC = "synthetic"
    LLM_INFERRED = "llm_inferred"


class RunKind(str, Enum):
    ANALYZE = "analyze"
    GENERATE = "generate"
    COMPARE = "compare"
    TRAIN = "train"
    VALIDATE = "validate"


class RunRequest(BaseModel):
    kind: RunKind = RunKind.ANALYZE
    title: str = Field(default="Untitled analysis", min_length=1, max_length=160)
    domain: Literal["platformer", "symbolic_puzzle", "ark_topdown"] = "platformer"
    engine: Literal["auto", "mock", "gb10_proxy", "ryz_simcore", "unity_remote"] = "auto"
    model_ids: list[str] = Field(default_factory=list, max_length=8)
    seed: int = Field(default=42, ge=0, le=2_147_483_647)
    episodes: int = Field(default=24, ge=1, le=500)
    budget: int = Field(default=5000, ge=1, le=1_000_000)
    source: dict[str, Any] = Field(default_factory=dict)
    use_qwen: bool = True

    @model_validator(mode="after")
    def validate_domain_engine(self) -> "RunRequest":
        if self.domain == "ark_topdown" and self.engine == "gb10_proxy":
            raise ValueError("GB10 platform checkpoints are incompatible with ARK top-down gameplay")
        return self


class RunEvent(BaseModel):
    sequence: int
    type: str
    summary: str
    payload: dict[str, Any] = Field(default_factory=dict)
    timestamp: str


class RunRecord(BaseModel):
    ok: bool = True
    run_id: str
    status: Literal["queued", "running", "complete", "failed", "canceled"]
    title: str
    created_at: str
    updated_at: str
    request: dict[str, Any]
    report: dict[str, Any] | None = None
    error: str = ""
    events: list[RunEvent] = Field(default_factory=list)


class ChatMessage(BaseModel):
    role: Literal["user", "assistant"]
    content: str = Field(min_length=1, max_length=4000)


class ChatRequest(BaseModel):
    question: str = Field(min_length=1, max_length=4000)
    run_id: str | None = None
    model_ids: list[str] = Field(default_factory=list, max_length=8)
    history: list[ChatMessage] = Field(default_factory=list, max_length=12)
