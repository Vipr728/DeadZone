# Project Discovery

## Scope

The Editor scanner inspects the open scene without mutating it. It ranks player roots and relevant components
from tags, Rigidbody2D/collider ownership, camera references, input-related members, movement-related members,
reset/death/completion members, and active-scene presence. Every score contribution is emitted as evidence.

The first backend uses Unity metadata and conservative symbol/member inspection. It is not Roslyn semantic
analysis and does not claim call-graph understanding. Input System assets may be inspected by serialized JSON
without taking a runtime dependency on `Unity.InputSystem`.

## Result

A scan produces candidates, issues, duration, a stable scene/script fingerprint, and an in-memory mechanics
manifest. Ambiguous top candidates remain reviewable. No tags, settings, files, or scene objects are changed.

## Cache

Editor cache entries live under `Library/Ryzi/cache/`. The fingerprint includes the active scene path and
dependency hash plus relevant script and input-asset dependency hashes. A changed dependency invalidates it.
