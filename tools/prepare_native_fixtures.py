#!/usr/bin/env python3
"""Prepare the dedicated M12 native file fixture directory without overwrites."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys


LARGE_SIZE = 0x80000003
DATA = b"1234567"
EXPECTED_NAMES = {"Data.bin", "empty.bin", "large.bin", "sparse.bin", "subdir"}


class FixtureError(Exception):
    pass


def make_sparse(path: Path, size: int) -> None:
    with path.open("xb") as stream:
        stream.seek(size - 1)
        stream.write(b"\0")


def validate_sparse(path: Path) -> None:
    stat = path.stat()
    if not path.is_file() or stat.st_size != LARGE_SIZE:
        raise FixtureError(f"{path} must be a regular file of {LARGE_SIZE} bytes")
    allocated_blocks = getattr(stat, "st_blocks", None)
    if allocated_blocks is not None and allocated_blocks * 512 >= LARGE_SIZE // 2:
        raise FixtureError(f"{path} is not sparse enough to be a fixture asset")


def validate_output(output: Path) -> None:
    if output.is_symlink() or not output.is_dir():
        raise FixtureError(f"output must be a dedicated directory: {output}")
    names = {entry.name for entry in output.iterdir()}
    if names != EXPECTED_NAMES:
        raise FixtureError(
            f"{output} must be empty or contain exactly {', '.join(sorted(EXPECTED_NAMES))}; "
            f"found {', '.join(sorted(names)) or '(empty)'}"
        )

    data = output / "Data.bin"
    empty = output / "empty.bin"
    subdir = output / "subdir"
    if not data.is_file() or data.read_bytes() != DATA:
        raise FixtureError(f"{data} must contain the seven bytes {DATA!r}")
    if not empty.is_file() or empty.stat().st_size != 0:
        raise FixtureError(f"{empty} must be an empty regular file")
    if not subdir.is_dir() or any(subdir.iterdir()):
        raise FixtureError(f"{subdir} must be an empty directory")
    validate_sparse(output / "large.bin")
    validate_sparse(output / "sparse.bin")


def prepare(output: Path) -> bool:
    """Create fixture assets; return False when an exact prepared set exists."""
    output = output.absolute()
    if output.is_symlink():
        raise FixtureError(f"refusing symlink output directory: {output}")
    if output.exists() and not output.is_dir():
        raise FixtureError(f"output is not a directory: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    if not output.exists():
        output.mkdir()

    existing = list(output.iterdir())
    if existing:
        validate_output(output)
        return False

    try:
        with (output / "Data.bin").open("xb") as stream:
            stream.write(DATA)
        (output / "empty.bin").touch(exist_ok=False)
        (output / "subdir").mkdir()
        make_sparse(output / "large.bin", LARGE_SIZE)
        make_sparse(output / "sparse.bin", LARGE_SIZE)
        validate_output(output)
    except Exception:
        # Keep any partial result visible; a rerun refuses it instead of
        # silently replacing files whose provenance is uncertain.
        raise
    return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "output",
        nargs="?",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "artifacts" / "m12-assets",
        help="dedicated output directory (default: artifacts/m12-assets)",
    )
    args = parser.parse_args(argv)
    try:
        created = prepare(args.output)
    except (FixtureError, OSError) as exc:
        print(f"native fixture preparation failed: {exc}", file=sys.stderr)
        return 2

    status = "created" if created else "validated existing"
    print(f"{status} M12 native file assets at {args.output.absolute()} ({LARGE_SIZE} bytes each)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
