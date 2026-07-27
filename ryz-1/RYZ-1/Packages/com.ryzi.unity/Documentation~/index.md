# Ryzi

Open `Tools > Ryzi`, scan the current scene, review evidence, then enter Play Mode for providers that use isolated
local physics scenes. Ryzi is local-first: output lives under `Library/Ryzi`, uploads are unavailable, and the
base package adds no scene objects or runtime build dependency beyond `Ryzi.Runtime`.

Use the project scanner for Tier 1/Tier 2 onboarding. For unusual controllers, implement
`IPlaytestGameAdapter`; for custom Editor orchestration, implement `IPlaytestIntegrationProvider` in an
Editor-only project assembly.

Current bundled repository integration supports the existing CelesteBenchmark simulator. Generic compatibility
outside that fixture is not yet claimed.
