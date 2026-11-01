using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ 全局物品供给（v2.1.8）—— 修复"任务要的物品在任何地方都刷不出来"。
///
/// 实证发现（全量审计 939 个任务物品需求，索引 4400+ 物品的获取途径）：
///   ❌ Hat with horns                 675aae1c26dc64e17800fee6  → Huntsman - Factory Chief（FIR）
///   ❌ Tarkovskaya vodka (稀有条目)    614451b71e5874611e2c7ae5  → Easy Money - Part 2（FIR）
///   ❌ Disk with unknown data         686666db646d47fec3018925  → Friend from Norvinsk - Part 5
///   ❌ Decrypted intelligence folder  68666887df54e1190902df57  → Progression Difficulties - Part 2
///   这些物品【不在任何地图战利品表、不在任何空投、不被 bot 携带、不被商人售卖、不是任务奖励】
///   → 接到任务也做不完。
///
/// 本机制把这些物品注入指定空投（默认 mixed），**常驻生效**（不跟活动绑定），可用 config.json 关闭。
///
/// config.json：
/// <code>
/// "itemSupply": {
///   "enabled": true,
///   "airdropAddLoot": { "mixed": { "<itemId>": [min, max] } },
///   "airdropWeight":  { }
/// }
/// </code>
/// </summary>
internal static class ItemSupply
{
    public static bool Enabled;
    public static Dictionary<string, Dictionary<string, List<int>>> AirdropAddLoot = new();
    public static Dictionary<string, int> AirdropWeight = new();

    private static void Info(string m) { try { AirdropDimension.Log?.Invoke("[EventRotator][供给] " + m); } catch { } }

    /// <summary>从 config.json 读配置。</summary>
    public static void Load(JsonElement root, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            if (!root.TryGetProperty("itemSupply", out var s) || s.ValueKind != JsonValueKind.Object)
            {
                log?.Info("[EventRotator] 未配置 itemSupply（跳过全局物品供给）");
                return;
            }
            if (s.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                Enabled = en.GetBoolean();
            else
                Enabled = true;   // 配了节点默认开

            if (s.TryGetProperty("airdropAddLoot", out var al) && al.ValueKind == JsonValueKind.Object)
            {
                foreach (var type in al.EnumerateObject())
                {
                    var inner = new Dictionary<string, List<int>>();
                    foreach (var item in type.Value.EnumerateObject())
                    {
                        var arr = new List<int>();
                        if (item.Value.ValueKind == JsonValueKind.Array)
                            foreach (var x in item.Value.EnumerateArray())
                                if (x.ValueKind == JsonValueKind.Number) arr.Add(x.GetInt32());
                        if (arr.Count == 0) arr = new List<int> { 1, 2 };
                        inner[item.Name] = arr;
                    }
                    if (inner.Count > 0) AirdropAddLoot[type.Name] = inner;
                }
            }
            if (s.TryGetProperty("airdropWeight", out var aw) && aw.ValueKind == JsonValueKind.Object)
                foreach (var kv in aw.EnumerateObject())
                    if (kv.Value.ValueKind == JsonValueKind.Number) AirdropWeight[kv.Name] = kv.Value.GetInt32();

            if (Enabled)
                log?.Warning($"[EventRotator] ★全局物品供给已启用：{AirdropAddLoot.Sum(k => k.Value.Count)} 个物品 → "
                             + string.Join(", ", AirdropAddLoot.Keys));
        }
        catch (Exception ex) { log?.Warning($"[EventRotator] itemSupply 配置解析失败: {ex.Message}"); }
    }

    /// <summary>把供给套用到 AirdropConfig（复用 AirdropDimension 的实现）。</summary>
    public static void Apply(ISptLogger<EventRotatorServer> logger, Action<Action> addRestore)
    {
        try
        {
            if (!Enabled || (AirdropAddLoot.Count == 0 && AirdropWeight.Count == 0)) return;
            if (AirdropDimension.Config == null) { logger?.Warning("[EventRotator][供给] 拿不到 AirdropConfig"); return; }

            var fake = new EventDef
            {
                Id = "__item_supply__",
                AirdropAddLoot = AirdropAddLoot,
                AirdropWeight = AirdropWeight,
            };
            AirdropDimension.Log = m => logger?.Info(m);
            AirdropDimension.AddRestore = addRestore;
            AirdropDimension.Apply(fake, logger);
            Info($"已注入 {AirdropAddLoot.Sum(k => k.Value.Count)} 个物品（常驻，可还原）");
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][供给] 失败: {ex.Message}"); }
    }
}
