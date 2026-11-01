# -*- coding: utf-8 -*-
"""一次性数据修复：casus_belli 取箱目标改成自造物品 + interchange chance 归零。

只改三处：
  1. events.json → casus_belli.looseLootInject.interchange
       valuable.chance 1.0 → 0.0；valuable.tpls 只留新 tpl；region maxPoints 保持 2；devNote 追加说明
  2. questlines/casus_belli.json → items / itemMeta / locales(en,ch)
  3. 任务 2c9d5272b5674c718fadae75 的 FindItem / HandoverItem target → 新 tpl
"""
import json
import os
import shutil
import time

ROOT = os.path.dirname(os.path.abspath(__file__))
OLD_TPL = "59fb023c86f7746d0d4b423c"
NEW_TPL = "6a1f00c70000000000000001"
QUEST = "2c9d5272b5674c718fadae75"

STAMP = time.strftime("%Y%m%d_%H%M%S")


def load(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        return json.load(f)


def save(path, obj, indent):
    # 原文件都是 json.dumps(indent=N, ensure_ascii=False) + CRLF + UTF-8 BOM：
    # 已逐字符核对（len 完全相等），所以这样写回不会产生格式漂移。
    bak = path + ".bak_before_arenacrate_" + STAMP
    if not os.path.exists(bak):
        shutil.copy2(path, bak)
    text = json.dumps(obj, ensure_ascii=False, indent=indent)
    with open(path, "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write(text)


# ── 1. events.json ──────────────────────────────────────────────────────────
ev_path = os.path.join(ROOT, "events.json")
events = load(ev_path)
casus = next(e for e in events if e.get("id") == "casus_belli")
inter = casus["looseLootInject"]["interchange"]
assert inter["valuable"]["chance"] == 1.0, inter["valuable"]
inter["valuable"]["chance"] = 0.0
inter["valuable"]["tpls"] = {NEW_TPL: 1}
assert inter["decor"]["chance"] == 0.0
assert inter["regions"][0]["name"] == "小黄车"
assert inter["regions"][0]["maxPoints"] == 2
assert inter["densityMultiplier"] == 1.0

note = (
    " ｜ ★★★ 2026-10-24【取箱目标改自造物品 —— 修复「小黄车地上没有箱子」】"
    "(1) 原因：原版武器箱 " + OLD_TPL + " 是 SimpleContainer（`_props.Grids` 非空、5×2 容器类物品），"
    "当成 loose loot 单物品刷点落地后客户端**不显示**（玩家实机：小黄车地上没有箱子）。"
    "**未证实**：没有找到「客户端过滤容器类 loose loot」的确切证据（未反编译客户端 loose loot 实例化路径），"
    "所以按「容器类物品当 loose loot 有风险」规避，而不是断言机制。"
    "(2) 另一处是我的数据错误：interchange 的 `valuable.chance` 曾误写成 1.0 ⇒ 全图 3769 个点位全被替换"
    "（日志逐字「物品替换 valuable 3769 / decor 0」）。已改回 **0.0**，只靠 region「小黄车」的 `maxPoints: 2` "
    "做确定性替换 ⇒ 只有区内 2 个点被换。"
    "(3) 自造物品「竞技场奖励模板箱」= tpl " + NEW_TPL + "（定义在 questlines/casus_belli.json 的 "
    "items / itemMeta / locales），`_parent` 用可拾取 barter 类 590c745b86f7743cc433c5f2（Other items），"
    "**没有 `_props.Grids` ⇒ 不是容器**；2×2、重 6kg、Prefab 复用武器箱的 "
    "`item_container_weaponcase.bundle`（客户端没有新模型，复用现成箱子外观）。"
    "(4) 任务 " + QUEST + " 的 FindItem / HandoverItem target 都已从原版武器箱改成该 tpl。"
    "(5) 官方原设计是「21WS 钥匙卡开集装箱」，本实现改成「小黄车旁直接捡自造箱」——"
    "不用卡、不用开容器，直接躺在地上可拾取；这是**还原取舍**，不是官方行为。"
    "(6) 诊断：LooseLootInjectDimension 现在会对注入 tpl 查 TemplateTable.Items，"
    "若该物品 `_props.Grids` 非空（= 容器类 / IsContainer 的等价判据）会打 warning"
    "「这类当 loose loot 可能不显示」。"
)
if "取箱目标改自造物品" not in casus["devNote"]:
    casus["devNote"] = casus["devNote"] + note
save(ev_path, events, 2)

# ── 2. questlines/casus_belli.json ──────────────────────────────────────────
ql_path = os.path.join(ROOT, "questlines", "casus_belli.json")
ql = load(ql_path)
assert NEW_TPL not in ql["items"]

phone_props = {
    "AnimationVariantsNumber": 0,
    "BackgroundColor": "blue",
    "CanRequireOnRagfair": False,
    "CanSellOnRagfair": True,
    "ConflictingItems": [],
    "Description": "A crate snatched from a Pillager. Inside are the arena's brand-new reward templates, still sealed.",
    "DiscardLimit": 1,
    "DiscardingBlock": False,
    "DropSoundType": "None",
    "ExamineExperience": 10,
    "ExamineTime": 1,
    "ExaminedByDefault": True,
    "ExtraSizeDown": 0,
    "ExtraSizeForceAdd": False,
    "ExtraSizeLeft": 0,
    "ExtraSizeRight": 0,
    "ExtraSizeUp": 0,
    "Height": 2,
    "HideEntrails": False,
    "InsuranceDisabled": False,
    "IsAlwaysAvailableForInsurance": False,
    "IsLockedafterEquip": False,
    "IsSecretExitRequirement": False,
    "IsSpecialSlotOnly": False,
    "IsUnbuyable": False,
    "IsUndiscardable": False,
    "IsUngivable": False,
    "IsUnremovable": False,
    "IsUnsaleable": False,
    "ItemSound": "generic",
    "LeftHandItem": False,
    "LootExperience": 30,
    "MaxResource": 0,
    "MergesWithChildren": False,
    "Name": "Arena reward template crate",
    "NotShownInSlot": False,
    "Prefab": {
        "path": "assets/content/items/containers/item_container_weaponcase/item_container_weaponcase.bundle",
        "rcid": "",
    },
    "QuestItem": False,
    "QuestStashMaxCount": 0,
    "RagFairCommissionModifier": 1,
    "RarityPvE": "Rare",
    "RepairCost": 0,
    "RepairSpeed": 0,
    "Resource": 0,
    "ShortName": "Arena crate",
    "StackMaxSize": 1,
    "StackObjectsCount": 1,
    "Unlootable": False,
    "UnlootableFromSide": [],
    "UnlootableFromSlot": "FirstPrimaryWeapon",
    "UsePrefab": {"path": "", "rcid": ""},
    "Weight": 6,
    "Width": 2,
}
ql["items"][NEW_TPL] = {
    "_id": NEW_TPL,
    "_name": "casus_arena_template_crate",
    "_parent": "590c745b86f7743cc433c5f2",   # Other items（barter 可拾取类，无 Grids）
    "_props": phone_props,
    "_type": "Item",
}
ql["itemMeta"][NEW_TPL] = {
    "price": 150000,
    "handbookParent": "5b5f6fa186f77409407a7eb7",   # Storage containers（原武器箱同分类）
}
ql["locales"]["en"][NEW_TPL + " Name"] = "Arena reward template crate"
ql["locales"]["en"][NEW_TPL + " ShortName"] = "Arena crate"
ql["locales"]["en"][NEW_TPL + " Description"] = (
    "A crate snatched from a Pillager. Inside are the arena's brand-new reward templates, still sealed."
)
ql["locales"]["ch"][NEW_TPL + " Name"] = "竞技场奖励模板箱"
ql["locales"]["ch"][NEW_TPL + " ShortName"] = "模板箱"
ql["locales"]["ch"][NEW_TPL + " Description"] = (
    "从劫掠者手里抢来的箱子，里面装着竞技场新款的奖励模板，封条还没拆。"
)

# ── 3. 改任务条件 target ────────────────────────────────────────────────────
q = ql["quests"][QUEST]
patched = []
for cond in q["conditions"]["AvailableForFinish"]:
    if cond.get("conditionType") in ("FindItem", "HandoverItem"):
        if cond.get("target") == [OLD_TPL]:
            cond["target"] = [NEW_TPL]
            patched.append(cond["conditionType"])
assert sorted(patched) == ["FindItem", "HandoverItem"], patched
save(ql_path, ql, 1)

print("events.json  : valuable.chance=%.1f tpls=%s regions=%s"
      % (inter["valuable"]["chance"], list(inter["valuable"]["tpls"]),
         [(r["name"], r["maxPoints"]) for r in inter["regions"]]))
print("questline    : items=%s patched=%s" % (list(ql["items"]), patched))
print("locales      : en/ch 各 3 键")
