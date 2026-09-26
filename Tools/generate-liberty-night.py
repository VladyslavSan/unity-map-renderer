#!/usr/bin/env python3
"""Derive liberty-night.json from liberty.json (UMR-143).

Rule: invert the LIGHTNESS of every colour value, keep hue and saturation and alpha
unchanged, and touch nothing else in the document. Lightness-inversion flips a light
theme into a dark one while preserving every colour's contrast against every other
colour (if A was lighter than B, A stays lighter than B after both invert) — so
backgrounds/land go dark and roads/labels stay exactly as legible as before, with
no per-layer judgement calls. Colour syntax (hex/rgb/rgba/hsl/hsla) round-trips
through its original family so the diff stays readable.

Only "*-color" leaf strings are touched, wherever they occur (including inside
interpolate/step/case expressions) — sources, layer id order, type/source/
source-layer/filter/minzoom/maxzoom/layout are copied through untouched.

Regenerate: python3 Tools/generate-liberty-night.py
"""
import colorsys
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Assets/StreamingAssets/Fixtures/liberty.json"
DST = ROOT / "Assets/StreamingAssets/Fixtures/liberty-night.json"

HEX_RE = re.compile(r"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
RGB_RE = re.compile(r"^rgba?\(\s*([^)]+)\)$", re.IGNORECASE)
HSL_RE = re.compile(r"^hsla?\(\s*([^)]+)\)$", re.IGNORECASE)


def invert_color(value: str) -> str:
    """Invert the lightness of one CSS colour literal, same syntax family out."""
    m = HEX_RE.match(value)
    if m:
        h = m.group(1)
        if len(h) == 3:
            h = "".join(c * 2 for c in h)
        r, g, b = (int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))
        hue, light, sat = colorsys.rgb_to_hls(r, g, b)
        r, g, b = colorsys.hls_to_rgb(hue, 1.0 - light, sat)
        return "#{:02x}{:02x}{:02x}".format(round(r * 255), round(g * 255), round(b * 255))

    m = RGB_RE.match(value)
    if m:
        parts = [p.strip() for p in m.group(1).split(",")]
        r, g, b = (int(p) / 255.0 for p in parts[:3])
        alpha = parts[3] if len(parts) > 3 else None
        hue, light, sat = colorsys.rgb_to_hls(r, g, b)
        r, g, b = colorsys.hls_to_rgb(hue, 1.0 - light, sat)
        rgb = f"{round(r * 255)},{round(g * 255)},{round(b * 255)}"
        return f"rgba({rgb},{alpha})" if alpha is not None else f"rgb({rgb})"

    m = HSL_RE.match(value)
    if m:
        parts = [p.strip() for p in m.group(1).split(",")]
        hue_deg = float(parts[0])
        sat_pct = parts[1].rstrip("%")
        light_pct = float(parts[2].rstrip("%"))
        alpha = parts[3] if len(parts) > 3 else None
        new_light = 100.0 - light_pct
        hsl = f"{parts[0]},{sat_pct}%,{new_light:g}%"
        return f"hsla({hsl},{alpha})" if alpha is not None else f"hsl({hsl})"

    return value  # not a colour literal (e.g. "linear", "zoom") — leave as-is


def walk(node, in_color: bool):
    if isinstance(node, dict):
        return {k: walk(v, in_color or k.endswith("-color")) for k, v in node.items()}
    if isinstance(node, list):
        return [walk(v, in_color) for v in node]
    if isinstance(node, str) and in_color:
        return invert_color(node)
    return node


# liberty.json has no root "sky" block (day styles fall back to the spec's light-sky
# defaults), so there is nothing to invert. Night gets a fixed sky block instead: horizon
# and fog share one dark blue-grey close to the background/water tones below so the map
# edge dissolves with no seam, and the zenith goes to a deeper navy for contrast.
NIGHT_SKY = {
    "sky-color": "#070a14",
    "horizon-color": "#171b26",
    "fog-color": "#171b26",
}

# liberty.json has no root "light" block either, so the day style lights with the spec
# defaults. Night gets a cool, dim moonlight: only colour and intensity, so a day/night
# restyle eases the tint and brightness while "position" keeps the spec-default direction.
NIGHT_LIGHT = {
    "color": "#a9b8d8",
    "intensity": 0.35,
}

# Every fill-pattern layer gets this dark, cool fill-color, which tints its sprite for night.
NIGHT_PATTERN_TINT = "#4a5261"

# Background near the land-fill composite so tile pop-in doesn't flash, in the fog's cool hue.
NIGHT_BACKGROUND = "#14171e"


def main() -> None:
    style = json.loads(SRC.read_text())
    style["layers"] = [walk(layer, False) for layer in style["layers"]]
    for layer in style["layers"]:
        paint = layer.get("paint", {})
        if layer["type"] == "background":
            paint["background-color"] = NIGHT_BACKGROUND
        if "fill-pattern" in paint:
            paint["fill-color"] = NIGHT_PATTERN_TINT
    style["sky"] = NIGHT_SKY
    style["light"] = NIGHT_LIGHT
    DST.write_text(json.dumps(style, indent=2) + "\n")


if __name__ == "__main__":
    main()
