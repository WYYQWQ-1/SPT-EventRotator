using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ 自定义 bot 类型 `bossScourge`（灾厄小队冠军）+ `followerScourge`（灾厄小队小弟）的服务端注册。
///
/// <para><b>为什么必须自己加载</b>：SPT 4.1.6 **没有**"自动合并 mod 的 <c>db/</c> 目录"这回事 ——
/// 反编译实证：
/// <list type="bullet">
///   <item>唯一读数据库的入口 <c>SPTarkov.Server.Helpers.DatabaseImporter.LoadDatabaseAsync</c>
///     只扫 <c>./SPT_Data/database/</c>（本机 SPT.Server.dll 反编译 :1383）；</item>
///   <item>mod 加载器 <c>SPTarkov.Server.Modding.ModLoader</c> 只把每个 mod 目录里的 <c>*.dll</c> 载入
///     （:645-663），**从不读 mod 目录下的 json**；</item>
///   <item>BlackDiv / ISB 的 <c>db/bots/types/*.json</c> 能生效，是因为它们在 <c>IOnLoad</c> 里调用
///     **自己的库**读盘并写进 <c>BotTable.Types</c>（BlackDivServer.dll：<c>moreBotsLib.LoadBots(assembly)</c>；
///     ISB：<c>moreBotsLib.LoadBotsShared(assembly,…)</c>），不是 SPT 原生行为。</item>
/// </list>
/// 所以本 mod 自己读 <c>&lt;mod&gt;/db/bots/types/*.json</c>，反序列化成 SPT 运行期的 <c>BotType</c>
/// （用 DI 注入的 <c>JsonUtil</c>，它带全部自定义转换器），再写进 <c>BotTable.Types</c>。</para>
///
/// <para><b>键名</b>：SPT 的 <c>BotHelper.GetBotTemplate(role)</c> 用 <c>role.ToLowerInvariant()</c> 查
/// <c>BotTable.Types</c>（BotHelper.cs:33-41），系统自带文件的文件名都是小写 ⇒ 本加载器也写**小写键**。
/// 文件名（去掉 .json）就是 role：这与黑狐 <c>bossWedge.json</c>（文件里没有 <c>_id</c>）的写法一致。</para>
///
/// <para><b>与枚举的关系</b>：本类只负责 bot 数据表。服务端 <c>WildSpawnType</c> 枚举由 SPT 原生
/// enum prepatcher 注册（<c>HasPrepatcher=true</c> + <c>user/patchers/&lt;ModGuid&gt;/EventRotatorEnums.json</c>）；
/// 这一步是**必须的**：<c>BotController.GetAllBotDifficulties()</c> 遍历的是**服务端枚举值**，
/// 只有枚举里有该角色，客户端拿到的难度表里才会有对应小写名（否则客户端
/// <c>DifficultyManager.Get</c> 直接 KeyNotFound）。这里顺带逐角色自检并打日志。</para>
///
/// <para><b>★ 四个角色（2026-10-29 第 11 项再扩展）</b>：<c>ScourgeRoles</c> 之外新增
/// <see cref="CleanupCrewRole"/>(13792) / <see cref="PillagerRole"/>(13793)，统一走 <see cref="CustomRoles"/> 表；
/// 数据文件是 <c>arenaFighterEvent</c> 的逐字拷贝（这一版只求「名字对」，不换皮）。
/// <b>2026-10-29 线上修正</b>：这三个角色也必须 <c>IsBoss=true</c>（进 <c>BotConfig.Bosses</c>），
/// 否则客户端拿不到它们的 boss 身份（证据见 <see cref="RoleDef.IsBoss"/> 注释）。</para>
///
/// <para><b>★ 两个角色（2026-10-23 扩展）</b>：本类从"只处理冠军"改成**列表驱动**，
/// <see cref="ScourgeRoles"/> = { <c>bossScourge</c>(13790), <c>followerScourge</c>(13791) }。
/// 数据文件：<c>db/bots/types/bossScourge.json</c> / <c>followerScourge.json</c>（都是
/// <c>arenafighterevent.json</c> 的逐字拷贝，只改 <c>health</c> 档位）。
/// 按角色分键的两张表（<c>Equipment</c> / <c>ItemSpawnLimits</c>）**每个角色都要一份**，
/// 键必须 `role.ToLowerInvariant()`；<c>BotConfig.Bosses</c> **只登记冠军**（小弟不是首领）。</para>
/// </summary>
///
/// <para><b>★ 借用标注（2026-10-29 加）</b>：本文件里因为参考 <c>danauraborealis/Scourge</c>
/// （作者 Manimal1920，sp-mod.com/mod/3196）而写成的那几处，全部用
/// <c>// [借·待重写] SCOURGE</c> 标出，方便将来一次性剥离：</para>
/// <list type="bullet">
///   <item><b>借的</b>：<c>MergeRoleEquipmentConfig</c> 及其调用点
///     ——「每个角色的装备过滤器放在 <c>db/bots/config/&lt;role&gt;.json</c> 的 <c>equipment</c> 段，运行期叠加」
///     这个做法和他的文件布局。</item>
///   <item><b>借的</b>：<c>EnsurePresetBatch</c> 及其调用点
///     —— <c>presetBatch = 5（冠军）/ 10（小弟）</c> 这两个取值直接照抄他的 config。</item>
/// </list>
/// <para><b>不是借的（独立实现，剥离时不用动）</b>：SPT「不合并 mod 的 db 目录」的逆向结论、
/// enum prepatch 自检（<c>VerifyServerEnum</c>）、从 <c>arenafighterevent</c> 克隆
/// <c>Equipment</c>/<c>ItemSpawnLimits</c>、<c>BotConfig.Bosses</c> 登记、键名大小写规则。
/// 这些是实机报错驱动出来的，跟他的实现无关。</para>
/// <para>彻底剥离的具体步骤见 <c>audit/借用标注-灾厄小队.md</c>。</para>
internal static class CustomBotTypes
{
    /// <summary>冠军的角色名（= db 文件名、= 客户端/服务端枚举名）。</summary>
    public const string BossRole = "bossScourge";

    /// <summary>小弟（护卫）的角色名（= db 文件名、= 客户端/服务端枚举名）。</summary>
    public const string FollowerRole = "followerScourge";

    /// <summary>清洁队（Cleanup Crew）的角色名 —— 第 11 项契约：小写 <c>cleanupcrew</c>、枚举 <c>13792</c>。</summary>
    public const string CleanupCrewRole = "cleanupcrew";

    /// <summary>劫掠者（Pillager）的角色名 —— 第 11 项契约：小写 <c>pillager</c>、枚举 <c>13793</c>。</summary>
    public const string PillagerRole = "pillager";

    /// <summary>第 11 项：清洁队枚举值。三处必须一致（本类 / patchers/EventRotatorEnums.json / 客户端 patcher）。</summary>
    public const int CleanupCrewEnumValue = 13792;

    /// <summary>第 11 项：劫掠者枚举值。三处必须一致（同上）。</summary>
    public const int PillagerEnumValue = 13793;

    /// <summary>第 11 项追加：寻血猎犬（Bloodhound）的角色名 —— 契约：小写 <c>bloodhound</c>、枚举 <c>13794</c>。</summary>
    public const string BloodhoundRole = "bloodhound";

    /// <summary>第 11 项追加：寻血猎犬枚举值。三处必须一致（本类 / patchers/EventRotatorEnums.json / 客户端 patcher）。</summary>
    public const int BloodhoundEnumValue = 13794;

    // ══════════════════ ★ 第 9 项（Arms Race · 2024 军备竞赛）两个军火商 ══════════════════
    // 契约（与客户端那条线钉死，逐字）：
    //   role=gus      枚举=13795  locale=ScavRole/Gus        en=Gus      ch=古斯
    //   role=basmach  枚举=13796  locale=ScavRole/Basmach    en=Basmach  ch=巴斯马赫
    // 克隆源（**不是猜的**）：PPES `Factions/FactionCatalog.cs:35-38`
    //   `new(731004, Gus,      "PpesGus",     "ppes_gus",     ["followerkolontaysecurity", "pmcbot"], "Gus",     …)`
    //   `new(731005, Basmach,  "PpesBasmach", "ppes_basmach", ["followerkolontaysecurity", "pmcbot"], "Basmach", …)`
    //   ⇒ `Sources` 第一优先 = **followerkolontaysecurity**（Kolontay 的保镖），第二兜底 pmcbot。
    //   本机 `database/bots/types/followerkolontaysecurity.json` 存在（122,988 B）⇒ 用它。
    //   两角色与 Kolontay 的护卫同源，正是官方「军火商带 Kolontay 护卫」的语义。

    /// <summary>第 9 项：军火商「古斯」角色名（小写 = db 文件名 = 枚举名 = BotHelper 查表口径）。</summary>
    public const string GusRole = "gus";

    /// <summary>第 9 项：军火商「古斯」枚举值。三处必须一致（本类 / patchers / 客户端 patcher）。</summary>
    public const int GusEnumValue = 13795;

    /// <summary>第 9 项：军火商「巴斯马赫」角色名。</summary>
    public const string BasmachRole = "basmach";

    /// <summary>第 9 项：军火商「巴斯马赫」枚举值。三处必须一致。</summary>
    public const int BasmachEnumValue = 13796;

    /// <summary>
    /// 一个自定义角色的**全部注册参数**（列表驱动：加角色 = 这里加一条 + 枚举表加一条 + locale 加一条 + db 放一个 json）。
    /// 第 11 项的两个角色（cleanupcrew / pillager）与灾厄小队走同一条路：
    /// 数据表从 <c>arenaFighterEvent</c> 克隆（<c>db/bots/types/&lt;role&gt;.json</c>），
    /// 装备/外观保持克隆源（这一版目标是"名字对"，不是换皮）。
    /// </summary>
    public sealed class RoleDef
    {
        /// <summary>db 文件名 / 服务端-客户端枚举名（**小写**：BotHelper 查表用 role.ToLowerInvariant()）。</summary>
        public string Role;
        /// <summary>WildSpawnType 枚举值（由 enum prepatch 注入）。</summary>
        public int EnumValue;
        /// <summary>
        /// 是否登记进 <c>BotConfig.Bosses</c>。
        /// <para>★ 2026-10-29 线上实测修正：三个新角色（cleanupcrew / pillager / bloodhound）最初写成 false，
        /// 结果**游戏里完全不刷**；改成 true 后与灾厄冠军同口径。</para>
        /// <para>IL 依据：<c>BotConfig.Bosses</c> 全库只有 3 个读者 —— <c>BotHelper.IsBotBoss</c>
        /// （只被 <c>DurabilityLimitsHelper.GetDurabilityRole</c> 用来选耐久档）、
        /// <c>InRaidController.GetBossTypes</c>（把列表原样回给客户端路由 <c>/singleplayer/bosstypes</c>）、
        /// <c>PrintMembers</c>。而客户端 <c>spt-custom.dll</c> 的
        /// <c>SPT.Custom.Patches.CustomAiPatch._bossTypes</c> 就是这个路由的返回，
        /// 在 <c>StandartBotBrain.Activate</c> 的 PatchPrefix（IL 0x014C）里用
        /// <c>_bossTypes.Contains(role)</c> 决定**要不要给该角色生成 BossLogic**
        /// （不在列表里 ⇒ 跳过 boss 逻辑分支，日志 <c>lacked BossLogic, generating</c> 那条路永远不会走）。</para>
        /// </summary>
        public bool IsBoss;
        /// <summary><c>BotConfig.PresetBatch</c>：灾厄小队沿用上游 5/10；第 11 项两个角色用克隆源 arenaFighterEvent 的 15。</summary>
        public int PresetBatch;
        /// <summary>
        /// 该角色在 <c>BotConfig.Equipment</c> / <c>ItemSpawnLimits</c> 里的**克隆源**（小写）。
        /// <para>空 / null = 沿用老口径 <c>arenafighterevent</c>（第 11 项三个角色都是它）。</para>
        /// <para>★ 第 9 项（gus / basmach）必须显式写 <c>followerkolontaysecurity</c>：PPES
        /// <c>FactionCatalog.cs</c> 给这两个角色配的 <c>Sources</c> 就是它（第一优先），
        /// 而装备过滤器表按角色分键 ⇒ 用 arenaFighterEvent 的过滤器会与 bot json 的装备链对不上。</para>
        /// </summary>
        public string ConfigCloneSource;
        public string DisplayEn;
        public string DisplayZh;
        /// <summary>SAIN 注册用的 Description（英文，SAIN 面板显示）。</summary>
        public string Description;
    }

    /// <summary>本 mod 注册的**全部**自定义角色（列表驱动）。</summary>
    public static readonly RoleDef[] CustomRoles =
    {
        new()
        {
            Role = BossRole, EnumValue = BossEnumValue, IsBoss = true, PresetBatch = 5,
            DisplayEn = "Scourge", DisplayZh = "灾祸",
            Description = "EventRotator custom Scourge boss (WildSpawnType 13790).",
        },
        new()
        {
            Role = FollowerRole, EnumValue = FollowerEnumValue, IsBoss = false, PresetBatch = 10,
            DisplayEn = "Scourge's guard", DisplayZh = "灾祸小队护卫",
            Description = "EventRotator custom Scourge follower (WildSpawnType 13791).",
        },
        new()
        {
            Role = CleanupCrewRole, EnumValue = CleanupCrewEnumValue, IsBoss = true, PresetBatch = 15,
            DisplayEn = "Cleanup Crew", DisplayZh = "清场小队",
            Description = "EventRotator custom Cleanup Crew (WildSpawnType 13792, clone of arenaFighterEvent).",
        },
        new()
        {
            Role = PillagerRole, EnumValue = PillagerEnumValue, IsBoss = true, PresetBatch = 15,
            DisplayEn = "Pillager", DisplayZh = "劫掠者",
            Description = "EventRotator custom Pillager (WildSpawnType 13793, clone of arenaFighterEvent).",
        },
        new()
        {
            Role = BloodhoundRole, EnumValue = BloodhoundEnumValue, IsBoss = true, PresetBatch = 15,
            DisplayEn = "Bloodhound", DisplayZh = "寻血猎犬",
            Description = "EventRotator custom Bloodhound (WildSpawnType 13794, clone of arenaFighterEvent).",
        },
        // ★ 第 9 项（Arms Race）：两个军火商。克隆源 = followerkolontaysecurity（PPES FactionCatalog.cs:35-38 的
        //   Sources 第一优先），presetBatch 取该源的 10（本机 configs/bot.json → presetBatch.followerKolontaySecurity = 10）。
        //   ⚠ IsBoss 口径：**与清洁队保持现状一致（true）**。理由（实机证据，见 RoleDef.IsBoss 注释）：
        //   第 11 项三个角色最初写 false 时「游戏里完全不刷」，改 true 后才正常。
        //   两个军火商是走 BossLocationSpawn + 护卫刷出来的，同属这一档 ⇒ 跟清洁队一致取 true。
        new()
        {
            Role = GusRole, EnumValue = GusEnumValue, IsBoss = true, PresetBatch = 10,
            ConfigCloneSource = "followerkolontaysecurity",
            DisplayEn = "Gus", DisplayZh = "古斯",
            Description = "EventRotator custom arms dealer Gus (WildSpawnType 13795, clone of followerKolontaySecurity).",
        },
        new()
        {
            Role = BasmachRole, EnumValue = BasmachEnumValue, IsBoss = true, PresetBatch = 10,
            ConfigCloneSource = "followerkolontaysecurity",
            DisplayEn = "Basmach", DisplayZh = "巴斯马赫",
            Description = "EventRotator custom arms dealer Basmach (WildSpawnType 13796, clone of followerKolontaySecurity).",
        },
    };

    /// <summary>灾厄小队专用的两个角色（<see cref="WttRigLayoutCompat"/> 只该处理这两个的装备池）。</summary>
    public static readonly string[] ScourgeRoles = { BossRole, FollowerRole };

    /// <summary>冠军枚举数值。必须与 <c>Patcher/Patcher.cs</c>、<c>user/patchers/.../EventRotatorEnums.json</c> 三处一致。
    /// 选 13790：避开 ISB(13701-13716)、BlackDiv(848420-848426)、MoreBotsAPI(1000+ 自增)。</summary>
    public const int BossEnumValue = 13790;

    /// <summary>小弟枚举数值。三处必须一致（同上），取冠军 +1 = 13791。</summary>
    public const int FollowerEnumValue = 13791;

    /// <summary>由 <c>EventRotatorServer.OnLoadAsync</c> 调用（必须在 RestoreThenApply 之前，
    /// 否则 <c>botHealthOverride</c> 找不到这两个角色）。</summary>
    public static void Install(string modPath, object botTable, object botConfig, object jsonUtil, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            VerifyServerEnum(botTable, log);

            var types = Prop(botTable, "Types") as IDictionary;
            if (types == null)
            {
                log?.Warning("[EventRotator][自定义bot类型] ⚠ 拿不到 BotTable.Types ⇒ 跳过（bossScourge / followerScourge 将不可用）");
                return;
            }

            var dir = Path.Combine(modPath ?? "", "db", "bots", "types");
            if (!Directory.Exists(dir))
            {
                log?.Warning($"[EventRotator][自定义bot类型] ⚠ 缺少目录 {dir} ⇒ 跳过");
                return;
            }

            var valueType = types.GetType().IsGenericType
                ? types.GetType().GetGenericArguments().LastOrDefault()
                : null;
            if (valueType == null)
            {
                log?.Warning("[EventRotator][自定义bot类型] ⚠ 拿不到 BotTable.Types 的值类型 ⇒ 跳过");
                return;
            }

            int added = 0;
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var role = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(role)) continue;
                var key = role.ToLowerInvariant();        // 与 BotHelper.GetBotTemplate 的查找口径一致
                if (types.Contains(key))
                {
                    log?.Info($"[EventRotator][自定义bot类型] '{key}' 已存在（其它 mod/数据已注册）⇒ 保留原值，不覆盖");
                    continue;
                }

                object botType = null;
                try { botType = Deserialize(jsonUtil, file, valueType); }
                catch (Exception ex)
                {
                    log?.Warning($"[EventRotator][自定义bot类型] ⚠ 反序列化 {Path.GetFileName(file)} 失败: {(ex.InnerException ?? ex).Message}");
                }
                if (botType == null)
                {
                    log?.Error($"[EventRotator][自定义bot类型] ❌ {Path.GetFileName(file)} 解析失败或 JsonUtil 不可用 ⇒ 跳过");
                    continue;
                }

                types[key] = botType;
                added++;
                log?.Info($"[EventRotator][自定义bot类型] ✔ 已注册 '{key}' ← {Path.GetFileName(file)}（SPT 4.1.6 不会自动合并 mod 的 db 目录，本 mod 自读）");
            }
            // ★★★ 关键补丁（2026-10-23 实机报错驱动）：
            //   只往 `BotTable.Types` 加类型不够 —— `BotInventoryGenerator` 还会按角色去
            //   `BotConfig.Equipment[role]` 取装备过滤器；缺条目会抛
            //   `KeyNotFoundException: 'bossscourge' was not present`，结果是**每个 bot 生成都失败、整波不刷**
            //   （实机表现：进实验室一个 AI 都没有）。实机日志逐字：
            //   `Bot Equipment generation failed, unable to find equipment filters for: bossscourge`
            //   ⇒ 用源角色 `arenafighterevent` 深拷贝一份，`equipment` / `itemSpawnLimits` 两张按角色分键的表都补。
            int cfgAdded = CloneRoleConfigs(botConfig, jsonUtil, log, modPath);
            // [借·待重写] SCOURGE ★★★ 借自 danauraborealis/Scourge 的 `data/db/bots/config/*.json`（presetBatch=5 / 10）：
            //   SPT 的 `BotConfig.PresetBatch` 是 **`Dictionary<string,int>`，键 = 枚举名（camelCase）**，
            //   内置 bot.json 里就是 "arenaFighter"/"bossBoar"/"assault" 这种写法。
            //   反编译读点：`BotController.GetBotPresetGenerationLimit(type)`（BotController.cs:38-47）
            //   `if (!botConfig.PresetBatch.TryGetValue(type, out var value)) { 警告... return 10; }`，
            //   调用点 :266 `BotCountToGenerate = Math.Max(GetBotPresetGenerationLimit(role), condition.Limit)`。
            //   ⇒ 缺键只是「每个 bot 只预生成 10 套装备」并刷一条告警
            //     （实机逐字：`找不到要为机器人生成的预设数: bossScourge, 默认设值为 30`），不致命但该补。
            EnsurePresetBatch(botConfig, log);   // [借·待重写] SCOURGE presetBatch 5/10 取值来自他的 config
            // ★★★ 2026-10-24 实机 bug：搜尸时「战术胸挂」越搜越多（同一件被客户端反复新建视图）。
            //   根因不在本 mod / SPT / PBS，而是 WTT-ContentBackport 2.0.4 的 update9-20-26 新弹挂
            //   **缺 rig layout**（RigLayoutName 指向的预制体不在盘上）；2.0.5 的提交 2f54edf0 才补上。
            //   ⇒ 这里做**带版本/能力探测**的规避：WTT 没修才把这几款坏弹挂从 Scourge 装备池里换掉；
            //     别人的 WTT ≥ 2.0.5（或已带 arena_s3_layouts.bundle / 修好的 vest_config）就**完全不加载**。
            WttRigLayoutCompat.Apply(modPath, botTable, log);
            log?.Info($"[EventRotator][自定义bot类型] 本次新增 {added} 个；"
                      + string.Join("；", CustomRoles.Select(r => Describe(types, r.Role.ToLowerInvariant())))
                      + $"；按角色配置补了 {cfgAdded} 项");
        }
        catch (Exception ex)
        {
            log?.Error($"[EventRotator][自定义bot类型] 注册失败: {ex}");
        }
    }

    /// <summary>用 SPT 注入的 JsonUtil（带 ListOrT/Temperature 等自定义转换器）反序列化成运行期 BotType。</summary>
    private static object Deserialize(object jsonUtil, string file, Type type)
    {
        if (jsonUtil == null) return null;
        var t = jsonUtil.GetType();

        var m = t.GetMethod("DeserializeFromFile", new[] { typeof(string), typeof(Type) });
        if (m != null) return m.Invoke(jsonUtil, new object[] { file, type });

        var m2 = t.GetMethod("Deserialize", new[] { typeof(string), typeof(Type) });
        if (m2 != null) return m2.Invoke(jsonUtil, new object[] { File.ReadAllText(file), type });

        return null;
    }

    private static string Describe(IDictionary types, string key)
        => types.Contains(key)
            ? $"BotTable.Types['{key}'] = OK"
            : $"BotTable.Types['{key}'] = **缺失**";

    /// <summary>自检：服务端运行期 WildSpawnType 枚举里是否有本 mod 的**两个**角色（由 SPT enum prepatcher 注入）。
    /// 没有的话服务端不会给客户端下发该角色的难度表 ⇒ 客户端生成该 bot 时会崩，这里提前报清楚。</summary>
    private static void VerifyServerEnum(object botTable, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            const string enumFullName = "SPTarkov.Server.Core.Models.Eft.Common.WildSpawnType";
            var asm = botTable?.GetType().Assembly;   // ★ 运行期（可能是 Core.Patched.dll），不要用编译期 typeof
            var enumType = asm?.GetType(enumFullName)
                           ?? AppDomain.CurrentDomain.GetAssemblies()
                                .Select(a => { try { return a.GetType(enumFullName); } catch { return null; } })
                                .FirstOrDefault(x => x != null);

            if (enumType == null)
            {
                log?.Warning($"[EventRotator][自定义bot类型] 找不到服务端枚举 {enumFullName}，无法自检");
                return;
            }
            var names = Enum.GetNames(enumType);
            foreach (var def in CustomRoles)
            {
                var role = def.Role;
                if (names.Any(n => string.Equals(n, role, StringComparison.OrdinalIgnoreCase)))
                {
                    // 第 11 项自检口径：名字在枚举里 **且数值一致** 才算真的注册对
                    var actual = Convert.ToInt64(Enum.Parse(enumType, role, true), CultureInfo.InvariantCulture);
                    log?.Info($"[EventRotator][自定义bot类型] 服务端枚举 {enumType.FullName}.{role} = {actual} "
                              + (actual == def.EnumValue
                                    ? "✔（enum prepatch 生效，数值与契约一致）"
                                    : $"⚠（期望 {def.EnumValue}，实际 {actual} —— 检查 EventRotatorEnums.json）"));
                }
                else
                    log?.Error($"[EventRotator][自定义bot类型] ❌ 服务端枚举里没有 '{role}'！" +
                               " 检查 ①package 里的 EventRotatorMetadata.HasPrepatcher=true ②" +
                               " SPT_Runtime/user/patchers/com.kirisame.eventrotator/EventRotatorEnums.json 是否存在。" +
                               " 缺它 ⇒ BotController.GetAllBotDifficulties() 不会下发该角色难度 ⇒ 客户端生成该 bot 时会失败。");
            }
        }
        catch (Exception ex) { log?.Warning("[EventRotator][自定义bot类型] 枚举自检失败: " + ex.Message); }
    }

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);
        }
        catch { return null; }
    }

    /// <summary>
    /// 把源角色（arenafighterevent）在 `BotConfig` 里**按角色分键**的配置深拷贝给**每个**自定义角色
    /// （<see cref="ScourgeRoles"/>：bossScourge 与 followerScourge 各一份）。
    /// 目前 SPT 4.1.6 的 `configs/bot.json` 里有两张按角色分键的表：`equipment`（装备过滤器）与
    /// `itemSpawnLimits`；缺 `equipment` 会让 BotInventoryGenerator 直接抛 KeyNotFoundException
    /// （实机已复现：进实验室一个 AI 都没有）。失败只 warning，不抛。
    /// </summary>
    private static int CloneRoleConfigs(object botConfig, object jsonUtil, ISptLogger<EventRotatorServer> log, string modPath = null)
    {
        if (botConfig == null || jsonUtil == null)
        {
            log?.Warning("[EventRotator][自定义bot类型] ⚠ 没有 BotConfig/JsonUtil ⇒ 无法为 " + string.Join("/", ScourgeRoles)
                         + " 复制按角色配置（会导致 bot 生成失败）");
            return 0;
        }
        int done = 0;
        // 默认克隆源（第 11 项三个角色的 db json 也是它的拷贝）；第 9 项两个军火商改用
        // RoleDef.ConfigCloneSource = followerkolontaysecurity（PPES FactionCatalog 的 Sources 第一优先）。
        const string DefaultSrcRole = "arenafighterevent";
        foreach (var def in CustomRoles)
        {
            var role = def.Role;
            var srcRole = string.IsNullOrWhiteSpace(def.ConfigCloneSource) ? DefaultSrcRole : def.ConfigCloneSource;
            foreach (var propName in new[] { "Equipment", "ItemSpawnLimits" })
            {
                try
                {
                    var table = Prop(botConfig, propName) as IDictionary;
                    if (table == null) continue;
                    if (HasKeyIgnoreCase(table, role)) continue;
                    var srcVal = GetValueIgnoreCase(table, srcRole);
                    if (srcVal == null)
                    {
                        log?.Warning($"[EventRotator][自定义bot类型] ⚠ BotConfig.{propName} 里没有源角色 '{srcRole}' ⇒ 无法为 {role} 复制配置");
                        continue;
                    }
                    // 序列化：优先用 SPT 的 `Serialize(object, Type, bool)`（三参，非泛型），
                    // 兜底再试泛型 `Serialize<T>`。⚠ 之前用模糊匹配选到了三参重载却只传 1 个实参
                    // ⇒ `TargetParameterCountException: Parameter count mismatch`（实机日志已复现）。
                    var jt = jsonUtil.GetType();
                    string jsonText = null;
                    var ser3 = jt.GetMethod("Serialize", new[] { typeof(object), typeof(Type), typeof(bool) });
                    if (ser3 != null)
                    {
                        jsonText = ser3.Invoke(jsonUtil, new object[] { srcVal, srcVal.GetType(), false }) as string;
                    }
                    else
                    {
                        var serG = jt.GetMethods().FirstOrDefault(m => m.Name == "Serialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                        if (serG != null) jsonText = serG.MakeGenericMethod(srcVal.GetType()).Invoke(jsonUtil, new[] { srcVal }) as string;
                    }
                    var des = jt.GetMethods().FirstOrDefault(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                    if (jsonText == null || des == null)
                    {
                        log?.Warning($"[EventRotator][自定义bot类型] ⚠ JsonUtil 序列化/反序列化不可用 ⇒ 无法复制 BotConfig.{propName}");
                        continue;
                    }
                    var clone = des.MakeGenericMethod(srcVal.GetType()).Invoke(jsonUtil, new object[] { jsonText });
                    // [借·待重写] SCOURGE ★★★ 装备过滤器再叠一层「仓库自己的 db/bots/config/<role>.json」。
                    //   上游仓库把每个角色的装备过滤器写在那里（`{"equipment":{...}}`，字段名与 SPT 的
                    //   `EquipmentFilters` 逐字一致）。本 mod 不合并 mod 的 db/ 目录，所以这里读盘再做深合并：
                    //   只覆盖仓库**显式写了**的键，没写的（如 weaponModLimits）保留 arenafighterevent 的值
                    //   —— 避免把 `WeaponModLimits` 变 null 后影响取镜上限。
                    if (string.Equals(propName, "Equipment", StringComparison.OrdinalIgnoreCase))
                        clone = MergeRoleEquipmentConfig(clone, modPath, role, jsonUtil, log) ?? clone;
                    // ★ 必须用小写键：SPT 查 `botConfig.Equipment[role]` 时传的是 role.ToLowerInvariant()（BotEquipmentFilterService.cs:118），
                    //   而 `Dictionary<string,…>` 默认大小写敏感 —— 写 "bossScourge" 会查不到（实机 KeyNotFoundException 已复现）。
                    table[role.ToLowerInvariant()] = clone;
                    done++;
                    log?.Info($"[EventRotator][自定义bot类型] ✔ BotConfig.{propName}['{role.ToLowerInvariant()}'] ← 复制自 '{srcRole}'");
                }
                catch (Exception ex)
                {
                    log?.Warning($"[EventRotator][自定义bot类型] BotConfig.{propName} 复制失败（已忽略）：{(ex.InnerException ?? ex).Message}");
                }
            }
        }
        // `botConfig.Bosses` 是按角色名的**列表**（IsBotBoss 的判据之一）；**只登记冠军**
        // （小弟是随从，不是 boss，登记进去会让 IsBotBoss 误判）。不在列表里不会报错，但会少一层首领语义。
        try
        {
            var bosses = Prop(botConfig, "Bosses") as System.Collections.IList;
            if (bosses != null)
            {
                // 只登记 IsBoss=true 的（灾厄冠军）；小弟 / 第 11 项的 Cleanup Crew / Pillager 都是小队成员，不登记
                foreach (var def in CustomRoles.Where(r => r.IsBoss))
                {
                    var exists = false;
                    foreach (var x in bosses) if (string.Equals(x?.ToString(), def.Role, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                    if (exists) continue;
                    bosses.Add(def.Role);
                    done++;
                    log?.Info($"[EventRotator][自定义bot类型] ✔ BotConfig.Bosses += '{def.Role}'（只有 IsBoss 的角色登记）");
                }
            }
        }
        catch (Exception ex) { log?.Warning("[EventRotator][自定义bot类型] BotConfig.Bosses 登记失败（已忽略）：" + ex.Message); }

        return done;
    }

    /// <summary>
    /// [借·待重写] SCOURGE 把 <c>&lt;mod&gt;/db/bots/config/&lt;role&gt;.json</c> 里的 <c>equipment</c> 段叠加到按角色复制出来的
    /// <c>EquipmentFilters</c> 上（上游仓库 <c>danauraborealis/Scourge</c> 就是这么放每个角色的装备过滤器的）。
    ///
    /// <para><b>只覆盖仓库显式写了的键</b>：仓库两份 config 都没有 <c>weaponModLimits</c>，
    /// 若整份替换会把它变成 null，影响 `BotWeaponModLimitService.GetWeaponModLimits` 的取镜/手电上限
    /// ⇒ 这里做的是合并，不是替换。</para>
    ///
    /// <para><b>为什么能用 JsonUtil 往返</b>：字段名（`faceShieldIsActiveChancePercent` /
    /// `forceOnlyArmoredRigWhenNoArmor` / `laserIsActiveChancePercent` / `lightIsActive*` /
    /// `nvgIsActive*` / `forceStock`）与 SPT 的 <c>EquipmentFilters</c> 的 <c>[JsonPropertyName]</c> 逐字一致。
    /// 任何一步失败 ⇒ 返回 null，调用方沿用源角色配置（不会崩）。</para>
    /// </summary>
    private static object MergeRoleEquipmentConfig(object clone, string modPath, string role,
                                                   object jsonUtil, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            if (clone == null || jsonUtil == null || string.IsNullOrEmpty(modPath)) return null;
            var cfgPath = Path.Combine(modPath, "db", "bots", "config", role + ".json");
            if (!File.Exists(cfgPath)) return null;

            var jt = jsonUtil.GetType();
            var ser3 = jt.GetMethod("Serialize", new[] { typeof(object), typeof(Type), typeof(bool) });
            var jsonText = ser3?.Invoke(jsonUtil, new object[] { clone, clone.GetType(), false }) as string;
            if (jsonText == null) return null;

            var root = System.Text.Json.Nodes.JsonNode.Parse(jsonText) as System.Text.Json.Nodes.JsonObject;
            var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath)) as System.Text.Json.Nodes.JsonObject;
            if (root == null || cfg == null) return null;
            if (!cfg.TryGetPropertyValue("equipment", out var eqNode) ||
                eqNode is not System.Text.Json.Nodes.JsonObject eq) return null;

            int applied = 0;
            foreach (var kv in eq)
            {
                root[kv.Key] = kv.Value?.DeepClone();
                applied++;
            }
            var des = jt.GetMethods().FirstOrDefault(m => m.Name == "Deserialize"
                                                       && m.IsGenericMethodDefinition
                                                       && m.GetParameters().Length == 1);
            if (des == null) return null;
            var merged = des.MakeGenericMethod(clone.GetType()).Invoke(jsonUtil, new object[] { root.ToJsonString() });
            if (merged != null)
                log?.Info($"[EventRotator][自定义bot类型] ✔ '{role}' 装备过滤器叠加 {applied} 项仓库值"
                          + $"（db/bots/config/{role}.json → equipment；未写的键沿用源角色）");
            return merged;
        }
        catch (Exception ex)
        {
            log?.Warning($"[EventRotator][自定义bot类型] 叠加仓库 db/bots/config/{role}.json 失败"
                         + $"（已忽略，沿用源角色配置）：{(ex.InnerException ?? ex).Message}");
            return null;
        }
    }

    /// <summary>
    /// [借·待重写] SCOURGE 借自 <c>danauraborealis/Scourge</c>：它把每个角色的预设批次数写在自己的
    /// <c>data/db/bots/config/&lt;role&gt;.json</c>（<c>{"presetBatch": 5, "isBoss": true, ...}</c>）。
    /// 本 mod 不读那个目录（SPT 4.1.6 不合并 mod 的 db/，见类头说明），所以在运行期直接补 <c>BotConfig.PresetBatch</c>。
    ///
    /// <para><b>取值照抄 Scourge</b>：冠军 <c>5</c>、小弟 <c>10</c>（它两份 config 里的 presetBatch）。
    /// 键必须是**枚举名原样（camelCase）**：内置 <c>configs/bot.json</c> 的键就是 <c>"arenaFighter"</c>/<c>"bossBoar"</c>，
    /// 反编译读点 <c>BotController.GetBotPresetGenerationLimit(type)</c> 用的也是客户端下发的角色名原样
    /// （实机告警里打出来是 <c>bossScourge</c> 带大写 S）⇒ 这里只写**枚举名原样**这一个键，
    /// 与内置 bot.json 的键风格一致。</para>
    ///
    /// <para><b>幂等</b>：已有同名键（含用户自己在 <c>configs/bot.json</c> 里加了）⇒ 不动，只记日志。</para>
    /// </summary>
    private static void EnsurePresetBatch(object botConfig, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            var table = Prop(botConfig, "PresetBatch") as IDictionary;
            if (table == null)
            {
                log?.Warning("[EventRotator][自定义bot类型] ⚠ 拿不到 BotConfig.PresetBatch ⇒ 跳过（会继续报「找不到要为机器人生成的预设数」）");
                return;
            }

            // 取值见 RoleDef.PresetBatch：灾厄小队 5/10（上游 config），第 11 项两个角色 = 克隆源 arenaFighterEvent 的 15。
            foreach (var def in CustomRoles)
            {
                var role = def.Role;
                var batch = def.PresetBatch;
                if (HasKeyIgnoreCase(table, role))
                {
                    continue;
                }
                table[role] = batch;                 // 枚举名原样 = SPT 的读点口径（第 11 项两个角色本身就是小写）
                long len = 0;
                try { len = Convert.ToInt64(table[role]); } catch { }
                log?.Info($"[EventRotator][自定义bot类型] ✔ BotConfig.PresetBatch['{role}'] = {len}（借自 Scourge 的 presetBatch）");
            }
        }
        catch (Exception ex)
        {
            log?.Warning("[EventRotator][自定义bot类型] PresetBatch 补写失败（已忽略，不影响出图）：" + ex.Message);
        }
    }

    private static bool HasKeyIgnoreCase(IDictionary d, string key)
    {
        foreach (var k in d.Keys) if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static object GetValueIgnoreCase(IDictionary d, string key)
    {
        foreach (DictionaryEntry de in d)
            if (string.Equals(de.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return de.Value;
        return null;
    }
}
