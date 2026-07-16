#!/bin/bash
set -e

# Build MidTerm AOT for macOS (auto-detects arm64 vs x64)
# Usage: ./build-aot-macos.sh [--reproducible]
#
# --reproducible: Enable reproducible build mode (ContinuousIntegrationBuild=true)
#                 Use this when building for verification/audit purposes.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Detect architecture
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
else
    RID="osx-x64"
fi

CI_FLAG=""
if [[ "$1" == "--reproducible" ]]; then
    CI_FLAG="/p:ContinuousIntegrationBuild=true"
    echo "Building tlbx AOT for macOS ($RID)..."
    echo "  (Reproducible build mode enabled)"
else
    echo "Building tlbx AOT for macOS ($RID)..."
fi

dotnet publish -c Release -r "$RID" /p:IsPublishing=true $CI_FLAG

OUT_PATH="bin/Release/net10.0/$RID/publish/mt"
echo ""
echo "Build complete!"
if [ -f "$OUT_PATH" ]; then
    echo "Output: $OUT_PATH ($(du -h "$OUT_PATH" | cut -f1))"
    if [[ "$1" == "--reproducible" ]]; then
        echo "SHA256: $(shasum -a 256 "$OUT_PATH" | cut -d' ' -f1)"
    fi
else
    echo "(file not found - check build output)"
fi
