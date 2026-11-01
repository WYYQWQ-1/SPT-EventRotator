using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace EventRotator;

/// <summary>
/// ★★★ v2.6.70【性能 · Y2（Lead 已批准）】**可开关的热点计时**（只为测量，不改变任何业务行为）。
///
/// 用法（**零分配、关闭时零开销**）：
/// <code>
/// long t0 = PerfTimer.Start();          // 关闭时返回 0
/// …原有逻辑，一行不动…
/// PerfTimer.End("MapGate.Prefix", t0);  // t0==0 时直接返回
/// </code>
///
/// 开关 = `EventEngine.DebugLogging`（= `logLevel != "off"`；SPT 默认 `basic` ⇒ **默认是开的**，
/// 但只在**每 500 次调用**打一条聚合行，且聚合本身只加 long，不会成为新瓶颈）。
/// 输出形如：
/// <code>[EventRotator][perf] MapGate.Prefix   n=1500  总=42.1ms  平均=28.1µs  最大=1.20ms  (每 500 次一条)</code>
///
/// ⚠ 行为风险：**仅新增日志输出**；不读/不写任何业务状态；`Start()` 关闭时返回 0 且不取样。
/// </summary>
internal static class PerfTimer
{
    /// <summary>
    /// ★★★ v2.6.75【用户实测反馈：`[perf]` 一行都没打出来】**阈值从 500 降到 10**，并加两条兜底，
    /// 保证低频热点也能看到数据：
    ///   · **首见即报**（`n == 1` ⇒ 每条 tag 一局至少有一条）—— 覆盖"每局一次"的埋点（进图/ABPS/天气…）；
    ///   · **时间兜底**（距上次打印 ≥ <see cref="ReportEverySeconds"/> 秒且计数有增长 ⇒ 报一条）——
    ///     覆盖"每波 10~40 次但不足 10 的倍数"的情况；
    ///   · 阈值可配置：`config.json` 里的 **`perfLogEvery`**（数字，缺省 = 10；**显式 `0` 或负数 ⇒ 恢复旧行为 = 500**）。
    /// </summary>
    private static int ReportEvery = 10;
    private static int ReportEverySeconds = 30;

    /// <summary>
    /// ★★★ v2.6.76【P0 修复 · 独立复核 🟡-1 的施工】**监控层改成"独立开关、默认关"**。
    ///
    /// <para>问题（实测）：原来 `Enabled =&gt; EventEngine.DebugLogging`，而 `DebugLogging = (logLevel != "off")`
    /// ⇒ 用户当前 `logLevel=basic` 时 **24 个埋点全开**，其中 3 个在**每-bot** 路径
    /// （`清场小队.OnBotGenerated(每bot)` / `按图.BotPrefix(每bot)` / `IsNightTime(只读回调)`）
    /// ⇒ 每局 ~370 只 bot 会新增 **40~120 条同步日志**（含文件 I/O）＋每只 ~0.2 µs 的计时
    /// ⇒ **监控层自己在制造小卡顿**。</para>
    ///
    /// <para>现在：`perfLog`（`config.json` 里的 bool，**缺省 false**）＝唯一开关；
    /// `false` ⇒ `Start()` 返回 `0L` ⇒ `End()` 第一行 `return` ⇒ **零开销、零日志**。</para>
    ///
    /// <para>⚠ 与 `logLevel` **完全解耦**：`perfLog: true` 时那行 `[perf]` 走
    /// <see cref="Emit"/>（**直接 `EventEngine.Log.Info`**），**不受 `logLevel=off` 影响** —— 否则"开了开关却看不到数据"。</para>
    /// </summary>
    private static bool PerfLog;

    /// <summary>由 `EventEngine` 在读取 `config.json` 时调用（缺省值见上）。`enabled` = `config.json` 的 `perfLog`（缺省 false）。</summary>
    public static void Configure(bool? enabled, int? everyCalls, int? everySeconds)
    {
        if (enabled.HasValue) PerfLog = enabled.Value;
        // ★ v2.6.76：注释与实现对齐 —— **显式 0/负数 = 恢复旧行为 500**（缺省 null 才用默认 10）
        if (everyCalls.HasValue) ReportEvery = everyCalls.Value > 0 ? everyCalls.Value : 500;
        if (everySeconds.HasValue && everySeconds.Value > 0) ReportEverySeconds = everySeconds.Value;
    }

    /// <summary>★ v2.6.73【G6】改用 `Interlocked` 原子累加（**不再加锁**）；读取用 `Volatile.Read`。</summary>
    private sealed class Stat
    {
        public long Count;
        public long Total;
        public long Max;
        public long LastPrintedCount;     // ★ v2.6.75：上次打印时的计数（时间兜底用）
        public long LastPrintedTicks;     // ★ v2.6.75：上次打印的时间戳（Stopwatch）
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Stat> _stats = new(StringComparer.Ordinal);

    /// <summary>
    /// 是否计时。★ v2.6.76：**唯一开关 = `config.json` 的 `perfLog`**（缺省 false），
    /// 不再跟随 `logLevel` —— 关闭时调用方那次 `Start()` 返回 0 ⇒ `End()` 立即返回 ⇒ 零开销。
    /// </summary>
    public static bool Enabled { get { try { return PerfLog; } catch { return false; } } }

    /// <summary>取起始时间戳（关闭时返回 0 ⇒ `End` 会直接返回）。</summary>
    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>记账；每 `ReportEvery` 次打一条聚合日志。</summary>
    public static void End(string tag, long t0)
    {
        if (t0 == 0L) return;
        try
        {
            long dt = Stopwatch.GetTimestamp() - t0;
            // ★ v2.6.73【G6】无锁累加：`Interlocked.Increment` / `Interlocked.Add(ref long)`；
            //   最大值用"比较交换"循环（**计数语义与加锁版完全一致**，只是不再抢锁）。
            var st = _stats.GetOrAdd(tag, static _ => new Stat());
            long n = System.Threading.Interlocked.Increment(ref st.Count);
            System.Threading.Interlocked.Add(ref st.Total, dt);
            long cur = System.Threading.Volatile.Read(ref st.Max);
            while (dt > cur)
            {
                long seen = System.Threading.Interlocked.CompareExchange(ref st.Max, dt, cur);
                if (seen == cur) break;
                cur = seen;
            }
            // ★ v2.6.75：首见即报 / 每 N 次 / 时间兜底（三者取或）
            long nowTs = Stopwatch.GetTimestamp();
            long lastPrinted = System.Threading.Interlocked.Read(ref st.LastPrintedTicks);
            long lastCnt = System.Threading.Interlocked.Read(ref st.LastPrintedCount);
            bool dueByCount = n == 1 || (ReportEvery > 0 && n % ReportEvery == 0);
            bool dueByTime = lastPrinted == 0
                             ? false
                             : (nowTs - lastPrinted) * 1.0 / Stopwatch.Frequency >= ReportEverySeconds && n > lastCnt;
            if (!dueByCount && !dueByTime) return;
            System.Threading.Interlocked.Exchange(ref st.LastPrintedCount, n);
            System.Threading.Interlocked.Exchange(ref st.LastPrintedTicks, nowTs);
            long totalTicks = System.Threading.Volatile.Read(ref st.Total);
            long maxTicks = System.Threading.Volatile.Read(ref st.Max);
            double msTotal = totalTicks * 1000.0 / Stopwatch.Frequency;
            double usAvg = totalTicks * 1_000_000.0 / Stopwatch.Frequency / n;
            double msMax = maxTicks * 1000.0 / Stopwatch.Frequency;
            string line = $"[EventRotator][perf] {tag,-26} n={n,-7} 总={msTotal:0.0}ms  平均={usAvg:0.0}µs  最大={msMax:0.00}ms"
                        + $"  (每 {ReportEvery} 次 或 每 {ReportEverySeconds}s 一条，首见必报；由 config.json 的 perfLog=true 开启)";
            Emit(line);
        }
        catch { }
    }

    /// <summary>
    /// ★★★ v2.6.76【施工要求】`perfLog: true` 时**强制输出**，**不依赖 `logLevel`**。
    ///
    /// <para>为什么不能用 `EventEngine.Debug(line)`：它被 `DebugLogging = (logLevel != "off")` 门控
    /// ⇒ 用户把 `logLevel` 设成 `off` 时要诊断就"开了开关却一行都看不到"。</para>
    /// <para>为什么直接写 `Info`：SPT 的 logger 对 `Info` 不按本 mod 的等级过滤；
    /// 而**只有** `perfLog=true` 才会走到这里（`Start()` 返回 0 时 `End()` 第一行就 return）⇒ 平时零输出。</para>
    /// </summary>
    private static void Emit(string line)
    {
        try { EventEngine.Log?.Info(line); }
        catch { }
    }

    /// <summary>探针/排障：取某个 tag 的聚合快照（`n|总ticks|最大ticks`）。</summary>
    internal static string Snapshot(string tag)
    {
        return _stats.TryGetValue(tag, out var s)
            ? $"{System.Threading.Volatile.Read(ref s.Count)}|{System.Threading.Volatile.Read(ref s.Total)}|{System.Threading.Volatile.Read(ref s.Max)}"
            : "0|0|0";
    }
}
