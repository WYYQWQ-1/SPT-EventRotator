using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>活动表加载 + 当前活动选择（日期窗 / force.txt 手动强制）。</summary>
public static class EventEngine
{
    public static readonly List<EventDef> All = new();

    /// <summary>
    /// ★ v1.97 多活动支持：同时生效的**全部**活动（按 events.json 里的先后顺序）。
    ///   历史事件（如"八影会谈"）本身就是**多个活动一起开**的：
    ///   全首领聚海关 + 邪教徒爆率↑ + 邪教徒刷新↑ + 邪教房爆率↑ + 走私者回归。
    ///   force.txt 支持逗号分隔来同时强制多个，例如：meeting_of_the_bosses,smugglers
    /// </summary>
    public static readonly List<EventDef> Actives = new();

    /// <summary>兼容旧代码：第一个生效的活动（旧行为 = 单选时它就是唯一的那个）。</summary>
    public static EventDef Active => Actives.Count > 0 ? Actives[0] : null;

    public static string ForcedId = "";
    public static string ModPath = "";
    public static Action OnRefresh;   // 由 Plugin 注入：进图刷新时重新套用爆率等

    /// <summary>
    /// ★★★ v2.7.0【硬开关】**不启用任何活动**（`config.json` 顶层 `disableAllEvents`，默认 `false`）。
    ///
    /// <para><b>中文</b>：不启用任何活动 —— 开启后**无论如何都不套用任何活动**
    /// （含 force.txt 手动强制、主轮换、季节阶段自动接管、日期窗、奖励轮换）。
    /// 服务端会先执行一次**完整还原**（DB 首领 / 敌对性 / 战利品 transformer / 天气 /
    /// 地点感染 / 外部联动 Blackout …），然后停在"裸 SPT"状态。</para>
    /// <para><b>English</b>: Disable all events. When true, no event is ever applied (force.txt,
    /// rotation, seasonal takeover, date windows and reward rotation are all ignored) and every
    /// change made by a previously applied event is restored once, leaving a vanilla SPT server.</para>
    ///
    /// <para><b>为什么需要它</b>（真实用户反馈「关掉了所有活动并重启，活动依旧生效」）：
    /// 面板上"全部取消勾选"只是**清空 force.txt（手动强制层）**，下面还有
    /// ① 季节阶段自动接管 ② 主轮换 ③ 奖励轮换 三层会自动把活动补回来。
    /// 本开关是**最高优先级**：<see cref="SelectActive"/> 最前面直接清空 <see cref="Actives"/> 并
    /// return，<see cref="RefreshFromForce"/> 连 force.txt 都不读。</para>
    /// </summary>
    public static bool DisableAllEvents;

    /// <summary>
    /// 调试日志等级（mod 目录下 config.json 的 logLevel 字段）：
    ///   "off"     = 只输出警告/错误
    ///   "basic"   = 活动选择、每图汇总、进图概要、刷新统计（默认）
    ///   "verbose" = 上面全部 + 进图逐条首领明细 + 每个地图完整计划
    /// 兼容旧写法：{"debug": false} 等于 "off"，{"debug": true} 等于 "basic"。
    /// </summary>
    public static string LogLevel = "basic";

    /// <summary>手动开启万圣节时的感染扩散"第几天"：0=活动第 1 天（默认）；-1=按末期满额；N=第 N+1 天。</summary>
    public static int InfectionManualDays = 0;

    public static bool DebugLogging => !string.Equals(LogLevel, "off", StringComparison.OrdinalIgnoreCase);
    public static bool VerboseLogging => string.Equals(LogLevel, "verbose", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v1.3.7：灾厄小队主武器缺失时是否自动换成原版武器（config.json 的 <c>weaponFallback</c>，默认 true）。
    /// 设 false 表示「武器模板查不到也照原样用」——给明确知道自己 WTT 齐全、不想被改的玩家。
    /// </summary>
    public static bool WeaponFallbackEnabled = true;

    /// <summary>mod 根目录（用于探测其它 mod 是否装了"加武器预设"的类型）。</summary>

    /// <summary>
    /// 是否允许在被地图移植 mod 整份接管的图上**追加**首领（config.json: allowOnOverhaulMaps）。
    /// false（默认）= 避让（安全；但"格鲁哈上灯塔"这类活动会失效）；
    /// true = 照常追加（想让这类活动生效就开）。
    /// </summary>
    public static bool AllowOnOverhaulMaps = false;

    public static string ModDir = "";

    private static DateTime _forceWrite = DateTime.MinValue;

    /// <summary>加载 mod 目录下的 events.json / config.json / force.txt。</summary>
    public static void Load(string modPath, ISptLogger<EventRotatorServer> log)
    {
        ModPath = modPath;
        LoadConfig(modPath, log);
        try
        {
            var file = Path.Combine(modPath, "events.json");
            if (!File.Exists(file)) { log.Error($"[EventRotator] events.json not found: {file}"); return; }
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var defs = JsonSerializer.Deserialize<List<EventDef>>(File.ReadAllText(file), opts);
            All.Clear();
            if (defs != null) All.AddRange(defs.Where(d => d != null && !string.IsNullOrEmpty(d.Id)));
            log.Info($"[EventRotator] loaded {All.Count} events from events.json");
        }
        catch (Exception ex) { log.Error($"[EventRotator] events.json parse failed: {ex}"); }
        RefreshFromForce(log);
    }

    /// <summary>读 mod 目录下 config.json 的日志等级。文件不存在时默认 basic。</summary>
    private static void LoadConfig(string modPath, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            var f = Path.Combine(modPath, "config.json");
            if (!File.Exists(f))
            {
                log?.Info("[EventRotator] config.json 不存在，日志等级默认 basic");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (doc.RootElement.TryGetProperty("logLevel", out var lv) && lv.ValueKind == JsonValueKind.String)
                LogLevel = lv.GetString() ?? "basic";
            else if (doc.RootElement.TryGetProperty("debug", out var d) &&
                     (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False))
                LogLevel = d.GetBoolean() ? "basic" : "off";   // 兼容旧写法

            // ★ v1.3.7：灾厄武器兜底开关（默认 true；显式 false 才关）
            if (doc.RootElement.TryGetProperty("weaponFallback", out var wf) &&
                (wf.ValueKind == JsonValueKind.True || wf.ValueKind == JsonValueKind.False))
                WeaponFallbackEnabled = wf.GetBoolean();
            else
                WeaponFallbackEnabled = true;

            // ★★★ v2.7.0【硬开关】`disableAllEvents`（顶层 bool，**缺省 false**）：
            //   「不启用任何活动：开启后无论如何都不套用任何活动（含 force.txt、轮换、季节自动接管）」
            //   "Disable all events: when on, no event is ever applied (force.txt, rotation and
            //    seasonal takeover included)." —— 全部还原后停在裸 SPT 状态。
            //   ⚠ 每次读配置都**显式赋值**（不做 "缺失就保持上次"）：面板/手改删掉这个键 ⇒ 回到默认 false。
            if (doc.RootElement.TryGetProperty("disableAllEvents", out var dae) &&
                (dae.ValueKind == JsonValueKind.True || dae.ValueKind == JsonValueKind.False))
                DisableAllEvents = dae.GetBoolean();
            else
                DisableAllEvents = false;

            // ★★★ 2026-10-29【第 22 项 · 日期窗生效范围】config.json 顶层 `dateWindowRule`：
            //   seasonOffOnly（默认）| always（旧行为 / 回退）。
            //   ⚠ 取值不认识时**不报错中断**（沿用本文件一贯的"逐段解析、坏一段不拖垮全局"），
            //     只 Warning 并回落到默认；这里必须在 `SeasonRotation.Load` 之后才能打印"模式 ⇒ 是否生效"，
            //     所以日志放在下面 `SeasonRotation.Load` 那一段之后（见 `[日期窗] dateWindowRule=` 一行）。
            {
                string rawDwr = null;
                if (doc.RootElement.TryGetProperty("dateWindowRule", out var dwr) && dwr.ValueKind == JsonValueKind.String)
                    rawDwr = (dwr.GetString() ?? "").Trim();
                DateWindowRule = string.Equals(rawDwr, "always", StringComparison.OrdinalIgnoreCase) ? "always" : "seasonOffOnly";
                if (!string.IsNullOrEmpty(rawDwr) && !string.Equals(rawDwr, DateWindowRule, StringComparison.OrdinalIgnoreCase))
                    log?.Warning($"[EventRotator][日期窗] dateWindowRule 取值 '{rawDwr}' 不认识 ⇒ 已按默认 seasonOffOnly 处理"
                                 + "（可选：seasonOffOnly / always）");
            }


            // ★ v2.6.76【P0 修复 · 独立复核 🟡-1】**性能埋点改成独立开关、默认关**：
            //   `perfLog`（bool，**缺省 false**）= 唯一开关。
            //   为什么必须解耦：原来 `PerfTimer.Enabled => DebugLogging = (logLevel != "off")`
            //   ⇒ 用户 logLevel=basic 时 24 个埋点全开，其中 3 个在**每-bot** 路径 ⇒
            //     每局新增 40~120 条同步日志 + 每只 ~0.2µs 计时 ⇒ **监控层自己在制造小卡顿**。
            //   现在：不写 `perfLog` ⇒ 一律不采集、不打；要诊断时加 `"perfLog": true` 重启即可
            //   （那时 `[perf]` 行走 `PerfTimer.Emit` **强制输出**，不受 `logLevel` 影响）。
            //   可选微调：`perfLogEvery`（默认 10；**显式 0/负数 = 旧的每 500 次一条**）、`perfLogSeconds`（默认 30）。
            {
                bool? pl = null;
                int? pe = null, ps = null;
                if (doc.RootElement.TryGetProperty("perfLog", out var plEl) &&
                    (plEl.ValueKind == JsonValueKind.True || plEl.ValueKind == JsonValueKind.False))
                    pl = plEl.GetBoolean();
                if (doc.RootElement.TryGetProperty("perfLogEvery", out var peEl) && peEl.ValueKind == JsonValueKind.Number)
                    pe = peEl.GetInt32();
                if (doc.RootElement.TryGetProperty("perfLogSeconds", out var psEl) && psEl.ValueKind == JsonValueKind.Number)
                    ps = psEl.GetInt32();
                try { PerfTimer.Configure(pl, pe, ps); } catch { }
            }
            if (doc.RootElement.TryGetProperty("allowOnOverhaulMaps", out var aom) &&
                (aom.ValueKind == JsonValueKind.True || aom.ValueKind == JsonValueKind.False))
                AllowOnOverhaulMaps = aom.GetBoolean();

            // ★ v0.1.4 随机地图首领概率：随机抽几张图（0 = 用活动默认，1~11）
            // ★★ v0.1.5 修正：手动（force.txt / F12，当前不在活动窗内）开启万圣节时的"感染扩散进度"。
            //   旧行为是**按末期满额**处理 ⇒ 官方配置为 0 的图（海关/立交桥）会被爬升到峰值 15%，
            //   与活动 desc 里"海关与立交桥零感染"和玩家预期不符（用户实测截图）。
            //   新默认 **0 = 按活动第 1 天**（感染者各图取官方基础值、零感染图保持 0）；
            //   想看"扩散到末期"的样子就把本字段设成 -1（= 满额），或设 1/2/3… 指定第几天。
            if (doc.RootElement.TryGetProperty("infectionManualDays", out var imd) && imd.ValueKind == JsonValueKind.Number)
                InfectionManualDays = imd.GetInt32();

            if (doc.RootElement.TryGetProperty("randomBossMapCount", out var rbm) && rbm.ValueKind == JsonValueKind.Number)
                Patches.BossWavePatch.RandomBossMapCount = Math.Clamp(rbm.GetInt32(), 1, 11);
            // ★ v0.1.4 随机图的首领概率（默认 100 = 一定刷）
            if (doc.RootElement.TryGetProperty("randomBossMapChance", out var rbc) && rbc.ValueKind == JsonValueKind.Number)
                Patches.BossWavePatch.RandomBossMapChancePct = Math.Clamp(rbc.GetInt32(), 1, 100);

            Rotation.Load(doc.RootElement, log);
            SeasonRotation.Load(doc.RootElement, log);
            RewardRotation.Load(doc.RootElement, log);
            // ★ 第 22 项：模式已读进来 ⇒ 打印"日期窗到底参不参与"，一句就能看出配置是否按预期生效
            try
            {
                log?.Info($"[EventRotator][日期窗] dateWindowRule={DateWindowRule} · seasonRotation.mode={SeasonRotation.Mode}"
                          + $" ⇒ 日期窗**{(DateWindowActive ? "参与" : "不参与")}**判定"
                          + (DateWindowActive
                                ? (DateWindowAlways ? "（always = 旧行为：总是按现实日期开活动）" : "（季节模式=off ⇒ 按现实日期开活动）")
                                : "（活动完全由轮换 / 季节阶段决定；想按现实日期开请把 seasonRotation.mode 改成 off，或把 dateWindowRule 改成 always）"));
            }
            catch { }
            // ★★★ v1.0.0【合并成一套 · 用户裁决】旧写法 `rotation.rewardCountMin/Max` ⇒ 迁到 `rewardRotation.countMin/countMax`
            //   ⚠ **只迁内存、不写回 config.json**（Lead 裁决：写回会重排格式 + 丢注释；用户下次在 F12 改任意项时会自然写上新键）
            //   判据：`rewardRotation` 节点缺失、或其 `countMin/countMax` 都没显式配置，而旧键有值（>0）
            try
            {
                bool rrConfigured = doc.RootElement.TryGetProperty("rewardRotation", out var rrNode)
                                    && rrNode.ValueKind == JsonValueKind.Object
                                    && (rrNode.TryGetProperty("countMin", out _) || rrNode.TryGetProperty("countMax", out _));
                if (!rrConfigured && Rotation.RewardMax > 0)
                {
                    RewardRotation.CountMin = Math.Max(0, Rotation.RewardMin);
                    RewardRotation.CountMax = Math.Max(RewardRotation.CountMin, Rotation.RewardMax);
                    // ★ v1.3.8【P1-5 修】只搬数量不置 Enabled ⇒ 老配置升级后
                    //   「主轮换奖励被清零 + 奖励轮换默认关」= 奖励 1~2 → 0。这里一并打开。
                    RewardRotation.Enabled = true;
                    Log?.Info($"[EventRotator] 检测到旧写法 rotation.rewardCountMin/Max={Rotation.RewardMin}/{Rotation.RewardMax}"
                            + $" ⇒ 已迁移到 rewardRotation.countMin/countMax={RewardRotation.CountMin}/{RewardRotation.CountMax}"
                            + "（**只迁内存，未改动你的 config.json**；原键保留但不再生效；奖励数量以后请在【奖励类轮换】里改）");
                }
            }
            catch { }
            // ★★★ v1.0.0【合并成一套】**主轮换不再追加任何奖励活动**（奖励全权交给 `rewardRotation`）
            //   原理：`Rotation` 内 7 处 `TakeRewards(RewardMax)` 都用这个字段做上限 ⇒ 置 0 即"一处改动、全路径生效"，
            //   比逐处改 7 个调用点更安全（不动任何挑选/避冲突逻辑）。旧值已在上面迁移走 ✔
            Rotation.RewardMin = 0; Rotation.RewardMax = 0;
            LoadAutoPairs(doc.RootElement, log);   // ★ v0.1.4 自动配对 / 自动爆率

            log?.Info($"[EventRotator] 日志等级 = {LogLevel}（config.json 的 logLevel：off/basic/verbose）");

        }
        catch (Exception ex) { log?.Warning($"[EventRotator] config.json 解析失败（用默认 basic）: {ex.Message}"); }
    }

    /// <summary>
    /// ★ v0.1.4 读「自动配对」「地图补位」「自动爆率提升」三张表（config.json 顶层）：
    /// <code>
    /// "autoPairs":     { "meeting_of_the_bosses": ["smugglers"] },
    /// "mapFillers":    { "bigmap": ["smugglers"], "woods": ["smugglers"] },
    /// "autoLootBoost": { "cultist_hunting": 1.5, "permanent_boss_presence": 1.25 }
    /// </code>
    /// `mapFillers` 有**默认值**（海关/森林 → 走私者）：因为实测走私者只在海关+森林刷竞技场斗士，
    /// 正好补"八影会谈把首领抽到海关后、别的图空了"的缺口。可清空 = 关闭该功能。
    /// `autoPairs` 默认**空**（不预设任何组合）。
    /// </summary>
    /// <summary>★ v0.1.5 自动季节选择策略：newest（默认，同族只开最新）/ all（旧行为，全开）。</summary>
    public static string SeasonAutoPick = "newest";

    // ═══════════ ★★★ 2026-10-29【第 22 项 · 日期窗生效范围】═══════════
    /// <summary>
    /// 日期窗规则（config.json **顶层** <c>dateWindowRule</c>）：
    /// <list type="bullet">
    /// <item><c>seasonOffOnly</c>（**默认** · 用户裁决 B）—— 只有 <c>seasonRotation.mode == "off"</c>
    ///   （界面上的「跟随现实日期」）时，活动的日期窗才参与判定；
    ///   <c>rotate</c> / <c>followEvent</c> / <c>fixed</c> 一律**不看**日期窗（活动完全由轮换 + 季节阶段决定）。</item>
    /// <item><c>always</c> —— 日期窗**总是**参与判定（= 一键回退到"按现实日期开活动"的旧行为）。</item>
    /// </list>
    /// <para><b>⚠ 不在本规则管辖内（刻意不动，见 §实现要求 3）</b>：</para>
    /// <list type="bullet">
    /// <item><c>force.txt</c> 手动强制 —— 最高优先级，`SelectActive` 更早处就 return（永远生效）；</item>
    /// <item>奖励轮换 —— `RewardRotation.PickForToday` 自己那套窗口过滤（`RewardRotation.cs:103-110`）；</item>
    /// <item>季节轮换 / 季节阶段 / 主轮换本身 / 周抽签（`weeklyDraw`，`Rotation.cs:253+`）；</item>
    /// <item>「拦截原版季节窗」<c>enableSeasonalEventDetection</c> —— 另一套机制（`Plugin.cs:550+`）。</item>
    /// </list>
    /// </summary>
    public static string DateWindowRule = "seasonOffOnly";

    /// <summary><c>dateWindowRule == "always"</c>（旧行为 / 一键回退用）。</summary>
    public static bool DateWindowAlways =>
        string.Equals(DateWindowRule, "always", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 当前季节轮换模式是不是 <c>off</c>（= 界面上的「跟随现实日期」）。
    /// <para>复用 `SeasonRotation.Mode`（**已经解析好的**静态字段，这里只做大小写无关比较，
    /// 不重读/不重解析 config.json；与 `SeasonRotation.IsRotating` / `IsFixed` 同一套判据）。</para>
    /// </summary>
    public static bool SeasonModeIsOff =>
        string.Equals((SeasonRotation.Mode ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ★ 日期窗**此刻是否参与判定**：<c>always</c> ⇒ 永远 true；<c>seasonOffOnly</c> ⇒ 仅当季节模式 = off。
    /// <para>调用点：`SelectActive` 的日期窗分支、`ExpandFamilyCombos` 的窗口过滤、状态预览。</para>
    /// </summary>
    public static bool DateWindowActive => DateWindowAlways || SeasonModeIsOff;

    /// <summary>★ 第 22 项：日期窗跳过汇总日志的"去重签名"（同一 (模式|规则|命中集) 只打一条）。</summary>
    private static string _lastDateWindowSkipSig;

    private static void LoadAutoPairs(JsonElement root, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            EventRotatorServer.AutoPairs.Clear();
            EventRotatorServer.AutoLootBoost.Clear();
            EventRotatorServer.MapFillers.Clear();

            // ── ★ 默认地图补位表：**空的** ──
            //   ★★★ v0.1.4 修正（用户指出的设计错误）：
            //   八影会谈 / 狩猎开始 这类"抽首领"活动的**目的就是让 BOSS 聚到一张图**。
            //   如果自动把首领加回其它被清空的图，等于把这个机制**拉平**，就白做了。
            //   ⇒ 正确做法不是"补回首领"，而是**提高其它图自己本来那些首领的刷新概率**
            //     （三狗 bossKnight、黑老登 bossPartisan 等），见 config 的 `autoBossChance`。
            //   所以这里默认**不补位**；想补请自己往 config 的 mapFillers 里写。

            try
            {
                if (root.TryGetProperty("seasonAutoPick", out var sap) && sap.ValueKind == JsonValueKind.String)
                    SeasonAutoPick = sap.GetString() ?? "newest";
                log?.Info($"[EventRotator] 季节自动选择策略 seasonAutoPick = {SeasonAutoPick}");
            }
            catch { }

            if (root.TryGetProperty("autoPairs", out var ap) && ap.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ap.EnumerateObject())
                {
                    var ids = new List<string>();
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var x in p.Value.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String) { var s = (x.GetString() ?? "").Trim(); if (s.Length > 0) ids.Add(s); }
                    else if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = (p.Value.GetString() ?? "").Trim();
                        if (s.Length > 0) ids.Add(s);
                    }
                    if (ids.Count > 0) EventRotatorServer.AutoPairs[p.Name.Trim()] = ids;
                }
                if (EventRotatorServer.AutoPairs.Count > 0)
                    log?.Warning($"[EventRotator] ★自动配对已配置 {EventRotatorServer.AutoPairs.Count} 条："
                                 + string.Join("；", EventRotatorServer.AutoPairs.Select(kv => $"{kv.Key} → {string.Join("+", kv.Value)}")));
            }

            // mapFillers：把用户配的合并进来（默认表为空 = 不补位）。
            //   想彻底关掉请用 config 的 "mapFillersEnable": false。
            if (root.TryGetProperty("mapFillers", out var mf) && mf.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in mf.EnumerateObject())
                {
                    var ids = new List<string>();
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var x in p.Value.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String) { var s = (x.GetString() ?? "").Trim(); if (s.Length > 0) ids.Add(s); }
                    // 空数组 = 显式关闭这张图的补位
                    EventRotatorServer.MapFillers[p.Name.Trim()] = ids;
                }
            }
            if (root.TryGetProperty("mapFillersEnable", out var mfe)
                && (mfe.ValueKind == JsonValueKind.True || mfe.ValueKind == JsonValueKind.False)
                && !mfe.GetBoolean())
            {
                EventRotatorServer.MapFillers.Clear();
                log?.Warning("[EventRotator] 地图补位已被 mapFillersEnable=false 关闭");
            }
            EventRotatorServer.MapFillers = new Dictionary<string, List<string>>(
                EventRotatorServer.MapFillers.Where(kv => kv.Value.Count > 0), StringComparer.OrdinalIgnoreCase);
            if (EventRotatorServer.MapFillers.Count > 0)
                log?.Warning($"[EventRotator] ★地图补位表 {EventRotatorServer.MapFillers.Count} 张图："
                             + string.Join("；", EventRotatorServer.MapFillers.Select(kv => $"{Rotation.MapZh(kv.Key)} → {string.Join("+", kv.Value)}"))
                             + "（开启『抽首领类』活动时会自动补上被清空地图的人气）");
            else
                log?.Info("[EventRotator] mapFillers 为空 → 抽首领类活动不会自动补位");

            // ═══════════ ★★★ v0.1.4 自动首领概率提升（config → autoBossChance）═══════════
            //   用户指出的正确做法：像八影会谈/狩猎开始这种"把首领抽到一张图"的活动，
            //   **不应该**把首领加回被清空的图（那等于把机制拉平），
            //   而应该**提高其它图自己本来那些首领的刷新概率**（三狗 bossKnight、黑老登 bossPartisan 等）。
            //   概率提升 ≠ 必刷，所以"首领聚集在一张图"的核心体验保留，别图也不会彻底空。
            //
            //   配置形态：{ "<触发活动 id>": { "<地图键或 *>": { "<首领角色或 *>": 概率 } } }
            //   例：{ "meeting_of_the_bosses": { "*": { "bossKnight": 35, "bossPartisan": 25 } } }
            EventRotatorServer.AutoBossChance.Clear();
            if (root.TryGetProperty("autoBossChance", out var abc) && abc.ValueKind == JsonValueKind.Object)
            {
                foreach (var trig in abc.EnumerateObject())
                {
                    if (trig.Value.ValueKind != JsonValueKind.Object) continue;
                    var byMap = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var mp in trig.Value.EnumerateObject())
                    {
                        if (mp.Value.ValueKind != JsonValueKind.Object) continue;
                        var byBoss = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var bp in mp.Value.EnumerateObject())
                            if (bp.Value.ValueKind == JsonValueKind.Number)
                            {
                                var v = Math.Clamp(bp.Value.GetInt32(), 0, 100);
                                if (v > 0) byBoss[bp.Name.Trim()] = v;
                            }
                        if (byBoss.Count > 0) byMap[mp.Name.Trim()] = byBoss;
                    }
                    if (byMap.Count > 0) EventRotatorServer.AutoBossChance[trig.Name.Trim()] = byMap;
                }
                if (EventRotatorServer.AutoBossChance.Count > 0)
                    log?.Warning($"[EventRotator] ★自动首领概率提升已配置 {EventRotatorServer.AutoBossChance.Count} 个触发活动："
                                 + string.Join("；", EventRotatorServer.AutoBossChance.Select(kv =>
                                     $"{kv.Key} → " + string.Join("、", kv.Value.Select(m =>
                                         $"{Rotation.MapZh(m.Key)}({string.Join("/", m.Value.Select(b => $"{Rotation.BossZh(b.Key)}={b.Value}%"))})")))));
            }

            if (root.TryGetProperty("autoLootBoost", out var alb) && alb.ValueKind == JsonValueKind.Object)            {
                foreach (var p in alb.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number)
                    {
                        var v = p.Value.GetDouble();
                        if (v > 0 && Math.Abs(v - 1.0) > 0.000001) EventRotatorServer.AutoLootBoost[p.Name.Trim()] = v;
                    }
                if (EventRotatorServer.AutoLootBoost.Count > 0)
                    log?.Warning($"[EventRotator] ★自动爆率提升已配置 {EventRotatorServer.AutoLootBoost.Count} 条："
                                 + string.Join("；", EventRotatorServer.AutoLootBoost.Select(kv => $"{kv.Key} ×{kv.Value}")));
            }
        }
        catch (Exception ex) { log?.Warning($"[EventRotator] autoPairs/mapFillers/autoLootBoost 解析失败: {ex.Message}"); }
    }

    /// <summary>调试日志（basic 及以上输出）。</summary>
    public static void Debug(string msg)
    {
        if (DebugLogging) Log?.Info(msg);
    }

    /// <summary>详细日志（仅 verbose 输出）。</summary>
    public static void Verbose(string msg)
    {
        if (VerboseLogging) Log?.Info(msg);
    }

    /// <summary>当前 logger（由 Plugin 注入），供 EventEngine.Debug 使用。</summary>
    public static ISptLogger<EventRotatorServer> Log;

    /// <summary>读 force.txt（客户端 F12 会写它）→ 设定强制活动。文件没变则跳过。</summary>
    /// <summary>★ v1.3.9 把 force.txt 的多种分隔写法（逗号/分号/竖线/空格/换行/制表）统一成逗号。</summary>
    /// <para>网页多选强制曾经写成"每行一个 id"，而旧解析不认换行 ⇒ 两个 id 被粘成一个不存在的 id ⇒ 静默回退成自动。</para>
    private static string NormalizeForceIds(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var parts = raw.Split(new[] { ',', ';', '|', ' ', (char)9, (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(",", parts.Select(x => x.Trim()).Where(x => x.Length > 0));
    }

    public static void RefreshFromForce(ISptLogger<EventRotatorServer> log = null)
    {
        try
        {
            if (string.IsNullOrEmpty(ModPath)) return;

            // ★★★ v2.7.0【硬开关 ①】`disableAllEvents=true` ⇒ **完全不读 force.txt**
            //   （文件里写什么都不会被采用；也不碰"文件没变就跳过"的判据缓存，
            //    这样关掉开关后 ForceRecheck 一失效就能立刻吃到文件里的真实内容）。
            //   仍然走一次 SelectActive：它会清空 Actives，保证"一个活动都不套用"。
            if (DisableAllEvents)
            {
                ForcedId = "";
                SelectActive(log);
                return;
            }

            var f = Path.Combine(ModPath, "force.txt");
            string id = "";
            DateTime w = DateTime.MinValue;
            if (File.Exists(f))
            {
                w = File.GetLastWriteTimeUtc(f);
                id = File.ReadAllText(f).Trim();
                // ★ v1.3.9【多选强制修复】force.txt 可能是"每行一个 id"（网页多选就是这么写的），
                //   而旧解析只认 , ; | 空格 ⇒ 换行会把两个 id 粘成一个不存在的 id ⇒ 静默回退成"自动"。
                //   这里统一把换行/制表/连续空白折成逗号，后面所有解析都按逗号走。
                id = NormalizeForceIds(id);
            }
            // ★★★ v1.6.5【服务端审计 #2 修】force.txt **不存在**时，旧判据（空==空）会让首调直接 return ⇒
            //   `SelectActive` 永不执行 ⇒ 删掉/漏装 force.txt 就静默"一个活动都没有"。
            //   `_forceInited` 保证首次必跑。
            // ★★★ v1.6.5【服务端审计 #4 修】同时把"轮换档位 / 日期"纳入触发条件：
            //   以前只有 force.txt 变化才重算 ⇒ R8-A（季节活动按官方时长结束）、7 天主轮换、季节阶段
            //   在同一进程里永不推进（要重启才生效）。
            var slotKey = Rotation.CurrentSlot() + ":" + (TodayOverride ?? DateTime.Today).ToString("yyyy-MM-dd");
            bool forceChanged = !_forceInited || w != _forceWrite
                                || !string.Equals(id, _lastRawForceId, StringComparison.Ordinal);
            bool slotChanged = !_forceInited || !string.Equals(slotKey, _lastSlotKey, StringComparison.Ordinal);
            if (!forceChanged && !slotChanged) return;

            _forceInited = true;
            _forceWrite = w;
            _lastRawForceId = id;
            _lastSlotKey = slotKey;
            ForcedId = id;
            SelectActive(log);
        }
        catch { }
    }

    /// <summary>
    /// ★ v1.3.8【P0 热生效】网页面板保存后调这个：只重读 config.json（events.json 不动），
    /// 把 rotation / seasonRotation / rewardRotation / mapFillers / logLevel 全部重新灌进 static，
    /// 然后让 force 判据失效、重算一次当前活动。返回当前生效的活动 id（逗号分隔）。
    /// <para>⚠ 进程级的东西（枚举预补丁、启动时写过 BotTable 的维度）改不了，仍需重启。</para>
    /// </summary>
    public static string ReloadSettingsFromDisk(ISptLogger<EventRotatorServer> log = null)
    {
        try
        {
            if (string.IsNullOrEmpty(ModPath)) return "mod 目录未初始化";
            bool wasDisabled = DisableAllEvents;
            LoadConfig(ModPath, log ?? Log);
            // ★★★ v2.7.0【硬开关 ③】开关状态一变（面板勾/取消，或手改 config.json 后点保存）
            //   ⇒ **立刻做一次「完整还原 + 按新状态套用」**，否则要等到下一局进图才生效：
            //     · 打开：RestoreThenApply 会走硬还原分支（DB/战利品/天气/感染/Blackout 全部还原）；
            //     · 关闭：RestoreThenApply 立刻按正常轮换逻辑重新套用（= 恢复正常）。
            //   ⚠ 只在"真的变了"时调，避免每次保存都触发一整轮还原+套用。
            bool toggled = wasDisabled != DisableAllEvents;
            ForceRecheck(log ?? Log);
            if (toggled)
            {
                try { OnRefresh?.Invoke(); }
                catch (Exception ex) { log?.Warning("[EventRotator] disableAllEvents 切换后重新套用失败（已忽略）: " + ex.Message); }
            }
            try { StatusWriter?.Invoke(Actives); } catch { }
            var ids = Actives.Select(a => a.Id).ToList();
            return ids.Count == 0 ? "（当前没有活动生效）" : string.Join(",", ids);
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    /// <summary>
    /// ★ v1.3.8【P1 立即重新应用】不读盘，直接按内存里的配置重算 Actives 并刷新下发状态。
    /// 客户端面板轮询 <c>/eventrotator/status</c> ⇒ 下一次轮询就会显示新的活动列表。
    /// <para>表级效果（bot 表 / 掉落）多数是**进图瞬间**才读 Actives 的 ⇒ 下一次进图生效，不会破坏进行中的局。</para>
    /// </summary>
    public static string ReapplyNow(ISptLogger<EventRotatorServer> log = null)
    {
        try
        {
            // ★ 先让 force.txt 判据失效、重读一次（F12/外部工具直接改 force.txt 时也能吃到），
            //   再刷新下发状态；否则"立即重新应用"只重算轮换、漏掉刚写的强制活动。
            ForceRecheck(log ?? Log);
            try { StatusWriter?.Invoke(Actives); } catch { }
            var ids = Actives.Select(a => a.Id).ToList();
            return ids.Count == 0 ? "（当前没有活动生效）" : string.Join(",", ids);
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    /// <summary>让 force.txt 的"文件没变就跳过"判据失效，强制再读一次并重算。</summary>
    public static void ForceRecheck(ISptLogger<EventRotatorServer> log = null)
    {
        _forceInited = false;
        RefreshFromForce(log ?? Log);
    }

    /// <summary>
    /// ★ v1.3.8 状态文本写出回调（由 Plugin 在启动时注入 <c>WriteActiveStatus</c>）。
    /// 热应用之后要靠它把「当前生效活动.txt」和 HTTP 快照一起刷新，
    /// 否则网页显示"已应用"、客户端面板还是旧的。
    /// </summary>
    public static Action<List<EventDef>> StatusWriter;

    /// <summary>
    /// ★ v0.1.4 判断是不是"历年变体"（halloween_2022 / santa_2023 / halloween_infected / khorovod_2024 等）。
    /// 自动轮换只用**标准那个**季节活动，变体留给手动选择，避免"同一年开两个万圣节"。
    /// </summary>
    public static bool IsYearVariant(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var s = id.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(s, @"_(19|20)\d{2}$")
               || s.EndsWith("_infected")
               || s.StartsWith("khorovod");
    }

    /// <summary>★ v0.1.4 手动多开时的冲突原因（人话）。null = 不冲突。</summary>
    /// <summary>
    /// ★ v1.3.8【强制冲突检查】给一组活动 id，返回两两冲突的描述（空 = 不冲突）。
    /// 供网页面板在"手动强制多个活动"时提醒用 —— 强制会绕过轮换的避冲突闸，
    /// 但两个改同一批刷点/boss 的活动同时开，后写的会覆盖前一个。
    /// </summary>
    public static List<string> DescribeConflicts(IEnumerable<string> ids)
    {
        var res = new List<string>();
        try
        {
            var list = (ids ?? Enumerable.Empty<string>())
                .Select(id => All.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Where(e => e != null).Distinct().ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var why = ConflictReason(list[i], list[j]);
                    if (why != null) res.Add($"{list[i].NameZh} × {list[j].NameZh}：{why}");
                }
        }
        catch { }
        return res;
    }


    /// <summary>
    /// ★ 2026-10-31【邪教之夜 vs 僵尸万圣节】显式互斥判定（任一侧声明即算）。
    /// 与 <see cref="Rotation.Conflicts"/> 的区别：那条看"刷点交集/季节首领/跳蚤门槛"，
    /// 这条看**用户显式写的互斥表** —— 用于"刷的东西不同、但语义上不该同时出现"的组合。
    /// </summary>
    public static bool MutuallyExcluded(EventDef a, EventDef b)
    {
        try
        {
            if (a == null || b == null) return false;
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.MutualExcludeWith != null && a.MutualExcludeWith.Any(x => string.Equals(x, b.Id, StringComparison.OrdinalIgnoreCase))) return true;
            if (b.MutualExcludeWith != null && b.MutualExcludeWith.Any(x => string.Equals(x, a.Id, StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }
        catch { return false; }
    }

    public static string ConflictReason(EventDef a, EventDef b)
    {
        try
        {
            if (a == null || b == null) return null;
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return "同一个活动";

            if (!string.IsNullOrEmpty(a.ForceSeasonalBossKey) && !string.IsNullOrEmpty(b.ForceSeasonalBossKey)
                && !string.Equals(a.ForceSeasonalBossKey, b.ForceSeasonalBossKey, StringComparison.OrdinalIgnoreCase))
                return $"两者要开【不同的季节首领】（{a.ForceSeasonalBossKey} vs {b.ForceSeasonalBossKey}），本体同一时刻只能有一个季节首领表";

            var ta = Rotation.TouchSetPublic(a);
            var tb = Rotation.TouchSetPublic(b);
            var shared = ta.Intersect(tb, StringComparer.OrdinalIgnoreCase).ToList();
            if (shared.Count > 0)
            {
                var sample = shared.Take(3).Select(s =>
                {
                    var p = s.Split('|');
                    var map = p.Length > 0 ? Rotation.MapZh(p[0]) : "?";
                    var boss = p.Length > 1 ? Rotation.BossZh(p[1]) : "?";
                    var raw = p.Length > 1 ? p[1] : "";
                    return string.IsNullOrEmpty(raw) || string.Equals(raw, boss, StringComparison.OrdinalIgnoreCase)
                        ? $"{map} · {boss}"
                        : $"{map} · {boss}（{raw}）";
                }).ToList();
                var more = shared.Count > sample.Count ? $" 等 {shared.Count} 处" : "";
                return $"两个活动都会改同一批首领刷点：{string.Join("；", sample)}{more} —— 同时开启时后写入的那份会覆盖前一份";
            }
        }
        catch { }
        return null;
    }

    /// <summary>★ v0.1.4 找出与给定活动集合冲突的配对（自动轮换用）。</summary>
    public static List<string> ConflictPairs(List<EventDef> list)
    {
        var res = new List<string>();
        try
        {
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var why = ConflictReason(list[i], list[j]);
                    if (why != null) res.Add($"{list[i].NameZh} × {list[j].NameZh}：{why}");
                }
        }
        catch { }
        return res;
    }

    public static void SelectActive(ISptLogger<EventRotatorServer> log)
    {
        try
        {
            // ═══════════════════════════════════════════════════════════════════════════
            // ★★★ v2.7.0【硬开关 ②· 最高优先级】`disableAllEvents = true`
            //   ⇒ **什么都不选**：不读 force.txt、不做日期窗、不做季节阶段自动接管、
            //     不跑主轮换、不跑奖励轮换、不跑周抽签。
            //   这是本方法**第一个**分支（在所有其它判据之前）⇒ 任何自动补活动的路径都过不来，
            //   直接兑现用户原话「开启了这个开关之后就什么活动都不会开」。
            //   ⚠ 这里只清列表，真正把改动还原回去的是 `Plugin.RestoreThenApply()` 的硬还原分支
            //     （它随后会被 `ReloadSettingsFromDisk` / 进图 / 启动调用）。
            // ═══════════════════════════════════════════════════════════════════════════
            if (DisableAllEvents)
            {
                bool had = Actives.Count > 0;
                Actives.Clear();
                ForcedId = "";            // 手动强制层一并作废（连"坏 id 回落自动"都别走）
                if (had && log != null)
                    log.Info("[EventRotator] disableAllEvents=true ⇒ 当前生效活动集合已清空（不套用任何活动）");
                return;
            }

            var prevIds = string.Join(",", Actives.Select(a => a.Id));
            Actives.Clear();

            // ① force.txt 手动强制（★ 支持逗号分隔 = 同时开多个活动）
            //    ★ v2.0.9：支持 durationDays —— 手动强制的活动到期后自动结束（不再永久生效）
            if (!string.IsNullOrEmpty(ForcedId) && ForcedId != "none")
            {
                if (Rotation.ForcedExpired(ForcedId))
                {
                    if (log != null) log.Warning("[EventRotator] 手动强制的活动已到期 → 本次不强制（活动结束）");
                }
                else
                {
                    foreach (var raw in ForcedId.Split(new[] { ',', ';', '|', ' ', (char)9, (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var id = raw.Trim();
                        if (id.Length == 0) continue;
                        var hit = All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                        if (hit != null && !Actives.Contains(hit)) Actives.Add(hit);
                        else if (hit == null && log != null) log.Warning($"[EventRotator] forced id '{id}' not found");
                    }

                    // ★ v1.6.5【服务端审计 #8】这条计数移到"整组展开之后"，见下方。

                    // ★★★ v1.1.2【BugFix·强制残留把一切都关掉】force.txt 里若有**已经不存在的 id**
                    //   （旧活动被删/改名，或玩家在旧版面板勾过、文件留下），旧代码照样 `return`
                    //   ⇒ Actives 为空 ⇒ 日期窗和轮换都不跑 ⇒「服务端正常但一个活动都没有」。
                    //   现在：一个都没匹配上 ⇒ 告警并**回落到自动选择**。
                    // ★★★ v1.6.1【回归审计 #3 修】原来这里是 `if (空) 告警; else return;`
                    //   而"整组展开 + 手动冲突提示"写在它**下面** ⇒ 只要 force.txt 里有有效 id 就直接 return，
                    //   展开/提示全成死代码（用户 F12 勾选是主要操作方式，勾 christmas 不再带出 newyear）。
                    //   现在：非空 ⇒ 展开 + 冲突提示 + return；为空 ⇒ 告警并**落到下面的自动选择**。
                    if (Actives.Count == 0)
                    {
                        log?.Warning($"[EventRotator] force.txt 里的 id 全部无效（{ForcedId}）⇒ 忽略手动强制，回落到自动选择");
                        // ★ v1.6.5【服务端审计 #1 修】回落后必须清空 ForcedId，
                        //   否则下游（TryForceSeasonal / ApplyEventSeason / 状态文件）继续按"手动模式"处理：
                        //   原生季节强推被跳过、来源写成"手动强制（坏id）"。
                        ForcedId = "";
                    }

                    // ★★★ v0.1.5【组合式】手动强制同样"整组展开"（官方活动是组合式的：强制任一成员 ⇒ 带出同年同族）
                    //    ⚠ v2.6.3 修复：这段原来被误放进下面的 `if (Actives.Count > 1)` 多开分支里
                    //      ⇒ **只强制 1 个活动时完全不展开**（实测：force.txt=halloween 时
                    //        日志只有「组合展开：1 → 1 个活动 [halloween]」，night_of_the_cult 没被带出）。
                    //      展开是"补成员"，与"开几个"无关 ⇒ 必须先无条件展开，再做冲突提示。
                    try
                    {
                        // ★ v2.6.41【Lead 裁决】手动强制路径 ⇒ 走 Forced 版 ⇒ **不做日期窗过滤**（回归保护 R2）
                        foreach (var x in ExpandFamilyCombosForced(Actives, All))
                            if (!Actives.Contains(x)) Actives.Add(x);
                    }
                    catch (Exception ex2) { log?.Warning("[EventRotator][组合] 展开失败: " + ex2.Message); }

                    // ★★★ v0.1.4 手动多开：**列出冲突详情但不阻止**（方便你自己做测试）
                    //   之前手动强制完全跳过冲突检查，多开时不知道会打架；现在逐对检查并给出人话解释。
                    //   ★ v2.6.3：冲突检查放在**展开之后** ⇒ 展开带出来的成员造成的冲突也能被报出来
                    //     （原来单成员强制根本不展开，这些冲突永远看不到）。
                    if (log != null && Actives.Count > 1)
                    {
                        var shown = 0;
                        for (int i = 0; i < Actives.Count; i++)
                            for (int j = i + 1; j < Actives.Count; j++)
                            {
                                var why = ConflictReason(Actives[i], Actives[j]);
                                if (why == null) continue;
                                shown++;
                                log.Warning($"[EventRotator] ⚠ 冲突：『{Actives[i].NameZh}』×『{Actives[j].NameZh}』 —— {why}"
                                            + "（手动强制不阻止，仅提示；自动轮换会避开这种组合）");
                            }
                        if (shown == 0)
                            log.Info($"[EventRotator] ✔ 手动多开的 {Actives.Count} 个活动之间没有冲突（可放心一起开）");
                    }
                    // ★ v1.6.5【服务端审计 #8】FORCED 计数必须在"同族整组展开 + 冲突提示"之后打印，
                    //   否则日志写"共 1 个"，下一行展开成 4 个，自相矛盾。
                    if (log != null && Actives.Count > 0)
                        log.Warning($"[EventRotator] FORCED event(s) = {string.Join(",", Actives.Select(a => a.Id))}"
                                  + $"  （共 {Actives.Count} 个同时生效，已含同族展开）");

                    if (Actives.Count > 0) return;   // ★ v1.6.1 只有"真的选到活动"才结束；空集继续走自动
                }
            }

            // ② 按日期窗自动选择（★ 收集**全部**命中项，而不是第一个）
            //    ★ v0.1.3：原来这里还判 `e.Enabled`，但该字段在**轮换路径**上不被读取，
            //    造成"设成 false 却照样被轮换选中"的不一致，已把字段整体删除。
            //    现在规则单一无歧义：**日期窗参与判定时 → 按现实日期自动开**。
            // ★ v2.6.42：`now` 也走 `TodayOverride`（默认 null ⇒ `DateTime.Now`，**生产路径不变**）
            //   ⇒ 让"窗口重叠 ⇒ 按顺序取 1 个"这条能被自检/探针用**固定日期**确定复现
            var now = TodayOverride ?? DateTime.Now;
            var inWindow = new List<EventDef>();

            // ═══════════════════════════════════════════════════════════════════════════
            // ★★★ 2026-10-29【第 22 项 · 用户裁决 B】日期窗**只在「跟随现实日期」下生效**
            //   用户原话：「日期窗只有我们没开限定季节轮换时间的时候才会生效」。
            //   · `dateWindowRule = seasonOffOnly`（默认）：只有 `seasonRotation.mode == "off"`
            //     （界面上的「跟随现实日期」）时日期窗才参与判定；rotate / followEvent / fixed
            //     一律**不看**日期窗（活动完全由轮换 + 季节阶段决定）。
            //   · `dateWindowRule = always`：日期窗**总是**参与（= 一键回退到旧行为）。
            //   ⚠ 被替换掉的旧逻辑（v1.5.1 P0-2）：只判 `SeasonRotation.IsRotating` ⇒
            //     followEvent / fixed 下仍按现实日期开活动 —— 这正是用户这次要关掉的。
            //   ⚠ 不受本规则影响（刻意不动）：`force.txt` 手动强制（本方法更早处的 ① 分支已 return）、
            //     奖励轮换、季节轮换/季节阶段、主轮换、周抽签、`enableSeasonalEventDetection` 接管。
            // ═══════════════════════════════════════════════════════════════════════════
            bool dateWindowOn = DateWindowActive;

            // 日期窗命中集：判据与改前**逐字一致**（同年份变体跳过 + 带任务窗口重叠只留 1 个）。
            // 日期窗被规则关掉时也跑一遍（verbose=false），只为算出"本可生效 N 个"给一条汇总日志；
            // 这样"本可生效"与"真生效"永远同一套算法，不会各写一份漂移。
            List<EventDef> CollectWindowHits(bool verbose)
            {
                var hits = new List<EventDef>();
                foreach (var e in All)
                    // ★★★ v0.1.5【同年份才组合】：默认 seasonAutoPick="newest" 时，**同族的往年变体不自动开**
                    //   （否则 10/28 会同时冒出 halloween + halloween_2022 + halloween_2023 三个万圣节 ✗）
                    //   变体留给手动（force.txt / F12 勾选）；想恢复旧行为：config 里 seasonAutoPick="all"
                    if (InWindow(e, now))
                    {
                        if (IsOutdatedYearVariant(e, All))
                        {
                            if (verbose)
                                Log?.Info($"[EventRotator] 日期窗命中 '{e.Id}'（{e.FamilyYear} 场次）但同族已有更新场次"
                                         + " ⇒ 自动模式跳过（seasonAutoPick=newest；如需开启请手动强制或改 config）");
                            continue;
                        }
                        // ★ v1.6.0【S8】以前对"没有 family 的 yearVariant"（如 softcore）也会印
                        //   "这是同族最新场次（）"，括号里空着、误导。只有真的有族+年份才打这句。
                        if (verbose && e.YearVariant == true && !string.IsNullOrEmpty(e.Family) && e.FamilyYear.HasValue)
                            Log?.Info($"[EventRotator] 日期窗命中 '{e.Id}'：这是同族**最新**场次（{e.FamilyYear}）⇒ 按窗口正常开启");

                        hits.Add(e);
                    }

                // ★★★ v2.6.42【用户规格 ③】**带任务的窗口重叠 ⇒ 只开 1 个**
                //   用户原话：「**同时要是覆盖时间相同按照顺序来开启**」+ ⑥「带限时任务的活动轮换到了之后
                //   **只开启一个**带任务的」
                //   顺序（Lead 裁决，**两级、都不写死 id、不许随机**）：
                //     ① **窗口开始早的优先**（startMonth/startDay 升序）—— 语义 = "先到先得"，可解释
                //     ② **同一天开始的 ⇒ 按 `events.json` 的声明顺序**（`All` 列表的天然顺序）—— 稳定可复现
                var tasksInWindow = hits.Where(x => string.Equals(x.Category, "event_task", StringComparison.OrdinalIgnoreCase)).ToList();
                if (tasksInWindow.Count > 1)
                {
                    var winner = tasksInWindow
                        .OrderBy(x => x.StartMonth * 100 + x.StartDay)
                        .ThenBy(x => { int i = All.FindIndex(y => ReferenceEquals(y, x) || y.Id == x.Id); return i < 0 ? int.MaxValue : i; })
                        .First();
                    if (verbose)
                    {
                        var rest = string.Join(" / ", tasksInWindow.Where(x => !ReferenceEquals(x, winner))
                                                    .Select(x => $"{x.Id} {x.StartMonth}/{x.StartDay}"));
                        Log?.Info($"[轮换] 窗口重叠 {tasksInWindow.Count} 个 ⇒ 按\"开始早优先\"取 '{winner.Id}'（其余：{rest}）");
                    }
                    foreach (var t in tasksInWindow) if (!ReferenceEquals(t, winner)) hits.Remove(t);
                }
                return hits;
            }

            if (dateWindowOn)
                inWindow = CollectWindowHits(true);
            else
                LogDateWindowSkipped(log ?? Log, CollectWindowHits(false));   // ★ 只打**一条**汇总日志（同类去重）

            foreach (var e in inWindow) Actives.Add(e);

            // ②b ★★★ v0.1.4 季节活动阶段：季节轮换时，按"季节内阶段"自动开对应季节活动
            //     （config: seasonRotation.eventPhase = off / start / middle / end）
            //     例如 30 天季节 + 万圣节官方 13 天 + phase=middle → 第 18 天开始自动开万圣节。
            //     ★★ 用户要求：**同一个季节里有多个季节活动变体，轮换要在同季节内切**。
            //       实测：Halloween 4 个（halloween / _2024 / _2023 / _2022）、
            //             Christmas 6 个（christmas / khorovod_2024 / kolotun / santa_2023~2021）、
            //             新年 1 个、愚人节 1 个。
            //       现在由 SeasonRotation.PickSeasonalVariant 按"第几次轮到该季节"轮流取变体。
            try
            {
                var dueSeason = SeasonRotation.EventSeasonDueNow();
                if (!string.IsNullOrEmpty(dueSeason))
                {
                    var pick = SeasonRotation.PickSeasonalVariant(All);
                    if (pick == null)
                    {
                        // 兜底：没算出变体时退回"主活动"
                        pick = All.Where(e => string.Equals(e.SeasonalEvent, dueSeason, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(e => e.Id, StringComparer.Ordinal)
                                  .FirstOrDefault();
                    }
                    if (pick != null && Rotation.IsExcluded(pick.Id))
                    {
                        // ★ v1.3.8【轮换排除】季节阶段想自动开的这个活动被用户排除了 ⇒ 跳过并留痕
                        Log?.Info($"[EventRotator][轮换排除] 季节阶段本应自动开启『{pick.Id}』，但它被 rotation.exclude 排除 ⇒ 跳过");
                        pick = null;
                    }
                    if (pick != null && !Actives.Contains(pick))
                    {
                        Actives.Add(pick);
                        // ★ 记账：真正开过之后才把变体标记为"用过了"（下次随机不会重复抽到）
                        // ★ v1.5.0【C2】变体选择改成"按季节命中次数"的纯函数后，记账文件不再参与决策，
                        //   这里不再写 seasonal_state.json（避免留下一个"看着有用其实没用"的文件）。
                        // ★ v1.6.5【服务端审计 #6】改用与选择逻辑**同一个环**（以前这里用家族全集，
                        //   于是"共 3 个…固定取[2]"下一行又写"共 4 个…第 4 个"，自相矛盾）。
                        var variants = SeasonRotation.SeasonalRingOf(dueSeason, All);
                        if (log != null)
                        {
                            log.Warning($"[EventRotator] ★季节活动阶段已到（{SeasonRotation.EventPhase}）→ 自动开启『{pick.NameZh}』"
                                        + $"（本季 {SeasonRotation.CurrentSeason()}，进度 {SeasonRotation.SeasonProgress() * 100:0}%）");
                            if (variants.Count > 1)
                                log.Info($"[EventRotator]   本季共有 {variants.Count} 个 {dueSeason} 变体，本次按顺序取到第 "
                                         + $"{variants.FindIndex(x => x.Id == pick.Id) + 1} 个：{string.Join(" → ", variants.Select(v => v.Id))}"
                                         + "（同季节反复出现时会**逐个切换**，不再永远只开主活动）");
                        }
                    }
                }
            }
            catch (Exception ex) { log?.Warning("[EventRotator] 季节活动阶段处理失败: " + ex.Message); }

            // ★★★ v0.1.5【组合式（用户要求）】官方活动是**组合式**的（一个场次 = 一组活动）⇒
            //   先把选中的活动按"同族 + 同年份"整组展开（补上该族通用成员如 christmas/newyear），
            //   **然后**才跑填充轮换 ⇒ 填充轮换才能看到完整的官方组合，
            //   从而满足"有季节/带任务活动在跑时不轮换出别的活动"（只叠奖励）。
            try
            {
                // ★ v2.6.40/41【自动轮换路径】**保留日期窗过滤**（manual 默认 false）
                foreach (var x in ExpandFamilyCombos(Actives, All))
                    if (!Actives.Contains(x)) Actives.Add(x);
            }
            catch (Exception ex) { log?.Warning("[EventRotator][组合] 展开失败: " + ex.Message); }

            // ③ ★ 季节活动之间的"填充"：按间隔轮换（详见 Rotation.cs，含自动冲突保护）
            foreach (var f in Rotation.PickForToday(All, Actives))
                if (!Actives.Contains(f)) Actives.Add(f);

            // ④ ★ v2.2.0 奖励类轮换：从 category=reward 的活动里随机开 N~M 个（只改玩家侧，可叠加）
            foreach (var f in RewardRotation.PickForToday(All, Actives))
                if (!Actives.Contains(f)) Actives.Add(f);


            // ★ v1.3.2【空结果诊断】一个活动都没有时，把"为什么"直接写进日志 ——
            //   以前这种情况**完全静默**（只有 Info 级、且只在集合变化时打），
            //   玩家看到面板空白时无从判断是"锁死了"还是"池子空"。
            // ★★★ v1.6.1【R6/R7 修正】收敛必须在**奖励轮换之后**执行，
            //   否则"奖励上限"永远只裁剪轮换前的集合，后面又加回来的奖励不受约束（v1.6.0 的位置写错了）。
            //   同时**保护手动强制（force.txt）的活动**：玩家明确要的，不该被自动收敛删掉。
            try
            {
                var forced = (EventEngine.ForcedId ?? "")
                    // ★ v1.6.5【服务端审计 #5】SelectActive 支持 ',;| ' 作分隔符，这里必须一致，否则 '|' 分隔的手动 id 失保护
                    .Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool IsForced(EventDef x) => forced.Contains(x.Id);

                bool hasAi = Actives.Any(x => Rotation.IsAiEvent(x) && !IsForced(x));
                bool hasTask = Actives.Any(x => Rotation.IsTaskEvent(x) && !IsForced(x));
                if (hasAi && hasTask)
                {
                    int aiIdx = Actives.FindIndex(x => Rotation.IsAiEvent(x) && !IsForced(x));
                    int taskIdx = Actives.FindIndex(x => Rotation.IsTaskEvent(x) && !IsForced(x));
                    bool dropAi = taskIdx >= 0 && aiIdx > taskIdx;
                    var removed = Actives
                        .Where(x => !IsForced(x) && (dropAi ? Rotation.IsAiEvent(x) : Rotation.IsTaskEvent(x)))
                        .Select(x => x.Id).ToList();
                    Actives.RemoveAll(x => !IsForced(x) && (dropAi ? Rotation.IsAiEvent(x) : Rotation.IsTaskEvent(x)));
                    if (removed.Count > 0)
                        Log?.Warning($"[EventRotator] ⚠ 统一收敛（AI×带任务互斥）：移除了 {string.Join(",", removed)}"
                                   + $"（保留先到的 {(dropAi ? "带任务" : "AI")}；手动强制的未动）");
                }

                // ★ v1.6.5【服务端审计 #5】用户关掉「奖励类轮换」时不该再裁剪日期窗自带的奖励活动
                int cap = RewardRotation.Enabled ? Math.Max(0, RewardRotation.CountMax) : int.MaxValue;
                var rewardsNow = Actives.Where(RewardRotation.IsReward).ToList();
                if (rewardsNow.Count > cap)
                {
                    // 手动强制的奖励优先保留
                    var keep = rewardsNow.Where(IsForced).Select(x => x.Id)
                        .Concat(rewardsNow.Where(x => !IsForced(x)).Select(x => x.Id))
                        .Take(cap).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var dropped = Actives
                        .Where(x => RewardRotation.IsReward(x) && !keep.Contains(x.Id))
                        .Select(x => x.Id).ToList();
                    Actives.RemoveAll(x => RewardRotation.IsReward(x) && !keep.Contains(x.Id));
                    if (dropped.Count > 0)
                        Log?.Warning($"[EventRotator] ⚠ 统一收敛（奖励上限 {cap}）：移除了 {string.Join(",", dropped)}");
                }
            }
            catch (Exception ex) { Log?.Warning("[EventRotator] 收敛失败（已忽略）: " + ex.Message); }

            if (log != null && Actives.Count == 0)
            {
                try
                {
                    var reasons = new List<string>();
                    if (!Rotation.Enabled) reasons.Add("主轮换 enabled=false");
                    else if (Rotation.OnlyWhenNoSeasonal && Actives.Any(Rotation.IsSeasonal))
                        reasons.Add("仅在季节间隙轮换=true 且已有季节活动在生效 ⇒ 主轮换暂停");
                    else if (SeasonRotation.IsRotating)
                        reasons.Add("季节轮换已开启（rotate）⇒ 活动完全由轮换/季节阶段决定，而本轮轮换池没有可选项");
                    // ★ 第 22 项：日期窗被规则关掉时把"为什么日期窗没开活动"写清楚
                    else if (!dateWindowOn)
                        reasons.Add($"日期窗按 dateWindowRule={DateWindowRule} 跳过（当前季节模式={SeasonRotation.Mode}）"
                                    + "⇒ 只看轮换/季节阶段，而本轮没有可选项");
                    else reasons.Add("主轮换候选池为空（当日无可轮换活动）");
                    if (!RewardRotation.Enabled) reasons.Add("奖励轮换 enabled=false");
                    log.Warning("[EventRotator] ⚠ 本轮没有任何活动生效｜原因：" + string.Join("；", reasons)
                        + $"｜日期窗命中={inWindow.Count} 个｜主轮换={Rotation.Enabled} 仅季节间隙={Rotation.OnlyWhenNoSeasonal}"
                        + $"｜奖励轮换={RewardRotation.Enabled}({RewardRotation.CountMin}~{RewardRotation.CountMax})");
                }
                catch { }
            }

            if (log != null)
            {
                var nowIds = string.Join(",", Actives.Select(a => a.Id));
                if (!string.Equals(prevIds, nowIds, StringComparison.Ordinal))
                    log.Info(Actives.Count == 0
                        ? "[EventRotator] no event active today"
                        : $"[EventRotator] active event(s) = {nowIds}  （共 {Actives.Count} 个）");
            }
        }
        catch (Exception ex) { log?.Error($"[EventRotator] select failed: {ex}"); }
    }

    /// <summary>
    /// ★ v1.3.9【D3 修】判断某个 `yearVariant` 活动是不是「**过期的**往年场次」。
    /// <para>判据：同族里存在**更新年份**的成员 ⇒ 旧场次淘汰；同族最新年份 ⇒ **保留**（可正常开/进池）。</para>
    /// <para>为什么需要它：以前"往年变体"是一刀切（`YearVariant == true` 就拦），
    /// 结果 `night_of_the_cult`（2024 = halloween 族最新年份）在自己的官方窗口 10/11–10/24 里
    /// **永远开不出来**：日期窗路径拦它、季节阶段按 seasonalEvent 过滤（它为空）、组合展开又要 2024 种子先开。</para>
    /// <para>`seasonAutoPick = "all"` 时完全不拦（旧行为）；没有 `family` 的变体也不拦。</para>
    /// </summary>
    public static bool IsOutdatedYearVariant(EventDef e, List<EventDef> all)
    {
        try
        {
            if (e == null || e.YearVariant != true) return false;
            if (string.Equals(SeasonAutoPick, "all", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(e.Family)) return false;
            int myYear = e.FamilyYear ?? 0;
            int newestYear = 0;
            foreach (var x in all ?? new List<EventDef>())
                if (x != null && string.Equals(x.Family, e.Family, StringComparison.OrdinalIgnoreCase)
                    && (x.FamilyYear ?? 0) > newestYear)
                    newestYear = x.FamilyYear ?? 0;
            return newestYear > myYear;
        }
        catch { return false; }
    }

    /// <summary>
    /// ★★★ v0.1.5【组合式展开（用户要求）】官方活动是**组合式**的：一个场次 = 一组活动。
    /// 给定已选中的活动集合，补上"**同族 + 同年份**"的其它成员，以及该族的**通用成员**
    /// （本体季节窗，每年都开，如 christmas / newyear）。
    /// 纯函数 ⇒ 自检可直接调用验证。
    /// </summary>
    /// <summary>
    /// ★ 组合展开 —— **自动轮换路径**（`EventEngine.cs:494`）。全库**唯一**叫这个名字的公开方法
    /// （自检 `selftest:1073` 用 `GetMethod("ExpandFamilyCombos", Public|Static)` + **2 参**调用 ⇒
    ///  **绝不能加同名的其它公开重载**，否则 `AmbiguousMatchException` / 参数数不匹配，自检会崩 ✗）。
    ///
    /// <para><b>v2.6.40【用户裁决 ⑨-2】日期窗过滤</b>：用户原话「未指定季节或者未开启季节轮换时
    /// 按照现实时间来，开启季节轮换后按实际选择来」⇒「按实际选择来」时**不过滤**，否则只展开
    /// "今天确实落在它自己窗口内"的成员。</para>
    /// <para>⚠ <b>手动强制走 <see cref="ExpandFamilyCombosForced"/></b>（Lead 裁决 R2：手动**不过滤**）。</para>
    /// <para>⚠ 可降级：拿不到季节模式/日期 ⇒ **照旧整组展开**（保持现状）+ warning，绝不静默不展开。</para>
    /// </summary>
    public static List<EventDef> ExpandFamilyCombos(List<EventDef> actives, List<EventDef> all)
    {
        try
        {
            var byChoice = SeasonRotation.ExpansionFollowsSeasonChoice();
            // ★★★ 2026-10-29【第 22 项】日期窗被规则关掉时（seasonOffOnly + 非 off 模式），
            //   组合展开也**不再按窗口过滤** —— 否则同族成员仍会被日期窗挡掉，等于"日期窗还在起作用"，
            //   与用户裁决「其它模式一律不看日期窗」自相矛盾。
            //   ⚠ `byChoice == null`（拿不到季节模式）时下面照旧走"不过滤"的降级分支。
            bool noFilter = (byChoice ?? false) || !DateWindowActive;
            if (byChoice == null)
            {
                Log?.Warning("[EventRotator][组合] ⚠拿不到季节模式 ⇒ **照旧整组展开**（本次不做日期窗过滤）");
                return ExpandFamilyCombosCore(actives, all, TodayOverride ?? DateTime.Today, true, false);
            }
            return ExpandFamilyCombosCore(actives, all, TodayOverride ?? DateTime.Today, noFilter, false);
        }
        catch (Exception ex)
        {
            Log?.Warning("[EventRotator][组合] ⚠窗口过滤判定失败 ⇒ **照旧整组展开**（本次不做过滤）: " + ex.Message);
            try { return ExpandFamilyCombosCore(actives, all, DateTime.Today, true, false); }
            catch { return new List<EventDef>(actives ?? new List<EventDef>()); }
        }
    }

    /// <summary>
    /// ★★★ v2.6.41【Lead 裁决 · 回归保护 R2】**手动强制路径**（`force.txt` / F12 勾选）⇒ **不做日期窗过滤**。
    /// <para><b>为什么</b>（Lead 给的三条，均可查）：</para>
    /// <list type="number">
    /// <item>用户**已实测成功**的行为就是手动 force 的连带
    ///   （日志 `[组合] 同族整组展开：1 → 5 个（新增 christmas,newyear,cultist_pilgrimage,christmas_january）`）
    ///   ⇒ 过滤它 = **弄坏已测成功的**（回归基线 A 组）✗</item>
    /// <item>用户原话语境是**自动轮换**（「有没有因为组合活动**命中了规则**导致没法开启的」）
    ///   ⇒ 过滤的本意是防**自动**乱开，不是限制用户手动指定</item>
    /// <item>手动 = 用户明确要这个活动 ⇒ 连带展开**正是他的预期** ✔</item>
    /// </list>
    /// <para>⚠ 名字**故意不叫** `ExpandFamilyCombos`（避免与自动路径产生同名重载 ⇒ 自检崩）。</para>
    /// </summary>
    public static List<EventDef> ExpandFamilyCombosForced(List<EventDef> actives, List<EventDef> all)
    {
        try { return ExpandFamilyCombosCore(actives, all, TodayOverride ?? DateTime.Today, true, true); }
        catch (Exception ex)
        {
            Log?.Warning("[EventRotator][组合] ⚠手动展开失败（已忽略）: " + ex.Message);
            return new List<EventDef>(actives ?? new List<EventDef>());
        }
    }

    /// <summary>自检/干跑可注入"今天"（null = 用 <see cref="DateTime.Today"/>）。**只影响组合展开的窗口判定**。</summary>
    // ★ v1.6.5【服务端审计 #2/#4】force.txt 首调必跑 + 运行期按"档位/日期"推进
    private static bool _forceInited;
    private static string _lastRawForceId;
    private static string _lastSlotKey;

    public static DateTime? TodayOverride;

    /// <summary>
    /// ★ 纯函数核心（自检/探针可直接驱动）：<paramref name="bySeasonChoice"/> = true ⇒ **不做**日期窗过滤
    /// （= "按实际选择来"）；false ⇒ 只展开"今天在它自己窗口内"（或本来就没有日期窗）的成员。
    /// **"整组一起开"的语义不变** —— 只是把"窗口外/不该开的族"挡在展开之外。
    /// <paramref name="manual"/> **只影响日志措辞**。
    /// </summary>
    public static List<EventDef> ExpandFamilyCombosCore(List<EventDef> actives, List<EventDef> all,
                                                        DateTime today, bool bySeasonChoice, bool manual = false)
    {
        var res = new List<EventDef>(actives ?? new List<EventDef>());
        try
        {
            if (all == null || all.Count == 0) return res;
            var seeds = res.Where(e => !string.IsNullOrEmpty(e.Family)).ToList();
            if (seeds.Count == 0) return res;
            var skipped = new List<string>();
            foreach (var s in seeds)
            {
                foreach (var cand in all)
                {
                    if (cand == null || res.Contains(cand)) continue;
                    if (!string.Equals(cand.Family, s.Family, StringComparison.OrdinalIgnoreCase)) continue;
                    bool sameYear = s.FamilyYear != null && cand.FamilyYear != null && cand.FamilyYear == s.FamilyYear;
                    bool generic = cand.FamilyGeneric == true;
                    if (!sameYear && !generic) continue;

                    // ★ 2026-10-31 显式互斥（例：邪教之夜 ⇄ 僵尸万圣节）⇒ 不参与同族整组展开
                    if (MutuallyExcluded(s, cand))
                    {
                        skipped.Add($"{cand.Id}（显式互斥：与 {s.Id} 不同时开启）");
                        continue;
                    }

                    // ★ v2.6.40 日期窗过滤（只有"按现实时间"时才生效）
                    if (!bySeasonChoice && !InWindowOrTimeless(cand, today))
                    {
                        skipped.Add($"{cand.Id}（窗口外：今天 {today.Month}/{today.Day} 不在 "
                                  + $"{cand.StartMonth}/{cand.StartDay}~{cand.EndMonth}/{cand.EndDay}）");
                        continue;
                    }
                    res.Add(cand);
                }
            }
            if (res.Count != (actives?.Count ?? 0))
                Log?.Info($"[EventRotator][组合] 同族整组展开"
                        + (manual ? "（手动强制 ⇒ 不做日期窗过滤）" : "")
                        + $"：{actives.Count} → {res.Count} 个"
                        + $"（新增 {string.Join(",", res.Skip(actives.Count).Select(x => x.Id))}）");
            // ★ 反例必须能在日志里看出来（你/用户核对用）
            foreach (var line in skipped)
                Log?.Info($"[EventRotator][组合] 展开跳过 '{line}；季节模式={SeasonRotation.SeasonChoiceDesc()}）");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][组合] 展开失败: " + ex.Message); }
        return res;
    }

    /// <summary>
    /// ★★★ 2026-10-29【第 22 项】日期窗因 `dateWindowRule` 被跳过时的**唯一一条**汇总日志。
    /// <para>格式（Lead 指定）：<c>[EventRotator][日期窗] 当前季节轮换模式=rotate ⇒ 按 dateWindowRule=seasonOffOnly
    /// 跳过日期窗（本可生效 N 个：xxx,yyy…）</c></para>
    /// <para><b>为什么"只打一条"</b>：`SelectActive` 每档/每次重算都会跑，逐活动打日志会刷屏；
    /// 这里按 `(模式|规则|命中集)` 去重 —— 同样的结论只打第一次，命中集一变才会再打一条。</para>
    /// </summary>
    private static void LogDateWindowSkipped(ISptLogger<EventRotatorServer> log, List<EventDef> wouldBe)
    {
        try
        {
            var mode = (SeasonRotation.Mode ?? "").Trim();
            var ids = (wouldBe ?? new List<EventDef>()).Select(x => x.Id).ToList();
            var sig = mode + "|" + DateWindowRule + "|" + string.Join(",", ids);
            if (string.Equals(sig, _lastDateWindowSkipSig, StringComparison.Ordinal)) return;   // 同结论不重复刷
            _lastDateWindowSkipSig = sig;
            log?.Info($"[EventRotator][日期窗] 当前季节轮换模式={mode} ⇒ 按 dateWindowRule={DateWindowRule} 跳过日期窗"
                      + (ids.Count > 0
                            ? $"（本可生效 {ids.Count} 个：{string.Join(",", ids)}）"
                            : "（今日无窗口命中）"));
        }
        catch { }
    }

    /// <summary>
    /// "今天允许开"的判据：**本来就没有日期窗（`StartMonth&lt;=0`）= 常驻活动 ⇒ 永远允许**；
    /// 有窗 ⇒ 必须落在窗内。判不了 ⇒ 放行（可降级，宁多不少）。
    /// </summary>
    public static bool InWindowOrTimeless(EventDef e, DateTime today)
    {
        try
        {
            if (e == null) return false;
            if (e.StartMonth <= 0 || e.EndMonth <= 0) return true;   // 无窗 = 常驻
            return InWindow(e, today);
        }
        catch { return true; }
    }

    private static bool InWindow(EventDef e, DateTime now)
    {
        if (e.StartMonth <= 0 || e.EndMonth <= 0) return false;
        int cur = now.Month * 100 + now.Day, s = e.StartMonth * 100 + e.StartDay, t = e.EndMonth * 100 + e.EndDay;
        return s <= t ? (cur >= s && cur <= t) : (cur >= s || cur <= t);
    }
}
