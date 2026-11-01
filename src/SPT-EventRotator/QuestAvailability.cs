using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EventRotator;

/// <summary>
/// ★★★ 第 18 项【任务可用性】：让网页面板知道「**这台服务器上**某条活动任务到底存不存在」。
///
/// <para><b>为什么要这个</b>：活动 `enableQuests` 里写的 id 不一定在本机存在 ——
/// ① 我们自己的 `questlines/*.json` 注入的（存在）；② `SPT 原生 quests.json` 里的（存在）；
/// ③ 靠其它 mod（如 QuestBackport）注入的（id 可能**不一样**，只能按英文名认）；
/// ④ 压根没有（页面上应该标红"本机缺"）。</para>
///
/// <para><b>数据来源（全部只读，一次启动只扫一遍）</b>：</para>
/// <list type="bullet">
/// <item>我们的任务线：`&lt;mod&gt;/questlines/*.json` → `eventId` + `quests{}` + `locales{}`；</item>
/// <item>原生：`SPT_Data/database/templates/quests.json`（QuestName / traderId / conditions）；</item>
/// <item>文案：`SPT_Data/database/locales/global/{en,ch}.json` 的 `"&lt;id&gt; name"`；</item>
/// <item>商人名：`SPT_Data/database/traders/&lt;id&gt;/base.json` 的 `nickname`。</item>
/// </list>
///
/// <para><b>判存在顺序</b>（严格按契约）：questline → 原生 id → 英文名模糊匹配（归一化后比较）。</para>
/// </summary>
internal static class QuestAvailability
{
    /// <summary>一条任务的可用性快照（与 ConfigWeb.EventRow.QuestInfo 字段一一对应）。</summary>
    public sealed class Info
    {
        public string Id = "";
        public string NameZh = "";
        public string NameEn = "";
        public string Trader = "";
        public int Level;
        public string PreviousId = "";
        public bool Present;
        public string Source = "missing";     // questline | native | missing
        public bool FromOurQuestline;
    }

    private static readonly object _gate = new();
    private static bool _loaded;

    /// <summary>id → 信息（含"不存在"的占位，避免反复扫）。</summary>
    private static Dictionary<string, Info> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>归一化英文名 → id（模糊匹配用；只用**原生**表里的名字）。</summary>
    private static Dictionary<string, string> _normToId = new(StringComparer.Ordinal);

    /// <summary>活动 id → 我们 questlines 文件里的任务 id（**文件顺序**）。</summary>
    private static Dictionary<string, List<string>> _questlineByEvent = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> _traderNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SPT 原生 `<id> name` 文案（en / ch），任务线任务缺名字时兜底用。</summary>
    private static Dictionary<string, string> _nativeEn = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _nativeZh = new(StringComparer.Ordinal);

    private static void LoadNativeLocales()
    {
        _nativeEn = LocaleDict("en");
        _nativeZh = LocaleDict("ch");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  对外
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>我们 questlines 里属于该活动的任务 id（文件顺序；没有就空表）。</summary>
    public static List<string> QuestlineQuestsOf(string eventId)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(eventId)) return new List<string>();
        return _questlineByEvent.TryGetValue(eventId, out var ids) ? new List<string>(ids) : new List<string>();
    }

    /// <summary>按 quest id 或英文名查（命中顺序：questline → 原生 id → 英文名模糊匹配）。</summary>
    public static Info Lookup(string idOrName)
    {
        EnsureLoaded();
        var key = (idOrName ?? "").Trim();
        if (key.Length == 0) return Missing("");

        // ① id 精确（questline / 原生都已在 _byId 里）
        if (_byId.TryGetValue(key, out var hit)) return hit;

        // ② 不是 24 位 hex ⇒ 当英文名做归一化模糊匹配
        if (!IsHex24(key))
        {
            var norm = Normalize(key);
            if (norm.Length > 0 && _normToId.TryGetValue(norm, out var id) && _byId.TryGetValue(id, out var byName))
            {
                // 返回该「本机真实存在」的任务信息，但把 Id 保留为调用方给的标识
                return new Info
                {
                    Id = key,
                    NameZh = byName.NameZh,
                    NameEn = byName.NameEn,
                    Trader = byName.Trader,
                    Level = byName.Level,
                    PreviousId = byName.PreviousId,
                    Present = byName.Present,
                    Source = byName.Source,
                    FromOurQuestline = byName.FromOurQuestline,
                };
            }
        }

        var miss = Missing(key);
        _byId[key] = miss;          // 负缓存（下次直接命中）
        return miss;
    }

    /// <summary>自检/热重载用：丢掉已扫的结果，下一次 Lookup 重新扫（生产不需要调）。</summary>
    public static void ResetCache()
    {
        lock (_gate)
        {
            _loaded = false;
            _byId = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
            _normToId = new Dictionary<string, string>(StringComparer.Ordinal);
            _questlineByEvent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>归一化：小写 + 只保留字母/数字/CJK（去掉标点与空白）。</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static bool IsHex24(string s)
    {
        if (s.Length != 24) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    private static Info Missing(string id) => new()
    {
        Id = id,
        Present = false,
        Source = "missing",
        FromOurQuestline = false,
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  扫描（一次启动一遍）
    // ══════════════════════════════════════════════════════════════════════════

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            try { LoadTraders(); } catch { }
            try { LoadNativeLocales(); } catch { }  // 先读 SPT 原生 en/ch 文案（任务线缺名字时兜底）
            try { LoadQuestlines(); } catch { }     // 再加载我们的任务线，冲突时以我们的为准
            try { LoadNative(); } catch { }
            _loaded = true;
        }
    }

    private static void LoadTraders()
    {
        _traderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = SptPaths.TradersDir;
        if (dir == null || !Directory.Exists(dir)) return;
        foreach (var d in Directory.GetDirectories(dir))
        {
            var f = Path.Combine(d, "base.json");
            if (!File.Exists(f)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                var id = Path.GetFileName(d);
                var nick = Str(r, "nickname") ?? Str(r, "surname");
                if (!string.IsNullOrWhiteSpace(nick)) _traderNames[id] = nick!;
            }
            catch { }
        }
    }

    /// <summary>扫 `&lt;mod&gt;/questlines/*.json`：eventId → 任务 id（文件顺序）+ 任务信息 + 文案。</summary>
    private static void LoadQuestlines()
    {
        var modDir = ConfigWebService.ModDir;
        var dir = modDir == null ? null : Path.Combine(modDir, "questlines");
        if (dir == null || !Directory.Exists(dir)) return;

        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                var eventId = root.TryGetProperty("eventId", out var ev) ? ev.GetString() ?? "" : "";
                if (!root.TryGetProperty("quests", out var quests) || quests.ValueKind != JsonValueKind.Object) continue;

                var en = ReadLocaleBlock(root, "en");
                var ch = ReadLocaleBlock(root, "ch");
                var ids = new List<string>();

                foreach (var q in quests.EnumerateObject())
                {
                    var qid = q.Name;
                    if (!IsHex24(qid)) continue;
                    ids.Add(qid);

                    var nameEn = Lookup(en, qid + " name") ?? Lookup(_nativeEn, qid + " name")
                                 ?? Str(q.Value, "QuestName") ?? "";
                    var nameZh = Lookup(ch, qid + " name") ?? Lookup(_nativeZh, qid + " name") ?? "";
                    var traderId = Str(q.Value, "traderId") ?? "";
                    var prev = FirstPrevQuest(q.Value);
                    var lvl = FirstLevel(q.Value);

                    _byId[qid] = new Info
                    {
                        Id = qid,
                        NameZh = nameZh,
                        NameEn = nameEn,
                        Trader = TraderOf(traderId),
                        Level = lvl,
                        PreviousId = prev,
                        Present = true,
                        Source = "questline",
                        FromOurQuestline = true,
                    };
                    AddNameKey(nameEn, qid);
                    AddNameKey(nameZh, qid);
                }

                if (eventId.Length > 0 && ids.Count > 0) _questlineByEvent[eventId] = ids;
            }
        }
    }

    /// <summary>扫原生 `quests.json` + en/ch 文案。</summary>
    private static void LoadNative()
    {
        var file = SptPaths.QuestsFile;
        if (file == null || !File.Exists(file)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

        var en = _nativeEn;
        var ch = _nativeZh;

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var qid = p.Name;
            if (!IsHex24(qid)) continue;
            if (_byId.ContainsKey(qid)) continue;      // 我们的任务线优先

            var r = p.Value;
            var nameEn = Lookup(en, qid + " name") ?? Str(r, "QuestName") ?? "";
            var nameZh = Lookup(ch, qid + " name") ?? "";
            var traderId = Str(r, "traderId") ?? "";

            _byId[qid] = new Info
            {
                Id = qid,
                NameZh = nameZh,
                NameEn = nameEn,
                Trader = TraderOf(traderId),
                Level = FirstLevel(r),
                PreviousId = FirstPrevQuest(r),
                Present = true,
                Source = "native",
                FromOurQuestline = false,
            };
            AddNameKey(nameEn, qid);
            AddNameKey(nameZh, qid);
        }
    }

    private static void AddNameKey(string name, string id)
    {
        var norm = Normalize(name);
        if (norm.Length < 3 || string.IsNullOrEmpty(id)) return;
        _normToId.TryAdd(norm, id);
    }

    private static string TraderOf(string traderId)
    {
        if (string.IsNullOrEmpty(traderId)) return "";
        return _traderNames.TryGetValue(traderId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : traderId;
    }

    /// <summary>前置任务：`conditions.AvailableForStart[]` 里第一个 `conditionType=Quest` 的 target。</summary>
    private static string FirstPrevQuest(JsonElement quest)
    {
        try
        {
            if (!quest.TryGetProperty("conditions", out var c)) return "";
            if (!c.TryGetProperty("AvailableForStart", out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
            foreach (var cond in arr.EnumerateArray())
            {
                if (!string.Equals(Str(cond, "conditionType"), "Quest", StringComparison.OrdinalIgnoreCase)) continue;
                if (!cond.TryGetProperty("target", out var t)) continue;
                if (t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0)
                    return t[0].ValueKind == JsonValueKind.String ? t[0].GetString() ?? "" : "";
            }
        }
        catch { }
        return "";
    }

    /// <summary>等级门槛：`AvailableForStart[]` 里 `Level` 条件的 value（读不到 = 0）。</summary>
    private static int FirstLevel(JsonElement quest)
    {
        try
        {
            if (!quest.TryGetProperty("conditions", out var c)) return 0;
            if (!c.TryGetProperty("AvailableForStart", out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
            foreach (var cond in arr.EnumerateArray())
            {
                if (!string.Equals(Str(cond, "conditionType"), "Level", StringComparison.OrdinalIgnoreCase)) continue;
                if (cond.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt32();
            }
        }
        catch { }
        return 0;
    }

    private static Dictionary<string, string> ReadLocaleBlock(JsonElement root, string lang)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (root.TryGetProperty("locales", out var ls) && ls.ValueKind == JsonValueKind.Object
                && ls.TryGetProperty(lang, out var lb) && lb.ValueKind == JsonValueKind.Object)
                foreach (var kv in lb.EnumerateObject())
                    if (kv.Value.ValueKind == JsonValueKind.String) map[kv.Name] = kv.Value.GetString() ?? "";
        }
        catch { }
        return map;
    }

    private static Dictionary<string, string> LocaleDict(string lang)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var f = SptPaths.LocaleFile(lang);
            if (f == null || !File.Exists(f)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            foreach (var kv in doc.RootElement.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String) map[kv.Name] = kv.Value.GetString() ?? "";
        }
        catch { }
        return map;
    }

    private static string Lookup(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static string Str(JsonElement e, string name)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }
        catch { return null; }
    }
}

/// <summary>
/// SPT 数据目录解析（本文件与 <see cref="CompatReport"/> 共用）。
/// 只做**只读**路径推导：从 mod 目录往上找 `SPT_Data/database/templates/quests.json`，
/// 从 mod 目录往上找 `user/mods`；找不到返回 null（调用方全部当"没有"处理）。
/// </summary>
internal static class SptPaths
{
    private static bool _tried;
    private static string _sptData;
    private static string _mods;

    /// <summary>自检用：强制指定 SPT_Data 目录（null = 自动推导）。</summary>
    public static string SptDataOverride;

    /// <summary>自检用：强制指定 user/mods 目录（null = 自动推导）。</summary>
    public static string ModsOverride;

    /// <summary>自检用：丢掉已推导的路径。</summary>
    public static void ResetCache()
    {
        _tried = false;
        _sptData = null;
        _mods = null;
    }

    public static string SptData
    {
        get { EnsureTried(); return string.IsNullOrEmpty(SptDataOverride) ? _sptData : SptDataOverride; }
    }

    public static string Mods
    {
        get { EnsureTried(); return string.IsNullOrEmpty(ModsOverride) ? _mods : ModsOverride; }
    }

    public static string QuestsFile => SptData == null ? null : Path.Combine(SptData, "database", "templates", "quests.json");
    public static string TradersDir => SptData == null ? null : Path.Combine(SptData, "database", "traders");
    public static string LocationsDir => SptData == null ? null : Path.Combine(SptData, "database", "locations");

    public static string LocaleFile(string lang) => SptData == null
        ? null
        : Path.Combine(SptData, "database", "locales", "global", lang + ".json");

    private static void EnsureTried()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            var start = ConfigWebService.ModDir;
            if (string.IsNullOrEmpty(start))
            {
                try { start = Path.GetDirectoryName(typeof(SptPaths).Assembly.Location); } catch { }
            }
            if (string.IsNullOrEmpty(start)) return;

            // 候选起点：mod 目录 → 程序集目录（生产里 = SPT_Runtime）→ 当前目录
            var starts = new List<string> { start };
            try { var bd = AppContext.BaseDirectory; if (!string.IsNullOrEmpty(bd)) starts.Add(bd); } catch { }
            try { starts.Add(Directory.GetCurrentDirectory()); } catch { }

            foreach (var st in starts)
            {
                var d = new DirectoryInfo(st);
                for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
                {
                    // 1) 这一层就是 SPT_Runtime（含 SPT_Data/database/templates）
                    if (_sptData == null && Directory.Exists(Path.Combine(d.FullName, "SPT_Data", "database", "templates")))
                        _sptData = Path.Combine(d.FullName, "SPT_Data");
                    // 2) 这一层是 user（含 mods）
                    if (_mods == null && Directory.Exists(Path.Combine(d.FullName, "mods")))
                        _mods = Path.Combine(d.FullName, "mods");
                    // 3) 这一层里直接有 SPT_Data（少见但兜底）
                    if (_sptData == null && Directory.Exists(Path.Combine(d.FullName, "SPT_Data")))
                        _sptData = Path.Combine(d.FullName, "SPT_Data");
                    if (_sptData != null && _mods != null) return;
                }
            }
        }
        catch { }
    }
}
