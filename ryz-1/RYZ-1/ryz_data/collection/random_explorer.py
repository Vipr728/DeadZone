from __future__ import annotations
import random
from ..solver.beam_solver import SolverBudget
from ..solver.macros import macro_vocabulary
from ..sim.interface import SimCoreInterface
from .branch_recorder import records_from_search

def random_trajectory(sim: SimCoreInterface, controls: dict[str, int], rng: random.Random, max_steps: int = 40):
    """Persistent macro random policy. The TrialManager converts it to normalized records."""
    macros = macro_vocabulary(controls); steps=[]; current=None
    for _ in range(max_steps):
        if current is None or rng.random() < .3: current = rng.choice(macros)
        before=sim.get_observation(); result=sim.step(current.action, current.duration_frames); steps.append((before,current,result))
        if result.terminal: break
    return steps
