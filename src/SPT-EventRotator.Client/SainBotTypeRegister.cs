using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;

namespace EventRotator.Client
{
    /// <summary>
    /// ★★★ 把我们的 7 个自定义 role 注册进 SAIN 的「bot 分类表」，让 SAIN 把它们当 **Boss** 而不是「其他」。
    ///
    /// ── 真实机制（SAIN 4.5.1，Cecil/IL 实证）──
    ///  ① 服务端：<c>SAINServerMod.Generators.PresetGenerationService.AllBotTypes()</c>
    ///       = <c>DefaultBotTypes.All()</c>（**硬编码 45 条** Name/WildSpawnType/Difficulty/Section/Description）
    ///       + <c>_registered</c>（由 <c>RegisterAsync(SainBotTypeRegistration)</c> 追加，接口
    ///         <c>SAIN.ServerInterop.ISainBotTypeRegistry</c> 对外公开 —— BlackDiv / ISB / MoreBots 都是这么注册的）
    ///     经 <c>SAINCallbacks.GetBotTypes</c> 以 JSON 下发给客户端。
    ///  ② 客户端：<c>SAIN.Preset.BotTypeDefinitions.ImportBotTypes()</c> → <c>ToBotTypes()</c> →
    ///     静态 <c>BotTypes: Dictionary&lt;WildSpawnType, BotType&gt;</c>、<c>BotTypesList</c>、<c>BotTypesNames</c>。
    ///     <c>BotType</c> = { Name, Description, **Section**, WildSpawnType, BrainsToApply, LayersToRemove, BaseBrain }。
    ///  ③ 行为：<c>SAIN.BigBrainHandler.BrainAssignment.ApplyModdedBotLayers()</c> **遍历 BotTypesList**，
    ///     对每条的 <c>BrainsToApply</c> 注册 SAIN 自定义层、按 <c>LayersToRemove</c> 摘原版层；
    ///     缺失条目 ⇒ 这些 bot 完全不参与 SAIN 的层/脑定制 ⇒ 观感「有点笨、个别发呆」。
    ///  ④ 预设编辑器（F12 / 网页）也按同一份数据的 <c>Section</c> 分栏 ⇒ 未登记 ⇒ 落到「其他」。
    ///
    /// ── 本类怎么做 ──
    ///  在客户端 SAIN 导入完成后，把 7 个 role **补进同一份 BotTypes 字典/列表**：
    ///  Section / BrainsToApply / LayersToRemove / BaseBrain 全部**克隆 bossKnight**（首领档），
    ///  然后调一次 <c>BrainAssignment.ApplyModdedBotLayers()</c> 让 SAIN 自己把层注册上。
    ///  ★ 不碰任何原版条目（只 Add 我们自己的 7 个），且幂等（已存在就跳过）。
    ///
    /// 注：这是**客户端兜底**。最正统的是服务端用 <c>ISainBotTypeRegistry.RegisterAsync</c> 注册
    /// （BlackDiv/ISB 的做法），这样网页版编辑器也会显示为 Boss 分栏；两者可并存、互不冲突。
    /// </summary>
    internal static class SainBotTypeRegister
    {
        private static bool _tried, _ok;
        private static ManualLogSource _log;
        private static string _detail = "";

        /// <summary>与 patcher / 服务端逐字一致的 7 个 role（Name 用于 SAIN 列表显示，Value 是枚举值）。</summary>
        private static readonly (string Name, int Value)[] Roles =
        {
            ("bossScourge",     13790),
            ("followerScourge", 13791),
            ("cleanupcrew",     13792),
            ("pillager",        13793),
            ("bloodhound",      13794),
            ("gus",             13795),
            ("basmach",         13796),
        };

        internal static void SetLog(ManualLogSource log) => _log = log;

        internal static string Detail => _detail;

        /// <summary>周期性尝试（SAIN 可能比我们晚加载；成功后不再做）。幂等。</summary>
        internal static void Tick()
        {
            if (_ok) return;
            if (!_tried) _tried = true;
            try
            {
                var defs = FindType("SAIN.Preset.BotTypeDefinitions");
                if (defs == null) return;                                  // SAIN 还没加载
                var dict = defs.GetProperty("BotTypes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var list = defs.GetProperty("BotTypesList", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var names = defs.GetProperty("BotTypesNames", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (dict is not IDictionary d || list is not IList l || names is not IList nl) return;

                int added = Inject(d, l, nl, out var detail);
                _detail = detail;
                if (added > 0)
                {
                    _log?.LogInfo("[EventRotator.Client][SAIN分类] ★已把 " + added + " 个自定义 role 注册进 SAIN 分类表（Section=克隆 黑狐/回退bossKnight）：" + detail);
                    TryApplyModdedLayers();
                }
                _ok = true;
            }
            catch (Exception ex)
            {
                if (!_ok) _log?.LogWarning("[EventRotator.Client][SAIN分类] 注册失败: " + ex.GetType().Name + ": " + ex.Message);
                _ok = true;   // 不刷屏：失败也只试一次
            }
        }

        /// <summary>
        /// 纯注入逻辑（可被探针直接测）：把 Roles 补进 dict/list/names，
        /// 模板 = 字典里的 bossKnight（Section/BrainsToApply/LayersToRemove/BaseBrain 全抄它）。
        /// </summary>
        internal static int Inject(IDictionary dict, IList list, IList names, out string detail)
        {
            detail = "";
            var enumType = FindType("EFT.WildSpawnType");
            if (enumType == null) { detail = "找不到 EFT.WildSpawnType"; return 0; }

            // ★ 2026-10-30【用户口径 A】模板换成**黑狐（Black Division Lead 848420）**：
            //   它是 BlackDiv 自己注册的条目，带真实 BrainsToApply/BaseBrain（=> 黑狐战术层）。
            //   bossKnight 在本版 SAIN 里这三项是 null，抄它等于没有层。
            object template = null;
            string tplName = "(无)";
            try
            {
                foreach (DictionaryEntry de in dict)
                {
                    var v = de.Value; if (v == null) continue;
                    var vtF = v.GetType().GetField("WildSpawnType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var wtv = vtF?.GetValue(v);
                    if (wtv != null && Convert.ToInt32(wtv) == 848420) { template = v; tplName = "848420(Black Division Lead)"; break; }
                }
            }
            catch { }
            if (template == null)
            {
                try
                {
                    var bossKnight = Enum.Parse(enumType, "bossKnight");
                    if (dict.Contains(bossKnight)) { template = dict[bossKnight]; tplName = "bossKnight(回退)"; }
                }
                catch { }
            }
            if (template == null) { detail = "SAIN 分类表里没有黑狐(848420) 也没有 bossKnight 模板"; return 0; }

            var t = template.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            // ★ 2026-10-31【分类错列】原来 Section 直接克隆模板（黑狐 Lead）⇒ 面板里全落进 "Black Division"。
            //   SAIN 内置分节名就是 "Boss"（不是 "Bosses"）。用户裁决：7 个自定义角色统一放 Boss 列。
            string section = "Boss";
            string desc = t.GetField("Description", F)?.GetValue(template) as string;
            string baseBrain = t.GetField("BaseBrain", F)?.GetValue(template) as string;
            var brains = (t.GetField("BrainsToApply", F)?.GetValue(template) as IEnumerable)?.Cast<object>().ToList();
            var remove = (t.GetField("LayersToRemove", F)?.GetValue(template) as IEnumerable)?.Cast<object>().ToList();

            if (brains == null || brains.Count == 0)
                _log?.LogWarning("[EventRotator.Client][SAIN分类] ⚠ 模板 " + tplName + " 的 BrainsToApply 为空 ⇒ 这批 role 不会有额外 SAIN 战斗层");
            else
                _log?.LogInfo("[EventRotator.Client][SAIN分类] 模板=" + tplName + "｜BrainsToApply=" + brains.Count + " 项：" + string.Join(",", brains)
                            + "｜BaseBrain=" + (baseBrain ?? "(空)"));

            var added = new List<string>();
            var overridden = new List<string>();
            foreach (var (name, value) in Roles)
            {
                object wt;
                try { wt = Enum.Parse(enumType, name); } catch { continue; }

                // ★★ 2026-10-30【关键：改成覆盖】服务端有第三方（SAIN 扫 bot 定义）抢先注册了
                //   13790–13796，我们两次注册都被 "already known" 丢掉，所以 Section/brains 永远不是黑狐那套。
                //   客户端这张表是行为层的真正来源 ⇒ 条目已存在时就地改写它的 Section/brains/BaseBrain。
                if (dict.Contains(wt))
                {
                    var ex = dict[wt];
                    if (ex != null)
                    {
                        var et = ex.GetType();
                        SetF(et, ex, "Section", section);
                        SetF(et, ex, "BaseBrain", baseBrain);
                        SetF(et, ex, "BrainsToApply", CopyList(brains));
                        SetF(et, ex, "LayersToRemove", CopyList(remove));
                        overridden.Add(name);
                    }
                    continue;
                }

                var bt = Activator.CreateInstance(t);
                SetF(t, bt, "Name", "EventRotator " + name);
                SetF(t, bt, "Description", desc);
                SetF(t, bt, "Section", section);
                SetF(t, bt, "BaseBrain", baseBrain);
                SetF(t, bt, "WildSpawnType", wt);
                SetF(t, bt, "BrainsToApply", CopyList(brains));
                SetF(t, bt, "LayersToRemove", CopyList(remove));

                dict[wt] = bt;
                list.Add(bt);
                names.Add(name);
                added.Add(name);
            }
            detail = "新增 " + added.Count + " 个（" + string.Join(",", added) + "）"
                   + "｜覆盖 " + overridden.Count + " 个（" + string.Join(",", overridden) + "）"
                   + " ⇒ Section=" + section + "，BaseBrain=" + (baseBrain ?? "?")
                   + "，BrainsToApply=" + (brains?.Count ?? 0) + " 项";
            if (overridden.Count > 0)
                _log?.LogInfo("[EventRotator.Client][SAIN分类] ★已把 " + overridden.Count
                    + " 个自定义角色的行为层覆盖为黑狐档：" + string.Join(",", overridden)
                    + "（BaseBrain=" + (baseBrain ?? "?") + " / BrainsToApply=" + (brains?.Count ?? 0) + " 项）");
            return added.Count + overridden.Count;
        }

        private static object CopyList(List<object> src)
        {
            var ty = FindType("System.Collections.Generic.List`1");
            var lt = ty?.MakeGenericType(typeof(string));
            var inst = lt == null ? (object)new List<string>() : Activator.CreateInstance(lt);
            if (src == null || inst is not IList l) return inst;
            foreach (var s in src) l.Add(s);
            return inst;
        }

        private static void SetF(Type t, object obj, string name, object value)
        {
            try { t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(obj, value); }
            catch { }
        }

        /// <summary>让 SAIN 自己按新表把层注册一遍（走它的公开静态方法）。</summary>
        private static void TryApplyModdedLayers()
        {
            try
            {
                var ba = FindType("SAIN.BigBrainHandler.BrainAssignment");
                var m = ba?.GetMethod("ApplyModdedBotLayers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                m?.Invoke(null, null);
                _log?.LogInfo("[EventRotator.Client][SAIN分类] 已触发 SAIN.BrainAssignment.ApplyModdedBotLayers()（按新分类重挂层）");
            }
            catch (Exception ex)
            {
                _log?.LogWarning("[EventRotator.Client][SAIN分类] ApplyModdedBotLayers 调用失败（不影响分类本身）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType(fullName, false); if (t != null) return t; }
                catch { }
            }
            return null;
        }
    }
}
