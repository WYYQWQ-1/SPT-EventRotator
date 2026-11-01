import json, os
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database"
items = json.load(open(os.path.join(D, "templates/items.json"), encoding="utf-8"))
en = json.load(open(os.path.join(D, "locales/global/en.json"), encoding="utf-8"))
ch = json.load(open(os.path.join(D, "locales/global/ch.json"), encoding="utf-8"))
ev = json.load(open(os.path.join(os.path.dirname(__file__), "..", "events.json"), encoding="utf-8-sig"))
e = next(x for x in ev if x["id"] == "wild_wild_woods")
cfg = e["containerLootInject"]["woods"]

from collections import Counter
W = os.path.join(D, "locations", "woods")
sc = json.load(open(os.path.join(W, "staticContainers.json"), encoding="utf-8"))
def root(t):
    for it in t.get("Items") or []:
        if it.get("_id") == t.get("Root"): return it.get("_tpl")
cnt = Counter(root(x["template"]) for x in sc["staticContainers"])
labels = {"jacket":"夹克","medbox":"医疗箱","toolbox":"工具箱","safe":"保险箱","weaponbox":"武器箱","duffle":"旅行包"}
def nm(t):
    return en.get(t+" Name") or ch.get(t+" Name") or "?"
def zh(t):
    return ch.get(t+" Name") or en.get(t+" Name") or "?"
def sz(t):
    p=(items[t].get("_props") or {})
    return f"{p.get('Width')}x{p.get('Height')}"
def grids(t):
    p=(items[t].get("_props") or {})
    return ",".join(f"{((g.get('_props') or {}).get('cellsH'))}x{((g.get('_props') or {}).get('cellsV'))}" for g in (p.get("Grids") or []))

print("CHANCE", cfg["chance"])
tot=0
for k, c in cfg["categories"].items():
    n=sum(cnt[t] for t in c["containers"])
    tot+=n
    print(f"\n[{labels[k]}] containers={n}  expect={n*cfg['chance']:.2f}")
    for t in c["containers"]:
        print(f"   容器 {t}  {nm(t)} / {zh(t)}  grids={grids(t)}  woods_count={cnt[t]}")
    for t,w in c["items"].items():
        print(f"   物品 {t}  {nm(t)} / {zh(t)}  {sz(t)}  weight={w}")
print("\nTOTAL containers", tot, "expect", tot*cfg["chance"])
