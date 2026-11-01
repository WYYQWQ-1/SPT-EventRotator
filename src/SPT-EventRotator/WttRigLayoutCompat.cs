using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ WTT-ContentBackport <b>rig layout 缺失</b>的运行期兼容（2026-10-24 实机 bug 驱动）。
///
/// <para><b>症状（用户实机复现）</b>：打死 <c>bossScourge</c> / <c>followerScourge</c> 去搜尸体，
/// 「战术胸挂」那一格会显示 2、3、5 条同名弹挂（<c>PLATEminus V2</c> / <c>SD mod.2</c>），
/// 每件耐久各不相同，而且**每点开一次尸体就再多一个**；把它们丢到地上后，其它物品才露出来。
/// 拖到地上只得到 **1 件**（⇒ 服务端数据从头到尾只有 1 件，是**客户端把同一件反复新建视图**）。</para>
///
/// <para><b>根因（已用本地数据 vs 上游仓库逐字段对照证实）</b>：
/// WTT-ContentBackport 2.0.4 的 <c>update9-20-26</c> 那批新弹挂，其
/// <c>_props.RigLayoutName</c> 指向的预制体**不在本地 bundle 里**（数据里还写错成了别的型号的 layout，
/// 例如 Coyote 变体写的是 <c>plateminus_extreme</c>）。客户端是拿 <c>Grids[]._name</c> 去
/// <c>RigLayoutName</c> 那个预制体里**按名字找格子**的，找不到 ⇒ 容器视图建不出来 ⇒
/// 每次打开/搜索这件弹挂就再新建一个（坏的）视图 ⇒ 表现为"越搜越多"。</para>
///
/// <para><b>上游什么时候修的</b>：WTT-Backport 提交 <c>2f54edf0</c>
/// 「Rig Layout Fixes for update09-20-26」（合并进 PR#12 <c>0e3d91f7</c>），**随 2.0.5 发布**。
/// 该提交只有 4 个文件，其中两个是关键：
/// <list type="bullet">
///   <item><b>新增</b> <c>db/CustomRigLayouts/arena_s3_layouts.bundle</c>（真·rig layout 预制体）</item>
///   <item>改 <c>db/CustomItems/update9-20-26/vest_config.json</c>（<c>RigLayoutName</c> 改成
///     <c>bfg_plateminus_champion</c> / <c>bfg_plateminus_coyote</c> / <c>stich_mod2_cleanup</c> / <c>bp_d3crx_v2</c>，
///     <c>Grids[]._name</c> 改成 <c>GridView (1..n)</c>）</item>
/// </list>
/// ⚠ <b>关键结论</b>：在 2.0.4 上"只改数据救外观"是**不可能的** —— 对的 layout 资源本地压根没有。
/// 所以本兼容层不假装能修外观，它做的是**把这几款坏弹挂从 Scourge 的装备池里换掉**，
/// 换成上游这次没动 layout、可用的同批弹挂（<c>ANA Tactical M2</c> / <c>Thunderbolt</c>）。</para>
///
/// <para><b>★ 版本识别（用户要求：别人装的 WTT 比我们新就不要加载这个修复）</b>：
/// 判据按"**能力探测优先、版本号兜底**"，三条任一成立 ⇒ **不加载**（warn 一行说明）：
/// <list type="number">
///   <item><c>db/CustomRigLayouts/arena_s3_layouts.bundle</c> 存在（修复版资源已在盘上）；</item>
///   <item><c>vest_config.json</c> 里出现修复后的 layout 名（<c>bfg_plateminus_champion</c> /
///     <c>stich_mod2_cleanup</c> / <c>bp_d3crx_v2</c> / <c>GridView (</c>）—— 兼容"改了数据但没升版本号"的 fork；</item>
///   <item>DLL 的 <c>ProductVersion</c> / <c>FileVersion</c> ≥ <b>2.0.5</b>（本机实测 2.0.4 = <c>2.0.4+c5cd4013</c>）。</item>
/// </list>
/// 三条都不成立才动手；任何一步拿不到（没装 WTT、目录算不出来、JSON 读不了）⇒ **只打日志、什么都不改**（降级不炸）。</para>
///
/// <para><b>幂等</b>：<see cref="_applied"/> 只跑一次；池子按 tpl 字符串比对，重复调用结果一致。
/// <b>可还原</b>：换掉/加进去的每一条都记进 <see cref="_restore"/>，<see cref="Restore"/> 逆序回放。
/// <b>不碰别人的东西</b>：只写运行期 <c>BotTable.Types[role].BotInventory.Equipment[TacticalVest]</c>
/// 这一张按角色分键的池子，**不写盘、不改 WTT 的任何文件**。</para>
/// </summary>
internal static class WttRigLayoutCompat
{
    // ───────────────────────── 常量：WTT 侧的路径与阈值 ─────────────────────────

    private const string WttDirName = "WTT-ContentBackport";
    private const string WttDllName = "WTT-ContentBackport.dll";

    /// <summary>2.0.5 新增的真·rig layout 预制体（本机 2.0.4 没有这个文件）。</summary>
    private static readonly string LayoutBundleRel =
        Path.Combine("db", "CustomRigLayouts", "arena_s3_layouts.bundle");

    /// <summary>这批新弹挂的数据文件（修复提交改的就是它）。</summary>
    private static readonly string VestConfigRel =
        Path.Combine("db", "CustomItems", "update9-20-26", "vest_config.json");

    /// <summary>修复后的 layout 名（只要出现其一就认为是修复版；兼容"改了数据但没升版本号"的 fork）。</summary>
    private static readonly string[] FixedLayoutMarkers =
    {
        "bfg_plateminus_champion",
        "bfg_plateminus_coyote",
        "stich_mod2_cleanup",
        "bp_d3crx_v2",
        "GridView (",
    };

    /// <summary>含修复提交的最低 WTT 版本（本机实测 2.0.4 = <c>2.0.4+c5cd4013</c>，是修复提交 <c>2f54edf0</c> 的父提交 <c>c5cd4013</c> 构建的）。</summary>
    private static readonly Version FixedFrom = new Version(2, 0, 5);

    // ───────────────────────── 弹挂 tpl 名单（逐字，来自本地 vest_config.json） ─────────────────────────

    /// <summary>
    /// 上游 <c>2f54edf0</c> 改了 <c>RigLayoutName</c>/<c>Grids</c> 的那 5 个 tpl ⇒ 在未修复的 WTT 上客户端渲染不出来。
    /// （上游没动 <c>6a5629df…</c>/<c>6a565b5f…</c>，所以它们不在名单里，可以直接当替补。）
    /// </summary>
    private static readonly string[] BrokenTpls =
    {
        "6a7c8432f5f1ecf7b1033aee",   // Blue Force Gear PLATEminus V2 Plate Carrier (Scourge)   layout: plateminus_champion  → bfg_plateminus_champion
        "6a7c845c1fa6226c17096359",   // Blue Force Gear PLATEminus V2 Plate Carrier (Coyote Brown) layout: plateminus_extreme → bfg_plateminus_coyote
        "6a7c85022369e59dd307130c",   // Haley Strategic D3CRX Chest Harness (MultiCam)               layout: D3crx_v2        → bp_d3crx_v2（clone 也从 68a85ac6→5d5d85c5）
        "6a7c8531bcddc78fbd0f04c1",   // Stich Defense mod.2 plate carrier (Cleanup Crew)             layout: rig_stich_defence_v2 → stich_mod2_cleanup
        "6a7c855ba20fcf832f01ff78",   // Stich Defense mod.2 plate carrier (A-TACS FG)                layout: rig_stich_defence_v2 → stich_mod2_cleanup
    };

    /// <summary>替补（同批 update9-20-26 弹挂、上游**没有**改它们的 layout）：能装、没症状。</summary>
    private static readonly string[] SafeTpls =
    {
        "6a5629dfbd83cb160606681c",   // ANA Tactical M2 plate carrier (A-TACS iX)
        "6a565b5f9ec10bc6e30cdf46",   // Direct Action Thunderbolt compact chest rig (PenCott WildWood)
    };

    /// <summary>最后兜底：本体弹挂（万一 WTT 的替补也解析不出来，保证池子不空、bot 不会没弹挂）。</summary>
    private static readonly string[] VanillaFallbackTpls =
    {
        "592c2d1a86f7746dbe2af32a",   // ANA Tactical M1（本体）
        "5e4abfed86f77406a2713cf7",   // WARTECH TV-110（本体）
    };

    // ───────────────────────── 状态 ─────────────────────────

    private static bool _applied;

    /// <summary>逆序回放的还原动作（换掉的键值 / 加进去的键）。</summary>
    private static readonly List<Action> _restore = new();

    public static bool IsApplied => _applied;

    /// <summary>撤销：把池子恢复成我们动手之前的样子（排障用，幂等）。</summary>
    public static void Restore()
    {
        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            try { _restore[i](); } catch { }
        }
        _restore.Clear();
        _applied = false;
    }

    // ───────────────────────── 入口 ─────────────────────────

    /// <summary>
    /// 由 <see cref="CustomBotTypes.Install"/> 在 bot 类型注册（+ BotConfig 复制）之后调用。
    /// 拿不到 botTable / 算不出 WTT 目录 / 读不了文件 ⇒ 只打日志，绝不抛。
    /// </summary>
    public static void Apply(string modPath, object botTable, ISptLogger<EventRotatorServer> log)
    {
        if (_applied) return;
        _applied = true;
        try
        {
            var wttDir = ResolveWttDir(modPath);
            if (wttDir == null)
            {
                log?.Info("[EventRotator][WTT弹挂兼容] 没找到 WTT-ContentBackport 目录 ⇒ 跳过（不装 WTT 就没有这条问题）");
                return;
            }

            var version = ReadWttVersion(wttDir);
            var hasLayoutBundle = File.Exists(Path.Combine(wttDir, LayoutBundleRel));
            var vestCfg = Path.Combine(wttDir, VestConfigRel);
            var cfgLooksFixed = VestConfigLooksFixed(vestCfg);
            var versionFixed = version != null && version >= FixedFrom;

            log?.Info($"[EventRotator][WTT弹挂兼容] WTT 版本 = {(version == null ? "?（读不到）" : version.ToString())}"
                      + $"（修复从 {FixedFrom} 起）｜ arena_s3_layouts.bundle = {(hasLayoutBundle ? "有" : "**没有**")}"
                      + $"｜ vest_config 已含修复后 layout = {(cfgLooksFixed ? "是" : "否")}");

            if (hasLayoutBundle || cfgLooksFixed || versionFixed)
            {
                log?.Info("[EventRotator][WTT弹挂兼容] ✔ WTT 已带 rig layout 修复（版本或资源已就位）⇒ **不加载**本兼容修复");
                return;
            }

            // ── 到这里 = 未修复的 WTT（例如本机 2.0.4）⇒ 把坏弹挂从 Scourge 装备池里换掉 ──
            var types = Prop(botTable, "Types") as IDictionary;
            if (types == null)
            {
                log?.Warning("[EventRotator][WTT弹挂兼容] ⚠ 拿不到 BotTable.Types ⇒ 兼容修复未生效");
                return;
            }

            int roles = 0, removed = 0, added = 0;
            foreach (var role in CustomBotTypes.ScourgeRoles)
            {
                var pool = TacticalVestPool(types, role);
                if (pool == null)
                {
                    log?.Warning($"[EventRotator][WTT弹挂兼容] ⚠ '{role}' 找不到 Equipment[TacticalVest] 池子 ⇒ 跳过该角色");
                    continue;
                }
                roles++;

                // 1) 摘掉坏 tpl（先把「键 + 原权重」一起快照，还原时按原值写回）
                var bad = new List<KeyValuePair<object, object>>();
                foreach (DictionaryEntry de in pool)
                {
                    var tpl = de.Key?.ToString();
                    if (tpl != null && BrokenTpls.Any(b => string.Equals(b, tpl, StringComparison.OrdinalIgnoreCase)))
                        bad.Add(new KeyValuePair<object, object>(de.Key, de.Value));
                }
                foreach (var kv in bad)
                {
                    var capPool = pool; var capKey = kv.Key; var capVal = kv.Value;
                    _restore.Add(() => { try { capPool[capKey] = capVal; } catch { } });   // 还原 = 塞回原键 + 原权重
                    try { pool.Remove(kv.Key); removed++; } catch { }
                }

                // 2) 池子空了 ⇒ 先补 WTT 的可用替补，再不行兜本体
                if (pool.Count == 0)
                {
                    foreach (var tpl in SafeTpls) if (TryAdd(pool, tpl, out var _)) added++;
                    if (pool.Count == 0)
                    {
                        foreach (var tpl in VanillaFallbackTpls) if (TryAdd(pool, tpl, out var _)) added++;
                        log?.Warning($"[EventRotator][WTT弹挂兼容] ⚠ '{role}' 的替补 tpl 一个都解析不出来（WTT 装了但物品表里没有？）⇒ 退回本体弹挂");
                    }
                }

                log?.Info($"[EventRotator][WTT弹挂兼容] '{role}'：摘掉 {bad.Count} 个坏弹挂 tpl"
                          + $"{(bad.Count > 0 ? "（" + string.Join(", ", bad.Select(kv => kv.Key?.ToString())) + "）" : "")}"
                          + $"；池子现状 {pool.Count} 条"
                          + (pool.Count > 0 ? "：" + string.Join(", ", Keys(pool)) : ""));
            }

            if (removed > 0 || added > 0)
            {
                log?.Warning($"[EventRotator][WTT弹挂兼容] ★ 已规避「搜尸弹挂越搜越多」：{roles} 个角色共摘掉 {removed} 条、补入 {added} 条。"
                           + $" 根因在 WTT-ContentBackport {(version == null ? "" : version + " ")}的 update9-20-26 新弹挂缺少 rig layout"
                           + "（客户端按 Grids[]._name 去 RigLayoutName 预制体里找不到格子 ⇒ 每开一次多画一个）。"
                           + " 升级 WTT ≥ " + FixedFrom + "（或补上 db/CustomRigLayouts/arena_s3_layouts.bundle + 修好的 vest_config.json）"
                           + "后，本修复会**自动停用**。");
            }
            else
            {
                log?.Info("[EventRotator][WTT弹挂兼容] 待摘的坏 tpl 一个都不在池子里（可能已被别的数据改过）⇒ 无需改动");
            }
        }
        catch (Exception ex)
        {
            log?.Warning("[EventRotator][WTT弹挂兼容] 兼容处理失败（已忽略，不影响其它功能）：" + (ex.InnerException ?? ex).Message);
        }
    }

    // ───────────────────────── 探测：WTT 目录 / 版本 / 是否已修复 ─────────────────────────

    /// <summary>从我们的 modPath（<c>user/mods/SPT-EventRotator</c>）推同级的 <c>user/mods/WTT-ContentBackport</c>。</summary>
    private static string ResolveWttDir(string modPath)
    {
        try
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(modPath)) roots.Add(Path.GetFullPath(modPath));
            // 再兜一层：万一传进来的是 dll 路径
            if (!string.IsNullOrEmpty(modPath) && File.Exists(modPath)) roots.Add(Path.GetDirectoryName(Path.GetFullPath(modPath)));

            foreach (var r in roots)
            {
                var parent = Path.GetDirectoryName(r);
                if (string.IsNullOrEmpty(parent)) continue;
                var cand = Path.Combine(parent, WttDirName);
                if (Directory.Exists(cand)) return Path.GetFullPath(cand);
                // 少数整合包会把它放到再上一层
                var cand2 = Path.Combine(parent, "mods", WttDirName);
                if (Directory.Exists(cand2)) return Path.GetFullPath(cand2);
            }
        }
        catch { }
        return null;
    }

    /// <summary>读 WTT 的版本（本机实测 <c>ProductVersion = 2.0.4+c5cd4013</c>）。读不到返回 null。</summary>
    private static Version ReadWttVersion(string wttDir)
    {
        try
        {
            var dll = Path.Combine(wttDir, WttDllName);
            if (!File.Exists(dll)) return null;
            var info = FileVersionInfo.GetVersionInfo(dll);
            var raw = info.ProductVersion ?? info.FileVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // "2.0.4+c5cd4013" / "2.0.5" / "2.0.5.0" → 取前 2~3 段数字
            raw = raw.Split('+')[0].Trim();
            var parts = raw.Split('.');
            var nums = new List<int>();
            foreach (var p in parts)
            {
                if (nums.Count >= 3) break;
                if (int.TryParse(p.Trim(), out var n)) nums.Add(n); else break;
            }
            while (nums.Count < 2) nums.Add(0);
            return new Version(nums[0], nums[1], nums.Count > 2 ? nums[2] : 0);
        }
        catch { return null; }
    }

    /// <summary>vest_config.json 里出现任何一个"修复后"的 layout 名 ⇒ 认为数据已修（不解析整份 JSON，纯子串判定，抗格式变化）。</summary>
    private static bool VestConfigLooksFixed(string vestConfigPath)
    {
        try
        {
            if (!File.Exists(vestConfigPath)) return false;
            var txt = File.ReadAllText(vestConfigPath);
            foreach (var marker in FixedLayoutMarkers)
                if (txt.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
        }
        catch { }
        return false;
    }

    // ───────────────────────── 反射：拿池子 / 换条目 ─────────────────────────

    /// <summary><c>BotTable.Types[role].BotInventory.Equipment[TacticalVest]</c>（键是 <c>EquipmentSlots</c> 枚举）。</summary>
    private static IDictionary TacticalVestPool(IDictionary types, string role)
    {
        object botType = null;
        foreach (DictionaryEntry de in types)
            if (string.Equals(de.Key?.ToString(), role, StringComparison.OrdinalIgnoreCase)) { botType = de.Value; break; }
        if (botType == null) return null;

        var inv = Prop(botType, "BotInventory") ?? Prop(botType, "Inventory") ?? Prop(botType, "BotBaseInventory");
        if (inv == null) return null;
        if (!(Prop(inv, "Equipment") is IDictionary equip)) return null;

        foreach (DictionaryEntry de in equip)
            if (string.Equals(de.Key?.ToString(), "TacticalVest", StringComparison.OrdinalIgnoreCase))
                return de.Value as IDictionary;
        return null;
    }

    /// <summary>往池子里加一条（键按池子的真实泛型键类型构造；值类型可能是 double）。</summary>
    private static bool TryAdd(IDictionary pool, string tpl, out object key)
    {
        key = null;
        try
        {
            var gargs = pool.GetType().IsGenericType ? pool.GetType().GetGenericArguments() : null;
            if (gargs == null || gargs.Length < 2) return false;

            foreach (DictionaryEntry de in pool)
                if (string.Equals(de.Key?.ToString(), tpl, StringComparison.OrdinalIgnoreCase)) return false;   // 已存在

            key = MakeKey(gargs[0], tpl);
            if (key == null) return false;

            object val;
            try { val = Convert.ChangeType(1.0, gargs[1]); } catch { val = 1.0; }

            var capPool = pool; var capKey = key;
            _restore.Add(() => { try { if (capPool.Contains(capKey)) capPool.Remove(capKey); } catch { } });
            pool[key] = val;
            return true;
        }
        catch { return false; }
    }

    /// <summary>字符串 → 该字典的真实键类型（<c>MongoId</c> 走 string 构造；枚举走 Enum.Parse）。</summary>
    private static object MakeKey(Type keyType, string s)
    {
        if (keyType == null || string.IsNullOrEmpty(s)) return null;
        if (keyType == typeof(string)) return s;
        if (keyType.IsEnum)
        {
            try { return Enum.Parse(keyType, s, ignoreCase: true); } catch { return null; }
        }
        try { return Activator.CreateInstance(keyType, s); } catch { }
        try
        {
            var mi = keyType.GetMethod("op_Implicit", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (mi != null) return mi.Invoke(null, new object[] { s });
        }
        catch { }
        try { return Convert.ChangeType(s, keyType); } catch { }
        return null;
    }

    /// <summary>跨命名约定读属性（含 <c>[JsonPropertyName]</c> 反查；与 <see cref="EventBotTemplateFix"/> 同口径的简化版）。</summary>
    private static object Prop(object o, string name)
    {
        if (o == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var t = o.GetType();
            var stripped = name.TrimStart('_');
            var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                 ?? t.GetProperty(stripped, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (p != null) return p.GetValue(o);

            var props = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var q in props)
            {
                var jn = q.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                          .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name;
                if (jn != null && string.Equals(jn, name, StringComparison.OrdinalIgnoreCase)) return q.GetValue(o);
            }
            foreach (var q in props)
                if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(q.Name, stripped, StringComparison.OrdinalIgnoreCase))
                    return q.GetValue(o);
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> Keys(IDictionary d)
    {
        var list = new List<string>();
        try { foreach (DictionaryEntry de in d) list.Add(de.Key?.ToString()); } catch { }
        return list;
    }
}
