import json
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path
from unittest.mock import patch

from Docs.tools.offline_quality.archive_inventory import InventoryError, inspect_zip, main


class ArchiveInventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.path = self.root / "sample.zip"

    def tearDown(self):
        self.temp.cleanup()

    def make_zip(self, entries):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(self.path, "w") as archive:
                for name, data, attrs in entries:
                    info = zipfile.ZipInfo(name)
                    if attrs is not None:
                        info.external_attr = attrs
                    info.compress_type = zipfile.ZIP_DEFLATED
                    archive.writestr(info, data)
        return self.path

    def test_normal_inventory_metadata_only_and_hash(self):
        self.make_zip([("folder/item.txt", b"hello", None)])
        result = inspect_zip(self.path, hash_archive=True)
        self.assertEqual(result["member_count"], 1)
        self.assertEqual(result["mode"], "metadata_inventory_only")
        self.assertFalse(result["extraction_performed"])
        self.assertFalse(result["member_payloads_read"])
        self.assertEqual(result["members"][0]["path"], "folder/item.txt")
        self.assertIn("crc32_metadata", result["members"][0])
        self.assertNotIn("crc32", result["members"][0])
        self.assertEqual(len(result["archive_sha256"]), 64)

    def test_rejects_traversal_absolute_drive_ads_and_backslash(self):
        for name in ("../evil", "/absolute", "C:/evil", "C:evil", "folder\\..\\evil",
                     "file:stream", "dir/name:stream", "x:y/z"):
            with self.subTest(name=name):
                self.make_zip([(name, b"x", None)])
                with self.assertRaises(InventoryError):
                    inspect_zip(self.path)

    def test_rejects_windows_reserved_devices_case_insensitively(self):
        for name in ("CON", "con.txt", "PRN.log", "AUX", "NUL.dat", "COM1", "com9.txt",
                     "LPT1", "lpt9.csv", "COM¹", "LPT²", "folder/NUL.txt"):
            with self.subTest(name=name):
                self.make_zip([(name, b"x", None)])
                with self.assertRaisesRegex(InventoryError, "device name"):
                    inspect_zip(self.path)

    def test_rejects_trailing_dot_space_and_empty_or_dot_components(self):
        for name in ("name.", "name ", "dir./file", "dir /file", "a//b", "a/./b", ""):
            with self.subTest(name=name):
                self.make_zip([(name, b"x", None)])
                with self.assertRaises(InventoryError):
                    inspect_zip(self.path)

    def test_rejects_symlink(self):
        symlink_mode = (0o120777 << 16)
        self.make_zip([("link", b"target", symlink_mode)])
        with self.assertRaisesRegex(InventoryError, "symlink"):
            inspect_zip(self.path)

    def test_rejects_casefold_duplicates_and_file_directory_conflicts(self):
        for entries in (
            [("Folder/a", b"1", None), ("folder/A", b"2", None)],
            [("a", b"1", None), ("a/b", b"2", None)],
            [("a/b", b"1", None), ("a", b"2", None)],
            [("foo/", b"", None), ("FOO/", b"", None)],
        ):
            with self.subTest(entries=[e[0] for e in entries]):
                self.make_zip(entries)
                with self.assertRaises(InventoryError):
                    inspect_zip(self.path)

    def test_rejects_entry_total_file_and_ratio_limits(self):
        self.make_zip([("a", b"x" * 1000, None), ("b", b"x", None)])
        for limits in ({"max_entries": 1}, {"max_file_uncompressed": 500},
                       {"max_total_uncompressed": 100}, {"max_compression_ratio": 1.0}):
            with self.subTest(limits=limits), self.assertRaises(InventoryError):
                inspect_zip(self.path, limits=limits)

    def test_archive_byte_limit_precedes_zipfile_open(self):
        self.make_zip([("x", b"abc", None)])
        with patch("Docs.tools.offline_quality.archive_inventory.zipfile.ZipFile") as zip_open:
            with self.assertRaisesRegex(InventoryError, "archive size"):
                inspect_zip(self.path, limits={"max_archive_bytes": 1})
            zip_open.assert_not_called()

    def test_library_rejects_invalid_limit_types_and_nonfinite_values(self):
        self.make_zip([("x", b"x", None)])
        invalid = (
            {"max_archive_bytes": 1.5}, {"max_archive_bytes": True},
            {"max_entries": 1.0}, {"max_total_uncompressed": "10"},
            {"max_file_uncompressed": False}, {"max_compression_ratio": float("nan")},
            {"max_compression_ratio": float("inf")}, {"max_compression_ratio": -float("inf")},
            {"max_compression_ratio": 10**10000},
        )
        for limits in invalid:
            with self.subTest(limits=limits), self.assertRaises(InventoryError):
                inspect_zip(self.path, limits=limits)

    def test_rejects_encrypted_and_unknown_compression_metadata(self):
        self.make_zip([("encrypted.txt", b"x", None)])
        data = bytearray(self.path.read_bytes())
        local, central = data.find(b"PK\x03\x04"), data.find(b"PK\x01\x02")
        data[local + 6] |= 1
        data[central + 8] |= 1
        self.path.write_bytes(data)
        with self.assertRaisesRegex(InventoryError, "encrypted"):
            inspect_zip(self.path)
        self.make_zip([("odd", b"x", None)])
        data = bytearray(self.path.read_bytes())
        local, central = data.find(b"PK\x03\x04"), data.find(b"PK\x01\x02")
        data[local + 8:local + 10] = (99).to_bytes(2, "little")
        data[central + 10:central + 12] = (99).to_bytes(2, "little")
        self.path.write_bytes(data)
        with self.assertRaisesRegex(InventoryError, "unsupported compression"):
            inspect_zip(self.path)

    def test_malformed_truncated_and_non_zip_inputs_rejected(self):
        for contents in (b"not zip", b"PK\x03\x04", b"PK\x05\x06" + b"\x00" * 10):
            with self.subTest(contents=contents):
                self.path.write_bytes(contents)
                with self.assertRaises(InventoryError):
                    inspect_zip(self.path)

    def test_cli_json_and_archive_limit_option(self):
        self.make_zip([("x", b"abc", None)])
        from contextlib import redirect_stdout
        from io import StringIO
        output = StringIO()
        with redirect_stdout(output):
            self.assertEqual(main([str(self.path), "--json"]), 0)
        self.assertEqual(json.loads(output.getvalue())["member_count"], 1)
        error = StringIO()
        with patch("sys.stderr", error):
            self.assertEqual(main([str(self.path), "--max-archive-bytes", "1"]), 2)
        self.assertIn("archive size", error.getvalue())


if __name__ == "__main__":
    unittest.main()
