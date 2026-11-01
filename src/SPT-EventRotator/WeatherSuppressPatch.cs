using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;

namespace EventRotator;

/// <summary>
/// ★★★ 第 26 项【独立天气开关】**只压雨雪 / 浓雾**，不碰活动、不碰季节、不碰任何其它天气字段。
///
/// <para><b>为什么天气必须走补丁</b>：天气是**服务端生成**的，落点全在
/// <c>SPTarkov.Server.Core</c> 的这几个方法上（元数据实证，见下），
/// 而它们都不读我们的配置 ⇒ 只能 postfix 改返回值。</para>
///
/// <para><b>三个落点（为什么要三处都挂）</b>：</para>
/// <list type="number">
/// <item><c>Controllers.WeatherController.Generate() → WeatherData</c>（<c>/client/weather</c>，客户端用它建
///   <c>GameDateTime</c>/天色）⇒ 改 <c>WeatherData.Weather</c>；</item>
/// <item><c>Controllers.WeatherController.GenerateLocal(MongoId) → GetLocalWeatherResponseData</c>
///   （**进图用的本地天气**）⇒ 改 <c>GetLocalWeatherResponseData.Weather</c>（<c>List&lt;Weather&gt;</c>，**每一条都要改**）；</item>
/// <item><c>Services.InRaid.RaidWeatherService.GetCurrentWeather() / GetUpcomingWeather()</c>
///   ⇒ 返回的是**进战局前缓存好的**那份（<c>GenerateFutureWeatherAndCache</c> 写进去的），
///   若只改 ①② 而不改这里，**局内中途变天**仍会拿出旧天气 ⇒ 缓存残留必须一起治。</item>
/// </list>
///
/// <para><b>取值依据（不是拍脑袋）</b>：<c>SPT_Data\configs\weather.json → weather.presetWeights</c> 里
/// 4 个预设（SUNNY / RAINY / CLOUDY / WINTER）的取值实测为：</para>
/// <code>
/// SUNNY  : rain {"1":1}               rainIntensity {min:0, max:0}    fog {0.0013:30, 0.0018:6, 0.002:4, 0.004:3, 0.006:1}
/// RAINY  : rain {"2":5,"3":5,"4":5,"5":5} rainIntensity {min:0.1,max:1} fog {... 同上 ...}
/// CLOUDY : rain {"1":1}               rainIntensity {min:0, max:1}    fog {... 同上 ...}
/// WINTER : rain {"1":0,"2":1,...}     rainIntensity {min:0, max:1}    fog {0.0013:5, 0.0018:4, 0.002:1, 0.004:3, 0.012:2}
/// </code>
/// <list type="bullet">
/// <item><b>雨雪</b>：<c>Rain = 1</c> 是 SUNNY/CLOUDY 的干燥取值，<c>RainIntensity = 0</c> 是
///   **SUNNY 的 rainIntensity 区间 [0,0]** ⇒ 两者合起来 = "不下雨"。雪在寒冷季节由**同一条 Rain 通道**渲染，
///   所以这一对也能同时压掉雪。注意 <b>CLOUDY 的 rainIntensity 是 [0,1]</b>（可能小雨）⇒ 只压 "Rain" 不够，
///   必须同时把强度钉成 0。</item>
/// <item><b>浓雾</b>：4 个预设的 <c>fog</c> 都是"雾值→权重"的加权表，**全场最小值 = 0.0013**
///   （SUNNY/RAINY/CLOUDY 权重 30~35，WINTER 权重 5），**最大值 = 0.012**（WINTER）。
///   ⇒ 取 <see cref="FogValue"/> = <b>0.0001</b>，比"最晴的预设"再低约 13 倍，明显低于任何预设会掷出的雾值，
///   客户端渲染上等价于"没有雾"（不写 0 是为了不在别的读取方那里制造"缺值/异常值"）。</item>
/// </list>
///
/// <para><b>红线</b>：</para>
/// <list type="bullet">
/// <item>只写 <c>Rain</c> / <c>RainIntensity</c> / <c>Fog</c> 三个字段，**其它一律不碰**
///   （<c>Cloud/Fog/WindSpeed/WindGustiness/WindDirection/Pressure/Temperature/Time/Date/Timestamp/
///   SptInRaidTimestamp/SptChosenPreset/ExtensionData</c> 全部原样）。</item>
/// <item>两个子开关**互相独立**：只压雨雪 / 只压雾 / 都压 / 都不压，四种组合都成立。</item>
/// <item>两个都关 ⇒ postfix **在读完开关后立刻 return**（连 <c>__result</c> 都不碰）⇒ 行为与打补丁前逐字节一致。</item>
/// <item>全链路 try/catch；任何异常只 warning/静默，**绝不影响进图**。</item>
/// </list>
///
/// <para><b>挂载方式（不改 <c>Plugin.cs</c>）</b>：<c>[ModuleInitializer]</c> 自举，与
/// <c>AirdropAppendPatch</c> 同款。⚠ 自举发生在**模块初始化期**，此时
/// <c>RuntimeTypes.LiveCoreAssembly</c> 还没被 <c>Plugin.OnLoadAsync</c> 赋值 ⇒ 只看
/// <see cref="RuntimeTypes.Find"/> 有命中死副本的风险（4.1.6 enum prepatch 双载，见 RuntimeTypes 长注释）。
/// 所以这里双保险：① 先用 <c>RuntimeTypes.Find</c>（项目铁律）；② 若"运行时 Core"还没定下来，
/// 就把进程内**所有** <c>SPTarkov.Server.Core</c> 副本里的同名类型都挂上 —— 死副本**永远不会被调用**、
/// 零风险，而无论 prepatch 把哪一份变成"活得那份"，我们的 postfix 都一定在。
/// 挂载后把每一份的身份（assembly/ALC/MVID）打进日志，供现场核对。</para>
/// </summary>
internal static class WeatherSuppressPatch
{
    private const string HarmonyId = "com.kirisame.eventrotator.weather";
    private const string Tag = "[EventRotator][天气]";

    // ── 配置键（config.json → weather 节点；与 ConfigWeb.BoolKeys / razor 三处逐字一致）──
    internal const string KeyRainSnow = "weather.suppressRainSnow";
    internal const string KeyFog = "weather.suppressFog";
    internal const string KeyLogOnce = "weather.logOnce";

    // ── 目标类型全名（**不写 typeof**：4.1.6 双载时编译期 typeof 可能指向死副本）──
    private const string TN_Controller = "SPTarkov.Server.Core.Controllers.WeatherController";
    private const string TN_RaidWeatherService = "SPTarkov.Server.Core.Services.InRaid.RaidWeatherService";
    /// <summary>`Weather` 模型的 CLR 全名（元数据实证：`SPTarkov.Server.Core.Models.Eft.Weather.Weather`）。
    /// 用**全名字符串**判断而不是缓存 `Type`：双载时两份程序集的 `Weather` 是**不同的 Type**，
    /// 按名字比才不会出现"挂的是活副本、判类型用的是死副本 ⇒ 一个都没命中"。</summary>
    private const string TN_Weather = "SPTarkov.Server.Core.Models.Eft.Weather.Weather";

    // ── 压制值（依据见类注释里的 weather.json 实测表）──
    private const double RainValue = 1.0;             // = SUNNY/CLOUDY 的 rain {"1":1}
    private const double RainIntensityValue = 0.0;    // = SUNNY 的 rainIntensity {min:0,max:0}
    private const double FogValue = 0.0001;           // 全场最小预设雾值 0.0013 的 1/13

    private static readonly object _gate = new();
    private static bool _patched;
    private static bool _armMounted;
    private static string _mountInfo;
    private static bool _mountLogged;

    // ══════════════════════════ 开关热读（mtime + 1 秒节流）══════════════════════════
    /// <summary>一次读取得到的开关组合。<c>LogOnce</c> 缺省 true（与默认配置一致）。</summary>
    internal sealed class SwitchSet
    {
        public bool RainSnow;
        public bool Fog;
        public bool LogOnce = true;
        public override string ToString() => $"suppressRainSnow={RainSnow} suppressFog={Fog} logOnce={LogOnce}";
    }

    private static volatile SwitchSet _sw = new SwitchSet();
    private static long _lastScanTick;          // Environment.TickCount64；0 = 从没扫过
    private const int ScanThrottleMs = 1000;    // ★ 不要每次补丁执行都读盘：最多 1 秒一次

    /// <summary>当前开关（1 秒节流 + mtime 变化才重新解析文件）。任何异常 ⇒ 沿用上一次的结果。</summary>
    internal static SwitchSet Current()
    {
        var now = Environment.TickCount64;
        var last = _lastScanTick;
        if (last != 0 && now - last < ScanThrottleMs) return _sw;
        _lastScanTick = now;
        try
        {
            var p = ConfigPath();
            var st = LoadSwitch(p);
            _sw = st;
        }
        catch { }
        return _sw;
    }

    /// <summary>既不在 1 秒节流内、也只在 mtime/长度变了时才真正读盘解析（"热读但别每局都读盘"）。</summary>
    private static SwitchSet LoadSwitch(string path)
    {
        var st = new SwitchSet();
        if (string.IsNullOrEmpty(path)) return st;
        try
        {
            if (!File.Exists(path)) return st;
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return st;
            if (root.TryGetProperty("weather", out var wx) && wx.ValueKind == JsonValueKind.Object)
            {
                st.RainSnow = BoolOf(wx, "suppressRainSnow", false);
                st.Fog = BoolOf(wx, "suppressFog", false);
                st.LogOnce = BoolOf(wx, "logOnce", true);
            }
        }
        catch { }
        return st;
    }

    private static bool BoolOf(JsonElement obj, string name, bool dflt)
    {
        try
        {
            if (!obj.TryGetProperty(name, out var v)) return dflt;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        catch { }
        return dflt;
    }

    /// <summary>config.json 路径 = 本程序集所在目录（= mod 目录）下的 config.json。</summary>
    internal static string ConfigPath()
    {
        try
        {
            var d = Path.GetDirectoryName(typeof(WeatherSuppressPatch).Assembly.Location);
            if (!string.IsNullOrEmpty(d)) return Path.Combine(d, "config.json");
        }
        catch { }
        try { return Path.Combine(AppContext.BaseDirectory ?? "", "config.json"); } catch { }
        return null;
    }

    /// <summary>仅供探针：绕过节流，直接解析指定文件。</summary>
    internal static SwitchSet LoadSwitchForTest(string path) => LoadSwitch(path);

    // ══════════════════════════ 自举 + 挂载 ══════════════════════════
    /// <summary>自举（`[ModuleInitializer]`）：不改 `Plugin.cs` 也能生效；失败 = 不补丁（回到原行为）。</summary>
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Bootstrap() { try { Enable(null); } catch { } }
#pragma warning restore CA2255

    internal static bool Enable(Harmony harmony)
    {
        lock (_gate)
        {
            if (_patched) return true;
            try
            {
                var types = CandidateTypes(TN_Controller, TN_RaidWeatherService);
                if (types.Count == 0) return false;      // SPT 换版本/改名 ⇒ 降级：不补丁、行为不变

                harmony ??= new Harmony(HarmonyId);
                int ctrl = 0, raid = 0;
                var ids = new List<string>();
                foreach (var t in types)
                {
                    if (string.Equals(t.FullName, TN_Controller, StringComparison.Ordinal))
                    {
                        if (Patch(harmony, t, "Generate", nameof(PostfixGenerate))) ctrl++;
                        if (Patch(harmony, t, "GenerateLocal", nameof(PostfixGenerateLocal))) ctrl++;
                    }
                    else if (string.Equals(t.FullName, TN_RaidWeatherService, StringComparison.Ordinal))
                    {
                        if (Patch(harmony, t, "GetCurrentWeather", nameof(PostfixGetCurrentWeather))) raid++;
                        if (Patch(harmony, t, "GetUpcomingWeather", nameof(PostfixGetUpcomingWeather))) raid++;
                        // ★ 5th（只为"每战局一次日志"的边界；不改任何天气）：
                        //   `GenerateFutureWeatherAndCache` = 进战局前把未来天气算好塞缓存的那个点。
                        if (Patch(harmony, t, "GenerateFutureWeatherAndCache", nameof(PostfixArmRaidLog)))
                            _armMounted = true;
                    }
                    try { ids.Add(RuntimeTypes.Describe(t.Assembly)); } catch { }
                }

                _patched = ctrl > 0 && raid > 0;
                _mountInfo = $"{Tag} 补丁已挂载：WeatherController {ctrl} 个方法 / RaidWeatherService {raid} 个方法"
                           + ( _armMounted ? "（含战局边界钩子）" : "（⚠ 战局边界钩子未挂上 ⇒ 日志退化为 60 秒去重）")
                           + $"；守住的程序集：{string.Join(" ｜ ", ids.Distinct())}";
                return _patched;
            }
            catch { return false; }
        }
    }

    /// <summary>撤销（排障/探针用）。</summary>
    internal static void Disable()
    {
        lock (_gate)
        {
            try
            {
                var h = new Harmony(HarmonyId);
                foreach (var t in CandidateTypes(TN_Controller, TN_RaidWeatherService))
                {
                    foreach (var name in new[] { "Generate", "GenerateLocal", "GetCurrentWeather",
                                                 "GetUpcomingWeather", "GenerateFutureWeatherAndCache" })
                    {
                        try
                        {
                            var m = AccessTools.Method(t, name);
                            if (m != null) h.Unpatch(m, HarmonyPatchType.Postfix, HarmonyId);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _patched = false;
            _armMounted = false;
        }
    }

    /// <summary>挂一个 postfix；找不到方法/挂失败都只返回 false（调用方决定是否算"整体成功"）。</summary>
    private static bool Patch(Harmony harmony, Type type, string method, string postfix)
    {
        try
        {
            var m = AccessTools.Method(type, method);
            if (m == null) return false;
            harmony.Patch(m, null, new HarmonyMethod(typeof(WeatherSuppressPatch), postfix));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 候选类型：① `RuntimeTypes.Find`（运行时 Core 优先，项目铁律）；
    /// ② 只有在"运行时 Core 还没定下来"（= 自举期）时，才把进程内**所有** Core 副本的同名类型都算进来。
    /// </summary>
    private static List<Type> CandidateTypes(params string[] fullNames)
    {
        var list = new List<Type>();
        void Add(Type t) { try { if (t != null && !list.Contains(t)) list.Add(t); } catch { } }

        var liveKnown = RuntimeTypes.LiveCoreAssembly != null;
        foreach (var fn in fullNames)
        {
            Add(RuntimeTypes.Find(fn));
            if (liveKnown) continue;
            foreach (var a in RuntimeTypes.CoreCopies())
            {
                try { Add(a.GetType(fn, false)); } catch { }
            }
        }
        return list;
    }

    // ══════════════════════════ 四个 postfix ══════════════════════════
    // `Generate() -> WeatherData`（内含 `Weather` 属性）
    internal static void PostfixGenerate(object __result)
    {
        try { Run(__result); } catch { }
    }

    // `GenerateLocal(MongoId) -> GetLocalWeatherResponseData`（内含 `List<Weather> Weather`）
    internal static void PostfixGenerateLocal(object __result)
    {
        try { Run(__result); } catch { }
    }

    // `GetCurrentWeather() -> Weather`
    internal static void PostfixGetCurrentWeather(object __result)
    {
        try { Run(__result); } catch { }
    }

    // `GetUpcomingWeather() -> IEnumerable<Weather>`
    internal static void PostfixGetUpcomingWeather(object __result)
    {
        try { Run(__result); } catch { }
    }

    // `GenerateFutureWeatherAndCache(Season) -> void`：只当"战局边界"用，**不改天气**
    internal static void PostfixArmRaidLog()
    {
        try { lock (_gate) { _loggedThisRaid.Clear(); } } catch { }
    }

    /// <summary>统一的"读开关 → 应用 → 日志"入口；开关都关时**立刻返回**（不碰 `__result`）。</summary>
    private static void Run(object root)
    {
        if (root == null) return;
        var st = Current();
        if (!st.RainSnow && !st.Fog) return;              // ★ 关着 ⇒ 逐字节行为不变
        var r = ApplyDeep(root, st.RainSnow, st.Fog);
        if (r.Touched <= 0) return;
        LogMountOnce();
        if (st.RainSnow && ShouldLog("rain", st.LogOnce))
            Log($"{Tag} 已压制 雨/雪（Rain=1, RainIntensity=0）");
        if (st.Fog && ShouldLog("fog", st.LogOnce))
            Log($"{Tag} 已压制 浓雾（Fog=0.0001）");
    }

    // ══════════════════════════ 字段改写核心（探针直接打这里）══════════════════════════
    /// <summary>一次应用的结果。<c>Touched</c> = 认出的 <c>Weather</c> 对象数；<c>Changed</c> = 真正被改写的字段数。</summary>
    internal readonly struct ApplyResult
    {
        public readonly int Touched;
        public readonly int Changed;
        public ApplyResult(int touched, int changed) { Touched = touched; Changed = changed; }
        public override string ToString() => $"touched={Touched} changed={Changed}";
    }

    /// <summary>
    /// 从任意返回值里找出 <c>Weather</c> 对象并只改 <c>Rain</c>/<c>RainIntensity</c>/<c>Fog</c>。
    /// 兼容三种形状：直接是 <c>Weather</c>、含 <c>Weather</c> 属性的对象（WeatherData / GetLocalWeatherResponseData）、
    /// 以及 <c>Weather</c> 的集合（<c>List&lt;Weather&gt;</c> / <c>IEnumerable&lt;Weather&gt;</c>）。
    /// </summary>
    internal static ApplyResult ApplyDeep(object root, bool rainSnow, bool fog)
    {
        int touched = 0, changed = 0;
        try { Walk(root, rainSnow, fog, ref touched, ref changed, 0); } catch { }
        return new ApplyResult(touched, changed);
    }

    private static void Walk(object node, bool rainSnow, bool fog, ref int touched, ref int changed, int depth)
    {
        if (node == null || depth > 2) return;
        var t = node.GetType();

        if (IsWeatherType(t)) { touched++; changed += SetWeather(node, rainSnow, fog); return; }
        if (node is string) return;

        if (node is IEnumerable en)
        {
            int i = 0;
            foreach (var e in en)
            {
                if (++i > 4096) break;                     // 保险：不枚举超大集合
                if (e == null) continue;
                Walk(e, rainSnow, fog, ref touched, ref changed, depth + 1);
            }
            return;
        }

        foreach (var p in PropsOf(t))
        {
            // `ExtensionData`（JsonExtensionData）永远不含天气，跳过可省一次遍历
            if (p.GetIndexParameters().Length != 0) continue;
            if (string.Equals(p.Name, "ExtensionData", StringComparison.Ordinal)) continue;
            object v;
            try { v = p.GetValue(node); } catch { continue; }
            if (v == null) continue;
            var vt = v.GetType();
            if (IsWeatherType(vt)) { touched++; changed += SetWeather(v, rainSnow, fog); continue; }
            if (v is IEnumerable) Walk(v, rainSnow, fog, ref touched, ref changed, depth + 1);
        }
    }

    /// <summary>按**全名**判断是不是 <c>Weather</c>（双载时两份的 Type 不是同一个，不能比 Type）。</summary>
    private static bool IsWeatherType(Type t)
    {
        try { return t != null && string.Equals(t.FullName, TN_Weather, StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>只改三个字段；其它字段一个字都不碰。返回真正写入的字段数。</summary>
    internal static int SetWeather(object weather, bool rainSnow, bool fog)
    {
        int n = 0;
        try
        {
            if (weather == null) return 0;
            if (rainSnow)
            {
                if (SetNum(weather, "Rain", RainValue)) n++;
                if (SetNum(weather, "RainIntensity", RainIntensityValue)) n++;
            }
            if (fog)
            {
                if (SetNum(weather, "Fog", FogValue)) n++;
            }
        }
        catch { }
        return n;
    }

    /// <summary>写一个数值属性（支持 double/double?/float/float?/int/int?）；已经是目标值则**不动**（幂等）。</summary>
    private static bool SetNum(object o, string name, double v)
    {
        try
        {
            var p = Prop(o.GetType(), name);
            if (p == null || !p.CanWrite) return false;
            // ★ 3v：其它字段一律不碰 —— 这一层只认这三个属性名（调用方传进来的名字是常量）
            var underlying = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var old = p.GetValue(o);
            if (old != null && Math.Abs(Convert.ToDouble(old) - v) < 1e-9) return false;
            object boxed =
                underlying == typeof(double) ? (object)v :
                underlying == typeof(float) ? (object)(float)v :
                underlying == typeof(int) ? (object)(int)Math.Round(v) :
                underlying == typeof(long) ? (object)(long)Math.Round(v) :
                null;
            if (boxed == null) return false;             // 类型不认识 ⇒ 宁可不写
            p.SetValue(o, boxed);
            return true;
        }
        catch { return false; }
    }

    // ══════════════════════════ 反射缓存 ══════════════════════════
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> _propCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propArrCache = new();

    private static PropertyInfo Prop(Type t, string name)
        => _propCache.GetOrAdd((t, name), k =>
        {
            try { return k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase); }
            catch { return null; }
        });

    private static PropertyInfo[] PropsOf(Type t)
        => _propArrCache.GetOrAdd(t, k =>
        {
            try { return k.GetProperties(BindingFlags.Public | BindingFlags.Instance); }
            catch { return Array.Empty<PropertyInfo>(); }
        });

    // ══════════════════════════ 日志（每战局每项一次）══════════════════════════
    private static readonly HashSet<string> _loggedThisRaid = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> _lastLogAt = new(StringComparer.Ordinal);

    /// <summary>
    /// 是否该打这一行：`logOnce=false` ⇒ 每次都打（排障用）；
    /// `logOnce=true` ⇒ 有战局边界钩子时**每战局每项一次**，没有边界钩子时退化为**同类 60 秒一次**（宁可少打，不刷屏）。
    /// </summary>
    private static bool ShouldLog(string kind, bool logOnce)
    {
        if (!logOnce) return true;
        lock (_gate)
        {
            if (_armMounted) return _loggedThisRaid.Add(kind);
            var now = Environment.TickCount64;
            if (_lastLogAt.TryGetValue(kind, out var at) && now - at < 60000) return false;
            _lastLogAt[kind] = now;
            return true;
        }
    }

    private static void LogMountOnce()
    {
        if (_mountLogged) return;
        lock (_gate)
        {
            if (_mountLogged) return;
            _mountLogged = true;
        }
        if (!string.IsNullOrEmpty(_mountInfo)) Log(_mountInfo);
    }

    /// <summary>
    /// 日志出口：优先借 `ExtraDimensions.Log`（Plugin.OnLoadAsync 注入的 SPT logger），
    /// 退回 `AirdropDimension.Log`；都没有就静默（**绝不为写日志去碰 SPT 的 DI**）。
    /// </summary>
    private static void Log(string line, bool warn = false)
    {
        try
        {
            var lg = ExtraDimensions.Log;
            if (lg != null)
            {
                if (warn) lg.Warning(line); else lg.Info(line);
                return;
            }
        }
        catch { }
        try { AirdropDimension.Log?.Invoke(line); } catch { }
    }
}
