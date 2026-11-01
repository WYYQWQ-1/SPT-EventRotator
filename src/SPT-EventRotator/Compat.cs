using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// 环境探测：ABPS / 地图移植 mod。
///
/// ★ v2.0.1 重要修正 —— "谁接管了这张图"的判据改成**看实际数据文件**，不再猜文件夹名。
///
/// 旧实现的 bug（实测踩到）：
///   <c>HasManimalLabs = 目录名.Contains("manimallabs")</c>
///   → `ManimalLabsBoiler` 命中 → 实验室被误判为"被移植接管" → 首领追加被跳过。
///   但 `ManimalLabsBoiler` 只加了**战利品**（labs_containers.json / labs_office_looseloot.json），
///   **根本没碰首领刷新** —— 于是活动想往实验室加首领时全部静默失效。
///
/// 新判据（数据驱动）：
///   扫描每个 mod 目录下是否存在 <c>db/locations/&lt;地图&gt;/base.json</c>。
///   只有**整份替换地图数据**的 mod 才算"接管了这张图的首领表"。
///   这跟 tools/analyze_conflicts.py 一样，是"看数据不看名字"，不会误判。
/// </summary>
internal static class Compat
{
    public static bool HasAbps;
    public static bool HasManimalInterchange;
    public static bool HasManimalLighthouse;
    public static bool HasManimalLabs;

    /// <summary>被某个 mod 用 db/locations/&lt;map&gt;/base.json **整份替换**的地图集合（小写）。</summary>
    public static readonly HashSet<string> OwnedMaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>地图 → 接管它的 mod 名（日志用）。</summary>
    public static readonly Dictionary<string, string> OwnerOf = new(StringComparer.OrdinalIgnoreCase);

    // ═══════════════ ★ v0.1.3 PBS（Progressive Bot System）兼容探测 ═══════════════
    //
    // 实测（反编译 PBS 2.3.0 + 全量扫描它的数据）：
    //   PBS 用 Harmony **整体替换** `BotInventoryGenerator.GenerateInventory`，
    //   武器池 / 配件池 / 装配概率全部取自它自己的 `Data/Equipment|Mods|Chances/Tier*.json`，
    //   **不再读 `BotTable`** ⇒ 本 mod 的 `botAddItems` / `botAddMods`（火箭筒 / 大弹鼓 / 下挂榴弹 SCAV）
    //   的**武器侧**在装了 PBS 的机器上写了也不会被读。
    //   而 `botLootAdd_`（背包携带）**仍然有效**：PBS 的战利品池仍读 `BotInventory.Items`。
    //
    // 实测数字（本机）：弹鼓只在 PBS 的 Tier4+ 出现（Tier1 里 0 个），
    //   而 SCAV 因 `enableScavAttachmentTiering:false` **固定用 Tier1**，`mod_magazine` 概率 25%、
    //   `mod_launcher` = 0 ⇒「大弹鼓 SCAV」「下挂榴弹 SCAV」在本机概率**恰好 0%**。
    //   本探测把这些数字直接打进日志，避免"配了没效果还以为是自己姿势不对"。

    public static bool PbsPresent;
    public static string PbsDataDir;
    public static bool PbsScavAttachmentTiering;

    private static readonly HashSet<string> DrumMagIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "5bed625c0db834001c062946", "5c6175362e221600133e3b94", "544a37c44bdc2d25388b4567",
        "59c1383d86f774290a37e0ca", "55d482194bdc2d1d4e8b456b", "55d485be4bdc2d962f8b456f",
        "5a351711c4a282000b1521a4", "5a718f958dc32e00094b97e7", "5addccf45acfc400185c2989",
        "5b7bef9c5acfc43d102852ec", "5c5db6742e2216000f1b2852"
    };

    private static void DetectPbs(string modsDir, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            string dir = Path.Combine(modsDir, "acidphantasm-progressivebotsystem");
            if (!Directory.Exists(dir))
            {
                dir = null;
                foreach (var d in Directory.GetDirectories(modsDir))
                    if (Path.GetFileName(d).ToLowerInvariant().Contains("progressivebot")) { dir = d; break; }
            }
            if (dir == null) return;

            PbsPresent = true;
            PbsDataDir = Path.Combine(dir, "Data");

            try
            {
                var cfg = Path.Combine(dir, "config.json");
                if (File.Exists(cfg))
                {
                    var txt = File.ReadAllText(cfg);
                    var i = txt.IndexOf("enableScavAttachmentTiering", StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        var seg = txt.Substring(i, Math.Min(40, txt.Length - i)).ToLowerInvariant();
                        PbsScavAttachmentTiering = seg.Contains("true");
                    }
                }
            }
            catch { }

            log?.Warning("[EventRotator] ⚠ 检测到 Progressive Bot System（PBS）：**它接管了 bot 生成** ——");
            log?.Warning("[EventRotator]   武器池/配件池/概率全部来自它自己的 Data/Equipment|Mods|Chances/Tier*.json，"
                         + "不再读 BotTable。");
            log?.Warning("[EventRotator]   ⇒ ⚠ 【提示】**落点已换**："
                         + "旧写法（`botAddMods`/`botAddItems` → `BotTable.Types[*].Inventory`）在 PBS 接管后**确实是死的**"
                         + "（`Inventory.Mods` 0 读者、`Inventory.Equipment` 只当 boolean 闸门）；"
                         + "**但现在改走 `botGearUpgrade`** —— 落点是**我们自己的 `BotGenerator.PrepareAndGenerateBot` postfix**"
                         + "（生成后改写 `__result` 的 `BotBase`）⇒ **PBS 接管与否都能生效** ✔");

            if (PbsScavAttachmentTiering)
            {
                log?.Warning("[EventRotator]   PBS 配置 enableScavAttachmentTiering=true → SCAV 跟随档位，"
                             + "高档位（Tier4+）的配件池里是**有**弹鼓的，效果请以实测为准。");
                return;
            }

            // 算出真实概率（SCAV 固定 Tier1）
            try
            {
                var modsFile = Path.Combine(PbsDataDir, "Mods", "Tier1_mods.json");
                var chanceFile = Path.Combine(PbsDataDir, "Chances", "Tier1_chances.json");
                int withMag = 0, drumGuns = 0;
                if (File.Exists(modsFile))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(modsFile));
                    foreach (var w in doc.RootElement.EnumerateObject())
                    {
                        if (!w.Value.TryGetProperty("mod_magazine", out var mags)) continue;
                        withMag++;
                        foreach (var m in mags.EnumerateArray())
                        {
                            var id = m.GetString();
                            if (id != null && DrumMagIds.Contains(id)) { drumGuns++; break; }
                        }
                    }
                }
                double magChance = -1, launcherChance = -1;
                if (File.Exists(chanceFile))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(chanceFile));
                    if (doc.RootElement.TryGetProperty("scav", out var sc)
                        && sc.TryGetProperty("chances", out var ch)
                        && ch.TryGetProperty("weaponMods", out var wm))
                    {
                        if (wm.TryGetProperty("mod_magazine", out var mc) && mc.ValueKind == System.Text.Json.JsonValueKind.Number) magChance = mc.GetDouble();
                        if (wm.TryGetProperty("mod_launcher", out var lc) && lc.ValueKind == System.Text.Json.JsonValueKind.Number) launcherChance = lc.GetDouble();
                    }
                }
                log?.Warning($"[EventRotator]   PBS 现状（SCAV 固定用 Tier1）：带弹匣槽武器 {withMag} 把，"
                             + $"其中池子里有本 mod 弹鼓的 {drumGuns} 把，mod_magazine 概率 "
                             + $"{(magChance < 0 ? "?" : magChance + "%")}，mod_launcher 概率 "
                             + $"{(launcherChance < 0 ? "?" : launcherChance + "%")}");
                if (drumGuns == 0)
                {
                    log?.Warning("[EventRotator]   ⇒ ⚠ 【提示】PBS 的 Tier1 配件池里**没有**本 mod 旧写法列的那些弹鼓 ——"
                                 + "但这条**已经不影响我们了**：");
                    log?.Warning("[EventRotator]      `drum_mag_scavs` 现在用 **`biggestMagazine`**（从**每把枪自己的 mod_magazine filter** 里挑容量最大的），"
                                 + "`underbarrel_gl_scavs` 用 **`addLauncherTpl`**（先命中该枪自己的 mod_launcher filter 才加）；"
                                 + "两者都走 **`botGearUpgrade`** ⇒ 落点是我们的 `PrepareAndGenerateBot` postfix，**PBS 接管与否都生效** ✔");
                    log?.Warning("[EventRotator]   ★ 本 mod **仍然不会修改 PBS 的任何文件**（那是别人的 mod）—— 这条红线不变。");
                }
                else
                {
                    log?.Warning($"[EventRotator]   （PBS 的 Tier1 配件池里已有 {drumGuns} 把武器可装本 mod 的大弹鼓，"
                                 + "效果请以实测为准）");
                }
            }
            catch (Exception ex) { log?.Warning("[EventRotator]   读 PBS 数据失败: " + ex.Message); }
        }
        catch (Exception ex) { log?.Warning("[EventRotator] PBS 兼容探测失败: " + ex.Message); }
    }


    /// <summary>
    /// 是否允许在被移植接管的图上追加首领。
    /// false（默认）= 避让（安全，但该图的"新增首领"类活动会失效）；
    /// true = 照常追加（想让"格鲁哈上灯塔"这类活动生效就开它）。
    /// </summary>
    public static bool AllowOnOverhaulMaps;

    public static void Detect(string modsDir, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
            { log?.Warning($"[EventRotator] mods dir not found: {modsDir}"); return; }

            var dirs = Directory.GetDirectories(modsDir).Select(Path.GetFileName).ToList();
            var lower = dirs.Select(d => d.ToLowerInvariant()).ToList();

            HasAbps = lower.Any(d => d.Contains("botplacementsystem"));
            HasManimalInterchange = lower.Any(d => d.Contains("manimalinterchange") || d.Contains("manimal-interchange"));
            HasManimalLighthouse = lower.Any(d => d.Contains("manimallighthouse") || d.Contains("manimal-lighthouse"));
            HasManimalLabs = lower.Any(d => d.Contains("manimallabs") || d.Contains("manimal-labs"));

            log?.Info($"[EventRotator] compat scan: ABPS={HasAbps}, ManimalInterchange={HasManimalInterchange}, ManimalLighthouse={HasManimalLighthouse}, ManimalLabs={HasManimalLabs}");
            if (HasAbps) log?.Info("[EventRotator] ABPS detected → boss patch will run AFTER it (priority 300) & only boost/append, never clear.");

            // ★ v0.1.3：探测 PBS（它会把 bot 生成整条换掉，导致本 mod 三个 bot 装备维度失效）
            DetectPbs(modsDir, log);

            // ★ 数据驱动：找出所有提供 db/locations/<map>/base.json 的 mod
            OwnedMaps.Clear();
            OwnerOf.Clear();
            foreach (var modName in dirs)
            {
                try
                {
                    foreach (var bj in Directory.GetFiles(Path.Combine(modsDir, modName), "base.json", SearchOption.AllDirectories))
                    {
                        var norm = bj.Replace('/', '\\').ToLowerInvariant();
                        var i = norm.IndexOf("\\locations\\", StringComparison.Ordinal);
                        if (i < 0) continue;
                        var rest = norm.Substring(i + "\\locations\\".Length);
                        var parts = rest.Split('\\');
                        if (parts.Length < 2 || parts[parts.Length - 1] != "base.json") continue;
                        var map = parts[0];
                        if (string.IsNullOrEmpty(map) || map == "base") continue;
                        if (!OwnedMaps.Add(map)) continue;      // 已被别的 mod 标记
                        OwnerOf[map] = modName;
                    }
                }
                catch { }
            }

            if (OwnedMaps.Count > 0)
            {
                var list = OwnedMaps.OrderBy(m => m).Select(m => $"{m}({OwnerOf[m]})");
                log?.Info($"[EventRotator] 检测到整份替换地图数据的 mod（{OwnedMaps.Count} 张图）：{string.Join(", ", list)}");
                log?.Info("[EventRotator] 这些图默认**不追加**首领（避免与其自定义刷点冲突）；"
                          + "要让活动照常追加，把 config.json 的 \"allowOnOverhaulMaps\" 设为 true");
            }
            else
                log?.Info("[EventRotator] 没有 mod 整份替换地图数据 → 所有图都可正常追加首领");
        }
        catch (Exception ex) { log?.Error($"[EventRotator] compat scan failed: {ex.Message}"); }
    }

    /// <summary>
    /// 该地图是否被地图移植 mod **整份接管**（接管则默认不追加首领，避免与其自定义刷点冲突）。
    /// 判据 = 有 mod 提供了 db/locations/&lt;map&gt;/base.json，不是看 mod 名字。
    /// </summary>
    public static bool MapModOwnsMap(string mapKeyLower)
    {
        if (string.IsNullOrEmpty(mapKeyLower)) return false;
        if (AllowOnOverhaulMaps) return false;      // 用户明确要求照常追加
        return OwnedMaps.Contains(mapKeyLower);
    }
}
