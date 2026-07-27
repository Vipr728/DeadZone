from __future__ import annotations

import yaml
import pytest

from playtester_infra.config import ConfigError, load_config


def test_config_loads_typed_paths_and_model_override(config_factory):
    config_path, directories = config_factory()
    config = load_config(config_path)
    assert config.paths.reports_dir == directories["reports"].resolve()
    assert config.orchestration.num_envs == 2
    assert config.llm.selected_model == "test-model"

    raw = yaml.safe_load(config_path.read_text(encoding="utf-8"))
    raw["llm"]["use_gb10_model"] = True
    config_path.write_text(yaml.safe_dump(raw), encoding="utf-8")
    assert load_config(config_path).llm.selected_model == "test-gb10-model"


def test_environment_can_select_gb10_model(config_factory, monkeypatch):
    config_path, _ = config_factory()
    monkeypatch.setenv("PLAYTESTER_USE_GB10_MODEL", "true")
    assert load_config(config_path).llm.selected_model == "test-gb10-model"


@pytest.mark.parametrize(
    ("section", "key", "value", "message"),
    [
        ("llm", "backend", "cloud", "llm.backend"),
        ("llm", "timeout_seconds", 0, "timeout_seconds"),
        ("reporting", "failure_rate_too_hard", 2, "failure_rate_too_hard"),
        ("orchestration", "num_envs", 0, "num_envs"),
        ("orchestration", "execution_mode", "fake", "execution_mode"),
        ("sandbox", "backend", "magic", "sandbox.backend"),
    ],
)
def test_invalid_config_fails_explicitly(
    config_factory, section, key, value, message
):
    config_path, _ = config_factory()
    raw = yaml.safe_load(config_path.read_text(encoding="utf-8"))
    raw[section][key] = value
    config_path.write_text(yaml.safe_dump(raw), encoding="utf-8")
    with pytest.raises(ConfigError, match=message):
        load_config(config_path)
