# Unity, SimCore, and Python Boundary

Unity does not call cloud services and does not run the hackathon training loop. Unity exports and imports files.

Unity exports:

- Mechanics Manifest
- RYZ Task Bundle
- optional Unity replay traces for parity tests

GB10 native runtime owns:

- Task-bundle validation
- SimCore calibration and replay verification
- Search trajectory generation
- Dataset export
- Report generation

Python owns:

- Dataset validation/loading
- PyTorch policy-value model
- Training and evaluation
- Checkpoint and TensorBoard output
- ONNX export

Authoritative game simulation for the GB10 demo is `Ryz1.SimCore`, not Python.
