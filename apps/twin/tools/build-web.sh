#!/usr/bin/env bash
# Regenerates the scene from Authoring/ and builds Builds/WebGL (browser build served by the Portal at /twin/).
# Needs the WebGL module for the project's Unity version. Override UNITY=...
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity}"
mkdir -p "$ROOT/Builds"
"$UNITY" -batchmode -projectPath "$ROOT/unity" -buildTarget WebGL \
  -executeMethod ShipyardTwin.Editor.ShipyardTwinBuild.BuildWebBatch -logFile "$ROOT/Builds/build-web.log"
code=$?
grep -E "\[ShipyardTwin\] build|error CS" "$ROOT/Builds/build-web.log" | head -5
echo "build exit code: $code (log: $ROOT/Builds/build-web.log)"
exit $code
