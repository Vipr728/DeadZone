# ADR-010: Asmdef-separated folder under Assets/, package extraction deferred

Status: Accepted

Context: Product spec prefers a UPM package (`com.project.platformer-playtest`). But the adapter must reference `CelesteBenchmark` types, which live in Assembly-CSharp; a real package cannot reference Assembly-CSharp.

Decision: MVP ships as `Assets/PlatformerPlaytest/` with Runtime/Editor/Tests asmdefs enforcing package-shaped boundaries. Runtime asmdef references Assembly-CSharp only for the Adapter folder (enforced by review, since asmdef granularity is per-assembly). Extraction to `Packages/com.project.platformer-playtest` happens later by also giving the simulator an asmdef — a mechanical move because boundaries already hold.

Alternatives: Package now + simulator asmdef now — rejected: touches simulator meta files and every consumer for zero MVP value. No asmdefs — rejected: boundaries would rot immediately.

Consequences: One asmdef-reference-wide door to Assembly-CSharp; module-boundaries.md rule plus reviewer check keeps non-adapter code from using it.
