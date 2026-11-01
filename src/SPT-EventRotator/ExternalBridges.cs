using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EventRotator;

/// <summary>
/// ★ 2026-10-29【外部 mod 联动 · 实验室停电（Blackout 2026-07）】
///
/// <para><b>为什么用"联动"而不是自己实现</b>：停电/断电是**客户端机制**（灯光、夜视、电梯），
/// 服务端没有落点；但本机装了两个现成的第三方 mod 负责这件事——
/// <list type="bullet">
/// <item><b>Blackout</b>（<c>user/mods/Blackout/config.json</c> → <c>{"blackoutChance": 25}</c>）：
///   负责"每次进图有多大概率停电"。</item>
/// <item><b>BlackDivServer</b>（<c>user/mods/BlackDivServer/config.jsonc</c> → <c>spawns.labsStartChance</c>，当前 15）：
///   负责"实验室开局刷 Wedge（黑狐小队）"的概率。</item>
/// </list>
/// 事件生效时把两个值临时拉到 <see cref="ForcedChance"/>，事件结束后**从备份还原用户原值**。</para>
///
/// <para><b>读取时机（决定了能不能热生效，实测/反编译结论）</b>：</para>
/// <list type="bullet">
/// <item>Blackout：<c>BlackoutSpawnController.Roll()</c> 每次都 <c>LoadChance()</c> 重读盘；Roll 在
///   ① 服务端 OnLoad ② 每局结束 <c>/client/match/local/end</c> ⇒ **改文件下一局生效，不用重启**。</item>
/// <item>BlackDivServer：<c>ConfigController</c> 用 <c>FileSystemWatcher</c> 监听文件 ⇒ **改文件立刻生效**
///   ⇒ 我们**必须原子写**（同目录临时文件 + rename），否则 watcher 可能读到半截 JSONC。</item>
/// </list>
///
/// <para><b>安全规则</b>：</para>
/// <list type="number">
/// <item>只改目标数字，**其它字节一律不动**（用正则只替换数字本身；JSONC 里的 <c>//</c> 注释原样保留）。</item>
/// <item>第一次改之前先备份成 <c>&lt;file&gt;.bak-eventrotator</c>；**已存在就不覆盖** ⇒ 永远能还原回"用户原始值"。</item>
/// <item>mod 没装（文件不存在）⇒ **只打一条日志、不创建任何文件/目录**。</item>
/// <item>**状态不变就不写**（缓存上次状态 + 内容比对）：避免每次进图都触发黑狐的 FileSystemWatcher 热重载。</item>
/// <item><see cref="ReconcileOnStartup"/>：防"上次被强杀停在 100"——启动时若事件没生效、但备份存在
///   且当前值仍是 100，就从备份还原。</item>
/// </list>
///
/// <para><b>只读/写边界</b>：只碰上面两个 mod 的**配置文件**，绝不改它们的 dll/其它文件，
/// 也绝不写 events.json / 本 mod 自己的 config.json。</para>
/// </summary>
internal static class ExternalBridges
{
    /// <summary>触发联动的活动 id（events.json 里 `externalBridge = "blackout"` 的那条）。</summary>
    public const string BlackoutEventId = "blackout_2026";

    private const string LogTag = "[EventRotator][外部联动]";
    private const string BackupSuffix = ".bak-eventrotator";
    private const int ForcedChance = 100;

    /// <summary>
    /// ★ 测试专用：把"mods 根目录"指到别处（生产环境保持 null ⇒ 由 <c>EventEngine.ModPath</c> 推出来）。
    /// </summary>
    public static string RootOverride;

    /// <summary>上次写入的状态（null = 本次进程还没判过）。</summary>
    private static bool? _applied;
    /// <summary>测试/诊断用。</summary>
    public static bool? LastApplied => _applied;

    private static readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一个联动目标：哪个 mod 目录、哪个文件、改哪个键。</summary>
    private sealed class Target
    {
        public string ModName;      // 日志用
        public string DirName;      // mods 下的目录名
        public string FileName;     // 配置文件名
        public string Key;          // 要改的键
        public bool AllowDecimal;   // 原值可能是小数？
        public override string ToString() => $"{ModName}({DirName}/{FileName}#{Key})";
    }

    private static readonly Target[] Targets =
    {
        new Target { ModName = "Blackout",       DirName = "Blackout",       FileName = "config.json",  Key = "blackoutChance",  AllowDecimal = true  },
        new Target { ModName = "BlackDivServer", DirName = "BlackDivServer", FileName = "config.jsonc", Key = "labsStartChance", AllowDecimal = false },
    };

    private static void Info(string m) { try { EventEngine.Log?.Info(m); } catch { } }
    private static void Warn(string m) { try { EventEngine.Log?.Warning(m); } catch { } }

    /// <summary>mods 根目录 = `EventEngine.ModPath` 的上一级（= `user/mods`）。测试可被 <see cref="RootOverride"/> 覆盖。</summary>
    public static string ModsRoot()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(RootOverride)) return RootOverride;
            var mod = EventEngine.ModPath;
            if (string.IsNullOrWhiteSpace(mod)) return null;
            return Path.GetDirectoryName(mod.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch { return null; }
    }

    /// <summary>外部联动目标配置的绝对路径（不存在也能返回路径，方便日志说清"缺哪个文件"）。</summary>
    public static string PathOfTarget(string dir, string file) => PathOf(dir, file);
    private static string PathOf(string dir, string file)
    {
        var root = ModsRoot();
        return root == null ? null : Path.Combine(root, dir, file);
    }

    /// <summary>事件当前是否在生效集合里（只读判断，不改任何状态）。</summary>
    public static bool EventActive()
    {
        try { return EventEngine.Actives != null && EventEngine.Actives.Any(e => string.Equals(e?.Id, BlackoutEventId, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>
    /// 开/关外部联动。`on=true` ⇒ 两个值写成 <see cref="ForcedChance"/>；`on=false` ⇒ 从备份还原。
    /// **状态没变就直接返回**（不写盘 ⇒ 不触发黑狐的热重载）。
    /// </summary>
    public static void ApplyBlackout(bool on)
    {
        try
        {
            if (_applied == on) return;                 // ★ 状态没变：一行都不写
            var root = ModsRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                if (_missingLogged.Add("(noroot)"))
                    Warn($"{LogTag} 拿不到 mods 目录（EventEngine.ModPath 为空）⇒ 跳过外部联动");
                return;
            }

            int changed = 0;
            foreach (var t in Targets)
            {
                var path = PathOf(t.DirName, t.FileName);
                if (path == null) continue;
                if (!File.Exists(path))
                {
                    if (_missingLogged.Add(path))
                        Info($"{LogTag} {t.ModName} 未安装（{path} 不存在）⇒ 跳过（本事件不会有外部效果）");
                    continue;
                }
                if (on) { if (TurnOn(t, path)) changed++; }
                else { if (Restore(t, path, "还原")) changed++; }
            }
            _applied = on;
            if (on && changed == 0) Info($"{LogTag} 停电联动：目标值已经是 {ForcedChance}，无需改文件");
            else if (!on && changed == 0) Info($"{LogTag} 停电联动：已是用户原值，无需改文件");
        }
        catch (Exception ex) { Warn($"{LogTag} ApplyBlackout({on}) 失败: {ex.Message}"); }
    }

    /// <summary>
    /// 启动自检：事件**没生效**但备份还在、当前值仍是 100 ⇒ 说明上次被强杀停在 100，从备份还原。
    /// （事件在生效时什么都不做，交给 <see cref="ApplyBlackout"/> 保持 100。）
    /// </summary>
    public static void ReconcileOnStartup()
    {
        try
        {
            // ★ 事件在生效 ⇒ 这里**什么都不写**（交给紧随其后的 ApplyBlackout(true) 去同步），
            //   但必须把缓存清成"未知"，否则 ApplyBlackout(true) 会被 `_applied==true` 的早退挡掉，
            //   而文件其实还停在用户原值（25/15）——那就等于事件开了却没停电。
            if (EventActive()) { _applied = null; return; }
            int n = 0;
            foreach (var t in Targets)
            {
                var path = PathOf(t.DirName, t.FileName);
                if (path == null || !File.Exists(path)) continue;
                if (Restore(t, path, "启动自检还原")) n++;
            }
            _applied = false;
            if (n > 0)
                Info($"{LogTag} 启动自检：发现上次异常退出留下的 {ForcedChance}，已从备份还原 {n} 个配置");
        }
        catch (Exception ex) { Warn($"{LogTag} ReconcileOnStartup 失败: {ex.Message}"); }
    }

    // ── on：备份 + 只改数字 + 原子写 ─────────────────────────────
    private static bool TurnOn(Target t, string path)
    {
        var bak = path + BackupSuffix;
        if (!File.Exists(bak)) File.Copy(path, bak, false);   // ★ 先备份"用户原始值"；已有则不覆盖

        var cur = ReadText(path);
        if (cur == null) return false;
        var next = SetValue(cur.Text, t, ForcedChance, out bool changed, out double oldVal);
        if (!changed)
        {
            if (oldVal == ForcedChance) return false;          // 已经是 100：不改、不写
            Warn($"{LogTag} {t.ModName}：{t.FileName} 里找不到 \"{t.Key}\"（或不是数字）⇒ 无法联动");
            return false;
        }
        WriteAtomic(path, next, cur.Bom);
        Info($"{LogTag} {t.ModName}：{t.Key} {Fmt(oldVal)} → {ForcedChance}"
             + $"（原文件已备份为 {Path.GetFileName(bak)}）");
        return true;
    }

    // ── off：从备份精确还原（逐字节）────────────────────────────
    private static bool Restore(Target t, string path, string why)
    {
        var bak = path + BackupSuffix;
        if (!File.Exists(bak)) return false;                   // 我们没改过它（或用户删了备份）⇒ 不动
        byte[] curBytes, bakBytes;
        try { curBytes = File.ReadAllBytes(path); bakBytes = File.ReadAllBytes(bak); }
        catch (Exception ex) { Warn($"{LogTag} {t.ModName} 读文件失败: {ex.Message}"); return false; }
        if (SameBytes(curBytes, bakBytes)) return false;        // 已经是原值：不写（不碰 mtime）
        var cur = ReadText(path);
        var curVal = cur == null ? (double?)null : TryReadValue(cur.Text, t);
        WriteAtomicBytes(path, bakBytes);
        Info($"{LogTag} {t.ModName}：{t.Key} {(curVal.HasValue ? Fmt(curVal.Value) : "?")} → 还原用户原值（{why}）");
        return true;
    }

    // ── 文本/数字工具（纯函数，全部保持"只改数字"）──────────────────
    private sealed class TextFile
    {
        public string Text = "";
        public bool Bom;
    }

    private static TextFile ReadText(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            bool bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            var enc = new UTF8Encoding(false);
            var text = enc.GetString(b, bom ? 3 : 0, b.Length - (bom ? 3 : 0));
            return new TextFile { Text = text, Bom = bom };
        }
        catch (Exception ex) { Warn($"{LogTag} 读 {path} 失败: {ex.Message}"); return null; }
    }

    private static Regex KeyRegex(Target t) => new(
        "(\"" + Regex.Escape(t.Key) + "\"\\s*:\\s*)(\\d+(?:\\.\\d+)?)",
        RegexOptions.None, TimeSpan.FromSeconds(2));

    /// <summary>把 `<c>"key": N</c>` 的 N 换掉；**只替换第一处数字**，其它字节（含 JSONC 注释）不动。</summary>
    private static string SetValue(string text, Target t, int value, out bool changed, out double oldVal)
    {
        bool ch = false;
        double ov = double.NaN;
        try
        {
            var want = value.ToString(CultureInfo.InvariantCulture);
            var res = KeyRegex(t).Replace(text, m =>
            {
                ov = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (m.Groups[2].Value == want) return m.Value;      // 已是目标值
                ch = true;
                return m.Groups[1].Value + want;                     // ★ 只换数字，前后空白/引号原样
            }, 1);
            changed = ch;
            oldVal = ov;
            return res;
        }
        catch (Exception ex) { Warn($"{LogTag} 改写 {t.Key} 失败: {ex.Message}"); changed = ch; oldVal = ov; return text; }
    }

    private static double? TryReadValue(string text, Target t)
    {
        try
        {
            var m = KeyRegex(t).Match(text);
            if (!m.Success) return null;
            return double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static string Fmt(double d)
        => d == Math.Floor(d) ? ((long)d).ToString(CultureInfo.InvariantCulture)
                              : d.ToString("0.###", CultureInfo.InvariantCulture);

    // ── 原子写（同目录临时文件 + rename；watcher 永远读不到半个文件）──
    private static void WriteAtomic(string path, string text, bool bom)
        => WriteAtomicBytes(path, new UTF8Encoding(bom).GetBytes(text));

    private static void WriteAtomicBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".er-tmp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(tmp, bytes);
        try
        {
            File.Move(tmp, path, overwrite: true);            // 同卷 rename：原子替换
        }
        catch
        {
            try { File.Copy(tmp, path, overwrite: true); }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
