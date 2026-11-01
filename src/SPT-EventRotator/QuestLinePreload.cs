using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace EventRotator;

/// <summary>
/// ★★★ 自定义任务线的**注入入口**：必须排在 `OnLoadOrder.Preload`（=100000）。
///
/// ─────────────────────────────────────────────────────────────────────────────────────
/// 为什么不能放在主类（`EventRotatorServer`，`[Injectable]` 默认 `TypePriority = int.MaxValue`）里：
///   **实机证据**（2026-10-23 启动实测，日志逐字）：
///   <code>
///   2 item(s) were added to the database after profiles loaded:
///   6a1f00020000000000000001, 6a1f00030000000000000001.
///   Add items during OnLoadOrder.Preload, the database is fully loaded by then and lives for the rest of the server's lifetime
///      at SPTarkov.Server.Core.Services.Modding.Custom.DatabaseIntegrityService.EnsureNoItemsAddedSinceProfilesLoaded()
///      at SPTarkov.Server.Core.Services.Modding.Custom.DatabaseIntegrityService.OnLoadAsync(...)
///   ⇒ 直接 abort OnLoad/StartAsync —— 服务端起不来。
///   </code>
///   ⇒ 物品（以及引用它们的任务）统一挪到 Preload 档注入；这一档 SPT 保证"数据库已装满、且还没到读档之后"。
///
/// 与主类的分工：
///   · 本类：只做"往 DB 里加东西"（任务 / 物品 / 文案），幂等、失败只告警；
///   · 主类：照旧做"按活动改运行期数值 + 门控"，它跑在最后，看得见这里加进去的条目。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload)]
public class QuestLinePreload(
    ISptLogger<QuestLinePreload> logger,
    ModHelper modHelper,
    TemplateTable templateTable,
    LocaleTable localeTable,
    JsonUtil jsonUtil,
    IServiceProvider services        // ★ 用来解析 ItemBaseClassService（登记自定义物品缓存）
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            QuestLineDimension.Warn = s => logger.Warning(s);
            QuestLineDimension.Info = s => logger.Info(s);
            // ★ 2026-10-31【ABPS 兜底】Preload 阶段先试着快照第三方出生项（防 ABPS 更早清表）
            try { Patches.ThirdPartySpawnGuard.CaptureNow(services, m => logger.Info(m)); } catch { }

            var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

            // ★★★ SAIN 服务端预设：bossScourge(13790) / followerScourge(13791)
            //   实机报错 `[bossScourge] does not exist in SAINSettings Dictionary!` 的落点修复。
            //   放在 Preload 而不是主类 OnLoad：只读写磁盘 json、不依赖任何 SPT 表；而且比 SAIN 自己的
            //   OnLoad（会 UpgradeCustomToCurrentSchemaAsync 读这些自定义预设）更早。
            //   全程可降级：找不到 SPT_Runtime / SAIN 目录、解析失败 ⇒ 只 warning + 跳过，绝不抛。
            SainPresetInject.Info = s => logger.Info(s);
            SainPresetInject.Warning = s => logger.Warning(s);
            SainPresetInject.Run(modPath);

            QuestLineDimension.Load(modPath, templateTable, localeTable, jsonUtil);

            // ★ 把注入的自定义物品登记进 ItemBaseClassService 缓存
            //   依据：Scourge 的 ScourgeContent.cs:124-127 在加完物品后逐件 AddItemToCache
            //   （不调的话：跳蚤分类 / 商人收货 / 按父类匹配的任务条件都可能认不出这些物品）
            try
            {
                ItemCacheFix.Info = s2 => logger.Info(s2);
                ItemCacheFix.Warn = s2 => logger.Warning(s2);
                ItemCacheFix.Register(services, QuestLineDimension.InjectedItems);
            }
            catch (Exception ex) { logger.Warning("[EventRotator][物品缓存] 登记失败（已忽略）：" + ex.Message); }

            // ★★★ 自定义角色显示名：给 bossScourge / followerScourge 补自己的 ScavRole 文案
            //   （客户端 _spawnTypeSettings 的 ScavRoleKey 指向这里；只新增两条键，不动任何已有词条）
            LocaleInject.Warn = s => logger.Warning(s);
            LocaleInject.Info = s => logger.Info(s);
            LocaleInject.Apply(localeTable);

            // ★★★ 任务线全部完成 → 解锁成就（2026-11-01）
            //   落点 = TemplateTable.Achievements（List<Achievement>，即 achievements.json 那个数组）
            //   依据：AchievementController.GetAchievements() 的 IL 逐字就是
            //         `new GetAchievementsResponse { Elements = templateTable.Achievements }`
            //   ⇒ 必须排在本方法里 QuestLineDimension.Load 之后：那时自定义任务已在
            //      TemplateTable.Quests 里，成就条件引用的任务 id 才能被校验到。
            //   名称/描述由上面 LocaleInject 以 `<成就id> name` / `<成就id> description` 注入。
            AchievementInject.Warn = s => logger.Warning(s);
            AchievementInject.Info = s => logger.Info(s);
            AchievementInject.Load(modPath, templateTable, jsonUtil);

            // 任务配图：把 questlines/images/ 下的图注册成 /files/quest/icon/<文件名>
            // 依据：ImageRouter.CanHandle 会 StripExtension 再查表（ImageRouter.cs:23），
            //      所以任务里写 `/files/quest/icon/scourge_banner.jpg` 也能命中注册的 `/files/quest/icon/scourge_banner`。
            try
            {
                var imgDir = System.IO.Path.Combine(modPath, "questlines", "images");
                if (System.IO.Directory.Exists(imgDir))
                {
                    foreach (var f in System.IO.Directory.GetFiles(imgDir))
                    {
                        var key = "/files/quest/icon/" + System.IO.Path.GetFileNameWithoutExtension(f);
                        // ★ 2026-10-31【排查图片问题】改为**不再自己注册路由**：
                        //   自定义图标已复制到 SPT_Data/images/quest/icon/，由 SPT 自带的 ImageRouteImporter 注册，
                        //   避免我们用 imageRouter.AddRoute 干扰 SPT 的路由表。
                        _ = key;
                        logger.Info($"[EventRotator][任务线] 配图路由 {key} → {System.IO.Path.GetFileName(f)}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"[EventRotator][任务线] 配图注册失败（已忽略）：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[EventRotator][任务线] Preload 注入异常（已忽略，不影响启动）：{ex.Message}");
        }
        return Task.CompletedTask;
    }
}
