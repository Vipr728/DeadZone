# Compatibility Tiers

- Tier 1 Automatic: conventional 2D MonoBehaviour controllers where scanner evidence identifies player, input,
  reset, death, completion, and geometry with no ambiguous critical issue.
- Tier 2 Assisted: the same runtime surface, but one or more critical candidates require confirmation in UI.
- Tier 3 Adapter: unusual architectures implement `IPlaytestGameAdapter` or an Editor integration provider.

Current measured scope is the bundled CelesteBenchmark integration only. The generic scanner is a candidate
generator, not proof of zero-code compatibility on unrelated projects. Input System, legacy input, Rigidbody2D,
kinematic controllers, tilemaps, and prefabs are roadmap coverage until exercised by dedicated fixtures.
