using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MongoId = SPTarkov.Server.Core.Models.Common.MongoId;

namespace EventRotator.Patches;

/// <summary>
/// ★★★ v1.0.1【SPT prepatch 兼容 P0 修复】
///
/// <para><b>问题</b>：装了任何带 <c>user/patchers/&lt;guid&gt;/</c> 的 mod（如 ISB Special Forces）后，
/// SPT 4.x 会生成并加载 <c>SPTarkov.Server.Core.Patched.dll</c>（实测大 60KB）。</para>
///
/// <para>⇒ 运行时 <c>MethodInfo</c> 的参数/返回类型都来自**Patched 版程序集**，
/// 而本 mod 是针对**原始程序集**编译的 ⇒ <c>typeof(MongoId)</c> 与运行时类型
/// **不是同一个 Type 实例**（<c>FullName</c> 相同、<c>ReferenceEquals</c> 不同）。</para>
///
/// <para>⇒ 后果：<c>ps[0].ParameterType == typeof(MongoId)</c> 恒 false、
/// <c>__0 is MongoId</c> 恒 false ⇒ 任务门控两个 Harmony 补丁全被跳过
/// ⇒ 任务列表原样返回（玩家实测「没轮换相关活动但任务没隐藏」）。</para>
///
/// <para><b>修法</b>：所有类型比较改成 <see cref="Same"/>（全名比较）；
/// 所有 <c>is MongoId</c> 改成 <see cref="IsMongoId"/>；所有需要真正 MongoId 形参的调用
/// 改成 <see cref="InvokeWithSession"/> 运行时反射。</para>
/// </summary>
internal static class TypeCompat
{
    /// <summary>MongoId 的类型全名（跨程序集版本稳定）。</summary>
    internal const string MongoIdFullName = "SPTarkov.Server.Core.Models.Common.MongoId";

    /// <summary>
    /// 两个 <see cref="Type"/> 是否"同一个类型"——按**全名**判断，规避 SPT prepatch 的
    /// 程序集重载（原始 vs Patched）。
    /// </summary>
    internal static bool Same(Type a, Type b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return string.Equals(a.FullName, b.FullName, StringComparison.Ordinal);
    }

    /// <summary>该对象是不是（运行时版本的）MongoId。</summary>
    internal static bool IsMongoId(object o)
    {
        if (o == null) return false;
        var t = o.GetType();
        if (t == typeof(MongoId)) return true;
        return string.Equals(t.FullName, MongoIdFullName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在 <paramref name="target"/> 上找**只有一个参数、且该参数类型与 session 运行时类型同全名**的方法并调用。
    /// 用于 <c>SaveServer.GetProfile(MongoId)</c> / <c>EventOutputHolder.GetOutput(MongoId)</c> 等。
    /// 找不到/抛异常 ⇒ 返回 null（调用方自行降级）。
    /// </summary>
    internal static object InvokeWithSession(object target, string methodName, object session)
    {
        if (target == null || session == null || string.IsNullOrEmpty(methodName)) return null;
        try
        {
            var st = session.GetType();
            var m = target.GetType()
                          .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                          .FirstOrDefault(x => x.Name == methodName
                                            && !x.IsGenericMethod
                                            && x.GetParameters().Length == 1
                                            && Same(x.GetParameters()[0].ParameterType, st));
            if (m == null) return null;
            return m.Invoke(target, new[] { session });
        }
        catch { return null; }
    }
}
