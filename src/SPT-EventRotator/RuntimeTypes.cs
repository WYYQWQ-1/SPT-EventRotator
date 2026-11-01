using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

namespace EventRotator;

/// <summary>
/// ★★★ v1.0.2【实机根因修复】**只从"运行时真正在跑的那份" SPTarkov.Server.Core 解析类型**。
///
/// <para><b>病根（2026-10-24 实机日志逐字）</b>：</para>
/// <para>SPT 4.1.6 的 <c>ModLoader.RunModLoader</c> 在**存在 enum prepatch** 时会先跑一趟
/// <c>ApplyPrepatchesInMemory</c> + <c>BootPatchedServerInMemory</c>
/// （<c>SPT.Server.decompiled.cs:575-596</c> / <c>:729-736</c>）：
/// 外层进程把磁盘上的 <c>SPTarkov.Server.Core.dll</c> 载进 **Default ALC**，
/// 再把打过补丁的字节流用 <c>PrepatchLoadContext</c>（ALC 名 = <c>SPT.PrepatchHost</c>）
/// **重新载入一份**并启动真正的服务端（<c>SPT.Server.decompiled.cs:1160-1213</c>）。
/// 本 mod 自身带 enum prepatch（<c>user/patchers/com.kirisame.eventrotator/EventRotatorEnums.json</c>，
/// 日志第 45 行实证 "Applying enum prepatch definitions"）⇒ **进程里同时存在两份同名同版本的
/// <c>SPTarkov.Server.Core</c>**。</para>
///
/// <para>而 <c>HarmonyLib.AccessTools.TypeByName</c> 只按
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> 的**装载顺序**取第一个命中 ——
/// 外层那份（Default ALC、磁盘、**没有 enum prepatch**）先装载 ⇒ 它命中**死的那份**：
/// 补丁挂成功、<c>GetPatchedMethods</c> 也包含它、日志一切正常，但服务端根本不会调用它
/// ⇒ **无异常、无日志、postfix 永不触发**。</para>
///
/// <para><b>实机证据（spt20261024.log）</b>：</para>
/// <list type="bullet">
/// <item><c>:4309</c> 「钩子身份：…assembly=SPTarkov.Server.Core v4.1.6.0｜<b>ALC=Default</b>｜…GetPatchedMethods 含它=True」；</item>
/// <item><c>:01:20:23</c> SPT 自己打了 <c>LocationLootGenerator</c> 的
///   「A total of: 1857 static items spawned / 共计313个静态容器已生成」⇒ <c>GenerateStaticContainers</c> 明明执行了；</item>
/// <item>同一份日志里 <c>[EventRotator][容器注入]</c> 的「生成钩子已到达」/「[面具修正][诊断]」= **0 条**；</item>
/// <item>反证：<c>[自定义bot类型]</c> 的枚举自检用 <c>botTable.GetType().Assembly</c>（运行期那份）查到
///   <c>bossScourge</c> = ✔ ⇒ 真正在跑的那份**带 enum 补丁**，而 <c>AccessTools</c> 给的是 Default 那份（不带）⇒ 两者不是同一份。</item>
/// </list>
///
/// <para><b>正解</b>：以 DI 实例的 <c>GetType().Assembly</c> 为"运行时 Core"（<see cref="Init"/>），
/// 所有给 Harmony 用的 SPT 类型都经 <see cref="Find"/> 从它身上取；本 mod 自己的 ALC 里已装载的
/// 其它 mod 程序集（ABPS/PBS/SAIN…）作为第二优先；最后才回落 <c>AccessTools.TypeByName</c>。</para>
///
/// <para>⚠ 这**不是**新发明：项目里已经踩过同一坑两次并各自打了补丁 ——
/// <c>QuestListFilterPatch</c> 的 <c>QuestHelperInstance</c>（v1.0.1 注释「prepatch 兼容·真因」）
/// 与 <c>CustomBotTypes.VerifyServerEnum</c>（<c>botTable.GetType().Assembly</c>）。
/// 本文件把这条规则收成**唯一入口**，防止以后再有人新挂补丁时又用回 <c>AccessTools.TypeByName</c>。</para>
/// </summary>
internal static class RuntimeTypes
{
    /// <summary>运行时真正在执行的 <c>SPTarkov.Server.Core</c>（由 <see cref="Init"/> 定下来）。</summary>
    internal static Assembly LiveCoreAssembly;

    /// <summary>本 mod 被载入的那个 ALC（正常 = <c>SPT.PrepatchHost</c>；没有 enum prepatch 时 = <c>Default</c>）。</summary>
    internal static AssemblyLoadContext ModAlc
    {
        get
        {
            try { return AssemblyLoadContext.GetLoadContext(typeof(RuntimeTypes).Assembly); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 由 <c>Plugin.OnLoadAsync</c> **第一行**调用。优先用 DI 注入实例的 <c>GetType().Assembly</c>
    /// （= 服务端此刻真正在用的那份），拿不到就回落 <c>typeof(MongoId).Assembly</c>
    /// —— 本 mod 的编译期引用会经它自己的 ALC 解析，同样指向运行时那份。
    /// </summary>
    internal static void Init(object diInstance, Action<string> log = null)
    {
        string source;
        try
        {
            var a = diInstance?.GetType().Assembly;
            if (a != null && string.Equals(a.GetName().Name, "SPTarkov.Server.Core", StringComparison.OrdinalIgnoreCase))
            {
                LiveCoreAssembly = a;
                source = "DI 实例 " + diInstance.GetType().FullName;
            }
            else
            {
                LiveCoreAssembly = typeof(SPTarkov.Server.Core.Models.Common.MongoId).Assembly;
                source = "typeof(MongoId).Assembly（编译期引用）";
            }
        }
        catch
        {
            LiveCoreAssembly = typeof(SPTarkov.Server.Core.Models.Common.MongoId).Assembly;
            source = "typeof(MongoId).Assembly（异常回落）";
        }

        try
        {
            log?.Invoke($"[EventRotator][类型解析] 运行时 Core 已锁定：{source}｜{Describe(LiveCoreAssembly)}");
            // ★ 关键对照：本 mod 的编译期引用（typeof(MongoId)）落在哪一份？
            //   正常（mod 被载入 SPT.PrepatchHost ALC）应与运行时那份**同一个实例**；
            //   若这里出现 "同一份=False" ⇒ 说明连编译期引用都指向了死副本，必须立即上报（否则修法也不成立）。
            try
            {
                var byRef = typeof(SPTarkov.Server.Core.Models.Common.MongoId).Assembly;
                log?.Invoke($"[EventRotator][类型解析] 编译期引用 typeof(MongoId).Assembly：{Describe(byRef)}"
                          + $"｜与运行时同一份={ReferenceEquals(byRef, LiveCoreAssembly)}");
            }
            catch { }

            var copies = CoreCopies();
            if (copies.Count > 1)
            {
                log?.Invoke($"[EventRotator][类型解析] ⚠ 进程内有 {copies.Count} 份 SPTarkov.Server.Core（prepatch 双载）："
                          + string.Join(" ｜ ", copies.Select((c, i) => $"#{i + 1} {Describe(c)}{(ReferenceEquals(c, LiveCoreAssembly) ? " ←运行时" : " ←死副本")}")));
            }
            else
            {
                log?.Invoke($"[EventRotator][类型解析] 进程内 SPTarkov.Server.Core 只有 1 份 ⇒ 不存在 prepatch 双载问题");
            }
        }
        catch { }
    }

    /// <summary>
    /// 按**全名**解析 SPT / mod 类型：① 运行时 Core → ② 本 mod 所在 ALC → ③ <c>AccessTools</c> 兜底。
    /// 返回 null 表示三路都没找到（调用方按"降级不挂补丁"处理）。
    /// </summary>
    internal static Type Find(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;

        // ① 运行时 Core（唯一权威）—— 这一步就能把"挂到死副本"彻底堵死
        try
        {
            var t = LiveCoreAssembly?.GetType(fullName, false);
            if (t != null) return t;
        }
        catch { }

        // ② 本 mod 的 ALC 里已经装载的其它程序集（ABPS / PBS / SAIN / Massivesoft… 与我们同 ALC）
        try
        {
            var alc = ModAlc;
            if (alc != null)
            {
                foreach (var a in alc.Assemblies)
                {
                    try
                    {
                        var t = a.GetType(fullName, false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
        }
        catch { }

        // ③ 兜底：保持旧行为（可能命中死副本，但至少不是"完全找不到"）
        try { return AccessTools.TypeByName(fullName); }
        catch { return null; }
    }

    /// <summary>按**短名**在本 mod 的 ALC 内找（给老代码的 <c>FindType(simpleName)</c> 兜底用）。</summary>
    internal static Type FindSimple(string simpleName)
    {
        if (string.IsNullOrWhiteSpace(simpleName)) return null;
        try
        {
            var t = LiveCoreAssembly?.GetTypes().FirstOrDefault(x => string.Equals(x.Name, simpleName, StringComparison.Ordinal));
            if (t != null) return t;
        }
        catch { }
        try
        {
            var alc = ModAlc;
            if (alc != null)
                foreach (var a in alc.Assemblies)
                {
                    try
                    {
                        var t = a.GetTypes().FirstOrDefault(x => string.Equals(x.Name, simpleName, StringComparison.Ordinal));
                        if (t != null) return t;
                    }
                    catch { }
                }
        }
        catch { }
        return null;
    }

    /// <summary>人读的程序集身份：assembly 名/版本 + ALC + Location + MVID（**同一份**比较靠它）。</summary>
    internal static string Describe(Assembly a)
    {
        if (a == null) return "(null)";
        try
        {
            var n = a.GetName();
            var alc = AssemblyLoadContext.GetLoadContext(a)?.Name ?? "(none)";
            var loc = string.IsNullOrEmpty(a.Location) ? "(内存/流载入)" : a.Location;
            var mvid = a.ManifestModule?.ModuleVersionId.ToString() ?? "?";
            return $"assembly={n.Name} v{n.Version}｜ALC={alc}｜Location={loc}｜MVID={mvid}";
        }
        catch (Exception ex) { return "(describe failed: " + ex.GetType().Name + ")"; }
    }

    /// <summary>进程内所有名为 <c>SPTarkov.Server.Core</c> 的程序集（prepatch 双载时应有 2 份）。</summary>
    internal static List<Assembly> CoreCopies()
    {
        var list = new List<Assembly>();
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(a.GetName().Name, "SPTarkov.Server.Core", StringComparison.OrdinalIgnoreCase))
                        list.Add(a);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }
}
