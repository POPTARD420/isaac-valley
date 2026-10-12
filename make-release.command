#!/bin/zsh
# Double-click to build both release zips into the "release" folder (Mac).
cd "$(dirname "$0")"

DOTNET=$(command -v dotnet)
for d in /usr/local/share/dotnet/dotnet "$HOME/.dotnet/dotnet"; do
  [ -z "$DOTNET" ] && [ -x "$d" ] && DOTNET="$d"
done
if [ -z "$DOTNET" ]; then
  echo "Couldn't find dotnet. Install the .NET 6 SDK: https://dotnet.microsoft.com/download/dotnet/6.0"
  read; exit 1
fi

VERSION=$(sed -nE 's/.*"Version": *"([^"]+)".*/\1/p' IsaacValley/manifest.json)
echo "Building Isaac Valley $VERSION..."
mkdir -p release

"$DOTNET" build IsaacValley -c Release || { echo; echo "Build failed (see above)."; read; exit 1; }

STARDEW_ZIP=$(find IsaacValley/bin/Release -name "IsaacValley*.zip" | head -1)
cp "$STARDEW_ZIP" "release/IsaacValley-$VERSION-Stardew.zip"

rm -f "release/IsaacValleyBridge-$VERSION-Isaac.zip"
zip -rq "release/IsaacValleyBridge-$VERSION-Isaac.zip" IsaacValleyBridge \
  -x "*/save*.dat" "*/link_*" "*.tmp" "*/.DS_Store"

echo
echo "Done:"
ls -1 release
open release
