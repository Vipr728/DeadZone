"""Telemetry-to-report pipeline with contract validation and atomic output."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from jinja2 import Environment, FileSystemLoader, StrictUndefined

from playtester_infra.config import (
    AppConfig,
    DEFAULT_CONFIG_PATH,
    ReportingConfig,
    load_config,
)
from playtester_infra.io_utils import FileAlreadyExistsError, atomic_create_json
from playtester_infra.llm_client import ILLMClient
from playtester_infra.schemas import (
    DocumentValidationError,
    load_schema,
    validate_document,
)

PROMPTS_DIR = Path(__file__).resolve().parent / "prompts"
PROMPT_TEMPLATE = "report_prompt.v1.md.j2"
_SAFE_ID = re.compile(r"^[A-Za-z0-9_-]+$")


class ReportPipelineError(RuntimeError):
    """A report could not be safely generated or published."""


def _load_json(path: Path, label: str) -> dict[str, Any]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except OSError as exc:
        raise ReportPipelineError(f"Could not read {label} {path}: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise ReportPipelineError(f"{label.capitalize()} is not valid JSON at {path}: {exc}") from exc
    if not isinstance(document, dict):
        raise ReportPipelineError(f"{label.capitalize()} must be a JSON object: {path}")
    return document


def _render_prompt(telemetry: dict[str, Any], reporting: ReportingConfig) -> str:
    environment = Environment(
        loader=FileSystemLoader(PROMPTS_DIR),
        undefined=StrictUndefined,
        autoescape=False,
        keep_trailing_newline=True,
    )
    template = environment.get_template(PROMPT_TEMPLATE)
    prompt_telemetry = json.loads(json.dumps(telemetry))
    for episode in prompt_telemetry["episode_summaries"]:
        precedents = compute_level_local_precedent(episode["piece_results"])
        for piece, taught_earlier in zip(
            episode["piece_results"], precedents, strict=True
        ):
            piece["taught_earlier_in_this_level"] = taught_earlier
    return template.render(
        telemetry=prompt_telemetry,
        reporting=reporting,
        evidence_json=json.dumps(
            _summarize_telemetry(telemetry, reporting), indent=2, sort_keys=True
        ),
        telemetry_json=json.dumps(prompt_telemetry, indent=2, sort_keys=True),
    )


def compute_level_local_precedent(piece_results: list[dict[str, Any]]) -> list[bool]:
    """Whether the same mechanic appeared earlier in this ordered level run."""
    seen_piece_types: set[str] = set()
    result: list[bool] = []
    for piece in piece_results:
        piece_type = str(piece["piece_type"])
        result.append(piece_type in seen_piece_types)
        seen_piece_types.add(piece_type)
    return result


def _summarize_telemetry(
    telemetry: dict[str, Any], reporting: ReportingConfig
) -> dict[str, Any]:
    """Compute factual aggregates so small local models cannot miscount episodes."""
    outcomes = {"success": 0, "death": 0, "timeout": 0}
    pieces: dict[str, dict[str, Any]] = {}
    for episode in telemetry["episode_summaries"]:
        outcomes[episode["outcome"]] += 1
        for piece in episode["piece_results"]:
            summary = pieces.setdefault(
                piece["piece_id"],
                {
                    "piece_id": piece["piece_id"],
                    "max_attempts": 0,
                    "death_positions": [],
                    "out_of_stage1_range_failures": 0,
                },
            )
            summary["max_attempts"] = max(summary["max_attempts"], piece["attempts"])
            if piece["death_position"] is not None:
                summary["death_positions"].append(piece["death_position"])
            if not piece["seen_in_stage1_range"] and episode["outcome"] != "success":
                summary["out_of_stage1_range_failures"] += 1
    candidates = [
        piece
        for piece in pieces.values()
        if len(piece["death_positions"]) >= reporting.death_cluster_min_episodes
        or piece["max_attempts"] >= reporting.high_attempt_threshold
        or piece["out_of_stage1_range_failures"] >= 1
    ]
    episode_count = len(telemetry["episode_summaries"])
    failure_count = outcomes["death"] + outcomes["timeout"]
    return {
        "episode_count": episode_count,
        "outcome_counts": outcomes,
        "failure_rate": failure_count / episode_count if episode_count else 0.0,
        "piece_aggregates": list(pieces.values()),
        "planted_issue_candidate_count": len(candidates),
        "planted_issue_candidate_piece_ids": [
            candidate["piece_id"] for candidate in candidates
        ],
    }


def _semantic_guard(
    report: dict[str, Any],
    telemetry: dict[str, Any],
    reporting: ReportingConfig,
) -> dict[str, Any]:
    """Correct factual contradictions using configured telemetry aggregates."""
    guarded = json.loads(json.dumps(report))
    evidence = _summarize_telemetry(telemetry, reporting)
    candidate_ids = evidence["planted_issue_candidate_piece_ids"]
    planted = guarded["planted_issue_detected"]
    if not candidate_ids:
        planted["detected"] = False
        planted["description"] = None
        return guarded

    planted["detected"] = True
    aggregates = {
        piece["piece_id"]: piece for piece in evidence["piece_aggregates"]
    }
    planted["description"] = (
        "Configured telemetry thresholds identify planted-issue candidates: "
        + ", ".join(
            f"{piece_id} ({len(aggregates[piece_id]['death_positions'])} deaths, "
            f"{aggregates[piece_id]['out_of_stage1_range_failures']} "
            "out-of-range failures)"
            for piece_id in candidate_ids
        )
        + "."
    )
    existing_points = {
        point["location"]["piece_id"]: point for point in guarded["problem_points"]
    }
    for piece_id in candidate_ids:
        aggregate = aggregates[piece_id]
        if aggregate["death_positions"]:
            location = aggregate["death_positions"][0]
        else:
            location = next(
                (
                    episode["path_trace"][-1]
                    for episode in telemetry["episode_summaries"]
                    if episode["path_trace"]
                    and any(
                        piece["piece_id"] == piece_id
                        for piece in episode["piece_results"]
                    )
                ),
                None,
            )
            if location is None:
                raise ReportPipelineError(
                    f"Candidate piece {piece_id!r} has no recorded death or path "
                    "coordinate; refusing to invent a report location"
                )
        death_count = len(aggregate["death_positions"])
        canonical_point = {
            "location": {
                "piece_id": piece_id,
                "x": location["x"],
                "y": location["y"],
            },
            "issue": "Telemetry crosses a configured planted-issue threshold.",
            "severity": (
                "high"
                if death_count >= reporting.death_cluster_min_episodes
                or aggregate["max_attempts"] >= reporting.high_attempt_threshold
                else "medium"
            ),
            "evidence": (
                f"{death_count} recorded deaths; maximum attempts "
                f"{aggregate['max_attempts']}; out-of-range failures "
                f"{aggregate['out_of_stage1_range_failures']}."
            ),
        }
        if piece_id in existing_points:
            existing_points[piece_id].clear()
            existing_points[piece_id].update(canonical_point)
        else:
            guarded["problem_points"].append(canonical_point)
    if evidence["failure_rate"] >= reporting.failure_rate_too_hard:
        guarded["overall_difficulty"] = "too_hard"
        guarded["difficulty_rationale"] = (
            f"Failure rate {evidence['failure_rate']:.0%} meets the configured "
            f"too-hard threshold {reporting.failure_rate_too_hard:.0%}, with "
            f"candidate piece(s): {', '.join(candidate_ids)}."
        )
    return guarded


def report_output_path(
    telemetry: dict[str, Any], config_path: str | Path = DEFAULT_CONFIG_PATH
) -> Path:
    config = load_config(config_path)
    level_id = telemetry["level_id"]
    run_id = telemetry["run_id"]
    if not _SAFE_ID.fullmatch(level_id):
        raise ReportPipelineError(f"Unsafe level_id cannot be used in report filename: {level_id!r}")
    if not _SAFE_ID.fullmatch(run_id):
        raise ReportPipelineError(f"Unsafe run_id cannot be used in report filename: {run_id!r}")
    return config.paths.reports_dir / f"{level_id}_{run_id}.json"


def generate_report(
    telemetry_path: str,
    llm_client: ILLMClient,
    *,
    config_path: str | Path = DEFAULT_CONFIG_PATH,
    config: AppConfig | None = None,
    schema_title: str | None = None,
) -> dict[str, Any]:
    """Validate telemetry, generate a schema-valid report, and publish it once."""
    active_config = config or load_config(config_path)
    telemetry_file = Path(telemetry_path).expanduser().resolve(strict=False)
    telemetry = _load_json(telemetry_file, "telemetry")
    telemetry_schema = load_schema("telemetry.schema.json")
    report_schema = load_schema("report.schema.json")
    if schema_title is not None:
        report_schema = {**report_schema, "title": schema_title}
    try:
        validate_document(telemetry, telemetry_schema, "telemetry")
    except DocumentValidationError as exc:
        raise ReportPipelineError(str(exc)) from exc

    prompt = _render_prompt(telemetry, active_config.reporting)
    report = llm_client.generate_structured(prompt, report_schema)
    try:
        validate_document(report, report_schema, "report")
    except DocumentValidationError as exc:
        raise ReportPipelineError(str(exc)) from exc
    if report["level_id"] != telemetry["level_id"]:
        raise ReportPipelineError(
            f"Report level_id {report['level_id']!r} does not match telemetry "
            f"level_id {telemetry['level_id']!r}"
        )
    report = _semantic_guard(report, telemetry, active_config.reporting)
    try:
        validate_document(report, report_schema, "semantically guarded report")
    except DocumentValidationError as exc:
        raise ReportPipelineError(str(exc)) from exc

    level_id = telemetry["level_id"]
    run_id = telemetry["run_id"]
    if not _SAFE_ID.fullmatch(level_id):
        raise ReportPipelineError(
            f"Unsafe level_id cannot be used in report filename: {level_id!r}"
        )
    if not _SAFE_ID.fullmatch(run_id):
        raise ReportPipelineError(
            f"Unsafe run_id cannot be used in report filename: {run_id!r}"
        )
    output_path = active_config.paths.reports_dir / f"{level_id}_{run_id}.json"
    try:
        atomic_create_json(output_path, report)
    except FileAlreadyExistsError as exc:
        raise ReportPipelineError(str(exc)) from exc
    return report
