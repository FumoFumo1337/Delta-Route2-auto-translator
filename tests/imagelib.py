"""Minimal PNG and BMP readers, standard library only.

The golden image tests exist to survive the move of the drawing code from
Python-over-.NET to C#. Verifying that move with .NET would compare the port
against itself, so the reference is decoded here instead, with zlib and struct.

Everything is normalised to top-down RGBA8 so that pictures encoded at different
depths still compare equal. Depth itself is not thrown away - it is recorded
separately, because the engine reads the stored BMP header and a 24 bit resource
silently rewritten as 32 bit is a real regression that a pixel comparison alone
would not notice.
"""

from __future__ import annotations

import hashlib
import struct
import zlib
from dataclasses import dataclass


@dataclass(frozen=True)
class Image:
    width: int
    height: int
    depth: int
    """Bits per pixel as stored in the file, before normalisation."""
    pixels: bytes
    """width * height * 4 bytes, RGBA, first row first."""

    @property
    def signature(self) -> str:
        return hashlib.sha256(self.pixels).hexdigest()

    def pixel(self, x: int, y: int) -> tuple[int, int, int, int]:
        offset = (y * self.width + x) * 4
        red, green, blue, alpha = self.pixels[offset : offset + 4]
        return red, green, blue, alpha


def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def _unfilter(raw: bytes, width: int, height: int, sample_bytes: int) -> bytes:
    """Undo the per-scanline PNG filters.

    Filters reference the pixel to the left and the row above, so the rows have
    to be walked in order.
    """
    stride = width * sample_bytes
    out = bytearray(stride * height)
    previous = bytearray(stride)
    position = 0
    for row in range(height):
        if position >= len(raw):
            raise ValueError("PNG scanline data ended early")
        method = raw[position]
        position += 1
        line = bytearray(raw[position : position + stride])
        if len(line) != stride:
            raise ValueError("PNG scanline is short")
        position += stride

        if method == 1:
            for index in range(sample_bytes, stride):
                line[index] = (line[index] + line[index - sample_bytes]) & 0xFF
        elif method == 2:
            for index in range(stride):
                line[index] = (line[index] + previous[index]) & 0xFF
        elif method == 3:
            for index in range(stride):
                left = line[index - sample_bytes] if index >= sample_bytes else 0
                line[index] = (line[index] + ((left + previous[index]) >> 1)) & 0xFF
        elif method == 4:
            for index in range(stride):
                left = line[index - sample_bytes] if index >= sample_bytes else 0
                upper = previous[index]
                upper_left = previous[index - sample_bytes] if index >= sample_bytes else 0
                line[index] = (line[index] + _paeth(left, upper, upper_left)) & 0xFF
        elif method != 0:
            raise ValueError(f"Unknown PNG filter {method}")

        out[row * stride : (row + 1) * stride] = line
        previous = line
    return bytes(out)


def read_png(data: bytes) -> Image:
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Not a PNG file")

    position = 8
    header: tuple[int, ...] | None = None
    palette = b""
    transparency = b""
    compressed = bytearray()
    while position + 8 <= len(data):
        length, kind = struct.unpack_from(">I4s", data, position)
        body = data[position + 8 : position + 8 + length]
        position += 12 + length
        if kind == b"IHDR":
            header = struct.unpack(">IIBBBBB", body)
        elif kind == b"PLTE":
            palette = body
        elif kind == b"tRNS":
            transparency = body
        elif kind == b"IDAT":
            compressed.extend(body)
        elif kind == b"IEND":
            break

    if header is None:
        raise ValueError("PNG has no IHDR")
    width, height, bit_depth, color_type, compression, filtering, interlace = header
    if compression != 0 or filtering != 0:
        raise ValueError("Unsupported PNG compression or filter method")
    if interlace != 0:
        raise ValueError("Interlaced PNG is not supported")
    if bit_depth != 8:
        raise ValueError(f"Only 8 bit PNG samples are supported, got {bit_depth}")

    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(color_type)
    if channels is None:
        raise ValueError(f"Unknown PNG colour type {color_type}")

    raw = _unfilter(zlib.decompress(bytes(compressed)), width, height, channels)

    # Channel expansion runs through strided slice assignment rather than a
    # per-pixel loop: a full screen sheet is half a million pixels, and the
    # loop version made this reader too slow to run on every asset.
    count = width * height
    pixels = bytearray(count * 4)
    opaque = b"\xff" * count
    if color_type == 0:
        for channel in range(3):
            pixels[channel::4] = raw
        pixels[3::4] = opaque
    elif color_type == 2:
        for channel in range(3):
            pixels[channel::4] = raw[channel::3]
        pixels[3::4] = opaque
    elif color_type == 3:
        for channel in range(3):
            pixels[channel::4] = bytes(palette[entry * 3 + channel] for entry in raw)
        alpha = bytes(
            transparency[entry] if entry < len(transparency) else 255 for entry in raw
        )
        pixels[3::4] = alpha
    elif color_type == 4:
        for channel in range(3):
            pixels[channel::4] = raw[0::2]
        pixels[3::4] = raw[1::2]
    else:
        for channel in range(4):
            pixels[channel::4] = raw[channel::4]

    depth = {0: 8, 2: 24, 3: 8, 4: 16, 6: 32}[color_type]
    return Image(width, height, depth, bytes(pixels))


def read_bmp(data: bytes) -> Image:
    if data[:2] != b"BM":
        raise ValueError("Not a BMP file")

    start = struct.unpack_from("<I", data, 10)[0]
    header_size = struct.unpack_from("<I", data, 14)[0]
    if header_size < 40:
        raise ValueError(f"Unsupported BMP header size {header_size}")
    width, height, _planes, depth, compression = struct.unpack_from("<iiHHI", data, 18)
    if compression not in (0, 3):
        raise ValueError(f"Unsupported BMP compression {compression}")
    if depth not in (8, 24, 32):
        raise ValueError(f"Unsupported BMP depth {depth}")

    top_down = height < 0
    height = abs(height)

    palette = b""
    if depth == 8:
        used = struct.unpack_from("<I", data, 46)[0] or 256
        table = 14 + header_size
        palette = data[table : table + used * 4]

    stride = ((width * depth + 31) // 32) * 4
    sample = depth // 8
    pixels = bytearray(width * height * 4)
    # A 32 bit BI_RGB bitmap leaves the fourth byte undefined rather than
    # holding alpha, and these encoders write zero into it. Reading it as
    # transparency would make every such image compare equal to a blank one,
    # so every pixel is recorded opaque.
    opaque = b"\xff" * width
    for row in range(height):
        # BMP rows run bottom to top unless the stored height is negative.
        source_row = row if top_down else height - 1 - row
        line = start + source_row * stride
        packed = data[line : line + width * sample]
        if len(packed) != width * sample:
            raise ValueError(f"BMP row {row} is short")
        if depth == 8:
            channels = [bytes(palette[entry * 4 + shift] for entry in packed) for shift in range(3)]
        else:
            channels = [packed[shift::sample] for shift in range(3)]
        target = pixels[row * width * 4 : (row + 1) * width * 4]
        target[0::4] = channels[2]
        target[1::4] = channels[1]
        target[2::4] = channels[0]
        target[3::4] = opaque
        pixels[row * width * 4 : (row + 1) * width * 4] = target
    return Image(width, height, depth, bytes(pixels))


def unwrap_stored_iaf(data: bytes) -> bytes | None:
    """The BMP inside an uncompressed IAF, or None for any other IAF.

    This reverses reference_triangle_codec.wrap_bmp_bytes, the IAF form the
    toolset writes. The compressed forms the game itself ships need GARbro, and
    are deliberately not decoded here - a reference that leaned on GARbro to
    check GARbro would prove nothing.
    """
    if len(data) < 25 or data[0] != 0:
        return None
    size = struct.unpack_from("<I", data, 1)[0]
    if 1 + 4 + size + 20 != len(data) or data[5:7] != b"BM":
        return None
    tail = struct.unpack_from("<IIIII", data, 5 + size)
    if tail[:4] != (0, 0, 0, 0) or tail[4] != (0x40000000 | size):
        return None
    return data[5 : 5 + size]


def read_image(data: bytes) -> Image:
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return read_png(data)
    if data[:2] == b"BM":
        return read_bmp(data)
    raise ValueError("Unrecognised image container")
