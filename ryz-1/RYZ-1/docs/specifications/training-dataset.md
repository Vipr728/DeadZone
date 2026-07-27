# Training Dataset

Schema: `ryz-search-dataset/1.0`.

Current format is JSON for reliability during the hackathon. Larger runs can migrate to Parquet or compressed
NumPy shards behind the same fields.

Each transition records:

- `taskId`
- `trialId`
- `nodeId`
- `parentId`
- `searchDepth`
- `macroId`
- `observation`
- `nextObservation`
- `reward`
- `progress`
- `death`
- `completion`
- `survivedPruning`
- `eventuallyCompleted`
- `candidateScore`

Splits must be by entire task IDs, not individual transitions.
