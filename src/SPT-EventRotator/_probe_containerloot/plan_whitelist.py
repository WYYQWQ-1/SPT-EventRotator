import json, os
D = r"D:/PureEFTv4.1/EFTV4.1/SPT_Runtime/SPT_Data/database"
items = json.load(open(os.path.join(D, "templates/items.json"), encoding="utf-8"))
en = json.load(open(os.path.join(D, "locales/global/en.json"), encoding="utf-8"))
names = {k[:-5]: v for k, v in en.items() if k.endswith(" Name")}

CATS = {
 "jacket": ["578f8778245977358849a9b5"],
 "medbox": ["5909d24f86f77466f56e6855", "5d6fe50986f77449d97f7463"],
 "toolbox": ["5909d50c86f774659e6aaebe", "5d6fd45b86f774317075ed43", "59139c2186f77411564f8e42"],
 "safe": ["578f8782245977354405a1e3", "5d07b91b86f7745a077a9432"],
 "weaponbox": ["5909d7cf86f77470ee57d75a", "5909d5ef86f77467974efbd8", "5909d76c86f77471e53d2adf", "5909d89086f77472591234a0"],
 "duffle": ["578f87a3245977356274f2cb"],
}
ITEMS = {
 "jacket": ["5c1d0c5f86f7744bb2683cf0","5c1d0d6d86f7744bb2683e1f","5c1d0dc586f7744baf2e7b79",
            "5c1d0efb86f7744baf2e7b7b","5c1d0f4986f7744bb01837fa","5c1e495a86f7743109743dfb",
            "5d80c60f86f77440373c4ece","5d80c62a86f7744036212b3f","5ede7a8229445733cb4c18e2",
            "63a3a93f8a56922e82001f5d","64ccc25f95763a1ae376e447","5d235b4d86f7742e017bc88a",
            "59faff1d86f7746c51718c9c"],
 "medbox": ["590c657e86f77412b013051d","5c0e530286f7747fa1419862","5c0530ee86f774697952d952"],
 "toolbox": ["57347ca924597744596b4e71","57347baf24597738002c6178","59faff1d86f7746c51718c9c",
             "5d03794386f77420415576f5","590a3b0486f7743954552bdb","5c05308086f7746b2101e90b"],
 "safe": ["5c1d0c5f86f7744bb2683cf0","5c1d0d6d86f7744bb2683e1f","5c1d0dc586f7744baf2e7b79",
          "5d80c60f86f77440373c4ece","5d235b4d86f7742e017bc88a","59faff1d86f7746c51718c9c",
          "5c94bbff86f7747ee735c08f"],
 "weaponbox": ["5d1b5e94d7ad1a2b865a96b0","5a1eaa87fcdbcb001865f75e","5c110624d174af029e69734c",
               "544a3a774bdc2d3a388b4567","57ac965c24597706be5f975c","5c05293e0db83400232fff80"],
 "duffle": ["5d1b376e86f774252519444e","5d1b33a686f7742523398398","62a08f4c4f842e1bd12d9d62",
            "60b0f7057897d47c5b04ab94"],
}

def size(t):
    p = (items.get(t) or {}).get("_props") or {}
    return p.get("Width"), p.get("Height")

def grids(t):
    p = (items.get(t) or {}).get("_props") or {}
    return [((g.get("_props") or {}).get("cellsH"), (g.get("_props") or {}).get("cellsV")) for g in (p.get("Grids") or [])]

print("CATEGORY: containers")
for c, ts in CATS.items():
    print(" ", c)
    for t in ts:
        print(f"    {t}  grids={grids(t)}  {names.get(t)}")
print("\nCATEGORY: items (size / fit?)")
for c, ts in ITEMS.items():
    print(" ", c)
    for t in ts:
        w, h = size(t)
        fit = []
        for ct in CATS[c]:
            ok = any((w <= gh and h <= gv) or (h <= gh and w <= gv) for gh, gv in grids(ct))
            fit.append("Y" if ok else "N")
        print(f"    {t}  {w}x{h}  fit={''.join(fit)}  {names.get(t)}")
