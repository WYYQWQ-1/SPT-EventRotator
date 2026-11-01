using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EventRotator;

/// <summary>
/// ★ 把"当前生效的感染率"写成一个文本文件，供**客户端插件**读取用于地图界面自绘标记。
///
/// 为什么走文件而不是 HTTP：客户端本来就在读服务端 mod 目录（活动状态、生效列表都这么做），
/// 多写一个小文件零风险，也不用在客户端再实现一次带鉴权的请求。
///
/// 数据源：`globals.LocationInfection`（`Dictionary&lt;string,int&gt;`，我们每次套用后它才是最终值）。
/// 输出：`&lt;mod&gt;/感染率.txt`，逐行 `mapKey=百分比`，`#` 开头为注释。
/// 失败只 warning，绝不影响主流程。
/// </summary>
internal static class InfectionReport
{
    internal static Action<string> Info;
    internal static Action<string> Warn;

    internal static void Write(string modPath, object globals)
    {
        try
        {
            if (string.IsNullOrEmpty(modPath) || globals == null) return;
            var dict = globals.GetType()
                              .GetProperty("LocationInfection")?
                              .GetValue(globals) as IDictionary;
            if (dict == null || dict.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("# mapKey=百分比（由 SPT-EventRotator 写入，供客户端地图界面显示感染标记）");
            var keys = new List<string>();
            foreach (DictionaryEntry de in dict)
            {
                var k = de.Key?.ToString();
                if (string.IsNullOrEmpty(k)) continue;
                keys.Add(k);
            }
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
            {
                int v = 0;
                try { v = Convert.ToInt32(dict[k]); } catch { }
                sb.AppendLine($"{k}={v}");
            }

            var path = Path.Combine(modPath, "感染率.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Info?.Invoke($"[感染者] 已写出感染率报表 → {path}（{keys.Count} 项，供客户端地图界面显示）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke("[感染者] 写感染率报表失败（已忽略）：" + ex.Message);
        }
    }
}
