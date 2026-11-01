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
/// ★★★ 第 3 项【Fence 传闻】：**生效活动集合变化时，玩家进游戏收一条黑商消息**。
///
/// ── 落点（全部用 Cecil 反编译核对过，不是按记忆写的）───────────────────────────────
///   · 发送：<c>SPTarkov.Server.Core.Services.Commerce.MailSendService</c>（[Injectable]）
///       <c>SendDirectNpcMessageToPlayer(MongoId sessionId, string trader, MessageType messageType,
///         string message, List&lt;Item&gt; items, long? maxStorageTimeSeconds,
///         SystemData systemData, MessageContentRagfair ragfair)</c>
///     → 内部 <c>SendMessageToPlayer</c> → <c>GetDialog</c> / <c>CreateDialogMessage</c>
///       → 写进 <c>SptProfile.DialogueRecords[trader].Messages</c> + 通过
///       <c>NotificationSendHelper.SendMessageAsync</c> 推 <c>WsChatMessageReceived</c>（客户端**立刻**能看到）。
///   · 触发：<c>SPTarkov.Server.Core.Callbacks.GameCallbacks::GameStart(string url, EmptyRequestData, MongoId sessionID)</c>
///     —— 客户端「进入游戏」时调用一次，**带 sessionID**；IL 里它同步调
///     <c>GameController.GameStart(url, sessionID, timeUtil.GetTimeStamp())</c>（偏移 0x0053），postfix 即在之后。
///   · 语言：<c>SPTarkov.Server.Core.Services.Locales.LocaleService::GetDesiredGameLocale()</c>（= 客户端语言）。
///   · 立刻落盘：<c>SPTarkov.Server.Core.Servers.SaveServer::SaveProfileAsync(MongoId, CancellationToken)</c>（fire-and-forget）。
///
/// ── 黑商 id（数据实证，别照抄别人的猜测）─────────────────────────────────────────
///   `SPT_Data/database/traders/579dc571d53a0658a154fbec/base.json → nickname = "Fence"`。
///   ⚠ `54cb57776803fa99248b456e` 是 **Therapist**（同一份数据里 nickname=Therapist），不是黑商。
///
/// ── 不刷屏（三层，缺一层就会"每次登录都刷"）─────────────────────────────────────
///   ① 状态文件 `<mod>/fence_rumor_state.json`：`lastHash` 记录**上次已通知的活动集合哈希**
///      ⇒ 同一份集合重启多少次都只发一次（存档里的消息不会自己消失，所以必须自己记）；
///   ② 进程内 `_bootSent[(sessionId)] = hash`：**一次启动内同一集合最多发一次**（换集合才可能再发）；
///   ③ 限流：`maxPerDay`（按 UTC 日）+ `minIntervalMinutes`（两条之间最小间隔），配置在 `config.json → fenceRumor`。
///
/// ── 可测性 / 降级 ───────────────────────────────────────────────────────────────
///   · 服务全部从 DI（`IServiceProvider`）按**类型全名**取，取不到只 warning；
///   · 测试可注入 <see cref="SendOverride"/> 替身发送器 + <see cref="ActiveEvents"/> 活动来源；
///   · 任何异常都被吞掉并打 warning —— **绝不影响登录**。
/// </summary>
internal static class FenceRumor
{
    /// <summary>黑商（数据实证：traders/579dc571d53a0658a154fbec → nickname "Fence"）。</summary>
    public const string FenceTraderId = "579dc571d53a0658a154fbec";

    private const string StateFileName = "fence_rumor_state.json";
    private const string MailServiceType = "SPTarkov.Server.Core.Services.Commerce.MailSendService";
    private const string LocaleServiceType = "SPTarkov.Server.Core.Services.Locales.LocaleService";
    private const string SaveServerType = "SPTarkov.Server.Core.Servers.SaveServer";

    /// <summary>SPT 的 DI 容器（由 Plugin 注入；测试可为 null）。</summary>
    public static IServiceProvider Services;

    /// <summary>mod 目录（状态文件写这里）。</summary>
    public static string ModDir;

    /// <summary>当前生效活动来源（生产 = <c>() =&gt; EventEngine.Actives</c>；测试可替换）。</summary>
    public static Func<IEnumerable<EventDef>> ActiveEvents;

    public static Action<string> Info = _ => { };
    public static Action<string> Warn = _ => { };

    /// <summary>测试替身：非 null 时不走 DI 的 MailSendService，直接调用这个（收件人, 文本）。</summary>
    public static Action<object, string> SendOverride;

    private static readonly object _gate = new();

    /// <summary>本进程内已经通知过的 (sessionId → 已通知的集合哈希)。</summary>
    private static readonly Dictionary<string, string> _bootSent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按类型全名缓存的 DI 服务实例（只缓存成功；失败下次重试）。</summary>
    private static readonly Dictionary<string, object> _svcCache = new(StringComparer.Ordinal);

    /// <summary>只打一次的提示（避免每次登录刷屏）。</summary>
    private static readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);

    // ── 配置缓存（按 config.json 的 mtime/长度失效，改完不用重启）──
    private static string _cfgKey;
    private static Opt _cfg = new();

    internal sealed class Opt
    {
        public bool Enabled = true;
        public int MaxPerDay = 1;
        public int MinIntervalMinutes = 720;
    }

    internal sealed class State
    {
        public string LastHash = "";
        public string LastSentUtc = "";
        public string DayUtc = "";
        public int TodayCount;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  入口（由 Patches/FenceRumorPatch 的 GameStart postfix 调用）
    // ══════════════════════════════════════════════════════════════════════════

    public static void OnPlayerEnterGame(object session)
    {
        try
        {
            if (session == null) return;
            var sid = session.ToString();
            if (string.IsNullOrEmpty(sid)) return;

            var opt = ReadOpt();
            if (!opt.Enabled)
            {
                Once("disabled", "fenceRumor.enabled=false ⇒ 一条都不发");
                return;
            }

            var evs = (ActiveEvents?.Invoke() ?? Enumerable.Empty<EventDef>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id)).ToList();
            if (evs.Count == 0) return;      // 没有活动 = 没"动静"，不发也不改状态

            var hash = HashOf(evs);
            lock (_gate)
            {
                // ② 一次启动内，同一份集合不重复发
                if (_bootSent.TryGetValue(sid, out var sentHash) && sentHash == hash) return;

                var st = ReadState();
                // ① 同一份集合已经通知过（跨重启）
                if (string.Equals(st.LastHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    Once("dedupe:" + hash, $"活动集合未变化（hash={hash[..Math.Min(8, hash.Length)]}…）⇒ 不再通知");
                    return;
                }

                // ③ 限流
                var now = DateTime.UtcNow;
                var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.Equals(st.DayUtc, day, StringComparison.Ordinal) && st.TodayCount >= opt.MaxPerDay)
                {
                    Warn($"[Fence传闻] 今日（{day}，UTC）已发 {st.TodayCount} 条，达到 maxPerDay={opt.MaxPerDay} ⇒ 本次不发");
                    return;
                }
                if (DateTime.TryParse(st.LastSentUtc, CultureInfo.InvariantCulture,
                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var last)
                    && last != default)
                {
                    var gap = now - last;
                    if (gap.TotalMinutes < opt.MinIntervalMinutes)
                    {
                        Warn($"[Fence传闻] 距上次发送仅 {gap.TotalMinutes:0.#} 分钟，未到 minIntervalMinutes={opt.MinIntervalMinutes} ⇒ 本次不发");
                        return;
                    }
                }

                var lang = GetGameLocale();
                var text = BuildText(lang, evs);
                if (!Send(session, text)) return;   // 发送失败 ⇒ 不动状态，下次登录再试

                // 记账：只有真的发出去了才更新
                if (!string.Equals(st.DayUtc, day, StringComparison.Ordinal)) { st.DayUtc = day; st.TodayCount = 0; }
                st.TodayCount++;
                st.LastHash = hash;
                st.LastSentUtc = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                WriteState(st);
                _bootSent[sid] = hash;

                Info($"[Fence传闻] ✔ 已给 {sid} 发一条黑商消息（活动 {evs.Count} 个，hash={hash[..Math.Min(8, hash.Length)]}…，"
                   + $"语言={lang}，今日第 {st.TodayCount}/{opt.MaxPerDay} 条）");

                SaveProfile(session);
            }
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ 处理异常（已忽略，不影响登录）：" + ex.GetType().Name + ": " + ex.Message); } catch { }
        }
    }

    /// <summary>测试/热重载用：清掉"本次启动已发"的记录与已缓存的服务实例（模拟服务端重启）。</summary>
    public static void ResetBootState()
    {
        lock (_gate) { _bootSent.Clear(); _loggedOnce.Clear(); _svcCache.Clear(); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  文案
    // ══════════════════════════════════════════════════════════════════════════

    private static bool IsChinese(string lang)
    {
        if (string.IsNullOrEmpty(lang)) return false;
        var l = lang.Trim().ToLowerInvariant();
        return l.StartsWith("ch") || l.StartsWith("zh") || l.StartsWith("cn");
    }

    /// <summary>黑商口吻的双语消息；最多列 3 个活动，多了写「等 N 个」。</summary>
    internal static string BuildText(string lang, IReadOnlyList<EventDef> evs, int maxNames = 3)
    {
        var zh = IsChinese(lang);
        var names = evs.Take(maxNames).Select(e =>
        {
            var n = zh ? (string.IsNullOrWhiteSpace(e.NameZh) ? e.NameEn : e.NameZh)
                       : (string.IsNullOrWhiteSpace(e.NameEn) ? e.NameZh : e.NameEn);
            return string.IsNullOrWhiteSpace(n) ? e.Id : n.Trim();
        }).ToList();

        if (zh)
        {
            var list = string.Join("、", names.Select(n => "「" + n + "」"));
            var more = evs.Count > maxNames ? $"等 {evs.Count} 个" : "";
            var tail = evs.Count > maxNames
                ? "剩下的你自己慢慢打听，我不挨个念了。"
                : "就这些，别处那帮小子可不会白白告诉你。";
            return $"嘿，哥们儿。最近市面上有点动静——{list}{more}。{tail}想听更细的，来我这儿翻翻货，顺便聊聊。";
        }
        else
        {
            var list = string.Join(", ", names.Select(n => "\"" + n + "\""));
            var more = evs.Count > maxNames ? $" and {evs.Count - maxNames} more" : "";
            var tail = evs.Count > maxNames
                ? "The rest you can dig up yourself, I'm not reading out the whole list."
                : "That's all I've got. Nobody else will hand you this for free.";
            return $"Hey, pal. Word on the street — {list}{more}. {tail} Come dig through my stock if you want details.";
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  服务解析（★ 必须按"运行时那份 Core"取，见 RuntimeTypes 文件头）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 按类型全名从 DI 取服务。
    ///
    /// ── ★★★ 实机事故根因（2026-10-29，`spt20261029.log` 原文）─────────────────────
    ///   `[Fence传闻] ⚠ DI 里拿不到 MailSendService ⇒ 不发`
    ///   而挂载是成功的（`GameCallbacks` 命中 `ALC=SPT.PrepatchHost` 那份）。
    ///   真因：老代码 <c>FindType</c> 直接遍历 `AppDomain.CurrentDomain.GetAssemblies()` 取**第一个**命中
    ///   ⇒ 拿到的是 **Default ALC 的死副本**（Disk 那份、先装载），而 SPT 的 DI 里注册的是
    ///   `SPT.PrepatchHost` 那份的 `MailSendService` ⇒ `GetService(死副本的 Type)` **类型不是同一个** ⇒ null。
    ///   （这正是 `RuntimeTypes` 文件头记录的同一个 prepatch 双载坑；`Plugin.cs` 里所有 SPT 服务
    ///     都是 DI 注入的运行时类型，只有本文件自作聪明走了 `GetAssemblies()[0]`。）
    ///
    /// ── 修法 ───────────────────────────────────────────────────────────────────
    ///   ① 主路：`RuntimeTypes.Find(全名)` —— 它优先从 <see cref="RuntimeTypes.LiveCoreAssembly"/>
    ///      （Plugin 启动时用 DI 实例 `GetType().Assembly` 锁定的那份）取类型；
    ///   ② 兜底：把进程内**每一份** Core 里的同名类型都拿去 `GetService` 试一遍
    ///      （万一 DI 注册的是另一份，也能自愈，并把"用哪份成功"打出来）；
    ///   ③ 都失败 ⇒ **只打一条带身份的诊断日志**、返回 null（调用方不写状态文件，下次登录自动重试）。
    /// </summary>
    private static object ResolveService(string fullName, bool required = true)
    {
        try
        {
            lock (_gate)
            {
                if (_svcCache.TryGetValue(fullName, out var cached) && cached != null) return cached;
            }

            Type primary = null;
            try { primary = RuntimeTypes.Find(fullName); }
            catch { }

            foreach (var t in CandidateTypes(fullName, primary))
            {
                object inst = null;
                try { inst = Services?.GetService(t); }
                catch { inst = null; }
                if (inst == null) continue;

                lock (_gate) _svcCache[fullName] = inst;
                var shortName = fullName.Split('.').Last();
                Once("resolved:" + fullName,
                     $"{shortName} 已解析：类型={t.FullName}｜{RuntimeTypes.Describe(t.Assembly)}"
                   + $"｜与运行时 Core 同一份={ReferenceEquals(t.Assembly, RuntimeTypes.LiveCoreAssembly)}"
                   + $"｜实例={inst.GetType().FullName}");
                return inst;
            }

            if (required) WarnServiceMissing(fullName, primary);
            else Once("optional:" + fullName, $"{fullName.Split('.').Last()} 未解析到（该服务是可选的，本次走默认值）");
            return null;
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ 解析服务 " + fullName + " 异常： " + ex.GetType().Name + ": " + ex.Message); } catch { }
            return null;
        }
    }

    /// <summary>候选类型：运行时那份 → 其它 Core 副本 → 本 mod ALC 里同名的。
    /// ⚠ 用 List 收集而不是 yield（`try/catch` 里不允许 yield return）。</summary>
    private static List<Type> CandidateTypes(string fullName, Type primary)
    {
        var list = new List<Type>();
        if (primary != null) list.Add(primary);

        try
        {
            foreach (var a in RuntimeTypes.CoreCopies())
            {
                Type t = null;
                try { t = a.GetType(fullName, false); } catch { }
                if (t != null && !list.Contains(t)) list.Add(t);
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
                    try { t = a.GetType(fullName, false); } catch { }
                    if (t != null && !list.Contains(t)) list.Add(t);
                }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// 拿不到服务时打**一行**诊断（不刷屏）：解析到的类型身份 + 与运行时 Core 是否同一份 +
    /// 进程内每份 Core 的 ALC/Location/MVID + 每份里有没有这个类型。
    /// 有了这行，下次一眼就能看出是"双载取错副本"还是"真的没注册"。
    /// </summary>
    private static void WarnServiceMissing(string fullName, Type primary)
    {
        var shortName = fullName.Split('.').Last();
        bool first;
        lock (_gate) first = _loggedOnce.Add("svcmissing:" + fullName);
        if (!first)
        {
            // 已经打过完整诊断 ⇒ 以后每次只留一行短的（不刷屏）
            try { Warn($"[Fence传闻] ⚠ 仍拿不到 {shortName}（完整诊断见本次启动的第一条）⇒ 不发，不写状态"); } catch { }
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append("[Fence传闻] ⚠ DI 里拿不到 ").Append(fullName.Split('.').Last()).Append(" ⇒ 不发（不写状态，下次登录重试）。诊断：");
            sb.Append("Services=").Append(Services == null ? "null" : Services.GetType().FullName);
            sb.Append("｜解析类型=").Append(primary == null ? "(没找到)" : RuntimeTypes.Describe(primary.Assembly));
            sb.Append("｜与运行时 Core 同一份=")
              .Append(primary != null && ReferenceEquals(primary.Assembly, RuntimeTypes.LiveCoreAssembly));
            sb.Append("｜LiveCore=").Append(RuntimeTypes.LiveCoreAssembly == null
                ? "(RuntimeTypes.Init 未跑？)"
                : RuntimeTypes.Describe(RuntimeTypes.LiveCoreAssembly));

            var copies = RuntimeTypes.CoreCopies();
            sb.Append("｜进程内 Core ").Append(copies.Count).Append(" 份：");
            sb.Append(string.Join(" ｜ ", copies.Select((a, i) =>
            {
                bool has = false; string asmAlc = "?";
                try { has = a.GetType(fullName, false) != null; } catch { }
                try { asmAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a)?.Name ?? "(none)"; } catch { }
                return $"#{i + 1} ALC={asmAlc} 含该类型={has} {RuntimeTypes.Describe(a)}";
            })));

            Warn(sb.ToString());
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ DI 里拿不到 " + fullName + "（诊断本身也失败了：" + ex.Message + "）⇒ 不发"); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  发送（DI → MailSendService；反射调用，避免 prepatch 双载的类型身份问题）
    // ══════════════════════════════════════════════════════════════════════════

    private static bool Send(object session, string text)
    {
        try
        {
            if (SendOverride != null) { SendOverride(session, text); return true; }

            var svc = ResolveService(MailServiceType);
            if (svc == null) return false;           // 诊断已在 ResolveService 里打过

            var m = svc.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == "SendDirectNpcMessageToPlayer");
            if (m == null) { Warn("[Fence传闻] ⚠ MailSendService 上没有 SendDirectNpcMessageToPlayer ⇒ 不发"); return false; }

            var ps = m.GetParameters();
            var items = Activator.CreateInstance(ps[4].ParameterType);       // List<Item>
            var msgType = Enum.Parse(ps[2].ParameterType, "NpcTraderMessage"); // MessageType.NpcTraderMessage = 2
            var sessionArg = CoerceTo(session, ps[0].ParameterType);           // 运行时 MongoId（跨 ALC 时兜底构造）
            m.Invoke(svc, new object[] { sessionArg, FenceTraderId, msgType, text, items, null, null, null });
            return true;
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ 发送失败（已忽略）：" + ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message)); } catch { }
            return false;
        }
    }

    /// <summary>
    /// 把 <paramref name="v"/> 转成方法形参需要的类型（目前只用于 <c>MongoId</c>：测试/跨 ALC 偶发
    /// 拿 string 当 session 的场景）。失败就原样返回，让 SPT 自己的校验报错、外层 catch 兜住。
    /// </summary>
    private static object CoerceTo(object v, Type t)
    {
        if (v == null || t == null || t.IsInstanceOfType(v)) return v;
        try
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return ctor.Invoke(new object[] { v.ToString() });
            var parse = t.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (parse != null) return parse.Invoke(null, new object[] { v.ToString() });
        }
        catch { }
        return v;
    }

    /// <summary>发完立刻落盘（WS 推送已经让客户端当场看到；这一步保证重启/换角色也不丢）。</summary>
    private static void SaveProfile(object session)
    {
        try
        {
            var ss = ResolveService(SaveServerType, required: false);
            if (ss == null) return;
            var m = ss.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == "SaveProfileAsync");
            if (m == null) return;
            var sessionArg = CoerceTo(session, m.GetParameters()[0].ParameterType);
            var started = m.Invoke(ss, new object[] { sessionArg, CancellationToken.None });
            if (started is Task task)
                task.ContinueWith(x =>
                {
                    if (!x.IsFaulted) return;
                    try { Warn("[Fence传闻] ⚠ 保存 profile 失败（不影响游戏）：" + x.Exception?.GetBaseException().Message); } catch { }
                }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ 保存 profile 异常（不影响游戏）：" + ex.Message); } catch { }
        }
    }

    private static string GetGameLocale()
    {
        try
        {
            var svc = ResolveService(LocaleServiceType, required: false);
            if (svc == null) return "en";
            var m = svc.GetType().GetMethod("GetDesiredGameLocale", Type.EmptyTypes);
            var v = m?.Invoke(svc, null) as string;
            return string.IsNullOrWhiteSpace(v) ? "en" : v;
        }
        catch { return "en"; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  配置 / 状态 / 哈希
    // ══════════════════════════════════════════════════════════════════════════

    private static string ConfigPath =>
        string.IsNullOrEmpty(ModDir) ? null : Path.Combine(ModDir, "config.json");

    private static string StatePath =>
        string.IsNullOrEmpty(ModDir) ? null : Path.Combine(ModDir, StateFileName);

    internal static Opt ReadOpt()
    {
        try
        {
            var p = ConfigPath;
            if (p == null || !File.Exists(p)) return _cfg = new Opt();

            var fi = new FileInfo(p);
            var key = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
            if (key == _cfgKey && _cfg != null) return _cfg;

            var opt = new Opt();
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("fenceRumor", out var fr)
                && fr.ValueKind == JsonValueKind.Object)
            {
                if (fr.TryGetProperty("enabled", out var en) &&
                    (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                    opt.Enabled = en.GetBoolean();
                if (fr.TryGetProperty("maxPerDay", out var mp) && mp.ValueKind == JsonValueKind.Number)
                    opt.MaxPerDay = Math.Clamp(mp.GetInt32(), 0, 100);
                if (fr.TryGetProperty("minIntervalMinutes", out var mi) && mi.ValueKind == JsonValueKind.Number)
                    opt.MinIntervalMinutes = Math.Clamp(mi.GetInt32(), 0, 525600);
            }
            _cfgKey = key;
            return _cfg = opt;
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ config.json 读取失败 ⇒ 用默认值（enabled=true / maxPerDay=1 / 720 分钟）：" + ex.Message); } catch { }
            return _cfg = new Opt();
        }
    }

    internal static State ReadState()
    {
        var st = new State();
        try
        {
            var p = StatePath;
            if (p == null || !File.Exists(p)) return st;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return st;
            if (r.TryGetProperty("lastHash", out var h) && h.ValueKind == JsonValueKind.String) st.LastHash = h.GetString() ?? "";
            if (r.TryGetProperty("lastSentUtc", out var t) && t.ValueKind == JsonValueKind.String) st.LastSentUtc = t.GetString() ?? "";
            if (r.TryGetProperty("dayUtc", out var d) && d.ValueKind == JsonValueKind.String) st.DayUtc = d.GetString() ?? "";
            if (r.TryGetProperty("todayCount", out var c) && c.ValueKind == JsonValueKind.Number) st.TodayCount = c.GetInt32();
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ 状态文件读取失败（按空状态处理）：" + ex.Message); } catch { }
        }
        return st;
    }

    internal static void WriteState(State st)
    {
        try
        {
            var p = StatePath;
            if (p == null || ModDir == null) return;
            Directory.CreateDirectory(ModDir);
            var obj = new
            {
                lastHash = st.LastHash,
                lastSentUtc = st.LastSentUtc,
                dayUtc = st.DayUtc,
                todayCount = st.TodayCount,
            };
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, p, true);      // 原子替换，避免写一半留下坏文件
        }
        catch (Exception ex)
        {
            try { Warn("[Fence传闻] ⚠ 状态文件写入失败（不影响发送）：" + ex.Message); } catch { }
        }
    }

    /// <summary>活动集合指纹（FNV-1a 64，按 id 排序后哈希 ⇒ 与顺序无关）。</summary>
    internal static string HashOf(IEnumerable<EventDef> evs)
    {
        var ids = evs.Select(e => e?.Id ?? "").Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        ulong h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(string.Join("\n", ids)))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static void Once(string key, string msg)
    {
        lock (_gate) { if (!_loggedOnce.Add(key)) return; }
        try { Info("[Fence传闻] " + msg); } catch { }
    }
}
