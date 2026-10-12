#!/usr/bin/env python3
"""Pair implementation clients with demos at one logical pixel per pixel."""

import argparse
from pathlib import Path

from PIL import Image


NAMES = (
    "subtitle-submit-desktop",
    "subtitle-progress-desktop",
    "progress-from-tasklist-desktop",
    "subtitle-submit-web",
    "subtitle-progress-web",
    "subtitle-progress-mobile",
)

# .fw rectangles in the original 1280 x 800 desktop demos. Only background
# windows are excluded; the entire target window, including its title, remains.
DESKTOP_CLIENTS = {
    "subtitle-submit-desktop": (170, 120, 940, 560),
    "subtitle-progress-desktop": (120, 56, 1060, 712),
    "progress-from-tasklist-desktop": (150, 70, 1040, 690),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="Implementation PNG directory; demos default to its parent")
    parser.add_argument("--demos", type=Path, help="Directory containing demo PNGs")
    args = parser.parse_args()
    demos = args.demos or args.output.parent
    count = 0
    for name in NAMES:
        implementation = args.output / f"{name}.png"
        if not implementation.is_file():
            print(f"Skipped {name}: no implementation PNG")
            continue
        demo = demos / f"{name}.png"
        if not demo.is_file():
            raise FileNotFoundError(f"Missing demo for {implementation}: {demo}")
        with Image.open(demo) as left, Image.open(implementation) as right:
            left = left.convert("RGB")
            if name in DESKTOP_CLIENTS:
                x, y, width, height = DESKTOP_CLIENTS[name]
                scale = left.width / 1280
                left = left.crop(tuple(round(v * scale) for v in (x, y, x + width, y + height)))
                left = left.resize((width, height), Image.Resampling.LANCZOS)
            else:
                height = right.height
                left = left.resize((max(1, round(left.width * height / left.height)), height), Image.Resampling.LANCZOS)
            right = right.convert("RGB")
            comparison = Image.new("RGB", (left.width + right.width, max(left.height, right.height)), "white")
            comparison.paste(left, (0, 0))
            comparison.paste(right, (left.width, 0))
            output = args.output / f"compare-{name}.png"
            comparison.save(output)
            print(f"Created {output} ({comparison.width} × {comparison.height})")
            count += 1
    if not count:
        parser.error("No implementation PNGs found")


if __name__ == "__main__":
    main()
