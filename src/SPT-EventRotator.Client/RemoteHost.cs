using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EventRotator.Client;

/// <summary>
/// ★ v1.3.2【自动填主机地址】
/// <para>优先级：① F12 手填 → ② 读 Fika 的 `fika.jsonc` 里的 `server.backendIp/backendPort`
/// → ③ `<c>http://127.0.0.1:6969</c>`。</para>
/// <para>为什么读 Fika 配置文件：NAT / 内网穿透场景下，玩家本来就会把 `backendIp` 改成主机 IP，
/// 所以客机不需要再手填一遍；自己开服或普通直连时它通常是 `0.0.0.0`，这时退回 127.0.0.1 正好对。</para>
/// </summary>
internal static class RemoteHost
{
    // ══════════ ★★★ C9③【三个坑之一：每 5 秒读一次盘 + 注释里的旧地址会命中】══════════
    //   ① 缓存：`Resolve` 由面板的傅取循环每 5 秒调一次，每次都 `File.ReadAllText` + 跑正则；
    //      杀软环境下小文件读取能到毫秒级 ⇒ 现在**解析结果缓存 30 秒**（手填地址不缓存，改了立刻生效）。
    //   ② 剥注释：`fika.jsonc` 是"带注释的 JSON"，注释里常留着**被停用的旧 backendIp**；
    //      直接跑正则会命中注释里的地址 ⇒ 先把 `//` / `/* */` 注释剥掉（字符串里的 `http://` 必须保住）。
    //   ③ 日志：找不到文件 / ip 是 0.0.0.0 / 读失败 / 地址非法，都**回退 127.0.0.1 并打一条日志**（每类只打一次）。
    /// <summary>自动探测结果缓存时长（秒）。</summary>
    private const double CacheSeconds = 30;

    private static bool _loggedFound;
    private static bool _loggedMissing;
    private static bool _loggedNoIp;
    private static bool _loggedReadErr;
    private static bool _loggedBadUrl;

    private static string _cacheKey;
    private static string _cacheUrl;
    private static DateTime _cacheAt = DateTime.MinValue;

    /// <summary>解析出用于拉取状态的基地址（形如 http://ip:port，无尾斜杠）。</summary>
    internal static string Resolve(string modDir, string manual)
    {
        var m = (manual ?? "").Trim();
        if (m.Length > 0) return m.TrimEnd('/');   // 手填优先，不走缓存（改完立刻生效）

        // ★ C9③ 缓存：同一个 modDir 30 秒内只解析一次
        var key = modDir ?? "";
        var now = DateTime.UtcNow;
        if (_cacheUrl != null && string.Equals(_cacheKey, key, StringComparison.Ordinal)
            && (now - _cacheAt).TotalSeconds < CacheSeconds)
            return _cacheUrl;

        var url = ResolveUncached(key);
        _cacheUrl = url;
        _cacheKey = key;
        _cacheAt = now;
        return url;
    }

    private static string ResolveUncached(string modDir)
    {
        try
        {
            var root = modDir ?? "";
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(root); i++)
                root = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(root))
            {
                LogOnce(ref _loggedMissing, "未找到 SPT 根目录，回退 " + Fallback() + "（主机地址可在 F12 手填）");
                return Fallback();
            }

            var fika = Path.Combine(root, "SPT_Runtime", "user", "mods", "fika-server",
                                    "assets", "configs", "fika.jsonc");
            if (!File.Exists(fika))
            {
                LogOnce(ref _loggedMissing, "未找到 fika.jsonc（" + fika + "），回退 " + Fallback());
                return Fallback();
            }

            var text = File.ReadAllText(fika);
            // ★ C9③ 先剥注释，再找 "server" 段（否则会命中注释里被停用的旧地址）
            text = StripComments(text);
            // 只在 "server" 段里找（避免命中 natPunchServer 的 port）
            var idx = text.IndexOf("\"server\"", StringComparison.OrdinalIgnoreCase);
            var seg = idx >= 0 ? text.Substring(idx) : text;

            var ip = Match(seg, "\"backendIp\"\\s*:\\s*\"([^\"\\\\]+)\"");
            if (ip.Length == 0) ip = Match(seg, "\"ip\"\\s*:\\s*\"([^\"\\\\]+)\"");
            var port = Match(seg, "\"backendPort\"\\s*:\\s*(\\d+)");
            if (port.Length == 0) port = Match(seg, "\"port\"\\s*:\\s*(\\d+)");
            if (port.Length == 0) port = "6969";

            if (ip.Length == 0 || ip == "0.0.0.0" || ip == "*")
            {
                LogOnce(ref _loggedNoIp, "fika.jsonc 里没有可用的 backendIp（'" + ip + "'），回退 " + Fallback());
                return Fallback();
            }

            var url = "http://" + ip + ":" + port;
            // ★ C9③ 顺手校验一下拼出来的地址（例如 port 里混进奇怪字符时不至于发畸形请求）
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                LogOnce(ref _loggedBadUrl, "fika.jsonc 解析出的地址非法（" + url + "），回退 " + Fallback());
                return Fallback();
            }

            LogOnce(ref _loggedFound, "自动探测到主机地址：" + url + "（来自 fika.jsonc）", false);
            return url;
        }
        catch (Exception ex)
        {
            LogOnce(ref _loggedReadErr, "读 fika.jsonc 失败，回退 " + Fallback() + "：" + ex.Message);
            return Fallback();
        }
    }

    private static string Fallback() => "http://127.0.0.1:6969";

    /// <summary>★ C9③ 回退/失败日志：每类只打一次（避免每 5 秒刷屏）。<paramref name="warn"/> = false 时按 info 打（成功探测）。</summary>
    private static void LogOnce(ref bool flag, string msg, bool warn = true)
    {
        if (flag) return;
        flag = true;
        try
        {
            if (warn) Plugin.Log?.LogWarning("[EventRotator.Client] " + msg);
            else Plugin.Log?.LogInfo("[EventRotator.Client] " + msg);
        }
        catch { }
    }

    /// <summary>
    /// ★ C9③ 剥掉 JSONC 的 `//` 行注释与 `/* */` 块注释。
    /// <para>必须**跳过字符串内部**：`"backendIp": "http://1.2.3.4"` 里的 `//` 是值的一部分，不能当注释剥。</para>
    /// </summary>
    private static string StripComments(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        bool inStr = false, esc = false, lineComment = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (lineComment)
            {
                if (c == '\n' || c == '\r') { lineComment = false; sb.Append(c); }
                continue;
            }
            if (inStr)
            {
                if (esc) { esc = false; sb.Append(c); continue; }
                if (c == '\\') { esc = true; sb.Append(c); continue; }
                if (c == '"') inStr = false;
                sb.Append(c);
                continue;
            }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { lineComment = true; i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end >= 0) { i = end + 1; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Match(string text, string pattern)
    {
        try
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        catch { return ""; }
    }
}
