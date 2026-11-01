import json, os
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database"
items = json.load(open(os.path.join(D, "templates/items.json"), encoding="utf-8-sig"))
en = json.load(open(os.path.join(D, "locales/global/en.json"), encoding="utf-8-sig"))
ch = json.load(open(os.path.join(D, "locales/global/ch.json"), encoding="utf-8-sig"))

def nm(tpl):
    return (en.get(tpl + " Name") or ch.get(tpl + " Name") or "?")

def info(tpl):
    it = items.get(tpl)
    if not it:
        return f"{tpl}  [MISSING]"
    p = it.get("_props") or {}
    return f"{tpl}  {p.get('Width')}x{p.get('Height')}  {nm(tpl)}"

base = os.path.dirname(__file__)
ev = json.load(open(os.path.join(base, "..", "events.json"), encoding="utf-8-sig"))

def walk(o, path=""):
    if isinstance(o, dict):
        for k, v in o.items():
            yield from walk(v, path + "/" + str(k))
    elif isinstance(o, list):
        for i, v in enumerate(o):
            yield from walk(v, path + f"[{i}]")
    else:
        yield path, o

print("== tpls used in events.json loot pools ==")
seen = set()
for p, v in walk(ev):
    if isinstance(v, str) and (("tpls" in p) or ("valuable" in p and "chance" not in p) or p.endswith("/botLootAdd_") or "airdrops" in p):
        pass
# simpler: collect all 24-hex strings
import re
hexes = set()
for p, v in walk(ev):
    if isinstance(v, str) and re.fullmatch(r"[0-9a-f]{24}", v):
        hexes.add(v)
for t in sorted(hexes):
    if t in items:
        print("  ", info(t))
print("total hex tpls referenced:", len(hexes), "missing:", [t for t in hexes if t not in items])

print("\n== search by keyword ==")
keys = ["Grizzly", "Propital", "LEDX", "Graphics card", "RAM", "Bitcoin", "Physical bitcoin",
        "Moonshine", "Vodka", "Water", "Roly", "Doll", "GP coin", "Roubles", "Dollars", "Euro",
        "keycard", "Keycard", "Thermal", "Thermal imaging", "Scope", "M4A1", "AK-74", "Jacket", "Wardrobe",
        "Slickers", "Ski hat", "Bandana"]
names = {}
for k, v in en.items():
    if k.endswith(" Name"):
        names[k[:-5]] = v
for kw in keys:
    hits = [(t, n) for t, n in names.items() if kw.lower() in n.lower()]
    print(f"-- {kw}: {len(hits)}")
    for t, n in hits[:8]:
        it = items.get(t)
        if it:
            p = it.get("_props") or {}
            print("     ", t, f"{p.get('Width')}x{p.get('Height')}", n)
