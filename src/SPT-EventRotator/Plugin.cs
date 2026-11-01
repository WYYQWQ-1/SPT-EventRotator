using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Web;   // ★ v1.3.8 IModBlazorMetadata（网页面板入口）

// ★★★ v2.6.13【任务门控】三个短别名（这三个类型不在本文件已有的 using 里；
//   用别名是为了不让下面那一大段门控代码被全限定名淹没。全限定写法在本文件其它地方照旧可用）
using MongoId = SPTarkov.Server.Core.Models.Common.MongoId;
using QuestStatusEnum = SPTarkov.Server.Core.Models.Enums.QuestStatusEnum;
using SeasonalEventType = SPTarkov.Server.Core.Models.Enums.SeasonalEventType;

namespace EventRotator;

public record EventRotatorMetadata : IModMetadata, IModBlazorMetadata
{
    // ★ 2026-10-27：版本号单一来源。以前 package.json 由 ship.py 改、这一行的
    //   SemanticVersioning.Version 也由 ship.py 改，但 EventRotatorStatusRouter.ModVersion
    //   是另一处独立硬编码（漏改 ⇒ 服务端自报 1.3.5，Forge 的 dll_version_match 直接 Failed）。
    //   现在都从这里取；ship.py 的 bump_version 会同步改这一行。
    public const string VersionText = "1.3.15";
    public string ModGuid { get; init; } = "com.kirisame.eventrotator";
    public string Name { get; init; } = "SPT-EventRotator";
    public string Author { get; init; } = "Kirisame dish";
    public List<string> Contributors { get; init; } = new();
    public SemanticVersioning.Version Version { get; init; } = new SemanticVersioning.Version(VersionText, false);   // ★ v1.1.1（2026-10-25）：修「全新安装无轮换」+「F12 保存丢键」+ 缺预补丁自查（bossScourge 未注册时自动禁用灾厄小队）
    public SemanticVersioning.Range SptVersion { get; init; } = new SemanticVersioning.Range("~4.1.0", false);
    // ★★★ bossScourge：服务端 `WildSpawnType` 枚举由 SPT 原生 enum prepatcher 注入
    //   （定义文件：SPT_Runtime/user/patchers/com.kirisame.eventrotator/EventRotatorEnums.json）。
    //   必须为 true，否则 SPT 根本不会去读那个目录（ModLoader.LoadMod :760-767）。
    public bool HasPrepatcher { get; init; } = true;
    public List<string> Incompatibilities { get; init; } = new();
    public Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new();
    public string Url { get; init; } = "";
    public string License { get; init; } = "MIT";

    // ★ 2026-10-27【网页面板】SPT 4.1 原生支持 mod 自带 Blazor 页面：
    //   实现 IModBlazorMetadata 后，服务器 Web UI 的 mod 列表会给出入口链接（/eventrotator）。
    //   页面代码见 Web/ConfigPage.razor，读写逻辑见 ConfigWeb.cs。
    public string WWWRootUrl { get; init; } = "";
    public string HomePage { get; init; } = "/eventrotator";
    public string HomePageDescription { get; init; } = "活动轮换配置 · Event rotation config";
}

/// <summary>
/// 活动应用/还原。
/// ★ 采用「先还原 → 再套用」模型：每次进图都会先把所有被改过的配置恢复原值，再套用当前活动。
///   所以 关掉活动 / 换活动 都能干净回退，不会残留、不会叠加漂移；且只「加/放宽」，绝不删除存档数据。
/// </summary>
[Injectable]
public class EventRotatorServer(
    ISptLogger<EventRotatorServer> logger,
    ModHelper modHelper,
    LocationConfig locationConfig,
    LocationTable locationTable,
    TradersTable tradersTable,
    RagfairConfig ragfairConfig,
    AirdropConfig airdropConfig,
        GlobalTable globalTable,
        TemplateTable templateTable,
        HideoutConfig hideoutConfig,
        // ★★★ v2.6.12【P0 落点修正】`hideoutBuildSeconds`/`hideoutCraftSeconds` 改为直接改**已加载的**
        //   HideoutTable（Areas[].Stages[].ConstructionTime / Production.Recipes[].ProductionTime）——
        //   因为 HideoutConfig.Override*TimeSeconds 的唯一读点（PostDbLoadService.cs:59-60）在启动阶段，
        //   早于本 mod（TypePriority=int.MaxValue）⇒ 旧落点是空转。
        HideoutTable hideoutTable,
        TraderConfig traderConfig,
        BotConfig botConfig,
        BotTable botTable,
        LocaleTable localeTable,        // ★ v0.1.5 第1组：比特币改名需要 locale 表（官方 RenameBitcoin）
    SPTarkov.Server.Core.Services.Commerce.GiftService giftService,   // ★ E：玩家季节礼物
    SPTarkov.Server.Core.Servers.SaveServer saveServer,               // ★ E：取存档 id（= sessionId）
    IServiceProvider serviceProvider,        // ★ ④：用它**按类型**解析 PBS 的 DataLoader 单例（不能注入 IEnumerable<IOnLoad>：本类自己就是 IOnLoad ⇒ 循环依赖）
        WeatherConfig weatherConfig,
        QuestConfig questConfig,
    SeasonalEventService seasonalEventService,
    SeasonalEventConfig seasonalEventConfig,
    SPTarkov.Server.Core.Models.Spt.Config.InRaidConfig inRaidConfig,
    SPTarkov.Server.Core.Models.Spt.Config.PlayerScavConfig playerScavConfig,
    SPTarkov.Server.Core.Models.Spt.Config.ItemConfig itemConfig,
    SPTarkov.Server.Core.Helpers.Profile.HandbookHelper handbookHelper,
    SPTarkov.Server.Core.Utils.JsonUtil jsonUtil,
    // ★★★ v2.6.12【P0 落点修正】新增两个 SPT 单例（都不是本 mod 自己的类型，DI 里已注册 Singleton）：
    //   · PaymentHelper —— 商人报价"是不是货币"的**唯一判定口径**（PaymentHelper.cs:27-38 =
    //       卢布/美元/欧元/GP + inventoryConfig.customMoneyTpls）。用于 A2：
    //       `traderPriceMultiplier` 改为直接改已加载的 TradersTable.BarterScheme[].Count，
    //       选取口径必须与 SPT `TraderController.AdjustTraderItemPrices` 逐字一致（它调的就是这个类）。
    //   · ItemFilterService —— 跳蚤/战利品禁售的**运行期真身**：它在**构造函数**里把
    //       `itemConfig.Blacklist` 拷进 `ItemBlacklistCache`，而 `IsItemBlacklisted()` 只查 cache
    //       （ItemFilterService.cs:21-29 拷贝 / :112-115 查询）⇒ 只写 itemConfig.Blacklist 等于没写。
    //       用于 A4c：两边都要写（config + cache）。
    SPTarkov.Server.Core.Helpers.Commerce.PaymentHelper paymentHelper,
    SPTarkov.Server.Core.Services.Items.ItemFilterService itemFilterService,
    // ★★★ v2.6.13【任务门控】SPT 原生的任务状态读写器（[Injectable] Singleton，QuestHelper.cs:32-33）：
    //   · `ResetQuestState(pmcData, status, questId)`（QuestHelper.cs:650-672）—— "只清进度"的正解
    //     （清 `CompletedConditions` + 写 StatusTimers + 去掉高于目标状态的计时器）；
    //   · `GetQuestsFromDb()`（:578-581）—— 门控读点用的就是它（= templateTable.Quests.Values，实时的）。
    //   注入它而不是自己反射拼，是为了让"改了什么"有编译期保证。
    SPTarkov.Server.Core.Helpers.Quest.QuestHelper questHelper,
    // ★★★ v2.6.14【补洞·AcceptQuest 门控】构造"干净的失败响应"用的两个 SPT 单例
    //   （写法逐字照 `QuestController.ShowRepeatableQuestInvalidConditionError`：
    //    `output = eventOutputHolder.GetOutput(sessionID)` → `httpResponseUtil.AppendErrorToOutput(output, msg)`）：
    //   · EventOutputHolder（[Injectable] Singleton，EventOutputHolder.cs:16/:28-37）= 每个 session 的响应对象；
    //   · HttpResponseUtil（[Injectable] Transient，HttpResponseUtil.cs:14/:127）= 往响应里追加 Warning/ErrorMessage。
    //   ⚠ 不做这两步就只能抛异常 ⇒ HTTP 500 + 可能崩客户端，所以必须拿现成写法。
    SPTarkov.Server.Core.Routers.EventOutputHolder eventOutputHolder,
    SPTarkov.Server.Core.Utils.HttpResponseUtil httpResponseUtil,
    // ★★★ v2.6.37【只在该地图生效】SPT 记录"本局打哪张图"的服务
    //   （`ProfileActivityService.cs:46/:55`；SPT 官方自己在 `BotGeneratorHelper.cs:42` 就用
    //    `GetFirstProfileActivityRaidData()?.RaidConfiguration` 取当前局）——
    //   空投那侧用它判 `Location`；bot 那侧直接用 `BotGenerationDetails.Location`，不依赖它。
    SPTarkov.Server.Core.Services.Profile.ProfileActivityService profileActivityService
) : IOnLoad
{
    /// <summary>★ 2026-10-31 给"孤儿季节首领清理"用的配置对象（SeasonalEventConfig）。</summary>
    public static object SeasonalCfg;

    // ---- 基准值（还原用）----
    private Dictionary<string, double> _baseLoose = new();
    private Dictionary<string, double> _baseStatic = new();
    private readonly List<Action> _restoreActions = new();   // 每个被改动的还原闭包
    /// <summary>
    /// ★★★ v2.6.38【多活动冲突修复】`fleaMinLevel` 的「本轮取更严格」用的最大值：
    ///   `closed_flea`(=99，等于关闭) 与 `flea_promotion`(=1，任意等级可访问) 同开时，
    ///   **等级门槛取两者最大值** ⇒ 关闭照样生效 ✔；只有促销时 ⇒ 1 ⇒ 促销生效 ✔。
    ///   ⚠ 必须用「本轮已声明的最大值」而不是「当前字段值」来比较：后者会把「想降到 1」的促销诉求当成「更严格」挡掉
    ///   （离线探针实测踩到过）。每次套用前清零，见 `RestoreThenApply()`。
    /// </summary>
    private int? _fleaPassMax;

    /// <summary>★ v0.1.5：把 GlobalTable 暴露给补丁层做"进图自证"（复核 globals.config.EventType）。</summary>
    public static object GlobalTableRef;

    /// <summary>★ ③b：暴露 SaveServer（供进图钩子读房主等级做等级门槛判断）。</summary>
    public static object SaveServerRef;

    /// <summary>★ ② 当前生效的"首领时刻表"活动（进图时按系统时钟落图）。</summary>
    internal static EventDef _timetable;

    /// <summary>★ 路费机制：当前生效的路费活动（进图时执行）。</summary>
    internal static EventDef _toll;
    public Task OnLoadAsync(CancellationToken ct)
    {
        try
        {
            // ★★★ v1.0.2【实机根因·必须有，且必须排第一】锁定"运行时真正在跑的那份 SPTarkov.Server.Core"。
            //   病根：本 mod 自带 enum prepatch ⇒ SPT 4.1.6 会先跑一趟内存预补丁 + SPT.PrepatchHost ALC 重启
            //   （SPT.Server.decompiled.cs:575-596 / :729-736），进程里因此同时存在**两份同名同版本**的 Core；
            //   而 AccessTools.TypeByName 按装载顺序取第一个 ⇒ 命中外层 Default ALC 那份（死的），
            //   补丁挂上了却永不触发（实机 spt20261024.log:4309 `ALC=Default` + 两个 postfix 各 0 条）。
            //   ⇒ 之后所有给 Harmony 用的 SPT 类型都必须走 `RuntimeTypes.Find(...)`，不许再裸用 AccessTools。
            RuntimeTypes.Init(botTable, m => { try { logger.Info(m); } catch { } });
            FactionLootInject.Hook(m => { try { logger.Info(m); } catch { } });   // ★ 第 12 项：背包注入（必须在 Init 之后挂，才能挂到运行时那份 Core）
            PatchProbe.Log = m => { try { logger.Info(m); } catch { } };
            GlobalTableRef = globalTable;
            SaveServerRef = saveServer;   // ★ ③b 等级门槛用
            PbsPoolWriter.Log = logger;                                  // ★ ④ PBS 池子写入
            PbsPoolWriter.ServiceProvider = serviceProvider;
            TollDimension.Log = logger;                       // ★ 路费机制
            TollDimension.SaveServer = saveServer;              // ★ ④ 运行期按类型解析（避免循环依赖）
            // ★ v0.1.5：把表引用与还原通道交给"重验后补做"的维度层
            ExtraDimensions.Log = logger;
            ExtraDimensions.AddRestore = a => _restoreActions.Add(a);
            ExtraDimensions.BotTableRef = botTable;
            // ★ 万圣节感染者（僵尸）维度：DI 服务 + BotTable + 日志通道（必须在套用前接好）
            SeasonalZombieDimension.Services = serviceProvider;
            SeasonalZombieDimension.BotTable = botTable;
            SeasonalZombieDimension.LocationTable = locationTable;   // 把感染率写进图上（客户端感染条 + 刷怪判定）
            SeasonalZombieDimension.GlobalTable = globalTable;       // 同上：globals.LocationInfection
            SeasonalZombieDimension.Info = m => { try { logger.Info(m); } catch { } };
            SeasonalZombieDimension.Warn = m => { try { logger.Warning(m); } catch { } };
            // ★ 藏身处万圣节照明（与僵尸无关的独立维度；万圣节家族活动都会调它）
            HalloweenIllumination.GlobalTable = globalTable;
            HalloweenIllumination.Info = m => { try { logger.Info(m); } catch { } };
            HalloweenIllumination.Warn = m => { try { logger.Warning(m); } catch { } };
            HalloweenIllumination.AddRestore = a => _restoreActions.Add(a);
            // ★ SAIN 服务端分类注册（让网页预设编辑器把 7 个自定义角色显示在 Bosses；后台重试等 SAIN 就绪）
            SainBotRegistration.Init(serviceProvider,
                m => { try { logger.Info(m); } catch { } },
                m => { try { logger.Warning(m); } catch { } });
            ExtraDimensions.LocationTableRef = locationTable;
            ExtraDimensions.GlobalTableRef = globalTable;
            ExtraDimensions.LocaleTableRef = localeTable;   // ★ locale 表（比特币改名用）
            ExtraDimensions.TemplateTableRef = templateTable;   // ★ 物品模板表（容器守卫用：判 tpl 有没有 Grids）
            // ★★★ v2.6.37【只在该地图生效】按图门控要用 SPT 的"本局打哪张图"
            //   （补丁挂载在下面的 Harmony 块里，那时 harmony 才建好）
            MapGateDimension.Log = logger;
            MapGateDimension.ProfileActivityServiceRef = profileActivityService;
            // ★★★【整体替换 bot 装备池】`botEquipmentReplace`（casus_belli 劫掠者 6 款彩绘 CQCM 面具）：
            //   落点 = BotTable.Types[role].BotInventory.Equipment[slot]（整槽替换）
            //        + BotType.BotChances.EquipmentChances[slot]=100（保证一定戴）。
            //   按图开关的 prefix 在下面的 Harmony 块里挂（那时 harmony 才建好）。
            BotEquipmentReplaceDimension.Log = m => { try { logger.Info(m); } catch { } };
            BotEquipmentReplaceDimension.Warn = m => { try { logger.Warning(m); } catch { } };
            BotEquipmentReplaceDimension.BotTable = botTable;
            // ★★★【面具后置强制修正】PBS（progressivebotsystem）用 Prefix 拦截 GenerateInventory 后，
            //   装备池/概率全取它自己的 tier 数据（arenaFighterEvent → ExUsec），我们的 BotTable 池**是死代码**
            //   ⇒ 在 PrepareAndGenerateBot 的 postfix 里改**生成后的 BotBase**（见 FaceCoverFixDimension 类注释）。
            FaceCoverFixDimension.Log = m => { try { logger.Info(m); } catch { } };
            FaceCoverFixDimension.Warn = m => { try { logger.Warning(m); } catch { } };
            // ★【诊断】单独一路 INFO：每局一条「本局收到 N 只 bot + role Top5 + Location 样例 + 各判定命中数」，
            //   回答"postfix 到底跑没跑 / 是 role 不匹配还是 Location 不匹配"。与功能日志分开，
            //   便于离线探针按通道断言（`[面具修正]` 只表示真的改过装备）。
            FaceCoverFixDimension.DiagLog = m => { try { logger.Info(m); } catch { } };
            // ★★★ v1.0.2【问题 B】把**运行期** TemplateTable 交给面具修正：判断头盔 `_props.BlocksFaceCover`
            //   必须读真正在用的模板表（同 prepatch 双载风险，DI 实例的 GetType().Assembly 才是运行时那份）。
            FaceCoverFixDimension.TemplateTable = templateTable;
            var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            var modsDir = Directory.GetParent(modPath)?.FullName ?? modPath;

            // ★★★【自定义 bot 类型 bossScourge / followerScourge】（灾厄小队冠军 / 小弟）
            //   SPT 4.1.6 **不会**自动合并 mod 的 `db/` 目录（实证见 CustomBotTypes 注释）⇒ 本 mod 自己读
            //   `<mod>/db/bots/types/*.json` 写进运行期 `BotTable.Types`（小写键 = role）。
            //   ⚠ 必须在 `RestoreThenApply()`（下面的 :209）之前 —— 活动的 `botHealthOverride` 要能查到这两个角色。
            //   服务端 `WildSpawnType` 枚举本身由 SPT 原生 enum prepatcher 注册（HasPrepatcher=true → user/patchers）。
            // ★★★ 天灾独特语音（Scourge）：把 voice customization 注册进 TemplateTable；语音包由 bundles.json 提供给客户端
            ScourgeVoiceInject.Info = m => { try { logger.Info(m); } catch { } };
            ScourgeVoiceInject.Warn = m => { try { logger.Warning(m); } catch { } };
            try { ScourgeVoiceInject.Apply(templateTable, modPath, jsonUtil); } catch { }
            CustomBotTypes.Install(modPath, botTable, botConfig, jsonUtil, logger);

            // ★★★【SAIN 联合修复】只把 13790/13791 写进 Presets/*.json 不够：
            //   反编译实证 SAIN 服务端 `PresetGenerationService.ComposeCustom()` 只遍历 `AllBotTypes()`
            //   （45 个内置 + 各 mod 经 SAIN.ServerInterop 注册的），**不认识的键会被直接丢弃**，
            //   客户端永远收不到 ⇒ `[bossScourge] does not exist in SAINSettings Dictionary!` 照旧。
            //   这里用反射向 SAIN 注册（SAIN 未安装时静默跳过；必须在 CustomBotTypes.Install 之后，
            //   因为 SAIN 要读 BotTable.Types 拿难度默认值）。
            SainPresetInject.RegisterBotTypes(serviceProvider,
                m => logger.Info(m), m => logger.Warning(m));

            Compat.Detect(modsDir, logger);
            EventEngine.Log = logger;              // 供 EventEngine.Debug 输出
            AbpsCompat.Init(logger);               // ABPS 兼容层
            EventScavSuppress.Init(logger, locationTable);   // ★ A1：普通 SCAV 抑制（只写我们自己的 DB 对象，不碰 ABPS）
            EventGearUpgrade.Init(logger, templateTable);    // ★ 清场小队：装备差异化（改写我们自己的 bot 产出物）
            EventBotTemplateFix.Init(logger, botTable);     // ★ 弹药/装备补全（写运行期 BotTable，不写盘）
            PbsCompat.Init(logger);                // ★ v2.6.6 PBS 兼容层（装备运行期池）
            EnumGuard.Init(logger);                // 写枚举值前的合法性校验（防进图崩）
            EnsureDefaultConfig(modPath, logger);  // 首次运行时生成 config.json（日志等级开关）
            EventEngine.Load(modPath, logger);
            // ★★★ 2026-10-29【外部 mod 联动 · 实验室停电】启动自检：
            //   若上次被强杀停在"停电概率=100"，而此时事件其实没生效 ⇒ 从 .bak-eventrotator 还原。
            //   （必须在 EventEngine.Load 之后：它要先知道 Actives，才能判断事件是不是在生效。）
            try { ExternalBridges.ReconcileOnStartup(); }
            catch (Exception ex) { logger.Warning("[EventRotator][外部联动] 启动自检失败（已忽略）: " + ex.Message); }
            // ★ v1.3.8【热应用】把状态写出方法挂给引擎：网页热应用后立刻刷新
            //   「当前生效活动.txt」+ HTTP 快照 ⇒ 客户端面板下次轮询即更新。
            try { EventEngine.StatusWriter = WriteActiveStatus; } catch { }
            // ★★★ v1.1.1【安全网·缺预补丁】灾厄小队用的 `bossScourge`/`followerScourge` 是靠
            //   `SPT_Runtime\user\patchers\com.kirisame.eventrotator\EventRotatorEnums.json` 注入的。
            //   客户端 WildSpawnType 里没有这两个值 ⇒ 一遇到灾厄小队的 bot 就弹：
            //        Requested value 'bossScourge' was not found.
            //   ⚠ 实测（2026-10-25）两种缺法要分开看：
            //     ① 整个 patchers 目录/JSON 都不存在 → **SPT 自己在启动阶段就崩**，根本轮不到这里：
            //        `Failed to locate an enum prepatch JSON file for mod: com.kirisame.eventrotator`
            //     ② 文件在、但里面没写 bossScourge → 服务端能起来，这时才轮到这个自查
            //   ⇒ 这里只处理 ②：把『灾厄小队』活动临时摘掉，让它“不出现”，而不是“进图弹框”。
            //   ⚠ 解析必须走**运行时程序集 + 全名 GetType**（和 CustomBotTypes 一致）：
            //     不能用 `a.GetTypes()` 扫（Core.Patched 会抛 ReflectionTypeLoadException 被 catch 吞掉 ⇒ 误判成“有”）。
            static bool RoleExists(string fullName, string role)
            {
                try
                {
                    // ⚠ 必须扫**所有**程序集：SPT 4.1.6 因为 enum prepatch 同时存在
                    //   Default（死副本，没有我们的枚举）和 PrepatchHost/Core.Patched（运行时，有）。
                    //   只取第一个命中的类型会拿到死副本 ⇒ 误报“缺角色”（实测踩过）。
                    var typeFound = false;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = null;
                        try { t = a.GetType(fullName); } catch { }
                        if (t == null || !t.IsEnum) continue;
                        typeFound = true;
                        if (Enum.GetNames(t).Any(n => string.Equals(n, role, StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                    return !typeFound;      // 连枚举类型都找不到 → 保守放行
                }
                catch { return true; }
            }
            var bossOk = RoleExists("SPTarkov.Server.Core.Models.Eft.Common.WildSpawnType", "bossScourge");
            var followerOk = RoleExists("SPTarkov.Server.Core.Models.Eft.Common.WildSpawnType", "followerScourge");
            // ★ v1.3.7【WTT 武器兜底修正】灾厄主武器是 WTT 自定义物品，而 WTT 在它自己的 OnLoadAsync 里注册。
            //   两者是同一档 TypePriority ⇒ 先后顺序不保证，旧代码在这里就判"缺 tpl"并换原版枪（实测会把
            //   装了 WTT 的人也换掉）。现在：首轮只报不换，8 秒后复查（那时所有 mod 的 OnLoad 都跑完了）。
            ScourgeWeaponFallback.Init(logger, botTable, templateTable, EventEngine.ModPath);
            ScourgeWeaponFallback.Run("load");
            ScourgeWeaponFallback.Schedule();
            if (!bossOk || !followerOk)
            {
                // ⚠ 必须**两边一起摘**：`All`（boss 注入按它遍历）和 `Actives`（维度注入按它遍历）。
                //   只摘 All 会留下「Actives 里还有该活动 ⇒ 随从维度照跑 ⇒ 日志刷
                //   『laboratory 没有条目 bossScourge ⇒ 跳过』，而 boss 又因为 All 里没了不刷」这种半截状态。
                var removedAll = EventEngine.All.RemoveAll(e => string.Equals(e.Id, "scourge_incursion", StringComparison.OrdinalIgnoreCase));
                var removedActive = EventEngine.Actives.RemoveAll(e => string.Equals(e.Id, "scourge_incursion", StringComparison.OrdinalIgnoreCase));
                logger.Error($"[EventRotator] ✘ 服务端枚举缺角色：bossScourge={bossOk} followerScourge={followerOk}");
                logger.Error("[EventRotator] ✘ 说明预补丁没生效：检查 SPT_Runtime\\user\\patchers\\com.kirisame.eventrotator\\EventRotatorEnums.json");
                logger.Error("[EventRotator] ✘ 完整包有三部分（服务端 mod + patchers + 客户端插件），只装服务端会出这种事。");
                logger.Error($"[EventRotator] ✘ 已临时禁用『灾厄小队』活动（All 摘掉 {removedAll} 项 / Actives 摘掉 {removedActive} 项）"
                           + "—— 否则客户端会弹『Requested value \u0027bossScourge\u0027 was not found.』");
            }
            // ★★★ 自定义任务线（并进本 mod，不另开 mod）**不在这里注入** ——
            //   实机证据（2026-10-23 启动实测）：在 `[Injectable]` 默认 TypePriority=int.MaxValue 这一档加物品，
            //   会被 `DatabaseIntegrityService.EnsureNoItemsAddedSinceProfilesLoaded()` 直接判违规并 abort 启动：
            //   「2 item(s) were added to the database after profiles loaded … Add items during OnLoadOrder.Preload」。
            //   ⇒ 注入搬到 QuestLinePreload（TypePriority = OnLoadOrder.Preload = 100000）。
            try
            {
                var cfgP = System.IO.Path.Combine(modPath, "config.json");
                if (System.IO.File.Exists(cfgP))
                {
                    using var cfgDoc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(cfgP));
                    ItemSupply.Load(cfgDoc.RootElement, logger);
                }
            }
            catch (Exception ex) { logger.Warning($"[EventRotator] itemSupply 读取失败: {ex.Message}"); }
            // ★ 必须在 Load 之后同步（Load 里才读 config.json 的 allowOnOverhaulMaps）
            Compat.AllowOnOverhaulMaps = EventEngine.AllowOnOverhaulMaps;

            // ★★ v0.1.3：把空投维度的 DI 单例在**启动时**就接上。
            //    0.1.2 的 bug：AirdropDimension.Config 只在"某个活动带空投字段"时才被赋值，
            //    而全局物品供给（ItemSupply）先于它执行 ⇒ 永远打「拿不到 AirdropConfig」⇒ 供给功能全死。
            AirdropDimension.Init(airdropConfig, locationTable,
                m => logger.Info(m), a => _restoreActions.Add(a));
            logger.Info("[EventRotator][空投] 维度已接入：" + (airdropConfig != null ? "AirdropConfig OK" : "AirdropConfig 为空"));

            _baseLoose = new Dictionary<string, double>(locationConfig.LooseLootMultiplier ?? new());
            _baseStatic = new Dictionary<string, double>(locationConfig.StaticLootMultiplier ?? new());

            // 进图时：先读 force.txt → 再「还原+套用」（热切换且可回退）
            EventEngine.OnRefresh = () => { RestoreThenApply(); };

            // ★ v0.1.5 无条件护栏：官方 halloweensummon.laboratory = bossTagilla，
            //   而 SPT 的 Halloween 与 AprilFools **两个**季节分支都会注入它 ⇒ 实验室会莫名刷锤哥。
            //   启动时摘掉一次，之后任何季节强推都注入不进来（幂等）。
            SeasonalCfg = seasonalEventConfig;   // 供孤儿季节首领清理读 EventBossSpawns
            RitualVoiceFix.Apply(botTable, m => logger.Info(m), m => logger.Warning(m));
            try
            {
                // ★ 全量自检：列出 events.json 里**全部**活动对应的允许键（不只当前生效的），
                //   这样轮换到任何活动都能提前看出"它会不会误摘/该保留什么"。
                var ids = EventEngine.All.Where(e => e != null).Select(e => e.Id);
                Patches.BossWavePatch.LogSeasonAllowTable(ids, m => logger.Info(m));
            }
            catch { }   // ★ 复仇小鹿语音（scav → 扎里亚奇）
            ApplyVanillaSeasonTakeover(modPath);   // ★ v1.3.9【接管原版季节窗】
            ApplyHalloweenSummonPolicy();
            PrepareZombieHostility();   // ★ v0.1.5 僵尸敌对性数据接入（BPS 掐掉了官方那段）
            AssertNeverOpenQuests();    // ★ v0.1.5 断言"永远隐藏"清单没被任何活动引用

            RestoreThenApply();

            var harmony = new Harmony("com.kirisame.eventrotator");
            Patches.BossWavePatch.DataDirHint = modPath;   // ★ 让敌对名单能读本体 bot 类型表
            Patches.SeasonalTakeoverPatch.Enable(harmony, m => logger.Info(m));
            Patches.AbpsCachePatch.Enable(harmony, m => logger.Info(m));   // ★ 补 ABPS 内部缓存（多 boss 活动不再被重建抹掉）   // ★ 接管原版季节窗（拦 EnableSeasonalEvents）
            Patches.BossWavePatch.Enable(harmony, logger);
            // ★ 商人万圣节头像的**延迟补做**：SPT 的 TradersTable 在 OnLoad 时刻还没装满
            //   （实机铁证：OnLoad 时只有 Fence ⇒「改 1 个、找不到 7 个」）⇒ 挂到
            //   TraderHelper.GetAllTraders（traderSettings 构建点 = 客户端来取头像那一刻）补做。
            //   独立新文件、全程可降级、没声明 eventTraderIcons 时零开销。
            Patches.TraderIconPatch.Enable(harmony, logger);
            // ★★★ 容器注入：SPT 每局会把容器根 _id 换成新 MongoId ⇒ 预注入件的 parentId 会悬空
            //   （客户端 NoParentWithIdError 丢弃）。postfix 把它改指运行期 Root + 交给客户端自动选格。
            Patches.ContainerLootInjectPatch.Enable(harmony, m => logger.Info(m));
            // ★★★ v2.6.37【只在该地图生效】按图门控（用户裁决：致命毒素只在海关）：
            //   ① `BotGenerator.PrepareAndGenerateBot` prefix —— 按图开关 bot 战利品池；
            //   ② `LocationCallbacks.GetAirdropLoot` prefix —— 记住 sessionId（空投判定用）。
            MapGateDimension.Enable(harmony, logger);
            // ★★★【整体替换 bot 装备池】`botEquipmentReplace` 的按图开关 —— 与 MapGateDimension
            //   各挂各的 prefix（Harmony 允许多个 prefix，两者都只返回 true，互不拦截）。
            BotEquipmentReplaceDimension.Enable(harmony, logger);

            // ★★★ v2.6.14【补洞·AcceptQuest 门控】把两个"构造失败响应"的 SPT 单例交给补丁，
            //   然后挂 `QuestController.AcceptQuest` 的 prefix（活动没在跑 ⇒ 干净拒绝，不抛异常）。
            //   ⚠ 补丁内部全程可降级：找不到方法/类型/响应构造器 ⇒ warning + 一律放行，
            //     **绝不会因为补丁失败导致所有任务都接不了**（`QuestGateReady` 未就绪时同样放行）。
            Patches.QuestAcceptGatePatch.OutputHolder = eventOutputHolder;
            Patches.QuestAcceptGatePatch.HttpUtil = httpResponseUtil;
            Patches.QuestAcceptGatePatch.Enable(harmony, logger);

            // ★★★ v2.6.15【再补一层】任务**列表**过滤：把「已接条目 + 被门控 + 所属活动没在跑」的任务
            //   从 `/client/quest/list` 的返回里摘掉（`QuestHelper.GetClientQuests` :882-886 无条件放行那批）。
            //   ⚠ 同样全程可降级：任何异常 / 读不到 profile / 门控未就绪 ⇒ **列表原样返回**（绝不弄空或弄坏）。
            Patches.QuestListFilterPatch.SaveServer = saveServer;
            // ★ v1.0.1【prepatch 兼容·真因】把 DI 注入的**运行时那个** QuestHelper 实例交给补丁层：
            //   只有它身上 `GetType()` 出来的类型才是"真正在执行"的那一份程序集里的类型，
            //   用 `AccessTools.TypeByName` 可能在「原始 Core.dll + Core.Patched.dll」并存时命中错的那份
            //   ⇒ 补丁挂上了却永远不触发（无异常、无日志）。详见 `QuestListFilterPatch` 字段注释。
            Patches.QuestListFilterPatch.QuestHelperInstance = questHelper;
            Patches.QuestListFilterPatch.Enable(harmony, logger);

            // ★★★ 第 3 项【Fence 传闻】活动集合变化 ⇒ 玩家进游戏时收一条黑商消息。
            //   落点：`GameCallbacks.GameStart` postfix（带 sessionID）+ DI 的 `MailSendService`。
            //   去重靠 `<mod>/fence_rumor_state.json` 的 lastHash；限流靠 config.json → fenceRumor。
            //   全程可降级：拿不到服务/方法 ⇒ 只 warning，**绝不影响登录**。
            try
            {
                FenceRumor.Services = serviceProvider;
                FenceRumor.ModDir = modPath;
                FenceRumor.ActiveEvents = () => EventEngine.Actives;
                FenceRumor.Info = m => logger.Info(m);
                FenceRumor.Warn = m => logger.Warning(m);
                Patches.FenceRumorPatch.Enable(harmony, m => logger.Info(m));
            }
            catch (Exception ex) { logger.Warning("[EventRotator][Fence传闻] 接入失败（已忽略）：" + ex.Message); }
            // ★ v0.1.3：手续费/实验室免票改为**纯数据落点**，不再对游戏打补丁
            //   （实验室 → 该图 AccessKeys 置空；跳蚤 → globals 税率归零。见 LabsFleaDimension）

            // ★★★ 把 SPT 的 JsonUtil 交给 BossWavePatch：
            //     官方 seasonalevents.json 的条目里有 ListOrT<string>（如 Supports[].BossEscortDifficult 是数组），
            //     只有 SPT 自己的 JsonUtil（带全部自定义转换器）能正确反序列化；
            //     用 System.Text.Json 兜底会报 "could not be converted to ListOrT`1[System.String]"。
            Patches.BossWavePatch.JsonUtil = jsonUtil;
            logger.Info("[EventRotator] JsonUtil 已注入 → 官方季节首领条目可正确反序列化（ListOrT 支持）");

            // ★ 诊断：打印 SPT 静态路由的真实匹配顺序（证实/证伪"我的 mod 改变加载顺序导致路由被遮蔽"）



            // ★ 正解：直接把当前活动的首领改动套到 DB(LocationTable)——进图与地图选择界面(骷髅)一起生效
            //   ★★★【P0 修复后语义】这里 = **一次性初始化**（建模板库 + 记原值快照 + 首轮套用）。
            //   之后每次换活动/关活动，由 `RestoreThenApply()` 里的 `DbNeedsReapply → RestoreDbLayer()`
            //   + `ApplyDbForCurrentEvent()` 负责「先还原上一轮、再按当前活动套用」。
            //   ★ 现在**无条件调用**（旧代码只在 `Actives.Count > 0` 时调）：即使启动时没有活动，也要
            //     **建立原值快照 + 填 _dbBaseByMap**，否则 ① 之后新选的活动永远写不进 DB；
            //     ② D2 的 "ABPS 重建后幂等补写" 因为 `_dbBaseByMap` 为空而全程失效。
            //     没有活动时 `ApplyMap(..., "db")` 自己会直接返回（零写入）。
            try { var n = Patches.BossWavePatch.ApplyToDb(locationTable); logger.Info($"[EventRotator] DB boss apply on LocationTable: {n} maps （一次性初始化+首轮套用）"); }
            catch (Exception ex) { logger.Warning($"[EventRotator] DB boss apply failed: {ex.Message}"); }

            logger.Info($"[EventRotator] ready. active='{string.Join("+", EventEngine.Actives.Select(a => a.Id))}'  (restore-then-apply)");

            // 日志自带的验证指引
            if (EventEngine.DebugLogging)
            {
                logger.Info("[EventRotator] == 日志验证指引 ================================");
                logger.Info("[EventRotator] 1) 搜 [EventRotator][汇总]     → 每张被改动的图分别套用了什么");
                logger.Info("[EventRotator] 2) 搜 [EventRotator][进图]     → 进图瞬间该图的刷新计划（含 ★必刷100%）");
                logger.Info("[EventRotator] 3) 搜 [EventRotator][刷新统计] → 本局实际生成的 bot 角色分布");
                logger.Info($"[EventRotator] 当前等级={EventEngine.LogLevel}（要逐条明细把 config.json 的 logLevel 改成 verbose）");
                logger.Info("[EventRotator] 走私者在客户端里的 bot 类型名 = arenaFighterEvent");
                logger.Info("[EventRotator] ================================================");
            }
        }
        catch (Exception ex) { logger.Error($"[EventRotator] OnLoad failed: {ex}"); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 首次运行时生成默认 config.json（日志等级开关）。
    /// {"logLevel": "basic"} —— off / basic / verbose
    /// 也兼容旧写法 {"debug": true/false}。
    /// </summary>
    /// <summary>完整默认配置（18 键）。既用于首次生成，也用于给旧配置**补齐缺失键**。</summary>
    /// <remarks>
    /// ★ v2.7.0 新增顶层键 <c>disableAllEvents</c>（默认 false）：
    /// 「不启用任何活动：开启后无论如何都不套用任何活动（含 force.txt、轮换、季节自动接管）」/
    /// "Disable all events: when on, no event is ever applied (force.txt, rotation and
    ///  seasonal takeover included)". 加进这份默认表 ⇒ <c>MergeMissingKeys</c> 会给老配置自动补上。
    /// </remarks>
    private static readonly string DefaultConfigJson =
        "{\n"
        + "  \"disableAllEvents\": false,\n"
        + "  \"logLevel\": \"basic\",\n"
        + "  \"sainPresetInject\": true,\n"
        + "  \"rotation\": {\n"
        + "    \"enabled\": true,\n"
        + "    \"mode\": \"random\",\n"
        + "    \"intervalDays\": 7,\n"
        + "    \"slotRebaseHour\": 4,\n"
        + "    \"durationDays\": 0,\n"
        + "    \"count\": 1,\n"
        + "    \"seed\": 20261014,\n"
        + "    \"avoidConflicts\": true,\n"
        + "    \"onlyWhenNoSeasonal\": true,\n"
        + "    \"rewardCountMin\": 1,\n"
        + "    \"rewardCountMax\": 2,\n"
        + "    \"smallCountMax\": 2,\n"
        + "    \"pool\": []\n"
        + "  },\n"
        + "  \"seasonRotation\": {\n"
        + "    \"mode\": \"rotate\",\n"
        + "    \"intervalDays\": 60,\n"
        + "    \"offset\": 0,\n"
        + "    \"order\": [\n"
        + "      \"SPRING\",\n"
        + "      \"SUMMER\",\n"
        + "      \"AUTUMN\",\n"
        + "      \"WINTER\"\n"
        + "    ],\n"
        + "    \"autoOpenSeasonalEvent\": false,\n"
        + "    \"eventPhase\": \"middle\",\n"
        + "    \"seasonalEventDurations\": {\n"
        + "      \"HALLOWEEN\": 13,\n"
        + "      \"CHRISTMAS\": 19,\n"
        + "      \"NEWYEARS\": 2,\n"
        + "      \"APRILFOOLS\": 1\n"
        + "    },\n"
        + "    \"season\": \"SPRING\"\n"
        + "  },\n"
        + "  \"eventSeason\": {\n"
        + "    \"mode\": \"off\",\n"
        + "    \"season\": \"HALLOWEEN\"\n"
        + "  },\n"
        + "  \"vanillaSeasonTakeover\": true,\n"
        + "  \"rewardRotation\": {\n"
        + "    \"enabled\": true,\n"
        + "    \"countMin\": 1,\n"
        + "    \"countMax\": 2,\n"
        + "    \"avoidConflicts\": true,\n"
        + "    \"pool\": [],\n"
        + "    \"seed\": 0\n"
        + "  },\n"
        + "  \"weeklyDraw\": {\n"
        + "    \"mode\": \"off\",\n"
        + "    \"count\": 1,\n"
        + "    \"onlyNoRestart\": true,\n"
        + "    \"seed\": 0\n"
        + "  },\n"
        + "  \"fenceRumor\": {\n"
        + "    \"enabled\": true,\n"
        + "    \"maxPerDay\": 1,\n"
        + "    \"minIntervalMinutes\": 720\n"
        + "  },\n"
        + "  \"autoPairs\": {},\n"
        + "  \"autoBossChance\": {},\n"
        + "  \"autoLootBoost\": {\n"
        + "    \"all_bosses_reserve\": 1.25,\n"
        + "    \"art_of_explosion\": 1.25,\n"
        + "    \"boss_cultist_presence\": 1.5,\n"
        + "    \"bosses_rogues_wander\": 1.25,\n"
        + "    \"bosses_take_a_hint\": 1.5,\n"
        + "    \"cultist_hunting\": 1.5,\n"
        + "    \"cultist_pilgrimage\": 1.5,\n"
        + "    \"factory_cleanup_crew\": 1.25,\n"
        + "    \"full_speed_ahead\": 1.25,\n"
        + "    \"geared_scavs\": 1.25,\n"
        + "    \"hustle\": 1.5,\n"
        + "    \"key_to_salvation\": 1.25,\n"
        + "    \"meeting_of_the_bosses\": 1.5,\n"
        + "    \"permanent_boss_presence\": 1.25,\n"
        + "    \"reshala_vs_glukhar\": 1.25,\n"
        + "    \"sanitar_event\": 1.25,\n"
        + "    \"scav_on_labs\": 1.25,\n"
        + "    \"scav_takeover\": 1.25,\n"
        + "    \"shifting_balance\": 1.5,\n"
        + "    \"smugglers_2024\": 1.5,\n"
        + "    \"targrad_tales\": 1.25,\n"
        + "    \"tarkov_hospitality\": 1.5,\n"
        + "    \"tarkov_mystery\": 1.25,\n"
        + "    \"terragroup_trail\": 1.25\n"
        + "  },\n"
        + "  \"mapFillersEnable\": false,\n"
        + "  \"mapFillers\": {},\n"
        + "  \"allowOnOverhaulMaps\": true,\n"
        + "  \"randomBossMapCount\": 1,\n"
        + "  \"randomBossMapChance\": 100,\n"
        + "  \"seasonAutoPick\": \"newest\",\n"
        + "  \"families\": {\n"
        + "    \"halloween\": {\n"
        + "      \"2022\": [\n"
        + "        \"halloween_2022\"\n"
        + "      ],\n"
        + "      \"2023\": [\n"
        + "        \"halloween_2023\"\n"
        + "      ],\n"
        + "      \"2024\": [\n"
        + "        \"halloween\",\n"
        + "        \"night_of_the_cult\"\n"
        + "      ],\n"
        + "      \"2025\": [\n"
        + "        \"softcore\"\n"
        + "      ]\n"
        + "    },\n"
        + "    \"christmas\": {\n"
        + "      \"2022\": [\n"
        + "        \"santa_2022\"\n"
        + "      ],\n"
        + "      \"2023\": [\n"
        + "        \"permanent_boss_presence\"\n"
        + "      ],\n"
        + "      \"2024\": [\n"
        + "        \"khorovod_2024\",\n"
        + "        \"cultist_pilgrimage\"\n"
        + "      ]\n"
        + "    }\n"
        + "  },\n"
        + "  \"familyGeneric\": {\n"
        + "    \"christmas\": [\n"
        + "      \"christmas\",\n"
        + "      \"christmas_january\",\n"
        + "      \"newyear\"\n"
        + "    ]\n"
        + "  },\n"
        + "  \"seasonBagNoRepeat\": true\n"
        + "}\n";

    /// <summary>
    /// ★ v1.3.9【接管原版季节窗】SPT 默认 `enableSeasonalEventDetection = true`，会**按现实日期**
    /// 自动开官方节日（官方窗口：万圣节 10/28–11/9、圣诞 12/13–12/31、圣诞 1 月档 1/2–1/14、新年 1/1、愚人节 4/1）。
    /// 装了本 mod 的用户会遇到：① 官方节日照旧在过（10/28 实验室出现「仪式」撤离点 + 免票）；② 与自己轮换冲突。
    /// <para>`config.json → "vanillaSeasonTakeover": true`（默认开）时接管：</para>
    /// <list type="number">
    /// <item>磁盘：`SPT_Data\configs\seasonalevents.json` 的 `enableSeasonalEventDetection` 写 false
    ///   （改前留 `.bak-eventrotator`，可回退）——下次启动干净；</item>
    /// <item>内存：反射把 `SeasonalEventConfig.EnableSeasonalEventDetection` 置 false——本局立即生效。</item>
    /// </list>
    /// 关掉该键时只做反向还原（true），不改其它字段。异常只警告，绝不影响启动。
    /// </summary>
    private void ApplyVanillaSeasonTakeover(string modPath)
    {
        try
        {
            var cfgP = Path.Combine(modPath, "config.json");
            var on = true;
            if (File.Exists(cfgP))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgP));
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("vanillaSeasonTakeover", out var v)
                        && (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False))
                        on = v.GetBoolean();
                }
                catch (Exception ex) { logger.Warning($"[EventRotator][季节窗] 读取 vanillaSeasonTakeover 失败，按默认(开)处理: {ex.Message}"); }
            }

            // ★ 2026-10-31：真正生效靠 Harmony 拦截 EnableSeasonalEvents（见 SeasonalTakeoverPatch）
            Patches.SeasonalTakeoverPatch.Active = on;

            var sptRuntime = Directory.GetParent(modPath)?.Parent?.Parent?.FullName;
            var spPath = sptRuntime == null ? null : Path.Combine(sptRuntime, "SPT_Data", "configs", "seasonalevents.json");
            if (spPath != null && File.Exists(spPath))
            {
                var text = File.ReadAllText(spPath);
                var want = on ? "false" : "true";
                var patched = System.Text.RegularExpressions.Regex.Replace(
                    text,
                    "(\"enableSeasonalEventDetection\"\\s*:\\s*)(true|false)",
                    m => m.Groups[1].Value + want,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (patched != text)
                {
                    var bak = spPath + ".bak-eventrotator";
                    try { if (!File.Exists(bak)) File.Copy(spPath, bak, true); } catch { }
                    File.WriteAllText(spPath, patched, new System.Text.UTF8Encoding(false));
                    logger.Info($"[EventRotator][季节窗] 已{(on ? "关闭" : "恢复")}原版按日期自动开节日：seasonalevents.json → enableSeasonalEventDetection = {want}"
                        + "（⚠ 这一项要【完全关闭服务端再重新启动】才会生效；当前这局仍按旧值运行）");
                }
                else
                {
                    logger.Info($"[EventRotator][季节窗] seasonalevents.json 的 enableSeasonalEventDetection 已是 {want}（无需改动；本项每次启动读盘，改过之后须【关闭服务端再重新启动】才按新值运行）");
                }
            }
            else
            {
                logger.Warning("[EventRotator][季节窗] 找不到 SPT_Data/configs/seasonalevents.json ⇒ 磁盘接管跳过");
            }

            if (on)
            {
                try
                {
                    var pi = seasonalEventConfig?.GetType().GetProperty(
                        "EnableSeasonalEventDetection",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (pi != null && pi.CanWrite)
                    {
                        pi.SetValue(seasonalEventConfig, false);
                        logger.Info("[EventRotator][季节窗] 内存已置 false（只影响本局后续读取；SPT 在加载早期就已完成当季判定，同理本局可能仍是官方节日）——真正的生效时间是【关闭服务端后重新启动】");
                    }
                    else
                    {
                        logger.Warning("[EventRotator][季节窗] SeasonalEventConfig 没有可写的 EnableSeasonalEventDetection ⇒ 内存接管跳过（磁盘已改，下次启动生效）");
                    }
                }
                catch (Exception ex) { logger.Warning($"[EventRotator][季节窗] 内存接管失败: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[EventRotator][季节窗] 接管原版季节窗失败（不影响游戏）: {ex.Message}");
        }
    }

    private static void EnsureDefaultConfig(string modPath, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            var f = Path.Combine(modPath, "config.json");
            // ★★★ v1.1.4【BugFix·旧残缺 config 卡住】原来「文件存在就 return」——
            //   可是**旧版客户端**在全新安装时会写出只有 6 个键的 config.json
            //   （logLevel / sainPresetInject / rotation / rewardRotation / seasonRotation / eventSeason），
            //   缺 autoLootBoost(24 条爆率) / families(族表) / seasonAutoPick / autoPairs / mapFillers …
            //   ⇒ 用户换上完整新包也没用：文件还在，永远不补 ⇒ 表现为「季节不轮换(夏天) + 没有任何活动」。
            //   现在改成：文件存在也**逐键补齐缺失项**（只加缺的，绝不覆盖用户已改的值）。
            if (File.Exists(f))
            {
                try { MergeMissingKeys(f, log); return; }
                catch (Exception ex) { log?.Warning($"[EventRotator] 合并 config 默认键失败（忽略，继续启动）: {ex.Message}"); return; }
            }
            // ★★★ v1.1.1【BugFix·首次安装无轮换】原来只写 logLevel/sainPresetInject，
            //   而 `Rotation.Load()` 发现没有 `rotation` 节点就**直接 return**（Enabled 保持 C# 默认 false）
            //   ⇒ 全新安装（尤其单独下 mod zip、没有随包 config.json 的人）第一次进游戏**永远不会有轮换**，
            //   而且第二次也不会 —— 因为没有任何地方会补写 rotation 节点。
            //   ⇒ 现在把 rotation 段一起写进默认配置（与本包实测配置一致：每 7 天 1 档、凌晨 4 点换档）。
            var def = DefaultConfigJson;
            File.WriteAllText(f, def, new System.Text.UTF8Encoding(false));
            log?.Info($"[EventRotator] 已生成默认 config.json（含 rotation 段，轮换默认开启）: {f}");
        }
        catch (Exception ex) { log?.Warning($"[EventRotator] 生成 config.json 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★ v1.1.4：给**已存在**的 config.json 补齐默认键（只加缺失的键，绝不覆盖用户改过的值）。
    /// 背景：旧版客户端全新安装时会写出只有 6 个键的残留配置，缺 rotation / autoLootBoost / families / seasonAutoPick…
    /// ⇒ 表现是「季节不换（一直是夏天）+ 没有任何活动」。装完整新包也没用，因为老逻辑见到文件就 return。
    /// </summary>
    private static void MergeMissingKeys(string path, ISptLogger<EventRotatorServer> log)
    {
        var userText = File.ReadAllText(path);
        var user = System.Text.Json.Nodes.JsonNode.Parse(userText) as System.Text.Json.Nodes.JsonObject;
        var def = System.Text.Json.Nodes.JsonNode.Parse(DefaultConfigJson) as System.Text.Json.Nodes.JsonObject;
        if (user == null || def == null) return;

        var added = new List<string>();
        foreach (var kv in def)
            if (!user.ContainsKey(kv.Key)) { user[kv.Key] = kv.Value?.DeepClone(); added.Add(kv.Key); }
        if (added.Count == 0) return;                       // ✅ 正常配置：完全静默

        File.WriteAllText(path,
            user.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
        log?.Warning($"[EventRotator] 已为旧 config.json 补齐 {added.Count} 个缺失键：{string.Join(", ", added)}");
        log?.Warning("[EventRotator] （只补缺的键，你改过的值没动）—— 旧版客户端曾把配置写残成 6 个键，"
                   + "这正是「季节不换 / 没有任何活动」的原因。");
    }

    /// <summary>★ 核心：先还原上一轮的所有改动，再套用当前活动（v1.97 起支持**多个活动同时生效**）。</summary>
    private void RestoreThenApply()
    {
        Restore();                       // 1) 还原
        // ★★★ v2.6.12【P0 簇6·静态状态清零】必须排在 Restore() 之后、套用之前。
        //   `Restore()` 只回放 `_restoreActions` 里的闭包，**管不到"静态登记位"** ⇒ 换活动/关活动时，
        //   上一轮登记的对象会残留；而套用侧全是"只在活动声明了该字段时才写"（`if (ev.X != null)`）
        //   ⇒ 新活动没声明 = 旧值继续生效 = **关不掉的活动**。三条同型（正例对照见方法内注释）。
        ClearStaleStaticState();
        LabsFleaDimension.ResetFeeMultiplier();   // ★ v0.1.3 免挂单费也回到默认（1.0）
        // ★ task-34 ⑧-B：松散战利品的 `weightMult` 是**倍率缩放**（不是赋值），重复套用会累积
        //   ⇒ 还原之后必须清掉幂等表，否则换活动时新倍率会被当成"已缩放"而跳过。
        LooseLootWeightDimension.ResetApplied();
        // ★ 同型：地面战利品注入的幂等表（`(活动id|地图)`）也要清 —— 它的还原闭包自己会 remove，
        //   这里再清一次是兜底（防止"还原闭包没跑成"时把新活动误判成"已注入"）。
        LooseLootInjectDimension.ResetApplied();
        // ★ 同型：容器注入的幂等表 + 生成期登记表也要清。
        ContainerLootInjectDimension.ResetApplied();
        // ★ 同型（task-28）：静态战利品容器的注入计划也要清 —— transformer 在没计划时是直通，
        //   但 `_active` 残留会让下一个活动被误判成"已登记"而跳过。
        StaticLootAddDimension.ResetApplied();
        SeasonalZombieDimension.ResetApplied();   // ★ 万圣节感染者：清幂等标记（改动本身由 _restoreActions 还原）
        HalloweenIllumination.ResetApplied();      // ★ 藏身处万圣节照明：清幂等标记（改动由 _restoreActions 还原）
        // ★ 同型：集水器效率也是**倍率缩放** ⇒ 还原后必须清幂等表，否则换活动时新倍率被当成"已缩放"跳过。
        ExtraDimensions.ResetRecipeScaled();
        // ★ 同型：头像的"延迟补做"是**静态挂起位** ⇒ 换活动/关活动时必须清掉，
        //   否则上一轮要求换头像的活动关掉后，补做钩子还会继续把头像改回去（= 关不掉的活动）。
        ExtraDimensions.ResetTraderIconPending();
        // ★ 同型：按图门控也是**静态登记位**（池子条目 + 过滤地图）⇒ 换活动必须先清，
        //   否则上一轮"只海关"的过滤会残留到下一个活动上。
        MapGateDimension.Reset();

        // ═══════════════════════════════════════════════════════════════════════════
        // ★★★ v2.7.0【硬开关 · 唯一入口】`config.json → disableAllEvents = true`
        //   ⇒ 上面那一整段「还原」已经跑完（`Restore()` 回放全部还原闭包 + 清静态登记位），
        //     这里再补上 `Restore()` **摸不到的三层**（DB / 原生季节镜像 / 外部联动 / 天气季节），
        //     然后**直接返回**，绝不往下走任何 `Apply*` ⇒ 裸 SPT 状态。
        //   ⚠ 它是本方法**第一个也是唯一一个**出口：轮换 / 季节阶段 / 日期窗 / 奖励轮换 / 周抽签
        //     / ItemSupply / AirdropBaseline 全部不会执行（= 与"活动一个都没开"语义一致，
        //     且比旧的"无活动分支"更彻底 —— 旧分支仍会跑天气季节轮换与季节活动阶段）。
        // ═══════════════════════════════════════════════════════════════════════════
        if (EventEngine.DisableAllEvents)
        {
            _disableAllEventsWasOn = true;
            HardRestoreForDisableAllEvents();
            return;
        }
        // ★ v2.7.0 反向切换：上一轮是"裸 SPT"，这一轮开关关掉了 ⇒ 从这一行起恢复正常轮换/套用。
        if (_disableAllEventsWasOn)
        {
            _disableAllEventsWasOn = false;
            SigChanged("disableAllEvents", "false");
            logger.Warning("[EventRotator] disableAllEvents=false ⇒ 恢复正常轮换逻辑");
        }

        var evs = EventEngine.Actives;

        // ★★★ 2026-10-29【周 / 周末抽签】
        //   在**这里**（Actives 定好之后、所有维度套用之前）把本周抽中的活动补进 Actives ——
        //   只调既有列表，**不改 EventEngine 的活动判定**（SelectActive 一个字没动）。
        //   mode=off 时 SyncWeeklyDraw 什么都做（返回空、列表不动、日志不打）⇒ 与改动前逐字节一致。
        try { Rotation.SyncWeeklyDraw(evs, DateTime.Now); }
        catch (Exception ex) { logger.Warning("[EventRotator][周抽签] 应用失败（已忽略）: " + ex.Message); }

        // ★★★ 2026-10-29【外部 mod 联动 · 实验室停电（Blackout 2026-07）】
        //   `blackout_2026` 在生效集合里 ⇒ 把第三方 Blackout / BlackDivServer 的两个概率拉到 100；
        //   不在 ⇒ 从 .bak-eventrotator 还原用户原值。
        //   放在这里（Actives 最终确定之后）⇒ 启动/无活动/正常套用**三条路都会走到**，只加这一个调用点。
        //   ApplyBlackout 内部有状态缓存：状态没变**不写盘**（避免每次进图都触发黑狐的热重载）。
        try { ExternalBridges.ApplyBlackout(ExternalBridges.EventActive()); }
        catch (Exception ex) { logger.Warning("[EventRotator][外部联动] 应用失败（已忽略）: " + ex.Message); }

        // ★ v0.1.5 原生僵尸模式（实验室僵尸）：由活动里的 nativeZombies 声明决定
        WantNativeZombies = evs.Any(e => e.NativeZombies == true);
        // ★★★ v1.0.1【BugFix·全量审计】原来的致命缺陷：
        //   `EventType=Halloween` 的注入被绑在 `WantNativeZombies`（要不要开原生僵尸）上，
        //   而 events.json 里 **3 个万圣节活动全都没声明 `nativeZombies`**（对比：5 个圣诞活动 5/5 都声明了 nativeChristmas）
        //   ⇒ `WantNativeZombies` 恒为 false ⇒ **EventType 从来没被设成 Halloween**（日志实测 34 次「已还原→[None]」、0 次「→[Halloween]」）
        //   ⇒ 客户端永远不知道这是万圣节局 ⇒ **仪式不触发、感染条不显示**。
        //   现在单独立一个「万圣节族活动在跑」的标记：只要有 seasonalEvent=Halloween 的活动生效，
        //   就注入 EventType（与"要不要开原生僵尸"解耦）。
        // ★ v1.0.1【BugFix】把"是否允许 halloweensummon"推给补丁层 —— 补丁层从磁盘读 seasonalevents.json，
        //   只改内存的 ApplyHalloweenSummonPolicy 拦不住它（愚人节会误刷 2023 召唤仪式）。
        try { Patches.BossWavePatch.AllowHalloweenSummon = HalloweenSummonAllowed(); } catch { Patches.BossWavePatch.AllowHalloweenSummon = false; }
        WantHalloweenEventType = evs.Any(e =>
        {
            try { return string.Equals(e?.SeasonalEvent, "Halloween", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        WantNativeChristmas = evs.Any(e => e.NativeChristmas == true);   // ★ v0.1.5 圣诞复刻
        if (WantNativeZombies)
            logger.Warning("[EventRotator][万圣节] 本次套用含 nativeZombies 活动 → 开启 SPT 原生僵尸模式"
                         + "（实验室 100% 感染 + 官方会清空实验室首领表）");

        // ★★★【P0 修复 · DB 层「写进 DB 后不还原」】活动集合变了 ⇒ 先把上一轮写进 DB 的东西**还原**，
        //   再按当前活动重新**套用**。
        //   旧实现把"还原上一个活动"藏在 `BossWavePatch.ApplyToDb` 的 else 分支里，而 ApplyToDb 每进程
        //   **只被调用一次**（OnLoad）⇒ 还原分支永远不可达（日志实证：18 次「DB boss apply」、0 次还原）
        //   ⇒ 换活动后：首领追加 / BossChance / 红骷髅 / BossZone / 5 个 hostility 名单字段 /
        //     NonWaveSpawnBotsLimitPerPlayer 全部残留。
        //   顺序 = 先 Restore 再 Apply（与其它维度一致）；`DbNeedsReapply` 只在集合真的变了时为真 ——
        //   集合没变的局里 DB 已是目标状态，重复还原+套用既无意义、又会让每局多出几十行 db 日志。
        try
        {
            if (Patches.BossWavePatch.DbNeedsReapply)
            {
                RestoreDbLayer();                        // ① 还原（每步独立 try/catch，见方法注释）
                if (evs.Count > 0)
                {
                    var n = Patches.BossWavePatch.ApplyDbForCurrentEvent(locationTable);
                    logger.Info($"[EventRotator] DB boss re-apply on LocationTable: {n} maps"
                              + "（先还原上一轮，再按当前活动套用）");
                }
                else
                {
                    Patches.BossWavePatch.MarkDbRestored();   // 无活动 ⇒ 还原即终态，清掉记账避免每轮重跑
                    logger.Info("[EventRotator] 无活动在跑 ⇒ DB 首领改动已全部还原（追加/概率/骷髅/zone/hostility）");
                }
            }
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] DB 还原/重新套用失败（已跳过）: {ex.Message}"); }

        if (evs.Count == 0)
        {
            // 无活动 → 还原后即可（季节活动也回到"自动日期判定"）
            TryForceSeasonal(null);
            // ★ v2.1.8 全局物品供给是**常驻**的，无活动时也要生效
            try { ItemSupply.Apply(logger, a => _restoreActions.Add(a)); }
            catch (Exception ex) { logger.Error($"[EventRotator] 全局物品供给失败: {ex.Message}"); }
            // ★★★ v2.6.42【常驻】单局空投上限 ×3 + 概率下限 0.5 —— **无活动时也必须生效**
            //   （幂等 + 拿不到 LocationTable 时留待下次重试；不登记还原）
            AirdropBaseline.Ensure(logger);
            ApplyWeatherSeason(null);
            ApplyEventSeason(null, logger);
            // 无活动：不需要任务可完成性检查（没有活动 = 没有活动任务被打开）
            // ★★★ v2.6.13【任务门控】但**门控必须照做**：没有任何活动在跑 ⇒ 所有门控任务都要"关窗"
            //   （塞进 eventQuests + season=None 隐藏）+ 轮换重置（全部活动都"退出"了）。
            ApplyQuestGate(new List<EventDef>());
            _fleaPassMax = null;                     // ★ v2.6.38：新一轮套用 ⇒ 清空「本轮更严格值」
            WriteActiveStatus(evs);
            return;
        }

        // 2) ★★★ v0.1.4 先做「自动配对 + 地图补位 + 自动爆率」的展开：
        //    某些活动本身改了敌人/首领，但玩家收益没跟上、或者把别图的首领抽空了
        //    （用户点名的例子：邪教徒类自带爆率、八影会谈自带走私者补位）。
        //    这里把"伙伴活动"展开成一个统一列表，后面照常逐个套用 —— 伙伴活动就是普通活动，无特判。
        //    ⚠ 默认只配"地图补位"（mapFillers）；显式配对表 autoPairs 默认空，由使用者决定。
        var expanded = ExpandAutoPartners(evs);
        logger.Info($"[EventRotator] 组合展开：{evs.Count} → {expanded.Count} 个活动 "
                    + $"[{string.Join(",", expanded.Select(x => x.Id))}]"
                    + $"（autoPairs {EventRotatorServer.AutoPairs.Count} 条 / mapFillers {EventRotatorServer.MapFillers.Count} 张图）");
        // ★★★ v0.1.4 任务可完成性：检查"开了任务但没刷对应敌人"的情况，并自动补伙伴
        //    ⚠ 必须在 `ApplyQuestGate` 之前跑 —— 它可能再往 expanded 里加活动（补上会刷敌人的伙伴），
        //      后面那个 foreach 就会把新加的一起套用，而门控读的就是那个 foreach 之后的 `expanded`。
        CheckQuestCompletable(expanded);

        // 3) 逐个活动套用。倍率类会被后一个活动覆盖（与"同时开"的语义一致：
        //    多个活动改同一项时，events.json 里靠后的那个说了算），
        //    追加/解锁/任务类则各自累加。
        _fleaPassMax = null;                       // ★ v2.6.38：本轮套用开始 ⇒ 清空「本轮更严格值」
        // ★★★ v2.6.42【常驻 · 关键顺序】**必须在活动循环之前**先把"空投上限 ×3 + 概率下限 0.5"这条基线打好：
        //   活动的 `AirdropDimension.ApplyFrequency(mult)` 是**在当前值上再乘**（并登记还原 ⇒ 活动结束回到基线），
        //   所以顺序反了就会出现"活动 → 基线"导致活动结束后基线丢失（还原成原版）✗
        AirdropBaseline.Ensure(logger);
        foreach (var ev in expanded)
        {
            // ★★★ v2.6.37【只在该地图生效】**必须是本活动维度的第一件事** ——
            //   它要在 `ApplyBotEquipment(ev)`（里面读 `BotEquipmentDimension` 的 `botLootAdd_`）
            //   和 `ApplyAirdropAppend(ev)` 之前就把过滤登记好，否则那两处会当成"不过滤"而全图生效。
            MapGateDimension.SetOnlyOnMap(ev.OnlyOnMap);
            ApplyLoot(ev, expanded);
            ApplyTraders(ev);
            ApplyFlea(ev);
            // ★★★ v2.6.13【任务门控】`ApplyQuests(ev)` **已删除** —— 它按"单个活动"的视角改 eventQuests
            //   （只做"解锁"、还原时按原 season 放回 ⇒ 方向反了 + 跨活动泄漏）。
            //   取而代之的是主循环之后**一次调用**的 `ApplyQuestGate(expanded)`（全局视角）。
            ApplyAbps(ev);                   // ★ 与 ABPS 兼容：让 ABPS 自己去刷活动敌种
            ApplyScavSpawnSuppress(ev);      // ★★★ A1 修复：用 ABPS 的 maxBotSpawns 真正压住普通 SCAV
            ApplyBotGearUpgrade(ev);         // ★★★ 清场小队：更好的枪/弹/插板（改我们自己的 bot 产出物）
            ApplyBotAmmoAdd(ev);            // ★★★ 补本体模板缺失的口径弹药 / 装备槽
            ApplyAirdrop(ev);                // ★ v2.1.6 空投维度（打开 radar 空投 + 塞缺失物品）
            ApplyGlobals(ev);                // ★ v2.2.0 globals 维度（伤害/技能/经验/口渴）
            // ★★★ v2.6.44【P0 修复】**必须与 ApplyGlobals 平级** —— `stimulatorBuffsAdd` 与 `globals`
            //   无关；原来它挂在 `ApplyGlobals` 里、被 `if (ev.Globals == null) return;` 挡死
            //   （用户撤掉 `globals` 后「超人增益」整条消失 ✗ 用户实测抓到）
            ApplyStimulatorBuffs(ev);
            ApplyHideoutTrader(ev);          // ★ v2.3.0 藏身处秒建/商人折扣/bot预设
            ApplyBotEquipment(ev);           // ★ v2.4.0 bot 装备池（火箭筒 SCAV 等）
            ApplyBotLootStack(ev);           // ★★★ v2.6.74 必定携带 + 指定数量（走私者 GP 币 10~20）
            ApplyBotEquipmentReplace(ev);    // ★★★ v0.1.5 bot 装备池**整体替换**（casus_belli 劫掠者面具）
            ApplyLabsFlea(ev);               // ★ v2.5.0 实验室免票 + 跳蚤手续费
            ApplyItems(ev);                  // ★ v0.1.3 items 维度（霰弹/近战/针剂 Buff）
            ApplyTaggedAndCursed(ev);        // ★ v0.1.5 诅咒标签（Tagged and Cursed，官方 2022 万圣节内容）
            ApplyEventSettings(ev);          // ★ v2.6.8 官方「完整版万圣节模式」（所有白图 → 电闪雷鸣的黑夜）
            SeasonalZombieDimension.Apply(ev, a => _restoreActions.Add(a));   // ★ 万圣节感染者（僵尸）：enabled/感染率/Tagilla/业力
            HalloweenIllumination.Apply(ev);   // ★ 藏身处万圣节照明（万圣节家族活动；EventType 的唯一写入口）
            // ★ 把最终感染率写成报表文件，客户端地图界面据此画标记（不影响主流程）
            try
            {
                InfectionReport.Info = m => { try { logger.Info(m); } catch { } };
                InfectionReport.Warn = m => { try { logger.Warning(m); } catch { } };
                InfectionReport.Write(EventEngine.ModPath, GlobalTableRef);
            }
            catch { }
            ApplyAirdropAppend(ev);          // ★ v0.1.5 空投「追加」语义（区别于 airdropAddLoot 的整箱替换）
            ApplyPlayerScav(ev);             // ★ v0.1.5 玩家 SCAV 出生装备强化（官方 PScav武装強化 的正解）
            ApplyPrice(ev);                  // ★ v0.1.5 手册价改写（官方 Dogtag Sales 的 ₽2,646 等）
            ApplyFleaBlacklist(ev);          // ★ v0.1.5 跳蚤禁售表（官方 PBP 2026-07-29 首领取缔条款）
            ApplyAssortRemove(ev);           // ★ v0.1.5 按官方清单把商品从商人下架（Ammo Shortage 的 31 种弹药）
            ApplyBotTypeLimits(ev);          // ★ v0.1.5 按图设置 bot 类型上下限（官方"海关不刷普通 SCAV"）
            ApplyTraderAssort(ev);           // ★ v0.1.5 按商人粒度的报价折扣（官方"某商人折扣"类活动）
            ApplyTransits(ev);               // ★ v0.1.3 Transit 维度（马拉松：全图转场口）
            ApplyDrumMags(ev);               // ★ v0.1.3 弹鼓概率（纯 items 落点，对 PBS 也有效）
        }
        // ★ v0.1.5 修正（审计发现）：季节不再由「最后一个活动」决定 —— 那之后只要跟一个 seasonalEvent 为空的活动
        //   就会把季节重置回「按日期自动」（本次只是碰巧被季节轮换先强推了 HALLOWEEN 才没暴露）。
        //   改为取**最后一个声明了 seasonalEvent 的活动**；一个都没有才传 null（= 回到自动判定）。
        var seasonalOwner = expanded.LastOrDefault(x => !string.IsNullOrEmpty(x.SeasonalEvent) && x.SeasonalEvent != "None");
        TryForceSeasonal(seasonalOwner);

        // ★ v2.1.8 全局物品供给（常驻，不跟活动绑定）
        try { ItemSupply.Apply(logger, a => _restoreActions.Add(a)); }
        catch (Exception ex) { logger.Error($"[EventRotator] 全局物品供给失败: {ex.Message}"); }

        ApplyWeatherSeason(evs.Count > 0 ? evs[evs.Count - 1] : null);
        ApplyEventSeason(null, logger);
        if (WantNativeZombies)
        {
            FixZeroInfectionMaps();                                                // ① 官方配置 0 的图先归零
            ApplyInfectionRamp(evs.LastOrDefault(x => x.InfectionRamp != null));   // ② ★ 动态感染扩散
            RefreshInfectedMaps();                                                 // ③ 白名单跟着扩散走（避免"显示有感染却没僵尸"）
            LogInfectionState();                                                   // ④ 自证日志
        }
        WriteActiveStatus(evs);

        // ★★ 白天入夜：取本批活动中最大的强制概率（没有活动声明 = 关闭 ⇒ 恢复原版昼夜）
        try
        {
            var nightChance = evs.Where(x => x.ForceNightRaid.HasValue)
                                 .Select(x => x.ForceNightRaid.Value)
                                 .DefaultIfEmpty(0d).Max();
            Patches.BossWavePatch.SetForceNight(nightChance);
        }
        catch (Exception exN) { logger.Warning($"[EventRotator] 入夜设置失败: {exN.Message}"); }

        // ★★★ v2.6.12【P0 簇6·漏掉一整层维度】这里原来遍历的是 `evs`（用户显式开启的活动），
        //   而上面主循环（ApplyLoot/ApplyTraders/…）遍历的是 `expanded` = evs + autoPairs 伙伴 + mapFillers 补位
        //   ⇒ **自动配对/补位进来的伙伴活动漏掉了整整一层维度**（购买上限/BOSS 血量/瞄准抖动/惯性/受伤/
        //   撤离点与转场/装备注入/官方装备·战利品·外观表/诅咒标签…全都不套用），
        //   但它们其它维度又套用了 ⇒ 半套用状态（最难查的一类）。
        //   本机 `config.json` 的 `autoPairs` 默认 `{}`、`mapFillersEnable=false` ⇒ 现在不发作，属**排雷**。
        //   语义对齐既有设计：`ExpandAutoPartners` 的注释就写着"伙伴活动就是普通活动，无特判" ⇒ 主循环
        //   和这里必须用同一个列表。
        //   ⚠ 顺带修正日志口径：伙伴活动现在也会打 `applied '<id>'`（此前套用了却不出现在日志里）。
        foreach (var ev in expanded)
        {
            logger.Info($"[EventRotator] applied '{ev.Id}' ({ev.NameZh})");
            ApplyExtraDimensions(ev);          // ★ v0.1.5「重验后补做」的维度（购买上限/血量/瞄准抖动/惯性/受伤/撤离点/转场/装备注入）
            ApplyRelaxLevelQuests(ev);         // ★ 第 9 项：放开假的等级门槛（Level ≥99 → 1；活动关闭还原）
        }
        // ★ v0.1.5：季节"官方流程复刻"就地调用（幂等）——强制开启的活动不走季节预处理分支，
        //   所以在这里按"本次套用的活动声明"直接决定开/关，保证圣诞/万圣节两条复刻都能生效。
        PrepareHalloweenNative(WantNativeZombies);
        PrepareChristmasNative(WantNativeChristmas);
        // ★★★ v2.6.13【任务门控】主循环之后**一次调用**（全局视角）：
        //   · ① 活动在跑 ⇒ 它的任务闭包从 eventQuests 移出（可见）；没在跑 ⇒ 塞进去 + season=None（隐藏）
        //   · ② 轮换（活动集合发生变化）⇒ 该活动任务闭包在**所有 profile** 里"只清进度"（含 Success）
        //   ⚠ 必须在 `foreach (var ev in expanded)` 之后 —— 那时 `expanded` 才是最终集合。
        ApplyQuestGate(expanded);

        // ★★★ A1（task-26）**无 ABPS 的兜底路径**：D2 的 hook 挂在 ABPS 的
        //   `ReplaceOriginalLocationForMap` / `ReplaceOriginalLocations` 上 —— 没装 ABPS 时**根本挂不上**
        //   （D2 会打"未发现 MapSpawns 类型"的降级日志），但那时 SPT 用原版 `Base.Waves` 出 SCAV，更需要压。
        //   所以这里自己遍历 `locationTable` 做一遍；装了 ABPS 时这一步只是重复压（幂等），无害。
        try { EventScavSuppress.SuppressAllDbBases(); }
        catch (Exception ex) { logger.Warning($"[EventRotator][SCAV抑制] 兜底路径失败（已跳过）: {ex.Message}"); }

        if (evs.Count > 1)
            logger.Warning($"[EventRotator] 本次同时生效 {evs.Count} 个活动：{string.Join(" + ", evs.Select(a => a.Id))}");
    }

    /// <summary>
    /// 【与 ABPS 兼容】只做**只读探测 + 诊断**，打印 ABPS 对该敌种的 spawnChance。
    /// ⚠ 本 mod **绝不修改 ABPS 的任何文件/配置** —— 缺失的活动敌种由本 mod 在 raid 时自行追加。
    /// </summary>
    private void ApplyAbps(EventDef ev)
    {
        try
        {
            if (!AbpsCompat.Probe()) { logger.Info("[EventRotator] 未检测到 ABPS（本 mod 独立工作）"); return; }
            if (ev?.ForceAbpsSpawns == null || ev.ForceAbpsSpawns.Count == 0) return;
            foreach (var kv in ev.ForceAbpsSpawns)
                foreach (var map in kv.Value ?? new List<string>())
                {
                    var m = (map ?? "").ToLowerInvariant();
                    var sc = AbpsCompat.ReadSpawnChance(kv.Key, m);
                    logger.Info($"[EventRotator][只读诊断] ABPS {kv.Key}.SpawnChance[{m}] = {(sc < 0 ? "未就绪/无此键" : sc.ToString())}"
                                + (sc == 0 ? "  → ABPS 不会加它，本 mod 将在进图时自行追加" : ""));
                }
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] ABPS 只读诊断失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ A1 修复（task-26）：把活动声明的图的**普通 SCAV** 压掉。
    ///
    /// <para><b>硬约束</b>：用户拍板"<i>不动别的 mod，只改我们自己的</i>" ⇒
    /// **不碰 ABPS 的配置文件、不碰它的运行期对象、不给它打新补丁**。
    /// 本方法只把"要压哪些图"登记给 <see cref="EventScavSuppress"/>；
    /// 真正的压制发生在**我们已有的 D2 postfix**里（ABPS 写完 <c>Base.Waves</c> 之后），
    /// 写的是 **SPT 自己的 DB 对象**（<c>locationTable</c> 的 <c>LocationBase</c>）。</para>
    ///
    /// <para>为什么必须挂在 ABPS 之后：ABPS <c>MapSpawns.ReplaceOriginalLocations</c>
    /// / <c>ReplaceOriginalLocationForMap</c> 会 <c>Base.Waves = cloner.Clone(_scavMapCache[map])</c>
    /// **整体替换**该表（源码 <c>MapSpawns.cs:170-171</c> / <c>:223-224</c>）⇒ 提前写必被覆盖。
    /// 我们 D2 的 postfix 正好在它之后 ⇒ 复用即可，且每局结束都会重压（天然不被冲掉）。</para>
    /// </summary>
    private void ApplyScavSpawnSuppress(EventDef ev)
    {
        try
        {
            if (ev?.ScavSpawnSuppress == null || ev.ScavSpawnSuppress.Count == 0) return;
            EventScavSuppress.AddWanted(ev.ScavSpawnSuppress);
            logger.Info($"[EventRotator][SCAV抑制] '{ev.Id}' 登记压制：{string.Join(", ", ev.ScavSpawnSuppress)}"
                      + "（落点=我们的 DB 对象，在 ABPS 的 Waves 重建之后执行）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][SCAV抑制] {ev?.Id} 登记失败（已跳过）: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ 清场小队装备差异化（task-33）：登记"某个 bot role 生成时要升级装备"。
    ///
    /// <para>只登记规格；真正的改写发生在 `BotGenerator.PrepareAndGenerateBot` 的 postfix
    /// （<c>Patches/BossWavePatch.cs</c> 的 <c>BotGenerated</c>，见 <see cref="EventGearUpgrade"/>）。
    /// 那样拿到的才是**最终产出物**（含被 APBS 接管的装备生成结果），改 SPT 权重池不保证生效。</para>
    /// </summary>
    private void ApplyBotGearUpgrade(EventDef ev)
    {
        try
        {
            if (ev?.BotGearUpgrade == null || ev.BotGearUpgrade.Count == 0) return;
            foreach (var kv in ev.BotGearUpgrade)
            {
                var d = kv.Value;
                if (d == null) continue;
                // ★★★ 修复（task-42）：原来这里只查 BetterAmmo / BetterPlates / WeaponTpl **三个**字段，
                //   而 BotGearUpgradeDef 后来长到了 7 个字段 ⇒ 只用新字段的活动会被**整段 continue 跳过**，
                //   **连登记行都不打**（Lead 实跑确认：arms_race 只有 durabilityPercent，登记行缺失）。
                //   ⇒ "是否有活干"必须覆盖**全部**字段，否则以后每加一个字段都会再犯一次。
                bool hasWork = d.BetterAmmo || d.BetterPlates || d.BiggestMagazine
                            || !string.IsNullOrEmpty(d.WeaponTpl)
                            || !string.IsNullOrEmpty(d.AddLauncherTpl)
                            || (d.ExtraAmmoTpls != null && d.ExtraAmmoTpls.Count > 0)
                            || (d.DurabilityPercent > 0 && d.DurabilityPercent < 100);
                if (!hasWork) continue;
                d.Role = kv.Key;                       // 字典键就是 bot role
                EventGearUpgrade.AddSpec(d);
                logger.Info($"[清场小队] '{ev.Id}' 登记 {kv.Key} 装备升级："
                          + $"更好弹药={d.BetterAmmo} 更好插板={d.BetterPlates} 大弹鼓={d.BiggestMagazine}"
                          + $" 换枪={d.WeaponTpl ?? "(不换)"} 下挂={d.AddLauncherTpl ?? "(无)"}"
                          + $" 追加弹药={((d.ExtraAmmoTpls == null) ? 0 : d.ExtraAmmoTpls.Count)}"
                          + $" 低耐久={((d.DurabilityPercent > 0) ? (d.DurabilityPercent + "%" + (d.WesternOnly ? "(仅西式)" : "")) : "(不设)")}");
            }
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][清场小队] {ev?.Id} 登记失败（已跳过）: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ 补本体 bot 模板缺失的口径弹药 / 装备槽（task-34）。
    /// 只登记规格；真正的写入在 <see cref="EventBotTemplateFix.Apply"/>（写运行期 `BotTable`，不写盘）。
    /// </summary>
    private void ApplyBotAmmoAdd(EventDef ev)
    {
        try
        {
            if (ev?.BotAmmoAdd == null || ev.BotAmmoAdd.Count == 0) return;
            foreach (var kv in ev.BotAmmoAdd)
            {
                var d = kv.Value;
                if (d == null) continue;
                bool hasAmmo = d.Ammo != null && d.Ammo.Count > 0;
                bool hasEq = d.Equipment != null && d.Equipment.Count > 0;
                if (!hasAmmo && !hasEq) continue;
                d.Role = kv.Key;
                EventBotTemplateFix.AddFix(d);
                logger.Info($"[弹药补全] '{ev.Id}' 登记 {kv.Key}：口径补全={d.Ammo.Count} 项 装备槽补全={d.Equipment.Count} 项");
            }
            EventBotTemplateFix.Apply();   // 幂等，可重复调用
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][弹药补全] {ev?.Id} 登记失败（已跳过）: {ex.Message}"); }
    }

    // ---------- 还原 ----------
    private void Restore()
    {
        try
        {
            // 爆率：从基准复位
            foreach (var kv in _baseLoose) if (locationConfig.LooseLootMultiplier.ContainsKey(kv.Key)) locationConfig.LooseLootMultiplier[kv.Key] = kv.Value;
            foreach (var kv in _baseStatic) if (locationConfig.StaticLootMultiplier.ContainsKey(kv.Key)) locationConfig.StaticLootMultiplier[kv.Key] = kv.Value;
            // 其余被改动的（商人/跳蚤/任务）逐条还原
            // ★★★ v2.6.11【P0 通用修复·还原顺序漂移】全量审计 verify-c §②-4 提出、Lead 复核采纳。
            //   `_restoreActions` 是 **append-only**，旧实现**顺序**回放 ⇒ 同一字段被两个活动先后修改时：
            //     活动 A 先写（快照 = 原值），活动 B 后写（快照 = **已被 A 改过的值**）；
            //     顺序回放 ⇒ A 还原成原值，然后 B 又把它写成"A 改过的值" ⇒ **最终没还原**。
            //   本机日志铁证（spt20261014.log）：`configs/airdrop.json` 里 `airdropTypeWeightings.radar`
            //   原值是 **0**，日志里却能看到 `权重 'radar': 10000 → 300` —— 单独跑时是 `0 → 300`。
            //   ⇒ 逆序回放（LIFO）才符合"恢复现场"的语义：后改的先还原，最终精确回到最初的快照。
            //   影响面：所有走 `_restoreActions` 的维度（globals 倍率 / 爆率 / 跳蚤 / 空投 / AccessKeys /
            //   撤离点 / 首领表 …），也修掉 `labsFreeAccess` 两活动同轮时把实验室门卡永久清空的问题。
            for (int i = _restoreActions.Count - 1; i >= 0; i--) { try { _restoreActions[i](); } catch { } }
            _restoreActions.Clear();
            EventScavSuppress.ClearRound();  // ★ A1：清空"要压的图"，下一轮由活动数据重新填充（天然可还原）
            EventGearUpgrade.ClearRound();   // ★ 清场小队：清空装备升级规格（活动关掉就不再改，= 这里的"还原"）
            BotLootStackDimension.ClearRound();  // ★ v2.6.74 必定携带规格：同上（产出物层每局一次性）
            TraderAssortAddDimension.LimitsGloballyRemoved = false;   // ★ v2.6.74 本轮「取消购买上限」标志复位
            EventBotTemplateFix.Restore();   // ★ 弹药补全：逆序 LIFO 还原（先还原再清空规格）
            // ★★★【整体替换 bot 装备池】把整槽池子写回**原池实例**、装备概率写回原值。
            //   必须在这里（与其它还原同一处）：否则活动一关，劫掠者仍是 6 款面具。
            BotEquipmentReplaceDimension.Restore();
            // ★★★【面具后置强制修正】与上面同一处解除武装（活动关掉后生成的 bot 不再改面板）
            FaceCoverFixDimension.Restore();
        }
        catch (Exception ex) { logger.Error($"[EventRotator] restore failed: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ v2.7.0【硬开关实现】**不启用任何活动**（`config.json → disableAllEvents = true`）。
    ///
    /// <para>调用点 = <see cref="RestoreThenApply"/> 里，紧跟在「<see cref="Restore"/> +
    /// <see cref="ClearStaleStaticState"/> + 各维度 Reset*」之后。也就是说：**能登记的还原闭包
    /// 已经全部回放完了**，这里只补 <see cref="Restore"/> 摸不到的 4 层，然后**直接返回**：</para>
    /// <list type="number">
    ///   <item><b>DB 层</b>（<see cref="RestoreDbLayer"/>）—— 追加首领 / BossChance / 红骷髅 / BossZone /
    ///         5 个 hostility 名单字段 / 僵尸敌对性 / NonWave 上限。<b>无条件执行</b>（幂等），
    ///         不像正常路径那样被 `DbNeedsReapply` 门控 —— 硬开关要求"开一次就一定是干净状态"。</item>
    ///   <item><b>原生季节镜像</b>（`PrepareHalloweenNative(false)` / `PrepareChristmasNative(false)`）——
    ///         僵尸开关 `zombieSettings.enabled`、实验室感染、globals `EventType`、
    ///         圣诞老人 `ItemsToDrop`、Khorovod 概率。
    ///         ⚠ 这两步**旧的无活动分支根本没调**（只有"还有活动在跑"的那条路径末尾才调）
    ///         ⇒ 这是"关掉万圣节后实验室还在长僵尸"的真实原因之一。</item>
    ///   <item><b>外部联动 Blackout</b>（<see cref="ExternalBridges.ApplyBlackout"/>，传 false = 还原第三方概率）。</item>
    ///   <item><b>天气季节 / 活动季节</b> —— 直接把 `WeatherConfig.OverrideSeason` 还原成 null
    ///         （不覆盖），并把 SPT 的季节事件判定交回"按日期自动"。<b>绝不</b>走
    ///         `ApplyWeatherSeason` / `TryForceSeasonal` / `ApplyEventSeason`：
    ///         那三个在 rotate 模式下会**强行接管**天气季节与季节性活动，正好是用户要关掉的东西。</item>
    /// </list>
    ///
    /// <para>之后：任务门控按"空活动集合"收窗（活动任务全部隐藏）、写状态文件（面板显示"无活动"）、
    /// 打 ⛔ 日志。**不套用任何活动维度** ⇒ 裸 SPT。重复调用完全幂等（每一步都可重复执行）。</para>
    /// </summary>
    private void HardRestoreForDisableAllEvents()
    {
        // ── ① DB 层：无条件还原（4 类，全幂等）
        try
        {
            RestoreDbLayer();
            Patches.BossWavePatch.MarkDbRestored();      // 清记账：以后关掉开关重新套用时不会误判"已还原"
            Patches.BossWavePatch.SetForceNight(0);      // 昼→夜强制概率归零（回原版昼夜）
            logger.Info("[EventRotator][disableAllEvents] DB 层已还原：首领追加/概率/红骷髅/zone/"
                      + "5 个 hostility 名单/僵尸敌对性/NonWave 上限（无条件、幂等）");
        }
        catch (Exception ex) { logger.Warning("[EventRotator][disableAllEvents] DB 层还原失败（已跳过）: " + ex.Message); }

        // ── ② 原生季节镜像：僵尸开关 / EventType / 圣诞礼物清单 / Khorovod / 召唤仪式
        try
        {
            WantNativeZombies = false;
            WantHalloweenEventType = false;
            WantNativeChristmas = false;
            PrepareHalloweenNative(false);
            PrepareChristmasNative(false);
            try { Patches.BossWavePatch.AllowHalloweenSummon = false; } catch { }
            logger.Info("[EventRotator][disableAllEvents] 原生季节镜像已还原："
                      + "zombieSettings.enabled=false / globals EventType / 圣诞 ItemsToDrop / Khorovod / halloweensummon");
        }
        catch (Exception ex) { logger.Warning("[EventRotator][disableAllEvents] 原生季节镜像还原失败（已跳过）: " + ex.Message); }

        // ── ③ 外部联动（实验室停电 / 黑狐）：false = 从 .bak-eventrotator 还原用户原值
        try
        {
            ExternalBridges.ApplyBlackout(false);
            logger.Info("[EventRotator][disableAllEvents] 外部联动已还原"
                      + "（Blackout / BlackDivServer 的停电概率回 .bak-eventrotator 的用户原值；未安装则跳过）");
        }
        catch (Exception ex) { logger.Warning("[EventRotator][disableAllEvents] 外部联动还原失败（已跳过）: " + ex.Message); }

        // ── ④ 天气季节 / 活动季节：**还原**，不做任何接管（rotate 分支会强行套季节，必须绕开）
        try
        {
            WeatherSeasonDimension.WeatherConfig = weatherConfig;
            WeatherSeasonDimension.Log = m => logger.Info(m);
            WeatherSeasonDimension.AddRestore = a => _restoreActions.Add(a);
            WeatherSeasonDimension.Apply(null, "disableAllEvents（裸 SPT：不覆盖天气季节）", logger);
        }
        catch (Exception ex) { logger.Warning("[EventRotator][disableAllEvents] 天气季节还原失败（已跳过）: " + ex.Message); }

        try
        {
            // 把 SPT 的季节事件判定交回"按现实日期自动"（= 原版行为）。
            // ⚠ 只调 `EnableSeasonalEvents(null)`，**不调** `ForceSeasonalEvent`：后者无回滚，
            //   在本开关下再强推一次季节就会与"裸 SPT"矛盾（SPT 4.1.6 的 ForceSeasonalEvent 没有撤销 API）。
            var mEnable = seasonalEventService?.GetType()
                .GetMethod("EnableSeasonalEvents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (mEnable != null) mEnable.Invoke(seasonalEventService, null);
            EventRotatorStatusRouter.AppliedEventSeason = "";
            logger.Info("[EventRotator][disableAllEvents] 活动季节已还原：seasonal event → auto（按现实日期判定）");
        }
        catch (Exception ex) { logger.Warning("[EventRotator][disableAllEvents] 活动季节还原失败（已跳过）: " + ex.Message); }

        // ── ⑤ 任务门控：按"空活动集合"收窗（所有被门控的活动任务一律隐藏 + 轮换进度重置）
        try { ApplyQuestGate(new List<EventDef>()); }
        catch (Exception ex) { logger.Warning("[EventRotator][disableAllEvents] 任务门控失败（已跳过）: " + ex.Message); }

        // ── ⑥ 状态文件 / HTTP 快照：面板必须显示"没有活动"（避免 UI 说关、服务端还在跑）
        try { WriteActiveStatus(new List<EventDef>()); } catch { }

        // ── ⑦ 日志（用户指定的两行之一；状态没变不重复刷，但每次状态切换必打）
        if (SigChanged("disableAllEvents", "true"))
            logger.Warning("[EventRotator] ⛔ disableAllEvents=true ⇒ 不启用任何活动（已还原上次套用的全部改动）");
    }

    /// <summary>
    /// ★★★【P0 修复】DB 层的「先还原」：把上一轮**直接写进 DB(LocationTable)** 的改动撤掉。
    ///
    /// <para>覆盖 4 类（原来是同一个根因：还原动作全挂在"第二次 ApplyToDb"上，而它永不发生）：</para>
    /// <list type="number">
    ///   <item><c>BossWavePatch.RestoreDb</c> —— 追加首领 / BossChance / 红骷髅标记 / BossZone；</item>
    ///   <item><c>BossWavePatch.RestoreHostility</c> —— 敌对性（含 forceHostility 的 5 个名单字段）；</item>
    ///   <item><c>BossWavePatch.RestoreZombieHostility</c> —— 官方僵尸敌对性复刻；</item>
    ///   <item><c>EventScavSuppress.RestoreNonWaveLimits</c> —— <c>NonWaveSpawnBotsLimitPerPlayer(PvE)</c>。</item>
    /// </list>
    ///
    /// <para><b>每一步独立 try/catch</b>：任何一步失败都只打 warning，绝不能拖垮整个"还原→套用"流程。
    /// <b>全部幂等</b>：重复调用只是把值写回原位；没有改动时完全静默（不刷屏）。
    /// 调用时机 = <see cref="RestoreThenApply"/> 里、重新套用之前（保持"先 Restore 再 Apply"的既有顺序）。</para>
    /// </summary>
    private void RestoreDbLayer()
    {
        try { Patches.BossWavePatch.RestoreDb(locationTable); }
        catch (Exception ex) { logger.Warning($"[EventRotator] 还原 DB 首领表失败（已跳过）: {ex.Message}"); }

        try { Patches.BossWavePatch.RestoreHostility(locationTable); }
        catch (Exception ex) { logger.Warning($"[EventRotator] 还原 hostility 失败（已跳过）: {ex.Message}"); }

        try { Patches.BossWavePatch.RestoreZombieHostility(locationTable); }
        catch (Exception ex) { logger.Warning($"[EventRotator] 还原僵尸敌对性失败（已跳过）: {ex.Message}"); }

        try { EventScavSuppress.RestoreNonWaveLimits(locationTable); }
        catch (Exception ex) { logger.Warning($"[EventRotator][SCAV抑制] 还原 NonWave 上限失败（已跳过）: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ v2.6.12【P0 簇6】把"静态登记位"清零。
    ///
    /// 为什么需要：这些位置是 **static 字段 / static 列表**，不是"配置对象上的字段"⇒ 不在
    /// <see cref="_restoreActions"/> 里，`Restore()` 回放闭包摸不到它们。而套用侧一律写成
    /// `if (ev.X != null) ... = ev;`（"本次活动声明了才写"）⇒ 一旦换到"没声明 X 的活动"，
    /// 上一轮的值会**继续生效**（表现为：关掉活动后路费还在收、首领时刻表还在落图、空投还在追加）。
    ///
    /// 三条同型（证据）：
    ///   · `_toll`      —— 唯一写点 <c>ApplyExtraDimensions: if (ev.Toll != null) _toll = ev;</c>；
    ///                     唯一读点 <c>Patches/BossWavePatch.cs:282</c>（进图前配置离线局时读）。
    ///   · `_timetable` —— 唯一写点 <c>if (ev.BossTimetable != null) { ...; _timetable = ev; }</c>；
    ///                     唯一读点 <c>Patches/BossWavePatch.cs:1153</c>（进图时按系统时钟落图）。
    ///   · `AirdropAppendDimension.Items` —— 唯一写点 <c>ApplyAirdropAppend: if (非空) Set(...)</c>；
    ///                     唯一读点 <c>AirdropAppendDimension.Postfix:52</c>（每次空投箱生成时追加）。
    ///
    /// 对照**正例**：`TraderAssortDimension.Pending` 是"应用成功即从挂起表移除"（就绪后自动清空），
    /// 所以它没有这个问题 —— 这正是本节要补齐的语义。
    /// ⚠ 只在"套用入口"调用，不会与 `_restoreActions` 的还原闭包打架（两者互不相干）。
    /// </summary>
    private void ClearStaleStaticState()
    {
        try
        {
            if (_toll != null)
            {
                logger.Info($"[EventRotator][路费] 清零上一轮的 _toll（'{_toll.Id}'）⇒ 本次没声明 toll 的活动就是「关闭路费」");
                _toll = null;
            }
        }
        catch { }

        try
        {
            if (_timetable != null)
            {
                logger.Info($"[EventRotator][时刻表] 清零上一轮的 _timetable（'{_timetable.Id}'）⇒ 本次没声明 bossTimetable 就是「关闭时刻表」");
                _timetable = null;
            }
        }
        catch { }

        try
        {
            if (AirdropAppendDimension.Items.Count > 0)
            {
                logger.Info($"[EventRotator][空投] 清零上一轮的追加登记 {AirdropAppendDimension.Items.Count} 项"
                            + "（本次没声明 airdropAppendLoot 就是「不追加」）");
                AirdropAppendDimension.Items.Clear();
            }
        }
        catch { }
    }

    // ---------- 爆率 ----------
    private void ApplyLoot(EventDef ev, List<EventDef> allActive = null)
    {
        try
        {
            // ★★★ v0.1.4 倍率合成：本活动自带的爆率 × 它自动配对进来的伙伴活动的爆率
            //    （用户要求"开某些活动时额外加爆率、并自动带上伙伴活动"）。
            //    多个来源相乘（×1.5 × ×1.25 = ×1.875），与原语义（后一个覆盖）不同 ——
            //    因为这里表达的是"叠加效果"，相乘更符合直觉。
            double loose = ev.LooseLootMultiplier ?? 1.0;
            double stat = ev.StaticLootMultiplier ?? 1.0;
            var maps = ev.LootMaps;
            var extraSrc = new List<string>();

            var partners = AutoPartnersOf(ev);
            if (partners.Count > 0 && allActive != null)
                foreach (var pid in partners)
                {
                    var pe = allActive.FirstOrDefault(x => string.Equals(x.Id, pid, StringComparison.OrdinalIgnoreCase));
                    if (pe == null) continue;
                    if (pe.LooseLootMultiplier.HasValue) { loose *= pe.LooseLootMultiplier.Value; extraSrc.Add($"{pe.Id} 地面×{pe.LooseLootMultiplier.Value}"); }
                    if (pe.StaticLootMultiplier.HasValue) { stat *= pe.StaticLootMultiplier.Value; extraSrc.Add($"{pe.Id} 容器×{pe.StaticLootMultiplier.Value}"); }
                }

            // 用户配置的"自动爆率提升"（config: autoLootBoost["activityId"] = 倍率）
            double boost = AutoLootBoostOf(ev.Id);
            if (boost > 0 && Math.Abs(boost - 1.0) > 0.000001)
            {
                loose *= boost; stat *= boost;
                extraSrc.Add($"自动爆率配置 ×{boost}");
            }

            int n = 0;
            bool hasLoose = Math.Abs(loose - 1.0) > 0.000001;
            bool hasStat = Math.Abs(stat - 1.0) > 0.000001;
            if (hasLoose) n += Mul(locationConfig.LooseLootMultiplier, _baseLoose, loose, maps);
            if (hasStat) n += Mul(locationConfig.StaticLootMultiplier, _baseStatic, stat, maps);
            if (n > 0)
            {
                _lastLootMultiplier = (hasLoose ? loose : 1.0, hasStat ? stat : 1.0, maps);
                logger.Info($"[EventRotator] 爆率 地面×{loose:0.###} 容器×{stat:0.###}，改了 {n} 项"
                            + (extraSrc.Count > 0 ? "（来源：" + string.Join("、", extraSrc) + "）" : "")
                            + (maps != null && maps.Count > 0 ? $"，限定 {string.Join("/", maps)}" : ""));
            }
        }
        catch (Exception ex) { logger.Error($"[EventRotator] loot failed: {ex.Message}"); }
    }

    /// <summary>★ v0.1.4 当前生效的爆率（供状态文件/面板显示）。</summary>
    private (double loose, double stat, List<string> maps) _lastLootMultiplier = (1, 1, null);

    /// <summary>
    /// ★★★ v0.1.4 自动配对表（config.json → <c>autoPairs</c>）。
    /// 形如：<c>{ "meeting_of_the_bosses": ["smugglers"], "cultist_hunting": ["centralized_loot"] }</c>
    /// —— "开 A 就自动也开 B"。默认**空表**（什么都不加），由用户决定具体配对。
    /// 配到不存在的 id 或会冲突的搭档时只打日志、不静默失败。
    /// </summary>
    public static readonly Dictionary<string, List<string>> AutoPairs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>★ v0.1.4 自动爆率提升表（config.json → <c>autoLootBoost</c>）：活动 id → 倍率。</summary>
    public static readonly Dictionary<string, double> AutoLootBoost = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★★★ v0.1.4 **自动首领概率提升**（config.json → <c>autoBossChance</c>）：
    /// <c>{ 触发活动id: { 地图键或*: { 首领角色或*: 概率 } } }</c>
    /// <para>
    /// 用途（用户指出的正确做法）：像"八影会谈 / 狩猎开始"这类**把首领抽到一张图**的活动，
    /// 不应该把首领加回被清空的图（那等于把机制拉平），而应**提高其它图自己本来那些首领的刷新概率**
    /// —— 三狗 `bossKnight`、黑老登 `bossPartisan` 等。概率提升 ≠ 必刷，核心体验（首领聚集）保留。
    /// </para>
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>> AutoBossChance
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>★ v0.1.4 取"因某个活动而额外提升的首领概率"（会被 ApplyLoot 旁边那套 bossChance 逻辑合并）。</summary>
    public static Dictionary<string, Dictionary<string, int>> AutoBossChanceOf(string eventId)
        => !string.IsNullOrEmpty(eventId) && AutoBossChance.TryGetValue(eventId, out var v) ? v : null;

    /// <summary>
    /// ★★★ v0.1.4 **地图补位表**（config.json → <c>mapFillers</c>）：
    /// 有些活动"把全图首领抽到一张图上"（八影会谈、狩猎开始…），结果**别的图没 BOSS 了**。
    /// 这张表列出"可以用来补某张图人气"的活动；开启抽首领类活动时，
    /// 会自动（可加多个）把**所有被清空的图**都补上。
    ///
    /// ★★★ v2.6.12【注释与代码对齐】此前这里写着"默认表（9 张图，代码内置；config 里的项会合并进来）"，
    ///   **与代码不符**（会让人以为默认就补位、也就查不出"补位为什么没生效"）：
    ///   · 代码侧**没有任何内置表** —— 唯一填充点是 `EventEngine.Load()` 从 config.json 读 `mapFillers`
    ///     （EventEngine.cs:186-208，逐项 `MapFillers[key] = ids`），本类这个字段只是 `new()` 空表；
    ///   · 且 `mapFillersEnable=false` 时会在那之后 `MapFillers.Clear()`（EventEngine.cs:200-206），
    ///     最后只剩"值非空的键"（:207-208）。
    ///   ⇒ 实情：**默认关闭**。本机 `config.json` 就是 `"mapFillersEnable": false` + `"mapFillers": {}`
    ///     ⇒ `AutoPairs.Count == 0 && MapFillers.Count == 0` ⇒ `ExpandAutoPartners()` 在
    ///     :575 直接 `return result`（零开销、不补位）。
    ///   ⚠ 想要"9 张图默认补位"是**产品决策**（需要先把那张表写进 config.json 或写进代码），
    ///     本次只把注释改成实情，**没有**新增内置表。
    /// 想关掉：config 里 <c>"mapFillersEnable": false</c>（默认就是）；想关单图：给该图配空数组。
    /// </summary>
    public static Dictionary<string, List<string>> MapFillers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>★ v0.1.4 地图键 → 中文（日志用）。</summary>
    private static string MapZh(string m) => Rotation.MapZh(m);

    /// <summary>
    /// ★ v0.1.4 某个活动"动过"的全部地图（从 5 个字段聚合）。
    /// 用于判断"抽首领类活动清空了哪些图"以及"搭档能不能补上那张图"。
    /// </summary>
    public static HashSet<string> MapsTouchedBy(EventDef e)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (e == null) return s;
        void Add(object v)
        {
            if (v is Dictionary<string, List<string>> d1) foreach (var k in d1.Keys) s.Add(k);
            else if (v is Dictionary<string, Dictionary<string, string>> d2) foreach (var k in d2.Keys) s.Add(k);
            else if (v is Dictionary<string, double> d3) foreach (var k in d3.Keys) s.Add(k);
        }
        Add(e.BossSpawns); Add(e.BossChance); Add(e.ZeroBossSpawns); Add(e.ForceExtraSpawns); Add(e.ForceAbpsSpawns);
        if (e.ForceZone is Dictionary<string, Dictionary<string, string>> fz) foreach (var k in fz.Keys) s.Add(k);
        return s;
    }

    private static List<string> AutoPartnersOf(EventDef ev)
    {
        if (ev == null) return new List<string>();
        return AutoPairs.TryGetValue(ev.Id, out var l) ? l : new List<string>();
    }

    private static double AutoLootBoostOf(string id)
        => !string.IsNullOrEmpty(id) && AutoLootBoost.TryGetValue(id, out var v) ? v : 0;

    /// <summary>
    /// ★ v0.1.4 把"自动配对"+"地图补位"展开进活动列表。
    /// 规则：伙伴活动必须**不与已有活动冲突**（用 mod 自己的冲突判定），冲突则跳过并打日志。
    /// </summary>
    private List<EventDef> ExpandAutoPartners(List<EventDef> evs)
    {
        var result = new List<EventDef>(evs);
        try
        {
            if (AutoPairs.Count == 0 && MapFillers.Count == 0) return result;   // 默认不配 → 零开销

            // ① 显式配对（autoPairs，优先级最高）
            foreach (var ev in evs)
                foreach (var pid in AutoPartnersOf(ev))
                    TryAddPartner(result, ev, pid, "autoPairs");

            // ② 地图补位（mapFillers）：只对"把首领抽走/清空别图"的活动生效
            foreach (var ev in evs)
            {
                var zeroed = ev.ZeroBossSpawns != null ? new HashSet<string>(ev.ZeroBossSpawns.Keys, StringComparer.OrdinalIgnoreCase)
                                                       : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (zeroed.Count == 0) continue;                       // 没清空任何图 → 不需要补位

                var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var already in result) filled.UnionWith(MapsTouchedBy(already));

                var need = zeroed.Where(m => !filled.Contains(m)).ToList();
                if (need.Count == 0) continue;

                // ★ 轮询尝试：每轮挑"能覆盖最多剩余被清空地图"的候选；没有新的可加就停。
                //   一个补位活动可能覆盖多张图（如 permanent_boss_presence 覆盖全部），
                //   所以必须**跨图去重**，否则会把同一活动重复加 8 次。
                int guard = 0;
                while (need.Count > 0 && guard++ < 12)
                {
                    string best = null; int bestCover = 0, bestMapIdx = -1;
                    for (int mi = 0; mi < need.Count; mi++)
                    {
                        if (!MapFillers.TryGetValue(need[mi], out var cands)) continue;
                        foreach (var cid in cands)
                        {
                            var ce = EventEngine.All.FirstOrDefault(x => string.Equals(x.Id, cid, StringComparison.OrdinalIgnoreCase));
                            if (ce == null || result.Contains(ce)) continue;
                            int cover = MapsTouchedBy(ce).Count(x => need.Contains(x, StringComparer.OrdinalIgnoreCase));
                            if (cover == 0) cover = 1;                     // 用 '*' 全图型的活动：MapsTouchedBy 里只有 "*"
                            if (cover > bestCover) { bestCover = cover; best = cid; bestMapIdx = mi; }
                        }
                    }
                    if (best == null) break;

                    if (!TryAddPartner(result, ev, best, "mapFillers")) break;

                    // 该补位覆盖到的图，从 need 里划掉
                    var ce2 = EventEngine.All.FirstOrDefault(x => string.Equals(x.Id, best, StringComparison.OrdinalIgnoreCase));
                    var covered = MapsTouchedBy(ce2);
                    int before = need.Count;
                    need = need.Where(m => !covered.Contains(m)).ToList();
                    if (need.Count == before) need.RemoveAt(Math.Max(0, Math.Min(bestMapIdx, need.Count - 1)));  // 通配型：至少划掉一张
                }

                var stillEmpty = need;
                if (stillEmpty.Count > 0)
                    logger.Warning($"[EventRotator] ⚠『{ev.NameZh}』清空的首领里，还有 {stillEmpty.Count} 张图没补上"
                                   + $"（{string.Join("/", stillEmpty.Take(5).Select(MapZh))}）："
                                   + "mapFillers 里没有能覆盖它们的补位活动 → 这些图本次**没有活动敌种**。"
                                   + "要补请把合适的活动加进 config.json 的 mapFillers（键=地图键，值=活动 id 列表）");
                else
                    logger.Info($"[EventRotator] ✔ 地图补位完成：『{ev.NameZh}』清空的 {zeroed.Count} 张图都补上了");
            }
        }
        catch (Exception ex) { logger.Warning("[EventRotator] autoPairs 展开失败: " + ex.Message); }
        return result;
    }

    /// <summary>尝试把一个伙伴活动加进列表（做冲突检查 + 日志）。</summary>
    private bool TryAddPartner(List<EventDef> result, EventDef src, string pid, string why)
    {
        if (result.Any(x => string.Equals(x.Id, pid, StringComparison.OrdinalIgnoreCase))) return false;
        var pe = EventEngine.All.FirstOrDefault(x => string.Equals(x.Id, pid, StringComparison.OrdinalIgnoreCase));
        if (pe == null) { logger.Warning($"[EventRotator] {why}：找不到搭档活动 '{pid}'（被 '{src.Id}' 引用）"); return false; }

        var c1 = EventEngine.ConflictReason(src, pe);
        if (c1 != null)
        {
            logger.Warning($"[EventRotator] {why}：'{src.Id}' 的搭档 '{pid}' **被跳过**（与 {src.Id} 冲突：{c1}）");
            return false;
        }
        foreach (var other in result)
        {
            var c2 = EventEngine.ConflictReason(other, pe);
            if (c2 != null)
            {
                logger.Warning($"[EventRotator] {why}：'{src.Id}' 的搭档 '{pid}' 被跳过（与已生效的 {other.Id} 冲突：{c2}）");
                return false;
            }
        }
        result.Add(pe);
        logger.Warning($"[EventRotator] ★自动配对生效（{why}）：『{src.NameZh}』→ 额外开启『{pe.NameZh}』");
        return true;
    }
    private int Mul(Dictionary<string, double> map, Dictionary<string, double> baseMap, double f, List<string> maps)
    {
        if (map == null || baseMap == null) return 0; int n = 0;
        foreach (var k in new List<string>(baseMap.Keys)) { if (maps != null && maps.Count > 0 && !maps.Contains(k)) continue; map[k] = Math.Round(baseMap[k] * f, 3); n++; }
        return n;
    }

    // ---------- 商人解锁（可还原）----------
    private void ApplyTraders(EventDef ev)
    {
        if (!ev.UnlockTraders) return;
        try
        {
            // ★★★ v0.1.5 修正（**第三个静默失效**，实机日志铁证：`traders unlocked x0`）：
            //   旧代码取 `tradersTable.GetType().GetProperty("Traders")` —— 而 SPT 4.1.6 的
            //   `TradersTable : Dictionary<MongoId, Trader>` **本身就是那个字典，没有 Traders 属性**
            //   ⇒ 拿到 null ⇒ 循环整段跳过 ⇒ `unlockTraders` 维度（13 个活动在用）**从来没生效过**。
            //   现在直接把它当字典用（并保留属性路径作兜底，兼容将来 SPT 改结构）。
            var traders = tradersTable as IDictionary
                          ?? tradersTable.GetType().GetProperty("Traders")?.GetValue(tradersTable) as IDictionary;
            int n = 0;
            if (traders != null) foreach (DictionaryEntry e in traders)
                {
                    var t = e.Value; if (t == null) continue;
                    var b = t.GetType().GetProperty("Base")?.GetValue(t);
                    var levels = b?.GetType().GetProperty("LoyaltyLevels")?.GetValue(b) as IList;
                    if (levels == null) continue;
                    foreach (var ll in levels)
                    {
                        var oldLv = GetNum(ll, "MinLevel"); var oldSt = GetNum(ll, "MinStanding");
                        var cap = ll;  // 闭包捕获
                        _restoreActions.Add(() => { SetNum(cap, "MinLevel", oldLv); SetNum(cap, "MinStanding", oldSt); });
                        SetNum(ll, "MinLevel", 0); SetNum(ll, "MinStanding", 0d); n++;
                    }
                }
            logger.Info($"[EventRotator] traders unlocked x{n} (可还原)");
        }
        catch (Exception ex) { logger.Error($"[EventRotator] trader unlock failed: {ex.Message}"); }
    }

    // ---------- 跳蚤（可还原）----------
    private void ApplyFlea(EventDef ev)
    {
        if (!ev.FleaMinLevel.HasValue) return;
        try
        {
            // ★★★ v0.1.5 修正（审计实证：旧实现**完全失效**，不是"近似"）：
            //   旧代码在 `ragfairConfig.Dynamic` 上找 `MinUserLevel/MinLevel/MinimumLevel` ——
            //   SPT 4.1.6 的 `RagfairConfig.Dynamic` **这三个属性一个都不存在** ⇒ 找不到就静默 no-op
            //   ⇒ 所有用 `fleaMinLevel` 的活动（如 closed_flea「跳蚤关闭」）**从来没生效过**。
            //   真正的门槛 = `globals.json → config.RagFair.minUserLevel`（实机 15）；
            //   消费点：RagfairController.cs:52/:191、RagfairOfferHelper.cs:595
            //   （全部读 `GlobalTable.Configuration.RagFair.MinUserLevel`）。
            var rf = globalTable?.Configuration?.RagFair;
            if (rf != null)
            {
                var pMin = rf.GetType().GetProperty("MinUserLevel", BindingFlags.Public | BindingFlags.Instance);
                if (pMin != null && pMin.CanWrite)
                {
                    // ★★★ v2.6.38【多活动冲突修复 · 用户实测「跳蚤市场关闭因为冲突无效」】
                    //   根因：本函数是**按活动逐个调用**的（`Plugin.cs:344-352` 的 `foreach (var ev in expanded)`），
                    //   而旧实现是**直接赋值** ⇒ `closed_flea`（fleaMinLevel=99，99 级等于买不了）与
                    //   `flea_promotion`（=1，任意等级可访问）同时开时，**后调用的覆盖先调用的** ⇒ 关闭失效 ✗。
                    //   本版语义：等级门槛**取最大值**（更严格者胜）
                    //     · 只有促销 ⇒ 1（促销生效）· 只有关闭 ⇒ 99（关闭生效）· 两个同开 ⇒ **99**（关闭生效）✔
                    //   ⚠ 可降级：读不到"当前值"时**按原样写**（绝不因为比较失败就不写）
                    //   ⚠ `fleaTaxMultiplier` 是**另一个属性**（不冲突、照常叠加），本函数不碰它
                    int want = ev.FleaMinLevel.Value;
                    // ⚠ **不能拿「当前字段值」比**（那会把"想降到 1"的促销挡掉，探针实测踩到）
                    //   ⇒ 比的是「**本轮**已声明的最大值」，每次套用前由 `RestoreThenApply()` 清零。
                    if (_fleaPassMax.HasValue && _fleaPassMax.Value >= want)
                    {
                        logger.Info($"[EventRotator] 跳蚤等级门槛：本活动要 {want}，但本轮已有更严格的 {_fleaPassMax.Value}"
                                  + " ⇒ **保持更严格值**（多活动同开取最大值；不改写、不登记还原）");
                        return;
                    }
                    _fleaPassMax = want;                        // 本轮最大值（单调不减）
                    var cap = rf; var pp = pMin;
                    // ★ 降级：原值读不到 ⇒ **只是不登记还原**，但**照样写**（绝不断路）
                    try
                    {
                        var old0 = pp.GetValue(cap);
                        _restoreActions.Add(() => { try { pp.SetValue(cap, old0); } catch { } });
                    }
                    catch { logger.Warning("[EventRotator] 跳蚤等级门槛：原值读不到 ⇒ 本次不登记还原（**但仍然写入**）"); }
                    pp.SetValue(cap, Convert.ChangeType(want, Nullable.GetUnderlyingType(pp.PropertyType) ?? pp.PropertyType));
                    logger.Info($"[EventRotator] 跳蚤等级门槛 minUserLevel={want}"
                              + "（落点 globals.config.RagFair；取本轮最大值 ⇒ 更严格者胜；可还原）");
                    return;
                }
                logger.Warning("[EventRotator] 找不到 RagFair.MinUserLevel，退回旧路径探测");
            }
            // 兜底：旧路径（万一某些 SPT 版本把它放在 Dynamic 上）
            object dyn = ragfairConfig.GetType().GetProperty("Dynamic")?.GetValue(ragfairConfig) ?? ragfairConfig;
            foreach (var name in new[] { "MinUserLevel", "MinLevel", "MinimumLevel" })
            {
                var p = dyn.GetType().GetProperty(name);
                if (p == null || !p.CanWrite) continue;
                int want2 = ev.FleaMinLevel.Value;
                if (_fleaPassMax.HasValue && _fleaPassMax.Value >= want2)
                {
                    logger.Info($"[EventRotator] flea {name}：本活动要 {want2}，本轮已有更严格的 {_fleaPassMax.Value}"
                              + " ⇒ 保持更严格值（旧路径；不改写、不登记还原）");
                    return;
                }
                _fleaPassMax = want2;
                var cap = dyn; var pp = p;
                try
                {
                    var old = pp.GetValue(cap);
                    _restoreActions.Add(() => { try { pp.SetValue(cap, old); } catch { } });
                }
                catch { logger.Warning("[EventRotator] flea：原值读不到 ⇒ 本次不登记还原（**但仍然写入**）"); }
                pp.SetValue(cap, Convert.ChangeType(want2, Nullable.GetUnderlyingType(pp.PropertyType) ?? pp.PropertyType));
                logger.Info($"[EventRotator] flea {name}={want2} (取更严格；可还原)"); return;
            }
            logger.Warning("[EventRotator] flea min-level field not found（globals 与 Dynamic 两条路径都没有）");
        }
        catch (Exception ex) { logger.Error($"[EventRotator] flea failed: {ex.Message}"); }
    }

    /// <summary>
    /// 尽量把属性值读成 <c>int?</c>（读不到 / 不是数值 ⇒ null）。
    /// 调用方约定：**null = "读不到" ⇒ 按原样写**（绝不因为比较失败就不写）—— `ApplyFlea` 的"取更严格"用的就是它。
    /// </summary>
    private static int? TryReadInt(object v)
    {
        try
        {
            if (v == null) return null;
            return (int)Math.Round(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // ★★★ v2.6.13【任务门控】活动期间才可接 + 轮换后按「只清进度（含 Success）」重置
    //
    // 设计依据全部来自 `audit/任务门控-可行性.md`（反编译 file:line 逐条核对过）：
    //   · SPT **唯一**的任务可见性门控 = `QuestHelper.ShowEventQuestToPlayer`（QuestHelper.cs:280-295）
    //       → `SeasonalEventService.IsQuestRelatedToEvent`（SeasonalEventService.cs:252-259）
    //       → 读 `questConfig.EventQuests`：
    //         **在表内 = 被门控（它那一季没开就隐藏 / 不可接）；不在表内 = `IsQuestRelatedToEvent` 恒 false
    //         ⇒ 三个 if 全不成立 ⇒ 永远可见（一直可接）**。
    //   · 读点是**每次 `/client/quest/list` 实时读**（QuestStaticRouter.cs:18 → QuestCallbacks.cs:76
    //       → QuestController.cs:32-34 → QuestHelper.cs:870 `GetClientQuests`，其中 :879 遍历
    //       `GetQuestsFromDb()`（:578-581 = `templateTable.Quests.Values.ToList()`）、:889 过门控）
    //     ⇒ **运行期改立即生效，没有启动期快照**（区别于 ItemFilterService / PostDbLoadService 两个坑）。
    //   · `EventQuestData.Season` 是**唯一被读的字段**（:258）；`startTimestamp`/`endTimestamp`/`yearly`
    //     全库 0 读者（死字段，本方法只填 0，不写真实值）。
    //   · 本机 `questConfig.ShowNonSeasonalEventQuests = false` ⇒「塞进表 + `season=None`」就是
    //     **任务级的隐藏开关**（与季节开关解耦，不会跨活动泄漏）。这是整套机制的前提。
    //   · ⚠ `QuestHelper.cs:882-886`：**profile 里已有条目的任务会被无条件塞进任务列表、绕过门控**
    //     ⇒ 门控只能拦"没接过的"；"已经接过的"必须靠第二半（轮换重置 profile 进度）来处理。
    //
    // 与旧 `ApplyQuests(ev)` 的区别：
    //   旧：**按单个活动**调用，唯一动作是"从 eventQuests 移除"（= 解锁），还原时按**原 season** 放回
    //       ⇒ ① 方向反了（活动关了任务仍然可见）② 跨活动泄漏（`night_of_the_cult` 的 quest 表内 season=Halloween，
    //       别的活动把万圣节季节推开时它在自己活动没跑的情况下也可见）。
    //   新：**全局一次调用**，双向都做（在跑⇒移出 / 没跑⇒塞入且 season=None），并按**传递闭包**取任务集。
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>上一轮"在跑的活动 id 集合"（用于检测轮换：**进入、退出都算变化**）。</summary>
    private static readonly HashSet<string> _lastActiveActivityIds = new(StringComparer.OrdinalIgnoreCase);

    // ══════════════ ★ v1.0.1【日志降噪】状态不变就不重复打 ══════════════
    //   背景（实测 spt20261017.log）：套用轮次一多，同一句状态日志会被打几十次
    //   （`halloweensummon 摘掉` 74 次、`僵尸模式开关` 50 次、`礼物清单已复刻` 50 次、
    //    `环舞机会已复刻` 50 次、`EventType 已还原` 39 次、`未指定→不干预` 76 次），
    //   6 MB 日志里 EventRotator 有 1026 条 warning，而**真正的问题只有 7 条** ⇒ 极难排查。
    //   语义：**按 key 记内容签名，内容没变就不打；内容变了（状态真的变了）照常打。**
    //   ⇒ 不丢任何真实状态变化，只去掉纯重复。
    private static readonly Dictionary<string, string> _logSig = new(StringComparer.Ordinal);

    /// <summary>★ v1.0.1 日志降噪：同 key 的内容与上次相同则返回 false（= 不要打）。</summary>
    private static bool SigChanged(string key, string sig)
    {
        try
        {
            if (_logSig.TryGetValue(key, out var prev) && string.Equals(prev, sig, StringComparison.Ordinal))
                return false;
            _logSig[key] = sig;
            return true;
        }
        catch { return true; }   // 降噪失败绝不吞日志
    }

    /// <summary>是否已从状态文件恢复过"上一轮集合"（进程内只做一次，避免开服被误判成"全部刚进入"）。</summary>
    private static bool _questGateSeeded;

    /// <summary>任务 id → 直接后继任务 id（从 `TemplateTable.Quests` 现算一次后缓存；DB 运行期不变）。</summary>
    private static Dictionary<string, List<string>> _questSucc;

    /// <summary>活动 id → 该活动 `enableQuests` 的**传递闭包**（缓存）。</summary>
    private static readonly Dictionary<string, HashSet<string>> _questClosureCache = new(StringComparer.OrdinalIgnoreCase);
    // ★ 2026-10-30【第 9 项】放开假等级门槛的快照：`<任务id>|<条件id>` → 原始 value（double?）
    //   幂等依据：key 在 ⇒ 已经快照/改写过，重复套用不再记录、不再改写；还原闭包里删 key。
    private static readonly Dictionary<string, double?> _relaxLevelSaved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>`NeverOpenQuests` 的 id 集合（「永远隐藏」5 条，门控**硬排除**它们）。</summary>
    private static HashSet<string> NeverOpenIdSet()
        => new(NeverOpenQuests.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // ★★★ v2.6.14【补洞】喂给 `Patches/QuestAcceptGatePatch` 的门控状态（**不重算一套**）
    //
    // 补的是什么洞（全部有反编译 file:line）：
    //   · `QuestHelper.cs:882-886`：**profile 里已有条目的任务被无条件放进任务列表**，
    //     直接绕过 `QuestHelper.ShowEventQuestToPlayer`（:280-295）⇒ 门控对"已接过的任务"完全失效；
    //   · `QuestController.AcceptQuest`（QuestController.cs:47-77）**全程没有任何门控检查**：
    //     `if (questStatus != null) ResetQuestState(..., Started, ...) else pmcData.Quests.Add(...)`
    //     ⇒ 服务端"客户端要什么就给什么"；
    //   · 全库 `IsQuestRelatedToEvent` 只被 `ShowEventQuestToPlayer:284/288/292` 引用
    //     ⇒ 门控**只存在于「列任务」这一条路径**；
    //   · 本 mod 的轮换重置还会把已接任务翻回 `AvailableForStart` ⇒ 客户端出现"接受"按钮
    //     ⇒ **活动关着也能再接一次**。
    // ⇒ 所以在**接任务入口**再拦一次：见 `Patches/QuestAcceptGatePatch.cs`。
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>★ v2.6.14：门控反查表 —— questId → 拥有它的活动 id 集合（`ApplyQuestGate` 每轮刷新）。</summary>
    internal static readonly Dictionary<string, HashSet<string>> QuestGateOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>★ v2.6.14：本轮真正"在跑"的活动集合（判据之一）。</summary>
    internal static readonly HashSet<string> QuestGateActiveActivities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v2.6.14：门控是否**已就绪**。没就绪（`OnLoadAsync` 里第一轮门控之前 / 门控还没跑过）
    /// ⇒ 补丁**一律放行** —— 这是"绝不能因为补丁失败就让所有任务都接不了"的兜底开关。
    /// </summary>
    internal static volatile bool QuestGateReady;

    /// <summary>
    /// ★★★ v2.6.14【补洞】"这个任务现在允许被接受吗？" —— `Patches/QuestAcceptGatePatch` 的唯一判据。
    ///
    /// 返回 <c>true</c> = **应当拒绝**（任务被门控，且拥有它的活动**当前都没在跑**）。
    /// 语义（比 SPT 的 `ShowEventQuestToPlayer` 更强：**这里能拦住已接过的任务**）：
    ///   · 门控未就绪 ⇒ 放行（绝不误拦）；questId 为空 ⇒ 放行；
    ///   · 任务**不在**门控集（本体常驻任务 / 可重复任务 / 未声明）⇒ 放行；
    ///   · 任务在门控集里，且**任一**拥有它的活动正在跑 ⇒ 放行（正常可接）；
    ///   · 其余 ⇒ 拒绝。
    /// 任何异常 ⇒ 放行（宁可漏拦，不可误拦）。
    /// </summary>
    internal static bool IsQuestBlockedByGate(string questId, out string reason)
    {
        reason = null;
        try
        {
            if (!QuestGateReady) return false;
            if (string.IsNullOrWhiteSpace(questId)) return false;
            if (!QuestGateOwners.TryGetValue(questId, out var owners) || owners == null || owners.Count == 0)
                return false;
            foreach (var a in owners)
                if (QuestGateActiveActivities.Contains(a)) return false;
            reason = $"任务 {questId} 属于活动 [{string.Join("/", owners)}]，而这些活动当前都没在跑";
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 建"任务前置 → 后继"图（用于求**传递闭包**）。
    /// 依据：`Quest.Conditions.AvailableForStart[].conditionType == "Quest"` 时 `target` 指向前置任务
    /// （`QuestConditionTypes.AvailableForStart`，JSON "AvailableForStart"；`QuestCondition.ConditionType`(:161) /
    /// `Target`(:130-131，`ListOrT&lt;string&gt;` 单值或数组两种形态都要判)）。
    /// ⚠ 闭包是必须的：实测 `halloween` 只声明 **1** 个任务，但它的链有 **17** 个；全库 45 个声明 → 闭包 **74** 个。
    ///   只按声明清单做门控/重置，链尾会漏（"看得见、做不了"）。
    /// </summary>
    private void BuildQuestGraph()
    {
        if (_questSucc != null) return;
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var all = templateTable?.Quests;
            if (all == null) logger.Warning("[EventRotator][任务门控] 拿不到 TemplateTable.Quests ⇒ 闭包退化为声明清单");
            else
                foreach (var kv in all)
                {
                    var afs = kv.Value?.Conditions?.AvailableForStart;
                    if (afs == null) continue;
                    foreach (var c in afs)
                    {
                        if (c == null || !string.Equals(c.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase)) continue;
                        var t = c.Target;
                        if (t == null) continue;
                        var pres = new List<string>();
                        if (t.IsItem && !string.IsNullOrEmpty(t.Item)) pres.Add(t.Item);
                        if (t.IsList && t.List != null)
                            foreach (var x in t.List) if (!string.IsNullOrEmpty(x)) pres.Add(x);
                        foreach (var pre in pres)
                        {
                            if (!map.TryGetValue(pre, out var l)) map[pre] = l = new List<string>();
                            var succ = kv.Key.ToString();
                            if (!l.Contains(succ)) l.Add(succ);
                        }
                    }
                }
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][任务门控] 建任务图失败（闭包退化）: {ex.Message}"); }
        _questSucc = map;
    }

    /// <summary>某个活动 `enableQuests` 的**传递闭包**（含自身；带缓存）。</summary>
    private HashSet<string> QuestClosureOf(EventDef ev)
    {
        if (ev?.Id != null && _questClosureCache.TryGetValue(ev.Id, out var cached)) return cached;
        BuildQuestGraph();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        foreach (var d in (ev?.EnableQuests ?? new List<string>()))
            if (!string.IsNullOrWhiteSpace(d) && set.Add(d)) stack.Push(d);
        int guard = 0;
        while (stack.Count > 0 && guard++ < 200000)
        {
            var cur = stack.Pop();
            if (_questSucc != null && _questSucc.TryGetValue(cur, out var succ))
                foreach (var s in succ)
                    if (!string.IsNullOrEmpty(s) && set.Add(s)) stack.Push(s);
        }
        if (ev?.Id != null) _questClosureCache[ev.Id] = set;
        return set;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // ★ 2026-10-30【第 9 项】放开"假等级门槛"（`relaxLevelQuests`）
    //   问题：本体有任务带不可达门槛（Import Control `668bcccc…` = `Level >= 99`，上限 79）⇒
    //        它以及依赖它的整条链永远接不到（任务面板里完全不出现）。
    //   做法：只处理**本活动 enableQuests 声明的**任务（不碰闭包），把 `AvailableForStart` 里
    //        `conditionType == "Level"` 的 `value` 临时改成 1，快照原值登记到 `_restoreActions`（LIFO 回放）。
    //   幂等：`_relaxLevelSaved` 的 key（任务id|条件id）存在 ⇒ 已快照/已改写，直接跳过；
    //        还原闭包里删 key，所以"套用→还原→再套用"能重新快照。
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>由主套用循环（`foreach (var ev in expanded)`）逐活动调用。</summary>
    private void ApplyRelaxLevelQuests(EventDef ev)
    {
        try
        {
            RelaxLevelQuestsFor(ev, templateTable, _relaxLevelSaved,
                                a => _restoreActions.Add(a), m => logger.Info(m));
        }
        catch (Exception ex)
        {
            logger.Warning($"[EventRotator][任务门控] 放开等级门槛失败（{ev?.Id}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 纯逻辑实现（internal static ⇒ 离线探针可以用**真 TemplateTable + 真 Quest 对象**直接调）。
    /// 返回本次真正放开的条件条数。
    /// </summary>
    internal static int RelaxLevelQuestsFor(EventDef ev, TemplateTable table,
                                            Dictionary<string, double?> saved,
                                            Action<Action> addRestore, Action<string> info)
    {
        if (ev == null || ev.RelaxLevelQuests != true || table?.Quests == null || saved == null) return 0;
        var declared = (ev.EnableQuests ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (declared.Count == 0) return 0;

        var done = new List<string>();
        foreach (var qid in declared)
        {
            object quest = null;
            try { if (table.Quests.TryGetValue(new MongoId(qid), out var qq)) quest = qq; }
            catch { continue; }
            if (quest == null) continue;

            var conds = quest.GetType().GetProperty("Conditions")?.GetValue(quest);
            var starts = conds?.GetType().GetProperty("AvailableForStart")?.GetValue(conds) as IEnumerable;
            if (starts == null) continue;

            foreach (var c in starts)
            {
                if (c == null) continue;
                var ctype = c.GetType().GetProperty("ConditionType")?.GetValue(c) as string;
                if (!string.Equals(ctype, "Level", StringComparison.OrdinalIgnoreCase)) continue;

                var condId = c.GetType().GetProperty("Id")?.GetValue(c)?.ToString() ?? "";
                var key = qid + "|" + condId;
                if (saved.ContainsKey(key)) continue;                 // ★ 幂等：已快照过

                var vProp = c.GetType().GetProperty("Value");
                if (vProp == null) continue;
                var cur = vProp.GetValue(c);
                double? curD = cur == null ? (double?)null : Convert.ToDouble(cur, System.Globalization.CultureInfo.InvariantCulture);
                if (curD.HasValue && curD.Value <= 1.0) continue;      // 本来就可达成（或已被别的路径放开）

                saved[key] = curD;
                try { vProp.SetValue(c, 1.0); }
                catch (Exception ex)
                {
                    saved.Remove(key);
                    info?.Invoke($"[任务门控] {ev.Id}：⚠ 条件 `{key}` 写入失败（跳过，不影响其它）：{ex.Message}");
                    continue;
                }

                var qname = quest.GetType().GetProperty("QuestName")?.GetValue(quest) as string;
                if (string.IsNullOrEmpty(qname)) qname = qid;
                var origText = curD.HasValue
                    ? curD.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null";
                done.Add($"{qname} Level {origText} → 1");

                var cRef = c; var vRef = vProp; var sRef = saved; var idRef = ev.Id; var nameRef = qname; var origRef = curD;
                addRestore(() =>
                {
                    sRef.Remove(key);
                    try { vRef.SetValue(cRef, origRef.HasValue ? (object)origRef.Value : null); }
                    catch (Exception ex) { info?.Invoke($"[任务门控] {idRef}：⚠ 还原 {nameRef} 的等级条件失败：{ex.Message}"); return; }
                    info?.Invoke($"[任务门控] {idRef}：已还原 {nameRef} 的等级条件（→ "
                               + (origRef.HasValue ? origRef.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null") + "）");
                });
            }
        }

        if (done.Count > 0)
            info?.Invoke($"[任务门控] {ev.Id}：已放开 {done.Count} 条假的等级门槛（{string.Join("；", done)}）");
        return done.Count;
    }

    /// <summary>
    /// 造一条"隐藏用"的 `eventQuests` 条目：`season = None` + 三个**死字段填 0**
    /// （`Season` 是唯一被读的字段，见文件头说明）。
    /// </summary>
    private EventQuestData MakeHiddenEntry(string questId)
    {
        string name = questId;
        try
        {
            var q = templateTable?.Quests?[new MongoId(questId)];
            if (!string.IsNullOrEmpty(q?.QuestName)) name = q!.QuestName!;
        }
        catch { }
        return new EventQuestData
        {
            Name = name,
            Season = SeasonalEventType.None,
            StartTimestamp = 0,
            EndTimestamp = 0,
            Yearly = false,
        };
    }

    /// <summary>
    /// 从 `<modPath>/当前生效活动.txt`（`WriteActiveStatus` 每次套用后写）恢复**上一轮的活动 id 集合**。
    /// 机器可读行格式：`#事件|分类|中文名|英文名|id|说明` ⇒ 取第 5 列（下标 4）。
    /// 返回 null = 文件不存在/读失败（无法判断上一轮 ⇒ 本轮只记录、不做轮换重置，避免开服误清进度）。
    /// </summary>
    private static HashSet<string> ReadActiveIdsFromStatusFile()
    {
        try
        {
            var dir = EventEngine.ModPath;
            if (string.IsNullOrEmpty(dir)) return null;
            var f = System.IO.Path.Combine(dir, "当前生效活动.txt");
            if (!System.IO.File.Exists(f)) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in System.IO.File.ReadAllLines(f))
            {
                if (!line.StartsWith("#事件|", StringComparison.Ordinal)) continue;
                var parts = line.Split('|');
                if (parts.Length < 5) continue;
                var id = parts[4].Trim();
                if (id.Length > 0) set.Add(id);
            }
            return set;      // ★ 可能是"合法的空集"（上一轮确实没有活动）⇒ 调用方用 `!= null` 判断
        }
        catch { return null; }
    }

    /// <summary>
    /// ★★★ v2.6.13【任务门控】主入口 —— 在主循环之后**一次调用**（全局视角）。
    /// ① 门控：门控集里**任一拥有它的活动在跑 ⇒ 可见**（从 `eventQuests` 移出）；
    ///          **全都没跑 ⇒ 隐藏**（塞进 `eventQuests` 且 `season=None`；原本有别的 season 就改写）。
    /// ② 轮换重置：活动集合发生变化（进 / 出）⇒ 该活动任务闭包在**所有 profile** 里"只清进度"。
    /// </summary>
    private void ApplyQuestGate(List<EventDef> expanded)
    {
        try
        {
            // ── 0) 前提断言：本机制完全依赖 `showNonSeasonalEventQuests == false` ──
            bool showNone = false;
            try
            {
                showNone = Convert.ToBoolean(questConfig?.GetType()
                    .GetProperty("ShowNonSeasonalEventQuests")?.GetValue(questConfig) ?? false);
            }
            catch { }
            if (showNone)
                logger.Warning("[EventRotator][任务门控] ⚠ configs/quest.json 的 `showNonSeasonalEventQuests = true` ⇒ "
                             + "「塞进表 + season=None」**不会隐藏任何任务**，本轮门控不生效（本 mod 不替用户改这个配置）；"
                             + "轮换重置那一半不受影响，照常执行");

            // （`eq` 的获取已下移到 1.5 之后 —— 必须先算完"门控集/在跑集合"并刷新给补丁的状态，
            //   这样即使表拿不到，`Patches/QuestAcceptGatePatch` 也用的是**刚刷新过的**判据）

            // ── 1) 门控集 = 所有活动 enableQuests 的传递闭包；硬排除「永远隐藏」5 条 ──
            var neverOpen = NeverOpenIdSet();
            var gate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            int declaredTotal = 0, closedExtra = 0, excludedNeverOpen = 0;
            var declaredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in EventEngine.All)
            {
                if (e?.EnableQuests == null || e.EnableQuests.Count == 0) continue;
                foreach (var d in e.EnableQuests)
                    if (!string.IsNullOrWhiteSpace(d)) { declaredTotal++; declaredSet.Add(d); }
                foreach (var q in QuestClosureOf(e))
                {
                    if (neverOpen.Contains(q)) { excludedNeverOpen++; continue; }   // ★ 硬排除
                    if (!declaredSet.Contains(q)) closedExtra++;
                    gate.Add(q);
                    if (!owners.TryGetValue(q, out var o))
                        owners[q] = o = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    o.Add(e.Id);
                }
            }

            // ── 1.5) ★★★ v2.6.14【补洞】把门控状态交给 `Patches/QuestAcceptGatePatch`（**不重算一套**）──
            //   为什么需要它：`QuestHelper.cs:882-886` 对"profile 里已有条目"的任务**无条件放进任务列表、
            //   绕过 `ShowEventQuestToPlayer` 门控**，而 `QuestController.AcceptQuest`（:47-77）
            //   **全程没有任何门控检查** ⇒ 光靠"列任务"那一层，**已接过的任务在活动关闭时照样能再接一次**
            //   （用户追问的"要是我已经接了呢"就是这个）。
            //   这里把「任务 → 所属活动」反查表 + 「本轮在跑的活动集合」喂给补丁，
            //   让它在**接任务的入口**再硬拦一次（活动在跑则放行）。
            var activeIds = new HashSet<string>(
                (expanded ?? new List<EventDef>()).Select(x => x?.Id ?? "").Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            QuestGateOwners.Clear();
            foreach (var kv0 in owners) QuestGateOwners[kv0.Key] = kv0.Value;
            QuestGateActiveActivities.Clear();
            foreach (var a0 in activeIds) QuestGateActiveActivities.Add(a0);
            QuestGateReady = true;      // ★ 只有跑到这里，补丁才敢拦人（没就绪 ⇒ 一律放行）

            // 收尾（轮换重置 + 记录本轮集合）：抽成局部函数，保证"表拿不到"时也能照做，逻辑不会走漂
            void FinishRotation()
            {
                var rot = new HashSet<string>(
                    EventEngine.Actives.Select(x => x?.Id ?? "").Where(x => x.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                ApplyQuestRotationReset(rot);
                _lastActiveActivityIds.Clear();
                foreach (var a in rot) _lastActiveActivityIds.Add(a);
            }

            var eq = questConfig?.GetType().GetProperty("EventQuests")?.GetValue(questConfig) as IDictionary;
            if (eq == null)
            {
                logger.Warning("[EventRotator][任务门控] 拿不到 questConfig.EventQuests ⇒ 本轮只做轮换重置；"
                             + "门控表不动（AcceptQuest 硬拦仍按**刚刷新过的**「任务→活动」+「在跑集合」判定）");
                FinishRotation();
                return;
            }

            logger.Info($"[EventRotator][任务门控] 门控集 {gate.Count} 个任务"
                      + $"（活动声明 {declaredTotal} 个 → 传递闭包 +{closedExtra} 个；"
                      + $"已硬排除「永远隐藏」{excludedNeverOpen} 条引用）"
                      + $"；在跑活动 {(expanded?.Count ?? 0)} 个，eventQuests 原 {eq.Count} 条");

            // 硬排除的自证（只读）：这 5 条到底藏没藏住 —— 让"永远隐藏"这个承诺在日志里可核对
            foreach (var nq in NeverOpenQuests)
            {
                var kk = new MongoId(nq.Id);
                bool inT = eq.Contains(kk);
                var s = inT ? eq[kk]?.GetType().GetProperty("Season")?.GetValue(eq[kk])?.ToString() : null;
                bool hidden = inT && string.Equals(s, "None", StringComparison.OrdinalIgnoreCase) && !showNone;
                var ownerList = EventEngine.All.Where(x => x?.EnableQuests != null && x.EnableQuests.Contains(nq.Id))
                                               .Select(x => x.Id).ToList();
                logger.Info($"[EventRotator][任务门控] 　永远隐藏『{nq.Name}』({nq.Id})："
                          + $"在表内={inT}、season={s ?? "-"} ⇒ {(hidden ? "已隐藏 ✔" : "⚠ 没藏住")}"
                          + (ownerList.Count > 0 ? $"；⚠ 但它被活动 {string.Join("/", ownerList)} 的 enableQuests 引用"
                                                + "（已被门控硬排除 ⇒ 不会再冒出来）" : ""));
            }

            // ── 2) 逐个门控任务：决定"可见/隐藏"并按快照改写 eventQuests ──
            //   （`activeIds` 已在 1.5 处算好并同步给了接任务门控补丁）
            var snap = new Dictionary<object, object>();     // key(MongoId) → 原对象；null = 原本**没有**这条
            int nOpen = 0, nClose = 0, nRewroteSeason = 0, nAlready = 0, nKeyBad = 0, nSkipInProfile = 0;
            // ★★★ 2026-10-31【修「塔科夫之迷无图/目标为空」】
            //   凡是**玩家存档里已经有**的任务，一律不改可见性。
            //   原因：隐藏会把它从 /client/quests 的完整数据里拿掉，客户端只剩档里的残留条目
            //   ⇒ 渲染成"无图、目标为空"的空壳。阻止"再次接取"另有 AcceptQuest 补丁，不需要靠隐藏。
            var inProfileQids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var allProfiles = saveServer?.GetProfiles();
                if (allProfiles != null)
                {
                    foreach (var kv in allProfiles)
                    {
                        var chars = kv.Value?.CharacterData;
                        if (chars == null) continue;
                        foreach (var pd in new[] { chars.PmcData, chars.ScavData })
                        {
                            if (pd?.Quests == null) continue;
                            foreach (var q in pd.Quests)
                            {
                                var idv = q?.QId;
                                if (idv != null) inProfileQids.Add(idv.ToString());
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { logger.Warning("[EventRotator][任务门控] 读存档任务列表失败（本轮不做「档内有则跳过隐藏」）：" + ex.Message); }
            if (inProfileQids.Count > 0)
                logger.Info($"[EventRotator][任务门控] 档内任务 {inProfileQids.Count} 个：命中的门控任务一律**不隐藏**（只挡再次接取）");
            foreach (var qid in gate)
            {
                bool visible = false;
                if (owners.TryGetValue(qid, out var o))
                    foreach (var a in o) if (activeIds.Contains(a)) { visible = true; break; }
                // ★ 档里有的任务：保持原样（不隐藏、不改 season）
                if (!visible && inProfileQids.Contains(qid)) { nSkipInProfile++; continue; }
                object key;
                try { key = new MongoId(qid); } catch { nKeyBad++; continue; }

                bool inT = eq.Contains(key);
                var cur = inT ? eq[key] : null;
                if (visible)
                {
                    if (inT)
                    {
                        if (!snap.ContainsKey(key)) snap[key] = cur;      // 原样快照 ⇒ 还原时放回原对象
                        eq.Remove(key);
                        nOpen++;
                    }
                }
                else
                {
                    var season = cur?.GetType().GetProperty("Season")?.GetValue(cur)?.ToString();
                    if (!inT)
                    {
                        if (!snap.ContainsKey(key)) snap[key] = null;     // 原本没有 ⇒ 还原 = 移除
                        eq[key] = MakeHiddenEntry(qid);
                        nClose++;
                    }
                    else if (!string.Equals(season, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        // ★ 关键：改写为 None（而不是保留原 season）—— 堵掉跨活动泄漏
                        //   （例：`night_of_the_cult` 的任务表内 season=Halloween，别的活动把万圣节季节推开时它会漏出来）
                        // ★★★ 2026-10-31【修「塔科夫之迷图片空白」】原来这里**整个对象替换**成 MakeHiddenEntry（只保留
                        //   Name/Season/Start/End/Yearly），实测会让客户端把该任务渲染成"无图、目标为空"的空壳。
                        //   现在改为**只改 Season 字段、其它字段原样保留**，并单独记录原值用于还原。
                        try
                        {
                            var sp = cur?.GetType().GetProperty("Season");
                            if (sp != null && sp.CanWrite)
                            {
                                var pt = sp.PropertyType;
                                object? orig = sp.GetValue(cur);
                                object? noneVal =
                                    pt.IsEnum ? Enum.Parse(pt, "None", true)
                                    : (pt == typeof(string) ? "None" : Convert.ChangeType("None", pt));
                                sp.SetValue(cur, noneVal);
                                var target = cur; var prop = sp; var origVal = orig;
                                _restoreActions.Add(() => { try { prop.SetValue(target, origVal); } catch { } });
                                nRewroteSeason++;
                            }
                            else
                            {
                                // 拿不到 Season 属性（类型变了）⇒ 退回整体替换，至少保证隐藏生效
                                if (!snap.ContainsKey(key)) snap[key] = cur;
                                eq[key] = MakeHiddenEntry(qid);
                                nRewroteSeason++;
                            }
                        }
                        catch
                        {
                            if (!snap.ContainsKey(key)) snap[key] = cur;
                            eq[key] = MakeHiddenEntry(qid);
                            nRewroteSeason++;
                        }
                    }
                    else nAlready++;
                }
            }
            // ★ 可还原：按快照"原本有 ⇒ 放回原对象；原本没有 ⇒ 移除"（逆序回放在 Restore() 里统一做）
            foreach (var kv in snap)
            {
                var k = kv.Key; var v = kv.Value; var d = eq;
                _restoreActions.Add(() =>
                {
                    try { if (v == null) { if (d.Contains(k)) d.Remove(k); } else d[k] = v; } catch { }
                });
            }

            // ── 3) 回读自证：不信"我改了"，只信"表里现在是什么" ──
            int okShow = 0, okHide = 0, bad = 0;
            foreach (var qid in gate)
            {
                bool visible = false;
                if (owners.TryGetValue(qid, out var o))
                    foreach (var a in o) if (activeIds.Contains(a)) { visible = true; break; }
                object key; try { key = new MongoId(qid); } catch { continue; }
                bool inT = eq.Contains(key);
                var season = inT ? eq[key]?.GetType().GetProperty("Season")?.GetValue(eq[key])?.ToString() : null;
                bool ok = visible ? !inT : (inT && string.Equals(season, "None", StringComparison.OrdinalIgnoreCase));
                if (ok) { if (visible) okShow++; else okHide++; } else bad++;
            }
            logger.Info($"[EventRotator][任务门控] ① 门控结果：开窗（移出⇒可见）{nOpen} 个 / "
                      + $"关窗（塞入⇒隐藏）{nClose} 个 / 改写 season→None {nRewroteSeason} 个 / 本就是 None {nAlready} 个"
                      + (nKeyBad > 0 ? $" / ⚠ {nKeyBad} 个 id 不是合法 MongoId" : "")
                      + $"；回读自证：可见 {okShow} ✔ + 隐藏 {okHide} ✔"
                      + (bad == 0 ? "，全部符合预期 ✔" : $"，⚠ {bad} 个与预期不符"));
            if (nOpen + nClose + nRewroteSeason == 0 && gate.Count > 0)
                logger.Info("[EventRotator][任务门控] 　（本轮 eventQuests 无需改动：全部已处于目标状态）");

            // ── 4) 轮换重置（只清进度，含 Success；不删 profile 条目）+ 5) 记录本轮集合 ──
            //   ⚠ 轮换检测用 `EventEngine.Actives`（= 用户实际开启的活动集合），**不是** `expanded`：
            //     `WriteActiveStatus(evs)` 每次套用后写进 `当前生效活动.txt` 的就是这个集合，
            //     下一轮开服也是从那个文件恢复"上一轮集合"⇒ 两边必须是**同一个集合**，
            //     否则 `autoPairs` 伙伴活动会在每次开服被误判成"刚进入"⇒ 白清一次进度。
            FinishRotation();
        }
        catch (Exception ex) { logger.Error($"[EventRotator][任务门控] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ v2.6.13【轮换重置】活动"在跑 ⇄ 不在跑"发生变化时，把该活动任务闭包在**所有 profile** 里的
    /// 进度重置回 **可再接**（`QuestStatusEnum.AvailableForStart`，含 `Success`）。
    ///
    /// **状态选择理由**：目标语义是"下次再来同样的活动**从头开始接**"（用户原话）。`QuestStatusEnum` 里
    ///   只有 `AvailableForStart`（=1）表达"可再接"；客户端对它有一整条接受流程
    ///   （`Assembly-CSharp`：`:991785` 按商人统计"可接任务数"、`:1076520` 过滤出可接任务、
    ///    `:1078276` 弹「NotifierQuestAvailableForStart」提示、`:987214` 显示接受按钮），
    ///   而且 SPT 自己的"全任务可接"档就是这么写的（`CreateProfileService.cs:140`
    ///   `AddAllQuestsToProfile(pmcData, QuestStatusEnum.AvailableForStart)`）⇒ 是**被支持的 profile 状态**。
    ///   （对比 `Started`：那是"进行中"，玩家不会再看到"接受"，不符合"从头开始接"。）
    ///
    /// **触发语义：进、出都算变化（对称差）**，理由：
    ///   · 退出 ⇒ 清空（用户原话"活动轮换之后原本任务状态会被清空"）；
    ///   · 进入 ⇒ 也清一次 —— 否则玩家在"活动没开"期间把任务做完了，活动回来时它是 Success，
    ///     就再也"从头开始接"不了，直接违背"直到下一次再来同样的活动再从头开始接"；
    ///   · 集合没变（同一活动连着两轮都在跑）⇒ **不动** ⇒ 玩家正常进度不会被误清。
    ///
    /// ⚠ **故意不登记 `_restoreActions`**：进度重置是**单向的历史事实**（"这一轮活动结束了"）。
    ///   若登记成可还原，下一次 `RestoreThenApply()` 开头就会把旧进度**复活**，而那一轮未必再发生轮换
    ///   （集合没变）⇒ 旧进度永久残留 ⇒ **直接违背用户需求**。所以这里只做"改前快照式日志"，不登记还原。
    ///
    /// ⚠ 首次运行（开服）**不做**重置：先尝试从 `当前生效活动.txt` 恢复上一轮集合；
    ///   恢复不到就只记录本轮集合 —— 否则每次开服都会被误判成"所有活动刚进入"⇒ 把玩家进度清掉。
    /// </summary>
    private void ApplyQuestRotationReset(HashSet<string> activeNow)
    {
        try
        {
            if (!_questGateSeeded)
            {
                _questGateSeeded = true;
                var fromFile = ReadActiveIdsFromStatusFile();
                if (fromFile != null)
                {
                    _lastActiveActivityIds.Clear();
                    foreach (var x in fromFile) _lastActiveActivityIds.Add(x);
                    logger.Info($"[EventRotator][任务门控][轮换] 已从「当前生效活动.txt」恢复上一轮集合 "
                              + $"[{string.Join(",", _lastActiveActivityIds)}]（本轮 [{string.Join(",", activeNow)}]）");
                }
                else
                {
                    _lastActiveActivityIds.Clear();
                    foreach (var x in activeNow) _lastActiveActivityIds.Add(x);
                    logger.Info("[EventRotator][任务门控][轮换] 状态文件不可读 ⇒ 本次开服只记录本轮集合、"
                              + "**不做轮换重置**（避免把玩家进度误清；下一次套用起正常判轮换）");
                    return;
                }
            }

            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in activeNow) if (!_lastActiveActivityIds.Contains(a)) changed.Add(a);
            foreach (var a in _lastActiveActivityIds) if (!activeNow.Contains(a)) changed.Add(a);
            if (changed.Count == 0) return;

            var neverOpen = NeverOpenIdSet();
            var quests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var detail = new List<string>();
            int excluded = 0;
            foreach (var aid in changed)
            {
                var ev = EventEngine.All.FirstOrDefault(x => string.Equals(x?.Id, aid, StringComparison.OrdinalIgnoreCase));
                var dir = activeNow.Contains(aid) ? "进入" : "退出";
                if (ev == null) { detail.Add($"{aid}({dir}，未找到活动定义)"); continue; }
                var cl = QuestClosureOf(ev);
                int n = 0;
                foreach (var q in cl)
                {
                    if (neverOpen.Contains(q)) { excluded++; continue; }
                    if (quests.Add(q)) n++;
                }
                detail.Add($"{aid}({dir}，闭包 {cl.Count} 个 ⇒ 新增待重置 {n} 个)");
            }
            logger.Info($"[EventRotator][任务门控][轮换] 活动集合变化：{string.Join("；", detail)}"
                      + $" ⇒ 需重置进度的任务共 {quests.Count} 个"
                      + (excluded > 0 ? $"（另有 {excluded} 条「永远隐藏」引用被硬排除）" : "")
                      + $"，策略=只清进度、含 Success、不删条目（按用户决定「可重刷活动奖励」）");
            ResetQuestProgress(quests);
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][任务门控][轮换] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// 把一批任务在**所有 profile**（PMC + SCAV，逐个存档）里的进度重置为"可再接"。
    ///
    /// 做法（全部走 SPT 原生接口 + 逐项回读自证）：
    ///   1. `QuestHelper.ResetQuestState(pmcData, QuestStatusEnum.AvailableForStart, qid)`
    ///      （QuestHelper.cs:650-672）—— 清 `CompletedConditions`、写 `StatusTimers`、去掉高于目标状态的计时器；
    ///      源码只在 `newQuestState == Started` 时才改 `StartTime` ⇒ 这里**额外**把 `StartTime=0`、
    ///      `AvailableAfter=null` 显式补上（任务要求"清 StartTime"，且避免残留时间锁）。
    ///   2. **清 `TaskConditionCounters`**（键=条件 id、`SourceId`=任务 id，`QuestController.cs:94-100`）
    ///      —— **结论：要清**。这是 `SellItemToTrader` 这类条件的**服务端进度计数器**（`:259-264` 累加、
    ///      `:145` 手交时读）。它**不会**被 `ResetQuestState` 清掉、也不会被重新接取重置
    ///      （`AddTaskConditionCountersToProfile` 只在缺键时新建）。若留着，玩家下次接取时
    ///      这个"已满足的计数器"会直接顶掉一条目标 ⇒ 与"进度归零"矛盾。
    ///   3. 幂等：已经是 `AvailableForStart` + `CompletedConditions` 空 + 无计数器 ⇒ **跳过**
    ///      （否则每次进图都会重写 StartTime / StatusTimers）。
    /// </summary>
    private void ResetQuestProgress(HashSet<string> questIds)
    {
        try
        {
            if (questIds == null || questIds.Count == 0) return;
            var profiles = saveServer?.GetProfiles();
            if (profiles == null || profiles.Count == 0)
            {
                logger.Info("[EventRotator][任务门控][轮换] 当前没有任何 profile ⇒ 本轮无需重置");
                return;
            }

            int questDone = 0, alreadyClean = 0, noEntry = 0, countersRemoved = 0, profTouched = 0, bad = 0;
            foreach (var kv in profiles)
            {
                var prof = kv.Value;
                var chars = prof?.CharacterData;
                if (chars == null) continue;
                bool touched = false;
                foreach (var side in new[] { ("PMC", chars.PmcData), ("SCAV", chars.ScavData) })
                {
                    var pd = side.Item2;
                    if (pd?.Quests == null) continue;
                    foreach (var qid in questIds)
                    {
                        var qs = pd.Quests.FirstOrDefault(x => x != null
                            && string.Equals(x.QId.ToString(), qid, StringComparison.OrdinalIgnoreCase));
                        if (qs == null) { noEntry++; continue; }        // 从没接过 ⇒ 门控负责"不可接"，无需重置

                        // ① 先清该任务的进度计数器（Step 2 的理由）
                        int rm = 0;
                        if (pd.TaskConditionCounters != null)
                        {
                            var toks = pd.TaskConditionCounters
                                .Where(x => x.Value != null
                                         && string.Equals(x.Value.SourceId.ToString(), qid, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Key).ToList();
                            foreach (var k in toks) if (pd.TaskConditionCounters.Remove(k)) rm++;
                        }

                        // ② 幂等：已经干净就跳过
                        bool clean = qs.Status == QuestStatusEnum.AvailableForStart
                                     && (qs.CompletedConditions == null || qs.CompletedConditions.Count == 0)
                                     && rm == 0;
                        if (clean) { alreadyClean++; continue; }

                        var beforeStatus = qs.Status.ToString();
                        int beforeCond = qs.CompletedConditions?.Count ?? 0;
                        double beforeStart = qs.StartTime;
                        double? beforeAvail = qs.AvailableAfter;

                        if (qs.StatusTimers == null) qs.StatusTimers = new Dictionary<QuestStatusEnum, double>();
                        // ★ SPT 原生接口（只清进度，**不删条目**）
                        questHelper.ResetQuestState(pd, QuestStatusEnum.AvailableForStart, qs.QId);
                        qs.StartTime = 0;
                        qs.AvailableAfter = null;

                        // ③ 回读自证（本项目铁律：日志说改了就必须真改了）
                        int afterCond = qs.CompletedConditions?.Count ?? 0;
                        bool ok = qs.Status == QuestStatusEnum.AvailableForStart && afterCond == 0 && qs.StartTime == 0;
                        if (!ok) bad++;
                        logger.Info($"[EventRotator][任务门控][轮换] {side.Item1} 存档 {kv.Key} 『{qid}』："
                                  + $"status {beforeStatus}→{qs.Status}、完成条件 {beforeCond}→{afterCond}、"
                                  + $"StartTime {beforeStart:0}→{qs.StartTime:0}、时间锁 {beforeAvail?.ToString() ?? "-"}→"
                                  + $"{(qs.AvailableAfter?.ToString() ?? "-")}、进度计数器清 {rm} 个 "
                                  + (ok ? "✔（条目保留，可再接）" : "⚠ 回读不符"));
                        questDone++; countersRemoved += rm; touched = true;
                    }
                }
                if (touched) profTouched++;
            }
            logger.Info($"[EventRotator][任务门控][轮换] ② 重置结果：改了 {questDone} 条 profile 任务状态"
                      + $"（涉及 {profTouched} 个角色档案；清进度计数器 {countersRemoved} 个；"
                      + $"本就是可再接 {alreadyClean} 条；profile 里没有条目 {noEntry} 条 ⇒ 它们由①门控负责「不可接」）"
                      + (bad == 0 ? "；全部回读通过 ✔" : $"；⚠ {bad} 条回读不符"));
            if (questDone > 0)
                logger.Warning("[EventRotator][任务门控][轮换] ⚠ 按用户决定：**含已完成(Success)任务一起重置** ⇒ "
                             + "同样的活动下次再来时这些任务可以重做、**奖励可重复领取**（这是用户明确要的「可重刷活动奖励」）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][任务门控][轮换] profile 进度重置失败: {ex.Message}"); }
    }

    // ---------- ★ v0.1.5 万圣节原生开关（实验室僵尸 / 实验室锤哥）----------
    /// <summary>
    /// 本次套用是否要让 SPT 原生僵尸模式生效（由活动里的 <see cref="EventDef.NativeZombies"/> 决定）。
    /// </summary>
    public static bool WantNativeZombies;
    /// <summary>★ v1.0.1【BugFix】是否有 seasonalEvent=Halloween 的活动在跑 —— 决定是否注入 globals.config.EventType。</summary>
    public static bool WantHalloweenEventType;

    /// <summary>本次套用是否要复刻官方圣诞流程（由活动里的 <see cref="EventDef.NativeChristmas"/> 决定）。</summary>
    public static bool WantNativeChristmas;

    /// <summary>
    /// ★ v2.7.0【硬开关】上一次套用是否处于 `disableAllEvents = true`（裸 SPT）状态。
    /// 只用于"关掉开关 ⇒ 恢复正常轮换逻辑"那一行日志的**状态切换判定**（不是配置真值源；
    /// 真值在 <see cref="EventEngine.DisableAllEvents"/>）。
    /// </summary>
    private bool _disableAllEventsWasOn;

    /// <summary>
    /// ★ v0.1.5 强推「万圣节」季节前的预处理 —— 修掉用户实机反馈的两个问题：
    ///   ① 进实验室**看不见僵尸**  ② 实验室**刷 bossTagilla（锤哥）**。
    ///
    /// 根因（SPT 4.1.6 反编译实证 `SeasonalEventService.ApplyHalloweenEvent`）：
    /// <code>
    /// if (Settings.EnableSummoning == true)  { EnableHalloweenSummonEvent(); AddEventBossesToMaps("halloweensummon"); }
    /// if (Settings.ZombieSettings.Enabled == true) ConfigureZombies(Settings.ZombieSettings);
    /// </code>
    /// 官方 `configs/seasonalevents.json` 里 halloween 是 `enableSummoning=true` / `zombieSettings.enabled=false`：
    ///   · `eventBossSpawns.halloweensummon.laboratory` = **bossTagilla**（BossChance 100、3 个刷点）
    ///     ⇒ 只要服务器处于万圣节季节，实验室就 100% 刷锤哥（实测日志指纹 `bossTagilla:100@z3` 逐字吻合）；
    ///   · `zombieSettings.enabled=false` ⇒ `ConfigureZombies` 整段不跑 ⇒ 实验室既不感染（没僵尸），
    ///     官方那条 `disableBosses:["laboratory"]`（清空实验室首领表）也不生效。
    ///
    /// 本方法：
    ///   ① 按活动声明写 `zombieSettings.enabled`（true 时官方自动：清空实验室首领表 + 实验室 100% 感染 + 追加 29 条 infected）；
    ///   ② 不开僵尸时，把 `halloweensummon.laboratory` 摘掉，保证"实验室不刷锤哥"。
    /// </summary>
    /// <summary>
    /// ★★★ v0.1.5 新增：**复刻官方 `SeasonalEventService.ConfigureZombies` 的关键部分**。
    ///
    /// 背景（反编译实证）：官方 `ApplyHalloweenEvent` → `if (ZombieSettings.Enabled) ConfigureZombies(...)`，
    /// 而 `ApplyHalloweenEvent` 只由 `CacheActiveEvents()/UpdateGlobalEvents()` 按**现实日期**触发
    /// （官方万圣节窗 = 10/28–11/9）。用户在**窗外**用 `force.txt`/F12 强制开启时，这段整段不跑：
    ///   · `botTable.Core.ActiveHalloweenZombiesEvent = true`  ← **不设置**
    ///   · `SeasonActivity.InfectionHalloween.Enabled / DisplayUIEnabled`
    ///   · `zombieSettings.disableBosses`（官方：清空**实验室**首领表）
    ///   · `zombieSettings.disableWaves`
    /// ⇒ 表现就是用户实测的「**僵尸正常刷新、行动，但不攻击玩家**」（僵尸 AI 没进"僵尸模式"）。
    ///
    /// 本方法把上面四项补齐（全部快照可还原）；季节窗内时官方本来就会做，这里是**幂等**的。
    /// </summary>
    private void ReplicateConfigureZombies(bool on)
    {
        try
        {
            // ★★★ v0.1.5（第二轮修正）：官方 ApplyHalloweenEvent 的**第一步**就是
            //   `globalConfig.EventType = EventType.Where(x => x != None); EventType.Add(Halloween); EventType.Add(HalloweenIllumination);`
            //   而客户端正是靠 `globals.config.EventType` 判断"现在是不是万圣节" ⇒ 决定要不要显示
            //   **地图界面感染条**、以及是否启用万圣节相关行为（僵尸 AI）。
            //   用户实测：地图选择界面**没有感染条**、僵尸**不攻击**，而 globals 里 EventType 一直是 ["None"]。
            try
            {
                var cfgObj = PropOf(globalTable, "Configuration");
                var etProp = cfgObj?.GetType().GetProperty("EventType",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                var etList = etProp?.GetValue(cfgObj) as System.Collections.IList;
                if (etList != null)
                {
                    var enumType = etList.GetType().IsGenericType
                        ? etList.GetType().GetGenericArguments()[0]
                        : (etList.Count > 0 ? etList[0]!.GetType() : null);
                    if (enumType != null && enumType.IsEnum)
                    {
                        object Val(string n) => Enum.Parse(enumType, n, true);
                        bool Has(object v) { foreach (var x in etList) if (Equals(x, v)) return true; return false; }

                        var vNone = Val("None");
                        var vHW = Val("Halloween");
                        var vIll = Val("HalloweenIllumination");

                        // ★★★ v1.0.1【BugFix·用户实测「杀小鹿仪式没触发」】
                        //   原来这里用 `on`（=要不要原生僵尸）决定 EventType。
                        //   而 `halloween_2023` 是 **Zryachiy 仪式**活动、**没有 nativeZombies 声明** ⇒ on=false
                        //   ⇒ 代码主动把 EventType **压回 [None]**（日志实测 34 次「已还原 → [None]」、0 次「→ [Halloween]」）
                        //   ⇒ 客户端永远以为"现在不是万圣节" ⇒ **杀小鹿不触发仪式**。
                        //   修法：EventType 反映的是"**当前是不是万圣节活动局**"，与"要不要开原生僵尸"无关。
                        bool etOn = WantHalloweenEventType;   // ★ v1.0.1【BugFix】与"要不要开僵尸"解耦
                        if (etOn)
                        {
                            if (Has(vNone)) { etList.Remove(vNone); _restoreActions.Add(() => { try { if (!Has(vNone)) etList.Add(vNone); } catch { } }); }
                            if (!Has(vHW)) { etList.Add(vHW); _restoreActions.Add(() => { try { if (Has(vHW)) etList.Remove(vHW); } catch { } }); }
                            if (!Has(vIll)) { etList.Add(vIll); _restoreActions.Add(() => { try { if (Has(vIll)) etList.Remove(vIll); } catch { } }); }
                            logger.Warning($"[EventRotator][万圣节] ★globals.config.EventType → [{string.Join(", ", etList.Cast<object>())}]"
                                         + $"（来源={(on ? "原生僵尸局" : "万圣节活动局(无僵尸)")}；客户端据此显示感染条并启用万圣节行为/仪式）");
                        }
                        else
                        {
                            if (Has(vHW)) etList.Remove(vHW);
                            if (Has(vIll)) etList.Remove(vIll);
                            if (!Has(vNone)) etList.Add(vNone);
                            if (SigChanged("et-restore-hw", string.Join(",", etList.Cast<object>())))
                                logger.Info($"[EventRotator][万圣节] globals.config.EventType 已还原 → [{string.Join(", ", etList.Cast<object>())}]");
                        }
                    }
                }
            }
            catch (Exception exEt) { logger.Warning($"[EventRotator][万圣节] 设置 config.EventType 失败: {exEt.Message}"); }

            // ② 官方 ApplyHalloweenEvent 还会把默认 stim buff 换成万圣节版（Buffs_Halloween）
            try
            {
                var cfgObj2 = PropOf(globalTable, "Configuration");
                var phs = PropOf(PropOf(cfgObj2, "Health"), "ProfileHealthSettings");
                var dsp = phs?.GetType().GetProperty("DefaultStimulatorBuff",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                if (dsp != null && dsp.CanWrite)
                {
                    var oldv = dsp.GetValue(phs) as string ?? "";
                    if (on && oldv != "Buffs_Halloween")
                    {
                        dsp.SetValue(phs, "Buffs_Halloween");
                        _restoreActions.Add(() => { try { dsp.SetValue(phs, oldv); } catch { } });
                        logger.Info($"[EventRotator][万圣节] DefaultStimulatorBuff: '{oldv}' → 'Buffs_Halloween'（官方同款）");
                    }
                    else if (!on && oldv == "Buffs_Halloween")
                    {
                        dsp.SetValue(phs, "");
                        logger.Info("[EventRotator][万圣节] DefaultStimulatorBuff 已还原为空");
                    }
                }
            }
            catch { }

            // ① 僵尸模式总开关（最关键）
            try
            {
                var core = botTable?.GetType().GetProperty("Core",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(botTable);
                var pFlag = core?.GetType().GetProperty("ActiveHalloweenZombiesEvent",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                if (pFlag != null && pFlag.CanWrite)
                {
                    var cur = pFlag.GetValue(core);
                    bool curB = cur is bool b && b;
                    if (curB != on)
                    {
                        var cap = core; var capP = pFlag; var capOld = cur;
                        _restoreActions.Add(() => { try { capP.SetValue(cap, capOld); } catch { } });
                        pFlag.SetValue(core, on);
                    }
                    if (SigChanged("zombie-flag", $"{curB}->{on}"))
                        logger.Warning($"[EventRotator][万圣节] ★僵尸模式开关 ActiveHalloweenZombiesEvent: {curB} → {on}"
                                     + "（官方 ConfigureZombies 的第一行；窗外强制开启时官方不会执行，故由本 mod 复刻）");
                }
                else logger.Warning("[EventRotator][万圣节] 拿不到 BotTable.Core.ActiveHalloweenZombiesEvent，僵尸行为可能仍旧不对");
            }
            catch (Exception ex1) { logger.Warning($"[EventRotator][万圣节] 设置僵尸模式开关失败: {ex1.Message}"); }

            // ② 感染 UI / 玩法开关
            try
            {
                var sa = PropOf(globalTable, "Configuration") is object cfg
                    ? cfg.GetType().GetProperty("SeasonActivity",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(cfg)
                    : null;
                var inf = sa?.GetType().GetProperty("InfectionHalloween",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(sa);
                foreach (var fld in new[] { "Enabled", "DisplayUIEnabled" })
                {
                    var pf = inf?.GetType().GetProperty(fld,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.IgnoreCase);
                    if (pf == null || !pf.CanWrite) continue;
                    var oldV = pf.GetValue(inf);
                    bool oldB = oldV is bool ob && ob;
                    if (oldB == on) continue;
                    var capI = inf; var capF = pf; var capO = oldV;
                    _restoreActions.Add(() => { try { capF.SetValue(capI, capO); } catch { } });
                    pf.SetValue(inf, on);
                    logger.Info($"[EventRotator][万圣节] SeasonActivity.InfectionHalloween.{fld}: {oldB} → {on}");
                }
            }
            catch { }

            // ③ 官方 zombieSettings.disableBosses / disableWaves（官方值：disableBosses=["laboratory"]、disableWaves=[]）
            try
            {
                var halloween = seasonalEventConfig?.Events?.FirstOrDefault(
                    e => string.Equals(e?.Type.ToString(), "Halloween", StringComparison.OrdinalIgnoreCase));
                var zs = halloween?.Settings?.ZombieSettings;
                var db = PropOf(zs, "DisableBosses") as System.Collections.IEnumerable;
                var dw = PropOf(zs, "DisableWaves") as System.Collections.IEnumerable;
                foreach (var pair in new[] { (db, "BossLocationSpawn"), (dw, "Waves") })
                {
                    if (pair.Item1 == null) continue;
                    foreach (var m in pair.Item1)
                    {
                        var mapId = m?.ToString(); if (string.IsNullOrEmpty(mapId)) continue;
                        var loc = locationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })
                                                            ?.Invoke(locationTable, new object[] { mapId });
                        var bs = PropOf(PropOf(loc, "Base"), pair.Item2) as System.Collections.IList;
                        if (bs == null || bs.Count == 0) continue;
                        if (on)
                        {
                            var snap = bs.Cast<object>().ToList();
                            var capBs = bs;
                            _restoreActions.Add(() => { try { capBs.Clear(); foreach (var x in snap) capBs.Add(x); } catch { } });
                            bs.Clear();
                            logger.Warning($"[EventRotator][万圣节] 已按官方 disableBosses/disableWaves 清空 {mapId}.{pair.Item2}（{snap.Count} 条，可还原）");
                        }
                    }
                }
            }
            catch (Exception ex3) { logger.Warning($"[EventRotator][万圣节] 清空首领/波次失败: {ex3.Message}"); }
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][万圣节] 复刻 ConfigureZombies 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★ v0.1.5「全清单重验后补做」的维度总入口（用户要求把重验发现能做的 9 条补齐）。
    ///
    /// 一次全清单重验发现：我原先判成"做不到"的条目里有 9 条其实能做（冻伤那次的教训 —— 不是"SPT 没有"，
    /// 而是"本体有、只是没打开"或"我落点找错了"）。已在 <see cref="ExtraDimensions"/> 里实现 7 条：
    ///   ① 取消购买上限（`assort.items[].upd.BuyRestrictionMax`）
    ///   ② boss 血量（`bots/types/*.json → health.BodyParts`）
    ///   ③ 瞄准抖动（`config.AimPunchMagnitude` + `config.Aiming`）
    ///   ④ 惯性（`config.Inertia`）
    ///   ⑤ 玩家受伤倍率（`config.Hands/Legs/StomachOverdamage`）
    ///   ⑥ 撤离点锁定（`location.Base.exits[].Chance`）
    ///   ⑦ 按图禁用转场（`location.Base.transits[].active`）
    ///   ⑧ 按 bot 类型注入装备（`bots/types/*.json → inventory.equipment`，**绕开 PBS 对运行时注入的拦截**）
    ///   ⑨ 商人头像 —— 落点（`httpConfig.ServerImagePathOverride`）已核实，但**本机缺 halloween 图片** ⇒ 默认不启用。
    /// </summary>
    private void ApplyExtraDimensions(EventDef ev)
    {
        // ★★★ P1-1【verify-a 审计】**一个维度一个异常边界**（旧实现是一口大锅 try/catch）：
        //   危害实证：`HideoutAddRequirements` / `LooseLootWeightSet` 排在 `ApplyOfficialGear`/`OfficialLoot`/
        //   `GifterOfficial`/`PlayerGift` **之前** ⇒ 前者一抛异常，整个圣诞/万圣节内容**静默消失**。
        //   现在：任一维度抛异常**只跳过它自己**，后续维度照常执行；失败信息**带维度名**，
        //   末尾再打一行"本轮跳过了哪些维度"（顺序与逻辑**一个字都没改**，只改异常边界）。
        var skipped = new List<string>();
        void Dim(string name, Action act)
        {
            try { act(); }
            catch (Exception ex)
            {
                skipped.Add(name);
                logger.Warning($"[EventRotator][补做维度] ⚠ 维度「{name}」失败 ⇒ **只跳过这一个**，其余维度照常执行"
                             + $"（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        try
        {
            Dim("取消购买上限", () => ExtraDimensions.ApplyPurchaseLimits(ev.RemovePurchaseLimits == true, TraderAssortDimension.TraderTable));

            if (ev.BotHealthMultiplier != null && ev.BotHealthMultiplier.Count > 0)
                Dim("bot 血量倍率", () => ExtraDimensions.ApplyBotHealth(ev.BotHealthMultiplier));

            // ★★★【逐部位覆盖】`botHealthOverride`：按 bot 类型把指定部位的 `min`/`max` 直接写成给定值
            //   （绝对值；与上面的"倍率缩放全部部位"互补）。落点/还原/降级见 EventDef.BotHealthOverride 文档。
            if (ev.BotHealthOverride != null && ev.BotHealthOverride.Count > 0)
                Dim("bot 血量逐部位覆盖", () => ExtraDimensions.ApplyBotHealthOverride(ev.BotHealthOverride));

            if (ev.AimPunchMultiplier.HasValue)
                Dim("瞄准抖动倍率", () =>
                {
                    ExtraDimensions.ScaleConfigNumber("AimPunchMagnitude", ev.AimPunchMultiplier.Value);
                    ExtraDimensions.ScaleConfigObject("Aiming", ev.AimPunchMultiplier.Value);
                });
            if (ev.InertiaMultiplier.HasValue)
                Dim("惯性倍率", () => ExtraDimensions.ScaleConfigObject("Inertia", ev.InertiaMultiplier.Value));
            if (ev.OverdamageMultiplier.HasValue)
                Dim("受伤倍率", () =>
                {
                    var m = ev.OverdamageMultiplier.Value;
                    ExtraDimensions.ScaleConfigNumber("HandsOverdamage", m);
                    ExtraDimensions.ScaleConfigNumber("LegsOverdamage", m);
                    ExtraDimensions.ScaleConfigNumber("StomachOverdamage", m);
                });

            if (ev.ExitKeepOnly != null && ev.ExitKeepOnly.Count > 0 && ev.ExitLockMaps != null && ev.ExitLockMaps.Count > 0)
                Dim("撤离点锁定", () => ExtraDimensions.ApplyLocationExtras(ev.ExitKeepOnly, ev.ExitLockMaps, null));
            if (ev.TransitDisableMaps != null && ev.TransitDisableMaps.Count > 0)
                Dim("按图禁用转场", () => ExtraDimensions.ApplyLocationExtras(null, null, ev.TransitDisableMaps));

            // ★★ v0.1.5 第⑤条：按 transit id 精确禁用（"只禁进入某图的转场"）
            if (ev.TransitDisableIds != null && ev.TransitDisableIds.Count > 0)
                Dim("按 id 禁用转场", () => ExtraDimensions.ApplyLocationExtras(null, null, null, ev.TransitDisableIds));

            if (ev.BotEquipmentAdd != null && ev.BotEquipmentAdd.Count > 0)
                Dim("bot 装备追加", () => ExtraDimensions.ApplyBotEquipment(ev.BotEquipmentAdd));

            // ═══ ★★★ v0.1.5 第 1 组：照官方组合表补的 4 个小项（僵尸相关的按用户要求不补）═══
            //   依据：反编译 SeasonalEventService —— AddPumpkinsToScavBackpacks / RenameBitcoin /
            //   DefaultStimulatorBuff="Buffs_Halloween" / AdjustZryachiyMeleeChance
            if (ev.PumpkinScavBackpack == true) Dim("南瓜进 SCAV 背包", () => ExtraDimensions.ApplyPumpkinBackpack(true));
            if (ev.RenameBitcoin == true) Dim("比特币改名", () => ExtraDimensions.ApplyRenameBitcoin(true));
            if (ev.HalloweenStimBuff == true) Dim("万圣节针剂 buff", () => ExtraDimensions.ApplyHalloweenStimBuff(true));
            if (ev.ZryachiyMeleeChance == true) Dim("Zryachiy 近战率", () => ExtraDimensions.ApplyZryachiyMeleeChance(true));
            // ★ v0.1.5（对标 SVM）：随从类型（立交桥 Killa ← bossTagilla）
            if (ev.EscortType != null && ev.EscortType.Count > 0) Dim("随从类型", () => ExtraDimensions.ApplyEscortType(ev.EscortType));

            // ★★★ v0.1.5【SCAV 接管 · 路费机制】：登记本活动的路费配置（进图时由 ConfigureOfflineRaid prefix 执行）
            // ★★★ 路费：只在**活动真正套用**时走这里 ⇒ 活动关掉就不会发消息；
            //        且内部做状态比对 ⇒ 免费图/所需物品/日期变了才重发。
            if (ev.Toll != null)
                Dim("路费机制", () =>
                {
                    EventRotatorServer._toll = ev;
                    TollDimension.PublishIfChanged(ev);
                    TollDimension.ApplyAccessKeys(ev, locationTable, a => _restoreActions.Add(a));   // ★ 实验室门卡机制
                });

            // ★★★ 商人易物注入（官方「Cultist Pilgrimage / 邪教朝圣」：Mechanic 收邪教徒之刃 → 出 6 色门卡）
            //   ⚠ 这段**曾经一次都没生效**：旧 BarterDimension 用 string 当 Dictionary<MongoId,…> 的键
            //     ⇒ 抛 ArgumentException 被 catch 吞掉 ⇒ 6 条门卡配方静默丢失（task-34 已修 + 已用真实 DLL 验证）。
            //   现在 BarterDimension 是 TraderAssortAddDimension 的**薄适配器**，两者共用唯一实现。
            if (ev.TraderBarterAdd != null && ev.TraderBarterAdd.Count > 0)
                Dim("商人易物（barterAdd）", () =>
                {
                    BarterDimension.Log = logger; BarterDimension.AddRestore = a => _restoreActions.Add(a);
                    BarterDimension.TraderTable = TraderAssortDimension.TraderTable;
                    foreach (var kv in ev.TraderBarterAdd) BarterDimension.Apply(kv.Key, kv.Value);
                });

            // ══════════════════════════════════════════════════════════════════════════
            // ★★★ task-34（⑧⑨ 三维度）—— 三个"能做没做"的维度，各自落在已实证的读点上
            // ══════════════════════════════════════════════════════════════════════════

            // ⑨-A 藏身处升级需求注入（官方 beer_upgrades：Pevko 啤酒进升级材料）
            //   落点 areas[].stages[].requirements[]；SPT 只读 stages（HideoutController.cs:86/124/205/1093）
            if (ev.HideoutAddRequirements != null && ev.HideoutAddRequirements.Count > 0)
                Dim("藏身处升级需求", () =>
                {
                    HideoutRequirementDimension.Log = m => logger.Info(m);
                    HideoutRequirementDimension.AddRestore = a => _restoreActions.Add(a);
                    HideoutRequirementDimension.Apply(ev, hideoutTable, logger);
                });

            // ⑧-B 按 (地图 + tpl) 调松散战利品权重（官方 softcore 第 5 条：实验室彩卡刷新率）
            //   落点 Location.LooseLoot.Value.Spawnpoints[].Probability / ItemDistribution[].RelativeProbability
            if (ev.LooseLootWeightSet != null && ev.LooseLootWeightSet.Count > 0)
                Dim("松散战利品权重", () =>
                {
                    LooseLootWeightDimension.LocationTable = locationTable;
                    LooseLootWeightDimension.Log = m => logger.Info(m);
                    LooseLootWeightDimension.AddRestore = a => _restoreActions.Add(a);
                    LooseLootWeightDimension.Apply(ev, logger);
                });

            // ★★★ 地面战利品注入（加点位 + 换成指定物品；官方 Wild Wild Woods 的"新增刷新点"那一半）
            //   落点 = Location.LooseLoot.Value.Spawnpoints[]（克隆）+ SpawnpointCount.Mean（局内数量）
            //        + SpawnpointTemplate.Items / Spawnpoint.ItemDistribution（换物品）
            if (ev.LooseLootInject != null && ev.LooseLootInject.Count > 0)
                Dim("地面战利品注入", () =>
                {
                    LooseLootInjectDimension.LocationTable = locationTable;
                    LooseLootInjectDimension.TemplateTable = templateTable;   // ★ 容器类 tpl 诊断（IsContainer / _props.Grids）
                    LooseLootInjectDimension.Log = m => logger.Info(m);
                    LooseLootInjectDimension.AddRestore = a => _restoreActions.Add(a);
                    LooseLootInjectDimension.Apply(ev);
                });

            // ★★★ 容器战利品注入（按容器类型往静态容器里塞对应高资；官方 Wild Wild Woods 的
            //   "容器里能摸出更好的东西"那一半）
            //   落点 = Location.StaticContainers（LazyLoad<StaticContainerDetails>）→ StaticContainers[].Template.Items
            //   尺寸过滤用 TemplateTable.Items 的 _props.Width/Height 与容器 _props.Grids[].cellsH/cellsV
            if (ev.ContainerLootInject != null && ev.ContainerLootInject.Count > 0)
                Dim("容器战利品注入", () =>
                {
                    ContainerLootInjectDimension.LocationTable = locationTable;
                    ContainerLootInjectDimension.TemplateTable = templateTable;
                    ContainerLootInjectDimension.Log = m => logger.Info(m);
                    ContainerLootInjectDimension.AddRestore = a => _restoreActions.Add(a);
                    ContainerLootInjectDimension.Apply(ev);
                });

            // ★★★ task-28【静态战利品容器注入】（PPES 的 AddStaticLoot：抽屉/外套/电脑里能翻出指定物品）
            //   落点 = Location.StaticLoot（LazyLoad<Dictionary<MongoId, StaticLootDetails>>）
            //        → StaticLootDetails.ItemDistribution（{tpl, relativeProbability}）+ AddTransformer
            //   LocationLootGenerator.GenerateStaticContainers 每局读它 ⇒ 改这里才是"翻抽屉能捡到"。
            if (ev.StaticLootAdd != null)
                Dim("静态战利品注入", () =>
                {
                    StaticLootAddDimension.LocationTable = locationTable;
                    StaticLootAddDimension.TemplateTable = templateTable;   // 容器名字匹配（_name）
                    StaticLootAddDimension.Log = m => logger.Info(m);
                    StaticLootAddDimension.AddRestore = a => _restoreActions.Add(a);
                    StaticLootAddDimension.Apply(ev);
                });

            // ① 商人万圣节头像（官方 SeasonalEventService.cs:450 → ApplyHalloweenEvent 内**无条件**调用；
            //   归属 = 万圣节家族级，故所有万圣节活动都声明 eventTraderIcons:true）
            //   ⚠ 不照抄官方那条 ServerImagePathOverride —— IL 扫描证实全 SPT 无人读它（死机制）；
            //     真实落点 = Trader.Base.Avatar → /files/trader/halloween/<id>.png（走 ImageRouteImporter 路由）
            if (ev.EventTraderIcons == true)
                Dim("商人万圣节头像", () =>
                {
                    ExtraDimensions.Log = logger;                      // ISptLogger，不是 Action
                    ExtraDimensions.AddRestore = a => _restoreActions.Add(a);
                    // ★ 2026-10-30：先把 mod 自带的万圣节头像铺进 SPT_Data（幂等；没带图就静默跳过）
                    TraderIconAssetCopier.Info = m => { try { logger.Info(m); } catch { } };
                    TraderIconAssetCopier.Warn = m => { try { logger.Warning(m); } catch { } };
                    TraderIconAssetCopier.Run(EventEngine.ModPath);
                    // ① 立刻试一次（表已装满时就成了，大多数"换活动/进图"路径都是这样）
                    ExtraDimensions.ApplyTraderIcons(true, TraderAssortDimension.TraderTable);
                    // ② 登记"延迟补做"：SPT 的 TradersTable 在 OnLoad 时刻**还没装满**
                    //    （实机铁证：12:40:34 OnLoad 时表里只有 Fence ⇒「改 1 个、找不到 7 个」；
                    //      12:42:55 用户 F12 二次套用时同一张表已能找到 Mechanic）。
                    //    ⇒ 由 Patches/TraderIconPatch 在 TraderHelper.GetAllTraders（= traderSettings 构建点，
                    //      也正是客户端来取头像的那一刻）补做，那时表必然已装满，且幂等。
                    ExtraDimensions.WantTraderIcons = true;
                    ExtraDimensions.TraderIconTableRef = TraderAssortDimension.TraderTable;
                });

            // ②③ 按配方 id 覆写藏身处配方（啤酒 30h/1 瓶）+ 按 areaType 缩放制作时间（集水器效率）
            //   ⚠ 不能用现成的 hideoutCraftSeconds —— 那是**全局上限**（HideoutTraderDimension.cs:150-165
            //     的 ClampCeiling 遍历所有配方取 min），既不能只改一条、也只能降不能升。
            if ((ev.HideoutRecipeOverride != null && ev.HideoutRecipeOverride.Count > 0)
                || ev.WaterCollectorEfficiency.HasValue)
                Dim("藏身处配方覆写/集水器", () =>
                {
                    ExtraDimensions.Log = logger;                      // ISptLogger，不是 Action
                    ExtraDimensions.AddRestore = a => _restoreActions.Add(a);
                    // 集水器 = HideoutAreas.WaterCollector = 6（areas.json type 6 ↔ locale hideout_area_6_name）
                    ExtraDimensions.ApplyRecipeOverride(ev.HideoutRecipeOverride, hideoutTable,
                                                        ev.WaterCollectorEfficiency, 6);
                });

            // ⑧-C 往商人 assort 追加一条易物（官方 softcore 第 5 条：LL2 狗牌易物）            //   ★ 这是**唯一实现**：旧 `traderBarterAdd`（BarterDimension）已在 task-34 收尾时
            //     统一到本维度上（它原先的 MongoId 键转换 / CloneScheme 返回层级两处硬故障已废弃）。
            //     ⇒ 两个活动字段共用同一套注入逻辑，不存在两套实现。
            if (ev.TraderAssortAdd != null && ev.TraderAssortAdd.Count > 0)
                Dim("商人易物追加（assortAdd）", () =>
                {
                    TraderAssortAddDimension.TraderTable = TraderAssortDimension.TraderTable;
                    TraderAssortAddDimension.Log = m => logger.Info(m);
                    TraderAssortAddDimension.AddRestore = a => _restoreActions.Add(a);
                    TraderAssortAddDimension.Apply(ev, logger);
                });

            // ★★★ v0.1.5 ② 首领时刻表：记下当前活动，进图时按系统时钟落到对应地图
            if (ev.BossTimetable != null)
                Dim("首领时刻表登记", () =>
                {
                    TimetableDimension.Log = logger;
                    _timetable = ev;   // 单活动生效即可（多活动时取最后一个声明的）
                });

            // ★★★ v0.1.5【ABPS 接管修复】把 bossChance 同步写进 ABPS 的运行期配置对象。
            //   实测根因：ABPS 接管刷怪后，改 location 表的 BossChance 对它无效 ⇒ 邪教徒不刷。
            if (ev.BossChance != null && ev.BossChance.Count > 0 && AbpsCompat.IsPresent)
                Dim("ABPS 首领概率同步", () =>
                {
                    foreach (var mk in ev.BossChance)
                        foreach (var bk in mk.Value)
                        {
                            int pct = (int)Math.Round(Convert.ToDouble(bk.Value));
                            AbpsCompat.SetSpawnChance(bk.Key, mk.Key, pct, a => _restoreActions.Add(a));
                        }
                });
            // ★★★ v0.1.5 第 2~4 组：直接消费官方"组合表"数据（seasonalEventConfig）
            //   ★ P1-1 关键点：这三条以前和下面所有的圣诞/万圣节内容**共用一口大锅 catch** ⇒
            //     只要其中一条抛，`GifterOfficial`/`PlayerGift` 等**全部静默消失**。现在各管各的。
            Dim("官方 bot 装备", () => ExtraDimensions.ApplyOfficialGear(ev.OfficialGear, seasonalEventConfig,
                                                                        ev.EventGearKeepBossIconic == true));
            Dim("官方 bot 掉落", () => ExtraDimensions.ApplyOfficialLoot(ev.OfficialLoot, seasonalEventConfig));
            Dim("bot 外观", () => ExtraDimensions.ApplyBotAppearance(ev.BotAppearance, seasonalEventConfig));

            // ★★★ v2.6.14【外观覆盖·用户要求】按 bot 类型直接覆盖 `Appearance` 段。
            //   现状：本体数据给 `ravangezryachiyevent`（复仇小鹿）的 body/feet/head 是
            //   **普通 SCAV 通用外观**（`wild_body_1`/`wild_feet_wasatch`/`wild_head_1`，
            //   分别被 9/11/24 个 bot 类型共用），不是小鹿外观
            //   （`Top_BOSS_Zryachi`/`Pants_BOSS_Zryachi`/`Head_BOSS_Zryachi`，各只被 `bosszryachiy` 用）。
            //   **这是官方数据行为**（文件 mtime 与全库一致、未被任何 mod 改过；官方 wiki 的
            //   `===Zryachiy Equipment===` 也只有 Headwear/Face cover 标注 "Only on Vengeful Zryachiy"）。
            //   用户 2026-10-16 明确要求覆盖成本体小鹿外观（"背离原始数据但符合直觉"）。
            Dim("bot 外观覆盖（小鹿）", () => ExtraDimensions.ApplyBotAppearanceOverride(ev.BotAppearanceOverride, botTable));

            // ★★★ v2.6.6【PBS 兼容】把同一份官方装备表**再写一遍进 PBS 的运行期池子**。
            //   反编译实证：PBS 用 Harmony Prefix 拦截 `BotInventoryGenerator.GenerateInventory`
            //   （`GenerateInventoryPatch.Prefix` 返回 false），装备生成全走它自己的
            //   `CustomBotInventoryGenerator`，数据来自 `DataLoader.AllTierDataDirty`，
            //   **完全不读 `BotType.BotInventory.Equipment``** ⇒ 上面那行 `ApplyOfficialGear`
            //   在装了 PBS 的本机等于写进没人读的地方（实测：南瓜头盔/鬼脸面罩一件都不出现）。
            //   概率同理（`GetChancesByBotRole`），所以 `PbsCompat` 会连概率一起抬到官方下界 75。
            if (ev.OfficialGear != null && ev.OfficialGear.Count > 0 && PbsCompat.IsPresent)
                Dim("PBS 装备池同步", () =>
                {
                    var gearTable = ExtraDimensions.ReadSlotTableForCompat(seasonalEventConfig, "EventGear", ev.OfficialGear);
                    if (gearTable != null && gearTable.Count > 0)
                        PbsCompat.ApplyEventGear(gearTable, a => _restoreActions.Add(a),
                                                 ev.EventGearKeepBossIconic == true);   // ★ v2.6.39 opt-in（默认 false）
                });
            // ★★★ v0.1.5 完全还原同步 A~D
            if (ev.GifterOfficial == true) Dim("圣诞老人", () => ExtraDimensions.ApplyGifterOfficial(seasonalEventConfig, locationTable));
            if (ev.ChristmasLootBoost.HasValue) Dim("圣诞爆率", () => ExtraDimensions.ApplyChristmasLootBoost(ev.ChristmasLootBoost.Value));
            if (ev.BtrTarcolaSkin == true) Dim("BTR 涂装", () => ExtraDimensions.ApplyBtrTarcolaSkin(true));
            if (ev.RunddansActive == true) Dim("圆环舞开关", () => ExtraDimensions.ApplyRunddansActive(true));
            if (ev.PlayerGift != null && ev.PlayerGift.Count > 0)
                Dim("季节礼物", () => ExtraDimensions.ApplyPlayerGift(ev.PlayerGift, giftService, saveServer));
        }
        // 兜底网（理论上不该触发：Dim 已经把每个维度的异常都吃了）——留着以防"维度分发器自身"出问题
        catch (Exception ex) { logger.Warning($"[EventRotator][补做维度] 维度分发器自身异常: {ex.GetType().Name}: {ex.Message}"); }

        if (skipped.Count > 0)
            logger.Warning($"[EventRotator][补做维度] ⚠ 本轮共跳过 {skipped.Count} 个维度：{string.Join("，", skipped)}"
                         + "（**其余维度都已正常执行**；每个维度的失败原因见上面各自那条带维度名的警告）");
    }

    /// <summary>
    /// ★★★ v0.1.5 新增：**复刻官方 `SeasonalEventService.ApplyChristmasEvent`**（窗外强制开启圣诞活动时官方不会执行）。
    ///
    /// 官方原文（反编译）：
    ///   · `if (Settings?.EnableChristmasHideout == true) { EventType = …去掉 None; EventType.Add(EventType.Christmas); }`
    ///   · `botTable.Core.ActivePatrolGeneratorEvent = true;`
    ///   · `AddEventGearToBots / AddEventLootToBots`
    ///   · `if (Settings?.EnableSanta == true) { AddGifterBotToMaps(); AddLootItemsToGifterDropItemsList(); }`
    ///   · `EnableDancingTree();`  `AdjustBotAppearanceValues`  `EnableRunnansEvent`
    /// 本方法复刻前两项（全局信号 + 巡逻生成器），其余见活动 desc / 已知做不到清单。
    /// 圣诞老人的**刷新**由活动数据里的 `bossSpawns`/`bossChance` 下发 `gifter`（本机 `bots/types/gifter.json` 存在）。
    /// </summary>
    private void PrepareChristmasNative(bool want)
    {
        try
        {
            // ★★★ v1.0.1【BugFix·全量审计#2】官方圣诞季会被本函数误伤：
            //   本函数在 `want=false` 时会**主动清空**三处圣诞镜像值（EventType 去 Christmas /
            //   gifter ItemsToDrop 清空 / Khorovod.Chance 归 0）。它原本**不知道"现在是不是官方圣诞窗"**，
            //   于是「12/13–1/14 期间开一个非圣诞活动」就会把 SPT 自己刚激活的官方圣诞关掉，
            //   而且三处都没登记还原 ⇒ 关掉活动也恢复不了，只能重启服务端。
            //   修法：`want=false` 且 **SPT 自己已激活圣诞季**（IsSeasonalActive）⇒ 整段**跳过**。
            //   为什么这样是"更正确"而不是妥协：
            //     · SPT 的 `ForceSeasonalEvent` **没有回滚**（本文件 2329 附近注释已写明）
            //       ⇒ 我们"关掉"的只是**镜像值**，SPT 内部仍认为圣诞开着 ⇒ 清了反而制造不一致状态。
            //     · `want=true`（轮换到冬天 / 开圣诞活动）路径**完全不受影响**。
            if (!want && IsSeasonalActive("Christmas"))
            {
                logger.Info("[EventRotator][圣诞] 官方圣诞季当前生效（IsSeasonalActive=true）⇒ **跳过关圣诞**"
                          + "（避免误伤官方日期窗 12/13–1/14；此时清了会与 SPT 内部状态不一致）");
                return;
            }

            // ① 客户端圣诞信号：globals.config.EventType 加/去 Christmas
            try
            {
                var cfgObj = PropOf(globalTable, "Configuration");
                var etProp = cfgObj?.GetType().GetProperty("EventType",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                var etList = etProp?.GetValue(cfgObj) as System.Collections.IList;
                if (etList != null)
                {
                    var enumType = etList.GetType().IsGenericType
                        ? etList.GetType().GetGenericArguments()[0]
                        : (etList.Count > 0 ? etList[0]!.GetType() : null);
                    if (enumType != null && enumType.IsEnum)
                    {
                        object Val(string n) => Enum.Parse(enumType, n, true);
                        bool Has(object v) { foreach (var x in etList) if (Equals(x, v)) return true; return false; }
                        var vNone = Val("None"); var vXmas = Val("Christmas");
                        if (want)
                        {
                            if (Has(vNone)) { etList.Remove(vNone); _restoreActions.Add(() => { try { if (!Has(vNone)) etList.Add(vNone); } catch { } }); }
                            if (!Has(vXmas)) { etList.Add(vXmas); _restoreActions.Add(() => { try { if (Has(vXmas)) etList.Remove(vXmas); } catch { } }); }
                            logger.Warning($"[EventRotator][圣诞] ★globals.config.EventType → [{string.Join(", ", etList.Cast<object>())}]"
                                         + "（客户端圣诞信号；官方 ApplyChristmasEvent 的第一步，窗外强制时官方不执行）");
                        }
                        else if (Has(vXmas))
                        {
                            etList.Remove(vXmas);
                            if (!Has(vNone)) etList.Add(vNone);
                            if (SigChanged("et-restore-xm", string.Join(",", etList.Cast<object>())))
                                logger.Info($"[EventRotator][圣诞] EventType 已还原 → [{string.Join(", ", etList.Cast<object>())}]");
                        }
                    }
                }
            }
            catch (Exception e1) { logger.Warning($"[EventRotator][圣诞] 设置 EventType 失败: {e1.Message}"); }

            // ② botTable.Core.ActivePatrolGeneratorEvent（官方 ApplyChristmasEvent 无条件设置）
            try
            {
                var core = botTable?.GetType().GetProperty("Core",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(botTable);
                var pf = core?.GetType().GetProperty("ActivePatrolGeneratorEvent",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                if (pf != null && pf.CanWrite)
                {
                    var cur = pf.GetValue(core);
                    bool curB = cur is bool b && b;
                    if (curB != want)
                    {
                        var cap = core; var capP = pf; var capO = cur;
                        _restoreActions.Add(() => { try { capP.SetValue(cap, capO); } catch { } });
                        pf.SetValue(core, want);
                        logger.Warning($"[EventRotator][圣诞] ★ActivePatrolGeneratorEvent: {curB} → {want}（官方 ApplyChristmasEvent 同款）");
                    }
                }
                else logger.Info("[EventRotator][圣诞] 拿不到 BotTable.Core.ActivePatrolGeneratorEvent（跳过，不影响圣诞老人刷新）");
            }
            catch (Exception e2) { logger.Warning($"[EventRotator][圣诞] 设置巡逻生成器开关失败: {e2.Message}"); }

            // ③ ★ v0.1.5 复刻官方 `AddLootItemsToGifterDropItemsList()`：
            //   官方把"圣诞老人背包（gifter.json → inventory.items.Backpack，本机 1856 件）"里的**所有物品**
            //   并进 `BotDifficulty[难度].Patrol.ItemsToDrop`。不跑这段，圣诞老人**手里有礼物但不会掉**（实测原值=None）。
            try
            {
                var gifter = PropOf(PropOf(botTable, "Types"), null);   // Types 是字典，取不到就走下面的兜底
            }
            catch { }
            try
            {
                var types = botTable?.GetType().GetProperty("Types",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(botTable) as System.Collections.IDictionary;
                object gifter = null;
                if (types != null)
                {
                    foreach (System.Collections.DictionaryEntry de in types)
                        if (string.Equals(de.Key?.ToString(), "gifter", StringComparison.OrdinalIgnoreCase)) { gifter = de.Value; break; }
                }
                if (gifter == null) logger.Info("[EventRotator][圣诞] 拿不到 botTable.Types['gifter']，跳过礼物清单复刻");
                else
                {
                    // 背包物品（BotInventory.Items.Backpack 的键）
                    var inv = PropOf(gifter, "BotInventory") ?? PropOf(gifter, "Inventory");
                    var items = PropOf(inv, "Items");
                    var backpack = PropOf(items, "Backpack") as System.Collections.IDictionary;
                    var giftIds = new List<string>();
                    if (backpack != null) foreach (System.Collections.DictionaryEntry de in backpack) giftIds.Add(de.Key?.ToString() ?? "");
                    var diff = PropOf(gifter, "BotDifficulty") as System.Collections.IDictionary;
                    int touched = 0;
                    if (diff != null)
                        foreach (System.Collections.DictionaryEntry de in diff)
                        {
                            var patrol = PropOf(de.Value, "Patrol");
                            if (patrol == null) continue;
                            var pf = patrol.GetType().GetProperty("ItemsToDrop",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.IgnoreCase);
                            if (pf == null || !pf.CanWrite) continue;
                            var oldV = pf.GetValue(patrol) as string;
                            if (want)
                            {
                                if (giftIds.Count == 0) continue;
                                var set = new HashSet<string>(StringComparer.Ordinal);
                                if (!string.IsNullOrWhiteSpace(oldV)) foreach (var s in oldV!.Split(',')) if (s.Length > 0) set.Add(s);
                                foreach (var g in giftIds) set.Add(g);
                                var capP = patrol; var capF = pf; var capO = pf.GetValue(patrol);
                                _restoreActions.Add(() => { try { capF.SetValue(capP, capO); } catch { } });
                                pf.SetValue(patrol, string.Join(",", set));
                                touched++;
                            }
                            else if (!string.IsNullOrEmpty(oldV))
                            {
                                pf.SetValue(patrol, "");
                                touched++;
                            }
                        }
                    if (SigChanged("gifter-gifts", $"{giftIds.Count}/{touched}"))
                        logger.Warning($"[EventRotator][圣诞] ★礼物清单已复刻：gifter 背包 {giftIds.Count} 件 → ItemsToDrop（{touched} 个难度）"
                                     + "（官方 AddLootItemsToGifterDropItemsList 同款；不做这段圣诞老人不会掉礼物）");
                }
            }
            catch (Exception e3) { logger.Warning($"[EventRotator][圣诞] 礼物清单复刻失败: {e3.Message}"); }

            // ④ ★ v0.1.5 复刻官方 `EnableDancingTree()`：
            //   官方语义（反编译实证）**不是藏身处圣诞树**，而是把**所有非藏身处地图**的
            //   `Base.Events.Khorovod.Chance` 与 `Base.BotLocationModifier.KhorovodChance` 设为 100（环舞/Khorovod 事件）。
            try
            {
                var dict = locationTable?.GetType().GetMethod("GetDictionary")?.Invoke(locationTable, null) as System.Collections.IDictionary;
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hideout", "base", "privatearea" };
                int maps = 0;
                if (dict != null)
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        var key = de.Key?.ToString() ?? "";
                        if (skip.Contains(key)) continue;
                        var b = PropOf(de.Value, "Base");
                        var kh = PropOf(PropOf(b, "Events"), "Khorovod");
                        var blm = PropOf(b, "BotLocationModifier");
                        foreach (var pair in new[] { (kh, "Chance"), (blm, "KhorovodChance") })
                        {
                            var o = pair.Item1; var pn = pair.Item2;
                            if (o == null) continue;
                            var pf = o.GetType().GetProperty(pn, System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                            if (pf == null || !pf.CanWrite) continue;
                            // ★ 官方反编译里 `Khorovod.Chance` 是 **double?**（可空）—— 原实现只认 double，导致 0 处命中。
                            var pt = pf.PropertyType;
                            bool isDouble = pt == typeof(double) || pt == typeof(double?);
                            if (!isDouble) continue;
                            var rawOld = pf.GetValue(o);
                            double oldD = rawOld == null ? 0.0 : Convert.ToDouble(rawOld);
                            if (want && Math.Abs(oldD - 100.0) > 0.001)
                            {
                                var capO = o; var capF = pf; var capV = rawOld;
                                _restoreActions.Add(() => { try { capF.SetValue(capO, capV); } catch { } });
                                pf.SetValue(o, pt == typeof(double?) ? (double?)100.0 : (object)100.0); maps++;
                            }
                            else if (!want && Math.Abs(oldD - 100.0) < 0.001)
                            {
                                pf.SetValue(o, pt == typeof(double?) ? (double?)0.0 : (object)0.0); maps++;
                            }
                        }
                    }
                if (SigChanged("khorovod", $"{maps}/{want}"))
                    logger.Warning($"[EventRotator][圣诞] ★环舞(Khorovod)机会已复刻：{maps} 处设为 {(want ? 100 : 0)}（官方 EnableDancingTree 同款）");
            }
            catch (Exception e4) { logger.Warning($"[EventRotator][圣诞] 环舞机会复刻失败: {e4.Message}"); }
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][圣诞] 复刻 ApplyChristmasEvent 失败: {ex.Message}"); }
    }

    private void PrepareHalloweenNative(bool wantZombies)
    {
        try
        {
            if (seasonalEventConfig?.Events == null)
            { logger.Warning("[EventRotator][万圣节] 拿不到 SeasonalEventConfig，跳过原生开关预处理"); return; }

            var halloween = seasonalEventConfig.Events.FirstOrDefault(
                e => string.Equals(e?.Type.ToString(), "Halloween", StringComparison.OrdinalIgnoreCase));
            if (halloween == null)
            { logger.Warning("[EventRotator][万圣节] seasonalevents.json 里找不到 Halloween 事件，跳过"); return; }

            var zs = halloween.Settings?.ZombieSettings;
            if (zs == null)
                logger.Warning("[EventRotator][万圣节] 官方 halloween.settings.zombieSettings 为空，无法切换僵尸模式");
            else
            {
                bool before = zs.Enabled == true;
                if (before != wantZombies)
                {
                    zs.Enabled = wantZombies;
                    logger.Warning($"[EventRotator][万圣节] 原生僵尸模式 zombieSettings.enabled: {before} → {wantZombies}"
                        + (wantZombies
                            ? "　⇒ 官方将清空实验室首领表 + 实验室 100% 感染 + 追加官方 infected 条目"
                            : "　⇒ 实验室不长僵尸（下方会护住实验室首领表）"));
                }
                else
                { if (SigChanged("zombie-keep", wantZombies.ToString())) logger.Info($"[EventRotator][万圣节] 原生僵尸模式保持 enabled={wantZombies}"); }

                // ★★★ v0.1.5：官方 `ConfigureZombies` 只在**季节窗内**跑（10/28–11/9 由 UpdateGlobalEvents 触发）。
                //   窗外手动强制时它整段不执行 ⇒ 最关键的 `ActiveHalloweenZombiesEvent` 没被设置
                //   ⇒ 用户实测现象「僵尸正常刷新、行动，但**不攻击玩家**」。这里自己补齐。
                ReplicateConfigureZombies(wantZombies);
            }

            // ② 实验室锤哥护栏（幂等；启动时已摘过，这里兜底再确认一次）
            ApplyHalloweenSummonPolicy();
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][万圣节] 原生开关预处理失败: {ex.Message}"); }
    }

    /// <summary>
    /// 季节预处理：按季节名做"官方流程复刻"（官方那段只在现实日期窗口内跑，窗外强制开启时必须自己来）。
    ///   HALLOWEEN           → 僵尸模式开关 / EventType / 清实验室首领表
    ///   CHRISTMAS / NEWYEARS → 圣诞 EventType / 巡逻生成器开关（★ v0.1.5 圣诞复刻）
    /// 方法名沿用旧名以免动 4 处调用点。
    /// </summary>
    private void MaybePrepareHalloween(string seasonName)
    {
        bool isHalloween = string.Equals(seasonName, "HALLOWEEN", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(seasonName, "Halloween", StringComparison.OrdinalIgnoreCase);
        bool isChristmas = string.Equals(seasonName, "CHRISTMAS", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(seasonName, "Christmas", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(seasonName, "NEWYEARS", StringComparison.OrdinalIgnoreCase);

        if (isHalloween) PrepareHalloweenNative(WantNativeZombies);
        if (isChristmas) PrepareChristmasNative(WantNativeChristmas);
        if (!isHalloween && !isChristmas)      // 非季节活动 ⇒ 两边都还原
        {
            PrepareHalloweenNative(false);
            PrepareChristmasNative(false);
        }
    }

    /// <summary>
    /// ★ v0.1.5 **同一个服务器进程内，每个季节只强推一次**。
    ///
    /// 为什么必须这样（审计实证，SPT 4.1.6）：
    ///   `ForceSeasonalEvent` 的改动**全部落在内存单例表上且没有回滚**，而且有些是**追加式**的：
    ///     · `ApplyChristmasEvent` L487-525 给**每张图** `AddTransformer` 一个圣诞物资概率提升器
    ///       （`LazyLoad.AddTransformer` 是**追加**）⇒ 每强推一次就多叠一层概率，**重复强推 = 概率累积**；
    ///     · `AddGifterBotToMaps` / `AddEventBossesToMaps` / 商人图标 / `EventSettings.EventActive` 等同样只增不减。
    ///   而本 mod 的 `RestoreThenApply()` 是**每次进图**都会跑的（`EventEngine.OnRefresh`）⇒ 旧代码等于**每局都强推一次季节**。
    ///
    /// 现在：同进程内同季节只强推第一次；如果已经强推过**另一个**季节，则只告警不执行
    /// （因为 SPT 无法回滚，换季节本来就必须重启服务端 —— 与本 mod "活动重启才生效"的原则一致）。
    /// </summary>
    private void ForceSeasonalOnce(string seasonName, Action doForce, string reason)
    {
        if (string.IsNullOrEmpty(seasonName)) return;
        bool sameAsBefore = string.Equals(_forcedSeasonalName, seasonName, StringComparison.OrdinalIgnoreCase);

        // ① SPT 自己已经激活了这个季节（真实日期窗内 `PostDbLoadService` 会先跑一次官方激活）
        //    → **不能**再整段 ForceSeasonalEvent：`AddEventBossesToMaps` 的判重对
        //      `TriggerName=="botEvent"` 的条目无效（官方条目绝大多数都是 botEvent）⇒ 会**追加第二遍**。
        //    只做本 mod 需要的补充（僵尸开关），官方内容保持它自己那一次。
        if (!sameAsBefore && IsSeasonalActive(seasonName))
        {
            logger.Info($"[EventRotator] 季节 {seasonName} 已由 SPT 自己激活 → 跳过重复强推（{reason}），只补本 mod 需要的部分");
            _forcedSeasonalName = seasonName;
            EnsureZombies(seasonName);
            return;
        }

        if (sameAsBefore) { EnsureZombies(seasonName); return; }   // 本进程已推过同一季节（防累积）

        if (!string.IsNullOrEmpty(_forcedSeasonalName))
        {
            logger.Warning($"[EventRotator] ★季节强推已跳过：本进程已强推过 {_forcedSeasonalName}，"
                         + $"现在要换 {seasonName}（{reason}）—— SPT 的 ForceSeasonalEvent 无回滚，"
                         + "中途换季节会叠加残留（圣诞物资概率等），**请重启服务端**");
            return;
        }

        doForce();
        _forcedSeasonalName = seasonName;
        EnsureZombies(seasonName);      // 兜底：doForce 里通常已经开过了，这里会是空操作
    }

    /// <summary>官方季节是否已经处于激活状态（只有 Halloween / Christmas 有公开 getter）。</summary>
    private bool IsSeasonalActive(string seasonName)
    {
        try
        {
            string getter = seasonName.ToUpperInvariant() switch
            {
                "HALLOWEEN" => "HalloweenEventEnabled",
                "CHRISTMAS" => "ChristmasEventEnabled",
                _ => null,
            };
            if (getter == null) return false;
            var m = seasonalEventService.GetType().GetMethod(getter, BindingFlags.Instance | BindingFlags.Public);
            return m != null && m.Invoke(seasonalEventService, null) is bool b && b;
        }
        catch { return false; }
    }

    /// <summary>
    /// ★ v0.1.5 兜底：需要原生僵尸、但官方僵尸开关还关着时，**直接反射调用官方的 `ConfigureZombies`**
    /// （protected 方法），而不是再整段 `ForceSeasonalEvent(Halloween)`。
    ///
    /// 适用两种情形：① 季节已被 SPT 自己激活（见上）；② 同季内切换变体（如 halloween_2022 → halloween_2024）
    /// 时 `_forcedSeasonalName` 相同、跳过了强推，但僵尸开关需要跟着变。
    /// `ConfigureZombies` 本身是幂等的：先 `&lt;实验室&gt;.Base.BossLocationSpawn = 新空表`，再按官方表追加 infected。
    /// </summary>
    private void EnsureZombies(string seasonName)
    {
        if (!WantNativeZombies) return;
        if (seasonName == null || !seasonName.StartsWith("Halloween", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var zs = seasonalEventConfig?.Events?.FirstOrDefault(
                e => string.Equals(e?.Type.ToString(), "Halloween", StringComparison.OrdinalIgnoreCase))
                ?.Settings?.ZombieSettings;
            if (zs == null) return;
            if (zs.Enabled == true) return;      // 上一次强推里已经打开过 → 不重复调用

            var m = seasonalEventService.GetType().GetMethod("ConfigureZombies",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) { logger.Warning("[EventRotator][万圣节] 找不到官方 ConfigureZombies，僵尸无法直接开启"); return; }

            zs.Enabled = true;                   // 状态保持一致，避免重复调用
            m.Invoke(seasonalEventService, new object[] { zs });
            logger.Warning("[EventRotator][万圣节] ★季节已激活 → 直接调用官方 ConfigureZombies 开僵尸"
                         + "（不再重复整段 ApplyHalloweenEvent，避免召唤/首领条目被追加第二遍）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][万圣节] 直接开僵尸失败: {ex.Message}"); }
    }

    /// <summary>★ v0.1.5 本进程已强推过的季节（null = 还没推过）。</summary>
    private string _forcedSeasonalName;

    /// <summary>
    /// <summary>
    /// ★★★ v2.6.46【用户裁决 **A+B**】`halloweensummon`（Zryachiy 召唤仪式）按**活动年份**门控。
    ///
    /// <para><b>官方原文（决定性）</b>：<c>events_wiki.txt:1026-1031</c>
    /// <code>
    /// ==Halloween 2023 Part 2 (30 October 2023)==
    /// *[[Cultist]] ritual on all maps besides [[Factory]] and [[The Lab]].
    /// **Finding and killing the roaming [[Zryachiy]] will close all extractions but 1 … start a ritual …
    /// </code>
    /// ⇒ **整场 `halloweensummon` 都是 `Halloween 2023 Part 2` 的内容** ✗
    /// ⇒ 用户开 `halloween_2022` 却看到 2023 的 Zryachiy = **真 bug**（用户实测截图抓到）。</para>
    ///
    /// <para><b>A（默认摘掉整场）</b>：除非下条 B 放行，否则把 `eventBossSpawns.halloweensummon`
    /// 的**全部地图条目**摘掉（bigmap/interchange/lighthouse/rezervbase/shoreline/tarkovstreets/woods）。
    /// ⚠ 官方 `UpdateGlobalEvents` 的 **`Halloween` 与 `AprilFools` 两个分支**都会调
    /// `AddEventBossesToMaps("halloweensummon")` ⇒ 摘掉后**愚人节也不再注入**（用户口径：愚人节不要它）。</para>
    ///
    /// <para><b>B（`halloween_2023` 在跑时放行）</b>：仅当**当前生效活动**里存在 <c>halloween_2023</c>
    /// 时才保留 —— 判据用**活动 id 精确匹配**（见 <see cref="HalloweenSummonAllowed"/>），
    /// **不用"family 里有 halloween"这种宽判据**：`halloween` / `halloween_2022` / `halloween_2023`
    /// **family 全等**（都是 `halloween`），只有 `familyYear` 不同 ⇒ 宽判据会把 2022 也放行 ✗（正是本次 bug 的形状）。</para>
    ///
    /// <para><b>⚠ `laboratory` 永远摘掉</b>：官方原文「ritual on all maps besides [[Factory]] and [[The Lab]]」
    /// ⇒ **无论 B 是否放行**，The Lab 都不该有它 ✔（官方表里却给 laboratory 配了 `bossTagilla` ——
    /// 官方数据与官方说明不一致，我们以**说明**为准）。</para>
    ///
    /// <para><b>⚠ 可降级（fail-safe：宁可少放不可乱放）</b>：拿不到活动列表 / 判定抛异常
    /// ⇒ **按 A 处理（摘掉整场）** + warning ✔</para>
    ///
    /// <para><b>可逆</b>：首次调用时把**原始表**快照进 <see cref="_summonOriginal"/>；
    /// 每次进来先**还原成原样**再按策略摘 ⇒ 反复调用/切换活动都幂等，且放行/收回都生效 ✔</para>
    /// </summary>
    private void ApplyHalloweenSummonPolicy()
    {
        try
        {
            if (seasonalEventConfig?.EventBossSpawns == null) return;
            if (!seasonalEventConfig.EventBossSpawns.TryGetValue("halloweensummon", out var cur))
            {
                logger?.Warning("[EventRotator][万圣节] ⚠官方表里没有 `eventBossSpawns.halloweensummon` ⇒ 无需处理");
                return;
            }

            // ① 首次：快照原始表（只拷 map→list 引用，列表本身我们不改）
            if (_summonOriginal == null)
            {
                _summonOriginal = new Dictionary<string, List<SPTarkov.Server.Core.Models.Eft.Common.BossLocationSpawn>>(StringComparer.OrdinalIgnoreCase);
                if (cur != null)
                    foreach (var kv in cur) _summonOriginal[kv.Key] = kv.Value;
                logger?.Info($"[EventRotator][万圣节] `halloweensummon` 原始表已快照：{_summonOriginal.Count} 张图"
                           + $"[{string.Join(",", _summonOriginal.Keys)}]（供按年份门控放行/收回）");
            }

            // ② 先还原成原样（可逆：上一轮摘掉的这里会回来）
            cur ??= new Dictionary<string, List<SPTarkov.Server.Core.Models.Eft.Common.BossLocationSpawn>>(StringComparer.OrdinalIgnoreCase);
            cur.Clear();
            foreach (var kv in _summonOriginal) cur[kv.Key] = kv.Value;
            seasonalEventConfig.EventBossSpawns["halloweensummon"] = cur;

            // ③ ★ laboratory **永远**摘掉（官方：仪式不含 The Lab）
            bool hadLab = cur.Remove("laboratory");

            // ④ B：只有 `halloween_2023` 在跑才放行
            if (HalloweenSummonAllowed())
            {
                logger?.Info("[EventRotator][万圣节] ★`halloween_2023` 在跑 ⇒ **放行 `halloweensummon`（Zryachiy 召唤仪式）**"
                           + $"：{cur.Count} 张图 [{string.Join(",", cur.Keys)}]"
                           + (hadLab ? "；⚠ laboratory 仍按官方说明摘掉" : ""));
            }
            else
            {
                string names = string.Join(",", cur.Keys);
                cur.Clear();     // ★ A：摘掉整场
                if (SigChanged("summon-off", names))
                    logger?.Warning("[EventRotator][万圣节] ★已摘掉**整场** `halloweensummon`（Zryachiy 召唤仪式）"
                              + " —— 它是 `Halloween 2023 Part 2` 的内容（events_wiki.txt:1026-1031），"
                              + "当前生效活动里没有 `halloween_2023` ⇒ 不放行"
                              + (names.Length > 0 ? $"（摘掉 {names}；" : "（")
                              + (hadLab ? "laboratory 无论放行与否都摘" : "laboratory 原表里就没有")
                              + "）。⚠ 官方 Halloween 与 AprilFools 两个分支都会调 AddEventBossesToMaps ⇒ 愚人节同样不再注入。");
            }
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][万圣节] halloweensummon 年份门控失败（已忽略）: {ex.Message}"); }
    }

    /// <summary>原始 `halloweensummon` 表快照（首次调用时捕获）⇒ 支持按年份门控放行/收回。</summary>
    private Dictionary<string, List<SPTarkov.Server.Core.Models.Eft.Common.BossLocationSpawn>>? _summonOriginal;

    /// <summary>
    /// ★ B 的判据：**当前生效活动里是否有 `halloween_2023`**（**活动 id 精确匹配**）。
    /// ⚠ 为什么不用"family=halloween"这种宽判据：`halloween` / `halloween_2022` / `halloween_2023`
    /// **family 全等**（都是 `halloween`），只有 `familyYear` 不同 ⇒ 宽判据会把 2022 也放行 ✗。
    /// ⚠ **fail-safe**：拿不到列表 / 抛异常 ⇒ **false（= 摘掉）** + warning —— 宁可少放不可乱放 ✔
    /// </summary>
    private bool HalloweenSummonAllowed()
    {
        try
        {
            var actives = EventEngine.Actives;
            if (actives == null)
            {
                logger?.Warning("[EventRotator][万圣节] ⚠拿不到 EventEngine.Actives ⇒ 按 A 处理（**摘掉整场**）");
                return false;
            }
            return actives.Any(e => string.Equals(e?.Id, "halloween_2023", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger?.Warning("[EventRotator][万圣节] ⚠判定 `halloween_2023` 是否在跑时出错 ⇒ 按 A 处理（**摘掉整场**）: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// ★ v0.1.5 用户拍板：这 5 条 `eventQuests` 条目**永远保持隐藏**（不挂给任何活动）。
    ///
    /// 机制：它们在 `SPT_Data/configs/quest.json → eventQuests` 里且 `season=None`，
    /// 而 `showNonSeasonalEventQuests=false` ⇒ `QuestHelper.ShowEventQuestToPlayer` 第三个 if 会拦下它们；
    /// **只要没有活动把它们从表里移除**，玩家就永远看不到（除非存档里已经接过 —— 那是本体行为，改不了）。
    ///
    /// 本数组有两个用途：
    ///   ① ★ v2.6.13 起：作为【任务门控】的**硬排除名单**（`ApplyQuestGate` 的 `neverOpen`）——
    ///      即使某个活动的 `enableQuests` 引用了它们，门控也**不会**把它们从 `eventQuests` 移出
    ///      ⇒ 它们永远藏得住（这是 v2.6.13 新增的保证）；
    ///   ② `AssertNeverOpenQuests()` 的**启动断言 + 告警**：万一将来有人给它们挂了活动，
    ///      日志里能看到"数据与意图不一致"（行为已由①兜住，但数据该改）。
    /// </summary>
    // ★★★ v2.6.51【🚨 修「活动任务接不到」2026-10-16 · 用户实机发现】
    //   用户原话：「**我在Peacekeeper那里没接到惩罚者大丰收**」
    //   【根因】本条清单是 v0.1.5 定的 —— 当时这 4 个任务**所属活动还没实现**，为避免玩家看到
    //     "永远无法完成的任务"而**硬隐藏**。但**现在活动全都实现了**、`enableQuests` 也引用了它们
    //     ⇒ `ApplyQuestGate` 的 `neverOpen` **硬排除**把它们挡在门外 ⇒ **活动开着也永远接不到** ✗✗
    //   【证据】服务端日志逐字：
    //     `永远隐藏『惩罚者-大丰收 The Punisher Harvest』(655e…)：在表内=True、season=None ⇒ 已隐藏 ✔；
    //      ⚠ 但它被活动 ammo_shortage 的 enableQuests 引用（已被门控硬排除 ⇒ 不会再冒出来）` ✗
    //   【修正】**只保留"本体任务库里已不存在"的那一条**（它确实无解）；其余 4 条**放行** ✔
    //     `641dbfd7…`=重要伤员/`bandages_event` · `64957049…`=吹哨人/`tourists_2023` ·
    //     `64916da7…`=东窗事发/`cultist_hunting` · `655e427b…`=惩罚者-大丰收/`ammo_shortage`
    private static readonly (string Id, string Name)[] NeverOpenQuests =
    {
        ("674647f38466ebb03408b291", "That's a Great Plan, Walter（本体任务库里已不存在）"),
    };

    /// <summary>
    /// 断言"永远隐藏清单"没有被任何活动引用（被引用就要告警）。
    /// ★ v2.6.13：告警文案已随【任务门控】更新 —— 这 5 条现在被门控**硬排除**
    ///   （`ApplyQuestGate` 的 `neverOpen`），所以即使活动声明了它们，它们也**不会再被移出 eventQuests**
    ///   ⇒ 仍然藏得住。这里的告警保留，是为了让"events.json 里挂了这些 id"这件事**继续可见**
    ///   （属于数据与意图不一致，值得记录，但不影响行为）。
    /// </summary>
    private void AssertNeverOpenQuests()
    {
        try
        {
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in EventEngine.All)
                if (e.EnableQuests != null)
                    foreach (var q in e.EnableQuests)
                        if (!string.IsNullOrEmpty(q)) declared.Add(q);

            var leaked = NeverOpenQuests.Where(x => declared.Contains(x.Id)).Select(x => x.Name).ToList();
            if (leaked.Count > 0)
                logger.Warning("[EventRotator] ⚠『永远隐藏』清单里的任务被活动 enableQuests 引用了："
                             + string.Join("、", leaked)
                             + " —— 已由【任务门控】**硬排除**（不会跟着活动冒出来），"
                             + "但建议把这几条从 events.json 的 enableQuests 里去掉（数据与意图不一致）");
            else
                logger.Info($"[EventRotator] 『永远隐藏』清单已确认无活动引用（{NeverOpenQuests.Length} 条，按你的要求保持隐藏）："
                          + string.Join("、", NeverOpenQuests.Select(x => x.Name)));
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] 隐藏清单断言失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★ v0.1.5 修正 SPT 的一处不自洽（实测发现）：
    /// `ConfigureZombies` 遍历 `mapInfectionAmount` 的**所有**键（含值为 0 的），
    /// 对非 100 的值一律 `randomUtil.GetInt((int)v, 100)` —— 配置 0 ⇒ `GetInt(0,100)` ⇒ **随机 0~99**，
    /// 于是「海关 bigmap / 立交桥 Interchange」也被写进了 `LocationInfection` 和
    /// `Base.Events.Halloween2024.InfectionPercentage`（实测 bigmap=37、Interchange=14）。
    /// 但同一个方法后面用 `GetLocationsWithZombies()`（按 `> 0` 过滤）去决定僵尸刷新点与敌对性白名单
    /// ⇒ 这两张图**没有** halloweenzombies 条目 ⇒ 客户端地图界面显示"海关 37% 感染"，
    /// 进图却一条僵尸刷新点都没有。官方数据把 0 写成 0 显然是"这里不感染"的意思，这里按本意归零。
    /// </summary>
    /// <summary>官方 `mapInfectionAmount` 的**原始值**快照（感染扩散每次按它重算，避免反复相乘）。</summary>
    private static readonly Dictionary<string, double> _origInfection = new(StringComparer.OrdinalIgnoreCase);

    private static void SnapshotInfectionOriginals(IDictionary<string, double> inf)
    {
        if (_origInfection.Count > 0 || inf == null) return;
        foreach (var kv in inf) _origInfection[kv.Key] = kv.Value;
    }

    /// <summary>
    /// ★★ v0.1.5 动态感染扩散 —— 还原官方 2024 感染者事件"随时间慢慢感染"的行为。
    ///
    /// 依据：① 官方配置 `mapInfectionAmount` 给的是**下限**（`ConfigureZombies` 里
    /// `value = (num == 100) ? 100 : rand(num, 100)`，每次开服重摇）；② 社区实测是逐图/随时间推进的：
    /// 典型证词「killa 因为被玩家击杀过多导致立交桥的**零感染**连 12 小时都没撑到」—— 连"0 感染的图"
    /// 也会在事件进行中开始感染。SPT 只有静态快照，这里用"活动第 N 天"折算来近似。
    ///
    /// 规则（可由 `infectionRamp` 覆盖）：
    ///   · 有感染配置的图：官方值 × (min(天数, rampDays) + 1) / rampDays，第 rampDays 天起等于官方值；
    ///   · 官方配置 0 的图（海关/立交桥）：前 zeroMapsJoinAtDay 天保持 0，之后线性爬到 zeroMapPeakPercent；
    ///   · 官方 100 的图（实验室）永不折算 —— 官方公告就是 100%。
    /// 同时写回 `zs.MapInfectionAmount`（供 SPT ConfigureZombies 用）与客户端显示字段。
    /// </summary>
    private void ApplyInfectionRamp(EventDef ev)
    {
        try
        {
            if (ev == null) return;
            var ramp = ev.InfectionRamp;
            if (ramp != null && ramp.Enabled == false) return;

            var zs = seasonalEventConfig?.Events?.FirstOrDefault(
                e => string.Equals(e?.Type.ToString(), "Halloween", StringComparison.OrdinalIgnoreCase))
                ?.Settings?.ZombieSettings;
            var inf = zs?.MapInfectionAmount;
            if (inf == null) return;
            SnapshotInfectionOriginals(inf);

            int rampDays = Math.Max(1, ramp?.RampDays ?? 6);
            int zeroJoin = Math.Max(0, ramp?.ZeroMapsJoinAtDay ?? 3);
            double zeroPeak = ramp?.ZeroMapPeakPercent ?? 15.0;

            int rawDay = DaysIntoEvent(ev);                    // 0-based：活动第 1 天 = 0；<0 = 当前不在活动窗内
            bool manual = rawDay < 0;                          // ★ 窗外被 F12/force.txt 手动开启
            // ★★ v0.1.5 修正：手动开启**默认按活动第 1 天**（零感染图保持 0），不再一律按末期满额。
            //   旧行为会把海关/立交桥爬到 15%，与 desc 和官方基础值都不符（用户实测）。
            var manualDays = EventEngine.InfectionManualDays;
            //   手动开启三档（config.json → infectionManualDays）：
            //     0（默认）= **原样使用官方各图数值**（实验室 100 / 森林 60 / 其余 50 / 沙盒 25；零感染图保持 0）
            //                 —— 这样面板 desc 写的比例与游戏内"地点感染等级"完全一致（用户实测诉求）
            //     N>0 = 按活动第 N+1 天（带爬升）    -1 = 按末期满额（零感染图会被爬到峰值 15%）
            int day = manual ? (manualDays < 0 ? rampDays : (manualDays == 0 ? 0 : Math.Max(0, manualDays))) : rawDay;
            double f = (manual && manualDays == 0) ? 1.0
                       : Math.Min(1.0, (double)(Math.Min(day, rampDays) + 1) / rampDays);

            var parts = new List<string>();
            int lateMaps = 0;
            // ★ v0.1.5 验证计数器：确认"感染值"真的写进了 globals.LocationInfection 与 location.Base.Events.Halloween2024.InfectionPercentage
            int liOk = 0, locOk = 0; var missList = new List<string>();
            foreach (var key in _origInfection.Keys.ToList())
            {
                double official = _origInfection[key];
                int val;
                if (official >= 100) val = 100;                                 // 实验室锁定 100
                else if (official > 0) val = (int)Math.Round(official * f);     // 按天数爬升（官方值是"下限"）
                else
                {
                    if (day < zeroJoin) val = 0;
                    else
                    {
                        int span = Math.Max(1, rampDays - zeroJoin);
                        double g = Math.Min(1.0, (double)(day - zeroJoin + 1) / span);
                        val = (int)Math.Round(zeroPeak * g);
                        if (val > 0) lateMaps++;
                    }
                }
                inf[key] = val;

                try
                {
                    var li = globalTable?.GetType().GetProperty("LocationInfection")?.GetValue(globalTable) as IDictionary;
                    if (li != null && li.Contains(key)) { li[key] = val; liOk++; }
                    foreach (var locId in ExpandInfectedKey(key))
                    {
                        var loc = locationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })?.Invoke(locationTable, new object[] { locId });
                        var hwRoot = PropOf(PropOf(PropOf(loc, "Base"), "Events"), "Halloween2024");
                        if (PropOf(hwRoot, "InfectionPercentage") != null) { SetPropOf(hwRoot, "InfectionPercentage", (double)val); locOk++; }
                        else missList.Add(key + "→" + locId);
                    }
                }
                catch (Exception exw) { missList.Add(key + "!" + exw.GetType().Name); }
                if (val > 0) parts.Add($"{key}={val}");
            }

            logger.Warning($"[EventRotator][感染扩散] {(manual ? (manualDays < 0 ? $"当前不在活动窗内（手动开启）⇒ 按末期满额（{rampDays} 天）" : (manualDays == 0 ? "当前不在活动窗内（手动开启）⇒ 按官方各图原值（零感染图保持 0）" : $"当前不在活动窗内（手动开启）⇒ 按活动第 {day + 1} 天")) : $"活动第 {day + 1} 天 / 折算 {rampDays} 天")} ⇒ 系数 {f:0.00}"
                         + $"　有感染图：{string.Join(" ", parts)}"
                         + (lateMaps > 0
                             ? $"　｜ 官方配置 0 的图已在第 {zeroJoin} 天起开始感染（{lateMaps} 张，峰值 {zeroPeak:0}%）"
                             : $"　｜ 官方配置 0 的图（海关/立交桥）尚未开始感染（第 {zeroJoin} 天起）")
                         + $"　｜ **写入校验**：globals.LocationInfection 成功 {liOk} 项、图上 InfectionPercentage 成功 {locOk} 处"
                         + (missList.Count > 0 ? $"　⚠ 未命中 {missList.Count}：{string.Join(" ", missList.Take(6))}" : "　（无未命中）"));
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][感染扩散] 失败: {ex.Message}"); }
    }

    /// <summary>活动开始到今天的天数（0-based）。活动窗前 → 0；窗后 → 窗口长度（= 全额）。</summary>
    private static int DaysIntoEvent(EventDef ev)
    {
        try
        {
            var now = DateTime.Now;
            DateTime start, end;
            try { start = new DateTime(now.Year, Math.Max(1, ev.StartMonth), Math.Max(1, ev.StartDay)); }
            catch { return 0; }
            try { end = new DateTime(now.Year, Math.Max(1, ev.EndMonth), Math.Max(1, ev.EndDay)); }
            catch { end = start; }
            if (end < start) end = end.AddYears(1);
            // ★ 窗外返回 -1：调用方据此判定"手动开启 ⇒ 按全额"，避免在非活动期测试时只看到 1/6 感染率
            if (now < start) return -1;
            if (now > end) return -1;
            return (int)(now.Date - start.Date).TotalDays;
        }
        catch { return 0; }
    }

    private void FixZeroInfectionMaps()
    {
        try
        {
            var zs = seasonalEventConfig?.Events?.FirstOrDefault(
                e => string.Equals(e?.Type.ToString(), "Halloween", StringComparison.OrdinalIgnoreCase))
                ?.Settings?.ZombieSettings;
            var inf = zs?.MapInfectionAmount;
            if (inf == null) return;

            SnapshotInfectionOriginals(inf);                      // ★ 先存官方原值（感染扩散按它折算）
            var li = globalTable?.GetType().GetProperty("LocationInfection")?.GetValue(globalTable) as IDictionary;
            int fixedMaps = 0;
            foreach (var kv in inf)
            {
                var official = _origInfection.TryGetValue(kv.Key, out var ov) ? ov : kv.Value;
                if (official > 0) continue;                       // 只处理"官方配置为 0 = 不感染"的键
                var key = kv.Key;
                foreach (var locId in ExpandInfectedKey(key))
                {
                    if (li != null && li.Contains(key)) li[key] = 0;
                    try
                    {
                        var loc = locationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })?.Invoke(locationTable, new object[] { locId });
                        var ev = PropOf(PropOf(PropOf(PropOf(loc, "Base"), "Events"), "Halloween2024"), "InfectionPercentage");
                        if (ev != null) SetPropOf(PropOf(PropOf(PropOf(loc, "Base"), "Events"), "Halloween2024"), "InfectionPercentage", 0d);
                    }
                    catch { }
                }
                fixedMaps++;
            }
            if (fixedMaps > 0)
                logger.Info($"[EventRotator][感染] 已按官方数据本意把 {fixedMaps} 个『配置 0 = 不感染』的图归零"
                          + "（SPT 会给它们随机一个感染率，导致地图界面显示有感染、进图却没有僵尸刷新点）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][感染] 归零 0 感染图失败: {ex.Message}"); }
    }

    /// <summary>官方 key → 实际地图 id（与 SPT `GetLocationFromInfectedLocation` 一致）。</summary>
    private static IEnumerable<string> ExpandInfectedKey(string key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (k == "factory4") return new[] { "factory4_day", "factory4_night" };
        if (k == "sandbox") return new[] { "sandbox", "sandbox_high" };
        return new[] { k };
    }

    private static object PropOf(object o, string name) =>
        o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);

    /// <summary>
    /// ★★★ v2.6.12【P0 落点修正】取出某张图**已加载的** `LocationBase.MinMaxBots`
    /// （`List&lt;MinMaxBot&gt;`，LocationBase.cs:890-891）—— 这是 `PostDbLoadService.AdjustMapBotLimits()`
    /// 唯一真正改写的那张表（PostDbLoadService.cs:441-456），也是客户端每局拉 `/client/locations` 时读的表。
    ///
    /// 键名兼容由 `LocationTable.GetLocation(string)` 负责（LocationTable.cs:385-392：先 `GetMappedKey`
    /// 把 "bigmap"/"Bigmap" 归一成 "Bigmap"，再在大小写不敏感的 `GetDictionary()` 里查，:398-427）。
    /// 取不到（地图不存在 / 属性类型变了）返回 null，由调用方打日志 —— **绝不静默失败**。
    /// </summary>
    private List<SPTarkov.Server.Core.Models.Eft.Common.MinMaxBot> MinMaxBotsOf(string mapKey)
    {
        try
        {
            var loc = locationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) })
                                          ?.Invoke(locationTable, new object[] { mapKey });
            return PropOf(PropOf(loc, "Base"), "MinMaxBots")
                   as List<SPTarkov.Server.Core.Models.Eft.Common.MinMaxBot>;
        }
        catch { return null; }
    }

    private static void SetPropOf(object o, string name, object val)
    {
        var p = o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p == null || !p.CanWrite) return;
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        p.SetValue(o, t == typeof(double) ? Convert.ToDouble(val) : Convert.ChangeType(val, t));
    }

    /// <summary>
    /// ★ v0.1.5 感染率自证日志：把**客户端显示所需的服务端字段**原样打出来，便于和游戏里对照。
    ///
    /// 客户端实现（反编译 `EFT.UI.ZombieEventLocationInfectionController.ProcessLocation`）：
    /// <code>
    /// var ih = Singleton&lt;GlobalConfiguration&gt;.Instance.SeasonActivityConfig?.InfectionHalloweenConfig;
    /// if (location.Enabled &amp;&amp; !location.Locked &amp;&amp; ih != null &amp;&amp; ih.DisplayUIEnabled
    ///     &amp;&amp; ih.LocationInfection.TryGetValue(地图Key, out var v) &amp;&amp; v &gt;= 1)
    ///         view.SetInfection(v, ih.RedThreatValue);   // RedThreatValue 是客户端常量 50 ⇒ ≥50 显示为红
    /// </code>
    /// 地图 Key 映射：Sandbox/Sandbox_high→"Sandbox"、factory4_day/factory4_night→"factory4"，其余用 location.Id。
    /// 数据来源全部是 `/client/globals`（`LocationInfection` + `SeasonActivity.InfectionHalloween`）
    /// ⇒ **改完必须重启游戏客户端**才会重新拉取。
    /// </summary>
    private void LogInfectionState()
    {
        try
        {
            var sa = globalTable?.Configuration?.SeasonActivity;
            var ih = sa?.GetType().GetProperty("InfectionHalloween")?.GetValue(sa);
            if (ih == null) { logger.Warning("[EventRotator][感染] globals 里没有 SeasonActivity.InfectionHalloween，客户端不会显示感染率"); return; }

            bool ui = Convert.ToBoolean(ih.GetType().GetProperty("DisplayUIEnabled")?.GetValue(ih) ?? false);
            bool en = Convert.ToBoolean(ih.GetType().GetProperty("Enabled")?.GetValue(ih) ?? false);

            var li = globalTable?.GetType().GetProperty("LocationInfection")?.GetValue(globalTable) as IDictionary;
            var pairs = new List<string>();
            if (li != null)
                foreach (DictionaryEntry kv in li)
                {
                    int v = Convert.ToInt32(kv.Value ?? 0);
                    pairs.Add($"{kv.Key}={v}{(v >= 50 ? "(红)" : v >= 1 ? "(显示)" : "(不显示)")}");
                }

            logger.Warning($"[EventRotator][感染] 客户端显示开关 DisplayUIEnabled={ui} Enabled={en}；"
                         + $"LocationInfection {li?.Count ?? 0} 项：{string.Join("  ", pairs)}");
            logger.Warning("[EventRotator][感染] ↑ 与游戏「地图选择界面」右上角感染标记对照（≥1 显示、≥50 变红）；"
                         + "这些值来自 /client/globals，**必须重启游戏客户端**才会重新拉取");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][感染] 状态dump失败: {ex.Message}"); }
    }

    // ---------- 季节活动（ev==null 时回到自动判定）----------

    /// <summary>
    /// ★ v0.1.5 把官方僵尸活动的**敌对性表**与**有感染地图**交给 BossWavePatch（只读复制，不改官方文件）。
    ///
    /// 背景：SPT 官方在 `ApplyHalloweenEvent` 里会调
    /// `ReplaceBotHostility(hostilitySettingsForEvent["zombies"], GetLocationsWithZombies(mapInfectionAmount))`，
    /// 但用户的 **BPS** 对该方法打了 Prefix `return false`（反编译 `BotPlacementSystemServer.Patches.ReplaceBotHostilityPatch` 实证）
    /// ⇒ 官方那段整段不执行。所以由 mod 在进图时自己复刻（含完整快照/还原）。
    /// </summary>
    private void PrepareZombieHostility()
    {
        try
        {
            if (seasonalEventConfig?.HostilitySettingsForEvent == null) return;

            if (!seasonalEventConfig.HostilitySettingsForEvent.TryGetValue("zombies", out var byMap) || byMap == null)
            { logger.Warning("[EventRotator][万圣节] 官方 hostilitySettingsForEvent.zombies 不存在，僵尸敌对性无法复刻"); return; }

            var dict = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byMap)
                dict[kv.Key] = (kv.Value ?? new()).Cast<object>().Where(x => x != null).ToList();
            Patches.BossWavePatch.ZombieHostilityByMap = dict;

            var maps = RefreshInfectedMaps();

            logger.Info($"[EventRotator][万圣节] 官方僵尸敌对表已接入：{dict.Count} 个键；有感染地图 {maps.Count} 张 [{string.Join(",", maps)}]"
                      + "（BPS 把 SPT 的 ReplaceBotHostility Prefix 掉了 → 进图时由 mod 自己复刻）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][万圣节] 接入僵尸敌对表失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★ v0.1.5：按**当前**的 `mapInfectionAmount` 重算"有感染地图"白名单。
    ///
    /// 为什么必须能重算：动态感染扩散（ApplyInfectionRamp）会在运行中把感染率往上推，
    /// 而这张白名单同时驱动两件事 —— ① 僵尸敌对性注入（`ZombieHostilityByMap` 只对有感染的图生效）；
    /// ② 官方 infected 波次表的追加过滤（感染率 0 的图不追加）。
    /// 若白名单停留在扩散前的旧值，就会出现"**地图界面显示有感染、进图却没有僵尸**"（用户实测踩过）。
    /// 映射与官方 `GetLocationFromInfectedLocation` 一致：factory4 → day+night；sandbox → +sandbox_high。
    /// </summary>
    private HashSet<string> RefreshInfectedMaps()
    {
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var halloween = seasonalEventConfig?.Events?.FirstOrDefault(
                e => string.Equals(e?.Type.ToString(), "Halloween", StringComparison.OrdinalIgnoreCase));

            // ★★★ v2.6.45【准确性修复 · Lead 指出】**必须先看僵尸模式是否启用**
            //
            //   <para><b>⚠ 关键：「万圣节僵尸模式」的开关**不是**事件级 <c>enabled</c>，而是
            //     <c>settings.zombieSettings.enabled</c>。</b> 本机 <c>SPT_Data/configs/seasonalevents.json</c> 实测：</para>
            //   <code>
            //     type         event.enabled   zombieSettings.enabled   >0 的感染图
            //     HALLOWEEN    True            **False** ← ★            8（Sandbox/factory4/laboratory/Woods/
            //                                                              Shoreline/RezervBase/Lighthouse/TarkovStreets）
            //     APRILFOOLS   True            **True**                 1（laboratory）
            //     CHRISTMAS    True            None                     0
            //   </code>
            //   <para>⇒ **事件级 <c>enabled</c> 全是 True**；真正"禁用"的是 <c>zombieSettings.enabled</c>。
            //     旧实现**两个都不看** ⇒ 会把 HALLOWEEN 那 8 张图算进"有感染地图"白名单 ✗</para>
            //
            //   <para>⚠ 影响范围**仅**"僵尸敌对性数据的适用白名单"（<c>BossWavePatch.InfectedMaps</c>）；
            //     **不影响 SPT 刷不刷波次**（`zombieSettings.enabled=false` ⇒ SPT 不生成感染波次）
            //     ⇒ 玩家不会真看到僵尸，但白名单算错 = **准确性瑕疵**。</para>
            //
            //   <para><b>CLR 名 = `Enabled`</b>（按 `[JsonPropertyName]` 反查：
            //     <c>ZombieSettings.cs:12-13</c> <c>[JsonPropertyName("enabled")] public bool? Enabled</c>
            //     —— <b>可空 <c>bool?</c></b>，所以"拿不到"是**真实可能**的；
            //     嵌套路径 <c>SeasonalEvent.Settings.ZombieSettings</c> 由既有代码
            //     <c>halloween?.Settings?.ZombieSettings?.MapInfectionAmount</c> 证实）。</para>
            //
            //   <para>⚠ <b>可降级</b>：事件条目找不到 / <c>ZombieSettings</c> 为 null /
            //     <c>Enabled</c> 为 <c>null</c>（未声明）/ 读取抛异常 ⇒ **保持现状**（继续按
            //     <c>mapInfectionAmount</c> 算）+ warning —— **绝不因此静默变成"全部无感染"** ✗</para>
            if (halloween == null)
            {
                logger?.Warning("[EventRotator][感染图] ⚠找不到 HALLOWEEN 事件条目 ⇒ **保持现状**"
                              + "（仍按 mapInfectionAmount 算白名单，不做 zombieSettings.enabled 过滤）");
            }
            else
            {
                bool? zombieOn = null;
                try { zombieOn = halloween.Settings?.ZombieSettings?.Enabled; }
                catch (Exception ex)
                {
                    logger?.Warning("[EventRotator][感染图] ⚠读 HALLOWEEN 的 `ZombieSettings.Enabled` 失败 ⇒ **保持现状**"
                                  + "（仍按 mapInfectionAmount 算）: " + ex.Message);
                }
                if (zombieOn == null)
                {
                    logger?.Warning("[EventRotator][感染图] ⚠HALLOWEEN 的 `settings.zombieSettings.enabled` **未声明**"
                                  + " ⇒ **保持现状**（仍按 mapInfectionAmount 算白名单）");
                }
                else if (zombieOn == false)
                {
                    // ★ 空集：语义 = "僵尸模式没启用 ⇒ 没有任何图处于感染状态"（不是"没有数据"）
                    Patches.BossWavePatch.InfectedMaps = maps;
                    logger?.Info("[EventRotator][感染图] HALLOWEEN 的 `settings.zombieSettings.enabled=false` "
                               + "⇒ **感染图白名单 = 空**（否则会把 8 张 mapInfectionAmount>0 的图误当成感染图；"
                               + "白名单为空 ⇒ 僵尸敌对性数据在任何图上都不生效）");
                    return maps;
                }
            }

            var inf = halloween?.Settings?.ZombieSettings?.MapInfectionAmount;
            if (inf != null)
                foreach (var kv in inf)
                {
                    if (kv.Value <= 0) continue;
                    var k = (kv.Key ?? "").Trim().ToLowerInvariant();
                    if (k == "factory4") { maps.Add("factory4_day"); maps.Add("factory4_night"); }
                    else if (k == "sandbox") { maps.Add("sandbox"); maps.Add("sandbox_high"); }
                    else if (k.Length > 0) maps.Add(k);
                }
        }
        catch { }
        Patches.BossWavePatch.InfectedMaps = maps;
        return maps;
    }

    private void TryForceSeasonal(EventDef ev)
    {
        try
        {
            var mForce = seasonalEventService.GetType().GetMethod("ForceSeasonalEvent", BindingFlags.Instance | BindingFlags.Public);
            var mEnable = seasonalEventService.GetType().GetMethod("EnableSeasonalEvents", BindingFlags.Instance | BindingFlags.Public);
            var et = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Enums.SeasonalEventType");

            // ★ v2.1.0 季节轮换（合并 spt_season_rotator 的能力）：
            //   rotate 模式下季节**完全由天数决定** —— 不跟现实日期、也不跟活动。
            //   这样"一个月一季"这种压缩轮换就能实现（SPT 原生是几个月才轮到一次）。
            if (SeasonRotation.IsRotating)
            {
                var want = SeasonRotation.CurrentSeason();     // 天气季节：SPRING/SUMMER/AUTUMN/WINTER

                // ★★ v0.1.4 关键修正（实测发现的真 bug）：
                //   这里原来写的是 `Enum.Parse(et, want, true)` —— 把**天气季节名**（AUTUMN）
                //   当成**活动季节枚举**（SeasonalEventType）解析。两个枚举根本不是一套：
                //     · Season              : SUMMER / AUTUMN / WINTER / SPRING / AUTUMN_LATE / SPRING_EARLY / STORM
                //     · SeasonalEventType   : None / Christmas / Halloween / NewYears / Promo / AprilFools
                //   只有 "Christmas" 恰好两边同名，所以以前"冬天能开、秋天报错"，
                //   日志里那句 `季节轮换强推 AUTUMN 失败: Requested value 'AUTUMN' was not found.` 就是它。
                //   现在明确分两步：先算**天气季节**，再映射到**活动季节**（用 SeasonToEventMap）。
                var wApply = WeatherSeasonDimension.ParseSeason(want);
                if (wApply != null)
                {
                    WeatherSeasonDimension.WeatherConfig ??= weatherConfig;
                    WeatherSeasonDimension.Apply(want, "季节轮换", logger);
                }

                var evSeason = SeasonRotation.CurrentEventSeasonForWeather(want);   // AUTUMN → HALLOWEEN

                // ★★★ v0.1.5【用户选项 C】手动指定活动时 **暂停季节活动强推**：
                //   force.txt 有活动（且不是 none）⇒ 本轮**不注入活动季节** ——
                //   召唤首领（和平/复仇扎里亚奇 + 邪教祭司）、僵尸设置、EventType 一律不动；
                //   **天气季节照常**（上面已经套过了，那是外观）。
                //   起因（实测）：用户测 `sanitar_event` 时，季节轮换把关海关在内的 8 张图塞进了
                //   万圣节召唤首领，用户误以为是我们活动刷的 ⇒ 测试场景需要"活动独占"。
                if (!string.IsNullOrEmpty(EventEngine.ForcedId)
                    && !string.Equals(EventEngine.ForcedId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    logger.Info($"[EventRotator][季节轮换] 手动指定了活动（force.txt = {EventEngine.ForcedId}）→ **暂停**季节活动强推"
                                + (string.IsNullOrEmpty(evSeason) ? "" : $"（本应强推 {evSeason}）")
                                + "；不按季节强推原生开关。天气季节 " + want + " 照常。"
                                + "（★ 若强制的活动自身是万圣节/圣诞族，其必需的原生开关仍会按活动注入 —— 见下方 [万圣节]/[圣诞] 行）");
                }
                else if (!string.IsNullOrEmpty(evSeason) && mForce != null && et != null)
                {
                    try
                    {
                        var evSeasonEnum = Enum.Parse(et, evSeason, true);
                        // ★ v0.1.5：同进程同季节只强推一次（SPT 无回滚，重复强推会累积圣诞物资概率等）
                        ForceSeasonalOnce(evSeason, () =>
                        {
                            MaybePrepareHalloween(evSeason);   // 万圣节原生开关（僵尸/锤哥）
                            // ★★★ v2.6.46【A+B】**必须在官方强推之前**应用年份门控 ——
                            //   官方 `AddEventBossesToMaps("halloweensummon")` 读的就是这张表；
                            //   这里放行/摘掉之后紧接着强推，才能做到"开 halloween_2023 ⇒ 有仪式、
                            //   开 2022/别的 ⇒ 没有" ✔
                            ApplyHalloweenSummonPolicy();
                            mForce.Invoke(seasonalEventService, new[] { evSeasonEnum });
                        }, $"季节轮换 {want}");
                        // ★ v1.5.1【P2-7 修】rotate 分支强推了活动季节，但以前不记录 ⇒
                        //   面板「活动季节(已开启)」在 rotate 模式下永远是空的。
                        EventRotatorStatusRouter.AppliedEventSeason = evSeason;
                        logger.Warning($"[EventRotator] ★季节轮换 → 天气 {want}" + (wApply == null ? "（天气套用失败）" : " ✔")
                                       + $" / 活动季节 {evSeason}"
                                       + $"（每 {SeasonRotation.IntervalDays} 天换一季，档位#{SeasonRotation.CurrentSlot()}，"
                                       + $"距下次换季 {SeasonRotation.DaysUntilNext():0.#} 天）");
                    }
                    catch (Exception ex) { logger.Warning($"[EventRotator] 季节轮换强推活动季节 {evSeason} 失败: {ex.Message}"); }
                }
                else if (string.IsNullOrEmpty(evSeason))
                {
                    // ★ 2026-10-31【面板陈旧值】rotate 模式下 ④ 分支不清空本值 ⇒ 从"有映射的季节"切到
                    //   夏天/暴风雨后，面板会一直写着上一季的"活动季节：圣诞节（本季已开启）"（用户实测撞到）。
                    //   这里在本季**没有对应活动季节**时明确清空，并说明原生节日残留要靠重启清。
                    var stale = EventRotatorStatusRouter.AppliedEventSeason;
                    EventRotatorStatusRouter.AppliedEventSeason = "";
                    logger.Info($"[EventRotator] ★季节轮换 → 天气 {want}（该季节**没有**对应的活动季节，只换天气与物资）"
                                + (string.IsNullOrEmpty(stale)
                                    ? ""
                                    : $"；已清空面板上的旧值 '{stale}'（SPT 的原生节日效果无法回滚，若上一季开过节日，需**重启服务端**才会彻底消失）"));
                }
                else
                    logger.Warning($"[EventRotator] 季节轮换：算不出季节或 API 不可用（want={want} force={mForce != null} type={et != null}）");
                return;
            }

            // ★ v2.6.2 注意：这里的【指定季节】已移到 ApplyWeatherSeason + AutoOpenSeasonalFromWeather 处理。
            //   原因：SeasonRotation.FixedSeason 现在存的是**天气季节**（SPRING/SUMMER/AUTUMN/WINTER），
            //   而 TryForceSeasonal 需要的是**活动季节枚举**（SeasonalEventType：HALLOWEEN/CHRISTMAS/…），
            //   两者不是一回事 —— 拿 WINTER 去 Parse SeasonalEventType 会报
            //   "Requested value 'WINTER' was not found"。所以这里不再处理 fixed。

            if (ev == null || string.IsNullOrEmpty(ev.SeasonalEvent) || ev.SeasonalEvent == "None")
            {
                mEnable?.Invoke(seasonalEventService, null);   // 还原为"按日期自动判定"
                logger.Info("[EventRotator] seasonal event → auto (restored)");
                return;
            }
            if (mForce == null || et == null) { logger.Warning("[EventRotator] ForceSeasonalEvent unavailable"); return; }
            var evSzn = Enum.Parse(et, ev.SeasonalEvent, true);
            ForceSeasonalOnce(ev.SeasonalEvent, () =>
            {
                MaybePrepareHalloween(ev.SeasonalEvent);   // 万圣节原生开关（僵尸/锤哥）
                mForce.Invoke(seasonalEventService, new[] { evSzn });
            }, $"活动 {ev.Id}");
            logger.Info($"[EventRotator] forced seasonal = {ev.SeasonalEvent}");
        }
        catch (Exception ex) { logger.Error($"[EventRotator] seasonal failed: {ex.Message}"); }
    }


    /// <summary>
    /// ★ v2.1.6 空投维度：打开某类空投权重 + 往空投里塞物品（解决"活动物品刷不出来"）。
    /// 实证案例：mortar_strike 的 radar 空投权重=0，且 GARY ZONT 电子战装置无任何获取途径。
    /// </summary>

    /// <summary>★ v2.2.0 globals 维度：双倍伤害 / 技能加速 / 经验加成 / 口渴加速。</summary>

    /// <summary>★ v2.3.0 藏身处秒建 / 商人折扣 / bot 预设。</summary>

    /// <summary>★ v2.4.0 bot 装备池：往角色槽位加物品（火箭筒 SCAV 等）。</summary>

    /// <summary>★ v2.5.0 实验室免票 + 跳蚤手续费（覆盖官方 #56/#65/#51/#23 与 #64/#91）。</summary>

    /// <summary>
    /// ★ v2.5.1 把"当前实际生效的活动"写到 mod 目录的 当前生效活动.txt，
    ///   供客户端 F12 面板底部显示（用户要求：想知道现在在享受哪些活动）。
    /// </summary>

    /// <summary>
    /// ★ v2.6.0 天气季节（春夏秋冬）。
    ///   优先级：① 活动自带 forceWeatherSeason ② seasonRotation.mode=rotate 按天数轮换
    ///           ③ mode=fixed 用 seasonRotation.season ④ 其它 → 不覆盖（还原默认）
    ///   落点：WeatherConfig.OverrideSeason : Nullable&lt;Season&gt;（configs/weather.json → overrideSeason）
    /// </summary>

    /// <summary>
    /// ★ v2.6.1 天气季节 → 自动开对应【活动季节】。
    ///   用户要求：进入相应季节就先开那个活动季节；**手动指定了活动（force.txt）就忽略**。
    ///   映射：春→愚人节 / 夏→无 / 秋→万圣节 / 冬→圣诞节
    /// </summary>

    /// <summary>
    /// ★ v0.1.2 活动季节（万圣节/圣诞/新年/愚人节）—— **独立控制，不由天气季节主导**。
    ///
    /// 优先级（从高到低）：
    ///   ① `eventSeason.mode = fixed` → 用 `eventSeason.season` ★ 用户直接指定（想开万圣节哪个版本都行）
    ///   ② force.txt 有值           → 什么都不动（跟着活动自己的 seasonalEvent 走）
    ///   ③ `seasonRotation.autoOpenSeasonalEvent = true` → 按天气季节映射（春→愚人节/秋→万圣/冬→圣诞）
    ///   ④ 否则                     → 什么都不动（保持原样）
    ///
    /// 这样"同一个天气季节开不同的活动季节"就能做到（万圣节有 2022/2023/2024/感染者/邪教之夜 多个版本）。
    /// </summary>
    private void ApplyEventSeason(string weatherSeason, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            var mForce = seasonalEventService.GetType().GetMethod("ForceSeasonalEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var et = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Enums.SeasonalEventType");
            if (mForce == null || et == null) { logger.Warning("[EventRotator][活动季节] API 不可用"); return; }

            // ① 直接指定活动季节（最高优先）
            if (SeasonRotation.IsEventSeasonFixed)
            {
                var fixedSzn = Enum.Parse(et, SeasonRotation.EventSeasonVal, true);
                ForceSeasonalOnce(SeasonRotation.EventSeasonVal, () =>
                {
                    MaybePrepareHalloween(SeasonRotation.EventSeasonVal);   // 万圣节原生开关
                    mForce.Invoke(seasonalEventService, new[] { fixedSzn });
                }, "eventSeason=fixed");
                EventRotatorStatusRouter.AppliedEventSeason = SeasonRotation.EventSeasonVal;
                logger.Warning($"[EventRotator][活动季节] ★已【直接指定】活动季节 → {SeasonRotation.EventSeasonVal}（独立于天气季节）");
                return;
            }

            // ② 手动指定了活动 → 不动
            if (!string.IsNullOrEmpty(EventEngine.ForcedId) && EventEngine.ForcedId != "none")
            {
                logger.Info("[EventRotator][活动季节] 手动指定了活动（force.txt = " + EventEngine.ForcedId + "）→ 活动季节跟活动走，不干预");
                return;
            }

            // ③ 天气季节联动（可选，默认关）
            if (SeasonRotation.AutoOpenSeasonalEvent && !string.IsNullOrWhiteSpace(weatherSeason)
                && SeasonRotation.SeasonToEventMap.TryGetValue(weatherSeason.Trim(), out var evType) && !string.IsNullOrEmpty(evType))
            {
                var wSzn = Enum.Parse(et, evType, true);
                ForceSeasonalOnce(evType, () =>
                {
                    MaybePrepareHalloween(evType);   // 万圣节原生开关
                    mForce.Invoke(seasonalEventService, new[] { wSzn });
                }, $"天气联动 {weatherSeason}");
                EventRotatorStatusRouter.AppliedEventSeason = evType;
                logger.Warning($"[EventRotator][活动季节] ★天气季节 {weatherSeason} → 自动开活动季节 {evType}（可关掉这个联动）");
                return;
            }

            // ④ 不动
            // ★ v1.5.1【P2-7 修】以前不清空 ⇒ 从"有映射的季节"切到"无映射的季节"后，
            //   面板会一直显示上一季的活动季节（陈旧值）。
            // ★ v1.6.1 只在**非季节轮换**模式下清空：rotate 模式下本值由 TryForceSeasonal 写入，
            //   这里若无条件清空会把刚记下的活动季节抹掉（P2-7 的修复被覆盖）。
            if (!SeasonRotation.IsRotating)
                EventRotatorStatusRouter.AppliedEventSeason = "";
            if (SigChanged("evseason-none", weatherSeason ?? ""))
                logger.Info($"[EventRotator][活动季节] 未指定 → 不干预（活动季节保持原样；天气季节={weatherSeason}）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][活动季节] 失败: {ex.Message}"); }
    }

    private void AutoOpenSeasonalFromWeather(string weatherSeason, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (!SeasonRotation.AutoOpenSeasonalEvent) return;

            // ★ 手动指定了活动 → 忽略联动（活动季节跟活动走）
            if (!string.IsNullOrEmpty(EventEngine.ForcedId) && EventEngine.ForcedId != "none")
            {
                logger.Info("[EventRotator][活动季节] 检测到手动指定活动（force.txt = " + EventEngine.ForcedId + "）→ 跳过「天气季节→活动季节」联动");
                return;
            }

            if (string.IsNullOrWhiteSpace(weatherSeason)) return;
            if (!SeasonRotation.SeasonToEventMap.TryGetValue(weatherSeason.Trim(), out var evType) || string.IsNullOrEmpty(evType))
            {
                logger.Info($"[EventRotator][活动季节] 天气季节 {weatherSeason} 没有对应的活动季节 → 不动");
                return;
            }

            var mForce = seasonalEventService.GetType().GetMethod("ForceSeasonalEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var et = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Enums.SeasonalEventType");
            if (mForce == null || et == null) { logger.Warning("[EventRotator][活动季节] API 不可用"); return; }
            var wSzn2 = Enum.Parse(et, evType, true);
            ForceSeasonalOnce(evType, () =>
            {
                MaybePrepareHalloween(evType);   // 万圣节原生开关
                mForce.Invoke(seasonalEventService, new[] { wSzn2 });
            }, $"天气联动 {weatherSeason}");
            logger.Warning($"[EventRotator][活动季节] ★天气季节 {weatherSeason} → 自动开启活动季节 {evType}（想手动控制就勾选具体活动）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][活动季节] 联动失败: {ex.Message}"); }
    }

    private void ApplyWeatherSeason(EventDef ev)
    {
        try
        {
            WeatherSeasonDimension.WeatherConfig = weatherConfig;
            WeatherSeasonDimension.Log = m => logger.Info(m);
            WeatherSeasonDimension.AddRestore = a => _restoreActions.Add(a);

            // ① 活动自带（最高优先）
            if (ev != null && !string.IsNullOrWhiteSpace(ev.ForceWeatherSeason))
            {
                WeatherSeasonDimension.Apply(ev.ForceWeatherSeason, "活动 " + ev.Id + " 自带", logger);
                ApplyEventSeason(ev.ForceWeatherSeason, logger);
                return;
            }

            // ② 按天数轮换
            if (SeasonRotation.IsRotating)
            {
                var slot = SeasonRotation.CurrentSlot();
                var want = WeatherSeasonDimension.CurrentRotated(slot + SeasonRotation.Offset);
                WeatherSeasonDimension.Apply(want, $"按天数轮换（档位#{slot}，每 {SeasonRotation.IntervalDays} 天）", logger);
                ApplyEventSeason(want, logger);
                return;
            }

            // ③ 指定季节
            if (SeasonRotation.IsFixed)
            {
                WeatherSeasonDimension.Apply(SeasonRotation.FixedSeason, "指定季节（fixed）", logger);
                ApplyEventSeason(SeasonRotation.FixedSeason, logger);
                return;
            }

            // ④ 其它 → 不覆盖
            WeatherSeasonDimension.Apply(null, "mode=" + SeasonRotation.Mode, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 天气季节失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★ v1.2.0【英文摘要】给客户端英文界面用的**结构化一句话**（不翻译中文散文，避免机翻腔）。
    /// 只从字段生成：刷首领 / 爆率 / 任务 / 交易 / 空投 等，老外一眼能看懂"这个活动改了什么"。
    /// </summary>
    private static string BuildEnSummary(EventDef e)
    {
        try
        {
            var parts = new List<string>();

            // 首领
            int bossMaps = 0, bossKinds = 0;
            if (e.BossSpawns != null && e.BossSpawns.Count > 0)
            {
                bossMaps = e.BossSpawns.Count;
                foreach (var kv in e.BossSpawns) bossKinds += (kv.Value == null ? 0 : kv.Value.Count);
            }
            if (e.BossChance != null && e.BossChance.Count > 0)
            {
                foreach (var kv in e.BossChance) bossKinds += (kv.Value == null ? 0 : kv.Value.Count);
                if (bossMaps == 0) bossMaps = e.BossChance.Count;
            }
            if (bossKinds > 0) parts.Add($"boss spawns on {bossMaps} map(s)");

            // 爆率
            if (e.LooseLootMultiplier.HasValue && Math.Abs(e.LooseLootMultiplier.Value - 1f) > 0.01f)
                parts.Add($"loose loot x{e.LooseLootMultiplier.Value:0.##}");
            if (e.StaticLootMultiplier.HasValue && Math.Abs(e.StaticLootMultiplier.Value - 1f) > 0.01f)
                parts.Add($"container loot x{e.StaticLootMultiplier.Value:0.##}");

            // 任务
            int q = e.EnableQuests != null ? e.EnableQuests.Count : 0;
            if (q > 0) parts.Add($"{q} quest(s)");

            // 商人 / 跳蚤
            if (e.UnlockTraders) parts.Add("trader unlocks");
            if (e.FleaMinLevel.HasValue) parts.Add($"flea min level {e.FleaMinLevel.Value}");

            // 加成
            if (e.ExpMultiplier.HasValue && Math.Abs(e.ExpMultiplier.Value - 1f) > 0.01f)
                parts.Add($"EXP x{e.ExpMultiplier.Value:0.##}");
            if (e.SkillMultiplier.HasValue && Math.Abs(e.SkillMultiplier.Value - 1f) > 0.01f)
                parts.Add($"skill rate x{e.SkillMultiplier.Value:0.##}");

            if (parts.Count == 0)
                return e.NameEn ?? "";

            return string.Join(" · ", parts);
        }
        catch { return e.NameEn ?? ""; }
    }

    private bool _b3Warned;

    private void WriteActiveStatus(List<EventDef> evs)
    {
        // ★ v1.5.0【B3】config.json 的两个旧段落下线提示（只报一次/每次写都报，日志可见即可）
        try
        {
            var cfgPath = System.IO.Path.Combine(EventEngine.ModPath ?? "", "config.json");
            if (System.IO.File.Exists(cfgPath))
            {
                var raw = System.IO.File.ReadAllText(cfgPath);
                if ((raw.Contains("\"families\"") || raw.Contains("\"familyGeneric\"")) && !_b3Warned)
                {
                    _b3Warned = true;
                    logger?.Warning("[EventRotator] ⚠ config.json 里的 `families` / `familyGeneric` 是**旧版遗留、已不再生效**"
                        + "（组合展开只读 events.json 的 family/familyYear/familyGeneric）。改这两段没有任何效果，建议删除。");
                }
            }
        }
        catch { }

        try
        {
            var dir = EventEngine.ModPath;
            if (string.IsNullOrEmpty(dir)) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 当前生效的活动 ===");
            sb.AppendLine("更新时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            bool forced = !string.IsNullOrEmpty(EventEngine.ForcedId) && EventEngine.ForcedId != "none";
            // ★ 2026-10-29【周 / 周末抽签】来源行追加标注（mode=off ⇒ tag 为空 ⇒ 这一行与改动前逐字节一致）
            var srcLine = EventEngine.DisableAllEvents
                ? "⛔ 不启用任何活动（config.json → disableAllEvents = true）"
                : forced ? ("手动强制（force.txt = " + EventEngine.ForcedId + "）")
                         : "自动（日期窗 / 活动轮换 / 奖励轮换）";
            var wtag = Rotation.WeeklyDrawTag();
            var wids = Rotation.WeeklyAdded;
            if (wtag.Length > 0 && wids != null && wids.Count > 0)
                srcLine += " ＋" + wtag + "（" + string.Join(",", wids.Select(x => x.Id)) + "）";
            sb.AppendLine("来源: " + srcLine);
            sb.AppendLine("数量: " + (evs?.Count ?? 0));
            sb.AppendLine();
            if (evs == null || evs.Count == 0)
            {
                sb.AppendLine("(当前没有任何活动生效)");
                sb.AppendLine();
                sb.AppendLine("提示：");
                sb.AppendLine("  · 想手动开活动 → 在下面勾选任意活动");
                sb.AppendLine("  · 想自动轮换   → 打开【轮换设置】的『启用自动轮换』");
                sb.AppendLine("  · 想自动开奖励 → 打开【奖励类轮换】的『启用奖励类轮换』");
            }
            else
            {
                sb.AppendLine("共 " + evs.Count + " 个活动同时生效：");
                sb.AppendLine();
                int idx = 1;
                foreach (var e in evs)
                {
                    string cat;
                    var c2 = (e.Category ?? "").Trim().ToLowerInvariant();
                    // ★★★ 2026-10-24【分类显示修正】旧映射只认 `seasonal` / `limited`（v0.1.5 之前的老分类名）
                    //   ⇒ `event_task` 与 `event_ai` **全部**掉进 `else` = "③ 小活动" ✗
                    //   实测后果：`当前生效活动.txt` 把 `scourge_incursion`(event_task) /
                    //   `meeting_of_the_bosses_2026`(event_ai) 都写成「③ 小活动」，
                    //   而客户端 EventsOverlay 的 CatOrder() 正是靠这串标签分组 ⇒
                    //   F12 面板把它们排进「小活动」段、用户看到的分类与 events.json 的 category 不一致。
                    //   ⇒ 这里与客户端 CatOrder()/服务端 CategoryDesc() 对齐：
                    //     ① 季节 / ② 限时带任务 / ★ AI 活动 / ③ 小活动 / ④ 奖励。
                    //   ⚠ 客户端靠 `①`/`②`/`③`/`④` 与「AI」关键字排序（EventsOverlay.cs:898-909），
                    //     所以标签**必须**保留这些记号。
                    if (c2 == "seasonal") cat = "① 季节";
                    else if (c2 == "event_task" || c2 == "limited") cat = "② 限时带任务";
                    else if (c2 == "event_ai") cat = "★ AI 活动";
                    else if (c2 == "reward") cat = "④ 奖励类";
                    else cat = "③ 小活动";
                    sb.AppendLine(idx++ + ". [" + cat + "] " + e.NameZh + " (" + e.Id + ")");
                    // ★ v0.1.3：给客户端的悬浮面板用的机器可读行（"#事件|分类|中文名|英文名|id|内容说明"）
                    //   —— 客户端按键弹出的活动面板直接读这一行，不需要再解析人读文本。
                    var desc = !string.IsNullOrWhiteSpace(e.Desc) ? e.Desc : (e.Note ?? "");
                    desc = desc.Replace("\r", " ").Replace("\n", " ").Replace("|", "／").Trim();
                    if (desc.Length > 220) desc = desc.Substring(0, 220) + "…";
                    var descEn = BuildEnSummary(e).Replace("|", "/").Trim();
                    if (descEn.Length > 200) descEn = descEn.Substring(0, 200) + "...";
                    sb.AppendLine("#事件|" + cat + "|" + (e.NameZh ?? "").Replace("|", "／")
                                  + "|" + (e.NameEn ?? "").Replace("|", "／")
                                  + "|" + e.Id + "|" + desc + "|" + descEn);
                    if (e.EnableQuests != null && e.EnableQuests.Count > 0)
                        sb.AppendLine("      任务 " + e.EnableQuests.Count + " 个");
                }
            }
            sb.AppendLine();
            sb.AppendLine("── 季节 ──");
            var seasonLine = "模式: " + SeasonRotation.Mode
                             + (SeasonRotation.IsFixed ? ("（指定 " + SeasonRotation.FixedSeason + "）") : "")
                             + (SeasonRotation.IsRotating ? ("（每 " + SeasonRotation.IntervalDays + " 天轮换 → 当前 " + SeasonRotation.CurrentSeason() + "）") : "");
            sb.AppendLine(seasonLine);
            // ★ 机器可读的季节行：模式|指定天气季节|指定活动季节|轮换到的季节|友好名
            sb.AppendLine("#季节|" + SeasonRotation.Mode
                          + "|" + (SeasonRotation.IsFixed ? SeasonRotation.FixedSeason : "")
                          + "|" + (SeasonRotation.IsEventSeasonFixed ? SeasonRotation.EventSeasonVal : "")
                          + "|" + (SeasonRotation.IsRotating ? SeasonRotation.CurrentSeason() : "")
                          + "|" + SeasonRotation.FriendlySeason()
                          + "|" + (EventRotatorStatusRouter.AppliedEventSeason ?? ""));

            // ═══════════ ★ v0.1.3 轮换状态（供客户端判断"需不需要重启"）═══════════
            //   背景：活动大多是**服务端启动时**才套用的，而客户端有几样东西（物品模板 / globals）
            //   只在**游戏启动时**从服务端拉一次。所以换档后可能需要"重启服务端"甚至"重启游戏"。
            //   这里把"服务端本次实际套用的档位"写出来，客户端拿本机时间算出"当前应该是哪个档位"，
            //   两者不一致就提示玩家重启 —— 这样玩家在启动游戏时就能立刻知道该不该重启，
            //   而不是进去了才发现活动没变。
            try
            {
                int appliedSlot = Rotation.CurrentSlot();                  // 本次启动实际套用/计算的档位
                var nextStart = Rotation.NextSlotStart();
                // ★ v1.3.8【P1-6 修】预览必须与真实路径用**同一个 already**：
                //   以前只传"有 seasonalEvent 的活动" ⇒ 早退判据(already.Count>0)的结论与真实相反，
                //   于是 `#轮换` 行会写一个本档根本没开的活动（面板骗人）。
                // ★ v1.6.0【R3 修】预览不能把"本档轮换自己产出的活动"当成 already：
                //   否则它们会以"已存在"的身份把自己的候选挡掉 ⇒ 预览与真实结果不一致（60/60 档不符）。
                //   只保留**非轮换来源**（季节阶段开的 + force.txt 手动的）。
                var forcedIds = (EventEngine.ForcedId ?? "")
                    .Split(new[] { ',', ';', '|', ' ', (char)9, (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // ★ v1.6.1【回归审计 #5】日期窗生效时，窗口来源的活动也是真 already，
                //   预览若把它们漏掉，会显示一个真实路径开不出来的活动。
                // ★ 2026-10-29【第 22 项】"日期窗生不生效"不再只看 rotate ⇒ 改用同一个
                //   `EventEngine.DateWindowActive`（dateWindowRule + seasonRotation.mode 的唯一判据），
                //   否则 `dateWindowRule=always` 时预览会漏判窗口来源、与真实 Actives 漂移。
                bool windowForPreview = EventEngine.DateWindowActive;
                var previewAlready = (evs ?? new List<EventDef>())
                    .Where(x => !string.IsNullOrEmpty(x.SeasonalEvent)
                                || forcedIds.Contains(x.Id)
                                || (windowForPreview && EventEngine.InWindowOrTimeless(x, DateTime.Today)))
                    .ToList();
                var nextIds = Rotation.PreviewSlot(appliedSlot + 1, EventEngine.All, previewAlready);
                var curIds = Rotation.PreviewSlot(appliedSlot, EventEngine.All, previewAlready);

                sb.AppendLine();
                sb.AppendLine("── 轮换 ──");
                sb.AppendLine("档位: #" + appliedSlot + "（每 " + Rotation.IntervalDays + " 天换一档，基准 "
                              + Rotation.SlotRebaseHour + ":00）");
                sb.AppendLine("本档开始: " + Rotation.SlotStart(appliedSlot).ToString("yyyy-MM-dd HH:mm"));
                sb.AppendLine("下次换档: " + nextStart.ToString("yyyy-MM-dd HH:mm"));
                if (nextIds.Count > 0)
                    sb.AppendLine("下一档活动: " + string.Join(" + ", nextIds));
                else if (Rotation.OnlyWhenNoSeasonal && previewAlready.Any(Rotation.IsSeasonal))
                    sb.AppendLine("下一档活动: （当季有季节活动 → 暂停填充轮换）");

                sb.AppendLine("#轮换|" + appliedSlot
                              + "|" + Rotation.IntervalDays
                              + "|" + Rotation.SlotRebaseHour
                              + "|" + nextStart.ToString("yyyy-MM-ddTHH:mm:ss")
                              + "|" + Rotation.Enabled.ToString().ToLowerInvariant()
                              + "|" + Rotation.Mode
                              + "|" + string.Join(",", curIds)
                              + "|" + string.Join(",", nextIds));
                sb.AppendLine("#手动|" + (EventEngine.ForcedId ?? "").Trim()
                              + "|" + string.Join(",", (evs ?? new List<EventDef>()).Select(x => x.Id)));
            }
            catch (Exception ex2) { try { logger.Warning("[EventRotator] 写轮换状态失败: " + ex2.Message); } catch { } }

            var statusText = sb.ToString();
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "当前生效活动.txt"), statusText, new System.Text.UTF8Encoding(false));

            // ★ v1.3.0【联机下发】同一份文本缓存给 HTTP 接口（客机拉取用；失败不影响写文件）
            try
            {
                EventRotatorStatusRouter.Snapshot = statusText;
                EventRotatorStatusRouter.SnapshotAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                EventRotatorStatusRouter.ModVersion = EventRotatorMetadata.VersionText;
            }
            catch { }
        }
        catch (Exception ex) { try { logger.Warning("[EventRotator] 写当前生效活动失败: " + ex.Message); } catch { } }
    }

    private void ApplyLabsFlea(EventDef ev)
    {
        try
        {
            if (ev.LabsFreeAccess != true && !ev.FleaTaxMultiplier.HasValue && ev.FleaZeroListingFee != true) return;
            LabsFleaDimension.LocationTable = locationTable;
            LabsFleaDimension.RagfairConfig = ragfairConfig;
            LabsFleaDimension.GlobalTable = globalTable;   // ★ v0.1.3 客户端显示税额（globals CommunityItemTax）
            LabsFleaDimension.Log = m => logger.Info(m);
            LabsFleaDimension.AddRestore = a => _restoreActions.Add(a);
            LabsFleaDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 实验室/跳蚤维度失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★ v0.1.3 弹鼓概率：把弹鼓补进武器的弹匣槽过滤表（**纯 items 数据，PBS 也读**）。
    /// 这条是本机 PBS 环境下"大弹鼓 SCAV"唯一可行的实现路径 —— 不需要改 PBS 的任何文件。
    /// </summary>
    private void ApplyDrumMags(EventDef ev)
    {
        try
        {
            if (ev.DrumMagazineBoost == null || ev.DrumMagazineBoost.Count == 0) return;
            DrumMagazineDimension.TemplateTable = templateTable;
            DrumMagazineDimension.Log = m => logger.Info(m);
            DrumMagazineDimension.AddRestore = a => _restoreActions.Add(a);
            DrumMagazineDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 弹鼓维度失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ v0.1.4 **base.json 里真实存在的敌种**（顶层 BossName + BossEscortType + Supports[].BossEscortType）。
    /// <para>
    /// 用户指出的关键点：判断"任务能不能做完"不能只看**活动怎么刷**，还要看**游戏里有没有这个实体** ——
    /// 只要 base.json 里有条目，活动的 `bossChance` 就能提升它的概率 ⇒ 任务可做。实测例子：
    ///   · `exUsec` 是灯塔的顶层 BossName，也是三狗（bossKnight）的 BossEscortType
    ///   · `pmcBot`（Rogue）是实验室/储备站的顶层 BossName
    ///   · `followerBigPipe` / `followerBirdEye`（三狗小弟）藏在 bossKnight 的 **Supports[]** 里
    /// 之前只看活动的 bossSpawns，把这 4 种误判成"没人刷、补不了"。
    /// </para>
    /// </summary>
    private static HashSet<string> _gameRoles;

    /// <summary>加载 base.json 里真实存在的敌种（首次调用时读，之后缓存）。</summary>
    public static HashSet<string> GameRoles()
    {
        if (_gameRoles != null) return _gameRoles;
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = System.IO.Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "locations");
            if (!System.IO.Directory.Exists(root)) return _gameRoles = res;
            foreach (var dir in System.IO.Directory.GetDirectories(root))
            {
                var f = System.IO.Path.Combine(dir, "base.json");
                if (!System.IO.File.Exists(f)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f));
                    if (!doc.RootElement.TryGetProperty("BossLocationSpawn", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    foreach (var e in arr.EnumerateArray())
                    {
                        foreach (var key in new[] { "BossName", "BossEscortType" })
                            if (e.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                            { var s = v.GetString(); if (!string.IsNullOrWhiteSpace(s)) res.Add(s); }
                        if (e.TryGetProperty("Supports", out var sup) && sup.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var s2 in sup.EnumerateArray())
                                if (s2.TryGetProperty("BossEscortType", out var v2) && v2.ValueKind == System.Text.Json.JsonValueKind.String)
                                { var s = v2.GetString(); if (!string.IsNullOrWhiteSpace(s)) res.Add(s); }
                    }
                }
                catch { }
            }
        }
        catch { }
        return _gameRoles = res;
    }

    /// <summary>
    /// ★★★ v0.1.4 某个活动"会刷哪些敌种"（含 `*` = 全部图/全部首领）。
    /// 用于"活动任务能不能做完"的自动检查。
    /// </summary>
    public static (HashSet<string> roles, bool allRoles) SpawnedRolesOf(EventDef e)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool all = false;
        if (e == null) return (roles, all);
        void AddList(List<string> l)
        {
            foreach (var x in l ?? new List<string>())
            {
                if (string.Equals(x, "*", StringComparison.OrdinalIgnoreCase)) all = true;
                else roles.Add(x);
            }
        }
        if (e.BossSpawns != null) foreach (var v in e.BossSpawns.Values) AddList(v);
        if (e.ZeroBossSpawns != null) foreach (var v in e.ZeroBossSpawns.Values) AddList(v);
        if (e.BossChance != null) foreach (var v in e.BossChance.Values) foreach (var k in v.Keys) { if (k == "*") all = true; else roles.Add(k); }
        if (e.ForceAbpsSpawns != null) foreach (var k in e.ForceAbpsSpawns.Keys) { if (k == "*") all = true; else roles.Add(k); }
        if (e.ForceZone != null) foreach (var v in e.ForceZone.Values) foreach (var k in v.Keys) roles.Add(k);
        // ★ 修正（踩过的坑）：`forceExtraSpawns` 只是"某张图多刷几处"，**不代表提供了全部敌种**。
        //   之前把它当成 all=true，结果"任务缺敌种"的检查被整体跳过、永远不报警
        //   （血猎犬就是这样被漏掉的：它配了 forceExtraSpawns，于是检查以为"敌种齐了"）。
        // 季节活动才真的自带一整套敌种。
        if (!string.IsNullOrEmpty(e.SeasonalEvent) && !string.Equals(e.SeasonalEvent, "None", StringComparison.OrdinalIgnoreCase)) all = true;
        return (roles, all);
    }

    /// <summary>
    /// ★★★ v0.1.4 **任务可完成性检查**（用户问"bloodhounds 能还原吗"引出的真问题）。
    /// <para>
    /// 有些活动只开了任务，却没提供任务要杀的敌人 ⇒ 接了也做不完（`bloodhounds` 要 arenaFighterEvent、
    /// `dark_times` 要感染者…）。这里在展开搭档之后检查一遍：
    ///   ① 缺敌种 → 打**警告**，并**自动补上一个能刷该敌种的伙伴**
    ///   ② 补不上 → 明确告诉玩家"这个任务本次做不完"
    /// </para>
    /// </summary>
    private void CheckQuestCompletable(List<EventDef> active)
    {
        try
        {
            var allRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool all = false;
            foreach (var e in active)
            {
                var (r, a) = SpawnedRolesOf(e);
                allRoles.UnionWith(r);
                all |= a;
            }
            // ★ 游戏里本来就有实体的敌种（exUsec / pmcBot / 三狗小弟…）也算"够得着"：
            //   活动只要有 bossChance 就能提升它们的概率，任务因此可做。
            var game = GameRoles();
            var available = new HashSet<string>(allRoles, StringComparer.OrdinalIgnoreCase);
            available.UnionWith(game);

            foreach (var e in active.ToList())
            {
                if (e.QuestRequires == null || e.QuestRequires.Count == 0) continue;
                logger.Info($"[EventRotator] 任务可完成性检查：『{e.NameZh}』需检查 {e.QuestRequires.Count} 个任务；"
                            + $"本组合活动可刷 {allRoles.Count} 种敌种，base.json 实有 {game.Count} 种"
                            + (game.Count == 0 ? " ← ⚠ 读不到 base.json，检查会误报" : ""));
                foreach (var kv in e.QuestRequires)
                {
                    var need = (kv.Value ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                    if (need.Count == 0) continue;
                    var missing = need.Where(r => !available.Contains(r)).ToList();
                    if (missing.Count == 0 || all) continue;

                    logger.Warning($"[EventRotator] ⚠ 任务可完成性：『{e.NameZh}』开了任务 {kv.Key}，"
                                   + $"但它要杀的敌种 {string.Join("/", missing)} **游戏里没有实体、也没有活动会刷** "
                                   + "⇒ 这个任务做不完（要么补上能刷它的活动，要么别接）");

                    // ★ 逐个补：每补上一个就重新算"还缺什么"
                    foreach (var role in missing.ToList())
                    {
                        if (available.Contains(role)) continue;
                        var provider = EventEngine.All
                            .Where(p => !active.Contains(p))
                            .Where(p => SpawnedRolesOf(p).roles.Contains(role))
                            .OrderBy(p => p.Id, StringComparer.Ordinal)
                            .FirstOrDefault();
                        if (provider == null)
                        {
                            // ★ 兜底：没有活动能刷它，但**游戏里可能没有实体**——那就直接把该敌种
                            //   加进本活动的 bossChance（内层 "*" = 该图所有首领 → 改成点名该敌种）。
                            //   这样即使没有伙伴活动，本体 base.json 里的该敌种条目也会被拉到 100%。
                            //   （实测例子：`arenaFighterEvent` 在 bigmap/woods 有实体，但没有专门活动刷它）
                            if (GameRoles().Contains(role))
                            {
                                var (ok, why2) = InjectRoleChance(e, role, 100);
                                if (ok)
                                {
                                    allRoles.Add(role); available.Add(role);
                                    logger.Warning($"[EventRotator]    ✔ 已把 '{role}' 直接加进『{e.NameZh}』的首领概率表"
                                                   + "（游戏里有实体，提升它的刷新率即可）→ 这个任务现在能做了");
                                }
                                else
                                    logger.Warning($"[EventRotator]    × 无法把 '{role}' 加进概率表：{why2}");
                            }
                            else
                                logger.Warning($"[EventRotator]    × 找不到会刷 '{role}' 的活动，且 base.json 里也没有 '{role}' 实体 → 这个敌种无法补救");
                            continue;
                        }
                        var why = EventEngine.ConflictReason(e, provider)
                                  ?? active.Select(a2 => EventEngine.ConflictReason(a2, provider)).FirstOrDefault(x => x != null);
                        if (why != null)
                        {
                            logger.Warning($"[EventRotator]    × '{provider.Id}' 能刷 '{role}'，但与当前组合冲突（{why}）→ 跳过");
                            continue;
                        }
                        active.Add(provider);
                        var pr = SpawnedRolesOf(provider);
                        allRoles.UnionWith(pr.roles);
                        available.UnionWith(pr.roles);
                        all |= pr.allRoles;
                        logger.Warning($"[EventRotator]    ✔ 已自动补上『{provider.NameZh}』"
                                       + $"（它会刷 {role}）→ 这个任务现在能做了");

                        // ★★ 传递展开：补进来的伙伴自己可能还缺东西（autoPairs 链）。
                        //   实测例子：邪教之夜要感染者 → 补上「万圣节感染者」→ 但那个活动自己
                        //   缺邪教祭司 → 而万圣节季节活动提供 → 所以还要再展开一层。
                        //   没有这一步，链式依赖会静默失败（日志看着"补上了"，实际任务仍做不完）。
                        try
                        {
                            foreach (var pid in AutoPartnersOf(provider))
                            {
                                if (active.Any(x => string.Equals(x.Id, pid, StringComparison.OrdinalIgnoreCase))) continue;
                                if (!TryAddPartner(active, provider, pid, "autoPairs(链式)")) continue;
                                var pp = EventEngine.All.FirstOrDefault(x => string.Equals(x.Id, pid, StringComparison.OrdinalIgnoreCase));
                                if (pp != null)
                                {
                                    var prr = SpawnedRolesOf(pp);
                                    allRoles.UnionWith(prr.roles);
                                    available.UnionWith(prr.roles);
                                    all |= prr.allRoles;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex) { logger.Warning("[EventRotator] 任务可完成性检查失败: " + ex.Message); }
    }

    /// <summary>
    /// ★ v0.1.4 兜底修法：把某个敌种直接加进活动的 <c>bossChance</c>（每个已有地图 + 内层点名该敌种）。
    /// 返回 (是否成功, 原因)。注意：**不改 events.json 文件**，只改内存里的这一个活动对象，
    /// 重启服务端即恢复（属于运行期补救）。
    /// </summary>
    private static (bool ok, string why) InjectRoleChance(EventDef e, string role, int pct)
    {
        try
        {
            e.BossChance ??= new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            if (e.BossChance.Count == 0)
                return (false, "该活动没有任何 bossChance 地图条目可挂（需要先配最少一张图）");

            int n = 0;
            foreach (var kv in e.BossChance)
            {
                var byBoss = kv.Value;
                if (byBoss == null) { byBoss = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); e.BossChance[kv.Key] = byBoss; }
                // 内层 "*" 会把该图所有首领一起拉到 100%，太粗暴 → 保留原有 + 点名该敌种
                byBoss[role] = pct;
                n++;
            }
            return n > 0 ? (true, null) : (false, "没有地图条目");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>★ v0.1.4 转场维度（马拉松）：</summary>
    private void ApplyTransits(EventDef ev)
    {
        try
        {
            if (ev.TransitsActive != true) return;
            TransitDimension.LocationTable = locationTable;
            TransitDimension.Log = m => logger.Info(m);
            TransitDimension.AddRestore = a => _restoreActions.Add(a);
            TransitDimension.Apply(true, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 转场维度失败: {ex.Message}"); }
    }

    /// <summary>★ v0.1.3 items 维度（霰弹/全部弹药/近战/针剂 Buff）。</summary>
    /// <summary>按商人粒度的报价折扣（新维度；挂到主流程，别放进有前置 return 的方法里）。</summary>
    private void ApplyTraderAssort(EventDef ev)
    {
        // ★★ v0.1.5 新维度：按商人粒度的折扣 + 易物配方（审计指出这是"商人活动"的真正落点）
        try
        {
            TraderAssortDimension.TraderTable = tradersTable;
            TraderAssortDimension.Log = m => logger.Info(m);
            TraderAssortDimension.AddRestore = a => _restoreActions.Add(a);
            TraderAssortDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 商人 assort 维度失败: {ex.Message}"); }
    }

    /// <summary>空投追加：把活动物品登记进 AirdropAppendDimension（每次空投箱生成时追加）。</summary>
    /// <summary>玩家 SCAV 出生装备强化（官方 2024-12-21 PScav武装強化 的正解落点）。</summary>
    /// <summary>手册价改写（官方 Dogtag Sales / RAM Shortage 的正解落点）。</summary>
    /// <summary>把物品加进跳蚤禁售表（官方 2026-07-29 PBP「首领专属装备不可在跳蚤出售」）。</summary>
    /// <summary>按官方清单把商品从商人 assort 下架（官方 2023-11-25 Ammo Shortage）。</summary>
    /// <summary>按图设置 bot 类型上下限（官方 2026-05-08 Reshala vs Glukhar：海关不刷普通 SCAV）。</summary>
    private void ApplyBotTypeLimits(EventDef ev)
    {
        try
        {
            // ★ C3 修复：本方法同时实现两个字段 —— `botTypeLimitSet` 与 `scavRaidMapDisable`（后者在本方法后半段，
            //   原守卫之后 123 行）。旧守卫只看 `BotTypeLimitSet` ⇒ 只声明 `scavRaidMapDisable` 的活动会被这里
            //   静默 return（连日志都没有，日志全在 return 之后）。⇒ 守卫必须"两个字段都空"才早退。
            //   ⚠ 两处都用 `ev?.`：`&&` 不会因左操作数为 true 而跳过右操作数，直接用 `ev.ScavRaidMapDisable`
            //     在 ev == null 时会 NRE。
            if ((ev?.BotTypeLimitSet == null || ev.BotTypeLimitSet.Count == 0)
                && (ev?.ScavRaidMapDisable == null || ev.ScavRaidMapDisable.Count == 0)) return;

            // ══════════ ② botTypeLimitSet：★ P0 落点修正 —— 直接改**已加载的 locationTable** ══════════
            //   根因（全量审计实证）：本 mod 是 [Injectable]（Plugin.cs:41）⇒ **TypePriority = int.MaxValue**；
            //   SPT 在 `SPTStartupHostedService.StartAsync` 里按 TypePriority **升序**跑所有 IOnLoad
            //   （audit/spt_di/SPTarkov.DI.decompiled.cs:90 的 orderby + :278 的默认值）
            //   ⇒ 实际顺序：GameCallbacks(200000) → TraderCallbacks(700000) → RagfairCallbacks(900000) → 本 mod。
            //   而 `locationConfig.BotTypeLimits` 的**唯一读者**是 `PostDbLoadService.AdjustMapBotLimits()`
            //   （_forensic_tmp/ilspy_full/SPTarkov.Server.Core.Services.Server/PostDbLoadService.cs:425-459），
            //   由 `GameCallbacks.OnLoadAsync → GameController.Load() → PerformPostDbLoadActions()`
            //   （同文件 :46-48 总开关与调用；GameController.cs:402）触发 —— **早于我们**
            //   ⇒ 旧写法「写 locationConfig.BotTypeLimits」是**空转**，重启也一样（每次都是「SPT 先读、我们后写」）。
            //   正解 = 照 `AdjustMapBotLimits` 干的事**直接改已加载的表**：
            //     `location.Base.MinMaxBots`（List<MinMaxBot>，LocationBase.cs:890-891）里
            //     `WildSpawnType == type` 那条改 Min/Max；该图没有这条就 `Add` 一条（PostDbLoadService.cs:441-456）。
            //   这是与 bossSpawns 同一条「进图前改数据」路子（本文件把 LocationTable 交给 BossWavePatch.ApplyToDb）。
            //   ⚠ `MinMaxBot` 的 CLR 属性名是 **WildSpawnType**（MinMaxBot.cs:12-13）；注意基类 `MinMax<int>` 上
            //     还有一个**不同**的属性 `Type`（JSON 名 "type"，MinMax.cs:8-9）—— 写错属性 = 写了没人读
            //     （本项目的老毛病）。SPT 自己的代码也只写 `WildSpawnType`（PostDbLoadService.cs:452）。
            //   ⚠ 为什么这个落点"运行期改就有效"：这张表是**每局进图时**由客户端从 `/client/locations` 拉取、
            //     驱动波次生成的（即"进图前改数据"），不像 config 那样只在启动期烘一次 ⇒ 换活动后**立即生效**。
            //   ⚠ 旧实现（v0.1.5~v2.6.11）还顺手写了 `locationConfig.EnableBotTypeLimits`（总开关），
            //     它的唯一读点是 PostDbLoadService.cs:46 —— 同样是启动期 ⇒ 一并删除，免得下一个人照抄错落点。
            if (ev.BotTypeLimitSet != null && ev.BotTypeLimitSet.Count > 0)
            {
                int touched = 0;
                foreach (var mapKv in ev.BotTypeLimitSet)
                {
                    var mapName = mapKv.Key;
                    // ★ 键名/大小写兼容交给 `LocationTable.GetLocation`：它先过 `GetMappedKey`
                    //   （"bigmap"/"Bigmap" → "Bigmap"，LocationTable.cs:322-362 + :412-419），
                    //   再在 `GetDictionary()`（StringComparer.OrdinalIgnoreCase，:426）里查
                    //   ⇒ 小写 Id 风格与 CLR 属性名风格都能命中（旧代码手工补 3 种大小写变体的做法不再需要）。
                    var bots = MinMaxBotsOf(mapName);
                    if (bots == null)
                    {
                        logger.Warning($"[EventRotator][bot上限] locationTable 里找不到地图 '{mapName}'"
                                     + "（或该图 Base.MinMaxBots 不是 List<MinMaxBot>）→ 跳过");
                        continue;
                    }

                    foreach (var entry in mapKv.Value)
                    {
                        var typeName = entry.Key;
                        var mm = entry.Value ?? new List<int>();
                        int min = mm.Count > 0 ? mm[0] : 0;
                        int max = mm.Count > 1 ? mm[1] : min;

                        // 已有同类型条目 → 改 Min/Max（匹配口径与 SPT 逐字一致：`WildSpawnType == type`，
                        //   PostDbLoadService.cs:441；这里额外做了 OrdinalIgnoreCase 容错）
                        int hit = bots.FindIndex(b => b != null
                            && string.Equals(b.WildSpawnType, typeName, StringComparison.OrdinalIgnoreCase));
                        if (hit >= 0)
                        {
                            var b = bots[hit];
                            int oldMin = b.Min, oldMax = b.Max;
                            var capB = b;
                            // ★ AddRestore 登记原值（Min/Max 一起，逆序回放 ⇒ 多活动同轮也精确回到原值）
                            _restoreActions.Add(() => { try { capB.Min = oldMin; capB.Max = oldMax; } catch { } });
                            b.Min = min;
                            b.Max = max;
                            touched += 2;
                            // ★ 写后回读自证（本项目的老毛病就是"打了日志却没生效"）
                            int backMin = b.Min, backMax = b.Max;
                            logger.Info($"[EventRotator][bot上限] {mapName}.MinMaxBots['{b.WildSpawnType}']: "
                                      + $"min {oldMin}→{backMin}、max {oldMax}→{backMax}"
                                      + (backMin == min && backMax == max ? " ✔（回读自证）" : " ⚠ 回读不符"));
                        }
                        else
                        {
                            // ★ 该图原本**没有**这一条（实测：`database/locations/bigmap/base.json` 只有 marksman、
                            //   `factory4_day/base.json` 的 MinMaxBots 是**空数组**）⇒ 追加一条；
                            //   还原时必须**移除**它，否则会永久多出一条"0/0 的 assault"。
                            var nb = new SPTarkov.Server.Core.Models.Eft.Common.MinMaxBot
                            {
                                WildSpawnType = typeName,   // ★ CLR 名（**不是** JSON 里的 "type"，那对应基类 Type）
                                Min = min,
                                Max = max,
                            };
                            var capL = bots;
                            _restoreActions.Add(() => { try { capL.Remove(nb); } catch { } });   // 原本没有 ⇒ 还原=移除
                            bots.Add(nb);
                            touched++;
                            // ★ 写后回读自证：从表里把刚加的那条**再查一遍**
                            var rb = bots.FirstOrDefault(b => b != null
                                && string.Equals(b.WildSpawnType, typeName, StringComparison.OrdinalIgnoreCase));
                            logger.Info($"[EventRotator][bot上限] {mapName}.MinMaxBots **新增** "
                                      + $"{{WildSpawnType:'{rb?.WildSpawnType}', min:{rb?.Min}, max:{rb?.Max}}}"
                                      + (rb != null && rb.Min == min && rb.Max == max ? " ✔（回读自证；还原时移除）" : " ⚠ 回读不符"));
                        }
                    }
                    // ★ 整图回读自证：把写完之后表里的**真实内容**打出来（而不是把我们的意图再念一遍）
                    var back = string.Join("、", bots.Select(b => $"{b?.WildSpawnType}:[{b?.Min},{b?.Max}]"));
                    bool okAll = true;
                    foreach (var kv3 in mapKv.Value)
                    {
                        var b2 = bots.FirstOrDefault(b => b != null
                            && string.Equals(b.WildSpawnType, kv3.Key, StringComparison.OrdinalIgnoreCase));
                        int wMin = kv3.Value != null && kv3.Value.Count > 0 ? kv3.Value[0] : 0;
                        int wMax = kv3.Value != null && kv3.Value.Count > 1 ? kv3.Value[1] : wMin;
                        if (b2 == null || b2.Min != wMin || b2.Max != wMax) okAll = false;
                    }
                    logger.Info($"[EventRotator][bot上限] {mapName} 期望 "
                              + string.Join("、", mapKv.Value.Select(kv3 => $"{kv3.Key}:[{string.Join(",", kv3.Value)}]"))
                              + $" ⇒ 表里实际 {back} " + (okAll ? "✔" : "⚠ 回读不符"));
                }
            }

            // ★ 禁止玩家 SCAV 进入这些图（官方 2026-05-01：Factory is not accessible in the Scav game mode）
            //   ★★★ v2.6.11【P0 落点修正·全量审计 verify-b ②-2 提出、Lead 独立复核】
            //   旧实现删的是 `locationConfig.ScavRaidTimeSettings.Maps[key]`，两个致命问题：
            //     ① **客户端 0 读取**该配置（全客户端反编译 grep `scavRaidTime` 0 命中）⇒ 根本拦不住玩家选图；
            //     ② 服务端唯一读者 `RaidTimeAdjustmentService.GetMapSettings` 用的是**直接索引器**
            //        `Maps[location.ToLowerInvariant()]`（**不是 TryGetValue**）⇒ 键被删掉后 SCAV 战局会抛
            //        `KeyNotFoundException`（PMC 分支在 :165 就 return，只炸 SCAV —— 正好是本条要禁的场景）。
            //        ⇒ 等于"用异常当拦截"，而 C3 守卫修好后这段才第一次真的会执行。
            //   真落点 = `LocationBase.DisabledForScav`（bool?），客户端 4 处逐字消费：
            //     Assembly-CSharp:966002 / :966158（选图界面锁图标 + 锁面板）
            //                      :966297（官方文案 "This location is not available for SCAV"）
            //                      :1025076（Ready 按钮禁用）
            //   本体自己的用法就是它：laboratory / sandbox_high 的 base.json 都靠 `DisabledForScav=true` 禁 SCAV。
            if (ev.ScavRaidMapDisable != null && ev.ScavRaidMapDisable.Count > 0)
            {
                try
                {
                    foreach (var mid in ev.ScavRaidMapDisable)
                    {
                        var loc = locationTable?.GetType()
                            .GetMethod("GetLocation", new[] { typeof(string) })
                            ?.Invoke(locationTable, new object[] { mid });
                        if (loc == null) { logger.Warning($"[EventRotator][SCAV图] 找不到 location {mid}"); continue; }

                        var b = loc.GetType().GetProperty("Base",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(loc);
                        if (b == null) { logger.Warning($"[EventRotator][SCAV图] {mid} 没有 Base"); continue; }

                        var pD = b.GetType().GetProperty("DisabledForScav",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.IgnoreCase);
                        if (pD == null || !pD.CanWrite) { logger.Warning($"[EventRotator][SCAV图] {mid}.Base 没有可写的 DisabledForScav"); continue; }

                        var oldV = pD.GetValue(b);
                        var capB = b; var capP = pD; var capV = oldV;
                        _restoreActions.Add(() => { try { capP.SetValue(capB, capV); } catch { } });
                        pD.SetValue(b, (bool?)true);

                        var after = pD.GetValue(b);   // 回读自证（本项目的老毛病就是"打了日志却没生效"）
                        logger.Info($"[EventRotator][SCAV图] {mid}.Base.DisabledForScav: {oldV} → {after}"
                                    + (Equals(after, (bool?)true)
                                       ? " ✔（客户端会锁该图并显示「This location is not available for SCAV」）"
                                       : " ⚠ 回读不符"));
                    }
                }
                catch (Exception ex2) { logger.Warning($"[EventRotator][SCAV图] 失败: {ex2.Message}"); }
            }
            // ★ 旧代码在这里打 `共改动 {touched} 个字段（location.json → botTypeLimits）` —— 那句话本身就是
            //   "打了日志却没生效"的典型（写的是启动期才被读的 config）。现在统计与自证日志全部落在
            //   上面 `botTypeLimitSet` 分支内部（那里的 `touched` 才对应**真正被改的表数据**）。
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] bot 上限设置失败: {ex.Message}"); }
    }

    private void ApplyAssortRemove(EventDef ev)
    {
        try
        {
            // ★★ 必须在这里**自带初始化**：本方法在主循环里排在 ApplyTraderAssort **之前**，
            //   而 TraderTable/Log/AddRestore 是在那个方法里赋值的 ⇒ 首次执行时它们还是 null，
            //   表现为"下架静默失败"（实测：字段绑上了、却没打任何日志）。
            TraderAssortDimension.TraderTable = tradersTable;
            TraderAssortDimension.Log = m => logger.Info(m);
            TraderAssortDimension.AddRestore = a => _restoreActions.Add(a);
            TraderAssortDimension.RemoveFromAssort(ev, logger);
            TraderAssortDimension.DisableCashOffers(ev, logger);   // ★ 官方 Barter Only（除 Fence 只收易物）
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] 商人下架失败: {ex.Message}"); }
    }

    private void ApplyFleaBlacklist(EventDef ev)
    {
        try
        {
            var add = ev?.FleaBlacklistAdd;
            if (add == null || add.Count == 0) return;
            if (itemConfig?.Blacklist == null) { logger.Warning("[EventRotator][跳蚤] 拿不到 itemConfig.Blacklist，未生效"); return; }

            int n = 0;
            var newlyAdded = new List<SPTarkov.Server.Core.Models.Common.MongoId>();
            foreach (var tpl in add)
            {
                if (string.IsNullOrWhiteSpace(tpl)) continue;
                var id = (SPTarkov.Server.Core.Models.Common.MongoId)tpl;
                if (itemConfig.Blacklist.Contains(id)) continue;   // 已在表里（如 SPT 自带条目）⇒ 不动，免得还原时删掉原有的
                var cap = itemConfig.Blacklist; var capId = id;
                _restoreActions.Add(() => { try { cap.Remove(capId); } catch { } });
                itemConfig.Blacklist.Add(id);
                newlyAdded.Add(id);
                n++;
            }

            // ★★★ v2.6.12【P0 落点修正·两边都要写】`itemConfig.Blacklist` **只是构造期快照的来源**：
            //   `ItemFilterService` 在**构造函数**里把它逐项拷进 `ItemBlacklistCache`
            //   （ItemFilterService.cs:21-29），而 `IsItemBlacklisted()` **只查 cache**（:112-115）；
            //   `GetBlacklistedItems()`（:61-64）虽然返回 config 那份，但真正做过滤的路径全都走 cache：
            //     · LootGenerator.cs:437 / :550         （战利品池）
            //     · ScavCaseRewardGenerator.cs:76/:113  （藏身处情报站回收）
            //     · FenceBaseAssortGenerator.cs:35      （Fence 基础报价）
            //     · BotEquipmentModGenerator.cs:1242 / PMCLootGenerator.cs:137（用 GetBlacklistedItems，config 口径）
            //   ⇒ 只写 config 的话，上面这些路径**永远看不到**本 mod 加的条目（Spike：日志说"加了 208 项"，
            //     实际跳蚤/战利品里一件都没拦住）。
            //   正解 = 调 SPT 自带的 `ItemFilterService.AddItemToBlacklistCache(IEnumerable<MongoId>)`
            //   （ItemFilterService.cs:107-110，内部 `HashSet.UnionWith` ⇒ 幂等）。
            int cacheAdded = 0;
            if (itemFilterService == null)
                logger.Warning("[EventRotator][跳蚤] 拿不到 ItemFilterService（DI 未注入）⇒ 禁售只写进了 config，"
                             + "运行期过滤路径（LootGenerator/ScavCase/Fence）看不到");
            else if (newlyAdded.Count > 0)
            {
                itemFilterService.AddItemToBlacklistCache(newlyAdded);
                cacheAdded = newlyAdded.Count;
                // ★ 写后回读自证：逐项问 SPT 自己的查询接口（本项目的老毛病就是"打了日志却没生效"）
                int okCnt = newlyAdded.Count(id => itemFilterService.IsItemBlacklisted(id));
                logger.Info($"[EventRotator][跳蚤] AddItemToBlacklistCache 回读自证：{okCnt}/{newlyAdded.Count} 项 "
                          + (okCnt == newlyAdded.Count ? "✔ IsItemBlacklisted 全部为 true" : "⚠ 有项目未进 cache"));
                // ⚠ 还原侧：`ItemBlacklistCache` 是 `protected readonly HashSet<MongoId>`，SPT **没有**提供
                //   移除接口 ⇒ 这里用反射拿到那个字段并逐项 Remove（readonly 只挡重新赋值，不挡集合内容变更）。
                //   不这样做的话：换到"不禁售"的活动后，本会话内这些物品仍被禁售（要重启服务端才清），
                //   正是本次要消灭的"关不掉的活动"类残留。
                var fld = itemFilterService.GetType().GetField("ItemBlacklistCache",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var cacheSet = fld?.GetValue(itemFilterService)
                               as HashSet<SPTarkov.Server.Core.Models.Common.MongoId>;
                if (cacheSet == null)
                    logger.Warning("[EventRotator][跳蚤] ⚠ 反射没拿到 ItemFilterService.ItemBlacklistCache ⇒ "
                                 + "cache 侧的还原做不到（换活动后本会话内仍在禁售，需重启服务端）");
                else
                {
                    var capSet = cacheSet;
                    var capIds = new List<SPTarkov.Server.Core.Models.Common.MongoId>(newlyAdded);
                    _restoreActions.Add(() =>
                    {
                        try { foreach (var x in capIds) capSet.Remove(x); } catch { }
                    });
                }
            }
            logger.Info($"[EventRotator][跳蚤] 首领专属装备加入禁售表：{n} 项"
                      + $"（config.Blacklist 现 {itemConfig.Blacklist.Count} 项 + 运行期 cache {cacheAdded} 项；"
                      + "原有条目不动、两边都登记了还原）");
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] 跳蚤禁售表失败: {ex.Message}"); }
    }

    private void ApplyPrice(EventDef ev)
    {
        try
        {
            PriceDimension.TemplateTable = templateTable;
            PriceDimension.HandbookHelper = handbookHelper;
            PriceDimension.Log = m => logger.Info(m);
            PriceDimension.AddRestore = a => _restoreActions.Add(a);
            PriceDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] 价格维度失败: {ex.Message}"); }
    }

    private void ApplyPlayerScav(EventDef ev)
    {
        try
        {
            PlayerScavDimension.PlayerScavConfig = playerScavConfig;
            PlayerScavDimension.Log = m => logger.Info(m);
            PlayerScavDimension.AddRestore = a => _restoreActions.Add(a);
            PlayerScavDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] 玩家SCAV 维度失败: {ex.Message}"); }
    }

    private void ApplyAirdropAppend(EventDef ev)
    {
        try
        {
            AirdropAppendDimension.Log = m => logger.Info(m);
            // ★ 过滤已在套用循环开头由 `MapGateDimension.SetOnlyOnMap(ev.OnlyOnMap)` 登记，
            //   这里只需登记物品；实际判定发生在每次空投箱生成时（`Postfix` → `AirdropAllowed()`）。
            if (ev.AirdropAppendLoot != null && ev.AirdropAppendLoot.Count > 0)
                AirdropAppendDimension.Set(ev.AirdropAppendLoot, ev.OnlyOnMap);
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] 空投追加登记失败: {ex.Message}"); }
    }

    private void ApplyTaggedAndCursed(EventDef ev)
    {
        // ★★ v0.1.5【诅咒标签 Tagged and Cursed】—— 官方 2022 万圣节内容之一。
        //   反编译实证（客户端 Assembly-CSharp + SPT 自带插件 spt-custom.dll）：
        //     · 客户端 `EFT.WavesSettings.IsTaggedAndCursed` 是**离线局开关**（`RaidSettingsWindow` 的拨杆，只在 ERaidMode.Local 解锁）；
        //     · 效果 = `BaseLocalGame.ModifySettings` 把全图 `assault` 波次换成 `cursedAssault`
        //       （专用大脑 CursedAssaultLayersStrategy：GroupAttackLayer + PursuitLayer + AllPursuit ⇒ 集体冲锋死追）；
        //     · 服务端下发链路 = `/singleplayer/settings/raid/menu` → spt-custom 的
        //       `SetPreRaidSettingsScreenDefaultsPatch` 把它写进 `WavesSettings.IsTaggedAndCursed`；
        //     · 配置项 = `InRaidConfig.RaidMenuSettings.TaggedAndCursed`（默认 false）。
        //   ⇒ 落点就是这一行；**每次打开离线局设置界面都会重拉**，所以不必重启客户端。
        try
        {
            if (ev.TaggedAndCursed.HasValue)
            {
                var rms = inRaidConfig?.GetType().GetProperty("RaidMenuSettings")?.GetValue(inRaidConfig);
                var pTac = rms?.GetType().GetProperty("TaggedAndCursed");
                if (pTac != null && pTac.CanWrite)
                {
                    var oldVal = pTac.GetValue(rms);
                    var capR = rms; var capP = pTac;
                    _restoreActions.Add(() => { try { capP.SetValue(capR, oldVal); } catch { } });
                    pTac.SetValue(rms, ev.TaggedAndCursed.Value);
                    logger.Warning($"[EventRotator] ★诅咒标签 Tagged and Cursed：{oldVal} → {ev.TaggedAndCursed.Value}"
                                 + "（全图 SCAV 波次换成疯狂 SCAV；打开离线局设置界面即生效，无需重启客户端）");
                }
                else logger.Warning("[EventRotator] 找不到 RaidMenuSettings.TaggedAndCursed，未生效");
            }
        }
        catch (Exception ex) { logger.Warning($"[EventRotator] 诅咒标签设置失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ v2.6.8【官方「完整版万圣节模式」】—— `globals.config.EventSettings.EventActive = true`。
    ///
    /// 官方机制（客户端反编译实证，详见 `ExtraDimensions.ApplyEventWeather` 的注释与 `EventDef.EventActive`）：
    ///   打开后客户端会 ① 在除 `LocationsToIgnore`（factory4_day/night、hideout、laboratory）外的
    ///   **所有地图**挂 `Prefabs/HALLOWEEN_CONTROLLER`；② 世界钟在 `WeatherChangeTime` 秒内扫到
    ///   `EventWeather.Hour`(21:00) 并锁定 ⇒ **进图即黑夜**；③ 天气切到
    ///   `Cloudness 1 + Rain 0.1 + Wind 4` ⇒ **电闪雷鸣**；④ HUD 出现活动计时器。
    ///
    /// ⚠ 用户明确指正：**入夜（forceNightRaid）是给邪教徒类活动用的；雷雨/万圣节模式是万圣节专属**。
    ///   本 mod 此前从未设置过 `EventActive`（源码里只命中一行注释），SPT 本体也不设置
    ///   ⇒ 这套官方模式在本机一次都没生效过。本方法是补上它。
    /// </summary>
    private void ApplyEventSettings(EventDef ev)
    {
        try
        {
            if (ev.EventActive != true) return;
            ExtraDimensions.GlobalTableRef = globalTable;
            ExtraDimensions.Log = logger;
            ExtraDimensions.AddRestore = a => _restoreActions.Add(a);
            ExtraDimensions.ApplyEventWeather(true, "活动 " + ev.Id + " 自带 eventActive");
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 万圣节模式维度失败: {ex.Message}"); }
    }

    private void ApplyItems(EventDef ev)
    {
        try
        {
            if (ev.Items == null || ev.Items.Count == 0) return;
            ItemsDimension.TemplateTable = templateTable;
            ItemsDimension.GlobalTable = globalTable;
            ItemsDimension.Log = m => logger.Info(m);
            ItemsDimension.AddRestore = a => _restoreActions.Add(a);
            ItemsDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] items 维度失败: {ex.Message}"); }
    }
    private void ApplyBotEquipment(EventDef ev)
    {
        try
        {
            if ((ev.BotAddItems == null || ev.BotAddItems.Count == 0)
                && (ev.BotAddMods == null || ev.BotAddMods.Count == 0)
                && (ev.BotLootAdd_ == null || ev.BotLootAdd_.Count == 0)) return;
            logger.Info($"[EventRotator][bot诊断] {ev.Id}: items={(ev.BotAddItems == null ? -1 : ev.BotAddItems.Count)} mods={(ev.BotAddMods == null ? -1 : ev.BotAddMods.Count)} loot={(ev.BotLootAdd_ == null ? -1 : ev.BotLootAdd_.Count)}");
            BotEquipmentDimension.BotTable = botTable;
            BotEquipmentDimension.Log = m => logger.Info(m);
            BotEquipmentDimension.AddRestore = a => _restoreActions.Add(a);
            BotEquipmentDimension.Apply(ev, logger);
            BotEquipmentDimension.ApplyMods(ev, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] bot 装备池失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★【整体替换 bot 装备池】`botEquipmentReplace` —— 把一个角色某个装备槽的池子**整个换掉**
    /// （不是追加），并把该槽的装备概率置 100。落点见 <see cref="BotEquipmentReplaceDimension"/> 的类注释。
    /// 按图开关由 <c>BotGenerator.PrepareAndGenerateBot</c> 的 prefix 在**每只 bot 生成前**执行。
    /// 全程可降级：任何异常只 warning，不影响其它维度。
    /// </summary>
    private void ApplyBotEquipmentReplace(EventDef ev)
    {
        try
        {
            var spec = ev?.BotEquipmentReplace;
            if (spec == null || spec.Roles == null || spec.Roles.Count == 0) return;
            BotEquipmentReplaceDimension.BotTable = botTable;
            BotEquipmentReplaceDimension.Log = m => { try { logger.Info(m); } catch { } };
            BotEquipmentReplaceDimension.Warn = m => { try { logger.Warning(m); } catch { } };
            BotEquipmentReplaceDimension.Apply(ev);
            // ★★★【面具后置强制修正】只从同一份配置里读「地图 + 6 款面具」，武装 postfix（PBS 接管时唯一有效的落点）
            FaceCoverFixDimension.Apply(ev);
            logger.Info($"[EventRotator][装备替换] '{ev.Id}' 登记 {spec.Roles.Count} 个角色 / "
                      + $"{spec.Roles.Sum(x => x.Value?.Slots?.Count ?? 0)} 个装备槽 / "
                      + $"{spec.Roles.Sum(x => x.Value?.Mods?.Count ?? 0)} 个护甲插板池；地图=[{string.Join(",", spec.Maps ?? new List<string>())}]");
        }
        catch (Exception ex) { logger.Error($"[EventRotator] bot 装备整槽替换失败: {ex.Message}"); }
    }
    /// <summary>
    /// ★★★ v2.6.74【必定携带 + 指定数量】`botLootStack_`（走私者身上的 GP 币 10~20 枚）。
    ///
    /// <para>只登记规格 + 写配置层（`BotConfig.CurrencyStackSize`，走 <c>AddRestore</c> 可还原）；
    /// 产出物层（保证"每只都有、只有一叠"）在 `BotGenerator.PrepareAndGenerateBot` 的 postfix 里做
    /// —— 两层缺一不可，原因见 <see cref="BotLootStackDimension"/> 类注释。</para>
    /// </summary>
    private void ApplyBotLootStack(EventDef ev)
    {
        try
        {
            if (ev?.BotLootStack_ == null || ev.BotLootStack_.Count == 0) return;
            BotLootStackDimension.BotConfig = botConfig;
            BotLootStackDimension.BotTable = botTable;
            BotLootStackDimension.TemplateTable = templateTable;
            BotLootStackDimension.Log = m => { try { logger.Info(m); } catch { } };
            BotLootStackDimension.Warn = m => { try { logger.Warning(m); } catch { } };
            BotLootStackDimension.AddRestore = a => _restoreActions.Add(a);
            BotLootStackDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] bot 携带物品(数量)维度失败: {ex.Message}"); }
    }

    private void ApplyHideoutTrader(EventDef ev)
    {
        try
        {
            if (!ev.HideoutBuildSeconds.HasValue && !ev.HideoutCraftSeconds.HasValue
                && !ev.TraderPriceMultiplier.HasValue && !ev.TraderUpdateSeconds.HasValue
                && !ev.TraderPurchasesFir.HasValue
                && (ev.BotPresetBatch == null || ev.BotPresetBatch.Count == 0) && !ev.BotMaxCap.HasValue) return;
            HideoutTraderDimension.HideoutConfig = hideoutConfig;
            HideoutTraderDimension.TraderConfig = traderConfig;
            HideoutTraderDimension.BotConfig = botConfig;
            // ★★★ v2.6.12【P0 落点修正】秒建/秒造、全局价格倍率改为**直接改已加载的表**，
            //   所以这里必须把表和"货币判定器"一起交给维度层（旧代码只给 config 对象 —— 那正是空转的原因）。
            HideoutTraderDimension.HideoutTable = hideoutTable;    // 建造 = Areas[].Stages[].ConstructionTime
            HideoutTraderDimension.TraderTable = tradersTable;     // 价格 = Assort.BarterScheme[].Count
            HideoutTraderDimension.PaymentHelper = paymentHelper;  // 货币判定口径（与 SPT 同一实例）
            HideoutTraderDimension.Log = m => logger.Info(m);
            HideoutTraderDimension.AddRestore = a => _restoreActions.Add(a);
            HideoutTraderDimension.Apply(ev, logger);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 藏身处/商人维度失败: {ex.Message}"); }


    }
    /// <summary>
    /// ★★★ v2.6.44【P0 修复 · 用户实测「超人增益被你修没了」】
    ///
    /// <para><b>`stimulatorBuffsAdd` 与 `globals` 毫无关系</b> —— 它只往
    /// <c>globals → config.Health.Effects.Stimulator.Buffs</c> **新增一条自定义 buff 的"定义"**；
    /// 触发者是客户端插件（`fix/SPT-EventRotator.Client/ToxinInfection.cs:55` 的
    /// <c>SupermanBuff = "Buffs_EventToxinSuperhuman"</c>）⇒ **必须独立成方法**。</para>
    ///
    /// <para><b>踩的坑（原样记录，防再犯）</b>：用户裁决 Q2=C 撤掉"无限体力"后
    /// `june2022_toxin.globals` 变 `null` ⇒ 旧代码把它挂在 `ApplyGlobals` 的
    /// <c>if (ev.Globals == null || ev.Globals.Count == 0) return;</c> **之后**
    /// ⇒ `AddStimulatorBuffs` **永不执行** ⇒ `Buffs_EventToxinSuperhuman` 从未写进 globals
    /// ⇒ 客户端 `TryDoExternalBuff("Buffs_EventToxinSuperhuman")` 找不到这个名字
    /// ⇒ **超人对玩家"不存在"** ✗（**删掉一个字段 ⇒ 另一个无关功能跟着失效**）。</para>
    ///
    /// <para><b>验收判据</b>：<c>globals == null</c> 且 <c>stimulatorBuffsAdd</c> 有值
    /// ⇒ **buff 仍必须被正确写入** ✔</para>
    /// </summary>
    private void ApplyStimulatorBuffs(EventDef ev)
    {
        try
        {
            if (ev.StimulatorBuffsAdd == null || ev.StimulatorBuffsAdd.Count == 0) return;   // ★ 本字段**自己的**守卫
            // 这两个静态位原来由 ApplyGlobals 设；本方法**不依赖 globals** ⇒ 自己设（幂等，多设一次无害）
            GlobalsDimension.GlobalTable = globalTable;
            GlobalsDimension.TemplateTable = templateTable;
            GlobalsDimension.Log = m => logger.Info(m);
            GlobalsDimension.AddRestore = a => _restoreActions.Add(a);
            GlobalsDimension.AddStimulatorBuffs(ev.StimulatorBuffsAdd);
        }
        catch (Exception ex) { logger.Warning($"[EventRotator][兴奋剂Buff] 维度失败: {ex.Message}"); }
    }

    private void ApplyGlobals(EventDef ev)
    {
        try
        {
            if (ev.Globals == null || ev.Globals.Count == 0) return;
            GlobalsDimension.GlobalTable = globalTable;
            GlobalsDimension.TemplateTable = templateTable;   // ★ v2.3.1 真正的物品数据库
            // ★ v0.1.3 口渴/能量速率：直接用 GlobalTable.Configuration.Health（与注入的 HealthConfig 同一实例，
            //   这样不用改构造参数）
            try
            {
                var gcfg = globalTable?.GetType().GetProperty("Configuration")?.GetValue(globalTable);
                GlobalsDimension.HealthConfig = gcfg?.GetType().GetProperty("Health")?.GetValue(gcfg);
            }
            catch { GlobalsDimension.HealthConfig = null; }
            GlobalsDimension.Log = m => logger.Info(m);
            GlobalsDimension.AddRestore = a => _restoreActions.Add(a);
            GlobalsDimension.Apply(ev, logger);
            // ★★★ v2.6.44：`AddStimulatorBuffs` **已挪出本方法** ⇒ 见 `ApplyStimulatorBuffs(ev)`（套用循环里与
            //   本方法**平级**调用）。原因：它属 `stimulatorBuffsAdd` 字段、与 `globals` 无关，
            //   挂在这里会被上面的 `if (ev.Globals == null …) return;` 挡死
            //   ⇒ 用户撤掉 `globals` 后「超人增益」整条消失 ✗（用户实测抓到）。
        }
        catch (Exception ex) { logger.Error($"[EventRotator] globals 维度失败: {ex.Message}"); }
    }
    private void ApplyAirdrop(EventDef ev)
    {
        try
        {
            if ((ev.AirdropWeight == null || ev.AirdropWeight.Count == 0)
                && (ev.AirdropAddLoot == null || ev.AirdropAddLoot.Count == 0)
                && !ev.AirdropFrequencyMultiplier.HasValue
                && string.IsNullOrWhiteSpace(ev.AirdropForceType)) return;
            AirdropDimension.Config = airdropConfig;
            AirdropDimension.LocationTable = locationTable;
            AirdropDimension.Log = m => logger.Info(m);
            AirdropDimension.AddRestore = a => _restoreActions.Add(a);
            AirdropDimension.Apply(ev, logger);
            // ★ v0.1.3 空投潮（频率）：改每张图 AirdropParameters（含把 6 人门槛压到 1）
            if (ev.AirdropFrequencyMultiplier.HasValue)
                AirdropDimension.ApplyFrequency(ev.AirdropFrequencyMultiplier.Value, logger,
                                               (ev.AirdropFrequencyMaps != null && ev.AirdropFrequencyMaps.Count > 0)
                                                   ? ev.AirdropFrequencyMaps : null);
        }
        catch (Exception ex) { logger.Error($"[EventRotator] 空投维度失败: {ex.Message}"); }
    }
    private static double GetNum(object o, string p) { var x = o.GetType().GetProperty(p); if (x == null) return 0; try { return Convert.ToDouble(x.GetValue(o) ?? 0); } catch { return 0; } }
    private static bool GetBool(object o, string p) { var x = o.GetType().GetProperty(p); if (x == null) return false; try { return Convert.ToBoolean(x.GetValue(o) ?? false); } catch { return false; } }
    private static void SetNum(object o, string prop, double v)
    { var p = o.GetType().GetProperty(prop); if (p == null || !p.CanWrite) return; try { p.SetValue(o, Convert.ChangeType(v, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)); } catch { } }
    private static void SetBool(object o, string prop, bool v)
    { var p = o.GetType().GetProperty(prop); if (p == null || !p.CanWrite) return; try { p.SetValue(o, v); } catch { } }
}





