from __future__ import annotations
import random
from dataclasses import dataclass
from .control_randomizer import randomize_controls
from .level_generator import generate_level, validate_level
from .mechanics_generator import sample_mechanics
from .scenario_catalog import SCENARIO_CATALOG
from ..sim.interface import TaskConfig

@dataclass(frozen=True)
class GeneratedTask:
    config: TaskConfig
    ground_truth_manifest: dict

def generate_task(seed: int) -> GeneratedTask:
    rng = random.Random(seed); mechanics = sample_mechanics(rng).values
    controls = randomize_controls(rng, mechanics); scenario = SCENARIO_CATALOG[seed % len(SCENARIO_CATALOG)]
    level = generate_level(rng, mechanics, scenario.scenario_id); validate_level(level)
    level["route_metadata"]["scenario_family"] = scenario.family
    level["route_metadata"]["required_entities"] = list(scenario.required_entities)
    level["route_metadata"]["required_events"] = list(scenario.required_events)
    from .manifest_builder import build_ground_truth_manifest
    task_id = f"task-{seed:016x}"
    return GeneratedTask(TaskConfig(task_id, seed, level, mechanics, controls),
                         build_ground_truth_manifest(mechanics, controls, level).to_dict())
