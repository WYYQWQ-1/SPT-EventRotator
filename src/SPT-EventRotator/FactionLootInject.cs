using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;

namespace EventRotator;

/// <summary>
/// ★★★ 2026-10-30【第 12 项】"事件限定物品"的获取途径 —— 往走私者（`arenaFighterEvent`）背包里塞任务物品。
///
/// <para><b>为什么需要</b>：本机有 3 件任务物品（锁住的箱子 `6740987b89d5e1ddc603f4f0` /
/// 箱子钥匙 `67449b6c89d5e1ddc603f504` / 违禁品盒子 `67408903268737ef6908d432`）**原版任务引用 = 0**，
/// 玩家平时拿不到；而 `questlines/tarkov_mystery.json` 的【贸易禁令】及其「最后 chance」两条任务
/// 各自引用它们 8 处 ⇒ 不解决就必然卡死。</para>
///
/// <para><b>做法（照抄 PPES `Quests/EventQuests.cs:129-158` 的语义）</b>：
/// 打开目标 bot 的装备概率里 `Backpack = 100`（克隆源 Rogue 经常没背包，而货只往背包放），
/// 再把每件物品按 <c>AddShare</c> 公式换算成权重写进 <c>BotType.BotInventory.Items.Backpack</c>
/// （`Dictionary&lt;MongoId, double&gt;`，已用真 BotType 反射确认）：
/// <code>others = 其它条目权重之和；pool[tpl] = Math.Max(1, Math.Round(others * share / (1 - share)))</code></para>
///
/// <para><b>落点为什么不改 Plugin.cs</b>：任务门控那条线正在改 `Plugin.cs` ⇒ 本维度做成**自包含**：
/// 在模块初始化时给 <c>BotHelper.GetBotTemplate(string)</c> 挂 postfix，**在 bot 生成取模板的那一刻**
/// 按 `EventEngine.Actives` 现读现改（只有声明了 `factionLoot` 的角色才会走到改写逻辑，
/// 其它 bot 一次字符串比较就返回）。活动集合/权重变化时先**逐字还原**旧快照再写新值 ⇒ 平台期多次生成不会翻倍。</para>
///
/// <para><b>快照/还原</b>：改写前把整张 `Backpack` 字典（每个 tpl + 原权重）与
/// `BotChances.EquipmentChances["Backpack"]` 的**存在性 + 原值**存进 <see cref="Applied"/>；
/// 还原时 `Clear()` 后逐条写回、`EquipmentChances` 按"原本没有就删键"还原。</para>
///
/// <para><b>测试开关</b>：`config.json` 顶层 <c>factionLootTestGuarantee: true</c>（**不进 events.json**，默认 false）
/// ⇒ 所有 share 一律按 0.99 算（≈必出），并打一行醒目警告。</para>
/// </summary>
internal static class FactionLootInject
{
    private const string Tag = "[背包注入]";
    private const string HarmonyId = "com.kirisame.eventrotator.factionloot";

    /// <summary>一个角色当前被改写的内容 + 还原所需的快照。</summary>
    internal sealed class Applied
    {
        public string Signature = "";
        public object BotType;
        public List<KeyValuePair<object, double>> Backpack = new();
        public bool HadChance;
        public double Chance;
    }

    private static readonly Dictionary<string, Applied> _applied = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _recent = new();
    private static readonly object _gate = new();

    private static bool _hooked;
    private static MethodBase _patchedTarget;

    /// <summary>日志出口（由 <see cref="Hook"/> 的调用方注入 SPT logger；探针可传 Console）。</summary>
    public static Action<string> Log;

    // ★ 以下两个仅供离线探针造失败分支（生产环境保持 null，不走这些路）
    internal static Func<string, Type> ResolveForTest;
    internal static MethodBase TargetForTest;
    private static HashSet<string> _roleCache = new(StringComparer.OrdinalIgnoreCase);
    private static int _roleCacheCount = -1;

    private static bool? _testGuarantee;
    private static string _cfgPath;
    private static DateTime _cfgMtime;

    /// <summary>最近 200 行本维度日志（离线探针断言用）。</summary>
    internal static IReadOnlyList<string> RecentLogs { get { lock (_gate) return _recent.ToArray(); } }

    /// <summary>日志：走 SPT logger（由 Hook 的调用方注入；探针可传 Console）。**不再写 Console**。</summary>
    private static void Emit(string m)
    {
        lock (_gate)
        {
            _recent.Add(m);
            if (_recent.Count > 200) _recent.RemoveAt(0);
        }
        try { Log?.Invoke(m); } catch { }
    }

    private static void Info(string m) => Emit(Tag + " " + m);
    private static void Warn(string m) => Emit(Tag + " ⚠ " + m);

    private static void EmitOnce(string key, string m)
    {
        lock (_gate) { if (!_warned.Add(key)) return; }
        Emit(m);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // 自举：模块加载即挂 postfix（不依赖 Plugin.cs；任务门控那条线在改它）
    // ══════════════════════════════════════════════════════════════════════════════════════
    // ══════════════════════════════════════════════════════════════════════════════════════
    // ★★★ 2026-10-30 重做（P1-1）：**确定性**挂到 RuntimeTypes.LiveCoreAssembly 的 BotHelper 上
    //   旧实现用 [ModuleInitializer]（随程序集加载就跑）—— 那时 RuntimeTypes.Init 还没执行
    //   （Plugin.cs 里 Init 在 OnLoadAsync 内），Find 会退到 ALC/AccessTools 兜底 ⇒ 可能挂到
    //   Default ALC 的**死副本**：补丁表里有、实机永不触发（双载探针实证：A=1 / B=0）。
    //   现在：由 Plugin 在 `RuntimeTypes.Init(...)` **之后**调用 <see cref="Hook"/>（一行）；
    //   挂完立刻**自证**（patch 表里有我们 + target 的程序集 == LiveCoreAssembly）才算成功；
    //   失败只 warn、`_hooked` 保持 false（下次还能重试），且日志按步骤区分（解析 / 取方法 / 挂载 / 自证）。
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 挂 postfix（**必须**在 `RuntimeTypes.Init(...)` 之后调用）。
    /// 幂等：已挂在运行时那份上 ⇒ 直接返回；挂了但不是运行时那份 ⇒ Unpatch 后重挂（兜底）。
    /// </summary>
    /// <param name="log">日志出口（Plugin 传 <c>m =&gt; logger.Info(m)</c>；不传则沿用上次的，探针也能传 Console）。</param>
    public static void Hook(Action<string> log = null)
    {
        if (log != null) Log = log;

        var live = RuntimeTypes.LiveCoreAssembly;
        // 兜底：已 hooked 但目标不是运行时那份（极端：Init 前后被换过）⇒ Unpatch 重挂
        if (_hooked)
        {
            var cur = _patchedTarget;
            if (cur != null && live != null && ReferenceEquals(cur.DeclaringType?.Assembly, live)) return;   // 幂等
            Warn($"已挂补丁的目标不是当前运行时 Core（已挂={DescribeAsm(cur?.DeclaringType?.Assembly)}｜"
               + $"运行时={DescribeAsm(live)}）⇒ 先 Unpatch 再重挂");
            TryUnpatch(cur);
            _hooked = false;
            _patchedTarget = null;
        }

        // ① 解析类型（ResolveForTest 仅供离线探针造"找不到类型"这一支）
        Type helper;
        try
        {
            helper = ResolveForTest != null
                ? ResolveForTest("SPTarkov.Server.Core.Helpers.Bot.BotHelper")
                : RuntimeTypes.Find("SPTarkov.Server.Core.Helpers.Bot.BotHelper");
        }
        catch (Exception ex)
        {
            Warn($"① 解析 BotHelper 抛异常（下次会重试）：{ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (helper == null)
        {
            Warn("① 找不到 BotHelper 类型 ⇒ 背包注入不生效（下次进图重试）");
            return;
        }

        // ② 取方法（TargetForTest 仅供离线探针造"取方法/挂载"失败）
        MethodBase target;
        try
        {
            target = TargetForTest
                     ?? helper.GetMethod("GetBotTemplate", new[] { typeof(string) })
                     ?? helper.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                              .FirstOrDefault(x => x.Name == "GetBotTemplate" && x.GetParameters().Length == 1);
        }
        catch (Exception ex)
        {
            Warn($"② 取 BotHelper.GetBotTemplate 抛异常（下次会重试）：{ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (target == null)
        {
            Warn($"② {helper.FullName} 上找不到 GetBotTemplate(string) ⇒ 背包注入不生效（下次重试）");
            return;
        }

        // ③ 挂载
        try
        {
            var postfix = typeof(FactionLootInject).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception ex)
        {
            Warn($"③ 挂载 Harmony postfix 抛异常（下次会重试）：{ex.GetType().Name}: {ex.Message}");
            TryUnpatch(target);
            return;
        }

        // ④ 自证：patch 表里有我们 + 挂在运行时那份上，两个条件缺一不可
        var info = Harmony.GetPatchInfo(target);
        bool ours = info != null && info.Postfixes != null
                    && info.Postfixes.Any(p => string.Equals(p.owner, HarmonyId, StringComparison.Ordinal));
        var targetAsm = target.DeclaringType?.Assembly;
        bool sameAsm = live != null && ReferenceEquals(targetAsm, live);
        if (!ours)
        {
            Warn($"④ 自证失败：patch 表里找不到我们的 postfix（下次会重试）");
            TryUnpatch(target);
            return;
        }
        if (!sameAsm)
        {
            Warn($"④ 自证失败：补丁挂在 {DescribeAsm(targetAsm)}，运行时 Core 是 {DescribeAsm(live)} ⇒ 已撤销（下次重试）");
            TryUnpatch(target);
            return;
        }

        _hooked = true;
        _patchedTarget = target;
        Info($"已挂 BotHelper.GetBotTemplate postfix（{target.DeclaringType?.FullName}｜"
           + $"程序集={DescribeAsm(targetAsm)}｜与运行时 Core 同一份=True｜patchId={HarmonyId}）");
    }

    /// <summary>是否已成功挂到运行时那份上（自检/探针用）。</summary>
    public static bool IsHooked => _hooked;

    private static void TryUnpatch(MethodBase target)
    {
        if (target == null) return;
        try { new Harmony(HarmonyId).Unpatch(target, HarmonyPatchType.Postfix, HarmonyId); }
        catch (Exception ex) { Warn($"撤销补丁失败（已忽略）：{ex.GetType().Name}: {ex.Message}"); }
    }

    private static string DescribeAsm(Assembly a)
        => a == null ? "(null)" : $"{a.GetName().Name} v{a.GetName().Version} ALC="
            + (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a)?.Name ?? "(default)");


    private static void Postfix(string __0, object __result)
    {
        try { OnTemplate(__0, __result); }
        catch (Exception ex) { EmitOnce("post:" + ex.GetType().Name, $"{Tag} ⚠ 生成期注入异常（已忽略，不影响出图）：{ex.Message}"); }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // 生成期入口
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>角色在 events.json 的任意活动里出现过 `factionLoot` 才算"感兴趣"（其它 bot 一次比较就返回）。</summary>
    internal static HashSet<string> RolesOfInterest()
    {
        lock (_gate)
        {
            if (_roleCacheCount == EventEngine.All.Count) return _roleCache;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in EventEngine.All)
            {
                if (e?.FactionLoot == null) continue;
                foreach (var k in e.FactionLoot.Keys)
                    if (!string.IsNullOrWhiteSpace(k)) set.Add(k);
            }
            _roleCache = set;
            _roleCacheCount = EventEngine.All.Count;
            return set;
        }
    }

    /// <summary>当前**在跑**的活动里，给这个角色声明的 spec（第一个命中说了算）；没有 ⇒ null。</summary>
    internal static FactionLootDef CurrentSpec(string role, out string eventId)
    {
        eventId = null;
        foreach (var e in EventEngine.Actives)
        {
            if (e?.FactionLoot == null) continue;
            foreach (var kv in e.FactionLoot)
            {
                if (!string.Equals(kv.Key, role, StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Value == null) continue;
                eventId = e.Id;
                return kv.Value;
            }
        }
        return null;
    }

    internal static void OnTemplate(string role, object botType)
    {
        if (string.IsNullOrEmpty(role) || botType == null) return;
        if (!RolesOfInterest().Contains(role)) return;

        var spec = CurrentSpec(role, out var eventId);
        // ★ P2：配置读取（含文件 stat/读）放在锁**外**，别让 bot 生成线程在状态锁上等磁盘 IO
        var test = ReadTestGuarantee();
        lock (_gate)
        {
            if (spec == null)
            {
                if (_applied.ContainsKey(role)) RestoreLocked(role);
                return;
            }
            var sig = Signature(role, spec, test);
            if (_applied.TryGetValue(role, out var cur) && cur.Signature == sig) return;   // ★ 幂等
            if (_applied.ContainsKey(role)) RestoreLocked(role);                          // 状态变了先还原
            ApplyLocked(eventId ?? role, role, botType, spec, test, sig);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // 核心：快照 + 写入 / 还原
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>离线探针可直接调：对给定 BotType 套用一次 spec（返回真正写入的物品数）。</summary>
    internal static int ApplySpec(string eventId, string role, object botType, FactionLootDef spec, bool testGuarantee)
    {
        lock (_gate)
        {
            if (_applied.ContainsKey(role)) RestoreLocked(role);
            var test = testGuarantee;
            return ApplyLocked(eventId ?? role, role, botType, spec, test, Signature(role, spec, test));
        }
    }

    private static int ApplyLocked(string eventId, string role, object botType, FactionLootDef spec, bool testGuarantee, string sig)
    {
        var backpack = BackpackOf(botType);
        if (backpack == null)
        {
            EmitOnce("nobp:" + role, $"{Tag} ⚠ '{role}' 拿不到 BotInventory.Items.Backpack ⇒ 跳过（不影响出图）");
            return 0;
        }

        var snap = new Applied { Signature = sig, BotType = botType };
        foreach (DictionaryEntry de in backpack)
        {
            try { snap.Backpack.Add(new KeyValuePair<object, double>(de.Key, Convert.ToDouble(de.Value, CultureInfo.InvariantCulture))); }
            catch { }
        }
        var chances = EquipmentChancesOf(botType);
        if (chances != null && HasKey(chances, "Backpack"))
        {
            snap.HadChance = true;
            snap.Chance = Convert.ToDouble(chances[FindKey(chances, "Backpack")], CultureInfo.InvariantCulture);
        }

        var added = new List<string>();
        var items = (spec?.Items ?? new Dictionary<string, double>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).ToList();

        if (testGuarantee)
        {
            // ★★★ 2026-10-30【测试模式修正】不再用 share=0.99 走 PPES 顺序公式 ——
            //   顺序累计会让最后一项比前两项大两个数量级（实机实测：背包里只出「箱子钥匙」，
            //   锁住的箱子 / 违禁品盒子一件都不出）。
            //   改为：先算「原始池（快照）权重总和」⇒ W = 总和 × 20，三项**一次性**写同一个 W
            //   ⇒ 顺序无关、三者等概率，且 W 远大于池子总和 ⇒ 近似必出。
            double poolSum = 0;
            foreach (var kv0 in snap.Backpack) poolSum += kv0.Value;
            var w = Math.Round(poolSum * 20d);
            if (w < 1) w = 1;
            foreach (var kv in items)
            {
                var tpl = kv.Key.Trim();
                var key = FindKey(backpack, tpl) ?? NewKey(backpack, tpl);
                backpack[key] = w;
                added.Add($"{tpl} 等权重 {w.ToString("0", CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            foreach (var kv in items)
            {
                var tpl = kv.Key.Trim();
                var share = kv.Value;
                if (share <= 0 || share >= 1)
                {
                    EmitOnce("share:" + tpl, $"{Tag} ⚠ share 必须在 (0,1) 内：'{tpl}' = {share.ToString(CultureInfo.InvariantCulture)} ⇒ 跳过");
                    continue;
                }

                double others = 0;
                foreach (DictionaryEntry de in backpack)
                {
                    if (KeyIs(de.Key, tpl)) continue;
                    try { others += Convert.ToDouble(de.Value, CultureInfo.InvariantCulture); } catch { }
                }
                // ★ 正式分支：逐字照抄 PPES AddShare（顺序累计，一个字节都不动）
                var weight = Math.Max(1d, Math.Round(others * share / (1d - share)));
                var key = FindKey(backpack, tpl) ?? NewKey(backpack, tpl);
                backpack[key] = weight;
                added.Add($"{tpl} share={share.ToString("0.##", CultureInfo.InvariantCulture)} → 权重 {weight.ToString("0", CultureInfo.InvariantCulture)}");
            }
        }

        if (spec?.EnsureBackpack == true && chances != null)
        {
            chances[FindKey(chances, "Backpack") ?? "Backpack"] = 100d;
            added.Add("EquipmentChances[Backpack] = 100");
        }

        _applied[role] = snap;
        if (added.Count > 0)
            Emit($"{Tag} {eventId}：'{role}' 背包注入 {added.Count} 项（{string.Join("；", added)}）");
        if (testGuarantee && items.Count > 0)
        {
            var sum0 = 0d;
            foreach (var kv0 in snap.Backpack) sum0 += kv0.Value;
            var w0 = Math.Round(sum0 * 20d); if (w0 < 1) w0 = 1;
            Emit($"{Tag} ⚠ 测试模式：{items.Count} 件物品等权重 W={w0.ToString("0", CultureInfo.InvariantCulture)}（≈必出、等概率），验证完请关掉");
        }
        return added.Count;
    }

    /// <summary>还原所有已改写角色（探针用；运行期按角色在 <see cref="OnTemplate"/> 里按需还原）。</summary>
    internal static void RestoreAll()
    {
        lock (_gate)
        {
            foreach (var role in _applied.Keys.ToList()) RestoreLocked(role);
        }
    }

    /// <summary>还原单个角色：`Backpack` 与 `EquipmentChances` 与套用前**逐字相同**。</summary>
    private static void RestoreLocked(string role)
    {
        if (!_applied.TryGetValue(role, out var snap)) return;
        _applied.Remove(role);
        try
        {
            var backpack = BackpackOf(snap.BotType);
            if (backpack != null)
            {
                backpack.Clear();
                foreach (var kv in snap.Backpack) backpack[kv.Key] = kv.Value;
            }
            var chances = EquipmentChancesOf(snap.BotType);
            if (chances != null)
            {
                if (snap.HadChance) chances[FindKey(chances, "Backpack") ?? "Backpack"] = snap.Chance;
                else
                {
                    var k = FindKey(chances, "Backpack");
                    if (k != null) chances.Remove(k);
                }
            }
            Emit($"{Tag} 已还原 '{role}' 的背包（{snap.Backpack.Count} 项）与背包概率");
        }
        catch (Exception ex)
        {
            Emit($"{Tag} ⚠ 还原 '{role}' 失败（已忽略）：{ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // 测试开关（config.json 顶层字段，不进 events.json）
    // ══════════════════════════════════════════════════════════════════════════════════════
    internal static bool ReadTestGuarantee()
    {
        try
        {
            var dir = EventEngine.ModPath;
            if (string.IsNullOrEmpty(dir)) return false;
            var p = Path.Combine(dir, "config.json");
            var mtime = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
            if (CachedTestGuarantee(p, mtime, out var cached)) return cached;   // 只有命中缓存时才拿锁
            bool on = false;
            if (File.Exists(p))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p), new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("factionLootTestGuarantee", out var v))
                {
                    on = v.ValueKind == JsonValueKind.True
                         || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
                }
            }
            lock (_gate) { _testGuarantee = on; _cfgPath = p; _cfgMtime = mtime; }
            return on;
        }
        catch (Exception ex)
        {
            EmitOnce("cfg:" + ex.GetType().Name, $"{Tag} ⚠ 读 config.json 的 factionLootTestGuarantee 失败（按 false 处理）：{ex.Message}");
            return false;
        }
    }

    /// <summary>只读缓存命中（短锁，不碰磁盘）。</summary>
    private static bool CachedTestGuarantee(string path, DateTime mtime, out bool value)
    {
        lock (_gate)
        {
            if (_testGuarantee.HasValue && _cfgPath == path && _cfgMtime == mtime)
            {
                value = _testGuarantee.Value;
                return true;
            }
        }
        value = false;
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // 反射小工具
    // ══════════════════════════════════════════════════════════════════════════════════════
    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return p?.GetValue(o);
        }
        catch { return null; }
    }

    private static IDictionary BackpackOf(object botType)
        => Prop(Prop(Prop(botType, "BotInventory"), "Items"), "Backpack") as IDictionary;

    private static IDictionary EquipmentChancesOf(object botType)
        => Prop(Prop(botType, "BotChances"), "EquipmentChances") as IDictionary;

    private static string KeyText(object key)
    {
        try { return key?.ToString() ?? ""; } catch { return ""; }
    }

    private static bool KeyIs(object key, string tpl)
        => string.Equals(KeyText(key), tpl, StringComparison.OrdinalIgnoreCase);

    private static bool HasKey(IDictionary d, string key) => FindKey(d, key) != null;

    private static object FindKey(IDictionary d, string key)
    {
        try
        {
            foreach (var k in d.Keys)
                if (KeyIs(k, key)) return k;
        }
        catch { }
        return null;
    }

    /// <summary>按目标字典的 Key 类型造一个新键（`Dictionary&lt;MongoId,double&gt;` ⇒ `new MongoId(tpl)`）。</summary>
    private static object NewKey(IDictionary d, string tpl)
    {
        var kt = d.GetType().GetGenericArguments().FirstOrDefault() ?? typeof(string);
        if (kt == typeof(string)) return tpl;
        try
        {
            var ctor = kt.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return ctor.Invoke(new object[] { tpl });
        }
        catch { }
        try { return Activator.CreateInstance(kt); } catch { return tpl; }
    }

    private static string Signature(string role, FactionLootDef spec, bool testGuarantee)
    {
        var items = (spec?.Items ?? new Dictionary<string, double>())
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key + "=" + kv.Value.ToString("R", CultureInfo.InvariantCulture));
        return role + "|" + (spec?.EnsureBackpack == true ? "bp1" : "bp0") + "|" + (testGuarantee ? "T" : "F")
             + "|" + string.Join(",", items);
    }
}
