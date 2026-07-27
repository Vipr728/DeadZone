# ADR-011: UPM Package Boundaries

**Decision:** Add `Packages/com.ryzi.unity` with Runtime, Editor, and Tests assemblies. Keep the existing
prototype under Assets and connect it via an optional project integration provider. Package Runtime never
references project game assemblies.

**Consequence:** The package is removable and distributable; the repository-specific provider is separately
replaceable.
