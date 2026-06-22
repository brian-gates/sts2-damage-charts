#!/usr/bin/env bash
# Build Damage Charts in Release and stage the Steam Workshop content directory, then upload it
# (or print the upload command) via MegaCrit's official sts2-mod-uploader.
#
# Steam Workshop publishing talks to SteamUGC directly, so it CANNOT run in CI: the Steam client
# must be running and logged in on this machine. First run creates the item (per workshop.json's
# visibility) and writes dist/workshop/mod_id.txt; later runs update that same item.
#
# Env:
#   STS2GameDir  Path to the Slay the Spire 2 install (default: macOS Steam path below).
#   MODUPLOADER  Path to the ModUploader CLI (default: ./tools/ModUploader if present).
set -euo pipefail

cd "$(dirname "$0")/.."

GAME_DIR="${STS2GameDir:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
WORKSPACE="dist/workshop"
CONTENT="$WORKSPACE/content"

echo "==> Building Release (STS2GameDir=$GAME_DIR)"
dotnet build STS2_DamageCharts.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR"

echo "==> Staging $CONTENT"
mkdir -p "$CONTENT"
# Exactly what subscribers receive: the built DLL + the manifest, renamed to match the manual install.
cp out/STS2_DamageCharts.dll "$CONTENT/STS2_DamageCharts.dll"
cp mod_manifest.json "$CONTENT/STS2_DamageCharts.json"
echo "    $(ls -1 "$CONTENT" | tr '\n' ' ')"

UPLOADER="${MODUPLOADER:-./tools/ModUploader}"
if [[ -x "$UPLOADER" ]]; then
  echo "==> Uploading via $UPLOADER (Steam must be running + logged in)"
  "$UPLOADER" upload -w "$WORKSPACE"
else
  cat <<EOF

==> ModUploader CLI not found (looked for: $UPLOADER)
    Content is staged and ready. To publish:
      1. Download the uploader for your platform (osx-arm64) from
         https://github.com/megacrit/sts2-mod-uploader/releases
         and place the binary at ./tools/ModUploader (or set MODUPLOADER).
      2. Make sure the Steam client is running and logged in.
      3. Run:  "\$MODUPLOADER" upload -w $WORKSPACE
    First upload creates the item and writes $WORKSPACE/mod_id.txt — commit that file
    afterward so future updates target the same Workshop item.
EOF
fi
