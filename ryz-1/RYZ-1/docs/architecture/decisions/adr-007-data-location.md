# ADR-007: Generated data under Library/PlatformerPlaytest/

Status: Accepted

Context: Run data must not pollute Assets/ (imports, VCS noise).

Decision: All run output under `Library/PlatformerPlaytest/runs/<runId>/`. Library/ is already Unity-generated and git-ignored. Path provided by one static `PlaytestPaths` helper; tests assert nothing writes under Assets/.

Alternatives: `ProjectRoot/PlaytestData` — viable, but needs its own gitignore entry; Library needs nothing. Persistent data path — wrong scope (per-user, not per-project).

Consequences: Data dies with Library deletion — acceptable for generated runs; export command can copy a run out if a user wants to keep it.
