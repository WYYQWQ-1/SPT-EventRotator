using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EventRotator.Patches;

/// <summary>
/// ★★★ **容器注入 · 生成期修复 + 补齐**
///
/// 数据层（<see cref="ContainerLootInjectDimension"/>）把高价值物品追加进
/// `staticContainers.json → staticContainers[].template.Items`；但 SPT 在每局生成容器时会
/// **把容器根物品的 `_id` 换成一个新的 MongoId**（`LocationLootGenerator.AddLootToContainer`：
/// `containerClone.Template.Root = new MongoId(); containerClone.Template.Items.FirstOrDefault().Id = 同一个;`），
/// 而预注入件的 `parentId` 仍指向旧根 ⇒ 客户端 `ItemFactory.FlatItemsToTree` 抛
/// `NoParentWithIdError` ⇒ **物品被静默丢弃（看不到、摸不到）**。
///
/// ── ★★★★ v2：实机证明「预注入」这条路本身不保证送到 `__result` ──────────────────
///   `spt20261024.log`：chance=0.25（注入 48 件）与 chance=1.0（注入 205 件）两局的
///   `A total of: 1822 static items spawned` **完全一样**，且 `生成期修复 N 件` 一行未打
///   ⇒ 预注入件没进返回给客户端的对象；v1 的 postfix 因 `fixedN=0 && notSpawned=0` 而**全程静默**。
///   ⇒ 本补丁的 postfix 现在做两件事：
///      ① 认领到预注入件 ⇒ 把 `parentId` 改指运行期 `Root`、`slotId` 改成**该容器真实 grid 名**
///         （不再是硬编码 `"main"`）、`location` 置空；
///      ② 认领不到（预注入被覆盖/过滤/压根没注入）⇒ **当场按真实 grid 名补追加 1 件**。
///   并且**每次都打诊断日志**（`生成钩子已到达` / `本局容器 N 个，含注入物件的容器 M 个，改 parentId K 件`）。
///
/// ★ 全部用 `object __result` + 反射访问：SPT 运行时可能加载 `SPTarkov.Server.Core.Patched.dll`
///   （SPT.Server 会做内存预补丁后 `LoadFrom` 同一份 assembly identity，见
///   `SPT.Server.decompiled.cs:573-736`），编译期 `typeof(...)` 可能不是运行时那份程序集。
/// ★ 补丁挂不上（类型/方法改名）⇒ 只 warning，数据层注入照旧，绝不影响服务端启动。
/// </summary>
internal static class ContainerLootInjectPatch
{
    private static Action<string> _log;

    public static void Enable(Harmony harmony, Action<string> log)
    {
        _log = log;
        try
        {
            var t = RuntimeTypes.Find("SPTarkov.Server.Core.Generators.Loot.LocationLootGenerator")
                    ?? RuntimeTypes.FindSimple("LocationLootGenerator");
            if (t == null)
            {
                Warn("找不到 LocationLootGenerator ⇒ 生成期父节点修复未挂载（数据层注入仍生效，但局内可能看不到注入件）");
                return;
            }

            var m = AccessTools.Method(t, "GenerateStaticContainers");
            if (m == null)
            {
                Warn("找不到 LocationLootGenerator.GenerateStaticContainers ⇒ 生成期父节点修复未挂载");
                return;
            }

            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ContainerLootInjectPatch), nameof(Postfix)));
            _log?.Invoke("[EventRotator][容器注入] 已挂载 GenerateStaticContainers postfix"
                       + "（预注入件 parentId → 运行期 Root；认领不到就当场补注入；"
                       + "location 按容器真实占用算显式 {x,y,r}，算不出才留空）");
            // ★★ 挂载身份自证（v2 新增，专治「日志说挂上了、实机却没跑」）：
            //   SPT 4.1.6 会先把 Core 预补丁成内存程序集再在 `SPT.PrepatchHost` 加载上下文里启动
            //   （`SPT.Server.decompiled.cs:573-736`）。若进程里同时存在「磁盘那份 Core」和
            //   「内存补丁那份 Core」，`AccessTools.TypeByName` 可能返回**没人执行的那份** ⇒
            //   Patch 成功、日志正常、实机永不触发。这里把类型来源打出来，一行就能判死。
            try
            {
                var asm = t.Assembly;
                var alcName = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name ?? "(default)";
                var pars = string.Join(",", m.GetParameters().Select(p => p.Name));
                _log?.Invoke($"[EventRotator][容器注入] 钩子身份：type={t.FullName}｜assembly={asm.GetName().Name} "
                           + $"v{asm.GetName().Version}｜ALC={alcName}｜参数=({pars})"
                           + $"｜GetPatchedMethods 含它={harmony.GetPatchedMethods().Contains(m)}");
            }
            catch (Exception ex2) { Warn("钩子身份自证失败（不影响补丁）：" + ex2.Message); }

            // ★★★ v1.0.2【补丁自证】把"挂载目标 vs 运行时 Core"逐字段打出来（ALC/Location/MVID + 方法签名）。
            PatchProbe.Mount(harmony, m, "LocationLootGenerator.GenerateStaticContainers");
        }
        catch (Exception ex)
        {
            Warn("挂载失败（已降级，数据层注入仍生效）：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// `LocationLootGenerator.GenerateStaticContainers(string locationId, Dictionary&lt;string, IEnumerable&lt;StaticAmmoDetails&gt;&gt;)`
    /// 的 postfix。`locationId` 是 SPT 传进来的**小写地图 id**（如 `woods`）。
    /// </summary>
    public static void Postfix(string locationId, object __result, MethodBase __originalMethod)
    {
        try
        {
            // ★★★ v1.0.2【补丁自证】进程内首次被调用 ⇒ 打印调用期身份，证明"调用的就是挂载的那个方法"。
            PatchProbe.OnCall(__originalMethod, "LocationLootGenerator.GenerateStaticContainers");
            if (__result == null) return;
            ContainerLootInjectDimension.FixUpGeneratedLoot(locationId, __result);
        }
        catch (Exception ex)
        {
            try { _log?.Invoke("[EventRotator][容器注入] ⚠ 生成期修复异常（不影响官方战利品）：" + ex.Message); } catch { }
        }
    }

    private static void Warn(string m)
    {
        try { _log?.Invoke("[EventRotator][容器注入] ⚠ " + m); } catch { }
    }
}
