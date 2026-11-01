#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
B1 / B2 可判读自证：把 BossWavePatch.ApplyOneEvent 里**真实代码结构**抽成判定函数，
逐挂点回放"同一活动连续进两局同图"的调用序列，对比 旧顺序 / 新顺序 的 addedThisRaid。

这不是重实现业务逻辑，而是把 C# 里那 3 个判定变量（_lastRaidMapObj / _lastRaidCount / _seenRaid）
与那个 if 块**逐行**照搬过来，用来证明"第二局不再被 return 掉"。
"""
import sys

# ── 真实调用序列（来自反编译 SPT 源码，不是猜） ────────────────────────────
# LocationLifecycleService.GenerateLocationAndLoot:
#   :263  locationBase = cloner.Clone(location.Base)      <-- 每局一个新对象
#   :279  raidTimeAdjustmentService.MakeAdjustmentsToMap(raidChanges, locationBase)
#           -> RaidTimeAdjustmentService:68  if (GetMapSettings(Id).AdjustWaves) AdjustWaves(mapBase, ...)
#                                                    ^^^ 我们的 Prefix 挂点 = "pre"
#         :279 返回后 -> 我们的 Postfix 挂点 = "post"（同一个 locationBase）
#   => 一局内同对象上依次到达： pre, post   （raidChanges == null 时只有 post）


def simulate(order_is_fixed, n_raids=3, hooks=("pre", "post"), verbose=True):
    """order_is_fixed=False 复刻旧代码（seen 判定在前，新一局判定在后）
       order_is_fixed=True  复刻新代码（新一局判定在前，seen 判定在后）"""
    _seenRaid = set()
    _lastRaidMapObj = None
    _lastRaidCount = -1
    rows = []
    for raid in range(1, n_raids + 1):
        mapBase = object()          # cloner.Clone(location.Base) → 每局全新对象
        list_count = 10             # 该图原生条目数
        for hook in hooks:
            # ---- 旧顺序：先 seen 判定（含 >512 兜底），后判新一局 ----
            if not order_is_fixed:
                if len(_seenRaid) > 512:
                    _seenRaid.clear()
                addedThisRaid = ("ev|map" not in _seenRaid)
                _seenRaid.add("ev|map")
            # ---- 新一局判定（两种顺序下代码相同，只是位置不同）----
            curCount = list_count
            new_raid = (mapBase is not _lastRaidMapObj) or (curCount < _lastRaidCount)
            if new_raid:
                _lastRaidMapObj = mapBase
                _lastRaidCount = curCount
                # _spawned/_spawnedEvent/_seenBotIds/_loggedRaidPlanFor 也在这里清（与本条无关）
                if order_is_fixed:
                    _seenRaid.clear()          # ★ B1 修复：补上漏清的 _seenRaid
            else:
                _lastRaidCount = curCount
            # ---- 新顺序：seen 判定挪到新一局判定之后 ----
            if order_is_fixed:
                if len(_seenRaid) > 512:
                    _seenRaid.clear()
                addedThisRaid = ("ev|map" not in _seenRaid)
                _seenRaid.add("ev|map")

            # 追加阶段（addedThisRaid==False ⇒ 旧代码在 :1550 直接 return）
            appended = addedThisRaid
            rows.append((raid, hook, new_raid, addedThisRaid, appended))
            if verbose:
                flag = "追加 ✓" if appended else "被 return ✗（BOSS 本局不存在）"
                print(f"  第{raid}局 {hook:4s} 新一局判定={'是' if new_raid else '否'} "
                      f"addedThisRaid={str(addedThisRaid):5s} -> {flag}")
    return rows


def b2_trace():
    """B2：escortCount 覆盖块的执行位置 vs 追加位置（行号取自改后文件）"""
    print("  [旧] 1.4 escortCount 覆盖 @:1531  →  1.5 seasonal 追加 @:1542  →  2 bossSpawns 追加 @:1615")
    print("       ⇒ 首次调用时 escortCount 遍历的是**还没有该 boss** 的 list ⇒ 扑空")
    print("       ⇒ 只能等同局第二个挂点（post）再跑一次来补救（依赖隐式时序）")
    print("  [新] 1.5 seasonal 追加 @:1542  →  2 bossSpawns 追加 @:1615  →  1.4 escortCount 后置 @:1629")
    print("       ⇒ 追加完成后同一次调用内立即覆盖 ⇒ 本局第一次落地就是活动写的护卫数")


if __name__ == "__main__":
    print("=" * 78)
    print("B1 自证：同一活动、同一张图，连续进 3 局（每局 pre+post 两个挂点）")
    print("=" * 78)
    print("\n【改前 · 旧顺序】seen 判定在 新一局判定 之前：")
    old = simulate(order_is_fixed=False)
    print("\n【改后 · 新顺序】新一局判定（含 _seenRaid.Clear()）在 seen 判定 之前：")
    new = simulate(order_is_fixed=True)

    print("\n" + "=" * 78)
    print("判定")
    print("=" * 78)
    old_append_raids = sorted({r[0] for r in old if r[4]})
    new_append_raids = sorted({r[0] for r in new if r[4]})
    print(f"  改前：实际发生「追加」的局 = {old_append_raids}   ← BUG：只有第 1 局")
    print(f"  改后：实际发生「追加」的局 = {new_append_raids}   ← 修好：每局都追加")
    ok = new_append_raids == [1, 2, 3] and old_append_raids == [1]
    print(f"  PASS={ok}")

    print("\n" + "=" * 78)
    print("B2 自证：escortCount 覆盖块的位置")
    print("=" * 78)
    b2_trace()
    print()
    sys.exit(0 if ok else 1)
