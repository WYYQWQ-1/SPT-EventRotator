import json, os, collections
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database/locations"
W = os.path.join(D, "woods")
ll = json.load(open(os.path.join(W, "looseLoot.json"), encoding="utf-8"))
print("keys:", list(ll.keys()))
sps = ll.get("spawnpoints") or []
print("n spawnpoints", len(sps))
s = sps[0]
print("spawnpoint keys:", list(s.keys()))
print("template keys:", list(s["template"].keys()))
print("sample:", json.dumps({k: s["template"][k] for k in s["template"] if k not in ("Items",)}, ensure_ascii=False)[:800])
iscont = collections.Counter(str(s["template"].get("IsContainer")) for s in sps)
print("IsContainer values:", iscont)
# also check isContainer lower
print("lowercase present:", sum(1 for s in sps if "isContainer" in s["template"]))
