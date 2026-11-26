#!/usr/bin/env bash
# Runs Orbit inside an isolated, virtual KWin Wayland session (own D-Bus, own XWayland, a throwaway HOME with sample
# files), plays a scenario of test commands (see src/MainWindow.Test.cs) and saves screenshots. Never touches the real
# desktop or your settings.
#   tools/nested-test.sh OUTPUT_DIR SCENARIO_FILE
set -euo pipefail
OUT=$(realpath -m "${1:?output dir}")
SCENARIO=$(realpath "${2:?scenario file}")
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOTNET=${DOTNET:-dotnet}
BIN=${ORBIT_BIN:-$ROOT/bin/Debug/net10.0/orbit.dll}
mkdir -p "$OUT"
rm -f "$OUT"/*.png "$OUT"/orbit-test.log

# a throwaway home with a bit of everything to look at
H="$OUT/home"
if [ ! -d "$H" ]; then
  mkdir -p "$H"/{Desktop,Documents/Projects/orbit/src,Downloads,Music,Pictures/Holiday,Videos,.config,.local/share}
  printf 'XDG_DESKTOP_DIR="$HOME/Desktop"\nXDG_DOCUMENTS_DIR="$HOME/Documents"\nXDG_DOWNLOAD_DIR="$HOME/Downloads"\nXDG_MUSIC_DIR="$HOME/Music"\nXDG_PICTURES_DIR="$HOME/Pictures"\nXDG_VIDEOS_DIR="$HOME/Videos"\n' > "$H/.config/user-dirs.dirs"
  for c in tomato steelblue seagreen gold orchid; do
    magick -size 1600x1000 "gradient:$c-white" -fill white -pointsize 90 -gravity center -annotate 0 "$c" "$H/Pictures/$c.jpg" 2>/dev/null || true
  done
  magick -size 800x600 plasma:fractal "$H/Pictures/Holiday/beach.png" 2>/dev/null || true
  ffmpeg -v error -f lavfi -i testsrc=duration=6:size=640x360:rate=24 -f lavfi -i sine=frequency=440:duration=6 -shortest -c:v libx264 -pix_fmt yuv420p "$H/Videos/test pattern.mp4" 2>/dev/null || true
  ffmpeg -v error -f lavfi -i sine=frequency=330:duration=8 "$H/Music/tone.mp3" 2>/dev/null || true
  printf '# Orbit\n\nA fast, modern file manager.\n\n- tabs\n- previews\n' > "$H/Documents/README.md"
  printf 'Notes for the week\n==================\n\nNothing urgent.\n' > "$H/Documents/notes.txt"
  printf '{\n  "name": "orbit",\n  "version": "1.0.0"\n}\n' > "$H/Documents/Projects/orbit/package.json"
  printf 'fn main() {\n    println!("hello");\n}\n' > "$H/Documents/Projects/orbit/src/main.rs"
  printf 'print("hi")\n' > "$H/Documents/Projects/orbit/src/app.py"
  magick -size 600x800 xc:white -pointsize 40 -annotate +60+120 'Invoice Q4' "$H/Documents/invoice_Q4_final.pdf" 2>/dev/null || true
  (cd "$H/Documents" && python3 -c "import zipfile;z=zipfile.ZipFile('archive.zip','w');[z.write(p) for p in ['README.md','notes.txt','Projects/orbit/package.json']];z.close()")
  tar -C "$H/Documents" -czf "$H/Downloads/project.tar.gz" Projects
  printf 'secret\n' > "$H/.hidden-file"
  mkdir -p "$H/.local/share/Trash"/{files,info}
  printf 'old\n' > "$H/.local/share/Trash/files/old notes.txt"
  printf '[Trash Info]\nPath=%s/Documents/old%%20notes.txt\nDeletionDate=2026-11-20T10:00:00\n' "$H" > "$H/.local/share/Trash/info/old notes.txt.trashinfo"
fi

cat > "$OUT/session.sh" <<INNER
#!/usr/bin/env bash
set -u
export HOME="$H" XDG_CONFIG_HOME="$H/.config" XDG_DATA_HOME="$H/.local/share" XDG_CACHE_HOME="$H/.cache"
export XDG_CURRENT_DESKTOP=KDE XDG_SESSION_TYPE=wayland
export ORBIT_TEST_SCRIPT="$SCENARIO" ORBIT_TEST_OUT="$OUT" ORBIT_SCREENSHOT_PY="$ROOT/tools/screenshot.py"
export DOTNET_ROOT="\${DOTNET_ROOT:-}"
if [[ "$BIN" == *.dll ]]; then "$DOTNET" "$BIN" "\${ORBIT_ARGS:-}"; else "$BIN" "\${ORBIT_ARGS:-}"; fi > "$OUT/orbit.log" 2>&1
echo "exit=\$?" >> "$OUT/orbit.log"
INNER
chmod +x "$OUT/session.sh"
export -n WAYLAND_DISPLAY DISPLAY
timeout ${TIMEOUT:-240} dbus-run-session -- kwin_wayland --virtual --xwayland --width ${W:-1440} --height ${H_PX:-900} \
  --socket orbit-test-wl --exit-with-session "$OUT/session.sh" > "$OUT/kwin.log" 2>&1 || true
ls "$OUT"
