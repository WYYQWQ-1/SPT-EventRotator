# -*- coding: utf-8 -*-
"""把新物品的 Prefab 换成「实机 looseLoot 已验证会落地的 2×2 箱子」，
并把这件事写进 devNote（原版武器箱 prefab 是本次现象的可疑因素之一，能避开就避开）。"""
import json, os

ROOT = os.path.dirname(os.path.abspath(__file__))
NEW = "6a1f00c70000000000000001"
PRECEDENT = "675a081c1474133a0d0bb214"   # item_barter_flyers_strange_case：2×2 / Grids=0 / 12 张图 looseLoot 里出现
NEW_PREFAB = "assets/content/items/barter/item_barter_flyers/item_barter_flyers_strange_case.bundle"

# ── questline：改 Prefab ────────────────────────────────────────────────────
qp = os.path.join(ROOT, "questlines", "casus_belli.json")
ql = json.load(open(qp, encoding="utf-8-sig"))
ql["items"][NEW]["_props"]["Prefab"]["path"] = NEW_PREFAB
open(qp, "w", encoding="utf-8-sig", newline="\r\n").write(json.dumps(ql, ensure_ascii=False, indent=1))

# ── events.json：devNote 改两处措辞 ──────────────────────────────────────────
ep = os.path.join(ROOT, "events.json")
d = json.load(open(ep, encoding="utf-8-sig"))
e = next(x for x in d if x["id"] == "casus_belli")

old1 = "Prefab 复用武器箱的 `item_container_weaponcase.bundle`（客户端没有新模型，复用现成箱子外观）。"
new1 = (
    "Prefab 复用本机已存在、且**实机 looseLoot 里已验证会落地显示**的 2×2 箱子 "
    + PRECEDENT + "（item_barter_flyers_strange_case，`_props.Grids`=0，出现在 12 张图的 looseLoot 里）"
    "的 `item_barter_flyers_strange_case.bundle`（客户端没有新模型，复用现成 2×2 箱子外观；"
    "**刻意不复用原版武器箱的 prefab**，避免它是不是也属于本次「看不见」的可疑因素）。"
)
assert old1 in e["devNote"]
e["devNote"] = e["devNote"].replace(old1, new1, 1)

old2 = "③ 仍无法排除的是「原版武器箱这个 5×2 大件的 prefab/尺寸/摆位」问题"
new2 = ("③ 仍无法排除的是「原版武器箱这个 5×2 大件的 prefab/尺寸/摆位」问题。"
        "④ 因此新物品改成 2×2，并改用本机另一个 2×2 箱子 " + PRECEDENT + " 的 prefab"
        "（该 tpl 在 12 张图的原版 looseLoot 里出现 ⇒ 同一 prefab 已被证明能当 loose loot 正常落地）")
assert old2 in e["devNote"]
e["devNote"] = e["devNote"].replace(old2, new2, 1)

open(ep, "w", encoding="utf-8-sig", newline="\r\n").write(json.dumps(d, ensure_ascii=False, indent=2))
print("prefab ->", NEW_PREFAB)
print("devnote len", len(e["devNote"]))
