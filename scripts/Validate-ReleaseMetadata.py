#!/usr/bin/env python3
"""Validate the version and its committed release notes before building packages."""

import argparse
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", help="Expected release version or v-prefixed tag")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version", "").strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        parser.error("Directory.Build.props must contain a semantic release version.")
    if args.version and args.version.removeprefix("v") != version:
        parser.error(f"Release tag {args.version} does not match source version {version}.")
    path = root / "docs" / "releases" / f"{version}.md"
    if not path.is_file():
        parser.error(f"Missing release notes: {path.relative_to(root)}. Commit them before tagging.")
    lines = path.read_text(encoding="utf-8-sig").strip().splitlines()
    if not lines or lines[0].strip() != f"# AvaMedia v{version}" or not any(line.strip() for line in lines[1:]):
        parser.error(f"{path.relative_to(root)} must have the matching version heading and release content.")
    print(f"Release metadata ready: v{version}, {path.relative_to(root)}")


if __name__ == "__main__":
    main()
