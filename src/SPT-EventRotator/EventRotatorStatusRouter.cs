using System;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace EventRotator;

/// <summary>
/// ★ v1.3.0【联机状态下发】给客机用的只读接口。
/// <para>背景：覆盖层原本只读本机文件 `当前生效活动.txt`，而该文件只有**服务端进程**会写
/// ⇒ Fika 客机的面板永远是"本机上次开服"的旧内容（天气季节、活动列表全错，甚至显示"没有任何活动生效"）。</para>
/// <para>做法：服务端把每次写文件的同一份文本缓存到 <see cref="Snapshot"/>，
/// 通过 <c>GET /eventrotator/status</c> 返回 <c>{ ok, version, updated, text }</c>；
/// 客户端优先拉这个接口，拉不到再退回读本地文件（单机/主机行为完全不变）。</para>
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue, TypePriority = 400000)]
public class EventRotatorStatusRouter : StaticRouter
{
    /// <summary>最近一次写出的状态文本（由 Plugin.WriteActiveStatus 赋值）。</summary>
    public static volatile string Snapshot = "";

    /// <summary>最近一次写出的时间（本地时间）。</summary>
    public static volatile string SnapshotAt = "";

    /// <summary>★ v1.3.1 实际生效的活动季节（圣诞/万圣/新年…）；空 = 没干预。</summary>
    public static volatile string AppliedEventSeason = "";

    public static volatile string ModVersion = "";

    public EventRotatorStatusRouter(JsonUtil jsonUtil)
        : base(jsonUtil, new RouteAction[]
        {
            new RouteAction<EmptyRequestData>(
                "/eventrotator/status",
                (string url, EmptyRequestData info, MongoId sessionId, string output, CancellationToken cancellationToken)
                    => ValueTask.FromResult(BuildJson())),
        })
    {
    }

    private static string BuildJson()
    {
        var payload = new
        {
            ok = !string.IsNullOrEmpty(Snapshot),
            version = ModVersion,
            updated = SnapshotAt,
            text = Snapshot ?? "",
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }
}
