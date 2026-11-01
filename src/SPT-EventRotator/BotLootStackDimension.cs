using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ v2.6.74【走私者身上带 GP 币 10~20 枚】—— 活动字段 `botLootStack_`。
///
/// <para><b>为什么不能只用现成的 `botLootAdd_`</b>（用户任务 B 的原问题）：
/// `botLootAdd_` 只是往 `BotTable.Types[role].BotInventory.Items.&lt;池&gt;` 里加
/// <c>tpl → 相对权重</c>（<see cref="BotEquipmentDimension.ApplyMods"/>）。而 SPT 抽到该物品后
/// <b>数量是固定的 1</b>：`BotLootGenerator.AddLootFromPool` → `new Item{ Upd =
/// botGeneratorHelper.GenerateExtraPropertiesForItem(tpl, role, forceStackObjectsCount: true) }`，
/// 而 `BotGeneratorHelper.cs:141-155` 里 <c>forceStackObjectsCount</c> 只做
/// 「<c>StackObjectsCount</c> 没有就给 1.0」⇒ **战利品池里无法指定数量**。
/// 唯一例外是**货币**（`BotLootGenerator.cs:304-317 AddRequiredChildItemsToParent`
/// → `IsOfBaseclass(MONEY)` → `RandomiseMoneyStackSize`），它的数量取自
/// `BotConfig.CurrencyStackSize[role][tpl]` 的加权随机。</para>
///
/// <para><b>本维度分两层（缺一层就不成立）</b>：
/// <list type="number">
///   <item><b>① 配置层（数量）</b>：把 <c>BotConfig.CurrencyStackSize[roleLowercase][tpl]</c> 写成
///     <c>{min:1 … max:1}</c>（**先克隆 `default` 再覆盖该 tpl**，否则其它货币 tpl 会
///     在 `RandomiseMoneyStackSize` 里触发 `KeyNotFoundException` —— 它是**索引器**取值，
///     见 `BotLootGenerator.cs:419-430`）。
///     <br/>⚠ **这一层对 SPT 原版生成器有效，对 PBS/ABPS 也有效**（旧版归档
///     `audit/pbs_v416/...decompiled.cs` 的 `CustomBotLootGenerator.RandomiseMoneyStackSize`
///     `:45045-45052` 读的是**同一个** `botConfig.CurrencyStackSize`）✔</item>
///   <item><b>② 产出物层（必定携带 + 只一叠）</b>：`BotGenerator.PrepareAndGenerateBot` 的 postfix
///     （本项目已有挂点，见 `Patches/BossWavePatch.cs` 的 <c>BotGenerated</c>）里，
///     对登记过的 role：把它身上**所有**该 tpl 的堆叠合并成**一叠**、数量重写为 `[min,max]` 随机；
///     **一个都没有就当场加一个**（放进 Pockets 的某个空格子；没有 Pockets 就退到
///     TacticalVest/Backpack/SecuredContainer）。
///     <br/>为什么必须做这一层：装了 **PBS/ABPS** 时，抽几次货币由 **PBS 自己的分级表**决定
///     （`audit/pbs_v416/...decompiled.cs` `GenerateLoot` 用的是
///     `botEquipmentHelper.GetChancesByBotRole(role, tier).Generation.Items`，**根本不读**
///     `botJsonTemplate.BotGeneration.Items`）。实测本机 PBS `Data/Chances/Tier*_chances.json`
///     里 `exusec`（`arenafighterevent` 走这条分支，见 `GetChancesByBotRole` 的
///     `case "arenafighter"/"exusec"/"arenafighterevent"`）的
///     `generation.items.currency.weights = {0:20, 1:5, 2:1}` ⇒ **约 77% 的走私者根本不掉货币**
///     ⇒ 只靠配置层做不到「他们身上带 GP 币」。</item>
/// </list></para>
///
/// <para><b>落点合法性（为什么可以往成品 bot 里加物品）</b>：
/// 本项目的既有做法就是「改写产出物而不是改权重池」（见 <see cref="EventGearUpgrade"/> 类注释：
/// PBS 会整体替换 `BotInventoryGenerator.GenerateInventory`，但 `PrepareAndGenerateBot` 的
/// **postfix 照常执行**，`__result` 即最终产物）。加进去的字段形状逐字对齐
/// `BotInventoryContainerService.TryAddItemToBotContainer`（`_forensic_tmp/ilspy_full/...:104-125`）：
/// <c>ParentId = 容器物品 Id</c>、<c>SlotId = 该 grid 的 `_name`</c>（大口袋是
/// `pocket1`…`pocket5`，箱包是 `main`）、<c>Location = {x,y,r}</c>。</para>
///
/// <para><b>降级</b>：role 找不到 / 容器找不到 / 模板解析失败 / 装不进 ⇒ **warning + 跳过**，
/// 绝不影响 bot 生成（全部 try/catch，外层再包一层）。</para>
///
/// <para><b>可还原</b>：配置层的写入全部登记 <c>AddRestore</c> 闭包；
/// 产出物层是每局一次性对象（与 <see cref="EventGearUpgrade"/> 同语义）——
/// 活动关掉后 <see cref="ClearRound"/> 清空规格，就不再改。</para>
/// </summary>
internal static class BotLootStackDimension
{
    public static object BotConfig;         // SPT BotConfig（含 CurrencyStackSize）
    public static object BotTable;          // BotTable（取 role 模板做回读自证）
    public static object TemplateTable;     // TemplateTable（读物品/容器的 _props）
    public static Action<string> Log;
    public static Action<string> Warn;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][bot携带] " + m); } catch { } }
    private static void W(string m) { try { Warn?.Invoke("[EventRotator][bot携带] ⚠ " + m); } catch { } }

    /// <summary>role（忽略大小写）→ tpl → (min,max)。由 Plugin 在套用活动时登记，<see cref="ClearRound"/> 清空。</summary>
    private static readonly Dictionary<string, Dictionary<string, (int Min, int Max)>> _specs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Random _rnd = new();

    /// <summary>`Item` / `Upd` / `ItemLocation` / `ItemRotation` 类型（反射拿到，避免硬依赖具体版次）。</summary>
    private static Type _itemType, _updType, _locType;

    public static bool IsEmpty => _specs.Count == 0;

    public static void ClearRound()
    {
        try { _specs.Clear(); _logged.Clear(); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ① 配置层：BotConfig.CurrencyStackSize[roleLower][tpl] = {min..max: 1}
    // ═══════════════════════════════════════════════════════════════════════
    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            var data = ev?.BotLootStack_;
            if (data == null || data.Count == 0) return;
            if (BotConfig == null) { W("拿不到 BotConfig，配置层跳过（产出物层仍会生效）"); }

            foreach (var roleKv in data)
            {
                var role = roleKv.Key?.Trim();
                if (string.IsNullOrEmpty(role)) continue;
                var items = roleKv.Value;
                if (items == null || items.Count == 0) continue;

                var spec = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
                foreach (var itemKv in items)
                {
                    var tpl = itemKv.Key?.Trim();
                    if (string.IsNullOrEmpty(tpl)) continue;
                    var range = itemKv.Value;
                    int min, max;
                    if (range != null && range.Count >= 2) { min = range[0]; max = range[1]; }
                    else if (range != null && range.Count == 1) { min = max = range[0]; }
                    else { W($"{role}: tpl {tpl} 没给数量区间（要 [min,max]），跳过"); continue; }
                    if (min > max) { var t = min; min = max; max = t; }
                    if (min < 1) min = 1;
                    var cap = StackMaxSize(tpl);
                    if (cap > 0 && max > cap)
                    {
                        W($"{role}: tpl {tpl} 的数量上限 {max} > StackMaxSize {cap} ⇒ 夹到 {cap}");
                        max = cap;
                        if (min > max) min = max;
                    }
                    spec[tpl] = (min, max);
                }
                if (spec.Count == 0) continue;
                _specs[role] = spec;

                if (BotConfig != null) ApplyCurrencyStackSize(role, spec);
                Info($"'{ev.Id}' 登记 {role}：必然携带 {string.Join("、", spec.Select(x => $"{x.Key} ×{x.Value.Item1}~{x.Value.Item2}"))}");
            }
        }
        catch (Exception ex) { W($"配置层失败（已降级）: {ex.Message}"); }
    }

    private static void ApplyCurrencyStackSize(string role, Dictionary<string, (int Min, int Max)> spec)
    {
        try
        {
            var css = R.Prop(BotConfig, "CurrencyStackSize");
            if (css == null || !css.GetType().IsGenericType) { W("BotConfig.CurrencyStackSize 拿不到，配置层跳过"); return; }

            var roleDictType = css.GetType().GetGenericArguments()[1];          // Dictionary<string, Dictionary<string,double>>
            var sizeDictType = roleDictType.GetGenericArguments()[1];           // Dictionary<string,double>
            if (!(css is IDictionary cssDict)) { W("CurrencyStackSize 不是 IDictionary"); return; }

            // ★ SPT 自己的 key 是**小写 role**（BotGenerator.cs:158 `RoleLowercase`），
            //   而 `TryGetValue(botRole)` 是大小写敏感的 ⇒ 必须写小写。
            var roleKey = role.ToLowerInvariant();

            // 基座：已有该 role 就用它，否则用 `default`（保证**其它货币 tpl 也在**，
            // 否则 RandomiseMoneyStackSize 的索引器取值会抛 KeyNotFoundException）
            object baseRole = null;
            try
            {
                if (cssDict.Contains(roleKey)) baseRole = cssDict[roleKey];
                else if (cssDict.Contains("default")) baseRole = cssDict["default"];
            }
            catch { }

            object newRole;
            try { newRole = Activator.CreateInstance(roleDictType); } catch { W("造角色货币字典失败"); return; }
            if (newRole is IDictionary newRoleDict)
            {
                var src = baseRole as IDictionary;
                if (src != null)
                {
                    foreach (DictionaryEntry kv in src)
                    {
                        try { newRoleDict[kv.Key] = kv.Value; } catch { }
                    }
                }
                foreach (var kv in spec)
                {
                    object sizeDict;
                    try { sizeDict = Activator.CreateInstance(sizeDictType); } catch { continue; }
                    if (sizeDict is IDictionary sd)
                        for (int n = kv.Value.Min; n <= kv.Value.Max; n++)
                        {
                            try { sd[n.ToString(CultureInfo.InvariantCulture)] = 1.0; } catch { }
                        }
                    // 该 role 下若原本没有这个 tpl，也要能写进去（string 键）
                    try { newRoleDict[kv.Key] = sizeDict; } catch (Exception ex) { W($"写 CurrencyStackSize[{roleKey}][{kv.Key}] 失败: {ex.Message}"); }
                }
            }

            // 快照 + 还原（原来没有该 role ⇒ 还原时删掉）
            bool existed = false; object old = null;
            try { existed = cssDict.Contains(roleKey); if (existed) old = cssDict[roleKey]; } catch { }
            var capDict = cssDict; var capKey = roleKey; var capEx = existed; var capOld = old;
            AddRestore?.Invoke(() =>
            {
                try
                {
                    if (capEx) capDict[capKey] = capOld;
                    else if (capDict.Contains(capKey)) capDict.Remove(capKey);
                }
                catch { }
            });

            cssDict[roleKey] = newRole;
            // 回读自证
            string proof = "(回读失败)";
            try
            {
                var back = (cssDict[roleKey] as IDictionary);
                if (back != null)
                {
                    var parts = new List<string>();
                    foreach (var kv in spec)
                    {
                        var sd = back[kv.Key] as IDictionary;
                        parts.Add(sd == null ? $"{kv.Key}=缺失" :
                            $"{kv.Key}={{[{string.Join(",", sd.Keys.Cast<object>().Select(k => k?.ToString()))}]}}");
                    }
                    proof = string.Join(" ", parts);
                }
            }
            catch { }
            Info($"CurrencyStackSize['{roleKey}'] ← {proof}（每叠数量加权表；键=数量，值=权重，全部 1 ⇒ 均匀）");
        }
        catch (Exception ex) { W($"写 CurrencyStackSize 失败（已降级）: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ② 产出物层：由 `BotGenerator.PrepareAndGenerateBot` 的 postfix 调用
    // ═══════════════════════════════════════════════════════════════════════
    public static void OnBotGenerated(object details, object bot)
    {
        if (IsEmpty || bot == null) return;
        try
        {
            var role = R.Prop(R.Prop(R.Prop(bot, "Info"), "Settings"), "Role")?.ToString()
                    ?? R.Prop(details, "Role")?.ToString()
                    ?? R.Prop(details, "RoleLowercase")?.ToString();
            if (string.IsNullOrEmpty(role)) return;
            role = role.Trim();
            if (!_specs.TryGetValue(role, out var spec) || spec == null) return;

            var items = R.Prop(R.Prop(bot, "Inventory"), "Items") as IList;
            if (items == null) { W($"{role}: 拿不到 Inventory.Items"); return; }

            foreach (var kv in spec)
            {
                var tpl = kv.Key;
                int min = kv.Value.Min, max = kv.Value.Max;
                int want = min == max ? min : _rnd.Next(min, max + 1);

                var stacks = new List<object>();
                foreach (var it in items)
                {
                    var t = R.Prop(it, "Template")?.ToString() ?? R.Prop(it, "_tpl")?.ToString();
                    if (string.Equals(t, tpl, StringComparison.OrdinalIgnoreCase)) stacks.Add(it);
                }

                if (stacks.Count > 0)
                {
                    // 只留一叠，其余合并掉（数量已由 ① 决定，这里强制重写一次）
                    var keep = stacks[0];
                    for (int i = stacks.Count - 1; i >= 1; i--)
                    {
                        try { items.Remove(stacks[i]); } catch { }
                    }
                    bool ok = SetStack(keep, want);
                    if (_logged.Add(role + "|" + tpl))
                        Info($"{role}: 身上已有 {stacks.Count} 叠 {tpl} ⇒ 合并为 1 叠 ×{want}{(ok ? " ✔" : "（⚠ 写数量失败）")}");
                }
                else
                {
                    var added = AddToContainer(items, tpl, want, out var where);
                    if (_logged.Add(role + "|" + tpl))
                    {
                        if (added) Info($"{role}: 原本没有 {tpl} ⇒ 已加 1 叠 ×{want} 到 {where} ✔");
                        else W($"{role}: 原本没有 {tpl}，且加不进去（{where}）—— 该 bot 这次不带");
                    }
                }
            }
        }
        catch (Exception ex) { W($"产出物层失败（已跳过，不影响 bot 生成）: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>把某个物品的 `Upd.StackObjectsCount` 写成给定值（必要时补一个 `Upd`）。</summary>
    private static bool SetStack(object item, int count)
    {
        try
        {
            var upd = R.Prop(item, "Upd");
            if (upd == null)
            {
                if (_updType == null) return false;
                try { upd = Activator.CreateInstance(_updType); } catch { return false; }
                if (!R.SetProp(item, "Upd", upd)) return false;
            }
            return R.SetProp(upd, "StackObjectsCount", (double)count);
        }
        catch { return false; }
    }

    /// <summary>
    /// 往 bot 的容器（Pockets → TacticalVest → Backpack → SecuredContainer）里加一件物品。
    /// 槽/坐标的写法逐字对齐 `BotInventoryContainerService.TryAddItemToBotContainer`。
    /// </summary>
    private static bool AddToContainer(IList items, string tpl, int count, out string where)
    {
        where = "";
        try
        {
            EnsureTypes();
            if (_itemType == null) { where = "拿不到 Item 类型"; return false; }

            foreach (var slot in new[] { "Pockets", "TacticalVest", "Backpack", "SecuredContainer" })
            {
                object container = null;
                foreach (var it in items)
                {
                    var sl = R.Prop(it, "slotId")?.ToString() ?? R.Prop(it, "SlotId")?.ToString();
                    if (string.Equals(sl, slot, StringComparison.OrdinalIgnoreCase)) { container = it; break; }
                }
                if (container == null) continue;

                var contId = R.Prop(container, "Id")?.ToString() ?? R.Prop(container, "_id")?.ToString();
                var contTpl = R.Prop(container, "Template")?.ToString() ?? R.Prop(container, "_tpl")?.ToString();
                if (string.IsNullOrEmpty(contId) || string.IsNullOrEmpty(contTpl)) continue;

                var grids = GridsOf(contTpl);
                if (grids == null || grids.Count == 0) continue;

                foreach (var g in grids)
                {
                    var gridName = R.Prop(g, "Name")?.ToString() ?? "main";
                    var props = R.Prop(g, "Properties");
                    int cellsH = ToInt(R.Prop(props, "CellsH"), 1);
                    int cellsV = ToInt(R.Prop(props, "CellsV"), 1);
                    if (cellsH <= 0 || cellsV <= 0) continue;

                    var used = new bool[cellsH, cellsV];
                    bool gridDirty = false;      // ★ 有子物品读不出坐标 ⇒ 无法保证不重叠 ⇒ 这个 grid 直接放弃（宁可换下一个）
                    foreach (var it in items)
                    {
                        if (ReferenceEquals(it, container)) continue;
                        var pid = R.Prop(it, "parentId")?.ToString() ?? R.Prop(it, "ParentId")?.ToString();
                        if (!string.Equals(pid, contId, StringComparison.OrdinalIgnoreCase)) continue;
                        var childGrid = R.Prop(it, "slotId")?.ToString() ?? R.Prop(it, "SlotId")?.ToString();
                        if (!string.IsNullOrEmpty(childGrid) && !string.Equals(childGrid, gridName, StringComparison.OrdinalIgnoreCase)) continue;
                        var loc = R.Prop(it, "Location") ?? R.Prop(it, "location");
                        if (!TryGetXY(loc, out int x, out int y)) { gridDirty = true; break; }
                        int iw = Math.Max(1, SizeOf(R.Prop(it, "Template")?.ToString() ?? R.Prop(it, "_tpl")?.ToString(), 0));
                        int ih = Math.Max(1, SizeOf(R.Prop(it, "Template")?.ToString() ?? R.Prop(it, "_tpl")?.ToString(), 1));
                        for (int dy = 0; dy < ih; dy++)
                            for (int dx = 0; dx < iw; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx >= 0 && xx < cellsH && yy >= 0 && yy < cellsV) used[xx, yy] = true;
                            }
                    }
                    if (gridDirty) continue;

                    // 找空位（我们的物品是 1×1，见 tools/verify_bot_loot_stack.py 的自证）
                    int fx = -1, fy = -1;
                    for (int yy = 0; yy < cellsV && fy < 0; yy++)
                        for (int xx = 0; xx < cellsH; xx++)
                            if (!used[xx, yy]) { fx = xx; fy = yy; break; }
                    if (fx < 0) continue;

                    var newId = NewId();
                    var json = "{\"_id\":\"" + newId + "\",\"_tpl\":\"" + tpl + "\","
                             + "\"parentId\":\"" + contId + "\",\"slotId\":\"" + gridName + "\","
                             + "\"upd\":{\"StackObjectsCount\":" + count + "}}";
                    var item = R.FromJson(_itemType, json, W);
                    if (item == null) { where = "造物品失败"; continue; }

                    // location 用强类型 ItemLocation 塞（`Location` 是 object，走 SPT 自己的模型 ⇒
                    // 序列化结果与 SPT 自己生成的战利品**完全同形**）
                    if (_locType == null)
                    {
                        W("拿不到 ItemLocation 类型 ⇒ 加进去的物品没有坐标（客户端可能摆到 (0,0)）");
                    }
                    else
                    {
                        try
                        {
                            var loc = Activator.CreateInstance(_locType);
                            R.SetProp(loc, "X", fx);
                            R.SetProp(loc, "Y", fy);
                            R.SetProp(loc, "R", "Horizontal");     // ItemRotation.Horizontal
                            R.SetProp(item, "Location", loc);
                        }
                        catch (Exception ex) { W($"造 location 失败: {ex.Message}"); }
                    }

                    items.Add(item);
                    where = $"{slot}/{gridName}({fx},{fy})";
                    return true;
                }
            }
            where = "四个容器都没有空位/容器";
            return false;
        }
        catch (Exception ex) { where = "异常 " + ex.GetType().Name; return false; }
    }

    /// <summary>取容器模板的 grid 列表（`TemplateTable.Items[tpl].Properties.Grids`）。</summary>
    private static IList GridsOf(string containerTpl)
    {
        try
        {
            var table = R.Prop(TemplateTable, "Items") as IDictionary;
            if (table == null) return null;
            object tplItem = null;
            var key = R.Coerce(containerTpl, R.DictKeyType(table));
            try { if (key != null && table.Contains(key)) tplItem = table[key]; } catch { }
            if (tplItem == null) tplItem = R.DictGet(table, containerTpl);
            var props = R.Prop(tplItem, "Properties");
            var grids = R.Prop(props, "Grids") as IList;
            if (grids != null) return grids;
            var en = R.Prop(props, "Grids") as IEnumerable;
            return en?.Cast<object>().ToList();
        }
        catch { return null; }
    }

    /// <summary>物品在 grid 里的占位尺寸（宽 / 高，`_props.Width`/`_props.Height`）。</summary>
    private static int SizeOf(string tpl, int which)
    {
        try
        {
            var table = R.Prop(TemplateTable, "Items") as IDictionary;
            var tplItem = table == null ? null : R.DictGet(table, tpl);
            var props = R.Prop(tplItem, "Properties");
            return ToInt(R.Prop(props, which == 0 ? "Width" : "Height"), 1);
        }
        catch { return 1; }
    }

    private static int StackMaxSize(string tpl)
    {
        try
        {
            var table = R.Prop(TemplateTable, "Items") as IDictionary;
            var tplItem = table == null ? null : R.DictGet(table, tpl);
            return ToInt(R.Prop(R.Prop(tplItem, "Properties"), "StackMaxSize"), 0);
        }
        catch { return 0; }
    }

    /// <summary>
    /// 读物品在容器 grid 里的坐标。
    /// <para>⚠ 两种形态都要支持（踩过）：SPT 运行时给的是强类型 <c>ItemLocation</c>（X/Y 属性），
    /// 但**经 JSON 往返过**的物品（例如我们自己用 `R.FromJson` 造出来的假数据、或别的 mod 走 JSON 克隆）
    /// 的 `Location` 会是 <c>JsonElement</c> —— 读不到就当这个 grid 不可用（宁可换下一个，也不叠放）。</para>
    /// </summary>
    private static bool TryGetXY(object loc, out int x, out int y)
    {
        x = y = -1;
        if (loc == null) return false;
        try
        {
            if (loc is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var key in new[] { "x", "X" })
                    if (je.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                    { x = v.GetInt32(); break; }
                foreach (var key in new[] { "y", "Y" })
                    if (je.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                    { y = v.GetInt32(); break; }
                return x >= 0 && y >= 0;
            }
            x = ToInt(R.Prop(loc, "X") ?? R.Prop(loc, "x"), -1);
            y = ToInt(R.Prop(loc, "Y") ?? R.Prop(loc, "y"), -1);
            return x >= 0 && y >= 0;
        }
        catch { return false; }
    }

    private static int ToInt(object v, int dflt)
    {
        try
        {
            if (v == null) return dflt;
            if (v is IConvertible) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return int.TryParse(v.ToString(), out var n) ? n : dflt;
        }
        catch { return dflt; }
    }

    private static readonly Random _idRnd = new();
    private static string NewId()
    {
        const string hex = "0123456789abcdef";
        var c = new char[24];
        for (int i = 0; i < 24; i++) c[i] = hex[_idRnd.Next(16)];
        return new string(c);
    }

    /// <summary>惰性解析 Item / Upd / ItemLocation 类型（首次调用时从已加载程序集里找）。</summary>
    private static void EnsureTypes()
    {
        if (_itemType != null) return;
        try
        {
            // ★★★ v1.1.0【BugFix·同 FaceCoverFix】prepatch 双载下 AppDomain 里有 2 份 Core，
            //   必须先取**运行时那份**的 Item/ItemLocation，否则 R.FromJson 会因 MongoId
            //   类型不匹配报 "could not be converted to ... MongoId. Path: $._id"。
            var liveItem = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Eft.Common.Tables.Item");
            if (liveItem != null && liveItem.GetProperty("Template") != null) _itemType = liveItem;
            var liveLoc = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemLocation");
            if (liveLoc != null) _locType = liveLoc;

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                // ★ 只认运行时那份（LiveCoreAssembly 拿不到时才退回全扫）
                if (RuntimeTypes.LiveCoreAssembly != null && !ReferenceEquals(a, RuntimeTypes.LiveCoreAssembly)) continue;
                Type[] ts;
                try { ts = a.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { ts = rtle.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                if (_itemType == null)
                    _itemType = ts.FirstOrDefault(t => t.Name == "Item" && t.Namespace != null
                                                    && t.Namespace.Contains("Eft.Common.Tables")
                                                    && t.GetProperty("Template") != null);
                if (_locType == null)
                    _locType = ts.FirstOrDefault(t => t.Name == "ItemLocation" && t.Namespace != null
                                                   && t.Namespace.Contains("Eft.Common.Tables"));
                if (_itemType != null && _locType != null) break;
            }
            if (_itemType != null)
                _updType = _itemType.GetProperty("Upd")?.PropertyType;
        }
        catch { }
    }
}
