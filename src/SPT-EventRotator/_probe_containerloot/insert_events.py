import json, os
p = os.path.join(os.path.dirname(__file__), "..", "events.json")
raw = open(p, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
lines = text.splitlines(keepends=True)

cfg = {
    "containerLootInject": {
        "woods": {
            "chance": 0.25,
            "categories": {
                "jacket": {
                    "containers": ["578f8778245977358849a9b5"],
                    "items": {
                        "5c1d0c5f86f7744bb2683cf0": 1,
                        "5c1d0d6d86f7744bb2683e1f": 1,
                        "5c1d0dc586f7744baf2e7b79": 1,
                        "5c1d0efb86f7744baf2e7b7b": 1,
                        "5c1d0f4986f7744bb01837fa": 1,
                        "5c1e495a86f7743109743dfb": 1,
                        "5d80c60f86f77440373c4ece": 2,
                        "5d80c62a86f7744036212b3f": 2,
                        "5ede7a8229445733cb4c18e2": 2,
                        "63a3a93f8a56922e82001f5d": 2,
                        "64ccc25f95763a1ae376e447": 2,
                        "5d235b4d86f7742e017bc88a": 4,
                        "59faff1d86f7746c51718c9c": 1,
                    },
                },
                "medbox": {
                    "containers": ["5909d24f86f77466f56e6855", "5d6fe50986f77449d97f7463"],
                    "items": {
                        "590c657e86f77412b013051d": 3,
                        "5c0e530286f7747fa1419862": 4,
                        "5c0530ee86f774697952d952": 1,
                    },
                },
                "toolbox": {
                    "containers": ["5909d50c86f774659e6aaebe", "5d6fd45b86f774317075ed43", "59139c2186f77411564f8e42"],
                    "items": {
                        "57347ca924597744596b4e71": 3,
                        "57347baf24597738002c6178": 4,
                        "59faff1d86f7746c51718c9c": 1,
                        "5d03794386f77420415576f5": 2,
                        "590a3b0486f7743954552bdb": 3,
                        "5c05308086f7746b2101e90b": 1,
                    },
                },
                "safe": {
                    "containers": ["578f8782245977354405a1e3", "5d07b91b86f7745a077a9432"],
                    "items": {
                        "5c1d0c5f86f7744bb2683cf0": 1,
                        "5c1d0d6d86f7744bb2683e1f": 1,
                        "5c1d0dc586f7744baf2e7b79": 1,
                        "5d80c60f86f77440373c4ece": 2,
                        "5d235b4d86f7742e017bc88a": 4,
                        "59faff1d86f7746c51718c9c": 2,
                        "5c94bbff86f7747ee735c08f": 3,
                    },
                },
                "weaponbox": {
                    "containers": ["5909d7cf86f77470ee57d75a", "5909d5ef86f77467974efbd8",
                                   "5909d76c86f77471e53d2adf", "5909d89086f77472591234a0"],
                    "items": {
                        "5d1b5e94d7ad1a2b865a96b0": 2,
                        "5a1eaa87fcdbcb001865f75e": 2,
                        "5c110624d174af029e69734c": 1,
                        "544a3a774bdc2d3a388b4567": 2,
                        "57ac965c24597706be5f975c": 2,
                        "5c05293e0db83400232fff80": 1,
                    },
                },
                "duffle": {
                    "containers": ["578f87a3245977356274f2cb"],
                    "items": {
                        "5d1b376e86f774252519444e": 3,
                        "5d1b33a686f7742523398398": 3,
                        "62a08f4c4f842e1bd12d9d62": 3,
                        "60b0f7057897d47c5b04ab94": 2,
                    },
                },
            },
        }
    }
}

ev_start = next(i for i, ln in enumerate(lines) if '"id": "wild_wild_woods"' in ln)
anchor = next(i for i in range(ev_start, min(ev_start + 200, len(lines)))
              if '"staticLootMultiplier": 1.5,' in lines[i])
assert "containerLootInject" not in text, "already inserted"

value = cfg["containerLootInject"]
body = json.dumps(value, indent=2, ensure_ascii=False)
body_lines = body.splitlines()
block = '    "containerLootInject": ' + body_lines[0] + "\n"
block += "\n".join("    " + ln for ln in body_lines[1:])
insert = block + ",\n"

pos = sum(len(x) for x in lines[:anchor + 1])
new_text = text[:pos] + insert + text[pos:]
data = json.loads(new_text)
ev = next(e for e in data if e.get("id") == "wild_wild_woods")
assert "containerLootInject" in ev
assert ev["containerLootInject"]["woods"]["chance"] == 0.25
assert len(ev["containerLootInject"]["woods"]["categories"]) == 6
open(p, "wb").write((b"\xef\xbb\xbf" if bom else b"") + new_text.encode("utf-8"))
print("inserted after line", anchor + 1, "bom=", bom)
print("\n".join(new_text.splitlines()[anchor - 1:anchor + 8]))
