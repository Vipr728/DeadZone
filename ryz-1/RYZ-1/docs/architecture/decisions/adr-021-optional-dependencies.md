# ADR-021: Optional Dependency Strategy

**Decision:** Base assemblies use Unity core APIs only. Input System assets are inspected as assets; project
integrations live outside the base package and are discovered dynamically.

**Consequence:** Missing Input System or game assemblies do not break the commercial package.
