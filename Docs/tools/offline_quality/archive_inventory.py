#!/usr/bin/env python3
"""Read-only ZIP metadata inventory; never extracts or reads member payloads."""
from __future__ import annotations

import argparse
import hashlib
import math
import re
import stat
import sys
import zipfile
from pathlib import Path

DEFAULT_LIMITS = {
    # Pilot-sized intake, not a general archive security boundary.
    "max_archive_bytes": 150 * 1024**2,
    "max_entries": 20_000,
    "max_total_uncompressed": 2 * 1024**3,
    "max_file_uncompressed": 512 * 1024**2,
    "max_compression_ratio": 200.0,
}
SUPPORTED_COMPRESSION = {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}
_DEVICE_NAME = re.compile(r"^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\..*)?$", re.IGNORECASE)


class InventoryError(ValueError):
    """Archive rejected by the metadata-inventory policy."""


def _validate_limits(limits: dict) -> None:
    for key in ("max_archive_bytes", "max_entries", "max_total_uncompressed", "max_file_uncompressed"):
        value = limits[key]
        if isinstance(value, bool) or not isinstance(value, int):
            raise InventoryError(f"{key} must be an integer")
        minimum = 1 if key in ("max_archive_bytes", "max_entries") else 0
        if value < minimum:
            raise InventoryError(f"{key} must be >= {minimum}")
    ratio = limits["max_compression_ratio"]
    if isinstance(ratio, bool) or not isinstance(ratio, (int, float)):
        raise InventoryError("max_compression_ratio must be a finite number")
    try:
        finite_ratio = math.isfinite(ratio)
    except OverflowError as exc:
        raise InventoryError("max_compression_ratio must be finite and > 0") from exc
    if not finite_ratio or ratio <= 0:
        raise InventoryError("max_compression_ratio must be finite and > 0")


def _safe_name(name: str) -> tuple[str, bool]:
    if not name or "\\" in name or "\x00" in name:
        raise InventoryError(f"unsafe member path: {name!r}")
    is_dir = name.endswith("/")
    if name.startswith("/"):
        raise InventoryError(f"absolute member path: {name!r}")
    parts = name.split("/")
    if is_dir:
        parts = parts[:-1]
    if not parts or any(part in ("", ".", "..") for part in parts):
        raise InventoryError(f"empty/dot/traversal member path: {name!r}")
    for part in parts:
        if ":" in part:
            raise InventoryError(f"Windows drive/ADS path component: {name!r}")
        if part.endswith((".", " ")):
            raise InventoryError(f"Windows trailing dot/space component: {name!r}")
        if _DEVICE_NAME.fullmatch(part):
            raise InventoryError(f"Windows reserved device name: {name!r}")
    return "/".join(parts), is_dir


def _check_tree_path(key: str, is_dir: bool, seen: dict[str, tuple[str, bool]], display: str) -> None:
    if key in seen:
        raise InventoryError(f"duplicate normalized path (case-insensitive): {display!r}")
    components = key.split("/")
    for index in range(1, len(components)):
        ancestor = "/".join(components[:index])
        if ancestor in seen and not seen[ancestor][1]:
            raise InventoryError(f"file/directory prefix conflict: {display!r}")
    if not is_dir:
        prefix = key + "/"
        if any(existing.startswith(prefix) for existing in seen):
            raise InventoryError(f"file/directory prefix conflict: {display!r}")
    seen[key] = (display, is_dir)


def _inspect_zip_archive(archive: zipfile.ZipFile, *, source_name: str, archive_size: int, limits: dict) -> dict:
    infos = archive.infolist()
    # This check is necessarily after stdlib central-directory parsing, not a memory sandbox.
    if len(infos) > limits["max_entries"]:
        raise InventoryError(f"entry count {len(infos)} exceeds limit {limits['max_entries']}")
    seen: dict[str, tuple[str, bool]] = {}
    total = 0
    members = []
    for info in infos:
        name, is_dir = _safe_name(info.filename)
        _check_tree_path(name.casefold(), is_dir, seen, info.filename)
        if info.flag_bits & 0x1:
            raise InventoryError(f"encrypted member: {info.filename!r}")
        if info.compress_type not in SUPPORTED_COMPRESSION:
            raise InventoryError(f"unsupported compression method {info.compress_type}: {info.filename!r}")
        mode = (info.external_attr >> 16) & 0xFFFF
        if stat.S_ISLNK(mode):
            raise InventoryError(f"symlink member: {info.filename!r}")
        if info.file_size < 0 or info.file_size > limits["max_file_uncompressed"]:
            raise InventoryError(f"member size exceeds limit: {info.filename!r} ({info.file_size})")
        total += info.file_size
        if total > limits["max_total_uncompressed"]:
            raise InventoryError(f"total expanded size exceeds limit {limits['max_total_uncompressed']}")
        ratio = info.file_size / max(info.compress_size, 1)
        if ratio > limits["max_compression_ratio"]:
            raise InventoryError(f"compression ratio {ratio:.1f} exceeds limit: {info.filename!r}")
        members.append({"path": name, "size": info.file_size,
                        "compressed_size": info.compress_size,
                        "compression": info.compress_type, "crc32_metadata": f"{info.CRC:08x}"})
    return {"archive": source_name, "archive_size": archive_size,
            "member_count": len(members), "total_uncompressed_size": total,
            "members": members, "mode": "metadata_inventory_only",
            "extraction_performed": False, "member_payloads_read": False}


def inspect_zip_bytes(data: bytes, *, source_name: str = "<memory>", limits: dict | None = None) -> dict:
    """Inventory one immutable caller-provided ZIP snapshot without extracting members."""
    import io
    configured = {**DEFAULT_LIMITS, **(limits or {})}
    _validate_limits(configured)
    if not isinstance(data, bytes):
        raise InventoryError("snapshot must be bytes")
    if len(data) > configured["max_archive_bytes"]:
        raise InventoryError(f"archive size {len(data)} exceeds limit {configured['max_archive_bytes']}")
    try:
        with zipfile.ZipFile(io.BytesIO(data), "r") as archive:
            return _inspect_zip_archive(archive, source_name=source_name,
                                        archive_size=len(data), limits=configured)
    except InventoryError:
        raise
    except (zipfile.BadZipFile, OSError, RuntimeError, EOFError, ValueError) as exc:
        raise InventoryError(f"cannot inspect ZIP snapshot metadata: {exc}") from exc


def inspect_zip(path: Path, *, limits: dict | None = None, hash_archive: bool = False) -> dict:
    configured = {**DEFAULT_LIMITS, **(limits or {})}
    _validate_limits(configured)
    path = Path(path)
    try:
        # Early coarse bound, before ZipFile parses the central directory. This stat is
        # not an atomic guarantee against concurrent replacement/growth (TOCTOU).
        archive_size = path.stat().st_size
        if archive_size > configured["max_archive_bytes"]:
            raise InventoryError(f"archive size {archive_size} exceeds limit {configured['max_archive_bytes']}")
        with zipfile.ZipFile(path, "r") as archive:
            result = _inspect_zip_archive(archive, source_name=str(path),
                                          archive_size=archive_size, limits=configured)
    except InventoryError:
        raise
    except (zipfile.BadZipFile, OSError, RuntimeError, EOFError, ValueError) as exc:
        raise InventoryError(f"cannot inspect ZIP metadata: {exc}") from exc
    if hash_archive:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        result["archive_sha256"] = digest.hexdigest()
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path, help="ZIP path (read-only; never extracted)")
    parser.add_argument("--json", action="store_true", help="emit JSON inventory")
    parser.add_argument("--sha256", action="store_true", help="hash the archive file itself")
    parser.add_argument("--max-archive-bytes", type=int, default=DEFAULT_LIMITS["max_archive_bytes"])
    parser.add_argument("--max-entries", type=int, default=DEFAULT_LIMITS["max_entries"])
    parser.add_argument("--max-total-bytes", type=int, default=DEFAULT_LIMITS["max_total_uncompressed"])
    parser.add_argument("--max-file-bytes", type=int, default=DEFAULT_LIMITS["max_file_uncompressed"])
    parser.add_argument("--max-ratio", type=float, default=DEFAULT_LIMITS["max_compression_ratio"])
    args = parser.parse_args(argv)
    limits = {"max_archive_bytes": args.max_archive_bytes, "max_entries": args.max_entries,
              "max_total_uncompressed": args.max_total_bytes,
              "max_file_uncompressed": args.max_file_bytes, "max_compression_ratio": args.max_ratio}
    try:
        result = inspect_zip(args.archive, limits=limits, hash_archive=args.sha256)
    except InventoryError as exc:
        print(f"archive rejected: {exc}", file=sys.stderr)
        return 2
    if args.json:
        import json
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(f"{result['archive']}: {result['member_count']} entries, {result['total_uncompressed_size']} metadata bytes (not extracted)")
        for member in result["members"]:
            print(f"{member['size']:>10}  {member['path']}")
        if "archive_sha256" in result:
            print(f"SHA-256 {result['archive_sha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
