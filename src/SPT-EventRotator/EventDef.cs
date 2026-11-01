using System;
using System.Collections.Generic;

namespace EventRotator;

/// <summary>
/// 单个活动定义（events.json 的一项）。
/// 只包含「可完美复刻」的维度：首领刷新、爆率、空投、商人、藏身处、活动任务、季节活动。
/// </summary>
public class EventDef
{
    public string Id { get; set; } = "";

    // 中文名 / 英文名（用于日志与发布）
    public string NameZh { get; set; } = "";
    public string NameEn { get; set; } = "";

    // 日期窗（现实日期，跨年用 startMonth>endMonth 表示）。0 = 不定。
    public int StartMonth { get; set; }
    public int StartDay { get; set; }
    public int EndMonth { get; set; }
    public int EndDay { get; set; }

    /// <summary>
    /// ★★★ 2026-10-24【用户要求：把新做的 4 个活动加进轮换，按原本轮换规则来】
    /// **即使有日期窗，也把它放进自动轮换候选池**（默认 false = 完全旧行为，其余 87 个活动一个都不变）。
    ///
    /// <para><b>为什么需要它</b>：`rotation.pool` 为空时的既有规则是
    /// 「无日期窗（`StartMonth &lt;= 0`）才进池」，另外 `seasonRotation.mode == "rotate"`（= 实机现状）时
    /// `event_task` 也不看日期窗（`Rotation.cs:209` 的 v2.6.42 豁免）。而 `event_ai` **没有**这条豁免 ⇒
    /// 有日期窗的 AI 活动（如 `meeting_of_the_bosses_2026`，3/12~3/19）永远进不了池，
    /// 只能靠日期窗/手动开。</para>
    ///
    /// <para><b>为什么不直接把 `rotation.pool` 写成白名单</b>：`pool` 非空 = **只轮这些** ⇒
    /// 必须把「原本空池时的全部候选」手抄进去，等于把候选池**冻结**（以后新增/改窗活动不会自动进池）；
    /// 而且实机 `config.json` 是用户自己的配置（打包工具是原样带走 `SPT_Runtime\user\*`），
    /// 改工作区里的 config 默认值**不会**同步到实机 ⇒ 得让用户手改配置才生效。</para>
    ///
    /// <para><b>为什么不做"`event_ai` 全部豁免"（照抄 event_task 那条）</b>：那会把 22 个带日期窗的
    /// AI 活动一次性塞进池 ⇒ AI 桶从 2 个变 24 个，老 AI 活动（`bosses_home_generic` /
    /// `all_bosses_shoreline`）出档率被稀释 ~92%，正是用户说的"把别的活动挤出轮换"。
    /// 本字段**只在显式标注的活动上生效**（当前 = 4 个新活动）。</para>
    ///
    /// <para><b>边界（护栏）</b>：① 只影响轮换**候选资格**，不改日期窗自动开启（有窗的活动照旧在
    /// 自己窗口内由 `EventEngine.cs` 的日期窗分支自动开，两边独立）；② `event_ai` 也好 `event_task`
    /// 也好，都**不能**靠它进池的是 `seasonal` —— `CandidatePool()` 里 `!IsSeasonal(e)` 在**最前面**
    /// 短路，季节活动永远只走季节系统；③ 只在 `rotation.pool` **为空**时参与——池子非空时以白名单
    /// 为准（白名单没写它就不轮）。</para>
    ///
    /// <para><b>可复跑验证</b>：`tools/selftest_v014` 的「轮换候选池（alwaysInRotation）」段
    /// （断言 before→after 的候选差集**恰好**只有 `meeting_of_the_bosses_2026`，
    /// 即老活动一个都没被挤掉）+ `tools/rotation_pool_probe`（打印候选清单与 12 档 before/after 模拟）。</para>
    /// </summary>
    public bool AlwaysInRotation { get; set; }

    // ★ v0.1.3 移除：原来的 `Enabled` 字段**实际不起作用**（实测）：
    //   · EventEngine 的日期窗分支读它，但**轮换池完全不看**（只判 StartMonth<=0）
    //   · 客户端 F12 的勾选列表来自服务端状态文件，也不读它
    //   ⇒ 43 个 Enabled=false 的活动照样会被自动轮换选中，属误导性数据，已从 events.json 删除。
    //   现在区分方式只有一条、且无歧义：**有日期窗 = 按现实日期自动开；无日期窗 = 进自动轮换池**。
    //   （2026-10-24 补：**例外**只有显式 `alwaysInRotation: true` 的活动 —— 它们有窗也进轮换池，
    //     见上面 `AlwaysInRotation`。）

    // ---- 复刻动作 ----

    /// <summary>首领追加/提升：mapKey -> 该图要 100% 刷的首领 bot 类型列表（如 bossBully）。只追加，不清空。</summary>
    public Dictionary<string, List<string>> BossSpawns { get; set; } = new();

    /// <summary>把某图已有的指定 boss 概率设为指定值（如 {"customs":{"bossBully":100}}）。</summary>
    public Dictionary<string, Dictionary<string, int>> BossChance { get; set; } = new();

    /// <summary>爆率倍数：loose/static 乘子（作用于全图或指定图）。</summary>
    public float? LooseLootMultiplier { get; set; }
    public float? StaticLootMultiplier { get; set; }
    public List<string> LootMaps { get; set; } = new();

    /// <summary>强制季节（1=Autumn,2=Winter,3=Spring,4=AutumnLate,5=SpringEarly），0=不改。</summary>
    public int ForceSeason { get; set; }

    /// <summary>季节活动类型：None/Christmas/Halloween/NewYears/Promo/AprilFools。</summary>
    public string SeasonalEvent { get; set; } = "";

    /// <summary>商人解锁到最高忠诚等级。</summary>
    public bool UnlockTraders { get; set; }

    // ★ v0.1.3 移除死字段 `TraderDiscount`（float, 1.0=不打折）：
    //   它在 C# 里有属性，但**代码里没有任何实现**、events.json 里**0 个活动**使用 —— 完全无效。
    //   商人折扣现在统一走 `TraderPriceMultiplier`（语义已修正为"最终价格倍率"，0.5 = 五折）。

    /// <summary>跳蚤市场最低等级（99 ≈ 关闭）。null = 不改。</summary>
    public int? FleaMinLevel { get; set; }

    // ★ 移除死字段 `InstantHideout`（bool）：服务端 **0 处读取**（grep `\.InstantHideout` 0 命中），
    //   当前 events.json 里 **0 个活动**使用 —— 绑上也不会有任何效果。
    //   真正生效的是下面的 `HideoutBuildSeconds`（HideoutTraderDimension 热更 `OverrideBuildTimeSeconds`，
    //   见 HideoutTraderDimension.cs:74/79）。

    /// <summary>技能/经验加成（>1 加速）。null = 不改。</summary>
    public float? SkillMultiplier { get; set; }
    public float? ExpMultiplier { get; set; }

    // ═══ ★★★ v0.1.5 第 1 组补齐：照官方组合表逐项补（反编译 SeasonalEventService 逐字对应）═══
    /// <summary>⑨ 南瓜：官方 `AddPumpkinsToScavBackpacks()` —— `Types["assault"].Backpack[pumpkin]=400`（万圣节/愚人节）。</summary>
    public bool? PumpkinScavBackpack { get; set; }
    /// <summary>⑩ 比特币改名：官方 `RenameBitcoin()` —— locale 里 59faff1d… 改为 Physical SPT Coin / 0.2SPT（愚人节）。</summary>
    public bool? RenameBitcoin { get; set; }
    /// <summary>⑪ 万圣节兴奋剂 Buff：官方 `DefaultStimulatorBuff = "Buffs_Halloween"`。</summary>
    public bool? HalloweenStimBuff { get; set; }
    /// <summary>⑫ Zryachiy 近战率：官方 `AdjustZryachiyMeleeChance()` —— bosszryachiy 的 Scabbard 概率 → 100。</summary>
    public bool? ZryachiyMeleeChance { get; set; }

    // ═══ ★★★ v0.1.5 第 2~4 组：照官方"组合表"读 seasonalEventConfig 的数据 ═══
    /// <summary>
    /// ⑬ 官方 `AddEventGearToBots(&lt;key&gt;)`：直接读 `seasonalevents.json → eventGear[key]`
    /// （形状 `{botType:{槽位:{tpl:概率}}}`，与我们的装备注入**同构** ⇒ 复用 ApplyBotEquipment）。
    /// 愚人节官方同时上 `halloween` + `christmas` 两套。
    /// </summary>
    public List<string> OfficialGear { get; set; }
    /// <summary>
    /// ★★★ v2.6.39【用户裁决 · opt-in】**保住首领标志性装备**（默认 **关** = 完全照官方）。
    ///
    /// `true` ⇒ 官方装备注入（`officialGear`）对 **`boss*` / `follower*` 角色**的
    /// **`FaceCover` / `Headwear`** 槽**整槽跳过写入**（连"概率抬到 75"一起跳）——
    /// 例：`bossknight` 的「Death Knight面具」不会被官方圣诞装备顶掉。
    ///
    /// 不写 / `false`（**默认，88 个活动当前都没写**）= **照官方行为**：官方 `AddEventGearToBots`
    /// （`SeasonalEventService.cs:948`）是"往池子里直接赋值、**不清理原条目**"⇒
    /// `bossknight.FaceCover` 变成 `{Death Knight面具: 1, 假白胡子: 1500}` ⇒ 面具命中率 1/1501 ≈ 0.067%
    /// （**官方数据与官方代码的固有结果，不是本 mod 偏差**；核查报告 `audit/骑士面罩-核查.md`）。
    ///
    /// 两条注入路径都认这个开关：`ExtraDimensions.ApplyBotEquipment` 与 `PbsCompat.ApplyEventGear`。
    /// </summary>
    public bool? EventGearKeepBossIconic { get; set; }
    /// <summary>⑭ 官方 `AddEventLootToBots(&lt;key&gt;)`：读 `eventLoot[key]`（形状同上，落点 inventory.items）。</summary>
    public List<string> OfficialLoot { get; set; }
    /// <summary>⑮ 官方 `AdjustBotAppearanceValues(&lt;key&gt;)`：读 `botAppearanceChanges[key]`（`{botType:{body|feet:{tpl:权重}}}`）写进 bot 类型 `appearance` 段。</summary>
    public List<string> BotAppearance { get; set; }

    /// <summary>
    /// ★★★ v2.6.14【按 bot 类型直接覆盖外观】**mod 自定义维度**（官方没有对应机制）。
    /// <para>形状：`{ "bot类型": { "body|feet|head|hands|voice": "外观定制 id" } }`。</para>
    /// <para>落点：`BotTable.Types[botType].Appearance[part]` —— 注意 SPT 的前缀陷阱：
    /// JSON 里的 `appearance` 在 CLR 模型里是 **`Appearance`/`BotAppearance`**（与 `health→BotHealth`、
    /// `inventory→BotInventory` 同一套路）。</para>
    /// <para>语义：**整体替换**该部位的字典内容（外观字典通常只有 1 个条目），
    /// 原条目进快照，还原时精确放回。</para>
    /// <para>⚠ 用途说明（为什么需要它）：`ravangezryachiyevent`（复仇小鹿）在本体数据里
    /// `appearance.body/feet/head` 用的是**普通 SCAV 通用外观**
    /// （`wild_body_1` / `wild_feet_wasatch` / `wild_head_1` —— 分别被 9/11/24 个 bot 类型共用），
    /// 而不是小鹿外观（`Top_BOSS_Zryachi` / `Pants_BOSS_Zryachi` / `Head_BOSS_Zryachi`，各只被
    /// `bosszryachiy` 一个用）。**这是官方数据行为**（文件 mtime 与全库一致、未被任何 mod 改过；
    /// 官方 wiki 的 `===Zryachiy Equipment===` 也只有 Headwear/Face cover 两栏标注
    /// 「Only on Vengeful Zryachiy」，没有身体/裤子栏）。
    /// 用户 2026-10-16 明确要求**覆盖成本体小鹿外观**（"背离原始数据但符合直觉"）⇒ 用本维度实现，
    /// 并在 `devNote` 里标注"与本体数据不符、是 mod 取值"。</para>
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> BotAppearanceOverride { get; set; }

    // ═══ ★★★ v0.1.5 完全还原同步 A~D（依据反编译 SeasonalEventService 的方法体逐行）═══
    /// <summary>A 官方 `AddGifterBotToMaps()`：读 `gifterSettings`（10 图 + 每图 spawnChance + zones），
    /// 把 gifter 的 BossChance 设为官方概率、BossZone 设为官方 zones（没有该条目就按官方字段新建）。</summary>
    public bool? GifterOfficial { get; set; }
    /// <summary>B 官方 `christmasLootBoostAmount`：松散战利品概率按 `p + (1-p)*boost` 提升（圣诞=0.03）。</summary>
    public double? ChristmasLootBoost { get; set; }
    /// <summary>C 官方 `ChangeBtrToTarColaSkin()`：BTR 皮肤 → "Tarcola"（Woods / TarkovStreets）。</summary>
    public bool? BtrTarcolaSkin { get; set; }
    /// <summary>D 官方 `EnableRunnansEvent()`：`RunddansSettings.Active = true` 且 `ActivePVE = true`（圆环舞真开关）。</summary>
    public bool? RunddansActive { get; set; }

    /// <summary>E 官方 `GivePlayerSeasonalGifts`：给玩家发节日礼物（Christmas⇒Christmas2022；NewYears⇒NewYear2023+NewYear2024）。</summary>
    public List<string> PlayerGift { get; set; }

    /// <summary>要开启的活动任务 id（留空 = 由 eventQuests 日期窗自然生效）。</summary>
    public List<string> EnableQuests { get; set; } = new();

    // ══════════════════════════════════════════════════════════════════════════════════
    // ★ 2026-10-30【万圣节感染者（僵尸）】—— 实现见 <see cref="SeasonalZombieDimension"/>
    //   当年放弃是因为「僵尸不挥刀」；客户端 SainZombieMeleeFix（PPES 1.4.0 同款）已上线，故重做。
    //   总开关沿用既有 <see cref="NativeZombies"/>（v0.1.5 已存在）；以下 4 个是本维度新增的细粒度开关。
    // ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>逐图把 `zombieSettings.mapInfectionAmount` 的**下限**提上来（<see cref="ZombieOutbreakDef"/>）。</summary>
    public ZombieOutbreakDef ZombieOutbreak { get; set; }

    /// <summary>
    /// `true` ⇒ 把 5 类 Scav 角色（assault/marksman/cursedassault/assaultgroup/crazyassaultevent）的
    /// `BotExperience.StandingForKill` **负值清零**（正值不动），活动关闭时逐字还原。
    /// </summary>
    public bool? NoKarmaLoss { get; set; }

    /// <summary>把 `eventBossSpawns` 里 `infectedTagilla` 且原概率 `&lt;= 0` 的条目改成该值（默认 12）。</summary>
    public double? InfectedTagillaChance { get; set; }

    /// <summary>操作哪条季节条目（默认 `halloween`）。</summary>
    public string ZombieSeasonalKey { get; set; }

    /// <summary>
    /// ★ 2026-10-30【第 12 项】事件限定物品的**获取途径**：往指定 bot 角色的**背包**物品池里塞指定 tpl。
    /// <para>数据形状：</para>
    /// <code>
    /// "factionLoot": {
    ///   "arenaFighterEvent": {
    ///     "ensureBackpack": true,                 // 装备概率里 Backpack = 100（克隆源经常没背包，而货只往背包放）
    ///     "items": { "6740987b89d5e1ddc603f4f0": 0.3,
    ///                "67408903268737ef6908d432": 0.3,
    ///                "67449b6c89d5e1ddc603f504": 0.2 }   // tpl → share(0~1)
    ///   }
    /// }
    /// </code>
    /// <para>实现见 <see cref="FactionLootInject"/>：按 PPES `Quests/EventQuests.cs` 的 `AddShare` 公式换算权重，
    /// 写进 `BotType.BotInventory.Items.Backpack`；快照/还原逐字回放。</para>
    /// </summary>
    public Dictionary<string, FactionLootDef> FactionLoot { get; set; } = new();

    /// <summary>
    /// ★ 2026-10-30【第 9 项】放开"假等级门槛"（`"relaxLevelQuests": true`，**默认 false**）。
    /// <para>背景（实机 + 本机 quests.json 实证）：本体有任务带**不可达**的等级门槛 ——
    /// 如 `Import Control`（`668bcccc167d507eb01a268b`，Mechanic）的 `AvailableForStart` 里
    /// `Level >= 99`，而等级上限只有 79 ⇒ 这条任务永远接不到，连带 `What's Your Evidence?` /
    /// `Caught Red-Handed` / 小本生意的风险 / 保护利益 / 西方质量标准 / 久经考验 整条链
    /// 在任务面板里完全不出现。</para>
    /// <para>语义：**只处理本活动 `enableQuests` 明确声明的任务**（不走传递闭包，保守）；
    /// 把它们的 `Conditions.AvailableForStart` 里 `conditionType == "Level"` 的条件值临时改成 1，
    /// 并快照原值登记还原（活动关闭/切走时逐字还原）。</para>
    /// </summary>
    public bool RelaxLevelQuests { get; set; }

    /// <summary>
    /// 【与 ABPS 兼容】活动激活时，让 ABPS 把这些 bot 在这些地图上 100% 刷：
    ///   { "arenaFighterEvent": ["bigmap", "woods"], "exUsec": ["shoreline"] }
    /// 实现方式：改 ABPS 的 BossConfig[boss].SpawnChance[map] = 100（它自己持有正确 bossZone 与随从模板），
    /// 活动切换/关闭时还原原值。这样两边不打架 —— ABPS 负责刷，本 mod 负责骷髅标记。
    /// ABPS 不存在时静默跳过。
    /// </summary>
    public Dictionary<string, List<string>> ForceAbpsSpawns { get; set; } = new();

    /// <summary>
    /// ★★★ A1 修复（task-26）【与 ABPS 兼容】活动激活时，把这些图的**普通 SCAV 生成压到 0**：
    ///   [ "factory4_day" ]   或   [ "bigmap", "factory4_day" ]
    ///
    /// **为什么必须有这个字段**：本 mod 原先用原版 `location.Base.MinMaxBots` 写 `[0,0]` 来压 SCAV ——
    /// 但那是**空转**：客户端 `MinMaxBots` 只读 `.min`、`CheckMinCounts` 只升不降、`max` 全库 0 读取；
    /// 而装了 ABPS 后它还会 `RemoveExistingWaves()` + 用自己的 `_scavMapCache` **整体重写**
    /// `location.Base.Waves`。
    ///
    /// **落点（★ 用户硬约束：不动别的 mod，只改我们自己的）**：在**我们已有的 D2 postfix** 里
    /// （ABPS 写完 `Base.Waves` **之后**），把该图 `Base.Waves` 中 `WildSpawnType` 为
    /// `assault` / `marksman` 的条目**删掉**，并把 SPT 自己的
    /// `BotLocationModifier.NonWaveSpawnBotsLimitPerPlayer(PvE)` 压 0。
    /// **只写 SPT 的 DB 对象，不碰 ABPS 的任何文件/对象**。实现见 <see cref="EventScavSuppress"/>。
    /// ABPS 不存在时走 `SuppressAllDbBases()` 兜底（那时 D2 的 hook 挂不上，但原版 `Base.Waves` 同样要压）。
    ///
    /// **可还原**：活动关闭/切换后不再压（`ClearRound()`），下一次 ABPS 重建
    /// （每局结束 / 启动 / PBS 保存）会把 `Base.Waves` 整体换回它自己的内容 ⇒ 天然还原。
    ///
    /// **覆盖范围（因为我们直接删 `Base.Waves` 的条目，三条路径里前两条一次覆盖）**：
    ///   ① 开局固定 SCAV（`GenerateStartingScavs` → `Base.Waves`）⇒ 条目被删
    ///   ② 迟到入局波次（`GetLateStartMapData` → `Base.Waves`）⇒ 条目被删
    ///      ★ 比"改 `maxBotSpawns`"更彻底：**ABPS 剩余时间 &lt;30 分钟时走硬编码 `GetInt(5,8)/(3,5)/(2,3)`，
    ///      那条路不看 `maxBotSpawns`** —— 而我们删的是它最终生成的表，所以一并覆盖。
    ///   ③ 非波次持续生成（客户端 `NonWavesSpawnScenario`，被 ABPS 客户端补丁改写）⇒ **不读 `Base.Waves`**，
    ///      改由 `BotLocationModifier.NonWaveSpawnBotsLimitPerPlayer(PvE)` 压 0 处理（同一个字段名，
    ///      也是 SPT 自己的 DB 字段）。⚠ 若 ABPS 的 BepInEx 客户端配置另行覆盖该上限，则那条仍可能漏 —— 按约束我们不碰。
    /// </summary>
    public List<string> ScavSpawnSuppress { get; set; } = new();

    /// <summary>
    /// ★★★ 清场小队装备差异化（task-33）：**按 bot role 升级生成产出物的装备**。
    /// 写法：<c>{ "arenaFighterEvent": { "betterAmmo": true, "betterPlates": true, "weaponTpl": "…" } }</c>
    ///
    /// <para>落点 = 我们已有的 <c>BotGenerator.PrepareAndGenerateBot</c> postfix
    /// （`Patches/BossWavePatch.cs` 的 `BotGenerated`），改写生成好的 <c>BotBase.Inventory.Items</c>。
    /// 为什么必须这样：装备生成被 APBS（`acidphantasm-progressivebotsystem`）的
    /// `GenerateInventoryPatch`（`BotInventoryGenerator.GenerateInventory` 的 Prefix）接管，
    /// 改 SPT 的权重池不保证生效。实现见 <see cref="EventGearUpgrade"/>。</para>
    ///
    /// <para>判据全部来自本机 `items.json` 数据字段（弹药：同口径 <c>PenetrationPower</c> 最高；
    /// 插板：该槽 filter 内 <c>ArmorType</c>/<c>MaxDurability</c>/<c>BluntThroughput</c> 最优），不凭名气。</para>
    ///
    /// <para><b>"可还原"语义</b>：产出物每局一次性生成，无对象可回滚 ⇒ 还原 = **活动关掉后不再改**
    /// （每轮 `ClearRound()` 清空规格），与 D2 / <c>EventScavSuppress</c> 一致。</para>
    /// </summary>
    public Dictionary<string, BotGearUpgradeDef> BotGearUpgrade { get; set; } = new();

    /// <summary>
    /// ★★★ 补全 bot 模板缺失项（task-34）：把**本体 `bots/types/&lt;role&gt;.json` 里缺的**
    /// 口径→弹药 / 装备槽→物品，补进**运行期 DB 对象**（`BotTable.Types[role].Inventory`），**不写盘**。
    /// 写法：<c>{ "ravangezryachiyevent": { "ammo": { "Caliber9x21": "5a26abfa…" },
    /// "equipment": { "Holster": { "59f98b49…": 1 } } } }</c>
    ///
    /// <para><b>为什么需要</b>：实例 —— <c>ravangezryachiyevent</c>（复仇小鹿）缺 SR-1MP 与 `Caliber9x21`，
    /// 而官方 wiki（`events_wiki.txt:1049-1097`）的装备表里 SR-1MP 属于**复仇小鹿那套**
    /// （`colspan` 横幅 `Only on Vengeful Zryachiy`（`:1093`）只覆盖它**下面**那行 = 邪教徒之刃），
    /// 普通 `<c>bosszryachiy.json</c>` 反而**有** SR-1MP 与 `Caliber9x21`。</para>
    ///
    /// <para>⚠ **光补弹药没用** —— 模板里连 9x21 的枪都没有，所以本维度同时支持补 <c>equipment</c>。</para>
    ///
    /// <para><b>读点</b>：原版 SPT <c>BotWeaponGenerator.GetWeightedCompatibleAmmo</c>
    /// （`BotWeaponGenerator.cs:446-449`）读的就是 `BotType.Inventory.Ammo` ⇒ 写它会被读到。
    /// 而 APBS 走它自己的 <c>Data/Ammo/Tier*_ammo.json</c>（`GetAmmoByBotRole`）—— 但它
    /// **不接管本 role**：`BotActivityHelper.IsBotDisabled` 里 <c>if (_events.Contains(botType)) return true;</c>
    /// ⇒ 事件 bot 一律 disabled ⇒ 回落原版。实现见 <see cref="EventBotTemplateFix"/>。</para>
    /// </summary>
    public Dictionary<string, BotTemplateFixDef> BotAmmoAdd { get; set; } = new();

    /// <summary>
    /// 【敌对性】活动激活时，给这些 bot 角色补/改地图的 AdditionalHostilitySettings，
    /// 让它们**真的打玩家**（否则新敌种在地图敌对表里没有条目 → 默认中立 → 站着不动）。
    ///   { "arenaFighterEvent": ["bigmap","woods"], "exUsec": ["lighthouse"] }
    /// 补的条目：对玩家三派系(PMC USEC/BEAR/SCAV)行为 = Enemies，并把互打关系补上。
    /// </summary>
    public Dictionary<string, List<string>> ForceHostility { get; set; } = new();

    /// <summary>
    /// ★★★ v0.1.5【结盟】让图上两个角色**互为友军**（官方例：The Goons 要 **guard** Sanitar，
    /// 而实测默认关系是**互相开火** ⇒ 必须显式结盟）。
    /// 落点：`mapBase.BotLocationModifier.AdditionalHostilitySettings`（与官方 `hostilitySettingsForEvent` 同结构）。
    /// </summary>
    public List<AllyPairDef> ForceAllyPairs { get; set; } = new();

    /// <summary>
    /// ★ 活动期间「老家不刷」：把这些首领在它们的老家地图上的刷新概率压到 0。
    ///   { "bigmap": ["bossBully"], "woods": ["bossKojaniy"], "shoreline": ["bossSanitar"] }
    ///
    /// 用途：像「八影会谈」这种"全首领全聚集到一张图"的活动，
    ///   官方设定是首领**不再出现在各自老家**（否则既在海关又在老家 = 不符合原活动）。
    ///
    /// 实现要点：
    ///   · 只压 <c>BossChance = 0</c>，**不删除条目**（第三方 mod 可能持有该条目引用，
    ///     删了会引发它们的状态错乱；压 0 是幂等且可还原的）。
    ///   · 原始概率通过既有的 <c>_dbSnap</c> 快照机制保存，换活动/关闭活动时会自动还原。
    ///   · ABPS 兼容：不使用通配符，只精确命中列出的 (地图, 首领) 组合。
    /// </summary>
    public Dictionary<string, List<string>> ZeroBossSpawns { get; set; } = new();

    /// <summary>
    /// 【测试用】给某图某敌种强制指定刷点（zone）：
    ///   { "woods": { "arenaFighterEvent": "ZoneScavBase2" } }
    /// 不填则用模板自带的 zone（来自 ABPS 的 Defaults/Bosses.json）。
    /// 用途：想把活动单位拉到固定、好找的刷点做验证。
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ForceZone { get; set; } = new();

    /// <summary>
    /// 【测试用】把活动敌种在每张图上**额外多刷几处**（总条目数 = 1 + ForceExtraSpawns）：
    ///   { "arenaFighterEvent": 4 }
    /// 用途：把刷新拉到极端，方便验证"到底刷没刷 / 打不打人"。
    /// 配合 ForceZone 可把多处集中在同几个刷点。
    /// </summary>
    public Dictionary<string, int> ForceExtraSpawns { get; set; } = new();

    /// <summary>
    /// 【测试用】追加条目时用**另一个角色名**生成（条目仍归属本活动的敌种）：
    ///   { "arenaFighterEvent": "arenaFighter" }
    /// 用途：对照实验 —— 判断"不打人"是该角色本身的问题还是别的原因。
    /// </summary>
    public Dictionary<string, string> ForceBossName { get; set; } = new();

    /// <summary>
    /// ★ 从 SPT 官方 `SPT_Data/configs/seasonalevents.json` 的 `eventBossSpawns[&lt;此键&gt;]` **原样复制**首领条目。
    ///   例："halloweennightcult" → 官方邪教之夜：
    ///     BossName=sectantPredvestnik，BossEscortType=exUsec，Supports=[sectantOni×1, sectantPrizrak×1]
    ///   好处：直接用官方数据（含 Supports 等复杂字段），不需要猜任何模板。
    ///   ★ 覆盖的地图**完全照官方**（halloweennightcult 只有 shoreline/woods/bigmap）——
    ///     不额外扩展到别的图，保持与原版一致。
    /// </summary>
    public string ForceSeasonalBossKey { get; set; } = "";

    /// <summary>
    /// ★ v0.1.5 **原生僵尸模式**：本活动生效时，把 SPT 官方 `seasonalevents.json → halloween.settings.zombieSettings.enabled`
    ///   打开（= 让官方 `SeasonalEventService.ConfigureZombies` 跑起来）。
    ///
    /// 官方这段代码干三件事（反编译实证）：
    ///   ① `locationInfection[map] = mapInfectionAmount[map]` + `Base.Events.Halloween2024.InfectionPercentage = 值`
    ///      → 官方分布：laboratory **100%**、factory4 50、Woods 60、Shoreline 50、RezervBase 50、Lighthouse 50、
    ///        TarkovStreets 50、Sandbox 25、bigmap 0、Interchange 0；
    ///   ② `foreach (disableBoss in DisableBosses) GetLocation(disableBoss).Base.BossLocationSpawn = 新空表`
    ///      → 官方 `disableBosses: ["laboratory"]`，**直接把实验室首领表清空**（锤哥、邪教祭司一并消失）；
    ///   ③ `AddEventBossesToMaps("halloweenzombies", 有感染的地图)` → 官方表里 laboratory 有 **29 条**
    ///      （infectedLaborant×14 + infectedPmc×15）。
    ///
    /// ⚠️ 注意：这是**启动时一次性**的（和藏身处秒建同类），关掉活动不会把已清空的实验室首领表还原，需重启服务端。
    /// <para>★ 2026-10-30：`settings.zombieSettings.enabled` 与感染率下限 / infectedTagilla 概率 / Scav 业力
    /// 改由 <see cref="SeasonalZombieDimension"/> 统一快照与还原（活动关闭/切走逐字回原值）。</para>
    /// </summary>
    public bool? NativeZombies { get; set; }

    /// <summary>
    /// ★ v0.1.5 新增：**原生圣诞复刻**（对应官方 `ApplyChristmasEvent`）。
    /// 官方那段只由 `CacheActiveEvents()/UpdateGlobalEvents()` 按**现实日期**触发（窗 12/13–12/31、1/1、1/2–1/14）
    /// ⇒ 窗外用 force.txt/F12 强制开启圣诞活动时，`ApplyChristmasEvent` 整段不跑：
    ///   · `globalConfig.EventType.Add(EventType.Christmas)` ← 客户端圣诞信号（UI/氛围）
    ///   · `botTable.Core.ActivePatrolGeneratorEvent = true`
    ///   · `EnableSanta` ⇒ `AddGifterBotToMaps()`（圣诞老人 gifter 进各图）+ `AddLootItemsToGifterDropItemsList()`
    ///   · `EnableDancingTree()`（藏身处圣诞树）
    /// 本 mod 复刻前两项（可还原）；圣诞老人的**刷新**用活动里的 `bossSpawns`/`bossChance` 直接下发 `gifter`；
    /// 礼物清单与跳舞树不实现（礼物清单是 SPT 内部列表，落点未逐字核实）。
    /// </summary>
    public bool? NativeChristmas { get; set; }

    /// <summary>
    /// ★★ v0.1.5【白天入夜】—— 官方 2022 万圣节的官方内容之一："白天突然入夜"。
    ///
    /// 0~1 的概率：`1.0` = 活动期间每局都入夜；`0.5` = 每局按 50% 掷（还原官方"突然"的感觉）；
    /// 不写或 0 = 不干预（原版昼夜）。落点：`MatchController.ConfigureOfflineRaid` 的入参
    /// `request.IsNightRaid`，在 prefix 里翻成 true（SPT 随后按它选天气/时间变体）。
    /// </summary>
    public double? ForceNightRaid { get; set; }

    /// <summary>
    /// ★★ v0.1.5【诅咒标签 Tagged and Cursed】—— 官方 2022 万圣节内容之一。
    ///
    /// `true` = 本活动期间**默认开启**该离线局开关：全图 SCAV 波次换成 `cursedAssault`（疯狂 SCAV，
    /// 集体冲锋 + 死追玩家）。客户端玩家可在设置界面自行取消。
    /// 落点：`InRaidConfig.RaidMenuSettings.TaggedAndCursed` → 由 `/singleplayer/settings/raid/menu`
    /// 下发到 `EFT.WavesSettings.IsTaggedAndCursed`（SPT 自带插件 spt-custom 负责写入）。
    /// **打开离线局设置界面即生效，不需要重启客户端**（与 NativeZombies 不同）。
    /// ⚠ 与「SCAV 友好信号弹」语义冲突，不建议同一活动同开。
    /// </summary>
    public bool? TaggedAndCursed { get; set; }

    /// <summary>
    /// ★★★ v2.6.8【官方「完整版万圣节模式」—— 所有白图变成电闪雷鸣的黑夜】
    ///
    /// `true` = 打开 `globals.config.EventSettings.EventActive`，一次性激活官方整套万圣节模式：
    ///   ① 客户端在**除 `LocationsToIgnore` 外的所有地图**实例化 `Prefabs/HALLOWEEN_CONTROLLER`
    ///      （`LocationsToIgnore` 官方默认 = `factory4_day / factory4_night / hideout / laboratory`
    ///        ⇒ 这就是视频说的「**所有白图**」）；
    ///   ② 世界钟在 `WeatherChangeTime` 秒内**加速扫到 `EventWeather.Hour`**（本机为 21:00），
    ///      完成后 `LockCurrentTime = true` ⇒ **进图即黑夜**；
    ///   ③ 天气切到 `EventWeather` = `Cloudness 1`（全阴）+ `Rain 0.1`（雨）+ `Wind 4`（大风）
    ///      + `ScaterringFogDensity 0.02` ⇒ **电闪雷鸣的黑夜**；
    ///   ④ HUD 出现**活动计时器**（`EventTime` 秒）；
    ///   ⑤ 顺带 `ExitTimeMultiplier 3` / `StaminaMultiplier 3`（官方事件值）。
    ///
    /// **客户端反编译实证**：`audit/client_full/Assembly-CSharp.decompiled.cs`
    ///   `:556831`（`GlobalConfiguration.EventSettings`）、`:555704`（`EventGlobalSettings` 字段全集）、
    ///   `:668717/:670038/:671607`（三处地图加载判定）、`:568290`（读 `EventWeather`）、
    ///   `:568394 → :568447`（`EnableWeatherEvent`）、`:165971`（`WeatherEventController`）、
    ///   `:941764`（HUD 计时器）。
    ///
    /// ⚠ **与 `forceNightRaid`（入夜）的区别**：入夜只改 `/client/weather` 的 `time`，是给**邪教徒**类
    ///   活动用的；本字段是官方**万圣节专属**的完整模式（控制器 + 天气 + 计时器 + 倍率），两者不要混用。
    /// </summary>
    public bool? EventActive { get; set; }

    /// <summary>
    /// ★★ v0.1.5 动态感染扩散（还原官方 2024 感染者事件"随时间慢慢感染"的行为）。
    ///
    /// 官方为什么是"动态"的：`seasonalevents.json` 的 `mapInfectionAmount` 给的是**下限**
    /// （`ConfigureZombies` 里 `value = (num == 100) ? 100 : rand(num, 100)`，每次开服重摇），
    /// 而社区实测到的是**逐图/随时间推进**的扩散 —— 典型证词："killa 因为被玩家击杀过多导致
    /// 立交桥的零感染连 12 小时都没撑到"。SPT 只有静态快照，这里用"活动第 N 天"折算来近似。
    ///
    /// 用法（写在活动上）：
    /// <code>
    /// "infectionRamp": { "rampDays": 6, "zeroMapsJoinAtDay": 3, "zeroMapPeakPercent": 15 }
    /// </code>
    /// 语义：
    ///   · `rampDays`（默认 6）：官方值按天数折算 —— 第 1 天 = 官方值 × 1/6，第 6 天起 = 官方值全额；
    ///   · `zeroMapsJoinAtDay`（默认 3）：官方配置为 **0** 的图（海关/立交桥）前 N 天保持 0，
    ///     第 N 天起线性爬到 `zeroMapPeakPercent`；
    ///   · `zeroMapPeakPercent`（默认 15）：这些"后来才感染"的图的峰值（官方从未给过它们的值）；
    ///   · `enabled:false` 可关掉。
    /// </summary>
    public InfectionRampDef InfectionRamp { get; set; }

    public class InfectionRampDef
    {
        public bool? Enabled { get; set; }
        public int? RampDays { get; set; }
        public int? ZeroMapsJoinAtDay { get; set; }
        public double? ZeroMapPeakPercent { get; set; }
    }

    /// <summary>
    /// ★ 仅夜晚生效：本局是白天时不刷本活动的敌人（默认 false = 原版行为，白天黑夜都刷）。
    ///   实现：SPT 的 MatchController.ConfigureOfflineRaid 里已经算好 `IsNightRaid`
    ///        （WeatherHelper.IsNightTime(raidSettings.TimeVariant, locationId)），
    ///        我们挂钩缓存它，在地图生成时判断。
    ///   用途：邪教/感染者这类"夜行主题"的活动，白天进图不该遇到。
    /// </summary>
    public bool NightOnly { get; set; }

    /// <summary>
    /// 分类（用于 F12 分组显示）：
    ///   "seasonal" = 季节活动（本体季节框架：万圣/圣诞/新年/愚人节）
    ///   "limited"  = 限时活动带任务（本体真有任务定义）
    ///   "noquest"  = 无任务可复现（能复现其它部分，但本体没有该活动的任务）★带提醒
    ///   "tweak"    = 单纯改动（只改首领/爆率/商人/跳蚤等，无任务）
    /// 留空则按「有无 enableQuests」自动归类。
    /// </summary>
    public string Category { get; set; } = "";

    /// <summary>⚠ 本体没有这个活动的任务定义（F12 里作为提醒显示）。</summary>
    public bool TaskMissing { get; set; }

    /// <summary>
    /// ★ 2026-10-29【周抽签 · onlyNoRestart】活动成熟度标签：`ready` | `simulated` | `experimental`
    /// （口径见 `audit/第一批补充-接口契约.md`；缺省按 simulated 处理）。
    /// <para>服务端只读它做一件事：`weeklyDraw.onlyNoRestart=true` 时**不抽 experimental**（那些依赖客户端机制/
    /// 未完全验证，属"要重启/不稳"的那类）。事件引擎本身**不读**这个字段，所以不影响既有活动判定。</para>
    /// </summary>
    public string UiTag { get; set; } = "";

    /// <summary>★ 2026-10-29 wiki 链接（可选；空 = 没有）。页面渲染用，引擎不读。</summary>
    public string Wiki { get; set; } = "";

    /// <summary>提醒的具体说明（缺哪些任务 / 哪些维度未实现）。</summary>
    public string TaskNote { get; set; } = "";

    /// <summary>备注（复刻性/限制说明）。</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// ★ v0.1.3 活动"内容说明" —— 客户端悬浮面板（按热键弹出）显示在每个活动名下面那一行。
    /// 与服务端日志/长备注不同，这里要求**一行内说清"这个活动到底改了什么"**，
    /// 例如："SCAV 主武器池加入 40mm 榴弹发射器（Milkor M32A1 / FN40GL）"。
    /// 留空时客户端会回退显示 Note 的前一段。
    /// </summary>
    public string Desc { get; set; } = "";

    // ── ★ v2.1.6 空投维度 ──
    /// <summary>提升/打开某类空投的权重。{"radar": 300} —— radar 原为 0（永不出现）。</summary>
    public Dictionary<string, int> AirdropWeight { get; set; }

    /// <summary>往某类空投里塞物品。{"radar": {"<itemId>": [min, max]}}</summary>
    public Dictionary<string, Dictionary<string, List<int>>> AirdropAddLoot { get; set; }

        ///   · <c>weapons</c>（★ v0.1.5 新增）：{ "malfunctionMultiplier": 3.0, "durabilityBurnMultiplier": …, "heatFactorMultiplier": … }
    ///     落点 <c>_props.MalfunctionChance</c> / <c>DurabilityBurnModificator</c> / <c>HeatFactor</c>（2022 万圣节「枪械故障率提升」）
    ///   · <c>food</c>（★ v0.1.5 新增）：{ "hydrationMultiplier": 1.5, "energyMultiplier": 1.5 }
    ///     落点 <c>_props.effects_health.&lt;Hydration|Energy&gt;.value</c>（2022 万圣节「食物属性提升」）
/// <summary>
    /// ★ v0.1.3 items 维度（物品改动）。选择器 → 改动项：
    /// <code>
    /// "items": {
    ///   "shotgunAmmo":         { "damageMultiplier": 2.0, "penetrationMultiplier": 1.5, "armorDamageMultiplier": 1.5 },
    ///   "ammo":                { "damageMultiplier": 2.0 },
    ///   "melee":               { "slashMultiplier": 2.0, "stabMultiplier": 2.0 },
    ///   "stim:Buffs_Obdolbos": { "valueMultiplier": 2.0, "durationMultiplier": 1.5, "chanceMultiplier": 1.0 }
    /// }
    /// </code>
    /// 落点：<c>templates/items.json</c> 的 <c>_props</c>（弹药 Damage/PenetrationPower/ArmorDamage、
    /// 近战 knifeHitSlashDam/knifeHitStabDam）与 <c>globals.json → config.Health.Effects.Stimulator.Buffs</c>。
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> Items { get; set; }

    /// <summary>
    /// ★ v0.1.3 空投频率倍率（空投潮）。作用在**每张图**的
    /// <c>locations/&lt;map&gt;/base.json → AirdropParameters[0]</c>：
    ///   · <c>PlaneAirdropChance</c> × 倍率（上限 1.0 = 必来）
    ///   · <c>PlaneAirdropMax</c> × 倍率（上限 20）
    ///   · <c>MinPlayersCountToSpawnAirdrop</c> 压到 1（单人局也能来 —— 本体默认要求 6 人！）
    /// 实证：本体 6 人阈值导致**单人 SPT 永远等不到飞机空投**，必须压到 1。
    /// </summary>
    public double? AirdropFrequencyMultiplier { get; set; }

    /// <summary>
    /// ★★ v0.1.5 新增维度：**按商人粒度的报价折扣**（官方"某商人折扣出售"类活动的真正落点）。
    ///
    /// 写法：`"traderPriceMultiplierPerTrader": { "Skier": 0.7, "Prapor": 0.75 }`
    /// 键可用商人昵称或 id；值 = 报价倍率（&lt;1 打折、&gt;1 涨价）。
    /// 落点：该商人 `Assort.BarterScheme` 里**货币条目**的 `Count`（只动卢布/美元/欧元，不动易物物品），可完整还原。
    /// 为什么不用 `loyaltyLevels[].buy_price_coef`：它经 `TraderHelper.GetHighestSellToTraderPrice` 只影响
    /// "玩家**卖给**商人"的价，与"商人卖给你"无关。
    /// </summary>
    public Dictionary<string, double> TraderPriceMultiplierPerTrader { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 新增维度：**玩家 SCAV 出生装备强化**（`int`：加到每个 karma 档位的每个装备槽修正值上）。
    ///
    /// 官方 2024-12-21「PScav武装強化」原文：**玩家 SCAV 的装备变好、敌方 SCAV 不变**。
    /// 落点：`configs/playerscav.json → karmaLevel[&lt;karma&gt;].modifiers.equipment.*`
    /// （实机：karma -7~-1 = -2、karma 0 = 0、karma 1..6 = +4..+70）。
    /// ⚠ 不要用 `botPresetBatch` 代替 —— 那只影响**所有** assault SCAV 的预设批次数，与"玩家 SCAV 出生装备"无关。
    /// </summary>
    public int? PlayerScavEquipmentBoost { get; set; }

    /// <summary>
    /// ★★ v0.1.5 新增维度：**手册价（handbook price）直接设值**。键 = 物品 tpl，值 = 目标价格。
    /// 依据：官方 2022-12-19「Dogtag Sales Event」原文「Dogtag sales price has been increased to **₽2,646 per level**」。
    /// 落点：`TemplateTable.Handbook.Items[].Price` + `HandbookHelper` 的私有运行期缓存（见 PriceDimension.cs）。
    /// </summary>
    public Dictionary<string, double> HandbookPriceSet { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 新增维度：**手册价按倍率缩放**。键 = 物品 tpl，值 = 倍率。
    /// 依据：官方 2025-12-15「RAM Shortage」第 ① 条「RAM sticks have their **value increased**」（官方未给数值 ⇒ 倍率由本 mod 取值）。
    /// </summary>
    public Dictionary<string, double> HandbookPriceMultiplier { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 新增维度：**把物品加进跳蚤禁售表**（`configs/item.json → blacklist`，即 `ItemFilterService.IsItemBlacklisted`）。
    /// 依据：官方 2026-07-29「Permanent Boss Presence」原文
    /// 「**Unique weapons and gear from Bosses cannot be sold on the flea market**」。
    /// 本 mod 直接采用 SPT 自己定义的 `itemConfig.BossItems`（40 个首领专属装备）作为清单，不自行猜测。
    /// </summary>
    public List<string> FleaBlacklistAdd { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 新增维度：**把指定物品从商人 assort 里下架**。
    /// 键 = 商人 id / 昵称 / `"*"`（全部商人）；值 = 要下架的物品 tpl 列表。
    /// 依据：官方 2023-11-25「Ammo Shortage」原文「**Armor-piercing and other high penetration ammunition
    /// is no longer available on traders**」+ 官方逐条列出的 **31 种弹药**。
    /// （旧实现用的是"全场涨价 ×1.8"，与官方"下架"完全不是一回事 ⇒ 已改。）
    /// </summary>
    public Dictionary<string, List<string>> TraderAssortRemove { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 新增：**关掉商人的现金报价**（只留易物）。
    /// 依据：官方 June 2022 删档前 **Day 4「Barter Only」**原文
    /// 「**Removed all cash-offers from the Traders (except for Fence) — you can only barter with them now.**」。
    /// 落点：遍历商人 assort 的 `BarterScheme`，凡"货币报价"（卢布/美元/欧元）的条目整条下架，Fence 除外。
    /// </summary>
    public bool TraderCashOffersDisabled { get; set; }

    /// <summary>
    /// ★★ v0.1.5 新增维度：**按地图设置 bot 类型数量上下限**（`configs/location.json → botTypeLimits`）。
    /// 键 = 地图名（官方键名形如 `Bigmap` / `TarkovStreets` / `Woods`），值 = `{ "类型": [min, max] }`。
    /// 依据：官方 2026-05-08「Reshala vs Glukhar」第 ④ 条
    /// 「**Regular Scavs do not spawn on Customs at all**」。
    /// </summary>
    public Dictionary<string, Dictionary<string, List<int>>> BotTypeLimitSet { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 新增维度：**把地图从"玩家 SCAV 战局"可选地图里移除**。
    /// 落点 `configs/location.json → scavRaidTimeSettings.maps`（实测键名是小写地图 id，如 `factory4_day`）。
    /// 依据：官方 2026-05-01「Arena Cleanup Crew on Factory」第 ③ 条
    /// 「**Factory is not accessible in the Scav game mode**」。
    /// </summary>
    public List<string> ScavRaidMapDisable { get; set; } = new();

    /// <summary>
    /// ★ v0.1.5 空投频率的**限图**能力（官方 Lighthouse Airdrops 2026-09-26 只提升**灯塔**的频率，
    /// 而旧实现改的是全局硬编码 10 张图 —— 落点错、范围错）。
    /// 写法：`"airdropFrequencyMaps": ["lighthouse"]`；不写 = 沿用默认户外图列表。
    /// </summary>
    public List<string> AirdropFrequencyMaps { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 空投「**追加**」语义（补上审计指出的设计缺陷）：把指定物品**追加**进空投箱，
    /// 而不是像 `airdropAddLoot` 那样（`useForcedLoot=true`）**整箱替换**成只装这些物品。
    ///
    /// 写法：`"airdropAppendLoot": { "64897ffc3656831810043165": 1 }`（tpl → 数量）
    /// 落点：Harmony Postfix `AirdropService.GenerateAirdropLoot()`，直接往返回的箱子列表里追加
    /// （带正确的 parentId / slotId / StackObjectsCount）。
    /// 适用：官方 Weapon Case Airdrops（2023-06-19）「空投箱里出现已密封的武器箱」。
    /// </summary>
    public Dictionary<string, int> AirdropAppendLoot { get; set; } = new();

    /// <summary>
    /// ★ v0.1.5 官方随从人数覆盖（`bossSpawns` 用的是模板，人数常与官方不符）。
    /// 写法：`"escortCount": { "laboratory": { "bossGluhar": 6 } }`
    /// （官方 Scav On Labs 是 Glukhar + **6** 名护卫，而本体 rezervbase 模板会给 8 名）。
    ///
    /// <para>★★ 范围支持（task-33 追加）：值也可以写成**范围**，因为官方 `april_fools` 的
    /// `halloweennightcult` 随从数是「**0~2**」而不是单值。三种写法都接受（<see cref="EscortCountValue"/>）：
    /// <c>2</c>（纯数字，**旧数据原样兼容**）· <c>"0-2"</c> · <c>[0,2]</c>。
    /// 最终写进 <c>BossEscortAmount</c> 的就是对应的字符串（客户端字段本身就是字符串，
    /// 实测官方表里还出现过 <c>"0,0,0,0,2,2,2,1,1,1,1,1,0,2,30"</c> 这种列表形式）。</para>
    /// </summary>
    public Dictionary<string, Dictionary<string, EscortCountValue>> EscortCount { get; set; } = new();

    /// <summary>
    /// ★ v0.1.3 强制空投类型：<c>"radar" / "weaponArmor" / "mixed" / "foodMedical" / "barter" / "toiletPaper"</c>。
    /// 实现：把该类型权重拉满、其它类型压到极小值（只用权重，不动 SPT 的抽取代码，最稳）。
    /// </summary>
    public string AirdropForceType { get; set; }

    /// <summary>
    /// ★ v0.1.3 Transit（多图转运 / 马拉松）维度：把每张图的**转场入口**打开或关闭。
    ///
    /// 落点（实证）：`database/locations/&lt;map&gt;/base.json → transits[]`，
    ///   每条形如 <c>{ "id":1, "name":"SAN_TRANSIT_1", "location":"TarkovStreets", "active":true, "activateAfterSec":60, "time":20 }</c>。
    ///   **`active` 就是"这个转场口开不开"**：本体 13 张图共 38 条转场，其中一部分默认 `false`（要靠任务解锁）。
    ///
    /// 用法：<c>"transitsActive": true</c> = 全图所有转场口都打开（马拉松：零号地→街区↔实验室→立交桥→海关→工厂→森林→储备站→灯塔→海岸线）。
    /// 可还原。
    /// </summary>
    public bool? TransitsActive { get; set; }

    /// <summary>
    /// ★ v0.1.3 **弹鼓/大弹匣出现率**（对 PBS 也有效，因为不动 PBS 任何文件）。
    ///
    /// 落点：<c>items.json → 武器的 _props.Slots[mod_magazine]._props.filters[].Filter</c>
    ///   —— 把不属于该武器但**物理上能装**的弹鼓写进这把武器的弹匣槽过滤表。
    ///
    /// 为什么对 PBS 也有效（实证）：PBS 的 `ApbsExternalInventoryMagGen` /
    ///   `InventoryMagGenHelper` 挑弹匣时都会 `GetItem(weaponTpl).Properties.Slots
    ///   .FirstOrDefault(x => x.Name == "mod_magazine")` 去读**武器自己的槽位过滤**，
    ///   并且 PBS 的装备池本来也是从 items 的槽位过滤建出来的 ⇒ 扩这张表，
    ///   PBS 和本体都会跟着多用大弹鼓，**不需要改 PBS 的任何文件**。
    ///
    /// 用法：<c>"drumMagazineBoost": { "magazineMultiplier": 4, "alsoLaunchers": 1 }</c>
    /// ★ 注意：value 必须是**数字**（0/1），不能写 true/false —— 这是个
    ///   <c>Dictionary&lt;string, double&gt;</c>，写布尔会让**整个 events.json 反序列化抛异常**
    ///   （干跑实测踩到过：`alsoLaunchers: false` → JsonException，服务端直接起不来）。
    /// </summary>
    public Dictionary<string, double> DrumMagazineBoost { get; set; }

    /// <summary>
    /// ★★★ v0.1.4 **活动任务要杀的敌种**（用于"任务可完成性"检查）。
    /// <para>
    /// 起因（实测发现的严重设计缺陷）：有些活动只开了任务，却**没提供任务要求的敌人**
    /// ⇒ 玩家接了任务也**永远做不完**。例：
    ///   · `bloodhounds` 开了任务 Bloodhounds，但任务要杀 `arenaFighterEvent`（竞技场斗士），
    ///     而刷它的是 `smugglers` —— 单开血猎犬 = 死任务。
    ///   · `dark_times` 要杀感染者，但感染者只在万圣节季节刷出。
    /// </para>
    /// 填法：<c>"questRequires": { "64764abcd125ab430a14ccb5": ["arenaFighterEvent"] }</c>
    /// （键 = 任务 id，值 = CounterCreator 里要求的 savageRole 列表）
    /// 启动时会检查：本活动（或它的自动伙伴）是否真的刷这些敌种 → 没有就打警告，并尝试**自动补上伙伴**。
    /// </summary>
    public Dictionary<string, List<string>> QuestRequires { get; set; }

    /// <summary>
    /// ★★★ v0.1.4 **随机地图首领概率**（本 mod 自建玩法，补上"随机地图 boss 刷新率提高"这一类）。
    /// <para>
    /// <c>true</c> 时，<see cref="BossChance"/> 里的地图会在**每次启动/换档时随机重掷**：
    /// 从 11 张战局地图里随机抽 N 张（N 由 config 的 <c>randomBossMapCount</c> 决定，默认 3），
    /// 把每张图的"全部首领"概率设成 **60%**（不是 100% —— 保留"碰运气"的感觉）。
    /// </para>
    /// 这样每次开游戏随机到的是不同的图，而不是固定的那几张。
    /// </summary>
    public bool RandomBossMaps { get; set; }

    // ── ★ v0.1.3 globals 维度 ──
    /// <summary>
    /// globals 系数：{"skillMultiplier":3.0, "expMultiplier":2.0, "damageMultiplier":2.0, "hydrationMultiplier":2.0}
    ///
    /// ★ v0.1.3 落点更正（实证）：
    ///   · <c>hydrationMultiplier</c> → **<c>globals.json → config.Health.Effects.Existence.HydrationDamage / EnergyDamage</c>**
    ///     （消耗速率，每个 tick 掉多少；旧代码改的 <c>_props.Hydration</c> **字段不存在**，所以从来没生效过）
    ///   · <c>damageMultiplier</c> → 全部弹药 <c>_props.Damage</c>（服务端无伤害结算代码，这是唯一落点；
    ///     客户端 <c>AmmoTemplate.Damage</c> 是 <c>int</c> ⇒ 结果四舍五入）
    /// </summary>
    public Dictionary<string, double> Globals { get; set; }

    // ── ★ v2.3.0 藏身处 / 商人 / bot 预设 ──
    /// <summary>建造时间覆盖（秒）。0 = 秒建。本体默认 -1（真实时间）。</summary>
    public double? HideoutBuildSeconds { get; set; }
    /// <summary>制作时间覆盖（秒）。0 = 秒造。</summary>
    public double? HideoutCraftSeconds { get; set; }
    /// <summary>商人全局价格倍率。1 = 原价，0.5 = 五折，2 = 双倍。</summary>
    public double? TraderPriceMultiplier { get; set; }
    /// <summary>商人刷新间隔（秒）。</summary>
    public double? TraderUpdateSeconds { get; set; }
    /// <summary>商人购买是否算"战局中找到"。</summary>
    public bool? TraderPurchasesFir { get; set; }
    /// <summary>bot 装备预设批次数（调大 = 出装更杂）。{"assault":90}</summary>
    public Dictionary<string, int> BotPresetBatch { get; set; }
    /// <summary>每图 bot 上限。</summary>
    public double? BotMaxCap { get; set; }

    /// <summary>
    /// ★ v2.4.0 bot 装备池：往某角色的某槽位加物品（物品→权重）。
    /// {"assault": {"FirstPrimaryWeapon": {"6275303a9f372d6ea97f9ec7": 150}}}
    /// 槽位名：FirstPrimaryWeapon / SecondPrimaryWeapon / Holster / Scabbard / Headwear / ArmorVest / …
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> BotAddItems { get; set; }

    /// <summary>
    /// ★ v2.4.1 bot 武器配件池（列表结构，靠重复提概率）。
    /// {"assault": {"*": {"mod_magazine": ["5bed625c0db834001c062946"]}}}
    /// 武器键支持 "*" = 所有已存在的武器。
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>> BotAddMods { get; set; }

    /// <summary>
    /// ★ v2.4.1 bot 携带物品（背包/口袋/战术背心）加权。
    /// {"assault": {"Backpack": {"5bed625c0db834001c062946": 10000}}}
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> BotLootAdd_ { get; set; }

    /// <summary>
    /// ★★★ v2.6.74【**必定携带 + 指定数量**】—— `botLootAdd_` 的加强版（数量可控）。
    ///
    /// <para>写法：<c>"botLootStack_": { "arenaFighterEvent": { "&lt;tpl&gt;": [10, 20] } }</c>
    /// （键 = bot role（忽略大小写）→ 物品 tpl → **[min, max]** 闭区间；
    /// 只写一个数 <c>[N]</c> 也接受 = 恒定 N）。</para>
    ///
    /// <para><b>为什么需要新字段</b>：`botLootAdd_` 只能往战利品池里加
    /// <c>tpl → 相对权重</c>，而 SPT 抽到物品后数量恒为 1
    /// （`BotLootGenerator.AddLootFromPool` → `GenerateExtraPropertiesForItem(..., forceStackObjectsCount: true)`
    /// 只把 `StackObjectsCount` 补成 1.0）。**数量**只有货币类物品能在配置里控制
    /// （`BotConfig.CurrencyStackSize`，见 `BotLootGenerator.RandomiseMoneyStackSize`）。</para>
    ///
    /// <para><b>语义</b>：活动期间，该 role 的**每一只** bot 身上保证有**恰好一叠**该物品，
    /// 数量 = `[min,max]` 随机（超过 `StackMaxSize` 会被夹到上限）。实现分两层
    /// （配置层改 `CurrencyStackSize` 管数量，产出物层在 `PrepareAndGenerateBot` postfix 里
    /// 合并/补齐保证"必定携带且只一叠"）—— 详见 <see cref="BotLootStackDimension"/> 类注释。</para>
    /// </summary>
    public Dictionary<string, Dictionary<string, List<int>>> BotLootStack_ { get; set; }

    /// <summary>
    /// ★★★ v2.6.37【**只在该地图生效**】—— 地图 id（与 `bot.json → maxBotCap` 的键同格式），
    /// 例如 <c>"bigmap"</c> = 海关。
    ///
    /// <para><b>为什么要它</b>：用户裁决「致命毒素（`june2022_toxin`）**只在海关生效**」。
    /// 而这两个维度的落点是**全局共享**的：</para>
    /// <list type="bullet">
    /// <item><c>airdropAppendLoot</c> → 每次空投箱生成都追加（与地图无关）</item>
    /// <item><c>botLootAdd_</c> → 写进 <c>botTable.Types[role].BotInventory.Items[slot]</c> 这个**所有图共用**的池子</item>
    /// </list>
    ///
    /// <para><b>语义</b>：本字段**只门控这两个维度**（不碰 `globals` —— 那是真全局，服务端做不到按图）。
    /// <b>不填 = 完全保持原行为</b>（供 `hustle` / `ram_shortage` / `instant_hideout_build` /
    /// `weapon_case_airdrops` / `smugglers_2026` 等既有活动继续用 ⇒ **零回归**）。</para>
    ///
    /// <para><b>拿不到地图时的兜底</b>：**照旧生效 + warning**
    /// （理由：漏掉解药会让"8 分钟必死"变成**无解**，比"解药出现在别的地图"严重得多）。</para>
    /// </summary>
    public string OnlyOnMap { get; set; }

    /// <summary>
    /// ★★★【整体替换 bot 装备池】—— 活动期间把**某角色某装备槽**的池子**整个换掉**（不是追加），
    /// 并把该槽的**装备概率置 100**（保证一定戴）；全部可还原。
    ///
    /// <para><b>语义</b>（与 <see cref="BotAddItems"/> 的"追加"严格区分）：</para>
    /// <list type="number">
    /// <item><c>BotTable.Types[role].BotInventory.Equipment[slot]</c> 被**整体替换**为新池
    ///   （原池对象**原样备份**，还原时把同一个对象写回 ⇒ 逐字节复原）。</item>
    /// <item><c>BotType.BotChances.EquipmentChances[slot]</c> 置 <c>100</c>
    ///   （= 客户端 JSON 的 <c>chances.equipment[slot]</c>；SPT
    ///   <c>BotInventoryGenerator.GenerateEquipment</c> 用 <c>RandomUtil.GetChance100(该值)</c> 掷骰，
    ///   100 ⇒ 必过 ⇒ **一定戴**）。原值备份，还原时写回。</item>
    /// <item><b>插板池</b>（<c>mods</c>，见 <see cref="BotEquipmentReplaceRole.Mods"/>）：
    ///   把 <c>BotInventory.Mods[护甲 tpl][槽位]</c> 的 <c>HashSet&lt;MongoId&gt;</c> **整体替换**为新集合
    ///   （原 HashSet **实例**备份，还原时写回同一实例），并把该槽的
    ///   <c>chances.equipmentMods[槽位小写]</c> 置 100 —— 否则本机 <c>left/right_side_plate</c> 原值 **0**
    ///   ⇒ 侧板永远不会出现（SPT <c>BotEquipmentModGenerator.ShouldModBeSpawned</c> 先掷这个概率）。</item>
    /// <item><b>只对 <see cref="BotEquipmentReplaceSpec.Maps"/> 里的地图生效</b>：落点是
    ///   **全图共用**的 bot 模板，故由 <c>BotGenerator.PrepareAndGenerateBot</c> 的 prefix
    ///   按 <c>BotGenerationDetails.Location</c> 逐只 bot 切回原池/新池（同 <see cref="OnlyOnMap"/> 的思路）。</item>
    /// </list>
    ///
    /// <para><b>写法</b>：</para>
    /// <code>
    /// "botEquipmentReplace": {
    ///   "maps": ["bigmap", "woods"],
    ///   "roles": {
    ///     "arenaFighterEvent": {
    ///       "FaceCover": { "67a5c657782ce4655104db16": 1 },
    ///       "mods": { "5c0e51be86f774598e797894": { "Front_plate": { "656f603f94b480b8a500c0d6": 1 } } }
    ///     }
    ///   }
    /// }
    /// </code>
    ///
    /// <para><b>降级</b>：角色/槽位/字典类型任一拿不到 ⇒ 只 warning + 跳过该条，绝不抛。</para>
    /// </summary>
    public BotEquipmentReplaceSpec BotEquipmentReplace { get; set; }

    // ── ★ v2.5.0 实验室免票 + 跳蚤手续费 ──
    /// <summary>true = 实验室免卡进入（清空 laboratory 的 AccessKeys / AccessKeysPvE）。</summary>
    public bool? LabsFreeAccess { get; set; }
    /// <summary>跳蚤挂单手续费倍率（RagfairConfig.OfferListingTaxMultiplier）。调低 = 手续费大降。</summary>
    public double? FleaTaxMultiplier { get; set; }

    /// <summary>
    /// ★ v0.1.3 **跳蚤免挂单费**。
    ///
    /// 纯数据落点（**不对游戏打任何补丁**）：
    ///   ① 实扣：<c>RagfairConfig.OfferListingTaxMultiplier = 0</c>（服务端 CalculateTax 归零）
    ///   ② 显示：<c>globals → config.RagFair.CommunityItemTax / CommunityRequirementTax → 1e-6</c>
    ///      客户端挂单界面的手续费由 <c>EFT.Trading.PriceCalculator.CalculateTaxPrice</c> 用这两个值算出，
    ///      1e-6 取整显示为 <c>0 ₽</c>，而它 &gt; 0 ⇒ 不会触发 <c>AddOfferWindow.SetPrices</c> 的"税额&gt;0 才可点上架"门槛。
    /// </summary>
    public bool? FleaZeroListingFee { get; set; }

    /// <summary>
    /// ★ v2.6.0 天气季节（春夏秋冬）：SUMMER / AUTUMN / WINTER / SPRING
    /// （另可填 AUTUMN_LATE / SPRING_EARLY / STORM）。
    /// 落点 WeatherConfig.OverrideSeason（configs/weather.json → overrideSeason）。
    /// </summary>
    public string ForceWeatherSeason { get; set; }

    // ══════════════════════════════════════════════════════════════════════
    // ★★ v0.1.5「重验后补做」的维度（原先被我误判为"做不到"，落点已逐字核实）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 取消**购买上限**（官方 Unlocked Traders 第三条 "All item purchase limits are removed"）。
    /// 落点：每个商人 `assort.items[].upd.BuyRestrictionMax`（置 null + UnlimitedCount=true）。可还原。
    /// </summary>
    public bool? RemovePurchaseLimits { get; set; }

    /// <summary>
    /// 缩放指定 bot 类型的**血量**：`bots/types/&lt;type&gt;.json → health.BodyParts[].max/min`。
    /// 形如 {"bossTagilla": 1.5}。官方 2022 万圣节只说"血量池提到 1900"、未给逐部位值 ⇒ 用倍率表达。
    /// </summary>
    public Dictionary<string, double> BotHealthMultiplier { get; set; }

    /// <summary>
    /// ★★★【按 bot 类型**逐部位覆盖**血量】**mod 自定义维度**（官方没有对应机制）。
    ///
    /// <para>写法：<c>"botHealthOverride": { "arenafighterevent": { "Head": 60, "Chest": 120 } }</c>
    /// （键 = bot 类型 / role，匹配用 <c>OrdinalIgnoreCase</c>；值 = 部位名 → 目标血量）。</para>
    ///
    /// <para>落点：`BotTable.Types[role].BotHealth.BodyParts[0][部位]` 的 **`min` 和 `max` 同时**
    /// 写成给定值（本体 `arenafighterevent` 实测：Chest 160 / Head 45 / Stomach 130 /
    /// LeftArm 100 / RightArm 100 / LeftLeg 110 / RightLeg 110，min 与 max 相等；
    /// CLR 侧 `BodyParts` 是 `List&lt;BodyPart&gt;`，部位是 `BodyPart` 的属性、类型 `MinMax&lt;double&gt;`）。
    /// 与上面的 <see cref="BotHealthMultiplier"/>（**倍率**缩放**全部**部位）互补：
    /// 本字段是**绝对值**、且**只覆盖 JSON 里写了的部位**，没写的保持原值。</para>
    ///
    /// <para>还原：每个部位写之前把原 `{min,max}` 记进 `AddRestore` 快照 ⇒ 活动关闭/切换时精确放回
    /// （与 <see cref="BotAppearanceOverride"/> 同一套快照机制，`Restore()` 逆序回放）。</para>
    ///
    /// <para>降级：角色找不到 / 没有 `BotHealth` / `BodyParts` 为空 / 值非法（≤0 或 NaN/∞）
    /// ⇒ **只打 warning 并跳过这一条**，不影响同活动其它部位、也不影响其它维度。</para>
    ///
    /// <para>实现见 <see cref="ExtraDimensions.ApplyBotHealthOverride"/>。</para>
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> BotHealthOverride { get; set; }

    /// <summary>
    /// 瞄准抖动倍率：`globals.config.AimPunchMagnitude`（float，原值 5.76）。
    /// 官方 softcore「瞄准抖动 −60%」⇒ 填 0.4。同时按同倍率缩放 `config.Aiming` 里的数值字段。
    /// </summary>
    public double? AimPunchMultiplier { get; set; }

    /// <summary>惯性倍率：`globals.config.Inertia`（字典）内所有数值字段 ×该倍率。</summary>
    public double? InertiaMultiplier { get; set; }

    /// <summary>
    /// 玩家受伤倍率：同时缩放 `globals.config.HandsOverdamage / LegsOverdamage / StomachOverdamage`
    /// （官方 double_damage「除头外受伤翻倍」⇒ 填 2；覆盖四肢+腹部，躯干暂无对应字段）。
    /// </summary>
    public double? OverdamageMultiplier { get; set; }

    /// <summary>撤离点锁定：**只保留**名字里含这些片段的撤离点，其余常规/秘密撤离点 Chance 置 0。</summary>
    public List<string> ExitKeepOnly { get; set; }

    /// <summary>撤离点锁定作用的图（配合 <see cref="ExitKeepOnly"/>）。</summary>
    public List<string> ExitLockMaps { get; set; }

    /// <summary>按图禁用转场：把这些图的 `base.transits[].active` 置 false。</summary>
    public List<string> TransitDisableMaps { get; set; }

    /// <summary>
    /// ★★ v0.1.5 第⑤条：**按 transit id 精确禁用**（{地图: [转场 id 或 name]}）。
    ///
    /// 为什么需要它：<see cref="TransitDisableMaps"/> 会把整张图的**所有**转场关掉，
    /// 无法表达官方那种"**只禁用进入某图的转场**"（如官方 Arena Cleanup Crew：「**Transits to Factory are disabled**」
    /// —— 而工厂自身**出向**的 3 条转场不受影响）。实证：全库 38 条转场里目标=工厂的只有 3 条：
    /// `bigmap` id=10 `CUS_TRANSIT_10`、`woods` id=15 `WOO_TRANSIT_15`、`labyrinth` id=15（⚠ labyrinth 那组是 woods 的逐字拷贝，疑 BSG 数据遗留）。
    /// </summary>
    public Dictionary<string, List<string>> TransitDisableIds { get; set; }

    /// <summary>
    /// ★★ v0.1.5（对照 SVM `Sections/Events.cs` 的实证做法）：**随从类型** —— `{地图: {boss: "随从类型"}}`。
    ///
    /// 用途：官方"兄弟团聚"的语义是 **Tagilla 陪在 Killa 身边**（Kiba 旁）。
    /// 我们原先的做法是把 Tagilla **追加成独立条目** ⇒ 两人各刷各的、**不会贴在一起** ✗。
    /// SVM 的正解（其源码注释原文："this way they spawn close to each other and actually stick together"）：
    /// 把**已有 Killa 条目**的 `BossEscortType` 设成 `bossTagilla`、`BossEscortAmount` 设成 `1`。
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> EscortType { get; set; }

    /// <summary>
    /// ★ 2026-10-30【追加条目的首领难度】—— `{地图: {boss: "hard"}}`。
    ///
    /// 语义：**本活动追加**该首领条目时，把 `BossLocationSpawn.BossDifficulty` 设为这里的值；
    /// 没写的图/角色保持模板/默认（`normal`）。**只影响我们追加的条目**，本体已有条目一个字段都不动
    /// （与 <see cref="BossChance"/> 的行为一致）。不做单独快照 —— 随整个追加条目一起移除/还原。
    /// 合法值（客户端按这个字符串解析）：`easy` / `normal` / `hard` / `impossible`；非法值只 warning 并保留默认。
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> BossDifficulty { get; set; } = new();

    /// <summary>
    /// ★ 2026-10-30【追加条目的随从难度】—— `{地图: {boss: "hard"}}`，写 `BossEscortDifficulty`。
    /// 语义/校验/还原与 <see cref="BossDifficulty"/> 完全一致。
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> EscortDifficulty { get; set; } = new();

    /// <summary>
    /// ★★ v0.1.5 ③b：**玩家等级门槛** —— 只有 ≥ 该等级的玩家才让本活动生效（0/空 = 不限制）。
    /// 官方 2024 场「Permanent Boss &amp; Cultists Presence」对邪教徒限定 **21 级以上**玩家。
    /// 实现在进图钩子里按**房主存档等级**判断（低于门槛 ⇒ 本局跳过该活动，日志写明原因）。
    /// </summary>
    public int? MinPlayerLevel { get; set; }

    /// <summary>
    /// ★★★ **按图**的玩家等级门槛（用户裁决「按图分别设门槛做」）。
    /// 语义：`{ "sandbox_high": 21 }` ⇒ **只有 sandbox_high 需要 21 级，其余图不受限**。
    ///
    /// <para><b>与活动级 <see cref="MinPlayerLevel"/> 的优先级（两级门槛）</b>：
    /// <list type="bullet">
    ///   <item>该图**在表里** ⇒ **用表里的值**（**覆盖**活动级对该图的判定）</item>
    ///   <item>该图**不在表里** ⇒ **回落到活动级 <see cref="MinPlayerLevel"/>**（原有行为一字不变）</item>
    ///   <item>表为空 ⇒ 与加这个字段之前**完全等价**（所以 `boss_cultist_presence` 的 `minPlayerLevel: 21` 行为不受影响）</item>
    /// </list></para>
    ///
    /// <para>用途（第一个消费者）：官方 `Smugglers 2026 (2026-03-12)` 逐字
    /// 「They can be found in groups of 3-4 on Customs, Woods, Shoreline, Interchange, Lighthouse,
    /// Streets of Tarkov and <b>Ground Zero (Level 21+)</b>」⇒ 只有 Ground Zero 那张有 21 级门槛
    /// ⇒ `smugglers_2026` 写 <c>{ "sandbox_high": 21 }</c>。
    /// （`Ground Zero` 在 0.16.9 有**两个** location：`sandbox`(Id=`Sandbox`) 与
    /// `sandbox_high`(Id=`Sandbox_high`)；后者才是 **21+** 那一版，也正是我们 `bossSpawns` 已经在用的 key。）</para>
    ///
    /// <para>落点：`Patches/BossWavePatch.cs` 里进图等级判断处（与活动级同一处，按 <c>mapKey</c> 查表）。
    /// **可降级**：表里没有该图 / 值 ≤ 0 / 结构不符 ⇒ 走回落分支或静默跳过 + warning。</para>
    /// </summary>
    public Dictionary<string, int> MinPlayerLevelByMap { get; set; } = new();

    /// <summary>
    /// ★★★ v0.1.5【同年份才组合】标记"**往年场次变体**"（如 halloween_2022 / halloween_2023 /
    /// khorovod_2024 / santa_2022）：它们各代表**某一年**的官方场次，
    /// **不应与其它年份的同族活动同时自动开启**（否则 10/28 会同时冒出三个万圣节）。
    /// 默认由**自动/日期窗**路径跳过，只自动开"标准/最新"的那个；变体留给手动（force.txt / F12）。
    /// 想恢复"全开"旧行为：config 里 `seasonAutoPick: "all"`。
    /// </summary>
    public bool? YearVariant { get; set; }

    /// <summary>
    /// ★★★ v0.1.5【族 + 年份组合】活动所属"节日族"（halloween / christmas …）。
    /// 官方活动是**组合式**的：一个场次 = 一组活动。轮换轮到该族时**整组一起开**。
    /// </summary>
    public string Family { get; set; }

    /// <summary>该族内的**年份场次**（如 2024）。与 <see cref="FamilyGeneric"/> 二选一。</summary>
    public int? FamilyYear { get; set; }

    /// <summary>
    /// ★★★ 2026-10-31【邪教之夜 vs 僵尸万圣节】**显式互斥表**（双向或单向都算）。
    /// <para>为什么需要：这两个事件同族同年（halloween/2024），组合展开会把它们整组打开；
    /// 但它们刷的是**不同的东西**（邪教之夜刷 sectantPriest 邪教徒，僵尸万圣节刷全图感染者），
    /// 官方窗口也从不重叠（10/11–10/24 vs 10/28–11/9）。</para>
    /// <para>冲突检测抓不到它，因为僵尸万圣节没有 `bossSpawns` 条目（走 `nativeZombies`）⇒ 刷点交集为空。
    /// 所以用本字段显式声明：命中任一侧即视为互斥（组合展开跳过 / 轮换不同批选）。</para>
    /// </summary>
    public List<string> MutualExcludeWith { get; set; } = new();

    /// <summary>该族的**通用成员**（本体季节窗，每年都开；如 christmas / newyear）。</summary>
    public bool? FamilyGeneric { get; set; }

    /// <summary>
    /// ★★★ v0.1.5【SCAV 接管 · 路费机制】见 <see cref="TollDef"/>。
    /// 官方：只有一张图能免费进；其它图要交「特定物品」（原话 "pay, but not with money, but goods"）；
    /// **16 级以下玩家不受影响**；免费图按全服击杀数切换（顺序 Custom→Reserve→Woods→Factory→Shoreline→
    /// Interchange→Lighthouse→Streets→Lab）。
    /// ⚠ 各图具体物品官方从未公布 ⇒ `ItemsPerMap` **可配置**；免费图切换用**时间**近似（官方是击杀数驱动）。
    /// </summary>
    public TollDef Toll { get; set; }

    /// <summary>
    /// ★★★ v0.1.5 ②：**首领时刻表**（官方 #79「Bosses &amp; Rogues Wander」）。
    ///
    /// 官方原文是一张 7 行 × 6 时区（MSK/UTC/EDT/CEST/KST/BST）的表格，周期 24h（4+1+4+4+1+4+6=24）：
    ///   MSK 8:00–12:00 → Reserve ｜ 12:00–13:00 → **无** ｜ 13:00–17:00 → Customs ｜ 17:00–21:00 → Shoreline
    ///   21:00–22:00 → **无** ｜ 22:00–2:00 → Interchange ｜ 2:00–8:00 → Woods
    /// 而「哪张图有谁」来自紧随其后的 Boss locations：**这五张图都是同一组 5 位首领**
    /// （Glukhar / Killa+Tagilla / Sanitar / Shturman / Reshala）。
    /// 另：Knight/BigPipe/Birdeye **只在白天工厂**（本活动的 `bossSpawns` 已表达）。
    ///
    /// 结构：{ "tz": "MSK", "rows": [ {"from":8,"to":12,"map":"rezervbase"}, … ], "bosses": [ … ], "escorts": {…} }
    /// 落点：进图时按**系统时钟**换算到指定时区，取当前行 ⇒ 把该行地图加上 bosses。
    /// </summary>
    public BossTimetableDef BossTimetable { get; set; }

    /// <summary>
    /// ★★★ v0.1.5 ①：**商人易物注入** —— `{ "<商人 id 或昵称>": [ {reward, rewardCount, cost, costCount, loyalty} ] }`
    /// 用途：官方「Cultist Pilgrimage」= Mechanic **收邪教刀、出彩色门卡**（6 色配方）。
    /// 实现方式：克隆该商人已有的任一条易物条目，替换 reward/cost 后追加（不动商人其它商品）。
    /// </summary>
    public Dictionary<string, List<BarterAddDef>> TraderBarterAdd { get; set; }

    /// <summary>
    /// 按 bot 类型注入装备（**走数据层，绕开 PBS/ABPS 对运行时注入的拦截**）：
    /// `bots/types/&lt;type&gt;.json → inventory.equipment[槽位][tpl] = 概率`，并把 `chances.equipment[槽位]` 抬到至少该概率。
    /// 形如 {"assault": {"Headwear": {"5bd06f5d86f77427101ad47c": 60}}}。可还原。
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, double>>> BotEquipmentAdd { get; set; }

    // ══════════════════════════════════════════════════════════════════════════════
    //  ★ task-34（⑧⑨ 三维度）新增 —— 三个此前"能做没做"的维度
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★★★ ⑨-A **藏身处升级需求注入**（官方 `beer_upgrades`：把 Pevko 啤酒加进藏身处升级材料）。
    ///
    /// 写法：`"hideoutAddRequirements": { "&lt;area id 或 type 序号&gt;": [ { "stage": 2, "templateId": "62a09f32621468534a797acb", "count": 2 } ] }`
    /// 落点：`hideout/areas.json[i].stages[&lt;n&gt;].requirements[]`（`StageRequirement`，type = Item）。
    /// ★ 关键实证：SPT **只读 `stages`、从不读 `levels`** —— `HideoutArea` 上根本没有 `levels` 属性，
    ///   读点是 `HideoutController.cs:86/124/205/1093`、`HideoutHelper.cs:1088`、`ProfileFixerService.cs:481-487`。
    /// 幂等（同 templateId 已存在则跳过）、可还原（从 requirements 里摘掉）、可降级（找不到只 warning）。
    /// </summary>
    public Dictionary<string, List<HideoutReqDef>> HideoutAddRequirements { get; set; } = new();

    /// <summary>
    /// ★★★ ⑧-B **按 (地图 + tpl) 调松散战利品权重/概率**（官方 `softcore` 第 5 条「实验室彩卡刷新率」）。
    ///
    /// 写法：`"looseLootWeightSet": { "laboratory": { "5c1d0d6d86f7744bb2683e1f": { "probabilityMult": 60, "weightMult": 4 } } }`
    /// 落点：`Location.LooseLoot.Value.Spawnpoints[]` 的
    ///   · `Probability`（刷点整体出现概率；彩卡刷点里只有那 1 张卡 ⇒ 改它就等于改这张卡的刷新率）
    ///   · `ItemDistribution[].RelativeProbability`（该 tpl 在多物品刷点里的相对权重）
    /// 幂等（probability 是赋值天然幂等；weightMult 用 `(map|tpl|field)` 表防重复累积）、可还原、可降级。
    /// </summary>
    public Dictionary<string, Dictionary<string, LooseLootWeightOpt>> LooseLootWeightSet { get; set; } = new();

    /// <summary>
    /// ★★★ **松散战利品注入：加点位 + 换成指定物品**（官方 Wild Wild Woods 的「More and rare loot /
    /// 新增刷新点」那一半；本 mod 此前标注"无现成维度 ⇒ 未做"）。
    ///
    /// 写法（详见 <c>LooseLootInjectDimension</c> 类注释里的 IL 铁证）：
    /// <code>
    /// "looseLootInject": {
    ///   "woods": {
    ///     "densityMultiplier": 1.6,        // 点位克隆倍数（同时把 SpawnpointCount.Mean ×1.6）
    ///     "scaleSpawnpointCount": true,    // 可选，默认 true；false = 只克隆候选池、不提高局内数量
    ///     "valuable": { "chance": 0.35, "tpls": { "&lt;tpl&gt;": 1 } },
    ///     "decor":    { "chance": 0.30, "tpls": { "&lt;tpl&gt;": 1 } },
    ///     "regions":  [ { "name": "…", "min": {"x":..,"z":..}, "max": {"x":..,"z":..}, "valuableBoost": 2.0 } ]
    ///   }
    /// }
    /// </code>
    /// 落点：`Location.LooseLoot.Value.Spawnpoints[]` + `LooseLoot.SpawnpointCount.Mean`
    ///      + `SpawnpointTemplate.Items` / `Spawnpoint.ItemDistribution`。
    /// 幂等（`(活动id|地图)` 表）、可还原（移除克隆点 / 放回原 Items 与 ItemDistribution 引用 / Mean 回滚）、
    /// 可降级（任一环拿不到只 warning）。`regions` 无真实坐标时**不要写** ⇒ 纯全局。
    /// </summary>
    public Dictionary<string, LooseLootInjectMapOpt> LooseLootInject { get; set; } = new();

    /// <summary>
    /// ★★★ **按容器类型往地图静态容器里注入对应高价值物品**（官方 Wild Wild Woods 的
    /// 「容器里能摸出更好的东西」那一半；本 mod 此前只有松散战利品注入，没有容器维度）。
    ///
    /// 写法：
    /// <code>
    /// "containerLootInject": {
    ///   "woods": {
    ///     "chance": 0.25,                     // 每个容器命中概率（默认 0.25，可省略）
    ///     "categories": {
    ///       "jacket":    { "containers": ["&lt;容器 tpl&gt;"], "items": { "&lt;物品 tpl&gt;": 1 } },
    ///       "medbox":    { ... }, "toolbox": { ... }, "safe": { ... },
    ///       "weaponbox": { ... }, "duffle":   { ... }
    ///     }
    ///   }
    /// }
    /// </code>
    ///
    /// 落点：`Location.StaticContainers : LazyLoad&lt;StaticContainerDetails&gt;`
    ///      → `StaticContainers[].Template.Items`（追加 1 件）。
    /// 语义：**概率 P 命中才追加 1 件**；每个容器**最多 1 件**；不命中完全不动。
    /// 尺寸：物品 `_props.Width/Height` 必须塞得进容器 `_props.Grids[]._props.cellsH/cellsV`（允许旋转），
    ///      否则跳过并计数。容器/物品 tpl 拿不到就丢弃该条并 warning。
    /// 可还原（transformer 转直通，json 全程只读不写盘）。实现见 <see cref="ContainerLootInjectDimension"/>。
    /// </summary>
    public Dictionary<string, ContainerLootInjectMapOpt> ContainerLootInject { get; set; } = new();

    /// <summary>
    /// ★★★ **往静态战利品容器的概率表里塞指定物品**（= PPES 的
    /// `Effects.AddStaticLoot(ctx, tpl, 2)`；官方 tarkovMystery 的「抽屉/外套/电脑里能翻出加密闪存盘」）。
    ///
    /// 写法（**全局生效，不按图分键**）：
    /// <code>
    /// "staticLootAdd": {
    ///   "containers": ["drawer", "jacket", "pc"],   // 容器匹配：具名优先，其次子串（大小写不敏感）
    ///   "items": { "&lt;物品 tpl&gt;": 2 },              // tpl → 追加几条概率条目（= 一局最多能抽到几件）
    ///   "weight": 60                                // 追加条目的 relativeProbability（相对权重）
    /// }
    /// </code>
    ///
    /// 落点：`Location.StaticLoot : LazyLoad&lt;Dictionary&lt;MongoId, StaticLootDetails&gt;&gt;`
    ///      → `StaticLootDetails.ItemDistribution`（`{tpl, relativeProbability}` 列表）。
    ///      `LocationLootGenerator.GenerateStaticContainers` 每局读它生成容器内容 ⇒ 生效。
    /// 语义：`relativeProbability` 是**权重**不是百分比（单容器命中率 ≈ `1-(1-w/(Σw+w))^平均件数`）。
    ///      实测 bigmap：Drawer P90≈6483 / Jacket P90≈3548 / PCblock P90≈23442；
    ///      `weight: 60` ⇒ 单容器 ≈0.02%~0.06%（万分之一级）；PPES 原 `percent=2` ≈ weight 3300~5300（≈2%）。
    /// 幂等（同 tpl 已在容器里就跳过）、可还原（`ItemDistribution` 换回快照的原始对象引用 + transformer 转直通）、
    /// 可降级（容器不匹配只 warning 一次）。实现见 <see cref="StaticLootAddDimension"/>。
    /// </summary>
    public StaticLootAddOpt StaticLootAdd { get; set; }

    /// <summary>
    /// ★★★ ⑧-C **往商人 assort 追加一条易物**（官方 `softcore` 第 5 条「LL2 狗牌易物」）。
    ///
    /// 写法：`"traderAssortAdd": { "Therapist": [ { "reward": "&lt;要买的东西&gt;", "cost": "59f32c3b86f77472a31742f0", "costCount": 20, "loyalty": 2, "side": "Usec" } ] }`
    /// 落点：`Trader.Assort` 的**三键** —— `Items` + `BarterScheme`(JSON `barter_scheme`) + `LoyalLevelItems`(JSON `loyal_level_items`)。
    /// ⚠ 键类型是 **MongoId**，直接拿 string 当键会抛 `ArgumentException`（本项目历史坑，已在本维度内部正确转换）。
    /// 幂等（同 reward+cost 已存在则跳过）、可还原（三处一起摘）、可降级。
    /// </summary>
    public Dictionary<string, List<BarterAddDef>> TraderAssortAdd { get; set; } = new();

    /// <summary>
    /// ★★★ **新增自定义兴奋剂 buff**（条款③「致命毒素」的**「超人增益」**）。
    ///
    /// 为什么需要它：官方现成的 <c>BuffsZombieInfection</c> **带副作用**（脱水 / 听觉改变），
    /// 而用户裁决「**超人不带副作用**」⇒ 必须自己造一条干净的 buff 挂进
    /// `globals → config.Health.Effects.Stimulator.Buffs`。
    ///
    /// 写法：`"stimulatorBuffsAdd": { "Buffs_EventToxinSuperhuman": [ { "BuffType": "HealthRate", … } ] }`
    /// 落点链（全部已用真实 DLL 反射核对）：
    ///   `GlobalTable.Configuration` → `Health` → `Effects` → `Stimulator` → `Buffs`
    ///   `Buffs` 的真实类型 = **`Dictionary&lt;string, IEnumerable&lt;Buff&gt;&gt;`**
    ///   （`Models.Spt.Tables/Stimulator.cs:26`）；`Buff` 是 record（`Models.Spt.Tables/Buff.cs:10-103`），
    ///   成员 = `BuffType`(string) / `Chance`·`Delay`·`Duration`·`Value`(double) /
    ///          `AbsoluteValue`(bool) / `SkillName`(string) / `AppliesTo`(IEnumerable&lt;string&gt;)。
    ///
    /// 幂等：**键已存在且三条完全一致 ⇒ 跳过**（不覆盖 SPT 自带的同名 buff）；
    ///      已存在但不一致 ⇒ 覆盖，并把**原值登记还原**。
    /// 可还原：新键登记 `Remove`；覆盖键登记 `Restore 原值`。
    /// 可降级：链路任一环拿不到 ⇒ warning + 跳过，绝不抛。
    /// </summary>
    public Dictionary<string, List<StimulatorBuffDef>> StimulatorBuffsAdd { get; set; } = new();

    /// <summary>
    /// ★★★ **商人万圣节头像**（官方 `SeasonalEventService.AdjustTraderIcons` / `:881-894`）：
    /// 把 8 个商人（Prapor/Therapist/Fence/Skier/Peacekeeper/Mechanic/Ragman/Jaeger）的头像换成万圣节版。
    ///
    /// ⚠ **归属 = 万圣节家族级，不是按年**（官方证据，`file:line`）：
    ///   · `SeasonalEventService.cs:450` —— `AdjustTraderIcons(eventType.Type)` 是
    ///     `ApplyHalloweenEvent(...)`（`:419` 起）里**无条件调用**的一行（前后没有 `if` 守卫），
    ///     而 `ApplyHalloweenEvent` 由 `:394` 那个 `switch (eventType.Type)` 的
    ///     **`case SeasonalEventType.Halloween:`** 分支进入（枚举值，不是年份）。
    ///   · `configs/seasonalevents.json` 的 `events[]` 里万圣节条目 **`name: "halloween"`**
    ///     （家族名，**没有** `halloween2022`/`halloween2023` 这种按年键）。
    ///   ⇒ 官方 = **只要是万圣节事件就换头像**。故本字段应在**所有万圣节活动**上为 `true`。
    /// 落点见 <c>ExtraDimensions.ApplyTraderIcons</c>（**不是**官方那条死掉的
    /// `ServerImagePathOverride` —— 全 SPT 无人读它，见该方法注释里的 IL 扫描证据）。
    /// </summary>
    public bool? EventTraderIcons { get; set; }

    /// <summary>
    /// ★★★ **按配方 id 覆写藏身处配方**（官方 Heatwave「Water collector 效率」/ 啤酒 30h·1 瓶）。
    ///
    /// 写法：`"hideoutRecipeOverride": { "62a11658650cc35fa94e009a": { "productionTime": 108000, "count": 1 } }`
    /// 键 = `hideout/production.json → recipes[]._id`；不写的字段保持原值。
    ///
    /// 为什么需要**按配方**而不能用现成的 `hideoutCraftSeconds`：
    ///   `HideoutTraderDimension.cs:150-165` 的 `ClampCeiling` 是**全局上限**（遍历所有配方取最小值），
    ///   ⇒ 既不能只改啤酒，也只能**降低**不能提高。
    /// 落点：`HideoutTable.Production.Recipes[]` 的
    ///   `ProductionTime`（`Models.Eft.Hideout/HideoutProduction.cs:59-60`，`double?`）/
    ///   `Count`（同文件 `:152-153`，`int?`）。
    /// 幂等（已是目标值则跳过）、可还原（登记原值）、可降级、回读自证。
    /// </summary>
    public Dictionary<string, HideoutRecipeOverrideDef> HideoutRecipeOverride { get; set; } = new();

    /// <summary>
    /// ★★★ **集水器（Water collector）效率倍率**（官方 Heatwave 2021-07-29 第 ④ 条，逐字：
    /// *"Reduced efficiency of the Hideout module Water collector"*）。
    ///
    /// 语义：**产出效率**。`&lt;1` = 效率降低（同样产出要更久 ⇒ `ProductionTime ÷ efficiency`）；
    /// `&gt;1` = 效率提高（更快）。只作用在 **`areaType == 6`（Water Collector）** 的配方上。
    ///
    /// 为什么落 `productionTime` 而不是某个"效率字段"：
    ///   实测 `Requirement`（`Models.Eft.Hideout/Requirement.cs`）**没有** `productionTime` 成员
    ///   （只有 `TemplateId`/`Count`/`Resource`/`AreaType`/`RequiredLevel`/`QuestId`/…），
    ///   藏身处区域模型里也**没有** "Efficiency" 成员（全库 grep 0 命中）⇒
    ///   SPT 里"集水器效率"唯一可表达处就是 `production.json` 配方的 `productionTime` / `count`。
    ///
    /// ⚠ 官方**没给数值**（原文只有 "Reduced efficiency"）⇒ 倍率由 `events.json` 决定（见交付里的建议值）。
    /// 幂等：`(配方id|time)` 只缩放一次；还原后由 <c>ExtraDimensions.ResetRecipeScaled()</c> 清零。
    /// </summary>
    public double? WaterCollectorEfficiency { get; set; }
}

/// <summary>
/// 藏身处配方覆写（见 <see cref="EventDef.HideoutRecipeOverride"/>）。
/// 两个字段都可空：**只写你要改的那一个**，另一个保持原值。
/// </summary>
public class HideoutRecipeOverrideDef
{
    /// <summary>新的制作秒数（JSON `productionTime`）。不写 = 不改。</summary>
    public double? ProductionTime { get; set; }
    /// <summary>新的产出件数（JSON `count`）。不写 = 不改。</summary>
    public int? Count { get; set; }
}

/// <summary>
/// 自定义兴奋剂 buff 定义（见 <see cref="EventDef.StimulatorBuffsAdd"/>）。
/// 字段名/类型**逐一对应** SPT 的 <c>Models.Spt.Tables.Buff</c>，故可经 JSON 直接反序列化成强类型对象。
/// </summary>
public class StimulatorBuffDef
{
    /// <summary>buff 种类。本体已有的取值（实测 21 种）：
    /// <c>HealthRate</c> / <c>EnergyRate</c> / <c>HydrationRate</c> / <c>SkillRate</c> / <c>StaminaRate</c> /
    /// <c>MaxStamina</c> / <c>WeightLimit</c> / <c>HandsTremor</c> / <c>BodyTemperature</c> / <c>Antidote</c> 等。</summary>
    public string BuffType { get; set; }
    /// <summary>数值。语义随 <see cref="AbsoluteValue"/> 而变（绝对值 or 乘数）。</summary>
    public double Value { get; set; }
    /// <summary>持续时间（秒）。</summary>
    public double Duration { get; set; }
    /// <summary>生效延迟（秒）。</summary>
    public double Delay { get; set; }
    /// <summary>触发概率（0~1）。</summary>
    public double Chance { get; set; } = 1;
    /// <summary>true = <see cref="Value"/> 是绝对值；false = 是乘数（本体有 24 条用 false）。</summary>
    public bool AbsoluteValue { get; set; } = true;
    /// <summary><c>BuffType == "SkillRate"</c> 时指定技能名（如 <c>Vitality</c>）；其它类型留空字符串。</summary>
    public string SkillName { get; set; } = "";
}

/// <summary>⑨-A 藏身处升级需求定义（见 <see cref="EventDef.HideoutAddRequirements"/>）。</summary>
public class HideoutReqDef
{
    /// <summary>stages 的键（字符串 "0".."3"，对应升级到第 N 级）。</summary>
    public int Stage { get; set; }
    /// <summary>要追加的物品 tpl。</summary>
    public string TemplateId { get; set; }
    /// <summary>数量（默认 1）。</summary>
    public int Count { get; set; } = 1;
}

/// <summary>⑧-B 松散战利品权重选项（见 <see cref="EventDef.LooseLootWeightSet"/>）。</summary>
public class LooseLootWeightOpt
{
    /// <summary>刷点整体出现概率的倍率（不写 = 不改）。</summary>
    public double? ProbabilityMult { get; set; }
    /// <summary>该 tpl 在 itemDistribution 里的相对权重倍率（不写 = 不改）。</summary>
    public double? WeightMult { get; set; }
}

/// <summary>单张地图的松散战利品注入配置（见 <see cref="EventDef.LooseLootInject"/>）。</summary>
public class LooseLootInjectMapOpt
{
    /// <summary>
    /// 点位数量放大倍数（**克隆**现有刷点，不是凭空造坐标）。
    /// 同时会把 `LooseLoot.SpawnpointCount.Mean` 乘上该倍数 —— 否则局内刷出数量不变（IL 铁证见维度类注释）。
    /// 不写 = 1.0。
    /// </summary>
    public double? DensityMultiplier { get; set; }

    /// <summary>
    /// 是否同时缩放 `SpawnpointCount.Mean`（默认 **true**）。
    /// 官方 `GenerateDynamicLoot` 用 `Mean/Std` 正态抽样决定"局内抽几个点"⇒ 不缩放时加点位只是候选池变大。
    /// 若同一活动已用 `looseLootMultiplier` 提过数量、怕叠加过头，可设 false。
    /// </summary>
    public bool? ScaleSpawnpointCount { get; set; }

    /// <summary>
    /// ★ 2026-10-30【按精确坐标追加刷点】—— 用于 Scourge 的实验室任务物品。
    /// 每个元素 = 一个独立的松散战利品刷点（与 `densityMultiplier` 的"克隆"无关，是在原始数据上**新增**）。
    /// 幂等：同一 `locationId` 已存在则跳过；活动关闭时移除本次追加的点（原有点一个不动）。
    /// </summary>
    public List<LooseLootAddPointOpt> AddPoints { get; set; } = new();

    /// <summary>高价值池（私酒/颜色卡/符号钥匙 …）：`chance` 命中后按 `tpls` 的权重抽一个 tpl。</summary>
    public LooseLootPoolOpt Valuable { get; set; }

    /// <summary>日常池（纯净水/玩偶 …）：先掷 valuable，未命中再掷这一档。</summary>
    public LooseLootPoolOpt Decor { get; set; }

    /// <summary>
    /// 可选：地标包围盒（= 圆心 ± 半径）。**没有真实地标坐标就别写**（写了错坐标 = 物品飞墙上）。
    /// 每个点位只归**优先级最高**的那个盒子：`maxPoints` 小者先（更精确的区先生效）→ 盒子小者先 → 声明顺序。
    /// 见 <see cref="LooseLootRegionOpt.MaxPoints"/>（区内确定性替换）与 .ValuableBoost（超限点的概率倍率）。
    /// </summary>
    public List<LooseLootRegionOpt> Regions { get; set; } = new();
}

/// <summary>一个战利品池（见 <see cref="LooseLootInjectMapOpt.Valuable"/> / .Decor）。</summary>
public class LooseLootPoolOpt
{
    /// <summary>该档替换概率（0~1）。valuable 与 decor 是**两次独立掷骰**，先 valuable。</summary>
    public double? Chance { get; set; }

    /// <summary>tpl → 相对权重（权重 ≤0 时按 1 处理）。tpl 必须是 24 位 hex，否则运行时丢弃并 warning。</summary>
    public Dictionary<string, int> Tpls { get; set; } = new();
}

// ══════════════════════════════════════════════════════════════════════════════
//  ★ 容器战利品注入（见 EventDef.ContainerLootInject / ContainerLootInjectDimension）
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>单张地图的容器注入配置（见 <see cref="EventDef.ContainerLootInject"/>）。</summary>
public class ContainerLootInjectMapOpt
{
    /// <summary>
    /// 每个容器命中概率（0~1）。不写 = **0.25**（用户明确："每个容器 20~30% 概率多塞 1 件"，其余保持原样）。
    /// 0 = 完全关闭该图注入。
    /// </summary>
    public double? Chance { get; set; }

    /// <summary>
    /// 类别表：键 = 类别名（jacket/medbox/toolbox/safe/weaponbox/duffle，也可自定义），
    /// 值 = 该类的容器 tpl 白名单 + 注入物 tpl 池。
    /// ⚠ 同一容器 tpl 在多个类别里出现时**先声明的类别生效**，后面的重复项丢弃并 warning。
    /// </summary>
    public Dictionary<string, ContainerLootCategoryOpt> Categories { get; set; } = new();
}

// ══════════════════════════════════════════════════════════════════════════════
//  ★ 静态战利品容器注入（见 EventDef.StaticLootAdd / StaticLootAddDimension）
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 静态战利品容器注入配置（见 <see cref="EventDef.StaticLootAdd"/>）。
/// 对应 PPES 的 `AddStaticLoot(ctx, tpl, percent)` —— 但这里用**直白的相对权重** `weight`，
/// 免得"percent 换算"藏在代码里（换算公式见 <see cref="StaticLootAddDimension"/> 文件头）。
/// </summary>
public class StaticLootAddOpt
{
    /// <summary>
    /// 容器匹配关键字（大小写不敏感）。匹配顺序：
    /// <list type="number">
    /// <item><b>具名</b>：容器 `_name`（= `TemplateTable.Items[tpl].Name`，如 `Drawer` / `Jacket` / `PCblock`）
    ///   或容器 tpl 与关键字**逐字相等**；</item>
    /// <item><b>子串</b>：上面两者任意一个**包含**关键字（如 `"pc"` → `PCblock`）。</item>
    /// </list>
    /// ⚠ 有关键字命中"具名"时**只用具名结果**，不再吃子串 —— 这样 `"jacket"` 只会匹配正牌 `Jacket`，
    ///   不会把 `jacket_key204`（整箱只有 8 条、权重和 14）这种任务专用外套也算进去。
    /// 实际匹配到的容器名会打进日志。
    /// </summary>
    public List<string> Containers { get; set; } = new();

    /// <summary>
    /// 要塞的物品：tpl → **追加几条 `itemDistribution` 条目**（每条权重 = <see cref="Weight"/>）。
    /// 为什么不是"每容器几件"：SPT 是 `DrawAndRemove(件数)` —— **抽走不放回**，
    /// 同一 tpl 只有一份条目则每个容器最多出 1 件；写 2 就是"最多 2 件"（两条独立条目）。
    /// </summary>
    public Dictionary<string, int> Items { get; set; } = new();

    /// <summary>
    /// 追加条目的 `relativeProbability`（**相对权重**，不是百分比）。不写 = <c>60</c>。
    /// 单容器命中率 ≈ `1-(1-w/(Σw+w))^平均件数`；实测 P90：Drawer 6483 / Jacket 3548 / PCblock 23442
    /// ⇒ 要"翻几次就能捡到"（≈2%，与 PPES 同档）应写 **4000~5000**；60 只有 0.02%~0.06%。
    /// </summary>
    public double? Weight { get; set; }
}

/// <summary>一个"容器类别"：哪些容器算这一类 + 命中时从哪些高价值物品里抽（权重）。</summary>
public class ContainerLootCategoryOpt
{
    /// <summary>
    /// 容器 **tpl 白名单**（不是按名字匹配）。必须是 24 位 hex 且能在 TemplateTable.Items 里找到、
    /// 并且 `_props.Grids[]._props.cellsH/cellsV` 可读；否则该条丢弃并 warning。
    /// </summary>
    public List<string> Containers { get; set; } = new();

    /// <summary>
    /// 注入物：tpl → 相对权重（权重 ≤0 按 1 处理）。
    /// 命中后按权重抽 1 个；物品尺寸必须塞得进该容器的格子（允许旋转），塞不进就跳过并计数。
    /// </summary>
    public Dictionary<string, int> Items { get; set; } = new();

    /// <summary>
    /// ★ v3 新增：**可堆叠物的数量** tpl → `upd.StackObjectsCount`（不写 = 1）。
    /// 弹药（`patron_*`）与卢布这类可堆叠物如果按 1 写，玩家捡到的是"1 发子弹/1 卢布"，
    /// 所以允许在这里显式指定（超过 `_props.StackMaxSize` 时按上限截断并 warning）。
    /// 例：`"stacks": { "6768c25aa7b238f14a08d3f6": 40, "5449016a4bdc2d6f028b456f": 5000 }`
    /// </summary>
    public Dictionary<string, int> Stacks { get; set; }
}

/// <summary>地标包围盒（见 <see cref="LooseLootInjectMapOpt.Regions"/>）。x/z 为世界坐标（米）。</summary>
public class LooseLootRegionOpt
{
    /// <summary>地标名（日志里按这个名字报"命中 N 个，实际替换 M 个"）。</summary>
    public string Name { get; set; }

    /// <summary>包围盒左下角 = 圆心 − 半径（米）。只用 (x,z)。</summary>
    public LooseLootPoint2D Min { get; set; }

    /// <summary>包围盒右上角 = 圆心 + 半径（米）。只用 (x,z)。</summary>
    public LooseLootPoint2D Max { get; set; }

    /// <summary>
    /// 该盒内 valuable 概率的倍率（默认 1.0）。**只对"没被确定性替换"的点位生效**：
    /// 设了 <see cref="MaxPoints"/> 时，区内前 maxPoints 个点位直接换成 valuable（不掷骰），
    /// 剩下的点才走 `valuable.chance × valuableBoost`。写 1.0 = 与全局概率完全一致。
    /// </summary>
    public double? ValuableBoost { get; set; }

    /// <summary>
    /// ★ 区内**确定性替换**上限：命中该盒的点位按"距圆心由近到远"排序，前 maxPoints 个**直接**换成
    /// `valuable.tpls` 里的物品（不掷骰）⇒ 保证"中心最肥"；超出上限的点位照旧走全局 `valuable.chance`。
    /// 不写 / null / ≤0 = 本区不做确定性替换（退回纯概率增益）。
    /// 多个盒子重叠时 **maxPoints 小的区优先**（更精确的区先生效）；一个点位只会被替换一次。
    /// </summary>
    public int? MaxPoints { get; set; }

    /// <summary>
    /// ★ 锚点（可选，3D 坐标）：写了它就按"到锚点的**真实 3D 距离**"由近到远挑点
    /// （不写 = 老行为，按到盒心的水平 2D 距离），日志里也按锚点报距离。
    /// 用途：盒子是"圆心 ± 半径"的粗包围盒，真正要贴的是某个具体物件（例：小黄车车头）；
    /// 用 3D 距离才能排掉"同一 x/z、但站在二层/地下"的点位。
    /// </summary>
    public LooseLootPoint3D Anchor { get; set; }

    /// <summary>锚点的中文短名（默认「锚点」）。例 `"车"` ⇒ 日志 `… 选区替换点 (x,y,z) 距车 4.51 米`。</summary>
    public string AnchorName { get; set; }

    /// <summary>
    /// ★★★ **局内必刷**保证：区内**按距离最近的**前 `guaranteedPoints` 个替换点，把
    /// `Spawnpoint.Probability` 设成 **1.0**。0 / null = 不保证（默认，老行为）。
    ///
    /// 依据（SPT 4.1.6 `LocationLootGenerator.GenerateDynamicLoot` 反编译，逐字）：
    /// <code>
    /// else if (spawnpoint.Template.IsAlwaysSpawn != true) {
    ///     if (spawnpoint.Probability == 1.0) list3.Add(spawnpoint);              // ← 必刷
    ///     else probabilityObjectArray.Add(new ProbabilityObject(…, spawnpoint.Probability…));
    /// }
    /// …
    /// double num2 = num - list4.Count;
    /// if (num2 > 0) foreach (var item in probabilityObjectArray.DrawAndRemove((int)num2)) list4.Add(…);
    /// </code>
    /// ⇒ `Probability == 1.0` 的点**根本不进概率抽取**、每局直接进 `list4` ⇒ 局内必有这一件。
    ///
    /// 为什么开这个开关（实测数，不是推测）：interchange 盒内最近的 2 个点
    /// `probability = 0.437 / 0.411`（Σp = 578.03），而
    /// `num = round(LooseLootMultiplier 2.8 × Normal(621.244, 18.379)) ≈ 1739` 要从 3820 个点里抽
    /// ⇒ 单点落地率 ≈ `1 - exp(-1739 × 0.437 / 578.03) ≈ 73%`（另一点 ≈71%）
    /// ⇒ **那次跑过去一件都没有的概率 ≈ 8%**。它不是"看不到"的主因（主因是外部模型错了），
    /// 但一个寻物任务的关键道具没理由留 8% 的空白 ⇒ 最近的那个点设成必刷。
    /// </summary>
    public int? GuaranteedPoints { get; set; }
}

/// <summary>包围盒角点（只用水平面 x/z —— y 不参与，避免地形高低差导致漏判）。</summary>
public class LooseLootPoint2D
{
    public double X { get; set; }
    public double Z { get; set; }
}

/// <summary>3D 锚点（与 looseLoot 的 `Position` 同一坐标系：x/z 水平、y 垂直）。</summary>
public class LooseLootPoint3D
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

/// <summary>② 首领时刻表定义（见 <see cref="EventDef.BossTimetable"/>）。</summary>
public class BossTimetableDef
{
    /// <summary>用哪个时区判定（官方表头；默认 MSK = UTC+3）。</summary>
    public string Tz { get; set; } = "MSK";
    /// <summary>24 小时制时段 → 地图（map 为空 = 该时段无首领图）。</summary>
    public List<TimetableRow> Rows { get; set; } = new();
    /// <summary>要放到该时段地图上的首领（官方 Boss locations 里五张图同一组）。</summary>
    public List<string> Bosses { get; set; } = new();
    /// <summary>可选：每个首领的随从数（不写则用模板/本体）。</summary>
    public Dictionary<string, int> Escorts { get; set; }
}

public class TimetableRow
{
    public int From { get; set; }
    public int To { get; set; }
    public string Map { get; set; }
}

/// <summary>① 商人易物注入条目（见 <see cref="EventDef.TraderBarterAdd"/>）。</summary>
public class BarterAddDef
{
    /// <summary>奖励物品 tpl（商人给的）。</summary>
    public string Reward { get; set; }
    /// <summary>
    /// 奖励物品一次**交付**的件数。
    /// <para>⚠ v2.6.74 起**不再参与库存**：旧实现把它写进 `upd.StackObjectsCount`，等于"总共只有
    /// `rewardCount` 件、卖完必须重启服务端"（SPT 的补货**不恢复存量**）。现在存量固定写
    /// <c>9999999</c>（本体同款），玩家实际拿到几件由客户端请求的购买数量决定
    /// （`TradeHelper.BuyItem` 会把交付物的 `StackObjectsCount` 重写成 `min(StackMaxSize, 请求数)`）。
    /// 保留本字段只为**向后兼容既有 events.json 数据**，语义上已无作用。</para>
    /// </summary>
    public int RewardCount { get; set; } = 1;
    /// <summary>需要的物品 tpl（玩家交的）。</summary>
    public string Cost { get; set; }
    public int CostCount { get; set; } = 1;
    /// <summary>忠诚等级（默认 1）。</summary>
    public int Loyalty { get; set; } = 1;
    /// <summary>
    /// ★ v2.6.74 **每次补货的限购件数**（`upd.BuyRestrictionMax`；`upd.BuyRestrictionCurrent` 恒写 0 作种子）。
    /// <para>`0`（或负数）= **不限购**（两个字段都不写 ⇒ SPT 的 `HasBuyRestrictions()` 恒 false）。</para>
    /// <para>与**存量**无关：存量恒 9999999（等价无限），本字段只控制"一个补货周期内能买几件"。
    /// 重置时机 = 该商人补货（`TraderController.Update` → `ResetTraderPurchasesStoredInProfile`），
    /// **不需要重启服务端**。语义与重置链路的 file:line 证据见
    /// <see cref="TraderAssortAddDimension"/> 类注释的「限购语义」一节。</para>
    /// </summary>
    public int MaxPerRestock { get; set; } = 1;

    // ── ★ task-34 ⑧-C（`traderAssortAdd` 专用，`traderBarterAdd` 不读这两个）──
    /// <summary>
    /// 付款物品的**狗牌等级要求**（`BarterScheme.Level`，JSON 名 `level`）。
    /// 官方狗牌易物都带这个：例 Therapist 要 `level: 15` 的狗牌。默认 1 = 不限等级。
    /// </summary>
    public int CostLevel { get; set; } = 1;
    /// <summary>
    /// 付款物品的**阵营限定**（`BarterScheme.Side`，JSON 名 `side`）。
    /// 取值 `"Any"` / `"Bear"` / `"Usec"`（例 Prapor 那条是 `"Usec"`、Skier 那条是 `"Bear"`）。
    /// 留空 = 不写该字段（跟随本体默认，通常是 Any）。
    /// </summary>
    public string Side { get; set; }
}

/// <summary>路费机制定义（见 <see cref="EventDef.Toll"/>）。</summary>
public class TollDef
{
    /// <summary>豁免等级（官方 16）。</summary>
    public int ExemptLevel { get; set; } = 16;
    /// <summary>当期免费图（留空则按 <see cref="Rotation"/> + <see cref="RotationMode"/> 推算）。</summary>
    public string FreeMap { get; set; }
    /// <summary>免费图顺序（官方顺序，见类注释）。</summary>
    public List<string> Rotation { get; set; } = new();
    /// <summary>`day`（默认，每天推进一格）或 `hours`（每 <see cref="RotationHours"/> 小时一格）。</summary>
    public string RotationMode { get; set; } = "day";
    public int RotationHours { get; set; } = 6;
    /// <summary>各图所需物品（`{"<map>": [{"tpl":"…","count":1}]}`；`"*"` = 兜底）。官方未公布 ⇒ 自行配置。</summary>
    public Dictionary<string, List<TollItemDef>> ItemsPerMap { get; set; } = new();
    /// <summary>
    /// ★ 路费物品**从每日任务的上交物品池里抽**（用户指定）——来源
    /// `SPT_Data/database/templates/repeatableQuests.json → data.completion.itemsWhitelist`，
    /// 按玩家等级过滤（组内 `minPlayerLevel`），数量取 `questConfig.Completion[].requestedItemCount` 范围。
    /// 同一（地图, 日期）当天固定，便于日志与排查。
    /// </summary>
    public bool UseDailyQuestPool { get; set; } = true;

    /// <summary>
    /// ★ 机制模式：`accessKey`（默认，**和实验室门卡一样** —— 用本体 `location.Base.AccessKeys`：
    /// 进图前必须带在身上、进图后自动消失，由游戏本身执行）；`serverDeduct` = 由本 mod 在服务端扣存档。
    /// </summary>
    public string Mode { get; set; } = "accessKey";

    /// <summary>
    /// ★ **测试期信物**：非空时，所有非免费图都改用它（而不是从池子里抽随机物品）。
    /// 想恢复"随机物品"把它清空即可（`[]` 或删掉该字段）。
    /// </summary>
    public List<string> TestItemTpls { get; set; } = new();

    /// <summary>true = 未交路费就**拦下进图**；false（默认）= 只记警告并放行。</summary>
    public bool Strict { get; set; }
}

public class TollItemDef
{
    public string Tpl { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>结盟对（见 <see cref="EventDef.ForceAllyPairs"/>）。</summary>
public class AllyPairDef
{
    /// <summary>地图 key。</summary>
    public string Map { get; set; }
    /// <summary>角色 A（如 bossKnight）。</summary>
    public string A { get; set; }
    /// <summary>角色 B（如 bossSanitar）。</summary>
    public string B { get; set; }
}

/// <summary>
/// ★★ `escortCount` 的值：**单值或范围**（task-33 追加）。
///
/// <para>为什么需要：官方 `april_fools` 的 `halloweennightcult` 随从数是「**0~2**」范围，
/// 而旧字段是 <c>int</c>，写成范围就解析不了。这里做成一个类型，JSON 三种写法都收：</para>
/// <list type="bullet">
///   <item><c>2</c> —— 纯数字（**向后兼容**，旧 events.json 不用改）</item>
///   <item><c>"0-2"</c> —— 字符串范围</item>
///   <item><c>[0,2]</c> —— 数组范围</item>
/// </list>
/// <para>输出：<see cref="ToAmountString"/> 给出要写进 <c>BossEscortAmount</c> 的字符串 ——
/// 单值原样（<c>"2"</c>），范围保持 <c>"0-2"</c>（客户端字段本身就是字符串，官方表里
/// 还见过 <c>"0,0,0,0,2,2,2,1,1,1,1,1,0,2,30"</c> 这种列表形式）。</para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(EscortCountValueConverter))]
public class EscortCountValue
{
    /// <summary>下界（单值时 == <see cref="Max"/>）。</summary>
    public int Min { get; set; }

    /// <summary>上界。</summary>
    public int Max { get; set; }

    /// <summary>是否是一个范围（下界 ≠ 上界）。</summary>
    public bool IsRange => Max != Min;

    /// <summary>写进 `BossEscortAmount` 的字符串：单值 `"2"`、范围 `"0-2"`。</summary>
    public string ToAmountString() => IsRange ? $"{Min}-{Max}" : Min.ToString();

    public override string ToString() => ToAmountString();

    public static implicit operator EscortCountValue(int v) => new() { Min = v, Max = v };
}

/// <summary>
/// `EscortCountValue` 的 JSON 转换器：接受 **数字 / 字符串 "a-b" / 数组 [a,b]**；写出时按字符串。
/// 容错：解析不出就退化成 0（配合消费端的 try/catch，绝不因数据笔误让服务端崩）。
/// </summary>
public class EscortCountValueConverter : System.Text.Json.Serialization.JsonConverter<EscortCountValue>
{
    public override EscortCountValue Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        try
        {
            switch (reader.TokenType)
            {
                case System.Text.Json.JsonTokenType.Number:
                    {
                        var n = reader.GetInt32();
                        return new EscortCountValue { Min = n, Max = n };
                    }
                case System.Text.Json.JsonTokenType.String:
                    {
                        var s = (reader.GetString() ?? "").Trim();
                        var parts = s.Split(new[] { '-', '~', ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))
                            return new EscortCountValue { Min = a, Max = b };
                        if (parts.Length == 1 && int.TryParse(parts[0], out var one))
                            return new EscortCountValue { Min = one, Max = one };
                        return new EscortCountValue();
                    }
                case System.Text.Json.JsonTokenType.StartArray:
                    {
                        var vals = new List<int>();
                        reader.Read();
                        while (reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
                        {
                            if (reader.TokenType == System.Text.Json.JsonTokenType.Number) vals.Add(reader.GetInt32());
                            reader.Read();
                        }
                        if (vals.Count >= 2) return new EscortCountValue { Min = vals[0], Max = vals[1] };
                        if (vals.Count == 1) return new EscortCountValue { Min = vals[0], Max = vals[0] };
                        return new EscortCountValue();
                    }
                default:
                    reader.Skip();
                    return new EscortCountValue();
            }
        }
        catch { return new EscortCountValue(); }
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, EscortCountValue value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(value.ToAmountString());
    }
}

/// <summary>
/// 单个 bot role 的装备升级规格（见 <see cref="EventDef.BotGearUpgrade"/>）。
/// 三项都可独立开关；全部字段缺省时该 role 不会被改动。
/// </summary>
public class BotGearUpgradeDef
{
    /// <summary>目标 bot role（**大小写不敏感**，如 `arenaFighterEvent`）。由 Plugin 从字典键回填。</summary>
    public string Role { get; set; }

    /// <summary>true = 把该 bot 身上所有弹药的 <c>_tpl</c> 换成**同口径全库最优**（穿透 → 伤害 → 甲伤）。默认 false。</summary>
    public bool BetterAmmo { get; set; }

    /// <summary>true = 把每块已装插板换成**它所在槽的 filter 内最优**（ArmorType → MaxDurability → BluntThroughput）。默认 false。</summary>
    public bool BetterPlates { get; set; }

    /// <summary>可选：换成这把枪（tpl）。空 = 不换枪。换枪时按新枪的 <c>mod_magazine</c> 槽 filter 重挑弹匣。</summary>
    public string WeaponTpl { get; set; }

    /// <summary>可选：显式指定弹匣 tpl。空 = 自动从新枪的 <c>mod_magazine</c> filter 里挑容量最大的。</summary>
    public string MagazineTpl { get; set; }

    /// <summary>换枪时是否清掉旧枪的其他配件（它们未必能装到新枪上）。默认 true。</summary>
    public bool ClearOldMods { get; set; } = true;

    /// <summary>
    /// ★ 大弹鼓路线（官方 `Permanent Boss Presence` 2026-07-29「high-capacity drum magazines」）：
    /// 把该 bot **现有主武器**的弹匣换成"该武器 `mod_magazine` 槽 filter 里容量最大"的那个
    /// （容量取 `Slot.MaxCount`，JSON 名 `_max_count`）。**从武器自己的 filter 里挑 ⇒ 必然装得上**。
    /// ⚠ 实测本机 11 个鼓里有 **5 个是死条目**（没有任何枪接受：PM 84发 / Glock 50发 / M14 X-14 /
    /// SA58 X-FAL / MPX 50发）⇒ 用"从 filter 里挑"就自动避开它们。
    /// </summary>
    public bool BiggestMagazine { get; set; }

    /// <summary>
    /// ★ 下挂榴弹路线：给该 bot 的**现有主武器**加一个 `mod_launcher` 子件。
    /// **必须命中该武器自己的 `mod_launcher` 槽 filter**（否则跳过 —— 官方数据里
    /// `M203`/`FN40GL Mk2`/`FN GL-40` 对多数枪根本不适配，硬塞会做出非法搭配）。
    /// </summary>
    public string AddLauncherTpl { get; set; }

    /// <summary>给"该武器已装/新加的 `mod_launcher`"补的榴弹 tpl（作为该下挂的子物品）。</summary>
    public string LauncherAmmoTpl { get; set; }

    /// <summary>★ 追加弹药：把这些 tpl 作为**新物品**塞进该 bot 的 `Pockets` 容器（火箭筒/下挂的 40mm 弹走这条）。</summary>
    public List<string> ExtraAmmoTpls { get; set; } = new();

    /// <summary>
    /// SCAV low-durability western weapons (official events_wiki.txt:773 verbatim:
    /// "Scavs have a chance to spawn with nearly destroyed western weapons, modded similarly to the Rogues.").
    /// Semantics: set this bot's WEAPONS (those whose _props has WeapClass, i.e. firearms; melee has none)
    /// Item.Upd.Repairable.Durability = MaxDurability * value / 100, while MaxDurability KEEPS the weapon's
    /// original value (do not shrink it too, or client/repair logic would think that is its natural state).
    /// 1..99 = active, 0 = off. Mods/attachments are NOT touched.
    /// </summary>
    public int DurabilityPercent { get; set; }

    /// <summary>
    /// Only apply to "western" weapons (used with DurabilityPercent).
    /// Criterion: in this DB every weapon's items.json._name is "weapon_&lt;maker&gt;_..." so we classify by the
    /// maker token against a curated western-maker allowlist (see WesternMakers in EventGearUpgrade).
    /// NOTE: that allowlist is hand-curated, NOT a data field.
    /// </summary>
    public bool WesternOnly { get; set; }
}

/// <summary>
/// 单个 bot role 的"模板补全"规格（见 <see cref="EventDef.BotAmmoAdd"/>）。
/// 两个字段都可独立使用；都为空则该 role 不会被改动。
/// </summary>
public class BotTemplateFixDef
{    /// <summary>目标 bot role（**大小写不敏感**，如 `ravangezryachiyevent`）。由 Plugin 从字典键回填。</summary>
    public string Role { get; set; }

    /// <summary>要补的 **口径 → 弹药 tpl**：<c>{ "Caliber9x21": "5a26abfac4a28232980eabff" }</c>。
    /// 口径已存在 ⇒ 只往该口径池子里**追加**缺失的 tpl（不动原有条目）；整个口径缺失 ⇒ 新建池子。</summary>
    public Dictionary<string, string> Ammo { get; set; } = new();

    /// <summary>要补的 **装备槽 → { tpl: 权重 }**：<c>{ "Holster": { "59f98b4986f7746f546d2cef": 1 } }</c>。
    /// ⚠ 光补 <see cref="Ammo"/> 没用 —— 若模板里连该口径的**枪**都没有，必须同时补槽。</summary>
    public Dictionary<string, Dictionary<string, double>> Equipment { get; set; } = new();
}

/// <summary>
/// ★★★「整体替换 bot 装备池」的规格（见 <see cref="EventDef.BotEquipmentReplace"/>）。
/// <para>与 <c>botAddItems</c>（往池子里**追加**条目）不同：这里把整个槽位的池子**换成**给定集合，
/// 原池对象备份、还原时原样写回。</para>
/// </summary>
public class BotEquipmentReplaceSpec
{
    /// <summary>
    /// 生效地图 id 列表（小写，与 <c>bot.json → maxBotCap</c> / <c>location.Base.Id</c> 同格式：
    /// <c>bigmap</c> 海关 / <c>woods</c> 森林 / <c>interchange</c> 立交桥 / <c>lighthouse</c> 灯塔 /
    /// <c>shoreline</c> 海岸线）。空 = 不按图过滤（全图生效）。
    /// </summary>
    public List<string> Maps { get; set; } = new();

    /// <summary>
    /// <c>{"角色": {"槽位": {"tpl": 权重}, "mods": {"&lt;护甲 tpl&gt;": {"&lt;槽位名&gt;": {"&lt;插板 tpl&gt;": 1}}}}}</c>，例：
    /// <code>
    /// "roles": {
    ///   "arenaFighterEvent": {
    ///     "FaceCover":  { "67a5c657782ce4655104db16": 1 },
    ///     "ArmorVest":  { "5c0e51be86f774598e797894": 1 },
    ///     "mods": {
    ///       "5c0e51be86f774598e797894": {
    ///         "Front_plate": { "656f603f94b480b8a500c0d6": 1, "656f611f94b480b8a500c0db": 1 },
    ///         "Back_plate":  { "656efaf54772930db4031ff5": 1, "656efd66034e8e01c407f35c": 1 }
    ///       }
    ///     }
    ///   }
    /// }
    /// </code>
    /// 角色名/槽位名**大小写不敏感**；装备槽位必须是该角色 <c>BotInventory.Equipment</c> 里**已存在**的键；
    /// <c>mods</c> 的键 = <c>BotInventory.Mods</c> 里该护甲的 tpl（不存在时由维度**新建**并在还原时整键删除）。
    /// </summary>
    public Dictionary<string, BotEquipmentReplaceRole> Roles { get; set; } = new();
}

/// <summary>
/// 一个 bot 角色的替换规格（= <see cref="BotEquipmentReplaceSpec.Roles"/> 的值）。
///
/// <para><b>为什么需要自定义 JSON 转换器</b>：JSON 里 <c>mods</c> 与装备槽名是**同一层**的兄弟键
/// （见 <see cref="BotEquipmentReplaceSpec.Roles"/> 的示例），但它们的结构深度不同：</para>
/// <list type="bullet">
/// <item>装备槽：<c>{"槽位": {"tpl": 权重}}</c> → <see cref="Slots"/></item>
/// <item><c>mods</c>：<c>{"护甲 tpl": {"槽位名": {"插板 tpl": 1}}}</c> → <see cref="Mods"/></item>
/// </list>
/// <para>⇒ 用一个键名为 <c>mods</c> 的特例把两者拆开，其余键**原样**当作装备槽
/// （大小写不敏感）。这样 events.json 的形状保持自然，也不需要把 <c>"mods"</c> 挪到别处。</para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(BotEquipmentReplaceRoleConverter))]
public class BotEquipmentReplaceRole
{
    /// <summary>装备槽 → (tpl → 权重)，原 <c>Roles</c> 的形状一字不变。</summary>
    public Dictionary<string, Dictionary<string, double>> Slots { get; set; } = new();

    /// <summary>
    /// 护甲 tpl → (槽位名 → (插板 tpl → 1))。值是 <c>BotInventory.Mods</c> 里那个
    /// <c>HashSet&lt;MongoId&gt;</c> 的内容 —— **只表达"有哪些"，权重无意义**（写 1 即可，≤0 会被跳过）。
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, double>>> Mods { get; set; } = new();
}

/// <summary>
/// <see cref="BotEquipmentReplaceRole"/> 的 JSON 转换器：把 <c>mods</c> 键读进 <see cref="BotEquipmentReplaceRole.Mods"/>，
/// 其余键读进 <see cref="BotEquipmentReplaceRole.Slots"/>。写出时反向展开（便于回写/调试）。
/// </summary>
public class BotEquipmentReplaceRoleConverter : System.Text.Json.Serialization.JsonConverter<BotEquipmentReplaceRole>
{
    private static readonly System.Text.Json.JsonSerializerOptions Sub = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public override BotEquipmentReplaceRole Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
                                                 System.Text.Json.JsonSerializerOptions options)
    {
        var role = new BotEquipmentReplaceRole();
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject) { reader.Skip(); return role; }

        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject) break;
            if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) { reader.Skip(); continue; }

            var name = reader.GetString();
            if (!reader.Read()) break;

            if (string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase))
            {
                role.Mods = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, double>>>>(ref reader, Sub)
                    ?? new Dictionary<string, Dictionary<string, Dictionary<string, double>>>();
            }
            else
            {
                var pool = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, double>>(ref reader, Sub);
                if (pool != null && !string.IsNullOrEmpty(name)) role.Slots[name] = pool;
            }
        }
        return role;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, BotEquipmentReplaceRole value,
                               System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value?.Slots != null)
        {
            foreach (var kv in value.Slots)
            {
                writer.WritePropertyName(kv.Key ?? "");
                System.Text.Json.JsonSerializer.Serialize(writer, kv.Value, Sub);
            }
        }
        if (value?.Mods != null && value.Mods.Count > 0)
        {
            writer.WritePropertyName("mods");
            System.Text.Json.JsonSerializer.Serialize(writer, value.Mods, Sub);
        }
        writer.WriteEndObject();
    }
}

/// <summary>★ 第 12 项：一个 bot 角色的背包注入声明（见 <see cref="EventDef.FactionLoot"/>）。</summary>
public class FactionLootDef
{
    /// <summary>把该角色的 `EquipmentChances["Backpack"]` 改成 100（PPES 同款：克隆源 Rogue 经常没背包）。</summary>
    public bool EnsureBackpack { get; set; }

    /// <summary>tpl → share（0~1）。按 PPES 的 `AddShare` 公式换算成背包物品池权重。</summary>
    public Dictionary<string, double> Items { get; set; } = new();
}

/// <summary>★ 万圣节感染者：逐图感染率下限（见 <see cref="EventDef.ZombieOutbreak"/>）。</summary>
public class ZombieOutbreakDef
{
    /// <summary>所有图：若现值 &lt; 该数则提到该数（缺省 40，与 PPES 1.4.0 ApplyOutbreak2026 一致）。</summary>
    public double? EveryMapMin { get; set; }

    /// <summary>个别图单独指定下限（键 = `mapInfectionAmount` 里的图名，大小写不敏感）。</summary>
    public Dictionary<string, double> Extra { get; set; } = new();
}

/// <summary>
/// ★ 按精确坐标追加的一个松散战利品刷点（见 <see cref="LooseLootInjectMapOpt.AddPoints"/>）。
/// <para>两种写法二选一：只给 <see cref="Tpl"/>（自动造单物品模板），或给 <see cref="ItemsJson"/>
/// （完整 `SpawnpointTemplate` JSON，高级用法）。</para>
/// </summary>
public class LooseLootAddPointOpt
{
    /// <summary>坐标：`"(x, y, z)"`。小数点支持 `.` 与俄式 `,`（`-167,97823` = `-167.97823`）。</summary>
    public string LocationId { get; set; }

    /// <summary>该点抽取权重（缺省 1.0 = 每局必刷，与 SPT `Probability==1.0` 语义一致）。</summary>
    public double? Probability { get; set; }

    /// <summary>物品 tpl（24 位 hex）。给了它就自动造"单物品 SpawnpointTemplate"。</summary>
    public string Tpl { get; set; }

    /// <summary>
    /// 可选：直接给完整 `SpawnpointTemplate` JSON（结构同 `looseLoot.json` 里的 `template`），
    /// 位置/Id/Root 会按需要覆盖。与 <see cref="Tpl"/> 二选一（都写时以 ItemsJson 为准）。
    /// </summary>
    public string ItemsJson { get; set; }
}
