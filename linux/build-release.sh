#!/usr/bin/env bash
# Builds the Linux release: dist/Orbit-<version>-linux-<arch>.tar.gz (self-contained, nothing else to install) + SHA256SUMS.
#   linux/build-release.sh [linux-x64|linux-arm64]
set -euo pipefail
rid=${1:-linux-x64}
case "$rid" in linux-x64|linux-arm64) ;; *) echo "Runtime must be linux-x64 or linux-arm64." >&2; exit 2;; esac
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
dotnet=${DOTNET:-dotnet}
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$here/Orbit.Linux.csproj" | head -1)
name="Orbit-$version-${rid}"
out="$root/dist/$name"
rm -rf "$out" "$root/dist/$name.tar.gz"
"$dotnet" publish "$here/Orbit.Linux.csproj" -c Release -r "$rid" --self-contained true -o "$out" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None -p:DebugSymbols=false
rm -f "$out"/*.pdb
cp "$here/packaging/install.sh" "$here/packaging/uninstall.sh" "$here/packaging/orbit.desktop" "$out/"
cp "$here/Assets/orbit.png" "$out/orbit.png"
chmod 755 "$out/orbit" "$out/install.sh" "$out/uninstall.sh"
mkdir -p "$out/licences"
cp "$here/Assets/Fonts/FluentSystemIcons-LICENSE.txt" "$out/licences/"
packages=$(sed -n 's/.*"packagesPath": "\(.*\)",/\1/p' "$here/obj/project.assets.json" | head -1)
for pack in avalonia skiasharp harfbuzzsharp svg.skia sharpcompress ssh.net "microsoft.netcore.app.runtime.$rid"; do
  dir=$(ls -d "$packages/$pack"/*/ 2>/dev/null | sort -V | tail -1 || true)
  [ -n "$dir" ] || continue
  for f in "$dir"LICENSE* "$dir"license* "$dir"THIRD-PARTY-NOTICES*; do [ -f "$f" ] && { mkdir -p "$out/licences/$pack"; cp "$f" "$out/licences/$pack/"; }; done
done
tar -C "$root/dist" -czf "$root/dist/$name.tar.gz" "$name"
(cd "$root/dist" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
echo "Built dist/$name.tar.gz"
