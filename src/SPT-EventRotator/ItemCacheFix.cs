using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace EventRotator;

/// <summary>
/// ★ 把本 mod 注入的自定义物品登记进 **ItemBaseClassService 的缓存**。
///
/// 依据（读 Scourge 源码发现的缺口）：`danauraborealis/Scourge` 的 `ScourgeContent.cs:124-127`
/// 在把物品塞进 `TemplateTable.Items` 之后，会对每件物品调用
/// <c>itemBaseClassService.AddItemToCache(itemId)</c>。
///
/// 不调它的后果：物品模板虽然在表里，但**基类/分类缓存**里没有 ⇒
/// 跳蚤分类、商人收货判定、以及部分任务条件（按父类匹配）会认不出这些物品。
///
/// 容错：任何一环拿不到都只 warning，不抛（拿不到就走原有行为）。
/// </summary>
internal static class ItemCacheFix
{
    internal static Action<string> Info;
    internal static Action<string> Warn;
    private static bool _done;

    internal static void Register(IServiceProvider services, System.Collections.Generic.IEnumerable<string> itemIds)
    {
        if (_done) return;
        _done = true;
        try
        {
            var ids = (itemIds ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (ids.Count == 0) return;
            if (services == null) { Warn?.Invoke("[EventRotator][物品缓存] 拿不到 IServiceProvider ⇒ 跳过（物品仍可用，但分类缓存未登记）"); return; }

            var svcType = RuntimeTypes.Find("SPTarkov.Server.Core.Services.Items.ItemBaseClassService");
            if (svcType == null) { Warn?.Invoke("[EventRotator][物品缓存] 找不到 ItemBaseClassService ⇒ 跳过"); return; }

            var svc = services.GetService(svcType);
            if (svc == null) { Warn?.Invoke("[EventRotator][物品缓存] DI 里拿不到 ItemBaseClassService ⇒ 跳过"); return; }

            var mi = svcType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name == "AddItemToCache" && m.GetParameters().Length == 1);
            if (mi == null) { Warn?.Invoke("[EventRotator][物品缓存] ItemBaseClassService 上没有 AddItemToCache(1) ⇒ 跳过"); return; }

            var pType = mi.GetParameters()[0].ParameterType;
            int ok = 0;
            foreach (var id in ids)
            {
                try
                {
                    object arg = pType == typeof(string) ? id : Activator.CreateInstance(pType, id)!;
                    mi.Invoke(svc, new[] { arg });
                    ok++;
                }
                catch (Exception ex) { Warn?.Invoke($"[EventRotator][物品缓存] {id} 登记失败：{ex.GetType().Name}: {ex.Message}"); }
            }
            Info?.Invoke($"[EventRotator][物品缓存] ✔ 已把 {ok}/{ids.Count} 件自定义物品登记进 ItemBaseClassService 缓存（跳蚤分类/商人收货/任务条件才认得出）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke("[EventRotator][物品缓存] 登记异常（已忽略）：" + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
