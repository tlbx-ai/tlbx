#!/bin/bash
set -e

# Build MidTerm AOT for Linux x64
# Usage: ./build-aot-linux.sh [--reproducible]
#
# --reproducible: Enable reproducible build mode (ContinuousIntegrationBuild=true)
#                 Use this when building for verification/audit purposes.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

CI_FLAG=""
if [[ "$1" == "--reproducible" ]]; then
    CI_FLAG="/p:ContinuousIntegrationBuild=true"
    echo "Building tlbx AOT for Linux x64..."
    echo "  (Reproducible build mode enabled)"
else
    echo "Building tlbx AOT for Linux x64..."
fi

dotnet publish -c Release -r linux-x64 /p:IsPublishing=true $CI_FLAG

OUT_PATH="bin/Release/net10.0/linux-x64/publish/mt"
echo ""
echo "Build complete!"
if [ -f "$OUT_PATH" ]; then
    echo "Output: $OUT_PATH ($(du -h "$OUT_PATH" | cut -f1))"
    if [[ "$1" == "--reproducible" ]]; then
        echo "SHA256: $(sha256sum "$OUT_PATH" | cut -d' ' -f1)"
    fi
else
    echo "(file not found - check build output)"
fi
