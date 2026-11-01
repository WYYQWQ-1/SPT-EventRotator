import json, os, collections
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database"
items = json.load(open(os.path.join(D, "templates/items.json"), encoding="utf-8"))
en = json.load(open(os.path.join(D, "locales/global/en.json"), encoding="utf-8"))
names = {k[:-5]: v for k, v in en.items() if k.endswith(" Name")}
L = os.path.join(D, "locations")


def root_tpl(t):
    for it in t.get("Items") or []:
        if it.get("_id") == t.get("Root"):
            return it.get("_tpl")
    return None


glob = collections.Counter()
per = {}
for m in sorted(os.listdir(L)):
    f = os.path.join(L, m, "staticContainers.json")
    if not os.path.isfile(f):
        continue
    d = json.load(open(f, encoding="utf-8"))
    c = collections.Counter(root_tpl(e["template"]) for e in d.get("staticContainers") or [])
    per[m] = c
    glob.update(c)

print("ALL container tpls across maps:")
for t, c in glob.most_common():
    it = items.get(t) or {}
    p = it.get("_props") or {}
    grids = ",".join(f"{((g.get('_props') or {}).get('cellsH'))}x{((g.get('_props') or {}).get('cellsV'))}"
                     for g in (p.get("Grids") or []))
    print(f"  {t}  {names.get(t,'?'):38} grids={grids:8} total={c}")
print("\nwoods:", per.get("woods"))

print("\nper-map presence of woods tpls:")
for t in per.get("woods", {}):
    ms = [m for m in per if per[m].get(t)]
    print(f"  {t} {names.get(t,'?'):32} -> {len(ms)} maps: {','.join(ms[:14])}")
