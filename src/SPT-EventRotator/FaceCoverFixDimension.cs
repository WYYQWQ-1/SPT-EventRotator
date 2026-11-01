using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace EventRotator;

/// <summary>
/// ★★★【面具后置强制修正】`casus_belli`（战争之因）劫掠者（<c>arenaFighterEvent</c>）**生成之后**
/// 把 <c>FaceCover</c> 槽换成 6 款彩绘 CQCM 面具。
///
/// <para><b>为什么需要这一层（实机问题 + 反编译证据）</b>：</para>
/// <list type="number">
/// <item>本机同时装了 <c>acidphantasm-progressivebotsystem</c>（PBS/APBS，**管装备生成**）。
///   它用 Harmony **Prefix 拦截**了 <c>BotInventoryGenerator.GenerateInventory</c> 且返回 false
///   （<c>audit/pbs_full/ProgressiveBotSystem.Patches/GenerateInventoryPatch.cs:56-72,128-129</c>），
///   整条装备生成改走它自己的 <c>CustomBotInventoryGenerator</c>。</item>
/// <item>该生成器对**每个槽**取的是 **PBS 自己的 tier 池**，不是我们的 <c>BotTable</c> 模板池：
///   <c>CustomBotInventoryGenerator.cs:154</c>
///   <c>var equipmentByBotRole = _botEquipmentHelper.GetEquipmentByBotRole(botGenerationDetails.RoleLowercase, tierNumber);</c>
///   ⇒ <c>:203-204</c> <c>RootEquipmentSlot = FaceCover, RootEquipmentPool = equipmentByBotRole[FaceCover]</c>。
///   概率也取 PBS 自己的 <c>:99</c> <c>GetChancesByBotRole(...)</c> ⇒ <c>chances.equipment.FaceCover=100</c> 同样被无视。</item>
/// <item><c>arenafighterevent</c> 在 PBS 里映射到 **ExUsec** 那一份数据
///   （<c>BotEquipmentHelper.cs:210-212</c> <c>case "arenafighter": case "exusec": case "arenafighterevent": return tierEquipment.ExUsec.Equipment;</c>；
///   概率同理 <c>:278-280</c>），而 <c>BotActivityHelper</c> 把它当 **SpecialBot**
///   （<c>ProgressiveBotSystem.Constants/SpecialBots.cs:7</c>），
///   <c>IsBotDisabled → !config.SpecialBots.Enable</c>，本机 <c>specialBots.enable = true</c>
///   ⇒ **PBS 确实接管**。</item>
/// <item>数据层铁证：6 款面具 tpl 在 PBS 的 <c>Data/Equipment/Tier0..7_equipment.json</c> 里
///   **一个都不存在**；而 <c>ExUsec.FaceCover</c> 的 10 件池子里包含
///   <c>572b7fa524597762b747ce82</c>（Lower half-mask = 中文「半面巾」）与
///   <c>5e54f76986f7740366043752</c>（Shroud half-mask），Tier1..7 的 <c>FaceCover</c> 概率 = **75**
///   ⇒ 实机看到的就是「75% 概率 × 10 件里抽到半面巾」。
///   <para>⇒ **我们原来的 <c>botEquipmentReplace</c>（改 <c>BotTable</c> 模板池 + <c>chances</c>）在 PBS 接管下是死代码**。
///   这是"生成**前**"的池子被整体替换，postfix 改不到；但**生成后的 BotBase 是我们的**。</para></item>
/// </list>
///
/// <para><b>落点</b>：<c>BotGenerator.PrepareAndGenerateBot</c> 的 postfix（<
/// <c>Patches/BossWavePatch.cs::BotGenerated(object __1, object __result)</c>，本来就已挂载）。
/// 该方法在 SPT 里是 <c>BotGenerator.cs:111 → :120 GenerateBot → :206 botInventoryGenerator.GenerateInventory</c>
/// 之后返回，<c>__result</c> 是**最终的 BotBase**（PBS 接管与否都由它产出）
/// ⇒ 在这里直接改 <c>Inventory.Items</c>，PBS 绕不过去。</para>
///
/// <para><b>调用链实证（2026-10-24，逐条对 decompile 核对，回答"PBS 是否绕过本 postfix"）</b>：</para>
/// <list type="number">
/// <item>SPT 侧：<c>BotController.TryGenerateSingleBot:206</c> → <c>BotGenerator.PrepareAndGenerateBot:111</c>
///   → <c>:120 GenerateBot</c> → <c>:206 BotInventoryGenerator.GenerateInventory</c>（一行直调，**中间无分支**）。</item>
/// <item>PBS（`acidphantasm-progressivebotsystem`）**只做两件事**，都在这条链的**内部**：
///   <c>Patches.GenerateInventoryPatch</c>（`[PatchPrefix]` 打完自己那套装备后 `return false`）与
///   <c>Patches.GenerateBotPatch</c>（`[PatchPostfix]` 只往 `bot.Info.ExtensionData["Tier"]` 写一个数）。
///   ⇒ **没有**任何 Prefix 替换 <c>PrepareAndGenerateBot</c> 本身，**不存在绕过**。</item>
/// <item>ABPS（`acidphantasm-botplacementsystem`）与 MoreBots 的 DLL 里**完全没有**
///   `PrepareAndGenerateBot` / `GenerateBot` / `GenerateInventory` 这些字符串 ⇒ 它们不碰 bot 生成（只管刷点/首领表）。</item>
/// <item>⚠ 本机 4.1.6 的 `BotGenerationDetails` **只有 `Location`**（`BotGenerationDetails.cs:259`，JSON 名 = CLR 名），
///   没有 `LocationId`；`BotGenerationDetails.Role/EventRole/RoleLowercase` 都是 **`string`**
///   （不是 `WildSpawnType` 枚举）—— 见 <see cref="IsTargetRole"/> / <see cref="ResolveLocation"/> 的注释。</item>
/// </list>
///
/// <para><b>只做三件事</b>（严格限定范围）：</para>
/// <list type="bullet">
/// <item>role（大小写不敏感）必须 = <c>arenaFighterEvent</c>（四路来源任一命中，见 <see cref="IsTargetRole"/>），否则立刻返回；</item>
/// <item><c>BotGenerationDetails.Location</c> 必须在本活动的 <c>botEquipmentReplace.maps</c> 里
///   （casus_belli = bigmap / woods / interchange / lighthouse / shoreline）；
///   地图读不到 ⇒ **跳过 + 每局 warning 一次**（宁可不改，也不全图乱改）；</item>
/// <item>找 <c>SlotId == "FaceCover"</c> 的那件：
///   · 有 ⇒ **只**把 <c>_tpl</c> 换成 6 款里等概率随机的 1 款（<c>_id</c> / <c>upd</c> 原样保留）；
///   · 没有 ⇒ 新造一件塞进 <c>Inventory.Items</c>（写法照 SPT
///     <c>BotInventoryGenerator.GenerateEquipment:466-473</c>：<c>_id</c> 新生成、<c>slotId="FaceCover"</c>、
///     <c>parentId = Inventory.Equipment</c>、<c>upd={StackObjectsCount:1}</c>
///     —— 注意 SPT 的 <c>GenerateExtraPropertiesForItem</c> 对这 6 款（只有 MaxDurability、无 ArmorClass/WeapClass）
///     产出的也正是 "只有 StackObjectsCount" 的 upd）。</item>
/// </list>
///
/// <para><b>诊断（★只读，不改行为）</b>：本文件下半部分有一路独立统计 —— 每局一条
/// <c>[面具修正][诊断] 本局收到 N 只 bot｜role Top5｜Location 样例｜role/地图/替换/补件/幂等/无Location 各命中几次</c>，
/// 外加进程内首次调用的一条「已接管 bot 生成」，用于把"没进来 / role 不匹配 / Location 不匹配"三种静默失败分开。
/// 见 <see cref="OnRaidStart"/> 与 <see cref="FlushDiag"/>。</para>
///
/// <para><b>为什么保留 <c>_id</c></b>：bot 的物品树按 <c>parentId → _id</c> 挂接，换 tpl 不动 id
/// ⇒ 挂在这件装备下的子物品（面罩挂件等）不会悬空；客户端也只需重建这一件的模型。</para>
///
/// <para><b>可能副作用（诚实声明）</b>：① 这 6 款面具 <c>BlocksEyewear/BlocksHeadwear = true</c>
/// ⇒ 原本 PBS 给的面罩可能与眼镜/头盔并存，换成面具后客户端会把冲突的 Headwear/Eyewear 视觉上遮住（服务端不动它们）；
/// ② 保留旧 <c>upd</c> 时，若原物有 Durability，新面具会显示旧耐久（本机这 6 款与半面巾的 upd 实际只有 StackObjectsCount）；
/// ③ PBS 下一局仍会按它自己的池子生成（我们改的是**产出物**，不是它的池子）⇒ 每局都要靠本 postfix 再修一次，这是设计；
/// ④ 只在活动登记时武装（<see cref="Apply"/>），活动关掉即失效，没有常驻污染。</para>
/// </summary>
internal static class FaceCoverFixDimension
{
    /// <summary>目标角色（PBS 与 BotTable 都是这个名字，大小写不敏感比较）。</summary>
    private const string TargetRole = "arenaFighterEvent";

    /// <summary>要修正的装备槽（<c>Item.SlotId</c> 的取值）。</summary>
    private const string TargetSlot = "FaceCover";

    /// <summary>Info 日志通道（Plugin 注入）。</summary>
    public static Action<string> Log;

    /// <summary>
    /// ★★★ v1.0.2【问题 B】运行期 `TemplateTable`（Plugin 注入）。
    /// 用途：在读/写装备前判断**头盔是否阻挡面罩**（`TemplateItem.Properties.BlocksFaceCover`）——
    /// 这是客户端/官方生成器共用的真实判定（`BotGeneratorHelper.IsItemIncompatibleWithCurrentItems:325`
    /// 就是 `itemToEquip.Properties.BlocksFaceCover ?? false`）。注入运行期那份，避免 prepatch 双载读错表。
    /// </summary>
    public static object TemplateTable;

    /// <summary>非目标时也不动的"不阻挡 FaceCover 的通用头盔"替补（顺序即优先顺序；全部实测 BlocksFaceCover=false）。</summary>
    private static readonly string[] NonBlockingHeadwear =
    {
        "5b432d215acfc4771e1c6624",   // LZSh
        "5b40e1525acfc4771e1c6611",   // ULACH
        "5aa7e4a4e5b5b000137b76f2",   // ZSh-1-2M
        "5645bc214bdc2d363b8b4571",   // Kiver-M
        "5ac8d6885acfc400180ae7b0",   // FAST MT
        "5aa7d193e5b5b000171d063f",   // SFERA-S
    };

    /// <summary>`tpl → BlocksFaceCover` 缓存（null = 查不到；不影响功能，只是下一只重查）。</summary>
    private static readonly Dictionary<string, bool?> _blocksCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _tplLock = new();

    /// <summary>Warning 日志通道（Plugin 注入）。</summary>
    public static Action<string> Warn;

    /// <summary>
    /// ★【诊断】日志通道（Plugin 注入 → logger.Info）。**单独一路**的原因：
    /// 诊断行（"本局收到 N 只 bot / role 分布 / Location 样例"）不能和功能日志混在一起，
    /// 否则离线探针里"非目标 role ⇒ 一条日志都不打"这类断言会被诊断行污染。
    /// 未注入时回落到 <see cref="Log"/>。
    /// </summary>
    public static Action<string> DiagLog;

    /// <summary>
    /// 需求给定的 6 款彩绘 CQCM 面具（tpl → 日志用中文短名）。
    /// 只有在活动配置里**没读到** <c>botEquipmentReplace.roles.arenaFighterEvent.FaceCover</c> 时才回落用它。
    /// </summary>
    private static readonly (string Tpl, string Label)[] BuiltinMasks =
    {
        ("67a5c657782ce4655104db16", "亡灵节"),
        ("67a5c5df782ce4655104db14", "战疤"),
        ("67a5c5f37f52620c5b05b4d6", "标靶"),
        ("67a5c61c7f52620c5b05b4d8", "恶魔"),
        ("67a5c6068fcd9fb73f0752cf", "骷髅"),
        ("68a9a15d73d52d47830759c9", "恶魔獠牙"),
    };

    /// <summary>casus_belli 的 5 张图（配置里没写 maps 时的兜底；与 <c>botEquipmentReplace.maps</c> 一致）。</summary>
    private static readonly HashSet<string> DefaultMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigmap", "woods", "interchange", "lighthouse", "shoreline",
    };

    private static bool _armed;
    private static readonly List<(string Tpl, string Label)> _masks = new();
    private static HashSet<string> _maps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>只提示一次的键（防每只 bot 刷屏）。</summary>
    private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    // ═══════════════════ 诊断（★只统计、不改变任何行为）═══════════════════
    //
    // 背景（实机问题）：日志里只有「已武装」，**一条按 bot 的「替换/补件」都没有**，玩家看到的仍是半面巾。
    // 这种"静默失败"只有三种可能，而原代码对前两种**完全不留证据**：
    //   ① postfix 根本没被调用（未生成 bot / 没挂上 / 已解除武装）；
    //   ② 进来了，但 role 判定全不命中（静默 return，没有任何日志）；
    //   ③ 进来了、role 也命中，但 Location 读不到或不匹配（只有一条**每进程一次**的 warning，或者干脆静默）。
    // ⇒ 这里加**一路纯只读统计**：本局收到多少只 bot、role 分布 Top5、Location 样例、
    //   以及 role/地图/替换/补件/幂等/无 Location 各命中多少次，**每局打一条汇总**。
    //
    // 触发点（三条，互为保险）：
    //   · 进程内**第一次**进 postfix ⇒ 立刻打一条「已接管 bot 生成（首次调用）」—— 秒级回答"postfix 到底跑没跑"；
    //   · 本局累计 **≥ <see cref="DiagSampleAt"/> 只** ⇒ 打本局汇总（不等开局，进图几秒就能看到）；
    //   · 新一局开始（<see cref="OnRaidStart"/>，由 `MatchController.ConfigureOfflineRaid` postfix 调）
    //     或距上只 bot 超过 <see cref="DiagRaidGapMs"/> ⇒ 打"上一局"汇总并清零
    //     （若上局一只都没收到，也会明确写「收到 0 只」⇒ 证明 postfix 没被调用）。
    // ⚠ 全部 try/catch + 只在已武装时统计，绝不影响 bot 生成。

    /// <summary>本局（=两次 <see cref="OnRaidStart"/> 之间）进过 postfix 的 bot 数。</summary>
    private static int _diagBots;

    /// <summary>本局 role 分布（取 `bot.Info.Settings.Role` → `details.Role` → `details.EventRole` → `details.RoleLowercase` 首个非空）。</summary>
    private static readonly Dictionary<string, int> _diagRoles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本局 `BotGenerationDetails.Location` 的**样例**（去重、最多 5 个）。</summary>
    private static readonly List<string> _diagLocs = new();

    private static int _diagRoleHit;     // role 判定命中
    private static int _diagMapHit;      // role + 地图都命中（真正进到改装备那一步）
    private static int _diagReplaced;    // 替换成功
    private static int _diagAdded;       // 补件成功
    private static int _diagAlready;     // 已经是 6 款之一（幂等跳过）
    private static int _diagNoLoc;       // 拿不到 Location ⇒ 跳过
    private static int _diagHelmetSwap;  // ★ v1.0.2：头盔阻挡 FaceCover ⇒ 已换掉
    private static int _diagHelmetDrop;  // ★ v1.0.2：头盔阻挡 FaceCover ⇒ 已摘除

    /// <summary>本局汇总是否已打过（保证每局只打一条）。</summary>
    private static bool _diagPrinted;
    /// <summary>进程内"首次调用"那条是否已打过。</summary>
    private static bool _diagFirstSeen;
    /// <summary>"收到 0 只"那条是否已打过（防每次开局都刷）。</summary>
    private static bool _diagZeroReported;
    /// <summary>上只 bot 的时间戳（TickCount64；用于"新一局"保险丝）。</summary>
    private static long _diagLastTick;
    /// <summary>统计段序号（每过一次"新一局"分母点 +1；只用于日志可读性，别当成真实局号）。</summary>
    private static int _diagRaidNo;

    /// <summary>
    /// ★ 诊断状态的锁。**必须**：bot 生成是 PLINQ 并行的
    /// （`BotController.GenerateBotWave:187-188` `Enumerable.Range(…).AsParallel()`），
    /// 多个线程会同时进 postfix ⇒ 无锁改 <c>Dictionary</c>/<c>List</c> 会把集合写坏（甚至卡死）。
    /// 计数用 <c>Interlocked</c>（int 读写本身原子），集合/汇总走这把锁。
    /// </summary>
    private static readonly object _diagLock = new();

    /// <summary>累计到这么多只就打一次本局汇总（进图几秒内就能看到结论，不必等开局）。</summary>
    private const int DiagSampleAt = 30;

    /// <summary>距上只 bot 超过这么久 ⇒ 视为新一局（保险丝：万一 <see cref="OnRaidStart"/> 没被调用）。</summary>
    private const int DiagRaidGapMs = 120_000;

    /// <summary>
    /// 本局汇总（**每局最多一条**）。<paramref name="reason"/> 只写进日志，便于判断触发点。
    /// 清零后等下一局。
    /// </summary>
    private static void FlushDiag(string reason)
    {
        try
        {
            if (_diagBots == 0)
            {
                // ★ 这是最关键的一条：postfix 一只 bot 都没收到。
                if (_armed && !_diagZeroReported)
                {
                    _diagZeroReported = true;
                    Diag($"[面具修正][诊断] {reason}：本局收到 **0 只 bot** ⇒ 该 postfix 没被调用"
                       + $"（已武装=是，地图表=[{string.Join(",", _maps)}]）"
                       + "。可能原因：本局没有生成 bot / `BotGenerator.PrepareAndGenerateBot` 被别家 mod 整体替换 / 活动已解除武装。");
                }
            }
            else
            {
                var top = string.Join(" ", _diagRoles.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key + "×" + kv.Value));
                var locs = _diagLocs.Count == 0 ? "(空)" : "[" + string.Join(",", _diagLocs) + "]";
                Diag($"[面具修正][诊断] {reason}：本局收到 {_diagBots} 只 bot（统计段 #{_diagRaidNo}，已武装=是）"
                   + $"｜role Top5：{top}"
                   + $"｜Location 样例={locs}"
                   + $"｜role 命中 {_diagRoleHit} 只、其中地图命中 {_diagMapHit} 只"
                   + $"｜替换 {_diagReplaced}、补件 {_diagAdded}、已是面具 {_diagAlready}、无 Location 跳过 {_diagNoLoc}"
                   + $"、头盔阻挡 {_diagHelmetSwap + _diagHelmetDrop}（换 {_diagHelmetSwap} / 摘 {_diagHelmetDrop}）"
                   + $"\n[面具修正][诊断] 判定条件：role 必须 ∈ {{{TargetRole}}}（大小写不敏感）；"
                   + $"Location 必须 ∈ [{string.Join(",", _maps)}]（大小写不敏感）");
            }
        }
        catch { }
        // ★ 只把"本局已打过汇总"这面旗立起来，**不动** `_diagRaidNo` ——
        //   否则每 30 只就会重新打一条（一局 300 只 = 10 条刷屏）。真正换局在 <see cref="ResetRaidDiag"/>。
        _diagPrinted = true;
        ResetCounters();
    }

    /// <summary>只清计数/分布/样例（**保留** <c>_diagPrinted</c> 与局序号）。</summary>
    private static void ResetCounters()
    {
        _diagBots = 0;
        _diagRoles.Clear();
        _diagLocs.Clear();
        _diagRoleHit = _diagMapHit = _diagReplaced = _diagAdded = _diagAlready = _diagNoLoc = 0;
        _diagHelmetSwap = _diagHelmetDrop = 0;
    }

    /// <summary>真·新一局：计数清零 + 汇总旗复位 + 局序号 +1。</summary>
    private static void ResetRaidDiag()
    {
        ResetCounters();
        _diagPrinted = false;
        _diagRaidNo++;
    }

    /// <summary>
    /// ★【诊断】新一局开始（由 `BossWavePatch.AfterConfigureOfflineRaid` 在
    /// `MatchController.ConfigureOfflineRaid` postfix 里调用，即 `/client/raid/configuration` 时）。
    /// 把**上一局**的汇总打出来（若还没打）再清零 ⇒ 即使是"一只都没收到"也会有明确一行。
    /// </summary>
    public static void OnRaidStart(string map)
    {
        if (!_armed) return;
        lock (_diagLock)
        {
            try
            {
                if (_diagBots > 0 || !_diagPrinted)
                    FlushDiag($"上一局结束（新一局开始，地图={map ?? "?"}）");
                ResetRaidDiag();            // 无论打没打，都进新一局（计数清零 + 汇总旗复位）
            }
            catch { }
        }
    }

    /// <summary>★【诊断】每只 bot 进 postfix 都记一次（纯只读，线程安全：见 <see cref="_diagLock"/>）。</summary>
    private static void DiagTick(object details, object bot)
    {
        lock (_diagLock)
        {
            try
            {
                long now = Environment.TickCount64;
                // 保险丝：距上只 bot 太久 ⇒ 视为换局（先打上一局，再清零）
                if (_diagLastTick != 0 && now - _diagLastTick > DiagRaidGapMs && (_diagBots > 0 || !_diagPrinted))
                {
                    FlushDiag($"距上只 bot 已超过 {DiagRaidGapMs / 1000}s");
                    ResetRaidDiag();        // 保险丝判定的"换局"同样要复位汇总旗
                }
                _diagLastTick = now;

                // ★ 采样阈值检查放在自增**之前**：这样触发汇总的那只 bot 是从**下一局/下一窗口**开始算的，
                //   打印出来的「替换/补件」等计数与「收到 N 只」严格对应（不会出现"第 30 只的处理结果没算进去"）。
                if (!_diagPrinted && _diagBots >= DiagSampleAt)
                {
                    FlushDiag($"本局累计 {_diagBots} 只 bot 采样");
                    _diagLastTick = now;
                }

                _diagBots++;

                var role = FirstRole(details, bot) ?? "(null)";
                _diagRoles[role] = _diagRoles.TryGetValue(role, out var c) ? c + 1 : 1;

                var loc = ResolveLocation(details);
                if (_diagLocs.Count < 5 && !_diagLocs.Contains(loc ?? "(null)"))
                    _diagLocs.Add(loc ?? "(null)");

                // ★ 首次调用：秒级证明"postfix 被调用了"，并把最关键的四个取值原样打出来
                if (!_diagFirstSeen)
                {
                    _diagFirstSeen = true;
                    Diag($"[面具修正][诊断] ★已接管 bot 生成（进程内首次调用）："
                       + $"bot.Info.Settings.Role={R.Prop(R.Prop(R.Prop(bot, "Info"), "Settings"), "Role")?.ToString() ?? "(null)"}"
                       + $"｜details.Role={R.Prop(details, "Role")?.ToString() ?? "(null)"}"
                       + $"｜details.EventRole={R.Prop(details, "EventRole")?.ToString() ?? "(null)"}"
                       + $"｜details.RoleLowercase={R.Prop(details, "RoleLowercase")?.ToString() ?? "(null)"}"
                       + $"｜details.Location={R.Prop(details, "Location")?.ToString() ?? "(null)"}");
                }
            }
            catch { }
        }
    }


    private static void Info(string m) { try { Log?.Invoke(m); } catch { } }
    private static void W(string m) { try { Warn?.Invoke(m); } catch { } }
    private static void Diag(string m) { try { (DiagLog ?? Log)?.Invoke(m); } catch { } }
    private static void WarnOnce(string key, string m) { try { if (_warned.Add(key)) W(m); } catch { } }

    // ═════════════════════════════ 武装 / 解除 ═════════════════════════════

    /// <summary>
    /// 由 `Plugin.ApplyBotEquipmentReplace` 调用（只在活动**真的在跑**时调用）。
    /// 从同一个 <c>botEquipmentReplace</c> 配置里读「地图 + 面具 tpl」⇒ 数据只有一份，不会和 `BotEquipmentReplaceDimension` 漂移。
    /// 全程 try/catch，失败只 warning（等价于"不修正"，绝不影响 bot 生成）。
    /// </summary>
    public static void Apply(EventDef ev)
    {
        try
        {
            var spec = ev?.BotEquipmentReplace;
            if (spec?.Roles == null || spec.Roles.Count == 0) return;

            BotEquipmentReplaceRole role = null;
            foreach (var kv in spec.Roles)
                if (string.Equals(kv.Key?.Trim(), TargetRole, StringComparison.OrdinalIgnoreCase)) { role = kv.Value; break; }
            if (role?.Slots == null) return;

            Dictionary<string, double> pool = null;
            foreach (var kv in role.Slots)
                if (string.Equals(kv.Key?.Trim(), TargetSlot, StringComparison.OrdinalIgnoreCase)) { pool = kv.Value; break; }

            var masks = new List<(string Tpl, string Label)>();
            if (pool != null)
            {
                foreach (var tpl in pool.Keys)
                {
                    if (string.IsNullOrWhiteSpace(tpl)) continue;
                    var t = tpl.Trim();
                    masks.Add((t, LabelOf(t)));
                }
            }
            if (masks.Count == 0)
            {
                masks = BuiltinMasks.ToList();
                WarnOnce("builtin", $"[面具修正] 活动 '{ev?.Id}' 的 botEquipmentReplace 里没有 {TargetRole}.{TargetSlot} ⇒ 用内置 6 款面具兜底");
            }

            var maps = (spec.Maps ?? new List<string>())
                       .Where(m => !string.IsNullOrWhiteSpace(m))
                       .Select(m => m.Trim())
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
            if (maps.Count == 0) maps = DefaultMaps.ToList();

            _masks.Clear();
            _masks.AddRange(masks);
            _maps = new HashSet<string>(maps, StringComparer.OrdinalIgnoreCase);
            _armed = true;

            // ★【诊断】重新武装 = 重新开始统计（否则上一轮活动的计数会混进本局汇总）
            lock (_diagLock) { try { _diagFirstSeen = false; _diagZeroReported = false; _diagLastTick = 0; ResetRaidDiag(); } catch { } }

            Info($"[面具修正] '{ev?.Id}' 已武装：role={TargetRole} 面具 {_masks.Count} 款（等概率）"
               + $"｜地图=[{string.Join(",", _maps)}]｜生成后强制修正（PBS 接管时同样生效）"
               + "｜诊断已开：每局一条「[面具修正][诊断] 本局收到 N 只 bot + role Top5 + Location 样例」");
        }
        catch (Exception ex)
        {
            WarnOnce("apply", $"[面具修正] 武装失败（已跳过，不影响 bot 生成）: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>活动关掉/换轮 ⇒ 解除武装（`Plugin.Restore` 里与 `BotEquipmentReplaceDimension.Restore()` 同一处调用）。</summary>
    public static void Restore()
    {
        try
        {
            if (_armed) Info("[面具修正] 活动已还原 ⇒ 解除武装（此后生成的 bot 不再改面板）");
        }
        catch { }
        finally
        {
            _armed = false;
            _masks.Clear();
            _maps.Clear();
            _warned.Clear();
            lock (_diagLock) { try { _diagLastTick = 0; ResetRaidDiag(); } catch { } }
        }
    }

    // ═════════════════════ 产出物层（PrepareAndGenerateBot postfix）═════════════════════

    /// <summary>
    /// `BotGenerator.PrepareAndGenerateBot` 的 postfix 体（由 `BossWavePatch.BotGenerated` 调用）。
    /// <paramref name="details"/> = `BotGenerationDetails`（读 <c>Role</c>/<c>EventRole</c>/<c>Location</c>），
    /// <paramref name="bot"/> = 最终的 `BotBase`（改它的 <c>Inventory.Items</c>）。
    /// **任何异常都只 warning，绝不抛出**（否则会污染 SPT 的 bot 生成）。
    /// </summary>
    public static void OnBotGenerated(object details, object bot)
    {
        if (!_armed || bot == null) return;
        if (_masks.Count == 0) return;                 // 武装时就没有面具 tpl ⇒ 什么都不做（防御性）
        try
        {
            // ★【诊断】先记一笔（纯只读）：本局 bot 数 / role 分布 / Location 样例 / 各类命中数。
            //   放在任何 return **之前** —— 否则"role 不匹配"这条最常见的失败路径又是零证据。
            DiagTick(details, bot);

            // ① 只认目标 role（不看别的 role，哪怕同图同波）
            if (!IsTargetRole(details, bot)) return;
            System.Threading.Interlocked.Increment(ref _diagRoleHit);

            // ② 只认 casus_belli 的 5 张图（地图未知 ⇒ 跳过，不做"全图兜底"）
            var map = ResolveLocation(details);
            if (string.IsNullOrWhiteSpace(map))
            {
                System.Threading.Interlocked.Increment(ref _diagNoLoc);
                // ★ 原来这里是**每进程一次**（`WarnOnce("noloc", …)`）⇒ 第一局打过一次后，
                //   后面每一局都**静默跳过**。现在键里带本局序号 ⇒ **每局最多一次**。
                WarnOnce("noloc#" + _diagRaidNo, "[面具修正] ⚠拿不到 BotGenerationDetails.Location ⇒ 本局**不修正**（避免改到非目标地图）");
                return;
            }
            if (!_maps.Contains(map)) return;
            System.Threading.Interlocked.Increment(ref _diagMapHit);

            var items = R.Prop(R.Prop(bot, "Inventory"), "Items") as IList;
            if (items == null) { WarnOnce("noitems", "[面具修正] 拿不到 bot.Inventory.Items ⇒ 跳过"); return; }

            // ★★★ v1.0.2【问题 B】先解决"头盔挡住面孔"：
            //   官方生成器在装配阶段就用 `TemplateItem.Properties.BlocksFaceCover` 判冲突
            //   （`BotGeneratorHelper.IsItemIncompatibleWithCurrentItems:325`）；我们是生成后再改，绕过它，
            //   但客户端/观感仍会把被挡的面罩吃掉 ⇒ 头盔阻挡时先**换掉或摘掉头盔**，再装面具。
            //   ⚠ 不阻挡的头盔（如劫掠者池里的 LZSh/ULACH/ZSh-1-2M）**一律不动**。
            try { EnsureFaceCoverNotBlocked(items, map); } catch { }

            // ③ 找 SlotId == "FaceCover" 的那一件
            object existing = null;
            try
            {
                foreach (var it in items)
                {
                    var slot = R.Prop(it, "SlotId")?.ToString() ?? R.Prop(it, "slotId")?.ToString();
                    if (string.Equals(slot, TargetSlot, StringComparison.OrdinalIgnoreCase)) { existing = it; break; }
                }
            }
            catch (Exception ex) { WarnOnce("scan", $"[面具修正] 遍历 Inventory.Items 失败（已跳过）: {ex.Message}"); return; }

            if (existing != null)
            {
                var oldTpl = TplOf(existing);
                if (IsOneOfMasks(oldTpl)) { System.Threading.Interlocked.Increment(ref _diagAlready); return; }   // 已经是 6 款之一 ⇒ 幂等，不动（也不刷日志）

                var pick = _masks[Random.Shared.Next(_masks.Count)];   // 等概率（Random.Shared 线程安全）
                if (!R.SetProp(existing, "Template", pick.Tpl) && !R.SetProp(existing, "_tpl", pick.Tpl))
                {
                    WarnOnce("set-tpl:" + oldTpl, $"[面具修正] 改写 {map} {TargetRole} 的面具 tpl 失败（{oldTpl} → {pick.Tpl}）");
                    return;
                }
                System.Threading.Interlocked.Increment(ref _diagReplaced);
                Info($"[面具修正] {map} {TargetRole}：替换 1 件 {ShortName(oldTpl)} → 涂装 CQCM（{pick.Label}）");
                return;
            }

            // ④ 原本没有 FaceCover ⇒ 补一件
            var add = _masks[Random.Shared.Next(_masks.Count)];
            if (AddFaceCoverItem(items, R.Prop(bot, "Inventory"), add.Tpl))
            {
                System.Threading.Interlocked.Increment(ref _diagAdded);
                Info($"[面具修正] {map} {TargetRole}：原本没有 FaceCover ⇒ 补 1 件 涂装 CQCM（{add.Label}）");
            }
            else
                WarnOnce("add", $"[面具修正] {map} {TargetRole}：没有 FaceCover 且补不进去（见上一条 warning）—— 该 bot 这次不戴");
        }
        catch (Exception ex)
        {
            WarnOnce("gen", $"[面具修正] 产出物层失败（已跳过，不影响 bot 生成）: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// role 判定：**任一**来源命中即算（大小写不敏感）：
    /// <list type="number">
    /// <item><c>bot.Info.Settings.Role</c> —— 生成完的真实 role（SPT 4.x 里 <c>BotBase.Info.Settings.Role</c> 是
    ///   <c>string</c>；`GenerateBot:212-215` 会把 <c>details.EventRole</c> 写回去 ⇒ 事件 bot 这里是 <c>arenaFighterEvent</c>）；</item>
    /// <item><c>details.Role</c> —— 注意 SPT 4.x `BotController.GenerateBotWave:177-181` 对 <c>*event*</c> 角色做了转换：
    ///   <c>EventRole = role; Role = seasonalEventService.GetBaseRoleForEventBot(EventRole)</c>；
    ///   本机 `seasonalevents.json` 的 `eventBotMapping` 里 <c>arenaFighterEvent → arenaFighterEvent</c>（自身映射）
    ///   ⇒ <c>details.Role</c> 同样是 <c>arenaFighterEvent</c>（**若换成本体映射到别人的事件，这里就不命中，所以只当一路来源**）；</item>
    /// <item><c>details.EventRole</c> —— 事件角色原文；</item>
    /// <item><c>details.RoleLowercase</c> —— <c>GenerateBot:158</c> 写的全小写（PBS 装备池就是用这个键查的），
    ///   本 role = <c>arenafighterevent</c> ⇒ 大小写不敏感比较同样命中（这是本次新加的一路）。</item>
    /// </list>
    /// </summary>
    private static bool IsTargetRole(object details, object bot)
    {
        foreach (var r in RoleSamples(details, bot))
            if (string.Equals(r, TargetRole, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>按优先级取 role 的几个来源（首个非空的用于诊断统计）。</summary>
    private static IEnumerable<string> RoleSamples(object details, object bot)
    {
        string a = null, b = null, c = null, d = null;
        try { a = R.Prop(R.Prop(R.Prop(bot, "Info"), "Settings"), "Role")?.ToString()?.Trim(); } catch { }
        try { b = R.Prop(details, "Role")?.ToString()?.Trim(); } catch { }
        try { c = R.Prop(details, "EventRole")?.ToString()?.Trim(); } catch { }
        try { d = R.Prop(details, "RoleLowercase")?.ToString()?.Trim(); } catch { }
        foreach (var x in new[] { a, b, c, d })
            if (!string.IsNullOrEmpty(x)) yield return x;
    }

    /// <summary>诊断统计用的"这只 bot 是什么 role"（同 <see cref="RoleSamples"/> 的第一个非空值）。</summary>
    private static string FirstRole(object details, object bot)
    {
        foreach (var r in RoleSamples(details, bot)) return r;
        return null;
    }

    /// <summary>
    /// 地图来源（大小写不敏感比较，配置里是小写 location id 如 <c>woods</c>/<c>bigmap</c>）：
    /// <list type="number">
    /// <item><c>BotGenerationDetails.Location</c> —— SPT 4.1.x 的**唯一**地图字段。
    ///   反编译实证：<c>BotController.GetBotGenerationDetailsForWave:271</c> <c>Location = raidSettings?.Location</c>，
    ///   而 <c>RaidSettings.Location</c>（JSON 名 <c>location</c>）就是 `/client/raid/configuration` 里的地图 id
    ///   （`BotGenerationDetails.cs:259` 只有这个 <c>Location</c> 属性，**没有** <c>LocationId</c>）；</item>
    /// <item><c>LocationId</c>/<c>RaidLocation</c> —— 防御性多源：老版本 SPT 或别家 mod（如 APBS 的
    ///   <c>RaidInformation.RaidLocation</c>）可能换成别的名字，读到就用；读不到就继续；</item>
    /// <item><c>details.ExtensionData</c> 里的同名键 —— 有些 mod 把地图塞进 <c>[JsonExtensionData]</c>。</item>
    /// </list>
    /// 全部读不到 ⇒ 返回 null（调用点按"地图未知 ⇒ 本局不修正"处理）。
    /// </summary>
    private static string ResolveLocation(object details)
    {
        try
        {
            foreach (var name in new[] { "Location", "LocationId", "RaidLocation" })
            {
                var v = R.Prop(details, name)?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            if (R.Prop(details, "ExtensionData") is IDictionary ext)
            {
                foreach (var k in new[] { "Location", "location", "LocationId", "locationId", "RaidLocation", "raidLocation" })
                {
                    try
                    {
                        if (!ext.Contains(k) || ext[k] == null) continue;
                        var v = ext[k].ToString();
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                    }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }

    private static string TplOf(object item)
    {
        try { return R.Prop(item, "Template")?.ToString() ?? R.Prop(item, "_tpl")?.ToString(); }
        catch { return null; }
    }

    private static bool IsOneOfMasks(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return false;
        try { return _masks.Any(m => string.Equals(m.Tpl, tpl, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    // ═══════════════════ v1.0.2【问题 B】头盔阻挡 FaceCover ⇒ 换/摘 ═══════════════════
    //
    // 实机取证（2026-10-24，`SPT_Data/database/templates/items.json`）：
    //   · 劫掠者 Headwear 池 17 件（`bots/types/arenafighterevent.json`）**全部 BlocksFaceCover=false**
    //     —— 含用户截图里那顶 LZSh（tpl `5b432d215acfc4771e1c6624`，`BlocksFaceCover=false`）；
    //   · PBS 的 ExUsec 池（Tier1..7_equipment.json）20 件也**全部=false**；
    //   · 全库只有 3 件"头部槽"物品 BlocksFaceCover=true：
    //       `5ab8f20c86f7745cdb629fb2` 滑雪帽、`618aef6d0a5a59657e5f55ee` LLCS 巴拿马帽、
    //       `5ea058e01dbce517f324b3e2` Heavy Trooper。
    //   ⇒ 用户「戴不上是因为头盔」的直觉**在本例不成立**（LZSh 不挡）—— 真因是 postfix 一次都没被调用（问题 A）。
    //     但作为**通用护栏**仍然落地：任何 bot 只要头部槽是不阻挡的目标之外的头盔，就换/摘掉，
    //     保证"面具一定戴得上"，并留下逐字日志。
    //
    // 判定读的是**运行期** TemplateTable 的 `TemplateItem.Properties.BlocksFaceCover`（与官方生成器同源）。

    /// <summary>查 `TemplateItem.Properties.BlocksFaceCover`（缓存）。null = 查不到（不当作阻挡）。</summary>
    private static bool? BlocksFaceCover(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return null;
        lock (_tplLock) { if (_blocksCache.TryGetValue(tpl, out var hit)) return hit; }

        bool? r = null;
        try
        {
            var tmpl = FindTemplate(tpl);
            var props = R.Prop(tmpl, "Properties") ?? R.Prop(tmpl, "_props");
            var v = R.Prop(props, "BlocksFaceCover");
            if (v is bool b) r = b;
            else if (v != null) { try { r = Convert.ToBoolean(v); } catch { } }
        }
        catch { }

        lock (_tplLock) { try { _blocksCache[tpl] = r; } catch { } }
        return r;
    }

    /// <summary>运行期 `TemplateTable.Items[tpl]`。</summary>
    private static object FindTemplate(string tpl)
    {
        try
        {
            var table = R.Prop(TemplateTable, "Items") as IDictionary;
            if (table == null || string.IsNullOrEmpty(tpl)) return null;
            var direct = R.DictGet(table, tpl);
            if (direct != null) return direct;
            var key = R.Coerce(tpl, R.DictKeyType(table));
            if (key != null && table.Contains(key)) return table[key];
        }
        catch { }
        return null;
    }

    /// <summary>挑一件"不阻挡 FaceCover"的替补头盔（不存在/也阻挡 ⇒ 跳过）。</summary>
    private static string PickNonBlockingHeadwear(string blockedTpl)
    {
        foreach (var cand in NonBlockingHeadwear)
        {
            if (string.Equals(cand, blockedTpl, StringComparison.OrdinalIgnoreCase)) continue;
            if (FindTemplate(cand) == null) continue;       // 该 SPT 版本没有这件
            if (BlocksFaceCover(cand) == true) continue;    // 数据变了也绝不选阻挡的
            return cand;
        }
        return null;
    }

    /// <summary>
    /// 头部槽若阻挡 FaceCover ⇒ 换掉（无挂件时，保留 _id ⇒ 物品树不断）或摘掉（有挂件时，连挂件一起摘）。
    /// 只处理 `SlotId == "Headwear"` 且 `BlocksFaceCover == true` 的那一件；其它一律不动。
    /// </summary>
    private static bool EnsureFaceCoverNotBlocked(IList items, string map)
    {
        object head = null;
        try
        {
            foreach (var it in items)
            {
                var slot = R.Prop(it, "SlotId")?.ToString() ?? R.Prop(it, "slotId")?.ToString();
                if (string.Equals(slot, "Headwear", StringComparison.OrdinalIgnoreCase)) { head = it; break; }
            }
        }
        catch { return false; }
        if (head == null) return false;                         // 没头盔 ⇒ 不关本护栏的事

        var tpl = TplOf(head);
        if (BlocksFaceCover(tpl) != true) return false;         // ★ 非阻挡头盔（LZSh/ULACH/…）**不动**
        var name = ShortName(tpl);

        // 挂件（头盔面罩/护甲板…）：有挂件时换壳会让挂件挂在不匹配的壳上 ⇒ 走"整体摘除"
        var headId = R.Prop(head, "Id")?.ToString() ?? R.Prop(head, "_id")?.ToString();
        var kidCount = 0;
        if (!string.IsNullOrEmpty(headId))
        {
            try
            {
                foreach (var it in items)
                {
                    if (ReferenceEquals(it, head)) continue;
                    var pid = R.Prop(it, "ParentId")?.ToString() ?? R.Prop(it, "parentId")?.ToString();
                    if (string.Equals(pid, headId, StringComparison.OrdinalIgnoreCase)) kidCount++;
                }
            }
            catch { }
        }

        if (kidCount == 0)
        {
            var repl = PickNonBlockingHeadwear(tpl);
            if (repl != null && R.SetProp(head, "Template", repl))
            {
                System.Threading.Interlocked.Increment(ref _diagHelmetSwap);
                Info($"[面具修正] {map} {TargetRole}：头盔 {name} 阻挡 FaceCover ⇒ 已换为 {ShortName(repl)}（保留 {TargetSlot}）");
                return true;
            }
        }

        // 摘除（含直接挂件；本 mod 不碰别的槽，只动以该头盔为 parent 的子树）
        var removed = 0;
        try
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                var pid = R.Prop(it, "ParentId")?.ToString() ?? R.Prop(it, "parentId")?.ToString();
                if (ReferenceEquals(it, head)
                    || (!string.IsNullOrEmpty(headId) && string.Equals(pid, headId, StringComparison.OrdinalIgnoreCase)))
                {
                    items.RemoveAt(i);
                    removed++;
                }
            }
        }
        catch { }

        if (removed > 0)
        {
            System.Threading.Interlocked.Increment(ref _diagHelmetDrop);
            Info($"[面具修正] {map} {TargetRole}：头盔 {name} 阻挡 FaceCover ⇒ 已摘除头盔（含 {removed - 1} 件挂件，保留 {TargetSlot}）");
            return true;
        }

        WarnOnce("unblock#" + tpl, $"[面具修正] 头盔 {name} 阻挡 FaceCover，但换/摘都失败 ⇒ 该 bot 的面具可能不显示");
        return false;
    }

    // ═════════════════════════ 新造一件装备（照 SPT 写法）═════════════════════════

    private static Type _itemType;
    private static bool _itemTypeProbed;

    /// <summary>惰性解析 `SPTarkov.Server.Core.Models.Eft.Common.Tables.Item`（不硬依赖，找不到就降级）。</summary>
    private static void EnsureItemType()
    {
        if (_itemTypeProbed) return;
        _itemTypeProbed = true;
        try
        {
            // ★★★ v1.1.0【BugFix】必须取**运行时那份**程序集的 Item —— prepatch 双载下
            //   AppDomain 里有 2 份 SPTarkov.Server.Core，按加载顺序取第一个会拿到死副本 ⇒
            //   它的 MongoId 与 JsonOpts 的 StringToMongoIdConverter 不是同一类型 ⇒
            //   R.FromJson 报 "could not be converted to ... MongoId. Path: $._id"（实机 5 次复现）。
            var liveT = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Eft.Common.Tables.Item");
            if (liveT != null && liveT.GetProperty("Template") != null) { _itemType = liveT; return; }
        }
        catch { }
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                // ★ 只认运行时那份（LiveCoreAssembly 拿不到时才退回全扫）
                if (RuntimeTypes.LiveCoreAssembly != null && !ReferenceEquals(a, RuntimeTypes.LiveCoreAssembly)) continue;
                Type[] ts;
                try { ts = a.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { ts = rtle.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                var t = ts.FirstOrDefault(x => x != null
                        && x.Name == "Item"
                        && x.Namespace != null && x.Namespace.Contains("Eft.Common.Tables")
                        && x.GetProperty("Template") != null);
                if (t != null) { _itemType = t; return; }
            }
            WarnOnce("noitemtype", "[面具修正] 找不到 SPT 的 Item 类型 ⇒ 补不到 FaceCover（已有面具的替换不受影响）");
        }
        catch (Exception ex) { WarnOnce("itemtype", $"[面具修正] 解析 Item 类型失败（已降级）: {ex.Message}"); }
    }

    private static bool AddFaceCoverItem(IList items, object inventory, string tpl)
    {
        EnsureItemType();
        if (_itemType == null || items == null || string.IsNullOrEmpty(tpl)) return false;
        try
        {
            // ★ parentId 用 `Inventory.Equipment`（= 物品树根 "inventory" 那一件的 _id）：
            //   逐字对齐 SPT `BotInventoryGenerator.GenerateEquipment:471-473`
            //     `MongoId? equipment = settings.Inventory.Equipment;
            //      item2.ParentId = (equipment.HasValue ? (string)equipment.GetValueOrDefault() : null);`
            //   ⚠ 需求原文写的是 `ParentId=null`；照字面做会让这件装备在物品树里**没有父节点**
            //   （客户端按 parentId 建树 ⇒ 会被丢弃/报警告），所以这里按 SPT 官方写法取根 id，取不到才回落 null。
            string parentId = null;
            try { parentId = R.Prop(inventory, "Equipment")?.ToString(); } catch { }

            var json = "{\"_id\":\"" + NewId() + "\",\"_tpl\":\"" + tpl + "\","
                       + (string.IsNullOrEmpty(parentId) ? "" : "\"parentId\":\"" + parentId + "\",")
                       + "\"slotId\":\"" + TargetSlot + "\",\"upd\":{\"StackObjectsCount\":1}}";

            var item = R.FromJson(_itemType, json, m => WarnOnce("fromjson", "[面具修正] 造面具失败: " + m));
            if (item == null) return false;
            items.Add(item);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("add-ex", $"[面具修正] 新造 FaceCover 失败（已降级）: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string NewId()
    {
        const string hex = "0123456789abcdef";
        var c = new char[24];
        for (int i = 0; i < c.Length; i++) c[i] = hex[Random.Shared.Next(16)];
        return new string(c);
    }

    // ═══════════════════════════ 名称（只用于日志）═══════════════════════════

    private static readonly Dictionary<string, string> BuiltinLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["67a5c657782ce4655104db16"] = "亡灵节",
        ["67a5c5df782ce4655104db14"] = "战疤",
        ["67a5c5f37f52620c5b05b4d6"] = "标靶",
        ["67a5c61c7f52620c5b05b4d8"] = "恶魔",
        ["67a5c6068fcd9fb73f0752cf"] = "骷髅",
        ["68a9a15d73d52d47830759c9"] = "恶魔獠牙",
    };

    private static string LabelOf(string tpl)
    {
        if (!string.IsNullOrEmpty(tpl) && BuiltinLabels.TryGetValue(tpl, out var l) && !string.IsNullOrEmpty(l)) return l;
        var n = ShortName(tpl);
        return string.IsNullOrEmpty(n) ? tpl : n;
    }

    /// <summary>tpl → 本地化短名（只读 `<SPT_Data>/database/locales/global/{ch,zh,en}.json`；取不到返回 tpl）。</summary>
    private static string ShortName(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return "?";
        try
        {
            EnsureLocale();
            if (_locale != null)
            {
                if (_locale.TryGetValue(tpl + " ShortName", out var s) && !string.IsNullOrEmpty(s)) return s;
                if (_locale.TryGetValue(tpl + " Name", out var n) && !string.IsNullOrEmpty(n)) return n;
            }
        }
        catch { }
        return tpl;
    }

    private static Dictionary<string, string> _locale;

    private static void EnsureLocale()
    {
        if (_locale != null) return;
        try
        {
            var dir = EventEngine.ModPath;
            if (string.IsNullOrEmpty(dir)) return;
            // <mods>/SPT-EventRotator → 上溯 3 级 = SPT_Runtime（SPT_Data 就在它下面）
            var root = Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));
            foreach (var lang in new[] { "ch", "zh", "en" })
            {
                var f = Path.Combine(root, "SPT_Data", "database", "locales", "global", lang + ".json");
                if (!File.Exists(f)) continue;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var pr in doc.RootElement.EnumerateObject())
                    if (pr.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = pr.Value.GetString();
                        if (!string.IsNullOrEmpty(s)) map[pr.Name] = s;
                    }
                _locale = map;
                return;
            }
        }
        catch { /* 名称只是日志装饰，失败静默 */ }
    }
}
