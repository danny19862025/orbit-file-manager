#!/usr/bin/env bash
# Installs Orbit for your account only (no root needed): ~/.local/opt/orbit, a launcher in your app menu and `orbit` on your PATH.
#   ./install.sh            install or update
#   ./install.sh --default  also make Orbit the default app for folders
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
dest=$(realpath -m "$HOME/.local/opt/orbit")
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
bin="$HOME/.local/bin"

pkill -x orbit 2>/dev/null && sleep 1 || true
mkdir -p "$dest" "$apps" "$icons" "$bin"
cp -r "$here"/. "$dest"/
chmod 755 "$dest/orbit"
cp "$here/orbit.png" "$icons/orbit-file-manager.png"
ln -sf "$dest/orbit" "$bin/orbit"
sed "s|@BIN@|$dest/orbit|g" "$here/orbit.desktop" > "$apps/orbit-file-manager.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "${icons%/256x256/apps}" 2>/dev/null || true
if [ "${1:-}" = "--default" ]; then
  xdg-mime default orbit-file-manager.desktop inode/directory && echo "Orbit is now the default app for folders."
fi
echo "Installed Orbit to $dest — find it in your app menu, or run: orbit"
case ":$PATH:" in *":$bin:"*) ;; *) echo "(Add $bin to your PATH to start it with the orbit command.)";; esac
