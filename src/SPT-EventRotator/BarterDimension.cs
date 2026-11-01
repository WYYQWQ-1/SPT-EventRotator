using System;
using System.Collections.Generic;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ 商人易物注入 —— 活动字段 `traderBarterAdd`
/// （官方「Cultist Pilgrimage / 邪教朝圣」：Mechanic **收邪教徒之刃、出 6 色实验室门卡**）。
///
/// ── ★★ 本文件已**统一到唯一实现** `TraderAssortAddDimension`（task-34 收尾）────────────────
///   历史：这里原先是"**克隆该商人已有的任一条易物条目**再改 tpl"的做法，它有两处**硬故障**，
///   导致 `traderBarterAdd` 从上线起**一次都没注入成功过**（`cultist_pilgrimage` 那 6 条门卡配方
///   数据完全正确，却全被静默吞掉）：
///
///   ① (旧 `:94`) `schemes[newId] = scheme;` —— `newId` 是 **string**，而
///      `TraderAssort.BarterScheme` 的键类型是 **`MongoId`**
///      （`Models.Eft.Common.Tables/TraderAssort.cs:43-44`）⇒ 抛
///      `ArgumentException: The value "…" is not of type "…MongoId"`；
///      更早一步 (旧 `:76`) `schemes.Contains(id)` 就已经因为同样原因**恒为 false**，
///      于是旧 `:79` `schemes[tplId]` 用 string 取 MongoId 字典**当场就抛**。
///      异常被旧 `:122` 的 `catch` 吞掉 ⇒ **整组静默失败**（连一条 warning 都没有"注入失败"的意思）。
///   ② (旧 `CloneScheme`) 返回的是**外层集合**而不是该有的 value 层级
///      （旧 `:189-192` `return outerNew;`）⇒ 即使 ① 修好，写进字典的 value 也会**多一层**，
///      与 SPT 的读法 `TraderController.cs:64-66`
///        `BarterScheme barterScheme = item.Value.FirstOrDefault()?.FirstOrDefault();`
///      不匹配（value 应为 `List<List<BarterScheme>>`）。
///
///   ⇒ 现在本类**不再自己拼结构**，改为把参数翻译成 `EventDef.TraderAssortAdd`，
///     调用 <see cref="TraderAssortAddDimension"/>（用 JSON 造强类型对象 + `R.DictSet` 做
///      MongoId 键转换 + 显式幂等 + 三键回读自证）。**两套实现合并为一套**，
///     `traderBarterAdd` 字段与既有 `events.json` 数据**语义不变、无需迁移**。
///
/// ── 行为兼容 ──────────────────────────────────────────────────────────────────
///   · `traderBarterAdd` 的 JSON 结构（`{reward, rewardCount, cost, costCount, loyalty, maxPerRestock}`）
///     一字不改，`cultist_pilgrimage` 现有数据**直接生效**。
///   · `rewardCount`：旧实现**完全忽略**它（恒克隆成模板的 1 件）。新实现**兑现**它
///     （>1 时写 `StackObjectsCount`）；现有数据的 `rewardCount` 全是 1 ⇒ **行为一致**。
///   · `costLevel` / `side`：旧实现是从克隆模板**继承**（随机取决于哪条条目被选中），
///     新实现默认 `costLevel=1` / 不写 `side`（= 本体默认 Any）⇒ 更确定、更可预期。
///     要用等级/阵营限定，写 `costLevel` / `side` 即可。
/// </summary>
internal static class BarterDimension
{
    public static ISptLogger<EventRotatorServer> Log;
    public static Action<Action> AddRestore;
    public static object TraderTable;

    /// <summary>
    /// 把一批易物条目注入指定商人（`traderKey` = 商人 **id** 或昵称）。
    /// 幂等：同一 (reward, cost) 已存在则跳过；可还原：闭包把 `_id` 从三处一起摘掉。
    /// </summary>
    public static void Apply(string traderKey, List<BarterAddDef> adds)
    {
        if (adds == null || adds.Count == 0) return;
        if (string.IsNullOrWhiteSpace(traderKey))
        {
            Log?.Warning("[EventRotator][易物] traderBarterAdd 的商人键为空，跳过");
            return;
        }
        try
        {
            // ★ 统一实现：翻译成 TraderAssortAdd 后交给唯一实现去做。
            var ev = new EventDef
            {
                TraderAssortAdd = new Dictionary<string, List<BarterAddDef>>(StringComparer.OrdinalIgnoreCase)
                {
                    [traderKey] = adds
                }
            };

            TraderAssortAddDimension.TraderTable = TraderTable;
            TraderAssortAddDimension.Log = m => Log?.Info(m);
            TraderAssortAddDimension.AddRestore = AddRestore;
            TraderAssortAddDimension.Apply(ev, Log);
        }
        catch (Exception ex)
        {
            // ★ 不静默：降级但必须可见（旧实现在这里只吞异常，正是"看不见的坏"的根源）。
            Log?.Warning($"[EventRotator][易物] 注入 '{traderKey}' 失败（已降级，不影响服务端）: {ex.Message}");
        }
    }
}
