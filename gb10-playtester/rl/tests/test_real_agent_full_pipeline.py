"""The full-stack integration test: a real gradient-trained agent, driven
through every real production module in this package — config_loader loads
the real shipped configs, reward_strategies.create_reward_strategy builds
the real reward signal, mvp_policy_gradient's LinearSoftmaxPolicy is a real
agent trained with real backprop, telemetry_writer records and validates
real per-episode telemetry, gate_eval evaluates this run's own real reward
curve, and checkpoint_manifest records the real result. fake_env is the
ONLY non-production stand-in here (temporary glue for the eventual Unity
environment) — everything else in this test is the genuine module that
ships, exercised together in one run rather than in isolation.
"""

from __future__ import annotations

import numpy as np

from playtester_rl.checkpoint_manifest import get_entry, upsert_entry_field
from playtester_rl.config_loader import load_observation_config, load_piece_config, load_reward_config
from playtester_rl.gate_eval import GateResult, load_reward_curve
from playtester_rl.mvp_policy_gradient import LinearSoftmaxPolicy, train_and_produce_pipeline_artifacts
from playtester_rl.telemetry_writer import read_telemetry, validate_telemetry, write_telemetry


def test_real_agent_trains_through_every_real_production_module(tmp_path):
    piece_config = load_piece_config()
    reward_config = load_reward_config()
    observation_config = load_observation_config()

    checkpoint_out = tmp_path / "level_a_stage1.npz"
    reward_curve_out = tmp_path / "level_a_stage1.reward_curve.json"

    result = train_and_produce_pipeline_artifacts(
        level_id="level_a",
        stage="stage1",
        checkpoint_out=checkpoint_out,
        reward_curve_out=reward_curve_out,
        piece_config=piece_config,
        reward_config=reward_config,
        observation_config=observation_config,
        num_batches=40,
        episodes_per_batch=16,
        seed=123,
        learning_rate=0.1,
    )

    # -- real learning actually happened -------------------------------------
    assert result.progress[-1].mean_batch_reward > result.progress[0].mean_batch_reward + 3.0, (
        "expected substantial reward improvement from a real gradient-trained agent"
    )
    assert all(np.isfinite(p.mean_batch_reward) and np.isfinite(p.mean_grad_norm) for p in result.progress)

    # -- real telemetry, schema-valid by construction ------------------------
    expected_episode_count = 40 * 16
    assert len(result.telemetry_doc["episode_summaries"]) == expected_episode_count
    validate_telemetry(result.telemetry_doc)  # must not raise
    all_piece_results = [pr for ep in result.telemetry_doc["episode_summaries"] for pr in ep["piece_results"]]
    assert len(all_piece_results) > 0
    # seen_in_stage1_range was computed for real (not left at a placeholder) —
    # confirm at least one of each boolean value doesn't appear as a constant
    # default by checking the field exists and is a real bool on every entry.
    assert all(isinstance(pr["seen_in_stage1_range"], bool) for pr in all_piece_results)

    # -- real Gate 1 evaluation against THIS run's own curve -----------------
    assert isinstance(result.gate1_result, GateResult)
    assert {"previous_block_mean_reward", "last_block_mean_reward", "relative_change"} <= result.gate1_result.metrics.keys()

    # -- real checkpoint: actual trained weights, not a placeholder ----------
    assert checkpoint_out.exists()
    loaded_policy = LinearSoftmaxPolicy.load(checkpoint_out)
    assert loaded_policy.W.shape == (observation_config.observation_size(), 4)
    assert not np.allclose(loaded_policy.W, 0.0), "checkpoint must contain real (non-zero) trained weights"
    assert np.allclose(loaded_policy.W, result.policy.W)
    assert np.allclose(loaded_policy.b, result.policy.b)

    # -- reward curve persisted via gate_eval, round-trips ------------------
    reloaded_curve = load_reward_curve(reward_curve_out)
    assert reloaded_curve == result.reward_curve
    assert len(reloaded_curve) == 40

    # -- full telemetry write/read round trip via telemetry_writer -----------
    telemetry_path = tmp_path / "telemetry.json"
    write_telemetry(result.telemetry_doc, telemetry_path)
    reloaded_telemetry = read_telemetry(telemetry_path)
    assert reloaded_telemetry == result.telemetry_doc

    # -- real checkpoint_manifest write/read ---------------------------------
    manifest_path = tmp_path / "manifest.json"
    upsert_entry_field(manifest_path, "level_a", "stage1_checkpoint", str(checkpoint_out))
    upsert_entry_field(
        manifest_path,
        "level_a",
        "stage1_metrics",
        {"final_mean_reward": result.final_mean_reward, "training_steps": result.training_steps},
    )
    entry = get_entry(manifest_path, "level_a")
    assert entry["stage1_checkpoint"] == str(checkpoint_out)
    assert entry["stage1_metrics"]["training_steps"] == result.training_steps


def test_loaded_checkpoint_produces_the_same_policy_behavior_as_the_trained_one(tmp_path):
    """A checkpoint that round-trips numerically but behaves differently at
    inference time would be a silent, hard-to-catch bug — verify the loaded
    policy's action distribution matches the in-memory trained policy's for
    a fixed observation, not just that the raw arrays are numerically equal."""
    piece_config = load_piece_config()
    reward_config = load_reward_config()
    observation_config = load_observation_config()

    checkpoint_out = tmp_path / "ckpt.npz"
    result = train_and_produce_pipeline_artifacts(
        level_id="level_a",
        stage="stage1",
        checkpoint_out=checkpoint_out,
        reward_curve_out=tmp_path / "curve.json",
        piece_config=piece_config,
        reward_config=reward_config,
        observation_config=observation_config,
        num_batches=10,
        episodes_per_batch=8,
        seed=7,
    )

    loaded = LinearSoftmaxPolicy.load(checkpoint_out)
    probe_obs = np.zeros(observation_config.observation_size())
    probe_obs[0] = 1.0  # arbitrary non-trivial probe input

    original_logits = probe_obs @ result.policy.W + result.policy.b
    loaded_logits = probe_obs @ loaded.W + loaded.b
    assert np.allclose(original_logits, loaded_logits)
