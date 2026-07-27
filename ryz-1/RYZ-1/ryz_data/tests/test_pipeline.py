from __future__ import annotations
import random
from pathlib import Path
import pytest
from ryz_data.calibration.probe_runner import ProbeRunner
from ryz_data.collection.trial_manager import TrialManager
from ryz_data.dataset.pytorch_dataset import RYZTransitionDataset, collate_transitions
from ryz_data.dataset.validation import validate_dataset
from ryz_data.dataset.writer import DatasetWriter
from ryz_data.generation.control_randomizer import randomize_controls
from ryz_data.generation.mechanics_generator import sample_mechanics, validate_mechanics
from ryz_data.generation.task_generator import generate_task
from ryz_data.generation.scenario_catalog import SCENARIO_CATALOG
from ryz_data.sim.adapter_example import ExampleSimCoreAdapter
from ryz_data.sim.actions import PrimitiveAction
from ryz_data.solver.beam_solver import BeamSolver, SolverBudget

def test_deterministic_task_generation():
    assert generate_task(42) == generate_task(42)

def test_full_curriculum_catalog_is_unique_and_attached_to_tasks():
    assert len(SCENARIO_CATALOG) == 89
    assert len({scenario.scenario_id for scenario in SCENARIO_CATALOG}) == len(SCENARIO_CATALOG)
    task=generate_task(0)
    assert task.config.level["route_metadata"]["scenario_id"] == SCENARIO_CATALOG[0].scenario_id
    assert task.config.level["route_metadata"]["scenario_family"] == SCENARIO_CATALOG[0].family

def test_mechanics_and_controls_are_valid():
    values=sample_mechanics(random.Random(1)).values; validate_mechanics(values)
    controls=randomize_controls(random.Random(2),values); assert len(set(controls.values()))==len(controls)

def test_clone_restore_hash_and_solver_determinism():
    task=generate_task(7); sim=ExampleSimCoreAdapter(); handle=sim.create_task(task.config); sim.reset_task(handle); snapshot=sim.clone_state(); first=sim.hash_state(); sim.step(PrimitiveAction(),2);sim.restore_state(snapshot); assert sim.hash_state()==first
    sim.reset_task(handle); one=BeamSolver(SolverBudget(8,20)).solve(sim,task.config.controls);sim.reset_task(handle);two=BeamSolver(SolverBudget(8,20)).solve(sim,task.config.controls)
    assert [(n.state_hash,n.prune_reason) for n in one.nodes]==[(n.state_hash,n.prune_reason) for n in two.nodes]

def test_calibration_manifest():
    task=generate_task(5);sim=ExampleSimCoreAdapter();h=sim.create_task(task.config);sim.reset_task(h); result=ProbeRunner().run(sim,h,task.config.controls)
    assert result.probes and result.inferred_manifest["manifest_version"]=="1.0.0"

def test_end_to_end_five_tasks(tmp_path: Path):
    pytest.importorskip("pyarrow"); pytest.importorskip("torch")
    writer=DatasetWriter(tmp_path,shard_size=30)
    for seed in range(5):
        task=generate_task(seed);sim=ExampleSimCoreAdapter()
        output=TrialManager(sim,SolverBudget(8,20),SolverBudget(3,8),random.Random(seed)).collect(task,2)
        writer.add("tasks",output.task)
        for row in output.trials:writer.add("trials",row)
        for row in output.trajectories:writer.add("trajectories",row)
        for row in output.calibrations:writer.add("calibrations",row)
        for row in output.transitions:writer.add("transitions",row)
    writer.close(); report=validate_dataset(tmp_path);assert report.valid,report.errors
    dataset=RYZTransitionDataset(tmp_path);assert len(dataset)>0;batch=collate_transitions([dataset[0],dataset[1]]);assert batch["observation"].shape[0]==2
