#!/usr/bin/env python3
"""Pair each available implementation PNG with its demo, at equal height."""

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
            # Keep the implementation at its native scale; proportionally resize the demo.
            height = right.height
            left = left.convert("RGB").resize(
                (max(1, round(left.width * height / left.height)), height), Image.Resampling.LANCZOS
            )
            right = right.convert("RGB")
            comparison = Image.new("RGB", (left.width + right.width, height), "white")
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
