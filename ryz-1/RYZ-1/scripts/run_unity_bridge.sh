#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

MAC_HOST="${RYZ1_MAC_HOST:-rahulpeesa@rahuls-macbook-pro-2}"
SSH_SOCKET="${RYZ1_SSH_SOCKET:-/tmp/ryz1-rahul-ssh.sock}"
MAC_PROJECT="${RYZ1_MAC_PROJECT:-/Users/rahulpeesa/Documents/GitHub/Ryzi-labs/RYZ-1}"
UNITY_BIN="${RYZ1_UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity}"
DOTNET="${RYZ1_DOTNET:-/home/dell/.dotnet/dotnet}"
MODEL="${RYZ1_MODEL:-Library/RYZ1/models/curriculum-sequence-v3/ryz1-sequence.onnx}"
RUN_ID="${RYZ1_BRIDGE_RUN_ID:-unity-bridge-$(date -u +%Y%m%d-%H%M%S)}"
OUT="${RYZ1_BRIDGE_OUT:-Library/RYZ1/runs/$RUN_ID}"
REMOTE_ROOT="/tmp/$RUN_ID"
SNAPSHOT="$OUT/unity-snapshot.json"

if [[ ! -S "$SSH_SOCKET" ]]; then
  echo "error: Rahul Mac SSH control socket not found at $SSH_SOCKET" >&2
  exit 10
fi
if [[ ! -x "$DOTNET" ]]; then
  echo "error: dotnet not found at $DOTNET" >&2
  exit 11
fi
if [[ ! -f "$MODEL" ]]; then
  echo "error: sequence model not found at $MODEL" >&2
  exit 12
fi

mkdir -p "$OUT"
ssh -S "$SSH_SOCKET" -o BatchMode=yes "$MAC_HOST" "mkdir -p '$REMOTE_ROOT'"

ssh -S "$SSH_SOCKET" -o BatchMode=yes "$MAC_HOST" \
  "cd '$MAC_PROJECT' && RYZ1_UNITY_SNAPSHOT_OUT='$REMOTE_ROOT/unity-snapshot.json' \
  '$UNITY_BIN' -batchmode -nographics -projectPath . -runTests -testPlatform PlayMode \
  -testFilter Ryzi.Integrations.Tests.PlayMode.NativeSimCoreBridgeTests.UnityFixture_ExportsNativeSnapshot \
  -testResults 'Library/RYZ1/unity-tests/$RUN_ID-export.xml' \
  -logFile 'Library/RYZ1/unity-tests/$RUN_ID-export.log'"

scp -o "ControlPath=$SSH_SOCKET" -o BatchMode=yes \
  "$MAC_HOST:$REMOTE_ROOT/unity-snapshot.json" "$SNAPSHOT"

"$DOTNET" run --project src/Ryz1.Runner/Ryz1.Runner.csproj -c Release -- \
  solve-unity-snapshot \
  --snapshot "$SNAPSHOT" \
  --model "$MODEL" \
  --neural-sequence-length 16 \
  --beam 20 \
  --depth 50 \
  --out "$OUT"

scp -o "ControlPath=$SSH_SOCKET" -o BatchMode=yes \
  "$OUT/task_bundle.json" "$OUT/replay.json" "$MAC_HOST:$REMOTE_ROOT/"

ssh -S "$SSH_SOCKET" -o BatchMode=yes "$MAC_HOST" \
  "cd '$MAC_PROJECT' && RYZ1_NATIVE_BRIDGE_DIR='$REMOTE_ROOT' \
  '$UNITY_BIN' -batchmode -nographics -projectPath . -runTests -testPlatform PlayMode \
  -testFilter Ryzi.Integrations.Tests.PlayMode.NativeSimCoreBridgeTests.NativeReplay_CompletesInAuthoritativeUnityArena \
  -testResults 'Library/RYZ1/unity-tests/$RUN_ID-replay.xml' \
  -logFile 'Library/RYZ1/unity-tests/$RUN_ID-replay.log'"

scp -o "ControlPath=$SSH_SOCKET" -o BatchMode=yes \
  "$MAC_HOST:$REMOTE_ROOT/unity-verification.json" "$OUT/unity-verification.json"

echo "RYZ-1 Unity bridge verified: $OUT"
cat "$OUT/unity-verification.json"
