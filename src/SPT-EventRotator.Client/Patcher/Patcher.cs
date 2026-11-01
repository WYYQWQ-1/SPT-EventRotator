using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Mono.Cecil;

/// <summary>
/// ★★★ BepInEx 5 预加载补丁：给客户端 <c>EFT.WildSpawnType</c> 注册自定义角色
/// <c>bossScourge</c>(13790)、<c>followerScourge</c>(13791)（灾厄小队冠军 / 小弟）
/// 以及 <c>cleanupcrew</c>(13792)、<c>pillager</c>(13793)、<c>bloodhound</c>(13794)
/// （清洁队 / 劫掠者 / 寻血猎犬，第 11 项，克隆源 arenaFighterEvent）
/// 和 <c>gus</c>(13795)、<c>basmach</c>(13796)（两个军火商，第 9 项，克隆源 arenaFighterEvent）。
///
/// <para><b>为什么必须是 preloader patcher</b>：<c>EFT.WildSpawnType</c> 是编译进
/// <c>Assembly-CSharp.dll</c> 的**元数据枚举**，运行时无法给已加载的枚举追加成员。全链路里有两处
/// 会按**名字**解析它，缺一个就炸：
/// <list type="number">
///   <item><c>BossLocationSpawn.ParseBossName()</c> / <c>ParseExcortType()</c>
///     （Assembly-CSharp:136127-136136）直接 <c>Enum.Parse(typeof(WildSpawnType), BossName)</c> ——
///     SPT 服务端把地图数据里的角色名原样下发 ⇒ 未知名字直接抛异常（进不了图）。</item>
///   <item>服务端返回的 bot profile 里 <c>Info.Settings.Role</c> 是字符串（SPT 服务端模型
///     <c>BotInfoSettings.Role</c> 是 string），客户端用 SPT 的 <c>EnumConverter&lt;WildSpawnType&gt;</c>
///     按名字反序列化（其实是 <c>EnumHelper&lt;WildSpawnType&gt;</c> 反射表）⇒ 同样要求枚举里有这个名字。</item>
/// </list>
/// 现实做法与 ISB / 黑狐 / MoreBotsAPI 完全一致：BepInEx preloader patcher 用 Mono.Cecil 在
/// 程序集**载入前**给枚举加 field。
/// <list type="bullet">
///   <item>ISB：<c>BepInEx/patchers/ISBSpecialForces.dll</c> 调
///     <c>CustomWildSpawnTypeManager.RegisterWildSpawnType(...)</c> → <c>EnumUtils.AddEnumValue</c>
///     → <c>type.Fields.Add(new FieldDefinition(name, Public|Static|Literal|HasDefault, type){Constant=value})</c>
///     （MoreBotsPrepatch 反编译 :180-185 / :245-254；ISB 只是复用了 MoreBotsAPI 的这套机制）。</item>
///   <item>黑狐：<c>BepInEx/patchers/BlackDiv.dll</c> 同样调 MoreBotsAPI，数值 848420-848426。</item>
/// </list>
/// ★ 本文件是**我们自己的实现**，不依赖 ISB / 黑狐 / MoreBotsAPI。
/// </para>
///
/// <para><b>数值选择</b>：冠军 <c>13790</c>、小弟 <c>13791</c>（连续两位，方便辨认）；
/// 第 11 项再追加 <c>cleanupcrew</c> = <c>13792</c>、<c>pillager</c> = <c>13793</c>、<c>bloodhound</c> = <c>13794</c>，
/// 第 9 项追加 <c>gus</c> = <c>13795</c>、<c>basmach</c> = <c>13796</c>（同样连续）。
/// 避开 ISB 已用 13701-13716、黑狐 848420-848426、MoreBotsAPI 从 1000 自增。
/// 必须与 ① <c>CustomBotTypes</c> 里各角色的枚举常量
/// ② <c>user/patchers/com.kirisame.eventrotator/EventRotatorEnums.json</c>
/// ③ 本文件的 <see cref="Roles"/> 三处一致。</para>
/// </summary>
public static class EventRotatorPatcher
{
    /// <summary>冠军角色名（= db 文件名、= 服务端/客户端枚举名）。</summary>
    public const string BossRoleName = "bossScourge";
    public const int BossRoleValue = 13790;

    /// <summary>小弟（护卫）角色名。</summary>
    public const string FollowerRoleName = "followerScourge";
    public const int FollowerRoleValue = 13791;

    /// <summary>★ 第 11 项：清洁队（Arena Cleanup Crew）—— 克隆源 arenaFighterEvent。</summary>
    public const string CleanupRoleName = "cleanupcrew";
    public const int CleanupRoleValue = 13792;

    /// <summary>★ 第 11 项：劫掠者（Pillagers）—— 克隆源 arenaFighterEvent。</summary>
    public const string PillagerRoleName = "pillager";
    public const int PillagerRoleValue = 13793;

    /// <summary>★ 第 11 项追加：寻血猎犬（Bloodhound）—— 克隆源 arenaFighterEvent。</summary>
    public const string BloodhoundRoleName = "bloodhound";
    public const int BloodhoundRoleValue = 13794;

    /// <summary>★ 第 9 项：军火商 Gus —— 克隆源 arenaFighterEvent（2 人一组）。</summary>
    public const string GusRoleName = "gus";
    public const int GusRoleValue = 13795;

    /// <summary>★ 第 9 项：军火商 Basmach —— 克隆源 arenaFighterEvent（2 人一组）。</summary>
    public const string BasmachRoleName = "basmach";
    public const int BasmachRoleValue = 13796;

    /// <summary>要注册的全部（名字, 数值）。加角色只改这里 + 服务端 2 处。</summary>
    private static readonly (string Name, int Value)[] Roles =
    {
        (BossRoleName, BossRoleValue),
        (FollowerRoleName, FollowerRoleValue),
        (CleanupRoleName, CleanupRoleValue),
        (PillagerRoleName, PillagerRoleValue),
        (BloodhoundRoleName, BloodhoundRoleValue),
        (GusRoleName, GusRoleValue),
        (BasmachRoleName, BasmachRoleValue),
    };

    /// <summary>预加载阶段没有插件 Logger ⇒ 每次现建一个 log source；同时写 console 双保险。</summary>
    private static void Note(string msg)
    {
        try { Console.WriteLine(msg); } catch { }
        try { Logger.CreateLogSource("EventRotator.Patcher").LogMessage(msg); } catch { }
    }

    /// <summary>BepInEx preloader 发现规则要求：public static IEnumerable&lt;string&gt; TargetDLLs。</summary>
    public static IEnumerable<string> TargetDLLs => new[] { "Assembly-CSharp.dll" };

    /// <summary>BepInEx preloader 发现规则要求：public static void Patch(AssemblyDefinition)。</summary>
    public static void Patch(AssemblyDefinition assembly)
    {
        try
        {
            if (assembly?.MainModule == null) return;

            // ★★ 2026-10-31【关了 mod 就进不了游戏】用户把 mod 目录移到 mods.disabled 后，
            //   本预补丁仍在预加载阶段往 EFT.WildSpawnType 注册 bossScourge/13790 等 7 个自定义角色，
            //   而定义这些角色的客户端插件/服务端数据已经不在了 ⇒ 客户端启动阶段报错、进不了游戏。
            //   这里先确认"mod 本体还在"，不在就整段跳过（并把原因写进日志，方便排查）。
            if (!ModPresent())
            {
                Note("[EventRotator.Patcher] mod 本体不在（已禁用/未安装）⇒ 跳过自定义角色注册"
                     + "（bossScourge / followerScourge / cleanupcrew / pillager / bloodhound / gus / basmach）");
                return;
            }

            // 先按 ISB 的写法取；找不到再退化按类型名扫（不同版本命名空间可能不同）
            var enumType = assembly.MainModule.GetType("EFT.WildSpawnType")
                           ?? assembly.MainModule.Types.FirstOrDefault(t => t.IsEnum && t.Name == "WildSpawnType");
            if (enumType == null || !enumType.IsEnum)
            {
                Note("[EventRotator.Patcher] ⚠ 找不到 EFT.WildSpawnType，跳过 " +
                     string.Join(" / ", Roles.Select(r => r.Name)) + " 注册");
                return;
            }

            foreach (var role in Roles)
                RegisterOne(enumType, role.Name, role.Value);
        }
        catch (Exception ex)
        {
            // 预加载阶段抛异常会拖垮整个客户端启动 ⇒ 只记录，不抛。
            Note("[EventRotator.Patcher] ❌ 注册自定义角色失败（bossScourge / followerScourge / cleanupcrew / pillager / bloodhound / gus / basmach）: " + ex);
        }
    }

    /// <summary>
    /// 判断 mod 本体是否还在（客户端插件 dll 或服务端 mod 目录任一存在即算在）。
    /// 预加载阶段拿不到 BepInEx 的 Paths，这里从预补丁自身位置反推游戏根目录：
    ///   &lt;game&gt;\BepInEx\patchers\SPT-EventRotator.Patcher.dll → 上溯 3 级得到 &lt;game&gt;。
    /// </summary>
    private static bool ModPresent()
    {
        try
        {
            var self = typeof(EventRotatorPatcher).Assembly.Location;
            if (string.IsNullOrEmpty(self)) return true;           // 取不到位置就不拦（保守）
            var game = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(self) ?? ".", "..", ".."));
            var plugin = System.IO.Path.Combine(game, "BepInEx", "plugins", "SPT-EventRotator.Client", "SPT-EventRotator.Client.dll");
            var server = System.IO.Path.Combine(game, "SPT_Runtime", "user", "mods", "SPT-EventRotator");
            return System.IO.File.Exists(plugin) || System.IO.Directory.Exists(server);
        }
        catch { return true; }
    }

    /// <summary>注册单个枚举成员（幂等 + 数值防撞，逐个角色独立判定）。</summary>
    private static void RegisterOne(TypeDefinition enumType, string roleName, int roleValue)
    {
        try
        {
            // 幂等：用户重复安装 / 其它 mod 已经注册过同名成员
            if (enumType.Fields.Any(f => string.Equals(f.Name, roleName, StringComparison.Ordinal)))
            {
                Note($"[EventRotator.Patcher] '{roleName}' 已存在于 {enumType.FullName}，跳过");
                return;
            }

            // 数值也不能撞（其它 mod 用了同一个数 ⇒ 枚举 ToString 二义、脚本/难度表会错）
            foreach (var f in enumType.Fields)
            {
                if (!f.IsLiteral || f.Constant == null) continue;
                try
                {
                    if (Convert.ToInt64(f.Constant) == roleValue)
                    {
                        Note($"[EventRotator.Patcher] ⚠ 数值 {roleValue} 已被 {enumType.FullName}.{f.Name} 占用，跳过 '{roleName}' 注册" +
                             "（请改用别的值，并同步 3 处常量）");
                        return;
                    }
                }
                catch { }
            }

            var field = new FieldDefinition(
                roleName,
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
                enumType)
            {
                Constant = roleValue   // 底层是 int（Assembly-CSharp 里 EFT.WildSpawnType : int）
            };
            enumType.Fields.Add(field);
            Note($"[EventRotator.Patcher] ✔ 已给 {enumType.FullName} 注册 {roleName} = {roleValue}");
        }
        catch (Exception ex)
        {
            Note($"[EventRotator.Patcher] ❌ 注册 '{roleName}' 失败: " + ex);
        }
    }
}
