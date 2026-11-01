using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ v1.3.7【WTT 灾厄武器兜底修正】灾厄小队的主武器是 WTT-ContentBackport 的自定义武器
/// （`db/bots/types/bossScourge.json → inventory.equipment.FirstPrimaryWeapon`）。
///
/// <para><b>问题</b>：这个 tpl 由 WTT-ContentBackport 自己在 `OnLoadAsync` 里注册。
/// 两个 mod 都走 `[Injectable]` 的默认 TypePriority，**先后顺序不保证** ⇒ 我们查表时它可能还没塞进去，
/// 于是误判「缺 WTT」把主武器换成原版枪（玩家反馈：装了最新 WTT 灾厄还是原版武器）。</para>
///
/// <para><b>做法</b>：
/// ① 首次检查（`Run("load")`）只报不换 —— 只要检测到 WTT-ContentBackport 已安装，就先等；
/// ② 8 秒后复查（`Run("late")`）—— 那时所有 mod 的 OnLoad 都已跑完，tpl 在就保持原武器；
/// ③ 复查仍缺才替换，并且**把两种原因分开报**（WTT 没装 / 装了但 tpl 仍不在 ⇒ 多半是别的 WTT 附加包或版本旧）；
/// ④ `config.json` 的 `"weaponFallback": false` 可整体关掉自动替换。</para>
///
/// <para><b>幂等</b>：tpl 存在时什么都不做（完全静默），不改变任何正常玩家的行为。</para>
/// </summary>
public static class ScourgeWeaponFallback
{
    private static ISptLogger<EventRotatorServer> _log;
    private static object _botTable;
    private static object _templateTable;
    private static string _modsDir = "";

    private static int _lateDone;                       // 0/1：延迟复查只跑一次
    private static int _offLogged;                      // 0/1：关闭提示只打一次
    private static readonly List<string> _pendingRoles = new();

    /// <summary>需要检查主武器的角色。</summary>
    private static readonly string[] Roles = { "bossScourge", "followerScourge" };

    /// <summary>捐武器的原版角色（按顺序取第一个有主武器且模板存在的）。</summary>
    private static readonly string[] Donors = { "bossknight", "bossglukhar", "pmcbot", "bossbully", "assault" };

    public static void Init(ISptLogger<EventRotatorServer> log, object botTable, object templateTable, string modPath)
    {
        _log = log;
        _botTable = botTable;
        _templateTable = templateTable;
        try { _modsDir = string.IsNullOrEmpty(modPath) ? "" : (Path.GetDirectoryName(modPath) ?? ""); } catch { }
    }

    /// <summary>启动时调用一次：先安排 8 秒后的复查，再跑第一轮（只报不换）。</summary>
    public static void Schedule()
    {
        if (_log == null || _botTable == null || _templateTable == null) return;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(8000).ConfigureAwait(false);
                if (System.Threading.Interlocked.Exchange(ref _lateDone, 1) == 1) return;
                Run("late");
            }
            catch (Exception ex)
            {
                try { _log?.Debug("[EventRotator][武器兜底] 延迟复查异常（忽略）: " + ex.GetType().Name); } catch { }
            }
        });
    }

    /// <summary>
    /// 检查并兜底。phase = "load"（启动首轮，只报不换）/ "late"（8 秒后复查，仍缺才换）。
    /// 拿不到表就静默跳过，绝不抛。
    /// </summary>
    public static void Run(string phase)
    {
        if (_botTable == null || _templateTable == null) return;

        if (!EventEngine.WeaponFallbackEnabled)
        {
            if (System.Threading.Interlocked.Exchange(ref _offLogged, 1) == 0)
                _log?.Info("[EventRotator][武器兜底] 已按 config.json 的 weaponFallback=false 关闭（主武器按原样使用）。");
            return;
        }

        try
        {
            var items = R.Prop(_templateTable, "Items") as IDictionary;
            var types = R.Prop(_botTable, "Types") as IDictionary;
            if (items == null || types == null)
            {
                _log?.Debug("[EventRotator][武器兜底] 拿不到 TemplateTable.Items / BotTable.Types（跳过）");
                return;
            }

            var wtt = WttPresent();
            var wttTag = wtt ? "检测到 WTT-ContentBackport" : "未检测到 WTT-ContentBackport";
            int stillMissing = 0;

            foreach (var role in Roles)
            {
                var botType = Get(types, role);
                if (botType == null) continue;

                var inv = R.Prop(botType, "BotInventory") ?? R.Prop(botType, "inventory");
                var equipment = inv == null ? null : R.Prop(inv, "Equipment") as IDictionary;
                var fpw = equipment == null ? null : Get(equipment, "FirstPrimaryWeapon") as IDictionary;
                if (fpw == null || fpw.Count == 0) continue;

                var ids = fpw.Keys.Cast<object>().Select(k => k?.ToString() ?? "").Where(s => s.Length > 0).ToList();
                var missing = ids.Where(id => !ItemExists(items, id)).ToList();
                if (missing.Count == 0) continue;      // ✅ 正常：静默

                // ① 启动首轮 + WTT 在场 ⇒ 只报告，不替换（等它把物品注册完）
                if (phase == "load" && wtt)
                {
                    if (!_pendingRoles.Contains(role)) _pendingRoles.Add(role);
                    _log?.Info($"[EventRotator][武器兜底] '{role}' 的主武器模板此刻还不在物品表里（{string.Join(", ", missing)}）；"
                             + $"{wttTag} ⇒ 先不替换，8 秒后复查（避开和 WTT 的加载顺序竞争）。");
                    continue;
                }

                stillMissing++;

                var donor = FindDonor(types, items);
                if (donor == null)
                {
                    _log?.Warning($"[EventRotator][武器兜底] '{role}' 的武器模板缺失（{string.Join(", ", missing)}），"
                                + "而且找不到可替换的原版武器 ⇒ 该 bot 生成时可能没有主武器。");
                    continue;
                }

                try
                {
                    var keyType = R.DictKeyType(fpw);
                    var key = R.Coerce(donor, keyType);
                    // ⚠ 内层字典的值类型实测是 **double**（不是 int）——写 int 会抛
                    //   ArgumentException: The value "1" is not of type "System.Double"
                    var valType = fpw.GetType().GetGenericArguments().Length > 1
                        ? fpw.GetType().GetGenericArguments()[1]
                        : typeof(double);
                    var val = Convert.ChangeType(1, valType);
                    if (key == null) continue;
                    fpw.Clear();
                    fpw[key] = val;
                }
                catch (Exception ex2)
                {
                    _log?.Warning($"[EventRotator][武器兜底] 替换武器失败（{ex2.GetType().Name}: {ex2.Message}）role={role} donor={donor}");
                    continue;
                }

                _log?.Warning($"[EventRotator][武器兜底] ⚠ '{role}' 的主武器模板不存在（{string.Join(", ", missing)}）"
                            + $" ⇒ 已自动换成原版武器 '{donor}'。");
                if (wtt)
                {
                    _log?.Warning("[EventRotator][武器兜底] ⚠ 但 " + wttTag + "，复查后物品表里仍然没有这些 tpl ⇒ 两种可能："
                                + "① 这批武器不在 ContentBackport 里，而是别的 WTT 附加包（Armory 等）；"
                                + "② WTT 版本旧、没带这批物品。");
                    _log?.Warning("[EventRotator][武器兜底] ⚠ 想保持原武器、不要自动替换：把 mod 目录 config.json 里 "
                                + "\"weaponFallback\": false 写上，重启服务端。");
                }
                else
                {
                    _log?.Warning("[EventRotator][武器兜底] ⚠ 原因通常是 **WTT-ContentBackport 没装**"
                                + "（灾厄小队用的是它的自定义武器）。装上后重启服务端即可恢复原武器。");
                    _log?.Warning("[EventRotator][武器兜底] ⚠ 不想自动替换：config.json 里设 \"weaponFallback\": false。");
                }
            }

            if (phase == "late" && _pendingRoles.Count > 0 && stillMissing == 0)
            {
                _log?.Info($"[EventRotator][武器兜底] ✔ 复查通过：{string.Join(", ", _pendingRoles)} 的 WTT 自定义武器已就位，主武器保持原样。");
                _pendingRoles.Clear();
            }
            else if (phase == "late" && wtt && stillMissing == 0)
            {
                // 一次性的"我查过了、没问题"日志：没有它就分不清「没触发兜底」和「代码没跑」
                _log?.Info("[EventRotator][武器兜底] ✔ 复查完成：灾厄主武器 tpl 均在物品表（WTT-ContentBackport 在场 ⇒ 不替换）。");
            }
        }
        catch (Exception ex)
        {
            _log?.Warning("[EventRotator][武器兜底] 检查失败（已跳过，不影响启动）: " + ex.GetType().Name);
        }
    }

    // ─────────────────────────── 工具 ───────────────────────────

    /// <summary>mods 目录里是否有 WTT-ContentBackport（只看目录 + 它的 dll，不加载它）。</summary>
    private static bool WttPresent()
    {
        try
        {
            if (string.IsNullOrEmpty(_modsDir) || !Directory.Exists(_modsDir)) return false;
            foreach (var dir in Directory.EnumerateDirectories(_modsDir))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("WTT-ContentBackport", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("WTT-ContentBackport", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Any()) return true;
            }
        }
        catch { }
        return false;
    }

    private static object Get(IDictionary d, string key)
    {
        if (d == null || string.IsNullOrEmpty(key)) return null;
        try { if (d.Contains(key)) return d[key]; } catch { }
        foreach (DictionaryEntry e in d)
            if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return e.Value;
        return null;
    }

    private static bool ItemExists(IDictionary items, string tpl)
    {
        try
        {
            var key = R.Coerce(tpl, R.DictKeyType(items));
            if (key != null && items.Contains(key)) return true;
        }
        catch { }
        return R.DictGet(items, tpl) != null;
    }

    private static string FindDonor(IDictionary types, IDictionary items)
    {
        foreach (var donor in Donors)
        {
            try
            {
                var botType = Get(types, donor);
                if (botType == null) continue;
                var inv = R.Prop(botType, "BotInventory") ?? R.Prop(botType, "inventory");
                var equipment = inv == null ? null : R.Prop(inv, "Equipment") as IDictionary;
                var fpw = equipment == null ? null : Get(equipment, "FirstPrimaryWeapon") as IDictionary;
                if (fpw == null || fpw.Count == 0) continue;
                foreach (DictionaryEntry e in fpw)
                {
                    var tpl = e.Key?.ToString();
                    if (!string.IsNullOrEmpty(tpl) && ItemExists(items, tpl)) return tpl;
                }
            }
            catch { }
        }
        return null;
    }
}
