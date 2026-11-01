#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
★ v2.6.74【交换配方改「限购模式」】—— 一次性/可重复执行的数据改写脚本。

背景（用户原话）：「我们之前做完交换配方，但忘记给足库存，导致买完之后要重启服务端才能再买。
想要的是：存量无限，但商人每次刷新只能买那么多」。

SPT 4.1.6 侧的反编译证据（详见 `TraderAssortAddDimension.cs` 类注释「限购语义」一节）：
  · 存量 = `upd.StackObjectsCount`；购买时扣的是 `TradersTable` 里的活表，而补货
    (`TraderAssortHelper.ResetExpiredTrader`) 把「同一张活表」克隆回自己 ⇒ **卖空后不会补货**，
    只有重启服务端重新读库才恢复 ⇒ 旧实现把 `rewardCount`(默认1) 当库存 = 买 1 件就永久售罄。
  · 每次补货限购 = `upd.BuyRestrictionMax` + `upd.BuyRestrictionCurrent`（两个都必须存在才生效）；
    计数存 `profile.TraderPurchases`，在 `TraderController.Update`（商人补货）时被
    `ResetTraderPurchasesStoredInProfile` 清空 ⇒ **按补货周期自动重置，不需要重启**。

本脚本做的事：给 `events.json` 里**每一条** barter（`traderAssortAdd` + `traderBarterAdd`）
写上限购件数 `maxPerRestock`（3/4/5），配合代码侧固定写
`UnlimitedCount:true + StackObjectsCount:9999999` ⇒ 存量无限 + 每次补货限购 N 件。

限购档位（**mod 取值**，官方没给具体数字；用户口径「如 3~5」）：
  3 = 稀有 / 唯一 / 高价值一次性物：实验室钥匙卡（6 色 + 实验室访问卡）· 热成像瞄具 ·
      首领独特装备（含其专属弹药/枪管/护木/面罩/斧/烟斗）· 物品箱容器
  4 = 装备类：防弹插板 · 头盔 · 插板背心 · 成枪
  5 = 消耗 / 可囤类：各口径弹药 · 内存条

用法：
    python tools/apply_barter_limits.py            # 就地改写 events.json
    python tools/apply_barter_limits.py --check    # 只校验（不改写），退出码 1 = 不合规
"""
import io
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EVENTS = os.path.join(ROOT, "events.json")

# ── 档位 3：稀有 / 唯一 / 高价值一次性物 ──────────────────────────────────────
LIMIT3 = {
    # 实验室钥匙卡（6 色）
    "5c1d0c5f86f7744bb2683cf0",  # 蓝
    "5c1d0d6d86f7744bb2683e1f",  # 黄
    "5c1d0dc586f7744baf2e7b79",  # 绿
    "5c1d0efb86f7744baf2e7b7b",  # 红
    "5c1e495a86f7743109743dfb",  # 紫
    "5c1d0f4986f7744bb01837fa",  # 黑
    "5c94bbff86f7747ee735c08f",  # TerraGroup 实验室访问卡
    # 热成像瞄具（Thermal Scopes for Treats）
    "67641b461c2eb66ade05dba6",  # Cyclone Shakhin 3.7x
    "5d1b5e94d7ad1a2b865a96b0",  # FLIR RS-32
    "63fc44e2429a8a166c7f61e6",  # Zeus-Pro 640
    "6478641c19d732620e045e17",  # ECHO1
    # 首领独特装备（Permanent Boss Presence 的「狗牌换首领独特装备」那一半）
    "65268d8ecb944ff1e90ea385",  # RPDN 轻机枪
    "67dc255ee3028a8b120efc48",  # .50 BMG M33
    "67d4178bffb910d21f04720a",  # AK-50 24" 枪管
    "67d417c023ec241bb70d4896",  # AK-50 M-LOK 护木+导气管
    "67d416e19bd76ef20f0e743b",  # AK-50 防尘盖
    "6529243824cbe3c74a05e5c1",  # 6.8x51 SIG Hybrid
    "62963c18dbc8ab5f0d382d0b",  # Death Knight 面具
    "678f84bb9e85556ca60f0362",  # ZABEY 电焊面罩
    "679ba90d269ddfea47012159",  # 系链双刃斧
    "62a61bbf8ec41a51b34758d2",  # Big Pipe 的烟斗
    # 容器
    "59fb042886f7746c5005a7b2",  # 物品箱（softcore LL2 狗牌易物）
}

# ── 档位 4：装备类 ───────────────────────────────────────────────────────────
LIMIT4 = {
    # 防弹插板
    "656fa99800d62bcd2e024088",  # Cult Termite
    "656fa8d700d62bcd2e024084",  # Cult Locust
    "656f63c027aed95beb08f62c",  # Granit 4RS
    "64afc71497cf3a403c01ff38",  # Granit Br5
    "656fa25e94b480b8a500c0e0",  # Kiba Arms Titan
    # 头盔 / 背心
    "5aa7e276e5b5b000171d0647",  # Altyn
    "5fd4c474dd870108a754b241",  # 5.11 Hexgrid
    # 成枪
    "66ffa9b66e19cc902401c5e8",  # AA-12 Gen1
    "5bb2475ed4351e00853264e3",  # HK 416A5
    "5df8ce05b11454561e39243b",  # KAC SR-25
    "627e14b21713922ded6f2c15",  # AXMC .338
}

# ── 档位 5：消耗 / 可囤类 ────────────────────────────────────────────────────
LIMIT5 = {
    # 弹药
    "601949593ae8f707c4608daa",  # 5.56 SSA AP
    "5c0d5e4486f77478390952fe",  # 5.45 7N39
    "5a6086ea4f39f99cd479502f",  # 7.62x51 M61
    "5efb0c1bd79ff02a1f5e68d9",  # 7.62x51 M993
    "601aa3d2b2bcb34913271e6d",  # 7.62x39 MAI AP
    "5e023d48186a883be655e551",  # 7.62x54R 7N37
    "5c0d591486f7744c505b416f",  # 12/70 RIP
    # 材料
    "57347baf24597738002c6178",  # 内存条（RAM Shortage）
}

LIMITS = {}
LIMITS.update({k: 3 for k in LIMIT3})
LIMITS.update({k: 4 for k in LIMIT4})
LIMITS.update({k: 5 for k in LIMIT5})
assert len(LIMITS) == len(LIMIT3) + len(LIMIT4) + len(LIMIT5), "档位表里有重复 tpl"


def barters(events):
    """产出 (event_id, 字段名, 商人键, 条目引用)。"""
    for ev in events:
        for fld in ("traderAssortAdd", "traderBarterAdd"):
            for trader, adds in (ev.get(fld) or {}).items():
                for item in adds:
                    yield ev["id"], fld, trader, item


def main():
    check_only = "--check" in sys.argv
    raw = io.open(EVENTS, encoding="utf-8-sig").read()
    events = json.loads(raw)

    rows, unknown = [], []
    for ev_id, fld, trader, item in barters(events):
        tpl = item["reward"]
        if tpl not in LIMITS:
            unknown.append((ev_id, tpl))
            continue
        want = LIMITS[tpl]
        old = item.get("maxPerRestock")
        item["maxPerRestock"] = want
        rows.append((ev_id, fld, trader, tpl, item.get("cost"), item.get("costCount"),
                     item.get("loyalty"), old, want))

    if unknown:
        print("✘ 有 reward tpl 没被档位表覆盖（拒绝写入）：")
        for x in unknown:
            print("   ", x)
        return 1

    # 覆盖率自证：events.json 里出现的每个 reward tpl 都必须在档位表里
    all_tpls = {i["reward"] for _, _, _, i in barters(events)}
    assert all_tpls <= set(LIMITS), "档位表覆盖不全"

    print(f"barter 条目总数 = {len(rows)}（traderAssortAdd + traderBarterAdd）")
    from collections import Counter
    c = Counter(r[8] for r in rows)
    print("限购档位分布：" + "，".join(f"{k} 件/次 = {c[k]} 条" for k in sorted(c)))
    print()
    cur_ev = None
    for ev_id, fld, trader, tpl, cost, costCount, loyalty, old, want in rows:
        if ev_id != cur_ev:
            cur_ev = ev_id
            print(f"── {ev_id}")
        print(f"   {fld:16s} {trader:12s} {tpl} ← {costCount}× {cost}  LL{loyalty}  "
              f"限购 {old if old is not None else '(缺省1)'} → {want}")

    if check_only:
        bad = [r for r in rows if r[7] != r[8]]
        print()
        if bad:
            print(f"✘ 有 {len(bad)} 条 maxPerRestock 与档位表不一致（需重跑本脚本）")
            return 1
        print("✔ 全部条目 maxPerRestock 已与档位表一致")
        return 0

    out = json.dumps(events, ensure_ascii=False, indent=2)
    # 保底：格式必须与原文完全同构（本文件就是 indent=2/无 ensure_ascii/无尾换行）
    io.open(EVENTS, "w", encoding="utf-8", newline="\n").write(out)
    print()
    print(f"✔ 已写入 {EVENTS}（{len(rows)} 条 barter 全部带上 maxPerRestock）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
