#!/usr/bin/env python3
"""Checks the packages `dotnet pack -c Release` produced before they are published.

Usage: tools/check-packages.py <directory holding the .nupkg files> <expected version>

Each package must exist at the expected version and carry README.md, LICENSE-MIT, LICENSE-APACHE and
THIRD-PARTY-NOTICES.md. The engine must have no dependencies at all.
"""
import re
import sys
import zipfile
from pathlib import Path

PREFIX = "RecursiveAI"
PACKAGES = [f"{PREFIX}.OneEcma", f"{PREFIX}.OneEcma.Generators", f"{PREFIX}.OneEcma.Cli"]
FILES = ["README.md", "LICENSE-MIT", "LICENSE-APACHE", "THIRD-PARTY-NOTICES.md"]


def main() -> int:
    folder, version = Path(sys.argv[1]), sys.argv[2]
    problems = []
    for package in PACKAGES:
        nupkg = folder / f"{package}.{version}.nupkg"
        if not nupkg.exists():
            problems.append(f"{nupkg.name} is missing")
            continue
        with zipfile.ZipFile(nupkg) as z:
            names = z.namelist()
            nuspec = z.read(next(n for n in names if n.endswith(".nuspec"))).decode("utf-8")
        for f in FILES:
            if f not in names:
                problems.append(f"{nupkg.name}: no {f}")
        if f"<version>{version}</version>" not in nuspec:
            problems.append(f"{nupkg.name}: nuspec version is not {version}")
        if "MIT OR Apache-2.0" not in nuspec:
            problems.append(f"{nupkg.name}: licence expression missing")
        if package == PACKAGES[0] and "<dependency " in nuspec:
            problems.append(f"{nupkg.name}: the engine must have no dependencies")
        print(f"{nupkg.name}: {len(names)} entries")
    for p in problems:
        print("FAIL:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
