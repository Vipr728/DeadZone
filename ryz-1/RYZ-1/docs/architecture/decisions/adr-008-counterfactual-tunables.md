# ADR-008: Counterfactual tunables as scenario-level overrides

Status: Accepted

Context: Counterfactuals need controlled single-parameter changes with everything else held fixed, and guaranteed restoration.

Decision: ScenarioConfig carries a list of TunableOverride entries (target id, field, value). The adapter applies overrides at arena reset and restores originals at arena teardown. MVP tunables: player controller public fields (coyoteTime, jumpBufferTime, dashSpeed, ...) and one dynamic-object tunable (named moving-platform speed; position/width deferred). Paired runs share seeds/agents/profiles; only one override differs. Apply-to-project is a manual editor action only.

Alternatives: Scene variants per counterfactual — rejected: heavy, drifts. Live reflection over arbitrary fields — deferred; MVP uses an explicit whitelist.

Consequences: Restoration is testable (capture → override → teardown → assert equal). Whitelist keeps the surface honest and small.
