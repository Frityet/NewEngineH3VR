#!/usr/bin/env python3
import json, struct, pathlib, collections

out = pathlib.Path("build/video/serialization")
schema = json.loads((out / "schema.json").read_text())


class Reader:
    def __init__(self, raw):
        self.raw = raw
        self.pos = 32
        self.trace = []

    def take(self, n):
        if self.pos + n > len(self.raw):
            raise ValueError(f"EOF at {self.pos} requesting {n}")
        b = self.raw[self.pos : self.pos + n]
        self.pos += n
        return b

    def align(self):
        self.pos = (self.pos + 3) & ~3

    def read(self, s, path=""):
        begin = self.pos
        k = s["kind"]
        if k == "struct":
            v = {f["name"]: self.read(f["schema"], path + "." + f["name"]) for f in s["fields"]}
        elif k == "array":
            n = struct.unpack("<i", self.take(4))[0]
            if not 0 <= n <= 10000:
                raise ValueError(f"array count {n} at {path} offset {begin}")
            v = [self.read(s["element"], path + f"[{i}]") for i in range(n)]
            self.align()
        elif k == "String":
            n = struct.unpack("<i", self.take(4))[0]
            if not 0 <= n <= 10000:
                raise ValueError(f"string size {n} at {path} offset {begin}")
            v = self.take(n).decode()
            self.align()
        elif k == "PPtr":
            v = dict(zip(("m_FileID", "m_PathID"), struct.unpack("<iq", self.take(12))))
        else:
            fmt = {
                "Int32": "i",
                "UInt32": "I",
                "Int64": "q",
                "UInt64": "Q",
                "Single": "f",
                "Double": "d",
                "Boolean": "?",
                "Byte": "B",
                "SByte": "b",
                "Int16": "h",
                "UInt16": "H",
            }[k]
            v = struct.unpack("<" + fmt, self.take(struct.calcsize(fmt)))[0]
            self.align()
        self.trace.append(
            dict(path=path, start=begin, end=self.pos, value=v if k != "struct" else None)
        )
        return v


records = []
for rec in json.loads((out / "inventory.json").read_text()):
    r = Reader((out / rec["raw_file"]).read_bytes())
    try:
        d = r.read(schema)
        assert r.pos == rec["byte_size"], (r.pos, rec["byte_size"])
        records.append(dict(**rec, fields=d))
    except Exception as e:
        (out / "decode-trace.json").write_text(json.dumps(r.trace, indent=2))
        print(rec["raw_file"], e)
        raise
(out / "decoded.json").write_text(json.dumps(records, indent=2))
print("Decoded", len(records), "full serialized payloads")
