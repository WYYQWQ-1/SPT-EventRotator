# -*- coding: utf-8 -*-
"""往 events.json 的 casus_belli.devNote 追加 2026-10-24【第二轮】结论（格式与文件同构：BOM + CRLF + indent=2）。"""
import io
import json

EV = "events.json"

NOTE = (
    " | ★★★ 2026-10-24【第二轮：外观改成武器箱 + 小黄车可见性（本段取代上面「取箱目标改自造物品」里关于 prefab 的结论）】"
    "(1) 上一轮把 Prefab 换成 675a081c1474133a0d0bb214 是**错的**：该 tpl 的本体 locale en `Name` = "
    "「Things Sure Are Strange poster」/ `ShortName` =「Poster」，即**一张海报**——与玩家实机反馈「像一张海报」完全一致。"
    "上一轮的理由「该 tpl 出现在 12 张图的原版 looseLoot 里 ⇒ 已被证明能当 loose loot 正常落地」只证明它会落地，"
    "**不证明它是个箱子**。"
    "(2) 现在 `_props.Prefab` = 原版武器箱 `59fb023c86f7746d0d4b423c` 的 `Prefab` **逐字照抄** = "
    "`assets/content/items/containers/item_container_weaponcase/item_container_weaponcase.bundle`"
    "（本体共 54 个 tpl 共用；该 tpl 自身 5×2、`Grids` = main 5×10）；我们的格子取 **3×2**"
    "（5 宽塞不进 4×4 口袋 ⇒ 玩家没背包就捡不起来；模型尺寸由 bundle 决定，不受 Width/Height 缩放）；"
    "`_props.Grids` 显式写 `[]`。`5909d7cf86f77470ee57d75a`（Weapon box，静态木箱）的 `_props.Prefab.path` 是"
    "**空字符串** ⇒ 做不了 prefab 来源，所以可选项其实只有 59fb023c 一个。"
    "(3) 「小黄车看不到」的实测（实机真正加载的 ManimalInterchange 那份 3820 点 looseLoot，只读）：盒内命中 6 个点，"
    "最近 2 点 3D 距车 **4.51 / 4.75 m**（(111.29,22.54,286.69) / (112.34,22.51,286.83)，车 (110.16,21.34,290.89)，"
    "dy≈+1.2 m）⇒ **坐标不是问题**；把 x/z 交换后盒内命中 **0** 个 ⇒ 与 woods 同一套世界坐标系，不需要交换轴或 y 偏移。"
    "(4) region「小黄车」新增三个字段：`anchor{x:110.16,y:21.34,z:290.89}` + `anchorName:\"车\"`"
    "（按**到车的真实 3D 距离**挑最近 N 个点，并逐点打日志 `interchange：小黄车 选区替换点 (x, y, z) 距车 d 米`，"
    "每点一行、最多 5 行、每活动首次生成时打一次）、`guaranteedPoints: 1`（最近那个点把 `Spawnpoint.Probability` 设 1.0；"
    "依据 SPT `LocationLootGenerator.GenerateDynamicLoot`：`if (spawnpoint.Probability == 1.0) list3.Add(spawnpoint)` "
    "⇒ 不进 `DrawAndRemove` 的概率抽取、每局必刷。原来单点落地率 ≈ 73%（另一点 ≈71%）"
    "——`num = round(2.8 × Normal(621.244,18.379)) ≈ 1739` 从 3820 点里抽、Σp=578.03），`maxPoints` 2 → 3。"
    "(5) 兜底方案「在小黄车位置 append 一个静态容器」**做不到**（不是「没做」）：实机 interchange 的 906 个静态容器 "
    "`Position` **全是 (0,0,0)**、`Id` 全是场景实例名（`Lootable_00000…`）、`statics.json` 只有 `Id→groupId` 没有坐标；"
    "客户端 `GameWorld.SpawnLoot`（Assembly-CSharp.decompiled.cs:552171-552231）对 `IsContainer` 刷点是 "
    "`array2.FirstOrDefault(x => x.Id == lootId)` 在**场景对象**里找，找不到就 `Debug.LogError(lootId + \" is missing\")` "
    "⇒ 新 Id 永远不生成。⇒ 「特定地点放特定任务物品」只剩 loose loot 这条路。"
    "(6) 未实机验证（按要求只读取证、未部署）；详见 `fix/SPT-EventRotator/小黄车两实机问题-外观与可见性-20261024.md`。"
)


def main():
    raw = io.open(EV, encoding="utf-8-sig").read()
    d = json.loads(raw)
    hit = 0
    for e in d:
        if e.get("id") != "casus_belli":
            continue
        if "第二轮：外观改成武器箱" in (e.get("devNote") or ""):
            print("devNote 已含本轮结论，跳过")
            return 0
        e["devNote"] = (e.get("devNote") or "") + NOTE
        hit += 1
    assert hit == 1, hit
    out = json.dumps(d, ensure_ascii=False, indent=2).replace("\n", "\r\n")
    io.open(EV, "w", encoding="utf-8", newline="").write("\ufeff" + out)
    print("已追加 devNote（%d 字）" % len(NOTE))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
