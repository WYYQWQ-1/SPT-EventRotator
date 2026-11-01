using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace EventRotator.Client
{
    /// <summary>
    /// SAIN 状态诊断：确认活动敌种是否真的被 SAIN 纳入管理。
    /// 按 F11 输出到 BepInEx 日志。
    /// </summary>
    internal static class SainProbe
    {
        internal static void Dump(BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var sain = FindType("SAIN.Components.BotManagerComponent");
                if (sain == null) { log.LogInfo("[SAIN探针] 找不到 SAIN.Components.BotManagerComponent"); return; }

                var inst = sain.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                log.LogInfo($"[SAIN探针] BotManagerComponent.Instance = {(inst == null ? "null" : "有")}");
                if (inst == null) return;

                // Bots 字典
                var bots = sain.GetProperty("Bots", BindingFlags.Public | BindingFlags.Instance)?.GetValue(inst);
                if (bots is System.Collections.IDictionary d)
                {
                    log.LogInfo($"[SAIN探针] BotManagerComponent.Bots 条目数 = {d.Count}");
                    int i = 0;
                    foreach (System.Collections.DictionaryEntry kv in d)
                    {
                        if (i++ >= 8) break;
                        log.LogInfo($"[SAIN探针]   条目: key={kv.Key}  value={kv.Value?.GetType().Name}");
                    }
                }
                else log.LogInfo($"[SAIN探针] Bots 类型 = {bots?.GetType().FullName ?? "null"}");

                // BotSpawnController 的集合
                var bsc = sain.GetProperty("BotSpawnController", BindingFlags.Public | BindingFlags.Instance)?.GetValue(inst);
                if (bsc != null)
                {
                    foreach (var name in new[] { "SAINBots", "VanillaBots", "BotDictionary" })
                    {
                        var v = bsc.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(bsc);
                        if (v == null) { log.LogInfo($"[SAIN探针] BotSpawnController.{name} = null"); continue; }
                        if (v is System.Collections.IDictionary dd) log.LogInfo($"[SAIN探针] BotSpawnController.{name} 条目数 = {dd.Count}");
                        else if (v is System.Collections.ICollection cc) log.LogInfo($"[SAIN探针] BotSpawnController.{name} 数量 = {cc.Count}");
                        else log.LogInfo($"[SAIN探针] BotSpawnController.{name} 类型 = {v.GetType().Name}");
                    }
                }

                // 逐个活动敌种 bot：查 SAIN 是否认识它
                var bc = HostilityGuard.GetBotsControllerPublic();
                var botsList = bc?.GetType().GetField("Bots", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(bc);
                var owners = botsList?.GetType().GetProperty("BotOwners", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botsList) as System.Collections.IEnumerable;
                if (owners == null) { log.LogInfo("[SAIN探针] 取不到 BotOwners"); return; }

                var bscType = bsc?.GetType();
                var getSain = bscType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                      .FirstOrDefault(x => x.Name == "GetSAIN" && x.GetParameters().Length == 1
                                                        && x.GetParameters()[0].ParameterType.Name == "BotOwner");
                int total = 0, hostile = 0, inSain = 0;
                foreach (var o in owners)
                {
                    if (o == null) continue;
                    total++;
                    var role = HostilityGuard.RoleOf(o);
                    if (string.IsNullOrEmpty(role) || !HostilityGuard.HostileRoles.Contains(role)) continue;
                    hostile++;
                    object sainComp = null;
                    try { sainComp = getSain?.Invoke(bsc, new[] { o }); } catch { }
                    var hasComp = sainComp != null;
                    if (hasComp) inSain++;
                    if (hostile <= 6)
                    {
                        var gname = HostilityGuard.RoleOf(o);
                        var grp = o.GetType().GetProperty("BotsGroup", BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
                        var members = grp?.GetType().GetField("_members", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(grp) as System.Collections.ICollection;
                        log.LogInfo($"[SAIN探针] role={role} SAIN组件={(hasComp ? "有" : "无")} 组={(grp == null ? "null" : "有")} 组内成员数={members?.Count ?? -1}");
                    }
                }
                log.LogInfo($"[SAIN探针] 汇总: bots={total} 活动敌种={hostile} 其中有 SAIN 组件={inSain}");
            }
            catch (Exception ex) { log.LogError("[SAIN探针] 异常: " + ex); }
        }

        private static Type FindType(string full) => HostilityGuard.FindTypePublic(full);

        /// <summary>找第一只"活动敌种"bot 并输出它的敌方状态链。</summary>
        internal static void DumpFirstHostileState(BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var bc = HostilityGuard.GetBotsControllerPublic();
                if (bc == null) { log.LogInfo("[敌方状态] BotsController=null"); return; }
                var bl = bc.GetType().GetField("Bots", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(bc);
                var owners = bl?.GetType().GetProperty("BotOwners", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bl) as System.Collections.IEnumerable;
                if (owners == null) { log.LogInfo("[敌方状态] 取不到 BotOwners"); return; }
                foreach (var o in owners)
                {
                    if (o == null) continue;
                    var r = HostilityGuard.RoleOf(o);
                    if (string.IsNullOrEmpty(r) || !HostilityGuard.HostileRoles.Contains(r)) continue;
                    DumpEnemyState(log, o);
                    return;
                }
                log.LogInfo("[敌方状态] 场上没有活动敌种 bot");
            }
            catch (Exception ex) { log.LogError("[敌方状态] 异常: " + ex.Message); }
        }

        /// <summary>
        /// ★ 敌方状态探针：把 SAIN 敌人链上每个关键值都探出来（只读）。
        ///   用于定位"注入成功但不开火"到底卡在哪一环。
        /// </summary>
        internal static void DumpEnemyState(BepInEx.Logging.ManualLogSource log, object botOwner)
        {
            try
            {
                var id = HostilityGuard.BotIdOf(botOwner);
                log.LogInfo($"[敌方状态] ─── bot={id} role={HostilityGuard.RoleOf(botOwner)} ───");

                // ① BotsGroup / Allies（UpdateEnemies 的第一道门）
                var grp = botOwner.GetType().GetProperty("BotsGroup", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
                log.LogInfo($"[敌方状态] ① BotsGroup = {(grp == null ? "null ❌（UpdateEnemies 会直接 return）" : "有")}");
                if (grp != null)
                {
                    var allies = grp.GetType().GetProperty("Allies", BindingFlags.Public | BindingFlags.Instance)?.GetValue(grp);
                    int ac = (allies as System.Collections.ICollection)?.Count ?? -1;
                    log.LogInfo($"[敌方状态] ① Allies = {(allies == null ? "null ❌（UpdateEnemies 会直接 return！）" : $"有，{ac} 个")}");
                    var mems = grp.GetType().GetField("_members", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(grp) as System.Collections.ICollection;
                    log.LogInfo($"[敌方状态] ① 组内成员数 = {mems?.Count ?? -1}");
                    var enemies = grp.GetType().GetField("Enemies", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(grp) as System.Collections.IDictionary;
                    log.LogInfo($"[敌方状态] ① 原版敌人表 Enemies = {(enemies == null ? "null" : $"{enemies.Count} 条")}");
                }

                // ② SAIN EnemyController（★ 僵尸身上通常**没有** SAIN 组件 ⇒ 这里不能 return，
                //    要继续输出**本体侧**关键项，否则看不到"组里到底有没有把你当敌人"）
                var comp = GetBotComponent(botOwner);
                log.LogInfo($"[敌方状态] ② SAIN BotComponent = {(comp == null ? "null ❌（SAIN 没接管这只 bot，行为走本体 brain）" : "有")}");

                // ②-b 本体侧（不依赖 SAIN）：组内敌人表里到底有没有玩家？Memory 的目标是谁？
                try
                {
                    var player0 = HostilityGuard.GetMainPlayer();
                    string pid = player0?.ProfileId ?? "";
                    if (grp != null)
                    {
                        var enemies2 = grp.GetType().GetField("Enemies", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(grp) as System.Collections.IDictionary;
                        bool inList = false;
                        var sample = new List<string>();
                        if (enemies2 != null)
                        {
                            foreach (System.Collections.DictionaryEntry de in enemies2)
                            {
                                // ★★ 重要修正：EFT 的 BotsGroup.Enemies 是 **Dictionary<IPlayer, EnemyInfo>**（键是**对象**，
                                //   不是 ProfileId 字符串）⇒ 旧诊断拿 de.Key.ToString() 比 ProfileId **永远是"否"**（假阴性！）。
                                //   这里改成：取键对象的 ProfileId/Id 属性来比，并顺带打印前 3 个键的身份。
                                var k = de.Key;
                                string kid = null;
                                if (k is string ks) kid = ks;
                                else if (k != null)
                                {
                                    var kt = k.GetType();
                                    kid = kt.GetProperty("ProfileId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(k)?.ToString()
                                       ?? kt.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(k)?.ToString()
                                       ?? kt.GetField("ProfileId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(k)?.ToString()
                                       ?? k.ToString();
                                }
                                if (sample.Count < 3) sample.Add($"{(k == null ? "null" : k.GetType().Name)}:{kid}");
                                if (string.Equals(kid, pid, StringComparison.OrdinalIgnoreCase)) inList = true;
                            }
                        }
                        log.LogInfo($"[敌方状态] ②-b 玩家在【组敌人表】里 = {(inList ? "是 ✅" : "否 ❌（AddEnemy 没落地）")}（表内 {enemies2?.Count ?? -1} 条；前三键 = {string.Join(" ", sample)}）");
                    }
                    // Memory.GoalEnemy（本体每只 bot 自己的当前目标）
                    var mem = botOwner.GetType().GetProperty("Memory", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
                    var goalObj = mem?.GetType().GetProperty("GoalEnemy", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mem);
                    string goalWho = "null（无目标）";
                    if (goalObj != null)
                    {
                        var person = goalObj.GetType().GetProperty("Person", BindingFlags.Public | BindingFlags.Instance)?.GetValue(goalObj);
                        var gid = person?.GetType().GetProperty("ProfileId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(person)?.ToString()
                               ?? person?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(person)?.ToString();
                        goalWho = string.Equals(gid, pid, StringComparison.OrdinalIgnoreCase) ? "★就是玩家 ✅" : $"{gid ?? "?"}（不是玩家）";
                    }
                    // ★★ v0.1.5 重要更正：**`BotOwner.Settings` 与 `BotOwner.Memory` 都是【公开字段】**（不是属性）
                    //   客户端反编译实证：`public BotSettings Settings;` / `public BotMemory Memory;`
                    //   ⇒ 旧诊断用 GetProperty 读 ⇒ **永远读到 null** ⇒ `Memory.GoalEnemy = null` 是**读取失败**而不是真实状态！
                    //   现在字段/属性都查（FieldOrProp），并且**直接调用**两个判定函数来测量。
                    object FieldOrProp(object o, string name)
                    {
                        if (o == null) return null;
                        var tt = o.GetType();
                        return tt.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
                            ?? tt.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);
                    }
                    var st2 = FieldOrProp(botOwner, "Settings");
                    // ②-c-1 直接**测量**：bot.Settings.IsPlayerEnemy(玩家)
                    try
                    {
                        object rr = null; string how = "";
                        if (st2 != null)
                        {
                            foreach (var m in st2.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == "IsPlayerEnemy" && x.GetParameters().Length == 1))
                            {
                                var pt = m.GetParameters()[0].ParameterType;
                                if (player0 == null || (!pt.IsInstanceOfType(player0) && !pt.IsAssignableFrom(player0.GetType()))) continue;
                                rr = m.Invoke(st2, new object[] { player0 }); how = pt.Name; break;
                            }
                        }
                        log.LogInfo($"[敌方状态] ②-c-1 bot.Settings.IsPlayerEnemy(玩家) = {rr?.ToString() ?? "（没找到可调用的重载）"}"
                                  + $"　｜ Settings={(st2 == null ? "null ❌（字段也读不到）" : st2.GetType().Name)} 参数类型={(how.Length == 0 ? "-" : how)} 玩家={player0?.GetType().Name}");
                    }
                    catch (Exception eA) { log.LogInfo($"[敌方状态] ②-c-1 抛异常: {eA.GetType().Name}: {eA.Message}"); }
                    // ②-c-2 直接**测量**：group.IsPlayerEnemy(玩家)（这是游戏里第一个被调用的判定）
                    try
                    {
                        object gg = null; string how2 = "";
                        if (grp != null)
                        {
                            foreach (var m in grp.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == "IsPlayerEnemy" && x.GetParameters().Length == 1))
                            {
                                var pt = m.GetParameters()[0].ParameterType;
                                if (player0 == null || (!pt.IsInstanceOfType(player0) && !pt.IsAssignableFrom(player0.GetType()))) continue;
                                gg = m.Invoke(grp, new object[] { player0 }); how2 = pt.Name; break;
                            }
                        }
                        log.LogInfo($"[敌方状态] ②-c-2 group.IsPlayerEnemy(玩家) = {gg?.ToString() ?? "（没找到可调用的重载）"}　参数类型={(how2.Length == 0 ? "-" : how2)}");
                    }
                    catch (Exception eB) { log.LogInfo($"[敌方状态] ②-c-2 抛异常: {eB.GetType().Name}: {eB.Message}"); }
                    // ②-c-3 Memory 的真实内容（用字段读）
                    try
                    {
                        var mem2 = FieldOrProp(botOwner, "Memory");
                        log.LogInfo($"[敌方状态] ②-c-3 Memory = {(mem2 == null ? "null ❌" : mem2.GetType().Name)}"
                                  + (mem2 == null ? "" : "　字段/属性: " + string.Join(",",
                                        mem2.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name)
                                            .Concat(mem2.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name)).Take(14))));
                        if (mem2 != null)
                        {
                            var goal2 = FieldOrProp(mem2, "GoalEnemy");
                            // ★★ v0.1.5：**只测量** —— 目标是谁？是不是玩家？距离/可见/可射击？
                            string who2 = "null（真的没有目标）", extra = "";
                            if (goal2 != null)
                            {
                                object person2 = FieldOrProp(goal2, "Person");
                                string gid2 = (FieldOrProp(person2, "ProfileId") ?? FieldOrProp(person2, "Id"))?.ToString();
                                who2 = string.Equals(gid2, pid, StringComparison.OrdinalIgnoreCase)
                                     ? "★就是玩家" : (string.IsNullOrEmpty(gid2) ? "（目标无 ProfileId）" : gid2 + "（不是玩家）");
                                extra = $"　距离={FieldOrProp(goal2, "Distance") ?? "-"} 可见={FieldOrProp(goal2, "IsVisible") ?? "-"} 可射击={FieldOrProp(goal2, "CanShoot") ?? "-"}";
                            }
                            log.LogInfo($"[敌方状态] ②-c-3 Memory.GoalEnemy = {who2}{extra}");
                            // 武器状态（字段/属性都查 —— 之前 "Melee=取不到" 就是只查了属性）
                            try
                            {
                                var wm3 = FieldOrProp(botOwner, "WeaponManager");
                                var cw3 = FieldOrProp(wm3, "CurrentWeapon");
                                var mw3 = FieldOrProp(wm3, "Melee");
                                log.LogInfo($"[敌方状态] ②-c-3 武器：CurrentWeapon={(cw3?.GetType().Name ?? "null")}"
                                          + $"　Melee={(mw3?.GetType().Name ?? "null ❌")}"
                                          + (mw3 == null ? "" : $"　HaveMelee={FieldOrProp(mw3, "HaveMelee")}　IsShoot={FieldOrProp(mw3, "IsShoot")}"));
                            }
                            catch (Exception eW) { log.LogInfo($"[敌方状态] ②-c-3 武器读取异常: {eW.GetType().Name}"); }

                            // ④ ★★ v0.1.5：**测量"它现在在做什么"**（客户端实证接口）
                            //   `BotOwner.Brain.BaseBrain.ShortName()` = 当前脑层名；
                            //   `DecisionQueue.LastDecision` / `GetCurrentDecision()` = 当前/上一个决策。
                            try
                            {
                                var brain = FieldOrProp(botOwner, "Brain");
                                var baseBrain = FieldOrProp(brain, "BaseBrain");
                                string sn = "-";
                                if (baseBrain != null)
                                {
                                    try
                                    {
                                        sn = baseBrain.GetType().GetMethod("ShortName", BindingFlags.Public | BindingFlags.Instance)
                                                      ?.Invoke(baseBrain, null)?.ToString() ?? baseBrain.GetType().Name;
                                    }
                                    catch { sn = baseBrain.GetType().Name + "(ShortName 抛异常)"; }
                                }
                                var dq = FieldOrProp(botOwner, "DecisionQueue");
                                var last = FieldOrProp(dq, "LastDecision");
                                string curDec = "-";
                                try
                                {
                                    curDec = dq?.GetType().GetMethod("GetCurrentDecision", BindingFlags.Public | BindingFlags.Instance)
                                             ?.Invoke(dq, null)?.ToString() ?? "-";
                                }
                                catch { }
                                log.LogInfo($"[敌方状态] ②-c-4 脑层={sn}　LastDecision={last?.ToString() ?? "-"}　当前决策={curDec}　Brain={(brain == null ? "null" : brain.GetType().Name)}");
                            }
                            catch (Exception eB2) { log.LogInfo($"[敌方状态] ②-c-4 读取异常: {eB2.GetType().Name}"); }
                        }
                    }
                    catch (Exception eC) { log.LogInfo($"[敌方状态] ②-c-3 读取异常: {eC.GetType().Name}"); }

                    // ⚠ 已删除误导性的一行：旧 `②-b Memory.GoalEnemy`（用 GetProperty 读**公开字段** ⇒ 永远 null）。
                    //   真实值看 ②-c-3（用 FieldOrProp 读，实测"★就是玩家 距离=2.2 可见=True 可射击=True"）。
                    var infos = mem?.GetType().GetProperty("EnemyInfos", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mem) as System.Collections.IDictionary;
                    log.LogInfo($"[敌方状态] ②-b Memory.EnemyInfos = {infos?.Count ?? -1} 条");

                    // ★★ v0.1.5：**最决定性的一项** —— 直接问这只 bot 自己的感知层：
                    //   `bot.Settings.IsPlayerEnemy(玩家)` 返回什么？
                    //   · false ⇒ 我们的 IsPlayerEnemy 补丁没生效（或被别的 mod 覆盖）⇒ 目标永远不会被选中（近战分支要求 goalEnemy）
                    //   · true  ⇒ 感知层认你是敌人，卡点在更后面（传感器 / brain 选目标）
                    try
                    {
                        var st = botOwner.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
                        // ★ 按**名字**找所有 IsPlayerEnemy 重载（不限定签名 —— 上一版用 typeof(EFT.IPlayer) 精确匹配，
                        //   实测匹配不上 ⇒ 一直打印"方法/玩家取不到"，拿不到任何数据）
                        var cands = st == null ? new MethodInfo[0]
                                  : st.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                      .Where(m => m.Name == "IsPlayerEnemy" && m.GetParameters().Length == 1).ToArray();
                        string sig = string.Join(" / ", cands.Select(m => m.GetParameters()[0].ParameterType.Name));
                        object rr = null; string err = "";
                        foreach (var m in cands)
                        {
                            try
                            {
                                var p0 = m.GetParameters()[0].ParameterType;
                                object arg = player0;
                                if (p0.IsInstanceOfType(player0) || p0.IsAssignableFrom(player0.GetType())) arg = player0;
                                else continue;
                                rr = m.Invoke(st, new object[] { arg });
                                break;
                            }
                            catch (Exception eI) { err = eI.GetType().Name; }
                        }
                        log.LogInfo($"[敌方状态] ②-c bot.Settings.IsPlayerEnemy(玩家) = {rr?.ToString() ?? "（取不到:" + err + "）"}"
                                  + $"　｜ Settings类型={st?.GetType().Name ?? "-"} 重载={cands.Length} 参数={sig}"
                                  + $"　｜ 玩家类型={player0?.GetType().Name ?? "-"} IsAI={player0?.IsAI}");
                    }
                    catch (Exception e10) { log.LogInfo($"[敌方状态] ②-c IsPlayerEnemy 读取失败: {e10.GetType().Name}"); }
                }
                catch (Exception e9) { log.LogInfo($"[敌方状态] ②-b 本体侧读取失败: {e9.GetType().Name}"); }

                if (comp == null) return;
                var ec = comp.GetType().GetProperty("EnemyController", BindingFlags.Public | BindingFlags.Instance)?.GetValue(comp);
                log.LogInfo($"[敌方状态] ② EnemyController = {(ec == null ? "null ❌" : "有")}");
                if (ec == null) return;

                var arr = ec.GetType().GetProperty("EnemiesArray", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ec) as System.Collections.ICollection;
                var dict = ec.GetType().GetProperty("Enemies", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ec) as System.Collections.IDictionary;
                log.LogInfo($"[敌方状态] ② EnemiesArray = {arr?.Count ?? -1} 个   Enemies字典 = {dict?.Count ?? -1} 条");

                var goal = ec.GetType().GetProperty("GoalEnemy", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ec);
                log.LogInfo($"[敌方状态] ② GoalEnemy = {(goal == null ? "null" : "有")}");

                // ③ 玩家那个 Enemy 对象
                var player = HostilityGuard.GetMainPlayer();
                if (player == null) { log.LogInfo("[敌方状态] ③ 玩家=null"); return; }
                object enemy = null;
                var g = ec.GetType().GetMethod("GetEnemy", BindingFlags.Public | BindingFlags.Instance);
                if (g != null) { try { enemy = g.Invoke(ec, new object[] { player.ProfileId, false }); } catch { } }
                log.LogInfo($"[敌方状态] ③ 玩家的 Enemy 对象 = {(enemy == null ? "null ❌（没被 SAIN 接受为敌人）" : "有")}");
                if (enemy == null) return;

                var et = enemy.GetType();
                var known = et.GetProperty("EnemyKnown", BindingFlags.Public | BindingFlags.Instance)?.GetValue(enemy);
                var valid = et.GetProperty("WasValid", BindingFlags.Public | BindingFlags.Instance)?.GetValue(enemy);
                var isCur = et.GetProperty("IsCurrentEnemy", BindingFlags.Public | BindingFlags.Instance)?.GetValue(enemy);
                var places = et.GetProperty("KnownPlaces", BindingFlags.Public | BindingFlags.Instance)?.GetValue(enemy);
                var lkp = places?.GetType().GetProperty("LastKnownPlace", BindingFlags.Public | BindingFlags.Instance)?.GetValue(places);
                var tSince = places?.GetType().GetProperty("TimeLastKnownUpdated", BindingFlags.Public | BindingFlags.Instance)?.GetValue(places);
                log.LogInfo($"[敌方状态] ③ EnemyKnown = {known}   WasValid = {valid}   IsCurrentEnemy = {isCur}");
                log.LogInfo($"[敌方状态] ③ LastKnownPlace = {(lkp == null ? "null ❌（ShallKnowEnemy 会返回 false）" : "有 ✅")}   TimeLastKnownUpdated = {tSince}");

                // ④ ★★★ 激活状态（SAIN 是否真的在"驱动战斗"）
                var boType = botOwner.GetType();

                // ④-0 ★ brain（SAIN 的战斗层只挂在特定 brain 上；若 brain 名不是 ArenaFighter，层就不生效）
                object brainObj = null;
                try
                {
                    brainObj = boType.GetProperty("Brain", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner)
                            ?? boType.GetField("Brain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(botOwner);
                }
                catch { }
                string brainName = "?";
                try
                {
                    if (brainObj != null)
                    {
                        brainName = brainObj.GetType().Name;
                        // ★★ 关键：BigBrain 用 Brain.BaseBrain.ShortName() 来匹配 brain！
                        try
                        {
                            var baseBrain = brainObj.GetType().GetProperty("BaseBrain", BindingFlags.Public | BindingFlags.Instance)?.GetValue(brainObj);
                            var shortName = baseBrain?.GetType().GetMethod("ShortName", BindingFlags.Public | BindingFlags.Instance)?.Invoke(baseBrain, null)?.ToString();
                            brainName += $"   BaseBrain.ShortName()='{shortName}'";
                        }
                        catch (Exception e2) { brainName += "   ShortName异常:" + e2.GetType().Name; }
                    }
                    else brainName = "null ❌";
                }
                catch { }
                // 角色（WildSpawnType）
                string roleName = "?";
                try
                {
                    var prof = boType.GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
                    var info = prof?.GetType().GetProperty("Info", BindingFlags.Public | BindingFlags.Instance)?.GetValue(prof);
                    var settings = info?.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info);
                    roleName = settings?.GetType().GetProperty("Role", BindingFlags.Public | BindingFlags.Instance)?.GetValue(settings)?.ToString() ?? "?";
                }
                catch { }
                log.LogInfo($"[敌方状态] ④-0 Brain = {brainName}   角色Role = {roleName}   ← SAIN 层只挂在 ArenaFighter brain 上");

                var botState = boType.GetProperty("BotState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
                object isActive = null;
                try
                {
                    var m = boType.GetMethod("IsBotActive", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (m != null) isActive = m.Invoke(botOwner, null);
                }
                catch { }
                log.LogInfo($"[敌方状态] ④ BotOwner.BotState = {botState}   IsBotActive() = {isActive}");

                var act = comp.GetType().GetProperty("BotActivation", BindingFlags.Public | BindingFlags.Instance)?.GetValue(comp);
                if (act == null) { log.LogInfo("[敌方状态] ④ BotActivation = null ❌"); return; }
                var at = act.GetType();
                object G(string n) { try { return at.GetProperty(n, BindingFlags.Public | BindingFlags.Instance)?.GetValue(act); } catch { return "?"; } }
                log.LogInfo($"[敌方状态] ④ BotActive = {G("BotActive")}   BotInStandBy = {G("BotInStandBy")}");
                log.LogInfo($"[敌方状态] ④ SAINLayersActive = {G("SAINLayersActive")}   ActiveLayer = {G("ActiveLayer")}   BotInCombat = {G("BotInCombat")}");
                var cur = G("CurrentAction");
                log.LogInfo($"[敌方状态] ④ CurrentAction = {(cur == null ? "null ❌（无动作 = 不会开火）" : cur.GetType().Name)}");

                // ⑤ 决策
                var dec = comp.GetType().GetProperty("Decision", BindingFlags.Public | BindingFlags.Instance)?.GetValue(comp);
                if (dec != null)
                {
                    object D(string n) { try { return dec.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance)?.GetValue(dec); } catch { return "?"; } }
                    log.LogInfo($"[敌方状态] ⑤ CurrentCombatDecision = {D("CurrentCombatDecision")}   CurrentDecision = {D("CurrentDecision")}");
                }
                else log.LogInfo("[敌方状态] ⑤ Decision = null ❌");
            }
            catch (Exception ex) { log.LogError("[敌方状态] 异常: " + ex); }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  【核心】给 SAIN 注入"已知敌人位置" → 让 bot 真正开火
        //
        //  源码依据（Solarint/SAIN · ArchangelWTF/SAIN）：
        //    EnemyKnownChecker.ShallKnowEnemy():
        //        if (places.LastKnownPlace == null) return false;     ← ★ 就是这里卡住
        //    EnemyKnownPlaces.SetLastKnown() 只被 4 个方法调用，其中：
        //        public EnemyPlace UpdateSeenPlace(Vector3 position, float currentTime)  ← public！
        //            → SetLastKnown(LastSeenPlace, time)
        //
        //  所以：对 SAIN 的 Enemy 对象调用 UpdateSeenPlace(玩家位置, Time.time)，
        //        LastKnownPlace 就有值 → ShallKnowEnemy=true → SAIN 进入战斗并开火。
        //  这是 SAIN 自己的公开 API，不是反射改内部状态。
        // ═══════════════════════════════════════════════════════════════════
        // ── 反射对象缓存（★ 性能关键）──
        private static MethodInfo _mGetSAIN;
        private static PropertyInfo _pBotCtlInstance;
        private static PropertyInfo _pEnemyController, _pEnemies, _pGoalEnemy;
        private static MethodInfo _mIsPlayerAnEnemy, _mCheckAddEnemy, _mGetEnemy, _mUpdateSeenPlace;
        private static PropertyInfo _pPlayerPosition, _pPlayerTransform;
        private static object _sainInst;
        // ── 每 bot 节流（profileId → 下次允许注入时间）──
        private static readonly Dictionary<string, float> _nextInject = new(StringComparer.Ordinal);
        private static int _injectOk;
        private static float _lastInjectLog;

        internal static void InjectEnemyKnown(object botOwner)
        {
            try
            {
                var id = HostilityGuard.BotIdOf(botOwner);
                if (string.IsNullOrEmpty(id)) return;

                // ★ 节流：每个 bot 每 2 秒最多一次（原来每秒对 20 个 bot 做数百次反射）
                float now = UnityEngine.Time.realtimeSinceStartup;
                bool known = _nextInject.TryGetValue(id, out var next);
                if (known && now < next) return;
                // ★★★ v1.0.1【性能·第十二轮】**错峰**：原来所有 bot 的令牌都对齐在整 2 秒 ⇒ 扫描时一起到期 ⇒
                //   一帧内 370 次注入（每次 3 次 Invoke）⇒ 每 2 秒卡一下。
                //   做法：**只在该 bot 的"第一个令牌"上加 0 / 0.5 / 1.0 / 1.5 秒的相位偏移**
                //   （按插入顺序均分）；之后每次都 `now + 2f`，相位自然保持。
                //   ⇒ 任意 0.5 秒窗口内只有约 1/4 的 bot 到期，而**每只 bot 的刷新间隔仍是 2 秒** ⇒ 行为不变。
                float stagger = known ? 0f : ((_nextInject.Count & 3) * 0.5f);
                _nextInject[id] = now + 2f + stagger;
                if (_nextInject.Count > 400) _nextInject.Clear();   // 防跨对局无限增长
                var comp = GetBotComponent(botOwner);
                if (comp == null) return;
                var ect0 = comp.GetType();
                if (_pEnemyController == null) _pEnemyController = ect0.GetProperty("EnemyController", BindingFlags.Public | BindingFlags.Instance);
                var ec = _pEnemyController?.GetValue(comp);
                if (ec == null) return;

                var player = HostilityGuard.GetMainPlayer();
                if (player == null) return;

                var ect = ec.GetType();
                if (_mIsPlayerAnEnemy == null) _mIsPlayerAnEnemy = ect.GetMethod("IsPlayerAnEnemy", BindingFlags.Public | BindingFlags.Instance);
                if (_mCheckAddEnemy == null) _mCheckAddEnemy = ect.GetMethod("CheckAddEnemy", BindingFlags.Public | BindingFlags.Instance);
                if (_mGetEnemy == null) _mGetEnemy = ect.GetMethod("GetEnemy", BindingFlags.Public | BindingFlags.Instance);
                if (_pEnemies == null) _pEnemies = ect.GetProperty("Enemies", BindingFlags.Public | BindingFlags.Instance);
                if (_pGoalEnemy == null) _pGoalEnemy = ect.GetProperty("GoalEnemy", BindingFlags.Public | BindingFlags.Instance);

                // 玩家是否已在 SAIN 敌人列表里
                bool inList = false;
                try { if (_mIsPlayerAnEnemy != null) inList = (bool)_mIsPlayerAnEnemy.Invoke(ec, new object[] { player.ProfileId }); } catch { }
                if (!inList)
                {
                    try { _mCheckAddEnemy?.Invoke(ec, new object[] { player }); } catch { }
                }

                // 取 Enemy 对象
                object enemy = null;
                if (_mGetEnemy != null)
                {
                    try { enemy = _mGetEnemy.Invoke(ec, new object[] { player.ProfileId, false }); } catch { }
                    if (enemy == null) { try { enemy = _mGetEnemy.Invoke(ec, new object[] { player.ProfileId, true }); } catch { } }
                }
                if (enemy == null)
                {
                    try
                    {
                        var d = _pEnemies?.GetValue(ec) as System.Collections.IDictionary;
                        if (d != null && d.Contains(player.ProfileId)) enemy = d[player.ProfileId];
                    }
                    catch { }
                }
                if (enemy == null) return;

                // ★ 注入"看见过玩家" → LastKnownPlace 有值
                var places = enemy.GetType().GetProperty("KnownPlaces", BindingFlags.Public | BindingFlags.Instance)?.GetValue(enemy);
                if (places == null) return;
                if (_mUpdateSeenPlace == null) _mUpdateSeenPlace = places.GetType().GetMethod("UpdateSeenPlace", BindingFlags.Public | BindingFlags.Instance);
                if (_mUpdateSeenPlace == null) { HostilityGuard.DiagKey("[SAIN注入] 找不到 UpdateSeenPlace"); return; }
                _mUpdateSeenPlace.Invoke(places, new object[] { GetPlayerPos(player), UnityEngine.Time.time });

                // 顺带把 GoalEnemy 指向玩家
                try { if (_pGoalEnemy != null && _pGoalEnemy.CanWrite) _pGoalEnemy.SetValue(ec, enemy); } catch { }

                // ★ 成功计数：确认注入真的生效（限频报，避免刷屏）
                _injectOk++;
                if (UnityEngine.Time.realtimeSinceStartup - _lastInjectLog > 10f)
                {
                    _lastInjectLog = UnityEngine.Time.realtimeSinceStartup;
                    if (HostilityGuard.DiagOn) HostilityGuard.DiagKey($"[SAIN注入] ★成功 {_injectOk} 次（最新 bot={id}）");
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                if (HostilityGuard.DiagOn) HostilityGuard.DiagKey("[SAIN注入] 异常: " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        // ★★★ v1.0.1【性能·第十九轮·Inject 阶段探针实测】
        //   实测 `Inject stage=12 total=25.3ms` —— stage 12 = GetBotComponent 返回 null，却花了 25.3ms。
        //   原因：`_sainInst == null` 时**每次都重做** FindType + GetProperty + GetValue；
        //     若 SAIN 的 BotSpawnController.Instance 暂时为 null（进图早期），**每只 bot 都要重来一遍**。
        //   修法：**负缓存 3 秒** —— null 时短期记住，别每只 bot 都重试；3 秒后再试（SAIN 晚加载也能恢复）。
        private static float _sainNullUntil;
        private static bool _sainFindFailed;      // FindType 找不到 SAIN ⇒ 永久关闭（SAIN 没装）
        private static int _sainMissCount;        // ★ v1.0.1 连续 GetSAIN 返回 null 的次数（达阈值强制重取 instance）

        private static object GetBotComponent(object botOwner)
        {
            try
            {
                if (_sainFindFailed) return null;
                // ★★★ v1.0.1【BugFix·全量审计#4】`_sainInst` 原来永不失效，连 Unity 对象销毁都不查。
                //   SAIN 的 `BotSpawnController.Instance` 是**局级对象**（代码自己在下面的注释里承认"进图早期可能为 null"）
                //   ⇒ 第 2 局起 `_sainInst` 可能指向**上一局已销毁**的 controller ⇒ `GetSAIN(bot)` 恒 null
                //   ⇒ SAIN 注入（EnemyKnown / GoalEnemy）永久静默失效 ⇒ "活动 bot 又不打人"。
                //   修法：先查 Unity 对象是否已销毁；再对"连续多次 GetSAIN 返回 null"做强制重取。
                try
                {
                    if (_sainInst is UnityEngine.Object uo && uo == null) { _sainInst = null; _mGetSAIN = null; _sainNullUntil = 0f; }
                }
                catch { }
                if (_sainInst == null)
                {
                    if (UnityEngine.Time.realtimeSinceStartup < _sainNullUntil) return null;   // ★ 负缓存
                    var bsc = FindType("SAIN.Components.BotController.BotSpawnController");
                    if (bsc == null) { _sainFindFailed = true; return null; }                   // ★ SAIN 没装 ⇒ 永久关
                    if (_pBotCtlInstance == null) _pBotCtlInstance = bsc.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    _sainInst = _pBotCtlInstance?.GetValue(null);
                    if (_sainInst == null) { _sainNullUntil = UnityEngine.Time.realtimeSinceStartup + 3f; return null; }
                    if (_mGetSAIN == null)
                        _mGetSAIN = _sainInst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                            .FirstOrDefault(x => x.Name == "GetSAIN" && x.GetParameters().Length == 1);
                }
                var __comp = _mGetSAIN?.Invoke(_sainInst, new[] { botOwner });
                if (__comp == null)
                {
                    // ★ v1.0.1【BugFix】连续拿不到 ⇒ 可能是旧局的 controller 已失效 ⇒ 强制重取（下次调用重新找 Instance）
                    if (++_sainMissCount >= 20) { _sainMissCount = 0; _sainInst = null; _mGetSAIN = null; _sainNullUntil = 0f; }
                }
                else _sainMissCount = 0;
                return __comp;
            }
            catch { return null; }
        }

        private static UnityEngine.Vector3 GetPlayerPos(object player)
        {
            try
            {
                if (_pPlayerPosition == null && _pPlayerTransform == null)
                {
                    var t = player.GetType();
                    _pPlayerPosition = t.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                    _pPlayerTransform = t.GetProperty("Transform", BindingFlags.Public | BindingFlags.Instance)
                                     ?? t.GetProperty("PlayerTransform", BindingFlags.Public | BindingFlags.Instance);
                }
                var v = _pPlayerPosition?.GetValue(player);
                if (v is UnityEngine.Vector3 vv) return vv;
                var tr = _pPlayerTransform?.GetValue(player);
                if (tr is UnityEngine.Transform trr) return trr.position;
            }
            catch { }
            return UnityEngine.Vector3.zero;
        }
    }
}
