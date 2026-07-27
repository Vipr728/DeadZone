# ADR-014: Static Analysis Plus Runtime Calibration

**Decision:** Static inspection creates candidates; isolated probes validate observable effects. The first static
backend is conservative Unity/member analysis, explicitly not full Roslyn semantics.

**Consequence:** Names alone never prove mechanics, and limitations remain visible.
