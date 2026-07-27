from pathlib import Path

from playtester_rl.playback import build_inference_command, default_base_port


def test_inference_command_resumes_exact_checkpoint_run() -> None:
    command = build_inference_command(
        trainer="/venv/bin/mlagents-learn",
        configuration_path=Path("/results/run/configuration.yaml"),
        player=Path("/builds/level_a.x86_64"),
        run_id="level_a_stage2_123",
        results_dir=Path("/results"),
        level_id="level_a",
        checkpoint_path=Path("/checkpoints/level_a_stage2.ckpt"),
        telemetry_dir=Path("/telemetry"),
        episodes=4,
        base_port=5205,
    )
    assert command[0] == "/venv/bin/mlagents-learn"
    assert "--resume" in command
    assert "--inference" in command
    assert "--run-id=level_a_stage2_123" in command
    assert "--results-dir=/results" in command
    assert "--base-port=5205" in command
    assert command[-2:] == ["--telemetry-dir", "/telemetry"]


def test_default_base_ports_do_not_collide_for_known_levels() -> None:
    assert default_base_port("level_a") != default_base_port("level_b")
