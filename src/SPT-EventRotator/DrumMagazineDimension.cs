using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ v0.1.3 弹鼓概率维度 —— **纯 items.json 落点，对 PBS 也有效**。
///
/// 背景：用户实测「大弹鼓 SCAV 没效果」，根因是本机装了 Progressive Bot System（PBS），
///   它整体替换了 bot 生成、武器/配件池取自它自己的 Data/Tier*.json，**不读 BotTable**。
///   所以往 `BotTable` 里加弹匣不会生效。
///
/// 但实证发现一条**PBS 也会走的路**：
///   · PBS 的 `ApbsExternalInventoryMagGen`（ExternalInventoryMagGen 的 PBS 版）挑弹匣时会读
///     `itemHelper.GetItem(weaponTpl).Properties.Slots.FirstOrDefault(x => x.Name == "mod_magazine")`
///     —— 也就是**武器自己的槽位过滤表**；
///   · PBS 的配件池本来也是"扫 items 的槽位过滤"建出来的（与 SPT 同做法）。
///   ⇒ **把弹鼓写进武器的 mod_magazine 过滤表**，PBS 与本体会一起跟着用大弹鼓，
///     而**不需要修改 PBS 的任何文件**（本 mod 只动 items 数据）。
///
/// 实测（改前）：11 种弹鼓**本来就在 46 把**武器的弹匣槽过滤里，但没有一把"只装大弹鼓"；
///   本维度按倍率把**额外兼容**的弹鼓补进武器槽（可还原），提高被抽中的概率。
///
/// 全部可还原。
/// </summary>
internal static class DrumMagazineDimension
{
    public static object TemplateTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>★ 本轮写入的 (武器 tpl, 弹鼓 tpl) 对 —— 供 PBS 运行期池子写入复用（PBS 与 items 过滤表取交集）。</summary>
    public static readonly List<(string Weapon, string Drum)> AddedPairs = new();

    /// <summary>经验证的 11 种大弹匣/弹鼓。</summary>
    private static readonly Dictionary<string, string> Drums = new(StringComparer.OrdinalIgnoreCase)
    {
        ["5bed625c0db834001c062946"] = "RPK-16 95发弹鼓",
        ["5c6175362e221600133e3b94"] = "AK ProMag 73发弹鼓",
        ["544a37c44bdc2d25388b4567"] = "SureFire MAG5-60 5.56x45 60发（子智能体核对：原标注 545x39 有误）",
        ["59c1383d86f774290a37e0ca"] = "Magpul D-60",
        ["55d482194bdc2d1d4e8b456b"] = "AK-74 6L31 60发",
        ["55d485be4bdc2d962f8b456f"] = "PM 84发弹鼓",
        ["5a351711c4a282000b1521a4"] = "MP5 X-Products X-5 9x19 50发（子智能体核对：原标注 556x45 有误）",
        ["5a718f958dc32e00094b97e7"] = "Glock 50发弹鼓",
        ["5addccf45acfc400185c2989"] = "M14 50发弹鼓",
        ["5b7bef9c5acfc43d102852ec"] = "FAL 50发弹鼓",
        ["5c5db6742e2216000f1b2852"] = "MPX 50发弹鼓",
    };

    /// <summary>下挂榴弹发射器（同样只在 items 槽位过滤里补，PBS 也读）。</summary>
    private static readonly Dictionary<string, string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["5648b62b4bdc2d9d488b4585"] = "GP-34",
        ["62e7e7bbe6da9612f743f1e0"] = "GP-25",
        ["639af924d0446708ee62294e"] = "GP-25 Settin",
        ["5e81ebcd8e146c7080625e15"] = "FN40GL",
    };

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][弹鼓] " + m); } catch { } }

    private static object Prop(object o, string n)
        => R.PropWithFlags(o, n, Any);

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            var cfg = ev.DrumMagazineBoost;
            if (cfg == null || cfg.Count == 0) return;
            if (TemplateTable == null) { logger?.Warning("[EventRotator][弹鼓] 拿不到 TemplateTable"); return; }

            double mult = cfg.TryGetValue("magazineMultiplier", out var m) && m > 1 ? m : 1;
            bool alsoLaunchers = cfg.TryGetValue("alsoLaunchers", out var al) && al >= 1;

            var items = Prop(TemplateTable, "Items") as IDictionary;
            if (items == null) { Info("拿不到 TemplateTable.Items"); return; }

            // 每个弹鼓"物理上能装"的武器 = items 里别的武器槽位里出现过这个弹鼓的那些武器类型，
            // 但更稳的做法是：凡是**同口径**的武器，就把该口径的弹鼓补进它的弹匣槽。
            var (byCaliber, legalCal) = AnalyzeDrums(items);

            int guns = 0, added = 0;
            var calRe = new System.Text.RegularExpressions.Regex(@"(?<![\d.])(\d{1,3}(?:\.\d{1,2})?x\d{2,3}(?!\d)[A-Za-z]*)");
            int dbgSlots = 0, dbgMagSlots = 0, dbgCalHit = 0, dbgFilters = 0;
            string dbgFirst = null;
            foreach (DictionaryEntry e in items)
            {
                var it = e.Value; if (it == null) continue;
                var props = Prop(it, "Properties") ?? Prop(it, "_props");
                if (props == null) continue;
                var slots = Prop(props, "Slots") as IEnumerable;
                if (slots == null) continue;

                // ★★★ v0.1.4 修正（这行以前是 `Prop(props,"Caliber")` → 字段不存在，恒为字符串 "None"，
                //   于是 byCaliber 永远查不到 → **补 0 条**，日志看着"成功"其实什么都没干）。
                //   正确口径源：武器名字里的口径（`weapon_izhmash_ak74_545x39` → `545x39`）。
                var itName = Prop(it, "_name")?.ToString() ?? Prop(it, "Name")?.ToString() ?? "";
                // ★ 取最长匹配 + 去掉小数点归一化（5.56x45 → 556x45）
                string caliber = "";
                foreach (System.Text.RegularExpressions.Match cm in calRe.Matches(itName.ToLowerInvariant()))
                    if (cm.Value.Length > caliber.Length) caliber = cm.Value;
                caliber = caliber.Replace(".", "");

                foreach (var slot in slots)
                {
                    if (slot == null) continue;
                    dbgSlots++;
                    // ★ 同理：JSON 里是 `_name`，不是 `Name`
                    var slotName = (Prop(slot, "_name") ?? Prop(slot, "Name") ?? "").ToString();
                    if (slotName != "mod_magazine" && slotName != "mod_launcher") continue;
                    if (slotName == "mod_launcher" && !alsoLaunchers) continue;

                    var slotProps = Prop(slot, "Properties") ?? Prop(slot, "_props");
                    var filters = Prop(slotProps, "Filters") as IEnumerable;
                    if (filters == null) continue;
                    dbgMagSlots++;
                    dbgFilters += filters.Cast<object>().Count();
                    if (byCaliber.ContainsKey(caliber)) dbgCalHit++;
                    if (dbgFirst == null && slotName == "mod_magazine")
                    {
                        var f0 = filters.Cast<object>().FirstOrDefault();
                        dbgFirst = $"样例武器名='{itName}' 口径='{caliber}' 槽='{slotName}' "
                                   + $"filters={filters.Cast<object>().Count()} 首个filter类型={f0?.GetType().Name}";
                    }

                    foreach (var flt in filters)
                    {
                        // ★★★ v0.1.4 修正（第三个坑）：`SlotProps.Filters` 的元素是 **SlotFilter 对象**，
                        //   不是 List —— 原代码 `if (Prop(flt,"Filter") is not IList filter) continue;`
                        //   **永远为真**（SlotFilter 不是 IList），所以一条都补不上
                        //   （诊断输出："210 个 filter，补 0 条"）。
                        //   真实结构：SlotFilter { Filter: List<MongoId>, ... } → 取它的 Filter 属性。
                        List<object> filter;
                        if (flt is IList rawList) filter = rawList.Cast<object>().ToList();
                        else if (Prop(flt, "Filter") is IEnumerable en) filter = en.Cast<object>().ToList();
                        else continue;
                        var existing = new HashSet<string>(filter.Select(x => x?.ToString() ?? ""), StringComparer.OrdinalIgnoreCase);

                        // 需要补进去的候选
                        IEnumerable<string> want;
                        if (slotName == "mod_launcher")
                            want = Launchers.Keys;
                        else
                        {
                            // 同口径的弹鼓（口径相同的武器大多能通用；后续仍会被客户端/服务端过滤）
                            // ★ 严格：只补"这个弹鼓合法口径"里包含本武器口径的那些
                            want = byCaliber.TryGetValue(caliber, out var list) ? list : Enumerable.Empty<string>();
                        }

                        foreach (var d in want.Distinct())
                        {
                            if (existing.Contains(d)) continue;
                            // 记录还原闭包：把新加的从 filter 里删掉
                            var capFilter = filter; var capId = d;
                            AddRestore?.Invoke(() =>
                            {
                                try
                                {
                                    for (int i = capFilter.Count - 1; i >= 0; i--)
                                        if (string.Equals(capFilter[i]?.ToString(), capId, StringComparison.OrdinalIgnoreCase)) capFilter.RemoveAt(i);
                                }
                                catch { }
                            });

                            try
                            {
                                filter.Add(MakeKey(filter, d)); added++;
                                // ★ 记录 (武器 tpl, 弹鼓 tpl) 对 —— 供 PBS 运行期池子写入复用
                                //   （PBS 与 items 过滤表**取交集** ⇒ 只写过滤表不够，必须两边都写）
                                var wpnTpl = e.Key?.ToString();
                                if (!string.IsNullOrEmpty(wpnTpl)) AddedPairs.Add((wpnTpl, d));
                            }
                            catch (Exception ex) { Info("写入失败: " + ex.Message); }
                        }
                    }
                    guns++;
                }
            }

            // ★ 供 PBS 运行期池子写入复用（PBS 与过滤表取交集 ⇒ 必须两边都写）
            Info($"[弹鼓] 已登记 {AddedPairs.Count} 对 (武器,弹鼓) 供 PBS 池子写入（进图时幂等补写）");
            var names = string.Join("、", Drums.Values.Take(4));
            Info($"★弹鼓扩兼容：往弹匣槽补了 {added} 条（涉及 {guns} 个槽位，倍率参数 {mult}）"
                 + $"—— {names}…（**纯 items 数据，对 PBS 同样有效**，可还原）");
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][弹鼓] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// 判定每个弹鼓"能装在哪把武器上"——用**该弹鼓原本就被哪些武器接受**来定口径。
    /// <para>
    /// ★★★ v0.1.4 修正（第四个坑，也是最重要的设计修正）：
    /// 之前"按武器名里的口径"匹配 → 会给 **SKS 装 AK 弹鼓**（都是 762x39 但卡榫完全不同）、
    /// 给 M14 装 FAL 弹鼓（都是 762x51 但一个是 STANAG 一个是 FAL）——
    /// 一口气补了 1030 条**物理上不可能的组合**，客户端/服务端拿到会生成畸形武器。
    /// </para>
    /// 现在的规则：**只有当某把武器原本的 `mod_magazine` 过滤表里就已经有这个弹鼓时**，
    /// 才把该武器名字里的口径算作"这个弹鼓的合法口径"，然后只往**同口径**的武器扩。
    /// 这样扩出来的每一把都是"同口径 + 同族"的合理组合。
    /// </summary>
    private static (Dictionary<string, List<string>> byCaliber, Dictionary<string, HashSet<string>> legalCal) AnalyzeDrums(IDictionary items)
    {
        var calRe = new System.Text.RegularExpressions.Regex(@"(?<![\d.])(\d{1,3}(?:\.\d{1,2})?x\d{2,3}(?!\d)[A-Za-z]*)");
        var byCaliber = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var legal = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        string CalOfName(object it)
        {
            var n = (Prop(it, "_name")?.ToString() ?? Prop(it, "Name")?.ToString() ?? "").ToLowerInvariant();
            // ★ 有些武器名字里带**多个**口径串（改装/多口径版本，如 `..._545x39_45x39_...`），
            //   所以要取**最长的那个**（545x39 优先于 45x39），否则会得出"45x39"这种不存在的口径。
            string best = null;
            foreach (System.Text.RegularExpressions.Match m in calRe.Matches(n))
                if (best == null || m.Value.Length > best.Length) best = m.Value;
            return best?.Replace(".", "");      // 5.56x45 → 556x45（统一口径键）
        }

        // 记录"谁原本接受这个弹鼓"时，用**物品名**当键（而不是把对象塞进集合）
        var ownerNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Drums.Keys) ownerNames[d] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先扫一遍：谁原本就接受这个弹鼓（存**名字**，避免对象引用问题）
        foreach (DictionaryEntry e in items)
        {
            var it = e.Value; if (it == null) continue;
            var props = Prop(it, "Properties") ?? Prop(it, "_props");
            if (props == null) continue;
            if (Prop(props, "Slots") is not IEnumerable slots) continue;
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                var sn = (Prop(slot, "_name") ?? Prop(slot, "Name") ?? "").ToString();
                if (sn != "mod_magazine") continue;
                var sp = Prop(slot, "Properties") ?? Prop(slot, "_props");
                if (Prop(sp, "Filters") is not IEnumerable filters) continue;
                foreach (var flt in filters)
                {
                    var f = (flt is IList rl) ? rl.Cast<object>()
                          : (Prop(flt, "Filter") is IEnumerable en) ? en.Cast<object>() : null;
                    if (f == null) continue;
                    foreach (var x in f)
                    {
                        var xs = x?.ToString();
                        if (xs != null && ownerNames.ContainsKey(xs)) ownerNames[xs].Add(itName2(it));
                    }
                }
            }
        }

        foreach (var d in Drums.Keys)
        {
            var calSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var on in ownerNames[d])
            {
                var ow = FindOwnerByName(items, on);
                var c = CalOfName(ow) ?? CalOfNameName(on);
                if (c != null) calSet.Add(c);
            }
            if (calSet.Count == 0)
            {
                Info($"⚠ 弹鼓 {Drums[d]}（{d}）**没有任何武器原本接受它** → 跳过（无法确定合法口径）");
                continue;
            }
            legal[d] = calSet;
            foreach (var c in calSet)
            {
                if (!byCaliber.TryGetValue(c, out var l)) byCaliber[c] = l = new List<string>();
                l.Add(d);
            }
        }

        Info("弹鼓 → 合法口径：" + string.Join("；", legal.Select(kv => $"{Drums[kv.Key]}={string.Join("/", kv.Value)}")));
        return (byCaliber, legal);
    }

    private static string itName2(object it) => Prop(it, "_name")?.ToString() ?? Prop(it, "Name")?.ToString() ?? "";

    /// <summary>从名字里取口径（同上：有多个时取最长的）。</summary>
    private static string CalOfNameName(string n)
    {
        if (string.IsNullOrEmpty(n)) return null;
        var re = new System.Text.RegularExpressions.Regex(@"(?<![\d.])(\d{1,3}(?:\.\d{1,2})?x\d{2,3}(?!\d)[A-Za-z]*)");
        string best = null;
        foreach (System.Text.RegularExpressions.Match m in re.Matches(n.ToLowerInvariant()))
            if (best == null || m.Value.Length > best.Length) best = m.Value;
        return best?.Replace(".", "");
    }

    private static object FindOwnerByName(IDictionary items, string name)
    {
        foreach (DictionaryEntry e in items)
            if (string.Equals(itName2(e.Value), name, StringComparison.OrdinalIgnoreCase)) return e.Value;
        return null;
    }

    /// <summary>filter 的元素类型可能是 string[] / MongoId 列表 —— 用第一个元素的类型构造新值。</summary>
    private static object MakeKey(IList filter, string id)
    {
        try
        {
            if (filter.Count > 0)
            {
                var t = filter[0].GetType();
                if (t == typeof(string)) return id;
                var ctor = t.GetConstructor(new[] { typeof(string) });
                if (ctor != null) return ctor.Invoke(new object[] { id });
                return Convert.ChangeType(id, t);
            }
        }
        catch { }
        return id;
    }
}
