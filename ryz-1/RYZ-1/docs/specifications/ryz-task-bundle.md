# RYZ Task Bundle

Schema: `ryz-task-bundle/1.0`.

Implemented in `src/Ryz1.Contracts`.

Top-level fields:

- `schemaVersion`
- `manifest`
- `task`

The task contains:

- `taskId`
- `manifestFingerprint`
- `levelFingerprint`
- `randomizationSeed`
- `trialCount`
- `maxTicks`
- `fixedDeltaTime`
- `actionSchema`
- `observationSchema`
- `mechanicsVector`
- `level`
- `movement`
- `reward`

Validation requires matching manifest fingerprint, supported schema versions, non-empty task ID, and at least one
platform. Unity and SimCore must exchange this DTO or a backward-compatible version.
