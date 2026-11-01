using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EventRotator;

/// <summary>
/// ★★★ 第 20 项【兼容性展示】：只读别的 mod 的配置，告诉玩家「这张图上还有谁在刷东西，可能与我们重叠」。
///
/// <para><b>硬边界</b>：**只读** —— 不写、不改、不依赖。文件不存在 / 坏 JSON / 字段缺失 ⇒ 该来源返回空表，
/// 最多在调试日志里留一条（本类不打印，由调用方决定）。</para>
///
/// <para><b>读谁</b>（路径相对 `&lt;SPT_Runtime&gt;/user/mods`，目录名允许轻微变体，见 <see cref="FindModDir"/>）：</para>
/// <list type="bullet">
/// <item><b>ABPS</b>（acidphantasm-botplacementsystem）：`config.json → bossConfig.&lt;role&gt;.{enable, spawnChance{map:pct}}`
///   ⇒ 每张图每个 boss 一条「ABPS 在本图刷 X N%」。</item>
/// <item><b>ISB</b>（ISB-SpecialForces）：`containment_breach_config.jsonc`（JSONC，带 `//` 注释）
///   `{enabled, chance, labsVariantsEnabled}` ⇒ 实验室一条。</item>
/// <item><b>Blackout</b>（Blackout）：`config.json → blackoutChance` ⇒ 全局一条。</item>
/// <item><b>ORBIT</b>（ORBIT）：`config.json` → `ambush.enabled`（全局一条）+ `map_overrides.&lt;map&gt;`（按图一条）。</item>
/// </list>
/// </summary>
internal static class CompatReport
{
    /// <summary>一条兼容性提示（与 ConfigWeb.EventRow.CompatNote 字段一一对应）。</summary>
    public sealed class Note
    {
        public string Source = "";    // ABPS | ISB | Blackout | ORBIT
        public string Map = "*";      // 地图 id；全局 "*"
        public string Text = "";      // 中文
        public string TextEn = "";    // English
    }

    private static readonly object _gate = new();
    private static bool _loaded;
    private static List<Note> _notes = new();

    /// <summary>本次扫描里各来源的失败原因（只用于日志/自检；空 = 全部读成功）。</summary>
    public static readonly List<string> Errors = new();

    /// <summary>自检/热重载用：丢掉已扫结果（生产不需要调）。</summary>
    public static void ResetCache()
    {
        lock (_gate)
        {
            _loaded = false;
            _notes = new List<Note>();
            Errors.Clear();
        }
    }

    /// <summary>角色 id → 中文/英文显示名（读不到就用 role 本身；不做 UI 花活）。</summary>
    private static readonly Dictionary<string, (string Zh, string En)> RoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bossKnight"] = ("Knight（鹅队）", "Knight"),
        ["bossBully"] = ("Reshala（暴徒）", "Reshala"),
        ["bossTagilla"] = ("Tagilla（锤哥）", "Tagilla"),
        ["bossKilla"] = ("Killa", "Killa"),
        ["bossZryachiy"] = ("Zryachiy", "Zryachiy"),
        ["bossGluhar"] = ("Gluhar（老格）", "Gluhar"),
        ["bossSanitar"] = ("Sanitar（医生）", "Sanitar"),
        ["bossKolontay"] = ("Kolontay", "Kolontay"),
        ["bossBoar"] = ("Kaban（野猪）", "Kaban"),
        ["bossKojaniy"] = ("Shturman（森林之子）", "Shturman"),
        ["bossPartisan"] = ("Partisan", "Partisan"),
        ["bossPriest"] = ("Cultist Priest（祭司）", "Cultist Priest"),
        ["bossSecretary"] = ("Cultist Secretary", "Cultist Secretary"),
        ["bossGuard"] = ("Cultist Guard", "Cultist Guard"),
    };

    /// <summary>地图 id → 中文名（大陆社区常用译法；面板直接显示）。</summary>
    private static string MapZh(string map) => map?.ToLowerInvariant() switch
    {
        "bigmap" => "海关",
        "woods" => "森林",
        "shoreline" => "海岸线",
        "interchange" => "立交桥",
        "rezervbase" => "储备站",
        "lighthouse" => "灯塔",
        "tarkovstreets" => "街区",
        "laboratory" => "实验室",
        "labyrinth" => "迷宫",
        "factory4_day" => "工厂（白天）",
        "factory4_night" => "工厂（夜晚）",
        "sandbox" => "沙盒",
        "sandbox_high" => "沙盒（高难度）",
        "terminal" => "终端站",
        "hideout" => "藏身处",
        _ => map ?? "",
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  对外
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 该活动相关的兼容性提示：全局（Map="*"）全给；按图的只给**本活动涉及的地图**。
    /// 空表 = 没装第三方 mod / 配置读不到 / 不涉及。
    /// </summary>
    public static List<Note> NotesFor(IEnumerable<string> maps)
    {
        EnsureLoaded();
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in maps ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(m)) want.Add(m.Trim().ToLowerInvariant());

        var list = new List<Note>();
        foreach (var n in _notes)
        {
            if (n.Map == "*") { list.Add(n); continue; }
            if (want.Contains(n.Map)) list.Add(n);
        }
        return list;
    }

    /// <summary>调试/自检用：所有读到的提示（不分地图）。</summary>
    public static List<Note> AllNotes()
    {
        EnsureLoaded();
        return new List<Note>(_notes);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  扫描（一次启动一遍；每个来源独立 try/catch）
    // ══════════════════════════════════════════════════════════════════════════

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            var notes = new List<Note>();
            Errors.Clear();
            foreach (var (name, loader) in new (string, Func<List<Note>>)[]
                     {
                         ("ABPS", LoadAbps), ("ISB", LoadIsb), ("Blackout", LoadBlackout), ("ORBIT", LoadOrbit),
                     })
            {
                try { notes.AddRange(loader() ?? new List<Note>()); }
                catch (Exception ex) { Errors.Add($"{name} 读取失败（已跳过）：{ex.GetType().Name}: {ex.Message}"); }
            }
            _notes = notes;
            _loaded = true;
        }
    }

    private static JsonDocumentOptions JsoncOpts => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string FindModDir(params string[] hints)
    {
        var mods = SptPaths.Mods;
        if (mods == null || !Directory.Exists(mods)) return null;
        try
        {
            foreach (var hint in hints)
            {
                var p = Path.Combine(mods, hint);
                if (Directory.Exists(p)) return p;
            }
            foreach (var d in Directory.GetDirectories(mods))
            {
                var name = Path.GetFileName(d);
                if (hints.Any(h => name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)) return d;
            }
        }
        catch { }
        return null;
    }

    private static JsonElement? LoadJson(string file, string source)
    {
        try
        {
            if (file == null || !File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), JsoncOpts);
            return doc.RootElement.Clone();      // Clone：doc 释放后仍可用
        }
        catch (Exception ex)
        {
            // ★ "只 warn 不抛"：单个来源坏了就记一条，其余来源照常；调用方（页面）只看空列表
            Errors.Add($"{source} 配置解析失败（已跳过）：{Path.GetFileName(file) ?? "?"}：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>ABPS：`bossConfig.&lt;role&gt;.{enable, spawnChance{map:pct}}`</summary>
    private static List<Note> LoadAbps()
    {
        var list = new List<Note>();
        var dir = FindModDir("acidphantasm-botplacementsystem", "botplacementsystem", "ABPS");
        var root = LoadJson(dir == null ? null : Path.Combine(dir, "config.json"), "ABPS");
        if (root == null) return list;
        if (!root.Value.TryGetProperty("bossConfig", out var bc) || bc.ValueKind != JsonValueKind.Object) return list;

        foreach (var boss in bc.EnumerateObject())
        {
            var role = boss.Name;
            var cfg = boss.Value;
            if (cfg.ValueKind != JsonValueKind.Object) continue;
            if (cfg.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.False) continue;
            if (!cfg.TryGetProperty("spawnChance", out var sc) || sc.ValueKind != JsonValueKind.Object) continue;

            var (zh, en2) = RoleNames.TryGetValue(role, out var nm) ? nm : (role, role);
            foreach (var m in sc.EnumerateObject())
            {
                if (m.Value.ValueKind != JsonValueKind.Number) continue;
                var pct = m.Value.GetDouble();
                if (pct <= 0) continue;
                // 只给"会真的刷"的图（ABPS 的 chance 是百分比）；>100 也按实际值显示
                list.Add(new Note
                {
                    Source = "ABPS",
                    Map = m.Name.ToLowerInvariant(),
                    Text = $"ABPS 在本图刷 {zh} {FmtNum(pct)}%",
                    TextEn = $"ABPS spawns {en2} on this map at {FmtNum(pct)}%",
                });
            }
        }
        // 概率高的排前面（面板上更该先看到）
        return list.OrderByDescending(n => ParsePct(n.Text)).ToList();
    }

    /// <summary>ISB：`containment_breach_config.jsonc → {enabled, chance, labsVariantsEnabled}`（实验室）。</summary>
    private static List<Note> LoadIsb()
    {
        var list = new List<Note>();
        var dir = FindModDir("ISB-SpecialForces", "ISB");
        var root = LoadJson(dir == null ? null : Path.Combine(dir, "containment_breach_config.jsonc"), "ISB");
        if (root == null) return list;
        var r = root.Value;

        var enabled = !(r.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
        if (!enabled) return list;

        double chance = r.TryGetProperty("chance", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
        bool labsVariants = r.TryGetProperty("labsVariantsEnabled", out var lv) && lv.ValueKind == JsonValueKind.True;

        list.Add(new Note
        {
            Source = "ISB",
            Map = "laboratory",
            Text = $"ISB「封锁突破」在本图有 {FmtNum(chance)}% 概率触发"
                 + (labsVariants ? "（labsVariantsEnabled=true，会替换部分实验室刷怪）" : ""),
            TextEn = $"ISB containment breach triggers on this map at {FmtNum(chance)}%"
                   + (labsVariants ? " (labsVariantsEnabled=true, replaces some Labs spawns)" : ""),
        });
        return list;
    }

    /// <summary>Blackout：`config.json → blackoutChance`（全局：每次进图）。</summary>
    private static List<Note> LoadBlackout()
    {
        var list = new List<Note>();
        var dir = FindModDir("Blackout");
        var root = LoadJson(dir == null ? null : Path.Combine(dir, "config.json"), "Blackout");
        if (root == null) return list;
        if (!root.Value.TryGetProperty("blackoutChance", out var ch) || ch.ValueKind != JsonValueKind.Number) return list;
        var pct = ch.GetDouble();
        if (pct <= 0) return list;

        list.Add(new Note
        {
            Source = "Blackout",
            Map = "*",
            Text = $"Blackout：每次进图 {FmtNum(pct)}% 概率停电（实验室体验可能与本活动重叠）",
            TextEn = $"Blackout: {FmtNum(pct)}% chance of a blackout per raid (may overlap with this event on Labs)",
        });
        return list;
    }

    /// <summary>ORBIT：`ambush.enabled`（全局）+ `map_overrides.&lt;map&gt;`（按图）。</summary>
    private static List<Note> LoadOrbit()
    {
        var list = new List<Note>();
        var dir = FindModDir("ORBIT");
        var root = LoadJson(dir == null ? null : Path.Combine(dir, "config.json"), "ORBIT");
        if (root == null) return list;
        var r = root.Value;

        if (r.TryGetProperty("ambush", out var amb) && amb.ValueKind == JsonValueKind.Object
            && (!amb.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False))
        {
            list.Add(new Note
            {
                Source = "ORBIT",
                Map = "*",
                Text = "ORBIT：伏击系统开启（PMC / 玩家 SCAV 会临时转为伏击行为）",
                TextEn = "ORBIT: ambush system enabled (PMCs / player scavs may switch to ambush behavior)",
            });
        }

        if (r.TryGetProperty("map_overrides", out var mo) && mo.ValueKind == JsonValueKind.Object)
        {
            foreach (var m in mo.EnumerateObject())
            {
                if (m.Value.ValueKind != JsonValueKind.Object) continue;
                var map = m.Name.ToLowerInvariant();
                list.Add(new Note
                {
                    Source = "ORBIT",
                    Map = map,
                    Text = $"ORBIT 对{MapZh(map)}有 AI 覆盖配置（睡眠/唤醒距离、守卫时长等）",
                    TextEn = $"ORBIT has AI overrides for this map (sleep/wake distances, guard duration, etc.)",
                });
            }
        }
        return list;
    }

    private static string FmtNum(double v)
        => Math.Abs(v - Math.Round(v)) < 1e-9
            ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>从 `… 30%` 里取回数字（只用于排序，取不到算 0）。</summary>
    private static double ParsePct(string text)
    {
        try
        {
            var pct = (text ?? "").TrimEnd('%');
            var j = pct.LastIndexOf(' ');
            if (j >= 0) pct = pct.Substring(j + 1);
            return double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }
}
