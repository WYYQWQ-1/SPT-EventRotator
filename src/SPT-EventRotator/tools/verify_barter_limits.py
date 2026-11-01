#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
★ v2.6.74 离线校验（不碰实机、不开服务端）：

① 交换配方（barter）—— 统计条数 + 逐条打印「限购数量 / 库存」值
   · `events.json` 里 `traderAssortAdd` + `traderBarterAdd` 共多少条
   · 每条 `maxPerRestock` 是多少（必须在 3~5；缺省会被代码兜成 1）
   · 库存（`upd.StackObjectsCount`）由代码固定写 —— 直接从
     `TraderAssortAddDimension.cs` 里抠出常量与 three-字段写法来证明：
     `UnlimitedCount:true` + `StackObjectsCount:9999999` + `BuyRestrictionMax:N` + `BuyRestrictionCurrent:0`
     （= EFT 本体 `database/traders/*/assort.json` 的「无限存量 + 限购」标准形态）

② 走私者 GP 币 —— 校验配置能真的落到「10~20 枚」
   · tpl 存在、父类链是 Money（否则不会被 `RandomiseMoneyStackSize` 处理）、StackMaxSize ≥ 20
   · 两场走私者活动（smugglers_2024 / smugglers_2026）都声明了 `botLootStack_`
   · role `arenaFighterEvent` 在本机有 `bots/types/arenafighterevent.json`
   · 代码侧两层都在：CurrencyStackSize 写入 + PrepareAndGenerateBot 产出物层

用法：python tools/verify_barter_limits.py [--spt-dir D:/PureEFTv4.1/EFTv4.1/SPT_Runtime]
退出码 0 = 全绿；1 = 有 ✘。
"""
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EVENTS = os.path.join(ROOT, "events.json")
ASSAULT_ADD = os.path.join(ROOT, "TraderAssortAddDimension.cs")
LOOT_STACK = os.path.join(ROOT, "BotLootStackDimension.cs")
PLUGIN = os.path.join(ROOT, "Plugin.cs")
BOSSWAVE = os.path.join(ROOT, "Patches", "BossWavePatch.cs")

SPT = "D:/PureEFTv4.1/EFTv4.1/SPT_Runtime"
if "--spt-dir" in sys.argv:
    SPT = sys.argv[sys.argv.index("--spt-dir") + 1]
DB = os.path.join(SPT, "SPT_Data", "database")

GP_TPL = "5d235b4d86f7742e017bc88a"
SMUGGLER_EVENTS = ("smugglers_2024", "smugglers_2026")
SMUGGLER_ROLE = "arenaFighterEvent"

ok_n = bad_n = 0


def ck(name, ok, detail=""):
    global ok_n, bad_n
    if ok:
        ok_n += 1
        print(f"  ✔ {name}" + (f"  {detail}" if detail else ""))
    else:
        bad_n += 1
        print(f"  ✘ {name}  {detail}")


def read(p):
    return io.open(p, encoding="utf-8-sig", errors="replace").read()


def main():
    if not os.path.isfile(EVENTS):
        print("✘ events.json 不存在：", EVENTS)
        return 1
    events = json.loads(read(EVENTS))
    print("=" * 90)
    print(f"① 交换配方（barter）—— events.json 共 {len(events)} 个活动")
    print("=" * 90)

    rows = []
    for ev in events:
        for fld in ("traderAssortAdd", "traderBarterAdd"):
            for trader, adds in (ev.get(fld) or {}).items():
                for it in adds:
                    rows.append((ev["id"], fld, trader, it))

    ck("barter 条目总数 = 74", len(rows) == 74, f"实际 {len(rows)}")
    on = [r for r in rows if "traderAssortAdd" in r[1]]
    ba = [r for r in rows if "traderBarterAdd" in r[1]]
    print(f"     · traderAssortAdd {len(on)} 条 / traderBarterAdd {len(ba)} 条")

    bad_limits = []
    from collections import Counter
    dist = Counter()
    for ev_id, fld, trader, it in rows:
        n = it.get("maxPerRestock")
        eff = n if (n is not None and n > 0) else 1     # 代码：>0 才写，≤0/缺省 = 不限... 缺省字段默认 1
        dist[eff] += 1
        if n is None or n < 3 or n > 5:
            bad_limits.append((ev_id, fld, trader, it.get("reward"), n))
    ck("每条 maxPerRestock 都显式写了、且落在 3~5", not bad_limits,
       "全部合规" if not bad_limits else f"不合规 {len(bad_limits)} 条 {bad_limits[:5]}")
    print("     限购档位分布：" + "，".join(f"{k} 件/次 = {dist[k]} 条" for k in sorted(dist)))

    # 代码侧：库存写法
    src = read(ASSAULT_ADD)
    m = re.search(r"UnlimitedStock\s*=\s*(\d+)", src)
    ck("代码常量 UnlimitedStock = 9999999（本体同款）", bool(m) and m.group(1) == "9999999",
       f"实际 {m.group(1) if m else '没找到'}")
    ck('items[] 写 "UnlimitedCount":true', '"UnlimitedCount":true' in src)
    ck('items[] 写 "StackObjectsCount":<UnlimitedStock>', '"StackObjectsCount":' in src and "+ UnlimitedStock" in src)
    ck('items[] 写 "BuyRestrictionMax":<maxPerRestock>', '"BuyRestrictionMax":' in src and "ad.MaxPerRestock" in src)
    ck('items[] 写 "BuyRestrictionCurrent":0', '"BuyRestrictionCurrent":0' in src)
    ck("maxPerRestock≤0 ⇒ 两个限购字段都不写（= 真不限购）", 'ad.MaxPerRestock > 0' in src)

    print()
    print("=" * 90)
    print("② 走私者 GP 币（10~20 枚）")
    print("=" * 90)

    items_path = os.path.join(DB, "templates", "items.json")
    if os.path.isfile(items_path):
        items = json.loads(read(items_path))
        it = items.get(GP_TPL)
        ck(f"tpl {GP_TPL} 在本机 items.json 存在", it is not None)
        if it:
            print(f"     · _name = {it.get('_name')}")
            # 父类链要含 Money
            chain, p = [], it.get("_parent")
            while p and len(chain) < 8:
                chain.append(items[p].get("_name", p))
                p = items[p].get("_parent")
            ck("父类链含 Money（否则不会被 RandomiseMoneyStackSize 处理）", "Money" in chain,
               " → ".join(chain))
            smax = (it.get("_props") or {}).get("StackMaxSize")
            ck("StackMaxSize ≥ 20（10~20 一叠合法）", (smax or 0) >= 20, f"StackMaxSize={smax}")
            w = (it.get("_props") or {}).get("Width")
            h = (it.get("_props") or {}).get("Height")
            ck("占格 1×1（保证 Pockets 有空位就能放）", w == 1 and h == 1, f"{w}×{h}")
    else:
        ck("items.json 可读", False, items_path)

    bot_type = os.path.join(DB, "bots", "types", "arenafighterevent.json")
    ck("bots/types/arenafighterevent.json 存在", os.path.isfile(bot_type), bot_type)

    loc = os.path.join(DB, "locales", "global", "en.json")
    if os.path.isfile(loc):
        en = json.loads(read(loc))
        ck("EN locale: ScavRole/ArenaFighterEvent = Smuggler（= 走私者）",
           en.get("ScavRole/ArenaFighterEvent") == "Smuggler", str(en.get("ScavRole/ArenaFighterEvent")))

    got = {}
    for ev in events:
        if ev.get("id") in SMUGGLER_EVENTS:
            got[ev["id"]] = ev.get("botLootStack_")
    for eid in SMUGGLER_EVENTS:
        spec = got.get(eid)
        good = (isinstance(spec, dict)
                and any(k.lower() == SMUGGLER_ROLE.lower() for k in spec)
                and any(v == [10, 20] for d in spec.values() for v in d.values()))
        ck(f"{eid} 声明了 botLootStack_[{SMUGGLER_ROLE}][{GP_TPL}] = [10,20]", good,
           json.dumps(spec, ensure_ascii=False) if spec is not None else "缺失")

    ls = read(LOOT_STACK)
    ck("配置层：写 BotConfig.CurrencyStackSize（每叠数量 10~20）",
       "CurrencyStackSize" in ls and "RoleLowercase" in ls)
    ck("产出物层：数量重写 + 合并成一叠", "StackObjectsCount" in ls and "items.Remove" in ls)
    ck("产出物层：没有就加进容器（找空格子）", "AddToContainer" in ls and "CellsH" in ls)
    ck("Plugin 已接线 ApplyBotLootStack", "ApplyBotLootStack(ev)" in read(PLUGIN))
    bs = read(BOSSWAVE)
    ck("PrepareAndGenerateBot postfix 已调 BotLootStackDimension.OnBotGenerated",
       "BotLootStackDimension.OnBotGenerated(__1, __result)" in bs)

    print()
    print("=" * 90)
    print("逐条明细（限购 / 库存）")
    print("=" * 90)
    stock = m.group(1) if m else "?"
    cur_ev = None
    for ev_id, fld, trader, it in rows:
        if ev_id != cur_ev:
            cur_ev = ev_id
            print(f"── {ev_id}（{fld if fld == 'traderBarterAdd' else 'traderAssortAdd'}）")
        lim = it.get("maxPerRestock")
        lim_s = f"{lim} 件/次" if lim else "不限购"
        print(f"   {trader:12s} {it.get('reward')}  ← {it.get('costCount')}× {it.get('cost')}"
              f"  LL{it.get('loyalty', 1)}  限购 {lim_s:10s} 库存 {stock}（UnlimitedCount=true）")

    print()
    print(f"总计：✔ {ok_n}  /  ✘ {bad_n}")
    return 0 if bad_n == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
