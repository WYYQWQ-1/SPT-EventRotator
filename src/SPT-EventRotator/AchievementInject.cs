using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IOPath = System.IO.Path;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace EventRotator;

/// <summary>
/// ★★★ 「任务线全部完成 → 解锁成就」的数据注入（v2.7.x）。
///
/// ─────────────────────────────────────────────────────────────────────────────────────
/// 落点（全部有反编译依据）：
///   ① 成就本体 ⇒ <c>TemplateTable.Achievements</c>（`List&lt;Achievement&gt;`，即
///      `SPT_Data/database/templates/achievements.json` 那个**数组**，本机 53 条）
///      依据：`AchievementController.GetAchievements()`（IL 逐字）
///      = `new GetAchievementsResponse { Elements = templateTable.Achievements }`
///      ⇒ 客户端 `/client/achievement/list` 读的就是这张表，**追加即生效**。
///      注意**不是** `templateTable.CustomAchievements`：那个字段只被
///      `PostDbLoadService.MergeCustomAchievements()` 读（同样的按 id 去重、幂等），
///      但那条路要等 SPT 自己的 PostDbLoad 时序，我们控制不了先后；直接追加到 Achievements 最稳。
///   ② 名称/描述 ⇒ 本地化键 `<c>&lt;成就id&gt; name</c>` / `<c>&lt;成就id&gt; description</c>`，
///      正文在 <see cref="LocaleInject"/>（en + ch），与本机 53 条、ISB-SpecialForces 的写法逐字一致。
///   ③ 图标 ⇒ `&lt;mod&gt;/images/achievement/*.png` 启动时**复制**到
///      `&lt;SPT_Runtime&gt;/SPT_Data/images/achievement/`。
///      依据：`ImageRouteImporter.OnLoadAsync` 把 `./SPT_Data/images/` 整棵树注册成
///      `/files/&lt;相对路径去扩展名&gt;`；`ImageRouter.CanHandle` 匹配时去扩展名 + 转小写
///      ⇒ 成就里写 `/files/achievement/ER_Xxx.png` 就能命中。
///      ⚠ **绝不用 `imageRouter.AddRoute`**：实测会把 SPT 自己的图片路由表搞坏（v1.3.12 的坑）。
///      `ImageRouteImporter` 是默认优先级（int.MaxValue，最晚跑）⇒ 本类在 Preload（100000）
///      铺图，扫描时文件已经在了。
///
/// ─────────────────────────────────────────────────────────────────────────────────────
/// 数据格式（`achievements/&lt;任务线&gt;.json`，**每个文件是一个成就数组**，与 ISB 的
/// `db/CustomAchievements/Achievements/*.json` 同形；本 mod 一条任务线一个文件、一个成就）：
/// <code>
/// [ { "id": "&lt;24位hex&gt;", "imageUrl": "/files/achievement/&lt;名&gt;.png", …
///     "conditions": { "availableForFinish": [ { "conditionType": "Quest", "target": "&lt;任务id&gt;",
///                                              "status": [4], "index": 0, … }, … ], "fail": [] } } ]
/// </code>
/// `status: [4]` = 任务成功；一条任务线要「全部完成」⇒ availableForFinish 里放**多条** Quest 条件，
/// `index` 递增（ISB 实测格式，本机 achievements.json 里没有 Quest 条件可抄）。
///
/// ─────────────────────────────────────────────────────────────────────────────────────
/// 降级原则（与本 mod 其它维度一致）：
///   · `achievements/` 不存在 ⇒ 静默返回（老用户升上来没有这个目录是正常状态）；
///   · 表拿不到 / 单条不合法 ⇒ 跳过该条 + warning，其它照常；
///   · 图标铺不进去 ⇒ warning（成就照常注入，只是显示默认图），绝不影响启动。
/// </summary>
internal static class AchievementInject
{
    public static Action<string>? Warn;
    public static Action<string>? Info;

    /// <summary>本次运行真正注入进 `TemplateTable.Achievements` 的成就 id（供自检/日志）。</summary>
    public static readonly List<string> InjectedIds = new();

    /// <summary>任务线 → 该成就的 Quest 条件条数（= 该线任务数）。</summary>
    public static readonly Dictionary<string, int> ByLine = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>条件里引用了、但 `TemplateTable.Quests` 里不存在的任务 id（数据错时用来定位）。</summary>
    public static readonly List<string> MissingQuestTargets = new();

    /// <summary>图标源目录（相对 mod 根）。</summary>
    private const string IconSubDir = "images/achievement";

    /// <summary>由 <see cref="QuestLinePreload.OnLoadAsync"/>（Preload 档）调用；幂等（按成就 id 去重）。</summary>
    public static void Load(string modPath, TemplateTable templateTable, JsonUtil jsonUtil)
    {
        try
        {
            if (string.IsNullOrEmpty(modPath)) return;

            var dir = IOPath.Combine(modPath, "achievements");
            if (!Directory.Exists(dir))
            {
                Info?.Invoke("[成就] 没有 achievements/ 目录，跳过任务线成就注入");
                return;
            }

            var list = templateTable?.Achievements;
            if (list == null)
            {
                Warn?.Invoke("[成就] 拿不到 TemplateTable.Achievements ⇒ 任务线成就一条都不加");
                return;
            }

            var files = Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return;

            // 已有 id（含本体 53 条 + 其它 mod 先注入的）⇒ 幂等：连跑两次数量不变
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in list)
            {
                if (a == null || a.Id.IsEmpty) continue;
                existing.Add((string)a.Id);
            }

            int added = 0, dup = 0, bad = 0;
            foreach (var file in files)
            {
                var line = IOPath.GetFileNameWithoutExtension(file);
                try
                {
                    int n = LoadOne(file, line, list, existing, templateTable, jsonUtil, ref dup, ref bad);
                    if (n > 0) added += n;
                }
                catch (Exception ex)
                {
                    bad++;
                    Warn?.Invoke($"[成就] {IOPath.GetFileName(file)} 处理失败，跳过：{ex.Message}");
                }
            }

            // ★ 图标：把 mod 自带的成就图铺进 SPT_Data（幂等；没带图就静默跳过）
            CopyIcons(modPath);

            if (added > 0)
            {
                var perLine = string.Join(" / ", ByLine.Select(kv => kv.Key + "×1"));
                var conds = string.Join(", ", ByLine.Select(kv => kv.Key + "=" + kv.Value));
                Info?.Invoke($"[成就] ✔ 已注入 {added} 条任务线成就（{perLine}）；"
                           + $"Quest 条件数 {conds}；成就表 {list.Count} 条"
                           + (dup > 0 ? $"（跳过重复 id {dup} 条）" : "")
                           + (bad > 0 ? $"（失败 {bad} 个文件）" : ""));
            }
            else if (dup > 0)
            {
                Info?.Invoke($"[成就] 任务线成就已全部在表里（跳过重复 id {dup} 条），成就表仍是 {list.Count} 条");
            }

            if (MissingQuestTargets.Count > 0)
            {
                Warn?.Invoke("[成就] 有 " + MissingQuestTargets.Count + " 个 Quest 条件引用了表里不存在的任务（这些条件永远不会完成）："
                           + string.Join(", ", MissingQuestTargets));
            }
        }
        catch (Exception ex)
        {
            Warn?.Invoke("[成就] 注入过程异常（已忽略，不影响启动）：" + ex.Message);
        }
    }

    /// <summary>处理一个文件（成就数组）；返回真正追加进表的条数。</summary>
    private static int LoadOne(string file, string line, List<Achievement> list, HashSet<string> existing,
                               TemplateTable templateTable, JsonUtil jsonUtil, ref int dup, ref int bad)
    {
        var name = IOPath.GetFileName(file);
        var raw = File.ReadAllText(file);

        // 允许「数组」与「单个对象」两种写法；正式数据是数组（与 ISB 一致）
        var docs = new List<string>();
        using (var doc = JsonDocument.Parse(raw))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray()) docs.Add(el.GetRawText());
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                docs.Add(doc.RootElement.GetRawText());
            }
            else
            {
                bad++;
                Warn?.Invoke($"[成就] {name}: 根节点既不是数组也不是对象，跳过");
                return 0;
            }
        }

        int added = 0;
        foreach (var json in docs)
        {
            Achievement? ach;
            try { ach = jsonUtil.Deserialize<Achievement>(json); }
            catch (Exception ex)
            {
                bad++;
                Warn?.Invoke($"[成就] {name}: 反序列化失败，跳过：{ex.Message}");
                continue;
            }
            if (ach == null) { bad++; Warn?.Invoke($"[成就] {name}: 反序列化为 null，跳过"); continue; }

            var invalid = Validate(ach, templateTable, out var condCount);
            if (invalid != null)
            {
                bad++;
                Warn?.Invoke($"[成就] {name}: 成就不合格（{invalid}），跳过");
                continue;
            }

            var id = (string)ach.Id;
            if (existing.Contains(id))
            {
                // 幂等：同一个 id 已经在表里（第二次调用 / 与其它 mod 撞车）⇒ 不加
                dup++;
                continue;
            }

            list.Add(ach);
            existing.Add(id);
            InjectedIds.Add(id);
            ByLine[line] = condCount;
            added++;
        }
        return added;
    }

    /// <summary>注入前的硬校验：返回 null = 合格；否则返回不合格原因。</summary>
    private static string? Validate(Achievement ach, TemplateTable templateTable, out int condCount)
    {
        condCount = 0;
        if (ach.Id.IsEmpty) return "id 为空";
        if (!IsMongoId((string)ach.Id)) return "id 不是 24 位十六进制";
        if (string.IsNullOrEmpty(ach.ImageUrl)) return "imageUrl 为空";
        if (!ach.ImageUrl.StartsWith("/files/achievement/", StringComparison.OrdinalIgnoreCase))
            return "imageUrl 不在 /files/achievement/ 下（成就图只能走这条静态路由）";
        if (ach.Conditions == null) return "conditions 为空";
        if (ach.Conditions.AvailableForFinish == null) return "conditions.availableForFinish 为空";
        if (ach.Conditions.Fail == null) return "conditions.fail 为空（客户端读它时没有 null 检查）";

        var conds = ach.Conditions.AvailableForFinish;
        if (conds.Count == 0) return "conditions.availableForFinish 一条都没有";

        for (int i = 0; i < conds.Count; i++)
        {
            var c = conds[i];
            if (c == null) return $"第 {i} 条条件为 null";
            if (!string.Equals(c.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase))
                return $"第 {i} 条条件的 conditionType 是「{c.ConditionType}」，不是 Quest（本 mod 只注入任务完成类成就）";
            // QuestCondition.Target 是 SPT 的 ListOrT<string>（单值走 .Item），不是裸 string
            var target = c.Target?.Item;
            if (!IsMongoId(target))
                return $"第 {i} 条条件的 target「{target}」不是 24 位十六进制任务 id";
            if (c.Status == null || c.Status.Count == 0)
                return $"第 {i} 条条件没有 status（应为 [4] = 任务成功）";

            // 任务是否真的在表里（QuestLineDimension 在同一个 Preload 里先跑，自定义任务此时已注入）
            var qid = new MongoId(target);
            if (templateTable?.Quests == null || !templateTable.Quests.ContainsKey(qid))
            {
                if (!MissingQuestTargets.Contains(target)) MissingQuestTargets.Add(target);
            }
            if (c.Id.IsEmpty || !IsMongoId((string)c.Id))
                return $"第 {i} 条条件的 id 不是 24 位十六进制";
        }

        condCount = conds.Count;
        return null;
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

    /// <summary>
    /// 把 `&lt;mod&gt;/images/achievement/*.png` 铺进 `&lt;SPT_Data&gt;/images/achievement/`。
    /// 同 <see cref="TraderIconAssetCopier"/> 的手法：内容相同 ⇒ 跳过；不同 ⇒ 先备份 `.bak_eventrotator` 再覆盖。
    /// 任何一步失败只 warning。
    /// </summary>
    private static void CopyIcons(string modPath)
    {
        try
        {
            var srcDir = IOPath.Combine(modPath, "images", "achievement");
            if (!Directory.Exists(srcDir)) return;   // 没带图 ⇒ 静默跳过

            var sptData = TraderIconAssetCopier.LocateSptData(modPath);
            if (sptData == null)
            {
                Warn?.Invoke("[成就] 找不到 SPT_Data 目录 ⇒ 成就图未铺（游戏里会显示默认图，不影响解锁）");
                return;
            }
            var dstDir = IOPath.Combine(sptData, "images", "achievement");
            Directory.CreateDirectory(dstDir);

            int copied = 0, same = 0, failed = 0;
            foreach (var src in Directory.GetFiles(srcDir, "*.png"))
            {
                try
                {
                    var fileName = IOPath.GetFileName(src);
                    var dst = IOPath.Combine(dstDir, fileName);
                    if (File.Exists(dst))
                    {
                        if (TraderIconAssetCopier.SameBytes(src, dst)) { same++; continue; }
                        try { File.Copy(dst, dst + ".bak_eventrotator", true); } catch { }
                    }
                    File.Copy(src, dst, true);
                    copied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Warn?.Invoke("[成就] 铺图失败 " + IOPath.GetFileName(src) + "：" + ex.Message);
                }
            }
            if (copied > 0 || failed > 0)
                Info?.Invoke($"[成就] 成就图：已铺 {copied} 张、相同跳过 {same} 张、失败 {failed} 张 → {dstDir}");
        }
        catch (Exception ex)
        {
            Warn?.Invoke("[成就] 铺图异常（已忽略）：" + ex.Message);
        }
    }
}
