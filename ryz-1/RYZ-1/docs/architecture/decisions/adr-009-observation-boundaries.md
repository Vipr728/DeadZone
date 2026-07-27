# ADR-009: Compact structured observations, no object-graph serialization

Status: Accepted

Context: Thousands of episodes; agents need enough state to act, not the whole scene.

Decision: Observation = player struct (position, velocity, grounded, wall contacts L/R, dashing/climbing flags, dash count, stamina, progress, section) + world sample (local occupancy grid of solid/one-way/hazard cells around the player, sampled via Physics2D queries against the arena's physics scene, plus a short list of nearby dynamic entities: moving platforms with velocity, springs, refills, goal). Recent action/event history kept by the agent, not re-serialized per tick.

Alternatives: Screenshots — excluded by product scope. Full scene serialization — rejected: cost and coupling.

Consequences: Grid sampling cost bounded and benchmarkable; adding new entity kinds is an adapter-local change.
