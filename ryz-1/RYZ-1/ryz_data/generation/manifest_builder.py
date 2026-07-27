from __future__ import annotations
from dataclasses import asdict, dataclass, field
from typing import Any

@dataclass(frozen=True)
class ResourceDefinition:
    name: str; maximum: float; regeneration: float = 0.0
@dataclass(frozen=True)
class CollisionProperties:
    width: float; height: float; one_way_supported: bool = True
@dataclass(frozen=True)
class EntityDefinition:
    kind: str; properties: dict[str, Any] = field(default_factory=dict)
@dataclass(frozen=True)
class MechanicsManifest:
    manifest_version: str; action_mapping: dict[str, int]; available_actions: list[str]
    continuous_mechanics: dict[str, float]; discrete_mechanics: dict[str, int | bool | str]
    resources: list[ResourceDefinition]; collision_properties: CollisionProperties
    state_machine_summary: dict[str, Any]; known_entities: list[EntityDefinition]
    confidence: dict[str, float]; visibility_mask: dict[str, bool]
    def to_dict(self) -> dict[str, Any]: return asdict(self)

def build_ground_truth_manifest(mechanics: dict[str, Any], controls: dict[str, int], level: dict[str, Any] | None = None) -> MechanicsManifest:
    continuous = {k: float(v) for k, v in mechanics.items() if isinstance(v, float)}
    discrete = {k: v for k, v in mechanics.items() if isinstance(v, (bool, int, str))}
    keys = list(mechanics)
    return MechanicsManifest("1.0.0", controls, list(controls), continuous, discrete,
        [ResourceDefinition("stamina", float(mechanics.get("climb_stamina", 0)),
                            float(mechanics.get("resource_regeneration", 0)))],
        CollisionProperties(float(mechanics["collision_width"]), float(mechanics["collision_height"])),
        {"states": ["grounded", "airborne", "dashing", "dead", "complete"]},
        [EntityDefinition("platform"), EntityDefinition("spike"), EntityDefinition("goal")] +
        [EntityDefinition(str(entity["kind"]), {k: v for k, v in entity.items() if k not in {"id", "kind"}})
         for entity in (level or {}).get("entities", [])],
        {key: 1.0 for key in keys}, {key: True for key in keys})
