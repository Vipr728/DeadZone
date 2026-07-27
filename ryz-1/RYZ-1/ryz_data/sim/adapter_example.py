"""A deterministic 2-D platformer adapter used for CI and integration development.

It intentionally models only a compact mechanics subset.  Production SimCore adapters
translate their native DTOs into the same observation/snapshot contract.
"""
from __future__ import annotations

import copy
import hashlib
import json
from dataclasses import dataclass
from typing import Any

from .actions import PrimitiveAction
from .interface import SimCoreInterface, TaskConfig, TaskHandle
from .state import Observation, PlayerState, SimSnapshot, StepResult


@dataclass
class _World:
    config: TaskConfig
    x: float = 0.0
    y: float = 1.0
    vx: float = 0.0
    vy: float = 0.0
    frame: int = 0
    jumped: bool = False
    terminal: bool = False
    died: bool = False
    completed: bool = False
    dash_cooldown: int = 0
    inventory: set[str] = None  # type: ignore[assignment]
    entity_state: dict[str, dict[str, Any]] = None  # type: ignore[assignment]

    def __post_init__(self) -> None:
        if self.inventory is None: self.inventory = set()
        if self.entity_state is None: self.entity_state = {}


class ExampleSimCoreAdapter(SimCoreInterface):
    """Small deterministic physics world; snapshots are deep-copyable pure Python state."""

    def __init__(self) -> None:
        self._world: _World | None = None

    def create_task(self, task_config: TaskConfig) -> TaskHandle:
        return TaskHandle(task_config.task_id, task_config)

    def reset_task(self, task: TaskHandle, seed: int | None = None) -> Observation:
        del seed
        config = task.opaque
        assert isinstance(config, TaskConfig)
        start = config.level["start"]
        self._world = _World(config=config, x=float(start[0]), y=float(start[1]))
        return self.get_observation()

    def _require_world(self) -> _World:
        if self._world is None:
            raise RuntimeError("reset_task must be called first")
        return self._world

    def _on_platform(self, x: float, y: float) -> bool:
        world = self._require_world()
        for platform in world.config.level["platforms"]:
            if platform[0] <= x <= platform[1] and abs(y - platform[2]) < 0.18:
                return True
        for entity in self._entities():
            if entity["kind"] == "moving_platform":
                px, py = self._entity_position(entity)
                width = float(entity.get("size", [2.0])[0])
                if px - width / 2 <= x <= px + width / 2 and abs(y - py) < 0.22: return True
        return False

    def _entities(self) -> list[dict[str, Any]]:
        return self._require_world().config.level.get("entities", [])

    def _entity_position(self, entity: dict[str, Any]) -> tuple[float, float]:
        world = self._require_world(); state = world.entity_state.get(entity["id"], {})
        if entity["kind"] != "moving_platform": return tuple(state.get("position", entity.get("position", [0., 0.])))
        path = entity["path"]; period = max(2, int(entity.get("period_frames", 90))); phase = (world.frame % period) / period
        phase = phase * 2 if phase < .5 else 2 - phase * 2
        return (path[0][0] + (path[1][0]-path[0][0]) * phase, path[0][1] + (path[1][1]-path[0][1]) * phase)

    def _update_entities(self, events: list[dict[str, Any]], interact: bool) -> None:
        world = self._require_world()
        for entity in self._entities():
            eid, kind = entity["id"], entity["kind"]; state = world.entity_state.setdefault(eid, {"collected": False, "open": not entity.get("locked", False)})
            ex, ey = self._entity_position(entity); close = abs(world.x-ex) < .75 and abs(world.y-ey) < 1.0
            if kind == "key" and close and not state["collected"]:
                state["collected"] = True; world.inventory.add(str(entity.get("key_id", eid))); events.append({"kind":"key_collected", "entity_id":eid, "frame":world.frame})
            elif kind == "resource_pickup" and close and not state["collected"]:
                state["collected"] = True; events.append({"kind":"resource_collected", "entity_id":eid, "frame":world.frame, "resource":entity.get("resource")})
            elif kind == "checkpoint" and close and not state.get("activated"):
                state["activated"] = True; events.append({"kind":"checkpoint_reached", "entity_id":eid, "frame":world.frame})
            elif kind == "switch" and close and (interact or True):
                state["activated"] = True; target=entity.get("target_id")
                if target: world.entity_state.setdefault(target,{})["open"] = True
                events.append({"kind":"switch_activated", "entity_id":eid, "frame":world.frame})
            elif kind == "door" and close and not state.get("open", False):
                required=entity.get("key_id")
                if required is None or str(required) in world.inventory:
                    state["open"] = True; events.append({"kind":"door_unlocked", "entity_id":eid, "frame":world.frame})

    def step(self, action: PrimitiveAction, frames: int = 1) -> StepResult:
        world = self._require_world()
        if world.terminal:
            return StepResult(self.get_observation(), 0.0, (), True, world.completed, world.died, 0)
        mechanics = world.config.mechanics
        controls = world.config.controls
        events: list[dict[str, Any]] = []
        start_x, start_y = world.x, world.y
        jump_pressed = controls.get("jump") in action.buttons
        dash_pressed = controls.get("dash") in action.buttons
        interact_pressed = controls.get("interact") in action.buttons
        for _ in range(frames):
            grounded = self._on_platform(world.x, world.y)
            accel = mechanics["ground_acceleration"] if grounded else mechanics["air_acceleration"]
            target_vx = action.horizontal_axis * mechanics["max_run_speed"]
            world.vx += max(-accel, min(accel, target_vx - world.vx))
            if abs(action.horizontal_axis) < 0.01 and grounded:
                decel = mechanics["ground_deceleration"]
                world.vx -= max(-decel, min(decel, world.vx))
            if jump_pressed and grounded:
                world.vy = mechanics["jump_impulse"]
                world.jumped = True
                events.append({"kind": "jump", "frame": world.frame})
            if dash_pressed and mechanics.get("dash_enabled", False) and world.dash_cooldown <= 0:
                direction = action.horizontal_axis or 1.0
                world.vx = direction * mechanics["dash_speed"]
                world.dash_cooldown = int(mechanics["dash_cooldown"])
                events.append({"kind": "dash", "frame": world.frame})
            world.vy -= mechanics["gravity"]
            world.vy = max(world.vy, -mechanics["max_fall_speed"])
            next_x, next_y = world.x + world.vx, world.y + world.vy
            # Land only while descending on an upper surface.
            if world.vy <= 0:
                for left, right, top in world.config.level["platforms"]:
                    if left <= next_x <= right and next_y <= top <= world.y:
                        next_y, world.vy, world.jumped = top, 0.0, False
                        events.append({"kind": "land", "frame": world.frame})
                        break
            world.x, world.y = next_x, next_y
            world.frame += 1
            world.dash_cooldown = max(0, world.dash_cooldown - 1)
            self._update_entities(events, interact_pressed)
            goal = world.config.level["goal"]
            doors = [e for e in self._entities() if e["kind"] == "door"]
            door_open = all(world.entity_state.get(e["id"], {}).get("open", not e.get("locked", False)) for e in doors)
            if door_open and abs(world.x - goal[0]) < 0.7 and abs(world.y - goal[1]) < 1.2:
                world.terminal, world.completed = True, True
                events.append({"kind": "complete", "frame": world.frame})
                break
            if world.y < world.config.level.get("kill_y", -8.0) or any(
                left <= world.x <= right and bottom <= world.y <= top
                for left, right, bottom, top in world.config.level.get("spikes", [])
            ):
                world.terminal, world.died = True, True
                events.append({"kind": "death", "frame": world.frame, "reason": "hazard"})
                break
        progress_before = self.progress_at(start_x, start_y)
        reward = self.progress_at(world.x, world.y) - progress_before
        return StepResult(self.get_observation(), reward, tuple(events), world.terminal,
                          world.completed, world.died, frames)

    def progress_at(self, x: float, y: float) -> float:
        world = self._require_world()
        sx, sy = world.config.level["start"]
        gx, gy = world.config.level["goal"]
        denom = max(1e-6, ((gx - sx) ** 2 + (gy - sy) ** 2) ** 0.5)
        return max(-1.0, min(1.5, ((x - sx) * (gx - sx) + (y - sy) * (gy - sy)) / denom**2))

    def clone_state(self) -> SimSnapshot:
        return SimSnapshot(copy.deepcopy(self._require_world()))

    def restore_state(self, snapshot: SimSnapshot) -> None:
        self._world = copy.deepcopy(snapshot.payload)

    def hash_state(self) -> str:
        w = self._require_world()
        payload = (round(w.x, 3), round(w.y, 3), round(w.vx, 3), round(w.vy, 3), w.frame,
                   w.terminal, w.died, w.completed, w.dash_cooldown)
        return hashlib.sha256(repr(payload).encode()).hexdigest()[:24]

    def get_observation(self) -> Observation:
        w = self._require_world()
        grounded = self._on_platform(w.x, w.y)
        return Observation(
            player=PlayerState(w.x, w.y, w.vx, w.vy, grounded=grounded,
                               dash_cooldown=w.dash_cooldown),
            goal=tuple(w.config.level["goal"]), local_geometry={"platforms": w.config.level["platforms"],
            "spikes": w.config.level.get("spikes", [])}, frame=w.frame,
            state_machine="grounded" if grounded else "airborne",
            nearby_entities=tuple({"id": e["id"], "kind": e["kind"], "position": self._entity_position(e),
                "state": w.entity_state.get(e["id"], {})} for e in self._entities()))

    def get_ground_truth_mechanics(self) -> dict[str, Any]:
        return copy.deepcopy(self._require_world().config.mechanics)

    def is_terminal(self) -> bool:
        return self._require_world().terminal

    def close(self) -> None:
        self._world = None
