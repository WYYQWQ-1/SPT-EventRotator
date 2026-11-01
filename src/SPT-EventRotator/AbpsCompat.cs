using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// 与 **`acidphantasm-botplacementsystem`**（DLL 命名空间 `BotPlacementSystemServer`，官方缩写 **ABPS**
/// = Acid's **B**ot **P**lacement **S**ystem）**兼容**，但**绝不修改它的任何文件或配置**。
///
/// ⚠ **别和 `acidphantasm-progressivebotsystem`（官方缩写 APBS）搞混** —— 本机两个都装着，
///   缩写字母顺序只差一点、含义相反：
///   那个**管装备生成**（见 `PbsCompat.cs`）；这个**只管刷点**。
///   本地 DLL 关键词实证：`SpawnChance` 在这里出现 **1239** 次、`BossLocationSpawn` 2 次，
///   而 `GenerateInventory` / `GetEquipmentByBotRole` **0** 次 ⇒ 它**完全不碰装备**。
///
/// 背景（从它的源码/反编译确认）：
///   ABPS 的 MapSpawns.ConfigureInitialData / RebuildMap 会清空并按它自己的配置重建
///   Base.BossLocationSpawn（BossSpawns.GetConfigValueForLocation 遍历 ModConfig.Config.BossConfig），
///   且 SpawnChance[map]==0 的条目直接 `continue`（根本不加入列表）。
///   它还 patch 掉了 SPT 的 SeasonalEventService.ReplaceBotHostility（Prefix return false）。
///   ⇒ 所以"改首领表"和"改 ABPS 配置"都不是本 mod 该做的事。
///
/// 本 mod 的**唯一策略**（全部只写本 mod 自己的东西）：
///   1) ABPS 跑完后，在 raid 阶段把缺失的活动敌种**自己追加**进地图首领表（带骷髅标记）
///   2) 把新敌种补进地图的 AdditionalHostilitySettings（否则它对玩家中立 → 站着不打人）
/// 本类只做**只读**探测与诊断（不改 ABPS 任何东西）。
/// </summary>
public static class AbpsCompat
{
    private const string TypeName = "BotPlacementSystemServer.Globals.ModConfig";

    private static Type _modConfigType;
    private static bool _probed;
    private static ISptLogger<EventRotatorServer> _log;

    public static bool Detected { get; private set; }

    /// <summary>ABPS 的 mod 目录（**只读**用途；找不到则 null）。</summary>
    public static string ModDir { get; private set; }

    // ═══════════════ ★★★ v2.6.53【P0 修复】写入时机问题 ═══════════════
    //
    // 实测：`Plugin.ApplyExtraDimensions` 在**本 mod 的** `OnLoadAsync` 里调 `SetSpawnChance`，
    //   而 ABPS 的 `ModConfig.Config` 要到**它自己的** `OnLoadAsync`（ABPS `Globals/ModConfig.cs:62-65`）
    //   才赋值 ⇒ 日志实证 `Config.BossConfig 拿不到（ABPS 可能尚未初始化）` **780 次 / 成功 0 次**。
    // 修法：
    //   ① 写失败时**暂存**到 `_pending`（不再每次刷 warning，只警告一次）；
    //   ② `BossWavePatch.ReplayAbpsBossChances()` 会在 ABPS 的
    //      `MapSpawns.ReplaceOriginalLocations/ForMap` **postfix**（ABPS 必定已初始化）里
    //      取走暂存项并重放，同时**回读自证**。
    public readonly struct PendingChance
    {
        public readonly string Map; public readonly string Boss; public readonly int Value;
        public PendingChance(string map, string boss, int value) { Map = map; Boss = boss; Value = value; }
    }

    private static readonly List<PendingChance> _pending = new();
    private static readonly HashSet<string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private static bool _warnedNotReady;

    // ═══════════════ ★★★【P0 修复】ABPS v2.1.0 强类型结构兼容 ═══════════════
    //
    // 实机 DLL（**Acid's Bot Placement System v2.1.0**，用 .NET MetadataReader/反射读出的权威结构）：
    //   `ModConfig.Config.BossConfig` : `BossConfig`          ← 属性，**不再是 IDictionary / Dictionary**
    //     └ `BossConfig.<BossXxx>`   : `BossLocationInfo`     ← 19 个 boss 属性（另有 EqualityContract / Enable；
    //                                                           `Item` 是**默认索引器** `this[string]`，不是 boss 属性）
    //         └ `.SpawnChance`       : `ValidLocationInt`     ← **不再是 IDictionary**
    //             └ `.<MapName>`     : `Int32`                ← 13 张地图（另有 `Item` 索引器 `this[string]`）
    // ⇒ 旧代码的 `BossConfig is IDictionary` / `SpawnChance is IDictionary` 在 v2.1.0 上**全部落空**，
    //   上层日志因此只剩一句「bossChance 未生效 … 回读 -1」，分不清是"没这个 boss"、"没这张图"还是"类型改名"。
    //
    // 本版改**双路径**：
    //   · `is IDictionary` ⇒ **旧版 ABPS**，写入/还原逻辑保持原样（含"用 Defaults/Bosses.json 模板建条目"）；
    //   · 否则            ⇒ **v2.1.0 强类型**，按**显式映射表**用反射写属性。
    //   ⚠ 映射表**不能写成大小写转换**：`bigmap`→`Customs`、`rezervbase`→`Reserve`、`sandbox`→`GroundZero`
    //     —— 这 3 张图的 ABPS 属性名和 SPT location id **毫无字面关系**，猜就会静默写错图。
    //   失败一律 fail-open：返回 false + `LastFailure` 写明具体原因，永不抛异常。
    //   全程只改 ABPS 的**内存对象**，绝不碰它的任何文件。

    /// <summary>最近一次读写走的路径：`"新版ABPS"`（v2.1.0 强类型）/ `"旧版ABPS"`（字典）。供上层日志标注用。</summary>
    public static string LastMode { get; private set; }

    /// <summary>最近一次写 SpawnChance 失败的**具体原因**（成功 / 未尝试时为 null）。供上层日志标注用。</summary>
    public static string LastFailure { get; private set; }

    /// <summary>强类型路径下"结构性找不到"（无此属性 / 无此图）的警告去重键 ⇒ 每进程只提示一次，避免逐局刷屏。</summary>
    private static readonly HashSet<string> _warnedMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>★★★ 缺陷2：【ABPS 结构上不支持自定义角色】的说明去重键 ⇒ 每进程只打**一条 info**（不打 warning）。</summary>
    private static readonly HashSet<string> _notedUnsupportedBoss = new(StringComparer.OrdinalIgnoreCase);

    // ═══════════════ ★★★ 缺陷2 修复（2026-10-24）：ABPS 不支持自定义角色 ⇒ 静默降级 ═══════════════
    //
    // 实机日志逐字（每局都刷）：
    //   `[EventRotator][ABPS] ⚠ bossChance 未生效：laboratory bossScourge 目标 100，写入返回 False，回读 -1`
    //   `｜原因：BossConfig 是强类型但找不到属性 'BossScourge'`
    //
    // 反编译实证（`acidphantasm-botplacementsystem.dll` → `BotPlacementSystemServer.Models.BossConfig`）：
    //   · 它**只有一个强类型类**，19 个 `BossLocationInfo` 属性（BossKnight … Gifter），**没有任何
    //     按字符串键的 map / Dictionary**；
    //   · 唯一的字符串入口是默认索引器 `this[string key]`，且对未知键**直接
    //     `throw new KeyNotFoundException`** ⇒ 不能拿它当"字典"写自定义角色；
    //   · 反序列化时未知 JSON 属性也只是被忽略 ⇒ 改它的 config.json 同样无解。
    //   ⇒ 本 mod 的自建角色（`bossScourge` / `followerScourge`）**结构上就写不进 ABPS**。
    //
    // 这不是故障，是设计边界：ABPS 只管它自己那 19 个角色。而本 mod 的追加条目是**直接写
    // `LocationBase.BossLocationSpawn`（含 `BossChance`）**、由 ABPS 重建之后再补回的
    // （`ReconcileActiveEventBosses` / `ApplyOneEvent`）⇒ 自定义角色的概率走 **SPT 原生 bossChance**，
    // 本来就不经过 ABPS。
    // ⇒ 修法取「b)：跳过写入 + 静默降级」——**不再每局刷 ⚠**，改成每进程一条 info 说明原因与替代路径。

    /// <summary>
    /// 该角色在 ABPS 的 `BossConfig` 里**有没有可写位置**（缺陷2 的分类依据）。
    /// <para><c>true</c> = 有：旧版字典里已有此键 / 旧版能按 `Defaults/Bosses.json` 模板新建 /
    /// v2.1.0 强类型里**真有**这个属性。</para>
    /// <para><c>false</c> = 确定没有：**自定义角色**（如 `bossScourge` / `followerScourge`）—— ABPS 只认它
    /// 那 19 个强类型属性，索引器对未知键直接抛 `KeyNotFoundException`，写不进去。</para>
    /// <para><c>null</c> = 暂时判断不了（ABPS 未初始化 / 反射失败）⇒ 调用方保持原逻辑（暂存重放）。</para>
    /// </summary>
    public static bool? BossSlotInAbps(string boss)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(boss)) return false;
            if (!Probe() || _modConfigType == null) return null;
            var cfg = _modConfigType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (cfg == null) return null;
            var bcObj = cfg.GetType().GetProperty("BossConfig")?.GetValue(cfg);
            if (bcObj == null) return null;

            // 旧版 ABPS：字典（键=角色名原样）；没这个键时还能用它的模板现建 ⇒ 算"有位置"。
            if (bcObj is IDictionary bc)
                return bc.Contains(boss) || !string.IsNullOrEmpty(ReadBossTemplateJson(boss));

            // v2.1.0：强类型。`BossPropertyName` 对未知名字只是"首字母大写"的 best-effort 猜测，
            // 所以这里必须回**真实类型**上查属性是否存在（`bossScourge → BossScourge` 不存在 ⇒ false）。
            var propName = BossPropertyName(boss);
            if (string.IsNullOrEmpty(propName)) return false;
            var t = bcObj.GetType();
            var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance)
                 ?? t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return p != null && p.GetIndexParameters().Length == 0;
        }
        catch { return null; }
    }

    /// <summary>
    /// ★ 缺陷2：把"ABPS 不支持该自定义角色 ⇒ 按 SPT 原生 bossChance 生效"的说明**每进程只打一次**（info，不 warning）。
    /// </summary>
    public static void NoteUnsupportedBossOnce(string boss, string map)
    {
        try
        {
            if (!_notedUnsupportedBoss.Add(boss ?? "")) return;
            _log?.Info($"[EventRotator][ABPS] '{boss}' 是自定义角色：ABPS 的 BossConfig 是强类型类（19 个固定属性），"
                     + "没有该角色的可写位置（索引器对未知键会抛 KeyNotFoundException）"
                     + $" ⇒ **跳过写 ABPS 的概率**（{map}），改按 **SPT 原生 bossChance** 生效："
                     + "本 mod 追加条目时会把 events.json 的 `bossChance` 直接写进 `BossLocationSpawn.BossChance`"
                     + "（ABPS 重建列表之后再由 D2 幂等补写补回）。此说明每进程只打一次，不再逐局告警。");
        }
        catch { }
    }

    /// <summary>取走（并清空）"配置还没就绪时暂存"的概率写入请求。</summary>
    public static List<PendingChance> TakePendingChances()
    {
        var copy = new List<PendingChance>(_pending);
        _pending.Clear(); _pendingKeys.Clear();
        return copy;
    }

    /// <summary>★ v2.6.53：配置未就绪时暂存 + 只警告一次（原来每次调用都刷一条 warning，实测 780 条）。</summary>
    private static void Queue(string boss, string map, int value, string reason)
    {
        var pk = map + "|" + boss;
        if (_pendingKeys.Add(pk)) _pending.Add(new PendingChance(map, boss, value));
        if (_warnedNotReady) return;
        _warnedNotReady = true;
        _log?.Warning($"[EventRotator][ABPS] Config.BossConfig 尚未就绪（{reason}）⇒ 本次概率写入已**暂存**"
                    + "（不再逐条刷屏）；将在 ABPS 重建地图首领表的 postfix 里重放并**回读自证**");
    }

    public static void Init(ISptLogger<EventRotatorServer> log)
    {
        _log = log;
    }

    /// <summary>探测 ABPS 是否在场（只读，不修改它任何东西）。</summary>
    public static bool Probe()
    {
        if (_probed) return Detected;
        _probed = true;
        try
        {
            _modConfigType = AccessTools.TypeByName(TypeName) ?? FindType("ModConfig");
            if (_modConfigType == null) { Detected = false; return false; }

            // ABPS 的 mod 目录：ModConfig._modPath（只读）
            try
            {
                var f = _modConfigType.GetField("_modPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                ModDir = f?.GetValue(null) as string;
            }
            catch { }

            var cfg = _modConfigType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            Detected = cfg != null;
            if (Detected)
                _log?.Info($"[EventRotator] ABPS 在场 → 只读兼容模式（不修改它的任何文件/配置）dir={ModDir ?? "?"}");
            return Detected;
        }
        catch (Exception ex)
        {
            _log?.Warning($"[EventRotator] ABPS 探测失败: {ex.Message}");
            Detected = false;
            return false;
        }
    }

    private static Type FindType(string simpleName)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(x => x != null && x.Name == simpleName && x.Namespace != null && x.Namespace.Contains("BotPlacement"));
        }
        catch { return null; }
    }

    /// <summary>
    /// 只读：取 ABPS 配置里某 bot 在某图的 SpawnChance（拿不到返回 **-1**，永不抛异常）。仅用于诊断/回读自证。
    /// ★ 双路径：旧版 ABPS = `Dictionary`（键是 SPT location id）；v2.1.0 = 强类型对象（键是 ABPS 自己的属性名）。
    ///   字典路径**原样保留**（`bc.Contains(boss)` / `sc.Contains(map)`），强类型路径走显式映射表。
    /// </summary>
    public static int ReadSpawnChance(string boss, string map)
    {
        if (!Probe()) return -1;
        try
        {
            var cfg = _modConfigType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (cfg == null) return -1;
            var bcObj = cfg.GetType().GetProperty("BossConfig")?.GetValue(cfg);
            if (bcObj == null) return -1;

            object entry;
            if (bcObj is IDictionary bc)                        // ★ 旧版 ABPS：BossConfig 是字典（键=boss 名原样）
            {
                LastMode = "旧版ABPS";
                if (!bc.Contains(boss)) return -1;
                entry = bc[boss];
            }
            else                                                // ★ v2.1.0：BossConfig 是强类型对象
            {
                LastMode = "新版ABPS";
                entry = GetByPropName(bcObj, BossPropertyName(boss));
                if (entry == null) return -1;
            }
            if (entry == null) return -1;

            var scObj = GetByPropName(entry, "SpawnChance");
            if (scObj == null) return -1;

            if (scObj is IDictionary sc)                        // ★ 旧版 ABPS：SpawnChance 是字典（键=SPT location id）
                return sc.Contains(map) ? Convert.ToInt32(sc[map] ?? 0) : -1;

            // ★ v2.1.0：显式映射（bigmap→Customs / rezervbase→Reserve / sandbox→GroundZero …）
            var val = GetByPropName(scObj, MapPropertyName(map));
            return val == null ? -1 : Convert.ToInt32(val);
        }
        catch { return -1; }
    }

    /// <summary>
    /// ★★★ v0.1.5【关键修复】把某敌种在某图的 SpawnChance **写进 ABPS 的运行期配置对象**。
    ///
    /// 为什么要这么做（实测根因）：日志逐字 `<c>[只读诊断] ABPS sectantPriest.SpawnChance[woods] = 未就绪/无此键</c>`
    /// —— ABPS 接管刷怪后，本 mod 改 location 表的 `BossChance` **对它无效** ⇒
    /// 用户实测"走遍森林也没看见邪教徒"。
    ///
    /// 原则：**只改 ABPS 的内存对象，绝不碰它的任何文件**（与处理 PBS 装备同一原则）。
    /// 失败一律只记 warning，不影响其它维度。
    /// </summary>
    /// <summary>ABPS 是否在场（探测过一次后缓存）。</summary>
    public static bool IsPresent { get { try { return Probe(); } catch { return false; } } }

    /// <summary>
    /// 把某敌种在某图的 SpawnChance 写进 ABPS 的**运行期配置对象**（只改内存，绝不碰它的文件）。
    /// ★ 双路径（详见文件顶部 v2.1.0 结构注释）：
    ///   · `BossConfig is IDictionary` ⇒ **旧版 ABPS**：原逻辑（含用 Defaults/Bosses.json 模板建条目）+ `sc[map]=value`；
    ///   · 否则                       ⇒ **ABPS v2.1.0 强类型**：显式映射表 + 反射写 `BossConfig.BossX.SpawnChance.<Map>` 属性。
    /// 失败一律 fail-open（返回 false，不抛异常），并把**具体原因**写进 <see cref="LastFailure"/> 与 warning 日志。
    /// </summary>
    public static bool SetSpawnChance(string boss, string map, int value, System.Action<System.Action> addRestore)
    {
        if (!Probe() || _modConfigType == null) return false;
        LastFailure = null;
        try
        {
            var cfg = _modConfigType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (cfg == null)
            {
                // ★ v2.6.53：和"BossConfig 拿不到"同等对待 —— 暂存 + 只警告一次（不再逐条刷屏）
                Queue(boss, map, value, "Config 尚未赋值");
                LastFailure = "ABPS 的 Config 尚未赋值（ABPS 还没初始化完）";
                return false;
            }
            var bcProp = cfg.GetType().GetProperty("BossConfig");
            var bcObj = bcProp?.GetValue(cfg);
            if (bcObj == null)
            {
                // ★ v2.6.53：不再每次刷 warning（实测 780 条刷屏）—— 只警告一次，并把这次写入**暂存**，
                //   等 `BossWavePatch.ReplayAbpsBossChances()`（挂在 ABPS 自己的 postfix 上）重放。
                var why = bcProp == null ? "Config 类型上找不到 BossConfig 属性" : "BossConfig 的值为 null（尚未初始化）";
                Queue(boss, map, value, why);
                LastFailure = "BossConfig 尚未就绪：" + why;
                return false;
            }

            // ══════════ 路径 A：旧版 ABPS（BossConfig / SpawnChance 都是 Dictionary）══════════
            //   本分支**逐行保持原逻辑**（含"该敌种没有条目就用它的 Defaults/Bosses.json 模板现建一个"）。
            if (bcObj is IDictionary bc)
            {
                LastMode = "旧版ABPS";

                // 该敌种在 ABPS 里有没有条目？没有就用它的 Defaults/Bosses.json 现建一个（仍只读文件）
                if (!bc.Contains(boss))
                {
                    var tpl = ReadBossTemplateJson(boss);
                    if (string.IsNullOrEmpty(tpl))
                    {
                        LastFailure = $"'{boss}' 在 ABPS 里没有条目，也读不到模板（该敌种可能不归 ABPS 管）";
                        _log?.Warning($"[EventRotator][ABPS][旧版ABPS] {LastFailure} ⇒ 跳过");
                        return false;
                    }
                    object entry = null;
                    try { entry = System.Text.Json.JsonSerializer.Deserialize(tpl, bc.GetType().GetGenericArguments()[1]); } catch { }
                    if (entry == null)
                    {
                        LastFailure = $"反序列化 '{boss}' 的 Defaults/Bosses.json 模板失败";
                        _log?.Warning($"[EventRotator][ABPS][旧版ABPS] {LastFailure}");
                        return false;
                    }
                    var capBc = bc; var capBoss = boss;
                    addRestore?.Invoke(() => { try { capBc.Remove(capBoss); } catch { } });
                    bc[boss] = entry;
                    _log?.Info($"[EventRotator][ABPS][旧版ABPS] '{boss}' 原先不在 ABPS 配置里 ⇒ 已用它的 Defaults/Bosses.json 模板建条目（仅内存，可还原）");
                }

                var ent = bc[boss];
                var scProp = ent.GetType().GetProperty("SpawnChance");
                if (scProp?.GetValue(ent) is not IDictionary sc)
                {
                    LastFailure = $"'{boss}'.SpawnChance 不是字典（类型 {ent?.GetType().Name ?? "?"}）";
                    _log?.Warning($"[EventRotator][ABPS][旧版ABPS] {LastFailure}");
                    return false;
                }
                return WriteChanceToDict(sc, boss, map, value, addRestore);
            }

            // ══════════ 路径 B：ABPS v2.1.0（BossConfig / BossLocationInfo / ValidLocationInt 强类型对象）══════════
            LastMode = "新版ABPS";
            return SetSpawnChanceStrong(bcObj, boss, map, value, addRestore);
        }
        catch (Exception ex)
        {
            LastFailure = $"写 SpawnChance 抛异常：{ex.GetType().Name}: {ex.Message}";
            _log?.Warning($"[EventRotator][ABPS] 写 SpawnChance 失败: {ex.Message}");
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 【ABPS v2.1.0 强类型路径】+ 映射表 / 反射辅助
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>结构性失败（无此属性 / 无此图）——每进程只打一条 warning，避免 ABPS 每图重建都刷屏。</summary>
    private static void WarnMissingOnce(string key, string message)
    {
        if (!_warnedMissing.Add(key)) return;
        _log?.Warning(message + "（同因每进程只提示一次，避免逐局刷屏）");
    }

    /// <summary>
    /// 【ABPS v2.1.0 写入】`BossConfig.BossX.SpawnChance.<Map>` 全是强类型属性 ⇒ 反射写属性。
    ///   ① boss → `BossConfig` 属性（显式表 + 大小写不敏感兜底）
    ///   ② `BossLocationInfo.SpawnChance` → `ValidLocationInt` 对象
    ///   ③ map → `ValidLocationInt` 属性（显式表；**不是大小写转换**）
    ///   ④ 写前存原值登记还原闭包（还原同样走属性写回），写后回读自证
    /// 任何一步找不到 ⇒ 返回 false + <see cref="LastFailure"/> 写明具体原因（fail-open，不抛异常）。
    /// </summary>
    private static bool SetSpawnChanceStrong(object bcObj, string boss, string map, int value, Action<Action> addRestore)
    {
        // ⓪ ★★★ 缺陷2 修复：自定义角色（如 `bossScourge`）在 ABPS 的强类型 `BossConfig` 里**没有属性**，
        //   ABPS 也不接受未知键（`this[string]` 索引器对未知键直接抛 KeyNotFoundException）
        //   ⇒ 结构上写不进去。这里**静默降级**：不打 warning、每进程只留一条 info 说明
        //   "ABPS 不支持自定义角色，按 SPT 原生 bossChance 生效"（原生 bossChance 由本 mod
        //   追加条目时写进 `BossLocationSpawn.BossChance`，不经过 ABPS）。
        if (BossSlotInAbps(boss) == false)
        {
            LastFailure = $"ABPS 的 BossConfig 是强类型类、没有 '{boss}' 的可写位置（自定义角色不归 ABPS 管）"
                        + " ⇒ 按 SPT 原生 bossChance 生效";
            NoteUnsupportedBossOnce(boss, map);
            return false;
        }

        // ① boss → BossConfig 属性名
        var bossProp = BossPropertyName(boss);
        if (string.IsNullOrEmpty(bossProp))
        {
            LastFailure = $"boss 名 '{boss}' 无法映射到 BossConfig 的属性名";
            WarnMissingOnce("boss|" + boss, $"[EventRotator][ABPS][新版ABPS] {LastFailure} ⇒ 跳过");
            return false;
        }
        var bMiss = DescribeMiss(bcObj, bossProp);
        if (bMiss != null)
        {
            LastFailure = $"BossConfig 是强类型但找不到属性 '{bossProp}'（boss={boss}；{bMiss}）";
            WarnMissingOnce("boss|" + boss, $"[EventRotator][ABPS][新版ABPS] {LastFailure} ⇒ 跳过（该敌种可能不归 ABPS 管；不改它任何文件）");
            return false;
        }
        var entry = GetByPropName(bcObj, bossProp);

        // ② BossLocationInfo.SpawnChance（正式是 ValidLocationInt；容错"仍被反序列化成字典"的混合结构）
        var sMiss = DescribeMiss(entry, "SpawnChance");
        if (sMiss != null)
        {
            LastFailure = $"SpawnChance 类型未知: '{bossProp}.SpawnChance' 取不到（{sMiss}）";
            WarnMissingOnce("sc|" + boss, $"[EventRotator][ABPS][新版ABPS] {LastFailure} ⇒ 跳过");
            return false;
        }
        var scObj = GetByPropName(entry, "SpawnChance");
        if (scObj is IDictionary scDict)                     // 混合结构：按键写（语义同旧版，键=SPT location id）
            return WriteChanceToDict(scDict, boss, map, value, addRestore);

        // ③ map → ValidLocationInt 属性名
        //    ★ 必须查显式映射表：bigmap→Customs、rezervbase→Reserve、sandbox→GroundZero，转换大小写会**静默写错图**。
        var mapProp = MapPropertyName(map);
        if (string.IsNullOrEmpty(mapProp))
        {
            LastFailure = $"map '{map}' 没有到 ABPS '{scObj.GetType().Name}' 属性名的映射（映射表未覆盖该 location id）";
            WarnMissingOnce("map|" + map, $"[EventRotator][ABPS][新版ABPS] {LastFailure} ⇒ 跳过");
            return false;
        }
        var mMiss = DescribeMiss(scObj, mapProp);
        if (mMiss != null)
        {
            LastFailure = $"SpawnChance 是 {scObj.GetType().Name} 但找不到属性 '{mapProp}'（map={map}；{mMiss}）";
            WarnMissingOnce("prop|" + boss + "|" + map, $"[EventRotator][ABPS][新版ABPS] {LastFailure} ⇒ 跳过");
            return false;
        }

        // ④ 写前存原值 + 登记还原（同一个 (map,boss) 只保留**首次**的还原闭包，由调用方去重）
        var oldVal = GetByPropName(scObj, mapProp);
        var scIsStruct = scObj.GetType().IsValueType;        // ★ 若 SpawnChance 是 struct，反射拿到的是**装箱副本** ⇒ 写完必须塞回去
        void SyncBack()
        {
            if (!scIsStruct) return;
            try { TrySetByPropName(entry, "SpawnChance", scObj, out _); } catch { }
        }

        addRestore?.Invoke(() =>
        {
            try { if (TrySetByPropName(scObj, mapProp, oldVal, out _)) SyncBack(); } catch { }
        });

        if (!TrySetByPropName(scObj, mapProp, value, out var setWhy))
        {
            LastFailure = $"SpawnChance 是 {scObj.GetType().Name} 但属性 '{mapProp}' 写不进去：{setWhy}";
            _log?.Warning($"[EventRotator][ABPS][新版ABPS] {LastFailure}");
            return false;
        }
        SyncBack();

        // ⑤ 回读自证（属性 init-only / 被 record 深拷贝时，这里立刻能发现"写了等于没写"）
        var after = ReadSpawnChance(boss, map);
        if (after != value)
        {
            LastFailure = $"写入后回读 SpawnChance.{mapProp} = {after}（期望 {value}）⇒ 属性可能只读或被克隆";
            _log?.Warning($"[EventRotator][ABPS][新版ABPS] {LastFailure}");
            return false;
        }

        _log?.Info($"[EventRotator][ABPS][新版ABPS] {boss}.SpawnChance[{map}→{mapProp}]: "
                 + $"{(oldVal == null ? "?" : oldVal.ToString())} → {value}（强类型属性写回，未改它任何文件；可还原）");
        return true;
    }

    /// <summary>
    /// 【旧版 ABPS 写入】把概率写进 `SpawnChance` 字典（键是 SPT location id，**原样不改**），并登记还原闭包。
    /// 语句与原实现逐行等价，只是抽出来给"旧版"与"v2.1.0 里 SpawnChance 仍是字典的混合结构"共用。
    /// </summary>
    private static bool WriteChanceToDict(IDictionary sc, string boss, string map, int value, Action<Action> addRestore)
    {
        var keyType = sc.GetType().IsGenericType ? sc.GetType().GetGenericArguments()[0] : typeof(string);
        var valType = sc.GetType().IsGenericType ? sc.GetType().GetGenericArguments()[1] : typeof(int);
        object k = keyType == typeof(string) ? map : Convert.ChangeType(map, keyType);
        var had = sc.Contains(k);
        var old = had ? sc[k] : null;
        var capSc = sc; var capK = k; var capHad = had; var capOld = old;
        addRestore?.Invoke(() => { try { if (capHad) capSc[capK] = capOld; else capSc.Remove(capK); } catch { } });
        sc[k] = Convert.ChangeType(value, valType);
        _log?.Info($"[EventRotator][ABPS][旧版ABPS] {boss}.SpawnChance[{map}]: " + (had ? $"{old} → {value}" : $"（新建）→ {value}")
                 + "（写进 ABPS 运行期配置对象，未改它任何文件；可还原）");
        return true;
    }

    /// <summary>
    /// 本 mod 的 map（**SPT location id**）→ ABPS `ValidLocationInt` 的属性名。
    /// ★ 必须用**显式表**，不能做大小写转换：`bigmap`→`Customs`（海关）、`rezervbase`→`Reserve`（储备站）、
    ///   `sandbox`→`GroundZero`（中心区）—— 这 3 张图的 ABPS 属性名和 SPT id **毫无字面关系**，
    ///   猜/转换大小写都会**静默写错图**（回读还会"成功"，最难查）。
    /// 表覆盖 ABPS v2.1.0 `ValidLocationInt` 的 **13 张真实地图**（第 14 个 `Item` 是默认索引器，不是地图），另收常见别名。
    /// 未知 map 返回 null ⇒ 调用方 fail-open 跳过并打出具体原因。
    /// </summary>
    private static string MapPropertyName(string map)
    {
        if (string.IsNullOrEmpty(map)) return null;
        switch (map.Trim().ToLowerInvariant())
        {
            // ── 13 张真实地图（本 mod 使用 SPT location id）──
            case "bigmap":           return "Customs";        // ★ 海关：SPT id = bigmap
            case "factory4_day":     return "Factory4Day";
            case "factory4_night":   return "Factory4Night";
            case "interchange":      return "Interchange";
            case "laboratory":       return "Laboratory";
            case "lighthouse":       return "Lighthouse";
            case "rezervbase":       return "Reserve";        // ★ 储备站：SPT id = rezervbase
            case "sandbox":          return "GroundZero";     // ★ 中心区：SPT id = sandbox
            case "sandbox_high":     return "GroundZeroHigh";
            case "shoreline":        return "Shoreline";
            case "tarkovstreets":    return "TarkovStreets";
            case "woods":            return "Woods";
            case "labyrinth":        return "Labyrinth";
            // 注意：ABPS 的 `Item` 是**默认索引器**（`this[string]`，反射里 idx=1），不是普通地图属性
            //       ⇒ 这里刻意不映射；`GetByPropName`/`TrySetByPropName` 会拒绝索引器并 fail-open。
            // ── 别名（万一上层直接传 ABPS 名 / 短名；仍是显式表，不是大小写转换）──
            case "customs":          return "Customs";
            case "reserve":          return "Reserve";
            case "groundzero":
            case "ground_zero":      return "GroundZero";
            case "groundzerohigh":
            case "ground_zero_high": return "GroundZeroHigh";
            case "factory4day":      return "Factory4Day";
            case "factory4night":    return "Factory4Night";
            case "streets":          return "TarkovStreets";
            case "lab":              return "Laboratory";
            default:                 return null;
        }
    }

    /// <summary>
    /// 本 mod 的 boss 名 → ABPS v2.1.0 `BossConfig` 的属性名（**显式表** + 大小写不敏感兜底）。
    /// 前 12 条是活动实际用到的首领；后面补齐 `BossConfig` 的其余属性名（*Agro / arenaFighterEvent / pmcBot …），
    /// 口径与旧版字典路径一致：**只要 ABPS 里有这个条目，旧版能写的，新版也能写**。
    /// 未知名字按"首字母大写"best-effort 返回，最终由 `GetByPropName` 的大小写不敏感兜底裁决。
    /// </summary>
    private static string BossPropertyName(string boss)
    {
        if (string.IsNullOrEmpty(boss)) return null;
        var t = boss.Trim();
        if (t.Length == 0) return null;

        switch (t.ToLowerInvariant())
        {
            // ── 12 个真实首领（活动配置里用到的名字）──
            case "bosskilla":         return "BossKilla";
            case "bossboar":          return "BossBoar";
            case "bosskolontay":      return "BossKolontay";
            case "bossgluhar":        return "BossGluhar";
            case "bosssanitar":       return "BossSanitar";
            case "bosskojaniy":       return "BossKojaniy";
            case "bosstagilla":       return "BossTagilla";
            case "bosszryachiy":      return "BossZryachiy";
            case "bossknight":        return "BossKnight";
            case "bossbully":         return "BossBully";
            case "bosspartisan":      return "BossPartisan";
            case "sectantpriest":     return "SectantPriest";
            // ── BossConfig 的其余属性（ABPS 自己会刷的其它首领/事件敌种）──
            case "bosstagillaagro":   return "BossTagillaAgro";
            case "bosskillaagro":     return "BossKillaAgro";
            case "tagillahelperagro": return "TagillaHelperAgro";
            case "arenafighterevent": return "ArenaFighterEvent";
            case "pmcbot":            return "PmcBot";
            case "exusec":
            case "exusecs":           return "ExUsec";
            case "gifter":            return "Gifter";
        }

        // 未知名字：首字母大写 best-effort（如 sectantOni → SectantOni），仍由 GetByPropName 兜底；找不到属性就 fail-open。
        return char.ToUpperInvariant(t[0]) + t.Substring(1);
    }

    /// <summary>
    /// 按属性名取值：① 精确匹配 → ② `BindingFlags.IgnoreCase` 兜底（"双重兜底"）。
    /// 取不到 / 异常一律返回 **null**（调用方按 fail-open 处理）。
    /// </summary>
    private static object GetByPropName(object obj, string name)
    {
        if (obj == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                 ?? t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null || p.GetIndexParameters().Length > 0) return null;
            return p.GetValue(obj);
        }
        catch { return null; }
    }

    /// <summary>
    /// 诊断用：解释"为什么按属性名取不到值"（返回 **null = 取到了**）。
    /// 只影响日志措辞，不影响任何功能，让失败原因能区分「没有该属性」/「值是 null」/「不可读」/「读取异常」。
    /// </summary>
    private static string DescribeMiss(object obj, string name)
    {
        if (obj == null) return "目标对象为 null";
        if (string.IsNullOrEmpty(name)) return "属性名为空（映射表未覆盖）";
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                 ?? t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null) return $"没有该属性（类型 {t.Name}）";
            if (p.GetIndexParameters().Length > 0) return $"{t.Name}.{p.Name} 是索引器（不是普通属性）";
            if (!p.CanRead) return $"{t.Name}.{p.Name} 不可读";
            var v = p.GetValue(obj);
            return v == null ? $"{t.Name}.{p.Name} 的值为 null" : null;
        }
        catch (Exception ex) { return $"{obj.GetType().Name}.{name} 读取异常：{ex.GetType().Name}"; }
    }

    /// <summary>
    /// 按属性名**写**值（同样"精确 → IgnoreCase"双兜底）；`CanWrite=false` 时兜底写编译器生成的
    /// `&lt;X&gt;k__BackingField`（record 的 `{ get; init; }` 在个别实现上会报只读）。
    /// 失败返回 false 并给出具体原因，**不抛异常**。
    /// </summary>
    private static bool TrySetByPropName(object obj, string name, object value, out string reason)
    {
        reason = null;
        if (obj == null) { reason = "目标对象为 null"; return false; }
        if (string.IsNullOrEmpty(name)) { reason = "属性名为空（映射表未覆盖）"; return false; }
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                 ?? t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null) { reason = $"强类型 {t.Name} 里找不到属性 '{name}'"; return false; }
            if (p.GetIndexParameters().Length > 0) { reason = $"{t.Name}.{p.Name} 是索引器"; return false; }

            if (p.CanWrite)
            {
                var v = Coerce(value, p.PropertyType, out var vErr);
                if (vErr != null) { reason = $"{t.Name}.{p.Name} 值不兼容：{vErr}"; return false; }
                p.SetValue(obj, v);
                return true;
            }

            var bf = t.GetField("<" + p.Name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (bf == null) { reason = $"{t.Name}.{p.Name} 只读（无 setter、无 backing field）"; return false; }
            var fv = Coerce(value, bf.FieldType, out var fErr);
            if (fErr != null) { reason = $"{t.Name}.{p.Name} 值不兼容：{fErr}"; return false; }
            bf.SetValue(obj, fv);
            return true;
        }
        catch (Exception ex) { reason = $"{obj.GetType().Name}.{name} 写入异常：{ex.GetType().Name}: {ex.Message}"; return false; }
    }

    /// <summary>写属性前的类型适配（正常情况都是 `Int32`；这里只兜"目标不是 int / 可空 int"的极端结构）。</summary>
    private static object Coerce(object value, Type target, out string error)
    {
        error = null;
        if (value == null)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) == null) { error = $"目标类型 {target.Name} 不接受 null"; return null; }
            return null;
        }
        if (target.IsInstanceOfType(value)) return value;
        try { return Convert.ChangeType(value, target); }
        catch (Exception ex) { error = $"{ex.GetType().Name}: 无法把 {value.GetType().Name} 转成 {target.Name}"; return value; }
    }

    /// <summary>
    /// 【只读模板源】从 ABPS 的 Defaults/Bosses.json 取某敌种的标准条目，返回**单个对象的 JSON**。
    /// 用它的确切数据（zone/escort/难度）作为追加条目的模板 —— 只读文件，不改 ABPS 任何东西。
    ///
    /// 为什么需要：ABPS 的 <c>MapSpawns.ConfigureInitialData</c> 会清空所有地图的
    /// BossLocationSpawn，所以本 mod 在 raid 阶段采集模板时是**空的**，
    /// 只能退化成"裸条目"（zone 为空 → 随机撒全图、escort 猜错）。
    /// </summary>
    public static string ReadBossTemplateJson(string bossName)
    {
        try
        {
            var dir = ModDir;
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return null;
            var f = System.IO.Path.Combine(dir, "Defaults", "Bosses.json");
            if (!System.IO.File.Exists(f)) return null;

            var txt = System.IO.File.ReadAllText(f);
            var key = "\"" + bossName + "Data\"";
            var i = txt.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var lb = txt.IndexOf('[', i);
            if (lb < 0) return null;

            int depth = 0, end = -1;
            for (int p = lb; p < txt.Length; p++)
            {
                if (txt[p] == '[') depth++;
                else if (txt[p] == ']') { depth--; if (depth == 0) { end = p; break; } }
            }
            if (end < 0) return null;
            var arr = txt.Substring(lb, end - lb + 1);

            var ob = arr.IndexOf('{');
            if (ob < 0) return null;
            int d2 = 0, oe = -1;
            for (int p = ob; p < arr.Length; p++)
            {
                if (arr[p] == '{') d2++;
                else if (arr[p] == '}') { d2--; if (d2 == 0) { oe = p; break; } }
            }
            if (oe < 0) return null;
            return arr.Substring(ob, oe - ob + 1);
        }
        catch (Exception ex) { _log?.Warning($"[EventRotator] 读取 ABPS 模板失败: {ex.Message}"); return null; }
    }
}
