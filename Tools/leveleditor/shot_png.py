#!/usr/bin/env python3
"""The only PNG code the shot tool needs: size check + provenance text chunks.

The browser produces the pixels (canvas.toDataURL), so no decoder is required.  A
``tEXt`` chunk is inserted before ``IEND`` so a downloaded image still says what it
is (level, view, UNLICENSED reference) even when separated from its JSON line.
"""

from __future__ import annotations

import struct
import zlib

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def _chunks(data):
    offset = len(PNG_SIGNATURE)
    while offset + 8 <= len(data):
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        kind = data[offset + 4:offset + 8]
        body = data[offset + 8:offset + 8 + length]
        crc = data[offset + 8 + length:offset + 12 + length]
        yield kind, body, crc
        offset += 12 + length
        if kind == b"IEND":
            break


def png_size(data):
    """(width, height) of a PNG, or None when the bytes are not a PNG."""
    if not isinstance(data, (bytes, bytearray)) or not data.startswith(PNG_SIGNATURE):
        return None
    for kind, body, _crc in _chunks(data):
        if kind == b"IHDR" and len(body) >= 8:
            width, height = struct.unpack(">II", body[:8])
            if width <= 0 or height <= 0 or width > 100_000 or height > 100_000:
                return None
            return int(width), int(height)
        return None
    return None


def _text_chunk(keyword, text):
    key = str(keyword).encode("latin-1", "replace")[:79]
    value = str(text).encode("latin-1", "replace")
    body = key + b"\x00" + value
    return (struct.pack(">I", len(body)) + b"tEXt" + body
            + struct.pack(">I", zlib.crc32(b"tEXt" + body) & 0xFFFFFFFF))


def add_text(data, entries):
    """Insert tEXt chunks before IEND, preserving everything else byte-for-byte."""
    if png_size(data) is None:
        raise ValueError("not a PNG")
    if not entries:
        return data
    extra = b"".join(_text_chunk(key, value) for key, value in entries.items()
                     if value is not None)
    position = data.rfind(b"IEND") - 4
    if position < len(PNG_SIGNATURE):
        raise ValueError("PNG has no IEND chunk")
    return data[:position] + extra + data[position:]


def chunks(data):
    """Read-only helper for tests: [(kind, body)] without the CRCs."""
    return [(kind.decode("latin-1"), body) for kind, body, _crc in _chunks(data)]
