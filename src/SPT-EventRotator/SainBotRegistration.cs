using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EventRotator;

/// <summary>
/// ★ 2026-10-31【服务端 SAIN 分类注册】把本 mod 的 7 个自定义角色注册进 SAIN 服务端，
/// 让网页版「预设编辑器」也把它们显示在 **Bosses** 分类下（BlackDiv / ISB 同款做法）。
///
/// <para><b>真实 API（SAIN 4.1.6 ServerMod 元数据实证）</b>：
/// <code>
/// SAIN.ServerInterop.ISainBotTypeRegistry
///     Task RegisterAsync(SainBotTypeRegistration registration, CancellationToken cancellationToken)
/// SAIN.ServerInterop.SainBotTypeRegistration
///     Name / WildSpawnType(int) / BotDbKey / DifficultyModifier(float) / Section /
///     Description / BrainsToApply(List&lt;string&gt;) / LayersToRemove(List&lt;string&gt;) / BaseBrain
/// </code>
/// 实现方 = `SAINServerMod.Generators.PresetGenerationService`（同时实现 `IOnLoad`），
/// 它的 `RegisterAsync` 会把条目追加进 `_registered`，而网页用的 `AllBotTypes()` = 本体 45 条 + `_registered`。
/// </para>
///
/// <para><b>模板</b>：运行时从 `AllBotTypes()` 里找 `bossKnight`（SAIN 表里 Name="Knight"、WildSpawnType=26）那条，
/// 克隆它的 `BrainsToApply / LayersToRemove / BaseBrain / DefaultDifficultyModifier` —— 不硬编码字符串表。
/// 实测该条在 SAIN 服务端的这三个字段本来就是 <c>null</c>（本体 45 条全都如此，SAIN 自己按默认处理），
/// 所以注册值同样是 null，和本体条目完全同构。</para>
///
/// <para><b>时序</b>：SAIN 可能比我们后加载 ⇒ 后台每 500ms 重试（最多 120s），直到 DI 里能拿到
/// `ISainBotTypeRegistry` 且 `PresetGenerationService._defaults` 已初始化；失败只 warn，不影响其它功能。</para>
/// </summary>
internal static class SainBotRegistration
{
    private const string Tag = "[SAIN]";
    private const string RegistryTypeName = "SAIN.ServerInterop.ISainBotTypeRegistry";
    private const string PresetTypeName = "SAINServerMod.Generators.PresetGenerationService";
    private const string RegistrationTypeName = "SAIN.ServerInterop.SainBotTypeRegistration";

    /// <summary>7 个角色的 SAIN 登记信息（Name 前缀避免和原版撞名；模板统一 bossKnight）。</summary>
    private static readonly (string DbKey, int WildSpawnType, string DisplayName, string Description)[] Roles =
    {
        ("bossScourge",    13790, "EventRotator bossScourge",    "EventRotator boss: Scourge (Lab event leader). Template: bossKnight."),
        ("followerScourge",13791, "EventRotator followerScourge","EventRotator follower: Scourge's guard. Template: bossKnight."),
        ("cleanupcrew",    13792, "EventRotator cleanupcrew",    "EventRotator faction: Cleanup Crew. Template: bossKnight."),
        ("pillager",       13793, "EventRotator pillager",       "EventRotator faction: Pillager. Template: bossKnight."),
        ("bloodhound",     13794, "EventRotator bloodhound",     "EventRotator faction: Bloodhound. Template: bossKnight."),
        ("gus",            13795, "EventRotator gus",            "EventRotator arms dealer: Gus. Template: bossKnight."),
        ("basmach",        13796, "EventRotator basmach",        "EventRotator arms dealer: Basmach. Template: bossKnight."),
    };

    public static Action<string> Info;
    public static Action<string> Warn;
    private static IServiceProvider _services;
    private static readonly object _gate = new();
    private static bool _started;
    private static bool _done;
    private static bool _sectionLogged;
    private static bool _logged;
    private static bool _skipLogged;
    private static bool _templateLogged;
    private static bool _warnLogged;

    // 探针 seams（生产为 null）
    internal static Func<string, Type> FindTypeForTest;
    internal static Func<Type, object> ResolveForTest;

    public static void Init(IServiceProvider services, Action<string> info, Action<string> warn)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        _services = services;
        Info = info;
        Warn = warn;

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 240 && !_done; attempt++)
            {
                try
                {
                    if (TryRegisterOnce()) return;
                }
                catch (Exception)
                {
                    // 静默重试：只在超时/状态变化时打日志，避免每 500ms 刷屏
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            if (!_done)
                Warn?.Invoke($"{Tag} ⚠ 等待 SAIN 服务端超时（120s）⇒ 本次未注册自定义角色（不影响其它功能）");
        });
    }

    /// <summary>探测一次：SAIN 未就绪返回 false；就绪则注册缺失角色并返回 true。</summary>
    private static bool TryRegisterOnce()
    {
        var ifaceType = FindType(RegistryTypeName);
        var presetType = FindType(PresetTypeName);
        if (ifaceType == null || presetType == null) return false;

        object registry = null;
        if (ResolveForTest != null) registry = ResolveForTest(ifaceType);
        if (registry == null) registry = SafeGetService(ifaceType);
        if (registry == null) registry = SafeGetService(presetType);
        if (registry == null) return false;

        // 等 SAIN 自己的 OnLoad 把 _defaults 建好（否则 RegisterAsync 里的 GenerateDefaults 可能是空跑/异常）
        var defaultsField = registry.GetType().GetField("_defaults", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (defaultsField != null && defaultsField.GetValue(registry) == null) return false;

        var allMethod = registry.GetType().GetMethod("AllBotTypes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (allMethod == null) { Warn?.Invoke($"{Tag} ⚠ 找不到 PresetGenerationService.AllBotTypes()"); return true; }

        var existing = (allMethod.Invoke(registry, null) as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
        var have = new HashSet<int>(existing.Select(x => EntryWildSpawn(x)).Where(v => v >= 0));
        var missing = Roles.Where(r => !have.Contains(r.WildSpawnType)).ToList();

        if (missing.Count == 0)
        {
            lock (_gate) _done = true;
            LogSkipOnce();
            return true;
        }

        // ★ 2026-10-30【用户口径 A】模板优先**黑狐 Lead（WildSpawnType=848420）**：
        //   它是 BlackDiv 注册的条目，带真实 BrainsToApply/BaseBrain（= 黑狐战术层）。
        //   本版 SAIN 的 bossKnight 这三项是 null，抄它等于没有层。
        //   黑狐不存在时回退 bossKnight（保持原行为）。
        object template = existing.FirstOrDefault(x => EntryWildSpawn(x) == 848420);
        string tplName = template != null ? "848420(Black Division Lead)" : "";
        if (template == null)
        {
            template = existing.FirstOrDefault(x => EntryWildSpawn(x) == 26)
                    ?? existing.FirstOrDefault(x => string.Equals(EntryStr(x, "Name"), "Knight", StringComparison.OrdinalIgnoreCase))
                    ?? existing.FirstOrDefault(x => string.Equals(EntryStr(x, "Name"), "bossKnight", StringComparison.OrdinalIgnoreCase));
            tplName = template != null ? "bossKnight(回退)" : "";
        }
        if (template == null) { Warn?.Invoke($"{Tag} ⚠ SAIN 表里既没有黑狐(848420) 也没有 bossKnight 模板 ⇒ 本次不注册"); return true; }

        var brains = EntryField(template, "BrainsToApply") as IEnumerable;
        var layers = EntryField(template, "LayersToRemove") as IEnumerable;
        var baseBrain = EntryField(template, "BaseBrain") as string;
        var diff = EntryField(template, "DefaultDifficultyModifier") is object o && o is not DBNull
                   ? Convert.ToSingle(o, CultureInfo.InvariantCulture) : 1f;

        var brainList = brains?.Cast<object>().Select(x => x?.ToString()).ToList();
        lock (_gate)
        {
            if (!_templateLogged)
            {
                _templateLogged = true;
                Info?.Invoke($"{Tag} 模板={tplName}｜BrainsToApply={(brainList == null ? "null" : brainList.Count + " 项：" + string.Join(",", brainList))}｜BaseBrain={(baseBrain ?? "(空)")}");
            }
        }

        var regType = FindType(RegistrationTypeName) ?? ifaceType.Assembly.GetType(RegistrationTypeName, false);
        if (regType == null) { Warn?.Invoke($"{Tag} ⚠ 找不到 {RegistrationTypeName}"); return true; }

        var register = registry.GetType().GetMethod("RegisterAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? ifaceType.GetMethod("RegisterAsync");
        if (register == null) { Warn?.Invoke($"{Tag} ⚠ 找不到 RegisterAsync"); return true; }

        int added = 0;
        foreach (var role in missing)
        {
            try
            {
                var reg = Activator.CreateInstance(regType);
                void Set(string name, object value)
                {
                    var p = regType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.CanWrite) p.SetValue(reg, value);
                }
                Set("Name", role.DisplayName);
                Set("WildSpawnType", role.WildSpawnType);
                Set("BotDbKey", role.DbKey);
                Set("DifficultyModifier", diff);
                // ★ 2026-10-31【分类不对：自定义 bot 落进"黑狐"列】Section 可能是枚举而非字符串。
                //   这里按真实类型处理，并把可选值打出来（只打一次），方便确认 SAIN 认哪些分类。
                var secProp = regType.GetProperty("Section", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (secProp != null && secProp.CanWrite)
                {
                    var pt = secProp.PropertyType;
                    if (!_sectionLogged)
                    {
                        _sectionLogged = true;
                        Info?.Invoke($"{Tag} Section 属性类型 = {pt.FullName}"
                            + (pt.IsEnum ? "｜可选值：" + string.Join(",", Enum.GetNames(pt)) : "｜（字符串）"));
                    }
                    object secVal;
                    if (pt == typeof(string)) secVal = "Boss";   // ★ SAIN 的分节名就是 "Boss"（"Bosses" 会被当成未知 ⇒ 落到其它自定义分节，实测掉进 Black Division 列）
                    else if (pt.IsEnum)
                    {
                        // 优先找名字里含 boss 的枚举值，找不到再用第一个
                        var names = Enum.GetNames(pt);
                        var pick = names.FirstOrDefault(n => n.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) ?? names.FirstOrDefault();
                        secVal = pick == null ? null : Enum.Parse(pt, pick);
                    }
                    else secVal = null;
                    if (secVal != null) secProp.SetValue(reg, secVal);
                }
                else Set("Section", "Boss");
                Set("Description", role.Description);
                Set("BrainsToApply", brains == null ? null : new List<string>(brains.Cast<object>().Select(x => x?.ToString())));
                Set("LayersToRemove", layers == null ? null : new List<string>(layers.Cast<object>().Select(x => x?.ToString())));
                Set("BaseBrain", baseBrain);

                var ret = register.Invoke(registry, new[] { reg, (object)CancellationToken.None });
                if (ret is Task t) t.GetAwaiter().GetResult();
                added++;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
                var msg = inner.Message ?? "";
                // 竞态：早期路径恰好抢先注册 ⇒ 按"已存在=跳过"处理，不打 Ignoring 噪音
                if (msg.Contains("already known", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("already registered", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("WildSpawnType is already", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                lock (_gate)
                {
                    if (!_warnLogged)
                    {
                        _warnLogged = true;
                        Warn?.Invoke($"{Tag} ⚠ 注册 {role.DbKey}（{role.WildSpawnType}）失败：{inner.GetType().Name}: {inner.Message}");
                    }
                }
            }
        }

        // 回读自证
        var after = (allMethod.Invoke(registry, null) as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
        var now = new HashSet<int>(after.Select(x => EntryWildSpawn(x)));
        int okCount = Roles.Count(r => now.Contains(r.WildSpawnType));
        if (okCount == Roles.Length)
        {
            lock (_gate) _done = true;
            if (added == 0) LogSkipOnce();
            else LogRegisteredOnce(added);
            return true;
        }
        return false;   // 还没齐 ⇒ 下一轮继续补
    }

    private static void LogSkipOnce()
    {
        lock (_gate)
        {
            if (_skipLogged) return;
            _skipLogged = true;
        }
        Info?.Invoke($"{Tag} {Roles.Length} 个角色已由早期路径注册（Section 由 SainPresetInject 决定），本条注册跳过");
    }

    private static void LogRegisteredOnce(int added)
    {
        lock (_gate)
        {
            if (_logged) return;
            _logged = true;
        }
        Info?.Invoke($"{Tag} ✔ 已补注册 {added} 个自定义角色（Section=Bosses）");
    }

    // ── 反射小工具 ─────────────────────────────────────────────────────────

    private static Type FindType(string fullName)
    {
        try
        {
            if (FindTypeForTest != null) return FindTypeForTest(fullName);
            return RuntimeTypes.Find(fullName);
        }
        catch { return null; }
    }

    private static object SafeGetService(Type t)
    {
        try { return _services?.GetService(t); }
        catch { return null; }
    }

    private static int EntryWildSpawn(object entry)
    {
        var v = EntryField(entry, "WildSpawnType");
        if (v == null) return -1;
        try { return v is Enum e ? Convert.ToInt32(e) : Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return -1; }
    }

    private static string EntryStr(object entry, string name) => EntryField(entry, name) as string;

    private static object EntryField(object entry, string name)
    {
        if (entry == null) return null;
        var t = entry.GetType();
        try
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (p != null) return p.GetValue(entry);
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            return f?.GetValue(entry);
        }
        catch { return null; }
    }
}
