#!/usr/bin/env bash
# Build release, create jfresolve.zip, and print MD5 for repository.json.
# Run from repo root or jfresolve-10.11. After running, update repository.json
# "checksum" for version 1.0.0.48 with the printed MD5.

set -e
cd "$(dirname "$0")"
VERSION=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Jfresolve.csproj)
BUILD_NUM="${VERSION##*.}"
RELEASE_TAG="10.11.1.0.0.${BUILD_NUM}"

echo "Building Jfresolve ${VERSION} (release tag: ${RELEASE_TAG})..."
rm -rf publish_release
dotnet publish Jfresolve.csproj -c Release -o publish_release

echo "Creating jfresolve.zip..."
rm -f jfresolve.zip
(cd publish_release && zip -r ../jfresolve.zip .)

echo ""
echo "Release package: $(pwd)/jfresolve.zip"
echo "MD5 checksum (update repository.json for this version):"
if command -v md5sum >/dev/null 2>&1; then
  md5sum jfresolve.zip | awk '{print $1}'
else
  md5 -q jfresolve.zip
fi
echo ""
echo "Next steps:"
echo "  1. Update repository.json: set \"checksum\" for version ${VERSION} to the MD5 above."
echo "  2. Create GitHub release tag: ${RELEASE_TAG}"
echo "  3. Upload jfresolve.zip to the release."
