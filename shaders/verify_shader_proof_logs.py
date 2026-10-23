"""Fail closed on native shader diagnostics that Unity's error APIs can miss."""

from pathlib import Path
import argparse, hashlib, json, re

POLICY_PATH = Path(__file__).resolve().parents[1] / "script/unity_compiler_errors.json"


def patterns():
    policy = json.loads(POLICY_PATH.read_text())
    if policy.get("schemaVersion") != 1 or not policy.get("patterns"):
        raise ValueError("Missing or unsupported compiler diagnostic policy")
    return [re.compile(value) for value in policy["patterns"]]


def inspect(path):
    path = Path(path)
    digest = hashlib.sha256()
    errors = []
    matchers = patterns()
    bytes_read = 0
    with path.open("rb") as stream:
        for line_number, raw in enumerate(stream, 1):
            digest.update(raw)
            bytes_read += len(raw)
            line = raw.decode("utf-8", errors="replace").strip()
            if any(pattern.search(line) for pattern in matchers):
                errors.append({"line": line_number, "message": line})
    if not bytes_read:
        errors.append({"line": 0, "message": "Unity build/compiler log is empty"})
    return dict(
        path=str(path),
        sha256=digest.hexdigest(),
        policySha256=hashlib.sha256(POLICY_PATH.read_bytes()).hexdigest(),
        passed=not errors,
        diagnostics=errors,
    )


def require_clean(*paths):
    rows = [inspect(path) for path in paths]
    if not all(r["passed"] for r in rows):
        raise ValueError(
            "Unity compiler log rejected: "
            + repr([(r["path"], len(r["diagnostics"])) for r in rows if not r["passed"]])
        )
    return rows


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("logs", nargs="+", type=Path)
    args = parser.parse_args()
    rows = [inspect(path) for path in args.logs]
    result = {"passed": all(r["passed"] for r in rows), "logs": rows}
    if args.output:
        args.output.write_text(json.dumps(result, indent=2))
    for row in rows:
        print(row["path"], "PASS" if row["passed"] else "FAIL", len(row["diagnostics"]))
    raise SystemExit(0 if result["passed"] else 1)
