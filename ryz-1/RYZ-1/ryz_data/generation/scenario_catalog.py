"""Declarative coverage catalog for the RYZ-1 platformer curriculum.

Each ID is a task-family contract.  Native SimCore adapters may add richer entity
properties, while data generation keeps the family ID and required observations stable.
"""
from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ScenarioSpec:
    scenario_id: str
    family: str
    required_entities: tuple[str, ...] = ()
    required_mechanics: tuple[str, ...] = ()
    required_events: tuple[str, ...] = ()
    temporal: bool = False


def _specs(family: str, names: tuple[str, ...], *, entities: tuple[str, ...] = (),
           mechanics: tuple[str, ...] = (), events: tuple[str, ...] = (), temporal: bool = False) -> list[ScenarioSpec]:
    return [ScenarioSpec(name, family, entities, mechanics, events, temporal) for name in names]


SCENARIO_CATALOG: tuple[ScenarioSpec, ...] = tuple(
    _specs("movement_timing", ("timed_moving_platform", "platform_chain", "falling_platform", "retracting_bridge",
      "crusher_timing", "laser_window", "short_long_jump", "air_control_recovery", "one_way_drop", "wall_jump_chain",
      "checkpoint_race", "conveyor_momentum", "slope_ice_wind", "precision_dash_gate"),
      entities=("moving_platform", "hazard"), events=("platform_boarded", "hazard_avoided"), temporal=True)
    + _specs("keys_doors_inventory", ("single_key_door", "colored_keys", "ordered_keys", "carry_key_route",
      "optional_key_shortcut", "consumable_key", "resource_gate", "timed_switch_door", "pressure_plate", "hidden_required_key"),
      entities=("key", "door", "switch"), events=("key_collected", "door_unlocked", "switch_activated"), temporal=True)
    + _specs("navigation", ("safe_vs_risky_route", "alternate_route", "backtrack_after_pickup", "checkpoint_strategy",
      "optional_objectives", "resource_route_choice", "subgoal_dependency", "vertical_shaft", "hub_loop", "one_way_dead_end"),
      entities=("checkpoint", "collectible"), events=("checkpoint_reached", "subgoal_completed"))
    + _specs("hazards_enemies", ("patrol_enemy", "projectile_volley", "rotating_laser", "crushers_pendulums",
      "enemy_knockback", "hazard_switch_phase", "pursuit_escape", "rhythm_hazard", "destructible_blocker", "bounded_damage"),
      entities=("enemy", "hazard"), events=("enemy_contact", "hazard_avoided"), temporal=True)
    + _specs("resources_abilities", ("stamina_climb", "ability_route_choice", "limited_air_jumps", "cooldown_wait",
      "dash_charge_gate", "health_ammo_route", "temporary_powerup", "resource_pickup_inference", "contextual_grapple"),
      entities=("resource_pickup",), mechanics=("dash_enabled",), events=("resource_collected", "cooldown_ready"), temporal=True)
    + _specs("physics_interaction", ("push_crate", "ride_physics_object", "breakable_floor", "move_block_switch",
      "spring_bounce", "portal_gravity", "rope_ladder_zipline", "hold_to_interact_door", "collision_transform"),
      entities=("interactable",), events=("object_moved", "interaction_completed"))
    + _specs("discovery", ("unknown_controls", "unknown_wall_ability", "unknown_platform_type", "unknown_cooldown",
      "unknown_key_mapping", "switch_door_mapping", "fog_of_war", "noisy_manifest"),
      events=("mechanic_discovered",))
    + _specs("multi_stage", ("collect_all_exit", "unordered_generators", "ordered_generators", "escort_npc",
      "carry_object", "time_trial", "survive_then_extract", "return_artifact", "boss_phases", "multiple_endings"),
      entities=("objective",), events=("objective_activated", "objective_completed"), temporal=True)
    + _specs("robustness", ("recoverable_mistake", "missed_platform_recovery", "wrong_key_recovery", "lower_route_recovery",
      "wasted_dash_recovery", "input_delay", "environment_phase_change", "unknown_vs_impossible", "solver_correction"),
      events=("recovery_started", "recovery_completed"))
)

CATALOG_BY_ID = {spec.scenario_id: spec for spec in SCENARIO_CATALOG}
