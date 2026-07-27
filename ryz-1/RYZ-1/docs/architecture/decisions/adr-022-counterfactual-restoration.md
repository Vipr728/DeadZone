# ADR-022: Counterfactual Restoration Guarantees

**Decision:** Capture each original once, apply candidates only in isolated simulations, and restore in `finally`
on success, failure, cancellation, and exception. Domain-reload recovery records are Editor-only.

**Consequence:** A restoration failure is surfaced as a blocking error, never hidden behind experiment results.
