import json, os, re
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database"
items = json.load(open(os.path.join(D, "templates/items.json"), encoding="utf-8"))
en = json.load(open(os.path.join(D, "locales/global/en.json"), encoding="utf-8"))
names = {k[:-5]: v for k, v in en.items() if k.endswith(" Name")}
for kw in ["doll", "tumbler", "matryosh", "roly", "chebur", "figur", "toy", "plush", "bobble", "statue"]:
    hits = [(t, n) for t, n in names.items() if kw in n.lower()]
    print(f"-- {kw}: {len(hits)}")
    for t, n in hits[:20]:
        p = (items.get(t) or {}).get("_props") or {}
        print("     ", t, f"{p.get('Width')}x{p.get('Height')}", n)
# 5b057b4f?
for t in ["5b057b4f86f7746d4e8e0a3e", "59faff1d86f7746c51718c9c", "5d235b4d86f7742e017bc88a",
          "5449016a4bdc2d6f028b456f", "5696686a4bdc2da3298b456a", "569668774bdc2da2298b4568",
          "5b9b902e86f7744b8f69e313", "5bb2475ed435e1eb5f795902", "5bb2475ed435e1eb5f795901",
          "57347baf24597738002c6178", "590a3b0486f7743954552bdb", "5c05308086f7746b2101e90b",
          "5d03794386f77420415576f5"]:
    it = items.get(t)
    if it:
        p = it.get("_props") or {}
        print(t, f"{p.get('Width')}x{p.get('Height')}", names.get(t))
    else:
        print(t, "MISSING")
