using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ task-34 ⑨-A：**藏身处升级需求注入**（官方 `beer_upgrades`：把 Pevko 啤酒加进藏身处升级材料）。
///
/// ── 落点（已实证，见 audit/能做没做-三维度.md）────────────────────────────────
///   `hideout/areas.json` 是**数组**（27 项），每项 `HideoutArea`：
///     · `HideoutArea.Stages : Dictionary&lt;string, Stage&gt;`（键 "0".."3"，有的到 "4"）
///     · `Stage.Requirements : List&lt;StageRequirement&gt;`
///   SPT 读点（**只读 Stages，从不读 levels**）：
///     · `HideoutController.cs:86`  `hideoutArea.Stages[(level+1).ToString()].ConstructionTime`
///     · `HideoutController.cs:124` `hideoutArea.Stages.TryGetValue(num.ToString(), out Stage)`
///     · `HideoutController.cs:205` `childDbArea.Stages.TryGetValue(profileParentHideoutArea.Level.ToString(), …)`
///     · `HideoutController.cs:1093` `hideoutArea.Stages[level.ToString()].Improvements`
///     · `HideoutHelper.cs:1088` / `ProfileFixerService.cs:481-487` / `CircleOfCultistService.cs:511`
///   ⇒ **`levels` 在 SPT 里根本不存在**（全库 grep `JsonPropertyName("levels")` 只命中
///      `Models.Spt.Config/RewardScaling.cs`，与藏身处无关；`HideoutArea` 没有该属性）。
///      所以 **只有 `stages` 生效** —— 本次任务要验的未验点，答案是：**stages**。
///   实测全库 463 条需求：`Item` 315 / `Area` 113 / `TraderLoyalty` 26 / `Skill` 9
///   ⇒ 结构本来就支持塞 `Item` 需求，官方自己也这么用（例 `5449016a4bdc2d6f028b456f` 卢布 ×25000）。
///
/// ── 幂等 / 还原 / 降级 ────────────────────────────────────────────────────────
///   · 幂等：先扫 `requirements[]`，**同一 (templateId) 已存在就跳过** ⇒ 重复套用不会重复追加。
///   · 还原：`Remove` 闭包登记进 `_restoreActions`（Plugin 侧**逆序 LIFO**执行）⇒ 干净回退。
///   · 降级：找不到区域/阶段/结构不符 ⇒ 只打 warning 并 continue，**绝不抛**。
///   · 自证：追加后**回读**该 requirements 列表，确认真的多了一条。
/// </summary>
internal static class HideoutRequirementDimension
{
    public static object LocationTable;           // 本维度不用，但保持与其它维度一致的签名（Plugin 统一注入）
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][藏身处需求] " + m); } catch { } }
    private static void Warn(string m) { try { Log?.Invoke("[EventRotator][藏身处需求] ⚠ " + m); } catch { } }

    /// <summary>
    /// 应用 `hideoutAddRequirements`。
    /// 写法：<c>{ "&lt;area 模板 id 或 type 序号&gt;": [ { "stage": 2, "templateId": "…", "count": 2 } ] }</c>
    /// </summary>
    public static void Apply(EventDef ev, object hideoutTable, ISptLogger<EventRotatorServer> logger)
    {
        if (ev?.HideoutAddRequirements == null || ev.HideoutAddRequirements.Count == 0) return;
        if (hideoutTable == null) { Warn("拿不到 HideoutTable（DI 没给到），本维度跳过"); return; }
        try
        {
            // HideoutTable.Areas : List<HideoutArea>（SPT: Models.Spt.Tables/HideoutTable.cs）
            var areas = R.Prop(hideoutTable, "Areas") as IList;
            if (areas == null) { Warn("HideoutTable 没有 Areas 列表，本维度跳过（结构变了？）"); return; }

            int added = 0, skipped = 0, missing = 0;
            foreach (var kv in ev.HideoutAddRequirements)
            {
                var areaKey = kv.Key;
                var wants = kv.Value;
                if (wants == null || wants.Count == 0) continue;

                var area = FindArea(areas, areaKey);
                if (area == null) { Warn($"找不到藏身处区域 '{areaKey}'（既不是 id 也不是 type 序号），跳过该组"); missing++; continue; }

                var stages = R.Prop(area, "Stages") as IDictionary;
                if (stages == null) { Warn($"区域 '{areaKey}' 没有 Stages 字典，跳过"); missing++; continue; }

                var areaName = R.Prop(area, "Id")?.ToString() ?? areaKey;
                var areaType = R.Prop(area, "Type")?.ToString() ?? "?";

                foreach (var want in wants)
                {
                    if (want == null || string.IsNullOrWhiteSpace(want.TemplateId)) continue;

                    // ── 找阶段：stages 的键是字符串 "0".."3" ──
                    object stage = null;
                    foreach (var sk in stages.Keys)
                        if (string.Equals(sk?.ToString(), want.Stage.ToString(), StringComparison.OrdinalIgnoreCase))
                        { stage = stages[sk]; break; }
                    if (stage == null) { Warn($"区域 '{areaKey}'(type {areaType}) 没有 stage {want.Stage}，跳过"); missing++; continue; }

                    var reqs = R.Prop(stage, "Requirements") as IList;
                    if (reqs == null) { Warn($"区域 '{areaKey}' stage {want.Stage} 没有 Requirements 列表，跳过"); missing++; continue; }

                    // ── 幂等：同 templateId 已存在 ⇒ 跳过 ──
                    bool dup = false;
                    foreach (var r0 in reqs)
                    {
                        var tid = R.Prop(r0, "TemplateId")?.ToString();
                        if (!string.IsNullOrEmpty(tid) &&
                            string.Equals(tid, want.TemplateId, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    }
                    if (dup) { skipped++; continue; }

                    var elemType = R.ElemType(reqs)
                                ?? R.Prop(stage, "Requirements")?.GetType().GetGenericArguments().FirstOrDefault();
                    if (elemType == null) { Warn("推不出 StageRequirement 元素类型，跳过"); missing++; continue; }

                    // 用 JSON 造强类型对象（required 成员也能正确填）
                    var json = "{"
                             + "\"templateId\":\"" + want.TemplateId + "\","
                             + "\"count\":" + Math.Max(1, want.Count) + ","
                             + "\"isFunctional\":false,\"isEncoded\":false,\"isSpawnedInSession\":false,"
                             + "\"type\":\"Item\"}";
                    var req = R.FromJson(elemType, json, Warn);
                    if (req == null) { Warn($"构造 Item 需求失败（{want.TemplateId}），跳过"); missing++; continue; }

                    var capReqs = reqs; var capReq = req;
                    AddRestore?.Invoke(() =>
                    {
                        try { if (capReqs.Contains(capReq)) capReqs.Remove(capReq); } catch { }
                    });
                    reqs.Add(req);
                    added++;

                    // ── 回读自证 ──
                    int now = 0;
                    foreach (var r1 in reqs)
                        if (string.Equals(R.Prop(r1, "TemplateId")?.ToString(), want.TemplateId,
                                          StringComparison.OrdinalIgnoreCase)) now++;
                    Info($"区域 {areaName}(type {areaType}) stage {want.Stage}："
                       + $"追加 Item 需求 {want.Count}× {want.TemplateId}（回读该模板命中 {now} 条 ✔，可还原）");
                }
            }

            Info($"★藏身处升级需求：新增 {added} 条、幂等跳过 {skipped} 条、未找到 {missing} 条"
               + $"（落点 areas[].stages[].requirements[]；SPT 只读 stages，不读 levels）");
        }
        catch (Exception ex) { Warn("失败（已降级，不影响服务端）：" + ex.Message); }
    }

    /// <summary>按 id 或 type 序号找区域。</summary>
    private static object FindArea(IList areas, string key)
    {
        foreach (var a in areas)
        {
            if (a == null) continue;
            var id = R.Prop(a, "Id")?.ToString();
            if (!string.IsNullOrEmpty(id) && string.Equals(id, key, StringComparison.OrdinalIgnoreCase)) return a;
        }
        // 其次按 type（HideoutAreas 枚举序号）
        foreach (var a in areas)
        {
            if (a == null) continue;
            var t = R.Prop(a, "Type")?.ToString();
            if (!string.IsNullOrEmpty(t) && string.Equals(t, key, StringComparison.OrdinalIgnoreCase)) return a;
        }
        return null;
    }
}
