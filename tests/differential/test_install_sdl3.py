import hashlib
import io
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path

from tools.install_sdl3 import ASSETS, extract_package, sha256_bytes, validate_archive


def fake_pe(machine):
    data = bytearray(0x100)
    data[0x3C:0x40] = (0x80).to_bytes(4, "little")
    data[0x80:0x84] = b"PE\0\0"
    data[0x84:0x86] = machine.to_bytes(2, "little")
    return bytes(data)


class InstallSdl3Tests(unittest.TestCase):
    def make_zip(self, entries):
        stream = io.BytesIO()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                for name, content in entries:
                    archive.writestr(name, content)
        return stream.getvalue()

    def test_extracts_only_expected_pe_and_license_files(self):
        payload = self.make_zip([
            ("SDL3.dll", fake_pe(0x8664)),
            ("LICENSE.txt", b"SDL zlib license"),
            ("../../outside.txt", b"must not be extracted"),
        ])
        with tempfile.TemporaryDirectory() as temp:
            dll, license_text = extract_package(payload, "win-x64")
            self.assertEqual(fake_pe(0x8664), dll)
            self.assertEqual(b"SDL zlib license", license_text)
            self.assertFalse((Path(temp).parent / "outside.txt").exists())

    def test_rejects_wrong_pe_architecture(self):
        payload = self.make_zip([("SDL3.dll", fake_pe(0x14C)), ("LICENSE.txt", b"license")])
        with self.assertRaisesRegex(ValueError, "architecture"):
            extract_package(payload, "win-x64")

    def test_rejects_duplicate_runtime_entry(self):
        payload = self.make_zip([
            ("SDL3.dll", fake_pe(0x8664)), ("SDL3.dll", fake_pe(0x8664)),
            ("LICENSE.txt", b"license"),
        ])
        with self.assertRaisesRegex(ValueError, "exactly one"):
            extract_package(payload, "win-x64")

    def test_validates_archive_digest_before_extraction(self):
        asset = ASSETS["win-x64"]
        with tempfile.TemporaryDirectory() as temp:
            archive = Path(temp) / "SDL3.zip"
            archive.write_bytes(b"incorrect download")
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                validate_archive(archive, asset)

    def test_sha256_helper_is_stable(self):
        self.assertEqual(hashlib.sha256(b"payload").hexdigest(), sha256_bytes(b"payload"))


if __name__ == "__main__":
    unittest.main()
