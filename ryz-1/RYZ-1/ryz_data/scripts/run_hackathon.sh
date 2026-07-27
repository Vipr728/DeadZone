#!/usr/bin/env bash
set -euo pipefail
python -m ryz_data.pipelines.generate_dataset --config ryz_data/config/hackathon.yaml --output "${1:-datasets/ryz_hackathon}"
