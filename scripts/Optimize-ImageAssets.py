#!/usr/bin/env python3
"""Shrink packaged PNG assets without palette reduction. Requires Pillow."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import struct

from PIL import Image


ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src/AvaMedia.Desktop/Assets"


def png_bytes(image):
    output = io.BytesIO()
    metadata = {key: image.info[key] for key in ("icc_profile", "dpi", "exif") if key in image.info}
    image.save(output, "PNG", optimize=True, compress_level=9, **metadata)
    return output.getvalue()


def write_smaller(path, data, resized=False):
    if resized or len(data) < path.stat().st_size:
        path.write_bytes(data)


def update_manifest(path, originals, limit):
    manifest = json.loads(path.read_text(encoding="utf-8"))
    entries = manifest.get("assets", [manifest])
    for entry in entries:
        image_path = path.parent / entry["file"]
        with Image.open(image_path) as image:
            entry["pixelWidth"], entry["pixelHeight"] = image.size
        digest = hashlib.sha256(image_path.read_bytes()).hexdigest().upper()
        if digest != entry["sha256"]:
            entry.setdefault("optimization", {
                "sourceSha256": originals[image_path]["sha256"],
                "sourcePixelWidth": originals[image_path]["width"],
                "sourcePixelHeight": originals[image_path]["height"],
            })
            entry["optimization"].update({"method": "Lanczos resize and optimized RGBA PNG", "maxPixels": limit})
            entry["sha256"] = digest
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def package_application_icon(directory):
    # Keep all standard and Retina sizes; share the same encoded frame between containers.
    with Image.open(directory / "app.png") as source:
        source = source.convert("RGBA")
        frames = {size: png_bytes(source.resize((size, size), Image.Resampling.LANCZOS))
                  for size in (16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024)}
    for size, frame in frames.items():
        (directory / f"app-{size}.png").write_bytes(frame)
    sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
    entries, offset = [], 6 + 16 * len(sizes)
    for size in sizes:
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(frames[size]), offset))
        offset += len(frames[size])
    (directory / "app.ico").write_bytes(struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries) +
                                       b"".join(frames[size] for size in sizes))
    types = {"icp4": 16, "icp5": 32, "icp6": 64, "ic07": 128, "ic08": 256, "ic09": 512,
             "ic10": 1024, "ic11": 32, "ic12": 64, "ic13": 256, "ic14": 512}
    chunks = b"".join(kind.encode("ascii") + struct.pack(">I", len(frames[size]) + 8) + frames[size]
                      for kind, size in types.items())
    (directory / "app.icns").write_bytes(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)
    for size in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            suffix = "@2x" if scale == 2 else ""
            (directory / "AvaMedia.iconset" / f"icon_{size}x{size}{suffix}.png").write_bytes(frames[size * scale])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--icon-size", type=int, default=256, help="Feature icons; the UI decoder uses at most 256 px.")
    args = parser.parse_args()
    if args.icon_size < 256:
        parser.error("Feature icons need at least 256 pixels for the existing high-DPI decoder.")
    paths = sorted([*ASSETS.rglob("*.png"), *(ROOT / "docs/assets").rglob("*.png")])
    originals = {}
    for path in paths:
        data = path.read_bytes()
        with Image.open(path) as image:
            originals[path] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest().upper(),
                               "width": image.width, "height": image.height}
            if image.is_animated:
                continue
            limit = args.icon_size if "FeatureIcons" in path.parts else 1024 if path.name == "app.png" else None
            resized = limit is not None and max(image.size) > limit
            if resized:
                image = image.convert("RGBA")
                image.thumbnail((limit, limit), Image.Resampling.LANCZOS)
            write_smaller(path, png_bytes(image), resized)
    package_application_icon(ASSETS / "AppIcon/v2")
    for family in ("v2", "macos9"):
        update_manifest(ASSETS / f"FeatureIcons/{family}/manifest.json", originals, args.icon_size)
    update_manifest(ASSETS / "AppIcon/v2/manifest.json", originals, 1024)
    before = sum(item["bytes"] for item in originals.values())
    after = sum(path.stat().st_size for path in paths)
    print(f"PNG assets: {len(paths)} files, {before / 1e6:.2f} MB -> {after / 1e6:.2f} MB")


if __name__ == "__main__":
    main()
