using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EventRotator;

/// <summary>
/// ★ v3 新增：**日志用的物品中文短名**（`tpl → 本地化 ShortName`）。
///
/// 为什么需要它：`ContainerLootInjectDimension` 现在每局要打
/// 「注入明细 显卡×12 内存条×9 私酒×14 …」和「样例 工具箱(5909d50c…) → 显示卡」——
/// 只打 24 位 tpl 玩家根本对不上自己开的箱子。
///
/// 落点与证据：只读 `<SPT_Runtime>/SPT_Data/database/locales/global/{ch,zh,en}.json`
/// （实测 `ch.json` 2.9 MB，一次性载入 ~40k 条，与 `FaceCoverFixDimension.ShortName`
/// 同一套做法；**不碰 SPT 内存里的 locale 表**）。目录推导见 <see cref="CandidateRoots"/>。
///
/// ⚠ 这是**纯日志装饰**：任何异常/缺文件/缺键都必须返回 tpl 本身，绝不影响注入逻辑。
/// </summary>
internal static class ItemNames
{
    /// <summary>可选：显式指定 SPT_Runtime 目录（探针/单测用）。空 = 自动推导。</summary>
    public static string LocaleRoot;

    private static Dictionary<string, string> _locale;
    private static bool _tried;

    /// <summary>tpl → 中文短名（取不到短名就用全名；都没有就返回 tpl 本身）。</summary>
    public static string Short(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return "?";
        try
        {
            EnsureLocale();
            if (_locale != null)
            {
                if (_locale.TryGetValue(tpl + " ShortName", out var s) && !string.IsNullOrEmpty(s)) return s;
                if (_locale.TryGetValue(tpl + " Name", out var n) && !string.IsNullOrEmpty(n)) return n;
            }
        }
        catch { }
        return tpl;
    }

    private static void EnsureLocale()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            foreach (var root in CandidateRoots())
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var lang in new[] { "ch", "zh", "en" })
                {
                    var f = Path.Combine(root, "SPT_Data", "database", "locales", "global", lang + ".json");
                    if (!File.Exists(f)) continue;
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var pr in doc.RootElement.EnumerateObject())
                        if (pr.Value.ValueKind == JsonValueKind.String)
                        {
                            var v = pr.Value.GetString();
                            if (!string.IsNullOrEmpty(v)) map[pr.Name] = v;
                        }
                    _locale = map;
                    return;
                }
            }
        }
        catch { /* 名称只是日志装饰，失败静默 */ }
    }

    /// <summary>
    /// 候选 SPT_Runtime 目录：先 <see cref="LocaleRoot"/>，再从 <c>AppContext.BaseDirectory</c>
    /// 逐级上溯（实机 = `<SPT_Runtime>/user/mods/SPT-EventRotator` ⇒ 上溯 3 级命中）。
    /// </summary>
    private static List<string> CandidateRoots()
    {
        var list = new List<string>();
        try
        {
            if (!string.IsNullOrEmpty(LocaleRoot)) list.Add(LocaleRoot);
        }
        catch { }
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent) list.Add(d.FullName);
        }
        catch { }
        return list;
    }
}
