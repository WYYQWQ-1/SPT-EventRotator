using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IOPath = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace EventRotator;

/// <summary>
/// ★★★ 自定义任务线注入（v2.6.4x）——把 `questlines/&lt;活动id&gt;.json` 里的任务**并进活动 mod**
/// （不另开 mod、不碰别人的 db 目录）。
///
/// ─────────────────────────────────────────────────────────────────────────────────────
/// 落点（全部有反编译依据）：
///   ① 任务本体 ⇒ `TemplateTable.Quests`（`Dictionary&lt;MongoId, Quest&gt;`）
///      依据：`QuestHelper.GetQuestsFromDb()`（QuestHelper.cs:578-581）= `templateTable.Quests.Values`
///      —— **运行期实时读**（本 mod 门控读的就是它），所以在 `OnLoadAsync` 里加进去，
///      客户端下一次 `/client/quest/list` 就能看到，不需要重启。
///   ② 任务文案 ⇒ `LocaleTable.Global[lang]`（`LazyLoad&lt;GlobalLocaleDictionary&gt;`，
///      而 `GlobalLocaleDictionary : Dictionary&lt;string,string&gt;`）
///      依据：`LocaleService.GetLocaleDb()`（LocaleService.cs:23-31）取的就是 `localeTable.Global[key].Value`；
///      而 `LazyLoad&lt;T&gt;.AddTransformer(Func&lt;T,T&gt;)`（LazyLoad.cs:50）是 SPT 官方留给"往懒加载表里补东西"的口子
///      —— 本 mod 的「比特币改名」用的正是它（ExtraDimensions.ApplyRenameBitcoin），这里沿用同一手法。
///      任务文案的键名规则照数据格式：`&lt;任务id&gt; name` / `&lt;任务id&gt; description` /
///      `&lt;任务id&gt; successMessageText` / `&lt;任务id&gt; failMessageText`
///      （与 SPT_Data/database/locales/global/*.json 里现成任务逐字一致）。
///
/// ─────────────────────────────────────────────────────────────────────────────────────
/// 数据格式（`questlines/scourge_incursion.json`）：
/// <code>
/// {
///   "eventId": "scourge_incursion",          // 归属活动 id（与 events.json 对齐，供门控/自检用）
///   "quests": { "&lt;24位id&gt;": { …整份 Quest JSON… } },
///   "locales": { "en": { "&lt;id&gt; name": "…" }, "ch": { … } }
/// }
/// </code>
///
/// ─────────────────────────────────────────────────────────────────────────────────────
/// 降级原则（和本 mod 其它维度一致：**任何一步失败都不许把游戏搞坏**）：
///   · `questlines/` 不存在 ⇒ 静默返回（老用户升上来没有这个目录是正常状态）；
///   · 单个文件解析失败 / 单条任务缺必填字段 ⇒ 跳过这一条 + warning，其它照常；
///   · 表拿不到（`Quests`/`Global` 为 null）⇒ warning + 什么都不加。
/// </summary>
internal static class QuestLineDimension
{
    /// <summary>日志出口（由注入入口赋值，避免这里依赖具体 IOnLoad 类型的 logger）。</summary>
    public static Action<string>? Warn;
    public static Action<string>? Info;

    /// <summary>活动 id → 该活动线注入的任务 id（文件里 `quests` 的书写顺序 = 任务线顺序）。</summary>
    public static readonly Dictionary<string, List<string>> ByEvent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次运行注入成功、并且真的进了 `TemplateTable.Quests` 的任务 id。</summary>
    public static readonly HashSet<string> Injected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次运行注入成功的自定义物品 tpl（任务要上交的那些）。</summary>
    public static readonly HashSet<string> InjectedItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>自定义物品 tpl → 它属于哪条任务线（活动 id）。</summary>
    public static readonly Dictionary<string, string> ItemOwner = new(StringComparer.OrdinalIgnoreCase);

    public static void Load(string modPath, TemplateTable templateTable, LocaleTable localeTable, JsonUtil jsonUtil)
    {
        try
        {
            var dir = IOPath.Combine(modPath, "questlines");
            if (!Directory.Exists(dir))
            {
                Info?.Invoke("[EventRotator][任务线] 没有 questlines/ 目录，跳过自定义任务注入");
                return;
            }

            var files = Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return;

            if (templateTable?.Quests == null)
            {
                Warn?.Invoke("[EventRotator][任务线] 拿不到 TemplateTable.Quests ⇒ 自定义任务一条都不加");
                return;
            }

            foreach (var file in files)
            {
                try { LoadOne(file, templateTable, localeTable, jsonUtil); }
                catch (Exception ex)
                {
                    Warn?.Invoke($"[EventRotator][任务线] {IOPath.GetFileName(file)} 处理失败，跳过：{ex.Message}");
                }
            }

            if (Injected.Count > 0 || InjectedItems.Count > 0)
            {
                Info?.Invoke($"[EventRotator][任务线] 已注入 {Injected.Count} 条自定义任务 / {InjectedItems.Count} 件自定义物品（"
                          + string.Join(", ", ByEvent.Select(kv => kv.Key + "×" + kv.Value.Count)) + "）"
                          + (InjectedItems.Count > 0 ? "，物品：" + string.Join(", ", InjectedItems) : ""));
            }
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"[EventRotator][任务线] 注入过程异常（已忽略）：{ex.Message}");
        }
    }

    private static void LoadOne(string file, TemplateTable templateTable, LocaleTable localeTable, JsonUtil jsonUtil)
    {
        var name = IOPath.GetFileName(file);
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;

        var eventId = root.TryGetProperty("eventId", out var e) ? (e.GetString() ?? "") : "";
        var ids = new List<string>();

        if (root.TryGetProperty("quests", out var quests) && quests.ValueKind == JsonValueKind.Object)
        {
            foreach (var q in quests.EnumerateObject())
            {
                var id = q.Name;
                if (!IsMongoId(id))
                {
                    Warn?.Invoke($"[EventRotator][任务线] {name}: 任务 id「{id}」不是 24 位十六进制，跳过");
                    continue;
                }
                try
                {
                    var quest = jsonUtil.Deserialize<Quest>(q.Value.GetRawText());
                    if (quest == null) { Warn?.Invoke($"[EventRotator][任务线] {name}: {id} 反序列化为 null，跳过"); continue; }
                    // ★ 硬校验（审查结论 P0-2/P0-3）：SPT 读任务列表时对这两处**没有 null 检查** ——
                    //   `QuestHelper.cs:1000` 直接 foreach `quest.Rewards`（缺了每次 /client/quest/list 抛 NRE）；
                    //   `QuestRewardHelper.cs:70` 取 `quest.Rewards[state]`（缺 Started/Success 键 ⇒ KeyNotFoundException）；
                    //   `QuestHelper.cs:901` 取 `quest.Conditions.AvailableForStart.GetQuestConditions()`（缺键 NRE）。
                    //   ⇒ 不合格的任务宁可不注入，也不能让玩家的任务列表崩。
                    var invalid = ValidateQuest(quest);
                    if (invalid != null)
                    {
                        Warn?.Invoke($"[EventRotator][任务线] {name}: 任务 {id} 不合格（{invalid}），跳过");
                        continue;
                    }
                    var key = new MongoId(id);
                    if (templateTable.Quests.ContainsKey(key))
                    {
                        Warn?.Invoke($"[EventRotator][任务线] {name}: 任务 {id} 已存在（id 撞车），跳过");
                        continue;
                    }
                    templateTable.Quests[key] = quest;
                    Injected.Add(id);
                    ids.Add(id);
                }
                catch (Exception ex)
                {
                    Warn?.Invoke($"[EventRotator][任务线] {name}: 任务 {id} 反序列化失败，跳过：{ex.Message}");
                }
            }
        }

        if (ids.Count > 0 && eventId.Length > 0) ByEvent[eventId] = ids;

        // ── 物品：任务要上交的自定义物品（如 Scourge 的智能手机 / 前体物质容器） ──
        //    落点 = `TemplateTable.Items`（`Dictionary<MongoId, TemplateItem>`）
        //    依据：客户端 `/client/items` 与任务条件校验读的都是这张表；加入后即可被 HandoverItem 条件引用。
        if (root.TryGetProperty("items", out var itemsNode) && itemsNode.ValueKind == JsonValueKind.Object)
        {
            if (templateTable?.Items == null)
            {
                Warn?.Invoke("[EventRotator][任务线] 拿不到 TemplateTable.Items ⇒ 自定义物品一件都不加");
            }
            else
            {
                foreach (var it in itemsNode.EnumerateObject())
                {
                    var id = it.Name;
                    if (!IsMongoId(id))
                    {
                        Warn?.Invoke($"[EventRotator][任务线] {name}: 物品 id「{id}」不是 24 位十六进制，跳过");
                        continue;
                    }
                    try
                    {
                        var tpl = jsonUtil.Deserialize<TemplateItem>(it.Value.GetRawText());
                        if (tpl == null) { Warn?.Invoke($"[EventRotator][任务线] {name}: 物品 {id} 反序列化为 null，跳过"); continue; }
                        var key = new MongoId(id);
                        if (templateTable.Items.ContainsKey(key))
                        {
                            Warn?.Invoke($"[EventRotator][任务线] {name}: 物品 {id} 已存在（tpl 撞车），跳过");
                            continue;
                        }
                        templateTable.Items[key] = tpl;
                        InjectedItems.Add(id);
                        if (eventId.Length > 0) ItemOwner[id] = eventId;

                        // ── 价格 + 手册：不上这两样，跳蚤里根本不会出现（也没法定价） ──
                        //   依据：`RagfairPriceService.GetFleaPriceForItem()`（RagfairPriceService.cs:155-172）
                        //         = `GetDynamicPriceForItem()`（= TemplateTable.Prices）→ 兜底 `GetStaticPriceForItem()`（= Handbook）
                        //         ⇒ 两处都没有时它会打 `ragfair-unable_to_find_item_price_for_item_in_flea_handbook` 告警、
                        //           并且返回 `num.Value`（可空为 null ⇒ 抛异常）。
                        //   手册结构：`HandbookBase.Items`（List<HandbookItem>：Id / ParentId / Price）。
                        ApplyItemMeta(id, metaFor(root, id), templateTable);

                    }
                    catch (Exception ex)
                    {
                        Warn?.Invoke($"[EventRotator][任务线] {name}: 物品 {id} 反序列化失败，跳过：{ex.Message}");
                    }
                }
            }
        }

        // ── 文案：往 en/ch 的 locale 懒加载表里挂补丁 ──
        if (root.TryGetProperty("locales", out var locales) && locales.ValueKind == JsonValueKind.Object && localeTable?.Global != null)
        {
            foreach (var langNode in locales.EnumerateObject())
            {
                var lang = langNode.Name;
                var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
                if (langNode.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in langNode.Value.EnumerateObject())
                    {
                        if (kv.Value.ValueKind == JsonValueKind.String)
                            pairs[kv.Name] = kv.Value.GetString() ?? "";
                    }
                }
                if (pairs.Count == 0) continue;

                if (!localeTable.Global.TryGetValue(lang, out var lazy) || lazy == null)
                {
                    Warn?.Invoke($"[EventRotator][任务线] {name}: locale 表里没有语种「{lang}」，该语种的文案没加");
                    continue;
                }
                lazy.AddTransformer(d =>
                {
                    if (d == null) return d;
                    foreach (var p in pairs) d[p.Key] = p.Value;
                    return d;
                });
            }
        }
    }

    private static bool IsMongoId(string s)
    {
        if (s == null || s.Length != 24) return false;
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>取 `itemMeta` 节点里某件物品的配置（价格 / 手册分类）。</summary>
    private static JsonElement? metaFor(JsonElement root, string id)
    {
        if (!root.TryGetProperty("itemMeta", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
        if (!meta.TryGetProperty(id, out var node) || node.ValueKind != JsonValueKind.Object) return null;
        return node;
    }

    /// <summary>把价格写进 `TemplateTable.Prices`、把手册条目追加进 `HandbookBase.Items`。任何一步失败只告警。</summary>
    private static void ApplyItemMeta(string id, JsonElement? meta, TemplateTable templateTable)
    {
        if (meta == null) return;
        var m = meta.Value;
        double price = 0;
        var hasPrice = m.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out price);

        var key = new MongoId(id);
        if (hasPrice && templateTable.Prices != null)
        {
            templateTable.Prices[key] = price;
        }
        else if (hasPrice)
        {
            Warn?.Invoke($"[EventRotator][任务线] 拿不到 TemplateTable.Prices ⇒ 物品 {id} 没有价格");
        }

        if (m.TryGetProperty("handbookParent", out var hp) && hp.ValueKind == JsonValueKind.String)
        {
            var parent = hp.GetString() ?? "";
            if (IsMongoId(parent) && templateTable.Handbook?.Items != null)
            {
                Info?.Invoke($"[EventRotator][任务线] 物品 {id}: 价格={price}，手册分类={parent}");
                var exists = templateTable.Handbook.Items.Any(x => x != null && x.Id == key);
                if (!exists)
                {
                    templateTable.Handbook.Items.Add(new SPTarkov.Server.Core.Models.Eft.Common.Tables.HandbookItem
                    {
                        Id = key,
                        ParentId = new MongoId(parent),
                        Price = hasPrice ? price : null,
                    });
                }
            }
            else
            {
                Warn?.Invoke($"[EventRotator][任务线] 物品 {id} 的手册分类「{parent}」不合法或手册表为空 ⇒ 没加手册条目");
            }
        }
    }

    /// <summary>注入前的硬校验：返回 null = 合格；否则返回不合格原因。</summary>
    private static string? ValidateQuest(Quest quest)
    {
        if (quest.Conditions == null) return "conditions 为空";
        if (quest.Conditions.AvailableForStart == null) return "conditions.AvailableForStart 为空";
        if (quest.Conditions.AvailableForFinish == null) return "conditions.AvailableForFinish 为空";
        if (quest.Conditions.Fail == null) return "conditions.Fail 为空";
        if (quest.Rewards == null) return "rewards 为空";
        foreach (var state in new[] { "Started", "Success", "Fail" })
        {
            if (!quest.Rewards.ContainsKey(state)) return "rewards 缺少「" + state + "」";
        }
        return null;
    }
}
