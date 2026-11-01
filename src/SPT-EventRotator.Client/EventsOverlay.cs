using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using BepInEx.Configuration;
using UnityEngine.Networking;
using UnityEngine;

namespace EventRotator.Client;

/// <summary>
/// ★ v0.1.3 新增：**当前生效活动悬浮面板**（按热键弹出/收起）。
///
/// 交互参考 DrakiaXYZ 的 SPT-QuestTracker（MIT）：按一个键切显隐，右上/左上角一块半透明面板，
/// 每条 = 「标题 + 右侧进度/状态」+ 下面若干行说明。区别是：
///   · 它显示**战局内任务进度**（数据来自 QuestController + 自己的 .bundle 预制体）；
///   · 我们显示**当前生效的活动**（数据来自服务端写的 `当前生效活动.txt`），
///     所以**不需要任何资源包/预制体**，整块 UI 用纯代码画，升级游戏也不会因为预制体丢失而崩。
///
/// 数据来源（服务端每次套用活动后写入，客户端每 1 秒读一次）：
/// <code>
/// #事件|① 季节|万圣节活动|Halloween|halloween|开启本体【万圣节…】季节；开放 3 个活动任务
/// #季节|off||HALLOWEEN|
/// </code>
///
/// 显示布局（对齐 QuestTracker 的观感）：
/// <code>
/// ┌──────────────────────────────────────────────┐
/// │ 当前生效的活动                          2026-10-14 20:55 │   ← 标题条
/// │ 万圣节活动                            ① 季节  ×1           │   ← 活动名 + 右侧分类/数量
/// │   开启本体【万圣节（僵尸/召唤/邪教任务）】季节；开放 3 个活动任务   │   ← 内容说明
/// │ ...                                          │
/// │ 天气季节: off   活动季节: HALLOWEEN                     │   ← 页脚
/// └──────────────────────────────────────────────┘
/// </code>
/// </summary>
internal class EventsOverlay : MonoBehaviour
{
    /// <summary>服务端状态文件里各机器可读行的前缀。</summary>
    private const string EventPrefix = "#事件|";
    private const string SeasonPrefix = "#季节|";
    private const string RotPrefix = "#轮换|";
    private const string ManualPrefix = "#手动|";

    private sealed class Row
    {
        public string Cat = "";
        public string NameZh = "";
        public string NameEn = "";
        public string Id = "";
        public string Desc = "";
        public string DescEn = "";   // ★ v1.2.0 服务端给的结构化英文摘要（第 7 列）
    }

    /// <summary>★ v0.1.3 轮换状态（服务端本次启动实际套用的档位 + 预计算好的下一档）。</summary>
    private sealed class RotInfo
    {
        public bool Has;
        public int AppliedSlot = -1;      // 服务端**本次启动**实际套用的档位
        public int IntervalDays = 3;
        public int RebaseHour;
        public DateTime NextStart;
        public bool Enabled;
        public string Mode = "";
        public List<string> CurIds = new();
        public List<string> NextIds = new();
    }

    private sealed class Status
    {
        public string Updated = "";
        public string Source = "";
        public int Count;
        public readonly List<Row> Rows = new();
        public string SeasonMode = "";      // `#季节|p[0]`=模式串（followEvent/rotate/fixed/off）—— **只做诊断/判断是否轮换，不当季节显示**
        public string WeatherSeason = "";   // `#季节|p[1]`=仅 fixed 模式有值的"指定天气季节"
        public string EventSeason = "";      // `#季节|p[2]`=仅 fixed 模式有值的"指定活动季节"
        /// <summary>★ C6 `#季节|p[3]`=**实际轮换到的季节**（rotate 时才有值）。历史字段名，语义是季节不是档位。</summary>
        public string RotateNow = "";
        /// <summary>★ v0.1.3 当前季节的一句话说明（服务端 SeasonRotation.FriendlySeason）。</summary>
        public string SeasonFriendly = "";
        /// <summary>★ v1.3.1 服务端实际开启的活动季节（圣诞/万圣/新年…）。</summary>
        public string AppliedEventSeason = "";
        public RotInfo Rot = new();
        public string ManualId = "";
        public List<string> AppliedIds = new();
    }

    public static EventsOverlay Instance;
    /// <summary>★ v1.3.0 最近一次 HTTP 拉取是否成功（仅诊断用）。</summary>
    private static bool _remoteOk;
    /// <summary>★ v1.6.5【复核 #7】最近一次 **HTTP 拉取成功**的时刻。
    /// <para>远程缓存的"新鲜度"必须按**拉取成功**算，不能按文件 mtime 算 ——
    /// 内容没变就不再写盘（见 <see cref="_remoteTextLast"/>），mtime 会一直停在旧时间，
    /// 若仍用 mtime 判新鲜，60 秒后客机会误判"远程缓存过期"而退回本机旧文件。</para></summary>
    private static DateTime _remoteOkAt = DateTime.MinValue;
    /// <summary>★ v1.6.5【复核 #7】上一次写进 `当前生效活动.remote.txt` 的内容（相同就跳过写盘）。</summary>
    private string _remoteTextLast;
    /// <summary>★ C7 本次读的是不是 `当前生效活动.remote.txt`（= 主机经 HTTP 拉下来并缓存的副本）。
    /// 与 <see cref="_remoteOk"/> 一起决定页脚「数据来源：主机 / 本机 / 已过期」。</summary>
    private bool _usingRemoteFile;

    /// <summary>面板当前是否可见（热键日志/F12 用）。</summary>
    public bool IsVisible => _visible;

    /// <summary>OnGUI 被调用过几次 —— 诊断用：面板不显示时用来区分"没弹出"还是"IMGUI 没被调用"。</summary>
    public int GuiCalls { get; private set; }
    /// <summary>最近一次实际绘制的时间（realtimeSinceStartup）。</summary>
    public float LastDrawTime { get; private set; }

    /// <summary>★ 诊断开关：true 时即使没按热键也画一个小角标，用来确认 IMGUI 通路是否可用。</summary>
    public static bool ProbeAlwaysDraw;

    /// <summary>★ 诊断：把面板内部状态打到 BepInEx 日志（F12 开关或与自检角标联动）。</summary>
    public static bool LogEnabled;

    private string _modDir;
    private Status _status = new();
    private float _timer;
    private float _fadeUntil;              // 自动隐藏（可选）
    private bool _visible;

    // ══════════ ★★★ v0.1.7【性能优化 G1~G4 / Y1~Y2】面板与功能 Tick 的缓存（**零行为变化**）══════════
    //  G1：面板样式以前每次 OnGUI 都 new 9 个 GUIStyle（IMGUI 每帧 ≥2 次事件）⇒ 现在只在
    //      (GUI.skin, fs) 变化时重建；**样式参数逐字照搬** ⇒ 绘制结果完全一致。
    //  G2/G3：列表排序结果 + 逐行显示字符串按 `_statusRevision` 缓存（Refresh 成功解析后 +1）。
    //  G4：万圣节门控用的"生效 id 列表 + bool"按 revision 缓存，不再每帧 LINQ。
    //  Y1：`TickClientFeatures` 整块 0.25 s 一次（**Harmony 事件回调不受节流** ⇒ 不漏触发）。
    private GUIStyle _titleStyle, _nameStyle, _catStyle, _descStyle, _footStyle, _hintStyle, _seasonStyle, _groupStyle, _probeStyle;
    private float _styleFs = float.NaN;
    private object _styleSkin;

    private sealed class RowView
    {
        public int Group;           // = CatOrder(r.Cat)
        public string GroupTitle;   // = GroupTitle(r.Cat)
        public string NameZh;       // 排序键（**与优化前同一个键**：原排序用 NameZh，不是显示标签）
        public string Label;        // 活动名行（含英文后缀，与优化前拼法逐字相同）
        public string Cat;          // 右侧分类标签
        public string DescLine;     // "   " + Desc（Desc 为空时 = ""）
    }

    private int _statusRevision;        // 状态刷新计数（Refresh 成功解析后 +1）

    // ══════════ ★★★ v0.1.7【UI 性能 · OnGUI 每帧零分配（文本逐字不变）】══════════
    //   以前 OnGUI（每帧 ≥2 次：Layout + Repaint）每次都重新算：RestartHint() 的 ConvertAll/Join/LINQ、
    //   SeasonText() 的 new StringBuilder、FooterText() 的 new StringBuilder + Join、以及每行 new GUIContent。
    //   现在：**状态一变（`_statusRevision`）就预算一次**，OnGUI 只读字段；文本拼法与内容**逐字相同**。
    private int _cacheRev = -1;          // ★★★ C1：这份缓存**只属于 RestartHint()**（键 = _statusRevision + _hintSlot）
    private string _cacheSeasonText;     // SeasonText() 结果（纯数据，无时间依赖）
    private string _cacheFooterNextIds;  // FooterText() 里 string.Join(" + ", rot.NextIds) 的结果
    private int _textRev = -1;           // ★★★ C1：**文本类缓存（SeasonText / FooterNextIds）自己**的版本号
    private bool _textLang;              // ★★★ C1：文本缓存是按哪种语言构建的（切语言必重建）
    private StringBuilder _sbFooter = new StringBuilder(512);            // 页脚复用缓冲（不清空池外的分配）
    private string _cacheHintText;       // RestartHint() 结果（键 = _cacheRev + _hintSlot）
    private int _hintSlot = int.MinValue;
    private readonly GUIContent _gcHint = new GUIContent();
    private readonly GUIContent _gcSeason = new GUIContent();
    private readonly GUIContent _gcFooter = new GUIContent();
    private readonly GUIContent _gcRow = new GUIContent();
    private int _viewRevision = -1;     // 面板缓存对应的 revision
    private List<RowView> _viewRows;
    private bool _viewLang;   // ★ v1.3.3 缓存是用哪种语言构建的
    private int _activeRev = -1;        // G4 缓存对应的 revision
    private string[] _activeIds;
    private bool _halloweenActive;
    private float _featureTickAt;       // Y1 节流时间戳
    private string _statusSig;          // Y2：状态文件 (mtime,长度) 签名
    /// <summary>★ C7/C10 最近一次成功读到的状态文件**原文**（供 F12 只读行复用，避免它再自己 stat/读盘）。</summary>
    private string _statusRawText;
    /// <summary>★ C2 读/解析失败时的日志去重（同类错误只打一次，避免每 3 秒刷屏）。</summary>
    private readonly HashSet<string> _refreshErrLogged = new();

    // ── 外观常量（尽量贴近 QuestTracker 的观感）──
    private const int BaseWidth = 560;
    private const int PadX = 14;
    private const int PadY = 10;
    private static readonly Color BgColor = new(0.06f, 0.07f, 0.08f, 0.86f);
    private static readonly Color TitleColor = new(1f, 1f, 1f, 0.97f);
    private static readonly Color NameColor = new(0.98f, 0.86f, 0.35f, 1f);     // 活动名：暖黄，像任务名
    private static readonly Color DescColor = new(0.78f, 0.80f, 0.82f, 1f);
    private static readonly Color CatColor = new(0.55f, 0.78f, 1f, 1f);
    private static readonly Color FootColor = new(0.62f, 0.66f, 0.70f, 1f);
    private static readonly Color LineColor = new(1f, 1f, 1f, 0.10f);

    public static EventsOverlay Attach(string modDir)
    {
        try
        {
            if (Instance != null) return Instance;
            var go = new GameObject("EventRotatorOverlay");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.None;
            var c = go.AddComponent<EventsOverlay>();
            if (c == null) return null;
            c._modDir = modDir;
            // ★ AddComponent 在"宿主 GameObject 未激活"时不会立刻走 Awake，
            //   所以这里**兜底再赋一次** Instance，保证热键分支一定拿得到对象。
            if (Instance == null) Instance = c;
            // ★ v1.6.5【复核 #3】留住协程句柄，`SetModDir()` 换目录时要重启它（旧协程握着旧目录）
            try { c._fetchRemoteCo = c.StartCoroutine(c.FetchRemoteLoop()); } catch { }
            Plugin.Log?.LogInfo($"[EventRotator.Client] 面板组件已创建（OnGUI 待命，dir={modDir}）");
            return Instance;
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError("[EventRotator.Client] 创建面板组件失败: " + ex);
            return null;
        }
    }

    /// <summary>★ v1.6.5【复核 #3】远程拉取协程句柄（换 mod 目录时用来停旧/起新）。</summary>
    private Coroutine _fetchRemoteCo;

    /// <summary>
    /// ★★★ v1.6.5【复核 #3】F12 改了「服务端 mod 目录」后，悬浮面板仍读旧目录。
    /// <para>原因：`_modDir` 只在 <see cref="Attach"/> 里赋一次，而 `Attach` 见 `Instance != null` 就早退，
    /// 所以改目录必须重启游戏才生效。这里给外部（Plugin 的 SettingChanged）一个明确的换目录入口：
    /// ① 指向新目录；② 作废状态文件缓存（签名 / 原文 / 文件时间 / 远程内容指纹）⇒ 下一轮全部重读；
    /// ③ 重启 5 秒远程拉取协程。</para>
    /// </summary>
    public void SetModDir(string modDir)
    {
        try
        {
            var d = modDir ?? "";
            if (string.Equals(_modDir ?? "", d, StringComparison.Ordinal)) return;
            _modDir = d;
            // 作废"按旧目录读到的"一切缓存，下一轮 Refresh 会从新目录重新读
            _statusSig = null;
            _statusFileTime = DateTime.MinValue;
            _statusRawText = null;
            _status = new Status();
            _statusRevision++;
            _usingRemoteFile = false;
            _remoteOk = false;
            _remoteOkAt = DateTime.MinValue;   // 旧目录的"拉取成功时刻"不能算到新目录头上
            _remoteTextLast = null;     // 新目录的远程缓存要重新写一次
            // "本客户端启动时看到的活动"也属于旧数据源 ⇒ 一起重置，免得换目录后误报「需要重启游戏」
            _clientStartCaptured = false;
            _clientStartIds.Clear();
            // 重启远程拉取（旧协程用旧 _modDir；虽然循环每轮都会重读字段，但停旧起新最干净）
            try { if (_fetchRemoteCo != null) StopCoroutine(_fetchRemoteCo); } catch { }
            try { _fetchRemoteCo = StartCoroutine(FetchRemoteLoop()); } catch { }
            Plugin.Log?.LogInfo("[EventRotator.Client] 服务端 mod 目录已切换 → 面板改读: " + (d.Length > 0 ? d : "(自动)"));
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning("[EventRotator.Client] 切换服务端 mod 目录失败: " + ex.Message);
        }
    }

    private void Awake()
    {
        var sb = new StringBuilder();
        sb.Append("按热键弹出/收起「当前生效的活动」面板。\n");
        sb.Append("内容由服务端每次套用活动后写入，客户端每秒刷新一次。\n");
        _ = sb;

        // ★★★ v0.1.6：本组件同时充当「仪式撤离点锁」的宿主（实现见 RitualExitLock.cs）。
        //   为什么挂在这里（而不是 Plugin.cs）：本组件是插件里**唯一常驻的 MonoBehaviour**
        //   （由 Plugin.Awake 的 EventsOverlay.Attach 创建、Plugin.Update 还会自愈式重建），
        //   天然有 Awake/Update 两个挂载点 ⇒ 与 Plugin.cs **零耦合**（当时 Plugin.cs 正被另一个
        //   writer 改 F12 同步，写域冲突）。**这是有意为之的宿主选择**；将来若要挪回 Plugin.cs，
        //   把下面这两处调用搬过去即可（Install 幂等，重复调用无害）。
        try { RitualExitLock.Install(Plugin.Log); }
        catch (Exception ex) { Plugin.Log?.LogWarning("[EventRotator.Client][撤离点] 初始化失败（不影响游戏）: " + ex.Message); }

        // ★ v0.1.6 条款③：「致命毒素」的感染与倒计时（见 ToxinInfection.cs）—— 用同一个宿主组件
        try { ToxinInfection.SetLog(Plugin.Log); }
        catch (Exception ex) { Plugin.Log?.LogWarning("[EventRotator.Client][致命毒素] 初始化失败（不影响游戏）: " + ex.Message); }

        // ★ v0.1.6：Halloween 2022「开局随机负面状态」（见 RandomDebuff.cs）
        try { RandomDebuff.SetLog(Plugin.Log); }
        catch (Exception ex) { Plugin.Log?.LogWarning("[EventRotator.Client][随机负面状态] 初始化失败（不影响游戏）: " + ex.Message); }
    }

    /// <summary>
    /// ★★★ v1.6.5【复核 #6】场景加载会销毁 DontDestroyOnLoad 之外的组件（实测日志出现过
    /// `面板组件不在（Instance=null）→ 重新创建`）。销毁时必须把静态 <see cref="Instance"/> 置空，
    /// 否则它指向一个已销毁的 UnityEngine.Object（`== null` 判真但成员访问会抛异常），
    /// 而 `Plugin.Update` 的重建检查也会因为引用非空而跳过。
    /// <para>重建由 <see cref="Plugin.Update"/> 里每 3 秒的低频自愈负责（不再只依赖按热键）。</para>
    /// </summary>
    private void OnDestroy()
    {
        try { if (ReferenceEquals(Instance, this)) Instance = null; } catch { }
        try { _fetchRemoteCo = null; } catch { }
    }

    /// <summary>
    /// ★ v1.3.0【联机】每 5 秒向**服务端**要一次状态（`GET /eventrotator/status`），
    /// 成功就缓存成 `当前生效活动.remote.txt`，由 <see cref="StatusFile"/> 优先读取。
    /// <para>拉不到（单机、主机、服务端没装 1.3.0）→ 什么都不做，行为与旧版完全一致。</para>
    /// </summary>
    private IEnumerator FetchRemoteLoop()
    {
        while (true)
        {
            yield return new WaitForSecondsRealtime(5f);
            // ★ v1.3.2：手填 > fika.jsonc 自动探测 > 127.0.0.1
            var baseUrl = RemoteHost.Resolve(_modDir, EventsOverlayConfig.RemoteStatusUrl);

            // ★★★ C9①【先校验地址再构造 UnityWebRequest】
            //   旧代码直接把 baseUrl + "/eventrotator/status" 丢给 UnityWebRequest.Get：
            //   用户手填 "1.2.3.4:6969"（漏了 http://）时 Unity 会抛异常或发一个畸形请求，
            //   而且**每个 5 秒循环都会再来一次**。现在：缺 scheme 就补 http://，再用 Uri.TryCreate 校验；
            //   真非法就只打一次日志并跳过本轮。
            if (!TryBuildStatusUrl(baseUrl, out var full))
            {
                if (!_invalidUrlLogged)
                {
                    _invalidUrlLogged = true;
                    Plugin.Log?.LogWarning("[EventRotator.Client] 主机地址无效，已跳过远程状态拉取: '" + (baseUrl ?? "") + "'");
                }
                continue;
            }

            UnityWebRequest req = null;
            var ok = false;
            var body = "";
            try
            {
                try { req = UnityWebRequest.Get(full); req.timeout = 4; }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("[EventRotator.Client] 创建状态请求失败（" + full + "）: " + ex.Message);
                    continue;
                }

                yield return req.SendWebRequest();

                try
                {
                    ok = req.result == UnityWebRequest.Result.Success;
                    if (ok) body = req.downloadHandler.text;
                }
                catch { ok = false; }
            }
            finally
            {
                // ★★★ C9②【Dispose 放进 finally】旧代码只在正常路径 Dispose；
                //   一旦 SendWebRequest 之后抛异常（断网 / TLS / handler 为 null）就漏掉 Dispose。
                try { req?.Dispose(); } catch { }
            }

            if (!ok) { _remoteOk = false; continue; }
            try
            {
                var text = (string)Newtonsoft.Json.Linq.JObject.Parse(body)["text"];
                if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(_modDir))
                {
                    // ★★★ v1.6.5【复核 #7】内容没变就**不写盘**。
                    //   旧代码每 5 秒无条件 WriteAllText ⇒ 远程缓存文件 mtime 每 5 秒必变
                    //   ⇒ 面板的 (mtime,长度,创建时间) 签名每 5 秒必变 ⇒ `_statusRevision` 每 5 秒 +1
                    //   ⇒ 列表缓存 / 文本缓存 / 重启提示缓存**每 5 秒全量重建一次**（服务端数据其实没变）。
                    if (!string.Equals(text, _remoteTextLast, StringComparison.Ordinal))
                    {
                        System.IO.File.WriteAllText(System.IO.Path.Combine(_modDir, "当前生效活动.remote.txt"),
                            text, new UTF8Encoding(false));
                        _remoteTextLast = text;
                    }
                    _remoteOkAt = DateTime.Now;   // ★ 拉取成功就刷新"新鲜度"（与是否写盘无关）
                    _remoteOk = true;
                }
                else _remoteOk = false;
            }
            catch { _remoteOk = false; }
        }
    }

    /// <summary>★ C9① 地址非法时只打一次日志（避免每 5 秒刷屏）。</summary>
    private static bool _invalidUrlLogged;

    /// <summary>
    /// ★★★ C9① 把（可能缺 scheme 的）基地址拼成可直接请求的状态 URL。
    /// <para>先补 `http://`，再用 <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> 校验；
    /// 非法返回 false（调用方只打一次日志并跳过）。</para>
    /// </summary>
    private static bool TryBuildStatusUrl(string baseUrl, out string full)
    {
        full = null;
        try
        {
            var b = (baseUrl ?? "").Trim();
            if (b.Length == 0) b = "http://127.0.0.1:6969";
            if (b.IndexOf("://", StringComparison.Ordinal) < 0) b = "http://" + b;   // 用户只填了 ip:port
            b = b.TrimEnd('/');
            if (!Uri.TryCreate(b + "/eventrotator/status", UriKind.Absolute, out var uri)) return false;
            if (!(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return false;
            full = uri.ToString();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// ★★★ C7【客机 F12 只读行读到本机旧文件】
    /// <para>把原来私有的 <c>StatusFile()</c> 逻辑提成**可复用**的静态方法：Plugin 生成 F12 只读状态文本、
    /// 以及本类自己读状态，都必须走同一个"远程缓存优先"的路径，否则客机上会读到**自己上次开服留下的旧文件**。</para>
    /// <para>规则：`当前生效活动.remote.txt`（60 秒内）→ 否则 `当前生效活动.txt`。</para>
    /// </summary>
    internal static string StatusFilePath(string modDir)
    {
        try
        {
            if (!string.IsNullOrEmpty(modDir))
            {
                var remote = Path.Combine(modDir, "当前生效活动.remote.txt");
                var fi = new FileInfo(remote);
                if (fi.Exists)
                {
                    // ★ v1.6.5【复核 #7】"新鲜"以**最近一次 HTTP 拉取成功**为准（内容没变不写盘 ⇒ mtime 不再前进）；
                    //   同时保留文件 mtime 这一条：进程刚启动、还没拉过一次时，上一次运行留下的远程缓存仍然可信。
                    var now = DateTime.Now;
                    bool freshByFetch = _remoteOkAt != DateTime.MinValue && (now - _remoteOkAt).TotalSeconds < 60;
                    bool freshByFile = (now - fi.LastWriteTime).TotalSeconds < 60;
                    if (freshByFetch || freshByFile) return remote;
                }
            }
        }
        catch { }
        return Path.Combine(modDir ?? "", "当前生效活动.txt");
    }

    /// <summary>实例版：额外记下"这次读的是不是主机缓存"，供页脚「数据来源」提示用。</summary>
    private string StatusFile()
    {
        var f = StatusFilePath(_modDir);
        _usingRemoteFile = f.EndsWith(".remote.txt", StringComparison.OrdinalIgnoreCase);
        return f;
    }

    /// <summary>
    /// ★ C7【数据来源提示】主机 / 本机 / 已过期。
    /// <para>判据：① 读到的是刚拉下来的主机缓存 ⇒ 主机；② 主机缓存还在有效期但最近一次 HTTP 失败 ⇒ 已过期；
    /// ③ 手填了主机地址却拿不到 ⇒ 已过期（本机文件是旧货）；④ 其余 ⇒ 本机。</para>
    /// </summary>
    internal string DataSourceText()
    {
        try
        {
            if (_usingRemoteFile) return _remoteOk ? Locale.T("主机", "host") : Locale.T("已过期", "stale");
            if (!_remoteOk && !string.IsNullOrEmpty((EventsOverlayConfig.RemoteStatusUrl ?? "").Trim()))
                return Locale.T("已过期", "stale");
            return Locale.T("本机", "local");
        }
        catch { return Locale.T("本机", "local"); }
    }

    /// <summary>
    /// ★ v0.1.6：某个活动 id 此刻是否生效（数据来自服务端写的 `当前生效活动.txt` 的 `#事件|…|<id>|…` 行，
    /// 由 <see cref="Refresh"/> 解析进 <c>_status.Rows</c>）。
    /// 用途：条款②「进图即锁撤离点」**只对 june2022_toxin 生效**。读不到/空格 → false ⇒ 不锁（fail-safe）。
    /// </summary>
    private bool IsActivityActive(string id)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) return false;
            var rows = _status?.Rows;
            if (rows == null) return false;
            foreach (var r in rows)
                if (r != null && string.Equals((r.Id ?? "").Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// ★★★ v0.1.6【用户裁决：`june2022_toxin` 只在海关（bigmap）生效】**地图三态**判定。
    /// 判据 = <c>Comfort.Common.Singleton&lt;GameWorld&gt;.Instance.LocationId</c>
    /// （Assembly-CSharp:551349 是 `GameWorld.LocationId` 属性；官方同款用法见 :435760
    /// <c>public static string LocationID =&gt; Comfort.Common.Singleton&lt;GameWorld&gt;.Instance.LocationId;</c>）。
    /// <para>返回值：<c>0</c> = **未就绪**（GameWorld 还没建好 / LocationId 还没赋值）；
    /// <c>1</c> = 是海关；<c>2</c> = 已经拿到地图且**不是**海关。</para>
    /// <para>⚠ 为什么必须分三态：实测日志里出现过「同一局内先判成'不是海关'、之后又正常执行」
    /// （用户当时**就在海关**）⇒ 早期 `LocationId` 还没就绪被误判成"非目标图" ⇒ **误报 + 在别的图上可能误执行**。
    /// 现在只有**确实拿到地图**（非 0）才做判断；未就绪 ⇒ 消费方"等待"（不动作、不打"跳过"）。</para>
    /// </summary>
    private byte MapState()
    {
        try
        {
            var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            if (gw == null) return 0;                       // 主菜单 / 战局还没建好 ⇒ 未就绪
            var loc = gw.LocationId;
            if (string.IsNullOrEmpty(loc)) return 0;        // GameWorld 有了但 LocationId 还没赋值 ⇒ 未就绪
            return string.Equals(loc, "bigmap", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)2;
            // bigmap = 海关（客户端同表 :488871 / 服务端 bossSpawns 键同名）
        }
        catch { return 0; }
    }

    /// <summary>
    /// ★ v0.1.3 用**与服务端 Rotation.SlotAt 完全相同**的公式算出"现在应该是哪个档位"。
    /// 必须是同一套算法（同样的 epoch / slotRebaseHour / intervalDays），否则判定会漂移。
    /// </summary>
    private static int SlotAt(DateTime now, int intervalDays, int rebaseHour)
    {
        try
        {
            int iv = Math.Max(1, intervalDays);
            int rh = Math.Clamp(rebaseHour, 0, 23);
            var shifted = now.AddHours(-rh);
            var epoch = new DateTime(2020, 1, 1, rh, 0, 0);
            var days = (shifted.Date - epoch.Date).TotalDays;
            return (int)Math.Floor(days / iv);
        }
        catch { return 0; }
    }

    /// <summary>
    /// ★ v0.1.3 判断"现在是不是需要重启才能生效"。返回 null = 一切正常。
    /// <para>
    /// 两种情况：
    ///   ① **需要重启游戏**：服务端已经换好了活动，但**本客户端**是在那之前启动的
    ///      —— 客户端只在游戏启动时拉一次 `/client/items`（物品模板）与 `/client/globals`，
    ///      所以物品类 / 口渴速率类改动要重开游戏才会反映。
    ///   ② **需要重启服务端**：按时间算现在已经到了新档位，但服务端**本次启动时还在旧档位**
    ///      （= 服务端还没重启过），此时先重启服务端。
    /// </para>
    /// 顺序：先服务端后游戏（服务端是前置）。
    /// </summary>
    public string RestartHint()
    {
        // ★ v0.1.7【UI 性能】整体结果缓存：键 =（状态版本 `_statusRevision`，当前时间档位 `nowSlot`）
        //   ⇒ 文本与"每帧现算"逐字相同（唯一的时变项就是 nowSlot 那一次整数比较），但每帧只读字段。
        try
        {
            var r0 = _status?.Rot;
            int slot0 = (r0 != null && r0.Has && r0.Enabled) ? SlotAt(DateTime.Now, r0.IntervalDays, r0.RebaseHour) : int.MinValue;
            if (_cacheRev == _statusRevision && _hintSlot == slot0) return _cacheHintText;
            _cacheRev = _statusRevision;
            _hintSlot = slot0;
            _cacheHintText = ComputeRestartHint();
            return _cacheHintText;
        }
        catch { return ComputeRestartHint(); }
    }

    private string ComputeRestartHint()
    {
        try
        {
            var rot = _status.Rot;
            var now = DateTime.Now;

            // ② 服务端落后于时间 → 需要重启服务端
            if (rot.Has && rot.Enabled)
            {
                int nowSlot = SlotAt(now, rot.IntervalDays, rot.RebaseHour);
                if (nowSlot > rot.AppliedSlot)
                    return Locale.T("⚠ 已到换档时间，需要【重启服务端】\n", "⚠ A new rotation slot is due — RESTART THE SERVER\n")
                         + Locale.T($"服务端本次启动时是档位 #{rot.AppliedSlot}，现在应是 #{nowSlot}",
                                    $"The server started on slot #{rot.AppliedSlot}; it should now be #{nowSlot}")
                         + (rot.NextIds.Count > 0
                            ? Locale.T($"（下一档：{string.Join(" + ", rot.NextIds)}）",
                                       $" (next slot: {string.Join(" + ", rot.NextIds)})")
                            : "");
            }

            // ① 服务端已套用了新活动，但本客户端启动更早 → 需要重启游戏
            var changed = _status.Rows.ConvertAll(r => r.Id);
            bool sameAsClient = changed.Count == _clientStartIds.Count
                                && changed.TrueForAll(id => _clientStartIds.Contains(id));
            if (rot.Has && !sameAsClient)
            {
                var what = _status.Rows.Count > 0
                    // ★ v1.6.5【复核 #1】横幅活动名以前**无条件**取 NameZh ⇒ 英文界面里重启横幅仍是中文。
                    //   改成和下面"手动未生效"那处同一套取名字逻辑。
                    ? string.Join(" + ", _status.Rows.ConvertAll(r =>
                        Locale.En && !string.IsNullOrEmpty(r.NameEn) ? r.NameEn : r.NameZh))
                    : Locale.T("（当前无活动）", "(no events active)");
                return Locale.T("⚠ 活动已变化，需要【重启游戏】才能完全生效\n", "⚠ Events changed — RESTART THE GAME to fully apply\n")
                     + Locale.T("现在生效：", "Currently applied: ") + what + "\n"
                     + Locale.T("（物品模板 / globals 只在游戏启动时拉取一次）",
                                "(item templates / globals are only fetched once at game startup)");
            }

            // ③ 手动选了活动，但和服务端实际套用的不一致 → 需要重启服务端
            if (rot.Has && !string.IsNullOrEmpty(_status.ManualId)
                && !string.Equals(_status.ManualId, "none", StringComparison.OrdinalIgnoreCase))
            {
                // ★★★ v0.1.5 修误报（用户反馈）：手动多选时 `ManualId` 是**整串**
                //   （"a,b"），而生效列表里是**分开的 id** ⇒ 整串比对永远不相等 ⇒
                //   每次多选都会误报"需要重启服务端" ✗。这里拆成多个 id 逐个比对。
                var manualIds = (_status.ManualId ?? "")
                    .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                var missing = manualIds
                    .Where(id => !_status.AppliedIds.Exists(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (missing.Count > 0)
                {
                    var shown = missing.Select(id =>
                    {
                        var r0 = _status.Rows.Find(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                        if (r0 == null) return id;
                        // ★ C5 英文模式优先用英文名（中文模式逐字不变，仍是原中文名）
                        var nm = Locale.En && !string.IsNullOrEmpty(r0.NameEn) ? r0.NameEn : r0.NameZh;
                        return id + Locale.T($"（{r0.NameZh}）", $" ({nm})");
                    });
                    var joiner = Locale.T("、", ", ");
                    return Locale.T("⚠ 你手动选择的活动还没生效，需要【重启服务端】\n", "⚠ Manually selected events are not applied yet — RESTART THE SERVER\n")
                         + Locale.T("未生效：", "Not applied: ") + string.Join(joiner, shown)
                         + (manualIds.Count > missing.Count
                            ? Locale.T($"（另外 {manualIds.Count - missing.Count} 个已生效）",
                                       $" ({manualIds.Count - missing.Count} more already applied)")
                            : "");
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>本客户端**首次读到状态文件时**的生效活动 id（用来判断之后是否变过）。</summary>
    private readonly List<string> _clientStartIds = new();
    private bool _clientStartCaptured;

    private DateTime _statusFileTime = DateTime.MinValue;
    private static readonly DateTime _clientStartTime = DateTime.Now;

    /// <summary>状态文件最后写入时间（判断"服务端是否比客户端更晚换过活动"）。</summary>
    public DateTime StatusFileTime => _statusFileTime;

    /// <summary>
    /// ★ C7/C10 最近一次**成功读到**的状态文件原文（解析用的就是这一份）。
    /// <para>F12 的「当前生效活动」只读行直接复用这个字符串，不再自己 stat + ReadAllText ——
    /// 既少一套 syscall，也保证 F12 与悬浮面板显示的是**同一份**数据（客机上就是主机拉下来的那份）。</para>
    /// </summary>
    internal string RawStatusText => _statusRawText;

    /// <summary>★ v1.2.0 让外部（切语言时）强制重建一次文本缓存：只脏缓存版本号，不重读文件。</summary>
    public static void RequestRefresh()
    {
        try
        {
            if (Instance == null) return;
            // ★★★ C1【各自独立失效】以前这里只置 `_cacheRev = -1`，而 `_cacheRev` 又是
            //   RestartHint() 与 EnsureTextCache() 共用的 ⇒ OnGUI 每帧先跑 RestartHint()（它会把
            //   `_cacheRev` 写回 `_statusRevision`），后面的文本缓存就永远认为"已是最新"，
            //   于是切语言 / 请求刷新后**季节文本一直是旧语言**。
            //   现在：文本缓存用它自己的 `_textRev`；重启提示用它自己的 `_cacheRev`；列表用它自己的
            //   `_viewRevision`（另有 `_viewLang` 兜底）—— 三个一起置脏，谁也不会洗白谁。
            Instance._cacheRev = -1;        // 重启用提示（RestartHint）
            Instance._textRev = -1;         // 文本类缓存（SeasonText / FooterNextIds）
            Instance._viewRevision = -1;    // 活动列表缓存
        }
        catch { }
    }

    public void Refresh()
    {
        try
        {
            var f = StatusFile();
            // ★ v1.0.1【性能】只构造一次 FileInfo，靠 fi.Exists 判断，省掉 File.Exists + GetLastWriteTime 两次 syscall
            FileInfo fi0;
            try { fi0 = new FileInfo(f); if (!fi0.Exists) return; } catch { return; }
            DateTime fileTime;
            try { fileTime = fi0.LastWriteTime; } catch { fileTime = DateTime.MinValue; }
            // ★★★ v0.1.7【Y2】文件没变（mtime + 长度都一样）⇒ **不重复解析**（内容必然相同 ⇒ 行为不变）。
            //   ⚠ 与优化前的差别：省掉的是"同一个内容再解析一遍"，解析结果与 `_status` 内容完全一致。
            //   ★ v0.1.8【复核 R2-B2】**签名加固 + 残余窗口的如实说明**：
            //     加固：把 `CreationTimeUtc` 也纳入签名 —— 若写入方走"临时文件 + 原子替换/删除重建"，
            //           即便 mtime 被保留、CreationTime 也会变 ⇒ **零额外 I/O**（FileInfo 已在手）就多一重保险。
            //     残余：若写入方**原地覆盖**（WriteAllText 到已存在文件），签名仍只有 mtime+长度
            //           ⇒ 理论上存在"(同长度) 且在同一个 100ns tick 内重写"的漏检窗口。
            //           **实测不可能**（普通文件写入的 mtime 必然前进）。若日后要严格零窗口，
            //           唯一办法是读内容做哈希 —— 但那会把"每 2 秒一次的全文件读取"还回来，**得不偿失**。
            //   ★★★ C2【一次读失败 ⇒ 面板永久停在旧数据】
            //     旧代码在这里就 `_statusSig = sig`，然后才 ReadAllLines；一旦读失败（文件被服务端
            //     正在重写 / 杀软锁文件），签名已经"认下"了这次内容 ⇒ 下轮签名相同直接 return，
            //     **再也读不进来**。现在：签名先算出来但**不落盘**，等解析成功、`_status = st` 之后才认。
            string sig = null;
            try
            {
                sig = fi0.LastWriteTimeUtc.Ticks + ":" + fi0.Length + ":" + fi0.CreationTimeUtc.Ticks;
                if (sig == _statusSig && _status != null) return;
            }
            catch { sig = null; }
            // 一次读全（同时保留原文给 F12 只读行复用；按行切分与 ReadAllLines 等价）
            var rawText = File.ReadAllText(f, Encoding.UTF8);
            var lines = rawText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var st = new Status();
            foreach (var raw in lines)
            {
                var line = raw ?? "";
                if (line.StartsWith(EventPrefix, StringComparison.Ordinal))
                {
                    var parts = line.Substring(EventPrefix.Length).Split('|');
                    st.Rows.Add(new Row
                    {
                        Cat = parts.Length > 0 ? parts[0] : "",
                        NameZh = parts.Length > 1 ? parts[1] : "",
                        NameEn = parts.Length > 2 ? parts[2] : "",
                        DescEn = parts.Length > 6 ? parts[6] : "",
                        Id = parts.Length > 3 ? parts[3] : "",
                        Desc = parts.Length > 4 ? parts[4] : "",
                    });
                    continue;
                }
                if (line.StartsWith(SeasonPrefix, StringComparison.Ordinal))
                {
                    var p = line.Substring(SeasonPrefix.Length).Split('|');
                    st.SeasonMode = p.Length > 0 ? p[0] : "";
                    st.WeatherSeason = p.Length > 1 ? p[1] : "";
                    st.EventSeason = p.Length > 2 ? p[2] : "";
                    st.RotateNow = p.Length > 3 ? p[3] : "";
                    st.SeasonFriendly = p.Length > 4 ? p[4] : "";
                    st.AppliedEventSeason = p.Length > 5 ? p[5] : "";
                    continue;
                }
                if (line.StartsWith(RotPrefix, StringComparison.Ordinal))
                {
                    var p = line.Substring(RotPrefix.Length).Split('|');
                    var rot = new RotInfo { Has = true };
                    int.TryParse(p.ElementAtOrDefault(0), out rot.AppliedSlot);
                    int.TryParse(p.ElementAtOrDefault(1), out rot.IntervalDays);
                    int.TryParse(p.ElementAtOrDefault(2), out rot.RebaseHour);
                    DateTime.TryParse(p.ElementAtOrDefault(3), out rot.NextStart);
                    rot.Enabled = string.Equals(p.ElementAtOrDefault(4), "true", StringComparison.OrdinalIgnoreCase);
                    rot.Mode = p.ElementAtOrDefault(5) ?? "";
                    foreach (var x in (p.ElementAtOrDefault(6) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) rot.CurIds.Add(x);
                    foreach (var x in (p.ElementAtOrDefault(7) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) rot.NextIds.Add(x);
                    st.Rot = rot;
                    continue;
                }
                if (line.StartsWith(ManualPrefix, StringComparison.Ordinal))
                {
                    var p = line.Substring(ManualPrefix.Length).Split('|');
                    st.ManualId = (p.ElementAtOrDefault(0) ?? "").Trim();
                    foreach (var x in (p.ElementAtOrDefault(1) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) st.AppliedIds.Add(x);
                    continue;
                }
                if (line.StartsWith("更新时间:", StringComparison.Ordinal))
                    st.Updated = line.Substring("更新时间:".Length).Trim();
                else if (line.StartsWith("来源:", StringComparison.Ordinal))
                    st.Source = line.Substring("来源:".Length).Trim();
                else if (line.StartsWith("数量:", StringComparison.Ordinal))
                    int.TryParse(line.Substring("数量:".Length).Trim(), out st.Count);
            }
            if (st.Rows.Count == 0 && st.Count == 0) st.Count = 0;
            _status = st;
            // ★★★ C2：解析完全成功之后才"认下"这份签名（读失败时它还是 null ⇒ 下轮强制重读）
            _statusSig = sig;
            _statusRawText = rawText;       // ★ C7/C10：F12 只读行复用这份原文，不再自己读盘
            _statusFileTime = fileTime;
            _statusRevision++;          // ★ v0.1.7【G2/G3/G4】数据变了 ⇒ 面板/门控缓存失效（唯一失效点）

            // ★ 记住"本客户端第一次看到的活动集合"，之后如果变了就提示重启游戏
            if (!_clientStartCaptured && st.Rows.Count > 0)
            {
                _clientStartCaptured = true;
                foreach (var r in st.Rows) _clientStartIds.Add(r.Id);
                Plugin.Log?.LogInfo("[EventRotator.Client] 面板记录初始活动：" + string.Join(",", _clientStartIds));
            }
        }
        catch (Exception ex)
        {
            // ★★★ C2【失败必须回滚签名 + 留一条日志】
            //   ① `_statusSig = null` ⇒ 下一轮一定重读（旧代码把它留在"已读"状态 ⇒ 永久旧数据）；
            //   ② 日志**同类只打一次**（键 = 异常类型 + 消息），避免每 3 秒刷屏。
            //   读失败仍保留旧 `_status`（避免面板闪空），与改动前一致。
            _statusSig = null;
            LogRefreshErrorOnce(ex);
        }
    }

    /// <summary>★ C2 状态文件读取/解析失败：同类错误只打一次日志（上限 8 类，防刷屏）。</summary>
    private void LogRefreshErrorOnce(Exception ex)
    {
        try
        {
            var key = (ex?.GetType().Name ?? "?") + ":" + (ex?.Message ?? "");
            if (_refreshErrLogged.Count >= 8) return;
            if (!_refreshErrLogged.Add(key)) return;
            Plugin.Log?.LogWarning("[EventRotator.Client] 读状态文件失败（保留旧数据，下一轮重试）: " + ex?.Message);
        }
        catch { }
    }

    public void Toggle()
    {
        _visible = !_visible;
        if (_visible)
        {
            Refresh();
            _scroll = Vector2.zero;                              // ★ v0.1.5 每次弹出从顶部开始
            _shownAt = UnityEngine.Time.realtimeSinceStartup;   // ★ v0.1.4 自动收起用时间戳，不用冻结的截止时间
            _fadeUntil = 0f;
        }
    }

    private void Update()
    {
        try { ScourgeDebug.Tick(); } catch { }
        _timer += UnityEngine.Time.unscaledDeltaTime;

        // ★★ v0.1.3 修 bug：**无条件**每秒刷新状态文件（旧代码只在"面板可见"时刷新，
        //    导致"关了活动 / 换了活动，面板内容不变"——面板一直是旧数据）。
        //    同时检测 force.txt 的改动：用户刚在 F12 勾了活动时，立刻能在面板上看到"需要重启"。
        // ★★★ v1.0.1【性能·第十二轮·玩家实测「走一两秒卡一下」】
        //   原来这里**每 1 秒**跑 Refresh() + DetectForceChange()，两者合计约 8 次文件 stat，全在 Unity 主线程。
        //   装杀软时单次 stat 可达 0.5~2ms ⇒ 每秒 10~20ms 抖动 = 「走一两秒卡一下」。
        //   现在：降到 3 秒，且两个函数各只用一个 FileInfo。
        if (_timer >= 3f)
        {
            _timer = 0f;
            Refresh();
            DetectForceChange();
        }

        // ★ 鼠标滚轮缩放（面板可见时）
        if (_visible) HandleMouse();

        // ★★ v0.1.4 自动收起修正：以前是在 Toggle 时算一个固定截止时间 `_fadeUntil`，
        //   一旦 F12 改「自动收起秒数」不会重新算、也不受配置同步时机影响 → 表现为"设了不生效"。
        //   现在改成**每次显示时记时间戳**，Update 里用**当前配置值**比较：
        //     · F12 改完立刻生效
        //     · 0 = 不自动收（一直显示）
        if (_visible)
        {
            float autoHide = EventsOverlayConfig.AutoHideSeconds;
            if (autoHide <= 0f)
            {
                _shownAt = UnityEngine.Time.realtimeSinceStartup;   // 不自动收 → 时间戳一直刷新
            }
            else if (UnityEngine.Time.realtimeSinceStartup - _shownAt > autoHide)
            {
                _visible = false;
                _fadeUntil = 0;
                Plugin.Log?.LogInfo($"[EventRotator.Client] 面板已自动收起（显示超过 {autoHide:0.#} 秒）");
            }
        }

        // ★★★ v0.1.6【面板优先 · 防御性顺序】插件各功能的 Tick 一律放在**面板逻辑之后**：
        //   即使将来某个 Tick 漏了 try/catch 抛异常，也**截断不到**面板的
        //   （状态刷新 / 鼠标 / 自动收起）逻辑 —— 面板是用户的刚需，任何功能都不许挡它的路。
        //   ⚠ 本次「按 9 面板不出来」的排查结论：**根因不在这段**（没有异常，日志干净），
        //     而是 cfg 里的热键从 Keypad9 变回了默认 F8（详见交付报告）；这里按"最坏情况"加固。
        try { TickClientFeatures(); } catch { }
    }

    /// <summary>
    /// ★ v0.1.6 客户端插件各功能的每帧 Tick（**每个都单独 try/catch 隔离**：
    /// 一个抛异常既不影响其它 Tick，也不影响面板；顺序在面板逻辑之后）。
    /// </summary>
    /// <summary>
    /// ★★★ v0.1.7【G2/G3】面板列表缓存。**为什么行为不变**：
    /// ① 排序键/比较器与优化前逐字相同（先 `CatOrder(Cat)`，再 `string.Compare(NameZh, …, Ordinal)`，同一输入顺序 ⇒ 同一结果）；
    /// ② 三处字符串（活动名 Label / 分类 Cat / 描述 "   " + Desc）拼法与优化前逐字相同；
    /// ③ 唯一失效点 = `_statusRevision`（Refresh 成功解析后 +1）⇒ 数据一变立刻重建，绝不显示旧数据。
    /// </summary>
    private List<RowView> CachedView()
    {
        // ★ v1.3.3【切语言不刷新】缓存键原先只看 `_statusRevision`，
        //   而 `RequestRefresh()` 只把它设成 -1 —— 但 `_cacheRev` 被**多个消费者共用**
        //   （RestartHint / EnsureTextCache 都会把它写回 `_statusRevision`），
        //   于是切语言后第一批消费者一跑，后面的列表缓存就"已经是最新"了 ⇒ 行文本一直是旧语言。
        //   ⇒ 把`当前语言`也纳入缓存键，切语言必重建。
        if (_viewRows != null && _viewRevision == _statusRevision && _viewLang == Locale.En) return _viewRows;
        var src = _status == null ? new List<Row>() : _status.Rows;
        var rows = new List<RowView>(src.Count);
        foreach (var r in src)
        {
            if (r == null) continue;
            var label = Locale.En && !string.IsNullOrEmpty(r.NameEn) ? r.NameEn : r.NameZh;
            if (!Locale.En && !string.IsNullOrEmpty(r.NameEn) && !string.Equals(r.NameEn, r.NameZh, StringComparison.OrdinalIgnoreCase))
                label += " <color=#8a8f94><size=-2>(" + r.NameEn + ")</size></color>";
            rows.Add(new RowView
            {
                Group = CatOrder(r.Cat),
                GroupTitle = GroupTitle(r.Cat),
                NameZh = r.NameZh,
                Label = label,
                Cat = r.Cat,
                DescLine = Locale.En
                    ? (string.IsNullOrEmpty(r.DescEn) ? "" : ("   " + r.DescEn))
                    : (string.IsNullOrEmpty(r.Desc) ? "" : ("   " + r.Desc)),
            });
        }
        rows.Sort((a, b) =>
        {
            if (a.Group != b.Group) return a.Group.CompareTo(b.Group);
            return string.Compare(a.NameZh, b.NameZh, StringComparison.Ordinal);
        });
        _viewRows = rows;
        _viewRevision = _statusRevision;
        _viewLang = Locale.En;   // ★ v1.3.3 与语言绑定
        return rows;
    }

    /// <summary>
    /// ★★★ v0.1.7【G1】面板样式懒建。**为什么行为不变**：9 个 GUIStyle 的构造参数（fontSize/fontStyle/
    /// alignment/wordWrap/richText/normal.textColor）与优化前**逐字相同**，只是从"每帧新建"变成"skin 或字号变了才重建"；
    /// 同一 (skin, fs) 下拿到的是同一组样式对象 ⇒ `GUI.Label`/`CalcHeight` 输出完全一致。
    /// ⚠ 必须在 OnGUI 内调用（`GUI.skin` 只能在 IMGUI 上下文中访问）。
    /// </summary>
    private void EnsureStyles(int fs)
    {
        if (_titleStyle != null && ReferenceEquals(_styleSkin, GUI.skin) && Mathf.Approximately(_styleFs, fs)) return;
        _styleSkin = GUI.skin;
        _styleFs = fs;
        var titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs + 4, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft,
            wordWrap = false, richText = true, normal = { textColor = TitleColor }
        };
        var nameStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs + 1, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft,
            wordWrap = false, richText = true, normal = { textColor = NameColor }
        };
        var catStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs - 1, alignment = TextAnchor.UpperRight, wordWrap = false,
            richText = true, normal = { textColor = CatColor }
        };
        var descStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs - 1, alignment = TextAnchor.UpperLeft, wordWrap = true,
            richText = true, normal = { textColor = DescColor }
        };
        var footStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs - 2, alignment = TextAnchor.UpperLeft, wordWrap = true,
            richText = true, normal = { textColor = FootColor }
        };
        var hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs - 1, alignment = TextAnchor.UpperLeft, wordWrap = true, richText = true,
            normal = { textColor = new Color(1f, 0.82f, 0.35f, 1f) }
        };
        var seasonStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs + 1, alignment = TextAnchor.UpperLeft, wordWrap = true, richText = true,
            normal = { textColor = new Color(0.60f, 0.92f, 0.72f, 1f) }
        };
        var groupStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs - 1, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft, wordWrap = false,
            richText = true, normal = { textColor = new Color(0.80f, 0.84f, 0.88f, 0.95f) }
        };
        _titleStyle = titleStyle; _nameStyle = nameStyle; _catStyle = catStyle; _descStyle = descStyle;
        _footStyle = footStyle; _hintStyle = hintStyle; _seasonStyle = seasonStyle; _groupStyle = groupStyle;
    }

    private void TickClientFeatures()
    {
        // ★★★ v0.1.7【Y1】整块 0.25 s 一次：这里管的都是"进图 / 仪式 / 开局"级状态，
        //   250 ms 粒度无感；**仪式开始是 Harmony 回调（OnRitualStarted）⇒ 不走这里 ⇒ 不漏触发**。
        float nowT = UnityEngine.Time.realtimeSinceStartup;
        if (nowT < _featureTickAt) return;
        _featureTickAt = nowT + 0.25f;
        // ★★★ v0.1.7【用户裁决：联机时不锁撤离】先推联机状态（2 秒缓存，成本≈0），再跑仪表
        try { RitualExitLock.CoopNoLock = Plugin.InCoopGame(); } catch { }

        // 仪式开始 ⇒ 只留 1 个撤离点（见 RitualExitLock.cs）
        try { RitualExitLock.Tick(); } catch { }

        // ★★★ v0.1.6【修法 1 · 仪式锁活动门控】只让"万圣节局"锁撤离点：
        //   nolist（还没读到过活动列表）⇒ 不锁（fail-safe）；读到了但里面没有 seasonalEvent=Halloween 的活动 ⇒ 不锁。
        //   原料 = 服务端 `当前生效活动.txt` 的生效 id（本类 Refresh() 已解析进 _status.Rows）
        //        + `Plugin.SeasonalEventById`（BuildEventToggles 从 events.json 的 seasonalEvent 建的索引）。
        try
        {
            // ★★★ v0.1.7【G4】只在状态刷新后重算（以前每帧 LINQ + 闭包分配）
            if (_activeRev != _statusRevision)
            {
                var ids = new List<string>();
                if (_status != null)
                    foreach (var r in _status.Rows)
                    {
                        var s = r == null ? null : (r.Id ?? "").Trim();
                        if (!string.IsNullOrEmpty(s)) ids.Add(s);
                    }
                _activeIds = ids.ToArray();
                _halloweenActive = RitualExitLock.HasHalloween(_activeIds, Plugin.SeasonalEventById);   // 同一个纯函数
                _activeRev = _statusRevision;
            }
            RitualExitLock.HalloweenActiveKnown = StatusFileTime > DateTime.MinValue;   // 列表是否已读到过
            RitualExitLock.HalloweenActive = _halloweenActive;
        }
        catch { }

        // ★★★ v0.1.6【用户裁决：`june2022_toxin` 只在海关（bigmap）生效】**三态**地图判定：
        //   0 = 未就绪（GameWorld 还没建好 / LocationId 还没赋值）⇒ 两条线"等待"（不动作、不打"跳过"）
        //   1 = 是海关 ⇒ 正常执行；2 = 已就绪但不是海关 ⇒ 跳过 + 一条警告/局
        //   （修的就是实测那次的误报：同一局里早期被判"不是海关"、之后又正常执行过）
        byte mapState = 0;
        try { mapState = MapState(); } catch { }

        // 把「致命毒素 june2022_toxin 是否生效」推给撤离点锁（数据来自服务端 当前生效活动.txt → _status.Rows）
        try { RitualExitLock.ToxinActivityActive = IsActivityActive("june2022_toxin"); } catch { }
        try { RitualExitLock.ToxinMapKnown = mapState != 0; } catch { }      // ★ 只门控条款②「进图即锁」
        try { RitualExitLock.ToxinIsOnBigmap = mapState == 1; } catch { }   // ★ v0.1.7【审计 P2-4】纯改名（旧 ToxinMapOk）

        // 「致命毒素」感染 + 8 分钟倒计时 + 解毒剂重置（见 ToxinInfection.cs）
        try
        {
            ToxinInfection.ActivityActive = RitualExitLock.ToxinActivityActive;
            ToxinInfection.MapKnown = mapState != 0;                         // ★ 只在海关生效（三态）
            ToxinInfection.MapOk = mapState == 1;
            ToxinInfection.Tick();
        }
        catch { }

        // Halloween 2022「开局随机负面状态」（官方 wiki:1584，见 RandomDebuff.cs）
        try
        {
            RandomDebuff.ActivityActive = IsActivityActive("halloween_2022");
            RandomDebuff.Tick();
        }
        catch { }
    }

    /// <summary>面板本次显示的开始时间（realtimeSinceStartup）。</summary>
    private float _shownAt;

    /// <summary>检测 force.txt 变化（用户在 F12 勾选/取消活动时它会变）→ 立即刷新提示。</summary>
    private void DetectForceChange()
    {
        try
        {
            var f = Path.Combine(_modDir ?? "", "force.txt");
            // ★ v1.0.1【性能】只构造一次 FileInfo（原来 File.Exists + GetLastWriteTime + f.Length 三次访问）
            FileInfo ffi;
            try { ffi = new FileInfo(f); if (!ffi.Exists) { _forceKnown = null; _forceCheap = null; return; } } catch { return; }
            // ★ v1.0.1【性能】先把廉价的 (mtime + 长度) 比对提到 ReadAllText 之前 —— 没变就完全不读盘。
            //   原来每次都先 ReadAllText 再比对，等于"白读"（1 Hz 的磁盘 I/O）。
            var cheap = ffi.LastWriteTimeUtc.Ticks.ToString() + "|" + ffi.Length;
            if (cheap == _forceCheap) return;
            // ★★★ C2/C10【指纹必须在**读成功之后**才记】
            //   旧代码先 `_forceCheap = cheap;` 再 ReadAllText：一旦这次读失败（文件正被写 / 被锁），
            //   指纹已经记下 ⇒ 之后同样的 (mtime,长度) 直接 return，**再也不会重读 force.txt**。
            var now = File.ReadAllText(f).Trim();
            _forceCheap = cheap;
            var stamp = now + "|" + ffi.LastWriteTimeUtc.Ticks;
            if (stamp == _forceKnown) return;
            bool first = _forceKnown == null;
            _forceKnown = stamp;

            // 用户本地选了活动 → 复读状态文件并打日志（面板上的"需要重启"横幅会自动出现）
            Refresh();
            var hint = RestartHint();
            Plugin.Log?.LogInfo($"[EventRotator.Client] 检测到 force.txt 变化 = '{now}'"
                                + (first ? "（首次）" : "")
                                + "；重启提示 = " + (string.IsNullOrEmpty(hint) ? "无" : hint.Replace("\n", " ")));
        }
        catch { }
    }

    private string _forceKnown;
    /// <summary>★ v1.0.1【性能】force.txt 的廉价指纹（mtime+长度），用于避免无谓读盘。</summary>
    private string _forceCheap;

    /// <summary>
    /// ★★★ v0.1.5 鼠标互动（用户实测反馈后重排）：
    /// <list type="bullet">
    /// <item><b>左键按住拖动</b> → 移动面板位置（拖动时暂停自动收起，免得拖一半自己没了）</item>
    /// <item><b>滚轮 → 滚动活动内容</b>（v0.1.5 修正：用户要求「滚轮应该是内容滚动，不然活动内容看不全」，
    ///       由 OnGUI 里的 ScrollView 消费；本方法只负责"滚动期间不自动收起"）</item>
    /// <item><b>Ctrl+滚轮</b> → 缩放（0.85×~1.6×，Shift 微调）；中键复位缩放</item>
    /// <item>双击面板 → 位置与缩放一起复位（回到 F12 设定的靠左/靠右）</item>
    /// </list>
    /// 位置偏移 <see cref="_dragOffset"/> 只在内存里（重启游戏复位），不写配置文件。
    /// </summary>
    private void HandleMouse()
    {
        try
        {
            var scr = new Vector2(Screen.width, Screen.height);
            var mouse = new Vector2(Input.mousePosition.x, scr.y - Input.mousePosition.y);
            bool overPanel = _lastRect.width > 0 && _lastRect.Contains(mouse);

            // ── ⓪ 拖右边/右下角 = 改面板大小（字体不变）★ v0.1.5 用户要求 ──
            const float GripPx = 8f;
            bool onRightEdge = _lastRect.width > 0f
                && mouse.x >= _lastRect.xMax - GripPx && mouse.x <= _lastRect.xMax + GripPx
                && mouse.y >= _lastRect.y && mouse.y <= _lastRect.yMax;
            bool onBottomEdge = _lastRect.width > 0f
                && mouse.y >= _lastRect.yMax - GripPx && mouse.y <= _lastRect.yMax + GripPx
                && mouse.x >= _lastRect.x && mouse.x <= _lastRect.xMax;

            if (Input.GetMouseButtonDown(0) && (onRightEdge || onBottomEdge))
            {
                _resizeEdge = (onRightEdge && onBottomEdge) ? 3 : (onRightEdge ? 1 : 2);
                _resizeGrabW = _lastRect.width;
                _resizeGrabH = _lastRect.height;
                _resizeStart = mouse;
                _shownAt = UnityEngine.Time.realtimeSinceStartup;
            }
            if (_resizeEdge != 0)
            {
                if (Input.GetMouseButton(0))
                {
                    float maxW = Mathf.Max(320f, Screen.width - 40f);
                    if (_resizeEdge == 1 || _resizeEdge == 3)
                        _widthOverride = Mathf.Clamp(_resizeGrabW + (mouse.x - _resizeStart.x), 300f, maxW);
                    if (_resizeEdge == 2 || _resizeEdge == 3)
                        _heightOverride = Mathf.Clamp(_resizeGrabH + (mouse.y - _resizeStart.y), 120f, Screen.height * 0.98f);
                    _shownAt = UnityEngine.Time.realtimeSinceStartup;
                }
                if (Input.GetMouseButtonUp(0))
                {
                    _resizeEdge = 0;
                    // ★ v1.6.1【回归审计 #6】以前只调 SaveSize，而 SaveSize 写回的是
                    //   EventsOverlayConfig.PanelWidth/Height（拖拽从未写进这两个字段）⇒ 拖出来的尺寸不落盘，
                    //   且在本次游戏内还会一直压制 F12 里改的宽高。现在先把拖拽值同步进去再保存。
                    if (_widthOverride > 1f) EventsOverlayConfig.PanelWidth = _widthOverride;
                    if (_heightOverride > 1f) EventsOverlayConfig.PanelHeight = _heightOverride;
                    try { EventsOverlayConfig.SaveSize?.Invoke(); } catch { }
                    Plugin.Log?.LogInfo($"[EventRotator.Client] 面板大小已调整为 {_widthOverride:0}×{_heightOverride:0}（字号不变；已写入 F12 配置）");
                }
                return;   // ★ 调整尺寸期间不进入"拖动移动"
            }

            // ── ① 左键拖动移动 ──
            if (Input.GetMouseButtonDown(0) && overPanel && !_dragging)
            {
                _dragging = true;
                _dragGrab = mouse - new Vector2(_lastRect.x, _lastRect.y);
            }
            if (_dragging)
            {
                if (Input.GetMouseButton(0))
                {
                    _dragOffset = mouse - _dragGrab - new Vector2(
                        EventsOverlayConfig.AlignRight ? Screen.width - _lastRect.width - 12 : 12, 12);
                    _shownAt = UnityEngine.Time.realtimeSinceStartup;   // 拖动期间不自动收起
                }
                if (Input.GetMouseButtonUp(0))
                {
                    _dragging = false;
                    Plugin.Log?.LogInfo($"[EventRotator.Client] 面板已拖动（偏移 {_dragOffset.x:0},{_dragOffset.y:0}）"
                                        + "; " + Locale.T("双击标题栏可复位", "double-click the title bar to reset"));
                }
            }

            // ── ② 双击 = 位置+缩放复位 ──
            if (overPanel && Input.GetMouseButtonDown(0))
            {
                if (UnityEngine.Time.realtimeSinceStartup - _lastClickAt < 0.35f)
                {
                    _dragOffset = Vector2.zero; _zoom = 1f;
                    _widthOverride = 0f; _heightOverride = 0f;     // ★ 尺寸也一并复位（拖出来的大小）
                    EventsOverlayConfig.PanelWidth = 0f;           // ★ v1.6.1 静态配置同步清零（否则 SaveSize 写回旧值）
                    EventsOverlayConfig.PanelHeight = 0f;
                    try { EventsOverlayConfig.SaveSize?.Invoke(); } catch { }
                    _lastClickAt = 0f;
                    Plugin.Log?.LogInfo("[EventRotator.Client] 面板位置、缩放与大小已复位");
                }
                else _lastClickAt = UnityEngine.Time.realtimeSinceStartup;
            }

            // ── ③ 滚轮 ＝ 滚动活动内容（★ v0.1.5 用户实测反馈：滚轮必须滚内容，不然活动说明看不全）──
            //    走 Input.GetAxis 这条**实机已验证可用**的路径（原来就是它在驱动缩放），
            //    不依赖 IMGUI ScrollView 对滚轮事件的消费方式；OnGUI 里会把 IMGUI 的滚轮吃掉防双倍。
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (Mathf.Abs(wheel) > 0.0001f && (overPanel || Input.GetKey(KeyCode.LeftAlt)))
            {
                if (ctrl)
                {
                    // Ctrl+滚轮 = 缩放（能力保留，但不再是默认行为）
                    float step = Input.GetKey(KeyCode.LeftShift) ? 0.03f : 0.10f;
                    _zoom = Mathf.Clamp(_zoom + Mathf.Sign(wheel) * step, 0.85f, 1.6f);
                }
                else
                {
                    _scroll.y = Mathf.Max(0f, _scroll.y - Mathf.Sign(wheel) * WheelStep);
                }
                _shownAt = UnityEngine.Time.realtimeSinceStartup;   // 滚动/缩放期间不自动收起
            }
            if (overPanel && Input.GetMouseButtonDown(2)) _scroll = Vector2.zero;   // 中键 = 滚回顶部
        }
        catch { }
    }

    private float _lastClickAt;
    private int _resizeEdge;          // 0=无 1=右边 2=下边 3=右下角
    private Vector2 _resizeStart;
    private float _resizeGrabW, _resizeGrabH;    
    private bool _dragging;
    private Vector2 _dragGrab;
    private Vector2 _dragOffset;

    private float _zoom = 1f;

    /// <summary>★ v0.1.5 新增（用户要求）：**拖边框改面板大小，字体不变**。
    /// 宽度/高度是独立于字号(<see cref="_zoom"/>)的量：改它们只让面板变宽变高（每行能放更多字、列表能显示更多行），
    /// 字号完全不动。0 = 自动（宽度按字号算、高度按内容算）。值会写回 F12 配置。</summary>
    private float _widthOverride;
    private float _heightOverride;
    /// <summary>★ v0.1.5 活动列表的滚动位置（滚轮滚内容）。</summary>
private Vector2 _scroll;
        /// <summary>★ v0.1.5 修正：上一帧**实测**的抬头高度（GetLastRect 量出来的）。
        /// 面板高度原先只用"估算值 hHeader" ⇒ 实际抬头更高时（横幅换行、季节块多行），
        /// 页脚会被挤出面板底部**被截断**。用实测值参与下一帧的高度计算即可根治（一帧延迟，肉眼不可见）。</summary>
        private float _measuredHeaderH;
        /// <summary>★ v0.1.5 修正：上一帧**实测**的页脚高度。页脚文本含换行 + 富文本，`
        /// CalcHeight 的估算会偏小 ⇒ 末行被面板底部裁掉（用户截图）。用实测值参与下一帧高度计算。</summary>
        private float _measuredFooterH;
    private float _lastLayoutLog;
    /// <summary>★ v0.1.5 每格滚轮滚动像素。</summary>
    private const float WheelStep = 46f;
    /// <summary>★ v0.1.5 本帧活动列表视口（屏幕坐标，供命中判定/诊断）。</summary>
    private Rect _listRect;
    /// <summary>★ v0.1.5 本帧列表内容高 / 视口高（诊断行用）。</summary>
    private float _listContentH, _listViewH;

    private Rect _lastRect;
    /// <summary>当前缩放倍率（F12 只读显示用）。</summary>
    public float Zoom => _zoom;

    /// <summary>
    /// ★ v0.1.3 用户要求的显示顺序：大活动（带任务）→ 小活动 → 奖励活动。
    /// 分类标签来自服务端写的「① 季节 / ② 限时带任务 / ③ 小活动 / ④ 奖励类」。
    /// </summary>
    private static int CatOrder(string cat)
    {
        if (string.IsNullOrEmpty(cat)) return 90;
        // ★ v0.1.5 分类重做：**中英文都认**
        //   0=季节  1=限时带任务  2=★新增「AI 活动」  3=小活动  4=奖励
        if (cat.Equals("seasonal", StringComparison.OrdinalIgnoreCase)) return 0;
        if (cat.Equals("event_task", StringComparison.OrdinalIgnoreCase)) return 1;
        if (cat.Equals("event_ai", StringComparison.OrdinalIgnoreCase)) return 2;
        if (cat.Equals("event_small", StringComparison.OrdinalIgnoreCase)) return 3;
        if (cat.Equals("reward", StringComparison.OrdinalIgnoreCase)) return 4;
        if (cat.Contains("①") || cat.Contains("季节")) return 0;
        if (cat.Contains("②") || cat.Contains("限时")) return 1;
        if (cat.Contains("AI") || cat.Contains("首领") || cat.Contains("邪教")) return 2;
        if (cat.Contains("③") || cat.Contains("小活动")) return 3;
        if (cat.Contains("④") || cat.Contains("奖励")) return 4;
        return 9;
    }

    /// <summary>把分类归成段名（★ v0.1.5 重做：新增「AI 活动」一段，便于优先测试）。</summary>
    private static string GroupTitle(string cat)
    {
        int o = CatOrder(cat);
        if (Locale.En)
        {
            return o switch
            {
                0 => "MAJOR - Seasonal",
                1 => "MAJOR - Timed (quest)",
                2 => "AI EVENTS - bosses / cultists / rogues",
                3 => "MINOR EVENTS",
                4 => "REWARDS",
                _ => "OTHER",
            };
        }
        return o switch
        {
            0 => "大活动 · 季节",
            1 => "大活动 · 限时（带任务）",
            2 => "AI 活动 · 首领 / 邪教 / 劫掠者",
            3 => "小活动",
            4 => "奖励活动",
            _ => "其它",
        };
    }

    /// <summary>★ v1.2.0 分类标签英文名（服务端标签带 ①②③④★ 记号，按记号映射）。</summary>
    private static string CatEn(string cat)
    {
        if (string.IsNullOrEmpty(cat)) return cat;
        if (cat.Contains("①")) return "Seasonal";
        if (cat.Contains("②")) return "Timed";
        if (cat.Contains("★")) return "AI";
        if (cat.Contains("④")) return "Reward";
        return "Minor";
    }

    private void OnGUI()
    {
        GuiCalls++;
        LastDrawTime = UnityEngine.Time.realtimeSinceStartup;

        // ★ 诊断：每秒报一次 OnGUI 是否真的在跑（用于定位"面板不显示"）
        if (LogEnabled && GuiCalls == 1)
            Plugin.Log?.LogInfo("[EventRotator.Client] 面板 OnGUI 已开始被调用（IMGUI 可用）");
        if (LogEnabled && GuiCalls % 60 == 0)
            Plugin.Log?.LogInfo($"[EventRotator.Client] OnGUI #{GuiCalls}  visible={_visible}  活动数={_status.Rows.Count}");

        // ★ 诊断角标：确认 IMGUI 通路可用（面板不显示时一眼看出是不是 IMGUI 没被调用）
        if (ProbeAlwaysDraw)
        {
            if (_probeStyle == null) _probeStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };  // ★ G1：只建一次
            var st = _probeStyle;
            st.normal.textColor = Color.green;
            GUI.Label(new Rect(10, 10, 720, 24),
                      // ★ v1.6.5【复核 #4】自检角标整串中文 ⇒ 英文界面里屏上直出中文。中文逐字不变。
                      Locale.T($"EventRotator 面板自检：OnGUI 已调用 {GuiCalls} 次，visible={_visible}，活动数={_status.Rows.Count}",
                               $"EventRotator overlay self-test: OnGUI called {GuiCalls} times, visible={_visible}, events={_status.Rows.Count}"), st);
        }

        if (!_visible) return;

        // ★ v0.1.5：滚轮由 HandleMouse（Input.GetAxis，实机已验证可用）驱动，
        //   这里把落在面板上的 IMGUI 滚轮事件吃掉，避免 ScrollView 再滚一次（双倍速度）。
        var ev = Event.current;
        if (ev != null && ev.type == EventType.ScrollWheel && _lastRect.width > 0f
            && (_lastRect.Contains(ev.mousePosition) || Input.GetKey(KeyCode.LeftAlt)))
        {
            ev.delta = Vector2.zero;
            ev.Use();
        }

        int baseFs = EventsOverlayConfig.FontSize;
        if (baseFs <= 0) return;

        int fs = Mathf.Clamp(Mathf.RoundToInt(baseFs * _zoom), 8, 40);
        // ★ v0.1.5：拖过边框就用拖出来的宽度；否则按字号自动。**字号与宽度彻底解耦**。
        // ★★★ C4【F12 改「面板宽度 / 高度」当场不生效】
        //   这两项以前在 ApplyOverlayConfig 里只被**写进** `EventsOverlayConfig`，面板却从来没读过它
        //   （`_widthOverride`/`_heightOverride` 只在鼠标拖拽时赋值）⇒ F12 改多少都没反应，
        //   而且拖出来的尺寸重启就丢。现在：本帧拖拽值 > F12 配置值 > 按字号/内容自动。
        float widthCfg = EventsOverlayConfig.PanelWidth;
        float heightCfg = EventsOverlayConfig.PanelHeight;
        float useW = _widthOverride > 1f ? _widthOverride : widthCfg;
        float useH = _heightOverride > 1f ? _heightOverride : heightCfg;
        int winW = useW > 1f
            ? Mathf.Clamp(Mathf.RoundToInt(useW), 300, Mathf.Max(320, Screen.width - 40))
            : Mathf.Clamp(Mathf.RoundToInt(BaseWidth * _zoom), 300, Mathf.Max(320, Screen.width - 40));
        EnsureStyles(fs);                      // ★★★ v0.1.7【G1】9 个 GUIStyle 只在 (skin, 字号) 变化时重建（以前每帧 new 9 个）

        int innerW = winW - PadX * 2;          // （原样保留：这两行原本夹在 footStyle 与 hintStyle 之间）

        var hint = RestartHint();
        var titleStyle = _titleStyle;          // 以下 8 行只是**取字段**：绘制时读到的样式值与优化前逐字相同
        var nameStyle = _nameStyle;
        var catStyle = _catStyle;
        var descStyle = _descStyle;
        var footStyle = _footStyle;
        var hintStyle = _hintStyle;
        var seasonStyle = _seasonStyle;
        var groupStyle = _groupStyle;

        // ★ v0.1.3 按用户要求分组：大活动（季节 + 限时带任务）→ 小活动 → 奖励活动
        // ★★★ v0.1.7【G2/G3】改为读缓存（排序键/比较器/字符串拼法与优化前**逐字相同**，见 CachedView()）
        var sorted = CachedView();

        // ★ v0.1.7【UI 性能】SeasonText 现在是**缓存字段**（每帧不再 new StringBuilder），文本逐字相同
        string SeasonText() { EnsureTextCache(); return _cacheSeasonText; }

        // ── ★ v0.1.5 几何：抬头 / 活动列表（可滚动） / 页脚 三段分开量 ──
        //   旧版三个口径不一致：背景框高被屏幕截断、绘制区却用**未截断**的 h，
        //   且行高常量 22/20/20 不随字号缩放 ⇒ 一缩放内容就成片溢出背景框、
        //   超出屏幕末尾的活动永远看不到（用户实测："活动内容看不全"）。现在统一成 panelH。
        float lineTitle = (fs + 4) + 8f;                      // 标题行
        float lineGroup = (fs - 1) + 8f;                      // 分组标题行
        float lineName = (fs + 1) + 6f;                       // 活动名行
        _gcHint.text = hint ?? "";                                 // ★ v0.1.7 复用 GUIContent（不 new）
        float hHint = string.IsNullOrEmpty(hint) ? 0f : hintStyle.CalcHeight(_gcHint, innerW) + 12f;
        _gcSeason.text = SeasonText();                              // ★ 复用
        float hSeason = lineGroup + seasonStyle.CalcHeight(_gcSeason, innerW) + 8f;
        float hHeader = hHint + lineTitle + hSeason;
        _gcFooter.text = FooterText();                              // ★ 复用
        float hFooter = 10f + footStyle.CalcHeight(_gcFooter, innerW) + 4f;

        // 列表内容高（按给定宽度量：出现滚动条后宽度会变窄，说明文字会变高）
        float ListContentH(float w)
        {
            if (sorted.Count == 0) return 26f;
            float acc = 0f; int lg = -99;
            foreach (var r in sorted)
            {
                int g = r.Group;                                  // ★ G2：缓存里已算好（= CatOrder(r.Cat)，同一个函数）
                if (g != lg) { acc += lineGroup + 6f; lg = g; }
                acc += lineName;
                if (!string.IsNullOrEmpty(r.DescLine))            // ★ G3：缓存里 = "   " + Desc（原判断 !IsNullOrEmpty(Desc)，等价）
                    _gcRow.text = r.DescLine;                           // ★ 复用（不 new）
                    acc += descStyle.CalcHeight(_gcRow, Mathf.Max(60f, w)) + 2f;
                acc += 4f;
            }
            return acc;
        }

        float screenMax = Screen.height * 0.92f;
        float hHeaderUse = Mathf.Max(hHeader, _measuredHeaderH);   // ★ 见 _measuredHeaderH 注释
        //    ★★ 余量说明（用户连续三次反馈底部被切）：预留里除了"页脚自身高度"，还必须覆盖
        //       下面那条分隔线(DrawHLine=1px+4px 间距) 与行距/基线误差 ⇒ 按字号再加一段自适应余量。
        float footSlack = Mathf.Max(12f, fs * 1.15f);
        float hFooterUse = Mathf.Max(hFooter, _measuredFooterH) + footSlack;
        float chrome = PadY * 2 + hHeaderUse + hFooterUse;
        float maxListH = Mathf.Max(80f, screenMax - chrome);

        float hList = ListContentH(innerW);
        bool needBar = hList > maxListH - 4f;
        if (needBar) hList = ListContentH(innerW - 16f);
        float listH = Mathf.Clamp(hList, 40f, maxListH);
        float panelH = useH > 1f
            ? Mathf.Clamp(useH, 120f, screenMax)
            : Mathf.Min(chrome + listH, screenMax);   // ★ C4：F12 的「面板高度」也会在这里生效（见 useH）
        if (panelH < 120f) panelH = 120f;

        int lastGroup = -99;
        int baseX = EventsOverlayConfig.AlignRight ? Screen.width - winW - 12 : 12;
        int x = Mathf.Clamp(baseX + (int)_dragOffset.x, -winW / 3, Screen.width - winW / 3);
        int y = Mathf.Clamp(12 + (int)_dragOffset.y, 0, Mathf.Max(0, (int)(Screen.height - panelH - 8)));
        var rect = new Rect(x, y, winW, panelH);
        _lastRect = rect;   // 供滚轮命中判定用

        // 背景 + 边框
        var old = GUI.color;
        GUI.color = BgColor;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;

        GUILayout.BeginArea(new Rect(rect.x + PadX, rect.y + PadY, innerW, panelH - PadY * 2));

        // ★★★ v0.1.3 顶部横幅：需要重启时**最显眼**（黄字），这是本次需求的核心
        if (!string.IsNullOrEmpty(hint))
        {
            DrawHLine(innerW);
            GUILayout.Label(hint, hintStyle);
            DrawHLine(innerW);
        }

        // 标题行 + 右侧更新时间
        GUILayout.BeginHorizontal();
        GUILayout.Label(Locale.T("当前生效的活动", "ACTIVE EVENTS"), titleStyle);
        GUILayout.FlexibleSpace();
        GUILayout.Label("<color=#9aa0a6>" + (_status.Updated.Length > 0 ? _status.Updated : "—") + "</color>", catStyle);
        GUILayout.EndHorizontal();

        // ★★★ v0.1.3 顶部：当前季节（用户要求放最上面）
        DrawHLine(innerW);
        GUILayout.Label(Locale.T("当前季节", "Current season"), groupStyle);   // ★ C5：硬编码中文 → Locale.T
        GUILayout.Label(SeasonText(), seasonStyle);
        GUILayout.Space(2);

        // ★★★ v0.1.5 活动列表进滚动视口（抬头/页脚固定）：
        //    抬头画完后用 GetLastRect 取实际 y（拿不到就退回估算值 hHeader），
        //    剩余高度给列表，页脚永远钉在面板底部 ⇒ 内容再多也滚得完、不会压到游戏 UI 上。
        float listTop = 0f;
        try { listTop = GUILayoutUtility.GetLastRect().yMax; } catch { }
if (listTop <= 1f || float.IsNaN(listTop)) listTop = hHeader;
        _measuredHeaderH = listTop;                                  // ★ 存给下一帧算面板高度
        float barW = needBar ? 16f : 0f;                             // ★ 滚动条占宽（内容要避开它）
        float areaH = panelH - PadY * 2;
        //    ★ 列表最小高度 48→24：空间紧张时先牺牲列表（列表可滚动），绝不挤页脚。
        float viewH = Mathf.Max(24f, areaH - listTop - hFooterUse);
        _listRect = new Rect(rect.x + PadX, rect.y + PadY + listTop, innerW, viewH);
        _listViewH = viewH;
        _listContentH = ListContentH(innerW - barW);

        _scroll = GUILayout.BeginScrollView(_scroll, false, needBar,
                                            GUIStyle.none, GUI.skin.verticalScrollbar ?? GUIStyle.none,
                                            GUILayout.Width(innerW), GUILayout.Height(viewH));
        // ★★★ v0.1.5 修正：IMGUI 的竖向滚动条画在视口**内部**，原先行内容按 innerW 排 ⇒
        //   右侧的分类标签会被滚动条盖住（用户实测截图）。这里把内容套进"innerW − 滚动条宽"的容器里。
        GUILayout.BeginVertical(GUILayout.Width(Mathf.Max(60f, innerW - barW)));
        if (sorted.Count == 0)
        {
            GUILayout.Space(4);
            // ★ C5：两行空列表提示也走 Locale.T（中文逐字不变）
            GUILayout.Label("<color=#c0c0c0>" + Locale.T("当前没有任何活动生效", "No events are active right now") + "</color>\n"
                            + "<color=#8a8f94>" + Locale.T("· 在 F12 勾选活动，或打开【轮换设置】的自动轮换",
                                                          "· Tick events in F12, or turn on auto-rotation in 【Rotation settings】") + "</color>", descStyle);
        }
        else
        {
            lastGroup = -99;
            foreach (var r in sorted)
            {
                int g = r.Group;                                  // ★ G2/G3：全部读缓存（值 = 优化前每帧现算的同一个）
                if (g != lastGroup)
                {
                    if (lastGroup != -99) GUILayout.Space(4);
                    DrawHLine(Mathf.RoundToInt(Mathf.Max(60f, innerW - barW)));
                    GUILayout.Label(r.GroupTitle, groupStyle);
                    GUILayout.Space(2);
                    lastGroup = g;
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label(r.Label, nameStyle);
                GUILayout.FlexibleSpace();
                GUILayout.Label(Locale.En ? CatEn(r.Cat) : r.Cat, catStyle);
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(r.DescLine))
                    GUILayout.Label(r.DescLine, descStyle);
                GUILayout.Space(3);
            }
        }
        GUILayout.EndVertical();     // ★ 配合上面的固定宽度容器
GUILayout.EndScrollView();

        DrawHLine(innerW);
        GUILayout.Label(FooterText(), footStyle);
        // ★ 右下角抓手提示（三条斜线）：让用户知道这里能拖
        try
        {
            float gx = rect.xMax - 14f, gy = rect.yMax - 14f;
            var oldc = GUI.color; GUI.color = new Color(0.75f, 0.78f, 0.82f, 0.85f);
            for (int i = 0; i < 3; i++)
                GUI.DrawTexture(new Rect(gx + i * 4f, gy + 8f - i * 4f, 7f, 1.5f), Texture2D.whiteTexture);
            GUI.color = oldc;
        }
        catch { }
        try
        {
            var fr = GUILayoutUtility.GetLastRect();
            if (fr.height > 1f) _measuredFooterH = fr.height + 14f;
            if (LogEnabled && UnityEngine.Time.realtimeSinceStartup - _lastLayoutLog > 2f)
            {
                _lastLayoutLog = UnityEngine.Time.realtimeSinceStartup;
                Plugin.Log?.LogInfo($"[EventRotator.Client][布局] 面板H={panelH:0} 区H={areaH:0} 抬头={listTop:0}(估算{hHeader:0}) "
                    + $"列表={viewH:0} 页脚预留={hFooterUse:0}(估算{hFooter:0} 实测{_measuredFooterH:0}) 实际页脚={fr.height:0}");
            }
        }
        catch { }
        GUILayout.EndArea();
    }

    /// <summary>
    /// ★ v0.1.7【UI 性能】保证"纯数据文本"缓存与当前 `_statusRevision` 同步（每帧只读，状态一变才重建）。
    /// 缓存项：SeasonText 结果 · FooterText 里的 nextIds Join · 每行的 GUIContent。
    /// **文本拼法与内容与优化前逐字相同**（只是从"每帧算"变成"状态变才算"）。
    /// <para>★★★ C1：本方法的缓存键是**自己的** `_textRev` + `_textLang`，不再和 RestartHint() 共用
    /// `_cacheRev`（后者每帧先跑并把版本号写回 ⇒ 这里永远认为"已是最新"，切语言后季节文本不刷新）。</para>
    /// </summary>
    private bool EnsureTextCache()
    {
        if (_textRev == _statusRevision && _textLang == Locale.En && _cacheSeasonText != null) return true;
        _textRev = _statusRevision;
        _textLang = Locale.En;
        try
        {
            // ★ v1.3.5【英文摘要】`SeasonFriendly` 是**服务端写的中文**（"天气季节：冬天（按天数轮换，每 30 天换一季）…"），
            //   英文模式下一律不用它，改由客户端按字段拼英文；中文模式保持原样（逐字不变）。
            var s = _status.SeasonFriendly;
            if (!Locale.En && !string.IsNullOrEmpty(s))
            {
                // ★ v1.6.5【复核 #2①】中文模式仍然采用服务端原句（措辞逐字保留），
                //   但旧服务端那句可能写"活动季节：由勾选的活动决定"，而 `#季节|` 里其实已经带
                //   `p[5]`=AppliedEventSeason（本季确实已开启）⇒ 头部与页脚自相矛盾。
                //   ⇒ 这里**只在原句没提过这个季节时追加一句**，绝不改写原有措辞、也不重复。
                var head = s;
                var applied = _status.AppliedEventSeason;
                if (!string.IsNullOrEmpty(applied))
                {
                    var nm = SeasonName(applied);
                    bool alreadyMentioned = head.IndexOf(nm, StringComparison.Ordinal) >= 0
                                            && head.IndexOf("已开启", StringComparison.Ordinal) >= 0;
                    if (!alreadyMentioned)
                        head = head.TrimEnd()
                             + Locale.T("　活动季节：", "   Event season: ") + nm
                             + Locale.T("（已开启）", " (applied)");
                }
                _cacheSeasonText = head;
            }
            else
            {
                var t = new StringBuilder();
                // ★★★ C6【天气季节显示的是模式枚举，不是季节】
                //   服务端 `#季节|` 行的列序是：`p[0]`=模式(followEvent/rotate/fixed/off)、
                //   `p[1]`=**仅 fixed 模式**有值的"指定天气季节"、`p[2]`=指定活动季节、
                //   `p[3]`=**实际轮换到的季节**（rotate 时才有值）。
                //   ⇒ 季节显示必须优先取 `p[3]`（= RotateNow，历史字段名，语义是"轮换到的季节"），
                //     为空才回退 `p[1]`；`p[0]`（SeasonMode）**只用于诊断/判断是否在轮换，绝不当季节显示**。
                var seasonName = !string.IsNullOrEmpty(_status.RotateNow)
                    ? _status.RotateNow
                    : (!string.IsNullOrEmpty(_status.WeatherSeason) ? _status.WeatherSeason : "—");
                t.Append(Locale.T("天气季节：", "Weather season: ")).Append(SeasonName(seasonName));
                if (Locale.En)
                {
                    // ★ C6：只在**季节**轮换开启时拼，且 N 必须是**季节**周期（旧代码用的是活动轮换的 IntervalDays）
                    if (SeasonRotationOn())
                    {
                        var days = SeasonIntervalDays();
                        t.Append(days > 0 ? $" (rotate, every {days} days)" : " (rotate)");
                    }
                }
                else if (!string.IsNullOrEmpty(_status.SeasonMode))
                    t.Append("（").Append(_status.SeasonMode).Append("）");
                if (!string.IsNullOrEmpty(_status.EventSeason)) t.Append(Locale.T("　活动季节：", "   Event season: ")).Append(SeasonName(_status.EventSeason));
                if (!string.IsNullOrEmpty(_status.AppliedEventSeason))
                    t.Append(Locale.T("　活动季节：", "   Event season: ")).Append(SeasonName(_status.AppliedEventSeason))
                     .Append(Locale.T("（已开启）", " (applied)"));
                _cacheSeasonText = t.ToString();
            }
            var rot = _status.Rot;
            _cacheFooterNextIds = (rot != null && rot.NextIds.Count > 0) ? string.Join(" + ", rot.NextIds) : "";
        }
        catch { _cacheSeasonText = ""; _cacheFooterNextIds = ""; }
        return true;
    }

    /// <summary>★ C6 季节是否在按天数轮换（用 `p[3]` 有值或 `p[0]`=="rotate" 判断，**不拿模式当季节显示**）。</summary>
    private bool SeasonRotationOn()
    {
        try
        {
            if (_status == null) return false;
            if (!string.IsNullOrEmpty((_status.RotateNow ?? "").Trim())) return true;
            return string.Equals((_status.SeasonMode ?? "").Trim(), "rotate", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// ★ C6 季节周期（天）：从服务端写的人读行 `模式: rotate（每 30 天轮换 → 当前 WINTER）` 里取。
    /// <para>为什么从这里取：`#季节|` 机器行**没有**周期列，而活动轮换的 `#轮换|IntervalDays` 是**活动**周期，
    /// 拿它拼 "every N days" 就是本次修的 bug。取不到返回 0（调用方退化成只写 "(rotate)"）。</para>
    /// </summary>
    private int SeasonIntervalDays()
    {
        try
        {
            var raw = _statusRawText;
            if (string.IsNullOrEmpty(raw)) return 0;
            var start = raw.IndexOf("── 季节 ──", StringComparison.Ordinal);
            if (start >= 0)
            {
                var end = raw.IndexOf("── 轮换 ──", start, StringComparison.Ordinal);
                if (end > start) raw = raw.Substring(start, end - start);
            }
            var m = Regex.Match(raw, @"每\s*(\d+)\s*天轮换");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var d) && d > 0) return d;
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// ★ C6 季节名：中文模式原样返回（服务端给什么就是什么，逐字不变）；
    /// 英文模式把 `WINTER` / `AUTUMN_LATE` 这类枚举映射成 `Winter` / `Autumn Late`（首字母大写，不通顺的全大写不露给玩家）。
    /// </summary>
    /// <summary>
    /// ★ v1.6.4【英文残留】`来源` 是服务端写的中文值（"自动（日期窗 / 活动轮换 / 奖励轮换）"），
    /// 英文模式只翻标签不够。按已知取值映射，未知值原样返回。
    /// </summary>
    private static string MapSourceText(string raw)
    {
        var s = (raw ?? "").Trim();
        if (!Locale.En || s.Length == 0) return s;
        if (s.StartsWith("自动", StringComparison.Ordinal)) return "Automatic";
        if (s.StartsWith("手动强制", StringComparison.Ordinal)) return "Forced manually (force.txt)";
        return s;
    }

    private static string SeasonName(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return s;
        // ★ v1.6.1【回归审计 #8】中文模式以前直接把枚举名（WINTER）丢给玩家，
        //   同一个面板头部却写「冬天」⇒ 两处不一致。这里补一张中文映射。
        if (!Locale.En)
        {
            switch (s.ToUpperInvariant())
            {
                // 天气季节
                case "SPRING": return "春天";
                case "SPRING_EARLY": return "早春";
                case "SUMMER": return "夏天";
                case "AUTUMN": return "秋天";
                case "AUTUMN_LATE": return "晚秋";
                case "WINTER": return "冬天";
                case "STORM": return "暴风雨";
                // ★ v1.6.5【复核 #2②】活动季节也是同一张表在显示（页脚 `活动季节` / `活动季节(已开启)`），
                //   以前漏了这 4 个枚举 ⇒ 中文界面直接打出 CHRISTMAS / HALLOWEEN 这种大写英文。
                //   取值与服端 EventSeasonZh 逐字对齐（万圣节 / 圣诞节 / 新年 / 愚人节 / 促销）。
                case "HALLOWEEN": return "万圣节";
                case "CHRISTMAS": return "圣诞节";
                case "NEWYEARS": return "新年";
                case "NEW_YEARS": return "新年";
                case "APRILFOOLS": return "愚人节";
                case "APRIL_FOOLS": return "愚人节";
                case "PROMO": return "促销";
                default: return s;
            }
        }
        try
        {
            var parts = s.ToLowerInvariant().Split(new[] { '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                var p = parts[i];
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p, 1, p.Length - 1);
            }
            return sb.ToString();
        }
        catch { return s; }
    }

    private string FooterText()
    {
        EnsureTextCache();
        // ★ v0.1.7【UI 性能】复用同一个 StringBuilder（不再每帧 new）；`string.Join` 改成读缓存。
        var sb = _sbFooter ?? (_sbFooter = new StringBuilder(512));   // ★ 防御：极端情况下为 null 也不崩（文本不变）
        sb.Clear();
        sb.Append(Locale.T("来源: ", "Source: ")).Append(MapSourceText(_status.Source.Length > 0 ? _status.Source : "—"));
        // ★★★ C7【数据来源提示】`_remoteOk` 以前只写不读：客机看到"主机数据 / 本机旧文件 / 已过期"分不清。
        //   现在页脚固定给一处「数据来源：主机 / 本机 / 已过期」。
        sb.Append(Locale.T("    数据来源: ", "    Data from: ")).Append(DataSourceText());
        // ★★★ C6：季节优先 `p[3]`（轮换到的季节），为空才回退 `p[1]`；**不再用 `p[0]` 模式串当季节**。
        var footerSeason = !string.IsNullOrEmpty(_status.RotateNow)
            ? _status.RotateNow
            : (_status.WeatherSeason.Length > 0 ? _status.WeatherSeason : "—");
        sb.Append(Locale.T("     天气季节: ", "     Weather season: ")).Append(SeasonName(footerSeason));
        if (Locale.En)
        {
            // ★ C6：只用**季节**周期拼 "(rotate, every N days)"
            if (SeasonRotationOn())
            {
                var days = SeasonIntervalDays();
                sb.Append(days > 0 ? $" (rotate, every {days} days)" : " (rotate)");
            }
        }
        else if (_status.WeatherSeason.Length > 0 && _status.SeasonMode.Length > 0)
            sb.Append(" → ").Append(_status.SeasonMode);   // 中文文案保持原样（仅 fixed 模式出手动指定季节时出现）
        if (_status.EventSeason.Length > 0) sb.Append(Locale.T("    活动季节: ", "    Event season: ")).Append(SeasonName(_status.EventSeason));
        if (_status.AppliedEventSeason.Length > 0)
            sb.Append(Locale.T("    活动季节(已开启): ", "    Event season (applied): ")).Append(SeasonName(_status.AppliedEventSeason));
        // ★★★ C6：原来这里写 `档位: {RotateNow}` —— RotateNow 其实是**季节**（`#季节|` 第 4 列），
        //   不是档位（档位在 `#轮换|` 第 1 列，下面那段已经打印），所以这行整个删掉。

        // ★ v0.1.3 轮换信息：当前档位 + 下次换档倒计时（预轮换是服务端启动时就算好的）
        var rot = _status.Rot;
        if (rot.Has && rot.Enabled)
        {
            sb.Append(Locale.T("\n轮换: 档位 #", "\nRotation: slot #")).Append(rot.AppliedSlot)
              .Append(Locale.T("（每 ", " (every ")).Append(rot.IntervalDays).Append(Locale.T(" 天，基准 ", " days, base hour ")).Append(rot.RebaseHour).Append(":00)");
            if (rot.NextStart != DateTime.MinValue)
            {
                var left = rot.NextStart - DateTime.Now;
                sb.Append(Locale.T("    下次换档: ", "    Next slot: ")).Append(rot.NextStart.ToString("MM-dd HH:mm"));
                if (left.TotalMinutes > 0) sb.Append(Locale.En ? $" (in {left.TotalHours:0.#} h)" : $"（还有 {left.TotalHours:0.#} 小时）");
                else sb.Append(Locale.T("（已到时间 → 重启服务端即生效）", " (due now — restart the server)"));
            }
            if (rot.NextIds.Count > 0) sb.Append(Locale.T("\n下一档: ", "\nNext slot: ")).Append(_cacheFooterNextIds);
        }
        return "<color=#8a8f94>" + sb + "</color>";
    }

    private static void DrawHLine(int width)
    {
        var r = GUILayoutUtility.GetRect(width, 1);
        var old = GUI.color; GUI.color = LineColor;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
        GUILayout.Space(4);
    }
}

/// <summary>面板的可配置项（由 Plugin 在 Awake 时用 Config.Bind 填好）。</summary>
internal static class EventsOverlayConfig
{
    public static int FontSize = 14;
    public static bool AlignRight = true;
    public static float AutoHideSeconds = 0f;
    /// <summary>★ 面板宽度像素（拖右下角改；0 = 自动按字号）。</summary>
    public static float PanelWidth = 0f;
    /// <summary>★ 面板高度像素（拖右下角改；0 = 自动按内容）。</summary>
    public static float PanelHeight = 0f;
    /// <summary>拖完边框后把尺寸写回配置（由 Plugin 赋值）。</summary>
    public static System.Action SaveSize;
    /// <summary>★ v1.3.0 联机时拉取服务端状态用的地址（默认 http://127.0.0.1:6969）。</summary>
    public static string RemoteStatusUrl = "";    
    /// <summary>默认 F8（开箱即用；F12 是 BepInEx 配置面板，不能占用）。</summary>
    public static KeyboardShortcut ToggleKey = new(KeyCode.F8);
}
