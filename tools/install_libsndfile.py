#!/usr/bin/env python3
"""Install the pinned official Windows x86 and x64 libsndfile runtimes."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import tempfile
import urllib.request
import zipfile
from io import BytesIO
from pathlib import Path


VERSION = "1.2.2"
COPYING_URL = "https://raw.githubusercontent.com/libsndfile/libsndfile/1.2.2/COPYING"
COPYING_SHA256 = "ad01ea5cd2755f6048383c8d54c88459cd6fcb17757c5c8892f8c5ea060f6140"
ALAC_LICENSE_URL = "https://raw.githubusercontent.com/libsndfile/libsndfile/1.2.2/src/ALAC/LICENSE"
ALAC_LICENSE_SHA256 = "232efbd83786e157286b0b96c68ebb673541b69b33036287bf1e5ec5067a49d9"
OPUS_COPYING_URL = "https://raw.githubusercontent.com/xiph/opus/v1.4/COPYING"
OPUS_COPYING_SHA256 = "8338ce8d922bb4416ce3dd1e5680173332435e3f0755007ac7801ccd674fe682"
MAX_ARCHIVE_SIZE = 4 * 1024 * 1024
MAX_DLL_SIZE = 4 * 1024 * 1024
MAX_COPYING_SIZE = 64 * 1024
THIRD_PARTY_NOTICES_STATUS = "incomplete"
THIRD_PARTY_NOTICES_NOTE = (
    "Known ALAC and Opus notices are included alongside libsndfile. The published DLLs contain "
    "other embedded codec code, but the release archives do not provide a complete codec "
    "version/license inventory; see docs/install-sdl3.md#codec-notice-status."
)

TARGETS = {
    "win-x64": {
        "archive": "libsndfile-1.2.2-win64.zip",
        "sha256": "2173935c0c1ed13cf627951d34483f9d405ead2eb473190461c42ba220643a3f",
        "machine": 0x8664,
    },
    "win-x86": {
        "archive": "libsndfile-1.2.2-win32.zip",
        "sha256": "2fafc8e06f84d8e42b9665b665a5084364454944dee81f47d67f317391f6c7f2",
        "machine": 0x014C,
    },
}


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def validate_pe(dll: bytes, target: str) -> None:
    if len(dll) < 0x40:
        raise ValueError("sndfile.dll is too short to contain a PE header")
    offset = int.from_bytes(dll[0x3C:0x40], "little")
    if offset < 0x40 or offset + 6 > len(dll) or dll[offset:offset + 4] != b"PE\0\0":
        raise ValueError("sndfile.dll has an invalid PE header")
    if int.from_bytes(dll[offset + 4:offset + 6], "little") != TARGETS[target]["machine"]:
        raise ValueError(f"sndfile.dll PE architecture does not match {target}")


def extract_runtime(payload: bytes, target: str) -> bytes:
    if target not in TARGETS:
        raise ValueError(f"unsupported libsndfile target: {target}")
    if len(payload) > MAX_ARCHIVE_SIZE:
        raise ValueError("libsndfile ZIP exceeds the safety limit")
    try:
        archive = zipfile.ZipFile(BytesIO(payload))
    except zipfile.BadZipFile as exc:
        raise ValueError("invalid libsndfile ZIP archive") from exc
    with archive:
        dll_member = f"libsndfile-{VERSION}-win{'64' if target == 'win-x64' else '32'}/bin/sndfile.dll"
        selected = [entry for entry in archive.infolist() if entry.filename == dll_member]
        if len(selected) != 1:
            raise ValueError(f"archive must contain exactly one {dll_member}")
        entry = selected[0]
        if entry.is_dir() or entry.flag_bits & 1 or not 0 < entry.file_size <= MAX_DLL_SIZE:
            raise ValueError("invalid or unsafe sndfile.dll ZIP entry")
        with archive.open(entry, "r") as source:
            dll = source.read(MAX_DLL_SIZE + 1)
        if len(dll) != entry.file_size or len(dll) > MAX_DLL_SIZE:
            raise ValueError("invalid sndfile.dll uncompressed size")
    validate_pe(dll, target)
    return dll


def download(url: str, destination: Path, maximum: int, expected_sha256: str) -> bytes:
    if destination.exists():
        data = destination.read_bytes()
        if len(data) <= maximum and sha256_bytes(data) == expected_sha256:
            return data
        destination.unlink()
    request = urllib.request.Request(url, headers={"User-Agent": "BlitzCS-native-runtime-installer"})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read(maximum + 1)
    if len(data) > maximum or sha256_bytes(data) != expected_sha256:
        raise ValueError(f"download verification failed for {url}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix="libsndfile-", dir=destination.parent)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(data)
        Path(temporary).replace(destination)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return data


def install_target(output_dir: Path, cache_dir: Path, target: str) -> dict[str, object]:
    asset = TARGETS[target]
    archive_url = f"https://github.com/libsndfile/libsndfile/releases/download/{VERSION}/{asset['archive']}"
    archive = download(archive_url, cache_dir / asset["archive"], MAX_ARCHIVE_SIZE, asset["sha256"])
    copying = download(COPYING_URL, cache_dir / f"libsndfile-{VERSION}-COPYING", MAX_COPYING_SIZE, COPYING_SHA256)
    alac_license = download(
        ALAC_LICENSE_URL,
        cache_dir / f"libsndfile-{VERSION}-ALAC-LICENSE",
        MAX_COPYING_SIZE,
        ALAC_LICENSE_SHA256,
    )
    opus_copying = download(
        OPUS_COPYING_URL,
        cache_dir / "opus-1.4-COPYING",
        MAX_COPYING_SIZE,
        OPUS_COPYING_SHA256,
    )
    dll = extract_runtime(archive, target)
    destination = output_dir / target
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "sndfile.dll").write_bytes(dll)
    (destination / "COPYING").write_bytes(copying)
    (destination / "ALAC-LICENSE.txt").write_bytes(alac_license)
    (destination / "OPUS-COPYING.txt").write_bytes(opus_copying)
    manifest = {
        "name": "libsndfile",
        "version": VERSION,
        "target": target,
        "source": archive_url,
        "archiveSha256": asset["sha256"],
        "runtimeFile": "sndfile.dll",
        "runtimeSha256": sha256_bytes(dll),
        "licenseSource": COPYING_URL,
        "license": "LGPL-2.1-or-later",
        "licenseFile": "COPYING",
        "licenseSha256": sha256_bytes(copying),
        "thirdPartyLicenses": [
            {
                "component": "ALAC (libsndfile bundled source)",
                "license": "Apache-2.0",
                "licenseSource": ALAC_LICENSE_URL,
                "licenseFile": "ALAC-LICENSE.txt",
                "licenseSha256": sha256_bytes(alac_license),
            },
            {
                "component": "Opus",
                "version": "1.4",
                "license": "BSD-3-Clause",
                "licenseSource": OPUS_COPYING_URL,
                "licenseFile": "OPUS-COPYING.txt",
                "licenseSha256": sha256_bytes(opus_copying),
            },
        ],
        "thirdPartyNoticesStatus": THIRD_PARTY_NOTICES_STATUS,
        "thirdPartyNoticesNote": THIRD_PARTY_NOTICES_NOTE,
    }
    (destination / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def install(output_dir: Path, cache_dir: Path, targets: list[str]) -> list[dict[str, object]]:
    return [install_target(output_dir, cache_dir, target) for target in targets]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=Path("artifacts/native/libsndfile"))
    parser.add_argument("--cache-dir", type=Path, default=Path.home() / ".cache" / "blitzcs" / "libsndfile")
    parser.add_argument("--target", choices=(*TARGETS, "all"), default="all")
    args = parser.parse_args()
    targets = list(TARGETS) if args.target == "all" else [args.target]
    for result in install(args.output_dir, args.cache_dir, targets):
        print(f"{result['target']}: sndfile.dll SHA-256 {result['runtimeSha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
