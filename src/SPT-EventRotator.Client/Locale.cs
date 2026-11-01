namespace EventRotator.Client;

/// <summary>
/// ★ 面板语言（v1.2.0）
/// <para>F12 →「★★ 悬浮面板 → ★面板语言」：中文 / English。</para>
/// <para>只影响本 mod 自己的界面文字（覆盖层标题、页脚标签、提示语、活动分类名）。
/// 游戏本体与物品名称仍跟随游戏内语言设置。</para>
/// <para>为什么不做"自动跟随客户端语言"：EFT 的语言值在不同版本里路径不同（<c>SharedGameSettingsClass</c>
/// 是混淆类名，跨版本会变），反射取不到就会静默退回中文 —— 老外遇到"设了英文还是中文"更难排查。
/// 给一个明确开关更可预期。</para>
/// </summary>
internal static class Locale
{
    /// <summary>true = 英文界面；false = 中文界面（默认）。</summary>
    internal static bool En;

    /// <summary>按当前语言二选一。</summary>
    internal static string T(string zh, string en) => En ? en : zh;

    /// <summary>
    /// ★ v1.3.6【F12 说明英文化】配置项的长说明（ConfigDescription）用这个：
    /// 中文原文逐字保留，英文只在 <see cref="En"/> 时返回。
    /// <para>注意：配置项是 <c>Awake</c> 里 Bind 的，**切语言后要重启游戏**这些说明才会换语言
    /// （ConfigurationManager 不吃运行时改描述）。</para>
    /// </summary>
    internal static string D(string zh, string en) => En ? en : zh;
}
