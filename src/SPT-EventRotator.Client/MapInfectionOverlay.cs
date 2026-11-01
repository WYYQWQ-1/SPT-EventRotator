using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EventRotator.Client;

/// <summary>
/// ★ v1.3.12【地图感染标记】在地图选择界面的「地点名」右侧画一个 ☣ 小图标，
/// 颜色按服务端写的 <c>&lt;modDir&gt;\感染率.txt</c> 分档：白（1~33）/ 黄（34~66）/ 红（67~100），0 = 不画。
///
/// <para><b>为什么自绘</b>：用户口径是「有个标记就行」；官方那套感染 UI 由服务端控制器驱动，
/// 客户端不去依赖它，直接在 IMGUI 覆盖层上按地点标签的屏幕坐标画字。官方改版也不会连带坏掉。</para>
///
/// <para><b>性能</b>：重活只有 <see cref="UnityEngine.Object.FindObjectsOfType{T}(bool)"/>，
/// 0.6~1 秒一次（**绝不每帧扫描**）；不在选图界面时 <see cref="_inMenu"/> = false ⇒ OnGUI 立即返回、每帧零绘制。
/// 文本位置每帧从缓存的 RectTransform 现算（很便宜），所以列表滚动/缩放时图标跟得住。</para>
///
/// <para><b>大小/可读性</b>：1080p 下默认 26px（旧版约 19px 用户反馈太小），并画 2px 黑色描边；
/// F12「地图标记大小」（0.6~2.5，默认 1.0）可实时放大/缩小。</para>
///
/// <para><b>容错</b>：任何异常只打一次日志；<c>感染率.txt</c> 不存在 ⇒ 清空数据、什么都不画、绝不报错；
/// 找不到 TMP_Text / UI.Text ⇒ 静默退出（说明客户端版本不同，不刷屏）。</para>
/// </summary>
internal sealed class MapInfectionOverlay : MonoBehaviour
{
    /// <summary>当前实例（Plugin 的自愈检查会看它）。</summary>
    internal static MapInfectionOverlay Instance;

    /// <summary>F12 开关（Plugin 启动时注入；false 时本组件不扫描、不绘制）。</summary>
    internal static bool Enabled = true;

    /// <summary>
    /// ★ 标记大小倍数（F12「地图标记大小」，0.6~2.5，默认 1.0）。1080p 下默认字号 = 26px。
    /// <para>用静态字段是为了绘制时零分配、改完立即生效。</para>
    /// </summary>
    internal static float MarkerScale = 1f;

    private static ConfigEntry<float> _sizeCfg;

    /// <summary>
    /// ★ 由 Plugin 在 Awake 里调用一次：把「地图标记大小」绑到 F12（与其它面板项同一个分区）。
    /// <para>配置细节留在本文件里，Plugin 只需一行调用。</para>
    /// </summary>
    internal static void Configure(ConfigFile config, string section)
    {
        if (config == null || _sizeCfg != null) return;
        try
        {
            _sizeCfg = config.Bind(section, "地图标记大小 (map marker size)", 1f,
                new ConfigDescription(
                    Locale.D("地图选择界面上的 ☣ 感染标记大小倍数。默认 1.0（1080p 下约 26px 高）；觉得小就调大，范围 0.6~2.5。\n改完立即生效（不用重启）。",
                             "Scale of the ☣ infection markers on the map-selection screen. Default 1.0 (about 26 px at 1080p); range 0.6-2.5.\nTakes effect immediately (no restart)."),
                    new AcceptableValueRange<float>(0.6f, 2.5f)));
            ApplyMarkerScale();
            _sizeCfg.SettingChanged += (_, __) =>
            {
                try { ApplyMarkerScale(); } catch { }
            };
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning("[EventRotator.Client][感染标记] 绑定「地图标记大小」失败: " + ex.Message);
        }
    }

    private static void ApplyMarkerScale()
    {
        var v = _sizeCfg != null ? _sizeCfg.Value : 1f;
        MarkerScale = Mathf.Clamp(v, 0.6f, 2.5f);
    }

    /// <summary>服务端 mod 目录提供者（Plugin 注入 `() =&gt; ModDir()`，F12 改目录后下一次读文件自动跟随）。</summary>
    private static Func<string> _dirProvider;

    /// <summary>服务端写的感染率文件名（固定名字，和 EventsOverlay 的「当前生效活动.txt」同一个目录）。</summary>
    private const string RateFileName = "感染率.txt";

    /// <summary>命中的地点标签（扫描缓存）。</summary>
    private sealed class LabelRef
    {
        public RectTransform Rt;
        public Camera Cam;              // Canvas 是 ScreenSpaceCamera 时才非 null；Overlay 用 null
        public string[] Keys;           // 该标签对应的感染率 key 候选（工厂白天/夜晚等有多个）
        public float LocalRightX;       // 文字**实际**右边缘（RectTransform 本地坐标；NaN = 未知）
    }

    private readonly List<LabelRef> _labels = new();
    private readonly Dictionary<string, int> _rates = new(StringComparer.OrdinalIgnoreCase);

    private float _scanAt;
    private int _emptyScans;   // 连续"没扫到任何选图界面标签"的次数（用于退避）
    private float _lastAncestorProbe = -999f;              // 下一次扫描标签的时间
    private float _fileAt;              // 下一次检查感染率文件的时间
    private bool _inMenu;               // true = 当前至少有一个地点标签可见 ⇒ 认为"在地图选择界面"
    private string _fileSig;            // (路径|mtime|长度)：文件没变就不重复解析

    private bool _missingLogged;        // 「感染率.txt 不存在」只提示一次
    private bool _foundLogged;          // 「读到感染率.txt」只提示一次
    private bool _noTmpLogged;          // 找不到 TMP_Text 只提示一次（且仅在诊断开启时）
    private bool _errLogged;            // 异常只打一次

    private GUIStyle _style;
    private int _styleFs = -1;
    private Font _styleFont;
    private bool _glyphChecked;
    private bool _glyphOk = true;       // GUI 字体是否真的有 ☣ 字形（没有就画一个色块兜底）

    // 颜色：严格按用户要求的三档
    private static readonly Color ColorWhite = new(1f, 1f, 1f, 1f);
    private static readonly Color ColorYellow = new(1f, 0.84f, 0.16f, 1f);
    private static readonly Color ColorRed = new(0.93f, 0.22f, 0.20f, 1f);
    /// <summary>描边颜色（黑，留一点透明度避免糊成一团）。</summary>
    private static readonly Color ColorOutline = new(0f, 0f, 0f, 0.92f);
    /// <summary>8 方向描边偏移表（实际偏移 = 表值 × outline 像素）。</summary>
    private static readonly int[] OutlineDx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] OutlineDy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    // ══════════ 地点识别表（中文 + 英文，大小写不敏感，允许包含关系）══════════
    // 每组：显示名关键词 → 感染率文件里的 mapKey 候选。
    // 工厂在文件里可能分成 factory4_day / factory4_night，而选图界面只有一个「工厂」⇒ 取两者最大值。
    private static readonly string[][] MapTokens =
    {
        new[] { "工厂", "factory" },
        new[] { "海关", "customs" },
        new[] { "森林", "woods" },
        new[] { "实验室", "the lab", "terragroup labs", "laboratory" },
        new[] { "立交桥", "interchange" },
        new[] { "海岸线", "shoreline" },
        new[] { "灯塔", "lighthouse" },
        new[] { "储备站", "reserve" },
        new[] { "塔科夫街区", "streets of tarkov", "streets" },
        new[] { "中心区", "ground zero" },
        // 可选（服务端写了就显示，没写就自然不显示）
        new[] { "破冰船", "icebreaker" },
        new[] { "迷宫", "labyrinth" },
    };

    private static readonly string[][] MapKeys =
    {
        new[] { "factory4_day", "factory4_night" },
        new[] { "bigmap" },
        new[] { "Woods" },
        new[] { "laboratory" },
        new[] { "Interchange" },
        new[] { "Shoreline" },
        new[] { "Lighthouse" },
        new[] { "RezervBase" },
        new[] { "TarkovStreets" },
        new[] { "Sandbox" },
        new[] { "Icebreaker" },
        new[] { "Labyrinth" },
    };

    /// <summary>
    /// ★ 挂载（幂等）。<paramref name="modDirProvider"/> 每次读文件时调用，
    /// 所以 F12 改「服务端 mod 目录」后不用重启游戏。
    /// </summary>
    internal static MapInfectionOverlay Attach(Func<string> modDirProvider)
    {
        _dirProvider = modDirProvider;
        try
        {
            if (Instance != null) return Instance;
            var go = new GameObject("EventRotatorMapInfectionOverlay");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var c = go.AddComponent<MapInfectionOverlay>();
            if (c == null) return null;
            if (Instance == null) Instance = c;
            Plugin.Log?.LogInfo("[EventRotator.Client] 地图感染标记组件已创建（选图界面显示 ☣）");
            return Instance;
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError("[EventRotator.Client] 创建地图感染标记组件失败: " + ex);
            return null;
        }
    }

    private void OnDestroy()
    {
        try { if (ReferenceEquals(Instance, this)) Instance = null; } catch { }
    }

    private void Update()
    {
        try
        {
            if (!Enabled)
            {
                if (_inMenu) { _inMenu = false; _labels.Clear(); }
                return;
            }

            float now = UnityEngine.Time.unscaledTime;

            // ① 感染率文件：1 秒看一次，内容没变就不解析
            if (now >= _fileAt)
            {
                _fileAt = now + 1f;
                TickRateFile();
            }

            // ② 地点标签：在选图界面 0.6 秒扫一次（位置跟得上），不在时 1 秒扫一次（只为发现"进入选图界面"）
            if (now >= _scanAt)
            {
                // ★ 2026-11-01【性能·仓库卡顿反馈】原来"不在选图界面"也每 1 秒 FindObjectsOfType 一次，
                //   仓库/任务页文字极多 ⇒ 玩家反馈"点仓库卡"。这里改成**自适应退避**：
                //   连续 0 命中就 1s → 2s → 5s；一旦命中（进选图界面）立刻回到 0.6s。
                float next = _inMenu ? 0.6f
                           : _emptyScans <= 1 ? 1.0f
                           : _emptyScans <= 4 ? 2.0f
                           : 5.0f;
                _scanAt = now + next;
                if (InRaidWorld())
                {
                    // 战局内根本没有"地图选择界面" ⇒ 一次 FindObjectsOfType 都不做（开销≈0）
                    if (_inMenu) { _inMenu = false; }
                    if (_labels.Count > 0) _labels.Clear();
                }
                else
                {
                    ScanLabels();
                    if (_inMenu) _emptyScans = 0;
                    else if (_emptyScans < 1000) _emptyScans++;
                }
            }
        }
        catch (Exception ex) { LogErrorOnce(ex); }
    }

    /// <summary>读 &lt;modDir&gt;\感染率.txt：文件不存在/读失败 ⇒ 清空数据（什么都不画），绝不抛给游戏。</summary>
    private void TickRateFile()
    {
        try
        {
            var dir = _dirProvider != null ? _dirProvider() : null;
            if (string.IsNullOrEmpty(dir))
            {
                if (_rates.Count > 0) _rates.Clear();
                _fileSig = null;
                return;
            }

            var path = Path.Combine(dir, RateFileName);
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                // 文件不存在：静默清空（用户口径：存在就画，不存在什么都不画）
                if (_rates.Count > 0) _rates.Clear();
                _fileSig = null;
                if (EventsOverlay.LogEnabled && !_missingLogged)
                {
                    _missingLogged = true;
                    Plugin.Log?.LogInfo($"[EventRotator.Client][感染标记] 未找到 {RateFileName}（服务端未开启感染，本次不画标记）");
                }
                return;
            }

            // 路径也纳入签名：F12 换目录后即使 (mtime,长度) 撞车也会重读
            var sig = path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
            if (string.Equals(sig, _fileSig, StringComparison.Ordinal)) return;
            _fileSig = sig;

            var text = File.ReadAllText(path, Encoding.UTF8);
            ParseRates(text);

            if (EventsOverlay.LogEnabled && !_foundLogged)
            {
                _foundLogged = true;
                Plugin.Log?.LogInfo($"[EventRotator.Client][感染标记] 读到 {RateFileName}：{_rates.Count} 个地点");
            }
        }
        catch (Exception ex)
        {
            // 读失败只清空（没数据就不画），不刷屏
            if (_rates.Count > 0) _rates.Clear();
            _fileSig = null;
            LogErrorOnce(ex);
        }
    }

    /// <summary>解析 <c># mapKey=百分比</c> 逐行格式；<c>#</c> 开头/空行/坏行一律跳过。</summary>
    private void ParseRates(string text)
    {
        _rates.Clear();
        if (string.IsNullOrEmpty(text)) return;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#' || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal)) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0 || eq >= line.Length - 1) continue;

            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim().TrimEnd('%').Trim();
            if (key.Length == 0) continue;
            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) continue;

            _rates[key] = Mathf.Clamp(p, 0, 100);
        }
    }

    /// <summary>扫描当前界面里的地点标签（TMP_Text 优先，再试老的 UI.Text）。</summary>
    private void ScanLabels()
    {
        _labels.Clear();
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            TMP_Text[] tmps = null;
            try { tmps = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true); }
            catch (Exception ex)
            {
                if (EventsOverlay.LogEnabled && !_noTmpLogged)
                {
                    _noTmpLogged = true;
                    Plugin.Log?.LogInfo("[EventRotator.Client][感染标记] 找不到 TMP_Text（客户端版本可能不同）: " + ex.Message);
                }
            }

            if (tmps != null)
            {
                for (int i = 0; i < tmps.Length; i++)
                {
                    var t = tmps[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    string txt;
                    try { txt = t.text; } catch { continue; }
                    AddIfMatch(t, t.rectTransform, CleanText(txt), seen);
                }
            }

            // 老版本 / 某些界面可能用 UnityEngine.UI.Text
            Text[] uis = null;
            try { uis = UnityEngine.Object.FindObjectsOfType<Text>(true); }
            catch { }
            if (uis != null)
            {
                for (int i = 0; i < uis.Length; i++)
                {
                    var t = uis[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    string txt;
                    try { txt = t.text; } catch { continue; }
                    AddIfMatch(t, t.rectTransform, CleanText(txt), seen);
                }
            }

            // ★ 2026-11-01【误显示】任务页「地点」列也会出现 森林/工厂/实验室 等文字，
            //   之前只要命中 1 个地点就当"在选图界面" ⇒ 任务页也画出了 ☣。
            //   选图界面会**同时**列出全部地图，这里要求 ≥6 个不同地图才认。
            {
                var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in _labels)
                    if (l.Keys != null && l.Keys.Length > 0) distinct.Add(l.Keys[0]);
                _inMenu = distinct.Count >= 6;
                // ★ 2026-11-01【界面探测】任务页也会列出 ≥6 张地图 ⇒ 阈值失效。
                //   这里不论结果，每 8 秒打一条祖先链：对比"任务页"和"选图界面"的差异节点。
                if (UnityEngine.Time.unscaledTime - _lastAncestorProbe >= 8f)
                {
                    _lastAncestorProbe = UnityEngine.Time.unscaledTime;
                    try
                    {
                        var names = new List<string>();
                        UnityEngine.Transform cur = _labels[0].Rt;
                        for (int k = 0; k < 10 && cur != null; k++) { names.Add(cur.name); cur = cur.parent; }
                        Plugin.Log?.LogInfo("[EventRotator.Client][感染标记] 界面探测"
                            + (_inMenu ? "A(判为选图)" : "B(判为非选图)")
                            + "：地点 " + distinct.Count + " 个｜祖先链 = " + string.Join("/", names.ToArray()));
                    }
                    catch { }
                }
                if (!_inMenu) _labels.Clear();
            }
        }
        catch (Exception ex)
        {
            _labels.Clear();
            _inMenu = false;
            LogErrorOnce(ex);
        }
    }

    /// <summary>文本 → 地点关键词匹配；命中就把标签放进缓存（同地点、同位置只留一个）。</summary>
    /// <summary>
    /// ★ 2026-11-01【任务页误显示】实测祖先链：
    /// <c>LocationName/Location/TaskShortInfo/TaskTemplate(Clone)/…/Tasks Panel</c>
    /// ⇒ 任务条目里的"地点"字段也匹配地图名。这里直接按祖先节点名排除（含 Task 的一律不画）。
    /// </summary>
    private static bool UnderTaskPane(RectTransform rt)
    {
        try
        {
            UnityEngine.Transform cur = rt;
            for (int i = 0; i < 8 && cur != null; i++, cur = cur.parent)
            {
                var n = cur.name;
                if (!string.IsNullOrEmpty(n) && n.IndexOf("Task", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    private void AddIfMatch(Component owner, RectTransform rt, string text, HashSet<string> seen)
    {
        if (owner == null || rt == null || string.IsNullOrEmpty(text)) return;
        if (UnderTaskPane(rt)) return;   // ★ 任务列表里的地点字段，不参与感染标记

        var s = text.Trim();
        // 地点名都很短；带换行/超长的多半是说明文字 ⇒ 不参与，避免误把描述里的「海关」当地点
        if (s.Length == 0 || s.Length > 24) return;
        if (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0) return;

        string[] keys = MatchKeys(s);
        if (keys == null) return;

        Camera cam = null;
        try
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) cam = canvas.worldCamera;
        }
        catch { }

        // 文字实际右边缘（后面去重和画标记都用它）
        float localRight = LocalTextRightX(owner, rt);
        float lx = float.IsNaN(localRight) ? rt.rect.xMax : Mathf.Min(localRight, rt.rect.xMax);

        // 去重：同一地点 + 同一屏幕位置（TMP 常有"阴影/描边"副本，别画两遍）
        string dedupe;
        try
        {
            var sp = RectTransformUtility.WorldToScreenPoint(cam,
                rt.TransformPoint(new Vector3(lx, rt.rect.center.y, 0f)));
            dedupe = string.Join(",", keys) + "@" + Mathf.RoundToInt(sp.x) + "," + Mathf.RoundToInt(sp.y);
        }
        catch { dedupe = null; }
        if (dedupe != null && !seen.Add(dedupe)) return;

        _labels.Add(new LabelRef { Rt = rt, Cam = cam, Keys = keys, LocalRightX = localRight });
    }

    /// <summary>
    /// 文字**实际**右边缘（本地坐标）：地图名可能放在"定宽容器"里（左对齐/居中），
    /// 这时直接用 <c>rect.xMax</c> 会把 ☣ 画到卡片最右边、离地名很远。
    /// <para>TMP 用 <c>textBounds</c>（真实字形范围，连对齐都算好）；取不到再按
    /// <c>preferredWidth</c> + 水平对齐推算；都失败返回 NaN（调用方退回 rect.xMax）。</para>
    /// </summary>
    private static float LocalTextRightX(Component c, RectTransform rt)
    {
        try
        {
            if (c is TMP_Text tmp)
            {
                var b = tmp.textBounds;
                // textBounds 在 m_textInfo 为空/无可见字符时会返回极大值 ⇒ 加一道合理范围校验
                if (b.size.x > 0.01f && b.size.x < 10000f
                    && !float.IsNaN(b.max.x) && !float.IsInfinity(b.max.x) && Mathf.Abs(b.max.x) < 10000f)
                    return b.max.x;
                float pw = tmp.preferredWidth;
                if (pw > 0.01f && pw < 10000f && !float.IsNaN(pw))
                {
                    if (tmp.horizontalAlignment == HorizontalAlignmentOptions.Center) return rt.rect.center.x + pw * 0.5f;
                    if (tmp.horizontalAlignment == HorizontalAlignmentOptions.Right) return rt.rect.xMax;
                    return rt.rect.xMin + pw;
                }
            }
            else if (c is Text ui)
            {
                float pw = ui.preferredWidth;
                if (pw > 0.01f && pw < 10000f && !float.IsNaN(pw))
                {
                    var a = ui.alignment;
                    if (a == TextAnchor.UpperCenter || a == TextAnchor.MiddleCenter || a == TextAnchor.LowerCenter)
                        return rt.rect.center.x + pw * 0.5f;
                    if (a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight)
                        return rt.rect.xMax;
                    return rt.rect.xMin + pw;
                }
            }
        }
        catch { }
        return float.NaN;
    }

    /// <summary>把一段 UI 文本映射到感染率 key 候选；不匹配任何地点返回 null。</summary>
    private static string[] MatchKeys(string text)
    {
        var s = text.ToLowerInvariant();
        for (int i = 0; i < MapTokens.Length; i++)
        {
            var toks = MapTokens[i];
            for (int j = 0; j < toks.Length; j++)
            {
                if (toks[j].Length > 0 && s.IndexOf(toks[j], StringComparison.Ordinal) >= 0)
                    return MapKeys[i];
            }
        }
        return null;
    }

    /// <summary>去掉富文本标签（地图名偶尔会带 &lt;color=…&gt;）。没有 '&lt;' 时原样返回，零分配。</summary>
    private static string CleanText(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s;
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    private void OnGUI()
    {
        // 不在选图界面（或功能关闭 / 没有命中标签）⇒ 每帧零开销
        if (!Enabled || !_inMenu || _labels.Count == 0) return;
        // 固定矩形贴图只需要在 Repaint 事件里画；Layout 那一趟直接跳过
        var ev = Event.current;
        if (ev != null && ev.type != EventType.Repaint) return;

        try { DrawMarkers(); }
        catch (Exception ex) { LogErrorOnce(ex); }
    }

    private void DrawMarkers()
    {
        // ★ 放大：1080p 基准 26px（旧值约 19px，用户反馈看不清），再乘 F12「地图标记大小」(0.6~2.5)
        int baseFs = Mathf.Max(12, Mathf.RoundToInt(Screen.height * (26f / 1080f)));
        int fs = Mathf.Clamp(Mathf.RoundToInt(baseFs * MarkerScale), 12, 96);
        float ui = Mathf.Max(1f, Screen.height / 1080f);
        // 间距：从文字真实右缘起算；默认 8px@1080p，随缩放略增（2.5× 时约 12.5px），不压到地名
        float gap = Mathf.Clamp((8f + 3f * (MarkerScale - 1f)) * ui, 6f, 28f);
        // 黑描边：24px 字号 = 2px；小字号 1px（用户要求 1~2px）
        int outline = Mathf.Clamp(Mathf.RoundToInt(fs / 12f), 1, 2);
        var style = EnsureStyle(fs);

        var oldColor = GUI.color;
        try
        {
            for (int i = 0; i < _labels.Count; i++)
            {
                var l = _labels[i];
                var rt = l.Rt;
                if (rt == null) continue;
                var go = rt.gameObject;
                if (go == null || !go.activeInHierarchy) continue;

                int p = RateFor(l.Keys);
                if (p <= 0) continue;           // 0 = 不画
                var col = ColorFor(p);
                GUI.color = Color.white;        // 字形分支用 style 颜色；别被上一轮兜底色块染到

                // 地点名文字实际右边缘 → 屏幕点 → GUI 坐标
                float sx, sy;
                try
                {
                    float lx = float.IsNaN(l.LocalRightX) ? rt.rect.xMax : Mathf.Min(l.LocalRightX, rt.rect.xMax);
                    var world = rt.TransformPoint(new Vector3(lx, rt.rect.center.y, 0f));
                    var sp = RectTransformUtility.WorldToScreenPoint(l.Cam, world);
                    sx = sp.x;
                    sy = Screen.height - sp.y;
                }
                catch { continue; }

                float w = fs * 1.45f;
                float h = fs * 1.45f;
                float x = sx + gap;
                float y = sy - h * 0.5f;        // 垂直居中在地名上，不越到相邻行

                if (GlyphAvailable(style))
                {
                    var r = new Rect(x, y, w, h);
                    // ① 8 方向黑色描边（1~2px）：白档在浅色地图缩略图上也能看清
                    style.normal.textColor = ColorOutline;
                    for (int d = 0; d < 8; d++)
                    {
                        GUI.Label(new Rect(r.x + OutlineDx[d] * outline, r.y + OutlineDy[d] * outline,
                                           r.width, r.height), "☣", style);
                    }
                    // ② 主体
                    style.normal.textColor = col;
                    GUI.Label(r, "☣", style);
                }
                else
                {
                    // 兜底：同色实心方块 + 黑色描边（字体没有 ☣ 字形时仍然是个清楚的标记）
                    float side = fs * 0.85f;
                    float bx = x;
                    float by = sy - side * 0.5f;
                    GUI.color = ColorOutline;
                    GUI.DrawTexture(new Rect(bx - outline, by - outline,
                                             side + outline * 2f, side + outline * 2f), Texture2D.whiteTexture);
                    GUI.color = col;
                    GUI.DrawTexture(new Rect(bx, by, side, side), Texture2D.whiteTexture);
                }
            }
        }
        finally
        {
            GUI.color = oldColor;
            if (style != null) style.normal.textColor = Color.white;
        }
    }

    /// <summary>该地点在感染率文件里的百分比（多 key 取最大；没有数据 = 0）。</summary>
    private int RateFor(string[] keys)
    {
        if (keys == null || keys.Length == 0 || _rates.Count == 0) return 0;
        int max = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (_rates.TryGetValue(keys[i], out var v) && v > max) max = v;
        }
        return max;
    }

    /// <summary>
    /// 是否在战局里（<c>GameWorld.Instance != null</c>）。
    /// <para>战局内不可能有「地图选择界面」⇒ 连 1 秒一次的 FindObjectsOfType 都省掉，做到真正接近零开销。</para>
    /// </summary>
    private static bool InRaidWorld()
    {
        try { return Comfort.Common.Singleton<EFT.GameWorld>.Instance != null; }
        catch { return false; }
    }

    /// <summary>
    /// 颜色分档（用户口径）：
    /// <c>p&lt;=0</c> = 不画；<c>1..33</c>=白；<c>34..66</c>=黄；<c>67..100</c>=红。
    /// </summary>
    private static Color ColorFor(int p)
    {
        if (p <= 0) return ColorWhite;
        if (p <= 33) return ColorWhite;
        if (p <= 66) return ColorYellow;
        return ColorRed;
    }

    private GUIStyle EnsureStyle(int fs)
    {
        var font = GUI.skin != null ? GUI.skin.font : null;
        if (_style != null && _styleFs == fs && ReferenceEquals(_styleFont, font)) return _style;

        _style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            richText = false,
            clipping = TextClipping.Overflow,
        };
        _style.normal.textColor = Color.white;
        _styleFs = fs;
        _styleFont = font;
        _glyphChecked = false;      // 字体换了 ⇒ 重新确认 ☣ 字形
        return _style;
    }

    /// <summary>GUI 字体是否包含 ☣ 字形（没有就用色块兜底，别让用户什么都看不到）。</summary>
    private bool GlyphAvailable(GUIStyle style)
    {
        if (_glyphChecked) return _glyphOk;
        _glyphChecked = true;
        try
        {
            var f = style != null && style.font != null ? style.font : (GUI.skin != null ? GUI.skin.font : null);
            _glyphOk = f != null && f.HasCharacter('\u2623');
        }
        catch { _glyphOk = true; }
        return _glyphOk;
    }

    /// <summary>异常只打一次（避免每帧刷屏）。</summary>
    private void LogErrorOnce(Exception ex)
    {
        if (_errLogged) return;
        _errLogged = true;
        try { Plugin.Log?.LogWarning("[EventRotator.Client][感染标记] 已停用绘制（只报一次）: " + ex); } catch { }
    }
}
