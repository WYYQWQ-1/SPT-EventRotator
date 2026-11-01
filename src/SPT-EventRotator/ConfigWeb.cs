using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;

namespace EventRotator;

/// <summary>
/// ★ v1.3.8【网页面板 · 数据层】给 Blazor 配置页用的读写服务。
///
/// <para><b>为什么不直接让页面写文件</b>：写 config.json 是危险操作（写坏 = 服务端起不来）。
/// 这里把三件事集中做掉：① 只接受「白名单字段」（页面传什么都不能越界写）；
/// ② 取值域校验（间隔天数、概率、枚举字符串）；③ 写前备份旧文件、写时先写临时文件再替换，
/// 中途失败不会留下半个 JSON。</para>
///
/// <para><b>生效说明</b>：配置是服务端启动时读的 ⇒ 保存后需要重启服务端才生效；页面会提示。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ConfigWebService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// ★ v1.3.9【启动竞态修复】ModPath 由 EventEngine.Load 赋值；服务端刚启动、mod 还没加载完时
    /// 面板若被访问（例如浏览器保留着旧标签页），ModPath 为空 ⇒ 读不到 events.json ⇒ 所有 id 都"不认识"。
    /// 这里退回程序集所在目录（就是 mod 目录），保证任何时刻都能读到文件。
    /// </summary>
    public static string ModDir
    {
        get
        {
            if (!string.IsNullOrEmpty(EventEngine.ModPath)) return EventEngine.ModPath;
            try { return Path.GetDirectoryName(typeof(ConfigWebService).Assembly.Location); } catch { return null; }
        }
    }

    public string ConfigPath => ModDir == null ? null : Path.Combine(ModDir, "config.json");

    /// <summary>读当前配置；读不到/坏掉时返回 null（页面显示错误，不崩）。</summary>
    public JsonObject Load() => LoadFrom(ConfigPath);

    public static JsonObject LoadFrom(string path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject;
        }
        catch { return null; }
    }

    // ── 白名单：路径 → 校验器 ─────────────────────────────────────────
    // 只允许改这些；新增字段必须在这里登记，否则页面传了也会被拒。
    private static readonly HashSet<string> BoolKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // ★★★ v2.7.0【硬开关 · 不启用任何活动】`config.json` 顶层 `disableAllEvents`。
        //   键名必须与 `EventEngine.LoadConfig` 里读的**逐字一致**（`TryGetProperty("disableAllEvents")`）。
        //   语义：开启后无论如何都不套用任何活动（含 force.txt / 轮换 / 季节自动接管 / 日期窗 / 奖励轮换），
        //         并先做一次完整还原（DB / 战利品 / 天气 / 感染 / 外部联动）⇒ 裸 SPT。
        //         英文：disable all events - no event is ever applied and every previous change is restored.
        "disableAllEvents",
        "weaponFallback", "sainPresetInject", "mapFillersEnable", "allowOnOverhaulMaps",
        "seasonBagNoRepeat", "seasonAutoPickOff", "rotation.enabled", "rotation.avoidConflicts",
        "rotation.onlyWhenNoSeasonal", "seasonRotation.autoOpenSeasonalEvent",
        "rewardRotation.enabled", "rewardRotation.avoidConflicts",
        // ★ 2026-10-29【第 25 项 · 接管原版季节窗】顶层布尔键。
        //   键名必须与 `Plugin.ApplyVanillaSeasonTakeover` 里读的**逐字一致**：
        //   `Plugin.cs` → `doc.RootElement.TryGetProperty("vanillaSeasonTakeover", out var v)`。
        //   ⚠ 之前**三处都漏**：patch 里没有这个键 ⇒ 保存"成功"但本项被**静默丢弃**（刷新弹回）；
        //     而若只补 razor、不补这份白名单，`Save()` 会因"不是可写字段"**整次保存被拒**。两处必须同时补。
        "vanillaSeasonTakeover",
        // ★ 2026-10-29【第 26 项 · 独立天气开关】`config.json → weather.*`（嵌套对象，SetByPath 会自动建 weather 节点）
        //   键名必须与 `WeatherSuppressPatch` 的 `KeyRainSnow` / `KeyFog` / `KeyLogOnce` **逐字一致**：
        //   它读的是 `root["weather"]["suppressRainSnow" | "suppressFog" | "logOnce"]`。
        //   ⚠ 两个子开关**互相独立**（只压雨雪 / 只压雾 / 都压都行）；都不开 ⇒ 天气字段一个都不改。
        "weather.suppressRainSnow", "weather.suppressFog", "weather.logOnce",
        // ★ 2026-10-29【周/周末抽签】
        "weeklyDraw.onlyNoRestart",
        // ★ 2026-10-29【第 21 项 · SAIN brain/layer】
        "sainBrains.enabled",
        // ★ 2026-10-29【第 3 项 · Fence 传闻】`config.json → fenceRumor.enabled`
        //   键名与 `FenceRumor.ReadOpt()` 读的逐字一致；SetByPath 会自动建 fenceRumor 节点。
        "fenceRumor.enabled",
    };

    private static readonly Dictionary<string, (double min, double max, bool integer)> NumKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rotation.intervalDays"] = (1, 365, true),
        ["rotation.count"] = (1, 10, true),
        ["rotation.slotRebaseHour"] = (0, 23, true),
        ["rotation.durationDays"] = (0, 365, true),
        ["rotation.rewardCountMin"] = (0, 10, true),
        ["rotation.rewardCountMax"] = (0, 10, true),
        ["rotation.smallCountMax"] = (0, 10, true),
        ["rotation.seed"] = (int.MinValue, int.MaxValue, true),
        ["seasonRotation.intervalDays"] = (1, 365, true),
        ["seasonRotation.offset"] = (-48, 48, true),
        ["randomBossMapCount"] = (0, 10, true),
        ["randomBossMapChance"] = (0, 100, true),
        ["rewardRotation.countMin"] = (0, 10, true),
        ["rewardRotation.countMax"] = (0, 10, true),
        // ★ 2026-10-29【周/周末抽签】每次抽 1~3 个；seed 与 rotation.seed 同域
        ["weeklyDraw.count"] = (1, 3, true),
        ["weeklyDraw.seed"] = (int.MinValue, int.MaxValue, true),
        // ★ 2026-10-29【第 3 项 · Fence 传闻】`config.json → fenceRumor.*`
        ["fenceRumor.maxPerDay"] = (0, 100, true),
        ["fenceRumor.minIntervalMinutes"] = (0, 525600, true),
    };

    private static readonly Dictionary<string, string[]> EnumKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logLevel"] = new[] { "off", "basic", "verbose" },
        ["rotation.mode"] = new[] { "random", "sequential" },
        ["seasonRotation.mode"] = new[] { "rotate", "followEvent", "fixed", "off" },
        ["seasonRotation.eventPhase"] = new[] { "off", "start", "middle", "end" },
        ["seasonRotation.season"] = new[] { "SPRING", "SUMMER", "AUTUMN", "WINTER" },
        ["eventSeason.mode"] = new[] { "off", "fixed" },
        ["seasonAutoPick"] = new[] { "newest", "oldest", "random" },
        // ★ 2026-10-29【第 22 项 · 日期窗生效范围】seasonOffOnly = 只有 seasonRotation.mode=off 才看日期窗（默认）；always = 旧行为
        ["dateWindowRule"] = new[] { "seasonOffOnly", "always" },
        // ★ 2026-10-29【周/周末抽签】off | weekly | weekend
        ["weeklyDraw.mode"] = new[] { "off", "weekly", "weekend" },
    };

    /// <summary>
    /// ★ 2026-10-29【第 21 项 · SAIN brain/layer】字符串键（数组在页面上没法编辑 ⇒ 登记成
    /// **逗号分隔字符串**，SainPresetInject 侧会按 ',' ';' 换行 等拆分）：
    /// <list type="bullet">
    /// <item><c>sainBrains.baseBrainBoss</c> / <c>sainBrains.baseBrainFollower</c> —— 单个 brain 名（如 ExUsec）</item>
    /// <item><c>sainBrains.brainsToApply</c> —— 例 <c>"ExUsec,PMC"</c></item>
    /// <item><c>sainBrains.layersToRemove</c> —— 例 <c>"Request,KnightFight,PmcBear,PmcUsec,ExURequest,StationaryWS"</c></item>
    /// </list>
    /// 值为空字符串 = 该键不生效（SainPresetInject 会回落到内置默认，避免把 SAIN 必需 brain 清空）。
    /// </summary>
    private static readonly HashSet<string> StringKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sainBrains.baseBrainBoss", "sainBrains.baseBrainFollower",
        "sainBrains.brainsToApply", "sainBrains.layersToRemove",
    };

    /// <summary>
    /// ★ v1.3.8【热应用边界】这些配置项**改完必须重启服务端**，热重载带不进来：
    /// <list type="bullet">
    /// <item><c>sainPresetInject</c> —— 它在 Preload 阶段把预设写进 SAIN 的 json 文件，
    ///   只在启动时跑一次；改开关不会撤掉已写入的预设，也不会补写。</item>
    /// </list>
    /// 加新项时请一并补到这里的注释里，页面上的「⟳ 需重启」标记读的是同一份名单。
    /// </summary>
    public static readonly string[] RestartRequiredKeys = {
        "sainPresetInject",
        // ★ 第 25 项：`vanillaSeasonTakeover` 只在 `Plugin.OnLoadAsync → ApplyVanillaSeasonTakeover`
        //   读一次（既改内存里的 SeasonalEventConfig，也改磁盘 seasonalevents.json）⇒ 热重载带不进来。
        "vanillaSeasonTakeover",
        // ★ 第 21 项：sainBrains 在 Preload 阶段（写 SAIN 预设 / 注册角色）就被读走 ⇒ 改完必须重启
        "sainBrains.enabled", "sainBrains.baseBrainBoss", "sainBrains.baseBrainFollower",
        "sainBrains.brainsToApply", "sainBrains.layersToRemove",
    };

    /// <summary>把这批键里"必须重启才生效"的挑出来（给提示用）。</summary>
    public static List<string> RestartOnly(IEnumerable<string> keys)
        => (keys ?? Enumerable.Empty<string>())
           .Where(k => RestartRequiredKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
           .ToList();

    /// <summary>
    /// ★ v1.3.9【防假成功 · 结构校验】保存前对**合并后的整份配置**做一次类型/取值域检查。
    /// 目的：`EventEngine.LoadConfig` 内部是 try/catch 逐段解析的（坏一段只 Warn 然后继续），
    /// 所以文件结构坏了也会"保存成功"而实际上没生效。这里把坏结构挡在写盘之前。
    /// 只检查**已知键**；未知键一律放行（向前兼容）。
    /// </summary>
    public static List<string> ValidateStructure(JsonObject cfg)
    {
        var bad = new List<string>();
        if (cfg == null) return new List<string> { "配置为空" };

        void RequireObj(string key, bool required = true)
        {
            if (cfg[key] is JsonObject) return;
            if (cfg[key] == null && !required) return;
            bad.Add($"{key} 必须是对象");
        }
        bool? BoolOf(JsonObject o, string k, string path)
        {
            if (o?[k] == null) return null;
            var t = o[k]!.ToJsonString().Trim();
            if (t == "true") return true;
            if (t == "false") return false;
            bad.Add($"{path} 必须是 true/false");
            return null;
        }
        double? NumOf(JsonObject o, string k, string path)
        {
            if (o?[k] == null) return null;
            if (double.TryParse(o[k]!.ToJsonString().Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            bad.Add($"{path} 必须是数字");
            return null;
        }
        void StrIn(JsonObject o, string k, string path, params string[] allowed)
        {
            if (o?[k] == null) return;
            var v = o[k]!.ToString();
            if (!allowed.Contains(v, StringComparer.OrdinalIgnoreCase))
                bad.Add($"{path} 只能是 {string.Join("/", allowed)}（当前 {v}）");
        }
        void Range(double? v, double lo, double hi, string path)
        {
            if (v == null) return;
            if (v < lo || v > hi) bad.Add($"{path} 超出范围 {lo}~{hi}");
        }

        // 顶层
        StrIn(cfg, "logLevel", "logLevel", "off", "basic", "verbose");
        BoolOf(cfg, "weaponFallback", "weaponFallback");
        BoolOf(cfg, "sainPresetInject", "sainPresetInject");
        BoolOf(cfg, "mapFillersEnable", "mapFillersEnable");
        BoolOf(cfg, "allowOnOverhaulMaps", "allowOnOverhaulMaps");
        Range(NumOf(cfg, "randomBossMapCount", "randomBossMapCount"), 1, 11, "randomBossMapCount");
        Range(NumOf(cfg, "randomBossMapChance", "randomBossMapChance"), 1, 100, "randomBossMapChance");
        StrIn(cfg, "seasonAutoPick", "seasonAutoPick", "newest", "oldest", "random", "all");
        // ★ 2026-10-29【第 22 项 · 日期窗生效范围】顶层新键：seasonOffOnly（默认）| always
        StrIn(cfg, "dateWindowRule", "dateWindowRule", "seasonOffOnly", "always");
        if (cfg["mapFillers"] is JsonObject mf)
            foreach (var kv in mf)
                if (kv.Value is not JsonArray) bad.Add($"mapFillers.{kv.Key} 必须是数组");

        // rotation
        RequireObj("rotation");
        var rot = cfg["rotation"] as JsonObject;
        BoolOf(rot, "enabled", "rotation.enabled");
        StrIn(rot, "mode", "rotation.mode", "random", "sequential");
        Range(NumOf(rot, "intervalDays", "rotation.intervalDays"), 1, 365, "rotation.intervalDays");
        Range(NumOf(rot, "count", "rotation.count"), 1, 10, "rotation.count");
        Range(NumOf(rot, "slotRebaseHour", "rotation.slotRebaseHour"), 0, 23, "rotation.slotRebaseHour");
        Range(NumOf(rot, "durationDays", "rotation.durationDays"), 0, 3650, "rotation.durationDays");
        // seed 是任意整数（int/long 都合法），只校验"是数字"
        NumOf(rot, "seed", "rotation.seed");
        BoolOf(rot, "avoidConflicts", "rotation.avoidConflicts");
        BoolOf(rot, "onlyWhenNoSeasonal", "rotation.onlyWhenNoSeasonal");
        if (rot?["exclude"] is not null && rot["exclude"] is not JsonArray) bad.Add("rotation.exclude 必须是数组");
        if (rot?["pool"] is not null && rot["pool"] is not JsonArray) bad.Add("rotation.pool 必须是数组");

        // seasonRotation
        RequireObj("seasonRotation");
        var sr = cfg["seasonRotation"] as JsonObject;
        StrIn(sr, "mode", "seasonRotation.mode", "rotate", "followEvent", "fixed", "off");
        Range(NumOf(sr, "intervalDays", "seasonRotation.intervalDays"), 1, 365, "seasonRotation.intervalDays");
        Range(NumOf(sr, "offset", "seasonRotation.offset"), -48, 48, "seasonRotation.offset");
        StrIn(sr, "season", "seasonRotation.season", "SPRING", "SUMMER", "AUTUMN", "WINTER");
        StrIn(sr, "eventPhase", "seasonRotation.eventPhase", "off", "start", "middle", "end");
        BoolOf(sr, "autoOpenSeasonalEvent", "seasonRotation.autoOpenSeasonalEvent");
        if (sr?["order"] is not null && sr["order"] is not JsonArray) bad.Add("seasonRotation.order 必须是数组");

        // rewardRotation
        RequireObj("rewardRotation", required: false);
        var rr = cfg["rewardRotation"] as JsonObject;
        BoolOf(rr, "enabled", "rewardRotation.enabled");
        BoolOf(rr, "avoidConflicts", "rewardRotation.avoidConflicts");
        var rmin = NumOf(rr, "countMin", "rewardRotation.countMin");
        var rmax = NumOf(rr, "countMax", "rewardRotation.countMax");
        Range(rmin, 0, 10, "rewardRotation.countMin");
        Range(rmax, 0, 10, "rewardRotation.countMax");
        if (rmin != null && rmax != null && rmin > rmax) bad.Add("rewardRotation.countMin 不能大于 countMax");

        // eventSeason
        RequireObj("eventSeason", required: false);
        var es = cfg["eventSeason"] as JsonObject;
        StrIn(es, "mode", "eventSeason.mode", "off", "fixed");

        return bad;
    }

    /// <summary>保存补丁：只应用白名单字段；全部校验通过才落盘。返回 (ok, error)。</summary>
    public (bool ok, string error) Save(JsonObject patch)
    {
        var path = ConfigPath;
        if (path == null) return (false, "找不到 mod 目录（服务端可能还没加载完）");
        if (patch == null) return (false, "空请求");

        var cfg = LoadFrom(path);
        if (cfg == null) return (false, "config.json 读不出来（可能已损坏），拒绝覆盖");

        // 1) 先全量校验，任何一项不合法就整体拒绝（不做半截写入）
        var applied = new List<string>();
        var errors = new List<string>();
        foreach (var kv in patch)
        {
            var key = kv.Key;
            var val = kv.Value;
            if (BoolKeys.Contains(key))
            {
                if (val is JsonValue jv && (jv.TryGetValue<bool>(out _))) { applied.Add(key); continue; }
                errors.Add($"{key}: 需要 true/false");
            }
            else if (NumKeys.TryGetValue(key, out var range))
            {
                if (TryNum(val, out var d))
                {
                    if (d < range.min || d > range.max) { errors.Add($"{key}: 超出范围 {range.min}~{range.max}"); }
                    else if (range.integer && Math.Abs(d - Math.Round(d)) > 1e-9) { errors.Add($"{key}: 需要整数"); }
                    else applied.Add(key);
                }
                else errors.Add($"{key}: 需要数字");
            }
            else if (EnumKeys.TryGetValue(key, out var allowed))
            {
                var s = val is JsonValue jv ? jv.ToString() : null;
                if (s != null && allowed.Contains(s, StringComparer.OrdinalIgnoreCase)) applied.Add(key);
                else errors.Add($"{key}: 只能是 {string.Join("/", allowed)}");
            }
            else if (StringKeys.Contains(key))
            {
                // ★ 第 21 项：SAIN brain/layer 的"逗号分隔字符串"键（数组在页面上不好编）。
                //   ⚠ 必须是 JSON **字符串**；数字/数组一律拒（否则 "123" 会被当成 brain 名）。
                if (val is JsonValue jv && jv.TryGetValue<string>(out var s) && s != null) applied.Add(key);
                else errors.Add($"{key}: 需要字符串（如 \"ExUsec,PMC\"）");
            }
            else if (key.StartsWith("autoLootBoost.", StringComparison.OrdinalIgnoreCase))
            {
                if (TryNum(val, out var d) && d >= 0.5 && d <= 10) applied.Add(key);
                else errors.Add($"{key}: 倍率需在 0.5~10 之间");
            }
            else if (string.Equals(key, "rotation.exclude", StringComparison.OrdinalIgnoreCase))
            {
                // ★ v1.3.8 排除列表：数组，元素必须是 events.json 里真实存在的活动 id
                if (val is JsonArray arr)
                {
                    var evRows = ListEvents();
        if (evRows.Count == 0)
            return (false, "服务端还在加载活动表（events.json 尚未读取），请几秒后重试");
        var known = new HashSet<string>(evRows.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
                    var bad = arr.Select(x => x?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s) && !known.Contains(s)).ToList();
                    if (bad.Count > 0) errors.Add($"rotation.exclude: 不认识的活动 {string.Join(",", bad)}");
                    else applied.Add(key);
                }
                else errors.Add("rotation.exclude: 需要字符串数组");
            }
            else errors.Add($"{key}: 不是可写字段");
        }
        if (errors.Count > 0) return (false, string.Join("；", errors));

        // 2) 合并（不合法值已排除；这里只改白名单字段）
        foreach (var key in applied)
        {
            SetByPath(cfg, key, patch[key]);
        }

        // 2b) ★ v1.3.9【结构校验】合并后整份配置先过一遍类型/范围检查，坏结构不许写盘
        var structBad = ValidateStructure(cfg);
        if (structBad.Count > 0)
            return (false, "配置结构校验失败（未写盘）：" + string.Join("；", structBad.Take(3))
                           + (structBad.Count > 3 ? $" 等 {structBad.Count} 项" : ""));

        // 3) 备份 + 原子写
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var backup = $"{path}.bak-web-{DateTime.Now:yyyyMMdd-HHmmss}";
            if (File.Exists(path)) File.Copy(path, backup, overwrite: true);

            var tmp = $"{path}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tmp, cfg.ToJsonString(WriteOpts));
            // 3a) 写出来的必须是合法 JSON，否则不替换（防止把好文件换成坏文件）
            if (LoadFrom(tmp) == null) { TryDelete(tmp); return (false, "序列化结果不是合法 JSON，已放弃写入"); }
            File.Move(tmp, path, overwrite: true);

            // 3b) ★ v1.3.9【防假成功】写完后回读，逐项核对"我写进去的值"是否真的落进文件。
            //     任何一项对不上就报错（而不是提示"已保存"却悄悄没生效）。
            try
            {
                var back = LoadFrom(path);
                var mismatch = new List<string>();
                foreach (var key in applied)
                {
                    var want = patch[key]?.ToJsonString() ?? "null";
                    var got = GetByPath(back, key)?.ToJsonString() ?? "null";
                    // 数字/字符串在 JSON 里可能有 int vs long 表示差异，统一比较数值文本
                    if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase)
                        && !(double.TryParse(want, out var dw) && double.TryParse(got, out var dg) && Math.Abs(dw - dg) < 1e-9))
                        mismatch.Add($"{key}（写入 {want} / 回读 {got}）");
                }
                if (mismatch.Count > 0)
                    return (false, "写盘后回读不一致，已放弃热应用：" + string.Join("；", mismatch.Take(3))
                                   + (mismatch.Count > 3 ? $" 等 {mismatch.Count} 项" : ""));
            }
            catch { }

            // 4) 只保留最近 20 份网页备份
            try
            {
                var olds = Directory.GetFiles(Path.GetDirectoryName(path), "config.json.bak-web-*")
                    .OrderByDescending(f => f).Skip(20).ToList();
                foreach (var f in olds) TryDelete(f);
            }
            catch { }
            // ★ v1.3.8【P0 热生效】写盘成功 → 立刻重读配置进 static，并重算当前活动
            string appliedIds = "(未知)";
            try { appliedIds = EventEngine.ReloadSettingsFromDisk(); } catch { }
            var needRestart = RestartOnly(applied);
            var tail = needRestart.Count > 0
                ? $"；⚠ 这些项要重启才生效：{string.Join(",", needRestart)}"
                : "";
            return (true, $"已保存 {applied.Count} 项 · 已热生效（当前活动：{appliedIds}）{tail}");
        }
        catch (Exception ex) { return (false, "写盘失败：" + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>
    /// ★ v1.3.8【P1】网页「立即重新应用」：**不写盘**，只按**当前内存里的配置**重算活动并刷新下发状态。
    /// 返回给页面显示的完整说明（含"这些项本来就不能热应用"的提醒）。
    /// </summary>
    public string Reapply()
    {
        var ids = EventEngine.ReapplyNow();
        var always = string.Join(",", RestartRequiredKeys);
        return $"{ids}｜（本次只重算活动选择；{always} 这类启动期项不在热应用范围内，改了仍需重启）";
    }

    /// <summary>★ v1.3.8【P0】网页「重新读配置」：从磁盘重读 config.json（外部手改后用）。</summary>
    public string ReloadFromDisk() => EventEngine.ReloadSettingsFromDisk();

    public List<string> ListBackups()
    {
        var path = ConfigPath;
        if (path == null) return new List<string>();
        try
        {
            return Directory.GetFiles(Path.GetDirectoryName(path), "config.json.bak-web-*")
                .OrderByDescending(f => f).Select(Path.GetFileName).Take(20).ToList();
        }
        catch { return new List<string>(); }
    }

    public (bool ok, string error) Restore(string backupName)
    {
        var path = ConfigPath;
        if (path == null) return (false, "找不到 mod 目录");
        if (string.IsNullOrWhiteSpace(backupName) || backupName.Contains("..") || backupName.Contains('/') || backupName.Contains('\\'))
            return (false, "备份名不合法");
        var src = Path.Combine(Path.GetDirectoryName(path), backupName);
        if (!File.Exists(src)) return (false, "备份不存在");
        if (LoadFrom(src) == null) return (false, "备份文件不是合法 JSON");
        try
        {
            File.Copy(src, path, overwrite: true);
            // ★ v1.3.8【热生效】回滚后同样重读配置 + 重算活动，否则页面显示回滚成功、运行中的还是旧配置
            string nowIds = "(未知)";
            try { nowIds = EventEngine.ReloadSettingsFromDisk(); } catch { }
            return (true, "已回滚到 " + backupName + " · 已热生效（当前活动：" + nowIds + "）");
        }
        catch (Exception ex) { return (false, "回滚失败：" + ex.Message); }
    }

    // ── 工具 ────────────────────────────────────────────────
    /// <summary>JsonNode 里的数字统一转 double（页面传 int / 配置里是 30.0，两种都要认）。</summary>
    private static bool TryNum(JsonNode node, out double d)
    {
        d = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    /// <summary>按点号路径取值（回读校验用）。</summary>
    private static JsonNode GetByPath(JsonObject root, string dotted)
    {
        JsonNode cur = root;
        foreach (var part in dotted.Split('.'))
        {
            if (cur is not JsonObject o) return null;
            cur = o[part];
            if (cur == null) return null;
        }
        return cur;
    }

    private static void SetByPath(JsonObject root, string dotted, JsonNode value)
    {
        var parts = dotted.Split('.');
        JsonObject cur = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                cur[parts[i]] = next;
            }
            cur = next;
        }
        cur[parts[^1]] = value?.DeepClone();
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    // ── 手动强制（force.txt）────────────────────────────────
    private string ForcePath => ModDir == null ? null : Path.Combine(ModDir, "force.txt");

    /// <summary>当前 force.txt 内容（每行一个 id；空 = 不强制）。</summary>
    public List<string> ReadForce()
    {
        try
        {
            var p = ForcePath;
            if (p == null || !File.Exists(p)) return new List<string>();
            // ★ v1.3.9【回归修复】以前按行读；写侧改成"单行逗号"之后，
            //   "a,b" 会被当成一个 id ⇒ 再点第二个时旧的被当"不存在"丢掉 ⇒ 永远只能开一个。
            //   现在按行 + 逗号/分号/竖线/空白一起切。
            var raw = File.ReadAllText(p);
            return raw.Split(new[] { ',', ';', '|', ' ', (char)9, (char)13, (char)10, (char)0xFEFF },
                             StringSplitOptions.RemoveEmptyEntries)
                      .Select(x => x.Trim())
                      .Where(x => x.Length > 0 && !x.StartsWith("#"))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>写 force.txt。ids 为空 = 清空（回到自动轮换）。只接受 events.json 里存在的 id 与 "none"。</summary>
    public (bool ok, string error) SetForce(List<string> ids)
    {
        var p = ForcePath;
        if (p == null) return (false, "找不到 mod 目录");
        var known = new HashSet<string>(ListEvents().Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        // ★ v1.3.9【不再整单拒绝】旧版/外部工具可能留下已知集合之外的 id（改名、删除、大小写、空白污染）。
        //   以前只要有一个不认识就整单失败 ⇒ 用户"清空后点开启"都会被挡住。
        //   现在：只写认识的那些，并把被忽略的 id 明确回报给页面。
        var all = (ids ?? new List<string>()).Select(x => (x ?? "").Trim())
                                            .Where(x => x.Length > 0)
                                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bad = all.Where(x => !known.Contains(x) && !string.Equals(x, "none", StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = all.Where(x => known.Contains(x) || string.Equals(x, "none", StringComparison.OrdinalIgnoreCase)).ToList();
        try
        {
            if (File.Exists(p)) File.Copy(p, p + ".bak-web-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            // ★ v1.3.9 单行逗号写法：跟旧版 force.txt 格式一致，任何解析路径都认
            File.WriteAllText(p, string.Join(",", keep), new System.Text.UTF8Encoding(false));
            // ★ v1.3.8【P1 热应用】写完 force.txt 立刻重算（Reset 掉"文件没变就跳过"的判据）
            string nowIds = "(未知)";
            try { nowIds = EventEngine.ReloadSettingsFromDisk(); } catch { }
            var ignored = bad.Count > 0 ? $"（已忽略不认识的 id：{string.Join(",", bad)}）" : "";
            // ★★★ v2.7.0【硬开关】`disableAllEvents=true` 时 force.txt 照样**写盘保存**（尊重用户的勾选，
            //   以后关掉开关还能直接生效），但必须**明确告诉用户"现在不采用"**，否则又是"面板说开了、
            //   服务端没开"的假成功。
            if (EventEngine.DisableAllEvents)
                return (true, $"已写入 force.txt（{string.Join(",", keep)}），但 config.json 的 disableAllEvents=true "
                            + "⇒ 当前**不采用任何活动**（关掉那个开关后这份强制才会生效）"
                            + $"· 当前活动：{nowIds}{ignored}");
            return (true, keep.Count == 0
                ? $"已清空强制（回到自动轮换）· 当前活动：{nowIds}{ignored}"
                : $"已强制开启 {string.Join(",", keep)} · 当前活动：{nowIds}{ignored}");
        }
        catch (Exception ex) { return (false, "写 force.txt 失败: " + ex.Message); }
    }

    // ── 只读状态（页面顶部胶囊）────────────────────────────────
    public string StatusText => EventRotatorStatusRouter.Snapshot ?? "";
    public string Version => EventRotatorMetadata.VersionText;
    public bool LogEnabled => EventEngine.DebugLogging;

    /// <summary>
    /// ★ v2.7.0【硬开关】服务端当前是否处于「不启用任何活动」状态（= `config.json → disableAllEvents`）。
    /// 只读；写入口是通用 <see cref="Save"/>（键已登记进 <see cref="BoolKeys"/>）。
    /// </summary>
    public bool DisableAllEvents => EventEngine.DisableAllEvents;

    /// <summary>英文说明映射（desc-en.json，随包发布；缺失时英文模式回退中文）。</summary>
    private static Dictionary<string, string> _enMap;
    private static Dictionary<string, string> EnMap
    {
        get
        {
            if (_enMap != null) return _enMap;
            _enMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var p = ModDir == null ? null : Path.Combine(ModDir, "desc-en.json");
                if (p != null && File.Exists(p) &&
                    JsonNode.Parse(File.ReadAllText(p)) is JsonObject o)
                {
                    foreach (var kv in o)
                        if (kv.Value != null) _enMap[kv.Key] = kv.Value.ToString();
                }
            }
            catch { }
            return _enMap;
        }
    }

    /// <summary>活动的页面展示模型（网页面板用）。</summary>
    public class EventRow
    {
        public string Id { get; set; } = "";
        public string Zh { get; set; } = "";
        public string En { get; set; } = "";
        public string Category { get; set; } = "";
        public string Window { get; set; } = "";
        public string Desc { get; set; } = "";
        public string DescEn { get; set; } = "";
        public bool Always { get; set; }
        public string Label => string.IsNullOrEmpty(Zh) ? Id : Zh;

        // ═══ ★ 2026-10-29【第一批补充 · 接口契约】给网页面板补充的 UI 元数据 ═══
        // 契约：audit/第一批补充-接口契约.md（A 线产出，B 线按此渲染）
        /// <summary>ready | simulated | experimental（缺省/非法一律按 simulated）。</summary>
        public string UiTag { get; set; } = "simulated";
        /// <summary>wiki 链接；空字符串 = 没有（契约允许缺省）。</summary>
        public string Wiki { get; set; } = "";
        /// <summary>日期窗开始日（0 = 无日期窗）。</summary>
        public int StartDay { get; set; }
        public int StartMonth { get; set; }
        public int EndDay { get; set; }
        public int EndMonth { get; set; }
        /// <summary>
        /// 影响面，逗号分隔；取值 = <see cref="TouchDims"/> 的顺序子集，**可带 ":{mapId}" 后缀**
        /// （契约 v2：按地图分键的字段拆到地图粒度，如 `bosses:laboratory`）。空 = 无。
        /// </summary>
        public string Touches { get; set; } = "";
        /// <summary>与之冲突的其它活动 id（契约 v2 的 token 规则，且至少一个不是 ready）。</summary>
        public List<string> ConflictsWith { get; set; } = new();

        // ═══ ★ 2026-10-29【第 18 + 20 项 · 数据层】任务可用性 + 兼容性提示 ═══
        // 字段名是**接口契约**（B 线按这个渲染），一个字都不能改。
        /// <summary>本活动的任务（按链顺序；enableQuests ∪ 我们的 questlines）。空 = 本活动没有任务。</summary>
        public List<QuestInfo> Quests { get; set; } = new();
        /// <summary>一句话摘要，例："5 条 · 本机缺 2 条"；没有任务时为空字符串。</summary>
        public string QuestSummary { get; set; } = "";
        /// <summary>与本活动地图重叠的其它 mod 行为提示（只读别人的配置）。</summary>
        public List<CompatNote> Compat { get; set; } = new();

        /// <summary>第 18 项：一条任务在本机是否可用（字段名 = 契约；B 线按 ConfigWebService.EventRow.QuestInfo 引用）。</summary>
        public sealed class QuestInfo
        {
            public string Id { get; set; } = "";
            public string NameZh { get; set; } = "";
            public string NameEn { get; set; } = "";
            public string Trader { get; set; } = "";
            public int Level { get; set; }
            /// <summary>链上前一个任务 id（没有就空）。</summary>
            public string PreviousId { get; set; } = "";
            /// <summary>本机存在？</summary>
            public bool Present { get; set; }
            /// <summary>"questline" | "native" | "missing"</summary>
            public string Source { get; set; } = "missing";
            /// <summary>是否由我们 questlines/*.json 提供。</summary>
            public bool FromOurQuestline { get; set; }
        }

        /// <summary>第 20 项：一条兼容性提示（字段名 = 契约；B 线按 ConfigWebService.EventRow.CompatNote 引用）。</summary>
        public sealed class CompatNote
        {
            /// <summary>"ABPS" | "ISB" | "Blackout" | "ORBIT"</summary>
            public string Source { get; set; } = "";
            /// <summary>地图 id；全局填 "*"。</summary>
            public string Map { get; set; } = "*";
            /// <summary>中文一句话。</summary>
            public string Text { get; set; } = "";
            /// <summary>English。</summary>
            public string TextEn { get; set; } = "";
        }
    }

    // ═══════════ ★ 2026-10-29【进图预览 · 契约 v3】只读推导的类型 ═══════════
    // 契约：audit/进图预览-设计.md 第八节。三个类型与 EventRow 同层，字段严格照契约。
    /// <summary>预览分组（Key 固定 8 个之一）。</summary>
    public sealed class PreviewGroup
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public List<PreviewItem> Items { get; set; } = new();
    }

    /// <summary>预览条目：文案已含数值；From/FromName 必填（追到来源活动）。</summary>
    public sealed class PreviewItem
    {
        public string Text { get; set; } = "";
        public string From { get; set; } = "";
        public string FromName { get; set; } = "";
    }

    /// <summary><see cref="BuildPreview"/> 的返回。</summary>
    public sealed class PreviewResult
    {
        public string Map { get; set; } = "";
        public List<PreviewGroup> Groups { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Sources { get; set; } = new();
    }

    /// <summary>Touches 的维度，**顺序即输出顺序**（契约规定）；地图级 token 会追加 ":{mapId}"。</summary>
    private static readonly string[] TouchDims =
    {
        "bosses", "loot", "traders", "flea", "quests",
        "weather", "exits", "bots", "globals", "hideout", "airdrop",
    };

    /// <summary>
    /// 允许地图级 token 的维度（其余维度保持全局 token）。
    /// 契约 v2 只有 bosses/loot/exits；★ 2026-10-29【用户裁决】把 bots / airdrop 也放开：
    /// `botTypeLimitSet:{woods}` → `bots:woods`、`airdropFrequencyMaps:[…]` → `airdrop:<map>`。
    /// 只有**结构上真的按图分键**的字段才会拆（`botAppearance`/`officialLoot`/`airdropWeight` 仍是全局）。
    /// </summary>
    private static readonly HashSet<string> MapLevelDims = new(StringComparer.OrdinalIgnoreCase)
    {
        "bosses", "loot", "exits", "bots", "airdrop",
    };

    /// <summary>
    /// SPT 真实 location id（全小写）。来源 = `SPT_Data/database/locations/` 下的目录名，2026-10-29 逐个核对：
    /// bigmap / factory4_day / factory4_night / interchange / laboratory / labyrinth / lighthouse / rezervbase /
    /// sandbox / sandbox_high / shoreline / tarkovstreets / woods / town / terminal / suburbs / hideout / develop / privatearea。
    /// 用途：**判断某个字段的键到底是不是地图**（不是地图的键 ⇒ 不造地图 token，绝不臆造）。
    /// 未列出的键一律当"不是地图"⇒ 回落全局 token（安全降级：新地图也会退化成粗粒度，不会算错）。
    /// </summary>
    private static readonly HashSet<string> KnownMapIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigmap", "factory4_day", "factory4_night", "interchange", "laboratory", "labyrinth",
        "lighthouse", "rezervbase", "sandbox", "sandbox_high", "shoreline", "tarkovstreets",
        "woods", "town", "terminal", "suburbs", "hideout", "develop", "privatearea",
    };

    /// <summary>
    /// events.json 字段 → 影响面（契约 Touches）。**只按实际声明的字段算**，不按分类猜。
    /// 一个字段只归一个桶；同一活动命中多个字段 ⇒ 多个桶。
    /// </summary>
    private static readonly Dictionary<string, string> TouchFieldMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // 刷首领 / 随从 / 首领刷点与敌对
        ["bossSpawns"] = "bosses", ["bossChance"] = "bosses", ["zeroBossSpawns"] = "bosses",
        ["forceExtraSpawns"] = "bosses", ["forceZone"] = "bosses", ["forceAbpsSpawns"] = "bosses",
        ["escortCount"] = "bosses", ["escortType"] = "bosses", ["bossTimetable"] = "bosses",
        // ★ v2 改判：`zryachiyMeleeChance` = 官方 AdjustZryachiyMeleeChance()，把 bosszryachiy 的
        //   **Scabbard（近战武器）概率**改成 100（EventDef.cs:109、ExtraDimensions.cs:1814）——
        //   这是"首领拿什么装备"，不是"首领刷在哪张图" ⇒ 归 bots，不归 bosses
        //   （否则万圣节会凭空拿到一个全局 bosses token，和所有刷首领的活动互撞）。
        ["forceSeasonalBossKey"] = "bosses", ["forceAllyPairs"] = "bosses", ["zryachiyMeleeChance"] = "bots",
        // 战利品 / 容器 / 掉率 / 物品数值
        ["items"] = "loot", ["officialLoot"] = "loot", ["officialGear"] = "loot",
        ["looseLootMultiplier"] = "loot", ["staticLootMultiplier"] = "loot", ["looseLootInject"] = "loot",
        ["looseLootWeightSet"] = "loot", ["containerLootInject"] = "loot", ["lootMaps"] = "loot",
        ["staticLootAdd"] = "loot",   // ★ task-28：静态战利品容器注入（全局，不按图分键）
        ["factionLoot"] = "loot",     // ★ 第 12 项：往指定 bot 角色背包里塞事件限定物品
        // ★ 万圣节感染者（僵尸）：感染者本身是"刷在地图上的 bot"，且 PreviewLayout 只有 8 组
        //   （新增 season 组要改 razor，而 razor 禁改）⇒ 归到 bosses 维度，预览与冲突提示都能看见。
        ["bossDifficulty"] = "bosses", ["escortDifficulty"] = "bosses",
        ["nativeZombies"] = "bosses", ["zombieOutbreak"] = "bosses",
        ["noKarmaLoss"] = "bosses", ["infectedTagillaChance"] = "bosses",
        ["zombieSeasonalKey"] = "bosses",
        ["christmasLootBoost"] = "loot", ["pumpkinScavBackpack"] = "loot", ["playerGift"] = "loot",
        ["playerScavEquipmentBoost"] = "loot", ["taggedAndCursed"] = "loot",
        ["botLootAdd_"] = "loot", ["botLootStack_"] = "loot",
        // 商人：库存 / 价格 / 解锁 / 手册价 / 图标
        ["unlockTraders"] = "traders", ["traderAssortAdd"] = "traders", ["traderAssortRemove"] = "traders",
        ["traderPriceMultiplier"] = "traders", ["traderPriceMultiplierPerTrader"] = "traders",
        ["traderBarterAdd"] = "traders", ["traderUpdateSeconds"] = "traders",
        ["traderCashOffersDisabled"] = "traders", ["removePurchaseLimits"] = "traders",
        ["handbookPriceMultiplier"] = "traders", ["handbookPriceSet"] = "traders",
        ["eventTraderIcons"] = "traders",
        // 跳蚤
        ["fleaMinLevel"] = "flea", ["fleaTaxMultiplier"] = "flea", ["fleaBlacklistAdd"] = "flea",
        // 任务开窗
        ["enableQuests"] = "quests", ["questRequires"] = "quests",
        ["relaxLevelQuests"] = "quests",   // ★ 第 9 项：放开任务里的假等级门槛（Level ≥99 → 1）
        // 天气 / 时间
        ["forceWeatherSeason"] = "weather", ["forceNightRaid"] = "weather", ["nightOnly"] = "weather",
        // 地图进出 / 转场 / 撤离
        ["exitLockMaps"] = "exits", ["exitKeepOnly"] = "exits", ["transitsActive"] = "exits",
        ["transitDisableIds"] = "exits", ["scavRaidMapDisable"] = "exits", ["labsFreeAccess"] = "exits",
        ["toll"] = "exits",
        // bot：外观 / 装备 / 血量 / 敌种数量与敌对 / 等级门槛
        ["botAppearance"] = "bots", ["botAppearanceOverride"] = "bots", ["botGearUpgrade"] = "bots",
        ["botEquipmentReplace"] = "bots", ["botHealthMultiplier"] = "bots", ["botHealthOverride"] = "bots",
        ["botAmmoAdd"] = "bots", ["eventActive"] = "bots", ["nativeChristmas"] = "bots",
        ["scavSpawnSuppress"] = "bots", ["botTypeLimitSet"] = "bots", ["forceHostility"] = "bots",
        ["minPlayerLevelByMap"] = "bots", ["minPlayerLevel"] = "bots",
        // 全局数值（伤害/技能/负重/口渴/命中抖动…）
        ["globals"] = "globals", ["overdamageMultiplier"] = "globals", ["stimulatorBuffsAdd"] = "globals",
        ["halloweenStimBuff"] = "globals", ["renameBitcoin"] = "globals",
        ["waterCollectorEfficiency"] = "globals", ["aimPunchMultiplier"] = "globals",
        ["inertiaMultiplier"] = "globals",
        // 藏身处
        ["hideoutBuildSeconds"] = "hideout", ["hideoutAddRequirements"] = "hideout",
        ["hideoutRecipeOverride"] = "hideout",
        // 空投 + 随季节活动一起下发的其它原生件
        ["airdropAppendLoot"] = "airdrop", ["airdropFrequencyMultiplier"] = "airdrop",
        ["airdropFrequencyMaps"] = "airdrop", ["airdropWeight"] = "airdrop",
        ["airdropAddLoot"] = "airdrop", ["airdropForceType"] = "airdrop",
        ["gifterOfficial"] = "airdrop", ["btrTarcolaSkin"] = "bots", ["runddansActive"] = "globals",
    };

    /// <summary>
    /// 按 events.json 里**实际生效的字段**推导 Touches（逗号分隔；契约维度顺序）。
    /// 契约 v2：`bosses / loot / exits` 里**按地图分键**的字段拆成 `维度:地图id`，其余保持全局 token。
    /// </summary>
    private static string TouchesOf(JsonObject o)
    {
        var globalDims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perMap = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in TouchFieldMap)
        {
            if (!HasEffect(o, kv.Key)) continue;
            var dim = kv.Value;
            var maps = MapLevelDims.Contains(dim) ? MapsOf(o[kv.Key]) : null;
            if (maps == null || maps.Count == 0) { globalDims.Add(dim); continue; }   // 全局 token
            if (!perMap.TryGetValue(dim, out var set))
                perMap[dim] = set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var m in maps) set.Add(m.ToLowerInvariant());
        }
        var tokens = new List<string>();
        foreach (var dim in TouchDims)
        {
            if (globalDims.Contains(dim)) tokens.Add(dim);
            if (perMap.TryGetValue(dim, out var set))
                foreach (var m in set) tokens.Add(dim + ":" + m);
        }
        return string.Join(",", tokens);
    }

    /// <summary>
    /// 判断一个字段的**真实结构**是否"按地图分键"；是则返回地图 id 集合，否则 null（= 全局 token）。
    /// 认得的四种结构（全部来自 events.json 实测形状）：
    /// <list type="bullet">
    /// <item>对象：键是地图 id（`bossSpawns` / `bossChance` / `zeroBossSpawns` / `forceZone` / `escortCount` /
    ///   `looseLootInject` / `containerLootInject` / `looseLootWeightSet` / `transitDisableIds` …）；
    ///   出现 `"*"`（通配所有图）⇒ 按全局 token 处理（语义等价，且不会臆造地图表）。</item>
    /// <item>字符串数组：元素是地图 id 或 `"地图|撤离点名"`（`lootMaps` / `exitLockMaps` / `exitKeepOnly` /
    ///   `scavRaidMapDisable` …）；一个都不认识（如 `officialLoot:["christmas"]`、`enableQuests:[任务id]`）⇒ null。</item>
    /// <item>对象数组：元素带 `"map"` 字段（`forceAllyPairs:[{map:"shoreline",…}]`、`bossTimetable.rows:[{map:…}]`）。</item>
    /// <item>**嵌一层**的地图声明：对象/数组里的值再声明地图表 —— 实测 `forceAbpsSpawns:{角色:[地图…]}`。
    ///   递归深度 ≤ 3，只认 <see cref="KnownMapIds"/> 里的真 id（认不出就什么都不加，不臆造）。</item>
    /// </list>
    /// 标量（数字/布尔/字符串，如 `looseLootMultiplier` / `transitsActive` / `labsFreeAccess`）⇒ null。
    /// </summary>
    private static HashSet<string> MapsOf(JsonNode v) => MapsOf(v, 0);

    private static HashSet<string> MapsOf(JsonNode v, int depth)
    {
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (v == null || depth > 3) return null;
        if (v is JsonObject ob)
        {
            bool wildcard = false;
            foreach (var kv in ob)
            {
                var k = (kv.Key ?? "").Trim();
                if (k == "*" || string.Equals(k, "any", StringComparison.OrdinalIgnoreCase)) { wildcard = true; continue; }
                if (KnownMapIds.Contains(k)) { maps.Add(k); continue; }
                if (depth < 3 && kv.Value is JsonArray or JsonObject)                  // 嵌一层的地图表
                {
                    var inner = MapsOf(kv.Value, depth + 1);
                    if (inner != null) maps.UnionWith(inner);
                }
            }
            if (wildcard) return null;                 // "*" = 所有图 ⇒ 全局
            return maps.Count > 0 ? maps : null;       // 键全不是地图（如 toll 的 rotation/mode）⇒ 全局
        }
        if (v is JsonArray arr)
        {
            foreach (var it in arr)
            {
                if (it is JsonValue jv)
                {
                    var s = jv.ToString();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    var m = s.Split('|')[0].Trim();    // "bigmap|Crossroads" ⇒ bigmap
                    if (KnownMapIds.Contains(m)) maps.Add(m);
                }
                else if (it is JsonObject oi)
                {
                    var mv = oi["map"]?.ToString();     // {map:"shoreline",…}
                    if (!string.IsNullOrWhiteSpace(mv) && KnownMapIds.Contains(mv.Trim())) maps.Add(mv.Trim());
                    var inner = MapsOf(oi, depth + 1);
                    if (inner != null) maps.UnionWith(inner);
                }
            }
            return maps.Count > 0 ? maps : null;       // 一个地图都不认识 ⇒ 不是地图表
        }
        return null;
    }

    /// <summary>
    /// 字段"有没有实际作用"：空数组 / 空对象 / false / null 一律视为没声明。
    /// （实例：`enableQuests: []` 的 5 个季节/回归条目不该算 quests；空 `bossChance: {}` 同理。
    ///  数字 0 仍然算"声明了"——`hideoutBuildSeconds:0`=秒建、`fleaTaxMultiplier:0`=零税都是有意义的取值。）
    /// </summary>
    private static bool HasEffect(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v == null) return false;
        switch (v)
        {
            case JsonArray a: return a.Count > 0;
            case JsonObject ob: return ob.Count > 0;
            case JsonValue jv:
                if (jv.TryGetValue<bool>(out var b)) return b;
                return true;
            default: return true;
        }
    }

    /// <summary>
    /// 契约 v2 冲突规则：两个活动 **Touches 按 token 精确取交集** 且 **至少一个不是 ready** ⇒ 互相记入 ConflictsWith。
    /// 例外（同维度内）：**全局 token** 与 **同维度的地图 token** 算冲突（`bosses` × `bosses:laboratory`）。
    /// 不同维度之间永远不算（`loot:bigmap` × `bosses:bigmap` 不冲突）。同 id 不算；对称；结果按 id 排序。
    /// </summary>
    private static void ComputeConflicts(List<EventRow> rows)
    {
        if (rows == null || rows.Count < 2) return;
        var touches = new List<string[]>(rows.Count);
        foreach (var r in rows)
            touches.Add((r.Touches ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        for (int i = 0; i < rows.Count; i++)
            for (int j = i + 1; j < rows.Count; j++)
            {
                if (string.Equals(rows[i].Id, rows[j].Id, StringComparison.OrdinalIgnoreCase)) continue;
                bool anyNotReady = !string.Equals(rows[i].UiTag, "ready", StringComparison.OrdinalIgnoreCase)
                                || !string.Equals(rows[j].UiTag, "ready", StringComparison.OrdinalIgnoreCase);
                if (!anyNotReady) continue;
                if (!TokensConflict(touches[i], touches[j])) continue;
                rows[i].ConflictsWith.Add(rows[j].Id);
                rows[j].ConflictsWith.Add(rows[i].Id);
            }
        foreach (var r in rows) r.ConflictsWith.Sort(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>契约 v2 的单对判定（纯函数，便于自检）：见 <see cref="ComputeConflicts"/> 的规则说明。</summary>
    private static bool TokensConflict(string[] a, string[] b)
    {
        foreach (var x in a)
        {
            if (string.IsNullOrEmpty(x)) continue;
            var dx = DimOf(x);
            bool xMap = x.Length > dx.Length;
            foreach (var y in b)
            {
                if (string.IsNullOrEmpty(y)) continue;
                if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;   // 精确 token 相同
                var dy = DimOf(y);
                if (!string.Equals(dx, dy, StringComparison.OrdinalIgnoreCase)) continue;   // 不同维度永不冲突
                bool yMap = y.Length > dy.Length;
                if (xMap != yMap) return true;                                             // 同维度：全局 × 地图
            }
        }
        return false;
    }

    /// <summary>token 的维度部分（`bosses:laboratory` ⇒ `bosses`）。</summary>
    private static string DimOf(string token)
    {
        int i = token.IndexOf(':');
        return i < 0 ? token : token.Substring(0, i);
    }

    /// <summary>列出 events.json 的活动（含说明/日期窗/分类），供页面做活动列表与排除勾选。</summary>
    public List<EventRow> ListEvents()
    {
        var list = new List<EventRow>();
        try
        {
            var p = ModDir == null ? null : Path.Combine(ModDir, "events.json");
            if (p == null || !File.Exists(p)) return list;
            if (JsonNode.Parse(File.ReadAllText(p), documentOptions: new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonArray arr) return list;

            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var id = o["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                string win = "-";
                if (int.TryParse(o["startMonth"]?.ToString(), out var a) && a > 0)
                    win = $"{a}/{o["startDay"]?.ToString()} - {o["endMonth"]?.ToString()}/{o["endDay"]?.ToString()}";

                var desc = o["desc"]?.ToString() ?? "";
                if (desc.Length > 160) desc = desc.Substring(0, 160) + "…";
                var descEn = EnMap.TryGetValue(id, out var en) ? en : "";
                if (descEn.Length > 300) descEn = descEn.Substring(0, 300) + "…";

                // ★ 2026-10-29【第一批补充 · 接口契约】uiTag / wiki / 日期窗 / 影响面
                //   uiTag 只认契约里的三个值，缺省或写错一律回落 simulated（页面永远有徽章可渲染）
                var uiTag = o["uiTag"]?.ToString() ?? "";
                if (uiTag != "ready" && uiTag != "simulated" && uiTag != "experimental") uiTag = "simulated";

                list.Add(new EventRow
                {
                    Id = id,
                    Zh = o["nameZh"]?.ToString() ?? "",
                    En = o["nameEn"]?.ToString() ?? "",
                    Category = o["category"]?.ToString() ?? "",
                    Window = win,
                    Desc = desc,
                    DescEn = descEn,
                    Always = o["alwaysInRotation"]?.GetValue<bool>() ?? false,
                    UiTag = uiTag,
                    Wiki = o["wiki"]?.ToString() ?? "",
                    StartDay = IntOf(o["startDay"]),
                    StartMonth = IntOf(o["startMonth"]),
                    EndDay = IntOf(o["endDay"]),
                    EndMonth = IntOf(o["endMonth"]),
                    Touches = TouchesOf(o),
                });
                FillQuestsAndCompat(list[^1], o);
            }

            // ★ 契约要求：冲突在这里一次算完（Touches 有交集 且 至少一个非 ready）
            ComputeConflicts(list);
        }
        catch { }
        return list.OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ★ 2026-10-29【第 18 + 20 项】任务可用性 + 兼容性提示（数据层，只读）
    //   · Quests  = enableQuests ∪ 我们 questlines 里该活动的任务，按 PreviousId 串成链；
    //   · Compat  = 别的 mod 在这张图上也在刷东西（ABPS / ISB / Blackout / ORBIT）。
    //   两边的扫描都各自缓存在 QuestAvailability / CompatReport 里（一次启动一遍）。
    // ═══════════════════════════════════════════════════════════════════════════

    private static void FillQuestsAndCompat(EventRow row, JsonObject o)
    {
        // ── 第 18 项：任务可用性 ──────────────────────────────────────────────
        try
        {
            var ids = new List<string>();
            var inSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (o["enableQuests"] is JsonArray eq)
                foreach (var n in eq)
                {
                    var qid = n?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(qid) && inSet.Add(qid)) ids.Add(qid);
                }
            // 我们 questlines 文件里属于该活动的任务（文件顺序追加）
            try { foreach (var q in QuestAvailability.QuestlineQuestsOf(row.Id)) if (inSet.Add(q)) ids.Add(q); } catch { }

            if (ids.Count > 0)
            {
                var infos = new Dictionary<string, QuestAvailability.Info>(StringComparer.OrdinalIgnoreCase);
                var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var qid in ids)
                {
                    var info = QuestAvailability.Lookup(qid);
                    infos[qid] = info;
                    var p = info.PreviousId ?? "";
                    // 只有"前置也在本活动链上"才算链上前一个；指到链外的（如原生根任务）留空
                    prev[qid] = (p.Length > 0 && inSet.Contains(p) && !string.Equals(p, qid, StringComparison.OrdinalIgnoreCase))
                        ? p : "";
                }

                // 拓扑排序：反复把「链上前置已经不在待排集合里」的挑出来；成环就原样追加剩余项
                var ordered = new List<string>();
                var remaining = new List<string>(ids);
                while (remaining.Count > 0)
                {
                    bool progressed = false;
                    for (int i = 0; i < remaining.Count;)
                    {
                        var qid = remaining[i];
                        var p = prev[qid];
                        if (p.Length == 0 || !remaining.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            ordered.Add(qid);
                            remaining.RemoveAt(i);
                            progressed = true;
                            continue;
                        }
                        i++;
                    }
                    if (!progressed) { ordered.AddRange(remaining); break; }
                }

                foreach (var qid in ordered)
                {
                    var info = infos[qid];
                    row.Quests.Add(new EventRow.QuestInfo
                    {
                        Id = qid,
                        NameZh = info.NameZh,
                        NameEn = info.NameEn,
                        Trader = info.Trader,
                        Level = info.Level,
                        PreviousId = prev[qid],
                        Present = info.Present,
                        Source = info.Source,
                        FromOurQuestline = info.FromOurQuestline,
                    });
                }

                var missing = row.Quests.Count(q => !q.Present);
                row.QuestSummary = row.Quests.Count == 0 ? ""
                    : $"{row.Quests.Count} 条 · " + (missing > 0 ? $"本机缺 {missing} 条" : "本机齐全");
            }
        }
        catch { row.Quests = new List<EventRow.QuestInfo>(); row.QuestSummary = ""; }

        // ── 第 20 项：兼容性提示 ──────────────────────────────────────────────
        try
        {
            var maps = MapsFromTouches(row.Touches);
            var hasExternalBridge = o["externalBridge"] != null;
            var dims = DimsFromTouches(row.Touches);

            var picked = new List<EventRow.CompatNote>();
            foreach (var n in CompatReport.NotesFor(maps))
            {
                if (n.Map != "*")
                {
                    picked.Add(new EventRow.CompatNote { Source = n.Source, Map = n.Map, Text = n.Text, TextEn = n.TextEn });
                    continue;
                }
                // 全局提示要"与这个活动有关"才显示（否则 92 个活动每个都挂一条 Blackout，太吵）：
                //   · Blackout / ISB：实验室相关（本图含 laboratory，或碰 access/exits，或活动有 externalBridge）
                //   · ORBIT：碰 bosses / bots（AI 行为重叠）
                bool relevant = n.Source switch
                {
                    "Blackout" or "ISB" => maps.Contains("laboratory") || hasExternalBridge
                                           || dims.Contains("access") || dims.Contains("exits"),
                    "ORBIT" => dims.Contains("bosses") || dims.Contains("bots") || maps.Count > 0,
                    _ => true,
                };
                if (!relevant) continue;
                picked.Add(new EventRow.CompatNote { Source = n.Source, Map = n.Map, Text = n.Text, TextEn = n.TextEn });
            }
            row.Compat = picked.Take(12).ToList();
        }
        catch { row.Compat = new List<EventRow.CompatNote>(); }
    }

    /// <summary>从 Touches token（`维度:地图id`）里抽地图 id（小写）。</summary>
    private static HashSet<string> MapsFromTouches(string touches)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(touches)) return set;
        foreach (var tok in touches.Split(','))
        {
            var i = tok.IndexOf(':');
            if (i <= 0 || i == tok.Length - 1) continue;
            set.Add(tok.Substring(i + 1).Trim().ToLowerInvariant());
        }
        return set;
    }

    /// <summary>从 Touches token 里抽维度名（去掉 `:地图id` 后缀）。</summary>
    private static HashSet<string> DimsFromTouches(string touches)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(touches)) return set;
        foreach (var tok in touches.Split(','))
        {
            var i = tok.IndexOf(':');
            set.Add((i > 0 ? tok.Substring(0, i) : tok).Trim());
        }
        return set;
    }

    /// <summary>读 JsonNode 里的整数（读不到/不是数字 ⇒ 0，与「无日期窗」同义）。</summary>
    private static int IntOf(JsonNode n)
    {
        if (n == null) return 0;
        if (int.TryParse(n.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ★ 2026-10-29【进图预览 · 契约 v3】只读推导
    //   设计：audit/进图预览-设计.md 第八节。
    //   **只读**：只读 events.json / StatusText（= 写 当前生效活动.txt 的同一份快照）/ force.txt；
    //   不写盘、不改任何静态状态、不调用任何 Dimension 的 Apply ⇒ 可以随便点，不会影响游戏。
    //   这是"按 events.json 字段的**近似**推导"（叠加规则见各分支注释），实际以引擎套用结果为准；
    //   页面底部固定那句「预览为近似推导，实际以游戏内为准」由 B 线渲染。
    //
    //   分组：契约固定 8 个 Key（页面按此顺序渲染）。契约把 bots / globals / hideout / flea 也列为
    //   "要对所有图输出"的维度，但没有独立 Key ⇒ 就近折叠进契约的 8 个组（Title 里说清）：
    //     bosses  ← bosses + bots（敌情）            traders ← traders + flea
    //     loot    ← loot + globals + hideout（玩家侧数值/时长）
    //   ⇒ 这样不会出现"页面不认识的 Key 被丢掉"的情况。
    // ═══════════════════════════════════════════════════════════════════════════
    private static readonly (string Key, string Title)[] PreviewLayout =
    {
        ("bosses",  "首领"),
        ("loot",    "战利品"),
        ("exits",   "撤离"),
        ("weather", "天气 / 时间"),
        ("airdrop", "空投"),
        ("access",  "门票 / 门禁"),
        ("quests",  "任务"),
        ("traders", "商人"),
    };

    /// <summary>
    /// 按当前「生效（StatusText 快照）+ 强制（force.txt）」的活动集合，推导指定地图下一次进图的改动。
    /// **只读**：不写盘、不改状态、不调用任何 Dimension 的 Apply。
    /// <para>地图级维度（bosses / loot 的按图键 / exits / 空投按图频率 / 门票）只看本图命中；
    /// 全局维度（商人 / 跳蚤 / 任务 / 全局数值 / 藏身处 / 天气 / 空投频率 / bot）对所有图都输出。</para>
    /// </summary>
    public PreviewResult BuildPreview(string mapId)
    {
        var res = new PreviewResult { Map = (mapId ?? "").Trim() };
        try
        {
            var map = res.Map;
            if (map.Length == 0) { res.Warnings.Add("没指定地图 id，无法推导"); return res; }
            if (!KnownMapIds.Contains(map))
                res.Warnings.Add($"「{map}」不是已知的 SPT location id（仍按字段推导，结果可能为空）");
            var mapZh = Rotation.MapZh(map);

            var nodes = LoadEventNodes();
            var nameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in nodes)
            {
                var eid = o["id"]?.ToString() ?? "";
                if (eid.Length == 0) continue;
                nameById[eid] = o["nameZh"]?.ToString() ?? eid;
            }

            // ── 参与集合 = StatusText 的生效列表 ∪ force.txt（复用现有读取方式，不另读一份） ──
            var want = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ActiveEventIds()) AddWant(id);
            foreach (var id in ReadForce()) AddWant(id);
            void AddWant(string id)
            {
                if (string.IsNullOrWhiteSpace(id)) return;
                var k = id.Trim();
                if (k.Length == 0 || string.Equals(k, "none", StringComparison.OrdinalIgnoreCase)) return;
                if (!nameById.ContainsKey(k) || !seen.Add(k)) return;
                want.Add(k);
            }
            res.Sources.AddRange(want);

            var groups = new Dictionary<string, PreviewGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, title) in PreviewLayout)
                groups[key] = new PreviewGroup { Key = key, Title = title };
            void Add(string key, string text, string from, string fromName)
            {
                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(from)) return;
                if (!groups.TryGetValue(key, out var g)) return;
                g.Items.Add(new PreviewItem { Text = text.Trim(), From = from, FromName = fromName ?? "" });
            }

            foreach (var o in nodes)
            {
                var id = o["id"]?.ToString() ?? "";
                if (!seen.Contains(id)) continue;
                var name = nameById.TryGetValue(id, out var nm) ? nm : id;
                FillBosses(Add, o, map, mapZh, id, name);
                FillLoot(Add, o, map, id, name);
                FillExits(Add, o, map, id, name);
                FillWeather(Add, o, id, name);
                FillAirdrop(Add, o, map, id, name);
                FillAccess(Add, o, map, id, name);
                FillQuests(Add, o, id, name);
                FillTraders(Add, o, id, name);
            }

            // ── ★ 2026-10-29【用户裁决：合并来源，不丢信息】同组内 `Text` 完全相同的条目合并成一条 ──
            //   · From = **最后一个套用者**（迭代顺序 = events.json 声明顺序 ≈ 引擎套用顺序）
            //   · FromName = 「<最后那个中文名> 等 N 个活动」（N = 去重后的来源活动数；N=1 不写"等"）
            //   · **不跨组**合并；顺序按首次出现的位置
            //   · Warnings 仍用**合并前**的来源集合算（否则来源被折叠后告警数会少算）
            var groupFroms = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, _) in PreviewLayout)
            {
                var g = groups[key];
                if (g.Items.Count == 0) continue;
                groupFroms[key] = g.Items.Select(i => i.From).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            foreach (var (key, _) in PreviewLayout) MergeSameText(groups[key]);

            // ── Warnings：同一张图同一维度被 2 个以上活动命中 ──
            foreach (var (key, _) in PreviewLayout)
            {
                var g = groups[key];
                if (g.Items.Count == 0) continue;
                if (groupFroms.TryGetValue(key, out var froms) && froms.Count >= 2)
                    res.Warnings.Add($"{froms.Count} 个活动同时改「{g.Title}」："
                        + string.Join("、", froms.Select(f => nameById.TryGetValue(f, out var n2) ? n2 : f))
                        + "（实际以最后套用者为准）");
                res.Groups.Add(g);
            }
            if (want.Count == 0)
                res.Warnings.Add("当前没有任何生效/强制的活动 ⇒ 下次进图不会有活动改动");
        }
        catch (Exception ex) { res.Warnings.Add("预览推导失败：" + ex.Message); }
        return res;
    }

    /// <summary>
    /// 同一个分组内把 <c>Text</c> 完全相同的条目合并成一条（用户裁决：合并来源，不丢信息）。
    /// From 取最后一个套用者；FromName 追加「等 N 个活动」（N = 去重后的来源活动数，N=1 保持原样）。
    /// </summary>
    private static void MergeSameText(PreviewGroup g)
    {
        if (g == null || g.Items.Count < 2) return;
        var merged = new List<PreviewItem>(g.Items.Count);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var froms = new List<HashSet<string>>(g.Items.Count);
        foreach (var it in g.Items)
        {
            var k = it.Text ?? "";
            if (index.TryGetValue(k, out var at))
            {
                froms[at].Add(it.From);
                merged[at].From = it.From;            // 最后一个套用者
                merged[at].FromName = it.FromName;    // 先记最后那个的名字，下面再拼"等 N 个"
                continue;
            }
            index[k] = merged.Count;
            merged.Add(new PreviewItem { Text = it.Text, From = it.From, FromName = it.FromName });
            froms.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { it.From });
        }
        for (int i = 0; i < merged.Count; i++)
            if (froms[i].Count > 1)
                merged[i].FromName = $"{merged[i].FromName} 等 {froms[i].Count} 个活动";
        g.Items.Clear();
        g.Items.AddRange(merged);
    }

    /// <summary>解析 StatusText（服务端快照）里的生效活动 id —— 与页面 <c>ActiveEventIds()</c> 同一口径。</summary>
    private List<string> ActiveEventIds()
    {
        var list = new List<string>();
        try
        {
            var status = StatusText ?? "";
            foreach (var raw in status.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#事件|", StringComparison.Ordinal))
                {
                    var parts = line.Split('|');
                    if (parts.Length > 4 && parts[4].Trim().Length > 0) list.Add(parts[4].Trim());
                }
                else if (line.StartsWith("#手动|", StringComparison.Ordinal))
                {
                    foreach (var seg in line.Substring("#手动|".Length).Split('|'))
                        foreach (var id in seg.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                            if (id.Trim().Length > 0) list.Add(id.Trim());
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>读 events.json 的原始节点（只读；给预览按字段推导用）。</summary>
    private List<JsonObject> LoadEventNodes()
    {
        var list = new List<JsonObject>();
        try
        {
            var p = ModDir == null ? null : Path.Combine(ModDir, "events.json");
            if (p == null || !File.Exists(p)) return list;
            if (JsonNode.Parse(File.ReadAllText(p), documentOptions: new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonArray arr) return list;
            foreach (var n in arr) if (n is JsonObject o) list.Add(o);
        }
        catch { }
        return list;
    }

    // ── 预览的通用小工具（全部纯函数）──────────────────────────────
    private static JsonNode Field(JsonObject o, string key)
        => o != null && o.TryGetPropertyValue(key, out var v) ? v : null;

    private static double? D(JsonNode n)
    {
        if (n is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        return null;
    }

    private static int? I(JsonNode n)
    {
        if (n is JsonValue v && v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    private static int CountOf(JsonNode n) => n switch
    {
        JsonArray a => a.Count,
        JsonObject o => o.Count,
        _ => 0,
    };

    /// <summary>按地图分键的对象/数组里取本图那一项（支持 `"*"` 通配）。</summary>
    private static bool HitMap(JsonNode mapKeyed, string map, out JsonNode val)
    {
        val = null;
        if (mapKeyed is not JsonObject ob) return false;
        foreach (var kv in ob)
            if (string.Equals((kv.Key ?? "").Trim(), map, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
            { val = kv.Value; return true; }
        foreach (var kv in ob)
            if (string.Equals((kv.Key ?? "").Trim(), "*", StringComparison.Ordinal) && kv.Value != null)
            { val = kv.Value; return true; }
        return false;
    }

    /// <summary>两级：<c>外[map][role]</c>（bossChance / escortCount / escortType 的形状）。</summary>
    private static JsonNode SubVal(JsonNode outer, string map, string role)
    {
        if (!HitMap(outer, map, out var inner) || inner is not JsonObject ob) return null;
        foreach (var kv in ob)
            if (string.Equals(kv.Key, role, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>数组型按图字段（lootMaps / exitLockMaps / scavRaidMapDisable …）是否含本图。</summary>
    private static bool ListHasMap(JsonNode arr, string map)
        => arr is JsonArray a && a.Any(x => string.Equals(x?.ToString()?.Split('|')[0]?.Trim(), map, StringComparison.OrdinalIgnoreCase));

    private static string WeatherZh(string s) => s?.Trim().ToUpperInvariant() switch
    {
        "WINTER" => "冬季", "AUTUMN" => "秋季", "AUTUMN_LATE" => "深秋", "SPRING" => "春季",
        "SPRING_EARLY" => "早春", "SUMMER" => "夏季", "STORM" => "暴风雨", _ => s,
    };

    /// <summary>首领 / 敌人（bosses ← bosses + bots）。</summary>
    private static void FillBosses(Action<string, string, string, string> add, JsonObject o,
                                   string map, string mapZh, string id, string name)
    {
        var bc = Field(o, "bossChance");
        var ec = Field(o, "escortCount");
        var bdf = Field(o, "bossDifficulty");
        var edf = Field(o, "escortDifficulty");
        var spawned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HitMap(Field(o, "bossSpawns"), map, out var roles) && roles is JsonArray ra)
            foreach (var r in ra)
            {
                var role = r?.ToString(); if (string.IsNullOrWhiteSpace(role)) continue;
                spawned.Add(role);
                var text = $"追加 {Rotation.BossZh(role)} 到 {mapZh}";
                if (D(SubVal(bc, map, role)) is double p) text += $" {F(p)}%";
                if (I(SubVal(ec, map, role)) is int n) text += $"（随从 {n}）";
                var bdv = SubVal(bdf, map, role)?.ToString();
                var edv = SubVal(edf, map, role)?.ToString();
                if (!string.IsNullOrWhiteSpace(bdv))
                    text += $"（难度 {bdv}" + (!string.IsNullOrWhiteSpace(edv) ? $" / 随从难度 {edv}" : "") + "）";
                add("bosses", text, id, name);
            }
        if (HitMap(bc, map, out var perRole) && perRole is JsonObject pr)
            foreach (var kv in pr)
            {
                if (spawned.Contains(kv.Key)) continue;
                if (D(kv.Value) is double p) add("bosses", $"{Rotation.BossZh(kv.Key)} {F(p)}%", id, name);
            }
        if (HitMap(Field(o, "zeroBossSpawns"), map, out var zr) && zr is JsonArray za)
            foreach (var r in za)
            {
                var role = r?.ToString(); if (string.IsNullOrWhiteSpace(role)) continue;
                add("bosses", $"移除 {Rotation.BossZh(role)}（压到 0%）", id, name);
            }
        if (HitMap(Field(o, "forceZone"), map, out var fz) && fz != null)
        {
            var zones = fz is JsonObject zo
                ? string.Join(" / ", zo.Select(kv => kv.Value?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(2)) + "…"
                : fz.ToString();
            add("bosses", $"首领改刷到本图指定刷点（{zones}）", id, name);
        }
        if (HitMap(Field(o, "forceExtraSpawns"), map, out var fx) && D(fx) is double extra)
            add("bosses", $"本图额外增加 {F(extra)} 波刷新", id, name);
        if (Field(o, "forceAbpsSpawns") is JsonObject fa)
            foreach (var kv in fa)
                if (kv.Value is JsonArray maps && maps.Any(m => string.Equals(m?.ToString(), map, StringComparison.OrdinalIgnoreCase)))
                    add("bosses", $"{Rotation.BossZh(kv.Key)} 100%（经 ABPS 刷新）", id, name);
        if (Field(o, "bossTimetable") is JsonObject tt && tt["rows"] is JsonArray rows)
            foreach (var r in rows)
                if (r is JsonObject ro && string.Equals(ro["map"]?.ToString(), map, StringComparison.OrdinalIgnoreCase)
                    && D(ro["from"]) is double h1 && D(ro["to"]) is double h2)
                    add("bosses", $"按时刻表在本图轮换首领（{F(h1)}:00–{F(h2)}:00 MSK）", id, name);
        // ★ 万圣节感染者（僵尸）
        if (Field(o, "nativeZombies") is JsonValue nz && nz.TryGetValue<bool>(out var nzon) && nzon)
        {
            var ob = Field(o, "zombieOutbreak") as JsonObject;
            var floor = D(ob?["everyMapMin"]) ?? 40;
            var extraN = (ob?["extra"] as JsonObject)?.Count ?? 0;
            add("bosses", $"全图感染者（僵尸）：感染率下限 {F(floor)}%" + (extraN > 0 ? $"（{extraN} 张图单独指定）" : ""), id, name);
            if (Field(o, "infectedTagillaChance") is JsonValue tc && D(tc) is double tcv && tcv > 0)
                add("bosses", $"感染者 Tagilla 概率提高到 {F(tcv)}%", id, name);
        }
        if (Field(o, "noKarmaLoss") is JsonValue nk && nk.TryGetValue<bool>(out var nkon) && nkon)
            add("bosses", "击杀 Scav 不扣业力（5 类角色 StandingForKill 负值清零）", id, name);
        if (Field(o, "forceAllyPairs") is JsonArray ap)
            foreach (var x in ap)
                if (x is JsonObject po && string.Equals(po["map"]?.ToString(), map, StringComparison.OrdinalIgnoreCase))
                    add("bosses", $"{Rotation.BossZh(po["a"]?.ToString())} 与 {Rotation.BossZh(po["b"]?.ToString())} 结盟", id, name);
        // ── bots 维度（折叠进本组）──
        if (Field(o, "botAppearance") is JsonArray ba && ba.Count > 0)
            add("bosses", $"bot 外观改为官方的 {string.Join("/", ba.Select(x => x?.ToString()))} 套装", id, name);
        if (Field(o, "botAppearanceOverride") is JsonObject bao && bao.Count > 0)
            add("bosses", $"bot 外观整体替换（{bao.Count} 个角色）", id, name);
        if (Field(o, "botGearUpgrade") is JsonObject bgu && bgu.Count > 0)
            add("bosses", $"bot 装备升级（{bgu.Count} 个角色）", id, name);
        if (Field(o, "botEquipmentReplace") is JsonObject ber && ber.Count > 0)
            add("bosses", "bot 装备池整体替换（自定义配置）", id, name);
        if (Field(o, "botHealthMultiplier") is JsonObject bhm && bhm.Count > 0)
            add("bosses", "bot 血量倍率：" + string.Join("、", bhm.Take(3).Select(kv => $"{Rotation.BossZh(kv.Key)} ×{F(D(kv.Value) ?? 1)}")), id, name);
        if (Field(o, "botHealthOverride") is JsonObject bho && bho.Count > 0)
            add("bosses", $"bot 血量改写（{bho.Count} 个角色）", id, name);
        if (Field(o, "botAmmoAdd") is JsonObject baa && baa.Count > 0)
            add("bosses", $"补口径弹药（{baa.Count} 个角色）", id, name);
        if (Field(o, "forceHostility") is JsonObject fh && fh.Count > 0)
            add("bosses", $"强制敌对关系（{fh.Count} 组）", id, name);
        if (ListHasMap(Field(o, "scavSpawnSuppress"), map))
            add("bosses", "本图普通 SCAV 刷新被压制", id, name);
        if (HitMap(Field(o, "botTypeLimitSet"), map, out var btl) && btl is JsonObject bto && bto.Count > 0)
            add("bosses", $"本图 bot 数量上限调整（{bto.Count} 类）", id, name);
        if (HitMap(Field(o, "minPlayerLevelByMap"), map, out var mpl) && I(mpl) is int lvl)
            add("bosses", $"本图需要 {lvl} 级才会出现这些敌人", id, name);
        if (I(Field(o, "minPlayerLevel")) is int mlv)
            add("bosses", $"需要 {mlv} 级才会出现这些敌人", id, name);
        if (Field(o, "gifterOfficial") is JsonValue gv && gv.TryGetValue<bool>(out var gon) && gon)
            add("bosses", "圣诞老人（gifter）按官方 10 图概率表刷新", id, name);
    }

    /// <summary>战利品 / 玩家侧数值（loot ← loot + globals + hideout）。</summary>
    private static void FillLoot(Action<string, string, string, string> add, JsonObject o, string map, string id, string name)
    {
        if (D(Field(o, "looseLootMultiplier")) is double ll) add("loot", $"全局地面爆率 ×{F(ll)}", id, name);
        if (D(Field(o, "staticLootMultiplier")) is double sl) add("loot", $"全局容器爆率 ×{F(sl)}", id, name);
        if (D(Field(o, "christmasLootBoost")) is double cb) add("loot", $"圣诞物资概率 +{F(cb * 100)}%（附加式）", id, name);
        if (CountOf(Field(o, "items")) is int ic && ic > 0) add("loot", $"物品数值改动（{ic} 组）", id, name);
        if (Field(o, "officialLoot") is JsonArray ol && ol.Count > 0)
            add("loot", $"官方战利品表 {string.Join("/", ol.Select(x => x?.ToString()))} 注入 bot 掉落", id, name);
        if (Field(o, "officialGear") is JsonArray og && og.Count > 0)
            add("loot", $"官方装备表 {string.Join("/", og.Select(x => x?.ToString()))} 注入 bot 装备", id, name);
        if (Field(o, "playerGift") is JsonArray pg && pg.Count > 0)
            add("loot", $"玩家节日礼物：{string.Join("/", pg.Select(x => x?.ToString()))}", id, name);
        if (I(Field(o, "playerScavEquipmentBoost")) is int ps && ps > 0)
            add("loot", $"SCAV 装备预设批次数 +{ps}", id, name);
        if (Field(o, "pumpkinScavBackpack") is JsonValue pv && pv.TryGetValue<bool>(out var pon) && pon)
            add("loot", "SCAV 背包追加南瓜", id, name);
        if (Field(o, "taggedAndCursed") is JsonValue tv && tv.TryGetValue<bool>(out var ton) && ton)
            add("loot", "全图 Tagged and Cursed（击杀会被标记）", id, name);
        if (CountOf(Field(o, "botLootAdd_")) is int bla && bla > 0) add("loot", $"bot 战利品注入（{bla} 个角色）", id, name);
        if (CountOf(Field(o, "botLootStack_")) is int bls && bls > 0) add("loot", $"bot 固定携带物品（{bls} 个角色）", id, name);
        // ★ task-28：静态战利品容器注入（全局；"抽屉/外套/电脑里能翻出 X"）
        if (Field(o, "staticLootAdd") is JsonObject sla && sla.Count > 0)
        {
            var kinds = CountOf(Field(sla, "items"));
            var boxes = Field(sla, "containers") is JsonArray ca ? ca.Count : 0;
            add("loot", $"静态战利品容器注入（{kinds} 种物品 × {boxes} 类容器）", id, name);
        // ★ 第 12 项：factionLoot（背包注入）—— 只报"几个角色 / 几件物品"，不臆造物品名
        if (Field(o, "factionLoot") is JsonObject fl && fl.Count > 0)
        {
            int flItems = 0;
            foreach (var kv in fl)
                if (kv.Value is JsonObject fo && fo["items"] is JsonObject io2) flItems += io2.Count;
            add("loot", $"往 {fl.Count} 类 bot 的背包加入 {flItems} 种事件限定物品", id, name);
        }
        }
        // 地图键的注入（只看本图）
        if (HitMap(Field(o, "looseLootInject"), map, out var lli) && CountOf(lli) is int n1 && n1 > 0)
            add("loot", $"本图地面战利品注入（{n1} 组）", id, name);
        // ★ 2026-10-30 按精确坐标追加刷点（addPoints）：挂在 looseLootInject 下，这里只做预览提示
        if (HitMap(Field(o, "looseLootInject"), map, out var lli2) && lli2 is JsonObject lliMap)
            foreach (var mkv in lliMap)
            {
                if (mkv.Value is not JsonObject mopt) continue;
                var apArr = mopt["addPoints"] as JsonArray;
                int apN = apArr?.Count ?? 0;
                if (apN == 0) continue;
                var tplCount = apArr!.Count(x => x is JsonObject aj
                    && aj["tpl"] is JsonValue tv && tv.TryGetValue<string>(out var ts) && !string.IsNullOrWhiteSpace(ts));
                add("loot", $"按坐标追加刷点（{mkv.Key}：{apN} 个，其中 {tplCount} 个用 tpl 自动造模板）", id, name);
            }
        if (HitMap(Field(o, "containerLootInject"), map, out var cli) && CountOf(cli) is int n2 && n2 > 0)
            add("loot", $"本图容器战利品注入（{n2} 组）", id, name);
        if (HitMap(Field(o, "looseLootWeightSet"), map, out var lws) && CountOf(lws) is int n3 && n3 > 0)
            add("loot", $"本图战利品权重改写（{n3} 项）", id, name);
        if (ListHasMap(Field(o, "lootMaps"), map)) add("loot", "本图战利品改动生效", id, name);
        // ── globals 维度（折叠进本组）──
        if (Field(o, "globals") is JsonObject gl)
            foreach (var kv in gl)
                add("loot", D(kv.Value) is double gv ? $"全局 {kv.Key} ×{F(gv)}" : $"全局 {kv.Key} = {kv.Value}", id, name);
        if (D(Field(o, "overdamageMultiplier")) is double od) add("loot", $"玩家伤害 ×{F(od)}", id, name);
        if (Field(o, "stimulatorBuffsAdd") is JsonObject sb)
            add("loot", $"新增兴奋剂 Buff：{string.Join("/", sb.Select(kv => kv.Key))}", id, name);
        if (Field(o, "halloweenStimBuff") is JsonValue hv && hv.TryGetValue<bool>(out var hon) && hon)
            add("loot", "万圣节兴奋剂 Buff 生效", id, name);
        if (Field(o, "renameBitcoin") is JsonValue rv && rv.TryGetValue<bool>(out var ron) && ron)
            add("loot", "比特币改名（Physical SPT Coin）", id, name);
        if (D(Field(o, "waterCollectorEfficiency")) is double wc) add("loot", $"集水器效率 ×{F(wc)}", id, name);
        if (D(Field(o, "aimPunchMultiplier")) is double ap) add("loot", $"瞄准抖动 ×{F(ap)}", id, name);
        if (D(Field(o, "inertiaMultiplier")) is double im) add("loot", $"惯性 ×{F(im)}", id, name);
        if (Field(o, "runddansActive") is JsonValue rv2 && rv2.TryGetValue<bool>(out var ron2) && ron2)
            add("loot", "开启圆环舞（Runddans）", id, name);
        if (Field(o, "btrTarcolaSkin") is JsonValue bv && bv.TryGetValue<bool>(out var bon) && bon)
            add("loot", "BTR 皮肤改为 Tarcola", id, name);
        if (Field(o, "nativeChristmas") is JsonValue nv && nv.TryGetValue<bool>(out var non) && non)
            add("loot", "启用原生圣诞事件", id, name);
        if (Field(o, "eventActive") is JsonValue ev2 && ev2.TryGetValue<bool>(out var eon) && eon)
            add("loot", "启用原生节日计时（EventActive）", id, name);
        // ── hideout 维度（折叠进本组）──
        if (I(Field(o, "hideoutBuildSeconds")) is int hb)
            add("loot", hb == 0 ? "藏身处建造/升级秒完成（0 秒）" : $"藏身处建造时间 {hb} 秒", id, name);
        if (CountOf(Field(o, "hideoutAddRequirements")) is int hr && hr > 0)
            add("loot", $"藏身处升级材料替换（{hr} 项）", id, name);
        if (CountOf(Field(o, "hideoutRecipeOverride")) is int ho && ho > 0)
            add("loot", $"藏身处配方覆盖（{ho} 项）", id, name);
    }

    /// <summary>撤离 / 转场。</summary>
    private static void FillExits(Action<string, string, string, string> add, JsonObject o, string map, string id, string name)
    {
        var keep = new List<string>();
        if (Field(o, "exitKeepOnly") is JsonArray ko)
            foreach (var x in ko)
            {
                var s = x?.ToString() ?? "";
                var parts = s.Split('|');
                if (parts.Length > 1 && string.Equals(parts[0].Trim(), map, StringComparison.OrdinalIgnoreCase))
                    keep.Add(parts[1].Trim());
            }
        if (ListHasMap(Field(o, "exitLockMaps"), map))
            add("exits", keep.Count > 0
                ? $"撤离点被锁：只保留 {keep.Count} 个（{string.Join("/", keep)}）"
                : "撤离点被锁（只保留官方指定的一个）", id, name);
        else if (keep.Count > 0)
            add("exits", $"撤离点只保留 {keep.Count} 个（{string.Join("/", keep)}）", id, name);
        if (HitMap(Field(o, "transitDisableIds"), map, out var td) && CountOf(td) is int tn && tn > 0)
            add("exits", $"禁用 {tn} 条转场口", id, name);
        if (ListHasMap(Field(o, "scavRaidMapDisable"), map))
            add("exits", "SCAV 模式无法进入本图", id, name);
        if (Field(o, "transitsActive") is JsonValue tv && tv.TryGetValue<bool>(out var ton) && ton)
            add("exits", "打开全图转场口（未解锁的那些）", id, name);
    }

    /// <summary>天气 / 时间（全局）。</summary>
    private static void FillWeather(Action<string, string, string, string> add, JsonObject o, string id, string name)
    {
        var ws = Field(o, "forceWeatherSeason")?.ToString();
        if (!string.IsNullOrWhiteSpace(ws)) add("weather", $"强制天气：{WeatherZh(ws)}", id, name);
        if (D(Field(o, "forceNightRaid")) is double nr && nr > 0)
            add("weather", $"强制夜图（概率 {F(nr * 100)}%）", id, name);
        if (Field(o, "nightOnly") is JsonValue nv && nv.TryGetValue<bool>(out var non) && non)
            add("weather", "只在夜间时段", id, name);
    }

    /// <summary>空投。</summary>
    private static void FillAirdrop(Action<string, string, string, string> add, JsonObject o, string map, string id, string name)
    {
        if (D(Field(o, "airdropFrequencyMultiplier")) is double fm) add("airdrop", $"空投频率 ×{F(fm)}", id, name);
        if (ListHasMap(Field(o, "airdropFrequencyMaps"), map)) add("airdrop", "本图空投频率提升", id, name);
        if (Field(o, "airdropWeight") is JsonObject aw && aw.Count > 0)
            add("airdrop", "空投箱型权重：" + string.Join("、", aw.Take(3).Select(kv => $"{kv.Key}={F(D(kv.Value) ?? 0)}")), id, name);
        if (Field(o, "airdropAddLoot") is JsonObject al && al.Count > 0)
            add("airdrop", $"空投追加物资（{al.Count} 类箱型）", id, name);
        if (Field(o, "airdropAppendLoot") is JsonObject ap && ap.Count > 0)
            add("airdrop", $"空投追加 {ap.Count} 件物品", id, name);
        var ft = Field(o, "airdropForceType")?.ToString();
        if (!string.IsNullOrWhiteSpace(ft)) add("airdrop", $"强制空投类型：{ft}", id, name);
    }

    /// <summary>门票（免票 / 路费）。</summary>
    private static void FillAccess(Action<string, string, string, string> add, JsonObject o, string map, string id, string name)
    {
        if (Field(o, "labsFreeAccess") is JsonValue lv && lv.TryGetValue<bool>(out var lon) && lon
            && string.Equals(map, "laboratory", StringComparison.OrdinalIgnoreCase))
            add("access", "实验室免票（清空门卡要求）", id, name);
        if (Field(o, "toll") is JsonObject toll)
        {
            var rot = toll["rotation"] is JsonArray ra ? ra.Count : 0;
            if (rot == 0 || ListHasMap(toll["rotation"], map))
            {
                var exempt = I(toll["exemptLevel"]) ?? 16;
                var mode = toll["rotationMode"]?.ToString() ?? "day";
                add("access", $"进图需交路费（{exempt} 级以下豁免；免费图按 {(mode == "hours" ? "小时" : "天")} 轮换）", id, name);
            }
        }
    }

    /// <summary>任务（全局）。</summary>
    private static void FillQuests(Action<string, string, string, string> add, JsonObject o, string id, string name)
    {
        if (CountOf(Field(o, "enableQuests")) is int eq && eq > 0)
            add("quests", $"解锁 {eq} 条活动任务", id, name);
        if (CountOf(Field(o, "questRequires")) is int qr && qr > 0)
            add("quests", $"任务前置条件 {qr} 条", id, name);
        // ★ 第 9 项：随便挑了 `IsOn` 风格 —— 只看字段真值，不臆测数量
        if (Field(o, "relaxLevelQuests") is JsonValue rl && rl.TryGetValue<bool>(out var rlon) && rlon)
            add("quests", "放开任务里的假等级门槛（如 Level ≥99 改为 1）", id, name);
    }

    /// <summary>商人与跳蚤（traders ← traders + flea）。</summary>
    private static void FillTraders(Action<string, string, string, string> add, JsonObject o, string id, string name)
    {
        if (Field(o, "unlockTraders") is JsonValue uv && uv.TryGetValue<bool>(out var uon) && uon)
            add("traders", "所有商人解锁到最高忠诚", id, name);
        if (D(Field(o, "traderPriceMultiplier")) is double pm) add("traders", $"商人价格 ×{F(pm)}", id, name);
        if (Field(o, "traderPriceMultiplierPerTrader") is JsonObject pt && pt.Count > 0)
            add("traders", "商人价格：" + string.Join("、", pt.Select(kv => $"{kv.Key} ×{F(D(kv.Value) ?? 1)}")), id, name);
        if (CountOf(Field(o, "traderAssortAdd")) is int aa && aa > 0) add("traders", $"商人上架新商品（{aa} 个商人）", id, name);
        if (CountOf(Field(o, "traderAssortRemove")) is int ar && ar > 0) add("traders", $"商人下架商品（{ar} 个商人）", id, name);
        if (CountOf(Field(o, "traderBarterAdd")) is int ba && ba > 0) add("traders", $"商人易物新增（{ba} 条）", id, name);
        if (I(Field(o, "traderUpdateSeconds")) is int us && us > 0) add("traders", $"商人补货间隔 {us} 秒", id, name);
        if (Field(o, "traderCashOffersDisabled") is JsonValue cv && cv.TryGetValue<bool>(out var con) && con)
            add("traders", "商人现金报价关闭（只能以物易物）", id, name);
        if (Field(o, "removePurchaseLimits") is JsonValue rv && rv.TryGetValue<bool>(out var ron) && ron)
            add("traders", "解除商人购买上限", id, name);
        if (D(Field(o, "handbookPriceMultiplier")) is double hm) add("traders", $"手册价 ×{F(hm)}", id, name);
        if (CountOf(Field(o, "handbookPriceSet")) is int hs && hs > 0) add("traders", $"手册价改写 {hs} 条", id, name);
        if (Field(o, "eventTraderIcons") is JsonValue iv && iv.TryGetValue<bool>(out var ion) && ion)
            add("traders", "商人换成节日头像", id, name);
        if (I(Field(o, "fleaMinLevel")) is int fl)
            add("traders", fl >= 90 ? $"跳蚤最低等级 {fl}（≈关闭）" : $"跳蚤最低等级 {fl}", id, name);
        if (D(Field(o, "fleaTaxMultiplier")) is double ft)
            add("traders", ft == 0 ? "跳蚤挂单零税" : $"跳蚤挂单税 ×{F(ft)}", id, name);
        if (CountOf(Field(o, "fleaBlacklistAdd")) is int fb && fb > 0) add("traders", $"跳蚤禁售 {fb} 种物品", id, name);
    }


    /// <summary>★ v1.3.8 当前强制列表里两两冲突的描述（空 = 不冲突）。</summary>
    public List<string> ForceConflicts() => EventEngine.DescribeConflicts(ReadForce());
}
