using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EventRotator;

/// <summary>
/// ★★★ 把自定义角色 <c>bossScourge</c>(13790) / <c>followerScourge</c>(13791) /
/// <c>cleanupcrew</c>(13792) / <c>pillager</c>(13793) / <c>bloodhound</c>(13794) 接进 **SAIN 服务端预设**。
///
/// <para><b>要解决的实机报错</b>（SAIN 客户端逐个 bot 取设置时打印）：
/// <code>
/// [bossScourge] does not exist in SAINSettings Dictionary!
/// </code>
/// 来源已反编译核实：<c>SAINBotSettingsClass.GetSAINSettings(type, difficulty)</c>
/// 在 <c>SAINSettings</c>（= 预设里的 <c>BotSettings</c>，键是 EFT 的 <c>WildSpawnType</c>）里查不到该角色。</para>
///
/// <para><b>预设文件在哪</b>：&lt;SPT_Runtime&gt;/user/mods/Solarint-SAIN-ServerMod/Presets/*.json
/// （本机 3 个：<c>Default [Modified].json</c> / <c>Default with Harder PMCs [Modified].json</c> /
/// <c>Less Difficult [Modified].json</c>；每个 51 个键 = 45 个内置角色 + BlackDiv 注册的 6 个）。</para>
///
/// <para><b>本类做两件事</b>：
/// <list type="number">
///   <item><b>写盘（Preload 档）</b>：给每个预设的 <c>BotSettings</c> 补 <c>"13790"</c>/<c>"13791"</c>
///     两个条目，内容克隆现成的 <c>arenaFighterEvent</c>（其 <c>Settings</c> 三档直接照抄）。
///     幂等：已有就跳过；写前备份 <c>.bak_before_scourgesain_&lt;yyyyMMddHHmmss&gt;</c>；
///     <b>只做字节级插入</b>，其它键/缩进/键序/数字写法**逐字不动**（不是整文件重排）。</item>
///   <item><b>向 SAIN 注册角色</b>（主 IOnLoad 档，反射 <c>SAIN.ServerInterop</c>）：
///     反编译实证 <c>SAINServerMod.Generators.PresetGenerationService.ComposeCustom()</c> 只遍历
///     <c>AllBotTypes()</c>（45 个内置 + 各 mod 通过 <c>ISainBotTypeRegistry.RegisterAsync</c> 注册的），
///     **预设里存在但 AllBotTypes 不认识的键会被直接丢弃**，客户端永远收不到。
///     ⇒ 只改 json 不足以消除上面的报错，必须同时注册。SAIN 未安装时本步静默跳过。</item>
/// </list></para>
///
/// <para><b>开关</b>：<c>config.json</c> 的 <c>"sainPresetInject"</c>（默认 true）。false ⇒ 两件事都不做。
/// <b>任何失败只 warning，绝不抛</b>（找不到 SPT_Runtime / SAIN 目录 / 解析失败都只跳过）。</para>
/// </summary>
public static class SainPresetInject
{
    public const int BossValue = 13790;
    public const int FollowerValue = 13791;

    // ★ 第 11 项（Cleanup Crew / Pillager）：与 CustomBotTypes.CustomRoles 同值；
    //   brain 用克隆源 arenaFighterEvent 的等价物（本类只认 ExUsec+PMC 这一套，见 ApplyBrainFields 注释）。
    public const int CleanupCrewValue = 13792;
    public const int PillagerValue = 13793;
    /// <summary>第 11 项追加：寻血猎犬（Bloodhound）。</summary>
    public const int BloodhoundValue = 13794;

    // ★ 第 9 项（Arms Race）两个军火商：与 CustomBotTypes.CustomRoles 同值（契约 13795 / 13796）。
    /// <summary>第 9 项：军火商「古斯」。</summary>
    public const int GusValue = 13795;
    /// <summary>第 9 项：军火商「巴斯马赫」。</summary>
    public const int BasmachValue = 13796;

    /// <summary>克隆模板：本机预设里现成的"泥地猎犬"条目（Settings 三档齐全）。</summary>
    /// <summary>
    /// ★ 2026-10-30【用户裁决】克隆源 = <c>bossKnight</c>（**首领档**）：7 个自定义角色全部按首领脾气注入
    /// （hard.Difficulty.ScatteringCoef=0.5 / AggressionCoef=2.0，见实测预设）。
    /// 旧值 <c>arenaFighterEvent</c> 是"普通"档（ScatteringCoef=1.0 / AggressionCoef=1.0）。
    /// </summary>
    private const string TemplateKey = "bossKnight";

    private const string SainModFolder = "Solarint-SAIN-ServerMod";
    private const string ConfigKey = "sainPresetInject";

    /// <summary>日志出口（Preload 档接 <c>logger.Info/Warning</c>）。</summary>
    public static Action<string> Info;

    /// <summary>日志出口（warning）。</summary>
    public static Action<string> Warning;

    /// <summary>本次启动是否启用（config.json 的 sainPresetInject；缺省 true）。</summary>
    public static bool Enabled { get; private set; } = true;

    // ═══════════════════════════ ★ 2026-10-29【第 21 项】SAIN brain / layer ═══════════════════════════
    // 背景（PPES 兼容做法，本机 SAIN.ServerInterop 4.5.1.0 已用反射核对）：
    //   SAIN 的 `SAIN.ServerInterop.SainBotTypeRegistration` 上有三个属性
    //     `List<string> BrainsToApply` / `List<string> LayersToRemove` / `string BaseBrain`
    //   我们以前只设了 Name/WildSpawnType/BotDbKey/Section/Description/DifficultyModifier
    //   ⇒ SAIN 只会给这两个自定义角色用"默认 brain"，灾厄小队的 AI 不真正吃 SAIN 大脑。
    //   PPES 的做法：BrainsToApply=["PMC","ExUsec"] + LayersToRemove 六层（+ 它没设 BaseBrain）。
    //
    // config.json（**缺这段时用下面的内置默认，行为与写了一份相同**）：
    //   "sainBrains": {
    //     "enabled": true,
    //     "baseBrainBoss": "ExUsec",
    //     "baseBrainFollower": "ExUsec",
    //     "brainsToApply": ["ExUsec", "PMC"],       // 也接受逗号字符串 "ExUsec,PMC"
    //     "layersToRemove": ["Request","KnightFight","PmcBear","PmcUsec","ExURequest","StationaryWS"]
    //   }
    //   `enabled=false` ⇒ **三个字段一个都不设**，退回旧行为（可回退）。
    /// <summary>是否设置 brain/layer 三个字段（config: sainBrains.enabled；false = 退回旧行为）。</summary>
    public static bool BrainsEnabled { get; private set; } = true;
    /// <summary>bossScourge 的 BaseBrain。</summary>
    public static string BaseBrainBoss { get; private set; } = "ExUsec";
    /// <summary>followerScourge 的 BaseBrain。</summary>
    public static string BaseBrainFollower { get; private set; } = "ExUsec";
    /// <summary>要套用的 brain 列表（顺序保持）。</summary>
    public static List<string> BrainsToApply { get; private set; } = new() { "ExUsec", "PMC" };
    /// <summary>要移除的 layer 列表。</summary>
    public static List<string> LayersToRemove { get; private set; } = new()
    { "Request", "KnightFight", "PmcBear", "PmcUsec", "ExURequest", "StationaryWS" };

    private const string BrainsConfigKey = "sainBrains";

    /// <summary>预加载阶段是否真的找到了 SAIN 预设目录（用于"注册"那步决定要不要报噪声 warning）。</summary>
    private static bool _sainPresetsFound;

    private static readonly (int Value, string Name, string DbKey)[] Roles =
    {
        (BossValue, "Scourge Boss", "bossscourge"),
        (FollowerValue, "Scourge Follower", "followerscourge"),
        // ★ 第 11 项：预制体仍克隆 arenaFighterEvent（TemplateKey），名字/描述按契约
        (CleanupCrewValue, "Cleanup Crew", "cleanupcrew"),
        (PillagerValue, "Pillager", "pillager"),
        (BloodhoundValue, "Bloodhound", "bloodhound"),
        // ★ 第 9 项（Arms Race 军火商）：DisplayKey 用契约里的小写 role 名（gus / basmach）。
        (GusValue, "Gus", "gus"),
        (BasmachValue, "Basmach", "basmach"),
    };

    /// <summary>该角色在 SAIN 里的描述（注册对象上的 Description 字段）。</summary>
    private static string DescriptionOf((int Value, string Name, string DbKey) role) => role.Value switch
    {
        BossValue => "EventRotator custom Scourge boss (WildSpawnType 13790).",
        FollowerValue => "EventRotator custom Scourge follower (WildSpawnType 13791).",
        CleanupCrewValue => "EventRotator custom Cleanup Crew (WildSpawnType 13792, clone of bossKnight (boss-tier preset)).",
        PillagerValue => "EventRotator custom Pillager (WildSpawnType 13793, clone of bossKnight (boss-tier preset)).",
        BloodhoundValue => "EventRotator custom Bloodhound (WildSpawnType 13794, clone of bossKnight (boss-tier preset)).",
        GusValue => "EventRotator custom arms dealer Gus (WildSpawnType 13795, clone of bossKnight (boss-tier preset)).",
        BasmachValue => "EventRotator custom arms dealer Basmach (WildSpawnType 13796, clone of bossKnight (boss-tier preset)).",
        _ => $"EventRotator custom role {role.Name} (WildSpawnType {role.Value}).",
    };

    // ═══════════════════════════ ① 写盘：注入预设 ═══════════════════════════

    /// <summary>
    /// 由 <see cref="QuestLinePreload.OnLoadAsync"/>（<c>OnLoadOrder.Preload</c> = 100000）调用。
    /// 选 Preload 而不是主类 <c>OnLoadAsync</c>（TypePriority = int.MaxValue）的原因：
    /// SAIN 服务端自己在 <c>OnLoad</c> 档会 <c>UpgradeCustomToCurrentSchemaAsync()</c> 读一遍这些
    /// 自定义预设，**越早写越好**；而这里只碰磁盘文件、不依赖任何 SPT 表，Preload 阶段完全可用。
    /// </summary>
    public static void Run(string modPath)
    {
        try
        {
            Enabled = ReadEnabled(modPath);
            // ★ 第 21 项：brain/layer 配置在这里一次性读掉（RegisterBotTypes 是另一次调用，没有 modPath）
            ReadSainBrains(modPath);
            if (!Enabled)
            {
                Info?.Invoke($"[EventRotator][SAIN] config.json 的 {ConfigKey}=false ⇒ 跳过预设注入（也不会向 SAIN 注册角色）");
                return;
            }
            if (!BrainsEnabled)
                Info?.Invoke("[EventRotator][SAIN] sainBrains.enabled=false ⇒ 不给角色设置 BaseBrain/BrainsToApply/LayersToRemove（退回旧行为）");

            var runtime = FindRuntime(modPath);
            if (runtime == null)
            {
                Warning?.Invoke("[EventRotator][SAIN] ⚠ 找不到 SPT_Runtime"
                    + $"（从 {modPath} 逐级向上、以及当前目录都没命中 SPTarkov.Server.Core.dll / SPT_Data）"
                    + " ⇒ 跳过 SAIN 预设注入（报错 [bossScourge] does not exist... 会继续存在）");
                return;
            }

            var presetsDir = Path.Combine(runtime, "user", "mods", SainModFolder, "Presets");
            if (!Directory.Exists(presetsDir))
            {
                Warning?.Invoke($"[EventRotator][SAIN] ⚠ 未找到 SAIN 服务端预设目录（没装 SAIN？）：{presetsDir} ⇒ 跳过注入");
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(presetsDir, "*.json");
            }
            catch (Exception ex)
            {
                Warning?.Invoke($"[EventRotator][SAIN] ⚠ 列目录失败 {presetsDir}：{ex.Message} ⇒ 跳过注入");
                return;
            }
            if (files.Length == 0)
            {
                Warning?.Invoke($"[EventRotator][SAIN] ⚠ SAIN 预设目录里没有 *.json：{presetsDir} ⇒ 跳过注入");
                return;
            }

            _sainPresetsFound = true;
            int injected = 0, addedRoles = 0, refreshedRoles = 0, already = 0, failed = 0;
            foreach (var file in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                try
                {
                    var (added, refreshed, backup, reason) = InjectFile(file);
                    if (added.Count > 0 || refreshed.Count > 0)
                    {
                        injected++;
                        addedRoles += added.Count;
                        refreshedRoles += refreshed.Count;
                        var parts = new List<string>();
                        if (added.Count > 0) parts.Add("+ " + string.Join(" +", added));
                        if (refreshed.Count > 0) parts.Add("刷新 " + string.Join(" +", refreshed));
                        Info?.Invoke($"[EventRotator][SAIN]   · {name}：{string.Join("；", parts)}（备份 {Path.GetFileName(backup)}）");
                    }
                    else if (reason != null)
                    {
                        failed++;
                        Warning?.Invoke($"[EventRotator][SAIN]   · {name}：跳过 —— {reason}");
                    }
                    else
                    {
                        already++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Warning?.Invoke($"[EventRotator][SAIN]   · {name}：注入失败（已跳过，不影响启动）—— {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ★ 用户要求的那一行（N = 本次真正写盘的文件数；幂等重跑时为 0）
            Info?.Invoke($"[EventRotator][SAIN] ✔ 已向 {injected} 个预设注入 {string.Join("/", Roles.Select(r => r.DbKey))}"
                + $"（克隆源 {TemplateKey}，首领档；新增 {addedRoles} 条"
                + (refreshedRoles > 0 ? $"、刷新了 {refreshedRoles} 条已存在的条目" : "")
                + "）"
                + (already > 0 ? $"（{already} 个预设已是 {TemplateKey} 内容，幂等跳过）" : "")
                + (failed > 0 ? $"（{failed} 个跳过/失败，见上）" : ""));
        }
        catch (Exception ex)
        {
            // 兜底：这一步永远不许把服务端 startup 弄挂
            Warning?.Invoke($"[EventRotator][SAIN] 预设注入异常（已忽略，不影响启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 对单个预设文件做「缺则补、旧则刷」：返回 (本次新增的键, 本次刷新的键, 备份路径, 跳过原因)。
    ///
    /// <para><b>为什么要"刷"</b>：部署机上这 7 条早就以 <c>arenaFighterEvent</c>（普通档）写进去了，
    /// 只做"有则跳"等于克隆源换了也不生效。这里按 <c>DifficultyModifier</c> + <c>Settings</c> 与
    /// <c>bossKnight</c>（首领档）逐字比较，不一致就用模板内容**整体覆盖**（保留原条目的
    /// <c>Name</c>/<c>WildSpawnType</c> 与我们以前写进去的 <c>Description</c>），写前仍备份。</para>
    ///
    /// <para>跳过原因 = null 表示"7 条都已在且已经是 bossKnight 内容"。</para>
    /// </summary>
    private static (List<string> Added, List<string> Refreshed, string Backup, string Reason) InjectFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("BotSettings", out var botSettings)
            || botSettings.ValueKind != JsonValueKind.Object)
        {
            return (new List<string>(), new List<string>(), null, "没有 BotSettings 对象");
        }

        var missing = new List<(int Value, string Name, string DbKey, string Key)>();
        var stale = new List<(int Value, string Name, string DbKey, string Key, JsonElement Existing)>();
        foreach (var role in Roles)
        {
            var key = role.Value.ToString(CultureInfo.InvariantCulture);
            if (!botSettings.TryGetProperty(key, out var existing))
            {
                missing.Add((role.Value, role.Name, role.DbKey, key));
            }
            else
            {
                stale.Add((role.Value, role.Name, role.DbKey, key, existing));   // 先收集，拿到模板后再判"是否已经是最新"
            }
        }

        // 模板必须在 BotSettings 里；找不到就只告警、不硬造（硬造 Settings 会缺字段，SAIN 会崩）
        if (!TryLocateInContainer(bytes, "BotSettings", TemplateKey, out var tplKeyStart, out var tplValueStart, out var tplValueEnd))
        {
            return (new List<string>(), new List<string>(), null, $"BotSettings 里没有 \"{TemplateKey}\" 模板条目可克隆");
        }

        var entryBytes = new byte[tplValueEnd - tplValueStart];
        Array.Copy(bytes, tplValueStart, entryBytes, 0, entryBytes.Length);

        // 用模板条目自身的 JSON 做"是否已经是首领档"的比较（同一份文件同一套格式 ⇒ 原始文本逐字比较）
        JsonDocument tplDoc;
        try { tplDoc = JsonDocument.Parse(entryBytes); }
        catch (Exception ex) { return (new List<string>(), new List<string>(), null, $"模板条目 JSON 解析失败：{ex.Message}"); }
        using var _tplDoc = tplDoc;
        var tplElement = tplDoc.RootElement;

        var needAdd = new List<(int Value, string Name, string DbKey, string Key)>(missing);
        var needRefresh = new List<(int Value, string Name, string DbKey, string Key, JsonElement Existing)>();
        foreach (var r in stale)
            if (!SameAsBossTemplate(r.Existing, tplElement)) needRefresh.Add(r);

        if (needAdd.Count == 0 && needRefresh.Count == 0)
        {
            return (new List<string>(), new List<string>(), null, null);   // 全是最新 ⇒ 幂等跳过、不写盘、不备份
        }

        // 新条目的缩进沿用模板键所在行的行首缩进（这样插进去后缩进与邻居一致）
        var lineStart = tplKeyStart;
        while (lineStart > 0 && bytes[lineStart - 1] != (byte)'\n') lineStart--;
        var indent = new byte[tplKeyStart - lineStart];
        Array.Copy(bytes, lineStart, indent, 0, indent.Length);
        var indentOk = true;
        foreach (var b in indent)
        {
            if (b != (byte)' ' && b != (byte)'\t') { indentOk = false; break; }
        }
        var insertAt = indentOk ? lineStart : tplKeyStart;
        if (!indentOk) indent = Array.Empty<byte>();
        var nl = Encoding.UTF8.GetBytes(bytes.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n");

        // ① 已有但内容旧的条目：原地整体替换（保留 Name/WildSpawnType/Description）
        var replacements = new List<(int Start, int End, byte[] Bytes)>();
        foreach (var r in needRefresh)
        {
            if (!TryLocateInContainer(bytes, "BotSettings", r.Key, out _, out var vStart, out var vEnd))
            {
                throw new InvalidOperationException($"已有条目 \"{r.Key}\" 定位失败，放弃写盘");
            }
            var refreshed = BuildClone(entryBytes, r.Name, r.Value);
            if (r.Existing.TryGetProperty("Description", out var desc) && desc.ValueKind == JsonValueKind.String)
                refreshed = SetTopLevelRaw(refreshed, "Description", desc.GetRawText());
            replacements.Add((vStart, vEnd, refreshed));
        }

        var working = (byte[])bytes.Clone();
        // 从后往前替换，避免偏移失效
        foreach (var rep in replacements.OrderByDescending(x => x.Start))
        {
            var tail = new byte[working.Length - rep.End];
            Array.Copy(working, rep.End, tail, 0, tail.Length);
            var head = new byte[rep.Start];
            Array.Copy(working, 0, head, 0, rep.Start);
            var merged = new byte[head.Length + rep.Bytes.Length + tail.Length];
            Array.Copy(head, 0, merged, 0, head.Length);
            Array.Copy(rep.Bytes, 0, merged, head.Length, rep.Bytes.Length);
            Array.Copy(tail, 0, merged, head.Length + rep.Bytes.Length, tail.Length);
            working = merged;
        }

        // ② 缺的条目：在模板键位置插入（插入点按前面的替换量修正）
        if (needAdd.Count > 0)
        {
            using var block = new MemoryStream();
            foreach (var m in needAdd)
            {
                var clone = BuildClone(entryBytes, m.Name, m.Value);
                block.Write(indent, 0, indent.Length);
                block.Write(Encoding.UTF8.GetBytes("\"" + m.Key + "\": "));
                block.Write(clone, 0, clone.Length);
                block.Write(Encoding.UTF8.GetBytes(","), 0, 1);
                block.Write(nl, 0, nl.Length);
            }
            var blockBytes = block.ToArray();
            int deltaBefore = 0;
            foreach (var rep in replacements)
                if (rep.Start < insertAt) deltaBefore += rep.Bytes.Length - (rep.End - rep.Start);
            var at = Math.Max(0, Math.Min(working.Length, insertAt + deltaBefore));
            var result = new byte[working.Length + blockBytes.Length];
            Array.Copy(working, 0, result, 0, at);
            Array.Copy(blockBytes, 0, result, at, blockBytes.Length);
            Array.Copy(working, at, result, at + blockBytes.Length, working.Length - at);
            working = result;
        }

        var resultBytes = working;

        // 写前自检：重新解析 + 断言"新增键正确、刷新项确实等于模板、原有键一个不少、其它结构没变"
        using (var check = JsonDocument.Parse(resultBytes))
        {
            if (check.RootElement.ValueKind != JsonValueKind.Object
                || !check.RootElement.TryGetProperty("BotSettings", out var newBs)
                || newBs.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("注入后重新解析失败：BotSettings 丢失");
            }

            int oldCount = 0;
            foreach (var p in botSettings.EnumerateObject())
            {
                oldCount++;
                if (!newBs.TryGetProperty(p.Name, out _))
                {
                    throw new InvalidOperationException($"注入后原有键 \"{p.Name}\" 丢失，放弃写盘");
                }
            }
            int newCount = 0;
            foreach (var _ in newBs.EnumerateObject()) newCount++;
            if (newCount != oldCount + needAdd.Count)
            {
                throw new InvalidOperationException($"注入后 BotSettings 键数 {newCount} ≠ 预期 {oldCount + needAdd.Count}，放弃写盘");
            }

            foreach (var m in needAdd)
            {
                if (!newBs.TryGetProperty(m.Key, out var added))
                    throw new InvalidOperationException($"注入后找不到新键 \"{m.Key}\"，放弃写盘");
                if (!added.TryGetProperty("WildSpawnType", out var wst)
                    || wst.ValueKind != JsonValueKind.Number
                    || wst.GetInt32() != m.Value)
                    throw new InvalidOperationException($"新键 \"{m.Key}\" 的 WildSpawnType 不是数值 {m.Value}，放弃写盘");
                if (!SameAsBossTemplate(added, tplElement))
                    throw new InvalidOperationException($"新键 \"{m.Key}\" 的内容与模板 {TemplateKey} 不一致，放弃写盘");
            }

            foreach (var r in needRefresh)
            {
                if (!newBs.TryGetProperty(r.Key, out var got))
                    throw new InvalidOperationException($"刷新后找不到已有键 \"{r.Key}\"，放弃写盘");
                if (!got.TryGetProperty("WildSpawnType", out var wst2)
                    || wst2.ValueKind != JsonValueKind.Number || wst2.GetInt32() != r.Value)
                    throw new InvalidOperationException($"刷新后 \"{r.Key}\" 的 WildSpawnType 被写坏，放弃写盘");
                if (!SameAsBossTemplate(got, tplElement))
                    throw new InvalidOperationException($"刷新后 \"{r.Key}\" 的内容与模板 {TemplateKey} 不一致，放弃写盘");
                if (r.Existing.TryGetProperty("Description", out var od)
                    && (!got.TryGetProperty("Description", out var nd)
                        || nd.GetRawText() != od.GetRawText()))
                    throw new InvalidOperationException($"刷新后 \"{r.Key}\" 的 Description 没有保留，放弃写盘");
            }
        }

        // 备份（写前；同一秒重复运行也不会覆盖旧备份）
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var bak = path + ".bak_before_scourgesain_" + stamp;
        var dup = 2;
        while (File.Exists(bak))
        {
            bak = path + ".bak_before_scourgesain_" + stamp + "_" + dup.ToString(CultureInfo.InvariantCulture);
            dup++;
        }
        File.Copy(path, bak, false);

        File.WriteAllBytes(path, resultBytes);

        // ★ 逐条日志（用户要求的格式）
        foreach (var m in needAdd)
            Info?.Invoke($"[SAIN] {m.Value} {DisplayKey((m.Value, m.Name, m.DbKey))}：新增（克隆源 {TemplateKey}，首领档）");
        foreach (var r in needRefresh)
            Info?.Invoke($"[SAIN] {r.Value} {DisplayKey((r.Value, r.Name, r.DbKey))}：克隆源 arenaFighterEvent → {TemplateKey}（首领档）");

        return (needAdd.Select(m => m.Key).ToList(), needRefresh.Select(r => r.Key).ToList(), bak, null);
    }

    /// <summary>已有条目是否已经是"首领档"：<c>DifficultyModifier</c> 与 <c>Settings</c> 都和模板逐字一致。</summary>
    private static bool SameAsBossTemplate(JsonElement entry, JsonElement tpl)
    {
        if (!JsonPropSame(entry, tpl, "DifficultyModifier")) return false;
        if (!JsonPropSame(entry, tpl, "Settings")) return false;
        return true;
    }

    private static bool JsonPropSame(JsonElement a, JsonElement b, string name)
    {
        bool ha = a.TryGetProperty(name, out var pa);
        bool hb = b.TryGetProperty(name, out var pb);
        if (ha != hb) return false;
        if (!ha) return true;
        return pa.GetRawText() == pb.GetRawText();
    }

    /// <summary>把顶层字符串字段写成原始 JSON（无则插到对象开头）。用于保留旧的 <c>Description</c>。</summary>
    private static byte[] SetTopLevelRaw(byte[] entry, string name, string rawJson)
    {
        if (TryLocateTopLevel(entry, name, out _, out var start, out var end))
        {
            var bytes = Encoding.UTF8.GetBytes(rawJson);
            var merged = new byte[entry.Length - (end - start) + bytes.Length];
            Array.Copy(entry, 0, merged, 0, start);
            Array.Copy(bytes, 0, merged, start, bytes.Length);
            Array.Copy(entry, end, merged, start + bytes.Length, entry.Length - end);
            return merged;
        }
        int brace = Array.IndexOf(entry, (byte)'{');
        if (brace < 0) throw new InvalidOperationException("条目不是 JSON 对象，无法写入 " + name);
        var ins = Encoding.UTF8.GetBytes("\"" + name + "\": " + rawJson + ",");
        var res = new byte[entry.Length + ins.Length];
        Array.Copy(entry, 0, res, 0, brace + 1);
        Array.Copy(ins, 0, res, brace + 1, ins.Length);
        Array.Copy(entry, brace + 1, res, brace + 1 + ins.Length, entry.Length - brace - 1);
        return res;
    }

    /// <summary>用模板条目的原始字节造一条新条目：只替换顶层 <c>Name</c> / <c>WildSpawnType</c> 两个值。</summary>
    private static byte[] BuildClone(byte[] entry, string name, int value)
    {
        if (!TryLocateTopLevel(entry, "Name", out _, out var nameStart, out var nameEnd))
        {
            throw new InvalidOperationException("模板条目没有顶层 Name 字段");
        }
        if (!TryLocateTopLevel(entry, "WildSpawnType", out _, out var wstStart, out var wstEnd))
        {
            throw new InvalidOperationException("模板条目没有顶层 WildSpawnType 字段");
        }

        var replacements = new List<(int Start, int End, byte[] Text)>
        {
            (nameStart, nameEnd, JsonSerializer.SerializeToUtf8Bytes(name)),
            (wstStart, wstEnd, Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture))),
        };
        replacements.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < replacements.Count; i++)
        {
            if (replacements[i].Start < replacements[i - 1].End)
            {
                throw new InvalidOperationException("模板条目字段区间重叠，放弃克隆");
            }
        }

        using var ms = new MemoryStream(entry.Length + 32);
        var pos = 0;
        foreach (var r in replacements)
        {
            ms.Write(entry, pos, r.Start - pos);
            ms.Write(r.Text, 0, r.Text.Length);
            pos = r.End;
        }
        ms.Write(entry, pos, entry.Length - pos);
        return ms.ToArray();
    }

    // ═══════════════════════ ② 向 SAIN 注册角色（反射，可选依赖）═══════════════════════

    /// <summary>
    /// 由主类 <c>EventRotatorServer.OnLoadAsync</c> 在 <c>CustomBotTypes.Install(...)</c> **之后**调用
    /// （SAIN 注册时要读 <c>BotTable.Types[&lt;botDbKey&gt;]</c> 拿难度默认值，必须先有类型）。
    ///
    /// <para>只用反射：<c>SAIN.ServerInterop.dll</c> 是 SAIN 的附属程序集，**不能**加编译引用
    /// （没装 SAIN 的机器会启动失败）。SAIN 缺席 ⇒ 静默跳过（预加载阶段已经警告过预设目录不存在）。</para>
    /// </summary>
    public static void RegisterBotTypes(IServiceProvider services, Action<string> info, Action<string> warning)
    {
        if (!Enabled) return;
        try
        {
            var registryType = FindLoadedType("SAIN.ServerInterop.ISainBotTypeRegistry");
            var registrationType = FindLoadedType("SAIN.ServerInterop.SainBotTypeRegistration");
            if (registryType == null || registrationType == null)
            {
                if (_sainPresetsFound)
                {
                    warning?.Invoke("[EventRotator][SAIN] ⚠ SAIN 预设目录存在，但运行时没找到 SAIN.ServerInterop"
                        + $"（SAIN 版本过旧/未加载）⇒ 跳过角色注册；预设里的 {string.Join("/", Roles.Select(r => r.Value.ToString()))} 会被 SAIN 的 ComposeCustom 过滤掉");
                }
                return;
            }

            var registry = services?.GetService(registryType);
            if (registry == null)
            {
                if (_sainPresetsFound)
                {
                    warning?.Invoke("[EventRotator][SAIN] ⚠ DI 容器里取不到 ISainBotTypeRegistry（SAIN 服务端未注册？）⇒ 跳过角色注册");
                }
                return;
            }

            var method = registryType.GetMethod("RegisterAsync", new[] { registrationType, typeof(CancellationToken) })
                         ?? registryType.GetMethod("RegisterAsync");
            if (method == null)
            {
                warning?.Invoke("[EventRotator][SAIN] ⚠ ISainBotTypeRegistry 上找不到 RegisterAsync ⇒ 跳过角色注册");
                return;
            }

            // ★ 2026-10-30【去重】新路径（SainBotRegistration）可能已经抢先注册（带 "EventRotator " 前缀、Section=Bosses）。
            //   这条旧路径原先不查表就调 RegisterAsync ⇒ SAIN 会把它们当"重复注册"打成 [Error] Ignoring…
            //   ⇒ 先查 AllBotTypes()，已存在的 WildSpawnType 直接跳过（保留新路径那份 Bosses 分类）。
            var existingIds = new HashSet<int>();
            try
            {
                var all = registry.GetType().GetMethod("AllBotTypes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (all?.Invoke(registry, null) is System.Collections.IEnumerable list)
                {
                    foreach (var it in list)
                    {
                        var wt = it?.GetType().GetProperty("WildSpawnType")?.GetValue(it)
                              ?? it?.GetType().GetField("WildSpawnType")?.GetValue(it);
                        if (wt != null) existingIds.Add(Convert.ToInt32(wt));
                    }
                }
            }
            catch { }

            int ok = 0, skippedDup = 0, skippedScourge = 0;
            foreach (var role in Roles)
            {
                // ★★ 2026-10-31【灾厄首领/随从不再走旧路径】实测反馈：旧路径给 followerScourge 设了
                //   brain=ExUsec + LayersToRemove 6 层 ⇒ 和 ScourgeAi 的 3 层抢同一个脑，
                //   表现是"首领站在原地看着你不动 / 有时整队行为异常"。
                //   灾厄两个角色应由 SainBotRegistration（Section=Bosses）注册、ScourgeAi 三层驱动；
                //   客户端还会把 SAIN 层从 ScourgeBrain 上摘掉，旧路径这里注册等于白注册 + 添乱。
                if (role.Value == 13790 || role.Value == 13791) { skippedScourge++; continue; }
                if (existingIds.Contains(role.Value)) { skippedDup++; continue; }
                try
                {
                    var registration = Activator.CreateInstance(registrationType);
                    SetProperty(registration, "Name", role.Name);
                    SetProperty(registration, "WildSpawnType", role.Value);
                    SetProperty(registration, "BotDbKey", role.DbKey);
                    // ★ 2026-10-30【实机根因】这里原来写死 "Other" ⇒ 编辑器把 7 个角色放进「其他」栏，
                    //   而这条路径是**同步、最早**执行的 ⇒ 后面 SainBotRegistration 写 "Bosses" 会被 SAIN 去重丢掉。
                    //   改成 "Bosses" 后「其他」栏问题从源头消失。
                    SetProperty(registration, "Section", "Bosses");
                    SetProperty(registration, "Description", DescriptionOf(role));
                    // 与预设里克隆的 bossKnight 保持一致（实测预设 DifficultyModifier=1.0；SAIN 只用它当 UI/默认值）
                    SetProperty(registration, "DifficultyModifier", 1.0f);

                    // ★ 2026-10-29【第 21 项】补 brain / layer 三字段（PPES 兼容做法）。
                    //   只在 sainBrains.enabled=true 时设；失败只 warning，绝不阻断注册。
                    if (BrainsEnabled)
                    {
                        // 只有灾厄冠军用"boss 版 brain"；小弟 + 第 11 项两个角色都走 follower 那套
                        // （克隆源已改为 bossKnight（首领档）；brain 仍按 follower/boss 两套赋值）
                        ApplyBrainFields(registration, role.Value == BossValue, DisplayKey(role), info, warning);
                        if (role.Value is CleanupCrewValue or PillagerValue or BloodhoundValue or GusValue or BasmachValue)
                            info?.Invoke($"[EventRotator][SAIN] '{role.DbKey}' 的 brain 取 follower 等价物："
                                       + $"BaseBrain={BaseBrainFollower} / BrainsToApply=[{string.Join(",", BrainsToApply)}]"
                                       + $" / LayersToRemove={LayersToRemove.Count} 层");
                    }

                    var task = method.Invoke(registry, new[] { registration, (object)CancellationToken.None }) as Task;
                    if (task == null)
                    {
                        warning?.Invoke($"[EventRotator][SAIN] ⚠ 注册 {role.DbKey} 没有返回 Task ⇒ 跳过该角色");
                        continue;
                    }
                    // SAIN 的 RegisterAsync 内部会 GenerateDefaults()（纯内存，毫秒级）+ 广播 WebSocket；
                    // 启动阶段没有 WS 客户端，给一个上限防止任何第三方实现卡死启动。
                    if (!task.Wait(TimeSpan.FromSeconds(15)))
                    {
                        warning?.Invoke($"[EventRotator][SAIN] ⚠ 注册 {role.DbKey} 超过 15s 未完成（继续启动）");
                    }
                    else if (task.IsFaulted)
                    {
                        warning?.Invoke($"[EventRotator][SAIN] ⚠ 注册 {role.DbKey} 失败：{task.Exception?.GetBaseException().Message}");
                    }
                    else
                    {
                        ok++;
                    }
                }
                catch (Exception ex)
                {
                    warning?.Invoke($"[EventRotator][SAIN] ⚠ 注册 {role.DbKey} 异常：{ex.GetBaseException().Message}");
                }
            }

            if (skippedDup > 0)
                info?.Invoke($"[EventRotator][SAIN] {skippedDup} 个角色此前已注册（由新路径写入 Section=Bosses）⇒ 本条旧路径跳过，不再重复注册");
            if (skippedScourge > 0)
                info?.Invoke($"[EventRotator][SAIN] {skippedScourge} 个灾厄角色（bossScourge/followerScourge）**不走旧路径** ⇒ 已跳过（由 Section=Bosses 注册 + ScourgeAi 三层驱动；避免与 SAIN 层抢同一个脑）");

            if (ok > 0)
            {
                info?.Invoke($"[EventRotator][SAIN] ✔ 已向 SAIN 注册自定义角色 {ok} 个（"
                    + string.Join(" / ", Roles.Select(r => $"{r.DbKey} {r.Value}")) + "）；"
                    + "SAIN 服务端 ComposeCustom 只保留 AllBotTypes 里的角色，不注册会把预设里的两个键过滤掉");
            }
        }
        catch (Exception ex)
        {
            warning?.Invoke($"[EventRotator][SAIN] 角色注册异常（已忽略，不影响启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SetProperty(object target, string name, object value)
    {
        if (target == null) return;
        var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanWrite) return;
        if (value != null && prop.PropertyType != value.GetType())
        {
            try { value = Convert.ChangeType(value, prop.PropertyType, CultureInfo.InvariantCulture); }
            catch { /* 类型对不上就保持原值，SAIN 侧有默认值 */ }
        }
        prop.SetValue(target, value);
    }

    // ═══════════════════ ★ 第 21 项：brain / layer（纯反射，失败只 warning）═══════════════════

    /// <summary>日志里用的角色名（跟 SAIN 注册的 DbKey 一致的大小写习惯）。</summary>
    private static string DisplayKey((int Value, string Name, string DbKey) role) => role.Value switch
    {
        BossValue => "bossScourge",
        FollowerValue => "followerScourge",
        CleanupCrewValue => "cleanupcrew",
        PillagerValue => "pillager",
        BloodhoundValue => "bloodhound",
        // ★ 第 9 项：与 LocaleInject 的 `ScavRole/Gus` / `ScavRole/Basmach`、以及 db 文件名一致（小写）
        GusValue => "gus",
        BasmachValue => "basmach",
        _ => role.DbKey,
    };

    /// <summary>
    /// 把三个字段写进 SAIN 的 registration 对象：
    /// <c>BaseBrain</c>（string）、<c>BrainsToApply</c> / <c>LayersToRemove</c>（字符串集合）。
    /// **属性不存在 / 类型不符 / 赋值异常 ⇒ 只 warning（写明属性名 + 期望类型 vs 实际类型），绝不抛**。
    /// 返回 true = 三个都设上了。
    /// </summary>
    private static bool ApplyBrainFields(object registration, bool isBoss, string displayKey,
                                         Action<string> info, Action<string> warning)
    {
        try
        {
            if (registration == null) return false;
            var brain = isBoss ? BaseBrainBoss : BaseBrainFollower;
            bool ok = SetStringProperty(registration, "BaseBrain", brain, displayKey, warning);
            ok &= SetCollectionProperty(registration, "BrainsToApply", BrainsToApply, displayKey, warning);
            ok &= SetCollectionProperty(registration, "LayersToRemove", LayersToRemove, displayKey, warning);
            if (ok)
                info?.Invoke($"[EventRotator][SAIN] ✔ 已为 {displayKey} 设置 brain={brain}，"
                    + $"applied=[{string.Join(",", BrainsToApply)}]，removed={LayersToRemove.Count} 层");
            return ok;
        }
        catch (Exception ex)
        {
            warning?.Invoke($"[EventRotator][SAIN] ⚠ {displayKey}：设置 brain/layer 异常（已忽略）：{ex.GetBaseException().Message}");
            return false;
        }
    }

    private static PropertyInfo FindWritable(object target, string name, string displayKey, Action<string> warning)
    {
        var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite && prop.GetSetMethod(false) != null) return prop;
        warning?.Invoke($"[EventRotator][SAIN] ⚠ {displayKey}：SAIN 的 {target.GetType().FullName} 上没有可写属性 {name}"
            + "（SAIN 版本可能变了）⇒ 跳过该字段");
        return null;
    }

    private static bool SetStringProperty(object target, string name, string value,
                                          string displayKey, Action<string> warning)
    {
        var prop = FindWritable(target, name, displayKey, warning);
        if (prop == null) return false;
        if (prop.PropertyType != typeof(string))
        {
            warning?.Invoke($"[EventRotator][SAIN] ⚠ {displayKey}：{name} 类型不符 —— 期望 System.String，"
                + $"实际 {prop.PropertyType.FullName} ⇒ 跳过该字段");
            return false;
        }
        try { prop.SetValue(target, value); return true; }
        catch (Exception ex)
        {
            warning?.Invoke($"[EventRotator][SAIN] ⚠ {displayKey}：设置 {name}=\"{value}\" 失败：{ex.GetBaseException().Message}");
            return false;
        }
    }

    private static bool SetCollectionProperty(object target, string name, List<string> values,
                                              string displayKey, Action<string> warning)
    {
        var prop = FindWritable(target, name, displayKey, warning);
        if (prop == null) return false;
        // ★ 按属性类型构造对应集合（本机实测是 List<string>；也兼容数组 / IReadOnlyCollection 等）
        var built = BuildStringCollection(prop.PropertyType, values);
        if (built == null)
        {
            warning?.Invoke($"[EventRotator][SAIN] ⚠ {displayKey}：{name} 类型不符 —— 期望字符串集合"
                + $"（List<string>/string[]/IReadOnlyCollection<string>…），实际 {prop.PropertyType.FullName} ⇒ 跳过该字段");
            return false;
        }
        try { prop.SetValue(target, built); return true; }
        catch (Exception ex)
        {
            warning?.Invoke($"[EventRotator][SAIN] ⚠ {displayKey}：设置 {name} 失败：{ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>按目标属性类型把字符串列表变成能直接赋值的对象；不支持的类型返回 null。</summary>
    private static object BuildStringCollection(Type propertyType, List<string> values)
    {
        if (propertyType == null) return null;
        var arr = (values ?? new List<string>()).ToArray();
        if (propertyType == typeof(string[])) return arr;
        // List<string> 本身就把下面这些接口都实现了：直接传它即可
        if (propertyType == typeof(List<string>)
            || propertyType == typeof(IList<string>) || propertyType == typeof(ICollection<string>)
            || propertyType == typeof(IEnumerable<string>)
            || propertyType == typeof(IReadOnlyList<string>) || propertyType == typeof(IReadOnlyCollection<string>))
            return new List<string>(arr);
        if (propertyType == typeof(HashSet<string>) || propertyType == typeof(ISet<string>))
            return new HashSet<string>(arr, StringComparer.OrdinalIgnoreCase);
        // 兜底：任何"能接受 List<string>"的集合/接口（例如将来改成 ReadOnlyCollection 接口）
        if (propertyType.IsAssignableFrom(typeof(List<string>))) return new List<string>(arr);
        return null;
    }

    private static Type FindLoadedType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch
            {
                // 个别动态程序集 GetType 会抛，忽略
            }
        }
        return null;
    }

    // ═══════════════════════════ ③ 杂项：路径 / 开关 ═══════════════════════════

    /// <summary>
    /// 从 mod 目录（<c>&lt;SPT_Runtime&gt;/user/mods/SPT-EventRotator</c>）逐级向上找 SPT_Runtime。
    /// 判据：目录里有 <c>SPTarkov.Server.Core.dll</c>（实机）或 <c>SPT_Data</c>（离线/测试）。
    /// 找不到再试当前工作目录；仍没有 ⇒ null。
    /// </summary>
    private static string FindRuntime(string modPath)
    {
        try
        {
            DirectoryInfo dir = null;
            if (!string.IsNullOrWhiteSpace(modPath))
            {
                dir = new DirectoryInfo(Path.GetFullPath(modPath));
            }
            while (dir != null)
            {
                if (LooksLikeRuntime(dir.FullName)) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // 路径非法 ⇒ 落到下面的当前目录兜底
        }

        try
        {
            var cwd = Environment.CurrentDirectory;
            if (LooksLikeRuntime(cwd)) return cwd;
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static bool LooksLikeRuntime(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "SPTarkov.Server.Core.dll"))
                   || Directory.Exists(Path.Combine(dir, "SPT_Data"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读 config.json 的 <c>sainPresetInject</c>；缺文件/缺键/解析失败都按默认 true。</summary>
    private static bool ReadEnabled(string modPath)
    {
        try
        {
            var cfg = Path.Combine(modPath ?? "", "config.json");
            if (!File.Exists(cfg)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(ConfigKey, out var flag)
                && (flag.ValueKind == JsonValueKind.False || flag.ValueKind == JsonValueKind.True))
            {
                return flag.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"[EventRotator][SAIN] ⚠ 读 config.json 的 {ConfigKey} 失败（按默认 true 处理）：{ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// 读 config.json 的 <c>sainBrains</c>（第 21 项）。**缺文件 / 缺这段 / 解析失败 ⇒ 用内置默认**
    /// （= 与写了一份 sainBrains 完全等价），所以老配置不需要动。
    /// <para>数组既接受 JSON 数组，也接受 ConfigWeb 那边登记的**逗号分隔字符串**（`"ExUsec,PMC"`）。</para>
    /// </summary>
    private static void ReadSainBrains(string modPath)
    {
        // 先复位（热重载 / 多次调用时不残留上一份）
        BrainsEnabled = true;
        BaseBrainBoss = "ExUsec";
        BaseBrainFollower = "ExUsec";
        BrainsToApply = new List<string> { "ExUsec", "PMC" };
        LayersToRemove = new List<string> { "Request", "KnightFight", "PmcBear", "PmcUsec", "ExURequest", "StationaryWS" };
        try
        {
            var cfg = Path.Combine(modPath ?? "", "config.json");
            if (!File.Exists(cfg)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty(BrainsConfigKey, out var sb) || sb.ValueKind != JsonValueKind.Object) return;

            if (sb.TryGetProperty("enabled", out var en)
                && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                BrainsEnabled = en.GetBoolean();
            BaseBrainBoss = ReadStr(sb, "baseBrainBoss", BaseBrainBoss);
            BaseBrainFollower = ReadStr(sb, "baseBrainFollower", BaseBrainFollower);
            BrainsToApply = ReadStrList(sb, "brainsToApply", BrainsToApply);
            LayersToRemove = ReadStrList(sb, "layersToRemove", LayersToRemove);
            Info?.Invoke($"[EventRotator][SAIN] sainBrains 配置：enabled={BrainsEnabled}"
                + $"，boss={BaseBrainBoss}，follower={BaseBrainFollower}"
                + $"，applied=[{string.Join(",", BrainsToApply)}]，removed={LayersToRemove.Count} 层");
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"[EventRotator][SAIN] ⚠ 读 config.json 的 {BrainsConfigKey} 失败（用内置默认值）：{ex.Message}");
        }
    }

    private static string ReadStr(JsonElement obj, string key, string def)
    {
        try
        {
            if (obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = (el.GetString() ?? "").Trim();
                if (s.Length > 0) return s;
            }
        }
        catch { }
        return def;
    }

    /// <summary>读字符串列表：JSON 数组 或 逗号/分号/换行分隔的字符串；读不到或为空 ⇒ 用默认。</summary>
    private static List<string> ReadStrList(JsonElement obj, string key, List<string> def)
    {
        var res = new List<string>();
        try
        {
            if (!obj.TryGetProperty(key, out var el)) return def;
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in el.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) AddUnique(res, x.GetString());
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                foreach (var part in (el.GetString() ?? "").Split(new[] { ',', ';', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    AddUnique(res, part);
            }
        }
        catch { }
        // 空 ⇒ 用默认（避免把 SAIN 必需的 brain 清空后 AI 直接不生成）
        return res.Count > 0 ? res : def;
    }

    private static void AddUnique(List<string> list, string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length > 0 && !list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
    }

    // ═══════════════════════════ ④ JSON 原始字节定位 ═══════════════════════════

    /// <summary>在 <paramref name="container"/> 对象里找属性，返回该属性名起点 + 值区间 [valueStart, valueEnd)。</summary>
    private static bool TryLocateInContainer(byte[] utf8, string container, string property,
        out int keyStart, out int valueStart, out int valueEnd)
    {
        keyStart = valueStart = valueEnd = -1;
        var reader = new Utf8JsonReader(utf8);
        string lastAtDepth1 = null;
        var inside = -1;   // 容器对象自身属性所在的 CurrentDepth（根对象深度 1 ⇒ 子对象属性深度 2）
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (inside >= 0 && reader.CurrentDepth == inside)
                    {
                        if (string.Equals(reader.GetString(), property, StringComparison.Ordinal))
                        {
                            keyStart = (int)reader.TokenStartIndex;
                            if (!reader.Read()) return false;
                            valueStart = (int)reader.TokenStartIndex;
                            reader.Skip();
                            valueEnd = (int)reader.BytesConsumed;
                            return true;
                        }
                    }
                    else if (inside < 0 && reader.CurrentDepth == 1)
                    {
                        lastAtDepth1 = reader.GetString();
                    }
                    break;

                case JsonTokenType.StartObject:
                    if (inside < 0 && reader.CurrentDepth == 1
                        && string.Equals(lastAtDepth1, container, StringComparison.Ordinal))
                    {
                        inside = 2;
                    }
                    break;

                case JsonTokenType.EndObject:
                    // 容器自身的结束符深度 = 1（容器内条目对象的结束符深度 = 2，不会误判）
                    if (inside >= 0 && reader.CurrentDepth == 1) return false;
                    break;
            }
        }
        return false;
    }

    /// <summary>把 <paramref name="utf8"/> 当作一个独立对象，找它的顶层属性（属性深度 1）。</summary>
    private static bool TryLocateTopLevel(byte[] utf8, string property,
        out int keyStart, out int valueStart, out int valueEnd)
    {
        keyStart = valueStart = valueEnd = -1;
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName
                && reader.CurrentDepth == 1
                && string.Equals(reader.GetString(), property, StringComparison.Ordinal))
            {
                keyStart = (int)reader.TokenStartIndex;
                if (!reader.Read()) return false;
                valueStart = (int)reader.TokenStartIndex;
                reader.Skip();
                valueEnd = (int)reader.BytesConsumed;
                return true;
            }
        }
        return false;
    }
}
