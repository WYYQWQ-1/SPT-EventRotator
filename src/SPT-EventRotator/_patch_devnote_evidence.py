# -*- coding: utf-8 -*-
"""把「容器类 tpl 不显示」的证据补进 devNote（诚实记录：反证 + 客户端唯一显式分支）。"""
import json, os

ROOT = os.path.dirname(os.path.abspath(__file__))
p = os.path.join(ROOT, "events.json")
d = json.load(open(p, encoding="utf-8-sig"))
e = next(x for x in d if x["id"] == "casus_belli")

old = (
    "**未证实**：没有找到「客户端过滤容器类 loose loot」的确切证据（未反编译客户端 loose loot 实例化路径），"
    "所以按「容器类物品当 loose loot 有风险」规避，而不是断言机制。"
)
new = (
    "**未证实**：没有找到「客户端过滤容器类 loose loot」的确切证据，所以按「容器类物品当 loose loot 有风险」规避，"
    "而不是断言机制。已查到的客观事实：① 客户端 `GameWorld.SpawnLoot`（audit/client_full/Assembly-CSharp.decompiled.cs:552171）"
    "只有**刷点模板** `IsContainer=true` 时才按静态容器 Id 匹配，找不到就打 `{lootId} is missing` 跳过（:552201）；"
    "我们替换后刷点的 `IsContainer` 仍是 false（ManimalInterchange 的 interchange looseLoot 3820 个点里 `IsContainer=true` = 0）"
    "⇒ 这条显式分支不解释本次现象。② **反证**：vanilla loose loot 里背包 56e335e4d2720b6c058b456d / 572b7adb24597762ae139821、"
    "文件包 590c60fc86f77412b13fddcf 等 `_props.Grids` 非空的物品本来就能正常落地 ⇒ **不是「容器一律不显示」**。"
    "③ 仍无法排除的是「原版武器箱这个 5×2 大件的 prefab/尺寸/摆位」问题（如穿模入地、`LootItem.CreateLoot` 在 prefab 缺组件时 NRE），"
    "但没有实机日志/客户端报错可证，故只登记为**未证实**。"
)
assert old in e["devNote"]
e["devNote"] = e["devNote"].replace(old, new, 1)

text = json.dumps(d, ensure_ascii=False, indent=2)
open(p, "w", encoding="utf-8-sig", newline="\r\n").write(text)
print("devNote updated, len", len(e["devNote"]))
