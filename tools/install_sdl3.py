#!/usr/bin/env python3
"""Install the official SDL 3 Windows runtime DLLs into ignored artifacts/."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import tempfile
import urllib.request
import zipfile
from dataclasses import dataclass
from io import BytesIO
from pathlib import Path


VERSION = "3.4.18"
MAX_ARCHIVE_SIZE = 24 * 1024 * 1024
MAX_DLL_SIZE = 16 * 1024 * 1024
MAX_LICENSE_SIZE = 1024 * 1024


@dataclass(frozen=True)
class Asset:
    rid: str
    url: str
    archive_sha256: str
    machine: int


ASSETS = {
    "win-x64": Asset(
        "win-x64",
        "https://github.com/libsdl-org/SDL/releases/download/release-3.4.18/SDL3-3.4.18-win32-x64.zip",
        "75c2c0fc74e7d1206aaedc22893a35887332f4f85f5c8b74fddc10d177985f2c",
        0x8664,
    ),
    "win-x86": Asset(
        "win-x86",
        "https://github.com/libsdl-org/SDL/releases/download/release-3.4.18/SDL3-3.4.18-win32-x86.zip",
        "99f2ee6e126d828c5e5b3d20201930f3d16a8c6e147d1de90a59f4ae6df138c9",
        0x014C,
    ),
}


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def validate_archive(path: Path, asset: Asset) -> bytes:
    size = path.stat().st_size
    if size > MAX_ARCHIVE_SIZE:
        raise ValueError(f"archive exceeds {MAX_ARCHIVE_SIZE} byte safety limit")
    payload = path.read_bytes()
    if sha256_bytes(payload) != asset.archive_sha256:
        raise ValueError(f"SHA-256 mismatch for {asset.rid} SDL archive")
    return payload


def _validate_pe(dll: bytes, rid: str) -> None:
    expected = ASSETS[rid].machine
    if len(dll) < 0x40:
        raise ValueError("SDL3.dll is too short to contain a PE header")
    pe_offset = int.from_bytes(dll[0x3C:0x40], "little")
    if pe_offset < 0x40 or pe_offset + 6 > len(dll) or dll[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise ValueError("SDL3.dll has an invalid PE header")
    machine = int.from_bytes(dll[pe_offset + 4:pe_offset + 6], "little")
    if machine != expected:
        label = "x64" if expected == 0x8664 else "x86"
        raise ValueError(f"SDL3.dll PE architecture does not match {rid} ({label})")


def extract_package(payload: bytes, rid: str) -> tuple[bytes, bytes]:
    if rid not in ASSETS:
        raise ValueError(f"unsupported SDL runtime target: {rid}")
    try:
        archive = zipfile.ZipFile(BytesIO(payload))
    except zipfile.BadZipFile as exc:
        raise ValueError("invalid SDL ZIP archive") from exc

    with archive:
        selected: dict[str, bytes] = {}
        for info in archive.infolist():
            # Never extract paths. Only read two exact root filenames.
            if info.filename not in ("SDL3.dll", "LICENSE.txt"):
                continue
            if info.is_dir() or info.flag_bits & 1:
                raise ValueError(f"unexpected ZIP entry type for {info.filename}")
            if info.filename in selected:
                raise ValueError(f"archive must contain exactly one {info.filename}")
            limit = MAX_DLL_SIZE if info.filename == "SDL3.dll" else MAX_LICENSE_SIZE
            if info.file_size <= 0 or info.file_size > limit:
                raise ValueError(f"unsafe size for {info.filename}")
            with archive.open(info, "r") as source:
                contents = source.read(limit + 1)
            if len(contents) != info.file_size or len(contents) > limit:
                raise ValueError(f"invalid uncompressed size for {info.filename}")
            selected[info.filename] = contents

    if set(selected) != {"SDL3.dll", "LICENSE.txt"}:
        raise ValueError("archive must contain exactly one SDL3.dll and LICENSE.txt")
    dll = selected["SDL3.dll"]
    _validate_pe(dll, rid)
    return dll, selected["LICENSE.txt"]


def _download(asset: Asset, cache_dir: Path) -> Path:
    cache_dir.mkdir(parents=True, exist_ok=True)
    archive_path = cache_dir / f"SDL-{VERSION}-{asset.rid}.zip"
    if archive_path.exists():
        try:
            validate_archive(archive_path, asset)
            return archive_path
        except ValueError:
            archive_path.unlink()

    request = urllib.request.Request(asset.url, headers={"User-Agent": "BlitzCS-SDL3-installer"})
    with urllib.request.urlopen(request, timeout=60) as response:
        payload = response.read(MAX_ARCHIVE_SIZE + 1)
    if len(payload) > MAX_ARCHIVE_SIZE:
        raise ValueError(f"download exceeds {MAX_ARCHIVE_SIZE} byte safety limit")
    if sha256_bytes(payload) != asset.archive_sha256:
        raise ValueError(f"SHA-256 mismatch for downloaded {asset.rid} SDL archive")
    cache_dir.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix="sdl3-", dir=cache_dir)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(payload)
        Path(temporary).replace(archive_path)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return archive_path


def install(output_dir: Path, cache_dir: Path, targets: list[str]) -> list[dict[str, str]]:
    results = []
    for rid in targets:
        asset = ASSETS[rid]
        archive_path = _download(asset, cache_dir)
        payload = validate_archive(archive_path, asset)
        dll, license_text = extract_package(payload, rid)
        destination = output_dir / rid
        destination.mkdir(parents=True, exist_ok=True)
        (destination / "SDL3.dll").write_bytes(dll)
        (destination / "LICENSE.txt").write_bytes(license_text)
        manifest = {
            "name": "SDL",
            "version": VERSION,
            "target": rid,
            "source": asset.url,
            "archiveSha256": asset.archive_sha256,
            "runtimeFile": "SDL3.dll",
            "runtimeSha256": sha256_bytes(dll),
            "licenseFile": "LICENSE.txt",
            "licenseSha256": sha256_bytes(license_text),
            "license": "zlib",
        }
        (destination / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        results.append(manifest)
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=Path("artifacts/native/sdl3"))
    parser.add_argument(
        "--cache-dir",
        type=Path,
        default=Path.home() / ".cache" / "blitzcs" / "sdl3",
        help="download cache (defaults outside the repository)",
    )
    parser.add_argument("--target", choices=(*ASSETS, "all"), default="all")
    args = parser.parse_args()
    targets = list(ASSETS) if args.target == "all" else [args.target]
    for result in install(args.output_dir, args.cache_dir, targets):
        print(f"{result['target']}: SDL3.dll SHA-256 {result['runtimeSha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
