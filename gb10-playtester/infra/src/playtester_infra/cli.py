"""Console entry points for reporting, watching, and the egress demo."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from playtester_infra.config import DEFAULT_CONFIG_PATH, ConfigError, load_config
from playtester_infra.llm_client import LLMClientError, create_llm_client
from playtester_infra.openclaw_skill import LevelWatcher
from playtester_infra.openshell_policy import (
    ApplicationEgressPolicy,
    EgressPolicyError,
    NetworkNamespaceEgressPolicy,
    OpenShellEgressPolicy,
    demo_egress_proof,
)
from playtester_infra.report_pipeline import ReportPipelineError, generate_report


def _config_arg(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--config", default=str(DEFAULT_CONFIG_PATH))


def report_main(
    argv: list[str] | None = None, *, schema_title: str | None = None
) -> int:
    parser = argparse.ArgumentParser(prog="playtester-report")
    parser.add_argument("telemetry_path")
    _config_arg(parser)
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config)
        report = generate_report(
            args.telemetry_path,
            create_llm_client(config),
            config_path=config.source_path,
            schema_title=schema_title,
        )
    except (ConfigError, LLMClientError, ReportPipelineError) as exc:
        parser.exit(1, f"ERROR: {exc}\n")
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0


def watch_main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="playtester-watch")
    _config_arg(parser)
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args(argv)
    try:
        watcher = LevelWatcher(args.config)
        if args.once:
            results = watcher.scan_once()
            print(json.dumps([result.__dict__ for result in results], indent=2))
            return 0 if all(result.success for result in results) else 1
        watcher.run()
    except KeyboardInterrupt:
        return 0
    except Exception as exc:
        parser.exit(1, f"ERROR: {exc}\n")
    return 0


def _policy_from_config(config_path: str | Path):
    config = load_config(config_path)
    common = (
        config.sandbox.allowed_read_paths,
        config.sandbox.allowed_write_paths,
    )
    if config.sandbox.backend == "application":
        return ApplicationEgressPolicy(*common, config.sandbox.llm_allowlist)
    if config.sandbox.backend == "network_namespace":
        return NetworkNamespaceEgressPolicy(*common)
    return OpenShellEgressPolicy(*common)


def egress_main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="playtester-egress-proof")
    _config_arg(parser)
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config)
        endpoint = {
            "ollama": config.llm.ollama_base_url,
            "nim": config.llm.nim_base_url,
            "nemoclaw": config.llm.nemoclaw_base_url,
        }[config.llm.backend]
        passed = demo_egress_proof(_policy_from_config(args.config), llm_endpoint=endpoint)
    except (ConfigError, EgressPolicyError) as exc:
        print(f"FAIL: {exc}")
        return 1
    if passed:
        print("PASS: outbound HTTP was blocked and the configured local LLM remained reachable")
        return 0
    print("FAIL: outbound HTTP request was not proven blocked")
    return 1


def main(argv: list[str] | None = None) -> int:
    """Unified command entry point retained for the merged infra platform.

    The dedicated console scripts remain the stable operator-facing interface;
    this dispatcher lets OpenClaw invoke the same code without shell aliases.
    """
    parser = argparse.ArgumentParser(prog="playtester-infra")
    subcommands = parser.add_subparsers(dest="command", required=True)
    subcommands.add_parser("report")
    subcommands.add_parser("watch")
    subcommands.add_parser("egress-proof")
    parsed, remaining = parser.parse_known_args(argv)
    if parsed.command == "report":
        return report_main(remaining, schema_title="Playtester design report")
    if parsed.command == "watch":
        return watch_main(remaining)
    return egress_main(remaining)
