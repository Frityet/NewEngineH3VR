"""Random-access reads from UnityFS v8/v9 player data without unpacking the game."""
from bisect import bisect_right
from functools import lru_cache
from io import BytesIO
from itertools import accumulate
from struct import unpack

from UnityPy.enums import CompressionFlags
from UnityPy.helpers.CompressionHelper import DECOMPRESSION_MAP


def read_string(stream):
    value = bytearray()
    while byte := stream.read(1):
        if byte == b"\0":
            return value.decode()
        value.extend(byte)
    raise ValueError("Unterminated UnityFS string")


def decompress(data, size, flags):
    codec = CompressionFlags(flags & 0x3F)
    result = DECOMPRESSION_MAP[codec](data, size) if codec else data
    if len(result) != size:
        raise ValueError("Incorrect UnityFS block size")
    return result


class PlayerArchive:
    def __init__(self, stream):
        stream.seek(0)
        if read_string(stream) != "UnityFS":
            raise ValueError("Expected a UnityFS archive")
        version, = unpack(">I", stream.read(4))
        if version not in (8, 9):
            raise ValueError(f"Unsupported UnityFS version {version}")
        read_string(stream)
        read_string(stream)
        archive_size, compressed, decoded, flags = unpack(">QIII", stream.read(20))
        offset = (stream.tell() + 15) & ~15
        stream.seek(archive_size - compressed if flags & 0x80 else offset)
        metadata = BytesIO(decompress(stream.read(compressed), decoded, flags))
        if not flags & 0x80:
            offset = stream.tell()
        if flags & 0x200:
            offset = (offset + 15) & ~15

        metadata.seek(16)
        count, = unpack(">I", metadata.read(4))
        blocks = ([unpack(">IIHQ", metadata.read(18)) for _ in range(count)] if version == 9
                  else [unpack(">IIH", metadata.read(10)) for _ in range(count)])
        self.starts = list(accumulate((row[0] for row in blocks), initial=0))
        physical = ([offset + row[3] for row in blocks] if version == 9
                    else list(accumulate((row[1] for row in blocks), initial=offset)))
        count, = unpack(">I", metadata.read(4))
        self.members = {}
        for _ in range(count):
            start, size, node_flags = unpack(">QQI", metadata.read(20))
            self.members[read_string(metadata)] = (start, size, node_flags)

        @lru_cache(maxsize=16)
        def block(index):
            size, compressed, block_flags = blocks[index][:3]
            stream.seek(physical[index])
            return decompress(stream.read(compressed), size, block_flags)
        self.block = block

    def read(self, name, offset=0, size=None):
        start, length, _ = self.members[name]
        if size is None:
            size = length - offset
        if offset < 0 or size < 0 or offset + size > length:
            raise ValueError(f"Read outside archive member {name}")
        pos, end = start + offset, start + offset + size
        index = bisect_right(self.starts, pos) - 1
        result = bytearray()
        while pos < end:
            limit = min(end, self.starts[index + 1])
            result.extend(self.block(index)[pos - self.starts[index]:limit - self.starts[index]])
            pos = limit
            index += 1
        return bytes(result)
