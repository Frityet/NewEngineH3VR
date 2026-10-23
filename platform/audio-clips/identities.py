"""Map original AudioClip objects to pristine AssetRipper exports and GUIDs."""

import pathlib, json, re, html, concurrent.futures, time, http.client, threading

local = threading.local()


def get(url):
    if not hasattr(local, "connection"):
        local.connection = http.client.HTTPConnection("127.0.0.1", 58947, timeout=90)
    local.connection.request("GET", url)
    response = local.connection.getresponse()
    data = response.read()
    assert response.status == 200
    return data


out = pathlib.Path("build/audio-clips")
root = pathlib.Path("recovered-full/ExportedProject")
cache = out / "source-records.json"
if cache.exists():
    assets = json.load(open(cache))
else:
    assets = []
    for c in json.load(open("build/shader-identities/collections.json")):
        for a in c["assets"]:
            if a["type"] == "AudioClip":
                assets.append(
                    dict(a, collection=c["name"], collection_path=c["path"], bundle=c.get("bundle"))
                )

    def detail(a):
        s = get(a["url"]).decode()
        m = re.search(r"<th>Original Path</th><td>(.*?)</td>", s)
        a["original_path"] = html.unescape(m.group(1)) if m else None
        return a

    start = time.monotonic()
    result = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as ex:
        for i, a in enumerate(ex.map(detail, assets)):
            result.append(a)
            if i % 1000 == 0:
                print(i, "/", len(assets), "seconds", round(time.monotonic() - start), flush=True)
    assets = result
    cache.write_text(json.dumps(assets, indent=2))
existing = {
    str(p.relative_to(root)): p
    for p in (root / "Assets/AudioClip").iterdir()
    if p.suffix in (".ogg", ".wav")
    and "AudioImporter:" in pathlib.Path(str(p) + ".meta").read_text()
}
canonical = {k.casefold(): k for k in existing}
used = set()
result = []
unresolved = []
for a in assets:
    directory = (
        str(pathlib.PurePosixPath(a["original_path"]).parent)
        if a["original_path"]
        else "Assets/AudioClip"
    )
    name = re.sub(
        r"[/\x00-\x1f:,\[\]]",
        "_",
        a["name"].replace("(Clone)", "").replace("(Instance)", "").strip(),
    )
    # Extension is validated against original FSB format during payload audit.
    candidates = []
    for ext in (".ogg", ".wav"):
        path = directory + "/" + name + ext
        i = 0
        while path.casefold() in used:
            path = directory + "/" + name + "_" + str(i) + ext
            i += 1
        if path.casefold() in canonical:
            candidates.append(path)
    if len(candidates) != 1:
        unresolved.append(dict(a, candidates=candidates))
        continue
    path = canonical[candidates[0].casefold()]
    used.add(path.casefold())
    a["exported_path"] = path
    a["source"] = "StreamingAssets/" + a["bundle"] if a["bundle"] else a["collection"]
    a["guid"] = re.search(
        r"^guid: (\w+)", pathlib.Path(str(existing[path]) + ".meta").read_text(), re.M
    ).group(1)
    result.append(a)
(out / "identity-map.json").write_text(json.dumps(result, indent=2))
(out / "map-unresolved.json").write_text(
    json.dumps(
        {
            "unmapped_files": sorted(k for k in existing if k.casefold() not in used),
            "unresolved_assets": unresolved,
        },
        indent=2,
    )
)
print(
    "mapped",
    len(result),
    "unresolved",
    len(unresolved),
    "unmapped",
    len(existing) - len(used),
    flush=True,
)
