using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★ v0.1.5 新增维度：**玩家 SCAV 出生装备强化**。
///
/// 为什么需要它（官方文案 + 审计实证）：
///   · 官方 2024-12-21 活动「**PScav武装強化**」原文（日文活动记录站 ministryoftarkovinfo 汇总页）：
///     「PScavの装備がよくなりました。**敵Scavはそのまま**」= **玩家 SCAV 的装备变好，敌方 SCAV 不变**；
///   · 而本 mod 旧实现用的是 `botPresetBatch`（改 `configs/bot.json → presetBatch`）—— 那只影响
///     **所有 assault SCAV 的装备预设批次数**，与"玩家 SCAV 出生装备"**完全不是一回事**。
///
/// 真正的落点：`configs/playerscav.json → karmaLevel[&lt;karma&gt;].modifiers.equipment.{Headwear, ArmorVest,
/// Backpack, FirstPrimaryWeapon, Holster, Scabbard}` —— 这些是**整数修正值**
/// （实机：karma -7~-1 为 -2、karma 0 为 0、karma 1..6 为 +4..+70），客户端据此决定玩家 SCAV 的出生装备档位。
/// 把它们整体上调就是"玩家 SCAV 装备变好"。
///
/// events.json 用法：`"playerScavEquipmentBoost": 15`（加到每个档位的每个槽位上，可还原）
/// </summary>
internal static class PlayerScavDimension
{
    public static object PlayerScavConfig;          // SPT PlayerScavConfig（由 Plugin 注入）
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][玩家SCAV] " + m); } catch { } }

    private static readonly string[] Slots =
        { "Headwear", "ArmorVest", "Backpack", "FirstPrimaryWeapon", "Holster", "Scabbard" };

    private static object Prop(object o, string n) =>
        o?.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (ev?.PlayerScavEquipmentBoost == null || ev.PlayerScavEquipmentBoost == 0) return;
            if (PlayerScavConfig == null) { logger?.Warning("[EventRotator][玩家SCAV] 拿不到 PlayerScavConfig，未生效"); return; }

            var karma = Prop(PlayerScavConfig, "KarmaLevel") as IDictionary;
            if (karma == null || karma.Count == 0)
            {
                logger?.Warning("[EventRotator][玩家SCAV] playerscav.json 里没有 karmaLevel，未生效");
                return;
            }

            int boost = ev.PlayerScavEquipmentBoost.Value;
            int touched = 0;
            foreach (DictionaryEntry kv in karma)
            {
                var eq = Prop(Prop(kv.Value, "Modifiers"), "Equipment");
                if (eq == null) continue;

                // ★ 实测修正：`Modifiers.Equipment` 的 CLR 类型是 **Dictionary<string, double>**
                //   （JSON 里按槽位名做键：Headwear/ArmorVest/Backpack/FirstPrimaryWeapon/Holster/Scabbard），
                //   **不是**带同名属性的对象 ⇒ 旧写法用 GetProperty("Headwear") 永远找不到、改了 0 项（实测铁证）。
                if (eq is IDictionary dict)
                {
                    foreach (var key in dict.Keys.Cast<object>().Select(x => x?.ToString() ?? "").ToList())
                    {
                        double old;
                        try { old = Convert.ToDouble(dict[key]); } catch { continue; }
                        var cap = dict; var capKey = key; var capOld = old;
                        AddRestore?.Invoke(() => { try { cap[capKey] = capOld; } catch { } });
                        dict[key] = old + boost;
                        touched++;
                    }
                    continue;
                }

                // 兜底：万一将来改成带属性的对象
                foreach (var slot in Slots)
                {
                    var p = eq.GetType().GetProperty(slot,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (p == null || !p.CanWrite) continue;
                    double old;
                    try { old = Convert.ToDouble(p.GetValue(eq)); } catch { continue; }
                    var cap = eq; var capP = p; var capOld = old;
                    AddRestore?.Invoke(() => { try { capP.SetValue(cap, capOld); } catch { } });
                    try { p.SetValue(eq, old + boost); touched++; } catch { }
                }
            }
            Info($"玩家 SCAV 出生装备修正 +{boost}：改了 {touched} 项（karmaLevel 全档 × {Slots.Length} 槽位，可还原）");
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][玩家SCAV] 失败: {ex.Message}"); }
    }
}
