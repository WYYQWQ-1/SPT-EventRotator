using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;

namespace EventRotator.Client;

// ★ 2026-10-27：版本号单一来源 —— 以前这里是另一个独立硬编码（发版脚本只改 csproj），
//   结果 1.3.6 的包里客户端自报 1.3.4，Forge 的 dll_version_match 失败：
//   "No client mod declares the published version (1.3.6); found: 1.3.4."
//   现在 BepInPlugin 也读这个常量；ship.py 的 bump_version 会同步改这一行。
[BepInPlugin("com.kirisame.eventrotator", "Event Rotator (Client)", Plugin.VersionText)]   // ★ v1.3.4：GUID 必须与服务端 package.json / IModMetadata 一致（The Forge 的 dll_guid_match 检查）
public class Plugin : BaseUnityPlugin
{
    /// <summary>版本号单一来源（BepInPlugin 用它；必须与服务端 package.json / IModMetadata 一致）。</summary>
    public const string VersionText = "1.3.15";
    public static ManualLogSource Log;
    private static ConfigEntry<bool> _forceHostileCfg;
    private static ConfigEntry<string> _modDirCfg;
    private static ConfigEntry<bool> _disableAllCfg;
    private static ConfigEntry<bool> _scourgeLegacyBrainCfg;
    private static ConfigEntry<bool> _scourgeDebugCfg;

    /// <summary>★ 排障开关：true = 灾厄小队用旧 ArenaFighter 脑。</summary>
    public static bool ScourgeLegacyBrain => _scourgeLegacyBrainCfg != null && _scourgeLegacyBrainCfg.Value;

    /// <summary>★ 灾厄诊断日志（每 5 秒统计一次灾厄单位的位置/层/决策）。</summary>
    public static bool ScourgeDebugOn => _scourgeDebugCfg == null || _scourgeDebugCfg.Value;
    /// <summary>★ v0.1.6 客户端「仪式撤离点锁定」的 F12 开关（实现见 RitualExitLock.cs）。
    /// 取值通过 <see cref="RitualExitLock.EnabledSource"/> 注入 ⇒ 每帧读到的都是 F12 里的当前值。</summary>
    private static ConfigEntry<bool> _ritualExitCfg;
    /// <summary>★ v0.1.6 条款②「进图即锁撤离点（仅致命毒素活动生效时）」的 F12 开关（实现见 RitualExitLock.TickStartLock）。</summary>
    private static ConfigEntry<bool> _toxinExitCfg;
    /// <summary>★ v0.1.6 条款③「致命毒素感染与倒计时」的 F12 开关（实现见 ToxinInfection.cs）。</summary>
    private static ConfigEntry<bool> _toxinBuffCfg;
    /// <summary>★ v0.1.6 Halloween 2022「开局随机负面状态」的 F12 开关（实现见 RandomDebuff.cs）。</summary>
    private static ConfigEntry<bool> _randDebuffCfg;
    private static readonly List<KeyValuePair<ConfigEntry<bool>, string>> _toggles = new();
    private static bool _suspend;

    /// <summary>★ v0.1.3 悬浮面板（当前生效活动）相关配置。</summary>
    private const string CatOverlay = "★★ 悬浮面板：当前生效活动 | Overlay (hotkey)";
    private static ConfigEntry<string> _panelLangCfg;
    private static ConfigEntry<string> _remoteUrlCfg;
    private static ConfigEntry<KeyboardShortcut> _overlayKeyCfg;
    private static ConfigEntry<float> _overlayAutoHideCfg;
    private static ConfigEntry<int> _overlayFontCfg;
    private static ConfigEntry<bool> _overlayRightCfg;
    /// <summary>★ v0.1.5（用户要求）：面板宽度像素，拖右边/右下角改；0 = 自动按字号。**改它不动字号**。</summary>
    private static ConfigEntry<float> _overlayWidthCfg;
    /// <summary>★ v0.1.5：面板高度像素，拖右下角改；0 = 自动按内容。</summary>
    private static ConfigEntry<float> _overlayHeightCfg;
    private static ConfigEntry<bool> _overlayProbeCfg;
    /// <summary>★ v1.0.2 诊断日志开关（同步到 <see cref="HostilityGuard.DiagOn"/>，默认 false）。</summary>
    private static ConfigEntry<bool> _diagLogCfg;
    /// <summary>★ v0.1.3 F12 只读行：需不需要重启。</summary>
    private static ConfigEntry<string> _restartHintCfg;
    /// <summary>★ v0.1.7【审计 P1-1】F12 只读行：仪式锁门控状态（等待活动列表 / 已就绪 / 超时）。</summary>
    private static ConfigEntry<string> _ritualGateCfg;
    private static string _ritualGateCache;
    /// <summary>★ v0.1.3 F12 只读行：鼠标操作说明。</summary>
    private static ConfigEntry<string> _overlayHelpCfg;
    /// <summary>★ v1.3.12 地图选择界面的感染标记开关（实现见 MapInfectionOverlay.cs）。</summary>
    private static ConfigEntry<bool> _mapInfectionCfg;
    private static float _overlayKeyCooldown;
    /// <summary>★ v1.6.5【复核 #6】面板组件自愈检查的节流时间戳（3 秒一次，与面板刷新心跳同频）。</summary>
    private static float _overlayHealTimer;
    /// <summary>★ v1.6.5【复核 #6】"面板组件不在"警告的节流时间戳（最多 60 秒一条，避免刷屏）。</summary>
    private static float _overlayHealLogAt;
    private static float _restartHintTimer;
    private static string _restartHintCache;

    private const string DefaultRelDir = @"SPT_Runtime\user\mods\SPT-EventRotator";

    public void Awake()
    {
        Log = Logger;
        // ★★★【自定义 bot 类型 bossScourge】（灾厄小队冠军）客户端接线：
        //   ① 枚举成员由 BepInEx\patchers\SPT-EventRotator.Patcher.dll 在 Assembly-CSharp 载入前注入；
        //   ② 这里补运行期行为元数据（WildSpawnTypeSettings / ComparableCache）+ 三处 Harmony 行为对齐。
        //   ⚠ 必须在任何 raid / bot 生成之前（Awake 最早）⇒ WildSpawnTypeExtension.Init() 才会看到这条设置。
        try { BossScourgeClient.Install(Log); }
        catch (Exception ex) { Log?.LogError("[bossScourge] 初始化失败: " + ex); }
        // ★★★【第 11 项 · 自定义 bot 类型 cleanupcrew/pillager】（清洁队 / 劫掠者）客户端接线：
        //   枚举成员同样由 Patcher 注入；这里补 _spawnTypeSettings（显示名/击杀提示）+ ComparableCache（随从适配）。
        //   与灾厄小队**互不影响**：不换脑层、不动它的敌对修正。同样必须赶在 bot 生成之前（Awake 最早）。
        try { CleanupPillagerClient.Install(Log); }
        catch (Exception ex) { Log?.LogError("[Cleanup/Pillager] 初始化失败: " + ex); }
        DetectFika();     // ★ v0.1.7【FIKA 感知 · 纯显示】必须在下面前所有 Config.Bind 之前（说明文字要用到 FikaPresent）

        // ★ v1.2.0【面板语言】放在**最顶上独立一段**（用户习惯：先选语言，再看其它设置）
        try
        {
            _panelLangCfg = Config.Bind("★界面语言 | UI LANGUAGE",
                "★面板语言 (panel language)", "中文",
                new ConfigDescription(
                    "只影响本 mod 自己的界面文字：面板标题、页脚标签、提示语、活动分类名。\n"
                  + "物品名称、任务名等仍由游戏本体按游戏内语言显示，不受这里影响。\n\n"
                  + "Affects only THIS MOD's own texts (panel title, footer labels, hints, category names).\n"
                  + "Item/quest names still follow your in-game language.\n\n"
                  + "选 English 后按一次热键重开面板即全部切换。",
                    new AcceptableValueList<string>("中文", "English")));
            Locale.En = string.Equals(_panelLangCfg.Value, "English", StringComparison.OrdinalIgnoreCase);
            _panelLangCfg.SettingChanged += (_, __) =>
            {
                Locale.En = string.Equals(_panelLangCfg.Value, "English", StringComparison.OrdinalIgnoreCase);
                try { EventsOverlay.RequestRefresh(); } catch { }
            };
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 语言项构建失败: {ex}"); }

        // ★★★ v0.1.5【同案例修复】SAIN 的僵尸近战 prefix（SAIN issue #278 / PR #328）：
        //   用协程每 5 秒尝试一次（SAIN 可能比我们晚加载 ⇒ 不能只在 Awake 试一次）。
        try { SainZombieMeleeFix.SetLog(Log); StartCoroutine(SainFixLoop()); } catch (Exception eS) { Log?.LogWarning("[SAIN僵尸近战修复] 启动协程失败: " + eS.Message); }
        _modDirCfg = Config.Bind("活动轮换器 | Event Rotator", "服务端 mod 目录 (server mod dir)", "", Locale.D("留空 = 自动（游戏根目录\\SPT_Runtime\\user\\mods\\SPT-EventRotator）。\nEmpty = auto.",
        "Leave empty = automatic (game root\\SPT_Runtime\\user\\mods\\SPT-EventRotator)."));
        // ★★★ v1.6.5【复核 #3】F12 改「服务端 mod 目录」后，悬浮面板仍读旧目录（要重启游戏才生效）。
        //   原因：`EventsOverlay._modDir` 只在 Attach 里赋一次，而 Attach 见 `Instance != null` 就早退。
        //   这里订阅配置变更，把新目录推给面板（作废状态缓存 + 重启远程拉取协程），改完当场生效。
        _modDirCfg.SettingChanged += (_, __) =>
        {
            try
            {
                var ov = EventsOverlay.Instance;
                if (ov != null) ov.SetModDir(ModDir());
            }
            catch (Exception ex) { try { Log?.LogWarning("[EventRotator.Client] 切换服务端 mod 目录失败: " + ex.Message); } catch { } }
        };
        _scourgeDebugCfg = Config.Bind("活动轮换器 | Event Rotator",
            "★灾厄调试日志 (scourge debug)",
            true,
            Locale.D("每 5 秒把每个灾厄单位的 角色/脑/当前层/决策/位移 打进 BepInEx 日志（排查首领罚站用）\n",
                     "Logs each Scourge unit's role / brain / current layer / decision / movement every 5s\n"));
        _scourgeLegacyBrainCfg = Config.Bind("活动轮换器 | Event Rotator",
            "★灾厄小队套黑狐层 (scourge: use SAIN/BlackDiv layers)",
            false,
            Locale.D("true = 灾厄小队使用旧的 ArenaFighter 脑（会同时吃 SAIN 6 层，掉帧但兼容性最好）\n" +
                     "false = 使用 ScourgeBrain 新脑（掉帧修复，但个别版本可能让首领卡住）\n\n" +
                     "如果发现「灾厄首领站原地不动、随从正常」，先把它改成 true 试一局。",
                     "true = legacy ArenaFighter brain for the Scourge squad (eats SAIN's 6 layers, lower FPS but max compatibility)\n" +
                     "false = new ScourgeBrain (FPS fix; on rare builds the boss may stand still)\n\n" +
                     "If the Scourge boss stands still while followers move, set this to true and test once."));
        _disableAllCfg = Config.Bind("活动轮换器 | Event Rotator", "关闭所有活动 (Disable all events)", false, Locale.D("一键关掉所有活动（写 force.txt=none）。\nTurn off every event.\n\n"
            // ★★★ v0.1.6【规矩 R1】用户 2026-10-16 口述的**轮换新规格**（服务端已落地 Rotation.cs / EventEngine.cs，自检 270/0）
            //   ⇒ F12 说明同步。**纯文本**：不动键名 / 不动分区 / 不动任何逻辑。
            //   来源：audit/轮换规则.md（9 条），下面逐条保留、信息不丢。
          + "【轮换规则】（服务端规格，F12 同步）\n"
          + "产出形状 = **1~2 个「活动」+ 1~2 个「奖励活动」**。\n"
          + "允许组合 = 「AI + 奖励」或「带任务 + 奖励」（**小活动同样算「活动」**，可与奖励组合）。\n"
          + "   ⚠ **硬互斥：AI 与带任务绝不能同时出现**；**带任务最多 1 个** ⇒ 形状 = 1 带任务 + 1~2 奖励。\n"
          + "带任务（event_task）的**日期窗保留**；但「按现实时间自动开」**只在季节轮换未开启时**生效\n"
          + "   （mode ≠ rotate，含 fixed）⇒ 与旧版一致；季节轮换开着（mode = rotate）⇒ **不看日期窗**，由轮换决定。\n"
          + "日期窗重叠 ⇒ **只开 1 个**，取「**窗口开始早**」的（同一天按 events.json 声明顺序，**不随机**）。\n"
          + "`standalone` / `noquest` **永不进池**（只能手动勾选 / force 开）。\n"
          + "手动 force / F12 勾选 ⇒ **组合展开不做日期窗过滤**（所以勾 khorovod_2024 仍会带出 christmas / newyear / christmas_january）。\n"
          + "大季节活动期间是否也轮换 AI / 带任务 = `rotation.rotateAiAndTaskDuringSeasonal`（**默认 false**）。\n"
          + "冲突规则 `rotation.avoidConflicts` 照旧生效。",
        "Turn off every event (writes force.txt=none).\n"
        + "\n"
        + "【Rotation rules】(server-side spec, mirrored in F12)\n"
        + "Output shape = **1~2 「events」 + 1~2 「reward events」**.\n"
        + "Allowed combos = 「AI + rewards」 or 「task event + rewards」 (**small events also count as 「events」** and can be combined with rewards).\n"
        + "   ⚠ **Hard exclusion: AI events and task events never appear together**; **at most 1 task event** ⇒ shape = 1 task event + 1~2 rewards.\n"
        + "The **date window of a task event (event_task) is kept**; but 「auto-start by real date」 **only works while season rotation is off**\n"
        + "   (mode ≠ rotate, fixed included) ⇒ same as the old behavior; with season rotation on (mode = rotate) the **date window is ignored** and the rotation decides.\n"
        + "Overlapping date windows ⇒ **only 1 event is opened**, the one whose 「**window starts earlier**」(same day = declaration order in events.json, **not random**).\n"
        + "`standalone` / `noquest` **never enter the pool** (manual tick / force only).\n"
        + "Manual force / F12 tick ⇒ **combo expansion skips date-window filtering** (so ticking khorovod_2024 still brings out christmas / newyear / christmas_january).\n"
        + "Whether AI / task events also rotate during a big seasonal event = `rotation.rotateAiAndTaskDuringSeasonal` (**default false**).\n"
        + "Conflict rule `rotation.avoidConflicts` still applies as before."));
        _disableAllCfg.SettingChanged += (_, __) => { if (!_suspend) Apply(); };

        // ★★★ v0.1.6 新增 F12 开关：客户端「仪式撤离点锁定」（用户规矩 R1：**改了行为就要连着更新 F12**）。
        //   功能实现见 RitualExitLock.cs：Harmony postfix `BotHalloweenEvent.StartRitual`（Assembly-CSharp:156633）
        //   → 调官方 `ExfiltrationController.EventDisableAllExitsExceptOne`（:521560，自带「关掉其它 + 该点 ×倍率」）。
        //   ★ 取值用 Func 注入（EnabledSource）⇒ RitualExitLock 每帧读到的都是 F12 的**当前值**，无需重启、也不会读到过期副本。
        try
        {
            _ritualExitCfg = Config.Bind("活动轮换器 | Event Rotator", "★仪式开始后只留 1 个撤离点 (ritual: keep 1 exit)", true, Locale.D("官方万圣节语义：**杀了仪式祭司（Zryachiy）、仪式开始之后**，关闭除「你出生点对应的那一个」以外的所有撤离点，\n"
              + "并把保留下来的那个点的撤离时间 ×3。**打仪式之前撤离点完全不变**（官方行为）。\n\n"
              + "   · 保留哪一个 = 由你这一局的**出生点**决定（出生点对应的那组撤离点），不是固定名字；日志会写出来\n"
              + "   · 触发点 = 游戏自己的仪式开始函数，每局只锁一次，重进同一局不会叠加\n"
              + ""
              + "   · 关掉本项 = 撤离点完全按原版/服务端数据走，不会因为仪式而变\n\n"
              + "⚠ **不要和服务端的撤离点锁定同时开**：\n"
              + "   服务端活动 halloween_2023 的 exitKeepOnly 里如果手动写了「@timing=always」（= 进图就锁），\n"
              + "   两边会**叠加 ×3**（撤离时间被乘两次）。本插件检测到「本局只剩 ≤1 个撤离点」会自动跳过，\n"
              + "   但正确做法是**只开一边**：服务端保持默认（不写 @timing = 什么都不锁），把这件事交给本开关。\n\n"
              + "⚠ 「仪式开始」是本地事件，服务端拿不到这个时刻 ⇒ 这一步由客户端完成。",
            "Official Halloween behavior: **after the ritual priest (Zryachiy) is killed and the ritual has started**, every extract except 「the one matching your spawn point」 is closed,\n"
            + "and the extraction time of the point kept is ×3. **Before you start the ritual the extracts do not change at all** (official behavior).\n"
            + "\n"
            + "   · Which one is kept = decided by your **spawn point** in this raid (the extract group matching that spawn), not a fixed name; it is written to the log\n"
            + "   · Trigger = the game's own ritual-start function; locks once per raid, re-entering the same raid does not stack\n"
            + "   · Turning this off = extracts follow vanilla / server data entirely and never change because of the ritual\n"
            + "\n"
            + "⚠ **Do not enable this together with the server-side extract lock**:\n"
            + "   if the server event halloween_2023 has 「@timing=always」 written by hand in exitKeepOnly (= lock on raid start),\n"
            + "   the two **stack ×3** (extraction time multiplied twice). This plugin detects 「only ≤1 extract left in this raid」 and skips,\n"
            + "   but the correct way is to **enable only one side**: leave the server at its default (no @timing = no lock) and let this switch handle it.\n"
            + "\n"
            + "⚠ 「Ritual started」 is a local event the server cannot see ⇒ this step is done by the client."));
            // ★ 每帧现读：不缓存 Value，避免"F12 改了但插件还按旧值跑"
            RitualExitLock.EnabledSource = () => { try { return _ritualExitCfg == null || _ritualExitCfg.Value; } catch { return true; } };
            RitualExitLock.Install(Log);      // ★ 挂 Harmony 补丁（幂等；EventsOverlay.Awake 也会调一次，重复无害）
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 仪式撤离点锁 F12 开关初始化失败: {ex}"); }

        // ★★★ v0.1.6 条款②：June 2022 删档前 Day7「Deadly Toxin 致命毒素」的第 ② 条
        //   官方原文（handover 的 events_wiki.txt:1636-1643）：「* All extracts except one are locked.」
        //   ⇒ **进图即锁**（与上面那条"仪式开始才锁"共用同一把官方刀，只是触发时机不同）。
        //   ⚠ 门控：**只有活动 june2022_toxin 正在生效时才锁**（活动列表来自服务端 当前生效活动.txt，
        //     由 EventsOverlay 每帧解析后推到 RitualExitLock.ToxinActivityActive）⇒ 读不到 = 不锁（fail-safe）。
        //   实现见 RitualExitLock.TickStartLock()（必须等 InitAllExfiltrationPoints :521467 跑完再动手）。
        try
        {
            _toxinExitCfg = Config.Bind("活动轮换器 | Event Rotator", "★进图即锁撤离点 · 仅「致命毒素」生效时 (toxin: lock exits at start)", true, Locale.D("对应官方活动：**June 2022 删档前 Day 7「Deadly Toxin 致命毒素」**的第 ② 条：\n"
              + "   「* All extracts except one are locked.」= 除一个撤离点外全部锁闭。\n\n"
              + "打开本项后：**只有当活动「致命毒素 june2022_toxin」正在生效时**，进图约 2 秒后把撤离点关到只剩\n"
              + "「你出生点对应的那一个」；**该活动没生效时完全不受影响**（读不到活动列表时不会锁）。\n\n"
              + "   · 保留哪一个 = 由你这一局的**出生点**决定（与「仪式开始后只留 1 个」同一套规则），日志会写出来\n"
              + "   · 每局只处理一次；进图 30 秒内拿不到出生点/可用撤离点就**放弃本局**（绝不把点全关掉）\n"
              + ""
              + "   · 关掉本项 = 该活动也不锁撤离点（撤离点按服务端数据正常）\n\n"
              + "⚠ 与上面那条「★仪式开始后只留 1 个撤离点」是**两条独立的线**（本条进图就锁、那条要等仪式开始）。\n"
              + "   同一局里先发生的那条生效；后发生的那条检测到「本局只剩 ≤1 个」会自动跳过（不会 ×3 叠加）。\n"
              + "⚠ 也别同时把服务端 exitKeepOnly 写成「@timing=always」（那是服务端版的「进图就锁」，会撞车；插件有防叠加保护）。",
            "Official event: **June 2022 pre-wipe Day 7 「Deadly Toxin」**, clause ②:\n"
            + "   「* All extracts except one are locked.」= every extract but one is locked.\n"
            + "\n"
            + "With this on: **only while the event 「Deadly Toxin june2022_toxin」 is active**, about 2 seconds into the raid the extracts are cut down to\n"
            + "「the one matching your spawn point」; **when that event is not active nothing happens at all** (no activity list = no locking).\n"
            + "\n"
            + "   · Which one is kept = decided by your **spawn point** in this raid (same rule set as 「keep 1 exit after the ritual」); it is written to the log\n"
            + "   · Handled once per raid; if the spawn point / usable extracts cannot be read within 30 seconds the round is **given up** (never closes every extract)\n"
            + "   · Turning this off = that event does not lock extracts either (extracts follow server data as usual)\n"
            + "\n"
            + "⚠ This and 「★keep 1 exit after the ritual starts」 above are **two independent paths** (this one locks on raid start, that one waits for the ritual).\n"
            + "   In one raid whichever happens first wins; the later one sees 「only ≤1 extract left」 and skips automatically (no ×3 stacking).\n"
            + "⚠ Also do not write 「@timing=always」 into the server's exitKeepOnly (that is the server-side 「lock on raid start」 and it will collide; the plugin guards against stacking)."));
            RitualExitLock.StartLockEnabledSource = () => { try { return _toxinExitCfg == null || _toxinExitCfg.Value; } catch { return true; } };
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 致命毒素「进图即锁」F12 开关初始化失败: {ex}"); }

        // ★★★ v0.1.6 条款③：June 2022 删档前 Day7「致命毒素」第 ③ 条（**用户已裁决**）
        //   官方原文：「The Unknown toxin debuff is given to your PMC at the start of the raid, which gives you
        //              8 minutes to live unless you find an antidote on Bosses or airdrops.」
        //   裁决：ⓐ 走"零 globals 改动"那条（插件计时 + Kill(EDamageType.LethalToxin)，不用缺失的 Effects.LethalIntoxication）；
        //         ⓑ **超人不带副作用** ⇒ 用**新增的** Buffs_EventToxinSuperhuman（**不用** BuffsZombieInfection，它带脱水/听觉副作用）；
        //         ⓒ 只解毒素（原生 RemoveIntoxication，不走未验证的"解病毒"路）。
        //   实现见 ToxinInfection.cs；门控与条款② 同源（只在该活动生效时跑）。
        try
        {
            _toxinBuffCfg = Config.Bind("活动轮换器 | Event Rotator", "★「致命毒素」感染与倒计时 (toxin: infection + 8min countdown)", true, Locale.D("对应官方活动：**June 2022 删档前 Day 7「Deadly Toxin 致命毒素」**的第 ③ 条：\n"
              + "   「The Unknown toxin debuff is given to your PMC at the start of the raid, which gives you\n"
              + "     8 minutes to live unless you find an antidote on Bosses or airdrops.」\n\n"
              + "打开本项后（**只有当活动「致命毒素 june2022_toxin」正在生效时**）：\n"
              + "   · 进图即感染「未知毒素」⇒ 开始 **8 分钟倒计时**（官方 8 minutes to live）\n"
              + "   · 同时获得「超人」增益：**回血 +50 / 能量 +50 / 活力技能 +20**，**不含任何副作用**\n"
              + "   · 倒计时到点 ⇒ **暴毙**（EDamageType.LethalToxin）\n"
              + "   · 打 **xTG-12 解毒剂**（空投箱 / SCAV 口袋 / boss 身上刷）⇒ 毒素解除、**倒计时重置为 8 分钟**；\n"
              + "     解毒剂自带 60 秒原生保护期，之后会重新感染（再打一针可再续）\n"
              + "   · 该活动没生效时：本项**完全不起作用**（不会做任何动作）\n"
              + "\n"
              + "⚠ 「超人」增益需要服务端配置到位；\n"
              + "   若服务端没配：**只有毒素、没有超人**（不会崩、也不会误判死亡）。\n"
              + "⚠ 安全护栏：**只有真正观察到毒素效果之后才开始计时**（buff 名不存在 / 免疫技能挡掉 ⇒ 绝不判死）。",
            "Official event: **June 2022 pre-wipe Day 7 「Deadly Toxin」**, clause ③:\n"
            + "   「The Unknown toxin debuff is given to your PMC at the start of the raid, which gives you\n"
            + "     8 minutes to live unless you find an antidote on Bosses or airdrops.」\n"
            + "\n"
            + "With this on (**only while the event 「Deadly Toxin june2022_toxin」 is active**):\n"
            + "   · You are infected with 「Unknown toxin」 on raid start ⇒ an **8-minute countdown** begins (official 8 minutes to live)\n"
            + "   · You also gain the 「superhuman」 buff: **health regen +50 / energy +50 / vitality skill +20**, **with no side effects at all**\n"
            + "   · When the countdown runs out ⇒ **instant death** (EDamageType.LethalToxin)\n"
            + "   · Injecting the **xTG-12 antidote** (spawns in airdrops / SCAV pockets / on bosses) ⇒ toxin cleared and **the countdown resets to 8 minutes**;\n"
            + "     the antidote itself has a 60-second vanilla protection window, after which you get reinfected (inject again to extend)\n"
            + "   · When that event is not active this item **does nothing** (performs no action)\n"
            + "\n"
            + "⚠ The 「superhuman」 buff requires matching server-side config;\n"
            + "   if the server has none: **toxin only, no superhuman buff** (no crash, no false death).\n"
            + "⚠ Safety rail: **the timer only starts after the toxin effect is actually observed** (buff name missing / blocked by an immunity skill ⇒ never judged dead)."));
            ToxinInfection.EnabledSource = () => { try { return _toxinBuffCfg == null || _toxinBuffCfg.Value; } catch { return true; } };
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 致命毒素「感染与倒计时」F12 开关初始化失败: {ex}"); }

        // ★★★ v0.1.6：Halloween 2022 官方条款「**开局随机给玩家一个负面状态**」
        //   官方逐字（handover 的 events_wiki.txt:1584）：
        //     「* A random negative status effect is given to your PMC at the start a raid.」
        //   ⇒ 官方**没点名**是哪几个，链接的是健康系统的「Temporary status effects」列表；
        //     本实现从**本机现成的纯负面 buff** 里随机（候选池与"连带 Effects.*"核对见 RandomDebuff.cs 顶部表格）。
        //   门控：只在该活动 halloween_2022 生效时跑（与条款②③ 同款 fail-safe）。
        try
        {
            _randDebuffCfg = Config.Bind("活动轮换器 | Event Rotator", "★开局随机负面状态 (Halloween 2022: random raid-start debuff)", true, Locale.D("对应官方活动：**Halloween 2022（2022-10-27）** 的条款「开局随机给玩家一个负面状态」：\n"
              + "   「* A random negative status effect is given to your PMC at the start a raid.」（wiki 原文）\n\n"
              + "打开本项后（**只有当活动「halloween_2022」正在生效时**）：进图后**随机**给你上一个负面状态，四选一：\n"
              + "   · 出血（轻出血 100% + 重出血 50%）\n"
              + "   · 脑震荡（头部 100 秒）+ 骨折\n"
              + "   · 脑震荡 + 骨折 + 疼痛（钝击）\n"
              + "   · 冻伤（体温下降 / 手抖 / 耐力·水分·能量·体力上限下降）\n"
              + "   · 该活动没生效时：本项**完全不起作用**（不会做任何动作）\n"
              + "   · 每局只随机一次\n\n"
              + "⚠ 官方那句**没有点名具体状态**（只说 random）⇒ 本实现取「本体现成的纯负面状态」作为**忠实近似**，\n"
              + "   候选池刻意避开：会给正面增益的（BuffsZombieInfection）、永久不可恢复的（未知毒素）、\n"
              + "   以及含「低概率秒死」条目的（Buffs_Obdolbos 有 25% 概率 HealthRate −600）。\n"
              + "⚠ 候选取自本机 globals 的 Effects.Stimulator.Buffs（四选一都逐条核过「连带读哪个 Effects.*」，见代码注释）。",
            "Official event: **Halloween 2022 (2022-10-27)**, clause 「a random negative status at raid start」:\n"
            + "   「* A random negative status effect is given to your PMC at the start a raid.」(wiki wording)\n"
            + "\n"
            + "With this on (**only while the event 「halloween_2022」 is active**): on raid start you are given **one random** negative status, one of four:\n"
            + "   · Bleeding (light bleeding 100% + heavy bleeding 50%)\n"
            + "   · Concussion (head, 100 s) + fracture\n"
            + "   · Concussion + fracture + pain (blunt)\n"
            + "   · Frostbite (lower body temperature / hand tremor / lower stamina·hydration·energy·health caps)\n"
            + "   · When that event is not active this item **does nothing** (performs no action)\n"
            + "   · Randomised only once per raid\n"
            + "\n"
            + "⚠ The official wording **does not name the statuses** (it only says random) ⇒ this implementation takes the game's existing purely-negative statuses as a **faithful approximation**,\n"
            + "   deliberately avoiding: ones that grant a positive buff (BuffsZombieInfection), permanent irreversible ones (Unknown toxin),\n"
            + "   and ones with a 「small chance of instant death」 entry (Buffs_Obdolbos has a 25% chance of HealthRate −600).\n"
            + "⚠ Candidates come from the local globals Effects.Stimulator.Buffs (all four were checked one by one against 「which Effects.* they pull in」, see code comments)."));
            RandomDebuff.EnabledSource = () => { try { return _randDebuffCfg == null || _randDebuffCfg.Value; } catch { return true; } };
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 「开局随机负面状态」F12 开关初始化失败: {ex}"); }

        // ★★ 强制敌视：已改为代码硬开关（见 FORCE_HOSTILE_ENABLED），不再写配置项，
        //    避免旧 cfg 没有该键导致"默认关闭"失效、补丁照样挂上、照样卡界面。

        // ★★★ v2.1.1 关键修复：**立即构建 F12 活动开关**。
        //   旧实现把 BuildEventToggles() 放在 StartRaidInit() 里（只在"检测到游戏世界"时跑一次），
        //   结果玩家在主界面打开 F12 只看到 2 项（关闭所有活动 / mod 目录），**根本开不了想开的活动**。
        //   日志实证：'events listed in N categories' 一条都没出现过 → 该函数从未执行。
        //   构建配置项本身极轻（读一次 events.json + Config.Bind），放 Awake 完全安全。
        try { BuildEventToggles(); SyncFromFile(); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] Awake 构建配置项失败: {ex}"); }

        // ★ v2.1.10 轮换/季节 的 F12 下拉框（读写服务端 config.json）
        try { _rotation = new RotationConfig(this, Config); _rotation.Load(); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 轮换配置构建失败: {ex}"); }

        // ★★ v0.1.2 放在**最后 Bind** → cfg 里最后出现 → F12 面板最底下（用户要求）
        try
        {
            _activeStatusCfg = Config.Bind(
                "★★★★ 当前生效的活动（看这里）| ACTIVE NOW",
                "★ 现在开着哪些活动（只读，服务端写）", "(尚未读取)", Locale.D("【只读】显示服务端此刻实际开启的活动（含自动轮换 / 奖励轮换开的）。\n\n"
              + "按热键还能弹出悬浮面板看同样的内容（带每个活动的内容说明）——\n"
              + "   到上面【★★ 悬浮面板：当前生效活动】分区里设一个按键（默认空 = 不启用）。\n\n"
              + "内容由服务端每次套用活动后写入 当前生效活动.txt，客户端每 2 秒读一次。\n\n"
              + StatusSourceLine(ModDir()) + FikaNoteText() + CoopLockNoteText()
              + "看哪几行：\n"
              + "   · 来源 = 手动强制 / 自动（日期窗-轮换-奖励轮换）\n"
              + "   · 共 N 个活动同时生效 → 下面逐个列出（带分类标签）\n"
              + "   · 最后一行是天气季节模式\n\n"
              + "⚠ 改了活动 / 轮换设置后要【重启服务端】才会更新这里。\n"
              // ★★★ v0.1.6【F12 说明纠错 · 用户要求「不要遗漏组合活动的开启关闭，手动开启关闭也遵循这个原则」】
              //   旧文案说"只开你勾的那一个" —— **错的**：服务端 EventEngine.ExpandFamilyCombos（EventEngine.cs:524）
              //   会在手动强制（:396）和自动（:492）两条路上都做"同族整组展开"。实测日志铁证：
              //   `FORCED event(s) = khorovod_2024` → `[组合] 同族整组展开：1 → 5 个（新增 christmas,newyear,cultist_pilgrimage,christmas_january）`
              + "⚠ 手动勾选活动（写 force.txt）：**轮换和奖励轮换会跳过**，但**官方组合表仍然生效** ——\n"
              + "   勾一个会按族**连带展开**整组（例：勾 khorovod_2024 ⇒ 自动带出 christmas / newyear / christmas_january / cultist_pilgrimage）。\n"
              + "   **关掉时同理**（整组一起退）；但若同族还有别的成员在开（含手动强制），被取消的那个**会被重新带出来** ⇒ 想整组退请把该族的『年份种子』一起取消。\n"
              + "   每个活动的 tooltip 里现在都写明了『这一族包含谁 / 勾它会连带谁』。",
                "\n【Read-only】Shows the events the server actually has active right now (including auto-rotated / reward-rotated ones).\n\n"
                + "Press the hotkey to pop up an overlay panel with the same content (plus an explanation per event) —\n"
                + "   set a key in the 【★★ Overlay panel: active events】 section above (default empty = disabled).\n\n"
                + "The server writes the content to 当前生效活动.txt every time it applies events; the client reads it every 2 seconds.\n\n"
                + StatusSourceLine(ModDir()) + FikaNoteText() + CoopLockNoteText()
                + "Which lines to read:\n"
                + "   · Source = manual force / automatic (date window - rotation - reward rotation)\n"
                + "   · N events active at the same time → each is listed below (with its category tag)\n"
                + "   · The last line is the weather season mode\n\n"
                + "⚠ After changing events / rotation settings you must 【restart the server】 for this to update.\n"
                + "⚠ Manually ticking an event (writes force.txt): **rotation and reward rotation skip it**, but the **official combo table still applies** —\n"
                + "   ticking one expands the whole family (e.g. ticking khorovod_2024 also brings christmas / newyear / christmas_january / cultist_pilgrimage).\n"
                + "   **Unticking works the same way** (the whole group goes); but if another member of the family is still on (including manually forced), the cancelled one **is brought back** ⇒ to drop a whole family, cancel its 『year seed』 as well.\n"
                + "   Every event tooltip now states 『who this family contains / what ticking it brings along』."));
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 状态显示构建失败: {ex}"); }

        // ★★★ v0.1.3 新增：悬浮面板（按热键弹出「当前生效的活动」）
        //   交互参考 DrakiaXYZ 的 SPT-QuestTracker：一个键切显隐、半透明面板、每条「名字 + 右侧标签」+ 下面说明行。
        //   我们的数据来自服务端写的 当前生效活动.txt，UI 纯代码绘制（不依赖任何 .bundle 预制体）。
        try
        {
            // ★ v1.3.0【联机】客机从这里拉服务端的真实状态（主机不用填）
            _remoteUrlCfg = Config.Bind(CatOverlay, "★主机地址 (host address, for Fika guests)", "", Locale.D("联机（Fika）当客机时填【主机】的 SPT 地址，例如 http://113.219.237.121:6969\n"
              + "填了以后面板会直接从主机拉取真实状态（天气季节、当前活动），不再显示本机的旧文件。\n\n"
              + "留空 = 自动尝试 http://127.0.0.1:6969（你本机开服时就是对的）。\n"
              + "自己开服 / 玩单机 → 留空即可。",
            "When playing as a Fika client, enter the 【host】's SPT address, e.g. http://113.219.237.121:6969\n"
            + "Once filled, the panel pulls the real state (weather season, active events) straight from the host instead of showing stale local files.\n"
            + "\n"
            + "Empty = try http://127.0.0.1:6969 automatically (correct when the server runs on your own machine).\n"
            + "Hosting yourself / playing single-player → leave it empty."));
            EventsOverlayConfig.RemoteStatusUrl = (_remoteUrlCfg.Value ?? "").Trim();
            _remoteUrlCfg.SettingChanged += (_, __) =>
                EventsOverlayConfig.RemoteStatusUrl = (_remoteUrlCfg.Value ?? "").Trim();

            _overlayKeyCfg = Config.Bind(CatOverlay, "★弹出面板热键 (toggle key)", new KeyboardShortcut(UnityEngine.KeyCode.F8), Locale.D("按这个键在游戏里弹出/收起「当前生效的活动」面板。\n\n"
              + "点右边的按钮可以改键。默认 F8（F12 是 BepInEx 配置面板，不能占用；\n"
              + "小键盘 1、2 已被本 mod 的敌对性诊断占用）。\n\n"
              + "面板内容：每个当前生效的活动一行名字 + 分类标签，名字下面一行说明这个活动到底改了什么。\n"
              + "主界面和战局内都能用。",
            "Press this key in game to show/hide the 「active events」 panel.\n"
            + "\n"
            + "Click the button on the right to rebind. Default F8 (F12 is the BepInEx config panel and cannot be used;\n"
            + "numpad 1 and 2 are already taken by this mod's hostility diagnostics).\n"
            + "\n"
            + "Panel content: one line per active event with its name + category tag, and a line under the name saying what the event actually changes.\n"
            + "Works in the main menu and in raid."));
            _overlayAutoHideCfg = Config.Bind(CatOverlay, "自动收起秒数 (auto hide)", 0f, Locale.D("弹出后多少秒自动收起。0 = 不自动收（一直显示到再按一次热键）。",
            "Seconds before the panel hides itself. 0 = never auto-hides (stays up until you press the hotkey again)."));
            _overlayFontCfg = Config.Bind(CatOverlay, "字号 (font size)", 14, Locale.D("面板字号。觉得挡视野就调小（12~14 比较合适）。",
            "Panel font size. Make it smaller if it blocks your view (12~14 works well)."));
            _overlayRightCfg = Config.Bind(CatOverlay, "靠右显示 (align right)", true, Locale.D("true = 贴右上角；false = 贴左上角。",
            "true = pin to the top-right corner; false = pin to the top-left corner."));
            // ★★ v0.1.5（用户要求）：**拖边框改面板大小，字体不变**。0 = 自动（宽度按字号 / 高度按内容）。
            _overlayWidthCfg = Config.Bind(CatOverlay, "★面板宽度像素 (panel width px)", 0f, Locale.D("拖面板右边或右下角可以直接改大小，改完会自动写回这里。\n"
              + "0 = 自动（宽度按字号算）。**改宽度不会改变字号** —— 只是每行能放更多字。",
            "Drag the panel's right or bottom-right edge to resize it; the new size is written back here automatically.\n"
            + "0 = auto (width derived from the font size). **Changing the width does not change the font size** — it only fits more characters per line."));
            _overlayHeightCfg = Config.Bind(CatOverlay, "★面板高度像素 (panel height px)", 0f, Locale.D("同上：拖右下角改高度，0 = 自动（按活动内容算）。调大可以一次看到更多活动行。",
            "Same as above: drag the bottom-right corner to change the height, 0 = auto (derived from the event content). Bigger shows more event lines at once."));
            try
            {
                var wc = _overlayWidthCfg; var hc = _overlayHeightCfg;
                EventsOverlayConfig.SaveSize = () =>
                {
                    try { wc.Value = EventsOverlayConfig.PanelWidth; hc.Value = EventsOverlayConfig.PanelHeight; } catch { }
                };
            }
            catch { }
            // ★★★ v1.0.2【性能 · F12 开关】把 HostilityGuard.DiagOn 接到 F12；**默认 false**（性能优先）
            //   以前它是 internal static bool = false，**没有任何 UI 开关** ⇒ 排查时只能改代码。
            _diagLogCfg = Config.Bind(CatOverlay, "★ 诊断日志（排查用）(diag log)", false, Locale.D("打开后会把 [HOST] 的诊断日志写进 BepInEx 日志（敌对性判定 / 扫描 / 强制加敌的每一步）。\n"
              + "用途：排查敌对性、或怀疑某处没生效时临时打开；**平时请关闭** —— 诊断会拼字符串，开着有额外开销。\n"
              + "**改完立即生效**（不用重启游戏）。",
            "When on, [HOST] diagnostic logs are written to the BepInEx log (every step of the hostility decision / scan / forced hostility).\n"
            + "Use it to investigate hostility, or temporarily when you suspect something is not taking effect; **keep it off normally** — diagnostics build strings and cost extra.\n"
            + "**Takes effect immediately** (no game restart needed)."));
            try { HostilityGuard.DiagOn = _diagLogCfg.Value; } catch { }        // 启动时立即同步
            _diagLogCfg.SettingChanged += (_, __) =>
            {
                try { HostilityGuard.DiagOn = _diagLogCfg.Value; } catch { }    // ★ 实时同步（改了就生效）
            };

            _overlayProbeCfg = Config.Bind(CatOverlay, "★ 自检角标（排查用）(GUI probe)", false, Locale.D("打开后游戏左上角会一直显示一行绿字：\n"
              + "   「OnGUI 已调用 N 次，visible=…，活动数=…」\n\n"
              + "用途：如果按了热键却看不到面板，看这行就能分清是哪一层出问题：\n"
              + "   · 有绿字但 N 不涨        → 游戏没在跑 IMGUI（这个面板方案不可用，需换成游戏内 UI）\n"
              + "   · 有绿字、N 在涨、visible=true、活动数>0 → 面板在画，只是位置/颜色不对（调字号/靠右）\n"
              + "   · 有绿字但 visible=false  → 热键没被识别（换个键 / 看日志有没有『已弹出』）\n"
              + "   · 连绿字都没有           → 插件没加载或 OnGUI 没被调用\n\n"
              + "打开时还会把面板内部状态写进 BepInEx 日志（每秒一次）。平时请保持关闭。",
            "When on, a line of green text is always shown in the top-left corner of the game:\n"
            + "   「OnGUI called N times, visible=…, events=…」\n"
            + "\n"
            + "Use: if pressing the hotkey shows no panel, this line tells you which layer is broken:\n"
            + "   · Green text but N does not grow            → the game is not running IMGUI (this panel approach cannot work; it would need in-game UI)\n"
            + "   · Green text, N growing, visible=true, events>0 → the panel is being drawn, only position/color is off (adjust font size / align right)\n"
            + "   · Green text but visible=false             → the hotkey is not recognised (try another key / check the log for 『shown』)\n"
            + "   · No green text at all                     → the plugin is not loaded or OnGUI is never called\n"
            + "\n"
            + "While on, the panel's internal state is also written to the BepInEx log (once per second). Keep it off normally."));

            _overlayHelpCfg = Config.Bind(CatOverlay, "★ 鼠标怎么用（只读）(mouse help)", "(说明)", Locale.D("面板弹出后支持鼠标操作：\n\n"
              + "   · 鼠标滚轮        → 滚动活动内容（活动多时右侧出现滚动条）\n"
              + "   · 鼠标左键按住拖动 → 移动面板位置（拖动/滚动期间不会自动收起）\n"
              + "   · **鼠标拖右边/右下角 → 改面板大小（字号不变）**，松手自动保存进本配置\n"
              + "   · Ctrl + 滚轮     → 缩放面板（0.85× ~ 1.60×）\n"
              + "   · Shift + Ctrl+滚轮 → 微调（每次 3%）\n"
              + "   · 鼠标中键（点击面板上）→ 活动列表滚回顶部\n"
              + "   · 双击面板        → 位置与缩放一起复位\n"
              + "   · 按住 Alt + 滚轮 → 鼠标不在面板上也能滚动（战局内指针被锁时用）\n\n"
              + "当前缩放倍率显示在上一条「需不需要重启」的末尾。\n"
              + "缩放只是**临时倍率**（不写进配置文件）；想永久改大小请调上面的「字号」。",
            "Once the panel is open you can use the mouse:\n"
            + "\n"
            + "   · Mouse wheel            → scroll the event content (a scrollbar appears on the right when there are many events)\n"
            + "   · Hold left button, drag  → move the panel (it does not auto-hide while dragging / scrolling)\n"
            + "   · **Drag the right / bottom-right edge → resize the panel (font size unchanged)**, saved into this config on release\n"
            + "   · Ctrl + wheel           → zoom the panel (0.85× ~ 1.60×)\n"
            + "   · Shift + Ctrl + wheel   → fine zoom (3% per step)\n"
            + "   · Middle mouse button (on the panel) → scroll the event list back to the top\n"
            + "   · Double-click the panel → reset position and zoom together\n"
            + "   · Hold Alt + wheel       → scroll even when the pointer is not over the panel (useful when the cursor is locked in raid)\n"
            + "\n"
            + "The current zoom factor is shown at the end of 「restart needed?」 above.\n"
            + "Zoom is only a **temporary multiplier** (not written to the config file); to change the size permanently use 「font size」 above."));
            _overlayKeyCfg.SettingChanged += (_, __) => ApplyOverlayConfig();
            _overlayProbeCfg.SettingChanged += (_, __) => ApplyOverlayConfig();

            // ★★★ v1.3.12【地图感染标记】纯自绘：在【地图选择界面】的地点名右侧画 ☣，
            //   颜色按服务端写的 <modDir>\感染率.txt 分档（白 1~33 / 黄 34~66 / 红 67~100，0 = 不画）。
            //   文件不存在 ⇒ 什么都不画（服务端没开启感染时零影响）。
            _mapInfectionCfg = Config.Bind(CatOverlay, "★地图感染标记 (map infection markers)", true, Locale.D(
                "在【地图选择】界面的地点名右侧显示一个 ☣ 感染标记（纯客户端自绘，不依赖官方感染 UI）：\n"
              + "   · 白色 = 感染率 1~33%\n   · 黄色 = 34~66%\n   · 红色 = 67~100%\n"
              + "   · 0% 或服务端没有写数据 ⇒ 不显示\n\n"
              + "数据来源：服务端 mod 目录下的「感染率.txt」（格式 # mapKey=百分比）；文件不存在时不会有任何标记，也不报错。\n"
              + "改完立即生效（不用重启游戏）。",
                "Shows a ☣ infection marker next to each location on the 【map selection】 screen (drawn by the client, not the official infection UI):\n"
              + "   · white = 1~33%   · yellow = 34~66%   · red = 67~100%\n"
              + "   · 0% / no data from the server ⇒ nothing is shown\n\n"
              + "Data comes from 「感染率.txt」 in the server mod folder (format: # mapKey=percent); if the file is missing nothing is drawn and no error is raised.\n"
              + "Takes effect immediately (no game restart needed)."));
            MapInfectionOverlay.Enabled = _mapInfectionCfg.Value;
            _mapInfectionCfg.SettingChanged += (_, __) =>
            {
                try { MapInfectionOverlay.Enabled = _mapInfectionCfg.Value; } catch { }
            };
            // ★★★ C4【F12 改字号/靠右/自动收起/面板宽高**当场**不生效】
            //   以前只有"热键"和"自检角标"两项挂了 SettingChanged → ApplyOverlayConfig()，
            //   其余 5 项（字号 / 靠右 / 自动收起 / 宽度 / 高度）改完只是写进 cfg，
            //   要等下一次 ApplyOverlayConfig（例如按热键触发组件自愈重建）才同步到 UI。
            //   现在这 5 项也挂**同一个**回调 ⇒ 改完下一帧就生效（配置项名字/含义均不变）。
            _overlayFontCfg.SettingChanged += (_, __) => ApplyOverlayConfig();
            _overlayRightCfg.SettingChanged += (_, __) => ApplyOverlayConfig();
            _overlayAutoHideCfg.SettingChanged += (_, __) => ApplyOverlayConfig();
            _overlayWidthCfg.SettingChanged += (_, __) => ApplyOverlayConfig();
            _overlayHeightCfg.SettingChanged += (_, __) => ApplyOverlayConfig();

            EventsOverlay.Attach(ModDir());
            ApplyOverlayConfig();
            // ★ v1.3.12 地图选择界面的 ☣ 感染标记（独立 MonoBehaviour，自带 OnGUI；默认开启）
            //   v1.3.13：把「地图标记大小」F12 项交给它自己绑（配置细节留在 MapInfectionOverlay.cs）
            MapInfectionOverlay.Configure(Config, CatOverlay);
            MapInfectionOverlay.Attach(() => ModDir());

            // ★ v0.1.3：把「需不需要重启」也做成 F12 只读行 —— 不按热键也能一眼看到
            _restartHintCfg = Config.Bind(CatOverlay, "★ 需不需要重启（只读）(restart needed?)", "(尚未读取)", Locale.D("【只读】自动判断当前活动是否需要重启才能生效：\n\n"
              + "   · 显示「需要重启服务端」→ 轮换时间到了但服务端还没重启，或你手动选的活动还没被服务端套用\n"
              + "   · 显示「需要重启游戏」  → 服务端已经换好活动，但本客户端是在那之前启动的\n"
              + "     （物品模板 / globals 只在游戏启动时拉取一次，所以要重开游戏）\n"
              + "   · 显示「无需重启」      → 一切正常\n\n"
              + "★ 关于轮换：轮换是**服务端启动时按时间预先算好**的 —— 你启动服务端那一刻就已经是当前档位。\n"
              + "   所以正常流程是「开服务端 → 开游戏」，不需要「先进游戏看到旧活动 → 再重启」。\n"
              + "   换档发生在每天 config.json 里 rotation.slotRebaseHour 指定的整点（默认凌晨 4 点）。",
            "【Read-only】automatically decides whether the current state needs a restart to take effect:\n"
            + "\n"
            + "   · Shows 「server restart needed」→ a rotation boundary has passed but the server has not been restarted, or the event you picked by hand has not been applied yet\n"
            + "   · Shows 「game restart needed」  → the server already switched events, but this client started before that\n"
            + "     (item templates / globals are only pulled once at game startup, so the game has to be restarted)\n"
            + "   · Shows 「no restart needed」    → everything is fine\n"
            + "\n"
            + "★ About rotation: the rotation is **pre-computed from the clock when the server starts** — the moment you start the server you are already on the current slot.\n"
            + "   So the normal flow is 「start the server → start the game」; no 「enter the game, see the old event, restart again」 needed.\n"
            + "   The slot changes at the hour set by rotation.slotRebaseHour in config.json each day (default 04:00)."));

            // ★★★ v0.1.7【审计 P1-1】把「仪式锁门控」也做成 F12 只读行 —— 让用户能一眼看到
            //   它是"在等活动列表 / 已就绪 / 已超时（本局不会锁撤离点）"，而不是静默失效。
            _ritualGateCfg = Config.Bind(CatOverlay, "★ 仪式锁门控状态（只读）(ritual-lock gate)", "(尚未读取)", Locale.D("【只读】「仪式开始后只留 1 个撤离点」这条功能的**门控状态**：\n\n"
              + "   · 等待活动列表（未就绪 ⇒ 暂不锁）→ 还没读到服务端的「当前生效活动」列表，**暂时不会锁**\n"
              + "   · 已就绪 · 有万圣节活动 ⇒ 会锁  → 门控通过，仪式一开始就会按官方实现锁撤离点\n"
              + "   · 已就绪 · 无万圣节活动 ⇒ 不锁  → 当前轮换里没有万圣节季节活动（例如 only june2022_toxin 那种局）\n"
              + "   · ★超时 → 连续 30 秒拿不到活动列表 ⇒ **本局不会锁任何撤离点**（绝不会\"超时就乱锁\"）\n\n"
              + "★ 若长期显示「★超时」：检查 ① 服务端是否写出了「当前生效活动.txt」；② F12 里「服务端 mod 目录」是否填对。\n"
              + "★ 语义保证：**拿不到活动列表就绝不锁** —— 宁可功能不生效，也不会误锁非万圣节局。",
            "【Read-only】**gate state** of the 「keep 1 exit after the ritual starts」 feature:\n"
            + "\n"
            + "   · Waiting for the event list (not ready ⇒ no locking yet) → the server's 「active events」 list has not been read yet, **nothing is locked for now**\n"
            + "   · Ready · Halloween event present ⇒ will lock  → gate passed; as soon as the ritual starts the extracts are locked as in the official implementation\n"
            + "   · Ready · no Halloween event ⇒ will not lock  → the current rotation has no Halloween seasonal event (e.g. a june2022_toxin-only raid)\n"
            + "   · ★Timeout → the activity list could not be read for 30 seconds straight ⇒ **nothing is locked this raid** (never \"lock blindly on timeout\")\n"
            + "\n"
            + "★ If 「★Timeout」 keeps showing: check ① whether the server wrote 「当前生效活动.txt」; ② whether 「server mod dir」 in F12 is correct.\n"
            + "★ Guarantee: **no activity list ⇒ never lock** — the feature may stay inactive rather than lock a non-Halloween raid by mistake."));
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 悬浮面板初始化失败: {ex}"); }

        Log.LogInfo("[EventRotator.Client] 已加载（配置项已就绪；AI 补丁仍延迟到进图）");
        try { HostilityGuard.SetDir(ModDir()); HostilityGuard.SetLog(Log); } catch { }

        // ★ v0.1.3 决策：**不再对游戏本体打任何 Harmony 补丁**。
        //   「手续费显示」和「实验室门禁」这两件事本来就该由**服务端数据**解决：
        //     · 实验室免票 → 服务端把该图 AccessKeys 置空
        //       （客户端 method_54 看到空数组 → list.Count==0 → 直接放行，已离线验证）
        //     · 跳蚤手续费 → 服务端 globals 的 CommunityItemTax / CommunityRequirementTax 归零
        //       （客户端 CalculateTaxPrice 读的就是它；1e-6 显示 0 ₽ 且 >0 不触发按钮禁用）
        //   走数据而不是劫持客户端逻辑：不改游戏行为、不随 0.16.x 方法名变化失效、无需维护补丁。
    }

    /// <summary>
    /// ★ 强制敌视开关（v1.65 重新开启）。
    ///   注：之前"卡主界面"已确认与服务端启动顺序有关（服务端未就绪时客户端请求数据会卡），
    ///   与本插件无关。因此这里放心开启。
    ///   配套防护：反射缓存、热键闸门、15 秒预热期、异常熔断。
    /// </summary>
    internal const bool FORCE_HOSTILE_ENABLED = true;

    /// <summary>是否启用"强制敌视"。</summary>
    internal static bool ForceHostileOn() => FORCE_HOSTILE_ENABLED;

    /// <summary>是否已完成进图初始化。</summary>
    private static bool _started;

    /// <summary>配置项是否已构建（幂等保护：Awake 与 StartRaidInit 都会调用 BuildEventToggles）。</summary>
    private static bool _togglesBuilt;

    private static float _lastHotkey;
    private static float _lastBeat;
    private static int _beats;

    /// <summary>
    /// 进图瞬间调用：挂补丁 + 载入名单 + 建配置项。**只做一次**。
    /// 由 DeferredInit() 在检测到游戏世界时触发；此时 bot 还没生成，所以不会错过时机。
    /// </summary>
    internal void StartRaidInit()
    {
        if (_started) return;
        _started = true;
        Log.LogInfo("[EventRotator.Client] ★进入游戏世界 → 开始初始化…");

        // ① 敌视名单（读 events.json）
        try { HostilityGuard.Load(ModDir(), Log); HostilityGuard.SetLog(Log); HostilityGuard.SetDir(ModDir()); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] HostilityGuard.Load 失败: {ex.Message}"); }

        // ② F12 配置项（71 个活动开关）—— 始终构建
        try { BuildEventToggles(); SyncFromFile(); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] BuildEventToggles 失败: {ex.Message}"); }

        // ③ AI 补丁：**只有开启"强制敌视"才挂**（这是导致卡主界面/卡部署的高危部分）
        if (!ForceHostileOn())
        {
            Log.LogInfo("[EventRotator.Client] 强制敌视=关闭(默认) → 不挂任何 AI 补丁，零风险");
            Log.LogInfo($"[EventRotator.Client] ready. {_toggles.Count} events listed. dir={ModDir()}");
            return;
        }

        Log.LogInfo("[EventRotator.Client] 强制敌视=开启 → 挂载 AI 补丁（实验性）");

        // ★★★ 关键时机：BigBrain 只在 bot 的 brain 激活那一刻注入已注册的层
        //     （BotBaseBrainActivatePatch 是 BaseBrain.Activate 的 prefix）。
        //     所以必须**在 bot 生成之前**把 SAIN 战斗层注册好 → 这里立刻预注册。
        try { SainLayerBinder.PreRegister(); } catch (Exception ex) { Log.LogError("[SAIN层补齐] PreRegister 失败: " + ex.Message); }

        try { new BotsGroupIsPlayerEnemyPatch().Enable(); Log.LogInfo("[EventRotator.Client] ★★★已挂载 BotsGroup.IsPlayerEnemy"); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] BotsGroup.IsPlayerEnemy 失败: {ex.Message}"); }
        try { new GetEnemyBotTypesPatch().Enable(); Log.LogInfo("[EventRotator.Client] ★已挂载 BotSettings.GetEnemyBotTypes"); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] GetEnemyBotTypes 失败: {ex.Message}"); }
        try { new BotsControllerInitPatch().Enable(); Log.LogInfo("[EventRotator.Client] 已挂载 BotsController.Init"); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] BotsController.Init 失败: {ex.Message}"); }
        // ★★ 已移除 BotOwnerFixedUpdatePatch ★★
        //    它挂在 BotOwner.FixedUpdate（Unity 物理帧）上跑反射，会干扰 bot 的角色控制器/物理，
        //    实测导致「AI 悬浮在半空、刷新不完全」，进而使战斗层无法执行（知道敌人也开不了枪）。
        //    SAIN 注入路径（EnemyKnown=True）已完全覆盖其功能，因此不再需要。
        // ★ 刻意不挂 AddMemberForceEnemyPatch（它在部署期就会被游戏的 AddMember 触发 → NRE → 卡部署）
        try { new AddNeutralToEnemyPatch().Enable(); Log.LogInfo("[EventRotator.Client] ★★★已挂载 BotsGroup.AddNeutral"); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] AddNeutral 失败: {ex.Message}"); }
        try { new BotSettingsIsPlayerEnemyPatch().Enable(); Log.LogInfo("[EventRotator.Client] ★已挂载 BotSettings.IsPlayerEnemy"); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] BotSettings.IsPlayerEnemy 失败: {ex.Message}"); }
        // ★ v0.1.5 同案例修复：SAIN 的僵尸近战 prefix（issue #278 / PR #328）
        try { SainZombieMeleeFix.SetLog(Log); SainZombieMeleeFix.Tick(); } catch { }
        // ★ 把 7 个自定义 role 注册进 SAIN 的分类表（Boss 档，克隆 bossKnight）
        try { SainBotTypeRegister.SetLog(Log); SainBotTypeRegister.Tick(); } catch { }

        Log.LogInfo($"[EventRotator.Client] ready. {_toggles.Count} events listed. dir={ModDir()}");
    }

    /// <summary>把 F12 的面板设置同步到 UI 组件（改完立刻生效，不用重启）。</summary>
    private void ApplyOverlayConfig()
    {
        try
        {
            EventsOverlayConfig.ToggleKey = _overlayKeyCfg?.Value ?? KeyboardShortcut.Empty;
            EventsOverlayConfig.AutoHideSeconds = UnityEngine.Mathf.Max(0f, _overlayAutoHideCfg?.Value ?? 0f);
            EventsOverlayConfig.FontSize = UnityEngine.Mathf.Clamp(_overlayFontCfg?.Value ?? 14, 8, 32);
            EventsOverlayConfig.AlignRight = _overlayRightCfg?.Value ?? true;
            EventsOverlayConfig.PanelWidth = UnityEngine.Mathf.Max(0f, _overlayWidthCfg?.Value ?? 0f);
            EventsOverlayConfig.PanelHeight = UnityEngine.Mathf.Max(0f, _overlayHeightCfg?.Value ?? 0f);
            EventsOverlay.ProbeAlwaysDraw = _overlayProbeCfg?.Value ?? false;
            EventsOverlay.LogEnabled = _overlayProbeCfg?.Value ?? false;   // 自检开关同时打开面板诊断日志

            // ★★★ v0.1.6【面板事故防复发 · 纯日志，不改行为】把**插件当前真正认的**面板热键打进日志。
            //   本次「按 9 面板不出来」排查结论：日志/代码里没有任何异常，真正原因是**用户的 cfg 里
            //   热键从 `Keypad9` 变回了默认 `F8`**（cfg 于 06:19 还是 Keypad9，20:46 已是 F8；
            //   而 section/键名三份版本完全一致 ⇒ 不是键改名导致，是值本身丢了）。
            //   ⇒ 以后遇到"面板不出来"，先看这行就知道插件认的是哪个键。
            try
            {
                var k = EventsOverlayConfig.ToggleKey;
                string kk = k.MainKey == UnityEngine.KeyCode.None
                    ? "(未设置)"
                    : (k.Modifiers.Any() ? string.Join("+", k.Modifiers) + "+" + k.MainKey : k.MainKey.ToString());
                if (!string.Equals(kk, _loggedHotkey, StringComparison.Ordinal))
                {
                    _loggedHotkey = kk;
                    Log.LogInfo($"[EventRotator.Client] 面板热键 = {kk}"
                              + (k.MainKey == UnityEngine.KeyCode.F8
                                 ? "（= 默认值；若你改过键却显示 F8 ⇒ cfg 里的值丢了，请在 F12 里重新点一次按键）"
                                 : ""));
                }

                // ★★★ v0.1.6【热键安全网 · 只提示，绝不自动改用户的值】
                //   判据：热键 == 默认 F8，**但同一个面板分区里的其它项被改过**
                //   ⇒ 说明这位用户是"会调面板设置的人"，热键却是默认值 ⇒ 高度疑似"值丢了"，
                //     给一条明确的自救指引（用户当初设的 Keypad9 现在就是这种情况）。
                //   ⚠ 刻意**不自动写回**任何值：动用户配置是越权。
                if (!_hotkeyHinted && k.MainKey == UnityEngine.KeyCode.F8
                    && (EventsOverlayConfig.AutoHideSeconds > 0f
                        || !EventsOverlayConfig.AlignRight
                        || EventsOverlay.ProbeAlwaysDraw
                        || EventsOverlayConfig.FontSize != 14
                        || EventsOverlayConfig.PanelWidth > 0f
                        || EventsOverlayConfig.PanelHeight > 0f))
                {
                    _hotkeyHinted = true;
                    Log.LogWarning("[EventRotator.Client] ⚠ 面板热键目前是**默认 F8**，但同分区其它设置被改过 —— "
                                 + "如果你以前把热键设成过别的键（例如小键盘 9）而按它不出来，说明 cfg 里那个值丢了："
                                 + "请到 F12 →【★★ 悬浮面板：当前生效活动】分区 →『★弹出面板热键』**重新点一次按键**。"
                                 + "（插件不会自动改你的配置值）");
                }
            }
            catch { }
        }
        catch { }
    }

    /// <summary>★ v0.1.6：上一次已打进日志的面板热键（只在变化时打，避免刷屏）。</summary>
    private static string _loggedHotkey;
    /// <summary>★ v0.1.6：热键疑似丢值的提示只打一次。</summary>
    private static bool _hotkeyHinted;

    /// <summary>热键是否被按下（支持组合键；空键 = 不启用）。</summary>
    // ★ v0.1.5：SAIN 僵尸近战修复的周期尝试（挂在每帧都会走的地方）
    internal static void TickSainFix() { try { SainZombieMeleeFix.Tick(); } catch { } }

    /// <summary>★ v0.1.5：每 5 秒尝试摘除 SAIN 的僵尸近战 prefix（成功后内部不再重复）。</summary>
    private System.Collections.IEnumerator SainFixLoop()
    {
        while (true)
        {
            try { SainZombieMeleeFix.Tick(); } catch { }
            try { SainBotTypeRegister.Tick(); } catch { }
            yield return new UnityEngine.WaitForSeconds(5f);
        }
    }

    private static bool OverlayKeyPressed()
    {
        try
        {
            var k = EventsOverlayConfig.ToggleKey;
            if (k.MainKey == UnityEngine.KeyCode.None) return false;
            if (!UnityEngine.Input.GetKeyDown(k.MainKey)) return false;
            foreach (var m in k.Modifiers) if (!UnityEngine.Input.GetKey(m)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>热键 + 延迟初始化。★ 每一步都有插桩日志，便于精确定位卡在哪一步。</summary>
    public void Update()
    {
        // ★★★ v1.0.1【性能·第十三轮】每帧摊开 SAIN 注入（每次 2 只）—— 削掉 140ms 的周期性尖峰
        try { HostilityGuard.TickInjections(); } catch { }

        // ★★★ v1.6.5【复核 #6】面板组件被场景加载销毁后，只有按热键才会重建（实测日志有 Instance=null）。
        //   期间的 3 秒状态刷新（EventsOverlay.Update）与 5 秒远程拉取（FetchRemoteLoop）两条心跳同时停摆。
        //   ⇒ 这里每 3 秒低频自检一次，丢了就重建（Attach 内部会重新 StartCoroutine + OnDestroy 已清引用）。
        try
        {
            _overlayHealTimer += UnityEngine.Time.unscaledDeltaTime;
            if (_overlayHealTimer >= 3f)
            {
                _overlayHealTimer = 0f;
                if (EventsOverlay.Instance == null)
                {
                    // 警告节流 60 秒：极端情况下（组件一直建不起来）别每 3 秒刷一条
                    float nowH = UnityEngine.Time.realtimeSinceStartup;
                    if (nowH - _overlayHealLogAt > 60f)
                    {
                        _overlayHealLogAt = nowH;
                        Log.LogWarning("[EventRotator.Client] 面板组件不在（Instance=null）→ 自愈重建（3 秒心跳）");
                    }
                    EventsOverlay.Attach(ModDir());
                    ApplyOverlayConfig();
                }

                // ★ v1.3.12 地图感染标记组件同样自愈（它自己带 OnGUI；被场景销毁后不重建就再也不画了）
                if (MapInfectionOverlay.Instance == null)
                    MapInfectionOverlay.Attach(() => ModDir());
            }
        }
        catch (Exception ex) { try { Log?.LogWarning("[EventRotator.Client] 面板自愈检查失败: " + ex.Message); } catch { } }

        // ★ v2.5.1 读服务端写的「当前生效活动.txt」，变了就刷新 F12 显示
        try
        {
            if (_activeStatusCfg != null)
            {
                // ★ v1.6.5【复核 #4】原来这里是 `const string` 中文；英文界面会直出。中文逐字不变。
                var notWritten = Locale.T("(服务端还没写过 当前生效活动.txt —— 请重启一次服务端)",
                                          "(the server has not written 当前生效活动.txt yet — please restart the server once)");
                // ★★★ C7【客机 F12 只读行读本机旧文件】
                //   旧代码这里写死 `Path.Combine(ModDir(), "当前生效活动.txt")` —— 联机当客机时，
                //   这个本机文件的最后写入者是自己上次开服 ⇒ 面板/F12 显示的是**旧活动**。
                //   现在统一走 `EventsOverlay.StatusFilePath()`（远程缓存优先），与悬浮面板同一路径规则。
                // ★★★ C10【重复 stat + 重复轮询】
                //   旧代码每 5 秒自己 FileInfo + ReadAllText 一次；而悬浮面板（EventsOverlay.Update）
                //   本来就每 3 秒读同一个文件。现在**直接复用面板已经读好并解析过的那份数据**
                //   （`RawStatusText` + `StatusFileTime` 变化即刷新），只有面板组件不在时才自己兜底读。
                var ov = EventsOverlay.Instance;
                string txt = null;
                if (ov != null)
                {
                    if (ov.StatusFileTime != _statusFileTimeSeen)
                    {
                        _statusFileTimeSeen = ov.StatusFileTime;
                        txt = ov.RawStatusText;
                        // ★ v1.6.5【复核 #6】组件刚被重建时 RawStatusText 还是 null（要等面板下一轮 3 秒刷新）；
                        //   已有旧文本就**先留着**，别立刻刷成"服务端还没写过"（改目录/场景切换时会闪一下错提示）。
                        if (string.IsNullOrEmpty(txt)) txt = _statusCache == null ? notWritten : null;
                    }
                    else if (_statusCache == null)
                    {
                        // 面板还没读到任何数据（首次 3 秒内 / 文件确实不存在）→ 5 秒后给一句占位提示
                        _statusTimer += UnityEngine.Time.unscaledDeltaTime;
                        if (_statusTimer >= 5f) { _statusTimer = 0f; txt = notWritten; }
                    }
                }
                else
                {
                    // 面板组件不在（极端情况）：退回到"同一路径规则 + 节流读盘"的兜底
                    _statusTimer += UnityEngine.Time.unscaledDeltaTime;
                    if (_statusTimer >= 5f)
                    {
                        _statusTimer = 0f;
                        try
                        {
                            var sp = EventsOverlay.StatusFilePath(ModDir());
                            var fi = new FileInfo(sp);
                            if (fi.Exists) txt = File.ReadAllText(sp, System.Text.Encoding.UTF8);
                        }
                        catch { }
                        if (string.IsNullOrEmpty(txt)) txt = notWritten;
                    }
                }
                if (txt != null && !string.Equals(txt, _statusCache, StringComparison.Ordinal))
                {
                    _statusCache = txt;
                    _activeStatusCfg.Value = txt;
                }
            }
        }
        catch { }

        // ★★★ v0.1.3：悬浮面板热键（主界面和战局内都能用 —— 与"当前生效活动"只读显示同一份数据）
        try
        {
            float nowOv = UnityEngine.Time.realtimeSinceStartup;
            if (nowOv - _overlayKeyCooldown > 0.3f && OverlayKeyPressed())
            {
                _overlayKeyCooldown = nowOv;
                // ★ 组件可能因为场景切换 / 初始化异常而不在（Instance 为 null）→ 这里**自愈式重建**，
                //   否则会出现"日志一直打已收起、面板永远不出现"的假象（实测踩到：Toggle 根本没被调用）。
                if (EventsOverlay.Instance == null)
                {
                    Log.LogWarning("[EventRotator.Client] 面板组件不在（Instance=null）→ 重新创建");
                    EventsOverlay.Attach(ModDir());
                    ApplyOverlayConfig();
                }
                var ov = EventsOverlay.Instance;
                if (ov == null)
                {
                    Log.LogError("[EventRotator.Client] 面板组件创建失败（AddComponent 没成功），本次按键无效");
                }
                else
                {
                    ov.Toggle();
                    Log.LogInfo("[EventRotator.Client] 当前生效活动面板：" + (ov.IsVisible ? "已弹出" : "已收起")
                                + $"（OnGUI 调用 {ov.GuiCalls} 次{(ov.GuiCalls == 0 ? " ← 如果一直为 0，说明游戏没跑 IMGUI" : "")}）");
                }
            }
        }
        catch (Exception ex) { Log.LogWarning("[EventRotator.Client] 面板热键处理失败: " + ex.Message); }

        // ★ v0.1.3：每 3 秒刷新"需不需要重启"（轮换跨过换档点时会自动变）
        try
        {
            _restartHintTimer += UnityEngine.Time.unscaledDeltaTime;
            if (_restartHintTimer >= 3f && _restartHintCfg != null)
            {
                _restartHintTimer = 0f;
                var ov = EventsOverlay.Instance;
                if (ov != null)
                {
                    // ★ C10：这里**不再调 `ov.Refresh()`** —— 悬浮面板的 Update 已经每 3 秒自己读一次
                    //   （`EventsOverlay.Update`），这里再读=同一份文件重复 stat。只取它算好的结论。
                    var hint = ov.RestartHint();
                    // ★ C5：硬编码中文 → Locale.T（中文文案逐字不变）
                    var text = string.IsNullOrEmpty(hint)
                        ? Locale.T("无需重启（当前活动已是最新）", "No restart needed — the active events are up to date")
                        : hint;
                    // ★ v1.6.5【复核 #4】末尾的"[面板缩放 x×]"以前是硬编码中文，实机英文日志里确实出现过。
                    text += Locale.T($"　[面板缩放 {ov.Zoom:0.00}×]", $"　[panel zoom {ov.Zoom:0.00}×]");
                    if (!string.Equals(text, _restartHintCache, StringComparison.Ordinal))
                    {
                        _restartHintCache = text;
                        _restartHintCfg.Value = text;
                        Log.LogInfo("[EventRotator.Client] 重启提示：" + text.Replace("\n", " "));
                    }
                }
            }
        }
        catch { }

        // ★★★ v0.1.7【审计 P1-1】F12 只读：仪式锁门控三态（等待活动列表 / 已就绪 / 超时）
        try
        {
            if (_ritualGateCfg != null)
            {
                // ★ v1.6.5【复核 #4】门控状态行（含前缀）以前整串中文 ⇒ 英文界面直出。中文逐字不变。
                var g = Locale.T("等待活动列表（未就绪 ⇒ 暂不锁）", "waiting for the event list (not ready ⇒ no locking yet)");
                try { g = RitualExitLock.HalloweenGateText; } catch { }
                var gText = Locale.T("[仪式锁门控] ", "[ritual-lock gate] ") + g;
                if (!string.Equals(gText, _ritualGateCache, StringComparison.Ordinal))
                {
                    _ritualGateCache = gText;
                    _ritualGateCfg.Value = gText;
                    Log.LogInfo("[EventRotator.Client] " + gText);
                }
            }
        }
        catch { }

        try
        {
            // ★★ 热键只在"已进图"后处理：主界面完全不读键
            if (_started)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastHotkey > 0.5f)
                {
                    if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Keypad2))
                    {
                        _lastHotkey = now;
                        HostilityGuard.FlushDiag(ModDir());
                        Log.LogInfo("[EventRotator.Client] 敌对性诊断已写出（小键盘 2）");
                    }
                    else if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Keypad1))
                    {
                        _lastHotkey = now;
                        SainProbe.Dump(Log);
                        SainProbe.DumpFirstHostileState(Log);   // ★ 敌方状态链条探查
                        Log.LogInfo("[EventRotator.Client] SAIN 探针已输出（小键盘 1）");
                    }
                }
            }
        }
        catch { }

        // ★ 插桩 1：DeferredInit（含耗时测量）
        try
        {
            DeferredInit();
        }
        catch (Exception ex) { Log.LogError("[EventRotator.Client] DeferredInit 异常: " + ex.Message); }

        // ★ 插桩 2：补调 AddPlayer
        //   ★★★ 关键修复：GetMainPlayer() 内部有 FindObjectOfType（全场景扫描），
        //       之前它每帧都被调用 → 主线程被拖垮 → 卡加载。
        //       现在改为调用无参版 ReapplyZoneAddPlayer()，它内部**先判 5 秒节流**再取玩家。
        try
        {
            if (HostilityGuard.Enabled && ForceHostileOn())
            {
                HostilityGuard.ReapplyZoneAddPlayer();
            }
        }
        catch (Exception ex) { Log.LogError("[EventRotator.Client] ReapplyZoneAddPlayer 异常: " + ex.Message); }

        // ★ 插桩 3：扫描强制加敌（含耗时）
        try
        {
            HostilityGuard.ScanAndForceEnemy();
        }
        catch (Exception ex) { Log.LogError("[EventRotator.Client] ScanAndForceEnemy 异常: " + ex.Message); }
    }

    private static bool _deferredDone;

    /// <summary>探测游戏世界是否存在。低频调用（每 0.5 秒），避免每帧 FindObjectOfType 造成卡顿。</summary>
    private static float _nextProbe;
    private bool IsGameWorldPresentThrottled()
    {
        try
        {
            if (UnityEngine.Time.unscaledTime < _nextProbe) return false;
            _nextProbe = UnityEngine.Time.unscaledTime + 0.5f;
            return IsGameWorldPresent();
        }
        catch { return false; }
    }

    private static Type _gwT;
    private static bool _gwTSearched;

    /// <summary>找到 EFT.GameWorld 类型（只找一次，之后用缓存）。</summary>
    private static Type FindGwType()
    {
        if (_gwTSearched) return _gwT;
        _gwTSearched = true;
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType("EFT.GameWorld", false); if (t != null) { _gwT = t; return _gwT; } } catch { }
            }
        }
        catch { }
        return _gwT;
    }

    private static bool IsGameWorldPresent()
    {
        try
        {
            // ★ 判据改为 Singleton<IBotGame>.Instance（BotsController 就是它的属性）：
            //   主界面时该单例不存在 → 不会提前挂补丁 → 不卡加载。
            //   比 FindObjectOfType(EFT.GameWorld) 更晚、更可靠。
            if (HostilityGuard.HasBotGame()) return true;

            // 退化：GameWorld 存在且玩家已在世界内
            var t = FindGwType();
            if (t == null) return false;
            var gw = UnityEngine.Object.FindObjectOfType(t);
            return gw != null && HostilityGuard.HasLocalPlayer();
        }
        catch { return false; }
    }

    /// <summary>
    /// 延迟初始化触发点：一旦检测到游戏世界存在（= 真的进图），立刻做完整初始化。
    /// 主界面阶段这里会返回；之后每 0.5 秒探测一次。
    /// </summary>
    private void DeferredInit()
    {
        if (_deferredDone) return;
        if (!IsGameWorldPresentThrottled()) return;

        _deferredDone = true;
        StartRaidInit();
    }

    // ★ v2.1.10 给 RotationConfig 用的公开访问器
    public string ModDirPublic => ModDir();
    public void LogPublic(string m) { try { Log.LogInfo("[EventRotator.Client] " + m); } catch { } }
    public bool SuspendCfg { get => _suspend; set => _suspend = value; }
    private RotationConfig _rotation;

    // ★ v2.5.1 F12 底部「当前生效的活动」只读显示（内容由服务端写 当前生效活动.txt）
    private ConfigEntry<string> _activeStatusCfg;
    private float _statusTimer;
    private string _statusCache;
    /// <summary>★ C10 F12 只读行复用的"面板最后看到的状态文件时间"（变化才重建文本，不再自己 stat）。</summary>
    private DateTime _statusFileTimeSeen = DateTime.MinValue;

    private string ModDir()
    {
        var d = _modDirCfg.Value;
        if (!string.IsNullOrWhiteSpace(d)) return d;
        try { return Path.Combine(BepInEx.Paths.GameRootPath, DefaultRelDir); } catch { return DefaultRelDir; }
    }
    private string EventsFile() => Path.Combine(ModDir(), "events.json");
    private string ForceFile() => Path.Combine(ModDir(), "force.txt");

    // ══════════════════════════════════════════════════════════════════════════
    // ★★★ v0.1.6【F12 勾选状态 ↔ 分区 解耦】
    //
    // 【问题】活动开关是 `Config.Bind(section = 分类区名, key = "事件: <id>")` —— BepInEx 的
    //   条目身份是 **(section, key) 这一对**。服务端把某个活动的 `category` 一改 ⇒ 分区名变 ⇒
    //   旧的那行成了"孤儿"、新分区按**默认值**重建 ⇒ **cfg 里看起来"勾选丢了"**，
    //   而且每次改分类都会在 cfg 里多留一批孤儿行（实测 42 行）。
    //
    // 【实测澄清（重要）】用户**真正生效**的开关状态在 `force.txt`，而且 `SyncFromFile()`
    //   （:1117-1135）**每次启动都会用 force.txt 覆盖所有勾选** ⇒ 改 `category` **不会**
    //   让用户实际开着的活动变掉（例：force.txt 里的 drum_mag_scavs/underbarrel_gl_scavs/rpg_scavs
    //   正是刚被改分类的三个，它们启动后照样是勾上的）。**本段修的是 cfg 这份镜像**：
    //   cfg 不再是"分区一改就清零"的易碎品，也不会再堆孤儿。
    //
    // 【做法（方案 B · 保留 5 个显示分区 + 启动迁移）】为什么不选"所有开关塞进一个固定分区"（方案 A）：
    //   那会**干掉 F12 的五段分类显示**（① 季节 / ② 带任务 / ★ AI / ③ 小活动 / ④ 奖励）——
    //   那是刚修好、写在回归基线里的东西，不能为了省事牺牲掉。
    //   ⇒ 保留分区做"显示分组"，值改为**跟着活动 id 走**：启动时若当前分区没有这个键、
    //     而**别的分区**有同名键（= 以前那份），就把值搬过来（**只搬、不删旧行**，绝不删用户数据）。
    //
    // 【纯函数、可单测】解析与判定都抽成下面的静态方法（探针 %TEMP%\f12check 会直接调它们）。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 解析 BepInEx cfg 原文 → { 分区名: { 键: 原始值 } }。
    /// 格式：`[分区]` / `# 注释` / `键 = 值`（值可能是别的 `=`，所以只按**第一个** " = " 切）。
    /// </summary>
    internal static Dictionary<string, Dictionary<string, string>> ParseRawCfg(string path)
    {
        var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return res;
            string sec = "";
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine ?? "";
                if (line.Length == 0) continue;
                if (line[0] == '#') continue;                       // 注释
                if (line[0] == '[' && line.EndsWith("]", StringComparison.Ordinal))
                {
                    sec = line.Substring(1, line.Length - 2);
                    if (!res.ContainsKey(sec)) res[sec] = new Dictionary<string, string>(StringComparer.Ordinal);
                    continue;
                }
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq <= 0) continue;
                if (sec.Length == 0) continue;
                var key = line.Substring(0, eq);
                var val = line.Substring(eq + 3);
                res[sec][key] = val;
            }
        }
        catch { }
        return res;
    }

    /// <summary>
    /// ★ 迁移判定（纯函数，可单测）：
    /// 当前分区**没有**这个键、而**别的分区**有同名键 ⇒ 返回那份值和它的来源分区。
    /// 当前分区已有值（用户正常设置过）⇒ 一律**不动**（返回 false）。
    /// </summary>
    internal static bool TryFindMigratedValue(Dictionary<string, Dictionary<string, string>> raw,
                                              string currentSection, string key,
                                              out string fromSection, out string value)
    {
        fromSection = null; value = null;
        try
        {
            if (raw == null || string.IsNullOrEmpty(key)) return false;
            if (raw.TryGetValue(currentSection ?? "", out var cur) && cur != null && cur.ContainsKey(key))
                return false;                                       // 当前位置已有值 ⇒ 不迁移（绝不覆盖用户当前设置）
            foreach (var kv in raw)
            {
                if (string.Equals(kv.Key, currentSection, StringComparison.Ordinal)) continue;
                if (kv.Value != null && kv.Value.TryGetValue(key, out var v))
                {
                    fromSection = kv.Key; value = v;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>把 F12 的轮换/季节设置写进服务端 config.json（由 RotationConfig 触发）。</summary>
    public void ApplyRotationConfig()
    {
        try { _rotation?.Save(); }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] 写 config.json 失败: {ex}"); }
    }

    private void BuildEventToggles()
    {
        // ★ 幂等：Awake 和 StartRaidInit 都会调用，重复 Config.Bind 同一 key 会产生重复项/异常
        if (_togglesBuilt) return;
        // ★★★ C3【首次缺 events.json ⇒ 整局无活动开关 + 仪式锁静默失效】
        //   旧代码在**读文件之前**就 `_togglesBuilt = true` ⇒ 首次启动时若服务端还没写出 events.json
        //   （先开游戏后开服务端 / 全新安装），这里早退后**永远不会再试**：
        //     · F12 里一个活动开关都没有（整局开不了活动）；
        //     · `SeasonalEventById` 一直是空的 ⇒ 仪式锁门控静默失效。
        //   现在：只有**成功建完**才置位；任何早退 / 异常都保持 false，下一次（StartRaidInit）还会重试。
        int togglesBefore = _toggles.Count;
        try
        {
            var f = EventsFile();
            if (!File.Exists(f))
            {
                Log.LogWarning($"[EventRotator.Client] events.json not found at {f}（F12 活动开关暂缺；进图/下次初始化会自动重试）");
                return;
            }
            var list = JsonConvert.DeserializeObject<List<EventInfo>>(File.ReadAllText(f));
            if (list == null)
            {
                Log.LogWarning($"[EventRotator.Client] events.json 解析为空（{f}）；F12 活动开关暂缺，稍后自动重试");
                return;
            }

            // ★★★ v0.1.6【F12 勾选状态 ↔ 分区 解耦】在 **Bind 之前**先读原始 cfg：
            //   Bind 之后"旧分区那行"就成孤儿了，必须先留一份原文才能把值搬回来。
            var rawCfg = ParseRawCfg(Config?.ConfigFilePath);
            int migrated = 0;

            // ★★★ v0.1.6【修法 1 · 仪式锁活动门控】建 id → SeasonalEvent 索引（供"这局是不是万圣节局"判断）。
            //   原料：events.json 的 `seasonalEvent` 字段（我在 BuildDesc 里已用过它）。
            //   口径：`halloween` / `halloween_2022` / `halloween_2023` 三个的 seasonalEvent 都是 "Halloween"
            //        （`night_of_the_cult` 没有该字段 ⇒ 不参与门控；真万圣节局由那三兄弟触发即可）。
            SeasonalEventById.Clear();
            foreach (var e0 in list)
            {
                if (e0 == null || string.IsNullOrEmpty(e0.Id) || string.IsNullOrEmpty(e0.SeasonalEvent)) continue;
                SeasonalEventById[e0.Id] = e0.SeasonalEvent;
            }
            if (SeasonalEventById.Count > 0)
                Log.LogInfo("[EventRotator.Client][仪式锁门控] 季节活动索引：" + string.Join("、",
                    SeasonalEventById.Select(kv => kv.Key + "=" + kv.Value)));

            // ★★★ v0.1.6【官方组合】先建"组合族"索引（BuildDesc 要用）→ family → 该族全部成员
            _familyIndex.Clear();
            foreach (var e0 in list)
            {
                if (e0 == null || string.IsNullOrEmpty(e0.Family) || string.IsNullOrEmpty(e0.Id)) continue;
                if (!_familyIndex.TryGetValue(e0.Family, out var fl) || fl == null)
                    _familyIndex[e0.Family] = fl = new List<EventInfo>();
                fl.Add(e0);
            }
            if (_familyIndex.Count > 0)
                Log.LogInfo("[EventRotator.Client][官方组合] 组合族索引：" + string.Join("；",
                    _familyIndex.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Select(x => x.Id))}]")));

            foreach (var e in list)
            {
                if (string.IsNullOrEmpty(e.Id)) continue;
                var disp = $"{e.NameZh} ({e.NameEn})";
                // ★★★ v0.1.6【组合标注】列表里一眼看出"这个活动属于哪一族、会被连带"
                if (!string.IsNullOrEmpty(e.Family))
                    disp += e.FamilyGeneric == true ? $"  【组合 {e.Family}·通用】" : $"  【组合 {e.Family}{(e.FamilyYear.HasValue ? "·" + e.FamilyYear.Value : "")}】";
                string desc = BuildDesc(e);
                // ★ 三层分类 → F12 里分三个区显示
                var cat = Category(e);
                var key = "事件: " + e.Id;
                var entry = Config.Bind(cat, key, false,
                    new ConfigDescription(desc, null, new ConfigurationManagerAttributes { DispName = disp, Order = (int)(100000 - _toggles.Count) }));

                // ★★ 分区迁移：本活动以前在**别的分区**（= 服务端改过它的 category）⇒ 把那份勾选值搬过来。
                //    · 只搬、不删旧行（绝不删用户数据）
                //    · 当前分区已有值 ⇒ TryFindMigratedValue 直接返回 false（绝不覆盖用户现在的设置）
                //    · 此刻还没挂 SettingChanged ⇒ 赋值不会触发 Apply/写 force.txt；再加 _suspend 双保险
                if (TryFindMigratedValue(rawCfg, cat, key, out var fromSec, out var rawVal))
                {
                    if (bool.TryParse(rawVal, out var mv))
                    {
                        _suspend = true;
                        try { entry.Value = mv; } finally { _suspend = false; }
                        migrated++;
                        if (mv) Log.LogInfo($"[EventRotator.Client][F12迁移] '{key}' 的勾选值 true 从旧分区『{fromSec}』搬到『{cat}』（分类改名不再丢状态）");
                    }
                    else
                    {
                        Log.LogWarning($"[EventRotator.Client][F12迁移] '{key}' 在旧分区『{fromSec}』的值「{rawVal}」不是 true/false ⇒ 跳过（保持默认）");
                    }
                }

                // ★★★ v1.6.5【复核 #8 · 本轮 C3 引入】C3 让构建失败后**下次进图重试**，但这里原来是匿名 lambda：
                //   匿名委托无法对称退订 ⇒ 每重试一次，同一个 ConfigEntry.SettingChanged 上就再挂一份回调
                //   （勾一次活动 Apply() 跑 N 次、写 force.txt N 次）。
                //   ⇒ 改用**命名方法** + 先 `-=` 再 `+=`：无论重试多少次，永远只有一份订阅。
                entry.SettingChanged -= OnEventToggleChanged;
                entry.SettingChanged += OnEventToggleChanged;
                _toggles.Add(new KeyValuePair<ConfigEntry<bool>, string>(entry, e.Id));
            }
            if (migrated > 0)
                Log.LogInfo($"[EventRotator.Client][F12迁移] 共 {migrated} 个活动的勾选值已搬回当前分区"
                          + "（⚠ 用户实际开关仍以 force.txt 为准，SyncFromFile 随后会再对齐一次）");

            // ★★★ v0.1.6【F12 清理陈旧活动键】用户删活动（events.json 少了 drum_mag_scavs / underbarrel_gl_scavs / arms_race）
            //   之后，cfg 里仍留着 `事件: <id>` 的旧键（BepInEx 把"没被 Bind 的条目"当**孤儿条目**继续写回文件）
            //   ⇒ 这里按"键名以 `事件: ` 开头 且 id 不在当前活动表里"清掉，**绝不碰用户其它设置**。
            //   ⚠ 拿不到活动表 ⇒ `BuildEventToggles` 早退（:812/:814），本段根本不会执行 ⇒ fail-safe ✓
            CleanStaleEventKeys(list, rawCfg);

            Log.LogInfo($"[EventRotator.Client] {_toggles.Count} events listed in 3 categories.");
            _togglesBuilt = true;   // ★ C3：只有**完整构建成功**才置位（早退/异常都保持 false ⇒ 下次还会重试）
        }
        catch (Exception ex)
        {
            // ★ C3：异常路径也不置位；并把本次已加进 `_toggles` 的半成品条目撤掉，避免重试时同一活动出现两遍
            //   ★ v1.6.5【复核 #8】事件订阅不需要在这里退订：订阅处用的是命名方法 + `先 -= 再 +=`，
            //   所以旧条目的订阅即使还在，重试时也只会被替换成同一份（不会叠加）。
            try { if (_toggles.Count > togglesBefore) _toggles.RemoveRange(togglesBefore, _toggles.Count - togglesBefore); } catch { }
            Log.LogError($"[EventRotator.Client] build toggles failed: {ex}");
        }
    }

    /// <summary>
    /// ★ v1.6.5【复核 #8】活动开关的统一点选回调。
    /// <para>**必须是命名方法**：匿名 lambda 每次都会生成新的委托实例，无法对称退订；
    /// 而 <see cref="BuildEventToggles"/> 在 C3 之后会"失败再重试"，用 lambda 就会重复订阅。</para>
    /// </summary>
    private void OnEventToggleChanged(object sender, EventArgs e)
    {
        try { if (!_suspend) Apply(); } catch { }
    }

    // ══════════ ★★★ v0.1.7【FIKA 感知（🟢 三项）+ 用户裁决「联机时不锁撤离」】══════════
    //   🟢 三项 = 纯显示/纯日志，**判定逻辑一行不动**：
    //     ① 启动时探测有没有装 FIKA（`com.fika.core`）⇒ 只打一条日志；
    //     ② F12 状态区加"数据来源（本机路径）"一行；
    //     ③ 相关日志加后缀 —— **只在检测到 FIKA 时加**。
    //   ⚠ 关键回归性质：**单机（没装 FIKA）时 FikaLogSuffix = ""、FikaNoteText() = ""**
    //     ⇒ 日志与说明文字与改动前**逐字相同**（拼 "" ⇒ 无任何差异）。
    /// <summary>是否装了 FIKA（`com.fika.core`）。**只影响日志/说明文字 + 联机时不锁撤离**。</summary>
    internal static bool FikaPresent { get; private set; }
    /// <summary>FIKA 版本号（探测不到时为空串）。</summary>
    internal static string FikaVersion { get; private set; } = "";

    /// <summary>只在检测到 FIKA 时附加的日志后缀（单机 = 空串 ⇒ 日志逐字不变）。</summary>
    internal static string FikaLogSuffix => FikaPresent ? "（FIKA 联机：本机判定）" : "";

    /// <summary>F12 里的"联机说明"（单机 = 空串 ⇒ 说明文字逐字不变）。纯文本、面向玩家。</summary>
    internal static string FikaNoteText()
    {
        if (!FikaPresent) return "";
        return Locale.D("\n【联机（FIKA " + FikaVersion + "）说明】\n"
             + " 本 mod 的**活动效果**（首领 / 敌人 / 空投 / 爆率 / 商人 / 天气）由服务端决定 ⇒ 联机时**所有玩家共享**。\n"
             + " 但这几项是**每台电脑各自判定**的：撤离点锁定 · 未知毒素与倒计时 · 开局随机负面状态。\n"
             + "   · 只有自己这台电脑读到了这些活动数据，功能才会在自己身上生效；\n"
             + "   · 队友没装本 mod（或读不到数据）时，他那边不会生效 —— 会出现「你只剩 1 个撤离点、他还是好几个」这类不一致；\n"
             + "   · 想保持一致：让每位玩家都装**同一版本**的本 mod。\n",
            "\n【Co-op (FIKA " + FikaVersion + ") notes】\n"
             + " This mod's **event effects** (bosses / enemies / airdrops / loot rates / traders / weather) are decided by the server ⇒ **shared by all players** in co-op.\n"
             + " But these are **judged per machine**: extract locking · unknown toxin and its countdown · random raid-start debuff.\n"
             + "   · A machine only gets the feature if it can read that event data itself;\n"
             + "   · A teammate without this mod (or without the data) will not see it — you get inconsistencies like 「you have 1 extract left, they still have several」;\n"
             + "   · To stay consistent: have every player install the **same version** of this mod.\n");
    }

    /// <summary>F12 状态区的"数据来源"一行（纯显示；联机时点明这是本机判定）。</summary>
    internal static string StatusSourceLine(string modDir)
    {
        // ★★★ v1.6.5【复核 #5】这里以前写死"本机 + 本机路径"，但悬浮面板 / 本只读行实际走的是
        //   `EventsOverlay.StatusFilePath()` = **主机缓存优先（60 秒内）→ 否则本机**。
        //   现在把两条真实路径都写清楚，避免用户以为看到的永远是本机旧文件。
        string remote = "", local = "";
        try
        {
            var dir = modDir ?? "";
            remote = System.IO.Path.Combine(dir, "当前生效活动.remote.txt");
            local = System.IO.Path.Combine(dir, "当前生效活动.txt");
        }
        catch { }
        return Locale.D(
            "数据来源：主机缓存优先（" + (remote.Length > 0 ? remote : "(mod 目录)\\当前生效活动.remote.txt") + "，60 秒内有效）\n"
            + "            → 拿不到时读本机 " + (local.Length > 0 ? local : "(mod 目录)\\当前生效活动.txt")
            + (FikaPresent ? "（联机时这是**本机**判定）" : "") + "\n",
            "Source: host cache first (" + (remote.Length > 0 ? remote : "(mod dir)\\当前生效活动.remote.txt") + ", valid for 60 s)\n"
            + "            → otherwise read the local " + (local.Length > 0 ? local : "(mod dir)\\当前生效活动.txt")
            + (FikaPresent ? " (**local** decision in co-op)" : "") + "\n");
    }

    /// <summary>
    /// ★★★ v0.1.7【用户裁决：**联机时就不锁撤离**】**纯判据（可单测）**：
    /// 只有**明确读到"不是单机"**才判联机；读不到任何信号 ⇒ **按单机处理**（保住单机功能）。
    /// </summary>
    internal static bool CoopFromSignals(bool? isSinglePlayer, bool coopHandlerFound)
        => isSinglePlayer.HasValue ? !isSinglePlayer.Value : coopHandlerFound;

    private static bool _coopVal;
    private static float _coopAt;
    private static System.Reflection.PropertyInfo _coopProp;
    private static System.Reflection.MethodInfo _coopTry;
    private static bool _coopTypesResolved;

    /// <summary>
    /// ★ v0.1.7【联机判定】**是否真的在联机局里**（装了 FIKA ≠ 联机 ⇒ 必须"在对局里"才算）。
    /// 两层信号（FIKA 2.4.3 的公开静态成员，**反射只读、不改 FIKA**）：
    ///   ① `FikaBackendUtils.IsSinglePlayer`（**权威**：false ⇒ 在联机局）—— `Fika.Core.Main.Utils.FikaBackendUtils`
    ///   ② `CoopHandler.TryGetCoopHandler(out …)`（当前局里存在联机组件 ⇒ 联机局）—— `Fika.Core.Main.Components.CoopHandler`
    /// ⚠ 没装 FIKA ⇒ 直接 false（**单机行为零变化**）；两个信号都读不到 ⇒ 也按单机（保持原行为）。
    /// ⚠ 2 秒缓存（联机状态只在局边界变）⇒ 每帧调用也几乎零成本。
    /// </summary>
    internal static bool InCoopGame()
    {
        try
        {
            if (!FikaPresent) return false;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _coopAt < 2f) return _coopVal;
            _coopAt = now;
            _coopVal = DetectCoopNow();
            return _coopVal;
        }
        catch { return false; }
    }

    private static bool DetectCoopNow()
    {
        bool? single = null;
        try
        {
            if (!_coopTypesResolved)
            {
                _coopTypesResolved = true;
                var t1 = FindTypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
                _coopProp = t1?.GetProperty("IsSinglePlayer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var t2 = FindTypeByName("Fika.Core.Main.Components.CoopHandler");
                _coopTry = t2?.GetMethod("TryGetCoopHandler", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
            if (_coopProp != null && _coopProp.PropertyType == typeof(bool))
                single = (bool)_coopProp.GetValue(null);
        }
        catch { }
        bool handler = false;
        try
        {
            if (_coopTry != null)
            {
                var args = new object[] { null };
                handler = (bool)_coopTry.Invoke(null, args);
            }
        }
        catch { }
        return CoopFromSignals(single, handler);
    }

    private static Type FindTypeByName(string full)
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType(full, false); if (t != null) return t; } catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>F12 说明里的"联机时锁会怎样"（**始终显示**，让玩家事先知道规则）。</summary>
    internal static string CoopLockNoteText()
        => Locale.D("联机（FIKA）时：「★仪式开始后只留 1 个撤离点」与「★进图即锁撤离点」这两个**撤离点锁定会自动关闭**（本机不再锁任何撤离点）。\n"
         + "原因：撤离点是**每台电脑各自的状态**，联机下锁了容易出现「你只剩 1 个、队友还有好几个」的不一致。\n",
            "In co-op (FIKA): both extract locks — 「★keep 1 exit after the ritual starts」 and 「★lock extracts on raid start」 — are **switched off automatically** (this machine no longer locks any extract).\n"
         + "Reason: extracts are **per-machine state**; locking in co-op easily produces inconsistencies like 「you have 1 left, your teammate still has several」.\n");

    /// <summary>探测 FIKA（只读 Chainloader，异常全吞）。</summary>
    private void DetectFika()
    {
        try
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.fika.core", out var pi)) return;
            FikaPresent = true;
            try { FikaVersion = pi?.Metadata?.Version?.ToString() ?? ""; } catch { }
            Log?.LogInfo($"[EventRotator.Client] 检测到 FIKA {FikaVersion}（com.fika.core）：本 mod 的**活动效果**由服务端决定、联机共享；"
                       + "而撤离点锁定 / 未知毒素 / 开局随机负面状态是**每台电脑各自判定**（本机生效）。单机游玩行为完全不变。");
        }
        catch { }
    }

    /// <summary>★ v0.1.6 组合族索引：family 名 → 该族全部成员（在 BuildEventToggles 里一次性建好，BuildDesc 只读）。</summary>
    private static readonly Dictionary<string, List<EventInfo>> _familyIndex = new Dictionary<string, List<EventInfo>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★★★ v0.1.6【F12 清理陈旧活动键 · ① 找出陈旧键】纯函数 · 可单测。
    /// 输入 = `ParseRawCfg` 解析出的原始 cfg + 当前活动 id 集合；
    /// 输出 = 每条 `string[3] { 分区, 键, id }`，条件是**键名以 `事件: ` 开头**且 **id 不在当前活动表里**。
    /// ⚠ **只认 `事件: ` 前缀** ⇒ 用户的其它设置（面板热键、季节模式、关闭所有活动…）**绝不会**被列进来。
    /// </summary>
    internal static List<string[]> CollectStaleEventKeys(Dictionary<string, Dictionary<string, string>> rawCfg, HashSet<string> liveIds)
    {
        var res = new List<string[]>();
        try
        {
            if (rawCfg == null || liveIds == null) return res;
            foreach (var sec in rawCfg)
            {
                if (sec.Value == null) continue;
                foreach (var kv in sec.Value)
                {
                    var key = kv.Key ?? "";
                    if (!key.StartsWith("事件: ", StringComparison.Ordinal)) continue;   // ★ 只认活动开关
                    var id = key.Substring("事件: ".Length).Trim();
                    if (id.Length == 0) continue;
                    if (liveIds.Contains(id)) continue;                                  // 活动还在 ⇒ 不动
                    res.Add(new[] { sec.Key ?? "", key, id });
                }
            }
        }
        catch { }
        return res;
    }

    /// <summary>
    /// ★★★ v0.1.6【F12 清理陈旧活动键 · ② 真正删掉】
    /// <para>**BepInEx 的移除 API（真实 BepInEx.dll 里核过）**：
    /// <c>public bool Remove(ConfigDefinition key)</c> —— 构造参数 <c>ConfigDefinition(string section, string key)</c>；
    /// 另有 <c>Clear()</c> / <c>Save()</c> / <c>ICollection Keys</c> / <c>ConfigDefinitions</c>。
    /// ⚠ <c>OrphanedEntries</c> 是**非公开**的（只有私有 backing field <c>&lt;OrphanedEntries&gt;k__BackingField</c>），**公开 API 拿不到**。</para>
    /// <para>所以做法是：**先 `Config.Bind(原分区, 原键, …)`** ⇒ BepInEx 会把该"孤儿条目"**收养**成本次会话的正常条目
    /// （孤儿随之从孤儿表移出）⇒ 再 `Config.Remove(def)` 删除 ⇒ `Config.Save()` 落盘。**全程只用公开 API、无需反射。**</para>
    /// </summary>
    private int CleanStaleEventKeys(List<EventInfo> list, Dictionary<string, Dictionary<string, string>> rawCfg)
    {
        try
        {
            var live = new HashSet<string>(
                list.Where(x => x != null && !string.IsNullOrEmpty(x.Id)).Select(x => x.Id),
                StringComparer.OrdinalIgnoreCase);
            var stale = CollectStaleEventKeys(rawCfg, live);
            if (stale.Count == 0) return 0;

            var removed = new List<string>();
            foreach (var row in stale)
            {
                var sec = row[0]; var key = row[1]; var id = row[2];
                try
                {
                    Config.Bind(sec, key, false, Locale.D("（陈旧键：对应活动已从 events.json 删除，本次启动已清理）",
                    "（Stale key: the matching event was removed from events.json and has been cleaned up on this start）"));
                    if (Config.Remove(new ConfigDefinition(sec, key))) removed.Add(id);
                    else Log.LogWarning($"[F12清理] Remove('{key}') 返回 false ⇒ 该键可能已不存在");
                }
                catch (Exception ex) { Log.LogWarning($"[F12清理] 移除 '{key}' 失败（跳过该键，不影响其它）: {ex.Message}"); }
            }
            if (removed.Count > 0)
            {
                try { Config.Save(); } catch { }
                Log.LogInfo($"[F12清理] 移除 {removed.Count} 个已删活动的陈旧键：{string.Join("、", removed)}");
            }
            return removed.Count;
        }
        catch (Exception ex) { Log.LogWarning("[F12清理] 清理陈旧活动键失败（不影响游戏）: " + ex.Message); return 0; }
    }


    /// <summary>
    /// ★★★ v0.1.6【修法 1 · 仪式锁活动门控的原料】活动 id → `seasonalEvent`（只放有值的）。
    /// 在 <c>BuildEventToggles</c> 里一次性建好；<c>EventsOverlay</c> 每帧用它判断
    /// 「当前生效的活动里有没有万圣节季节活动」⇒ 决定仪式锁要不要动作。
    /// </summary>
    internal static readonly Dictionary<string, string> SeasonalEventById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v0.1.6【官方组合】逐行对齐服务端 <c>EventEngine.cs:524 ExpandFamilyCombos</c>：
    /// 以 <paramref name="seed"/> 为种子时，<paramref name="cand"/> 是否会被**连带展开**进本局 ——
    /// 条件 = 同一个 family 且（familyYear 相同 或 cand 是 familyGeneric 通用成员）。
    /// ⚠ 这里必须与服务端**同一条判据**，否则 F12 说的"会连带谁"就是错的。
    /// </summary>
    private static bool SameCombo(EventInfo seed, EventInfo cand)
    {
        if (seed == null || cand == null) return false;
        if (string.IsNullOrEmpty(seed.Family)) return false;
        if (!string.Equals(seed.Family, cand.Family, StringComparison.OrdinalIgnoreCase)) return false;
        bool sameYear = seed.FamilyYear.HasValue && cand.FamilyYear.HasValue && cand.FamilyYear.Value == seed.FamilyYear.Value;
        return sameYear || cand.FamilyGeneric == true;
    }

    /// <summary>三层分类的 F12 区名（★ 按用户要求合并成 3 类）。</summary>
    private static string Category(EventInfo e)
    {
        var c = (e.Category ?? "").Trim().ToLowerInvariant();
        // ① 季节活动
        if (c == "seasonal") return "① 季节活动 | Seasonal";        // ② 限时活动（带任务）
        if (c == "event_task" || c == "limited") return "② 限时活动(带任务) | Limited";
        // ★★ v0.1.5 新增：AI 活动（首领 / 邪教 / 劫掠者 / SCAV 相关）—— 用户要求优先测这类
        if (c == "event_ai") return "★ AI 活动(首领/邪教/劫掠者) | AI events";
        // ③ 各种限时小活动（无任务可复现的 + 单纯改动，合并成一类）
        if (c == "event_small" || c == "noquest" || c == "tweak") return "③ 限时小活动 | Minor events";
        // ④ 奖励类（不改敌人、只对玩家有利，可多个叠加）
        if (c == "reward") return "④ 奖励类活动 | Rewards";
        // ★★★ v0.1.6【用户裁决】`standalone` = **单独活动**（不属于奖励池、也不跟别的活动叠）——
        //   用户原话：「这个活动不是奖励活动,算单独活动,而且这个活动只在海关地图生效」。
        //   目前使用者：`june2022_toxin`（June 2022 Day7 致命毒素）。**放最后**（新增第 6 个分区）。
        if (c == "standalone") return "⑤ 单独活动 | Standalone";
        // 缺省：有任务 → 限时；否则算小活动
        return (e.EnableQuests != null && e.EnableQuests.Count > 0)
            ? "② 限时活动(带任务) | Limited"
            : "③ 限时小活动 | Minor events";
    }

    /// <summary>
    /// ★★★ v0.1.4 重写 F12 的活动说明（用户反馈"乱糟糟的"）。
    /// <para>旧版问题：① 三份内容重复（desc / note / 客户端拼的"实际生效"）② 满满技术黑话
    /// （`bigmap:bossKnight=35%`、`海关←[bossGluhar]→0`、内部字段名）③ 与 desc 打架。</para>
    /// <para>新版结构：**活动内容（干净一句话）→ 实际效果（按类别、中文名）→ 任务 → 日期窗 → 怎么用**。
    /// 技术实现细节（落点/字段名/版本号）已从 events.json 的 <c>note</c> 挪到 <c>devNote</c>，
    /// <b>不再显示在 F12 里</b>，排查时直接看 events.json 即可。</para>
    /// </summary>
    /// <summary>★ v1.0.1【F12 性能】描述截断：保留前 n 字，超出加 `…`（只变短，不改语义）。</summary>
    private static string Cut(string s, int n)
        => string.IsNullOrEmpty(s) || s.Length <= n ? (s ?? "") : s.Substring(0, n) + "…";

    /// <summary>
    /// ★ v1.3.4【英文 tooltip】F12 里每个活动勾选项的说明。英文模式下走这一份：
    /// 只给**事实字段**（分类/ID/日期窗/任务数/首领/季节），不翻译中文散文。
    /// </summary>
    private static string BuildDescEn(EventInfo e)
    {
        var sb = new System.Text.StringBuilder();
        string cat = (e.Category ?? "").Trim().ToLowerInvariant() switch
        {
            "seasonal" => "SEASONAL EVENT (auto by season)",
            "event_task" => "LIMITED EVENT (with quests)",
            "limited" => "LIMITED EVENT (with quests)",
            "event_ai" => "AI EVENT (bosses / cultists / rogues)",
            "event_small" => "MINOR EVENT (enemy / fun)",
            "noquest" => "MINOR EVENT",
            "tweak" => "MINOR EVENT",
            "reward" => "REWARD EVENT",
            "standalone" => "STANDALONE EVENT",
            _ => (e.EnableQuests != null && e.EnableQuests.Count > 0) ? "LIMITED EVENT (with quests)" : "MINOR EVENT",
        };
        sb.AppendLine(cat);
        sb.AppendLine();
        sb.AppendLine("Name: " + (string.IsNullOrEmpty(e.NameEn) ? e.NameZh : e.NameEn));
        sb.AppendLine("ID: " + e.Id);
        if (e.StartMonth > 0)
            sb.AppendLine($"Date window: {e.StartMonth}/{e.StartDay} - {e.EndMonth}/{e.EndDay}");
        if (e.EnableQuests != null && e.EnableQuests.Count > 0)
            sb.AppendLine($"Quests: {e.EnableQuests.Count}");
        int bossMaps = e.BossSpawns != null ? e.BossSpawns.Count : 0;
        int bossChanceMaps = e.BossChance != null ? e.BossChance.Count : 0;
        if (bossMaps > 0 || bossChanceMaps > 0)
            sb.AppendLine($"Boss spawns on {Math.Max(bossMaps, bossChanceMaps)} map(s)");
        if (e.LooseLootMultiplier.HasValue && Math.Abs(e.LooseLootMultiplier.Value - 1.0) > 0.01)
            sb.AppendLine($"Loose loot x{e.LooseLootMultiplier.Value:0.##}");
        if (!string.IsNullOrEmpty(e.Family))
            sb.AppendLine($"Family: {e.Family}" + (e.FamilyYear.HasValue ? $" / {e.FamilyYear}" : "") +
                          (e.FamilyGeneric == true ? " (generic, runs every year)" : ""));
        if (!string.IsNullOrEmpty(e.SeasonalEvent))
            sb.AppendLine("Seasonal: " + e.SeasonalEvent);
        // ★ v1.3.6：本体没有配套任务时也要在英文里提醒（原先只有中文 BuildDesc 有这段）
        if (e.TaskMissing)
            sb.AppendLine("NOTE: no matching quests exist in vanilla for this event (enemies / loot / items only)."
                          + (string.IsNullOrEmpty(e.TaskNote) ? "" : " " + e.TaskNote.TrimStart('⚠', ' ')));
        return sb.ToString();
    }

    private static string BuildDesc(EventInfo e)
    {
        if (Locale.En) return BuildDescEn(e);
        var sb = new System.Text.StringBuilder();

        string cat = (e.Category ?? "").Trim().ToLowerInvariant() switch
        {
            // ★★★ v0.1.6【与 Category() 逐条对齐 · 分类审计 ③-6 的修复】
            //   以前这里缺 `standalone` / `limited` / `noquest` / `tweak` 四支 ⇒ `standalone` 落到
            //   `_ => "③ 限时小活动"` ⇒ **F12 分区⑤ / 描述③ 自相矛盾**（用户看到的那个 bug）。
            //   现在两张表的「编号 + 名称」**完全一致**，缺省也照 `Category()` 的规则（有任务 → ②，否则 ③）。
            "seasonal" => "① 季节活动 —— 跟着季节自动开",
            "event_task" => "② 限时活动(带任务)",
            "limited" => "② 限时活动(带任务)",
            "event_ai" => "★ AI 活动（首领 / 邪教 / 劫掠者）",
            "event_small" => "③ 限时小活动（改敌人 / 整活）",
            "noquest" => "③ 限时小活动",
            "tweak" => "③ 限时小活动",
            "reward" => "④ 奖励类活动",
            "standalone" => "⑤ 单独活动",
            _ => (e.EnableQuests != null && e.EnableQuests.Count > 0) ? "② 限时活动(带任务)" : "③ 限时小活动",
        };

        // ── ① 活动内容：desc 那一句，人话 ──
        sb.AppendLine("【活动内容】");
        sb.AppendLine("   " + Cut(string.IsNullOrEmpty(e.Desc) ? "(暂无描述)" : e.Desc, 100));   // ★ v1.0.1【F12 性能】
        sb.AppendLine();
        sb.AppendLine("【分类】" + cat);

        // ── ★★ C1 修复：taskMissing / taskNote 以前**从未被读取** ⇒ 玩家永远看不到"本体无此活动任务定义"。
        //   数据现状（events.json 84 条）：29 条 taskMissing=true；其中 28 条的 taskNote 自带 "⚠ " 前缀，
        //   1 条（free_lab_access）没有 taskNote ⇒ 用兜底文案。先剥掉自带前缀再统一加，避免显示成 "⚠ ⚠"。
        if (e.TaskMissing)
        {
            var warn = Cut(string.IsNullOrEmpty(e.TaskNote) ? "这个活动在本体里没有配套任务（只有敌人 / 爆率 / 物品等效果）" : e.TaskNote.TrimStart('⚠', ' '), 100);   // ★ v1.0.1【F12 性能】
            sb.AppendLine();
            sb.AppendLine("⚠ " + warn);
        }

        // ── ★★★ v0.1.6【官方组合（family）· 用户要求"不要遗漏组合活动的开启关闭，手动开启关闭也遵循这个原则"】──
        //   服务端 EventEngine.cs:524 ExpandFamilyCombos：以有 family 的已选活动为种子，
        //   补上**同族**且（**同年份** 或 **通用成员**）的其它活动 ⇒ 手动强制与自动轮换**都会连带**。
        // ★ v1.0.1【F12 性能】「官方组合」明细（本族成员 / 连带展开 / 取消后果）已移到附录，这里只留一行
        if (!string.IsNullOrEmpty(e.Family))
        {
            sb.AppendLine();
            sb.AppendLine($"【官方组合】{e.Family} 族（会连带开启/关闭同族成员；明细见附录）");
        }

        // ── ★★ v0.1.5 新增：官方"组合表"维度自证（字段存在才显示，描述自动补全）──
        var off = new List<string>();
        if (e.PumpkinScavBackpack == true) off.Add("南瓜 —— SCAV(assault) 背包加入南瓜容器（官方 AddPumpkinsToScavBackpacks）");
        if (e.RenameBitcoin == true) off.Add("比特币改名 —— Physical SPT Coin / 0.2SPT（官方 RenameBitcoin）");
        if (e.HalloweenStimBuff == true) off.Add("兴奋剂 Buff = Buffs_Halloween（官方 ApplyHalloweenEvent）");
        if (e.ZryachiyMeleeChance == true) off.Add("Zryachiy 近战率 —— bosszryachiy 的 Scabbard → 100（官方 AdjustZryachiyMeleeChance）");
        if (e.OfficialGear != null && e.OfficialGear.Count > 0) off.Add("官方 bot 装备 —— eventGear." + string.Join("+", e.OfficialGear) + "（官方 AddEventGearToBots 同源数据）");
        if (e.OfficialLoot != null && e.OfficialLoot.Count > 0) off.Add("官方 bot 掉落 —— eventLoot." + string.Join("+", e.OfficialLoot) + "（官方 AddEventLootToBots 同源数据）");
        if (e.BotAppearance != null && e.BotAppearance.Count > 0) off.Add("bot 外观 —— botAppearanceChanges." + string.Join("+", e.BotAppearance) + "（官方 AdjustBotAppearanceValues）");
        if (e.GifterOfficial == true) off.Add("圣诞老人 —— 按官方 gifterSettings 逐图概率(85/50/75)+zones 刷出（官方 AddGifterBotToMaps）");
        if (e.ChristmasLootBoost.HasValue) off.Add("圣诞爆率 —— 模板含 christmas 的刷点 p→p+(1-p)*" + e.ChristmasLootBoost.Value + "（官方 christmasLootBoostAmount）");
        if (e.BtrTarcolaSkin == true) off.Add("BTR 皮肤 —— Woods/TarkovStreets 换 Tarcola（官方 ChangeBtrToTarColaSkin）");
        if (e.RunddansActive == true) off.Add("圆环舞真开关 —— RunddansSettings.Active/ActivePVE=true（官方 EnableRunnansEvent）");
        if (e.PlayerGift != null && e.PlayerGift.Count > 0) off.Add("季节礼物 —— 给玩家发 " + string.Join("、", e.PlayerGift) + "（官方 GivePlayerSeasonalGifts/GiveGift）");
        if (e.NativeChristmas == true) off.Add("原生圣诞 —— EventType.Christmas + ActivePatrolGeneratorEvent + 礼物清单 + Khorovod");
        if (e.LabsFreeAccess == true) off.Add("实验室免门卡（官方 RemoveEntryRequirement）");
        // ★★★ v2.6.17【F12 同步】halloween_2023 的诅咒标签（官方 2023 仪式内容）
        if (e.TaggedAndCursed == true) off.Add("诅咒标签 —— 全图 SCAV 波次换成疯狂 SCAV（cursedAssault，集体冲锋死追；官方 2023 仪式内容）");
        // ★ v1.0.1【F12 性能】「官方内容落地清单」输出已移到附录（off 仍填充，供下方"实际效果"计数用）

        // ★ v1.0.1【F12 性能】「官方痕迹」输出已移到附录（判定逻辑见 audit/活动说明-附录.md）

        // ── ② 实际效果：按类别分组，全部中文名 ──
        var boss = new List<string>(); var enemy = new List<string>(); var loot = new List<string>();
        var trade = new List<string>(); var other = new List<string>();

        if (e.BossSpawns != null && e.BossSpawns.Count > 0)
            foreach (var kv in e.BossSpawns)
            {
                bool all = kv.Value != null && kv.Value.Contains("*");
                var names = kv.Value == null ? new List<string>() : kv.Value.Where(x => x != "*").Select(BossName).ToList();
                if (all) boss.Add($"{MapName(kv.Key)}：全部首领 100%");
                else if (names.Count > 0) boss.Add($"{MapName(kv.Key)}：{string.Join("、", names)} 100%");
            }
        if (e.BossChance != null && e.BossChance.Count > 0)
            foreach (var kv in e.BossChance)
                foreach (var kv2 in kv.Value)
                    boss.Add($"{MapName(kv.Key)}：{(kv2.Key == "*" ? "该图全部首领" : BossName(kv2.Key))} → {kv2.Value}%");
        if (e.ZeroBossSpawns != null && e.ZeroBossSpawns.Count > 0)
            foreach (var kv in e.ZeroBossSpawns)
                enemy.Add($"{MapName(kv.Key)}：{string.Join("、", kv.Value.Select(BossName))} 不再刷出（老家撤走）");
        if (e.ForceHostility != null && e.ForceHostility.Count > 0) enemy.Add("活动敌种互相开打，也会打你");
        if (e.ForceAbpsSpawns != null && e.ForceAbpsSpawns.Count > 0) enemy.Add("敌种刷新由刷怪系统接管（避免冲突）");
        if (e.NightOnly) enemy.Add("只在夜晚刷出");
        if (e.BotAddItems != null && e.BotAddItems.Count > 0) enemy.Add("SCAV 出生自带更好装备");
        if (e.BotAddMods != null && e.BotAddMods.Count > 0) enemy.Add("SCAV 武器会装更好配件");
        if (e.DrumMagazineBoost != null && e.DrumMagazineBoost.Count > 0) enemy.Add("SCAV 更容易拿大弹鼓");
        if (e.BotLootAdd_ != null && e.BotLootAdd_.Count > 0) enemy.Add("SCAV 背包里会带活动物品");
        if (e.BotPresetBatch != null && e.BotPresetBatch.Count > 0) enemy.Add("敌人预设批次调整");
        if (e.BotMaxCap.HasValue) enemy.Add("每图敌人数量上限调整");
        // ★★★ v2.6.17【F12 同步】敌种装备升级（更好的枪/弹药/插板；大弹鼓、下挂榴弹、火箭筒都走这里）
        //   ⚠ 这条是「三条 SCAV 装备活动」回归后的**唯一**可见说明（drum_mag_scavs / underbarrel_gl_scavs /
        //     rpg_scavs / factory_cleanup_crew）—— 旧写法（botAddItems/botAddMods）在 PBS 接管后已经死了，
        //     现在走 botGearUpgrade ⇒ F12 必须显示这条，否则玩家会以为活动还是坏的。
        if (e.BotGearUpgrade != null && e.BotGearUpgrade.Count > 0)
        {
            var roles = e.BotGearUpgrade.Keys.Where(k => !string.IsNullOrEmpty(k)).Select(BossName).ToList();
            var bits = new List<string>();
            if (e.BotGearUpgrade.Values.Any(v => v != null && v.BetterAmmo)) bits.Add("更好的弹药");
            if (e.BotGearUpgrade.Values.Any(v => v != null && v.BetterPlates)) bits.Add("更好的插板");
            if (e.BotGearUpgrade.Values.Any(v => v != null && !string.IsNullOrEmpty(v.WeaponTpl))) bits.Add("换用指定武器");
            if (e.BotGearUpgrade.Values.Any(v => v != null && !string.IsNullOrEmpty(v.MagazineTpl))) bits.Add("指定弹匣");
            if (e.BotGearUpgrade.Values.Any(v => v != null && v.BiggestMagazine)) bits.Add("大弹鼓");
            if (e.BotGearUpgrade.Values.Any(v => v != null && !string.IsNullOrEmpty(v.AddLauncherTpl))) bits.Add("下挂榴弹");
            if (e.BotGearUpgrade.Values.Any(v => v != null && v.ExtraAmmoTpls != null && v.ExtraAmmoTpls.Count > 0)) bits.Add("追加弹药");
            enemy.Add("敌种装备升级 —— " + (roles.Count > 0 ? string.Join("、", roles) : (e.BotGearUpgrade.Count + " 个角色"))
                      + (bits.Count > 0 ? "：" + string.Join("、", bits) : "（更好的枪 / 弹药 / 插板）"));
        }
        // ★★★ v2.6.17【F12 同步】随从数量（值支持区间 ⇒ 显示成 0~2）
        if (e.EscortCount != null && e.EscortCount.Count > 0)
            foreach (var kv in e.EscortCount)
                foreach (var kv2 in kv.Value ?? new System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JToken>())
                    boss.Add($"{MapName(kv.Key)}：{BossName(kv2.Key)} 随从数量 → {EscortText(kv2.Value)}");

        if (e.LooseLootMultiplier.HasValue && e.LooseLootMultiplier.Value > 0) loot.Add($"地面散落物资 ×{e.LooseLootMultiplier.Value}");
        if (e.StaticLootMultiplier.HasValue && e.StaticLootMultiplier.Value > 0) loot.Add($"容器物资 ×{e.StaticLootMultiplier.Value}");
        if (e.LootMaps != null && e.LootMaps.Count > 0) loot.Add($"（爆率只作用于：{string.Join("、", e.LootMaps.Select(MapName))}）");
        if (e.AirdropFrequencyMultiplier.HasValue) loot.Add($"空投频率 ×{e.AirdropFrequencyMultiplier.Value}（单人也触发）");
        if (!string.IsNullOrEmpty(e.AirdropForceType)) loot.Add($"空投强制为「{AirdropTypeName(e.AirdropForceType)}」箱");
        if (e.AirdropWeight != null && e.AirdropWeight.Count > 0)
            loot.Add("空投箱内容权重：" + string.Join("、", e.AirdropWeight.Select(kv => $"{AirdropTypeName(kv.Key)}={kv.Value}")));
        if (e.AirdropAddLoot != null && e.AirdropAddLoot.Count > 0) loot.Add("空投补上平时拿不到的物品");
        // ★★★ v2.6.17【F12 同步】指定物品刷新率（按图按 tpl 调松散战利品权重）
        if (e.LooseLootWeightSet != null && e.LooseLootWeightSet.Count > 0)
        {
            int n = e.LooseLootWeightSet.Values.Sum(v => v?.Count ?? 0);
            var maps = e.LooseLootWeightSet.Keys.Where(k => !string.IsNullOrEmpty(k)).Select(MapName).ToList();
            double? sampleP = e.LooseLootWeightSet.Values.SelectMany(v => v ?? new System.Collections.Generic.Dictionary<string, LootWeightDto>())
                                     .Select(o => o.Value?.ProbabilityMult).FirstOrDefault(x => x.HasValue);
            double? sampleW = e.LooseLootWeightSet.Values.SelectMany(v => v ?? new System.Collections.Generic.Dictionary<string, LootWeightDto>())
                                     .Select(o => o.Value?.WeightMult).FirstOrDefault(x => x.HasValue);
            loot.Add($"指定物品刷新率 —— {n} 个物品" + (maps.Count > 0 ? "（" + string.Join("、", maps) + "）" : "")
                     + (sampleP.HasValue ? $"，概率 ×{sampleP.Value:0.##}" : "")
                     + (sampleW.HasValue ? $"，权重 ×{sampleW.Value:0.##}" : ""));
        }

        if (e.UnlockTraders.HasValue && e.UnlockTraders.Value) trade.Add("所有商人忠诚等级解锁到最高");
        if (e.TraderPriceMultiplier.HasValue && Math.Abs(e.TraderPriceMultiplier.Value - 1.0) > 0.001)
        {
            var v = e.TraderPriceMultiplier.Value;
            trade.Add(v > 1 ? $"商人价格 ×{v}" : $"商人价格打 {Math.Round(v * 100)} 折");
        }
        if (e.TraderUpdateSeconds.HasValue) trade.Add("商人补货更快");
        if (e.TraderPurchasesFir.HasValue && e.TraderPurchasesFir.Value) trade.Add("从商人买的东西算「战局中获得」");
        if (e.FleaMinLevel.HasValue) trade.Add($"跳蚤等级门槛 → {e.FleaMinLevel.Value}");
        if (e.FleaTaxMultiplier.HasValue) trade.Add($"跳蚤挂单手续费 ×{e.FleaTaxMultiplier.Value}");
        if (e.FleaZeroListingFee.HasValue && e.FleaZeroListingFee.Value) trade.Add("跳蚤挂单不收手续费");
        // ★★★ v2.6.17【F12 同步】商人易物（两类字段：assort 追加 / barter 追加）
        //   ⚠ `traderBarterAdd` 以前是**静默失效**（0 条注入、玩家看不到任何效果），v2.6.x 已修好 ⇒ 文案必须写明，
        //     否则玩家会以为它还是坏的（项目规矩 R1 的"行为语义变了必须显式写出来"）。
        if (e.TraderAssortAdd != null && e.TraderAssortAdd.Count > 0)
            trade.Add("商人易物 —— " + string.Join("、", e.TraderAssortAdd.Where(kv => kv.Value != null).Select(kv => $"{TraderName(kv.Key)} {kv.Value.Count} 条"))
                      + "以物易物配方（含忠诚等级 / 单次补货上限）");
        if (e.TraderBarterAdd != null && e.TraderBarterAdd.Count > 0)
            trade.Add("商人易物 —— " + string.Join("、", e.TraderBarterAdd.Where(kv => kv.Value != null).Select(kv => $"{TraderName(kv.Key)} {kv.Value.Count} 条"))
                      + "以物易物配方");

        if (!string.IsNullOrEmpty(e.SeasonalEvent)) other.Add($"开启本体「{SeasonName(e.SeasonalEvent)}」季节（含专属内容）");
        if (!string.IsNullOrEmpty(e.ForceWeatherSeason)) other.Add($"天气锁定为 {SeasonName(e.ForceWeatherSeason)}");
        if (e.LabsFreeAccess.HasValue && e.LabsFreeAccess.Value) other.Add("实验室与迷宫不用门卡直接进");
        if (e.TransitsActive.HasValue && e.TransitsActive.Value) other.Add("全图转场口全部打开（可连续跨图）");
        if (e.Globals != null)
        {
            if (e.Globals.TryGetValue("damageMultiplier", out var dm)) other.Add($"所有子弹伤害 ×{dm}（⚠ 敌我通用）");
            if (e.Globals.TryGetValue("hydrationMultiplier", out var hm)) other.Add($"口渴与体力消耗 ×{hm}");
            if (e.Globals.TryGetValue("foodRestoreMultiplier", out var fm)) other.Add($"食物饮水补充量 ×{fm}");
            if (e.Globals.TryGetValue("skillMultiplier", out var sm)) other.Add($"技能成长 ×{sm}");
            if (e.Globals.TryGetValue("expMultiplier", out var em)) other.Add($"经验获取 ×{em}");
            // ★★★ v0.1.6【F12 同步 · 规矩 R1】june2022_toxin 的①「体力无限」以前**F12 里根本看不到**
            if (e.Globals.TryGetValue("staminaCapacityMultiplier", out var scm)) other.Add($"体力上限 ×{scm}（≈无限体力，官方「infinite Stamina」）");
        }
        // ★★★ v0.1.6【F12 同步 · 规矩 R1】致命毒素（june2022_toxin）—— 条款②③ 已落地，必须写明，
        //   否则玩家看了旧的 desc（"只实现第①条"）会以为还是没做。
        if (string.Equals(e.Id, "june2022_toxin", StringComparison.OrdinalIgnoreCase))
            other.Add("致命毒素 —— ① **进图即锁撤离点**：只留「你出生点对应的那一个」；"
                    + "② **进图即感染「未知毒素」+ 8 分钟倒计时**（到点暴毙），同时给「超人」增益（回血 +50 / 能量 +50 / 活力 +20，**无副作用**）；"
                    + "打到 **xTG-12 解毒剂**（空投箱 / SCAV 口袋 / 首领身上）⇒ 毒素解除 + **倒计时重置 8 分钟**。"
                    + "开关见本分区「★进图即锁撤离点 · 仅「致命毒素」生效时」与「★「致命毒素」感染与倒计时」（默认都开）");
        // ★★★ v0.1.6【F12 同步 · 规矩 R1】Halloween 2022 的「开局随机负面状态」—— 客户端插件已落地，必须写明
        //   （旧 desc 只写"本 mod 实现枪械卡壳与补给不再补充"，玩家看不到这条）。
        if (string.Equals(e.Id, "halloween_2022", StringComparison.OrdinalIgnoreCase))
            other.Add("开局随机负面状态 —— 进图后**随机**给你一个纯负面状态（四选一）："
                    + "出血 / 脑震荡+骨折 / 脑震荡+骨折+疼痛 / 冻伤。"
                    + "开关见本分区「★开局随机负面状态 (Halloween 2022: random raid-start debuff)」（默认开）；"
                    + "官方只说「随机」、没点名是哪一个 ⇒ 这里取本体现成的纯负面状态，四选一全部是纯负面、没有正面副作用");
        if (e.Items != null && e.Items.Count > 0)
            other.Add("物品数值调整：" + string.Join("、", e.Items.Keys.Select(ItemSelName)));
        // ★★★ v2.6.17【F12 同步 · 规矩 R1】这里的旧注释（"藏身处加速不显示，因为写了不生效、对应活动已删除"）
        //   已经**过期**：v2.6.12 把落点修成**直接改 HideoutTable**（`HideoutTraderDimension.cs:124-152` 写
        //   `Areas[].Stages[].ConstructionTime` / 制作时间），而 `instant_hideout_build` / `beer_upgrades`
        //   两个活动现在都真声明 `hideoutBuildSeconds: 0`（= 秒建）⇒ **必须显示回来**，否则玩家会以为没生效。
        if (e.HideoutBuildSeconds.HasValue) other.Add($"藏身处建造/升级时间 → {e.HideoutBuildSeconds.Value:0.##} 秒" + (e.HideoutBuildSeconds.Value <= 0 ? "（秒建）" : ""));
        if (e.HideoutCraftSeconds.HasValue) other.Add($"藏身处制作时间 → {e.HideoutCraftSeconds.Value:0.##} 秒" + (e.HideoutCraftSeconds.Value <= 0 ? "（秒做）" : ""));
        // ★★★ v2.6.17【F12 同步】藏身处追加升级需求（官方 Beer Upgrades）
        if (e.HideoutAddRequirements != null && e.HideoutAddRequirements.Count > 0)
        {
            int n = e.HideoutAddRequirements.Values.Sum(v => v?.Count ?? 0);
            var stages = e.HideoutAddRequirements.Values.SelectMany(v => v ?? new System.Collections.Generic.List<HideoutReqDto>())
                                    .Select(r => r.Stage).Where(s => s > 0).Distinct().OrderBy(s => s).ToList();
            other.Add($"藏身处追加升级需求 —— {e.HideoutAddRequirements.Count} 个区域 / {n} 条材料需求"
                      + (stages.Count > 0 ? $"（阶段 {string.Join("/", stages)}）" : "")
                      + "；官方 Beer Upgrades：升级改用 Pevko 淡啤酒");
        }
        // ★★★ v2.6.17【F12 同步】撤离点锁定 —— 语义变了，**必须显式写出来**（否则玩家以为活动坏了）
        //   ★ v0.1.6 更新（用户规矩 R1）：客户端插件**已经把这个功能做出来了**
        //     （F12「★仪式开始后只留 1 个撤离点」，见 RitualExitLock.cs）⇒ 这里不再说"需要客户端插件"。
        if (e.ExitKeepOnly != null && e.ExitKeepOnly.Count > 0)
            other.Add("撤离点锁定 —— 官方语义是「**杀了 Zryachiy、仪式开始之后**才只留 1 个撤离点"
                      + "（并把撤离时间 ×3）」。**打仪式之前撤离点完全正常**。"
                      + "仪式那一步由客户端插件在本地完成：见上方「★仪式开始后只留 1 个撤离点」（默认开）。"
                      + "⚠ 只开一边：本项保持默认（不写 @timing），把撤离点交给上面那个开关 —— 两边同时开会让撤离时间 ×3 叠加两次。");

        // ★★★ v1.0.1【F12 性能】⑦~⑪ 的逐条明细折叠成一行计数摘要（明细见附录）
        //   为什么：原来 5 段 Section 共 164 字/项 × 87 项 ≈ 1.4 万字，是 F12 文本量的最大单块。
        //   信息不丢：逐条明细已写入 mod 目录「活动说明-附录.md」。
        {
            var parts = new System.Collections.Generic.List<string>();
            if (boss.Count > 0) parts.Add("首领 " + boss.Count);
            if (enemy.Count > 0) parts.Add("敌种 " + enemy.Count);
            if (loot.Count > 0) parts.Add("爆率/物资 " + loot.Count);
            if (trade.Count > 0) parts.Add("商人/跳蚤 " + trade.Count);
            if (other.Count > 0) parts.Add("玩法/物品 " + other.Count);
            if (parts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【实际效果】" + string.Join(" / ", parts) + " 项改动（明细见附录）");
            }
        }

        if (e.EnableQuests != null && e.EnableQuests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"【活动任务 {e.EnableQuests.Count} 个】");
            sb.AppendLine("   开启活动后商人处会出现这些任务；活动关掉就消失（未完成的也做不完）。");
        }
        if (e.StartMonth > 0 && e.EndMonth > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"【官方日期窗】{e.StartMonth}/{e.StartDay} – {e.EndMonth}/{e.EndDay}");
            sb.AppendLine("   按现实日期自动开；手动勾选可无视。");
        }

        // ★ v1.0.1【F12 性能】「怎么用」原来 3 行 127 字，且**每项重复**（87 项 ≈ 1.1 万字）⇒ 压成一行
        sb.AppendLine();
        sb.AppendLine("【怎么用】勾选=强制开启；⚠ 任务/季节/模板类需重启服务端，刷怪/爆率/敌对性进图即生效。");
        // ★ v1.0.1【F12 性能】详细说明统一指向附录（F12 只留必要信息，避免 87 项长文本拖慢面板）
        sb.AppendLine();
        sb.AppendLine("【详细说明】官方组合明细 / 落地清单 / 官方痕迹 → 见 mod 目录「活动说明-附录.md」");
        return sb.ToString();
    }

    // ── 人话化的名字转换（避免 F12 里出现 bigmap / bossKnight 这种内部名）──
    private static readonly Dictionary<string, string> MapZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"] = "海关", ["woods"] = "森林", ["shoreline"] = "海岸线", ["interchange"] = "立交桥",
        ["lighthouse"] = "灯塔", ["rezervbase"] = "储备站", ["laboratory"] = "实验室", ["labyrinth"] = "迷宫",
        ["tarkovstreets"] = "街区", ["sandbox"] = "零号地", ["sandbox_high"] = "零号地高层",
        ["factory4_day"] = "工厂白天", ["factory4_night"] = "工厂夜晚", ["*"] = "全部地图",
    };
    /// <summary>
    /// ★ v0.1.4 首领名 → **保持本体英文原名**（用户要求：还是用原本英文）。
    /// 只做 id → 官方英文名的归一（去掉 `boss` 前缀做显示，保留大小写与社区熟悉的写法）。
    /// </summary>
    private static readonly Dictionary<string, string> BossZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bossbully"] = "Reshala",
        ["bosskilla"] = "Killa",
        ["bossgluhar"] = "Glukhar",
        ["bosssanitar"] = "Sanitar",
        ["bosskojaniy"] = "Shturman",
        ["bosspartisan"] = "Partisan",
        ["bosstagilla"] = "Tagilla",
        ["bosszryachiy"] = "Zryachiy",
        ["bossknight"] = "Knight",
        ["bossbigpipe"] = "Big Pipe",
        ["bossbirdeye"] = "Birdeye",
        ["bossboar"] = "Kaban",
        ["bosskolontay"] = "Kolontay",
        ["bossrogue"] = "Rogue",
        ["bosskillaagro"] = "Killa (Agro)",
        ["bosstagillaagro"] = "Tagilla (Agro)",
        ["tagillahelperagro"] = "Tagilla Helper",
        ["sectantpriest"] = "Cultist Priest",
        ["sectantpredvestnik"] = "Cultist Harbinger",
        ["sectantprizrak"] = "Cultist Ghost",
        ["sectantoni"] = "Cultist Oni",
        ["sectantpriestevent"] = "Cultist Priest",
        ["sectantpredvestnikevent"] = "Cultist Harbinger",
        ["sectantprizrakevent"] = "Cultist Ghost",
        ["sectantonievent"] = "Cultist Oni",
        ["arenafighterevent"] = "Arena Fighter",
        ["infectedassault"] = "Infected Assault",
        ["infectedpmc"] = "Infected PMC",
        ["infectedcivil"] = "Infected Civilian",
        ["infectedlaborant"] = "Infected Lab Assistant",
        ["infectedtagilla"] = "Infected Tagilla",
        ["gifter"] = "Santa (Gifter)",
        ["peacemaker"] = "Peacemaker",
        ["skier"] = "Skier's Squad",
        ["exusec"] = "Rogue (exUsec)",
        ["pmcbot"] = "Rogue (pmcBot)",
        ["followerbully"] = "Reshala's Guard",
        ["followergluharassault"] = "Glukhar's Assault",
        ["followergluharsecurity"] = "Glukhar's Security",
        ["followergluharscout"] = "Glukhar's Scout",
        ["followerkojaniy"] = "Shturman's Guard",
        ["followerzryachiy"] = "Zryachiy's Guard",
        ["followerbigpipe"] = "Big Pipe's Guard",
        ["followerbirdeye"] = "Birdeye's Guard",
        ["followersanitar"] = "Sanitar's Guard",
        ["followertagilla"] = "Tagilla's Guard",
        ["*"] = "All Bosses",
    };
    /// <summary>★ v2.6.17【F12 同步】商人（MongoId / 英文名 → 中文名），用于 `traderAssortAdd` / `traderBarterAdd` 的自证行。
    ///    id 取自本机 `SPT_Data/database/traders/*/base.json` 的目录名与 nickname（口径与游戏一致）。</summary>
    private static readonly Dictionary<string, string> TraderZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["54cb50c76803fa8b248b4571"] = "Prapor（大胡子）",
        ["54cb57776803fa99248b456e"] = "Therapist（治疗师）",
        ["579dc571d53a0658a154fbec"] = "Fence（黑市）",
        ["58330581ace78e27b8b10cee"] = "Skier（滑雪者）",
        ["5935c25fb3acc3127c3d8cd9"] = "Peacekeeper（维和者）",
        ["5a7c2eca46aef81a7ca2145d"] = "Mechanic（机械师）",
        ["5ac3b934156ae10c4430e83c"] = "Ragman（拾荒王）",
        ["5c0647fdd443bc2504c2d371"] = "Jaeger（猎人）",
        ["638f541a29ffd1183d187f57"] = "Lightkeeper（灯塔管理员）",
        ["Prapor"] = "Prapor（大胡子）", ["Therapist"] = "Therapist（治疗师）", ["Fence"] = "Fence（黑市）",
        ["Skier"] = "Skier（滑雪者）", ["Peacekeeper"] = "Peacekeeper（维和者）", ["Mechanic"] = "Mechanic（机械师）",
        ["Ragman"] = "Ragman（拾荒王）", ["Jaeger"] = "Jaeger（猎人）", ["Lightkeeper"] = "Lightkeeper（灯塔管理员）",
    };
    private static readonly Dictionary<string, string> SeasonZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Halloween"] = "万圣节", ["Christmas"] = "圣诞节", ["NewYears"] = "新年",
        ["AprilFools"] = "愚人节", ["Promo"] = "官方促销",
        ["SPRING"] = "春天", ["SUMMER"] = "夏天", ["AUTUMN"] = "秋天", ["WINTER"] = "冬天",
    };
    private static readonly Dictionary<string, string> ItemSelZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shotgunAmmo"] = "霰弹", ["ammo"] = "全部弹药", ["melee"] = "近战武器",
        ["stim:*"] = "全部针剂", ["stim:Buffs_Obdolbos"] = "奥布多洛斯针剂",
        ["stim:Buffs_Obdolbos2"] = "奥布多洛斯针剂(进阶)",
    };
    private static readonly Dictionary<string, string> AirdropZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mixed"] = "混合", ["weaponArmor"] = "武器护甲", ["radar"] = "雷达",
        ["supply"] = "补给", ["medical"] = "医疗",
    };

    private static string MapName(string k) => MapZh.TryGetValue(k ?? "", out var v) ? v : k;
    private static string TraderName(string k) => TraderZh.TryGetValue(k ?? "", out var v) ? v : k;
    /// <summary>随从数量：兼容数字（5）与区间对象（{"min":0,"max":2}）⇒ 显示成 `5` / `0~2`。</summary>
    private static string EscortText(Newtonsoft.Json.Linq.JToken t)
    {
        try
        {
            if (t == null) return "默认";
            if (t.Type == Newtonsoft.Json.Linq.JTokenType.Integer) return t.ToObject<int>().ToString();
            // ★ 字符串写法（如 april_fools 的 `"0-2"`）⇒ 统一成 `0~2`
            if (t.Type == Newtonsoft.Json.Linq.JTokenType.String)
            {
                var s = (t.ToString() ?? "").Trim();
                var parts = s.Split(new[] { '-', '~', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) return parts[0].Trim() + "~" + parts[1].Trim();
                return s;
            }
            var mn = t["min"] ?? t["Min"]; var mx = t["max"] ?? t["Max"];
            if (mn != null && mx != null)
            {
                int a = mn.ToObject<int>(), b = mx.ToObject<int>();
                return a == b ? a.ToString() : $"{a}~{b}";
            }
            return t.ToString();
        }
        catch { return "?"; }
    }
    private static string SeasonName(string k) => SeasonZh.TryGetValue(k ?? "", out var v) ? v : k;
    private static string ItemSelName(string k) => ItemSelZh.TryGetValue(k ?? "", out var v) ? v : k;
    private static string AirdropTypeName(string k) => AirdropZh.TryGetValue(k ?? "", out var v) ? v : k;
    private static string BossName(string k)
    {
        var key = (k ?? "").ToLowerInvariant().Replace("_", "");
        if (BossZh.TryGetValue(key, out var v)) return v;
        if (BossZh.TryGetValue(key.Replace("boss", ""), out var v2)) return v2;
        return k;
    }
    /// <summary>从当前 force.txt 同步 F12 勾选状态（不改文件）。</summary>
    private void SyncFromFile()
    {
        try
        {
            string cur = "";
            if (File.Exists(ForceFile())) cur = File.ReadAllText(ForceFile()).Trim();
            _suspend = true;
            _disableAllCfg.Value = cur == "none";
            // ★★ v0.1.5 修正（用户实测"F12 里一个活动都没勾上"）：原来拿**整个** cur 字符串去和每个活动 id 比较，
            //   多选时 cur = "khorovod_2024,halloween_infected" ⇒ 没有任何 id 能匹配 ⇒ 一个都不勾。
            //   现在按逗号/空白拆开逐个匹配（`none`/空 = 全不勾）。
            var parts = (cur ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var one in parts) { var s2 = one.Trim(); if (s2.Length > 0) picked.Add(s2); }
            foreach (var kv in _toggles) kv.Key.Value = picked.Contains(kv.Value);
        }
        catch { }
        finally { _suspend = false; }
    }

    /// <summary>
    /// 把 F12 的选择写进 force.txt。
    /// ★ v0.1.4：支持**多选**（逗号分隔）—— 便于测试组合；服务端会逐个套用并**列出互相冲突的配对**（不阻止）。
    /// </summary>
    private static string _lastWritten = null;   // ★ 值没变就不写文件（避免反复写盘/触发 UI 刷新）

    private void Apply()
    {
        try
        {
            if (!Directory.Exists(ModDir())) Directory.CreateDirectory(ModDir());
            string pick;
            if (_disableAllCfg.Value) pick = "none";
            else pick = string.Join(",", _toggles.Where(kv => kv.Key.Value).Select(kv => kv.Value).Distinct().ToList());

            _suspend = true;
            if (_disableAllCfg.Value)
                foreach (var kv in _toggles) kv.Key.Value = false;    // 关掉全部 → 清空勾选
            _suspend = false;

            var text = pick ?? "";
            if (_lastWritten == text) return;        // ★ 无变化直接返回，不写盘
            _lastWritten = text;
            File.WriteAllText(ForceFile(), text);

            int n = text.Length == 0 || text == "none" ? 0 : text.Split(',').Length;
            if (n > 1)
            {
                Log.LogInfo($"[EventRotator.Client] force.txt = '{text}'（**同时开 {n} 个活动**）"
                            + " → 重启服务端后生效；服务端会在日志里列出这些活动之间是否冲突");
                CheckConflictsLocally(text.Split(',').ToList());
            }
            else
                Log.LogInfo($"[EventRotator.Client] force.txt = '{text}'  → 重启服务端后生效");
        }
        catch (Exception ex) { Log.LogError($"[EventRotator.Client] apply failed: {ex}"); }
    }

    private static string _localConflictCache;

    /// <summary>
    /// ★ v0.1.4 客户端本地冲突预检：用服务端状态文件里的活动列表做一次粗判（只按分类规则提示，
    /// 精确的"同图同首领"冲突由服务端给出）。目的：多选时**立刻**给玩家一个提醒，不用等服务端。
    /// </summary>
    private void CheckConflictsLocally(List<string> ids)
    {
        try
        {
            var ov = EventsOverlay.Instance;
            if (ov == null) return;
            // 服务端状态文件里已有每个生效活动的分类，用它判断"是否会同时出现两个带任务活动"
            var lines = new List<string>();
            if (ids.Count > 1) lines.Add($"（服务端会在日志里给出精确冲突原因）");
            var text = "已选 " + ids.Count + " 个活动：\n" + string.Join("\n", ids.Select(i => "· " + i))
                       + "\n\n⚠ 精确冲突（同图同首领）由服务端在启动日志里列出；"
                       + "组合规则（只允许 1 个带任务活动）只对**自动轮换**生效，手动强制不阻止。";
            if (!string.Equals(text, _localConflictCache, StringComparison.Ordinal))
            {
                _localConflictCache = text;
                Log.LogInfo("[EventRotator.Client] 手动多选提示：\n" + text);
            }
        }
        catch { }
    }

    private class EventInfo
    {
        public string Id { get; set; }
        public string NameZh { get; set; }

        // ★★ v0.1.5 官方"组合表"维度（用于 F12 描述自证）
        public bool? PumpkinScavBackpack { get; set; }
        public bool? RenameBitcoin { get; set; }
        public bool? HalloweenStimBuff { get; set; }
        public bool? ZryachiyMeleeChance { get; set; }
        public bool? GifterOfficial { get; set; }
        public bool? BtrTarcolaSkin { get; set; }
        public bool? RunddansActive { get; set; }
        public bool? NativeChristmas { get; set; }
        public double? ChristmasLootBoost { get; set; }
        public List<string> OfficialGear { get; set; }
        public List<string> OfficialLoot { get; set; }
        public List<string> BotAppearance { get; set; }
        public List<string> PlayerGift { get; set; }
        public string NameEn { get; set; }
        public int StartMonth { get; set; }
        public int StartDay { get; set; }
        public int EndMonth { get; set; }
        public int EndDay { get; set; }
        /// <summary>三层分类：seasonal / limited / noquest / tweak（缺省按有无任务自动归类）。</summary>
        public string Category { get; set; }

        // ★★★ v0.1.6【官方组合（family）· 用户要求"不要遗漏组合活动的开启关闭"】
        //   数据来自 events.json 的 `family` / `familyYear` / `familyGeneric` 三个字段（客户端原来**没读**任何一个）。
        //   服务端 EventEngine.cs:524 ExpandFamilyCombos 的语义（逐行对齐）：
        //     以"有 family 的已选活动"为种子 ⇒ 补上 **同 family** 且（**同年份** 或 **familyGeneric=true**）的其它成员。
        //   ⇒ 勾一个会连带整组；取消时若同族还有成员在开，会被重新带出来。F12 必须把这个说清楚。
        /// <summary>组合族名（如 christmas / halloween / april_fools）；空 = 不属于任何组合。</summary>
        public string Family { get; set; }
        /// <summary>该族内的**年份场次**（如 2024）；与 <see cref="FamilyGeneric"/> 二选一（服务端 EventDef.cs:791 是 int?）。</summary>
        public int? FamilyYear { get; set; }
        /// <summary>该族的**通用成员**（每年都开的本体季节窗，如 christmas / newyear）；服务端 EventDef.cs:794 是 bool?。</summary>
        public bool? FamilyGeneric { get; set; }

        /// <summary>⚠ 本体无此活动任务定义（F12 里作为提醒显示）。</summary>
        public bool TaskMissing { get; set; }
        public string TaskNote { get; set; }
        public System.Collections.Generic.List<string> EnableQuests { get; set; }
        /// <summary>★ v0.1.4 玩家可读的一句话内容（F12 主要就看这个）。</summary>
        public string Desc { get; set; }
        /// <summary>★ v0.1.4 技术实现细节（原 note）—— **不在 F12 显示**，只用于排查。</summary>
        public string DevNote { get; set; }

        // ── ★ v2.1.10 悬浮说明：把"这个活动实际会改什么"也读进来 ──
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> BossSpawns { get; set; }
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, int>> BossChance { get; set; }
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> ZeroBossSpawns { get; set; }
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> ForceHostility { get; set; }
        public System.Collections.Generic.Dictionary<string, int> AirdropWeight { get; set; }
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>> AirdropAddLoot { get; set; }
        public double? LooseLootMultiplier { get; set; }
        public double? StaticLootMultiplier { get; set; }
        public string SeasonalEvent { get; set; }
        public bool NightOnly { get; set; }
        public string ForceSeasonalBossKey { get; set; }

        // ── ★★★ v0.1.4 补齐：新版 F12 说明要用到的全部字段（否则显示不全）──
        public System.Collections.Generic.List<string> LootMaps { get; set; }
        public bool? UnlockTraders { get; set; }
        public double? TraderPriceMultiplier { get; set; }
        public int? TraderUpdateSeconds { get; set; }
        public bool? TraderPurchasesFir { get; set; }
        public int? FleaMinLevel { get; set; }
        public double? FleaTaxMultiplier { get; set; }
        public bool? FleaZeroListingFee { get; set; }
        public bool? LabsFreeAccess { get; set; }
        public bool? TransitsActive { get; set; }
        public string ForceWeatherSeason { get; set; }
        // ★ C2：`instant_hideout` 的客户端 DTO 死字段已删除（服务端 0 读取；真正生效的是 hideoutBuildSeconds，
        //   而本客户端也从 v0.1.4 起就不再显示"藏身处加速"，见 BuildDesc() 里那段注释）。
        public int? HideoutBuildSeconds { get; set; }
        public int? HideoutCraftSeconds { get; set; }
        /// <summary>globals 维度（伤害/口渴/补充量/技能/经验系数）。</summary>
        public System.Collections.Generic.Dictionary<string, double> Globals { get; set; }
        /// <summary>items 维度选择器 → 各系数（只用来显示哪些物品被改了）。</summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, double>> Items { get; set; }
        public System.Collections.Generic.Dictionary<string, double> DrumMagazineBoost { get; set; }
        public System.Collections.Generic.Dictionary<string, object> BotAddItems { get; set; }
        public System.Collections.Generic.Dictionary<string, object> BotAddMods { get; set; }
        public System.Collections.Generic.Dictionary<string, object> BotLootAdd_ { get; set; }
        public System.Collections.Generic.Dictionary<string, int> BotPresetBatch { get; set; }
        public int? BotMaxCap { get; set; }
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> ForceAbpsSpawns { get; set; }
        public double? AirdropFrequencyMultiplier { get; set; }
        public string AirdropForceType { get; set; }

        // ── ★★★ v2.6.17【F12 同步 · 项目规矩 R1】本轮新增/改语义的字段 ──
        //   规矩：`events.json` 改了 `desc`/`devNote`/`taskNote` ⇒ F12 自动跟着变；
        //   但**新增字段** ⇒ 客户端不认识它 ⇒ 玩家看不到那条效果 ⇒ 必须在这里补属性 + 在 BuildDesc() 里补自证行。
        /// <summary>halloween_2023：全图 SCAV 波次换成诅咒标签 SCAV（cursedAssault 集体冲锋死追）。</summary>
        public bool? TaggedAndCursed { get; set; }
        /// <summary>beer_upgrades：往藏身处区域**追加**升级材料需求（官方 Beer Upgrades：升级改用 Pevko 淡啤酒）。</summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<HideoutReqDto>> HideoutAddRequirements { get; set; }
        /// <summary>softcore：按图按 tpl 调松散战利品权重/概率。</summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, LootWeightDto>> LooseLootWeightSet { get; set; }
        /// <summary>softcore：往商人 assort 追加以物易物配方。</summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<BarterDto>> TraderAssortAdd { get; set; }
        /// <summary>cultist_pilgrimage：追加以物易物配方（★ 此前**静默失效**、0 条注入，v2.6.x 起真注入 —— F12 必须写明）。</summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<BarterDto>> TraderBarterAdd { get; set; }
        /// <summary>指定 bot 角色的生成产出升级（更好的枪/弹药/插板；大弹鼓、下挂榴弹、火箭筒都走这里）。</summary>
        public System.Collections.Generic.Dictionary<string, BotGearUpgradeDto> BotGearUpgrade { get; set; }
        /// <summary>halloween_2023：撤离点锁定清单（★ 服务端默认**不锁**；仪式后的"只留 1 个"由**客户端插件**做，
        /// 见 F12「★仪式开始后只留 1 个撤离点」/ RitualExitLock.cs / BuildDesc 里的说明）。</summary>
        public System.Collections.Generic.List<string> ExitKeepOnly { get; set; }
        /// <summary>随从数量：值可能是数字（5）也可能是区间对象（{"min":0,"max":2}）⇒ 用 JToken 两种都收。</summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JToken>> EscortCount { get; set; }
    }
}

/// <summary>让 ConfigurationManager 支持 DispName 的标签类（属性名约定）。</summary>
internal sealed class ConfigurationManagerAttributes
{
    public string DispName;
    public int? Order;
}

// ── ★★★ v2.6.17【F12 同步】新增字段的 DTO（对齐服务端 EventDef.cs 里同名定义；Newtonsoft 反序列化大小写不敏感）──
/// <summary>藏身处追加需求一条（服务端 <c>EventDef.HideoutReqDef</c>）。</summary>
internal sealed class HideoutReqDto
{
    public int Stage { get; set; }
    public string TemplateId { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>松散战利品权重（服务端 <c>EventDef.LooseLootWeightOpt</c>）。</summary>
internal sealed class LootWeightDto
{
    public double? ProbabilityMult { get; set; }
    public double? WeightMult { get; set; }
}

/// <summary>以物易物配方（服务端 <c>EventDef.BarterAddDef</c>）。</summary>
internal sealed class BarterDto
{
    public string Reward { get; set; }
    public int RewardCount { get; set; } = 1;
    public string Cost { get; set; }
    public int CostCount { get; set; } = 1;
    public int Loyalty { get; set; } = 1;
    public int MaxPerRestock { get; set; } = 1;
    public int CostLevel { get; set; } = 1;
    public string Side { get; set; }
}

/// <summary>敌种装备升级（服务端 <c>EventDef.BotGearUpgradeDef</c>，字段逐一对齐）。</summary>
internal sealed class BotGearUpgradeDto
{
    public string Role { get; set; }
    public bool BetterAmmo { get; set; }
    public bool BetterPlates { get; set; }
    public string WeaponTpl { get; set; }
    public string MagazineTpl { get; set; }
    public bool BiggestMagazine { get; set; }
    public string AddLauncherTpl { get; set; }
    public string LauncherAmmoTpl { get; set; }
    /// <summary>★ rpg_scavs 用：换枪后额外补给这批弹药（如 40x46 榴弹）。</summary>
    public System.Collections.Generic.List<string> ExtraAmmoTpls { get; set; }
    /// <summary>换枪时是否清掉旧枪配件（服务端默认 true）。</summary>
    public bool ClearOldMods { get; set; } = true;
}
