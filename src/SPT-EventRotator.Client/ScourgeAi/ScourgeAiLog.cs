using BepInEx.Logging;

namespace EventRotator.ScourgeAi;

/// <summary>移植层统一日志口（原 Scourge 用 <c>ScourgeClientPlugin.LogSource</c>）。</summary>
internal static class ScourgeAiLog
{
    internal static ManualLogSource Source;
}
