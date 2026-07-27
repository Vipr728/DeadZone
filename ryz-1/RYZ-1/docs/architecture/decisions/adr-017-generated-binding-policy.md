# ADR-017: Generated Binding Policy

**Decision:** Prefer explicit/cached bindings. Generate only after preview and confirmation, into
`Assets/Ryzi.Generated`, deterministically, without editing customer scripts.

**Consequence:** Most supported projects incur no source changes and generated adapters are auditable.
