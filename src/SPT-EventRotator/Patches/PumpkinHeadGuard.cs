using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace EventRotator.Patches
{
    /// <summary>
    /// ★★ 2026-10-31【南瓜头外观排查/压制】
    /// <para><b>现象</b>：非万圣节活动期间，实验室里的 bot 也戴着「杰克南瓜灯战术南瓜头盔」
    /// （<c>59ef13ca86f77445fd0e2483</c>），搜尸时能在「头部装备」槽里看到它 39/39。</para>
    /// <para><b>已排除</b>：SPT 原生 <c>bots/types/bear.json</c> / <c>usec.json</c> 里该 tpl 的 Headwear 权重是 <b>0</b>；
    /// 我们的 <c>events.json</c> 里 0 次；我们的「装备注入」明细里也没有它。</para>
    /// <para><b>本类作用</b>：在所有 mod 之后（TypePriority = PostLoad + 69500，比 ABPS 还晚）扫一遍
    /// <c>BotTable.Types[*].BotInventory.Equipment[Headwear]</c>，把该 tpl 的权重压回 0，并把**是谁把它调高的**打出来
    /// （类型名 + 原权重）；每局进图再扫一次（有的 mod 在 raid 时改）。</para>
    /// <para>只在"当前没有万圣节族活动"时压制，避免把真正的万圣节弄没。</para>
    /// </summary>
    [Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 69500)]
    public class PumpkinHeadGuard(IServiceProvider services, SPTarkov.Common.Models.Logging.ISptLogger<PumpkinHeadGuard> logger) : IOnLoad
    {
        private const string PumpkinTpl = "59ef13ca86f77445fd0e2483";   // 杰克南瓜灯战术南瓜头盔
        private static IServiceProvider _sp;
        private static Action<string> _info;
        private static Action<string> _warn;
        private static bool _loggedEmptyOnce;

        private static void Info(string m) { try { _info?.Invoke(m); } catch { } }
        private static void Warn(string m) { try { _warn?.Invoke(m); } catch { } }

        public Task OnLoadAsync(System.Threading.CancellationToken token)
        {
            _sp = services;
            _info = m => logger.Info(m);
            _warn = m => logger.Warning(m);
            try { Clean("启动"); } catch { }
            return Task.CompletedTask;
        }

        /// <summary>进图时再扫一次（有的 mod 在 raid 阶段才改 bot 表）。</summary>
        public static void EnsureClean(string mapKey)
        {
            try { Clean("进图:" + mapKey); } catch { }
        }

        private static bool HalloweenActive()
        {
            try
            {
                return EventEngine.Actives != null && EventEngine.Actives.Any(a =>
                    string.Equals(a.Family, "halloween", StringComparison.OrdinalIgnoreCase)
                    || (a.Id ?? "").IndexOf("halloween", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        private static object Resolve(Type t)
        {
            try { return t == null ? null : _sp?.GetService(t); } catch { return null; }
        }

        private static object Prop(object o, string name)
        {
            try { return o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); }
            catch { return null; }
        }

        private static void Clean(string where)
        {
            if (HalloweenActive()) return;   // 万圣节期间不动
            var botTableType = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Spt.Tables.BotTable");
            var botTable = Resolve(botTableType);
            if (botTable == null) { Warn($"[EventRotator][南瓜头] 拿不到 BotTable ⇒ 跳过（{where}）"); return; }

            var types = Prop(botTable, "Types") as IDictionary;
            if (types == null) { Warn("[EventRotator][南瓜头] BotTable.Types 结构不识别 ⇒ 跳过"); return; }

            int touched = 0, foundAny = 0;
            foreach (DictionaryEntry kv in types)
            {
                var botName = kv.Key?.ToString() ?? "?";
                var inv = Prop(kv.Value, "BotInventory") ?? Prop(kv.Value, "Inventory");
                var equip = Prop(inv, "Equipment");
                if (equip is not IDictionary eq) continue;

                foreach (DictionaryEntry slot in eq)
                {
                    if (!string.Equals(slot.Key?.ToString(), "Headwear", StringComparison.OrdinalIgnoreCase)) continue;
                    if (slot.Value is not IDictionary pool) continue;

                    object hitKey = null;
                    double hitVal = 0;
                    foreach (DictionaryEntry tpl in pool)
                    {
                        if (!string.Equals(tpl.Key?.ToString(), PumpkinTpl, StringComparison.OrdinalIgnoreCase)) continue;
                        hitKey = tpl.Key;
                        try { hitVal = Convert.ToDouble(tpl.Value); } catch { }
                        break;
                    }
                    if (hitKey == null) continue;
                    foundAny++;
                    if (hitVal > 0)
                    {
                        try { pool[hitKey] = 0d; touched++; }
                        catch { try { pool[hitKey] = 0; touched++; } catch { } }
                        Warn($"[EventRotator][南瓜头] {botName}.Headwear[南瓜头] 权重 {hitVal} → 已压回 0（{where}；非万圣节期间不应出现）");
                    }
                }
            }

            if (foundAny == 0 && !_loggedEmptyOnce)
            {
                _loggedEmptyOnce = true;
                Info("[EventRotator][南瓜头] 扫了所有 bot 类型的 Headwear：⚠ 连这个 tpl 的**键**都不存在"
                     + " ⇒ 说明 bot 数据层没有它（那这份外观来自别处：客户端渲染 / 某个 mod 运行时直接改 bot 装备）");
            }
            else if (touched == 0 && foundAny > 0 && !_loggedEmptyOnce)
            {
                _loggedEmptyOnce = true;
                Info($"[EventRotator][南瓜头] 扫了 {foundAny} 个 bot 类型：权重本来就是 0 ⇒ 数据层干净（{where}）");
            }
        }
    }
}
