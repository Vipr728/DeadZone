# ADR-012: Automatic Discovery With Adapter Fallback

**Decision:** Rank conventional components using explainable evidence, allow assisted confirmation, and expose
`IPlaytestGameAdapter` plus `IPlaytestIntegrationProvider` for unsupported architectures.

**Consequence:** Automation can improve without expanding the stable runtime API or hard-coding every controller.
