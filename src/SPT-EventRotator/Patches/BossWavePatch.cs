using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;

namespace EventRotator.Patches;

/// <summary>
/// 首领刷新补丁（v1.6.4 修复版）。
///
/// ★ 1.6.3「加载卡转圈 / 进不去图」根因（用户客户端日志实证）：
///   System.ArgumentNullException at EFT.BossLocationSpawn.ParseDifficulty()
///     at BossSpawnScenario.Init(BossLocationSpawn[])
///     at Fika.Core.Main.GameMode.HostGameController..ctor
///   → "Local game matching failed"，raid 起不来。
///   原因：SPT 服务端模型的 CLR 属性名是 <c>BossDifficulty</c> / <c>BossEscortDifficulty</c>
///   （只有 JSON 名才叫 BossDifficult / BossEscortDifficult），1.6.3 反射写的是 JSON 名，
///   静默失败 → 追加条目的 BossDifficult = null
///   → 客户端 Enum.Parse(BotDifficulty, null) 抛异常，整局垮掉。
///
/// ★ 1.6.3「骷髅头不显示」根因：
///   EFT.UI.LocationInfoPanel.Set 的骷髅显隐条件是
///     Location.BossLocationSpawn.Any(x => x.ShowOnTarkovMap)（PvE 取 ShowOnTarkovMapPvE）
///   1.6.3 从不写这两个字段（可空 bool，克隆/新建后是 null）
///   → 即使首领加进去了，地图选择界面也不会出现骷髅。
///
/// ★ 本版做法：追加首领时优先**克隆一个真实存在的完整条目**（同图同名 → 他图同名 → 内置模板），
///   再覆盖 BossName / BossChance / 骷髅标记 / BossZone。字段全部是客户端认可的合法值。
/// </summary>
internal static class BossWavePatch
{
    // ★★★ v1.0.1【BugFix·全量审计#1】官方季节首领注入的**年份门控**。
    //   原来的致命缺陷：`Plugin.ApplyHalloweenSummonPolicy()` 把
    //   `seasonalEventConfig.EventBossSpawns["halloweensummon"]` 从**内存**里摘掉了，
    //   但本文件的 `SeasonalBossArray()` 是**直接从磁盘 seasonalevents.json 读原文**
    //   ⇒ 内存里摘了、磁盘还在 ⇒ 门控对 raid 注入**完全无效**
    //   ⇒ 玩「愚人节」(april_fools 也声明了 forceSeasonalBossKey=halloweensummon) 时
    //     照样刷出 2023 万圣节专属的扎里亚奇召唤仪式（含 laboratory 的 bossTagilla）。
    //   修法：由 `Plugin.RestoreThenApply()` 在每轮套用时推入"是否允许"，本文件据此跳过。
    public static bool AllowHalloweenSummon;

    /// <summary>★ v1.0.1 判断某个 forceSeasonalBossKey 的这张图是否该被跳过（年份门控 + 实验室护栏）。</summary>
    private static bool SkipSeasonalBossFor(string key, string mapKey, string evId, out string why)
    {
        why = null;
        if (!string.Equals(key, "halloweensummon", StringComparison.OrdinalIgnoreCase)) return false;
        // 官方语义：halloweensummon = Halloween 2023 Part 2 的 Zryachiy 召唤仪式 ⇒ 只有 halloween_2023 才允许
        if (!AllowHalloweenSummon || !string.Equals(evId, "halloween_2023", StringComparison.OrdinalIgnoreCase))
        {
            why = $"`halloweensummon` 是 2023 万圣节专属（当前活动 {evId} / 允许={AllowHalloweenSummon}）⇒ 跳过";
            return true;
        }
        // 官方说明里的硬约束：laboratory 永远摘掉
        if (string.Equals(mapKey, "laboratory", StringComparison.OrdinalIgnoreCase))
        {
            why = "laboratory 按官方说明永远摘掉 halloweensummon";
            return true;
        }
        return false;
    }
    private static ISptLogger<EventRotatorServer> _log;

    private static readonly Dictionary<string, string[]> Escorts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "bossBully", new[] { "followerBully" } },
        { "bossKilla", Array.Empty<string>() },
        { "bossTagilla", Array.Empty<string>() },
        { "bossGluhar", new[] { "followerGluharAssault", "followerGluharScout", "followerGluharSecurity" } },
        { "bossKojaniy", new[] { "followerKojaniy" } },
        { "bossSanitar", new[] { "followerSanitar" } },
        { "bossZryachiy", new[] { "followerZryachiy" } },
        { "bossKolontay", new[] { "followerKolontayAssault", "followerKolontaySecurity" } },
        { "bossBoar", new[] { "followerBoar", "followerBoarClose1", "followerBoarClose2" } },
        { "bossKnight", new[] { "followerBigPipe", "followerBirdEye" } },
        { "sectantPriest", new[] { "sectantWarrior", "sectantOni" } },
        // ★★★ v2.6.53【P2-#6 修复】把**0.16.9 逐条核对过**的 escort 类型补进表：
        //   原先表里没有这几种 ⇒ `FirstEscort()` 返回空 ⇒ `NewBossEntry` 只能硬编码 `sectantWarrior` 兜底
        //   ⇒ 正牌护卫不是邪教战士的 boss（如 `peacemaker`）会**丢护卫**（amount 被设 0）。
        //   证据（0.16.9 `database/locations/*/base.json` 逐条 dump）：
        //     · shoreline   peacemaker          escort=peacemaker          amount=2,2,3,3,3,4,4,5
        //     · lighthouse  crazyAssaultEvent   escort=crazyAssaultEvent   amount=2
        //     · bigmap      arenaFighterEvent   escort=arenaFighterEvent   amount=3,4
        //     · laboratory  pmcBot              escort=pmcBot              amount=1,1,1,1,2,2,2,…
        { "peacemaker", new[] { "peacemaker" } },
        { "crazyAssaultEvent", new[] { "crazyAssaultEvent" } },
        { "arenaFighterEvent", new[] { "arenaFighterEvent" } },
        { "pmcBot", new[] { "pmcBot" } },
    };

    public static void Enable(Harmony harmony, ISptLogger<EventRotatorServer> log)
    {
        _log = log;
        int ok = 0;

        // ★★ 主挂点（可靠）：LocationLifecycleService.GenerateLocationAndLoot
        //    SPT 里 MakeAdjustmentsToMap 是**条件调用**——
        //        RaidChanges raidAdjustments = ProfileActivityService.GetProfileActivityRaidData(sessionId)?.RaidAdjustments;
        //        if (raidAdjustments != null) raidTimeAdjustmentService.MakeAdjustmentsToMap(...);
        //    只有在"动态战局时间调整"存在时才会执行，所以进图时不一定会触发（实测 hook=post 0 次）。
        //    而这个方法**每局必走**且**返回 LocationBase**（就是发给客户端的地图对象）→ 用它的 Postfix。
        var lcs = RuntimeTypes.Find("SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService")
                  ?? FindType("LocationLifecycleService");
        var gll = lcs != null ? AccessTools.Method(lcs, "GenerateLocationAndLoot") : null;
        if (gll != null)
        {
            harmony.Patch(gll, null, new HarmonyMethod(typeof(BossWavePatch), nameof(PostfixResult)), null, null, null);
            _log?.Info("[EventRotator] ★主挂点：LocationLifecycleService.GenerateLocationAndLoot (postfix, 每局必走, 返回 LocationBase)");
            PatchProbe.Mount(harmony, gll, "LocationLifecycleService.GenerateLocationAndLoot");
            ok++;
        }
        else { _log?.Warning("[EventRotator] GenerateLocationAndLoot not found"); }

        // 策应：MakeAdjustmentsToMap（动态战局时间调整存在时也会触发，幂等）
        var rta = RuntimeTypes.Find("SPTarkov.Server.Core.Services.InRaid.RaidTimeAdjustmentService")
                  ?? FindType("RaidTimeAdjustmentService");
        var mai = rta != null ? AccessTools.Method(rta, "MakeAdjustmentsToMap") : null;
        if (mai != null)
        {
            harmony.Patch(mai, null, new HarmonyMethod(typeof(BossWavePatch), nameof(Postfix)), null, null, null);
            _log?.Info("[EventRotator] 策应挂点：RaidTimeAdjustmentService.MakeAdjustmentsToMap (postfix, 条件触发)");
            ok++;
        }
        else { _log?.Warning("[EventRotator] MakeAdjustmentsToMap not found"); }

        // 策应：AdjustWaves（幂等）
        var aw = rta != null ? AccessTools.Method(rta, "AdjustWaves") : null;
        if (aw != null)
        {
            var pre = new HarmonyMethod(typeof(BossWavePatch), nameof(Prefix)) { priority = 300 };
            harmony.Patch(aw, pre, null, null, null, null);
            _log?.Info("[EventRotator] boss patch also on AdjustWaves (prefix p300, ABPS-compatible)");
            ok++;
        }

        if (ok == 0) _log?.Error("[EventRotator] no boss hook could be mounted!");

        // ★★★★ D2（task-14）：ABPS 重建 DB 后的**幂等补写**钩子 ★★★★
        //   根因（task-12 诊断，反编译实证）：ABPS `MapSpawns` 拥有 DB 首领表的所有权 ——
        //     · `ConfigureInitialData()` → `BuildInitialCache()` → `ReplaceOriginalLocations()`
        //     · 每局结束 `/client/match/local/end` → `RaidLifecycleService.RaidEnded()` → `RaidFinished`
        //       → `RebuildMap(map)` → `ReplaceOriginalLocationForMap(map)`
        //   两处都是 `_locationData[key].Base.BossLocationSpawn = cloner.Clone(_botMapCache[...])`，
        //   而 `_locationData = locationTable.GetDictionary()`（同一批**实时**对象）
        //   ⇒ 本 mod 启动时 `ApplyToDb` 追加的活动首领（及其 `ShowOnTarkovMapPvE` 骷髅标记）**被整体冲掉**。
        //   用户实测：第 1 局地图界面有红骷髅、第 2 局没有（ABPS 的 `bossTagilla.spawnChance.laboratory = 0`
        //   ⇒ 它重建出的实验室表不含 bossTagilla）。
        //   这里挂两个 postfix，在 ABPS **写完**之后把活动声明的首领幂等补回去。
        //   失败/找不到 ABPS 一律静默跳过（退化到"没骷髅"可接受，崩不可）。
        MountAbpsRebuildHook(harmony);

        // ★ 调试用：统计本局实际生成的 bot 角色（回答"活动要刷的敌种到底刷没刷"）
        try
        {
            var gen = RuntimeTypes.Find("SPTarkov.Server.Core.Generators.Bot.BotGenerator")
                      ?? FindType("BotGenerator");
            var prep = gen != null ? AccessTools.Method(gen, "PrepareAndGenerateBot") : null;
            if (prep != null)
            {
                harmony.Patch(prep, null, new HarmonyMethod(typeof(BossWavePatch), nameof(BotGenerated)), null, null, null);
                _log?.Info("[EventRotator] bot 生成统计已挂载（PrepareAndGenerateBot postfix）");
                PatchProbe.Mount(harmony, prep, "BotGenerator.PrepareAndGenerateBot");
            }
            else _log?.Warning("[EventRotator] PrepareAndGenerateBot 未找到，跳过 bot 生成统计");

            // 一波 bot 全部生成完后，输出一次本局统计
            var botCtrl = RuntimeTypes.Find("SPTarkov.Server.Core.Controllers.BotController")
                          ?? FindType("BotController");
            var waves = botCtrl != null ? AccessTools.Method(botCtrl, "GenerateBotWaves") : null;
            if (waves != null)
            {
                harmony.Patch(waves, null, new HarmonyMethod(typeof(BossWavePatch), nameof(AfterBotWaves)), null, null, null);
                _log?.Info("[EventRotator] bot 波次统计已挂载（GenerateBotWaves postfix）");
                PatchProbe.Mount(harmony, waves, "BotController.GenerateBotWaves");
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] bot 生成统计挂载失败: {ex.Message}"); }

        // ★★★ 昼夜判定：MatchController.ConfigureOfflineRaid 里 SPT 已经算好 IsNightRaid
        //     （= WeatherHelper.IsNightTime(raidSettings.TimeVariant, locationId)），
        //     这里缓存它，供"仅夜晚"的活动判断（EventDef.NightOnly）。
        try
        {
            var mc = RuntimeTypes.Find("SPTarkov.Server.Core.Controllers.MatchController") ?? FindType("MatchController");
            var cor = mc != null ? AccessTools.Method(mc, "ConfigureOfflineRaid") : null;
            if (cor != null)
            {
                harmony.Patch(cor, null, new HarmonyMethod(typeof(BossWavePatch), nameof(AfterConfigureOfflineRaid)), null, null, null);
                _log?.Info("[EventRotator] 昼夜判定已挂载（MatchController.ConfigureOfflineRaid postfix → IsNightRaid）");

                // ★★ v0.1.5【白天入夜】：2022 万圣节官方内容之一 —— 白天进图会「突然入夜」。
                //   落点就是同一个方法的入参：把 request.IsNightRaid 翻成 true（SPT 随后按它选天气/时间变体）。
                try
                {
                    harmony.Patch(cor, new HarmonyMethod(typeof(BossWavePatch), nameof(PrefixForceNight)),
                                  null, null, null, null);
                    _log?.Info("[EventRotator] 入夜(服务端侧)已挂载（ConfigureOfflineRaid prefix → IsNightRaid=true，供 bot 夜战逻辑使用）");

                    // ★★★ v0.1.5【真正让客户端天黑】—— 反编译实证（2026-10-15）：
                    //   ① `ConfigureOfflineRaid` 里的 `IsNightRaid` 带 `[JsonIgnore]`（注释原文
                    //      "Custom property that is not received from or sent to the client"）⇒ 改它**不会让客户端变黑**，
                    //      只影响服务端 bot 生成；
                    //   ② 客户端每局的时间来源是 `/client/weather` → `WeatherController.Generate()` →
                    //      `WeatherHelper.GetInRaidTime()` = (3h + ts × acceleration) % 24h（莫斯科时间）；
                    //      客户端 `BaseLocalGame.InitWeather` 拿它建 `GameDateTime` 并交给 `TODSkyProvider` 驱动 TOD_Sky。
                    //   ⇒ 要真正入夜，必须 Postfix `WeatherController.Generate()` 改返回值的 `Time`/`Date`
                    //      （注意：CLR 属性名是 Time/Date，JSON 名是小写 time/date），
                    //      并同步 Postfix `WeatherHelper.IsNightTime` 让服务端判定一致。
                    var wc = RuntimeTypes.Find("SPTarkov.Server.Core.Controllers.WeatherController")
                             ?? FindType("WeatherController");
                    var wgen = wc != null ? AccessTools.Method(wc, "Generate") : null;
                    if (wgen != null)
                    {
                        harmony.Patch(wgen, null, new HarmonyMethod(typeof(BossWavePatch), nameof(PostfixWeatherGenerate)), null, null, null);
                        _log?.Info("[EventRotator] ★★白天入夜已挂载（WeatherController.Generate postfix → 真正改客户端时间/天色）");
                    }
                    else _log?.Warning("[EventRotator] WeatherController.Generate 未找到 → 入夜只能影响服务端判定");

                    // ★★ v0.1.5 空投「追加」语义：Postfix AirdropService.GenerateAirdropLoot()
                    var ads = RuntimeTypes.Find("SPTarkov.Server.Core.Services.InRaid.AirdropService") ?? FindType("AirdropService");
                    var adg = ads != null ? AccessTools.Method(ads, "GenerateAirdropLoot") : null;
                    if (adg != null)
                    {
                        harmony.Patch(adg, null, new HarmonyMethod(typeof(AirdropAppendDimension), nameof(AirdropAppendDimension.Postfix)), null, null, null);
                        _log?.Info("[EventRotator] ★空投追加已挂载（AirdropService.GenerateAirdropLoot postfix → 往箱里追加活动物品）");
                    }
                    else _log?.Warning("[EventRotator] AirdropService.GenerateAirdropLoot 未找到 → 空投追加不可用");

                    var wh = RuntimeTypes.Find("SPTarkov.Server.Core.Helpers.InRaid.WeatherHelper") ?? FindType("WeatherHelper");
                    var isn = wh != null ? AccessTools.Method(wh, "IsNightTime") : null;
                    if (isn != null)
                    {
                        harmony.Patch(isn, null, new HarmonyMethod(typeof(BossWavePatch), nameof(PostfixIsNightTime)), null, null, null);
                        _log?.Info("[EventRotator] 入夜判定已挂载（WeatherHelper.IsNightTime postfix → 与服务端判定保持一致）");
                    }
                    // ★ 模块加载期（RestoreThenApply）就已经调过 SetForceNight 了，而那时 _log 还没赋值 ⇒ 补打一次
                    if (_forceNightChance > 0)
                        _log?.Info($"[EventRotator] 白天入夜：启动时已启用，强制概率 {_forceNightChance:0.##}"
                                 + "（≥1 每局必夜；0<x<1 每局按概率掷）");
                }
                catch (Exception exN) { _log?.Warning($"[EventRotator] 入夜 prefix 挂载失败: {exN.Message}"); }
            }
            else _log?.Warning("[EventRotator] ConfigureOfflineRaid 未找到 → NightOnly 活动按夜晚处理（不拦截）");
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] 昼夜判定挂载失败: {ex.Message}"); }
    }

    // ─── 昼夜状态 ────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, bool> _nightBySession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v0.1.5 没有夜战变体的地图：『仅夜晚』(NightOnly) 活动在这些图上一律不生效。
    /// 依据：客户端 `JsonType.LocationSettings+Location.NightTimeAllowedLocations => AvailableMaps.Where(x => x != "laboratory")`。
    /// 用户实测症状：邪教之夜的 `bossSpawns: {"*": ["sectantPriest"]}` 把祭司刷进了实验室。
    /// </summary>
    private static readonly HashSet<string> NightlessMaps = new(StringComparer.OrdinalIgnoreCase)
    { "laboratory", "labyrinth" };
    private static bool? _lastKnownNight;          // 最近一次 ConfigureOfflineRaid 的结果（单机/Fika 主机都够用）

    /// <summary>
    /// ★★ v0.1.5【白天入夜】—— 官方 2022 万圣节内容之一（"白天突然入夜"）。
    ///
    /// 落点：`MatchController.ConfigureOfflineRaid(request, sessionId)` 的入参 `request.IsNightRaid`。
    /// SPT 在这个方法里用它算 `IsNightRaid`（= `WeatherHelper.IsNightTime(TimeVariant, map)`）并决定天气/时间变体，
    /// 所以在 **prefix** 里把它翻成 true 就是"进图即夜战"。
    ///
    /// 行为由 `EventDef.ForceNightRaid` 控制：
    ///   · `1.0` = 本活动期间**每局**都入夜；
    ///   · `0 < x < 1` = 每局按该概率掷（官方 2022 的"突然"感就靠这个，默认 0.5）；
    ///   · 不写 / 0 = 不干预。
    /// 事件引擎每次 refresh 会调 `SetForceNight(p)` 更新；未启用时 prefix 直接返回 true（不改任何东西）。
    /// </summary>
    private static double _forceNightChance;                    // 0 = 关闭
    private static readonly Random _rng = new();

    public static void SetForceNight(double chance)
    {
        var before = _forceNightChance;
        _forceNightChance = chance < 0 ? 0 : (chance > 1 ? 1 : chance);
        if (Math.Abs(before - _forceNightChance) > 0.0001)
            _log?.Info($"[EventRotator] 白天入夜：强制概率 {before:0.##} → {_forceNightChance:0.##}"
                     + (_forceNightChance > 0 ? "（本局进图是否入夜按此概率掷）" : "（已关闭）"));
    }

    /// <summary>夜间起始时刻（HH:mm:ss）。可由活动配置覆盖；默认 22:00。</summary>
    private static string _nightTimeOfDay = "22:00:00";

    /// <summary>
    /// ★★ v0.1.5【真正让客户端天黑】—— Postfix `WeatherController.Generate()`。
    ///
    /// 客户端每局的时间来源就是它：`WeatherHelper.GetInRaidTime()` = `(3h + ts × acceleration) % 24h`
    /// （莫斯科时间），客户端 `BaseLocalGame.InitWeather` 用它建 `GameDateTime` 交给 `TODSkyProvider` 驱动 TOD_Sky。
    /// 所以把返回值的 `Time`（JSON 名 `time`，BSG 格式 `HH:mm:ss`）改成夜间值，就是"进图即夜"。
    /// 注意：**不能**靠 `ConfigureOfflineRaid` 的 `IsNightRaid`（那是 `[JsonIgnore]`，只影响服务端 bot 生成）。
    /// </summary>
    private static void PostfixWeatherGenerate(object __result)
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】天气生成
        try
        {
            if (_forceNightChance <= 0 || __result == null) return;
            bool roll = _forceNightChance >= 1.0 || _rng.NextDouble() < _forceNightChance;
            if (!roll) return;
            var pT = __result.GetType().GetProperty("Time", BindingFlags.Public | BindingFlags.Instance);
            if (pT == null || !pT.CanWrite) return;
            var before = pT.GetValue(__result)?.ToString();
            pT.SetValue(__result, _nightTimeOfDay);
            _log?.Info($"[EventRotator] ★白天入夜：/client/weather 时间 {before} → {_nightTimeOfDay}（本局强制夜战）");
        }
        catch { }
    PerfTimer.End("天气生成.Postfix", t0);
    }

    /// <summary>Postfix `WeatherHelper.IsNightTime(...)`：入夜时让服务端判定与下发时间一致（bot 夜视/夜战装备池）。</summary>
    private static void PostfixIsNightTime(ref bool __result)
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】IsNightTime（夜间局可能每 bot）
        try { if (_forceNightChance > 0) __result = true; } catch { }
        PerfTimer.End("IsNightTime(只读回调)", t0);
    }

    private static void PrefixForceNight(object __0, object __1)
    {
        // ★★★ v0.1.5【每次进图都重刷季节】—— 客户端可能在服务端写入之前就取过天气，
        //   故在进图这一刻再写一次（幂等；与 SPT 好友命令 `itsonlysnowalan` 同一个开关
        //   `weatherConfig.OverrideSeason`，官方 ForceSnowMessageHandler 亦然）。
        try
        {
            var act = EventEngine.Actives;
            if (act != null && act.Count > 0)
            {
                var evW = act.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x?.ForceWeatherSeason));
                if (evW != null) WeatherSeasonDimension.ForceNow(evW.ForceWeatherSeason, "进图重刷（活动 " + evW.Id + "）");
            }
        }
        catch { }

            // ★★★ v0.1.5【SCAV 接管 · 路费机制】进图时执行（豁免等级 / 免费图 / 扣物品 / 可拦下）
            try
            {
                var tollEv = EventRotatorServer._toll;
                if (tollEv?.Toll != null)
                {
                    // ★ 与同方法下面的入夜逻辑一致：SPT 的参数顺序可能是 (sessionId, request) ⇒ 先判方向
                    object req = __0, sid0 = __1;
                    if (req != null && req.GetType().Name.IndexOf("MongoId", StringComparison.OrdinalIgnoreCase) >= 0)
                    { var tmp = req; req = sid0; sid0 = tmp; }
                    if (req == null) return;
                    var mk = (Prop(req, "LocationId") ?? Prop(req, "Location") ?? Prop(req, "locationId")
                              ?? Prop(req, "LocationKey"))?.ToString() ?? "";
                    mk = mk.Trim().ToLowerInvariant();
                    if (mk.Contains("factory4_day")) mk = "factory4_day";
                    else if (mk.Contains("factory4_night")) mk = "factory4_night";
                    else if (mk.Contains("sandbox_high")) mk = "sandbox_high";
                    var sid = sid0?.ToString();
                    if (!string.IsNullOrEmpty(mk)) TollDimension.Enforce(tollEv, mk, sid);
                    else _log?.Warning("[EventRotator][路费] 取不到目标地图 key（request 字段名可能不同）⇒ 本次跳过");
                }
            }
            catch (Exception ex) { _log?.Warning("[EventRotator][路费] 执行失败: " + ex.Message); }

        try
        {
            if (_forceNightChance <= 0) return;
            object req = __0, sid = __1;
            if (req != null && req.GetType().Name.IndexOf("MongoId", StringComparison.OrdinalIgnoreCase) >= 0)
            { var tmp = req; req = sid; sid = tmp; }
            if (req == null) return;

            bool roll = _forceNightChance >= 1.0 || _rng.NextDouble() < _forceNightChance;
            var pIsNight = req.GetType().GetProperty("IsNightRaid", BindingFlags.Public | BindingFlags.Instance);
            if (pIsNight == null || !pIsNight.CanWrite) return;
            bool before = pIsNight.GetValue(req) is bool bb && bb;
            if (roll && !before)
            {
                pIsNight.SetValue(req, true);
                _log?.Info($"[EventRotator] ★白天入夜：本局已强制为夜战（概率 {_forceNightChance:0.##}，session={sid}）");
            }
        }
        catch { }
    }

    /// <summary>MatchController.ConfigureOfflineRaid 的 Postfix：记录本局是否夜战。</summary>
    /// <summary>
    /// ★ v0.1.5：通配符 `"*"` 的语义修正 —— 只认「真实首领」。
    ///
    /// 审计实证（代码走查 + 本机日志）：`bossChance {"*":{"*":100}}` 与 `bossSpawns {map:["*"]}`
    /// 过去会把该图 `BossLocationSpawn` 里的**每一条**都设成 100%，实测 `bigmap: all 36 existing boss(es) → 100%`，
    /// 被一起拉满的包括：`gifter`（圣诞老人）、`arenaFighterEvent`（走私者）、万圣节召唤链
    /// （`peacefullZryachiyEvent`/`sectactPriestEvent`/`ravangeZryachiyEvent`）、`sectantPredvestnik`（邪教）、
    /// `blackDivAssault`、PvE 的 `pmcBEAR`/`pmcUSEC`、实验室的 `infectedLaborant`/`infectedPmc`。
    ///
    /// 官方的"全首领"活动只针对首领（Permanent Boss Presence 还额外排除邪教徒与游击队员），
    /// 所以通配符只应命中 `boss*`。需要特定非首领角色时，用**显式名字**写进 `bossSpawns`（不受此过滤影响）。
    /// </summary>
    private static bool IsRealBoss(string name)
        => !string.IsNullOrEmpty(name) && name.StartsWith("boss", StringComparison.OrdinalIgnoreCase);

    private static void AfterConfigureOfflineRaid(object __0, object __1)
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】每次进图
        try
        {
            // 参数顺序：(GetRaidConfigurationRequestData request, MongoId sessionId)
            object req = __0, sid = __1;
            // 兼容参数顺序颠倒的情况（按类型名判断）
            if (req != null && req.GetType().Name.IndexOf("MongoId", StringComparison.OrdinalIgnoreCase) >= 0)
            { var t = req; req = sid; sid = t; }

            var isNight = Prop(req, "IsNightRaid");
            if (isNight is bool b)
            {
                _lastKnownNight = b;
                var key = sid?.ToString();
                if (!string.IsNullOrEmpty(key)) _nightBySession[key] = b;
                EventEngine.Debug($"[EventRotator] 昼夜判定：session={key} IsNightRaid={b}");
            }

            // ★★★【面具修正·诊断】每局一次的分局点：`ConfigureOfflineRaid` 在 `/client/raid/configuration`
            //   时被调用 ⇒ 这里是**唯一**"一局只有一次"的稳定入口（`GenerateLocationAndLoot` 的 postfix 在
            //   本机 4.1.6 实机日志里**一次都没进**：全部 39 条 `ApplyMap` 都是 `hook=db`）。
            //   作用：把**上一局**的 bot 计数/role 分布/Location 样例打出来再清零（"收到 0 只"也会明说）。
            //   本函数其余逻辑与 `Main` 统计无关，纯旁挂，异常全吞。
            try { FaceCoverFixDimension.OnRaidStart(Prop(req, "Location")?.ToString()); } catch { }
        }
        catch { }
    PerfTimer.End("每次进图(ConfigureOfflineRaid)", t0);
    }

    /// <summary>查本局是否夜战。null = 未知（此时不拦截，按原版刷）。</summary>
    private static bool? IsNightRaid(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId) && _nightBySession.TryGetValue(sessionId, out var b)) return b;
        return _lastKnownNight;
    }

    private static Type FindType(string simpleName) =>
        // ★ v1.0.2：先在**运行时 Core + 本 mod 的 ALC** 里按短名找（避免扫到 prepatch 的死副本），
        //   再回落到"扫全部程序集"的老行为。
        RuntimeTypes.FindSimple(simpleName)
        ?? AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
            .FirstOrDefault(x => x.Name == simpleName);

    // GenerateLocationAndLoot(MongoId sessionId, string name, bool generateLoot) -> LocationBase
    //   → __result = 返回的地图对象（★主挂点）、__0 = sessionId
    private static void PostfixResult(object __result, object __0, MethodBase __originalMethod)
    {
        try { PatchProbe.OnCall(__originalMethod, "LocationLifecycleService.GenerateLocationAndLoot"); } catch { }
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】GenerateLocationAndLoot（每次进图计划）
        try { ApplyMap(__result, "post", __0?.ToString()); }
        finally { PerfTimer.End("进图计划.PostfixResult", t0); }
    }

    // MakeAdjustmentsToMap(RaidChanges raidAdjustments, LocationBase mapBase) → __1 = mapBase
    private static void Postfix(object __1)
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】MakeAdjustmentsToMap
        try { ApplyMap(__1, "post"); }
        finally { PerfTimer.End("地图调整.Postfix", t0); }
    }

    // AdjustWaves(LocationBase mapBase, RaidChanges raidAdjustments) → __0 = mapBase
    private static void Prefix(object __0) => ApplyMap(__0, "pre");

    /// <summary>
    /// 每（活动 × 地图）只跑一次。v1.97 起改为按 <c>"活动id|地图key"</c> 作键，
    /// 因为现在支持**多个活动同时生效** —— 各活动的追加/提升互不干扰。
    /// </summary>
    private static readonly HashSet<string> _seenMaps = new(StringComparer.Ordinal);

    /// <summary>raid 阶段专用的 seen 集合（与 DB 阶段分开，见 ApplyMap 里的说明）。</summary>
    private static readonly HashSet<string> _seenRaid = new(StringComparer.Ordinal);

    /// <summary>
    /// DB 阶段**上一次真正套用**的活动集合（<c>"id1+id2"</c>）。只有它和 <c>EventEngine.Actives</c>
    /// 不一致时才需要「先还原、再重新套用」（见 <see cref="DbNeedsReapply"/> 与 <c>Plugin.RestoreThenApply()</c>）。
    /// </summary>
    private static string _dbAppliedEvent;

    /// <summary>★ P0 修复：DB 原值快照是否已建立（一次性；<see cref="ApplyToDb"/> 首次调用时建立）。</summary>
    private static bool _dbInitialized;

    /// <summary>DB 快照：mapKey → 原始条目概率（还原用）。</summary>
    private static readonly Dictionary<string, List<double>> _dbSnap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v0.1.4 红骷髅标记快照：mapKey → [(ShowOnTarkovMap, ShowOnTarkovMapPvE)]，与 <see cref="_dbSnap"/> 同序。
    /// 编码：true=1 / false=0 / null=-1（见 <see cref="MarkOf"/>）。换活动时必须一起复位，
    /// 否则红骷髅会永久留在地图上。
    /// </summary>
    private static readonly Dictionary<string, List<(double, double)>> _dbMarkSnap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★★★ 方案A（task-41）：`forceZone` 现在也作用在「被提升概率的既有条目」上，所以必须快照原 `BossZone`
    /// 并能在换/关活动时复位 —— 沿用本文件自带的快照模式（`_dbSnap`/`_dbMarkSnap`/`_dbOrig` + `else` 分支）。
    /// 结构：dbKey → { bossName → 原 BossZone（可能为 null）}。
    /// </summary>
    private static readonly Dictionary<string, List<string>> _dbZoneSnap =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ P0 修复：快照时每张图 <c>BossLocationSpawn</c> 的 **BossName 顺序**。
    /// <para>还原 BossChance / 红骷髅是"按位置回放"的 —— 若别的插件（ABPS）整体重建过这张表、
    /// 内容或顺序对不上，按位置回放就会把**别人的条目**写坏 ⇒ 用它对账，对不上就整图跳过
    /// 概率/标记还原（见 <see cref="RestoreDb"/>）。</para>
    /// </summary>
    private static readonly Dictionary<string, List<string>> _dbNameSnap =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ P0 修复（同根因，抽 <see cref="RestoreDb"/> 时顺带发现）：DB 阶段的 `escortType` / `escortCount`
    /// 会覆盖**本来就存在于表里的条目**（活动覆盖优先），旧代码没有快照/还原 ⇒ 换活动后随从类型/人数残留。
    /// 结构：dbKey → { bossName → (原 BossEscortType, 原 BossEscortAmount) }。
    /// </summary>
    private static readonly Dictionary<string, List<(string Type, string Amount)>> _dbEscortSnap =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>DB 原始条目：mapKey → bossName → 条目（只读，用于“同图同名”克隆）。</summary>
    private static readonly Dictionary<string, Dictionary<string, object>> _dbOrig =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>完整模板库：bossName → 真实存在的完整 BossLocationSpawn 拷贝（“他图同名”克隆）。</summary>
    private static readonly Dictionary<string, object> _templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v0.1.3 地图键统一取法（修一个真 bug）：
    ///   <c>LocationTable</c> 的**属性名**与 <c>base.json</c> 的 <c>Id</c> **不是同一套名字**：
    ///     Id=<c>factory4_day</c>   → 属性名 <c>Factory4Day</c>
    ///     Id=<c>factory4_night</c> → 属性名 <c>Factory4Night</c>
    ///     Id=<c>Sandbox_high</c>   → 属性名 <c>SandboxHigh</c>
    ///   旧代码 **存快照用属性名、查快照用 Id.ToLower()** ⇒ 这三张图永远查不到：
    ///     ① <c>ZonesOfMap()</c> 返回空 → <c>ZoneValidForMap()</c> 提前放行 → 刷点回退保护失效
    ///        （可能把别图的 BossZone 写进来 → 首领刷不出来）
    ///     ② <c>RestoreHostility()</c> 永不命中 → 补的敌对性条目换活动时不还原、跨活动累积
    ///   现在统一取 <c>Base.Id.ToLowerInvariant()</c>（拿不到才退回属性名）。
    /// </summary>
    private static string KeyOf(object baseObj, PropertyInfo fallbackProp)
    {
        try
        {
            var id = baseObj?.GetType().GetProperty("Id")?.GetValue(baseObj)?.ToString();
            if (!string.IsNullOrWhiteSpace(id)) return id.ToLowerInvariant();
        }
        catch { }
        return fallbackProp?.Name?.ToLowerInvariant() ?? "?";
    }

    /// <summary>
    /// ★ 一次性初始化（每进程只该跑一次）：建模板库 + 给每张图登记原值快照，然后按当前活动套用到 DB。
    ///
    /// <para><b>★ P0 修复（写进 DB 后不还原）</b>：旧实现把"写入"和"还原上一个活动"都塞在本方法里
    /// （靠 <c>_dbAppliedEvent != evNow</c> 判断），但本方法每进程**只被调用一次**（<c>Plugin.OnLoadAsync</c>）
    /// ⇒ 那条"还原"分支**永远不可达** ⇒ 换活动后上一轮的首领追加 / BossChance / 红骷髅 / BossZone 全部残留。
    /// 现在拆成三块：本方法 = 一次性初始化；<see cref="ApplyDbForCurrentEvent"/> = 每轮可调用；
    /// <see cref="RestoreDb"/> = 每轮可调用的还原（由 <c>Plugin.RestoreThenApply()</c> 在套用之前调用）。</para>
    /// </summary>
    public static int ApplyToDb(object locationTable)
    {
        if (locationTable == null) return 0;
        EnsureDbInitialized(locationTable);
        var n = ApplyDbForCurrentEvent(locationTable);
        _log?.Info($"[EventRotator] boss templates: {_templates.Count} (from {_dbOrig.Count} maps)");
        return n;
    }

    /// <summary>DB 原值快照是否已建立（首次 <see cref="ApplyToDb"/> 时建立）。</summary>
    public static bool DbInitialized => _dbInitialized;

    /// <summary>
    /// DB 里当前套用的活动集合 vs <c>EventEngine.Actives</c>：不一致（含活动被关掉）⇒ 需要
    /// <see cref="RestoreDb"/> + <see cref="ApplyDbForCurrentEvent"/>。
    /// <para>用"集合有没有变"做门控是刻意的：集合没变的局里 DB 已经是目标状态，重复还原+套用
    /// 既无意义、又会让每局多出几十行 db 日志（用户对日志噪声很敏感）。</para>
    /// </summary>
    public static bool DbNeedsReapply
    {
        get
        {
            try
            {
                if (!_dbInitialized) return false;
                var evNow = string.Join("+", EventEngine.Actives.Select(a => a.Id));
                return !string.Equals(_dbAppliedEvent, evNow, StringComparison.Ordinal);
            }
            catch { return false; }
        }
    }

    /// <summary>没有活动在跑：还原完就是终态 ⇒ 清掉"已套用集合"记账（否则每轮都会判定为需要重新套用）。</summary>
    public static void MarkDbRestored() => _dbAppliedEvent = "";

    /// <summary>
    /// 一次性建快照（幂等）：mapKey → 原 BossChance / 红骷髅标记 / BossZone / 原始条目 / BossName 顺序。
    /// <para>⚠ 必须在**任何写入之前**跑一次，否则会把"已经改过的值"当成原值 ⇒ 之后永远还原不回去
    /// （首次 <c>RestoreThenApply()</c> 早于本初始化 ⇒ <c>Plugin</c> 那里用 <see cref="DbInitialized"/> 挡住套用）。
    /// </para>
    /// </summary>
    private static void EnsureDbInitialized(object locationTable)
    {
        if (_dbInitialized || locationTable == null) return;
        try
        {
            CollectTemplates(locationTable);
            SnapshotDb(locationTable);
            _dbInitialized = true;
            _log?.Info($"[EventRotator] DB 首领表原值快照完成：{_dbSnap.Count} 张图"
                     + "（BossChance / 红骷髅 / BossZone / 随从类型·人数 / BossName 顺序，换活动时可精确还原）");
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] DB 首领表快照失败（本轮不还原）: {ex.Message}"); }
    }

    /// <summary>给**尚未快照**的图登记原值（每张图只登记一次；重复调用不会把已改过的值当原值）。</summary>
    private static void SnapshotDb(object locationTable)
    {
        foreach (var p in locationTable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object loc;
            try { loc = p.GetValue(locationTable); } catch { continue; }
            if (loc == null) continue;
            var baseObj = loc.GetType().GetProperty("Base")?.GetValue(loc);
            if (baseObj == null) continue;
            var list = baseObj.GetType().GetProperty("BossLocationSpawn")?.GetValue(baseObj) as IList;
            if (list == null) continue;
            var key = KeyOf(baseObj, p);      // ★ v0.1.3 与 ApplyMap/查询端统一键空间
            if (_dbSnap.ContainsKey(key)) continue;
            var snap = new List<double>();
            foreach (var it in list) snap.Add(ChanceOf(it));
            _dbSnap[key] = snap;
            // ★★★ v0.1.4 红骷髅也要快照！旧代码只快照了 BossChance，
            //   而"概率提升"路径现在会顺手补 ShowOnTarkovMap=true ——
            //   不复位的话红骷髅会**永久留在地图上**（换活动也不消失）。
            //   null 用 -1 标记（-1 = 原本是 null）。
            var mark = new List<(double, double)>();
            foreach (var it in list) mark.Add((MarkOf(it, "ShowOnTarkovMap"), MarkOf(it, "ShowOnTarkovMapPvE")));
            _dbMarkSnap[key] = mark;
            var orig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in list)
            {
                var n = Prop(it, "BossName") as string;
                if (!string.IsNullOrEmpty(n) && !orig.ContainsKey(n)) orig[n] = it;
            }
            _dbOrig[key] = orig;
            // ★ P0 修复：记下 BossName 顺序（还原"按位置回放"之前用来对账，见 RestoreDb —— 防写坏别人的条目）
            var names = new List<string>();
            foreach (var it in list) names.Add(Prop(it, "BossName") as string ?? "");
            _dbNameSnap[key] = names;
            // ★ 方案A：快照原 BossZone（forceZone 现在也会改既有条目 ⇒ 必须能复位）
            // ★ 2026-10-30 修正：原来按 `BossName` 去重快照 ⇒ 同名多条目（如 laboratory 的 20 条 pmcBot，
            //   BotZoneFloor1/Floor2/Basement 各不相同）在还原时会全部被写回**第一条**的 zone。
            //   改成**按位置**快照/回放（前缀顺序已由 `_dbNameSnap` 对账保证）。
            var zs = new List<string>();
            foreach (var it in list) zs.Add(Prop(it, "BossZone")?.ToString());
            _dbZoneSnap[key] = zs;
            // ★ P0 修复（同根因、抽 RestoreDb 时顺带发现）：`escortType` / `escortCount` 也会覆盖
            //   **本来就存在于表里的条目**（ApplyOneEvent 末尾那段"活动覆盖"），旧代码没给它们登记原值
            //   ⇒ 换活动后随从类型/人数残留（代码注释写着"可还原"，其实从来没有还原路径）。
            // 同上：随从类型/人数也改成按位置快照（否则同名多条目的还原会把值折叠成第一条的）
            var es = new List<(string Type, string Amount)>();
            foreach (var it in list)
                es.Add((Prop(it, "BossEscortType") as string, Prop(it, "BossEscortAmount") as string));
            _dbEscortSnap[key] = es;
        }
    }

    /// <summary>
    /// ★ 每轮可调用：按**当前活动**把 DB(LocationTable) 的首领表重新套用一遍。
    /// <para>调用前必须先 <see cref="RestoreDb"/>（撤掉上一轮的追加/改写），否则就成了重复追加。
    /// <c>_seenMaps</c> 是旧代码"每进程只套用一次"的门控，与"每轮还原→套用"互斥 ⇒ 这里清空它，
    /// 让每个 (活动×地图) 都完整重放一遍（追加路径自带 presence 守卫、概率是赋值 ⇒ 幂等）。</para>
    /// </summary>
    public static int ApplyDbForCurrentEvent(object locationTable)
    {
        if (locationTable == null) return 0;
        EnsureDbInitialized(locationTable);
        int count = 0;
        _seenMaps.Clear();                  // ★ 旧门控与"每轮还原再套用"互斥，见方法注释
        foreach (var p in locationTable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object loc;
            try { loc = p.GetValue(locationTable); } catch { continue; }
            if (loc == null) continue;
            var baseObj = loc.GetType().GetProperty("Base")?.GetValue(loc);
            if (baseObj == null) continue;
            try
            {
                // ★ D2（task-14）：缓存 DB 的 LocationBase（key = `Base.Id` 小写，与 ApplyMap 的 mapKey 同空间）。
                //   给 ABPS 重建后的幂等补写用 —— 那里拿不到 LocationTable，只能靠这份缓存。
                try { _dbBaseByMap[KeyOf(baseObj, p)] = baseObj; } catch { }
                ApplyMap(baseObj, "db"); count++;
            }
            catch (Exception ex) { _log?.Warning($"[EventRotator] db apply '{p.Name}' failed: {ex.Message}"); }
        }
        try { _dbAppliedEvent = string.Join("+", EventEngine.Actives.Select(a => a.Id)); } catch { }
        return count;
    }

    /// <summary>
    /// ★★★ P0 修复：把 DB 阶段写进去的东西**还原**（可重复调用、幂等）。
    /// <list type="number">
    ///   <item>删掉超出快照条数的**追加首领**（我们只往末尾 Add）；</item>
    ///   <item><c>BossChance</c> 按位置回放原值；</item>
    ///   <item><c>ShowOnTarkovMap</c> / <c>ShowOnTarkovMapPvE</c>（红骷髅）按位置回放原值；</item>
    ///   <item><c>BossZone</c> 按 BossName 回放原值（forceZone 也会改既有条目）。</item>
    ///   <item><c>BossEscortType</c> / <c>BossEscortAmount</c> 按 BossName 回放原值
    ///         （`escortType` / `escortCount` 同样会覆盖既有条目 —— 同根因、抽方法时顺带补上）。</item>
    /// </list>
    /// <para>这段原来只写在 <see cref="ApplyToDb"/> 的 <c>else</c> 分支里，而 ApplyToDb 每进程只跑一次
    /// ⇒ **永远不可达**（日志实证：18 次「DB boss apply」、0 次还原）。现在独立出来，
    /// 由 <c>Plugin.RestoreThenApply()</c> 在重新套用之前调用。</para>
    /// <para><b>安全</b>：只对 <c>BossName</c> 与快照同序的条目按位置回写 —— 若别人（如 ABPS）整体重建过
    /// 该表、内容/顺序对不上，就**整张图跳过还原**（不删别人的条目），**绝不把别人的值写坏**。</para>
    /// </summary>
    public static int RestoreDb(object locationTable)
    {
        if (locationTable == null || _dbSnap.Count == 0) return 0;
        int removed = 0, chance = 0, mark = 0, zone = 0, escort = 0;
        try
        {
            foreach (var p in locationTable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object loc;
                try { loc = p.GetValue(locationTable); } catch { continue; }
                if (loc == null) continue;
                var baseObj = loc.GetType().GetProperty("Base")?.GetValue(loc);
                if (baseObj == null) continue;
                if (baseObj.GetType().GetProperty("BossLocationSpawn")?.GetValue(baseObj) is not IList list) continue;
                var key = KeyOf(baseObj, p);
                if (!_dbSnap.TryGetValue(key, out var snap) || snap == null) continue;

                // ① 先对账 BossName 顺序（在删追加项**之前**）：
                //    顺序对不上 ⇒ 这张表被别人（如 ABPS）整体重建过 ⇒ **整图跳过**（既不删也不改），
                //    绝不把别人的条目删掉/把别人的值写坏。只有前缀与快照同序，才敢按位置回放。
                _dbNameSnap.TryGetValue(key, out var names);
                int n = Math.Min(snap.Count, list.Count);
                bool orderOk = true;
                if (names != null)
                    for (int i = 0; i < n; i++)
                        if (!string.Equals(Prop(list[i], "BossName") as string, names[i], StringComparison.OrdinalIgnoreCase))
                        { orderOk = false; break; }
                if (!orderOk)
                {
                    _log?.Warning($"[EventRotator] {key}: 首领表已被其它插件重建（BossName 顺序对不上）"
                                + " → 整图跳过还原（不删别人的条目、不写坏别人的值）");
                    continue;
                }

                // ② 追加项：我们只 `list.Add`，所以"超出快照条数的尾部"就是上一轮补进去的
                while (list.Count > snap.Count) { list.RemoveAt(list.Count - 1); removed++; }

                // ③ 概率 + ④ 红骷髅：按位置回放（已确认前缀同序）
                for (int i = 0; i < n; i++)
                {
                    if (Math.Abs(ChanceOf(list[i]) - snap[i]) > 1e-6)
                    { SetOriginal(list[i], "BossChance", snap[i]); chance++; }
                    if (_dbMarkSnap.TryGetValue(key, out var mk) && i < mk.Count)
                    {
                        // ★ 只有真的写过（当前值 ≠ 快照值）才动，避免每次进图都无谓改写
                        var curM = MarkOf(list[i], "ShowOnTarkovMap");
                        var curP = MarkOf(list[i], "ShowOnTarkovMapPvE");
                        if (Math.Abs(curM - mk[i].Item1) > 0.5 || Math.Abs(curP - mk[i].Item2) > 0.5)
                        {
                            SetOriginal(list[i], "ShowOnTarkovMap", MarkToObj(mk[i].Item1));
                            SetOriginal(list[i], "ShowOnTarkovMapPvE", MarkToObj(mk[i].Item2));
                            mark++;
                        }
                    }
                }

                // ⑤ BossZone（按 BossName 回放；forceZone 改过既有条目 ⇒ 换/关活动时必须还原）
                if (_dbZoneSnap.TryGetValue(key, out var zsnap))
                {
                    for (int i = 0; i < n && i < zsnap.Count; i++)
                    {
                        var oz = zsnap[i];
                        if (string.Equals(Prop(list[i], "BossZone")?.ToString(), oz, StringComparison.Ordinal)) continue;
                        SetOriginal(list[i], "BossZone", oz ?? "");
                        zone++;
                    }
                }

                // ⑥ 随从类型/人数（`escortType` / `escortCount` 覆盖过**既有条目** ⇒ 换/关活动时必须还原）
                if (_dbEscortSnap.TryGetValue(key, out var esnap))
                {
                    for (int i = 0; i < n && i < esnap.Count; i++)
                    {
                        var it = list[i];
                        var oe = esnap[i];
                        if (oe.Type != null
                            && !string.Equals(Prop(it, "BossEscortType")?.ToString(), oe.Type, StringComparison.OrdinalIgnoreCase))
                        { SetOriginal(it, "BossEscortType", oe.Type); escort++; }
                        if (oe.Amount != null
                            && !string.Equals(Prop(it, "BossEscortAmount")?.ToString(), oe.Amount, StringComparison.OrdinalIgnoreCase))
                        { SetOriginal(it, "BossEscortAmount", oe.Amount); escort++; }
                    }
                }
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] RestoreDb 失败: {ex.Message}"); }
        int total = removed + chance + mark + zone + escort;
        if (total > 0)
            _log?.Info($"[EventRotator] 已还原 DB 首领表：撤销追加 {removed} 条 / BossChance {chance} 条"
                     + $" / 红骷髅 {mark} 条 / BossZone {zone} 条 / 随从类型·人数 {escort} 条（换活动不残留）");
        return total;
    }

    /// <summary>扫描 LocationTable 里所有地图的首领条目，按 BossName 建模板库（不修改任何东西）。</summary>
    /// <remarks>
    /// ★★★ v2.6.53【P2-#7 修复】原实现是"**首个出现者胜**"（`if (_templates.ContainsKey(name)) continue;`）——
    /// 而遍历的是**实时表**，装了 ABPS 时那张表已被它的 `Mapspawns.ConfigureInitialData` 换成
    /// `Defaults/Bosses.json` 的内容（19 个 boss、值也与原版不同，如 `sectantPriest amount=2,2,3,3,4`）
    /// ⇒ 模板库的内容**随时序变化**且**来源不可见**。
    /// 现在：① 每个 boss 在全部候选里选**最干净**的那条（无 trigger &gt; 有 trigger；escort 与该 boss 正牌随从一致 &gt; 不一致）；
    ///       ② 记下它来自哪张图，并在日志里逐条打出，便于"值不对时一眼看到是从哪抄的"。
    /// </remarks>
    private static void CollectTemplates(object locationTable)
    {
        var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in locationTable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object loc;
            try { loc = p.GetValue(locationTable); } catch { continue; }
            var baseObj = loc?.GetType().GetProperty("Base")?.GetValue(loc);
            var list = baseObj?.GetType().GetProperty("BossLocationSpawn")?.GetValue(baseObj) as IList;
            if (list == null) continue;
            var mapKey = KeyOf(baseObj, p);
            foreach (var it in list)
            {
                var name = Prop(it, "BossName") as string;
                if (string.IsNullOrEmpty(name)) continue;
                int score = TemplateScore(it, name);
                if (_templates.ContainsKey(name) && (!best.TryGetValue(name, out var b) || score <= b)) continue;
                var clone = Clone(it);
                if (clone == null) continue;
                _templates[name] = clone;
                best[name] = score;
                _tplSrc[name] = $"{mapKey}(分{score})";
            }
        }
        // ★ 逐条自证：模板是从哪张图抄来的、那一行的 escort/trigger 长什么样
        try
        {
            var lines = new List<string>();
            foreach (var kv in _templates)
            {
                var esc = Prop(kv.Value, "BossEscortType") as string;
                var amt = Prop(kv.Value, "BossEscortAmount") as string;
                var trig = Prop(kv.Value, "TriggerId") as string;
                var sup = Prop(kv.Value, "Supports") == null ? "-" : "有";
                lines.Add($"{kv.Key}←{(_tplSrc.TryGetValue(kv.Key, out var s) ? s : "?")}"
                          + $"[escort={esc} {amt}; trigger={(string.IsNullOrEmpty(trig) ? "无" : trig)}; supports={sup}]");
            }
            _log?.Info("[EventRotator] boss 模板来源（择优+留痕）：" + string.Join(" ｜ ", lines));
        }
        catch { }
    }

    /// <summary>模板"干净度"打分：无 trigger 更好；escort 与该 boss 正牌随从一致更好。</summary>
    private static int TemplateScore(object entry, string boss)
    {
        int s = 0;
        try { if (string.IsNullOrEmpty(Prop(entry, "TriggerId") as string)) s += 2; } catch { }
        try
        {
            var esc = Prop(entry, "BossEscortType") as string;
            var want = FirstEscort(boss);
            if (!string.IsNullOrEmpty(want) && string.Equals(esc, want, StringComparison.OrdinalIgnoreCase)) s += 1;
        }
        catch { }
        return s;
    }

    /// <summary>模板来源留痕：boss → "map(分N)"。</summary>
    private static readonly Dictionary<string, string> _tplSrc = new(StringComparer.OrdinalIgnoreCase);

    private static double ChanceOf(object item)
    {
        try { var v = Prop(item, "BossChance"); return v == null ? 0d : Convert.ToDouble(v); }
        catch { return 0d; }
    }

    // ═══════════════════ D2（task-14）：ABPS 重建 DB 后的幂等补写 ═══════════════════
    //
    // 【为什么需要】ABPS `MapSpawns` 拥有 DB 首领表的**所有权**，它两处都用
    //     `_locationData[key].Base.BossLocationSpawn = cloner.Clone(_botMapCache[...])`
    //   整体替换列表（`_locationData = locationTable.GetDictionary()` = 同一批实时对象）：
    //     ① `ConfigureInitialData()` → `BuildInitialCache()` → `ReplaceOriginalLocations()`      ← 启动 / PBS 网页保存
    //     ② `RaidFinished` → `RebuildMap(map)` → `ReplaceOriginalLocationForMap(map)`             ← 每局结束
    //   而本 mod 的骷髅标记只写在 DB 条目上（`ApplyToDb`，每进程只跑一次）⇒ 被冲掉后
    //   `/client/locations`（`LocationController.GenerateAll` → `locationTable.GetDictionary()`）
    //   流出的表里就没有那条 ⇒ 地图选择界面没骷髅。
    //
    // 【D2 口径】只补缺、只补显示字段，**不删不改**别的：
    //   · 该图 DB 表里**缺**这条 ⇒ 追加（新条目自带 `ShowOnTarkovMap`/`ShowOnTarkovMapPvE`）
    //   · **已存在** ⇒ 只把两个显示字段补成 true（不动 BossChance / BossZone / 护卫）
    //   · 只处理本 mod 当前活动声明的 boss name，绝不碰 ABPS 自己的条目
    //
    // 【红线】**绝不复用 `ApplyToDb`** —— 它第二次调用会走还原分支，而还原用
    //   `while (list.Count > snap.Count) list.RemoveAt(list.Count-1)` **按尾部下标删**，
    //   在 ABPS 刚重建的（长度/顺序都不同的）表上会**删错改错**。

    /// <summary>DB 的 LocationBase 缓存：mapId(小写) → LocationBase。由 <see cref="ApplyToDb"/> 填充。</summary>
    private static readonly Dictionary<string, object> _dbBaseByMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>补写统计（只用于日志限流，避免每次重建都刷屏）。</summary>
    private static int _abpsReconcileOps;

    /// <summary>
    /// 把 ABPS 的 `MapSpawns.ReplaceOriginalLocations()` / `ReplaceOriginalLocationForMap(string)`
    /// 挂上 postfix，在它**写完 DB 之后**做幂等补写。
    /// 找不到类型/方法一律**静默跳过**（ABPS 改名/卸载不能炸本 mod —— 退化到"没骷髅"可接受）。
    /// </summary>
    private static void MountAbpsRebuildHook(Harmony harmony)
    {
        try
        {
            var mapSpawns = AccessTools.TypeByName("BotPlacementSystemServer.Controllers.MapSpawns")
                            ?? FindTypeIn("BotPlacement", "MapSpawns");
            if (mapSpawns == null)
            {
                _log?.Info("[EventRotator][ABPS] 未发现 MapSpawns 类型 → 不挂 D2 补写钩子（无 ABPS 时正常，退化到无骷髅）");
                return;
            }

            int mounted = 0;

            // ① 每局结束那条链：RebuildMap → ReplaceOriginalLocationForMap(string)
            var perMap = AccessTools.Method(mapSpawns, "ReplaceOriginalLocationForMap", new[] { typeof(string) })
                         ?? mapSpawns.GetMethod("ReplaceOriginalLocationForMap",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (perMap != null)
            {
                harmony.Patch(perMap, null,
                    new HarmonyMethod(typeof(BossWavePatch), nameof(PostfixAbpsReplaceForMap)), null, null, null);
                _log?.Info("[EventRotator][ABPS] ★D2 补写钩子已挂载：MapSpawns.ReplaceOriginalLocationForMap (postfix, 每局结束触发)");
                mounted++;
            }
            else _log?.Warning("[EventRotator][ABPS] 找不到 MapSpawns.ReplaceOriginalLocationForMap（ABPS 可能改名）→ 该触发路径不覆盖");

            // ② 启动 / PBS 网页保存那条链：ConfigureInitialData → BuildInitialCache → ReplaceOriginalLocations()
            var allMaps = AccessTools.Method(mapSpawns, "ReplaceOriginalLocations", Type.EmptyTypes)
                          ?? mapSpawns.GetMethod("ReplaceOriginalLocations",
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (allMaps != null)
            {
                harmony.Patch(allMaps, null,
                    new HarmonyMethod(typeof(BossWavePatch), nameof(PostfixAbpsReplaceAll)), null, null, null);
                _log?.Info("[EventRotator][ABPS] ★D2 补写钩子已挂载：MapSpawns.ReplaceOriginalLocations (postfix, 启动 + PBS 网页保存触发)");
                mounted++;
            }
            else _log?.Warning("[EventRotator][ABPS] 找不到 MapSpawns.ReplaceOriginalLocations（ABPS 可能改名）→ 该触发路径不覆盖");

            if (mounted == 0)
                _log?.Warning("[EventRotator][ABPS] D2 补写钩子一个都没挂上 → 骷髅仍会在每局结束后被 ABPS 冲掉（功能退化，不影响其它维度）");
        }
        catch (Exception ex)
        {
            // 静默降级：绝不让 ABPS 兼容层把本 mod 其它维度带崩
            _log?.Warning($"[EventRotator][ABPS] D2 补写钩子挂载失败（已跳过，功能退化）: {ex.Message}");
        }
    }

    // ═══════════════ ★★★ v2.6.53【P0 修复】bossChance → ABPS 运行期配置 的重放 ═══════════════
    //
    // 【问题】装了 ABPS 时，"把某首领在某图的概率提到 N%" 这类活动**完全不生效**：
    //   ① ABPS `BossSpawns.cs` 每次重建首领表都执行
    //        `bossDefaultData[0].BossChance = bossData.SpawnChance[location];`
    //      ⇒ 我们在 location 表上写的 `BossChance` 被它**覆盖**；
    //   ② 本文件 D2 补写的口径是"只补缺 + 只补显示字段，不删不改别的"（见上方注释）
    //      ⇒ 不会重写**已存在**条目的概率（这是刻意的：那些条目归 ABPS 管）；
    //   ③ 唯一能改 ABPS 概率的通道 = 写它运行期对象的 `ModConfig.Config.BossConfig[boss].SpawnChance[map]`
    //      （`AbpsCompat.SetSpawnChance`）—— 但 `Plugin.cs` 只在 `OnLoadAsync` 调一次，
    //      那时 ABPS 的 `ModConfig.Config` 还没赋值 ⇒ 日志实证 780 次失败 / 0 次成功。
    //
    // 【修法·方案①（选它）】把同一次写入**重放到 ABPS 自己的 postfix 里**（ABPS 必定已初始化）。
    //   为什么选①而不是方案②（在 D2 里把概率重写到已存在条目）：
    //     · ② 写的是 location 表，而 ABPS 下次重建又会用它自己的值覆盖 ⇒ 只能保证"这一局"，且
    //       与 D2 "绝不改 ABPS 名下条目" 的口径冲突（那条口径是为了防 ABPS 与 mod 互相打架）；
    //     · ① 打的是**权威源**（ABPS 抽签就读它自己那份）⇒ 一次写入对**所有后续重建**都有效，
    //       且不需要碰任何不属于我们的条目 ✔
    // 【幂等】同一个 (map,boss) 反复写同值 ⇒ 无害；首次写入时把"还原闭包"留存，
    //   当活动不再声明该 (map,boss) 时**自动还原**（换/关活动不残留）。
    private static readonly Dictionary<string, Action> _abpsChanceRestore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _abpsChanceApplied = new(StringComparer.OrdinalIgnoreCase);

    private static void ReplayAbpsBossChances(string onlyMap)
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】ABPS 首领概率回放
        try
        {
            if (!AbpsCompat.IsPresent) return;
            var wantMap = string.IsNullOrEmpty(onlyMap) ? null : onlyMap.ToLowerInvariant();

            // ── 目标集合：当前所有生效活动声明的 bossChance ──
            //   ★ v2.6.53 探针实测教训：**必须保留原始大小写**！
            //     ABPS 的 `ModConfig.Config.BossConfig` 是从 JSON 反序列化出来的 `Dictionary`，
            //     默认比较器**大小写敏感**（`bc.Contains("bossbully")` = false）⇒ 小写化会
            //     ① 认不出已有键、② 用模板新建一个**小写重复键**（真正生效的仍是 ABPS 自己那条）
            //     ⇒ 概率看起来"写成功了"其实**完全没生效**。键空间一律用原样字符串，只把去重键小写。
            var want = new Dictionary<string, (string Boss, string Map, int Pct)>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in EventEngine.Actives)
            {
                var bc = ev?.BossChance;
                if (bc == null) continue;
                foreach (var mk in bc)
                {
                    var m = (mk.Key ?? "").Trim();
                    if (m.Length == 0) continue;
                    if (wantMap != null && !string.Equals(m, wantMap, StringComparison.OrdinalIgnoreCase)) continue;
                    if (mk.Value == null) continue;
                    foreach (var bk in mk.Value)
                    {
                        var boss = (bk.Key ?? "").Trim();
                        if (boss.Length == 0) continue;
                        try { want[m.ToLowerInvariant() + "|" + boss.ToLowerInvariant()] = (boss, m, (int)Math.Round(Convert.ToDouble(bk.Value))); }
                        catch { }
                    }
                }
            }
            // 补上"启动期写失败被暂存"的那些（Plugin.cs 的 OnLoadAsync 尝试）
            foreach (var pend in AbpsCompat.TakePendingChances())
            {
                var key = pend.Map.ToLowerInvariant() + "|" + pend.Boss.ToLowerInvariant();
                if (wantMap != null && !string.Equals(pend.Map, wantMap, StringComparison.OrdinalIgnoreCase)) continue;
                if (!want.ContainsKey(key)) want[key] = (pend.Boss, pend.Map, pend.Value);
            }

            // ── ① 撤销：上一轮写过、这一轮不再需要的 ⇒ 还原（活动切换/关闭不残留）──
            foreach (var key in _abpsChanceApplied.ToList())
            {
                if (want.ContainsKey(key)) continue;
                if (_abpsChanceRestore.TryGetValue(key, out var undo))
                {
                    try { undo(); } catch { }
                    _abpsChanceRestore.Remove(key);
                }
                _abpsChanceApplied.Remove(key);
                _log?.Info($"[EventRotator][ABPS] 撤销 bossChance：{key.Replace('|', ' ')}（活动已不声明）");
            }

            // ── ② 应用 + 回读自证 ──
            foreach (var kv in want)
            {
                var map = kv.Value.Map;
                var boss = kv.Value.Boss;                 // ★ 原样大小写
                var target = kv.Value.Pct;

                // ★★★ 缺陷2 修复（2026-10-24）：ABPS 的 `BossConfig` 是**强类型类**（19 个固定属性 + 一个对未知键
                //   会抛 KeyNotFoundException 的索引器），**没有任何按字符串键的 map** ⇒ 我们自建的
                //   `bossScourge` / `followerScourge` **结构上就写不进去**。
                //   旧行为：每局都打一条 `⚠ bossChance 未生效：laboratory bossScourge 目标 100，写入返回 False，回读 -1`
                //   ⇒ 那是"ABPS 的设计边界"，不是故障；而且把真正的故障（类型改名）淹没了。
                //   新行为：**静默降级** —— 跳过 ABPS 写入、不打 warning，只留一条 info（每进程一次）说明
                //   "ABPS 不支持自定义角色，按 SPT 原生 bossChance 生效"。
                //   ⚠ 原生 bossChance 确实生效：本 mod 的追加条目由 `BuildEntry` 直接把 events.json 的
                //     `bossChance[map][boss]` 写进 `BossLocationSpawn.BossChance`（`bossScourge` = 100），
                //     并且 ABPS 每局重建列表之后由 D2（`ReconcileBossListForMap`）把条目补回去。
                if (AbpsCompat.BossSlotInAbps(boss) == false)
                {
                    AbpsCompat.NoteUnsupportedBossOnce(boss, map);
                    continue;
                }

                var before = AbpsCompat.ReadSpawnChance(boss, map);      // -1 = 未就绪/无此键
                var capKey = kv.Key;
                bool ok = AbpsCompat.SetSpawnChance(boss, map, target,
                    a => { if (!_abpsChanceRestore.ContainsKey(capKey)) _abpsChanceRestore[capKey] = a; });
                var after = AbpsCompat.ReadSpawnChance(boss, map);        // ★ 回读自证
                if (ok && after == target)
                {
                    _abpsChanceApplied.Add(kv.Key);
                    // ★ v1.0.1【ABPS v2.1.0 兼容】成功行带上「走了哪条路」（旧版字典 / 新版强类型），
                    //   便于一眼确认到底命中的是新结构还是老结构。
                    _log?.Info($"[EventRotator][ABPS] ★ bossChance 已生效：{map} {boss} "
                             + $"{(before < 0 ? "(无此键)" : before.ToString())}→{target}（回读自证）"
                             + $"[{AbpsCompat.LastMode}]");
                }
                else
                {
                    // ★ v1.0.1【ABPS v2.1.0 兼容】失败行带上**具体原因**（AbpsCompat 记录的真实失败点：
                    //   找不到属性 / 索引器 / 值 null / map 未映射 …）—— 原来只有"可能已改名"这种猜测，
                    //   排查时得翻源码才知道到底哪一层断了。
                    _log?.Warning($"[EventRotator][ABPS] ⚠ bossChance 未生效：{map} {boss} 目标 {target}，"
                                + $"写入返回 {ok}，回读 {after}（ABPS 类型/字段可能已改名）"
                                + (string.IsNullOrEmpty(AbpsCompat.LastFailure) ? "" : $"｜原因：{AbpsCompat.LastFailure}"));
                }
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator][ABPS] bossChance 重放失败（已跳过）: {ex.Message}"); }
    PerfTimer.End("ABPS 概率回放(Replay)", t0);
    }

    /// <summary>ABPS `ReplaceOriginalLocationForMap(string location)` 的 postfix。</summary>
    private static void PostfixAbpsReplaceForMap(string location) => ReconcileActiveEventBosses(location);

    /// <summary>ABPS `ReplaceOriginalLocations()` 的 postfix（启动 + PBS 网页保存共用）。</summary>
    private static void PostfixAbpsReplaceAll() => ReconcileActiveEventBosses(null);

    /// <summary>
    /// 幂等补写。`onlyMap == null` = 全部图（`ReplaceOriginalLocations()` 的语义）。
    /// 每次 ABPS 重建后都调用；因为只"先判存在再 Add"，重复调用不会累积条目。
    /// <para>★ v2.6.9x：除"缺 ⇒ 追加"外，还会把活动声明的 boss 的
    /// `ShowOnTarkovMap`/`ShowOnTarkovMapPvE` **重新补成 true**（幂等、只写这两个 bool）——
    /// 这是"玩过 N 局之后地图选择界面骷髅仍在"的唯一保证（见方法内 P0 注释）。</para>
    /// </summary>
    private static void ReconcileActiveEventBosses(string onlyMap)
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】ABPS 应用（看频率：ABPS postfix 是否周期触发）
        try
        {
            // ★★★ v2.6.53【P0 修复】第一步就把 `bossChance` 重放进 ABPS 的运行期配置。
            //   为什么放在这里：本方法挂在 ABPS `MapSpawns.ReplaceOriginalLocations()` /
            //   `ReplaceOriginalLocationForMap(string)` 的 **postfix** 上 ⇒ 执行这一刻 **ABPS 必然已初始化**
            //   （它刚刚才写完地图首领表）。
            //   而 `Plugin.ApplyExtraDimensions` 里的那次写入发生在 `OnLoadAsync`，**早于** ABPS 的
            //   `ModConfig.OnLoadAsync`（ABPS 的 `Config` 那时还是 null）⇒ 日志实证 780 次
            //   "Config.BossConfig 拿不到（ABPS 可能尚未初始化）"、成功 0 次。
            //   ⇒ 概率提升类活动（permanent_boss_presence / boss_cultist_presence / meeting_of_the_bosses …）
            //     在装 ABPS 的机器上此前**全部不生效**（ABPS 每次重建都用它自己的
            //     `bossData.SpawnChance[location]` 覆盖条目 BossChance，见它的 BossSpawns.cs）。
            ReplayAbpsBossChances(onlyMap);

            if (_dbBaseByMap.Count == 0) return;                 // ApplyToDb 还没跑过 ⇒ 没得补
            var actives = EventEngine.Actives;
            if (actives == null || actives.Count == 0) return;

            var wantMap = string.IsNullOrEmpty(onlyMap) ? null : onlyMap.ToLowerInvariant();

            // ══════════ ★★★ v2.6.9x【P0 修复 · 骷髅标记每局必丢】目标集合**分两套**收 ══════════
            //  旧实现只按 `ev.BossSpawns` 收图、只按 `BossSpawns[map]` 收 boss ⇒ 对
            //  "**只提概率（bossChance）、不追加条目**"的活动整段空转。
            //  实例（用户实测 bug 的那一轮）：活动 = `bosses_take_a_hint` + `melee_event`，
            //  `bosses_take_a_hint` 的 9 张图**全部写在 `bossChance` 里、一条 `bossSpawns` 都没有**
            //  （见 events.json：bigmap/woods/shoreline/interchange/rezervbase/lighthouse/
            //   tarkovstreets/factory4_day/factory4_night）。
            //   ① DB 阶段（`ApplyToDb` → `ApplyOneEvent`）确实把 9 张图的
            //      `ShowOnTarkovMap`/`ShowOnTarkovMapPvE` 设成 true（boost 分支）；
            //   ② 玩完一局 → `/client/match/local/end` → ABPS `RaidFinished` → `RebuildMap(map)`
            //      → `MapSpawns.ReplaceOriginalLocationForMap` 把该图首领表**整体换成**
            //      `cloner.Clone(_botMapCache[map])`（ABPS `Defaults/Bosses.json` 里这些 boss 的
            //      ShowOnTarkovMap 是 **null/false**）⇒ 该图骷髅被冲掉；
            //   ③ 本方法作为 postfix 立刻被调用（日志实证：紧接着就打了 tarkovstreets 的
            //      bossChance 回放告警），但旧 `mapKeys` 来自 `BossSpawns` ⇒ **空集**
            //      ⇒ 在 `if (mapKeys.Count == 0) return;` 就返回 ⇒ 骷髅再也没补回来。
            //      铁证：spt20261017.log 全文 6.3MB、48 次启动、26 次 post 进图，
            //      `D2 幂等补写` **一次都没出现**。
            //  ⇒ 现在把"**要追加**的图/boss"（仍只认 bossSpawns，口径/行为一行不变）与
            //    "**要补显示字段**的图/boss"（bossSpawns ∪ bossChance ∪ autoBossChance ∪ forceAbpsSpawns）
            //    分开收。补标记走新函数 `MarkBossEntriesForMap`：**只写两个 bool，不增删条目、不改概率**。
            //    `zeroBossSpawns` 明确压 0 的 boss 一律排除（既然后台不刷，就不该在选图界面显示骷髅）。
            var addByMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);      // 缺 ⇒ 追加
            var markByMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);  // 已存在 ⇒ 补 true
            var zeroByMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);  // 明确压 0 ⇒ 绝不补 true
            var markWild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);                        // 声明了 "*"（全部真实首领）

            void WantMark(string map, string boss)
            {
                if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(boss)) return;
                var m = map.ToLowerInvariant();
                if (!_dbBaseByMap.ContainsKey(m)) return;
                if (!markByMap.TryGetValue(m, out var set))
                    markByMap[m] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(boss);
            }
            void WantAdd(string map, string boss)
            {
                if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(boss)) return;
                var m = map.ToLowerInvariant();
                if (!_dbBaseByMap.ContainsKey(m)) return;
                if (!addByMap.TryGetValue(m, out var l))
                    addByMap[m] = l = new List<string>();
                l.Add(boss);                                  // 同一 boss 多次声明也无害（追加前会重算 present）
            }
            void WantZero(string map, string boss)
            {
                if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(boss)) return;
                var m = map.ToLowerInvariant();
                if (!zeroByMap.TryGetValue(m, out var set))
                    zeroByMap[m] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(boss);
            }
            // 概率来源：`{ 地图: { boss 或 "*": 百分比 } }`。**只读**，绝不写概率。
            // 与 `ApplyOneEvent` 的 boost 口径逐条对齐：只处理 >0 的项；"*" = 全部真实首领（boss*）。
            void MarkFromBossChance(Dictionary<string, int> byBoss, string map)
            {
                if (byBoss == null) return;
                foreach (var bk in byBoss)
                {
                    var boss = (bk.Key ?? "").Trim();
                    if (boss.Length == 0) continue;
                    double pct;
                    try { pct = Convert.ToDouble(bk.Value); } catch { continue; }
                    if (pct <= 0) continue;                    // 与 boost 分支一致：0% 不打标记
                    if (boss == "*") markWild.Add(map);
                    else WantMark(map, boss);
                }
            }
            void MarkFromChanceTable(Dictionary<string, Dictionary<string, int>> src)
            {
                if (src == null) return;
                foreach (var kv in src)
                {
                    var m = (kv.Key ?? "").Trim();
                    if (m.Length == 0 || kv.Value == null) continue;
                    if (m == "*")
                    {
                        // 与 `ApplyOneEvent` 的 `chanceSrc.TryGetValue(mapKey) ?? chanceSrc["*"]` 回落一致
                        if (wantMap != null) MarkFromBossChance(kv.Value, wantMap);
                        else foreach (var mm in _dbBaseByMap.Keys) MarkFromBossChance(kv.Value, mm.ToLowerInvariant());
                        continue;
                    }
                    var ml = m.ToLowerInvariant();
                    if (wantMap != null && !string.Equals(ml, wantMap, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_dbBaseByMap.ContainsKey(ml)) continue;
                    MarkFromBossChance(kv.Value, ml);
                }
            }

            foreach (var ev in actives)
            {
                if (ev == null) continue;

                // ① bossSpawns：**既有行为**（缺就追加 + 已存在补两个显示字段），只多处理了一个 "*"
                //    （旧代码会把字面 "*" 当成 boss 名去 BuildEntry，属于潜伏 bug；现役 events.json 无人使用）
                if (ev.BossSpawns != null)
                {
                    foreach (var kv in ev.BossSpawns)
                    {
                        var mk = (kv.Key ?? "").ToLowerInvariant();
                        if (mk.Length == 0) continue;
                        if (wantMap != null && !string.Equals(mk, wantMap, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!_dbBaseByMap.ContainsKey(mk)) continue;
                        if (kv.Value == null) continue;
                        foreach (var b in kv.Value)
                        {
                            var bn = (b ?? "").Trim();
                            if (bn.Length == 0) continue;
                            if (bn == "*") { markWild.Add(mk); continue; }   // 通配 ⇒ 只补真实首领的标记，不追加字面条目
                            WantAdd(mk, bn);
                            WantMark(mk, bn);
                        }
                    }
                }

                // ② bossChance / autoBossChance：**只补显示字段**（条目与概率都归 ABPS，本方法不碰）
                MarkFromChanceTable(ev.RandomBossMaps ? RandomBossMapChance(ev) : ev.BossChance);
                MarkFromChanceTable(EventRotatorServer.AutoBossChanceOf(ev.Id));

                // ③ forceAbpsSpawns：ABPS 接管的敌种（只在声明了该图的条目上补标记）
                if (ev.ForceAbpsSpawns != null)
                {
                    foreach (var kv in ev.ForceAbpsSpawns)
                    {
                        var boss = (kv.Key ?? "").Trim();
                        if (boss.Length == 0 || kv.Value == null) continue;
                        foreach (var mm in kv.Value)
                        {
                            var ml = (mm ?? "").Trim().ToLowerInvariant();
                            if (ml.Length == 0) continue;
                            if (ml == "*")
                            {
                                if (wantMap != null) WantMark(wantMap, boss);
                                else foreach (var x in _dbBaseByMap.Keys) WantMark(x, boss);
                                continue;
                            }
                            if (wantMap != null && !string.Equals(ml, wantMap, StringComparison.OrdinalIgnoreCase)) continue;
                            WantMark(ml, boss);
                        }
                    }
                }

                // ④ zeroBossSpawns：这些 boss 本局被压成 0% ⇒ **绝不补 true**（不然又是"有骷髅、点进去没有"）
                if (ev.ZeroBossSpawns != null)
                {
                    foreach (var kv in ev.ZeroBossSpawns)
                    {
                        var ml = (kv.Key ?? "").Trim().ToLowerInvariant();
                        if (ml.Length == 0 || kv.Value == null) continue;
                        if (wantMap != null && !string.Equals(ml, wantMap, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var z in kv.Value) WantZero(ml, (z ?? "").Trim());
                    }
                }
            }

            // 要处理的图 = 两套目标的并集（同一张图只处理一次）
            var mapKeys = new List<string>();
            var mapKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void NeedMap(string m)
            {
                if (string.IsNullOrEmpty(m) || !_dbBaseByMap.ContainsKey(m)) return;
                if (mapKeySet.Add(m)) mapKeys.Add(m);
            }
            foreach (var m in addByMap.Keys) NeedMap(m);
            foreach (var m in markByMap.Keys) NeedMap(m);
            foreach (var m in markWild) NeedMap(m);
            // ★★★ A1（task-26）：普通 SCAV 抑制 —— **放在首领循环之前、且不受 `mapKeys.Count == 0` 的提前 return 影响**。
            //   因为 `mapKeys` 是按"首领目标"（bossSpawns / bossChance / forceAbpsSpawns）收的：只声明
            //   `scavSpawnSuppress` 而没有首领目标的活动会被下面整段跳过，所以必须在这里单独走一遍。
            //
            //   为什么必须在**本 postfix 里**做：ABPS `MapSpawns`
            //   （源码 Server/Controllers/MapSpawns.cs:170-171 与 :223-224）会
            //     `Base.Waves = cloner.Clone(_scavMapCache[map])`  **整体替换**该表；
            //   而本 postfix 正挂在它两个写入方法之后 ⇒ 只有在这里删 assault/marksman 才不会被冲掉。
            //   ⚠ 只写 SPT 自己的 DB 对象（`LocationBase`），**不碰 ABPS 的任何文件/对象**（用户硬约束）。
            //   `SuppressMap` 自带 try/catch 且只在活动声明了该图时才动手 ⇒ 不影响下面的 D2 首领补写。
            if (!EventScavSuppress.IsEmpty)
            {
                foreach (var kv in _dbBaseByMap)
                {
                    // ★ 用 SameMap（规范化比较，去下划线+小写）而不是裸字符串相等：
                    //   SPT 有两套地图键空间（`Base.Id` = `factory4_day` vs `GetDictionary()` 的
                    //   CLR 属性名 = `Factory4Day`），这个坑已经让 A1 的兜底路径空转过一次，这里防复发。
                    if (wantMap != null && !EventScavSuppress.SameMap(kv.Key, wantMap)) continue;
                    EventScavSuppress.SuppressMap(kv.Key, kv.Value);
                }
            }

            if (mapKeys.Count == 0) return;

            int added = 0, marked = 0;
            foreach (var mapKey in mapKeys)
            {
                var list = Prop(_dbBaseByMap[mapKey], "BossLocationSpawn") as IList;
                if (list == null) continue;

                // ① 追加：仍走纯函数 `ReconcileBossListForMap`（签名/语义不变，既有幂等验证探针照旧可用）
                if (addByMap.TryGetValue(mapKey, out var addWant) && addWant.Count > 0)
                {
                    var r = ReconcileBossListForMap(list, mapKey, addWant);
                    added += r.Added; marked += r.Marked;
                }

                // ② 补显示字段（幂等）：ABPS 重建后把活动声明的 boss 的两个 bool 重新补成 true。
                //    这是"第 N 次进图仍有骷髅"的唯一保证点 —— 每次 ABPS 重建都会重跑一次。
                marked += MarkBossEntriesForMap(list, mapKey,
                    markByMap.TryGetValue(mapKey, out var wantMarkNames) ? wantMarkNames : null,
                    markWild.Contains(mapKey),
                    zeroByMap.TryGetValue(mapKey, out var zeroNames) ? zeroNames : null);
            }

            if (added > 0 || marked > 0)
            {
                _abpsReconcileOps++;
                // 限流：第 1 次 + 之后每 20 次打一条，避免每局刷屏
                if (_abpsReconcileOps == 1 || _abpsReconcileOps % 20 == 0)
                    _log?.Info($"[EventRotator][ABPS] D2 幂等补写 #{_abpsReconcileOps}：map={(wantMap ?? "全部")} 追加 {added} 条 / 补骷髅 {marked} 条"
                             + "（ABPS 重建后会冲掉本 mod 的 DB 追加，这里补回；只补缺不删不改）");
            }
        }
        catch (Exception ex)
        {
            // ★ 静默降级要做到**极致**：连"记一条日志"本身都不许抛出去。
            //   实测教训（本补写的反射探针）：SPT 的 ISptLogger 实现链会牵出
            //   `Microsoft.Extensions.Logging.Abstractions`；若那个程序集因为任何原因解析不到，
            //   `_log.Warning(...)` 自己会抛 FileNotFoundException ——
            //   于是"被 catch 住的异常"会以**新异常**的形式逃出本方法，
            //   而这个 postfix 挂在 ABPS 的重建链上 ⇒ 会把 ABPS 一起带崩。
            SafeWarn($"[EventRotator][ABPS] D2 幂等补写失败（已跳过）: {ex.GetType().Name}");
        }
    PerfTimer.End("ABPS 应用(Reconcile)", t0);
    }

    /// <summary>
    /// 连日志都不许抛的告警（D2 降级路径专用）。<see cref="ISptLogger{T}.Warning(string, Exception)"/> 的实现链
    /// 会牵出 <c>Microsoft.Extensions.Logging.Abstractions</c>；解析不到时调用它会抛异常。
    /// </summary>
    private static void SafeWarn(string msg)
    {
        try { _log?.Warning(msg); } catch { }
    }

    /// <summary>
    /// ★ D2 核心（**纯函数：无日志、无静态状态**，可被探针直接反射验证幂等性）：
    /// 对**一张图**的 DB 首领表做幂等补写。`want` = 本次活动在该图声明的 boss 名。
    ///   · 缺 ⇒ <see cref="BuildEntry"/> 追加（它内部已设两个显示字段）
    ///   · 已存在 ⇒ **只补两个显示字段**，不动 BossChance / BossZone / 护卫
    /// 关键不变式：**进门先自己重算 `present`，每次 Add 后同步 `present.Add`**
    ///   ⇒ 同一张表上重复调用 N 次，同一 boss 也只会存在 1 条（不会逐局累积）。
    /// </summary>
    private static (int Added, int Marked) ReconcileBossListForMap(IList list, string mapKey, List<string> want)
    {
        int added = 0, marked = 0;
        if (list == null || want == null || want.Count == 0) return (0, 0);

        var elemType = list.GetType().GetGenericArguments().FirstOrDefault();
        if (elemType == null) return (0, 0);

        var present = ExistingBossNames(list);          // ★ 每次调用都重新算（幂等的第一半）

        foreach (var boss in want)
        {
            if (string.IsNullOrWhiteSpace(boss)) continue;

            if (present.Contains(boss))
            {
                foreach (var it in list)
                {
                    if (!string.Equals(Prop(it, "BossName")?.ToString(), boss, StringComparison.OrdinalIgnoreCase)) continue;
                    SetOriginal(it, "ShowOnTarkovMap", true);
                    SetOriginal(it, "ShowOnTarkovMapPvE", true);
                    marked++;
                }
                continue;
            }

            // 缺 ⇒ 追加（沿用既有"地图被地图类 mod 接管则跳过"约定）
            if (Compat.MapModOwnsMap(mapKey)) continue;
            var entry = BuildEntry(elemType, boss, mapKey);
            if (entry == null) continue;
            list.Add(entry);
            present.Add(boss);                          // ★ 幂等的第二半：立刻登记，防止同批重复 Add
            added++;
        }
        return (added, marked);
    }

    /// <summary>
    /// ★★★ v2.6.9x【P0 修复 · 骷髅标记持续存在】D2 的**纯"显示字段"修复**（纯函数：无日志、无静态状态）。
    /// 专治"活动只提概率（`bossChance`）、不追加条目"的情形 —— ABPS 每局结束用
    /// `cloner.Clone(_botMapCache[map])` **整体替换** `Base.BossLocationSpawn` 后，
    /// 那些条目的 `ShowOnTarkovMap`/`ShowOnTarkovMapPvE` 变成 ABPS 默认值（null/false），
    /// 本函数把活动声明的那些 boss 的两个字段重新补成 true。
    ///
    /// <para>只写这两个 bool：**不 Add、不 Remove、不改 BossChance/BossZone/护卫** ⇒ 不改变玩法行为。</para>
    /// <para>幂等：已经两个都为 true 的条目直接跳过（不重复反射写入）；同一张表调用 N 次结果一致。</para>
    /// </summary>
    /// <param name="list">该图的 DB 首领表。</param>
    /// <param name="mapKey">地图键（仅用于日志/调试时定位，本函数不写它）。</param>
    /// <param name="names">需要显示骷髅的 boss 名（`bossSpawns` ∪ `bossChance` ∪ `autoBossChance` ∪ `forceAbpsSpawns`）。</param>
    /// <param name="allRealBosses">该图是否声明了通配 "*"（= 全部 `boss*` 首领）。</param>
    /// <param name="exclude">活动明确压成 0% 的 boss（`zeroBossSpawns`）—— 绝不补 true。</param>
    /// <returns>实际被改写的条目数（已经是 true 的不计）。</returns>
    private static int MarkBossEntriesForMap(IList list, string mapKey, HashSet<string> names,
                                            bool allRealBosses, HashSet<string> exclude)
    {
        if (list == null) return 0;

        int marked = 0;
        foreach (var it in list)
        {
            var name = Prop(it, "BossName")?.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            if (exclude != null && exclude.Contains(name)) continue;   // 压 0 的不显示（与 ZeroBossSpawns 分支一致）
            bool want = allRealBosses ? IsRealBoss(name) : (names != null && names.Contains(name));
            if (!want) continue;

            // 幂等：两个都已经 true ⇒ 不写（ABPS 每局重建后的第一轮会写，之后的重建再写一次，开销可忽略）
            if (MarkOf(it, "ShowOnTarkovMap") > 0.5 && MarkOf(it, "ShowOnTarkovMapPvE") > 0.5) continue;

            SetOriginal(it, "ShowOnTarkovMap", true);
            SetOriginal(it, "ShowOnTarkovMapPvE", true);
            marked++;
        }
        return marked;
    }

    /// <summary>当前表里已存在的 BossName 集合（大小写不敏感）。</summary>
    private static HashSet<string> ExistingBossNames(IList list)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in list)
        {
            var n = Prop(it, "BossName")?.ToString();
            if (!string.IsNullOrEmpty(n)) set.Add(n);
        }
        return set;
    }

    /// <summary>按 namespace 片段找类型（兜底：ABPS 程序集里 <c>Name</c> 唯一的类型）。</summary>
    private static Type FindTypeIn(string namespacePart, string simpleName)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(x => x != null && x.Name == simpleName
                                     && (x.Namespace ?? "").Contains(namespacePart, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>★ v2.6.79【关键优化】`Prop` 的成员缓存（与 G1a/G1b/O1 同款手法）。
    /// 原来**每次调用**都 `GetType().GetProperty(name, BindingFlags…)` —— 在"遍历首领表"的循环里
    /// **每活动 × 每条目 × 数次** ⇒ 12 活动一局可达上千次反射 ⇒ 实测 7~9ms/活动的主要来源。
    /// **行为不变**：同 `Type`+名字 ⇒ 同 `PropertyInfo` ⇒ 同 `GetValue` 结果；`BindingFlags` 逐字照抄；
    /// 名字找不到 ⇒ 缓存 null ⇒ 返回 null（与原 `?.` 完全一致）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), System.Reflection.PropertyInfo> _propCache3 = new();

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        // ★ v2.6.80【Lead 裁决】去掉 `try/catch`，恢复**严格等价**：
        //   · 只缓存 `PropertyInfo`（找不到 ⇒ 缓存 null ⇒ `pi?.GetValue` 返回 null，与原 `?.` 逐字等价）；
        //   · **`GetValue` 的异常照旧向上抛**（原来是表达式体、无 catch）⇒ 行为与优化前完全一致；
        //   · `GetProperty` 自身抛异常（如 AmbiguousMatchException）也照旧传播（`GetOrAdd` 不缓存异常）。
        var pi = _propCache3.GetOrAdd((o.GetType(), name),
            static k => k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance));
        return pi?.GetValue(o);
    }

    /// <summary>
    /// 【敌对性】把活动指定的"新敌种"补进该地图的 BotLocationModifier.AdditionalHostilitySettings。
    /// 为什么需要：SPT 原版地图的敌对表只列了原版 bot（woods 只有 pmcBEAR/pmcUSEC/bossKojaniy/assault），
    /// 新敌种（如 arenaFighterEvent 走私者）**没有条目** → 客户端按默认处理 → 对玩家中立 → 站着不打人。
    /// ABPS 又 patch 掉了 SPT 的 SeasonalEventService.ReplaceBotHostility（Prefix return false），
    /// 所以这里必须自己补。补的规则：对三派系玩家(PMC/SCAV)行为 = Enemies，并互相敌对。
    ///
    /// 全部改动**只发生在本 mod 内**（改内存里的地图数据 + 记原值用于还原），不写 ABPS 的任何文件/配置。
    /// </summary>
    /// <summary>
    /// ★★★ v0.1.5【结盟】让 mapKey 图上的 A 与 B **互为友军**：
    /// 在 `BotLocationModifier.AdditionalHostilitySettings` 里把两者的 `AlwaysFriends` 补上对方、
    /// 并从 `AlwaysEnemies` 里移除对方（缺条目就克隆一条现成的改）。全部登记还原。
    /// </summary>
    private static void ApplyAllyPairs(object mapBase, string mapKey, EventDef ev)
    {
        try
        {
            var pairs = ev?.ForceAllyPairs;
            if (pairs == null || pairs.Count == 0) return;
            var blm = Prop(mapBase, "BotLocationModifier");
            var ahs = blm != null ? Prop(blm, "AdditionalHostilitySettings") as IList : null;
            if (ahs == null || ahs.Count == 0) { _log?.Warning($"[EventRotator][结盟] {mapKey}: 没有 AdditionalHostilitySettings"); return; }

            // ★ 模板选择（用户要求：别把角色改成 PMC 式行为）：
            //   优先同图**其它 boss** 的条目 → 其次 assault → 最后才 ahs[0]
            object tpl = null; string tplRole = null;
            foreach (var e in ahs)
            {
                var r = Prop(e, "BotRole")?.ToString() ?? "";
                if (r.StartsWith("boss", StringComparison.OrdinalIgnoreCase)) { tpl = e; tplRole = r; break; }
            }
            if (tpl == null)
                foreach (var e in ahs)
                    if (string.Equals(Prop(e, "BotRole")?.ToString(), "assault", StringComparison.OrdinalIgnoreCase)) { tpl = e; tplRole = "assault"; break; }
            if (tpl == null) { tpl = ahs[0]; tplRole = Prop(tpl, "BotRole")?.ToString(); }
            _log?.Info($"[EventRotator][结盟] {mapKey}: 新条目模板 = '{tplRole}'（同图 boss 优先；不用 PMC 条目，避免行为被改成 PMC 式）");
            var elemType = ahs.GetType().GetGenericArguments().FirstOrDefault() ?? tpl.GetType();

            foreach (var pr in pairs)
            {
                if (pr == null) continue;
                if (!string.Equals((pr.Map ?? "").Trim(), mapKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(pr.A) || string.IsNullOrEmpty(pr.B)) continue;

                foreach (var who in new[] { pr.A, pr.B })
                {
                    var other = string.Equals(who, pr.A, StringComparison.OrdinalIgnoreCase) ? pr.B : pr.A;
                    object entry = null;
                    foreach (var e in ahs)
                        if (string.Equals(Prop(e, "BotRole")?.ToString(), who, StringComparison.OrdinalIgnoreCase)) { entry = e; break; }
                    bool created = false;
                    if (entry == null)
                    {
                        entry = Activator.CreateInstance(elemType);
                        foreach (var pi in elemType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                            try { pi.SetValue(entry, pi.GetValue(tpl)); } catch { }
                        }
                        try { SetOriginal(entry, "BotRole", who); } catch { }
                        // 还原交给本类已有的 RestoreHostility()（它全量还原 AdditionalHostilitySettings）
                        ahs.Add(entry);
                        created = true;
                    }

                    // AlwaysFriends += {自己, 对方}
                    var friends = Prop(entry, "AlwaysFriends") as System.Collections.IEnumerable;
                    var newFriends = new List<string>();
                    if (friends != null) foreach (var f in friends) { var s = f?.ToString(); if (!string.IsNullOrEmpty(s)) newFriends.Add(s); }
                    var addList = new List<string> { who, other };
                    // ★ 把对方及其随从都加进友军（否则护卫组内部还会互殴）
                    foreach (var e2 in ahs)
                    {
                        var r2 = Prop(e2, "BotRole")?.ToString() ?? "";
                        if (string.IsNullOrEmpty(r2)) continue;
                        bool sameSide = r2.StartsWith(other, StringComparison.OrdinalIgnoreCase)
                                     || r2.StartsWith("follower" + other.Replace("boss", ""), StringComparison.OrdinalIgnoreCase)
                                     || r2.StartsWith("follower", StringComparison.OrdinalIgnoreCase) && other.IndexOf(r2.Replace("follower", ""), StringComparison.OrdinalIgnoreCase) >= 0;
                        if (sameSide) addList.Add(r2);
                    }
                    foreach (var x in addList)
                        if (!newFriends.Any(y => string.Equals(y, x, StringComparison.OrdinalIgnoreCase))) newFriends.Add(x);
                    SetOriginal(entry, "AlwaysFriends", new HashSet<string>(newFriends, StringComparer.OrdinalIgnoreCase));

                    // AlwaysEnemies -= 对方
                    if (Prop(entry, "AlwaysEnemies") is System.Collections.IEnumerable en)
                    {
                        var list = new List<string>();
                        foreach (var x in en) { var s = x?.ToString(); if (!string.IsNullOrEmpty(s) && !string.Equals(s, other, StringComparison.OrdinalIgnoreCase)) list.Add(s); }
                        SetOriginal(entry, "AlwaysEnemies", new HashSet<string>(list, StringComparer.OrdinalIgnoreCase));
                    }
                    _log?.Info($"[EventRotator][结盟] {mapKey}: '{who}' ⇄ '{other}' 互为友军"
                             + (created ? $"（新建条目，模板={tplRole}）" : "（改写已有条目）")
                             + "，已从 AlwaysEnemies 移除对方；**其它字段（含玩家行为）一律沿用模板/原值不动**（可还原）");
                }
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator][结盟] 失败: {ex.Message}"); }
    }

    private static void ApplyHostility(object mapBase, string mapKey, EventDef ev)
    {
        try
        {
            if (ev?.ForceHostility == null || ev.ForceHostility.Count == 0) return;

            // 该图需要强制的角色
            var wantRoles = new List<string>();
            foreach (var kv in ev.ForceHostility)
                if (kv.Value != null && kv.Value.Any(m => string.Equals((m ?? "").Trim().ToLowerInvariant(), mapKey, StringComparison.OrdinalIgnoreCase)))
                    wantRoles.Add(kv.Key);
            if (wantRoles.Count == 0) return;

            var blm = Prop(mapBase, "BotLocationModifier");
            if (blm == null) { _log?.Warning($"[EventRotator] {mapKey}: 没有 BotLocationModifier，无法设置敌对性"); return; }
            var ahs = Prop(blm, "AdditionalHostilitySettings") as IList;
            if (ahs == null) { _log?.Warning($"[EventRotator] {mapKey}: 没有 AdditionalHostilitySettings，无法设置敌对性"); return; }

            var elemType = ahs.GetType().GetGenericArguments().FirstOrDefault();
            if (elemType == null) return;

            foreach (var role in wantRoles)
            {
                // 找现有条目（按 BotRole）
                object existing = null;
                foreach (var e in ahs)
                    if (string.Equals(Prop(e, "BotRole") as string, role, StringComparison.OrdinalIgnoreCase)) { existing = e; break; }

                var key = mapKey + "|" + role;
                if (existing != null)
                {
                    // 已有 → 归一化成"除自己以外全敌对"，原值记下用于还原
                    if (!_hostOrig.ContainsKey(key))
                        _hostOrig[key] = Prop(existing, "SavagePlayerBehaviour") as string == "AlwaysEnemies"
                                         && Prop(existing, "BearPlayerBehaviour") as string == "AlwaysEnemies"
                                         && Prop(existing, "UsecPlayerBehaviour") as string == "AlwaysEnemies";
                    // ★★★【P0 修复】`SetHostBehaviour` 里现在**连 5 个名单字段一起登记原值** ⇒
                    //   必须先快照、再改写（旧实现是"先写 AlwaysEnemies 等 5 个字段、后快照"
                    //   ⇒ 那 5 个字段的快照值会等于"已改过的值"，还原等于没还原）。
                    SetHostBehaviour(existing, role);
                    SetOriginal(existing, "AlwaysEnemies", HostileSetFor(role, out var note1));
                    SetOriginal(existing, "AlwaysFriends", FactionMates(role));
                    SetOriginal(existing, "Neutral", NeutralPerOfficial(role));
                    SetOriginal(existing, "Warn", new List<string>());
                    SetOriginal(existing, "ChancedEnemies", null);
                    if (!_hostTouched.Contains(key)) { _hostTouched.Add(key); _log?.Info($"[EventRotator] {mapKey}: 敌对性 → '{role}' 按【{note1}】设置（原条目已存在；副手/同族=" + string.Join(",", FactionMates(role)) + "）"); }
                }
                else
                {
                    // 没有 → 复制"最像"的模板追加（优先 pmcBEAR，兜底 assault），再改成目标角色
                    object tpl = null;
                    foreach (var e in ahs) if (string.Equals(Prop(e, "BotRole") as string, "pmcBEAR", StringComparison.OrdinalIgnoreCase)) { tpl = e; break; }
                    foreach (var e in ahs) { if (tpl == null) tpl = e; }
                    var clone = Clone(tpl);
                    object ne = clone;
                    if (ne == null)
                    {
                        try { ne = Activator.CreateInstance(elemType); } catch { }
                        if (ne == null) { _log?.Warning($"[EventRotator] {mapKey}: 无法创建 hostility 条目"); continue; }
                    }
                    // 从玩家互打的角度重建：敌人名单**按官方表**（有的话）+ **同族绝不为敌**
                    SetHostBehaviour(ne, role);            // ★ 先快照（含 5 个名单字段）再改写
                    SetOriginal(ne, "BotRole", role);
                    SetOriginal(ne, "AlwaysEnemies", HostileSetFor(role, out var note2));
                    SetOriginal(ne, "AlwaysFriends", FactionMates(role));
                    SetOriginal(ne, "Neutral", NeutralPerOfficial(role));
                    SetOriginal(ne, "Warn", new List<string>());
                    SetOriginal(ne, "ChancedEnemies", null);
                    ahs.Add(ne);
                    _hostAdded.Add(key);
                    _log?.Info($"[EventRotator] {mapKey}: ★补敌对性条目 '{role}'：按【{note2}】设置（同族友军=" + string.Join(",", FactionMates(role)) + "）");
                }
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] {mapKey}: ApplyHostility 失败: {ex.Message}"); }
    }

    /// <summary>
    /// "除自己以外全部敌对"的名单。取自各图 AdditionalHostilitySettings 里 pmcBEAR 条目的
    /// AlwaysEnemies + Neutral 并集（覆盖原版所有 bot 角色），再补上玩家派系名与常见活动敌种。
    /// 注意：**不含自身角色** → 同类之间不会互殴。
    /// </summary>
    /// <summary>
    /// ★ 本体的**全部** bot 类型名（启动时从 database/bots/types/*.json 读一次）。
    ///   用于把 OthersHostileSet 做成"动态补全"，避免手写名单漏项。
    ///   实证教训：手写名单漏了 skier / sectantoni / sectantpredvestnik / sectantprizrak /
    ///   shooterbtr / bear / usec → 这些角色被设成敌对时只打玩家，**跟别的 AI 相遇不互打**。
    /// </summary>
    private static HashSet<string> _allBotTypes;

    /// <summary>让 Plugin 在启动时把 mod 目录传进来，以便读取 bot 类型表。</summary>
    public static string DataDirHint;

    private static HashSet<string> AllBotTypes()
    {
        if (_allBotTypes != null) return _allBotTypes;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var root in new[] { DataDirHint, AppContext.BaseDirectory })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var dir = System.IO.Path.Combine(root, "SPT_Data", "database", "bots", "types");
                if (!System.IO.Directory.Exists(dir))
                    dir = System.IO.Path.Combine(root, "database", "bots", "types");
                if (!System.IO.Directory.Exists(dir)) continue;
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.json"))
                    set.Add(System.IO.Path.GetFileNameWithoutExtension(f));
                if (set.Count > 0) break;
            }
        }
        catch (Exception ex) { _log?.Warning("[EventRotator] 读 bot 类型表失败: " + ex.Message); }
        _allBotTypes = set;
        return set;
    }

    private static HashSet<string> OthersHostileSet(string self)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 玩家三派系名（覆盖 Usec/Bear/Savage 三种玩家身份）
            "pmcUSEC", "pmcBEAR", "assault", "marksman", "pmcBot",
            // 玩家阵营本体 bot 类型（本体里 Mind=AlwaysEnemies）
            "bear", "usec",
            // 常见活动/新敌种
            "arenaFighterEvent", "arenaFighter", "crazyAssaultEvent", "cursedAssault", "exUsec",
            "infectedAssault", "infectedCivil", "infectedLaborant", "infectedPmc", "infectedTagilla",
            "gifter", "peacemaker",
            "skier",                                    // ★ 补：配件商 Skier 小队
            // ★ v0.1.3 修正大小写：SPT 4.1.6 的 WildSpawnType 里是这三个精确拼写
            //   （旧写成 sectantoni / sectantpredvestnik / sectantprizrak / shooterbtr，
            //    虽然 HashSet 大小写不敏感、但写到客户端 JSON 时会变成非法枚举名）
            "sectantOni", "sectantPredvestnik", "sectantPrizrak",
            "shooterBTR",                               // ★ 补：BTR 射手
            // ★ v0.1.3 删除 6 个 SPT 4.1.6 里**不存在**的值（写进 AlwaysEnemies 就是非法枚举名，
            //   有把整张地图数据反序列化搞崩的风险 —— 本 mod 的 EnumGuard 就记录过同类事故）：
            //     bloodhound / pillager / untar / cleanupcrew / sectactPriestEvent / spiritSpring / spiritWinter
            //   其中 untar_recon 活动想要的 "untar" 敌种本体确实不存在（已知缺口）。
        };
        // 原版首领 + 追随者
        foreach (var b in new[]
        {
            "bossBully", "bossBoar", "bossBoarSniper", "bossTagilla", "bossTagillaAgro", "bossKilla", "bossKillaAgro",
            "bossKojaniy", "bossSanitar", "bossKolontay", "bossGluhar", "bossKnight", "bossPartisan", "bossZryachiy",
            "followerBigPipe", "followerBirdEye", "followerBoar", "followerBoarClose1", "followerBoarClose2",
            "followerBully", "followerGluharAssault", "followerGluharScout", "followerGluharSecurity", "followerGluharSnipe",
            "followerKojaniy", "followerKolontayAssault", "followerKolontaySecurity", "followerSanitar",
            "followerTagilla", "followerZryachiy", "tagillaHelperAgro",
            "sectantPriest", "sectantWarrior",
            "peacefullZryachiyEvent", "ravangeZryachiyEvent",
        }) set.Add(b);

        // ★ 动态补全：把本体实际存在的所有 bot 类型都算成"敌对对象"
        //   （避免以后 BSG 加新敌种时又漏——本次就是这样漏了 skier 等 7 个）
        var all = AllBotTypes();
        if (all.Count > 0)
        {
            // 只补"看起来是战斗单位"的：排除明显非战斗/中立辅助
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "gifter", "peacefullZryachiyEvent", "ravangeZryachiyEvent", "bossZryachiy", "followerZryachiy",
            };
            foreach (var t in all)
                if (!skip.Contains(t)) set.Add(t);
        }

        set.Remove(self);       // ★ 只认自己人
        return set;
    }

    // ═══════════ ★★★ v2.6.53【P1-2 修复】敌对性：按官方表 + 同族保护 ═══════════
    //
    // 【原 bug】`forceHostility` 一律写 "除自己以外**全**敌对"（`OthersHostileSet`）⇒ 两个后果：
    //   ① **自相残杀**：邪教（`sectantPredvestnik`/`sectantOni`/`sectantPrizrak`）的敌人表里
    //      含 `sectantPriest`/`sectantWarrior`（`:1099` 硬编码 + `:1105-1114` 动态补全把**所有** bot 类型塞进去）
    //      ⇒ 邪教小队打自己的牧师；同理 `bossGluhar` 会打 `followerGluhar*`、`peacemaker` 打 peacemaker。
    //   ② **与官方表相反**：官方 `seasonalevents.json → hostilitySettingsForEvent.zombies.default` 里
    //      `sectantPriest`/`sectantWarrior` 是
    //        `AlwaysEnemies = [infectedAssault, infectedPmc, infectedCivil, infectedLaborant, infectedTagilla]`
    //        `Neutral = [……48 条：marksman/assault/bossBully/bossKilla/bossGluhar/sectantWarrior/sectantPriest/…]`
    //      即「**只对僵尸敌对、对所有人中立**」，而不是"对所有人敌对"。
    //
    // 【修法】① 官方表里有该角色 ⇒ 敌/中立名单**照官方**（只保留本机真实存在的 bot 类型名，防非法枚举名
    //          把客户端反序列化搞崩 —— 官方表里有 `spiritWinter`/`spiritSpring`/`sectactPriestEvent`
    //          这类 SPT 4.1.6 并不存在的名字）；再叠一层"同族绝不入敌人表"的保护。
    //        ② 官方表里没有的角色（`arenaFighterEvent`/`ravangeZryachiyEvent`/`bankGluhar` 等）⇒ 保持
    //          "除**同族**以外全敌对"（比原来多一层同族排除）。
    //   ⚠ 与官方的**唯一刻意偏离**：官方把三个 `*PlayerBehaviour` 设成 `Neutral`（万圣节里邪教不主动打玩家），
    //     而本 mod 的活动语义是"邪教/邪教徒是敌人"⇒ 这三个字段**保持 AlwaysEnemies**（不跟随官方）。
    //     想改成完全照官方，只需把 `KeepPlayersHostile` 置 false。
    private const bool KeepPlayersHostile = true;

    /// <summary>"家族"归并：首领↔随从、邪教↔邪教、三狗帮、僵尸、劫掠者… 同族之间**绝不为敌**。</summary>
    private static string FactionOf(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "";
        var r = role.Trim().ToLowerInvariant();
        if (r.StartsWith("sectant") || r == "sectactpriestevent") return "sectant";
        if (r.StartsWith("infected")) return "infected";
        if (r.StartsWith("bossknight") || r.StartsWith("followerbigpipe") || r.StartsWith("followerbirdeye")
            || r == "exusec") return "goons";
        if (r.StartsWith("bosstagilla") || r.StartsWith("followertagilla") || r.StartsWith("tagillahelper")) return "tagilla";
        if (r.StartsWith("bosskilla")) return "killa";
        if (r.StartsWith("arenafighter")) return "arenafighter";
        if (r.StartsWith("crazyassault") || r.StartsWith("cursedassault")) return "crazyassault";
        if (r.StartsWith("bosszryachiy") || r.StartsWith("followerzryachiy")
            || r.StartsWith("ravangezryachiyevent") || r.StartsWith("peacefullzryachiyevent")) return "zryachiy";
        if (r.StartsWith("peacemaker")) return "peacemaker";
        if (r.StartsWith("skier")) return "skier";
        if (r.StartsWith("follower")) return "fll" + r.Substring(8);   // followerGluharAssault → fllgluharassault
        if (r.StartsWith("boss")) return "boss" + r.Substring(4);      // bossGluhar → bossgluhar
        return r;
    }

    private static bool SameFaction(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var fa = FactionOf(a); var fb = FactionOf(b);
        if (fa.Length == 0 || fb.Length == 0) return false;
        if (string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)) return true;
        // 首领 ↔ 随从：`bossgluhar` vs `fllgluharassault` / `fllgluharscout` / `fllboarclose1` …
        //   ⇒ 去掉 `boss`/`fll` 前缀后做**前缀匹配**（随从名通常带 Assault/Security/Scout/Close1 后缀）。
        //   ⚠ v2.6.53 探针实测：**相等比较在这里必然失败**（"gluhar" ≠ "gluharassault"）
        //     ⇒ 会退化成"首领把自己的护卫当敌人"。必须用 StartsWith。
        var ca = fa.StartsWith("boss") ? fa.Substring(4) : (fa.StartsWith("fll") ? fa.Substring(3) : null);
        var cb = fb.StartsWith("boss") ? fb.Substring(4) : (fb.StartsWith("fll") ? fb.Substring(3) : null);
        if (ca == null || cb == null || ca.Length < 3 || cb.Length < 3) return false;
        return ca.StartsWith(cb, StringComparison.OrdinalIgnoreCase)
            || cb.StartsWith(ca, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>本族成员（含自己）—— 写进 `AlwaysFriends`，避免"首领打自己护卫"。</summary>
    private static HashSet<string> FactionMates(string role)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { role };
        var all = AllBotTypes();
        foreach (var t in all) if (SameFaction(t, role)) set.Add(t);
        foreach (var b in new[]
        {
            "bossBully", "bossBoar", "bossBoarSniper", "bossTagilla", "bossTagillaAgro", "bossKilla", "bossKillaAgro",
            "bossKojaniy", "bossSanitar", "bossKolontay", "bossGluhar", "bossKnight", "bossPartisan", "bossZryachiy",
            "followerBigPipe", "followerBirdEye", "followerBoar", "followerBoarClose1", "followerBoarClose2",
            "followerBully", "followerGluharAssault", "followerGluharScout", "followerGluharSecurity", "followerGluharSnipe",
            "followerKojaniy", "followerKolontayAssault", "followerKolontaySecurity", "followerSanitar",
            "followerTagilla", "followerZryachiy", "tagillaHelperAgro",
            "sectantPriest", "sectantWarrior", "sectantOni", "sectantPredvestnik", "sectantPrizrak",
        }) if (SameFaction(b, role)) set.Add(b);
        set.Remove(null);
        return set;
    }

    /// <summary>只保留"本机真实存在的角色名"，防非法枚举名把客户端反序列化搞崩。</summary>
    private static HashSet<string> FilterValidRoles(IEnumerable<string> src)
    {
        var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pmcUSEC", "pmcBEAR", "bear", "usec" };
        var all = AllBotTypes();
        foreach (var t in all) ok.Add(t);
        var outSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (src == null) return outSet;
        foreach (var s in src) if (!string.IsNullOrWhiteSpace(s) && ok.Contains(s.Trim())) outSet.Add(s.Trim());
        return outSet;
    }

    private static Dictionary<string, object> _officialHostByRole;
    private static object _officialHostSrc;

    /// <summary>官方 `hostilitySettingsForEvent.*` 的 role → 条目索引（只读，来自 Plugin 注入的官方配置副本）。</summary>
    private static bool TryGetOfficialHostility(string role, out object entry)
    {
        entry = null;
        try
        {
            var src = ZombieHostilityByMap;
            if (src == null || src.Count == 0) return false;
            if (_officialHostByRole == null || !ReferenceEquals(_officialHostSrc, src))
            {
                var idx = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in src)
                    foreach (var e in kv.Value ?? new List<object>())
                    {
                        var r = Prop(e, "BotRole") as string;
                        if (!string.IsNullOrWhiteSpace(r) && !idx.ContainsKey(r)) idx[r] = e;
                    }
                _officialHostByRole = idx; _officialHostSrc = src;
            }
            if (_officialHostByRole.TryGetValue(role, out var found) && found != null) { entry = found; return true; }
        }
        catch { }
        return false;
    }

    private static IEnumerable<string> RolesOf(object e, string field)
    {
        try
        {
            var v = Prop(e, field);
            if (v is IEnumerable<string> ss) return ss;
            if (v is System.Collections.IEnumerable en)
            {
                var list = new List<string>();
                foreach (var x in en) if (x != null) list.Add(x.ToString());
                return list;
            }
        }
        catch { }
        return Array.Empty<string>();
    }

    /// <summary>按官方表（若有）+ 同族保护，算出该角色的"敌人名单"。</summary>
    private static HashSet<string> HostileSetFor(string role, out string note)
    {
        try
        {
            if (TryGetOfficialHostility(role, out var oe))
            {
                var enemies = FilterValidRoles(RolesOf(oe, "AlwaysEnemies"));
                enemies.RemoveWhere(x => SameFaction(x, role));
                note = "官方表(hostilitySettingsForEvent)";
                return enemies;
            }
        }
        catch { }
        var set = OthersHostileSet(role);
        set.RemoveWhere(x => SameFaction(x, role));      // ★ 同族绝不为敌（原 bug 的核心）
        note = "自建：除同族外全敌对";
        return set;
    }

    /// <summary>官方表里的"中立名单"（已过滤成本机合法角色名；没有官方条目时返回空集）。</summary>
    private static HashSet<string> NeutralPerOfficial(string role)
    {
        try
        {
            if (TryGetOfficialHostility(role, out var oe))
            {
                var n = FilterValidRoles(RolesOf(oe, "Neutral"));
                n.RemoveWhere(x => SameFaction(x, role));    // 同族另有 AlwaysFriends 兜着
                return n;
            }
        }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 设置"对玩家"的行为。
    /// ⚠ 合法值来自客户端枚举 EFT.EWarnBehaviour：
    ///   Default / Neutral / Warn / **AlwaysEnemies** / AlwaysFriends / ChancedEnemies
    ///   （曾经误用 "Enemies" → 客户端反序列化失败 → 进图直接崩，报
    ///    `Error converting value "Enemies" to type 'EWarnBehaviour'`）
    /// </summary>
    private static void SetHostBehaviour(object entry, string role)
    {
        // ★ 先记真实原值（还原用）
        var mk = _lastRaidMap ?? "";
        var k = mk + "|" + role;
        foreach (var f in HostBehaviourFields)
        {
            var key = k + "|" + f;
            if (!_hostPrev.ContainsKey(key)) _hostPrev[key] = Prop(entry, f);
        }
        foreach (var f in HostEnemyChanceFields)
        {
            var key = k + "|" + f;
            if (!_hostPrevNum.ContainsKey(key) && Prop(entry, f) != null)
            { try { _hostPrevNum[key] = Convert.ToDouble(Prop(entry, f)); } catch { } }
        }
        // ★★★【P0 修复：forceHostility 的 5 个字段"写进 DB 后不还原"】
        //   这 5 个字段同样由 `ApplyHostility` 用 `SetOriginal` 写入，但旧快照/还原只覆盖了上面那 6 个
        //   ⇒ 换活动后「AlwaysEnemies / AlwaysFriends / Neutral / Warn / ChancedEnemies」**永远留着**，
        //   玩家看到的就是"邪教/PMC 继续全敌对"（日志实证：18 次「DB boss apply」、0 次「还原 hostility」）。
        //   ⚠ 调用方（ApplyHostility）已把本方法调到那 5 个字段写入**之前** ⇒ 这里拿到的是真原值。
        foreach (var f in HostListFields)
        {
            var key = k + "|" + f;
            if (!_hostPrev.ContainsKey(key)) _hostPrev[key] = Prop(entry, f);
        }

        // ★ 安全网：写之前校验枚举合法性（EWarnBehaviour 的合法值只有
        //   Default/Neutral/Warn/AlwaysEnemies/AlwaysFriends/ChancedEnemies）。
        //   写错会导致客户端反序列化失败 → 进不了图。
        // ★★★ v2.6.53【P1-2 修复】`KeepPlayersHostile=false` 时**完全照官方表**：
        //   官方 `hostilitySettingsForEvent.zombies.default` 把 `sectantPriest`/`sectantWarrior` 的
        //   三个 `*PlayerBehaviour` 都设成 `Neutral`（万圣节里邪教不主动打玩家）。
        //   默认 **true** = 保持本 mod 活动的语义（"邪教是敌人"）⇒ 这是与官方**唯一**的刻意偏离。
        var beh = "AlwaysEnemies";
        if (!KeepPlayersHostile && TryGetOfficialHostility(role, out var oe))
        {
            var official = Prop(oe, "SavagePlayerBehaviour") as string;
            if (EnumGuard.IsValid("EWarnBehaviour", official)) beh = official;
        }
        if (!EnumGuard.IsValid("EWarnBehaviour", beh))
        {
            _log?.Error($"[EventRotator] hostility 行为值 '{beh}' 非法，跳过本次敌对性修改（宁可它不打人，也不能让你进不了图）");
            return;
        }

        SetOriginal(entry, "SavagePlayerBehaviour", beh);
        SetOriginal(entry, "BearPlayerBehaviour", beh);
        SetOriginal(entry, "UsecPlayerBehaviour", beh);
        if (Prop(entry, "SavageEnemyChance") != null) SetOriginal(entry, "SavageEnemyChance", 100.0);
        if (Prop(entry, "BearEnemyChance") != null) SetOriginal(entry, "BearEnemyChance", 100.0);
        if (Prop(entry, "UsecEnemyChance") != null) SetOriginal(entry, "UsecEnemyChance", 100.0);
    }

    /// <summary>
    /// 把本 mod 追加/改动的 hostility 条目还原（活动切换或关闭时调用）。
    ///
    /// <para><b>★ P0 修复</b>：除了 3 个 <c>*PlayerBehaviour</c> + 3 个 <c>*EnemyChance</c>，
    /// 还要回放 **5 个名单字段**（<c>AlwaysEnemies</c>/<c>AlwaysFriends</c>/<c>Neutral</c>/<c>Warn</c>/
    /// <c>ChancedEnemies</c>）—— 它们同样被 <c>forceHostility</c> 写过而旧还原完全没覆盖
    /// ⇒ 换活动后"全敌对"永久保留。原值为 <c>null</c> 的字段跳过（不凭空造 null / 不写坏别的值）。</para>
    /// <para><b>幂等</b>：先删上一轮追加的条目，再逐字段回放真原值；重复调用结果相同。
    /// 只在**确实发生了还原**时打一条汇总 Info（不刷屏）。</para>
    /// </summary>
    internal static void RestoreHostility(object locationTable)
    {
        if (locationTable == null) return;
        if (_hostAdded.Count == 0 && _hostTouched.Count == 0) return;
        int removed = 0, touchedEntries = 0, fieldWrites = 0;
        try
        {
            foreach (var p in locationTable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object loc; try { loc = p.GetValue(locationTable); } catch { continue; }
                var baseObj = loc?.GetType().GetProperty("Base")?.GetValue(loc);
                if (baseObj == null) continue;
                var blm = Prop(baseObj, "BotLocationModifier");
                if (Prop(blm, "AdditionalHostilitySettings") is not IList ahs) continue;
                var mk = KeyOf(baseObj, p);      // ★ v0.1.3 与 ApplyToDb 写入端统一键空间（修还原永不命中）

                // ① 上一轮**追加**的条目整条移除（它们在 _hostAdded 里；还原后条目本身就不该存在）
                for (int i = ahs.Count - 1; i >= 0; i--)
                {
                    var role = Prop(ahs[i], "BotRole") as string ?? "";
                    if (_hostAdded.Contains(mk + "|" + role)) { ahs.RemoveAt(i); removed++; }
                }
                // ② 上一轮**改写**过的既有条目：逐字段回放真原值（含 5 个名单字段）
                foreach (var e in ahs)
                {
                    var role = Prop(e, "BotRole") as string ?? "";
                    var k2 = mk + "|" + role;
                    if (!_hostTouched.Contains(k2)) continue;
                    int n = 0;
                    foreach (var f in HostBehaviourFields)
                    {
                        if (!_hostPrev.TryGetValue(k2 + "|" + f, out var pv) || pv == null) continue;  // ★ 原值 null ⇒ 跳过
                        SetOriginal(e, f, pv); n++;
                    }
                    foreach (var f in HostEnemyChanceFields)
                    {
                        if (!_hostPrevNum.TryGetValue(k2 + "|" + f, out var pv2)) continue;
                        SetOriginal(e, f, pv2); n++;
                    }
                    foreach (var f in HostListFields)
                    {
                        if (!_hostPrev.TryGetValue(k2 + "|" + f, out var pv3) || pv3 == null) continue;  // ★ 原值 null ⇒ 跳过
                        SetOriginal(e, f, pv3); n++;
                    }
                    touchedEntries++; fieldWrites += n;
                }
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] RestoreHostility 失败: {ex.Message}"); }
        _hostAdded.Clear();
        _hostTouched.Clear();
        if (removed > 0 || touchedEntries > 0)
            _log?.Info($"[EventRotator] 已还原 hostility：移除追加条目 {removed} 条、"
                     + $"回放 {touchedEntries} 个条目的 {fieldWrites} 个字段原值（含 5 个名单字段）");
    }

    private static readonly HashSet<string> _hostAdded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _hostTouched = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> _hostOrig = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>forceHostility 写的 3 个"对玩家行为"字段（旧快照已覆盖）。</summary>
    private static readonly string[] HostBehaviourFields =
        { "SavagePlayerBehaviour", "BearPlayerBehaviour", "UsecPlayerBehaviour" };

    /// <summary>forceHostility 写的 3 个"对玩家开火概率"字段（旧快照已覆盖）。</summary>
    private static readonly string[] HostEnemyChanceFields =
        { "SavageEnemyChance", "BearEnemyChance", "UsecEnemyChance" };

    /// <summary>
    /// ★ P0 修复：forceHostility 写的 5 个**名单**字段 —— 旧快照/还原完全没覆盖 ⇒ 换活动后永久残留。
    /// </summary>
    private static readonly string[] HostListFields =
        { "AlwaysEnemies", "AlwaysFriends", "Neutral", "Warn", "ChancedEnemies" };

    // ---------- ★ v0.1.5 僵尸活动：复刻被 ABPS 掐掉的官方 ReplaceBotHostility ----------
    /// <summary>官方 `seasonalevents.json → hostilitySettingsForEvent.zombies`（mapId → 条目列表）。
    /// 由 Plugin 启动时从注入的 SeasonalEventConfig 只读复制过来。</summary>
    public static Dictionary<string, List<object>> ZombieHostilityByMap;

    /// <summary>官方"有感染"的地图（`zombieSettings.mapInfectionAmount &gt; 0`，按官方 id 映射展开成小写地图 id）。</summary>
    public static HashSet<string> InfectedMaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>官方合并时逐字段覆盖的字段名（与反编译 ReplaceBotHostility 一致）。</summary>
    private static readonly string[] ZombieHostFields =
    {
        "AlwaysEnemies", "AlwaysFriends", "Neutral", "Warn", "ChancedEnemies",
        "SavagePlayerBehaviour", "UsecPlayerBehaviour", "BearPlayerBehaviour",
        "SavageEnemyChance", "UsecEnemyChance", "BearEnemyChance",
    };

    private static readonly HashSet<string> _zombieHostAdded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, object> _zombieHostPrev = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v0.1.5 僵尸活动的敌对性：**mod 自己复刻** SPT 的
    /// `SeasonalEventService.ReplaceBotHostility(hostilitySettingsForEvent["zombies"], 有感染的地图)`。
    ///
    /// 为什么必须自己来：用户装了 **acidphantasm-botplacementsystem(BPS)**，它的
    /// `Patches.ReplaceBotHostilityPatch : AbstractPatch` 对 `SeasonalEventService.ReplaceBotHostility`
    /// 打了 **Prefix return false**（反编译实证）⇒ **官方那段整段不执行** ⇒
    /// 僵尸活动里"谁跟谁敌对"的官方关系（177 条）从来没生效过。种子来自官方配置，行为与官方一致：
    /// 逐字段覆盖（AlwaysEnemies/AlwaysFriends/Neutral/Warn/ChancedEnemies/三个 *PlayerBehaviour/三个 *EnemyChance），
    /// 没有该 BotRole 的条目则**追加**。全部改动都有快照，换活动/关活动时还原。
    /// </summary>
    /// <summary>
    /// ★ v0.1.5 诊断：把**合并之后**载荷里"感染者行"的敌对字段打出来。
    /// 用途：用户实测"实验室僵尸正常刷新、行动，但不打玩家" —— 需要确认落到客户端载荷里的
    /// `infected*` 行是不是 `AlwaysEnemies`（官方原值是 AlwaysEnemies ✓）。
    /// 只在 verbose 日志级别下输出。
    /// </summary>
    private static void DumpInfectedHostility(object mapBase, string mapKey)
    {
        try
        {
            if (!EventEngine.VerboseLogging) return;   // 只在 logLevel=verbose 时输出
            var blm = Prop(mapBase, "BotLocationModifier");
            var ahs = Prop(blm, "AdditionalHostilitySettings") as IList;
            if (ahs == null) { _log?.Info($"[EventRotator][敌对诊断] {mapKey}: AdditionalHostilitySettings 拿不到"); return; }

            var roles = new[] { "infectedAssault", "infectedPmc", "infectedCivil", "infectedLaborant" };
            int found = 0;
            foreach (var e in ahs)
            {
                if (e == null) continue;
                var role = Prop(e, "BotRole") as string ?? "";
                if (Array.FindIndex(roles, r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                found++;
                var ae = Prop(e, "AlwaysEnemies") as IEnumerable;
                int aeN = 0; if (ae != null) foreach (var _ in ae) aeN++;
                var nu = Prop(e, "Neutral") as IEnumerable;
                int nuN = 0; if (nu != null) foreach (var _ in nu) nuN++;
                _log?.Info($"[EventRotator][敌对诊断] {mapKey}: {role} → Bear={Prop(e, "BearPlayerBehaviour")} "
                         + $"Usec={Prop(e, "UsecPlayerBehaviour")} Savage={Prop(e, "SavagePlayerBehaviour")} "
                         + $"AlwaysEnemies={aeN} Neutral={nuN} BearChance={Prop(e, "BearEnemyChance")}");
            }
            if (found == 0) _log?.Info($"[EventRotator][敌对诊断] {mapKey}: 载荷里**没有任何 infected* 行**（共 {ahs.Count} 行）⇒ 客户端可能拿不到感染者的敌对定义");
            else _log?.Info($"[EventRotator][敌对诊断] {mapKey}: 共 {ahs.Count} 行，其中 infected* 行 {found} 条");
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator][敌对诊断] 失败: {ex.Message}"); }
    }

    private static void ApplyZombieHostility(object mapBase, string mapKey)
    {
        try
        {
            if (ZombieHostilityByMap == null || ZombieHostilityByMap.Count == 0) return;
            if (InfectedMaps.Count == 0 || !InfectedMaps.Contains(mapKey)) return;

            if (!ZombieHostilityByMap.TryGetValue(mapKey, out var entries) || entries == null)
                if (!ZombieHostilityByMap.TryGetValue("default", out entries) || entries == null) return;

            var blm = Prop(mapBase, "BotLocationModifier");
            var ahs = Prop(blm, "AdditionalHostilitySettings") as IList;
            if (ahs == null) return;

            int merged = 0, added = 0;
            foreach (var s in entries)
            {
                if (s == null) continue;
                var role = Prop(s, "BotRole") as string;
                if (string.IsNullOrEmpty(role)) continue;

                object existing = null;
                foreach (var e in ahs)
                    if (string.Equals(Prop(e, "BotRole") as string, role, StringComparison.OrdinalIgnoreCase)) { existing = e; break; }

                if (existing == null)
                {
                    var clone = Clone(s);
                    if (clone == null) continue;
                    ahs.Add(clone);
                    _zombieHostAdded.Add(mapKey + "|" + role);
                    added++;
                }
                else
                {
                    foreach (var f in ZombieHostFields)
                    {
                        var v = Prop(s, f);
                        if (v == null) continue;
                        var key = mapKey + "|" + role + "|" + f;
                        if (!_zombieHostPrev.ContainsKey(key)) _zombieHostPrev[key] = Prop(existing, f);
                        SetOriginal(existing, f, v);
                    }
                    merged++;
                }
            }
            if (added > 0 || merged > 0)
                _log?.Info($"[EventRotator] {mapKey}: ★官方僵尸敌对性已复刻（合并 {merged} 条 / 追加 {added} 条）"
                         + "—— BPS 把 SPT 的 ReplaceBotHostility Prefix 掉了，这里由 mod 补");
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] {mapKey}: 僵尸敌对性失败: {ex.Message}"); }
    }

    /// <summary>
    /// 还原官方僵尸敌对性（换活动/关活动时调用，与 <see cref="RestoreHostility"/> 同一时机）。
    /// ★ P0 修复：改成 <c>internal</c> —— 由 <c>Plugin.RestoreDbLayer()</c> 在每轮套用前显式调用
    /// （旧实现只在 <c>ApplyToDb</c> 的还原分支里，而那段永远不可达）。
    /// </summary>
    internal static void RestoreZombieHostility(object locationTable)
    {
        if (locationTable == null) return;
        try
        {
            if (_zombieHostAdded.Count == 0 && _zombieHostPrev.Count == 0) return;
            foreach (var p in locationTable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object loc; try { loc = p.GetValue(locationTable); } catch { continue; }
                var baseObj = loc?.GetType().GetProperty("Base")?.GetValue(loc);
                var blm = Prop(baseObj, "BotLocationModifier");
                if (Prop(blm, "AdditionalHostilitySettings") is not IList ahs) continue;
                var mk = KeyOf(baseObj, p);

                for (int i = ahs.Count - 1; i >= 0; i--)
                {
                    var role = Prop(ahs[i], "BotRole") as string ?? "";
                    if (_zombieHostAdded.Contains(mk + "|" + role))
                    { ahs.RemoveAt(i); _log?.Info($"[EventRotator] 还原僵尸敌对性: 移除 {mk} 的 '{role}' 条目"); }
                }
                foreach (var e in ahs)
                {
                    var role = Prop(e, "BotRole") as string ?? "";
                    int n = 0;
                    foreach (var f in ZombieHostFields)
                    {
                        var key = mk + "|" + role + "|" + f;
                        if (_zombieHostPrev.TryGetValue(key, out var old)) { SetOriginal(e, f, old); n++; }
                    }
                    if (n > 0) _log?.Info($"[EventRotator] 还原僵尸敌对性: {mk} 的 '{role}' 已复位（{n} 个字段）");
                }
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] 还原僵尸敌对性失败: {ex.Message}"); }
        _zombieHostAdded.Clear();
        _zombieHostPrev.Clear();
    }
    /// <summary>"map|role|字段" → 改动前的字符串原值（还原用）</summary>
    private static readonly Dictionary<string, object> _hostPrev = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"map|role|字段" → 改动前的数值原值（还原用）</summary>
    private static readonly Dictionary<string, double> _hostPrevNum = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★★★ 只允许对**真实战局地图**做首领改动（v1.92 关键修复）。
    ///
    /// 实测根因：`bossSpawns: {"*": [...]}` 的通配符会命中所有 map key，
    /// 包括 hideout（藏身处）等**非战局地图** —— 给藏身处塞 BossLocationSpawn 会让
    /// 客户端仓库操作失败（container:"hideout"），表现为「切仓库装东西必报未知错误」。
    ///
    /// 白名单方式（更安全）：只有明确列出的才是战局地图。
    /// </summary>
    private static readonly HashSet<string> RaidMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigmap",          // 海关 Customs
        "factory4_day",    // 工厂（白天）
        "factory4_night",  // 工厂（夜晚）
        "interchange",     // 立交桥
        "laboratory",      // 实验室
        "labyrinth",       // 迷宫
        "lighthouse",      // 灯塔
        "rezervbase",      // 储备站
        "sandbox",         // 沙盒（1 区）
        "sandbox_high",    // 沙盒（2 区）
        "shoreline",       // 海岸线
        "tarkovstreets",   // 街区
        "woods",           // 森林
    };

    /// <summary>是否真实战局地图（hideout / develop / private area / suburbs / terminal / town 一律 false）。</summary>
    private static bool IsRaidMap(string mapKey)
    {
        if (string.IsNullOrEmpty(mapKey)) return false;
        if (RaidMaps.Contains(mapKey)) return true;
        // 白名单外但看起来像新地图的（未来新增）→ 打日志提示，默认**允许**会有风险，
        // 所以这里选择**拒绝**并在日志里点名，让用户能反馈后手动加入白名单。
        return false;
    }

    private static void ApplyMap(object mapBase, string where, string sessionId = null)
    {
        try
        {
            if (mapBase == null) return;

            var mapId = mapBase.GetType().GetProperty("Id")?.GetValue(mapBase)?.ToString() ?? "";
            var mapKey = mapId.ToLowerInvariant();

            // ★★★★ 关键修复（v1.92）：**绝不碰非战局地图** ★★★★
            //   根因（实测复现）：bossSpawns 配了 {"*": [...]}，通配符会命中**所有** map key，
            //   包括 hideout（藏身处）/ develop / private area / suburbs / terminal / town 这些**非战局地图**。
            //   往藏身处的 LocationBase.BossLocationSpawn 里塞首领条目（还带藏身处不存在的 zone）
            //   → 客户端处理藏身处数据时出错 → **仓库操作（container:"hideout"）全部失败**
            //   → 服务端返回空响应 → 客户端 UNHANDLED RESPONSE: /client/game/profile/items/moving
            //   → 表现就是「切回仓库装东西必报未知错误只能退游戏」。
            if (!IsRaidMap(mapKey))
            {
                if (where == "post") _log?.Info($"[EventRotator] raid map={mapKey} 不是战局地图 → 跳过（绝不改动藏身处等非战局地图数据）");
                return;
            }

            // ★★ 2026-10-31【ABPS 兼容 · 第三方第一局不刷】在套用活动**之前**先把第三方出生项补回：
            //   ABPS 的 MapSpawns 会把 Base.BossLocationSpawn 整表替换成它自己的缓存
            //   ⇒ 黑狐/灾厄这类自定义 bot 的出生项在重启后第一局被清空（第二局因为别人重新注入才正常）。
            //   快照在 ABPS 清表之前（ThirdPartySpawnGuard，优先级 +69419）已经存好，这里每局补回。
            if ((where == "pre" || where == "post") && mapBase.GetType().GetProperty("BossLocationSpawn")?.GetValue(mapBase) is IList tpsList)
                ThirdPartySpawnGuard.Ensure(mapKey, tpsList);

            // ★★ 2026-10-31【孤儿仪式首领】接管了原版季节窗后，SPT 仍可能在启动时把
            //   `halloweensummon` 的仪式首领（扎里亚奇）塞进多张图；没有仪式数据时打死它
            //   ⇒ 不出复活光柱、客户端可能全场黑屏（用户实测 4 把里 1 次黑屏）。
            //   这里在非「万圣节 2023（仪式）」期间把这些孤儿条目摘掉，只保留灯塔的常规扎里亚奇。
            if (where == "pre" || where == "post") StripOrphanSeasonalBosses(mapKey, mapBase, where);
            if (where == "post") EnsureZombieSpawns(mapKey, mapBase);   // ★ ABPS 每局重建会抹掉僵尸条目 ⇒ 缺了就补

            // ★ 2026-10-31【南瓜头】非万圣节期间把「杰克南瓜灯战术南瓜头盔」的 Headwear 权重压回 0
            //   （并打出是哪个 bot 类型被调高的）
            if (where == "post") PumpkinHeadGuard.EnsureClean(mapKey);

            if (where == "pre")
            {
                EventEngine.RefreshFromForce();
                EventEngine.OnRefresh?.Invoke();
            }

            // ★ v1.97 多活动：遍历**全部**同时生效的活动，每个都独立套用一次
                        // v2.6.78 O2 第一步：每次 ApplyMap 一行（hook/map/session/对象hash/活动数）
            try { _log?.Info($"[EventRotator][空投/进图] ApplyMap hook={where} map={mapKey} session={(string.IsNullOrEmpty(sessionId) ? "(null)" : sessionId)} obj={mapBase.GetType().Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mapBase)} actives={EventEngine.Actives.Count}"); } catch { }
var actives = EventEngine.Actives;
            if (actives.Count == 0) { if (where == "post") _log?.Info("[EventRotator] no active event"); return; }

            if (where == "post")
            {
                if (actives.Count > 1)
                    _log?.Info($"[EventRotator] raid map={mapKey} 同时生效 {actives.Count} 个活动 → {string.Join(" + ", actives.Select(a => a.Id))}");
            }

            foreach (var oneEv in actives)
            {
                // ★ v2.6.76【深挖 19ms】按活动分段计时（只加计时，判定逻辑一行不动）
                long _tOne = PerfTimer.Start();
                try { ApplyOneEvent(mapBase, where, sessionId, mapKey, oneEv); }
                finally { PerfTimer.End("进图计划.ApplyOneEvent[" + (oneEv?.Id ?? "?") + "]", _tOne); }
            }

            // ★★ 2026-10-31【仪式补随从 · 独立后置步骤】必须放在"所有活动套用之后"，
            //   因为祭司条目来自 SPT 自己的 halloweensummon 表（不是我们活动的 bossSpawns），
            //   写在 ApplyOneEvent 里会被提前 return 跳过（实测日志无输出）。
            if ((where == "pre" || where == "post")
                && actives.Any(a => string.Equals(a.Id, "halloween_2023", StringComparison.OrdinalIgnoreCase)))
            {
                FixRitualEscorts(mapKey, mapBase, where);
            }

            // ★★★★ v0.1.5 ② 首领时刻表（官方 #79「Bosses & Rogues Wander」）★★★★
            //   官方原文是一张 7 行 × 6 时区的文本表格 ⇒ 按**系统时钟**换算到 MSK，取当前行对应的地图，
            //   把该图应有的首领组（Glukhar / Killa+Tagilla / Sanitar / Shturman / Reshala）落到本图。
            try
            {
                var ttEv = EventRotatorServer._timetable;
                if (ttEv?.BossTimetable != null && where != "db")
                {
                    var ttMap = TimetableDimension.CurrentMap(ttEv.BossTimetable, out var tthr);
                    if (!string.IsNullOrEmpty(ttMap) && string.Equals(ttMap, mapKey, StringComparison.OrdinalIgnoreCase))
                    {
                        if (where == "post")
                            _log?.Info($"[EventRotator][时刻表] MSK {tthr}:00 → 本时段首领图 = {ttMap}（即本图）"
                                     + $" ⇒ 落 {ttEv.BossTimetable.Bosses.Count} 位首领（官方 Bosses & Rogues Wander 班表）");
                        var tempEv = new EventDef
                        {
                            Id = ttEv.Id + "#timetable",
                            NameZh = ttEv.NameZh + "（时刻表）",
                            Category = ttEv.Category,
                            BossSpawns = new Dictionary<string, List<string>> { [mapKey] = new List<string>(ttEv.BossTimetable.Bosses) },
                            BossChance = new Dictionary<string, Dictionary<string, int>>
                            {
                                [mapKey] = ttEv.BossTimetable.Bosses.ToDictionary(b => b, _ => 100)
                            },
                            EscortCount = ttEv.BossTimetable.Escorts == null ? null
                                : new Dictionary<string, Dictionary<string, EscortCountValue>>
                                {
                                    [mapKey] = ttEv.BossTimetable.Escorts
                                        .ToDictionary(kv => kv.Key, kv => new EscortCountValue { Min = kv.Value, Max = kv.Value })
                                },
                        };
                        ApplyOneEvent(mapBase, where, sessionId, mapKey, tempEv);
                    }
                    else if (where == "post" && !string.IsNullOrEmpty(ttMap))
                        _log?.Info($"[EventRotator][时刻表] MSK {tthr}:00 → 本时段首领图 = {ttMap}（非本图，跳过）");
                }
            }
            catch (Exception ex) { _log?.Warning("[EventRotator][时刻表] 失败: " + ex.Message); }
        }
        catch (Exception ex) { _log?.Warning("[EventRotator] ApplyMap failed: " + ex.Message); }
    }

    /// <summary>把**单个**活动套用到某张图上（多活动支持的核心：对每个活动各调一次）。</summary>
    /// <summary>★ v2.6.78【O1】**带缓存的成员读取**（与 G1a/G1b 同款手法：同 `Type`+名字 ⇒ 同 `PropertyInfo` ⇒ 同值）。
    /// 用在**每活动**都要跑的 `ApplyOneEvent` 里（原来每次 `GetProperty("BossLocationSpawn")` 都重新解析）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), System.Reflection.PropertyInfo> _propCache2 = new();

    private static object PropCached(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var pi = _propCache2.GetOrAdd((o.GetType(), name), static k => k.Item1.GetProperty(k.Item2,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase));
            return pi?.GetValue(o);
        }
        catch { return null; }
    }

    private static void ApplyOneEvent(object mapBase, string where, string sessionId, string mapKey, EventDef ev)
    {
        long _tPre = PerfTimer.Start();     // ★ v2.6.77【129ms 归因】"共用前置"计时（入口 → 解析出 BossLocationSpawn 列表）
        try
        {
            // 【ABPS 兼容的关键】DB 阶段与 raid 阶段用两套 seen 集合。
            //   原因：ApplyToDb(hook=db) 会把 LocationTable 里的 LocationBase 标记进 _seenMaps，
            //   而进图时 GenerateLocationAndLoot / MakeAdjustmentsToMap 拿到的**很可能就是同一个实例**，
            //   于是 raid 阶段被 seen 命中直接 return，bossChance 根本没执行
            //   （症状：boosted/汇总 日志从不出现，走私者始终是原版 5%）。
            // 另外：**概率提升(bossChance)是幂等的，允许重复执行**；
            //   只有 append(bossSpawns) 需要防重复 —— 用 addedThisRaid 单独判断。
            // ★ v1.97：键改成 "活动id|地图key"，多活动并存时各自独立生效。
            var stateKey = ev.Id + "|" + mapKey;

            var list = PropCached(mapBase, "BossLocationSpawn") as IList;   // v2.6.78 O1 成员缓存
            if (list == null) return;
            var elemType = list.GetType().GetGenericArguments().FirstOrDefault();
            // ★ v2.6.77【深挖"最简单的活动也要 9ms"】**共用前置到此结束**：
            //   若这一行远小于上面的"总"，说明大头在**活动 body 的公共段**（bossChance/append 等），而不是入口反射。
            PerfTimer.End("进图计划.入口[共用前置]", _tPre);
            long _tBook = PerfTimer.Start();     // ★ v2.6.78【O4】段2 = 每局登记（_seenRaid/_seenMaps/addedThisRaid + 可选诊断日志）
            long _t2a1 = _tBook; long _t2a2 = 0;   // ★ v2.6.87 段2a1 = 新一局判定/清理块；段2a2 = 其余（到段2 Ends）

            // ★ 新一局判定。Fika 注意：一局联机里**所有玩家共用同一个 match.LocationData 对象**
            //   （Fika 的 StartLocalRaidOverride 把它直接发给每个玩家），所以这里用"累计测到的条目数回落"
            //   作为新一局的判定，而不是只靠对象引用 —— 否则联机第二局对象相同、统计不会清零。
            // ★★★ P0 修复（B1）：本块必须跑在 `_seenRaid` 判定**之前**。
            //   旧顺序 = 先 `_seenRaid.Add(stateKey)` 再判新一局 ⇒ 每局第一次调用时命中**上一局**残留的 key，
            //   `addedThisRaid=false` ⇒ :1550 直接 return，**追加类字段（bossSpawns / forceAbpsSpawns 兜底 /
            //   escortCount）整局被跳过**；又因 `LocationLifecycleService.GenerateLocationAndLoot()` 每局
            //   `cloner.Clone(location.Base)` 拿的是新克隆 ⇒ 第二局起那个首领**根本不存在**。
            //   同块里 `_spawned/_spawnedEvent/_seenBotIds/_loggedRaidPlanFor` 都清了、**只有 `_seenRaid` 漏清**
            //   ⇒ 这是漏清不是设计。顺序调整 + 补 `_seenRaid.Clear()` 后语义才自洽：
            //   "新一局" ⇒ 清空全部每局态（含 `_seenRaid`）⇒ 紧接着的 Add 必然成功 ⇒ 本局第一次调用会追加。
            int curCount = list.Count;
            bool newRaidObj = !ReferenceEquals(_lastRaidMapObj, mapBase);
            if (newRaidObj || curCount < _lastRaidCount)
            {
                _lastRaidMapObj = mapBase;
                _lastRaidCount = curCount;
                // ★★★ v2.6.72【Y1b/Y2b】**每局一条汇总**（basic 也可见）—— 在清计数之前打"上一局"的；
                //   明细（逐角色长行）改为 verbose 才输出，且不再是"每波一条"。
                // ★★★ v2.6.76【P0 修复 · 独立复核 🟡-3】**收紧为只在"对象引用真的换了"时 flush**：
                //   下面 R2 注释已实证 `curCount < _lastRaidCount` 这个条数回落启发式在 **SCAV 局同局内**也为真
                //   （SPT `RaidTimeAdjustmentService.AdjustPMCSpawns()` 把 BossLocationSpawn 换成更短的新列表）
                //   ⇒ 原来每次命中都会打一条"上一局 共记录 N 次"并**中途** `ResetHits()` ⇒ 汇总出现半局数字/重复。
                //   ⚠ 只收紧**日志与计数复位**这一处；正下方的清空逻辑与 `_seenRaid` 判定**一行未动**。
                long _t2b = PerfTimer.Start();     // ★ v2.6.85 段2b = **只包 FlushSpawnSummary**（段2a = 其余 = 段2 − 段2b）
                if (newRaidObj) FlushSpawnSummary(_lastRaidMap ?? "(unknown)");
                PerfTimer.End("进图计划.段2b[FlushSpawnSummary]", _t2b);
                _spawned.Clear();
                _spawnedEvent.Clear();
                _seenBotIds.Clear();        // ★ 新一局清空去重集合
                // ★★★ R2 修复（回归验证 verify-b §Q2b/§Q3 提出、Lead 采纳）：
                //   `_seenRaid`（"本局已追加过"的门控）**只在对象引用真的换了**时才清，不能用
                //   `curCount < _lastRaidCount` 这个条数回落启发式 —— 它在 SCAV 局会**同局内**为真：
                //   SPT `RaidTimeAdjustmentService.AdjustPMCSpawns()`（反编译 :116-117）会把
                //   `BossLocationSpawn` **整个换成更短的新列表**（`.Where(...).ToList()`，删掉
                //   `Time <= SimulatedRaidStartSeconds` 的 pmc 条目；用实机 pmc.json/location.json
                //   代入算得各图 r = 6~14 条）。
                //   ⇒ 同局第二个挂点（MakeAdjustmentsToMap 后置）会命中条数回落 ⇒ 若连 `_seenRaid`
                //     一起清，`addedThisRaid` 会重新变 true，追加阶段在同局内被重复执行。
                //     实测不会产生重复首领（三条追加路径各自重算 existing/present 兜住了），但会产生
                //     **重复日志**（★首领概率提升 等打 2 次）并让这道防线形同虚设。
                //   量化（verify-b）：50 个(活动,地图)对里 40 对必然同局重入、7 对不会（全在工厂）。
                if (newRaidObj) _seenRaid.Clear();
                if (newRaidObj) _loggedRaidPlanFor.Clear();
                if (newRaidObj) _loggedApplySummary.Clear();   // ★ v2.6.90 同一处（真·新局）   // ★ v2.6.88【L2】**只在真·新局清**（原来挂在本块的 `curCount < _lastRaidCount` 启发式上 ⇒ SCAV 局同局内反复清空 ⇒ 门失效）
            }
            else _lastRaidCount = curCount;
            _lastRaidMap = mapKey;      // bot 统计里显示地图名（之前是 unknown 就是这里没赋值）

            bool addedThisRaid;
            if (where == "db")
            {
                if (_seenMaps.Count > 512) _seenMaps.Clear();
                if (!_seenMaps.Add(stateKey)) return;
                addedThisRaid = true;                  // DB 阶段总要完整处理
                _dbAppliedEvent = ev.Id;
            }
            else
            {
                PerfTimer.End("进图计划.段2a1[新一局判定/清理]", _t2a1);
                _t2a2 = PerfTimer.Start();
                if (_seenRaid.Count > 512) _seenRaid.Clear();
                addedThisRaid = _seenRaid.Add(stateKey); // false = 本局第二次进入同图（ABPS 先后挂点都会到）
                if (!addedThisRaid)
                    _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 本局已追加过（hook={where}）→ 只跑幂等字段");
            }

            _log?.Info($"[EventRotator] raid map={mapKey} event={ev.Id} (hook={where})");

            // ★★ ABPS 兼容诊断：打印"进入本挂载点时"的真实首领表（名字:概率@zone数）
            //    ★ 1.3.5：默认**不再**打印整表（42 条 × 多活动 × 多图 = 几百行，用户会以为出错了，
            //      已经有人在 Forge 评论区把这段日志当报错贴出来）。只有 logLevel=verbose 才打明细。
            if (EventEngine.VerboseLogging)
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var it in list)
                    {
                        var n = Prop(it, "BossName")?.ToString() ?? "(null)";
                        var z = Prop(it, "BossZone")?.ToString() ?? "";
                        sb.Append($"{n}:{ChanceOf(it):0.#}");
                        if (z.Length > 0) sb.Append($"@z{z.Split(',').Length}");
                        sb.Append(' ');
                    }
                    bool hasArena = list.Cast<object>().Any(x => string.Equals(Prop(x, "BossName")?.ToString(), "arenaFighterEvent", StringComparison.OrdinalIgnoreCase));
                    _log?.Info($"[EventRotator][原表] {mapKey} hook={where} 共 {list.Count} 条 含走私者={hasArena} | {sb}");
                }
                catch (Exception ex) { _log?.Warning($"[EventRotator][原表] dump failed: {ex.Message}"); }
            }

            // ★★★ v0.1.4 随机地图首领概率：把活动配的地图**随机重掷**一遍
            //   （原配置里那几张只是占位；真实地图每次启动/换档重新抽）
            var chanceSrc = ev.BossChance;
            if (ev.RandomBossMaps)
            {
                chanceSrc = RandomBossMapChance(ev);
                if (where == "db" && mapKey == "bigmap")
                    _log?.Info($"[EventRotator] ★随机地图首领概率：本次抽到 {string.Join("/", chanceSrc.Keys)}"
                               + $"（每张图全部首领 → {chanceSrc.Values.FirstOrDefault()?.Values.FirstOrDefault() ?? 0}%）");
            }

            // ★ v2.6.78【O4】段2 收尾 + 段3 起（段3 = bossChance + 追加 + 其余全部）
            if (_t2a2 != 0) PerfTimer.End("进图计划.段2a2[seenMaps/其余]", _t2a2);
            PerfTimer.End("进图计划.段2[每局登记]", _tBook);   // 保留（总账）
            long _t3a = PerfTimer.Start();   // ★ v2.6.79【O4 段3a】RandomBossMaps / ForceAbpsSpawns / autoBossChance / 集合构造
            // 1) bossChance：{ 地图: { boss: 概率 } }（含内层 "*" = 该图所有首领）
            //    ★ 与 ABPS 兼容：ABPS 会按它自己的配置覆盖 BossChance，
            //      所以这里的提升只对"ABPS 没接管的条目"有效；活动敌种请用 forceAbpsSpawns 走 ABPS。
            Dictionary<string, int> byBoss = null;
            if (chanceSrc != null && !chanceSrc.TryGetValue(mapKey, out byBoss)) chanceSrc.TryGetValue("*", out byBoss);
            int boosted = 0;
            var boostedNames = new List<string>();
            // ABPS 接管的敌种（forceAbpsSpawns）→ 只补骷髅，不去改概率（改了也会被 ABPS 覆盖）
            var markerOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ev.ForceAbpsSpawns != null)
                foreach (var kv in ev.ForceAbpsSpawns)
                    if (kv.Value != null && kv.Value.Any(m => string.Equals((m ?? "").ToLowerInvariant(), mapKey, StringComparison.OrdinalIgnoreCase)))
                        markerOnly.Add(kv.Key);

            // ★★★ v0.1.4 自动首领概率提升（config → autoBossChance）
            //   用户指出的正确做法：像八影会谈/狩猎开始这种"把首领抽到一张图"的活动，
            //   不该把首领加回被清空的图（那等于把机制拉平），而应**提高别图自己那些首领的刷新概率**
            //   —— 三狗 bossKnight、黑老登 bossPartisan 等。概率提升 ≠ 必刷，核心体验（首领聚集）保留。
            //   这个提升与活动自带的 bossChance **合并**（同角色取较大值，避免把活动配的 100% 压低）。
            var autoChance = EventRotatorServer.AutoBossChanceOf(ev.Id);
            if (autoChance != null && autoChance.Count > 0)
            {
                Dictionary<string, int> ab = null;
                if (!autoChance.TryGetValue(mapKey, out ab)) autoChance.TryGetValue("*", out ab);
                if (ab != null && ab.Count > 0)
                {
                    if (byBoss == null) byBoss = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in ab)
                    {
                        if (byBoss.TryGetValue(kv.Key, out var exist)) byBoss[kv.Key] = Math.Max(exist, kv.Value);
                        else byBoss[kv.Key] = kv.Value;          // 注意：内层 "*" 键在这里天然生效于全图所有首领
                    }
                    _log?.Info($"[EventRotator] {mapKey}: ★自动首领概率提升（来自 {ev.Id}）→ "
                               + string.Join(" ", ab.Select(kv => $"{kv.Key}={kv.Value}%")));
                }
            }

            PerfTimer.End("进图计划.段3a[bossChance前置]", _t3a);
            long _t3b = PerfTimer.Start();   // ★ v2.6.79【O4 段3b】遍历首领表 + zeroBossSpawns + 追加 + 其余（差值 = 段3 − 段3a）
            // ★ v2.6.83【段3b2 下钻】`_t3b2a` = 段3b 起 → 敌对性注释（含 段3b1 的三处守卫循环 ⇒ 缺失追加 = 段3b2a − 段3b1）
            long _t3b2a = PerfTimer.Start();
            if (byBoss != null)
            {
                int all = byBoss.TryGetValue("*", out var ca) ? ca : -1;
                foreach (var item in list)
                {
                    var name = Prop(item, "BossName")?.ToString();
                    if (name == null) continue;
                    int chance;
                    if (!byBoss.TryGetValue(name, out chance))
                    {
                        if (all < 0) continue;
                        // ★ v0.1.5：内层 "*" 只命中真实首领
                        //   （否则会把 gifter / 节日波次 / PvE PMC 波 / infected* 一起拉满）
                        if (!IsRealBoss(name)) continue;
                        chance = all;
                    }
                    var before = ChanceOf(item);
                    SetOriginal(item, "BossChance", (double)chance);
                    // ★★★ 方案A（task-41）：`forceZone` 也必须作用在**被提升概率的既有条目**上。
                    //   原实现只在 :2390 附近的"**新建条目**"路径里应用它 ⇒ 对"只提概率、不新建条目"的活动
                    //   （如 `tarkov_hospitality` 的 `bossSanitar`）永远走不到那条路，`forceZone` 形同虚设
                    //   （Lead 实跑确认：日志里连 `+forceZone` 都没有）。
                    //   语义：`forceZone` = "本活动里这个 boss 就该在这个 zone"，**与是否新建条目无关**。
                    //   两条路径**都应用**（不替换），所以 `meeting_of_the_bosses` 等走新建路径的活动不受影响。
                    try
                    {
                        var fz = ForceZoneFor(mapKey, name);
                        if (!string.IsNullOrEmpty(fz))
                        {
                            var oldZ = Prop(item, "BossZone")?.ToString();
                            if (!string.Equals(oldZ, fz, StringComparison.Ordinal))
                            {
                                SetOriginal(item, "BossZone", fz);
                                _log?.Info($"[EventRotator] {mapKey}: {name} zone: {oldZ} → {fz}（forceZone，既有条目）");
                            }
                        }
                    }
                    catch (Exception ex) { _log?.Warning($"[EventRotator] {mapKey}: forceZone(既有条目) 失败（已跳过）: {ex.GetType().Name}"); }
                    // ★★★ v0.1.4 补齐「选图界面红骷髅」：这一条以前只改了概率，**没补骷髅标记**。
                    //   而"概率提升"正是 八影会谈 / 全首领 / autoBossChance（三狗·黑老登）唯一走过的路径
                    //   ⇒ 首领确实刷了，但地图选择界面上**看不到红骷髅**。
                    //   客户端的骷髅显隐条件是 `BossLocationSpawn.Any(x => x.ShowOnTarkovMap)`
                    //   （PvE 取 ShowOnTarkovMapPvE），所以两个都补。
                    if (chance > 0)
                    {
                        SetOriginal(item, "ShowOnTarkovMap", true);
                        SetOriginal(item, "ShowOnTarkovMapPvE", true);
                    }
                    boosted++;
                    if (Math.Abs(before - chance) > 0.001) boostedNames.Add($"{name}:{before:0.#}→{chance}");
                }
                if (boosted > 0)
                    _log?.Info($"[EventRotator] {mapKey}: ★首领概率提升 {boosted} 条 + 补红骷髅标记 [{string.Join(" ", boostedNames.Take(8))}]");
            }

            // ★ v1.98 活动期间「老家不刷」：把指定首领在本图的刷新概率压到 0
            //   用于「八影会谈」这类"全首领聚集到一张图"的活动 —— 官方设定是首领不再出现在老家。
            //   只压 BossChance（不删条目），走 SetOriginal 快照 → 换活动时自动还原。
            if (ev.ZeroBossSpawns != null && ev.ZeroBossSpawns.TryGetValue(mapKey, out var zeroList)
                && zeroList != null && zeroList.Count > 0)
            {
                int zeroed = 0;
                var zeroedNames = new List<string>();
                foreach (var item in list)
                {
                    var zn = Prop(item, "BossName")?.ToString();
                    if (zn == null) continue;
                    if (!zeroList.Any(z => string.Equals(z, zn, StringComparison.OrdinalIgnoreCase))) continue;
                    var zb = ChanceOf(item);
                    if (zb <= 0.001) continue;                 // 本来就是 0 → 不动
                    SetOriginal(item, "BossChance", 0d);
                    // ★ v0.1.4：首都不刷了就不该在选图界面显示红骷髅（否则玩家点进去找不到，等于骗人）
                    if (zb > 0.001 && ChanceOf(item) <= 0.001)
                    {
                        SetOriginal(item, "ShowOnTarkovMap", false);
                        SetOriginal(item, "ShowOnTarkovMapPvE", false);
                    }
                    zeroed++;
                    zeroedNames.Add($"{zn}:{zb:0.#}→0");
                }
                if (zeroed > 0)
                    _log?.Info($"[EventRotator] {mapKey}: ★活动期间老家不刷 → 压 0 了 {zeroed} 个首领 (hook={where})  [{string.Join(" ", zeroedNames)}]");
            }

            // ★ 骷髅标记 + 缺失兜底追加：
            //   ABPS（acidphantasm-botplacementsystem）的 MapSpawns 会清空并按它自己的配置重建
            //   Base.BossLocationSpawn，且它的模板不带 ShowOnTarkovMap。
            //   若 ABPS 因 SpawnChance=0 没把活动敌种加进来，这里用"他图同名模板"补上（含 escort/zone）。
            if (markerOnly.Count > 0)
            {
                int mk = 0;
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in list)
                {
                    var name = Prop(item, "BossName")?.ToString();
                    if (name == null) continue;
                    present.Add(name);
                    if (!markerOnly.Contains(name)) continue;
                    SetOriginal(item, "ShowOnTarkovMap", true);
                    SetOriginal(item, "ShowOnTarkovMapPvE", true);
                    mk++;
                }
                // ★ v2.6.81【Lead 批准】段3b1 到此结束（= bossChance 概率 / ZeroBossSpawns / marker 骷髅 三处**带守卫的**循环）
                //   段3b2（= 段3b − 段3b1）= 缺失追加(list.Add) · escortCount · ForceAbpsSpawns 兜底/ABPS · 写回
                PerfTimer.End("进图计划.段3b1[三处守卫循环]", _t3b);
                if (mk > 0) _log?.Info($"[EventRotator] {mapKey}: 给 ABPS 接管的 {mk} 条活动敌种补骷髅标记 [{string.Join(",", markerOnly)}]");

                // 缺失的 → 追加（只在本局第一次，避免重复）
                var missing = markerOnly.Where(n => !present.Contains(n)).ToList();
                if (missing.Count > 0)
                {
                    if (!addedThisRaid) { _log?.Info($"[EventRotator] {mapKey}: {missing.Count} 条活动敌种缺失，但本局已追加过，跳过"); }
                    else if (Compat.MapModOwnsMap(mapKey)) { _log?.Info($"[EventRotator] {mapKey}: 地图被地图类 mod 接管，跳过追加"); }
                    else if (elemType == null) { _log?.Warning($"[EventRotator] {mapKey}: elemType 为空，无法追加"); }
                    else
                    {
                        foreach (var boss in missing)
                        {
                            // ★ ABPS 会按自己的配置重建列表，这里**必须先主动构建**（不能靠 db 阶段采集的模板，
                            //   因为 CollectionTemplates 在 ABPS 清空之后才跑，那份缓存是空的）。
                            var entry = BuildEntry(elemType, boss, mapKey, ev);
                            if (entry == null) { _log?.Warning($"[EventRotator] {mapKey}: 无法为 '{boss}' 构建条目"); continue; }

                            // 【测试用】对照实验：把条目的 BossName 换成另一个角色（键仍用本活动的敌种）
                            var spawnAs = boss;
                            if (ev.ForceBossName != null && ev.ForceBossName.TryGetValue(boss, out var alt) && !string.IsNullOrWhiteSpace(alt))
                            {
                                spawnAs = alt;
                                SetOriginal(entry, "BossName", alt);
                                SetOriginal(entry, "BossEscortType", alt);
                                _log?.Info($"[EventRotator] {mapKey}: 测试模式 forceBossName → 以 '{alt}' 之名生成（键={boss}）");
                            }

                            // ★ 缺陷1 修复配套：概率也用"活动覆盖优先、内置默认兜底"（BuildEntry 已写入，这里只兜底落空的情况），
                            //   不再无条件写 100 —— 否则会把活动声明的百分比（如 60）拉满。
                            SetOriginal(entry, "BossChance", DeclaredBossChance(mapKey, boss, ev) ?? 100.0);
                            SetOriginal(entry, "ShowOnTarkovMap", true);
                            SetOriginal(entry, "ShowOnTarkovMapPvE", true);
                            list.Add(entry);
                            _log?.Info($"[EventRotator] {mapKey}: ★ABPS 未提供 '{boss}'，已自行追加 (src={_lastBuildSrc}, escort='{Prop(entry, "BossEscortType") as string}', amount='{Prop(entry, "BossEscortAmount") as string}', chance={ChanceOf(entry):0.#}, zone='{Prop(entry, "BossZone") as string}')");

                            // ★【测试用】额外多刷几处（events.json 的 forceExtraSpawns），把刷新拉到极端
                            int extra = 0;
                            if (ev.ForceExtraSpawns != null && ev.ForceExtraSpawns.TryGetValue(boss, out var ex)) extra = Math.Max(0, ex);
                            for (int k = 0; k < extra && addedThisRaid; k++)
                            {
                                var dup = BuildEntry(elemType, boss, mapKey, ev);
                                if (dup == null) break;
                                SetOriginal(dup, "BossChance", DeclaredBossChance(mapKey, boss, ev) ?? 100.0);
                                SetOriginal(dup, "ShowOnTarkovMap", true);
                                SetOriginal(dup, "ShowOnTarkovMapPvE", true);
                                list.Add(dup);
                            }
                            if (extra > 0) _log?.Info($"[EventRotator] {mapKey}: 测试模式 forceExtraSpawns → {boss} 额外 +{extra} 处（本图共 {extra + 1} 处）");
                        }
                    }
                }
            }

            if (boosted > 0)
                _log?.Info($"[EventRotator] {mapKey}: boosted {boosted} boss spawn(s) (hook={where})"
                          + (boostedNames.Count > 0 ? $"  [{string.Join(" ", boostedNames)}]" : ""));

            // ★ v2.6.83【段3b2 下钻】段3b2a 收尾（缺失追加 + 骷髅日志）⇒ 之后 = 僵尸敌对性/NightOnly/等级门槛 → 其余（差值）
            PerfTimer.End("进图计划.段3b2a[含段3b1]", _t3b2a);
            long _t3b2b = PerfTimer.Start();   // ★ v2.6.84【段3b2 下钻】段3b2b = 僵尸敌对性(有守卫) + NightOnly + 玩家等级门槛
            // ★★ 敌对性：把"新敌种"补进地图的 AdditionalHostilitySettings，否则它对玩家默认中立 → 站着不打人
            //   （Fika 的 StartLocalRaidOverride 之后会调 SPT 的 AdjustBotHostilitySettings，
            //    但它**只能改已存在的条目**、且只处理 pmcConfig.HostilitySettings 里的 PMC 角色，
            //    所以这里补的 arenaFighterEvent 不会被覆盖 —— 正是 Fika 下需要的那一步。）
            // ★ v0.1.5 僵尸活动：先复刻官方僵尸敌对性（BPS 把 SPT 那段 Prefix 掉了），
            //   再跑活动自带的 forceHostility —— 后者对同一角色**后写覆盖**，符合"活动说了算"的既有语义。
            if (EventRotatorServer.WantNativeZombies) { ApplyZombieHostility(mapBase, mapKey); DumpInfectedHostility(mapBase, mapKey); }
            ApplyHostility(mapBase, mapKey, ev);
            ApplyAllyPairs(mapBase, mapKey, ev);   // ★ 结盟（如 The Goons guard Sanitar）

            // ★★★ 仅夜晚（NightOnly）：本局是白天 → 跳过本活动的所有首领追加/提升
            //     SPT 的 MatchController.ConfigureOfflineRaid 已算好 IsNightRaid（= WeatherHelper.IsNightTime(TimeVariant, map)）
            if (ev.NightOnly)
            {
                // ★ v0.1.5 修正（用户实测：实验室里出现了邪教祭司）：
                //   DB 阶段（where=="db"）没有战局、`IsNightRaid(null)` 返回 null，旧代码走"昼夜未知 → 照原版照刷"，
                //   于是 `bossSpawns: {"*": ["sectantPriest"]}` 这种通配会把祭司塞进**所有**地图 ——
                //   包括没有夜战的 laboratory。⇒ DB 阶段直接跳过"仅夜晚"活动，交给进图时的 ApplyMap 判定。
                if (string.Equals(where, "db", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 设为【仅夜晚】→ DB 阶段无昼夜信息，跳过（进图时再判定）");
                    return;
                }
                // ★ v0.1.5 修正（用户实测：实验室里出现了邪教祭司）：实验室/迷宫**没有夜战变体**
                //   （客户端 `NightTimeAllowedLocations` 就明确排除了 laboratory），
                //   但 `IsNightRaid` 对这类图仍可能返回 true（按当前现实时间判定）⇒ 照样把祭司塞进实验室。
                //   这里直接按"没有夜战 = 永远不满足仅夜晚"处理。
                if (NightlessMaps.Contains(mapKey))
                {
                    _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 设为【仅夜晚】，但该图没有夜战 → 跳过");
                    return;
                }
                var night = IsNightRaid(sessionId);
                if (night == false)
                {
                    _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 设为【仅夜晚】→ 本局是白天，跳过（不在白天刷）");
                    LogRaidPlan(mapKey, ev.Id, where, list);
                    return;
                }
                if (night == null)
                    _log?.Warning($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 设为【仅夜晚】但昼夜未知 → 按原版处理（照刷）");
                else
                    _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 设为【仅夜晚】→ 本局是夜战 ✓ 正常刷");
            }

            // ★★★ v0.1.5 ③b：**玩家等级门槛**（官方 2024 场「Permanent Boss & Cultists Presence」对邪教徒限定 21 级以上）
            //   落点与 nightOnly 相同：进图时按**房主存档等级**判断，低于门槛就整局跳过本活动。
            // ★★★ 按图门槛（用户裁决「按图分别设门槛做」）—— **新增分支，插在活动级判断之前**，
            //   活动级那段（下面）**一字未改**，所以 `boss_cultist_presence: minPlayerLevel=21` 行为不变。
            //   优先级：**该图在表里 ⇒ 用表里的值（覆盖活动级）**；**不在表里 ⇒ 回落到活动级**。
            //   只影响本图的本次判断，不会波及其它地图。
            int? levelGate = null; string gateSrc = null;
            try
            {
                if (ev.MinPlayerLevelByMap != null && ev.MinPlayerLevelByMap.Count > 0)
                {
                    if (ev.MinPlayerLevelByMap.TryGetValue(mapKey, out var perMap) && perMap > 0)
                    {
                        levelGate = perMap; gateSrc = "按图门槛";
                    }
                    else
                    {
                        // 表里有内容但没这一张 ⇒ 回落活动级（下面那段会处理）
                        _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 不在按图门槛表里"
                                 + $"（表=[{string.Join(",", ev.MinPlayerLevelByMap.Keys)}]）→ 回落活动级门槛判定");
                    }
                }
            }
            catch (Exception ex) { _log?.Warning($"[EventRotator] {mapKey}: 按图门槛读取失败（已跳过）: {ex.GetType().Name}"); }

            if (levelGate.HasValue)
            {
                var lvl2 = PlayerLevelOf(sessionId);
                if (lvl2 > 0 && lvl2 < levelGate.Value)
                {
                    _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 按图门槛需 ≥{levelGate.Value} 级"
                             + $"，本局房主 {lvl2} 级 → 跳过（**仅本图受限**；官方 Ground Zero 21+）");
                    LogRaidPlan(mapKey, ev.Id, where, list);
                    return;
                }
                _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' {gateSrc} {levelGate.Value} ✓"
                         + (lvl2 > 0 ? $"（房主 {lvl2} 级）" : "（读不到等级 ⇒ 放行）"));
            }

            if (ev.MinPlayerLevel.HasValue && ev.MinPlayerLevel.Value > 0)
            {
                var lvl = PlayerLevelOf(sessionId);
                if (lvl > 0 && lvl < ev.MinPlayerLevel.Value)
                {
                    _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 需要玩家 ≥{ev.MinPlayerLevel.Value} 级"
                             + $"，本局房主 {lvl} 级 → 跳过（官方等级门槛，可还原）");
                    LogRaidPlan(mapKey, ev.Id, where, list);
                    return;
                }
                _log?.Info($"[EventRotator] {mapKey}: 活动 '{ev.Id}' 等级门槛 {ev.MinPlayerLevel.Value} ✓"
                         + (lvl > 0 ? $"（房主 {lvl} 级）" : "（读不到等级 ⇒ 放行）"));
            }

            PerfTimer.End("进图计划.段3b2b[敌对性+夜间+等级门槛]", _t3b2b);
            long _t3b2c = PerfTimer.Start();   // ★ v2.6.84 段3b2c = 商人 assort 懒加载（"每次进图补一次挂起折扣"）
            // ★ 商人 assort 懒加载 ⇒ 每次进图时补一次挂起的折扣（幂等，就绪后自动清空）
            TraderAssortDimension.ReapplyPending(null);

            PerfTimer.End("进图计划.段3b2c[assort懒加载]", _t3b2c);
            long _t3b2d = PerfTimer.Start();   // ★ v2.6.84 段3b2d = PBS 池子每局幂等补写
            // ★★ v0.1.5 ④：PBS 池子每局幂等补写（PBS 网页 Save/Reload 会重建池子）
            PbsPoolWriter.ReapplyPending();

            PerfTimer.End("进图计划.段3b2d[PBS池写入]", _t3b2d);
            long _t3b2e = PerfTimer.Start();   // ★ v2.6.84 段3b2e = escortCount 覆盖 + 其后到 P0 注释之间（季节首领复制等）
            // ★★★ 1.4) 官方随从人数覆盖（v0.1.5）
            //   为什么需要：`bossSpawns` 追加出来的条目用的是**本体模板**，随从人数常与官方不符。
            //   实例：官方 Scav On Labs 是 Gluhar + **6** 名护卫，而本体 rezervbase 模板给 8 名；
            //        官方 Shifting Balance 是 Glukhar + **3** 名（每种一名），模板给 8 名。
            //   写法：`"escortCount": { "<地图>": { "<boss>": <人数> } }`
            //   ⚠⚠ P0 修复（B2）：本块**必须排在所有"追加"之后**（旧位置在常量 1.4，早于 1.5 / 2 的追加）
            //     ⇒ 对"该图原生没有、靠追加进来"的首领，第一次落地用的是**他图模板的护卫数**
            //     （Gluhar 模板 8 名），活动写的 6/3/4 名要等同局第二个挂点才补上 —— 依赖隐式时序。
            //     现在整体后移到 :1620 之后（bossSpawns 追加块之后），本局内一次到位。
            //     已核对：全部 6 个带 escortCount 的活动，其地图键都与 bossSpawns 的显式列表同图，
            //     因此"挪到追加之后"不会漏掉任何一条（没有 `"*"` 通配与 escortCount 同时出现的情况）。

            // ★★★ 1.5) 从 SPT 官方 seasonalevents.json 原样复制首领条目
            //     例：ForceSeasonalBossKey="halloweennightcult" → 官方邪教之夜
            //         BossName=sectantPredvestnik + BossEscortType=exUsec + Supports=[sectantOni, sectantPrizrak]
            //     用官方数据、不猜模板；Supports 等复杂字段全部保真。
            long _t3b2e1 = PerfTimer.Start();   // ★ v2.6.85 段3b2e1 = 官方季节首领条目复制（seasonalevents.json + 建条目）
            if (!string.IsNullOrEmpty(ev.ForceSeasonalBossKey) && elemType != null && list != null)
                AppendSeasonalBossEntries(mapKey, list, elemType, ev, addedThisRaid);


            // 2) bossSpawns：{ 地图: [ "bossXxx", "*" ] } —— "*" = 该图所有既有首领→100%；否则提升/追加缺失
            //    ⚠ append 只在本局第一次执行时做（addedThisRaid），避免 ABPS 两个挂点各追加一次
            PerfTimer.End("进图计划.段3b2e1[季节首领复制]", _t3b2e1);
            long _t3b2e2 = PerfTimer.Start();   // ★ v2.6.85 段3b2e2 = BossSpawns 查找 + hasWant + (db) LogApplySummary
            long _tE2a = PerfTimer.Start();   // ★ v2.6.89【最后定位】段3b2e2a = BossSpawns 两次 TryGetValue
            List<string> want = null;
            if (ev.BossSpawns != null && !ev.BossSpawns.TryGetValue(mapKey, out want)) ev.BossSpawns.TryGetValue("*", out want);
            // ★★★ R1 修复（回归验证 verify-b §Q1 提出、Lead 采纳）：
            //   旧写法在这里 `return;` —— 而 B2 把 escortCount 的后置块移到了本方法**末尾**
            //   ⇒ "只声明 escortCount、不声明同图 bossSpawns"的活动会**静默失效**
            //      （旧代码里 escortCount 在 return 之前，是能对派生/原生首领覆盖随从数的）。
            //   现役 6 个带 escortCount 的活动**恰好**全部在同图有 bossSpawns 键，所以今天不炸；
            //   但这是本轮新引入的潜伏陷阱。改成旗标：没有 bossSpawns 时只跳过"追加阶段"，
            //   **后置块仍无条件可达**。
            PerfTimer.End("进图计划.段3b2e2a[want查找]", _tE2a);
            long _tE2b = PerfTimer.Start();
            bool hasWant = want != null && elemType != null;
            PerfTimer.End("进图计划.段3b2e2b[hasWant判断]", _tE2b);
            long _tE2c = PerfTimer.Start();
            if (!hasWant)
            {
                // 没有 bossSpawns：boost 已做完，这里打印"提升后"的刷新计划
                LogRaidPlan(mapKey, ev.Id, where, list);
                if (where == "db" && _loggedApplySummary.Add(mapKey + "|" + ev.Id + "|a")) LogApplySummary(mapKey, ev.Id, list);   // ★ v2.6.90
            }
            PerfTimer.End("进图计划.段3b2e2[want/hasWant]", _t3b2e2);
            PerfTimer.End("进图计划.段3b2e[escort覆盖+季节首领]", _t3b2e);   // 保留（总账）
            // ★ v2.6.84 之后（含 `!skipWant` 大循环/ABPS/写回）= 「未归因余量」= 总 − 入口 − 段3a − 段3b1 − 段3b2a − 段3b2b/c/d/e
            // ★ P0 修复（B2）：这里**不能 return** —— 否则 escortCount 后置块在
            //   "本局已追加过" 的第二个挂点上永远到不了（旧代码正是 return）。
            //   改成旗标：跳过"追加阶段"，但继续往下走到后置块。
            PerfTimer.End("进图计划.段3b2e2c[!hasWant分支]", _tE2c);
            bool skipWant = !hasWant || !addedThisRaid;

            if (!skipWant)
            {
                var existing = list.Cast<object>()
                    .Select(x => Prop(x, "BossName")?.ToString())
                    .Where(n => n != null).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (want.Contains("*"))
                {
                    int n = 0, skipped = 0;
                    foreach (var it in list)
                    {
                        // ★ v0.1.5：通配符只命中真实首领（boss*）。审计实证：旧实现把该图每一条都拉 100%，
                        //   实测 bigmap 被拉满 36 条，殃及 gifter / arenaFighterEvent / 万圣节召唤链 /
                        //   sectantPredvestnik / blackDivAssault / PvE 的 pmcBEAR·pmcUSEC / 实验室 infected*。
                        if (!IsRealBoss(Prop(it, "BossName")?.ToString())) { skipped++; continue; }
                        SetOriginal(it, "BossChance", 100.0);
                        SetOriginal(it, "ShowOnTarkovMap", true);        // ★ 骷髅头
                        SetOriginal(it, "ShowOnTarkovMapPvE", true);
                        n++;
                    }
                    _log?.Info($"[EventRotator] {mapKey}: {n} 个真实首领 → 100% + 骷髅标记"
                             + (skipped > 0 ? $"（跳过 {skipped} 条非首领：节日波次/PvE PMC/infected 等）" : ""));
                    if (where == "db" && _loggedApplySummary.Add(mapKey + "|" + ev.Id + "|b")) LogApplySummary(mapKey, ev.Id, list);   // ★ v2.6.90
                    // ★ P0 修复（B2）：这里也不能 return（旧代码在此 return，会跳过 escortCount 后置块）
                }
                else
                {
                    foreach (var boss in want)
                    {
                        if (existing.Contains(boss))
                        {
                            foreach (var it in list)
                            {
                                if (!string.Equals(Prop(it, "BossName")?.ToString(), boss, StringComparison.OrdinalIgnoreCase)) continue;
                                SetOriginal(it, "BossChance", 100.0);
                                SetOriginal(it, "ShowOnTarkovMap", true);
                                SetOriginal(it, "ShowOnTarkovMapPvE", true);
                            }
                            _log?.Info($"[EventRotator] {mapKey}: '{boss}' already present → chance 100% + map marker");
                            continue;
                        }
                        if (Compat.MapModOwnsMap(mapKey))
                        {
                            _log?.Info($"[EventRotator] {mapKey}: skip appending '{boss}' (map handled by a map-overhaul mod)");
                            continue;
                        }

                        var entry = BuildEntry(elemType, boss, mapKey, ev);
                        if (entry == null) { _log?.Warning($"[EventRotator] {mapKey}: could not build entry for '{boss}'"); continue; }
                        list.Add(entry);
                        existing.Add(boss);
                        _log?.Info($"[EventRotator] {mapKey}: appended boss '{boss}' (src={_lastBuildSrc}, difficult='{Prop(entry, "BossDifficulty") as string}', escortDifficult='{Prop(entry, "BossEscortDifficulty") as string}', escort='{Prop(entry, "BossEscortType") as string}', amount='{Prop(entry, "BossEscortAmount") as string}', chance={ChanceOf(entry):0.#}, marker={Prop(entry, "ShowOnTarkovMap")}, mode=[{string.Join(",", (Prop(entry, "SpawnMode") as IEnumerable<string>) ?? Enumerable.Empty<string>())}])");
                    }
                }
            }

            // ★★★ 1.4 后置块（P0 修复 B2）：官方随从人数覆盖 —— 必须在**全部追加路径**之后执行
            //   追加路径共三处：① markerOnly 兜底追加（约 :1355 `list.Add(entry)`）② AppendSeasonalBossEntries（1.5）
            //                    ③ 上面这个 `want` 逐条追加
            //   旧位置早于 ②③ ⇒ 追加进来的首领（rezervbase 5 个 / laboratory Gluhar / lighthouse Gluhar /
            //   shoreline 7 个）第一次落地用的是**他图模板**的护卫数（Gluhar 模板 8 名），
            //   活动写的 6/3/4 名要靠"同局第二个挂点再跑一次"才补上 —— 隐式时序、不可靠。
            //   现在放到最后：本局第一次调用即一次到位（where=db 时同样在追加之后）。
            //
            // ★★★ 缺陷1 修复（2026-10-24）后本块的角色变化：
            //   **本 mod 新建的条目**（三条追加路径全部走 `BuildEntry`）现在在**构造时**就按
            //   "活动覆盖优先、内置默认兜底"写好了 escortType/escortCount ⇒ 到这儿已经是同值、直接跳过
            //   （不会再刷「先默认后覆盖」两条日志，也不再有"第一落地用错值"的窗口）。
            //   本块**保留**且仍然必要：它负责"**本来就存在于列表里**的条目" —— ABPS 重建出来的、
            //   本体原版自带的、地图 mod 提供的那些（它们不是我们建的，无法在构造时介入）。
            //   对它们语义不变：活动声明优先（可还原，与 ApplyExtraDimensions 同值幂等）。
            if (ev.EscortType != null && ev.EscortType.Count > 0 && list != null
                && ev.EscortType.TryGetValue(mapKey, out var escortTypeByBoss) && escortTypeByBoss != null)
            {
                foreach (var it in list)
                {
                    var bn = Prop(it, "BossName")?.ToString();
                    if (bn == null || !escortTypeByBoss.TryGetValue(bn, out var tp) || string.IsNullOrWhiteSpace(tp)) continue;
                    var beforeT = Prop(it, "BossEscortType")?.ToString();
                    if (string.Equals(beforeT, tp, StringComparison.OrdinalIgnoreCase)) continue;
                    SetOriginal(it, "BossEscortType", tp);
                    _log?.Info($"[EventRotator] {mapKey}: 随从类型按活动覆盖 {bn}: {beforeT} → {tp}（**已存在**的条目，非本 mod 构建）");
                }
            }

            if (ev.EscortCount != null && ev.EscortCount.Count > 0 && list != null
                && ev.EscortCount.TryGetValue(mapKey, out var escortByBoss) && escortByBoss != null)
            {
                foreach (var it in list)
                {
                    var bn = Prop(it, "BossName")?.ToString();
                    if (bn == null || !escortByBoss.TryGetValue(bn, out var cnt)) continue;
                    var before = Prop(it, "BossEscortAmount")?.ToString();
                    var afterCnt = cnt.ToAmountString();
                    // ★ 缺陷1 修复配套：**已经同值就跳过**（本 mod 构建的条目在 BuildEntry 里已按活动覆盖写好）。
                    //   旧实现无条件重写并**无条件打日志** ⇒ 每局都会刷一条 "随从人数按官方覆盖 … 0 → 4"，
                    //   正是本次缺陷的日志证据。现在只对"别人建的那些条目"（值确实不同）才写 + 记日志。
                    if (string.Equals(before, afterCnt, StringComparison.OrdinalIgnoreCase)) continue;
                    SetOriginal(it, "BossEscortAmount", afterCnt);   // ★ 支持范围（"0-2"）
                    _log?.Info($"[EventRotator] {mapKey}: 随从人数按官方覆盖 {bn}: {before} → {afterCnt}"
                             + (cnt.IsRange ? "（范围）" : "") + "（**已存在**的条目，非本 mod 构建）");
                }
            }

            // ★★ 2026-10-31【仪式邪教徒补员】用户实测：仪式处只刷 1 个邪教徒，打死就"仪式失败"。
            //   官方仪式是"祭司 + 一圈邪教徒"，单人是因为召唤条目没有随从配置。
            //   这里只为「万圣节 2023（Zryachiy 仪式）」的祭司类条目补随从（不动其它活动/其它 boss）。
            if (string.Equals(ev.Id, "halloween_2023", StringComparison.OrdinalIgnoreCase) && list != null)
            {
                int fixedCnt = 0;
                foreach (var it in list)
                {
                    var bn = Prop(it, "BossName")?.ToString() ?? "";
                    if (bn.IndexOf("sectact", StringComparison.OrdinalIgnoreCase) < 0
                        && bn.IndexOf("sectantPriest", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var amt = Prop(it, "BossEscortAmount")?.ToString();
                    if (!string.IsNullOrEmpty(amt) && amt.Trim() != "0") continue;   // 已有随从就不动
                    SetOriginal(it, "BossEscortType", "sectantWarrior");
                    SetOriginal(it, "BossEscortAmount", "6");
                    fixedCnt++;
                }
                if (fixedCnt > 0 && (where == "db" || where == "post"))
                    _log?.Warning($"[EventRotator][仪式] {mapKey}：给 {fixedCnt} 条邪教祭司条目补了 6 名随从（sectantWarrior）"
                                + " —— 原先没有随从，仪式处只会站 1 个邪教徒，打死就判失败");
            }

            if (where == "db" && _loggedApplySummary.Add(mapKey + "|" + ev.Id + "|c")) LogApplySummary(mapKey, ev.Id, list);   // ★ v2.6.90
        }
        catch (Exception ex)
        {
            _log?.Warning($"[EventRotator] boss apply failed ({where}): {ex.Message}");
        }
    }

    /// <summary>形如合法 WildSpawnType / BotDifficulty 名字（客户端 Enum.Parse 拿到 null 会直接崩）。</summary>
    private static bool IsValidName(string v) => !string.IsNullOrWhiteSpace(v) && v.All(char.IsLetterOrDigit);

    // ================= 调试日志 =================

    /// <summary>
    /// DB 加载时，给每个被本活动碰过的地图打一行汇总：
    ///   bosses=[bot:概率*骷髅, ...]  —— 一眼看出活动要刷的东西有没有落到位
    /// </summary>
    private static void LogApplySummary(string mapKey, string eventId, IList list)
    {
        if (!EventEngine.DebugLogging) return;
        try
        {
            var parts = new List<string>();
            foreach (var it in list)
            {
                var name = Prop(it, "BossName")?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var chance = Prop(it, "BossChance");
                var marker = Prop(it, "ShowOnTarkovMap") as bool?;
                parts.Add($"{name}:{chance}{(marker == true ? "*" : "")}");
            }
            // ★ 1.3.6：默认只打「哪张图 ← 哪个活动 | 共几条」，**不列明细**。
            //   明细（一次 30~40 项、含大量 pmcUSEC/pmcBEAR）只在 logLevel=verbose 才打 —— 
            //   否则整行折行几百字，用户会当成报错（已经在 Forge 评论区被贴过一次）。
            if (EventEngine.VerboseLogging)
                EventEngine.Debug($"[EventRotator][汇总] {mapKey} ← {eventId} | {parts.Count} 条 | {string.Join(" ", parts)}");
            else
                EventEngine.Debug($"[EventRotator][汇总] {mapKey} ← {eventId} | {parts.Count} 条");
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] 汇总输出失败({mapKey}): {ex.Message}"); }
    }

    /// <summary>
    /// 已处理过的地图也要输出进图计划（否则只在服务端启动后第一局有日志）。
    /// 同时按"地图对象是否变化"来判定新一局，从而重置 bot 生成统计。
    /// </summary>
    private static void LogRaidPlanForMap(object mapBase, string where)
    {
        try
        {
            var mapKey = mapBase.GetType().GetProperty("Id")?.GetValue(mapBase)?.ToString()?.ToLowerInvariant() ?? "?";
            // v1.97 多活动：把全部生效活动都列出来
            var evIds = EventEngine.Actives.Count > 0
                ? string.Join("+", EventEngine.Actives.Select(a => a.Id))
                : "(none)";
            var list = mapBase.GetType().GetProperty("BossLocationSpawn")?.GetValue(mapBase) as IList;
            if (list != null) LogRaidPlan(mapKey, evIds, where, list);
        }
        catch { }
    }

    /// <summary>每局地图对象（用于判定"新一局"并重置 bot 统计）。</summary>
    private static object _lastRaidMapObj;
    /// <summary>上一局测到的条目数（Fika 下一局共用同一对象，用它判断是否换了新局）。</summary>
    private static int _lastRaidCount = -1;
    /// <summary>本局已为哪张图打过一次"刷新计划"日志（Fika 里同一对象会被多个玩家各触发一次）。</summary>
    /// <summary>★ v2.6.88【L2 批准】"每局每图每活动只打一次"的进图计划日志门。
    /// 原来是**单个 string**（只能记住最后一张图 ⇒ 多活动/多图交替时互相顶掉 ⇒ 门反复放行）。</summary>
    private static readonly HashSet<string> _loggedRaidPlanFor = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>★ v2.6.90【最后一行批准】`LogApplySummary` 的"每次套用每图每活动只打一次"门（三处调用点各一档）。</summary>
    private static readonly HashSet<string> _loggedApplySummary = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 进图时打印这张图的刷新计划：
    ///   basic   —— 概要 + **100% 必刷**的条目（活动要刷的东西一定在这里）
    ///   verbose —— 再加逐条明细（概率/骷髅/难度/zone）
    /// </summary>
    /// <summary>
    /// ★★ 2026-10-31【孤儿季节首领 · 全季节通用】
    /// <para>接管原版季节窗后，SPT 仍可能在启动时把**任意原生季节活动**的首领/感染者塞进地图
    /// （halloweensummon 的扎里亚奇/祭司、halloweenzombies 的感染者、halloweennightcult 的预言者、圣诞的礼物老人…）。
    /// 没有对应仪式数据时打死它们 ⇒ 不出复活光柱、客户端可能全场黑屏（用户实测 4 把 1 次黑屏）。</para>
    /// <para><b>规则</b>：只保留"我们当前活动声明要用的那批"——
    /// 我们每个活动声明的 <c>forceSeasonalBossKey</c> 决定允许哪个原生季节键；
    /// 若某条 spawn 的 BossName 只出现在原生季节表里、且不在允许集合内，就摘掉（并打日志）。
    /// 我们自己注入的条目（活动 bossSpawns 明写的）一律保留。</para>
    /// </summary>
    /// <summary>
    /// ★ 2026-11-01【第二局没僵尸】用户实测：第一局正常，第二局清图无僵尸。
    /// 原因：ABPS 在**每局结束/开始**都会用自身缓存整表重写 BossLocationSpawn，
    /// SPT 启动时塞进去的 `infected*` 条目被一并抹掉，之后没人补 ⇒ 第二局空图。
    /// 修法：当前活动是僵尸族时，按官方 `eventBossSpawns.halloweenzombies[map]` 的条目克隆补齐（缺什么补什么）。
    /// </summary>
    /// <summary>把 SeasonalCfg.EventBossSpawns 反射成 {key: {map: [条目对象]}}（避免编译期依赖具体泛型）。</summary>
    private static Dictionary<string, Dictionary<string, List<object>>> StripOrphanSeasonalBosses_Table()
    {
        try
        {
            var cfg = EventRotatorServer.SeasonalCfg;
            if (cfg == null) return null;
            var prop = cfg.GetType().GetProperty("EventBossSpawns");
            if (prop?.GetValue(cfg) is not IDictionary outer) return null;
            var res = new Dictionary<string, Dictionary<string, List<object>>>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry kv in outer)
            {
                var key = kv.Key?.ToString();
                if (string.IsNullOrEmpty(key) || kv.Value is not IDictionary inner) continue;
                var maps = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry mk in inner)
                {
                    var map = mk.Key?.ToString();
                    if (string.IsNullOrEmpty(map)) continue;
                    if (mk.Value is not IEnumerable seq) continue;
                    var items = new List<object>();
                    foreach (var o in seq) if (o != null) items.Add(o);
                    if (items.Count > 0) maps[map] = items;
                }
                if (maps.Count > 0) res[key] = maps;
            }
            return res;
        }
        catch { return null; }
    }

    private static void EnsureZombieSpawns(string mapKey, object mapBase)
    {
        try
        {
            if (EventEngine.Actives == null) return;
            bool zombieOn = EventEngine.Actives.Any(a => a != null
                && string.Equals(a.Id, "halloween", StringComparison.OrdinalIgnoreCase));
            if (!zombieOn) return;
            if (EventRotatorServer.SeasonalCfg == null) return;
            if (StripOrphanSeasonalBosses_Table() is not { } table) return;
            if (!table.TryGetValue("halloweenzombies", out var byMap) || byMap == null) return;
            if (!byMap.TryGetValue(mapKey, out var defs) || defs.Count == 0) return;

            var list = mapBase.GetType().GetProperty("BossLocationSpawn")?.GetValue(mapBase) as IList;
            if (list == null) return;

            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in list)
            {
                if (it == null) continue;
                var nm = Prop(it, "BossName")?.ToString() ?? "";
                var zn = Prop(it, "BossZone")?.ToString() ?? "";
                have.Add(nm + "|" + zn);
            }

            int added = 0;
            foreach (var def in defs)
            {
                var nm = Prop(def, "BossName")?.ToString() ?? "";
                var zn = Prop(def, "BossZone")?.ToString() ?? "";
                if (nm.Length == 0 || have.Contains(nm + "|" + zn)) continue;
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(def, def.GetType());
                    var clone = System.Text.Json.JsonSerializer.Deserialize(json, def.GetType());
                    if (clone == null) continue;
                    list.Add(clone);
                    have.Add(nm + "|" + zn);
                    added++;
                }
                catch { }
            }
            if (added > 0)
                _log?.Warning($"[EventRotator][感染者] {mapKey}：补回 {added} 条被 ABPS 重建抹掉的僵尸条目"
                            + "（第一局正常、第二局清图的根因）");
        }
        catch (Exception ex) { _log?.Warning("[EventRotator][感染者] 补僵尸条目失败: " + ex.Message); }
    }

    private static void StripOrphanSeasonalBosses(string mapKey, object mapBase, string where)
    {
        try
        {
            if (!EventRotator.Patches.SeasonalTakeoverPatch.Active) return;
            var list = mapBase.GetType().GetProperty("BossLocationSpawn")?.GetValue(mapBase) as IList;
            if (list == null || list.Count == 0) return;

            var actives = EventEngine.Actives ?? new List<EventDef>();

            // ① 我们允许的原生季节键（活动声明的 forceSeasonalBossKey）
            var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in actives)
            {
                if (!string.IsNullOrEmpty(a.ForceSeasonalBossKey)) allowedKeys.Add(a.ForceSeasonalBossKey);
                // ★ 2026-10-31【全量映射】只靠 forceSeasonalBossKey 会误摘（实验室 19 条 infected* 被当孤儿删光）。
                foreach (var k in AllowedSeasonKeysFor(a.Id)) allowedKeys.Add(k);
            }

            // ② 我们自己活动在这张图明写的 boss（一律保留）
            var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in actives)
            {
                if (a.BossSpawns != null)
                {
                    foreach (var kv in a.BossSpawns)
                        if (kv.Value != null) foreach (var b in kv.Value) ours.Add(b);
                    // 通配图也当"我们声明过"
                    if (a.BossSpawns.TryGetValue("*", out var all) && all != null)
                        foreach (var b in all) ours.Add(b);
                }
            }

            // ③ SPT 原生季节表：key → 图 → boss 名集合
            // ★★★ 2026-10-31【真凶】启动时我们把 halloweensummon 等"整场"从配置里摘掉了，
            //   于是运行时 SeasonalBossTable() 里就没有 peacefullZryachiyEvent 这些名字
            //   ⇒ 孤儿清理永远匹配不到 ⇒ 狂野森林期间照旧刷小鹿。
            //   修法：**永远**把内置名单并进来（不带条件）。
            var seasonal = SeasonalBossTable();
            if (seasonal == null) seasonal = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in BuiltinSeasonalBossTable())
                if (!seasonal.ContainsKey(kv.Key)) seasonal[kv.Key] = kv.Value;
            if (seasonal.Count == 0)
            {
                if (where == "post" && _seasonDiagLogged.Add(mapKey))
                    _log?.Warning($"[季节首领] {mapKey}：⚠ 季节表为空（SeasonalCfg={(EventRotatorServer.SeasonalCfg == null ? "null" : "已就绪")}）⇒ 无法判定，跳过清理");
                return;
            }

            // ④ 允许集合 = 允许键对应的所有 boss 名
            var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in seasonal)
                if (allowedKeys.Contains(kv.Key))
                    foreach (var n in kv.Value) allowedNames.Add(n);

            // ⑤ 全季节 boss 名（用来判定"这条是不是原生塞进来的"）
            var allSeasonalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in seasonal) foreach (var n in kv.Value) allSeasonalNames.Add(n);

            var remove = new List<object>();
            var names = new List<string>();
            foreach (var it in list)
            {
                var bn = Prop(it, "BossName")?.ToString();
                if (string.IsNullOrEmpty(bn)) continue;
                if (!allSeasonalNames.Contains(bn)) continue;      // 不是季节首领 ⇒ 不动
                if (allowedNames.Contains(bn)) continue;           // 当前活动要用 ⇒ 保留
                if (ours.Contains(bn)) continue;                   // 我们自己声明的 ⇒ 保留
                // 灯塔的常规扎里亚奇（ABPS 配置）不在季节表里，不会被命中；这里再加一道保险
                if (string.Equals(bn, "bossZryachiy", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(mapKey, "lighthouse", StringComparison.OrdinalIgnoreCase)) continue;
                remove.Add(it);
                if (!names.Contains(bn)) names.Add(bn);
            }
            if (remove.Count == 0)
            {
                if (where == "post" && _seasonDiagLogged.Add(mapKey))
                    _log?.Info($"[季节首领] {mapKey}：无需清理（季节表 {seasonal.Count} 组｜允许={string.Join(",", allowedKeys)}｜条目 {list.Count} 个）");
                return;
            }
            foreach (var it in remove) list.Remove(it);
            _log?.Warning($"[EventRotator][季节首领] {mapKey}：摘掉 {remove.Count} 条孤儿季节条目（{string.Join(",", names)}）"
                        + $"——当前活动没有声明对应季节键（允许={string.Join(",", allowedKeys)}）；"
                        + "留着会出现「有首领没仪式 ⇒ 无光柱/黑屏」");
        }
        catch (Exception ex) { _log?.Warning("[EventRotator][季节首领] 清理失败: " + ex.Message); }
    }

    private static Dictionary<string, List<string>> _seasonalBossCache;
    private static readonly HashSet<string> _seasonDiagLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从运行时 SeasonalEventConfig.EventBossSpawns 读"每个原生季节键会塞哪些 boss"（失败返回空表）。</summary>
    /// <summary>
    /// ★ 2026-10-31【兜底名单】运行时读不到 EventBossSpawns 时用这份（逐字抄 SPT seasonalevents.json 的 key → boss）。
    /// 目的：狂野森林等**非万圣节活动**期间，地图上不该出现和平小鹿/邪教祭司/复仇小鹿/感染者。
    /// </summary>
    /// <summary>
    /// ★ 2026-10-31【活动 id → 允许保留的原生季节键】
    /// 只按 forceSeasonalBossKey 判断会误摘：例如活动 id=`halloween`（僵尸档）没声明任何键，
    /// 结果实验室 19 条 infected* 被当成"孤儿季节条目"全删 ⇒ 有停电、没僵尸。
    /// 这里按活动族一次性把该保留的键列全（宁多勿错杀：多留只影响该活动本身的表现，错杀会直接没怪）。
    /// </summary>
    /// <summary>★ 启动自检：把"季节族活动 → 允许保留的键"整表打出来，方便人工核对轮换不会误摘。</summary>
    internal static void LogSeasonAllowTable(System.Collections.Generic.IEnumerable<string> eventIds, Action<string> info)
    {
        try
        {
            var lines = new List<string>();
            int total = 0, seasonalCount = 0;
            foreach (var id in (eventIds ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
            {
                total++;
                var keys = AllowedSeasonKeysFor(id).ToList();
                if (keys.Count > 0) { seasonalCount++; lines.Add($"{id} → {string.Join("+", keys)}"); }
            }
            info?.Invoke($"[季节族自检] 共 {total} 个活动，其中 {seasonalCount} 个属于季节族（会保留原生季节首领）：");
            foreach (var l in lines) info?.Invoke("    " + l);
            info?.Invoke("    （其余活动一律摘掉原生季节首领：和平小鹿/祭司/僵尸/gifter）");
        }
        catch { }
    }

    private static IEnumerable<string> AllowedSeasonKeysFor(string id)
    {
        var k = (id ?? "").Trim().ToLowerInvariant();
        if (k.Length == 0) yield break;

        if (k == "halloween")                      // 2024 僵尸档
        { yield return "halloweenzombies"; yield return "halloweennightcult"; yield break; }
        if (k == "halloween_nozombie")             // 无僵尸版（仪式/邪教）
        { yield return "halloweensummon"; yield return "halloweennightcult"; yield break; }
        if (k.StartsWith("halloween_2023"))         // Zryachiy 召唤仪式
        { yield return "halloweensummon"; yield return "halloweennightcult"; yield break; }
        if (k.StartsWith("halloween"))              // 2022 / 2019 / 2020 等旧僵尸档
        { yield return "halloweenzombies"; yield return "halloweennightcult"; yield return "halloweensummon"; yield break; }
        if (k.StartsWith("christmas") || k == "newyear" || k.StartsWith("khorovod"))
        { yield return "christmas"; yield break; }
        if (k.StartsWith("april"))
        { yield return "halloweensummon"; yield break; }   // 官方愚人节沿用召唤素材（保持既有行为）
    }

    private static Dictionary<string, List<string>> BuiltinSeasonalBossTable() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["halloweensummon"] = new List<string> { "peacefullZryachiyEvent", "sectactPriestEvent", "ravangeZryachiyEvent", "bossTagilla" },
        ["halloweenzombies"] = new List<string> { "infectedAssault", "infectedPmc", "infectedCivil", "infectedLaborant", "infectedTagilla" },
        ["halloweennightcult"] = new List<string> { "sectantPredvestnik", "sectantPriest" },
        ["christmas"] = new List<string> { "gifter" },
    };

    private static Dictionary<string, List<string>> SeasonalBossTable()
    {
        if (_seasonalBossCache != null) return _seasonalBossCache;
        var res = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cfg = EventRotatorServer.SeasonalCfg;
            if (cfg == null) return res;
            var p = cfg.GetType().GetProperty("EventBossSpawns", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var dict = p?.GetValue(cfg) as IDictionary;
            if (dict == null) return res;
            foreach (DictionaryEntry kv in dict)
            {
                var key = kv.Key?.ToString();
                if (string.IsNullOrEmpty(key)) continue;
                var set = new List<string>();
                if (kv.Value is IDictionary maps)
                {
                    foreach (DictionaryEntry m in maps)
                    {
                        if (m.Value is IEnumerable arr && m.Value is not string)
                            foreach (var it in arr)
                            {
                                var nm = Prop(it, "BossName")?.ToString();
                                if (!string.IsNullOrEmpty(nm)) set.Add(nm);
                            }
                    }
                }
                if (set.Count > 0) res[key] = set.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch { }
        // ★ 2026-10-31【关键】只有真的读到内容才缓存：
        //   db 阶段 ApplyMap 可能在 OnLoad 之前跑，那时 SeasonalCfg 还是 null ⇒ 缓存空表
        //   ⇒ 之后所有孤儿季节首领清理都静默失效（实测：狂野森林期间地图上仍有和平小鹿）。
        if (res.Count > 0) _seasonalBossCache = res;
        return res;
    }

    /// <summary>
    /// ★ 2026-10-31【仪式圈邪教徒】给「万圣节 2023（仪式）」的祭司类条目补 6 名随从。
    /// 原来条目没有 BossEscortAmount ⇒ 仪式圈只站 1 个邪教徒，打死就判"仪式失败"。
    /// 只处理 BossName 含 sectact / sectantPriest 的条目；已有随从的不动。
    /// </summary>
    private static void FixRitualEscorts(string mapKey, object mapBase, string where)
    {
        try
        {
            var list = mapBase.GetType().GetProperty("BossLocationSpawn")?.GetValue(mapBase) as IList;
            if (list == null || list.Count == 0) return;
            int fixedCnt = 0;
            foreach (var it in list)
            {
                var bn = Prop(it, "BossName")?.ToString() ?? "";
                if (bn.IndexOf("sectact", StringComparison.OrdinalIgnoreCase) < 0
                    && bn.IndexOf("sectantPriest", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var amt = Prop(it, "BossEscortAmount")?.ToString();
                var typ = Prop(it, "BossEscortType")?.ToString();
                if (!string.IsNullOrEmpty(amt) && amt.Trim() != "0"
                    && !string.IsNullOrEmpty(typ) && typ.Trim() != "0") continue;   // 已有随从配置 ⇒ 不动
                SetOriginal(it, "BossEscortType", "sectantWarrior");
                SetOriginal(it, "BossEscortAmount", "6");
                fixedCnt++;
            }
            if (fixedCnt > 0)
                _log?.Warning($"[EventRotator][仪式] {mapKey}：给 {fixedCnt} 条邪教祭司条目补了 6 名随从（sectantWarrior）"
                            + " —— 原先没有随从，仪式圈只会站 1 个邪教徒，打死就判失败");
        }
        catch (Exception ex) { _log?.Warning("[EventRotator][仪式] 补随从失败: " + ex.Message); }
    }

    private static void LogRaidPlan(string mapKey, string eventId, string where, IList list)
    {
            long _tLRP1 = PerfTimer.Start();   // ★ v2.6.89 入口 → gate（含 `all`/`guaranteed` 的构建与 LINQ）
        if (!EventEngine.DebugLogging) return;
        if (where != "pre" && where != "post") return;      // DB 加载期不刷屏
        try
        {
            var all = new List<(string name, double chance, bool marker, string diff, string zone)>();
            foreach (var it in list)
            {
                var name = Prop(it, "BossName")?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var chance = Convert.ToDouble(Prop(it, "BossChance") ?? 0d);
                var marker = (Prop(it, "ShowOnTarkovMap") as bool?) == true;
                var diff = Prop(it, "BossDifficulty")?.ToString();
                var zone = Prop(it, "BossZone")?.ToString();
                all.Add((name, chance, marker, diff, zone));
            }

            var guaranteed = all.Where(x => x.chance >= 100).ToList();
            // ★ Fika：一局联机里每个玩家都会触发一次同图的这条路径（大家共用同一个 LocationData），
            //   所以每局每图只打一次，避免日志被 N 份重复刷屏。
            PerfTimer.End("进图计划.LogRaidPlan[gate前]", _tLRP1);
            long _tLRP2 = PerfTimer.Start();
            if (!_loggedRaidPlanFor.Add(mapKey + "|" + eventId)) return;   // ★ v2.6.88【L2】键 = 图|活动
            // ★ v2.6.88【L2】原来的"记录最后一张图"已被上面的 `Add(mapKey+"|"+eventId)` 取代（键更细 ⇒ 门更强）

            PerfTimer.End("进图计划.LogRaidPlan[gate后]", _tLRP2);
            EventEngine.Debug($"[EventRotator][进图] hook={where} 地图={mapKey} 活动={eventId} 首领条目={all.Count} 个（其中概率100%的 {guaranteed.Count} 个）");
            // ★ v0.1.5 自证：进图瞬间 globals.config.EventType 是否仍是我们设的 [Halloween, HalloweenIllumination]。
            //   若被 SPT 的季节评估重置回 [None]，客户端就收不到"万圣节"信号（感染条 / 僵尸行为都会不对）。
            try
            {
                var gt = EventRotatorServer.GlobalTableRef;
                var etNow = Prop(Prop(gt, "Configuration"), "EventType") as System.Collections.IEnumerable;
                if (etNow != null)
                {
                    var names = new List<string>();
                    foreach (var x in etNow) names.Add(x?.ToString() ?? "");
                    EventEngine.Debug($"[EventRotator][进图] globals.config.EventType = [{string.Join(", ", names)}]（含僵尸的活动应为 Halloween）");
                }
            }
            catch { }
            if (guaranteed.Count > 0)
                EventEngine.Debug($"[EventRotator][进图] ★必刷(100%)：{string.Join("  ", guaranteed.Select(x => x.name + (x.marker ? "[骷髅]" : "")))}");
            else
                EventEngine.Debug("[EventRotator][进图] ⚠ 本图没有任何 100% 必刷条目（活动可能只提升概率 / 或配置不匹配本图）");

            if (EventEngine.VerboseLogging)
                foreach (var x in all.OrderByDescending(v => v.chance))
                    EventEngine.Verbose($"      {x.name,-24} 概率={x.chance,-5:0.#}{(x.marker ? " [骷髅]" : "")} 难度={x.diff ?? "-"} zone='{x.zone}'");
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] 进图计划输出失败({mapKey}): {ex.Message}"); }
    }

    /// <summary>本局每种 bot 实际生成了多少个（PrepareAndGenerateBot 后置补丁累计）。</summary>
    private static readonly Dictionary<string, int> _spawned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// BotGenerator.PrepareAndGenerateBot 的 Postfix：统计本局实际生成的 bot 角色。
    /// __1=BotGenerationDetails  __result=BotBase
    /// 取值优先级：bot.Info.Settings.Role → details.Role；事件专属 details.EventRole 单独记一份。
    /// </summary>
    private static void BotGenerated(object __1, object __result, MethodBase __originalMethod)
    {
        // ★★★ v1.0.2【补丁自证】进程内首次被调用时打一条：挂载/调用是不是同一份程序集（见 PatchProbe）。
        try { PatchProbe.OnCall(__originalMethod, "BotGenerator.PrepareAndGenerateBot"); } catch { }
        // ★★★ 清场小队装备差异化（task-33）：**必须放在 DebugLogging 守卫之前** ——
        //   它是**功能**而不是统计，日志等级关掉时也得生效。
        //   改写的是我们自己的产出物（`__result` = 最终 BotBase，含被 APBS 接管的装备生成结果）。
        //   本方法自带完整 try/catch + 只在活动登记了该 role 时才动手 ⇒ 不影响下面的统计逻辑。
        try { EventGearUpgrade.OnBotGenerated(__1, __result); } catch { }
        // ★★★ v2.6.74【走私者 GP 币 10~20 枚】与上面同理：**功能**，必须放在 DebugLogging 守卫之前。
        //   改写的是我们自己的产出物（`__result` = 最终 BotBase，含被 PBS/ABPS 接管的战利品生成结果）：
        //   把该 role 身上所有目标物品合并成一叠、数量重写成 [min,max]；一个都没有就加进 Pockets。
        //   为什么不能只靠 `botLootAdd_` + `CurrencyStackSize`，见 `BotLootStackDimension` 类注释。
        try { BotLootStackDimension.OnBotGenerated(__1, __result); } catch { }
        // ★★★【面具后置强制修正】casus_belli 劫掠者（arenaFighterEvent）的 FaceCover：
        //   本机 PBS（progressivebotsystem）用 Prefix 拦截了 BotInventoryGenerator.GenerateInventory，
        //   装备池/概率全取它自己的 tier 数据（arenaFighterEvent → ExUsec，FaceCover 75% × 含半面巾的 10 件池）
        //   ⇒ 我们的 BotTable 池替换 + chances=100 **在它接管时是死代码**。
        //   这里改的是**生成后的 BotBase**（__result），PBS 绕不过去；函数内部只认 arenaFighterEvent + 5 张图。
        try { FaceCoverFixDimension.OnBotGenerated(__1, __result); } catch { }
        // ★★★ v2.6.72【Y2b】**两级拆分**：
        //   ① **轻量计数（basic 也要）**：角色字符串（`Prop` 已缓存）+ 两个字典自增 ⇒ 供每局汇总行使用。
        //      这就是"生成**调用**次数"，本来就**不等于实际 bot 数**（旧注释已实证：`BotBase.Id` 是
        //      `Nullable<MongoId>`、去重早已失效，数值依旧虚高）⇒ 汇总行已如实标明。
        //   ② **明细去重（verbose 才做）**：每次 `Prop(Id)`/`Prop(Info.Settings.ProfileId)` + `HashSet.Add`
        //      —— 这 370 次/局的反射+分配原本是白花的（去重恒失效）⇒ 收紧到 verbose。
        try
        {
            var role = FirstString(__result, "Info", "Settings", "Role")
                    ?? FirstString(__1, "Role")
                    ?? FirstString(__1, "RoleLowercase")
                    ?? "?";
            _spawned[role] = _spawned.TryGetValue(role, out var c) ? c + 1 : 1;

            var eventRole = FirstString(__1, "EventRole");
            if (!string.IsNullOrEmpty(eventRole))
                _spawnedEvent[eventRole] = _spawnedEvent.TryGetValue(eventRole, out var c2) ? c2 + 1 : 1;
        }
        catch { }
        if (!EventEngine.VerboseLogging) return;      // ★ v2.6.72【Y2b】下面是"明细"专用（去重集合，仅 verbose）
        try
        {
            var id = Prop(__result, "Id")?.ToString()
                  ?? Prop(Prop(Prop(__result, "Info"), "Settings"), "ProfileId")?.ToString();
            if (!string.IsNullOrEmpty(id) && !_seenBotIds.Add(id)) return;   // 已统计过（verbose 明细去重）
        }
        catch { }
    }

    /// <summary>本局已统计过的 bot 唯一 Id（去重用）。</summary>
    private static readonly HashSet<string> _seenBotIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本局事件专属角色（BotGenerationDetails.EventRole）计数。</summary>
    private static readonly Dictionary<string, int> _spawnedEvent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>打印本局生成过的 bot 角色统计（回答"到底刷没刷"）。</summary>
    private static void LogSpawnedCounts(string mapKey)
    {
        if (!EventEngine.VerboseLogging) return;      // ★ v2.6.72【Y2b】明细只在 verbose 输出（basic 走汇总行）
        try
        {
            if (_spawned.Count == 0)
            {
                EventEngine.Debug($"[EventRotator][生成调用次数] 地图={mapKey} 本局还没记录到 bot 生成调用");
                return;
            }
            var parts = _spawned.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}");
            // ⚠️⚠️ 这不是"实际 bot 数"！PrepareAndGenerateBot 会被重复调用（含拒收/重试/预生成），
            //   而 BotBase.Id 是 Nullable<MongoId>（HasValue=false 时 ToString() 返回 ""），
            //   导致按 Id 去重失效 → 数值严重虚高（实测 assault×133，一局不可能有这么多 SCAV）。
            //   ★ 要"实际刷了什么"请以**客户端**的 [真实分布] 为准（读的是 raid 里真实存在的 bot 列表）。
            EventEngine.Debug($"[EventRotator][生成调用次数（生成调用次数，不等于实际生成数量）] 地图={mapKey} {string.Join(" ", parts)}");
            if (_spawnedEvent.Count > 0)
                EventEngine.Debug($"[EventRotator][生成调用次数（生成调用次数）] 事件专属角色：{string.Join(" ", _spawnedEvent.Select(kv => kv.Key + "×" + kv.Value))}");
        }
        catch { }
    }

    /// <summary>BotController.GenerateBotWaves 的 Postfix：一波生成完后输出本局 bot 统计。</summary>
    private static void AfterBotWaves()
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.71【Y2】每波埋点
        // ★★★ v2.6.72【Y1b】**原来这里每波打一条长日志**（`LogSpawnedCounts`，含 LINQ+插值+文件 I/O，
        //   一局 10~40 次 ⇒ 用户实测的"战局内一会小卡一下"）。现在**改为每局一条汇总**，
        //   由 `FlushSpawnSummary` 在换局时统一输出 ⇒ 本方法不再写日志。
        PerfTimer.End("每波.AfterBotWaves", t0);
    }

    /// <summary>
    /// ★★★ v2.6.72【Y1b/Y2b】**每局一条"生成汇总"**（`basic` 也可见 —— 排查"活动到底生效没有"靠它）：
    /// 例：`[生成汇总] 上一局 地图=woods 共记录 370 次 bot 生成调用（清场小队命中 12 只）｜Top：assault×134 …`
    /// 明细（逐角色长行）由 `LogSpawnedCounts` 在 `verbose` 下**同时**输出 ⇒ 信息不丢。
    /// </summary>
    private static void FlushSpawnSummary(string mapKey)
    {
        try
        {
            if (!EventEngine.DebugLogging) return;                       // basic ⇒ 汇总仍然可见
            int total = 0; foreach (var v in _spawned.Values) total += v;
            if (total == 0 && EventGearUpgrade.HitsThisRaid == 0) return; // 本局没记录 ⇒ 不打（与旧行为一致）
            var top = _spawned.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key + "×" + kv.Value);
            EventEngine.Debug($"[EventRotator][生成汇总] 上一局 地图={mapKey} 共记录 {total} 次 bot 生成调用"
                            + $"（**清场小队命中 {EventGearUpgrade.HitsThisRaid} 只**）"
                            + string.Join(" ", top.Select(x => "｜" + x))
                            + (_spawnedEvent.Count > 0 ? "｜事件角色：" + string.Join(" ", _spawnedEvent.Select(kv => kv.Key + "×" + kv.Value)) : ""));
            LogSpawnedCounts(mapKey);                                    // 明细：仅 verbose（旧内容，改成每局一次）
            EventGearUpgrade.ResetHits();
        }
        catch { }
    }

    /// <summary>本局地图名（进图计划输出时记录，供 bot 统计使用）。</summary>
    private static string _lastRaidMap;

    /// <summary>按属性路径取字符串（任一步为 null 就返回 null）。</summary>
    /// <summary>
    /// ★★★ v2.6.82【全量优化 · 第1件】**走已缓存的 `Prop`（`_propCache3`）** —— 原实现每次
    /// `cur.GetType().GetProperty(p, Public|Instance).GetValue(cur)`（每 bot ×4 次未缓存反射）。
    /// **同步语义**：同 `BindingFlags`（`Public|Instance`，与 `_propCache3` 一致）· 同"任一环为 null 即返回 null" ·
    /// 同 `cur?.ToString()`；`GetValue` 的异常照旧传播（`Prop` 里没有 catch）。
    /// ⚠ `params` 版本**保留**作兜底（>3 段路径仍可用）。
    /// </summary>
    private static string FirstString(object root, params string[] path)
    {
        object cur = root;
        foreach (var p in path)
        {
            if (cur == null) return null;
            cur = Prop(cur, p);                       // ★ v2.6.82：走缓存（原来每次 GetProperty）
        }
        return cur?.ToString();
    }

    // ★★★ v2.6.82【第1件】三个**定参重载** ⇒ 消除 `params string[]` 的每调用一次数组分配
    //   （实测调用形如 `FirstString(__result, "Info","Settings","Role")` ⇒ 每 bot 4 个数组、~1,480/局）
    private static string FirstString(object o, string a)
    {
        if (o == null) return null;
        return Prop(o, a)?.ToString();
    }

    private static string FirstString(object o, string a, string b)
    {
        if (o == null) return null;
        var v = Prop(o, a);
        if (v == null) return null;
        return Prop(v, b)?.ToString();
    }

    private static string FirstString(object o, string a, string b, string c)
    {
        if (o == null) return null;
        var v = Prop(o, a);
        if (v == null) return null;
        v = Prop(v, b);
        if (v == null) return null;
        return Prop(v, c)?.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ★ 官方季节活动首领条目：直接从 SPT 的 seasonalevents.json 原样复制
    // ─────────────────────────────────────────────────────────────────────────

    private static string _seasonalJsonCache;
    private static readonly HashSet<string> _summonSkipLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // ★ v1.0.1 门控日志去重
    private static bool _seasonalJsonTried;
    private static readonly Dictionary<string, string> _seasonalMapCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>定位并缓存 SPT_Data/configs/seasonalevents.json 的全文（找不到返回 null，只试一次）。</summary>
    private static string SeasonalJson()
    {
        if (_seasonalJsonTried) return _seasonalJsonCache;
        _seasonalJsonTried = true;
        try
        {
            // 从本 mod 所在目录向上找 SPT_Data/configs/seasonalevents.json
            var dir = System.IO.Path.GetDirectoryName(typeof(BossWavePatch).Assembly.Location);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                var cand = System.IO.Path.Combine(dir, "SPT_Data", "configs", "seasonalevents.json");
                if (System.IO.File.Exists(cand)) { _seasonalJsonCache = System.IO.File.ReadAllText(cand); break; }
                var parent = System.IO.Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            // 兜底：服务器工作目录
            if (_seasonalJsonCache == null)
            {
                var cand = System.IO.Path.Combine(Environment.CurrentDirectory, "SPT_Data", "configs", "seasonalevents.json");
                if (System.IO.File.Exists(cand)) _seasonalJsonCache = System.IO.File.ReadAllText(cand);
            }
            if (_seasonalJsonCache == null) _log?.Warning("[EventRotator] 找不到 SPT_Data/configs/seasonalevents.json（官方季节首领条目不可用）");
        }
        catch (Exception ex) { _log?.Warning("[EventRotator] 读 seasonalevents.json 失败: " + ex.Message); }
        return _seasonalJsonCache;
    }

    /// <summary>取 eventBossSpawns[key][mapKey] 的原始 JSON 数组文本（缓存）。</summary>
    private static string SeasonalBossArray(string key, string mapKey)
    {
        var ck = key + "|" + mapKey;
        if (_seasonalMapCache.TryGetValue(ck, out var hit)) return hit;
        string res = null;
        try
        {
            var txt = SeasonalJson();
            if (txt != null)
            {
                using var doc = JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("eventBossSpawns", out var ebs)
                 && ebs.TryGetProperty(key, out var byMap)
                 && byMap.TryGetProperty(mapKey, out var arr)
                 && arr.ValueKind == JsonValueKind.Array)
                    res = arr.GetRawText();
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] 解析 eventBossSpawns[{key}][{mapKey}] 失败: {ex.Message}"); }
        _seasonalMapCache[ck] = res;
        return res;
    }

    /// <summary>把官方 eventBossSpawns 的条目原样追加到地图首领表（含 Supports 支援单位）。</summary>
    private static void AppendSeasonalBossEntries(string mapKey, IList list, Type elemType, EventDef ev, bool addedThisRaid)
    {
        try
        {
            // ★★★ v1.0.1【BugFix】年份门控 / 实验室护栏（原先缺失 ⇒ 愚人节也刷万圣节召唤首领）
            if (SkipSeasonalBossFor(ev.ForceSeasonalBossKey, mapKey, ev.Id, out var _skipWhy))
            {
                if (!_summonSkipLogged.Contains(mapKey + "|" + ev.Id))
                {
                    _summonSkipLogged.Add(mapKey + "|" + ev.Id);
                    _log?.Info($"[EventRotator] {mapKey}: {_skipWhy}");
                }
                return;
            }

            var arrText = SeasonalBossArray(ev.ForceSeasonalBossKey, mapKey);
            if (string.IsNullOrEmpty(arrText)) return;      // ★ 官方没给这张图 → 就不到这张图刷（与官方一致）

            // ★★★ v0.1.5 修正（审计实证）：`halloweenzombies` 的官方表**每张图都有条目**
            //   （bigmap / interchange 各 28 条），但官方真正生效的图由
            //   `GetLocationsWithZombies(mapInfectionAmount > 0)` 白名单决定 ⇒
            //   **bigmap=0 / Interchange=0** 这两张官方零感染的图不该被追加僵尸。
            //   旧实现"逐图有表就加"会把僵尸塞进海关与立交桥，与官方数据直接矛盾。
            if (string.Equals(ev.ForceSeasonalBossKey, "halloweenzombies", StringComparison.OrdinalIgnoreCase)
                && InfectedMaps.Count > 0 && !InfectedMaps.Contains(mapKey))
            {
                _log?.Info($"[EventRotator] {mapKey}: 官方感染率为 0 → 跳过追加 infected 波次"
                         + "（与官方 GetLocationsWithZombies 白名单一致）");
                return;
            }

            var existing = list.Cast<object>()
                .Select(x => Prop(x, "BossName")?.ToString())
                .Where(n => n != null).ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(arrText);
            int added = 0, boosted = 0;
            foreach (var one in doc.RootElement.EnumerateArray())
            {
                var oneJson = one.GetRawText();
                var o = DeserializeEntry(elemType, oneJson);
                if (o == null) { _log?.Warning($"[EventRotator] {mapKey}: 官方条目反序列化失败: {oneJson.Substring(0, Math.Min(120, oneJson.Length))}"); continue; }
                var name = Prop(o, "BossName")?.ToString() ?? "?";
                var chanceObj = Prop(o, "BossChance");
                double chance = chanceObj is IConvertible c ? c.ToDouble(null) : 100.0;
                var zone = Prop(o, "BossZone")?.ToString();
                var escort = Prop(o, "BossEscortType")?.ToString();
                var escortAmt = Prop(o, "BossEscortAmount")?.ToString();
                int supportN = CountSupports(o);
                var modes = string.Join("/", ReadModes(o));

                if (existing.Contains(name))
                {
                    foreach (var it in list)
                    {
                        if (!string.Equals(Prop(it, "BossName")?.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                        SetOriginal(it, "BossChance", chance);
                        SetOriginal(it, "ShowOnTarkovMap", true);
                        SetOriginal(it, "ShowOnTarkovMapPvE", true);
                    }
                    boosted++;
                    _log?.Info($"[EventRotator] {mapKey}: 官方条目 '{name}' 已存在 → 概率={chance} + 骷髅标记");
                    continue;
                }
                if (!addedThisRaid) continue;              // 每局只追加一次

                SetOriginal(o, "ShowOnTarkovMap", true);   // ★ 骷髅标记（便于找到）
                SetOriginal(o, "ShowOnTarkovMapPvE", true);
                // 本活动的 bossChance 若显式指定了该角色 → 以它为准（便于测试时拉满概率）
                if (ev.BossChance != null && ev.BossChance.TryGetValue(mapKey, out var byBoss)
                    && byBoss != null && byBoss.TryGetValue(name, out var wantChance))
                {
                    chance = wantChance;
                    SetOriginal(o, "BossChance", wantChance);
                }
                list.Add(o);
                existing.Add(name);
                added++;
                _log?.Info($"[EventRotator] {mapKey}: ★追加官方条目 '{name}'"
                         + $" (chance={chance}, escort={escort}×'{escortAmt}', zone='{zone}', supports={supportN}, mode={modes})"
                         + $"  ← seasonalevents.{ev.ForceSeasonalBossKey}");
            }
            if (added > 0 || boosted > 0)
                _log?.Info($"[EventRotator] {mapKey}: 官方季节首领条目 [{ev.ForceSeasonalBossKey}] 追加 {added} 条 / 提升 {boosted} 条");
        }
        catch (Exception ex) { _log?.Warning("[EventRotator] AppendSeasonalBossEntries 异常: " + ex.Message); }
    }

    /// <summary>数一个条目有多少个 Supports 支援单位。</summary>
    private static int CountSupports(object entry)
    {
        try
        {
            if (Prop(entry, "Supports") is IEnumerable en && !(en is string))
            {
                int n = 0; foreach (var _ in en) n++;
                return n;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>读 SpawnMode 列表（用于日志确认条目形态）。</summary>
    private static List<string> ReadModes(object entry)
    {
        var res = new List<string>();
        try
        {
            if (Prop(entry, "SpawnMode") is IEnumerable en && !(en is string))
                foreach (var m in en) if (m != null) res.Add(m.ToString());
        }
        catch { }
        return res;
    }

    /// <summary>
    /// 造一个客户端能正常解析的完整首领条目：
    ///   1) 同图同名模板 → 2) 他图同名模板 → 3) 内置模板；
    /// 再覆盖 BossName / BossChance / 骷髅标记 / BossZone。
    /// <para>★★★ 缺陷1 修复（2026-10-24）：**取值顺序 = 活动覆盖优先、内置默认兜底**。
    /// 护卫类型/人数（`escortType` / `escortCount`）现在在**构造条目时**就按活动声明写入，
    /// 不再"先按内置默认建出来、append 之后再改写"（旧行为的日志实证：
    /// `构建 'bossScourge' 条目 escort='sectantWarrior' amount='0'` → 随后两条"按活动覆盖"
    /// 才改成 `followerScourge` / `4` ⇒ 第一次落地用的是错的内置默认值）。
    /// </para>
    /// <param name="ev">正在套用的活动（可为 null；为 null 时按 <see cref="EventEngine.Actives"/> 顺序找）。
    /// 只影响"活动覆盖"的取值，不影响模板/兜底逻辑。</param>
    /// </summary>
    private static object BuildEntry(Type elemType, string boss, string mapKey, EventDef ev = null)
    {
        object o = null;
        if (_dbOrig.TryGetValue(mapKey, out var byName) && byName.TryGetValue(boss, out var sameMap))
            o = Clone(sameMap);
        if (o == null && _templates.TryGetValue(boss, out var tpl)) o = Clone(tpl);

        // ★ ABPS 模板源（只读）：ABPS 的 MapSpawns 会清空地图首领表，导致上面两个缓存为空，
        //   这时从 ABPS 的 Defaults/Bosses.json 取**标准条目**（含正确的 BossZone / 护卫 / 难度）。
        //   否则 zone 为空 → bot 随机撒全图，玩家根本找不到。
        string src = o != null ? (_dbOrig.ContainsKey(mapKey) && _dbOrig[mapKey].ContainsKey(boss) ? "same-map" : "other-map") : null;
        if (o == null)
        {
            var json = AbpsCompat.ReadBossTemplateJson(boss);
            if (!string.IsNullOrEmpty(json))
            {
                var fromAbps = DeserializeEntry(elemType, json);
                if (fromAbps != null) { o = fromAbps; src = "abps-default"; }
            }
        }
        if (o == null) { o = NewBossEntry(elemType, boss); src = "builtin"; }
        if (o == null) return null;

        SetOriginal(o, "BossName", boss);
        // ★ 缺陷1 修复配套：`bossChance` 也是"活动覆盖优先、内置默认 100 兜底"。
        //   为什么必须在**构建时**就写：追加进 list 的条目**不会**再被上面那段
        //   `if (byBoss != null) { foreach (var item in list) … }` 概率提升循环扫到
        //   （那个循环跑在追加之前）⇒ 旧实现让追加条目一律吃硬编码 100%（把活动声明的 60% 也拉成 100%）。
        //   对 bossScourge 而言两者都是 100，行为不变；但"活动声明的百分比"从此在 LOCATION 表上**可直接核验**。
        var declaredChance = DeclaredBossChance(mapKey, boss, ev);
        SetOriginal(o, "BossChance", declaredChance ?? 100.0);
        if (declaredChance != null) src += "+evChance";
        if (!IsValidName(Prop(o, "BossDifficulty") as string)) SetOriginal(o, "BossDifficulty", "normal");
        if (!IsValidName(Prop(o, "BossEscortDifficulty") as string)) SetOriginal(o, "BossEscortDifficulty", "normal");
        // ★ 2026-10-30【活动声明难度】只作用于"我们追加的条目"（这里就是构造现场），活动值优先、不合法则保留默认。
        var evDiff = DeclaredDifficulty(mapKey, boss, ev, escort: false);
        if (evDiff != null)
        {
            if (IsValidDifficulty(evDiff)) { SetOriginal(o, "BossDifficulty", evDiff); src += "+evDiff"; }
            else _log?.Warning($"[EventRotator] {mapKey}: bossDifficulty '{evDiff}' 不是合法难度（easy/normal/hard/impossible）⇒ 保留默认（{boss}）");
        }
        var evEscortDiff = DeclaredDifficulty(mapKey, boss, ev, escort: true);
        if (evEscortDiff != null)
        {
            if (IsValidDifficulty(evEscortDiff)) { SetOriginal(o, "BossEscortDifficulty", evEscortDiff); src += "+evEscortDiff"; }
            else _log?.Warning($"[EventRotator] {mapKey}: escortDifficulty '{evEscortDiff}' 不是合法难度（easy/normal/hard/impossible）⇒ 保留默认（{boss}）");
        }
        if (!IsValidName(Prop(o, "BossEscortType") as string)) SetOriginal(o, "BossEscortType", FirstEscort(boss));
        if (string.IsNullOrWhiteSpace(Prop(o, "BossEscortAmount") as string)) SetOriginal(o, "BossEscortAmount", "0");
        // 该 boss 在原版不带护卫 → 把护卫数量归零（客户端会顺带跳过护卫类型解析，最稳）
        if (NoCanonicalEscorts(boss))
        {
            SetOriginal(o, "BossEscortAmount", "0");
            SetOriginal(o, "Supports", null);
        }

        // ★★★ 缺陷1 修复核心：**在条目构造完成前**就把活动声明的 escortType / escortCount 写进去，
        //   顺序 = ① 活动覆盖（events.json 的 `escortType` / `escortCount`）→ ② 内置默认兜底
        //   （`Escorts` 表 → 底模板自带的合法值 → `sectantWarrior` / `"0"`）。
        //   上面两行设置的就是"内置默认兜底"那一层；这里最后写 = 活动说了算。
        //   ⚠ 放在 `NoCanonicalEscorts` 之后：那条规则只是"原版没有护卫的 boss 别白送护卫"，
        //     活动显式声明了人数/类型时，**活动优先**（与 ApplyOneEvent 末尾后置块的既有语义一致）。
        if (TryEventEscort(mapKey, boss, ev, out var evEscortType, out var evEscortAmount))
        {
            if (!string.IsNullOrEmpty(evEscortType))
            {
                if (IsValidName(evEscortType)) { SetOriginal(o, "BossEscortType", evEscortType); src += "+evEscort"; }
                else _log?.Warning($"[EventRotator] {mapKey}: 活动声明的随从类型 '{evEscortType}' 不是合法 WildSpawnType 名（{boss}）⇒ 保留内置默认，避免客户端 Enum.Parse 崩");
            }
            if (!string.IsNullOrEmpty(evEscortAmount))
            {
                SetOriginal(o, "BossEscortAmount", evEscortAmount);
                src += "+evAmount";
            }
        }

        // ★ zone：只有"模板里本来就没有"时才留空（空 = 客户端自选刷点）。
        //   有 zone 就保留 —— 这决定了 bot 刷在哪，直接影响玩家能不能找到。
        var zone = Prop(o, "BossZone") as string;
        if (zone == null) SetOriginal(o, "BossZone", "");

        // ★ 测试期可强制指定刷点（events.json 的 forceZone）
        var forced = ForceZoneFor(mapKey, boss);
        if (!string.IsNullOrEmpty(forced)) { SetOriginal(o, "BossZone", forced); src += "+forceZone"; }

        // ★★★ v2.0.3 刷点校验（关键）：模板里的刷点必须在**目标图**里真实存在。
        //   根因：地图移植 mod（ManimalLighthouse 等）会重做刷点集合 ——
        //     本体灯塔有 Zone_OldHouse / Zone_Blockpost / Zone_TreatmentBeach 等 9 个，
        //     移植后只剩 Zone_Chalet / Zone_Hellicopter / Zone_Island / Zone_Rocks 4 个。
        //   于是"打开 allowOnOverhaulMaps 照常追加"补出来的条目会指向**不存在的刷点** → 照样刷不出来。
        //   这里做两件事：① 校验；② 不存在就回退到本图真实存在的刷点（没有则留空 = 客户端自选）。
        var zoneNow = Prop(o, "BossZone") as string;

        // ★★★ v0.1.5【用户规格】刷点来源优先级：
        //   ① **图自己的数据**里该 boss 的刷点（装了移植地图 ⇒ 移植 mod 的 base.json；没装 ⇒ 本体 base.json）；
        //   ② 跨图模板自带的刷点（若在本图合法）；
        //   ③ 本图已有刷点里出现最多的那个（兜底）。
        //   起因（实测）：立交桥是移植图，它的自带表里有 `bossTagilla → ZoneTagilla`（Kiba 一带），
        //   但**加载后的内存表里没有 Tagilla** ⇒ 校验集合里没有 ZoneTagilla ⇒ 我们回退成了 ZoneCenter，
        //   Tagilla 就不在 Kiba 旁边了。按规格改成先去**图自己的数据文件**里查。
        var ownZone = OwnZoneForBoss(mapKey, boss);
        if (!string.IsNullOrEmpty(ownZone))
        {
            if (!string.Equals(ownZone, zoneNow, StringComparison.OrdinalIgnoreCase))
            {
                SetOriginal(o, "BossZone", ownZone);
                src += "+ownZone";
                _log?.Info($"[EventRotator] {mapKey}: 刷点按【图自己的数据】采用 '{ownZone}'（boss={boss}，"
                         + (Compat.MapModOwnsMap(mapKey) ? "该图由移植 mod 提供" : "该图用本体数据") + "）");
            }
            // ★★ 关键：这个刷点**来自图自己的数据文件** ⇒ 它就是本图合法的刷点，
            //   不能再拿**内存表**的刷点集合去否掉它（实测踩过：内存表里没有 Tagilla ⇒ 刚取对的
            //   ZoneTagilla 又被校验打回 ZoneCenter）。取到就到此为止。
            zoneNow = ownZone;
            goto ZoneDecided;
        }

        if (!string.IsNullOrEmpty(zoneNow) && !ZoneValidForMap(mapKey, zoneNow))
        {
            var fb = PickZoneForMap(mapKey);
            _log?.Warning($"[EventRotator] {mapKey}: 模板刷点 '{zoneNow}' 在本图不存在（该刷点来自原图数据，本图没有同名刷点）→ 回退到 '{(string.IsNullOrEmpty(fb) ? "(留空=客户端自选)" : fb)}'");
            SetOriginal(o, "BossZone", fb ?? "");
            src += "+zoneFix";
        }
        ZoneDecided:;

        // ★★ v0.1.5（对照 SVM `CreateBasicBossWave`）：补 `Time = -1` = **不限时段刷新**。
        //   否则沿用模板值（部分模板带时段限制）⇒ 追加出来的首领可能只在特定小时出现。
        try { SetOriginal(o, "Time", -1.0); } catch { }

        SetOriginal(o, "ShowOnTarkovMap", true);        // ★ 骷髅头
        SetOriginal(o, "ShowOnTarkovMapPvE", true);
        SetOriginal(o, "SptId", null);                  // 不带 SPT 内部 id

        // ★★★ v2.6.53【P1-3 修复】把"克隆自别人的条目"里**不该继承**的字段统一重置。
        //   客户端对非空 `TriggerId` 的语义 = "必须等该 trigger 触发"（`ActivateByTriggerId`，
        //   Assembly-CSharp:158067/158425）⇒ 继承模板的 trigger = **追加出来的首领永不刷** ✗
        //   （0.16.9 全图 140 条里 TriggerId 非空 27 条、Supports 非空 18 条、DependKarma=true 4 条）
        //   ⚠ `Supports` **不在这里清**：它是"同一 boss 的合法随队"（bossKnight 的 BigPipe/BirdEye
        //     就靠它带出来）⇒ 只有 `NewBossEntry`（模板来自**别的** boss）那一路才清。
        ResetInheritedFields(o);

        _lastBuildSrc = src;
        _log?.Info($"[EventRotator] {mapKey}: 构建 '{boss}' 条目 src={src} escort='{Prop(o, "BossEscortType") as string}' amount='{Prop(o, "BossEscortAmount") as string}' zone='{Prop(o, "BossZone") as string}'");
        return o;
    }

    private static string _lastBuildSrc = "?";

    // ─────────────────── 刷点校验（v2.0.3）───────────────────

    /// <summary>
    /// 该图**真实存在**的刷点集合（小写）。来源 = 本图自己的 BossLocationSpawn 条目里用到的 BossZone。
    /// 地图移植 mod 会重做这套刷点，所以必须从**当前生效的地图数据**里读，不能写死。
    /// </summary>
    private static HashSet<string> ZonesOfMap(string mapKey)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!_dbOrig.TryGetValue(mapKey, out var byName) || byName == null) return set;
            foreach (var kv in byName)
            {
                var z = Prop(kv.Value, "BossZone") as string;
                if (string.IsNullOrWhiteSpace(z)) continue;
                foreach (var part in z.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = part.Trim();
                    if (p.Length > 0) set.Add(p);
                }
            }
        }
        catch { }
        return set;
    }

    /// <summary>刷点是否在本图存在（支持 "ZoneA,ZoneB" 这种多刷点写法：任一存在即算有效）。</summary>
    // ─────────────────── ★ 图自己的数据（本体 / 移植 mod）───────────────────
    private static readonly Dictionary<string, string> _ownZoneCache = new(StringComparer.OrdinalIgnoreCase);
    private static string _ownZoneModsDir;

    /// <summary>定位 user/mods 目录（从本 mod 目录向上找）。</summary>
    private static string ModsDirForZones()
    {
        if (_ownZoneModsDir != null) return _ownZoneModsDir;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(typeof(BossWavePatch).Assembly.Location);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                var cand = System.IO.Path.Combine(dir, "user", "mods");
                if (System.IO.Directory.Exists(cand)) { _ownZoneModsDir = cand; return cand; }
                cand = System.IO.Path.Combine(dir, "SPT_Data");
                if (System.IO.Directory.Exists(cand)) { _ownZoneModsDir = System.IO.Path.Combine(dir, "user", "mods"); return _ownZoneModsDir; }
                dir = System.IO.Path.GetDirectoryName(dir);
            }
        }
        catch { }
        _ownZoneModsDir = "";
        return _ownZoneModsDir;
    }

    /// <summary>
    /// 取 **该图自己的数据** 里指定 boss 的刷点：
    ///   移植图 ⇒ &lt;user/mods&gt;/&lt;接管该图的mod&gt;/db/locations/&lt;map&gt;/base.json
    ///   普通图 ⇒ &lt;SPT_Runtime&gt;/SPT_Data/database/locations/&lt;map&gt;/base.json
    /// 找不到返回 null。结果按 (map,boss) 缓存。
    /// </summary>
    private static string OwnZoneForBoss(string mapKey, string boss)
    {
        if (string.IsNullOrEmpty(mapKey) || string.IsNullOrEmpty(boss)) return null;
        var key = mapKey + "|" + boss;
        if (_ownZoneCache.TryGetValue(key, out var cached)) return cached.Length == 0 ? null : cached;
        string found = null;
        try
        {
            var candidates = new List<string>();
            var mods = ModsDirForZones();
            if (!string.IsNullOrEmpty(mods) && Compat.OwnerOf.TryGetValue(mapKey, out var owner) && !string.IsNullOrEmpty(owner))
            {
                candidates.Add(System.IO.Path.Combine(mods, owner, "db", "locations", mapKey, "base.json"));
                // 兼容：mod 目录名可能与 OwnerOf 记录不同 ⇒ 扫一遍同层找带该图 base.json 的 mod
                try
                {
                    foreach (var d in System.IO.Directory.GetDirectories(mods))
                    {
                        var f = System.IO.Path.Combine(d, "db", "locations", mapKey, "base.json");
                        if (System.IO.File.Exists(f) && !candidates.Contains(f)) candidates.Add(f);
                    }
                }
                catch { }
            }
            foreach (var root in new[] { PathFromModUp("SPT_Runtime"), System.IO.Path.Combine(Environment.CurrentDirectory) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(System.IO.Path.Combine(root, "SPT_Data", "database", "locations", mapKey, "base.json"));
            }
            foreach (var f in candidates)
            {
                if (!System.IO.File.Exists(f)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f));
                if (!doc.RootElement.TryGetProperty("BossLocationSpawn", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var e in arr.EnumerateArray())
                {
                    if (!e.TryGetProperty("BossName", out var bn)) continue;
                    if (!string.Equals(bn.GetString(), boss, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.TryGetProperty("BossZone", out var bz))
                    {
                        var z = bz.GetString();
                        if (!string.IsNullOrWhiteSpace(z)) { found = z; break; }
                    }
                }
                if (found != null) break;
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] {mapKey}: 读图自己的 base.json 取刷点失败: {ex.Message}"); }
        _ownZoneCache[key] = found ?? "";
        return found;
    }

    /// <summary>从本 mod 的 DLL 位置向上拼出 SPT 根（用于定位 SPT_Data）。</summary>
    private static string PathFromModUp(string child)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(typeof(BossWavePatch).Assembly.Location);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                var cand = System.IO.Path.Combine(dir, child);
                if (System.IO.Directory.Exists(cand)) return cand;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }


    /// <summary>★ ③b：取某 session（房主）的存档等级；读不到返回 0（= 放行，不误伤）。</summary>
    private static int PlayerLevelOf(string sessionId)
    {
        try
        {
            var ss = EventRotatorServer.SaveServerRef;
            if (ss == null || string.IsNullOrEmpty(sessionId)) return 0;
            var getProfile = ss.GetType().GetMethod("GetProfile", new[] { typeof(string) });
            var prof = getProfile?.Invoke(ss, new object[] { sessionId });
            if (prof == null) return 0;
            var info = Prop(prof, "Info") ?? Prop(prof, "ProfileInfo");
            var lvl = Prop(info, "Level") ?? Prop(prof, "Level");
            return lvl == null ? 0 : Convert.ToInt32(lvl);
        }
        catch { return 0; }
    }

    private static bool ZoneValidForMap(string mapKey, string zone)
    {
        try
        {
            var valid = ZonesOfMap(mapKey);
            if (valid.Count == 0) return true;          // 本图压根没有 zone 信息 → 不阻拦
            foreach (var part in zone.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (valid.Contains(part.Trim())) return true;
            return false;
        }
        catch { return true; }
    }

    /// <summary>从本图真实刷点里挑一个回退值（优先出现次数最多的那个）；没有则返回空串（= 客户端自选）。</summary>
    private static string PickZoneForMap(string mapKey)
    {
        try
        {
            var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (_dbOrig.TryGetValue(mapKey, out var byName) && byName != null)
            {
                foreach (var kv in byName)
                {
                    var z = Prop(kv.Value, "BossZone") as string;
                    if (string.IsNullOrWhiteSpace(z)) continue;
                    foreach (var part in z.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = part.Trim();
                        if (p.Length == 0) continue;
                        count[p] = count.TryGetValue(p, out var c) ? c + 1 : 1;
                    }
                }
            }
            if (count.Count == 0) return "";
            return count.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        }
        catch { return ""; }
    }

    /// <summary>
    /// ★ SPT 注入的 JsonUtil（唯一能正确处理 ListOrT&lt;T&gt; 等自定义类型的反序列化器）。
    ///   官方 seasonalevents.json 的 Supports[].BossEscortDifficult 是数组 → 必须用它。
    /// </summary>
    internal static object JsonUtil;

    /// <summary>用 SPT 的 JSON 反序列化器把 ABPS 模板 JSON 转成 BossLocationSpawn 对象。</summary>
    private static object DeserializeEntry(Type elemType, string json)
    {
        // ★★★ 首选：SPT 注入的 JsonUtil。它注册了 SPT 全部自定义转换器
        //      （含 ListOrT<T>）—— 官方季节条目里的 Supports[].BossEscortDifficult 是数组，
        //      只有它能正确解析；System.Text.Json 会报 "could not be converted to ListOrT`1[System.String]"。
        if (JsonUtil != null)
        {
            try
            {
                var m = JsonUtil.GetType().GetMethod("Deserialize", new[] { typeof(string), typeof(Type) });
                if (m != null)
                {
                    var r = m.Invoke(JsonUtil, new object[] { json, elemType });
                    if (r != null) return r;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"[EventRotator] 模板反序列化失败(JsonUtil): {(ex.InnerException ?? ex).Message}");
            }
        }

        // 兜底 1：反射找 SPT 的 JsonUtil 单例
        try
        {
            var t = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(x => x?.Name == "JsonUtil" && x.Namespace != null && x.Namespace.StartsWith("SPTarkov"));
            if (t != null)
            {
                var inst = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                        ?? AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                            .Where(x => x.IsClass && !x.IsAbstract && x.GetInterfaces().Any(i => i.Name == "IJsonUtil"))
                            .Select(x => { try { return Activator.CreateInstance(x); } catch { return null; } })
                            .FirstOrDefault(x => x != null);
                if (inst != null)
                {
                    var m = inst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(mm => mm.Name == "Deserialize" && mm.IsGenericMethod && mm.GetParameters().Length == 1);
                    if (m != null)
                    {
                        var gm = m.MakeGenericMethod(elemType);
                        var r = gm.Invoke(inst, new object[] { json });
                        if (r != null) return r;
                    }
                }
            }
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] JsonUtil 反序列化失败（改试 STJ）: {ex.Message}"); }

        // 兜底：System.Text.Json（属性名一致，足够用）
        try
        {
            var opt = new System.Text.Json.JsonSerializerOptions
            { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip };
            return System.Text.Json.JsonSerializer.Deserialize(json, elemType, opt);
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] 模板反序列化失败: {ex.Message}"); return null; }
    }

    // ═══════════════ ★★★ 缺陷1 修复：活动覆盖的取值（活动优先 / 内置默认兜底） ═══════════════
    //
    // 【旧行为】`BuildEntry` 只用内置默认值造条目（`Escorts` 表 → 底模板 → `sectantWarrior` / `"0"`），
    //   条目录进 `BossLocationSpawn` **之后**，才由 `ApplyOneEvent` 末尾的两段"按活动覆盖"后置块改写。
    //   实机日志逐字（每局都刷）：
    //     `实验室: 构建 'bossScourge' 条目 src=builtin+zoneFix escort='sectantWarrior' amount='0'`
    //     `实验室: 随从类型按活动覆盖 bossScourge: sectantWarrior → followerScourge（追加/进图落地后）`
    //     `实验室: 随从人数按官方覆盖 bossScourge: 0 → 4`
    //   ⇒ "第一次落地"用的是**错的内置默认值**，正确值要晚一步才补上（有隐式时序依赖）。
    //
    // 【新行为】在 `BuildEntry` **构造条目时**就按"活动覆盖优先、内置默认兜底"取值：
    //   · escortType   ← events.json `escortType[map][boss]`，没有才用 `Escorts` 表 / 底模板 / `sectantWarrior`
    //   · escortAmount ← events.json `escortCount[map][boss]`，没有才用模板值 / `"0"`
    //   · bossChance   ← events.json `bossChance[map][boss]`，没有才用 100（内置默认）
    //   末尾那两段后置块**保留**：它们还负责"本来就已经存在的条目"（ABPS / 本体原版给的那些），
    //   对新建条目已经同值 ⇒ 不再打出"先默认后覆盖"的两条日志。

    /// <summary>
    /// 活动的查询范围：① 正在套用的活动（<paramref name="prefer"/>）先；② 其余生效活动。
    /// 顺序 = "后者覆盖前者"（与既有"多活动并存时后写的说了算"语义一致）。
    /// </summary>
    private static List<EventDef> EventScope(EventDef prefer)
    {
        var list = new List<EventDef>();
        if (prefer != null) list.Add(prefer);
        try
        {
            foreach (var a in EventEngine.Actives)
            {
                if (a == null || ReferenceEquals(a, prefer)) continue;
                list.Add(a);
            }
        }
        catch { }
        return list;
    }

    /// <summary>按地图键取 `{ 地图: { boss: 值 } }` 的外层条目（大小写不敏感）。</summary>
    private static Dictionary<string, TV> MapEntryOf<TV>(Dictionary<string, Dictionary<string, TV>> map, string mapKey)
    {
        if (map == null || map.Count == 0 || string.IsNullOrEmpty(mapKey)) return null;
        if (map.TryGetValue(mapKey, out var exact) && exact != null) return exact;
        foreach (var kv in map)
            if (kv.Value != null && string.Equals(kv.Key, mapKey, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>按 boss 名取内层条目（大小写不敏感）。</summary>
    private static bool MapHit<TV>(Dictionary<string, TV> byBoss, string boss, out TV value)
    {
        value = default;
        if (byBoss == null || string.IsNullOrEmpty(boss)) return false;
        foreach (var kv in byBoss)
            if (string.Equals(kv.Key, boss, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        return false;
    }

    /// <summary>
    /// 取"活动声明的随从类型 / 人数"（缺陷1 的**活动覆盖**那一层）。
    /// 返回 true 表示至少声明了一项；两项各自独立，未声明的那个返回 null ⇒ 调用方保留内置默认。
    /// </summary>
    private static bool TryEventEscort(string mapKey, string boss, EventDef prefer,
                                       out string escortType, out string escortAmount)
    {
        escortType = null;
        escortAmount = null;
        try
        {
            foreach (var ev in EventScope(prefer))
            {
                var byType = MapEntryOf(ev.EscortType, mapKey);
                if (MapHit(byType, boss, out var tp) && !string.IsNullOrWhiteSpace(tp)) escortType = tp.Trim();
                var byCount = MapEntryOf(ev.EscortCount, mapKey);
                if (MapHit(byCount, boss, out var cnt) && cnt != null) escortAmount = cnt.ToAmountString();
            }
        }
        catch { }
        return !string.IsNullOrEmpty(escortType) || !string.IsNullOrEmpty(escortAmount);
    }

    /// <summary>取"活动声明的 BossChance 百分比"（活动优先、内置默认 100 兜底）；未声明返回 null。</summary>
    private static double? DeclaredBossChance(string mapKey, string boss, EventDef prefer)
    {
        double? res = null;
        try
        {
            foreach (var ev in EventScope(prefer))
            {
                var byBoss = MapEntryOf(ev.BossChance, mapKey);
                if (byBoss != null && MapHit(byBoss, boss, out var v)) res = v;
            }

        }
        catch { }
        return res;
    }

    /// <summary>
    /// 取"活动声明的追加条目难度"（`bossDifficulty` / `escortDifficulty`；活动优先，未声明返回 null）。
    /// 多活动按 <see cref="EventScope"/> 顺序，后者覆盖前者（与 <see cref="DeclaredBossChance"/> 一致）。
    /// </summary>
    private static string DeclaredDifficulty(string mapKey, string boss, EventDef prefer, bool escort)
    {
        string res = null;
        try
        {
            foreach (var ev in EventScope(prefer))
            {
                var byBoss = MapEntryOf(escort ? ev.EscortDifficulty : ev.BossDifficulty, mapKey);
                if (byBoss != null && MapHit(byBoss, boss, out var v) && !string.IsNullOrWhiteSpace(v)) res = v.Trim();
            }
        }
        catch { }
        return res;
    }

    /// <summary>客户端可解析的难度名（`EFT.BotDifficulty`）：easy / normal / hard / impossible。</summary>
    private static bool IsValidDifficulty(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        return string.Equals(v, "easy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "normal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "hard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "impossible", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>events.json 里可给某图某敌种强制指定刷点：forceZone = { "woods": { "arenaFighterEvent": "ZoneXxx" } }</summary>
    private static string ForceZoneFor(string mapKey, string boss)
    {
        try
        {
            // v1.97 多活动：按顺序在**所有**生效活动里找，第一个命中的说了算
            foreach (var ev in EventEngine.Actives)
            {
                var m = ev.ForceZone;
                if (m == null || !m.TryGetValue(mapKey, out var byBoss) || byBoss == null) continue;
                if (byBoss.TryGetValue(boss, out var z) && !string.IsNullOrEmpty(z)) return z;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>原版就没有护卫的首领（Escorts 表里为空数组）。</summary>
    private static bool NoCanonicalEscorts(string boss) =>
        Escorts.TryGetValue(boss, out var e) && e.Length == 0;

    /// <summary>
    /// 挑一个「字段一定合法、体量小」的原版条目作克隆底（不指定具体 boss）。
    /// 优先单 bot 无护卫型（如 sectantPriest），避免把一堆护卫一起带给新条目。
    /// </summary>
    private static object PickBaseTemplate()
    {
        foreach (var key in new[] { "sectantPriest", "bossBully", "bossSanitar", "bossGluhar" })
            if (_templates.TryGetValue(key, out var t) && t != null) return t;
        foreach (var kv in _templates)
        {
            var esc = Prop(kv.Value, "BossEscortType") as string;
            var amt = Prop(kv.Value, "BossEscortAmount") as string;
            if (IsValidName(esc) && (amt == "0" || string.IsNullOrEmpty(amt))) return kv.Value;
        }
        // ★★★ v2.6.52【🚨 修「工厂白天刷出邪教战士」2026-10-16 · 用户实机发现】**原第③行已删除** ✗
        //   用户原话：「**但是我进的白天工厂**」+ 截图：工厂白天出现 4 个 `Sektant [Follower (sectantWarrior)]` ✗
        //   服务端日志逐字（真凶）：
        //     `factory4_day: 构建 'followerBigPipe' 条目 src=builtin+zoneFix escort='sectantWarrior' amount='2,2,3,3,4' zone='BotZone'` ✗✗
        //   【根因】原第③行 `foreach (var kv in _templates) return kv.Value;`（**"随便一个原版条目"**）
        //     会把那个模板**自己的 `BossEscortType`/`BossEscortAmount`** 一起 `Clone` 过来
        //     ⇒ 若它恰好是"邪教牧师"那类条目（`escort=sectantWarrior` + `amount=2,2,3,3,4`）
        //     ⇒ **我们新建的 boss 条目就白送 2~4 个邪教战士** ✗✗（`followerBigPipe`/`followerBirdEye` 中招）
        //   【修法】**删掉第③行** ⇒ 找不到"escort 为空/0 的干净模板"时 `return null`
        //     ⇒ `NewBossEntry` 走**裸造路径** —— 它设 `BossEscortType="sectantWarrior"`（**合法枚举名，防客户端 Parse 崩**）
        //       + `BossEscortAmount="0"`（**不生成护卫**）⇒ **没有任何多余随从** ✔ 且字段全部合法 ✔
        //   ✅ 保留的第①②行：①按 key 精确命中；②只在"escort 合法且数量为 0/空"的模板里挑 ⇒ 都干净 ✔
        return null;
    }

    private static string FirstEscort(string boss) =>
        Escorts.TryGetValue(boss, out var e) && e.Length > 0 ? e[0] : "";

    /// <summary>
    /// ★★★ v2.6.53【P1-3 修复】把"克隆继承"的危险字段重置成安全值。
    /// <para>背景：条目是从**别的**条目 `Clone` 来的（`BuildEntry` 的同图/他图/ABPS 模板，
    /// 以及 `NewBossEntry` 的底模板）⇒ 那些字段会**一起复制**过来。真正危险的是：</para>
    /// <list type="bullet">
    /// <item>`TriggerId`/`TriggerName` 非空 ⇒ 客户端 `ParseTriggerName()` 让 `IsStartWave()==false`
    ///   （Assembly-CSharp:136092），此后只有 `ActivateByTriggerId(wave, id)` 命中才会激活
    ///   （`:158067/:158425`）⇒ **追加出来的首领永远不会刷**（且日志上看不出异常）。</item>
    /// <item>`DependKarma(true)` ⇒ 该条目要按玩家声望重算（客户端 `:136325`）⇒ 声望不好就不刷。</item>
    /// <item>`IgnoreMaxBots=false` ⇒ 该首领受 bot 上限约束（客户端 `:155575`）⇒ 图满时不刷。</item>
    /// <item>`Delay`/`ForceSpawn`/`SpawnMode` 继承 ⇒ 出现"延迟刷/强制刷/只在 pve 刷"等非预期行为。</item>
    /// </list>
    /// <para>⚠ `Supports` **刻意不在这里清**：它是"同一 boss 的合法随队"（`bossKnight` 的
    /// BigPipe/BirdEye 就靠它一起带出来）—— 只有 `NewBossEntry`（底模板来自**别的** boss）才清。</para>
    /// </summary>
    private static void ResetInheritedFields(object o, string why = null)
    {
        if (o == null) return;
        try
        {
            SetOriginal(o, "TriggerId", "");
            SetOriginal(o, "TriggerName", "");
            SetOriginal(o, "DependKarma", false);
            SetOriginal(o, "DependKarmaPVE", false);
            SetOriginal(o, "ForceSpawn", false);
            SetOriginal(o, "IgnoreMaxBots", true);
            SetOriginal(o, "Delay", 0.0);
            SetList(o, "SpawnMode", new List<string> { "regular", "pve" });
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] ResetInheritedFields 失败（已跳过）{why}: {ex.Message}"); }
    }

    private static object NewBossEntry(Type t, string boss)
    {
        try
        {
            // ★ 关键：先克隆一个「已知能被客户端解析」的原版条目作底。
            //   裸造条目 + BossEscortType="" 会让客户端 Enum.Parse(WildSpawnType, "") 抛异常
            //   （ParseExcortType 不做空值检查），必须保证护卫类型是合法枚举名。
            var baseTpl = PickBaseTemplate();
            if (baseTpl != null)
            {
                var c = Clone(baseTpl);
                if (c != null)
                {
                    // ★★★ v2.6.52【🚨 真凶在此 · 2026-10-16 · 用户实机发现「白天工厂刷邪教战士」】
                    //   用户原话：「**但是我进的白天工厂**」+ 截图：工厂白天 4 个 `Sektant [Follower (sectantWarrior)]` ✗
                    //   日志逐字：`factory4_day: 构建 'followerBigPipe' 条目 src=builtin+zoneFix
                    //              escort='sectantWarrior' amount='2,2,3,3,4'` ✗✗
                    //   【根因】`PickBaseTemplate` ①"按 key 精确命中"返回的模板（`_templates["followerBigPipe"]`）
                    //     **自带 `BossEscortType=sectantWarrior` + `BossEscortAmount=2,2,3,3,4`**
                    //     ⇒ `Clone` 把这两列**一起复制** ⇒ 我们新建的条目**白送 2~4 个邪教战士** ✗✗
                    //   【修法】克隆后**强制清 escort**：类型留一个合法枚举名（防客户端 `Enum.Parse("")` 崩），
                    //     数量设 `"0"`（**不生成护卫**）✔
                    //   ✅ **不影响 `bossKnight`**：它走 `src=other-map`（别的图原生条目 ⇒ **不经 `NewBossEntry`**），
                    //      官方 `escort='exUsec' amount='2'` 照旧 ✔（日志已证：`构建 'bossKnight' … src=other-map+zoneFix escort='exUsec' amount='2'` ✔）
                    //   ⚠ 若某 boss **确实需要随从**，应由 `Escorts` 表 / 调用方显式设置 ⇒ **不走这条裸建路径** ✔
                    // ★★★ v2.6.53【P2-#6 修复】护卫**类型**不再硬编码 `sectantWarrior`：
                    //   优先级 = ①本 boss 的正牌随从（`Escorts` 表，已逐条核对 0.16.9）
                    //           ②底模板自己的类型（合法枚举名）
                    //           ③`sectantWarrior` 兜底（**合法枚举名**，防客户端 Enum.Parse("") 崩）
                    //   原实现只有③ ⇒ `peacemaker` 这类"正牌护卫不是邪教战士"的 boss 会丢护卫
                    //   （证据：0.16.9 `shoreline` 的 `peacemaker` escort=peacemaker amount=2,2,3,3,3,4,4,5）。
                    var escType = FirstEscort(boss);
                    if (!IsValidName(escType)) escType = Prop(c, "BossEscortType") as string;
                    if (!IsValidName(escType)) escType = "sectantWarrior";
                    SetOriginal(c, "BossEscortType", escType);
                    SetOriginal(c, "BossEscortAmount", "0");
                    // ★ v2.6.53【P1-3】底模板是**别的 boss** 的条目 ⇒ 它的 `Supports`（额外小队，
                    //   如 Gluhar 的 6 名随从 / bossKnight 的 BigPipe+BirdEye）**必须清掉**，
                    //   否则新建条目会白送一整队（与本次已修的 escort 泄漏同类）。
                    SetOriginal(c, "Supports", null);
                    SetOriginal(c, "IsBossPlayer", false);
                    SetOriginal(c, "IsRandomTimeSpawn", false);
                    ResetInheritedFields(c, "NewBossEntry/clone");
                    return c;
                }
            }

            var o = Activator.CreateInstance(t);
            SetOriginal(o, "BossDifficulty", "normal");
            SetOriginal(o, "BossEscortDifficulty", "normal");
            // ★ v2.6.53【P2-#6】裸造路径同样优先用正牌随从类型，`sectantWarrior` 只兜底
            var escBare = FirstEscort(boss);
            if (!IsValidName(escBare)) escBare = "sectantWarrior";
            SetOriginal(o, "BossEscortType", escBare);            // 合法 WildSpawnType，绝不留空
            SetOriginal(o, "BossEscortAmount", "0");              // 数量 0 → 客户端不会真生成护卫
            SetOriginal(o, "IsBossPlayer", false);
            SetOriginal(o, "IsRandomTimeSpawn", false);
            SetOriginal(o, "IgnoreMaxBots", true);
            SetOriginal(o, "ForceSpawn", false);
            SetOriginal(o, "Time", -1.0);
            SetOriginal(o, "Delay", 0.0);
            SetOriginal(o, "TriggerId", "");
            SetOriginal(o, "TriggerName", "");
            SetOriginal(o, "DependKarma", false);
            SetOriginal(o, "DependKarmaPVE", false);
            SetOriginal(o, "Supports", null);
            SetOriginal(o, "BossZone", "");
            SetList(o, "SpawnMode", new List<string> { "regular", "pve" });
            return o;
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] NewBossEntry failed: {ex.Message}"); return null; }
    }

    /// <summary>深拷贝：服务端是 record（有带参拷贝构造），退路用 JSON 往返。</summary>
    private static object Clone(object o)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            var copyCtor = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(c =>
                            {
                                var ps = c.GetParameters();
                                return ps.Length == 1 && ps[0].ParameterType == t;
                            });
            if (copyCtor != null) return copyCtor.Invoke(new[] { o });
            var json = JsonSerializer.Serialize(o);
            return JsonSerializer.Deserialize(json, t);
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] clone failed: {ex.Message}"); return null; }
    }

    /// <summary>
    /// 写属性 —— 必须用服务端 CLR 属性名，不是 JSON 名：
    ///   BossDifficulty / BossEscortDifficulty
    /// 1.6.3 写的是 "BossDifficult"（JSON 名），反射静默失败 → 客户端 Enum.Parse(null) 崩溃。
    /// </summary>
    private static void SetOriginal(object o, string prop, object val)
    {
        try
        {
            if (o == null) return;
            var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return;
            var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;

            if (val == null)
            {
                if (!pt.IsValueType || Nullable.GetUnderlyingType(p.PropertyType) != null) p.SetValue(o, null);
                return;
            }
            if (pt.IsInstanceOfType(val)) { p.SetValue(o, val); return; }
            if (pt == typeof(double)) { p.SetValue(o, Convert.ToDouble(val)); return; }
            if (pt == typeof(int)) { p.SetValue(o, Convert.ToInt32(val)); return; }
            if (pt == typeof(bool)) { p.SetValue(o, Convert.ToBoolean(val)); return; }
            if (pt == typeof(string)) { p.SetValue(o, val.ToString()); return; }
            if (pt == typeof(float)) { p.SetValue(o, Convert.ToSingle(val)); return; }
            if (pt.IsEnum) { p.SetValue(o, Enum.Parse(pt, val.ToString(), true)); return; }
        }
        catch { }
    }

    /// <summary>
    /// ★ v0.1.4 读"红骷髅"标记并压成 double 以便快照（true=1, false=0, null=-1）。
    /// 用 double 是为了跟既有 <c>_dbSnap</c> 的风格一致、少一套容器。
    /// </summary>
    private static double MarkOf(object o, string prop)
    {
        try
        {
            var v = Prop(o, prop);
            if (v == null) return -1;
            return Convert.ToBoolean(v) ? 1 : 0;
        }
        catch { return -1; }
    }

    /// <summary>把 MarkOf 的编码还原成 bool / null。</summary>
    private static object MarkToObj(double v) => v < 0 ? null : (object)(v > 0.5);

    /// <summary>
    /// ★★★ v0.1.4 随机地图首领概率：从战局地图里随机抽 N 张，每张"全部首领"设成 60%。
    /// <b>同一个档位内结果稳定</b>（用档位号做种子），换档/重启才重新抽 —— 否则每次进图
    /// 都换一批图，玩家会以为出了 bug。
    /// </summary>
    private static Dictionary<string, Dictionary<string, int>> RandomBossMapChance(EventDef ev)
    {
        try
        {
            var all = RaidMaps.ToList();
            all.Sort(StringComparer.Ordinal);
            int count = Math.Clamp(RandomBossMapCount, 1, all.Count);
            // 用"活动 id + 档位/日期"做种子：同一天/同档位结果一致，换档位才变
            int seed = (ev.Id ?? "").GetHashCode()
                       ^ (Rotation.SlotRebaseEnabled ? Rotation.CurrentSlot() * 7919 : DateTime.Now.DayOfYear * 7919);
            var rnd = new Random(seed);
            var picked = all.OrderBy(_ => rnd.Next()).Take(count).ToList();

            var res = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in picked)
                // ★★★ v0.1.4 用户要求：随机挑到的那张图**一定刷**（100%），不是"提高概率"。
                //   所以内层用 "*"（该图全部首领）→ 100%。想调低请改 RandomBossMapChancePct。
                res[m] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["*"] = RandomBossMapChancePct };
            return res;
        }
        catch { return ev.BossChance; }
    }

    /// <summary>★ v0.1.4 随机图的首领概率（默认 100 = 一定刷；config: randomBossMapChance）。</summary>
    public static int RandomBossMapChancePct = 100;

    /// <summary>随机抽几张图（config: randomBossMapCount）。</summary>
    public static int RandomBossMapCount = 3;

    /// <summary>IEnumerable&lt;string&gt; 之类的集合属性。</summary>
    private static void SetList(object o, string prop, List<string> val)
    {
        try
        {
            if (o == null) return;
            var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return;
            if (p.PropertyType.IsAssignableFrom(val.GetType())) p.SetValue(o, val);
        }
        catch { }
    }
}
