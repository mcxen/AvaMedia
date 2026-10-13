#!/usr/bin/env python3
"""Rebuild desktop OFL font assets with fonttools 4.65.0 (pip install fonttools==4.65.0).

Sources are verified by SHA256; --source-dir reuses already downloaded originals.
No font downloads or subsetting occur during the application build.
"""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/AvaMedia.Desktop/Assets/Fonts"


def characters():
    result = set(range(0x20, 0x250)) | set(range(0x2000, 0x2070))
    result |= set(range(0x3000, 0x3040)) | set(range(0xFF00, 0xFFF0))
    for first in range(0xA1, 0xF8):
        for second in range(0xA1, 0xFF):
            try:
                result.add(ord(bytes([first, second]).decode("gb2312")))
            except UnicodeError:
                pass
    # Include simplified/traditional UI strings, without pulling in emoji fonts.
    for path in (ROOT / "src").rglob("*"):
        if path.suffix not in (".cs", ".axaml", ".json") or {"bin", "obj"} & set(path.parts):
            continue
        result.update(ord(c) for c in path.read_text(encoding="utf-8")
                      if 0x3400 <= ord(c) <= 0x9FFF)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path, required=True)
    args = parser.parse_args()
    sources = json.loads((ASSETS / "sources.json").read_text())
    args.source_dir.mkdir(parents=True, exist_ok=True)
    retained = characters()
    summary = []
    for family in sources:
        source = args.source_dir / family["file"]
        if not source.exists():
            urllib.request.urlretrieve(family["url"], source)
        if hashlib.sha256(source.read_bytes()).hexdigest() != family["sha256"]:
            raise ValueError(f"Upstream font changed: {source}; review provenance before updating")
        destination = ASSETS / family["family"].replace(" ", "")
        destination.mkdir(parents=True, exist_ok=True)
        for weight, style in family["weights"].items():
            font = TTFont(source, recalcTimestamp=False)
            axes = {"wght": int(weight)}
            if family["family"] == "Inter":
                axes["opsz"] = 14  # Text optical size for 12–18 DIP interface text.
            font = instantiateVariableFont(font, axes, inplace=True)
            options = subset.Options()
            options.layout_features = ["*"]  # Keep tnum and shaping features.
            options.name_IDs = ["*"]
            options.name_legacy = True
            options.name_languages = ["*"]
            worker = subset.Subsetter(options=options)
            worker.populate(unicodes=retained)
            worker.subset(font)
            # Give each static face a distinct full/PostScript name and correct style.
            for record in font["name"].names:
                value = {1: family["family"], 2: style,
                         4: family["family"] + " " + style,
                         6: family["family"].replace(" ", "") + "-" + style,
                         16: family["family"], 17: style}.get(record.nameID)
                if value is not None:
                    record.string = value.encode(record.getEncoding())
            target = destination / (family["family"].replace(" ", "") + "-" + style + ".ttf")
            font.save(target)
            summary.append({"file": str(target.relative_to(ASSETS)), "bytes": target.stat().st_size,
                            "codepoints": len(font.getBestCmap()),
                            "sha256": hashlib.sha256(target.read_bytes()).hexdigest()})
            font.close()
    (ASSETS / "subsets.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
