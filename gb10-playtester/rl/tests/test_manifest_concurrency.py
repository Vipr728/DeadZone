"""Concurrency stress test for checkpoint_manifest.upsert_entry_field.

This exists because rl/scripts/run_concurrent_demo.sh (PRD.md §7's
concurrent-parallelism GB10 demo) deliberately runs two training processes
in parallel against the SAME --output-manifest path. A real run of that
script against an unlocked upsert_entry_field corrupted the manifest file
with a JSONDecodeError — this test reproduces that race with real OS
threads/processes hammering the same file and asserts every write survives.
"""

from __future__ import annotations

import concurrent.futures

from playtester_rl.checkpoint_manifest import get_entry, upsert_entry_field


def test_concurrent_upserts_to_different_levels_all_survive(tmp_path):
    """N threads, each upserting a different level_id into the same manifest
    file at the same time — every level's write must be present at the end,
    none silently lost to a lost-update race."""
    manifest_path = tmp_path / "manifest.json"
    num_levels = 12

    def _write(i: int) -> None:
        upsert_entry_field(manifest_path, f"level_{i}", "stage1_checkpoint", f"ckpt/level_{i}")

    with concurrent.futures.ThreadPoolExecutor(max_workers=num_levels) as executor:
        list(executor.map(_write, range(num_levels)))

    for i in range(num_levels):
        entry = get_entry(manifest_path, f"level_{i}")
        assert entry is not None, f"level_{i}'s write was lost to a concurrency race"
        assert entry["stage1_checkpoint"] == f"ckpt/level_{i}"


def test_concurrent_upserts_to_the_same_level_do_not_corrupt_the_file(tmp_path):
    """Many threads racing to update the SAME level_id's manifest entry —
    the file must remain valid JSON/schema-conformant throughout, and the
    last writer's value must be exactly one of the values attempted (never
    a torn/partial write)."""
    manifest_path = tmp_path / "manifest.json"
    attempts = 30

    def _write(i: int) -> None:
        upsert_entry_field(
            manifest_path,
            "level_a",
            "stage1_metrics",
            {"final_mean_reward": float(i), "training_steps": i * 100},
        )

    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        list(executor.map(_write, range(attempts)))

    entry = get_entry(manifest_path, "level_a")
    assert entry is not None
    assert entry["stage1_metrics"]["training_steps"] in {i * 100 for i in range(attempts)}


def test_concurrent_writes_to_different_fields_of_the_same_level_do_not_clobber(tmp_path):
    """Simulates the real run_concurrent_demo.sh shape more closely: two
    'processes' each writing a DIFFERENT field of the SAME level's entry at
    the same time (e.g. stage2_checkpoint vs stage2_metrics) — both fields
    must be present afterward, neither silently overwritten by the other's
    stale read."""
    manifest_path = tmp_path / "manifest.json"

    def _write_checkpoint() -> None:
        for _ in range(20):
            upsert_entry_field(manifest_path, "level_a", "stage2_checkpoint", "ckpt/level_a_stage2")

    def _write_metrics() -> None:
        for _ in range(20):
            upsert_entry_field(
                manifest_path, "level_a", "stage2_metrics", {"final_mean_reward": 5.0, "training_steps": 1000, "steps_to_converge": 500}
            )

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        f1 = executor.submit(_write_checkpoint)
        f2 = executor.submit(_write_metrics)
        f1.result()
        f2.result()

    entry = get_entry(manifest_path, "level_a")
    assert entry["stage2_checkpoint"] == "ckpt/level_a_stage2"
    assert entry["stage2_metrics"]["training_steps"] == 1000
