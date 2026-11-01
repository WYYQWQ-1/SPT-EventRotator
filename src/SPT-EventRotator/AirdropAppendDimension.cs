using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace EventRotator;

/// <summary>
/// ★★ v0.1.5 新增：**空投「追加」语义**（补上审计指出的设计缺陷）。
///
/// 旧实现（`airdropAddLoot`）走的是 SPT 的 `loot.&lt;类型&gt;.useForcedLoot = true` + `forcedLoot`，
/// 而反编译实证（`AirdropService.GenerateAirdropLoot`）：
/// <code>
/// if (!(config.UseForcedLoot ?? false)) pool = lootGenerator.CreateRandomLoot(config);
/// else                                   pool = lootGenerator.CreateForcedLoot(config.ForcedLoot);   // ← 整箱替换！
/// </code>
/// ⇒ 打开 `useForcedLoot` 会**跳过全部随机战利品**，把箱子变成"只装指定物品"。
/// 官方 Weapon Case Airdrops（2023-06-19）要的是"空投箱里**出现**已密封的武器箱"，是**追加**，不是替换。
///
/// 另一条路 `itemTypeWhitelist` 也不行：`LootGenerator.GetItemRewardPool` 的过滤条件是
/// `itemTypeWhitelist.Contains(item.Parent)` —— 它按**基类**匹配（会放开整类物品），
/// 而且显式排除 `Properties.QuestItem`。
///
/// 所以这里用最直接的办法：**Postfix `AirdropService.GenerateAirdropLoot()`**，
/// 在返回的箱子里**追加**指定物品（带正确的 `parentId` / `slotId` / `StackObjectsCount`）。
/// </summary>
internal static class AirdropAppendDimension
{
    /// <summary>待追加的物品（tpl → 数量）。空 = 不干预。</summary>
    public static readonly List<(string Tpl, int Count)> Items = new();
    /// <summary>
    /// ★ v2.6.38 **本条登记的**地图过滤（= 声明 `airdropAppendLoot` 的那个活动自己的 `onlyOnMap`）。
    /// ⚠ 绝不能改用全局过滤 —— v2.6.37 回归就是"全局判据误伤没声明 `onlyOnMap` 的活动"
    ///   （`weapon_case_airdrops` / `smugglers_2026` 的空投物品会在非 bigmap 全丢）。
    /// 空 = 不过滤（原行为）。
    /// </summary>
    public static string OnlyOnMap;
    public static Action<string> Log;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][空投] " + m); } catch { } }

    /// <summary>由 Plugin 在活动套用时设置；传空 = 关闭追加。</summary>
    public static void Set(IEnumerable<KeyValuePair<string, int>> items, string onlyOnMap = null)
    {
        Items.Clear();
        OnlyOnMap = string.IsNullOrWhiteSpace(onlyOnMap) ? null : onlyOnMap.Trim();
        if (items != null)
            foreach (var kv in items)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0) Items.Add((kv.Key, kv.Value));
        if (Items.Count > 0)
            Info($"已登记活动物品追加：{string.Join("、", Items.ConvertAll(x => x.Tpl + "×" + x.Count))}（每次空投箱生成时追加）"
               + (OnlyOnMap != null ? $"　★仅 '{OnlyOnMap}' 生效" : ""));
    }

    /// <summary>Postfix `AirdropService.GenerateAirdropLoot()` —— 往生成的箱子里追加物品。</summary>
    public static void Postfix(object __result)
    {
        if (Items.Count == 0 || __result == null) return;
        // ★★★ v2.6.37【只在该地图生效】判据 = **本条登记自己的** `OnlyOnMap`（不是全局）。
        //   兜底在 MapGateDimension.AllowedFor()（拿不到地图 ⇒ 照旧追加 + warning）。
        if (!MapGateDimension.AllowedFor(OnlyOnMap)) return;
        try
        {
            var container = __result.GetType().GetProperty("Container",
                BindingFlags.Public | BindingFlags.Instance)?.GetValue(__result) as IList;
            if (container == null || container.Count == 0) return;

            // 第 0 个元素就是空投箱本体（GenerateAirdropLoot 里 crateItem 先被 Append 进列表）
            var crate = container[0];
            var crateId = crate?.GetType().GetProperty("Id")?.GetValue(crate)?.ToString();
            if (string.IsNullOrEmpty(crateId)) return;

            int added = 0;
            foreach (var (tpl, cnt) in Items)
            {
                var item = new Item
                {
                    Id = new MongoId(),
                    Template = new MongoId(tpl),
                    ParentId = crateId,
                    SlotId = "main",
                    Upd = new Upd { StackObjectsCount = cnt },
                };
                container.Add(item);
                added++;
            }
            Info($"★本局空投箱已追加 {added} 种活动物品（箱 {crateId}）");
        }
        catch (Exception ex) { Info("追加失败: " + ex.Message); }
    }
}
