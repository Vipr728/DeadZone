#!/bin/bash
# T14 cross-process determinism probe. Runs the SAME fixed 400-action stream on a cold-loaded SampleScene in N
# separate Unity processes and diffs the position traces. The standard test suite CANNOT cover this — a single
# process is deterministic by construction (limitations.md #14).
#
# Usage: tools/cross-process-determinism.sh [runs] [projectPath] [unityBinary]
# Exit 0 = all runs bit-identical. Exit 1 = divergence (prints the first diverging tick and the class count).
#
# NOTE: this is a sampling detector, not a proof. As of T14 the divergence has ~4 outcome classes, so two runs
# agree by chance often enough to be misleading — hence the default of 3 runs (~2.5 min each). Raise it if you
# need more confidence.
set -u
RUNS="${1:-3}"
PROJECT="${2:-$(cd "$(dirname "$0")/.." && pwd)}"
UNITY="${3:-/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity}"
OUT="$(mktemp -d)"
FILTER=PlatformerPlaytest.Tests.PlayMode.CrossProcessDeterminismTests.SampleScene_FixedStream_WritesTraceFingerprint

for i in $(seq 1 "$RUNS"); do
  PPT_DETERMINISM_OUT="$OUT/$i.txt" "$UNITY" -batchmode -nographics -projectPath "$PROJECT" \
    -runTests -testPlatform PlayMode -testFilter "$FILTER" \
    -testResults "$OUT/$i.xml" -logFile "$OUT/$i.log" >/dev/null 2>&1
  [ -f "$OUT/$i.txt" ] || { echo "FAIL: run $i produced no output (see $OUT/$i.log)"; exit 2; }
  echo "run $i: $(tr '\n' ' ' < "$OUT/$i.txt")"
done

classes=$(md5 -q "$OUT"/*.txt.trace 2>/dev/null || md5sum "$OUT"/*.txt.trace | cut -d' ' -f1)
distinct=$(echo "$classes" | sort -u | wc -l | tr -d ' ')
if [ "$distinct" -eq 1 ]; then
  echo "PASS: $RUNS processes produced identical traces"; rm -rf "$OUT"; exit 0
fi
for i in $(seq 2 "$RUNS"); do
  d=$(diff "$OUT/1.txt.trace" "$OUT/$i.txt.trace" | grep -m1 '^<' | cut -f1 | tr -d '< ')
  [ -n "$d" ] && { echo "DIVERGED: run 1 vs run $i at tick $d"; break; }
done
echo "FAIL: $distinct distinct traces across $RUNS processes; traces kept in $OUT"
exit 1
