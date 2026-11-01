import json, collections, sys, os

D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database/locations"
W = os.path.join(D, "woods")

sc = json.load(open(os.path.join(W, "staticContainers.json"), encoding="utf-8"))
print("staticContainers top keys:", list(sc.keys()))
for k, v in sc.items():
    print(" ", k, type(v).__name__, len(v) if isinstance(v, list) else "")
    if isinstance(v, list) and v:
        e = v[0]
        print("    elem keys:", list(e.keys()))
        print("    sample:", json.dumps({kk: e.get(kk) for kk in ("Id", "IsContainer", "Root", "IsAlwaysSpawn", "useGravity")}, ensure_ascii=False))
        items = e.get("Items") or []
        print("    items:", len(items), "first:", json.dumps(items[0], ensure_ascii=False)[:200] if items else None)

am = json.load(open(os.path.join(W, "staticAmmo.json"), encoding="utf-8"))
print("\nstaticAmmo top keys:", list(am.keys()))
for k, v in am.items():
    print(" ", k, type(v).__name__, (len(v) if hasattr(v, "__len__") else ""))
    if isinstance(v, list) and v:
        print("    elem keys:", list(v[0].keys()))
        print("    sample:", json.dumps(v[0], ensure_ascii=False)[:400])

# looseLoot containers
ll = json.load(open(os.path.join(W, "looseLoot.json"), encoding="utf-8"))
print("\nlooseLoot top keys:", list(ll.keys()))
sps = ll.get("spawnpoints")
print("spawnpoints:", len(sps))
cont = [s for s in sps if (s.get("template") or {}).get("IsContainer")]
print("container-type spawnpoints:", len(cont))
cnt = collections.Counter()
for s in cont:
    t = s["template"]
    root = None
    for it in t.get("Items") or []:
        if it.get("_id") == t.get("Root"):
            root = it.get("_tpl")
    cnt[root] += 1
print("container root tpl counts:", len(cnt))
for k, v in cnt.most_common(60):
    print("  ", k, v)
