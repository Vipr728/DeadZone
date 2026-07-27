# ADR-001: Unity-native runtime, Python optional and later

Status: Accepted

Context: Simulation needs direct access to Rigidbody2D state, colliders, and the player controller. MVP must ship without training.

Decision: All simulation, agents, telemetry, replay, and analysis run in Unity/C#. Python enters only later for offline training/statistics, communicating via exported telemetry files and ONNX imports.

Alternatives: Python-owned loop via ML-Agents/gRPC — rejected: adds mandatory dependency, IPC latency, deployment complexity, and no MVP benefit.

Consequences: No cross-language protocol to maintain in MVP; statistical tooling limited to C# until Python phase; ONNX/Sentis boundary designed but not built.
