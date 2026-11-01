using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace EventRotator;

/// <summary>
/// ★★★ 2026-10-30【万圣节感染者（僵尸）】维度实现 —— 把 SPT 原生 seasonal 僵尸开关打开并按需提升感染率。
///
/// <para><b>为什么能做</b>：当年放弃这条线是因为「僵尸不挥刀」；客户端 <c>SainZombieMeleeFix.cs</c>
/// （PPES 1.4.0 同款 <c>__runOriginal</c> 闸门）已在实机启用并部署 ⇒ 现在可以重新打开原生感染。</para>
///
/// <para><b>数据落点（本机实测）</b>：<c>SPT_Data/configs/seasonalevents.json</c> 的
/// <c>events[]</c> 里 <c>name = "halloween"</c> 的 <c>settings.zombieSettings</c>：
/// <c>{ enabled, mapInfectionAmount{10 张图}, disableBosses, disableWaves }</c>；
/// 以及顶层 <c>eventBossSpawns</c>（本机键名是 <b>halloweenzombies</b>，PPES 源码里写的是 "zombies" ⇒ 两个都认）。</para>
///
/// <para><b>语义照抄 PPES 1.4.0 <c>Services/SeasonalApplier.cs:203-221 ApplyOutbreak2026</c></b>：
/// ① 全图感染：<c>mapInfectionAmount</c> 里 &lt;=0 的图提到 40（本维度按 <c>zombieOutbreak.everyMapMin</c>，默认 40）；
/// ② <c>infectedTagilla</c>：<c>BossChance &lt;= 0</c> 的条目改成 <c>infectedTagillaChance</c>（默认 12）；
/// ③ 杀 Scav 不扣业力：5 类 scav 角色的 <c>BotExperience.StandingForKill</c> 负值清零（正值不动）。</para>
///
/// <para><b>快照 / 还原</b>：改动前记录 <c>zombieSettings.enabled</c> 原值、每个被提升的图的原感染率、
/// 每个 infectedTagilla 条目的原 <c>BossChance</c>、每个 role×档位的原 <c>StandingForKill</c>；
/// 登记到 <c>AddRestore</c>（Plugin 的 <c>_restoreActions</c>，LIFO 回放）⇒ 活动关闭/切走时逐字还原。</para>
///
/// <para><b>服务解析</b>：<c>SeasonalEventConfig</c> 从 DI 取；类型必须用 <see cref="RuntimeTypes.Find"/>
/// （prepatch 双载：DI 注册的是运行时那份，用死副本的 Type 调 GetService 会得 null —— 与 FenceRumor 同型）。</para>
/// </summary>
internal static class SeasonalZombieDimension
{
    private const string Tag = "[感染者]";
    private const string ConfigTypeName = "SPTarkov.Server.Core.Models.Spt.Config.SeasonalEventConfig";

    /// <summary>
    /// 会被"感染率越高普通 AI 越少"缩放的普通 AI 角色（大小写不敏感）。
    /// **不含** infected*（感染者）、bossScourge/followerScourge（自建首领）、活动 bossSpawns 声明的首领、
    /// 以及 bossTagilla 这类原生首领 —— 只缩"普通刷怪"。
    /// </summary>
    private static readonly HashSet<string> NormalAiRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pmcBEAR", "pmcUSEC", "pmcBot", "assault", "marksman", "cursedAssault",
        "crazyAssaultEvent", "exUsec", "arenaFighterEvent",
    };

    /// <summary>5 类 scav 角色（PPES 列表，逐字）：杀它们默认扣业力，这里把负值清零。</summary>
    private static readonly string[] ScavRoles =
    { "assault", "marksman", "cursedassault", "assaultgroup", "crazyassaultevent" };

    public static IServiceProvider Services;
    public static object BotTable;
    /// <summary>
    /// ⚠ 保留只为**不改 Plugin 接线**：按 PPES 最小实现，本维度**不再使用**这两个表
    /// （globals.LocationInfection / location.*.InfectionPercentage 一律不写，交给 SPT 的 ConfigureZombies）。
    /// </summary>
    public static object LocationTable;
    public static object GlobalTable;
    public static Action<string> Info;
    public static Action<string> Warn;

    private const string HarmonyIdAbps = "com.kirisame.eventrotator.zombieabps";
    private static bool _abpsHooked;
    private static bool _abpsMissing;   // 查过一次找不到 ABPS ⇒ 之后不再反射扫描（幂等/省开销）
    private const string HarmonyIdGlobals = "com.kirisame.eventrotator.zombieglobals";
    private static bool _globalsHooked;
    /// <summary>仅供离线探针验证"没装 ABPS ⇒ 静默跳过"（生产保持 null）。</summary>
    internal static string AbpsTypeNameForTest;

    /// <summary>仅供离线探针固定随机区间（生产 null）。</summary>
    internal static Func<double, double> OfficialRollForTest;

    private static object _randomCache;
    private static readonly object _gate = new();
    private static Snapshot _state;
    private static bool _warnedCfg, _warnedZombie;

    private sealed class Snapshot
    {
        public string Signature = "";
        public object ZombieSettings;
        public object EnabledProp;                  // PropertyInfo
        public object OrigEnabled;                  // bool?（boxed）
        public readonly List<(string Map, double Orig)> Infection = new();
        public readonly List<(object Spawn, object ChanceProp, object Orig)> Tagilla = new();
        /// <summary>布尔开关快照（当前只有 EnableSeasonalEventDetection）。</summary>
        public readonly List<(object Obj, PropertyInfo Prop, object Orig)> Bools = new();

        /// <summary>写进地图的感染率快照：globals.LocationInfection（`Dictionary&lt;string,int&gt;`）。
        /// Existed=false 表示该键原本不存在（还原时要删掉，别留垃圾键）。</summary>
        public readonly List<(IDictionary Li, object Key, object Orig, bool Existed)> GlobalInfection = new();
        /// <summary>写进地图的感染率快照：`location.Base.Events.Halloween2024.InfectionPercentage`（`double?`）。</summary>
        public readonly List<(object HwRoot, PropertyInfo Prop, object Orig)> LocInfection = new();
        /// <summary>已写过/已打过日志的图（同一轮只打一条）。</summary>
        public readonly HashSet<string> PushedMaps = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>本轮额外补写的图 id 键（用于汇总日志）。</summary>
        public readonly HashSet<string> ExtraKeys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>`eventBossSpawns["halloweenzombies"]` 的 byMap 引用（ABPS 重建后要用它补回）。</summary>
        public IDictionary WaveByMap;
        /// <summary>我们追加的僵尸波次（按对象引用记录；还原时只移除这些，本体条目不碰）。</summary>
        public readonly Dictionary<string, List<(IList List, object Item)>> AddedWaves =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>普通 AI 波次数量快照：map → [(Wave, 原 Number)]。</summary>
        public readonly Dictionary<string, List<(IList List, object Wave, int Orig)>> ScaledWaves =
            new(StringComparer.OrdinalIgnoreCase);
        /// <summary>BossLocationSpawn 里普通 AI 条目的 BossChance 快照：map → [(条目, 属性, 原值)]。</summary>
        public readonly Dictionary<string, List<(IList List, object Entry, PropertyInfo Prop, object Orig)>> ScaledBossChance =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(IDictionary Dict, string Key, double Orig)> Karma = new();
        public int RoleCount;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  入口
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>主套用循环逐活动调用（`NativeZombies != true` ⇒ 一个字段都不碰）。</summary>
    public static void Apply(EventDef ev, Action<Action> addRestore)
    {
        try
        {
            if (ev == null || ev.NativeZombies != true) return;

            var sig = Signature(ev);
            lock (_gate)
            {
                if (_state != null && string.Equals(_state.Signature, sig, StringComparison.Ordinal)) return;   // 幂等
            }

            var cfg = ResolveConfig();
            if (cfg == null)
            {
                Once(ref _warnedCfg, $"{Tag} ⚠ 拿不到 SeasonalEventConfig（DI/类型解析失败）⇒ 本次不开启感染（不影响其它功能）");
                return;
            }

            var seasonal = FindSeasonalEvent(cfg, ev.ZombieSeasonalKey ?? "halloween");
            if (seasonal == null)
            {
                Warn?.Invoke($"{Tag} ⚠ seasonalevents 里找不到事件「{ev.ZombieSeasonalKey ?? "halloween"}」⇒ 跳过");
                return;
            }

            var settings = Prop(seasonal, "Settings");
            var zombie = Prop(settings, "ZombieSettings");
            if (zombie == null)
            {
                Once(ref _warnedZombie, $"{Tag} ⚠ 该季节条目的 settings.zombieSettings 为空 ⇒ 跳过");
                return;
            }

            var snap = new Snapshot { Signature = sig, ZombieSettings = zombie };

            // ① 僵尸咬人的增益 buff（Vitality/HealthRate/EnergyRate）—— 仅僵尸活动期间移除，保留 ZombieInfection 标记
            ZombieBuffFix.Info = Info;
            ZombieBuffFix.Warn = Warn;
            ZombieBuffFix.Apply(GlobalTable, addRestore);

            // ② enabled → true
            var enProp = zombie.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            var origEnabled = enProp?.GetValue(zombie);
            if (enProp != null)
            {
                snap.EnabledProp = enProp;
                snap.OrigEnabled = origEnabled;
                bool isOn = origEnabled is bool b && b;
                if (!isOn)
                {
                    try
                    {
                        enProp.SetValue(zombie, (bool?)true);
                        Info?.Invoke($"{Tag} 已开启原生僵尸（zombieSettings.enabled {FmtBool(origEnabled)} → true）");
                    }
                    catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 enabled 失败：{ex.Message}"); }
                }
                else Info?.Invoke($"{Tag} 原生僵尸已是开启状态（enabled=true，不重复写）");
            }

            // ② 感染率下限（只有活动**显式声明** `zombieOutbreak` 才动；
            //    不声明 = 保持官方分布不变，避免"光是开了 nativeZombies 就把海关/立交桥提到 40%"的意外）
            if (ev.ZombieOutbreak != null) ApplyInfection(zombie, ev, snap);

            // ②-b 僵尸波次：把 seasonalevents 的 halloweenzombies 条目克隆追加到 location.BossLocationSpawn
            //     （强开活动时 SPT 不会跑 AddEventBossesToMaps ⇒ 只有感染率是刷不出 bot 的）
            ApplyZombieWaves(cfg, seasonal, zombie, snap);

            Info?.Invoke($"{Tag} 作用域=僵尸活动(本次生效)｜活动关闭后将逐字还原 Waves/BossLocationSpawn");

            // ②-c 感染率越高 → 普通 AI 越少：Waves 的 Wave.Number + BossLocationSpawn 普通 AI 条目的 BossChance
            bool zombiesOn = Prop(zombie, "Enabled") is bool zon && zon;
            if (!zombiesOn) { Info?.Invoke($"{Tag} zombieSettings.Enabled 不是 true ⇒ 跳过普通 AI 缩放"); }
            else
            {
                ApplyWaveScaling(zombie, snap);
                ApplyBossChanceScaling(ev, zombie, snap);
            }

            // ③ infectedTagilla 概率
            ApplyTagilla(cfg, seasonal, ev, snap);

            // ④ 季节事件检测开关（PPES 1.4.0 同款；其余 globals/location 字段一律不碰）
            ApplyDetectionSwitch(cfg, snap);

            // ⑤ 杀 scav 不扣业力
            if (ev.NoKarmaLoss == true) ApplyKarma(snap);

            lock (_gate) _state = snap;

            // ★ ABPS（BotPlacementSystem）兼容：它重建刷怪表后会冲掉我们写进地图的感染率
            EnsureAbpsHook();
            // ★ 客户端每次请求 /client/globals 时即时修正（防"写晚了/切活动后没重进图导致空数据"）
            EnsureGlobalsFreshHook();

            addRestore?.Invoke(() => Restore());

            if (snap.Infection.Count == 0 && snap.Tagilla.Count == 0 && snap.Karma.Count == 0 && snap.Bools.Count == 0
                && snap.OrigEnabled is bool ob && ob)
                Info?.Invoke($"{Tag} 本次无实际改动（enabled 已为 true、无感染率/概率/业力需要调整）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 套用异常（已忽略，不影响其它功能）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyInfection(object zombie, EventDef ev, Snapshot snap)
    {
        var inf = Prop(zombie, "MapInfectionAmount") as IDictionary;
        if (inf == null) return;
        var outbreak = ev.ZombieOutbreak;
        double floorAll = outbreak?.EveryMapMin ?? 40;          // 契约：everyMapMin 缺省 40（PPES 1.4.0 ApplyOutbreak2026）
        var extra = outbreak?.Extra ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var k in inf.Keys.Cast<object>().ToList())
        {
            var map = k?.ToString() ?? "";
            double cur;
            try { cur = Convert.ToDouble(inf[k], CultureInfo.InvariantCulture); } catch { continue; }
            // ★ 2026-10-30【用户裁决】extra 里的值是**最终值**（不是"下限提升"）：
            //   `extra: {factory4:100, laboratory:100}` ⇒ 这两张图直接 100（哪怕原值更低/更高）。
            //   其它图仍按 `everyMapMin` 作为**下限**提上来。
            double target = double.NaN;
            if (extra != null)
            {
                var hit = extra.FirstOrDefault(kv => string.Equals(kv.Key, map, StringComparison.OrdinalIgnoreCase));
                if (hit.Key != null && hit.Value >= 0) target = hit.Value;      // 最终值
            }
            if (double.IsNaN(target)) target = cur >= floorAll ? double.NaN : floorAll;   // 下限提升
            if (double.IsNaN(target)) continue;

            // ★ 官方公式（IL 实证）：num == 100 ⇒ 100；否则掷 [num, 100] 的整数
            double value = OfficialRoll(target);
            if (Math.Abs(cur - value) < 1e-9) continue;
            snap.Infection.Add((map, cur));
            try
            {
                inf[k] = value;
                if (Math.Abs(target - 100) < 1e-9)
                    Info?.Invoke($"{Tag} {map}：感染率 {Fmt(cur)}→100（100 直取）");
                else
                    Info?.Invoke($"{Tag} {map}：感染率 {Fmt(cur)}→随机 {Fmt(value)}（官方区间 {Fmt(target)}-100）");
            }
            catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写感染率 {map} 失败：{ex.Message}"); }
        }

        // ★ 2026-10-30【实机证据驱动的回补】强开的活动 SPT **不会**自己跑 ConfigureZombies
        //   ⇒ `zombieSettings.mapInfectionAmount` 改了也不会进 `globals.LocationInfection` /
        //   `location.*.InfectionPercentage`（地图界面感染率仍 0、进图没僵尸）。
        //   所以这里把**所有图**的最终值都写进去（实验室 100 / 森林 60 这些写一遍无害）。
        foreach (var k in inf.Keys.Cast<object>().ToList())
        {
            double v;
            try { v = Convert.ToDouble(inf[k], CultureInfo.InvariantCulture); } catch { continue; }
            PushLocationInfection(snap, k?.ToString() ?? "", v);
        }

        List<string> extraKeys;
        lock (_gate) extraKeys = snap.ExtraKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (extraKeys.Count > 0)
            Info?.Invoke($"{Tag} 额外补写图 id 键 {extraKeys.Count} 个：{string.Join("/", extraKeys)}"
                       + "（客户端选图界面按 location.Id 取值，官方键保持不变）");
    }

    /// <summary>
    /// 把感染率写进两处**真实字段**（类型按实测，不猜）。
    ///
    /// <para><b>★ 官方 `SeasonalEventService.ConfigureZombies` 反编译清单（Mono.Cecil，4.1.6.0）</b>：
    /// <list type="bullet">
    ///   <item>`botTable.Core.ActiveHalloweenZombiesEvent = true`（Nullable&lt;bool&gt;）；</item>
    ///   <item>`globalTable.Configuration.SeasonActivity.InfectionHalloween.DisplayUIEnabled = true`；</item>
    ///   <item>同一对象 `.Enabled = true`；</item>
    ///   <item>`var value = num == 100 ? num : (double)randomUtil.GetInt((int)num, 100, exclusive:false)`（num = mapInfectionAmount[map]）；</item>
    ///   <item>`location.Base.Events.Halloween2024.InfectionPercentage = (double?)value`；</item>
    ///   <item>`globalTable.LocationInfection[map] = (int)value`；</item>
    ///   <item>再按 `DisableBosses` 清空对应 `Base.BossLocationSpawn`、按 `DisableWaves` 清空 `Base.Waves`；</item>
    ///   <item>最后 `AddEventBossesToMaps("halloweenzombies", GetLocationsWithZombies(mapInfectionAmount))`。</item>
    /// </list>
    /// **全库扫描结论**：`Halloween2024` 的 setter 只有 `set_InfectionPercentage` 一个被调用；
    /// `MinInfectionPercentage` / `ZombieMultiplier` / `CrowdsLimit` / `CrowdAttackBlockRadius` /
    /// `MaxCrowdAttackSpawnLimit` **在 SPT Core 里没有任何 setter**（它们是 base.json 静态数据，不随感染率变）。
    /// 其余步骤（开关/清表/追加波次）由 Plugin 的 `ReplicateConfigureZombies` 与本维度的 `ApplyZombieWaves` 分担。</para>
    ///
    /// <para><b>与官方的唯一差异</b>：官方对 num≠100 的图会掷 `[num,100]` 随机数；我们按活动数据契约写**确定值**
    /// （`zombieOutbreak.extra` 是最终值）⇒ 地图界面显示的是数据里写的数，不会每轮重摇。</para>
    /// </summary>
    /// <list type="bullet">
    ///   <item><c>GlobalTable.LocationInfection</c>：实测 `Dictionary&lt;string, int&gt;` ⇒ 写 `(int)Math.Round(v)`；</item>
    ///   <item><c>location.Base.Events.Halloween2024.InfectionPercentage</c>：实测 `double?` ⇒ 写 double。</item>
    /// </list>
    /// 写前快照、活动关闭/切走时逐字还原；每图一条成功日志。
    /// </summary>
    private static void PushLocationInfection(Snapshot snap, string mapKey, double value)
    {
        if (string.IsNullOrEmpty(mapKey)) return;

        try
        {
            var li = Prop(GlobalTable, "LocationInfection") as IDictionary;
            if (li != null)
            {
                int iv = (int)Math.Round(value);

                // ① 官方语义键（mapInfectionAmount 原样，SPT 自己读这份）
                var key = FindKey(li, mapKey);
                if (key != null)
                {
                    lock (_gate) snap.GlobalInfection.Add((li, key, li[key], true));
                    li[key] = iv;
                }

                // ② ★ 客户端选图界面用 `location.Id` 取值（IL 实证：ProcessLocation 的 TryGetValue key
                //   = location.Id，仅 factory4_day/night → "factory4"、Sandbox/Sandbox_high → "Sandbox" 做归并）
                //   ⇒ 官方键是 Interchange/Woods/Shoreline/RezervBase/TarkovStreets/Lighthouse/factory4 这些，
                //   和 location.Id（interchange/woods/.../factory4_day）对不上 ⇒ 同时补写一套展开后的图 id 键。
                foreach (var ext in ExpandInfectedKey(mapKey))
                {
                    if (string.Equals(ext, mapKey, StringComparison.Ordinal)) continue;   // 与小写化的官方键完全相同就不重复写
                    bool existed = li.Contains(ext);
                    if (existed && Convert.ToInt32(li[ext], CultureInfo.InvariantCulture) == iv) continue;
                    lock (_gate) snap.GlobalInfection.Add((li, ext, existed ? li[ext] : null, existed));
                    li[ext] = iv;
                    lock (_gate) snap.ExtraKeys.Add(ext);
                }
            }
        }
        catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 globals.LocationInfection[{mapKey}] 失败：{ex.Message}"); }

        try
        {
            foreach (var locId in ExpandInfectedKey(mapKey))
            {
                object loc = null;
                try
                {
                    loc = LocationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })
                                 ?.Invoke(LocationTable, new object[] { locId });
                }
                catch { }
                var hwRoot = Prop(Prop(Prop(loc, "Base"), "Events"), "Halloween2024");
                if (hwRoot == null) continue;
                var pct = hwRoot.GetType().GetProperty("InfectionPercentage",
                    BindingFlags.Public | BindingFlags.Instance);
                if (pct == null) continue;
                var cur = pct.GetValue(hwRoot);
                lock (_gate) snap.LocInfection.Add((hwRoot, pct, cur));
                pct.SetValue(hwRoot, value);
            }
        }
        catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 {mapKey} 的 InfectionPercentage 失败：{ex.Message}"); }

        // 日志统一在 ApplyInfection 里逐图打（含官方随机区间），这里不再重复
    }

    /// <summary>
    /// 官方随机公式（`ConfigureZombies` IL 实证）：
    /// `num == 100 ⇒ 100；否则 (double)randomUtil.GetInt((int)num, 100, exclusive:false)`。
    /// RandomUtil 从 DI 取（`SPTarkov.Server.Core.Utils.RandomUtil`）；拿不到就退化为确定值（只警告一次）。
    /// </summary>
    private static double OfficialRoll(double num)
    {
        if (Math.Abs(num - 100) < 1e-9) return 100;   // 100 直取（先于测试 seam，保证语义）
        if (OfficialRollForTest != null) return OfficialRollForTest(num);
        int min = (int)Math.Round(num);
        if (min >= 100) return 100;
        if (min < 0) min = 0;
        try
        {
            var rnd = ResolveRandom();
            if (rnd == null) return num;
            var m = rnd.GetType().GetMethod("GetInt", new[] { typeof(int), typeof(int), typeof(bool) });
            if (m == null) return num;
            var v = m.Invoke(rnd, new object[] { min, 100, false });
            return v is int i ? i : num;
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 官方随机公式执行失败（本次用确定值 {Fmt(num)}）：{ex.Message}");
            return num;
        }
    }

    /// <summary>从 DI 取 SPT 的 RandomUtil（拿不到返回 null；只尝试一次）。</summary>
    private static object ResolveRandom()
    {
        if (_randomCache != null) return _randomCache;
        try
        {
            var t = RuntimeTypes.Find("SPTarkov.Server.Core.Utils.RandomUtil");
            if (t == null) return null;
            object inst = null;
            try { inst = Services?.GetService(t); } catch { inst = null; }
            if (inst == null)
            {
                try { inst = Activator.CreateInstance(t); } catch { }
            }
            if (inst != null) _randomCache = inst;
            return inst;
        }
        catch { return null; }
    }

    /// <summary>官方感染键 → 实际地图 id（与 SPT `GetLocationFromInfectedLocation` 一致）。</summary>
    private static IEnumerable<string> ExpandInfectedKey(string key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (k == "factory4") return new[] { "factory4_day", "factory4_night" };
        if (k == "sandbox") return new[] { "sandbox", "sandbox_high" };
        return new[] { k };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  僵尸波次注入（照 PPES 1.4.0 SeasonalRaidContent.AddEventBosses 的语义）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把 `SeasonalEventConfig.EventBossSpawns["halloweenzombies"]`（本机键名）里**本图那组**条目
    /// 克隆追加到 `location.Base.BossLocationSpawn`。
    ///
    /// <para><b>为什么必须做</b>：SPT 是在自己的季节路径里调 `AddEventBossesToMaps("halloweenzombies")`；
    /// 我们用 force.txt 强开时那段不跑 ⇒ 图上没有感染者波次 ⇒ 工厂照常刷普通 PMC。
    /// 感染率（LocationInfection/InfectionPercentage）只影响 UI 与感染程度，**不产生 bot**。</para>
    ///
    /// <para><b>幂等规则（SPT/PPES 同款）</b>：若图上已有同名 BossName 且其 `TriggerName != "botEvent"`
    /// ⇒ 跳过该条（保护本体首领）；官方表里 `TriggerName=="botEvent"` 的同名多条是允许的（一波多召）。
    /// 另外记录我们追加的实例引用：ABPS 整表重建后这些引用消失 ⇒ 会重新补。</para>
    /// </summary>
    private static void ApplyZombieWaves(object cfg, object seasonal, object zombie, Snapshot snap)
    {
        try
        {
            var ebs = Prop(cfg, "EventBossSpawns") as IDictionary;
            if (ebs == null) return;

            var name = (Prop(seasonal, "Name") as string) ?? "halloween";
            IDictionary byMap = null;
            foreach (var c in new[] { name + "zombies", "zombies", "halloweenzombies" })
            {
                byMap = FindValue(ebs, c) as IDictionary;
                if (byMap != null) break;
            }
            if (byMap == null) return;
            snap.WaveByMap = byMap;

            foreach (var mk in byMap.Keys.Cast<object>().ToList())
            {
                var map = mk?.ToString();
                if (string.IsNullOrEmpty(map)) continue;
                if (InfectionOfMap(zombie, map) <= 0) continue;     // 官方语义：只给有感染的图加
                AppendWavesForMap(map, byMap[mk] as IEnumerable, snap, log: true);
            }
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 追加僵尸波次失败（已忽略，不影响其它功能）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>给一张图追加波次；返回本次实际追加条数（幂等：已追过且实例还在 ⇒ 0）。</summary>
    private static int AppendWavesForMap(string map, IEnumerable bosses, Snapshot snap, bool log)
    {
        if (bosses == null) return 0;
        object loc = null;
        try
        {
            loc = LocationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })
                         ?.Invoke(LocationTable, new object[] { map });
        }
        catch { }
        if (loc == null) return 0;
        var baseObj = Prop(loc, "Base");
        if (baseObj == null) return 0;
        var list = Prop(baseObj, "BossLocationSpawn") as IList;
        if (list == null) return 0;

        // 已经追过、且我们的实例还在表里 ⇒ 幂等返回（ABPS 整表重建后实例会消失 ⇒ 会重新补）
        if (snap.AddedWaves.TryGetValue(map, out var prev) && prev.Count > 0 && prev.Any(p => list.Contains(p.Item)))
            return 0;

        // ★ 顺序很关键（SPT/PPES 的真规则是 `b.TriggerName == "botEvent" || b.BossName != boss.BossName`）：
        //   官方表里有些条目 TriggerName=""（laboratory 有 12 条）—— 若按原始顺序先加 "" 条目，
        //   后续同名条目会被判重挡住（laboratory 只会加到 19 条，而官方的实际意图是 29 条）。
        //   先把 `TriggerName=="botEvent"` 的加完、再加其余 ⇒ 同名条目全部能进（本机实测 laboratory=29）。
        var srcList = bosses.Cast<object>().Where(x => x != null).ToList();

        var added = new List<object>();
        var summaryParts = new List<string>();
        string zone = null;
        foreach (var src in srcList.Where(x => string.Equals(Prop(x, "TriggerName") as string, "botEvent", StringComparison.OrdinalIgnoreCase))
                                   .Concat(srcList.Where(x => !string.Equals(Prop(x, "TriggerName") as string, "botEvent", StringComparison.OrdinalIgnoreCase)))
                                   .ToList())
        {
            if (src == null) continue;
            var bossName = Prop(src, "BossName") as string;
            if (string.IsNullOrEmpty(bossName)) continue;

            bool blocked = false;
            foreach (var ex in list)
            {
                if (!string.Equals(Prop(ex, "BossName") as string, bossName, StringComparison.OrdinalIgnoreCase)) continue;
                // 本体同名首领（非 botEvent）⇒ 不重复加（SPT/PPES 同款）
                if (!string.Equals(Prop(ex, "TriggerName") as string, "botEvent", StringComparison.OrdinalIgnoreCase))
                { blocked = true; break; }
            }
            if (blocked) continue;

            object clone = null;
            try
            {
                var json = R.ToJson(src, m => Warn?.Invoke($"{Tag} ⚠ 僵尸波次序列化告警：{m}"));
                if (!string.IsNullOrEmpty(json)) clone = R.FromJson(src.GetType(), json, m => Warn?.Invoke($"{Tag} ⚠ 僵尸波次反序列化告警：{m}"));
            }
            catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 克隆僵尸波次失败（跳过一条）：{ex.Message}"); }
            if (clone == null) continue;

            try { list.Add(clone); added.Add(clone); } catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 追加僵尸波次失败（{map}）：{ex.Message}"); break; }

            if (zone == null)
            {
                var z = Prop(clone, "BossZone") as string;
                if (!string.IsNullOrWhiteSpace(z)) zone = z;
            }
        }

        if (added.Count > 0)
        {
            foreach (var src in srcList)
            {
                if (summaryParts.Count >= 3) break;
                var bn = Prop(src, "BossName") as string;
                if (string.IsNullOrEmpty(bn)) continue;
                var ch = Prop(src, "BossChance");
                var txt = $"{bn} {(ch == null ? "?" : Fmt(Convert.ToDouble(ch, CultureInfo.InvariantCulture)))}";
                if (!summaryParts.Contains(txt)) summaryParts.Add(txt);
            }

            lock (_gate)
            {
                if (!snap.AddedWaves.TryGetValue(map, out var l)) { l = new List<(IList, object)>(); snap.AddedWaves[map] = l; }
                l.Clear();
                foreach (var it in added) l.Add((list, it));
            }
            if (log)
            {
                var reason = added.Count < srcList.Count
                    ? $"（源 {srcList.Count} 条，按 SPT 判重规则同名只留 1 条非 botEvent 条目）"
                    : "";
                Info?.Invoke($"{Tag} {map}：追加僵尸波次 {added.Count} 条{reason}（{string.Join(" / ", summaryParts)}"
                           + (zone != null ? $"｜BossZone={zone}" : "") + "）");
            }
        }
        return added.Count;
    }

    /// <summary>按图取当前感染率（只读；用于"只给有感染的图加僵尸波次"）。</summary>
    private static double InfectionOfMap(object zombie, string map)
    {
        try
        {
            var inf = Prop(zombie, "MapInfectionAmount") as IDictionary;
            if (inf == null) return -1;
            foreach (var k in inf.Keys.Cast<object>().ToList())
            {
                var key = k?.ToString() ?? "";
                if (string.Equals(key, map, StringComparison.OrdinalIgnoreCase)
                    || ExpandInfectedKey(key).Any(x => string.Equals(x, map, StringComparison.OrdinalIgnoreCase)))
                    return Convert.ToDouble(inf[k], CultureInfo.InvariantCulture);
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// ★ 唯一要动的全局开关：`SeasonalEventConfig.EnableSeasonalEventDetection`（PPES 1.4.0 同款）。
    ///
    /// <para><b>★ 2026-10-30 最后一环</b>：光有感染率不产生 bot —— 僵尸由
    /// `eventBossSpawns["halloweenzombies"]` 的条目追加进 `location.BossLocationSpawn` 才刷；
    /// 强开活动时 SPT 的 `AddEventBossesToMaps` 不跑 ⇒ 本维度自己克隆追加（见 <see cref="ApplyZombieWaves"/>）。</para>
    ///
    /// <para><b>★ 2026-10-30 实机回补</b>：本机日志证明强开活动时 SPT **不会**跑自己的
    /// `SeasonalEventService.ConfigureZombies`（只有一句 `活动：Christmas 已启用`），
    /// ⇒ 光改 `zombieSettings` + `EnableSeasonalEventDetection`，地图界面感染率仍是 0、进图没僵尸。
    /// 因此本维度自己把感染率写进 `globals.LocationInfection`（实测 `Dictionary&lt;string,int&gt;`）与
    /// `location.Base.Events.Halloween2024.InfectionPercentage`（实测 `double?`）；PPES 不写这两处
    /// 是因为它跑在"季节窗内、SPT 自己会算"的前提上 —— 我们的场景不成立。</para>
    ///
    /// <para><b>为什么不再动别的</b>：PPES 1.4.0 的 `ApplyOutbreak2026` 只改
    /// `ZombieSettings.Enabled` / `mapInfectionAmount` / `EventBossSpawns` / `StandingForKill` /
    /// 这个检测开关；**从不写** `globals.LocationInfection`，也**从不动**
    /// `location.*.InfectionPercentage` —— 那些由 SPT 自己的 `SeasonalEventService.ConfigureZombies`
    /// 在进图时按配置算。我们之前顺手写它们，反而在实机出现"部分任务图不显示"，
    /// 且没有任何证据表明 SPT 不会自己打开 globals 的感染显示开关（上一版那次写 globals
    /// 在类型不匹配时**整段抛异常**了，所以那条日志不能作为"SPT 不开"的证据）
    /// ⇒ 按最小实现回退，globals / location 一律不碰。</para>
    /// </summary>
    /// <summary>
    /// 感染率 p 缩放 `location.Base.BossLocationSpawn` 里**普通 AI 条目**的 `BossChance`。
    ///
    /// <para><b>单位实证</b>：客户端 `BossLocationSpawn.CalculateChance()` → `MyExtensions.IsTrue100(BossChance)`，
    /// 而 `IsTrue100(Single)` 的 IL 是 `Random(0,100) &lt; chance` ⇒ **BossChance 是百分数（0~100，≥100 恒真）**。
    /// 事件表里 `infectedAssault 7000` 那种只是"恒真"，不是另一套刻度 —— 我们只缩普通 AI 白名单条目，
    /// 感染者/活动首领一个都不碰。</para>
    /// 语义：`new = floor(old × (100-p)/100)`；`p≥100 ⇒ 0`（保留条目）。
    /// </summary>
    private static void ApplyBossChanceScaling(EventDef ev, object zombie, Snapshot snap)
    {
        try
        {
            if (LocationTable == null) return;
            var inf = Prop(zombie, "MapInfectionAmount") as IDictionary;
            if (inf == null) return;

            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in inf.Keys.Cast<object>().ToList())
            {
                var key = k?.ToString() ?? "";
                if (string.IsNullOrEmpty(key)) continue;
                foreach (var m in ExpandInfectedKey(key)) maps.Add(m);
            }

            foreach (var map in maps)
            {
                double p = InfectionOfMap(zombie, map);
                if (p <= 0) continue;
                ScaleBossChanceForMap(map, p, ev, snap, log: true);
            }
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 缩放普通首领条目失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>给一张图缩放普通 AI 条目的 BossChance；返回改动条数（已缩过且实例还在 ⇒ 0）。</summary>
    private static int ScaleBossChanceForMap(string map, double p, EventDef ev, Snapshot snap, bool log)
    {
        object loc = null;
        try
        {
            loc = LocationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })
                         ?.Invoke(LocationTable, new object[] { map });
        }
        catch { }
        if (loc == null) return 0;
        var baseObj = Prop(loc, "Base");
        if (baseObj == null) return 0;
        var list = Prop(baseObj, "BossLocationSpawn") as IList;
        if (list == null || list.Count == 0) return 0;

        if (snap.ScaledBossChance.TryGetValue(map, out var prev) && prev.Count > 0 && prev.Any(x => list.Contains(x.Entry)))
            return 0;   // 幂等（ABPS 整表重建后旧实例消失 ⇒ 按新对象重算）

        // 本活动声明要刷的首领（这些一个都不动）
        HashSet<string> protectedRoles = null;
        try
        {
            if (ev?.BossSpawns != null && ev.BossSpawns.TryGetValue(map, out var declared) && declared != null)
                protectedRoles = new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase);
        }
        catch { }

        double factor = Math.Max(0, 100 - p) / 100.0;
        int n = 0;
        var changed = new List<string>();
        var listSnap = new List<(IList List, object Entry, PropertyInfo Prop, object Orig)>();
        foreach (var entry in list)
        {
            if (entry == null) continue;
            var role = Prop(entry, "BossName") as string;
            if (string.IsNullOrEmpty(role)) continue;
            if (!NormalAiRoles.Contains(role)) continue;                       // infected*/boss*/活动首领 全部排除
            if (protectedRoles != null && protectedRoles.Contains(role)) continue;
            var chProp = entry.GetType().GetProperty("BossChance", BindingFlags.Public | BindingFlags.Instance);
            if (chProp == null) continue;
            var curObj = chProp.GetValue(entry);
            if (curObj == null) continue;
            double cur = Convert.ToDouble(curObj, CultureInfo.InvariantCulture);
            int nv = (int)Math.Floor(cur * factor);
            if (p >= 100) nv = 0;
            if (nv == (int)Math.Floor(cur) && Math.Abs(cur - Math.Floor(cur)) < 1e-9) continue;
            try
            {
                chProp.SetValue(entry, (double?)nv);
                listSnap.Add((list, entry, chProp, curObj));
                n++;
                if (changed.Count < 4) changed.Add($"{role} {Fmt(cur)}→{nv}");
            }
            catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 {map}/{role} 的 BossChance 失败（跳过该条）：{ex.Message}"); }
        }

        if (n > 0)
        {
            lock (_gate)
            {
                if (!snap.ScaledBossChance.TryGetValue(map, out var l))
                { l = new(); snap.ScaledBossChance[map] = l; }
                l.Clear();
                l.AddRange(listSnap);
            }
            if (log)
                Info?.Invoke($"{Tag} {map}：感染率 {Fmt(p)} ⇒ 普通首领条目 {n} 条 BossChance ×{Fmt(factor)}"
                           + $"（{string.Join(" / ", changed)}{(n > changed.Count ? " …" : "")}）");
        }
        return n;
    }

    private static void ApplyDetectionSwitch(object cfg, Snapshot snap)
    {
        SetBool(cfg, "EnableSeasonalEventDetection", snap,
                m => Info?.Invoke($"{Tag} 已打开季节事件检测（EnableSeasonalEventDetection {m} → true）"));

        // ★ EventType 的补写已统一交给独立维度 `HalloweenIllumination`（万圣节家族活动都会调它，
        //   不再只限"原生僵尸"路径；重复写/重复快照由那边去重）。
    }

    /// <summary>把 `obj.Prop`（bool）置 true；改了就快照原值并打日志。</summary>
    private static void SetBool(object obj, string propName, Snapshot snap, Action<string> onChanged)
    {
        if (obj == null) return;
        try
        {
            var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || p.PropertyType != typeof(bool) || !p.CanWrite) return;
            bool cur = (bool)p.GetValue(obj);
            if (cur) return;
            snap.Bools.Add((obj, p, cur));
            p.SetValue(obj, true);
            onChanged?.Invoke("false");
        }
        catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 {propName} 失败：{ex.Message}"); }
    }

    private static void ApplyTagilla(object cfg, object seasonal, EventDef ev, Snapshot snap)
    {
        double target = ev.InfectedTagillaChance ?? 12;
        if (target <= 0) return;

        var ebs = Prop(cfg, "EventBossSpawns") as IDictionary;
        if (ebs == null) return;

        // 本机键名 = halloweenzombies；PPES 源码用 "zombies" ⇒ 两个都认
        var name = (Prop(seasonal, "Name") as string) ?? "halloween";
        var candidates = new[] { name + "zombies", "zombies", "halloweenzombies" };
        IDictionary byMap = null;
        foreach (var c in candidates)
        {
            byMap = FindValue(ebs, c) as IDictionary;
            if (byMap != null) break;
        }
        if (byMap == null)
        {
            Warn?.Invoke($"{Tag} ⚠ eventBossSpawns 里找不到 [{string.Join(" / ", candidates)}] ⇒ infectedTagilla 概率不改");
            return;
        }

        int n = 0;
        foreach (var mk in byMap.Keys.Cast<object>().ToList())
        {
            if (byMap[mk] is not IEnumerable list) continue;
            foreach (var sp in list.Cast<object>().ToList())
            {
                if (sp == null) continue;
                var boss = Prop(sp, "BossName") as string;
                if (!string.Equals(boss, "infectedTagilla", StringComparison.OrdinalIgnoreCase)) continue;
                var chProp = sp.GetType().GetProperty("BossChance", BindingFlags.Public | BindingFlags.Instance);
                if (chProp == null) continue;
                var curObj = chProp.GetValue(sp);
                double cur = curObj == null ? 0 : Convert.ToDouble(curObj, CultureInfo.InvariantCulture);
                if (cur > 0) continue;      // PPES 只改 <= 0 的
                snap.Tagilla.Add((sp, chProp, curObj));
                try { chProp.SetValue(sp, (double?)target); n++; }
                catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 infectedTagilla 概率失败：{ex.Message}"); }
            }
        }
        if (n > 0) Info?.Invoke($"{Tag} infectedTagilla 概率：{Fmt(target)}（共 {n} 条，原 0/空 → {Fmt(target)}）");
    }

    private static void ApplyKarma(Snapshot snap)
    {
        var types = Prop(BotTable, "Types") as IDictionary;
        if (types == null)
        {
            Warn?.Invoke($"{Tag} ⚠ 拿不到 BotTable.Types ⇒ 业力不改（不影响感染本体）");
            return;
        }
        int roles = 0;
        foreach (var role in ScavRoles)
        {
            var bot = FindValue(types, role);
            if (bot == null) continue;
            var exp = Prop(bot, "BotExperience");
            var sfl = Prop(exp, "StandingForKill") as IDictionary;
            if (sfl == null) continue;
            bool touched = false;
            foreach (var k in sfl.Keys.Cast<object>().ToList())
            {
                double v;
                try { v = Convert.ToDouble(sfl[k], CultureInfo.InvariantCulture); } catch { continue; }
                if (v >= 0) continue;               // 正值不动
                snap.Karma.Add((sfl, k?.ToString() ?? "", v));
                try { sfl[k] = 0d; touched = true; }
                catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 StandingForKill[{k}] 失败：{ex.Message}"); }
            }
            if (touched) roles++;
        }
        snap.RoleCount = roles;
        if (snap.Karma.Count > 0)
            Info?.Invoke($"{Tag} 业力：{roles} 个 scav 角色 × {snap.Karma.Count} 档 StandingForKill 已清零（原值已快照）");
        else
            Info?.Invoke($"{Tag} 业力：5 类 scav 角色里没有负值 StandingForKill（无需改动）");
    }

    /// <summary>还原（由 <c>_restoreActions</c> LIFO 回放）。逐字写回快照值。</summary>
    public static void Restore()
    {
        Snapshot snap;
        lock (_gate) { snap = _state; _state = null; }
        if (snap == null) return;

        int ok = 0, fail = 0;
        try
        {
            if (snap.EnabledProp is PropertyInfo ep && snap.ZombieSettings != null)
            {
                try { ep.SetValue(snap.ZombieSettings, snap.OrigEnabled); ok++; } catch { fail++; }
            }
            var inf = Prop(snap.ZombieSettings, "MapInfectionAmount") as IDictionary;
            if (inf != null)
                foreach (var (map, orig) in snap.Infection)
                {
                    var k = FindKey(inf, map);
                    if (k == null) continue;
                    try { inf[k] = orig; ok++; } catch { fail++; }
                }
            foreach (var (spawn, prop, orig) in snap.Tagilla)
            {
                try { ((PropertyInfo)prop).SetValue(spawn, orig); ok++; } catch { fail++; }
            }
            foreach (var (dict, key, orig) in snap.Karma)
            {
                var k = FindKey(dict, key);
                if (k == null) continue;
                try { dict[k] = orig; ok++; } catch { fail++; }
            }
            foreach (var (li, key, orig, existed) in snap.GlobalInfection)
            {
                try
                {
                    if (existed) { if (li.Contains(key)) { li[key] = orig; ok++; } }
                    else { if (li.Contains(key)) { li.Remove(key); ok++; } }   // 我们新增的键 ⇒ 删掉
                }
                catch { fail++; }
            }
            foreach (var (hwRoot, prop, orig) in snap.LocInfection)
            {
                try { prop.SetValue(hwRoot, orig); ok++; } catch { fail++; }
            }
            foreach (var (obj, prop, orig) in snap.Bools)
            {
                try { prop.SetValue(obj, orig); ok++; } catch { fail++; }
            }
            // ★ 普通 AI 条目的 BossChance 还原（逐字写回原值）
            foreach (var kv in snap.ScaledBossChance)
            {
                foreach (var (lst, entry, prop, orig) in kv.Value)
                {
                    try { if (prop != null && entry != null) { prop.SetValue(entry, orig); ok++; } } catch { fail++; }
                }
            }
            snap.ScaledBossChance.Clear();

            // ★ 普通 AI 波次数量还原（逐字写回原 Number）
            foreach (var kv in snap.ScaledWaves)
            {
                foreach (var (lst, wave, orig) in kv.Value)
                {
                    try
                    {
                        var p = wave?.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
                        if (p != null && wave != null) { p.SetValue(wave, (int?)orig); ok++; }
                    }
                    catch { fail++; }
                }
            }
            snap.ScaledWaves.Clear();

            // ★ 只移除我们追加的僵尸波次（按对象引用）；本体原有条目一个不动
            foreach (var kv in snap.AddedWaves)
            {
                foreach (var (lst, item) in kv.Value)
                {
                    try { if (lst != null && item != null && lst.Contains(item)) { lst.Remove(item); ok++; } } catch { fail++; }
                }
            }
            snap.AddedWaves.Clear();
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 还原异常（已尽量复原）：{ex.GetType().Name}: {ex.Message}");
        }

        Info?.Invoke($"{Tag} 已还原（enabled={FmtBool(snap.OrigEnabled)} / 感染率 {snap.Infection.Count} 项 / "
                   + $"概率 {snap.Tagilla.Count} 条 / 业力 {snap.Karma.Count} 档；复原 {ok} 处"
                   + (fail > 0 ? $"，失败 {fail} 处" : "") + "）");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ABPS（BotPlacementSystem）兼容
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ABPS 2.1.0 实测（本机 `acidphantasm-botplacementsystem.dll`，Mono.Cecil 反编译）：
    /// <code>
    /// BotPlacementSystemServer.Controllers.MapSpawns
    ///   private void ReplaceOriginalLocations()               // 全图：Base.BossLocationSpawn = 原版克隆；Base.Waves = 原版克隆
    ///   private void ReplaceOriginalLocationForMap(string name) // 单图：同上
    /// </code>
    /// ⇒ 它会把我们**追加进 BossLocationSpawn 的僵尸首领条目**冲掉；并且（实测路径上）会让
    /// `location` 上的感染字段回落到原版快照。Scourge 的做法见其 `BpsSpawnRestorePatch.cs`：
    /// 用 Harmony **postfix** 在重建之后把自己的条目再补一遍。我们照此实现：重建后**重新写感染率**。
    /// 没装 ABPS（类型找不到）⇒ 静默跳过。
    /// </summary>
    private static void EnsureAbpsHook()
    {
        lock (_gate)
        {
            if (_abpsHooked) return;
        }
        try
        {
            lock (_gate) { if (_abpsMissing) return; }
            var typeName = AbpsTypeNameForTest ?? "BotPlacementSystemServer.Controllers.MapSpawns";
            var t = RuntimeTypes.Find(typeName);
            if (t == null)
            {
                lock (_gate) _abpsMissing = true;        // 没装 ABPS ⇒ 静默跳过（只查一次）
                return;
            }

            var all = t.GetMethod("ReplaceOriginalLocations", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var one = t.GetMethod("ReplaceOriginalLocationForMap", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (all == null && one == null) return;

            var harmony = new HarmonyLib.Harmony(HarmonyIdAbps);
            int patched = 0;
            if (all != null)
            {
                var postfix = typeof(SeasonalZombieDimension).GetMethod(nameof(AbpsPostfixAll),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(all, postfix: new HarmonyLib.HarmonyMethod(postfix));
                patched++;
            }
            if (one != null)
            {
                var postfix = typeof(SeasonalZombieDimension).GetMethod(nameof(AbpsPostfixMap),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(one, postfix: new HarmonyLib.HarmonyMethod(postfix));
                patched++;
            }
            lock (_gate) _abpsHooked = true;
            Info?.Invoke($"{Tag} 已挂 ABPS 兼容补丁（{patched} 个方法：ReplaceOriginalLocations / ReplaceOriginalLocationForMap）");
        }
        catch (Exception ex)
        {
            // 补丁失败不许影响启动/其它功能
            Warn?.Invoke($"{Tag} ⚠ ABPS 兼容补丁挂载失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>`ReplaceOriginalLocations()` 之后：所有地图重写一遍。</summary>
    private static void AbpsPostfixAll()
    {
        try
        {
            ReapplyAfterAbps(null);
        }
        catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ ABPS 重建后重写感染率失败（全部地图）：{ex.Message}"); }
    }

    /// <summary>`ReplaceOriginalLocationForMap(string location)` 之后：只重写这一张。</summary>
    private static void AbpsPostfixMap(string location)
    {
        try
        {
            ReapplyAfterAbps(location);
        }
        catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ ABPS 重建后重写感染率失败（{location}）：{ex.Message}"); }
    }

    /// <summary>ABPS 重建后重写感染率（当前生效状态；不改快照、幂等：值相同就跳过写）。</summary>
    private static void ReapplyAfterAbps(string onlyMap)
    {
        Snapshot snap;
        lock (_gate) snap = _state;
        if (snap == null || snap.ZombieSettings == null) return;
        var inf = Prop(snap.ZombieSettings, "MapInfectionAmount") as IDictionary;
        if (inf == null) return;

        foreach (var k in inf.Keys.Cast<object>().ToList())
        {
            var key = k?.ToString() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            double v;
            try { v = Convert.ToDouble(inf[k], CultureInfo.InvariantCulture); } catch { continue; }

            // ★ 感染键 → 实际地图 id（factory4 → day/night、sandbox → +sandbox_high）
            var mapsOfKey = ExpandInfectedKey(key)
                .Where(m => string.IsNullOrEmpty(onlyMap) || string.Equals(m, onlyMap, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mapsOfKey.Count == 0) continue;

            // 只在命中（单图 postfix 只修对应图）时写官方键 + 展开图 id 键
            WriteInfectionWorld(key, v);

            foreach (var map in mapsOfKey)
            {

                // ★ 刷怪表被 ABPS 整表重建 ⇒ 我们的僵尸波次也一起没了，这里补回去
                int waves = 0;
                try
                {
                    var wk = FindKey(snap.WaveByMap, map);
                    if (wk != null && snap.WaveByMap[wk] is IEnumerable wv)
                        waves = AppendWavesForMap(map, wv, snap, log: false);
                }
                catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ ABPS 重建后补回僵尸波次失败（{map}）：{ex.Message}"); }

                int scaled = 0;
                try { scaled = ScaleWavesForMap(map, v, snap, log: false); }
                catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ ABPS 重建后重新缩放波次失败（{map}）：{ex.Message}"); }
                int scaledBoss = 0;
                try { scaledBoss = ScaleBossChanceForMap(map, v, null, snap, log: false); }
                catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ ABPS 重建后重新缩放首领条目失败（{map}）：{ex.Message}"); }

                Info?.Invoke($"{Tag} ABPS 重建了 {map} 的刷怪表 ⇒ 已重新写入感染率 {Fmt(v)}"
                           + (waves > 0 ? $"、并补回僵尸波次 {waves} 条" : "")
                           + (scaled > 0 ? $"、并按感染率重新缩放普通 AI 波次 {scaled} 条" : "")
                           + (scaledBoss > 0 ? $"、普通首领条目 {scaledBoss} 条" : ""));
            }
        }
    }

    /// <summary>只写、不改快照（ABPS 重建/客户端请求 globals 时用；值相同也照写，保证界面刷新）。
    /// 同时写官方键与展开后的图 id 键（缺键就补，客户端选图界面按 location.Id 取）。</summary>
    private static void WriteInfectionWorld(string mapKey, double value)
    {
        try
        {
            var li = Prop(GlobalTable, "LocationInfection") as IDictionary;
            if (li != null)
            {
                int iv = (int)Math.Round(value);
                var key = FindKey(li, mapKey);
                if (key != null) li[key] = iv;
                else li[mapKey] = iv;
                foreach (var ext in ExpandInfectedKey(mapKey))
                {
                    if (string.Equals(ext, mapKey, StringComparison.Ordinal)) continue;
                    li[ext] = iv;
                }
            }
        }
        catch { }
        try
        {
            foreach (var locId in ExpandInfectedKey(mapKey))
            {
                object loc = null;
                try
                {
                    loc = LocationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })
                                 ?.Invoke(LocationTable, new object[] { locId });
                }
                catch { }
                var hwRoot = Prop(Prop(Prop(loc, "Base"), "Events"), "Halloween2024");
                if (hwRoot == null) continue;
                var pct = hwRoot.GetType().GetProperty("InfectionPercentage", BindingFlags.Public | BindingFlags.Instance);
                pct?.SetValue(hwRoot, value);
            }
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  /client/globals 请求时即时修正
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 给 `SPTarkov.Server.Core.Callbacks.DataCallbacks.GetGlobals` 挂 postfix：
    /// 客户端**每次**拉 `/client/globals` 之前把感染率/EventType/波次再写一遍（幂等）。
    ///
    /// <para><b>为什么</b>：客户端 `CG_GetGlobalConfigAndClientSettingsConfig.MoveNext` 只在**启动/登录**时
    /// 拉一次 globals 并 `InfectionHalloween.Init(topLevel.LocationInfection)`；如果服务端在客户端已经拉过
    /// 之后才套用活动（切活动、F12），客户端到下次重启前都拿不到新数据。请求时补一次可以消除这个时序窗口。</para>
    /// </summary>
    private static void EnsureGlobalsFreshHook()
    {
        lock (_gate) { if (_globalsHooked) return; }
        try
        {
            var t = RuntimeTypes.Find("SPTarkov.Server.Core.Callbacks.DataCallbacks");
            if (t == null) return;
            var m = t.GetMethod("GetGlobals", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) return;
            var postfix = typeof(SeasonalZombieDimension).GetMethod(nameof(GlobalsPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            new HarmonyLib.Harmony(HarmonyIdGlobals).Patch(m, postfix: new HarmonyLib.HarmonyMethod(postfix));
            lock (_gate) _globalsHooked = true;
            Info?.Invoke($"{Tag} 已挂 /client/globals 请求补丁（每次请求前即时修正感染数据）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ /client/globals 请求补丁挂载失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>GetGlobals postfix：当前有生效状态 ⇒ 把感染率/EventType/波次再写一遍（幂等、只写不抛）。</summary>
    private static void GlobalsPostfix()
    {
        try
        {
            Snapshot snap;
            lock (_gate) snap = _state;
            if (snap == null) return;
            ReapplyAfterAbps(null);
            HalloweenIllumination.EnsureStillSet();
        }
        catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ globals 请求时修正失败（已忽略）：{ex.Message}"); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  普通 AI 波次数量缩放（感染率越高，普通 AI 越少）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// `Wave.Number : int?` 是"该波次刷多少个 bot"（`SlotsMin/SlotsMax` 是**刷点槽位**，不动）。
    /// 语义：`new = round(old * (100 - p) / 100)`；`p >= 100 ⇒ 0`（保留条目、数量 0）。
    /// 快照记录每个 Wave 的原数量，还原逐字写回；幂等（始终按快照原值算，不在上次结果上再乘）。
    /// </summary>
    private static void ApplyWaveScaling(object zombie, Snapshot snap)
    {
        try
        {
            if (LocationTable == null) return;
            var inf = Prop(zombie, "MapInfectionAmount") as IDictionary;
            if (inf == null) return;

            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in inf.Keys.Cast<object>().ToList())
            {
                var key = k?.ToString() ?? "";
                if (string.IsNullOrEmpty(key)) continue;
                foreach (var m in ExpandInfectedKey(key)) maps.Add(m);
            }

            foreach (var map in maps)
            {
                double p = InfectionOfMap(zombie, map);
                if (p <= 0) continue;
                ScaleWavesForMap(map, p, snap, log: true);
            }
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 缩放普通 AI 波次失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>给一张图缩放普通 AI 波次；返回本次改动的波次数（已缩过且实例还在 ⇒ 0）。</summary>
    private static int ScaleWavesForMap(string map, double p, Snapshot snap, bool log)
    {
        object loc = null;
        try
        {
            loc = LocationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })
                         ?.Invoke(LocationTable, new object[] { map });
        }
        catch { }
        if (loc == null) return 0;
        var baseObj = Prop(loc, "Base");
        if (baseObj == null) return 0;
        var waves = Prop(baseObj, "Waves") as IList;
        if (waves == null || waves.Count == 0) return 0;

        if (snap.ScaledWaves.TryGetValue(map, out var prev) && prev.Count > 0 && prev.Any(x => waves.Contains(x.Wave)))
            return 0;   // 幂等（ABPS 整表重建后旧实例消失 ⇒ 会按新对象重算）

        double factor = Math.Max(0, 100 - p) / 100.0;

        // 先收集每条的 (对象, 属性, 原数量)
        var items = new List<(object Wave, PropertyInfo Prop, int Orig)>();
        foreach (var w in waves)
        {
            if (w == null) continue;
            var numProp = w.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
            if (numProp == null || numProp.PropertyType != typeof(int?)) continue;
            var curObj = numProp.GetValue(w);
            if (curObj == null) continue;
            int cur = Convert.ToInt32(curObj, CultureInfo.InvariantCulture);
            if (cur <= 0) continue;
            items.Add((w, numProp, cur));
        }
        if (items.Count == 0) return 0;

        // ★ 按"整图总量"缩放再分摊到各波次（要求：newCount = round(Σold × (100-p)/100)；
        //   若逐波单独 round，3→1.5 会被抬成 2，总量就不对了（实测 24×0.5 得到 16 而不是 12）。
        int sumOld = items.Sum(x => x.Orig);
        int target = (int)Math.Round(sumOld * factor, MidpointRounding.AwayFromZero);
        if (p >= 100) target = 0;
        if (target > sumOld) target = sumOld;

        var ideal = items.Select(x => x.Orig * factor).ToArray();
        var newVals = ideal.Select(v => (int)Math.Floor(v)).ToArray();
        int deficit = target - newVals.Sum();
        // 余数大的先+1（同余数保持数组顺序，结果确定、可复现）
        var order = Enumerable.Range(0, items.Count)
            .OrderByDescending(i => ideal[i] - Math.Floor(ideal[i]))
            .ThenBy(i => i).ToArray();
        for (int k = 0; k < deficit && k < order.Length; k++) newVals[order[k]]++;
        for (int k = 0; k < -deficit && k < order.Length; k++)
            if (newVals[order[order.Length - 1 - k]] > 0) newVals[order[order.Length - 1 - k]]--;

        int n = 0, sumNew = 0;
        var list = new List<(IList List, object Wave, int Orig)>();
        for (int i = 0; i < items.Count; i++)
        {
            var (w, prop, cur) = items[i];
            int nv = newVals[i];
            try
            {
                prop.SetValue(w, (int?)nv);
                list.Add((waves, w, cur));
                n++; sumNew += nv;
            }
            catch (Exception ex) { Warn?.Invoke($"{Tag} ⚠ 写 {map} 波次 Number 失败（跳过该条）：{ex.Message}"); }
        }
        sumOld = list.Sum(x => x.Orig);
        sumNew = list.Sum(x => Convert.ToInt32(
            x.Wave.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance)
                  ?.GetValue(x.Wave) ?? 0, CultureInfo.InvariantCulture));

        if (n > 0)
        {
            lock (_gate)
            {
                if (!snap.ScaledWaves.TryGetValue(map, out var l)) { l = new(); snap.ScaledWaves[map] = l; }
                l.Clear();
                l.AddRange(list);
            }
            if (log)
                Info?.Invoke($"{Tag} {map}：感染率 {Fmt(p)} ⇒ 普通 AI 波次 {n} 条数量 ×{Fmt(factor)}"
                           + $"（原 {sumOld} → {sumNew}）");
        }
        return n;
    }

    /// <summary>供 Plugin 在「还原 + 套用」开头调用：清掉幂等标记（还原动作本身由 `_restoreActions` 负责）。</summary>
    public static void ResetApplied()
    {
        lock (_gate) _state = null;
        ZombieBuffFix.ResetApplied();
    }

    internal static string Describe()
    {
        lock (_gate)
        {
            if (_state == null) return $"{Tag} 当前无生效的感染改动";
            return $"{Tag} 生效中：enabled 原值={FmtBool(_state.OrigEnabled)}｜感染率改动 {_state.Infection.Count} 项"
                 + $"｜infectedTagilla {_state.Tagilla.Count} 条｜业力 {_state.Karma.Count} 档";
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  服务 / 反射工具
    // ══════════════════════════════════════════════════════════════════════════

    private static object _cfgCache;

    /// <summary>从 DI 取 SeasonalEventConfig（类型必须用运行时那份 Core，见 RuntimeTypes）。</summary>
    private static object ResolveConfig()
    {
        if (_cfgCache != null) return _cfgCache;
        Type primary = null;
        try { primary = RuntimeTypes.Find(ConfigTypeName); } catch { }

        var candidates = new List<Type>();
        if (primary != null) candidates.Add(primary);
        try
        {
            foreach (var a in RuntimeTypes.CoreCopies())
            {
                Type t = null;
                try { t = a.GetType(ConfigTypeName, false); } catch { }
                if (t != null && !candidates.Contains(t)) candidates.Add(t);
            }
        }
        catch { }
        try
        {
            var alc = RuntimeTypes.ModAlc;
            if (alc != null)
                foreach (var a in alc.Assemblies)
                {
                    Type t = null;
                    try { t = a.GetType(ConfigTypeName, false); } catch { }
                    if (t != null && !candidates.Contains(t)) candidates.Add(t);
                }
        }
        catch { }

        foreach (var t in candidates)
        {
            object inst = null;
            try { inst = Services?.GetService(t); } catch { inst = null; }
            if (inst == null) continue;
            _cfgCache = inst;
            Info?.Invoke($"{Tag} SeasonalEventConfig 已解析：{t.FullName}｜"
                       + $"与运行时 Core 同一份={ReferenceEquals(t.Assembly, RuntimeTypes.LiveCoreAssembly)}");
            return inst;
        }
        return null;
    }

    private static object FindSeasonalEvent(object cfg, string key)
    {
        var events = Prop(cfg, "Events") as IEnumerable;
        if (events == null) return null;
        foreach (var e in events)
        {
            var name = Prop(e, "Name") as string;
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?.GetValue(o);
        }
        catch { return null; }
    }

    private static object FindValue(IDictionary d, string key)
    {
        try
        {
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return d[k];
        }
        catch { }
        return null;
    }

    private static object FindKey(IDictionary d, string key)
    {
        try
        {
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return k;
        }
        catch { }
        return null;
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string FmtBool(object v) => v is bool b ? (b ? "true" : "false") : "(null)";

    private static void Once(ref bool flag, string msg)
    {
        if (flag) return;
        flag = true;
        Warn?.Invoke(msg);
    }

    private static string Signature(EventDef ev)
        => ev.Id + "|" + (ev.NativeZombies == true ? "1" : "0")
         + "|" + (ev.NoKarmaLoss == true ? "1" : "0")
         + "|" + (ev.InfectedTagillaChance?.ToString("R", CultureInfo.InvariantCulture) ?? "-")
         + "|" + (ev.ZombieSeasonalKey ?? "-")
         + "|" + (ev.ZombieOutbreak?.EveryMapMin?.ToString("R", CultureInfo.InvariantCulture) ?? "-")
         + "|" + string.Join(",", (ev.ZombieOutbreak?.Extra ?? new Dictionary<string, double>())
                                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                    .Select(kv => kv.Key + "=" + kv.Value.ToString("R", CultureInfo.InvariantCulture)));
}
