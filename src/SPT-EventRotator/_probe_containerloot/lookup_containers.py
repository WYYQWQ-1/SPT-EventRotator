import json, os, collections
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database"
items = json.load(open(os.path.join(D, "templates/items.json"), encoding="utf-8"))
en = json.load(open(os.path.join(D, "locales/global/en.json"), encoding="utf-8"))
zh = json.load(open(os.path.join(D, "locales/global/ch.json"), encoding="utf-8"))

def name(tpl):
    k = tpl + " Name"
    return en.get(k) or zh.get(k) or "?"

W = os.path.join(D, "locations", "woods")
sc = json.load(open(os.path.join(W, "staticContainers.json"), encoding="utf-8"))


def root_tpl(t):
    for it in t.get("Items") or []:
        if it.get("_id") == t.get("Root"):
            return it.get("_tpl")
    return None


cnt = collections.Counter()
for e in sc["staticContainers"]:
    cnt[root_tpl(e["template"])] += 1

print(f"{'tpl':26} {'count':>5}  {'name'}")
for tpl, c in cnt.most_common():
    it = items.get(tpl)
    if not it:
        print(f"{tpl:26} {c:5}  [MISSING FROM items.json]")
        continue
    p = it.get("_props") or {}
    grids = p.get("Grids") or []
    gd = []
    for g in grids:
        gp = g.get("_props") or {}
        gd.append(f"{gp.get('cellsH')}x{gp.get('cellsV')}")
    print(f"{tpl:26} {c:5}  {name(tpl):50} {p.get('Width')}x{p.get('Height')} grids={','.join(gd) or '-'}")

print("\nitems.json total:", len(items))
