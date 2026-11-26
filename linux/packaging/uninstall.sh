#!/usr/bin/env bash
# Removes Orbit installed by install.sh. Your settings (~/.config/Orbit) are kept unless you pass --purge.
set -euo pipefail
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
pkill -x orbit 2>/dev/null || true
rm -rf "$HOME/.local/opt/orbit"
rm -f "$HOME/.local/bin/orbit" "$apps/orbit-file-manager.desktop" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps/orbit-file-manager.png"
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" 2>/dev/null || true
if [ "${1:-}" = "--purge" ]; then
  rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/Orbit" "${XDG_CACHE_HOME:-$HOME/.cache}/Orbit"
  echo "Removed Orbit and its settings."
else
  echo "Removed Orbit. Your settings are still in ${XDG_CONFIG_HOME:-$HOME/.config}/Orbit (run with --purge to delete them)."
fi
