using System;
using System.Collections.Generic;
using System.IO;

namespace EventRotator;

/// <summary>
/// ★ 把 mod 自带的万圣节商人头像铺进 SPT 的静态图片目录。
///
/// 为什么必须铺到 SPT_Data：商人头像走 `ImageRouteImporter` 注册的静态路由
/// （`SPT_Data/images/...` 整棵树在启动时被注册），**不能**像任务图那样从 mod 目录随便引；
/// 而 `ImageRouter.AddRoute` 那条路我们已经在 v1.3.12 证明会破坏 SPT 自己的图片路由表，不能用。
///
/// 行为：
///   · 源目录：`&lt;mod&gt;/images/trader/halloween/*.png`
///   · 目标目录：`&lt;SPT_Runtime&gt;/SPT_Data/images/trader/halloween/`（同 `trader/avatar` 的兄弟目录）
///   · 目标已存在同名文件且内容相同 ⇒ 跳过（幂等）
///   · 内容不同 ⇒ 先把原文件备份成 `*.bak_eventrotator`，再覆盖
///   · 任何一步失败都只 warning，不抛
/// </summary>
internal static class TraderIconAssetCopier
{
    internal static Action<string> Info;
    internal static Action<string> Warn;

    internal static void Run(string modPath)
    {
        try
        {
            if (string.IsNullOrEmpty(modPath)) return;
            var srcDir = Path.Combine(modPath, "images", "trader", "halloween");
            if (!Directory.Exists(srcDir)) return;   // 没带图 ⇒ 静默跳过（不打扰用户）

            var sptData = LocateSptData(modPath);
            if (sptData == null)
            {
                Warn?.Invoke("[EventRotator][商人头像] 找不到 SPT_Data 目录 ⇒ 自带头像未铺（不影响其它功能）");
                return;
            }
            var dstDir = Path.Combine(sptData, "images", "trader", "halloween");
            Directory.CreateDirectory(dstDir);

            int copied = 0, same = 0, failed = 0;
            foreach (var src in Directory.GetFiles(srcDir, "*.png"))
            {
                try
                {
                    var name = Path.GetFileName(src);
                    var dst = Path.Combine(dstDir, name);
                    if (File.Exists(dst))
                    {
                        if (SameBytes(src, dst)) { same++; continue; }
                        try { File.Copy(dst, dst + ".bak_eventrotator", true); } catch { }
                    }
                    File.Copy(src, dst, true);
                    copied++;
                }
                catch (Exception ex) { failed++; Warn?.Invoke("[EventRotator][商人头像] 铺图失败 " + Path.GetFileName(src) + "：" + ex.Message); }
            }
            if (copied > 0 || failed > 0)
                Info?.Invoke($"[EventRotator][商人头像] 自带万圣节头像：已铺 {copied} 张、相同跳过 {same} 张、失败 {failed} 张 → {dstDir}");
        }
        catch (Exception ex) { Warn?.Invoke("[EventRotator][商人头像] 铺图异常（已忽略）：" + ex.Message); }
    }

    /// <summary>从 mod 目录往上找 `SPT_Runtime`，再拼 `SPT_Data`（标准安装结构）。</summary>
    /// <remarks>internal：成就图铺设（<see cref="AchievementInject"/>）复用同一套定位逻辑。</remarks>
    internal static string LocateSptData(string modPath)
    {
        try
        {
            var dir = new DirectoryInfo(modPath);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var cand = Path.Combine(dir.FullName, "SPT_Data");
                if (Directory.Exists(cand)) return cand;
                var cand2 = Path.Combine(dir.FullName, "SPT_Runtime", "SPT_Data");
                if (Directory.Exists(cand2)) return cand2;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    internal static bool SameBytes(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            var ba = new byte[8192]; var bb = new byte[8192];
            while (true)
            {
                int na = sa.Read(ba, 0, ba.Length), nb = sb.Read(bb, 0, bb.Length);
                if (na != nb) return false;
                if (na == 0) return true;
                for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
            }
        }
        catch { return false; }
    }
}
