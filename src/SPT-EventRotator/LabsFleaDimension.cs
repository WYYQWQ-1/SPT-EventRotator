using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;

using System.Reflection;
namespace EventRotator;

/// <summary>
/// ★ 实验室免票 + 跳蚤手续费 维度（v2.5.0）—— 覆盖 A 类：
///   · 官方 #56 Free Lab Access / #65 Laboratory Breach / #51 Bad Feeling / #23 Softcore
///   · 官方 #64 Flea Market Promotion / #91 Fleamarket Event
///
/// 落点（实证）：
///   · laboratory/base.json → **AccessKeys: ["5c94bbff86f7747ee735c08f"]**（= TerraGroup Labs access keycard）
///     + AccessKeysPvE。**清空即免卡**。类型 LocationBase.AccessKeys : IEnumerable&lt;String&gt;（可写）。
///   · RagfairConfig.**OfferListingTaxMultiplier** : Int32（挂单手续费倍率，调低 = 手续费大降）。
///     另有 RagfairConfig 的等级门槛字段（本 mod 已有 fleaMinLevel 维度）。
///
/// 全部可还原。
/// </summary>
internal static class LabsFleaDimension
{
    public static object LocationTable;
    public static object RagfairConfig;
    /// <summary>★ v0.1.3：GlobalTable —— 客户端显示税额（globals 的 CommunityItemTax）在这里。</summary>
    public static object GlobalTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][实验室/跳蚤] " + m); } catch { } }

    /// <summary>★ v1.0.1【性能·第六轮】元数据查找改走 R.GP / R.GF / R.GFs 缓存；控制流与匹配语义逐字保留。</summary>
    private static object Prop(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = R.GP(t, n, BindingFlags.Public | BindingFlags.Instance);
        if (p != null) return p.GetValue(o);
        var f = R.GF(t, n, BindingFlags.Public | BindingFlags.Instance);
        if (f != null) return f.GetValue(o);
        foreach (var f2 in R.GFs(t, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            if (f2.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return f2.GetValue(o);
        return null;
    }

    private static bool SetProp(object o, string n, object v)
    {
        if (o == null) return false;
        var t = o.GetType();
        var p = t.GetProperty(n);
        if (p != null && p.CanWrite) { try { p.SetValue(o, v); return true; } catch { } }
        var f = t.GetField(n);
        if (f != null) { try { f.SetValue(o, v); return true; } catch { } }
        return false;
    }

    /// <summary>
    /// 在 LocationTable 里找某个地图的 Location 对象。
    /// ★ v0.1.3 修正：**优先用官方的 <c>LocationTable.GetLocation(mapKey)</c>**（实测存在，返回 Location），
    ///   比反射猜内部缓存字段名可靠得多；缓存字典只作为兜底。
    /// </summary>
    private static object FindLocation(string mapKey)
    {
        // ① 官方 API（最稳）
        try
        {
            var m = LocationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) });
            if (m != null)
            {
                var loc = m.Invoke(LocationTable, new object[] { mapKey });
                if (loc != null) return loc;
            }
        }
        catch (Exception ex) { Info("GetLocation 调用失败: " + ex.Message); }

        // ② 兜底：内部缓存字典
        try
        {
            object cache = Prop(LocationTable, "_locationDictionaryCache") ?? Prop(LocationTable, "locationDictionaryCache");
            var d = cache as IDictionary;
            if (d == null) return null;
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), mapKey, StringComparison.OrdinalIgnoreCase)) return d[k];
        }
        catch (Exception ex) { Info("找地图失败: " + ex.Message); }
        return null;
    }

    /// <summary>
    /// ★ v0.1.3：把"免挂单费"重置为默认。
    /// 每次「还原 + 套用」开头调用；之后由 <see cref="Apply"/> 按当前活动重新设置，
    /// 所以"关掉活动/换活动"都能干净回退。
    /// </summary>
    public static void ResetFeeMultiplier()
    {
        // ★ v0.1.3：手续费已改为**纯数据落点**（globals 税率 + RagfairConfig 倍率），
        //   不再向游戏打 Harmony 补丁，因此这里不再需要重置任何补丁状态。
        //   保留方法名是为了让 Plugin 的调用点保持稳定。
    }

    /// <summary>
    /// ★ 让"客户端显示的手续费 = 0"的正确落点（实证来自对 Assembly-CSharp 的反编译 + IL）。
    ///
    /// 客户端税额 = <c>PriceCalculator.CalculateTaxPrice</c>，它读的是
    /// <c>RagFair.Settings.communityItemTax / communityRequirementTax</c>，
    /// 来自服务端 <c>globals.json → config.RagFair.CommunityItemTax / CommunityRequirementTax</c>（两者现值都是 3）。
    ///
    /// ⚠️ 致命陷阱：<c>AddOfferWindow.SetPrices</c> 里
    /// <c>_addOfferButton.Interactable = 够钱 &amp;&amp; 税额 &gt; 0 &amp;&amp; ...</c>（IL 是 <c>ble.un</c>）——
    /// **税额 ≤ 0 会把"上架"按钮直接禁用**，玩家根本发不出去。
    /// 而显示走 <c>FormatSeparate()</c>（<c>"N0"</c>，四舍五入取整）⇒ 只要税额落在 <c>(0, 0.5)</c>，
    /// 显示就是 <c>0 ₽</c>，同时按钮仍然可点。
    ///
    /// 所以这里设成**极小正数**（1e-6），绝不填 0。类型必须精确匹配：
    /// <c>CommunityItemTax</c> 是 <c>float</c>、<c>CommunityRequirementTax</c> 是 <c>double</c>，装箱错类型会静默失败。
    /// </summary>
    private const float TinyTaxFloat = 1e-6f;

    /// <summary>
    /// 把服务端 globals 的两个税率改成极小正数 → 客户端算出的手续费 ≈ 0（显示 <c>0 ₽</c>）但 &gt; 0（上架按钮仍可点）。
    /// 返回成功改动的字段数（0 = 没改到，会打警告）。
    /// </summary>
    private static int ZeroClientTaxDisplay(ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            var cfg = Prop(GlobalTable, "Configuration");
            if (cfg == null) { logger?.Warning("[EventRotator][跳蚤] 拿不到 GlobalTable.Configuration，客户端显示税额无法归零"); return 0; }
            var rag = Prop(cfg, "RagFair") ?? Prop(cfg, "Ragfair");
            if (rag == null) { logger?.Warning("[EventRotator][跳蚤] 拿不到 config.RagFair，客户端显示税额无法归零"); return 0; }

            int n = 0;
            // CommunityItemTax : float（客户端只读这一个）
            n += SetTiny(rag, "CommunityItemTax", TinyTaxFloat);
            // CommunityRequirementTax : double（服务端兜底公式也用它）
            n += SetTiny(rag, "CommunityRequirementTax", (double)TinyTaxFloat);
            if (n == 0) logger?.Warning("[EventRotator][跳蚤] CommunityItemTax / CommunityRequirementTax 都没改到（字段名或类型不匹配）");
            return n;
        }
        catch (Exception ex) { logger?.Warning("[EventRotator][跳蚤] 改客户端税率失败: " + ex.Message); return 0; }
    }

    /// <summary>写入并按原值登记还原；类型不匹配会明确报错（不静默吞）。</summary>
    private static int SetTiny(object obj, string name, object tiny)
    {
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name);
            if (p == null || !p.CanWrite) { Info($"'{name}' 不存在或不可写"); return 0; }
            var u = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var old = p.GetValue(obj);
            var capO = obj; var capP = p; var capOld = old;
            AddRestore?.Invoke(() => { try { capP.SetValue(capO, capOld); } catch { } });
            p.SetValue(obj, Convert.ChangeType(tiny, u));      // float/double 必须精确匹配，否则这里会抛
            double oldV = 0; try { oldV = Convert.ToDouble(old); } catch { }
            Info($"{name}: {oldV} → {tiny}（原值可还原）");
            return 1;
        }
        catch (Exception ex) { Info($"'{name}' 写入失败（类型不匹配？）: {ex.Message}"); return 0; }
    }

    /// <summary>
    /// 把 <c>IEnumerable&lt;string&gt;</c> 属性覆写成**空数组**（= SPT 官方 RemoveEntryRequirement 的做法）。
    /// 兼容三种声明类型：<c>string[]</c> / <c>IList&lt;string&gt;</c> / <c>IEnumerable&lt;string&gt;</c>。
    /// </summary>
    private static bool SetEmptyStringArray(object obj, string name)
    {
        try
        {
            var p = obj.GetType().GetProperty(name);
            if (p == null || !p.CanWrite) { Info($"'{name}' 不可写"); return false; }
            var pt = p.PropertyType;
            object empty;
            if (pt == typeof(string[])) empty = Array.Empty<string>();
            else if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(IList<>)) empty = new List<string>();
            else if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(IEnumerable<>)) empty = Array.Empty<string>();
            else if (pt.IsAssignableFrom(typeof(string[]))) empty = Array.Empty<string>();
            else if (pt.IsAssignableFrom(typeof(List<string>))) empty = new List<string>();
            else { Info($"'{name}' 类型 {pt.Name} 无法用空集合覆写"); return false; }

            p.SetValue(obj, empty);
            return true;
        }
        catch (Exception ex) { Info($"覆写 '{name}' 失败: {ex.Message}"); return false; }
    }

    /// <summary>
    /// ★ v0.1.3 应用一个活动的「实验室免票 / 跳蚤免挂单费」。
    /// 注意：这里**没有**"没写 fleaTaxMultiplier 就直接 return"的早退 ——
    /// 那个早退会让只写 <c>fleaZeroListingFee: true</c> 的配置完全失效（0.1.2 的 bug，分析实测发现）。
    /// </summary>
    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            // ensure：GlobalTable 由 Plugin 注入；这里兜底一次（避免空引用）
            if (GlobalTable == null) logger?.Warning("[EventRotator][跳蚤] GlobalTable 未注入（客户端显示税额无法归零）");

            // ── ★ 跳蚤免挂单费：**两处纯数据落点**（不对游戏打任何补丁）──
            //   ① 实扣：RagfairConfig.OfferListingTaxMultiplier = 0（服务端 CalculateTax 归零）
            //   ② 显示：globals → config.RagFair.CommunityItemTax / CommunityRequirementTax → 极小正数
            //      客户端 CalculateTaxPrice 读的就是这两个值：算出 1e-6 → 显示取整为 0 ₽，
            //      而它 > 0，所以不会触发 AddOfferWindow.SetPrices 里 `税额 > 0` 的按钮禁用。
            bool zeroFee = ev.FleaZeroListingFee == true
                           || (ev.FleaTaxMultiplier.HasValue && ev.FleaTaxMultiplier.Value >= 0
                               && Math.Abs(ev.FleaTaxMultiplier.Value - 1.0) > 0.000001);

            if (zeroFee)
            {
                double mult = ev.FleaZeroListingFee == true
                    ? 0
                    : ev.FleaTaxMultiplier.GetValueOrDefault(0);

                var n = ZeroClientTaxDisplay(logger);
                Info($"★跳蚤免挂单费已启用：服务端实扣倍率 ×{mult}"
                     + (n > 0
                        ? $"、客户端显示税额 → 极小正数（改了 {n} 项税率；客户端的手续费就是由它算出来的 → 显示 0 ₽ 且上架按钮仍可点）"
                        : "、但 globals 里两个税率没改到（见上面警告）"));
            }

            // ── 实验室 / 迷宫 免票 ──
            //   ★★★ v2.6.9【扩展到迷宫】官方原文是 "Entry to **The Lab** AND **The Labyrinth**
            //   doesn't require access keycards"（events_wiki.txt:369，Softcore 2025-10-08）——
            //   此前只清 laboratory。实测 labyrinth/base.json 的 AccessKeys / AccessKeysPvE
            //   与 laboratory 同构（["679b9819a2f2dd4da9023512"] / ["5c94bbff86f7747ee735c08f"]），
            //   故用同一段逻辑处理。⚠ labyrinth 在本体 `Enabled=false`（0.16.9 未启用），
            //   清卡属于"功能对齐"，实际进不去图。
            if (ev.LabsFreeAccess == true)
            {
                foreach (var labKey in new[] { "laboratory", "labyrinth" })
                {
                var lab = FindLocation(labKey);
                if (lab == null) { logger?.Warning($"[EventRotator][实验室] 在 LocationTable 里找不到 {labKey}"); }
                else
                {
                    var baseObj = Prop(lab, "Base") ?? Prop(lab, "base");
                    if (baseObj == null) logger?.Warning("[EventRotator][实验室] laboratory 没有 Base");
                    else
                    {
                        // ★★ v0.1.3 关键修正（0.1.2 实测失败的根因）：
                        //   LocationBase.AccessKeys 的声明类型是 **IEnumerable<string>**（实测），
                        //   而旧代码写的是 `var list = cur as IList; if (list == null ...) continue;`
                        //   → 永远 continue，**Clear() 从未执行过**（日志却打印"已清空"，纯误导）。
                        //   现在改为：枚举出原值 → 用**空数组**覆盖属性（与 SPT 官方
                        //   SeasonalEventService.RemoveEntryRequirement 的做法一致：AccessKeys = Array.Empty<string>()）。
                        int cleared = 0;
                        foreach (var fld in new[] { "AccessKeys", "AccessKeysPvE" })
                        {
                            var snapshot = new List<string>();
                            try
                            {
                                if (Prop(baseObj, fld) is IEnumerable cur)
                                    foreach (var x in cur) snapshot.Add(x?.ToString());
                            }
                            catch { }

                            if (!SetEmptyStringArray(baseObj, fld))
                            {
                                logger?.Warning($"[EventRotator][实验室] {fld} 覆写失败（属性名或类型不匹配）");
                                continue;
                            }
                            cleared++;

                            var capObj = baseObj; var capFld = fld; var capSnap = snapshot;
                            AddRestore?.Invoke(() =>
                            {
                                try
                                {
                                    var p = capObj.GetType().GetProperty(capFld);
                                    if (p != null && p.CanWrite) p.SetValue(capObj, capSnap.ToArray());
                                }
                                catch { }
                            });
                        }

                        // 覆写后回读，确认真的生效（不再"假成功"）
                        var after = Prop(Prop(lab, "Base") ?? Prop(lab, "base"), "AccessKeys") as IEnumerable;
                        if (after is string[] arr2) after = arr2;
                        var afterVals = new List<string>();
                        if (after != null) foreach (var x in after) afterVals.Add(x?.ToString());
                        Info($"★{labKey} 免票：AccessKeys/AccessKeysPvE 覆写为空数组（成功 {cleared} 个；"
                             + $"回读 AccessKeys 现在有 {afterVals.Count} 个门卡）"
                             + (afterVals.Count == 0 ? " ✔" : " —— ⚠ 没清干净！"));
                    }
                }
                }
            }

            // ── 跳蚤手续费倍率 ──
            if (ev.FleaTaxMultiplier.HasValue)
            {
                if (RagfairConfig == null) logger?.Warning("[EventRotator][跳蚤] 拿不到 RagfairConfig");
                else
                {
                    var old = Prop(RagfairConfig, "OfferListingTaxMultiplier");
                    int oldI = 0; try { oldI = Convert.ToInt32(old); } catch { }
                    var cap = RagfairConfig;
                    AddRestore?.Invoke(() => { try { SetProp(cap, "OfferListingTaxMultiplier", oldI); } catch { } });
                    SetProp(RagfairConfig, "OfferListingTaxMultiplier", (int)ev.FleaTaxMultiplier.Value);
                    Info($"跳蚤挂单手续费倍率 OfferListingTaxMultiplier: {oldI} → {(int)ev.FleaTaxMultiplier.Value}（可还原）");
                }
            }
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][实验室/跳蚤] 失败: {ex.Message}"); }
    }
}
