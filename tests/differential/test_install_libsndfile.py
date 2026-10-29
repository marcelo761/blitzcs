import io
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
import warnings
import zipfile
from unittest.mock import patch

from tools.install_libsndfile import TARGETS, THIRD_PARTY_NOTICES_STATUS, extract_runtime, install_target


def fake_pe(machine=0x8664):
    data = bytearray(0x100)
    data[0x3C:0x40] = (0x80).to_bytes(4, "little")
    data[0x80:0x84] = b"PE\0\0"
    data[0x84:0x86] = machine.to_bytes(2, "little")
    return bytes(data)


class InstallLibsndfileTests(unittest.TestCase):
    def make_zip(self, entries):
        stream = io.BytesIO()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                for name, content in entries:
                    archive.writestr(name, content)
        return stream.getvalue()

    def test_extracts_only_pinned_runtime_and_checks_architecture(self):
        dll_member = "libsndfile-1.2.2-win64/bin/sndfile.dll"
        payload = self.make_zip([
            ("unrelated.txt", b"ignored"),
            (dll_member, fake_pe()),
            ("../../outside.dll", b"never extracted"),
        ])
        self.assertEqual(fake_pe(), extract_runtime(payload, "win-x64"))

    def test_rejects_wrong_architecture(self):
        dll_member = "libsndfile-1.2.2-win64/bin/sndfile.dll"
        with self.assertRaisesRegex(ValueError, "architecture"):
            extract_runtime(self.make_zip([(dll_member, fake_pe(0x14C))]), "win-x64")

    def test_rejects_duplicate_runtime_entry(self):
        dll_member = "libsndfile-1.2.2-win64/bin/sndfile.dll"
        with self.assertRaisesRegex(ValueError, "exactly one"):
            extract_runtime(self.make_zip([(dll_member, fake_pe()), (dll_member, fake_pe())]), "win-x64")

    def test_extracts_x86_runtime_for_x86_target(self):
        dll_member = "libsndfile-1.2.2-win32/bin/sndfile.dll"
        payload = self.make_zip([(dll_member, fake_pe(0x14C))])
        self.assertEqual(fake_pe(0x14C), extract_runtime(payload, "win-x86"))

    def test_install_manifest_marks_codec_notice_inventory_incomplete(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch(
                "tools.install_libsndfile.download",
                side_effect=[b"archive", b"license", b"alac-license", b"opus-license"],
            ), \
                    patch("tools.install_libsndfile.extract_runtime", return_value=fake_pe()):
                result = install_target(root / "out", root / "cache", "win-x64")

            installed = root / "out" / "win-x64"
            manifest = json.loads((installed / "manifest.json").read_text(encoding="utf-8"))
            self.assertEqual(b"license", (installed / "COPYING").read_bytes())
            self.assertEqual(b"alac-license", (installed / "ALAC-LICENSE.txt").read_bytes())
            self.assertEqual(b"opus-license", (installed / "OPUS-COPYING.txt").read_bytes())
            self.assertEqual(THIRD_PARTY_NOTICES_STATUS, result["thirdPartyNoticesStatus"])
            self.assertEqual("incomplete", manifest["thirdPartyNoticesStatus"])
            self.assertIn("embedded codec", manifest["thirdPartyNoticesNote"])
            self.assertEqual(
                ["Apache-2.0", "BSD-3-Clause"],
                [item["license"] for item in manifest["thirdPartyLicenses"]],
            )


if __name__ == "__main__":
    unittest.main()
