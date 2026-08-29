"""Small independent Triangle codec used only by tests and fixture builders.

Production archive and image work belongs to DeltaResourceTool.exe. Keeping
this implementation in the test tree lets the suite compare the native code
against a structurally different reference without shipping two backends.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass

RLE_FLAG = 0x80000000
STORED_FLAG = 0x40000000
SIZE_MASK = 0x3FFFFFFF
RING_SIZE = 4096
WINDOW = 18
THRESHOLD = 2
NAME_FIELD = 28
ENTRY_RECORD = 32
INDEX_FLAG = 0x80000000
OFFSET_MASK = 0x7FFFFFFF


def decompress_lzss(source: bytes, size: int) -> bytes:
    ring = bytearray(RING_SIZE)
    cursor = RING_SIZE - WINDOW
    out = bytearray()
    position = 0
    flags = 0
    while len(out) < size and position < len(source):
        flags >>= 1
        if not flags & 0x100:
            flags = source[position] | 0xFF00
            position += 1
        if flags & 1:
            byte = source[position]
            position += 1
            out.append(byte)
            ring[cursor] = byte
            cursor = (cursor + 1) & (RING_SIZE - 1)
            continue
        if position + 1 >= len(source):
            break
        low, high = source[position], source[position + 1]
        position += 2
        start = low | ((high & 0xF0) << 4)
        length = (high & 0x0F) + THRESHOLD
        for step in range(length + 1):
            byte = ring[(start + step) & (RING_SIZE - 1)]
            out.append(byte)
            ring[cursor] = byte
            cursor = (cursor + 1) & (RING_SIZE - 1)
    return bytes(out)


def compress_lzss(data: bytes) -> bytes:
    ring = bytearray(RING_SIZE)
    cursor = RING_SIZE - WINDOW
    out = bytearray()
    flags = 0
    flag_count = 0
    chunk = bytearray()
    position = 0
    longest = WINDOW - 1
    while position < len(data):
        best_length = 0
        best_start = 0
        limit = min(longest, len(data) - position)
        if limit > THRESHOLD:
            window = data[position : position + limit]
            for start in range(RING_SIZE):
                length = 0
                while length < limit:
                    index = (start + length) & (RING_SIZE - 1)
                    offset = (index - cursor) & (RING_SIZE - 1)
                    value = window[offset] if offset < length else ring[index]
                    if value != window[length]:
                        break
                    length += 1
                if length > best_length:
                    best_length, best_start = length, start
                    if best_length == limit:
                        break
        if best_length > THRESHOLD:
            chunk.append(best_start & 0xFF)
            chunk.append(
                ((best_start >> 4) & 0xF0)
                | ((best_length - THRESHOLD - 1) & 0x0F)
            )
            emitted = best_length
        else:
            flags |= 1 << flag_count
            chunk.append(data[position])
            emitted = 1
        for step in range(emitted):
            ring[cursor] = data[position + step]
            cursor = (cursor + 1) & (RING_SIZE - 1)
        position += emitted
        flag_count += 1
        if flag_count == 8:
            out.append(flags)
            out.extend(chunk)
            flags = 0
            flag_count = 0
            chunk = bytearray()
    if flag_count:
        out.append(flags)
        out.extend(chunk)
    return bytes(out)


def decompress_rle(source: bytes, size: int) -> bytes:
    out = bytearray()
    position = 0
    while len(out) < size and position < len(source):
        count = source[position]
        position += 1
        if count:
            out.extend(bytes([source[position]]) * count)
            position += 1
        else:
            literal = source[position]
            position += 1
            out.extend(source[position : position + literal])
            position += literal
    return bytes(out)


def compress_rle(data: bytes) -> bytes:
    out = bytearray()
    position = 0
    while position < len(data):
        run = 1
        while (
            run < 255
            and position + run < len(data)
            and data[position + run] == data[position]
        ):
            run += 1
        if run > 1:
            out.append(run)
            out.append(data[position])
            position += run
            continue
        start = position
        while (
            position < len(data)
            and position - start < 255
            and not (
                position + 1 < len(data)
                and data[position + 1] == data[position]
            )
        ):
            position += 1
        literal = data[start:position] or data[start : start + 1]
        if not literal:
            break
        out.append(0)
        out.append(len(literal))
        out.extend(literal)
        position = start + len(literal)
    return bytes(out)


def unpack_payload(blob: bytes) -> tuple[bytes, str]:
    if len(blob) < 8:
        return blob, "raw"
    packed, sized = struct.unpack_from("<II", blob, 0)
    if packed == 0 and sized == 0:
        return blob, "raw"
    body = blob[8 : 8 + packed] if packed else blob[8:]
    size = sized & SIZE_MASK
    if sized & RLE_FLAG:
        return decompress_rle(body, size), "rle"
    if sized & STORED_FLAG:
        return body[:size], "stored"
    return decompress_lzss(body, sized), "lzss"


def pack_payload(data: bytes, mode: str) -> bytes:
    if mode == "raw":
        return data
    if mode == "stored":
        body = data
        sized = STORED_FLAG | len(data)
    elif mode == "rle":
        body = compress_rle(data)
        sized = RLE_FLAG | len(data)
    elif mode == "lzss":
        body = compress_lzss(data)
        sized = len(data)
    else:
        raise ValueError(f"Unknown compression mode: {mode}")
    return struct.pack("<II", len(body), sized) + body


@dataclass
class CgfEntry:
    name: str
    flagged: bool
    offset: int
    size: int


def read_cgf_index(data: bytes) -> list[CgfEntry]:
    if len(data) < 4:
        raise ValueError("Not a Delta CGF archive")
    count = struct.unpack_from("<I", data, 0)[0]
    if count == 0 or 4 + count * ENTRY_RECORD > len(data):
        raise ValueError("Not a Delta CGF archive")
    raw: list[tuple[str, int, bool]] = []
    for index in range(count):
        record = data[4 + index * ENTRY_RECORD : 4 + (index + 1) * ENTRY_RECORD]
        name = record[:NAME_FIELD].split(b"\0")[0].decode("cp932", errors="replace")
        value = struct.unpack_from("<I", record, NAME_FIELD)[0]
        raw.append((name, value & OFFSET_MASK, bool(value & INDEX_FLAG)))
    entries: list[CgfEntry] = []
    for index, (name, offset, flagged) in enumerate(raw):
        end = raw[index + 1][1] if index + 1 < len(raw) else len(data)
        entries.append(CgfEntry(name, flagged, offset, end - offset))
    return entries


def wrap_bmp_bytes(bitmap: bytes) -> bytes:
    if bitmap[:2] != b"BM":
        raise ValueError("Expected a BMP file")
    return (
        b"\0"
        + struct.pack("<I", len(bitmap))
        + bitmap
        + struct.pack("<IIII", 0, 0, 0, 0)
        + struct.pack("<I", STORED_FLAG | len(bitmap))
    )
