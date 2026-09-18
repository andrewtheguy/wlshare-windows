#!/usr/bin/env bash
#
# Render icon/AppIcon.svg — the macOS app's icon — into the app's .ico.
#
#   scripts/make-icon.sh
#
# The SVG is the icon; the .ico is generated from it and checked in, so a build
# needs neither this script nor its tools. Run it after editing the SVG.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

command -v rsvg-convert >/dev/null || { echo "rsvg-convert not found (apt install librsvg2-bin)" >&2; exit 1; }
command -v magick >/dev/null || { echo "magick not found (apt install imagemagick)" >&2; exit 1; }

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT

# The sizes Windows asks an .ico for: the shell's 16 to 48 at each DPI, and
# 256 for Explorer's large views.
pngs=()
for px in 16 20 24 32 40 48 64 256; do
    rsvg-convert -w "$px" -h "$px" icon/AppIcon.svg -o "$scratch/$px.png"
    pngs+=("$scratch/$px.png")
done
magick "${pngs[@]}" src/WlshareViewer/Assets/wlshare.ico
echo "src/WlshareViewer/Assets/wlshare.ico"
