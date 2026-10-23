"""Simplify proven eye*4 row indices, avoiding Unity2020 HLSLcc Metal bitfield vector/scalar bug.
By default propagates within straight-line blocks until the written register lane changes.
Optional scope tracking retains incoming facts only across blocks that never write their lane;
facts created conditionally never escape their scope.
For eye=0/1 and row=0..3, (eye*4+row)/4 equals eye exactly.
"""

from pathlib import Path
import re, json

root = Path(__file__).resolve().parents[1]


def repair(s, preserve_unchanged_scopes=False):
    known = {}
    origins = {}
    depth = 0
    out = []
    count = 0
    for line in s.splitlines(keepends=True):
        if preserve_unchanged_scopes:
            # Facts created inside a conditional/loop cannot escape its scope. Facts
            # entering that scope survive only if no assignment killed their lane.
            depth -= line.count("}")
            for reg in list(known):
                if origins[reg] > depth:
                    del known[reg]
        elif "}" in line:
            known.clear()
        for reg, expr in list(known.items()):
            line, n = re.subn(
                r"\[\(\(int\)\(" + re.escape(reg) + r"\) \+ [0-3]\) / 4\]", "[" + expr + "]", line
            )
            count += n
        write = re.search(r"\b(tmp\d+)(?:\.([xyzw]+))?\s*=", line)
        if write:
            for reg in list(known):
                base, component = reg.split(".")
                if base == write[1] and (write[2] is None or component in write[2]):
                    del known[reg]
        match = re.search(
            r"\b(tmp\d+\.[xyzw]) = float1\(int1\(unity_StereoEyeIndex\) << 2\);", line
        )
        if match:
            known[match[1]] = "unity_StereoEyeIndex"
            origins[match[1]] = depth
        if preserve_unchanged_scopes:
            depth += line.count("{")
        out.append(line)
    return "".join(out), count


if __name__ == "__main__":
    rows = []
    for p in (root / "shader-probe/Assets/Resources/Shaders").glob("*.shader"):
        s, n = repair(p.read_text())
        if n:
            p.write_text(s)
            rows.append({"file": p.name, "replaced_indices": n})
    (root / "shaders/stereo-matrix-repairs.json").write_text(json.dumps(rows, indent=2))
    print(len(rows), sum(x["replaced_indices"] for x in rows))
