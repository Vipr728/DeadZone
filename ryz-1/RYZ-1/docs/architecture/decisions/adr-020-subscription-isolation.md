# ADR-020: Subscription Service Isolation

**Decision:** Entitlement and model distribution are Editor-only asynchronous interfaces with local development
implementations. Local features are never authentication-gated.

**Consequence:** Billing and network policy cannot leak into simulation or customer builds.
