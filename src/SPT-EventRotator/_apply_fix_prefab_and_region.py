# -*- coding: utf-8 -*-
"""一次性的数据落点脚本（2026-10-24）：
① questlines/casus_belli.json 的自造物品 6a1f00c70000000000000001：
   Prefab 换成原版武器箱 59fb023c86f7746d0d4b423c 的 Prefab；_props.Grids = []；尺寸 3×2。
② events.json 的 casus_belli.looseLootInject.interchange.regions[「小黄车」]：
   加 anchor（车坐标，用于 3D 距离排序 + 日志「距车 d 米」）、anchorName、
   "guaranteedPoints": 1；maxPoints 2 → 3。

只改这两个文件；格式（BOM + CRLF + indent）与改前**逐字节同构**，先 dry-run 再写。
"""
import json
import sys

WEAPONCASE_TPL = "59fb023c86f7746d0d4b423c"   # item_container_weaponcase（原版武器箱）
NEW_TPL = "6a1f00c70000000000000001"
CART = (110.16, 21.34, 290.89)

QL = "questlines/casus_belli.json"
EV = "events.json"
ITEMS = r"D:/PureEFTv4.1/EFTv4.1/SPT_Runtime/SPT_Data/database/templates/items.json"


def load(p):
    raw = open(p, "rb").read().decode("utf-8-sig").replace("\r\n", "\n")
    return json.loads(raw)


def save(p, obj, indent):
    out = json.dumps(obj, ensure_ascii=False, indent=indent).replace("\n", "\r\n")
    open(p, "wb").write(("\ufeff" + out).encode("utf-8"))


def insert_after(d, key, value, after):
    """返回新 dict：key 插到 after 之后（`after` 不存在则追加到末尾）"""
    out = {}
    done = False
    for k, v in d.items():
        if k == key:
            continue
        out[k] = v
        if k == after:
            out[key] = value
            done = True
    if not done:
        out[key] = value
    return out


def main(apply):
    ql = load(QL)
    ev = load(EV)

    props = ql["items"][NEW_TPL]["_props"]

    prefab = None
    if apply:
        items = json.load(open(ITEMS, encoding="utf-8-sig"))
        src = items[WEAPONCASE_TPL]["_props"]
        prefab = src["Prefab"]
        print("原版武器箱 %s：Prefab=%s  W/H=%s/%s  Grids=%d"
              % (WEAPONCASE_TPL, prefab["path"], src["Width"], src["Height"],
                 len(src.get("Grids") or [])))
        props["Prefab"] = {"path": prefab["path"], "rcid": prefab.get("rcid", "")}
    else:
        print("(dry-run) 当前 Prefab=%s" % props.get("Prefab", {}).get("path"))

    props["Width"] = 3
    props["Height"] = 2
    ql["items"][NEW_TPL]["_props"] = insert_after(props, "Grids", [], "ExaminedByDefault")

    reg = None
    for e in ev:
        if e.get("id") == "casus_belli":
            for r in e["looseLootInject"]["interchange"]["regions"]:
                if r.get("name") == "小黄车":
                    reg = r
    if reg is None:
        print("!! 找不到「小黄车」region")
        return 2

    reg["maxPoints"] = 3
    reg["guaranteedPoints"] = 1
    reg.pop("anchor", None)
    reg.pop("anchorName", None)
    reg.clear()
    reg.update({
        "name": "小黄车",
        "min": {"x": 98.0, "z": 278.0},
        "max": {"x": 124.0, "z": 304.0},
        "valuableBoost": 1.0,
        "maxPoints": 3,
        "guaranteedPoints": 1,
        "anchor": {"x": CART[0], "y": CART[1], "z": CART[2]},
        "anchorName": "车",
    })

    p2 = ql["items"][NEW_TPL]["_props"]
    print("新物品：Prefab=%s  W/H=%s/%s  Grids=%r"
          % (p2["Prefab"]["path"], p2["Width"], p2["Height"], p2["Grids"]))
    print("region：%s" % json.dumps(reg, ensure_ascii=False))

    if not apply:
        print("\n(dry-run，未写盘)")
        return 0

    save(QL, ql, 1)
    save(EV, ev, 2)
    print("\n已写入 %s / %s" % (QL, EV))
    return 0


if __name__ == "__main__":
    sys.exit(main("--apply" in sys.argv))
