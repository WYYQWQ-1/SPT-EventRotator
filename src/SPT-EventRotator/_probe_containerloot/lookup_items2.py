import json, os, re
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database"
items = json.load(open(os.path.join(D, "templates/items.json"), encoding="utf-8"))
en = json.load(open(os.path.join(D, "locales/global/en.json"), encoding="utf-8"))
ch = json.load(open(os.path.join(D, "locales/global/ch.json"), encoding="utf-8"))
names = {k[:-5]: v for k, v in en.items() if k.endswith(" Name")}
zhnames = {k[:-5]: v for k, v in ch.items() if k.endswith(" Name")}

def nm(t):
    return names.get(t) or zhnames.get(t) or "?"

def info(t):
    it = items.get(t)
    if not it:
        return f"{t}   [MISSING]"
    p = it.get("_props") or {}
    return f"{t}  {p.get('Width')}x{p.get('Height')}  {nm(t)}"

ev = json.load(open(os.path.join(os.path.dirname(__file__), "..", "events.json"), encoding="utf-8-sig"))
evw = next(e for e in ev if e.get("id") == "wild_wild_woods")
lli = evw["looseLootInject"]["woods"]
print("== wild_wild_woods valuable ==")
for t, w in lli["valuable"]["tpls"].items():
    print("  ", info(t), "w=", w)
print("== decor ==")
for t, w in lli["decor"]["tpls"].items():
    print("  ", info(t), "w=", w)

print("\n== keyword search ==")
for kw in ["Grizzly", "Propital", "LEDX", "graphics card", "Graphics card", "RAM", "bitcoin",
           "moonshine", "Roly", "Doll", "GP coin", "Physical", "keycard", "thermal",
           "EOTech", "Trijicon", "Scope", "M4A1", "AK-74N", "AKM", "Slickers", "Jacket",
           "Wardrobe", "Water", "Duffel", "duffle", "Safe", "Toolbox", "Medbag", "Grenade box"]:
    hits = [(t, n) for t, n in names.items() if kw.lower() in n.lower()]
    print(f"-- {kw}: {len(hits)}")
    for t, n in hits[:10]:
        it = items.get(t)
        if it:
            p = it.get("_props") or {}
            print("     ", t, f"{p.get('Width')}x{p.get('Height')}", n)
