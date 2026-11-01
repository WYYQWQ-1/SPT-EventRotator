import json, collections, os
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database/locations"
W = os.path.join(D, "woods")
sc = json.load(open(os.path.join(W, "staticContainers.json"), encoding="utf-8"))
lst = sc["staticContainers"]
print("first full element:", json.dumps(lst[0], ensure_ascii=False)[:1500])
print()
print("template keys:", list(lst[0]["template"].keys()))
cnt = collections.Counter()
for e in lst:
    t = e.get("template") or {}
    root = None
    for it in t.get("Items") or []:
        if it.get("_id") == t.get("Root"):
            root = it.get("_tpl")
    cnt[root] += 1
print("total", len(lst), "distinct container root tpls", len(cnt))
for k, v in cnt.most_common(200):
    print("  ", k, v)

# probability values
probs = collections.Counter(e.get("probability") for e in lst)
print("probabilities:", dict(list(probs.items())[:20]))

# looseLoot container counts
ll = json.load(open(os.path.join(W, "looseLoot.json"), encoding="utf-8"))
sps = ll.get("spawnpoints")
cont = [s for s in sps if (s.get("template") or {}).get("IsContainer")]
cnt2 = collections.Counter()
for s in cont:
    t = s["template"]; root = None
    for it in t.get("Items") or []:
        if it.get("_id") == t.get("Root"): root = it.get("_tpl")
    cnt2[root] += 1
print("\nlooseLoot container spawnpoints:", len(cont), "distinct", len(cnt2))
for k, v in cnt2.most_common(200): print("  ", k, v)
