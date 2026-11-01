using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// 与 **`acidphantasm-progressivebotsystem`**（DLL 命名空间 `ProgressiveBotSystem`，官方名
/// **APBS** = Acid's Progressive Bot System）**兼容**，但**绝不修改它的任何文件**。
///
/// ⚠⚠ **命名歧义警告（实测踩过，务必按官网缩写）**：本机同时装着**两个** mod，
///   缩写**字母顺序只差一点**，含义却相反 ——
///   · `acidphantasm-progressivebotsystem` → `ProgressiveBotSystem` → 官方缩写 **APBS**
///     = Acid's **P**rogressive **B**ot **S**ystem → **管装备**（本类处理的就是它）
///   · `acidphantasm-botplacementsystem`   → `BotPlacementSystemServer` → 官方缩写 **ABPS**
///     = Acid's **B**ot **P**lacement **S**ystem → **管刷点**（见 `AbpsCompat`）
///   区分方法（DLL 关键词计数，本地实测）：
///     `GenerateInventory` / `GetEquipmentByBotRole` ⇒ 只有 **progressivebotsystem** 有（3 / 50 次）
///     `SpawnChance` / `BossLocationSpawn`           ⇒ 只有 **botplacementsystem** 有（1239 / 2 次）
///   ⇒ 「装备刷不出来」的锅在 **progressivebotsystem（APBS）**，**不是** `botplacementsystem`。
///
/// ## 为什么必须有这一层（反编译实证）
/// PBS 用 Harmony **Prefix 拦截**了 `BotInventoryGenerator.GenerateInventory`（`GenerateInventoryPatch.Prefix` 返回 false），
/// 装备整条生成链全走它自己的 `CustomBotInventoryGenerator`，其数据来自 **PBS 自己的池子**
/// （`Data/Equipment/Tier{0..7}_equipment.json`），**完全不读 `BotType.BotInventory.Equipment`**：
/// <code>
/// // CustomBotInventoryGenerator:154
/// var equipmentByBotRole = _botEquipmentHelper.GetEquipmentByBotRole(botGenerationDetails.RoleLowercase, tierNumber);
/// </code>
/// ⇒ 本 mod 往 `BotTypeInventory.Equipment` 写的万圣节装备（南瓜头盔 / 鬼脸面罩 / Tagilla 镰刀）
///   **在装了 PBS 的本机一件都不会出现**（纯 SPT 环境下是有效的，那部分代码是对的）。
///
/// 概率同理：`GenerateInventoryPatch.Prefix:99` 用的是
/// <code>
/// var chancesByBotRole = _botEquipmentHelper.GetChancesByBotRole(botGenerationDetails.RoleLowercase, num);
/// </code>
/// ⇒ 也取自 PBS 自己的 `ChancesData`，`botType.BotChances` 同样被无视。
///
/// ## 落点（反编译逐层确认）
/// <code>
/// ModConfig._dataLoader                       (static private, ProgressiveBotSystem.Globals)
///   └─ AllTierDataDirty                       (PBS 生成时**实际读**的那份；BotEquipmentHelper:56/70/98)
///        └─ Tiers : Dictionary&lt;int, TierInnerData&gt;
///             └─ [tier].EquipmentData : EquipmentTierData
///                  └─ .Scav / .PmcBear / .PmcUsec / .BossTagilla … : ApbsEquipmentBot
///                       └─ .Equipment : Dictionary&lt;ApbsEquipmentSlots, Dictionary&lt;MongoId, double&gt;&gt;
///                                                  ↑ 枚举键（Headwear / FaceCover / Scabbard …）
/// </code>
///
/// ## 局内安全（不会被打回）
/// `AllTierDataDirty` 只在 `ModConfig.ReloadConfig` / `SaveConfig` 里被 `DeepClone(AllTierDataClean)` 覆盖，
/// 而这两个方法的**第一件事**就是 `if (RaidInformation.IsInRaid) return ConfigOperationResult.InRaid;`
/// ⇒ **局内不会被重置**。但我们仍在每次进图时重刷一次（幂等），以防你在局外改配置。
///
/// ## 角色映射（`BotEquipmentHelper.GetEquipmentByBotRole:163+` 逐字）
/// `assault` / `marksman` / `cursedassault` → **`Scav`**；`pmcusec` → `PmcUsec`；`pmcbear` → `PmcBear`；
/// `bosstagilla` → `BossTagilla`；其余同理。
///
/// ## 槽位不在主循环里（别被 `_excludedEquipmentSlots` 误导）
/// `Headwear` / `FaceCover` / `ArmorVest` / `TacticalVest` / `Earpiece` 都在 `_excludedEquipmentSlots`，
/// 但它们随后被**逐个显式** `GenerateEquipment(...)` 调用（`CustomBotInventoryGenerator:203/216/229/…`），
/// 仍然会过 `SpawnChances.EquipmentChances[槽位]` 这道概率门 ⇒ **池子和概率都要写**。
/// </summary>
public static class PbsCompat
{
    private const string ModConfigTypeName = "ProgressiveBotSystem.Globals.ModConfig";

    private static Type _modConfigType;
    private static bool _probed;
    private static ISptLogger<EventRotatorServer> _log;

    /// <summary>★ v2.6.53：已警告过的"PBS 里没有该角色"集合 —— 每个角色只刷一次（原来每次启动 184 条 ×7）。</summary>
    private static readonly HashSet<string> _warnedMissingRole = new(StringComparer.OrdinalIgnoreCase);

    public static bool Detected { get; private set; }

    /// <summary>PBS 的 mod 目录（**只读**用途）。</summary>
    public static string ModDir { get; private set; }

    public static void Init(ISptLogger<EventRotatorServer> log) { _log = log; }

    /// <summary>探测 PBS 是否在场（只读）。</summary>
    public static bool Probe()
    {
        if (_probed) return Detected;
        _probed = true;
        try
        {
            _modConfigType = AccessTools.TypeByName(ModConfigTypeName);
            if (_modConfigType == null) { Detected = false; return false; }
            try
            {
                var f = _modConfigType.GetField("_modPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                ModDir = f?.GetValue(null) as string;
            }
            catch { }
            // 能拿到 _dataLoader 才算真的可用
            Detected = DataLoader() != null;
            if (Detected)
                _log?.Info($"[EventRotator] PBS 在场 → 装备走 PBS 运行期池（只写内存、不改它任何文件）dir={ModDir ?? "?"}");
            else if (_modConfigType != null)
                _log?.Warning("[EventRotator] PBS 类型在，但拿不到 ModConfig._dataLoader（可能尚未初始化）");
            return Detected;
        }
        catch (Exception ex)
        {
            _log?.Warning($"[EventRotator] PBS 探测失败: {ex.Message}");
            Detected = false;
            return false;
        }
    }

    public static bool IsPresent { get { try { return Probe(); } catch { return false; } } }

    /// <summary>★ v2.6.39：`eventGearKeepBossIconic` 跳过提示只打一次（键 = `bot|槽`）。</summary>
    private static readonly HashSet<string> _iconicKeptWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>★ v2.6.73【G7】`ModConfig._dataLoader` 的 `FieldInfo` 只解析一次（原来每次调用都反射找字段）。
    /// 与 `_modConfigType` 同步（`Probe()` 只认一次类型，这里仍保守判 `DeclaringType`）。</summary>
    private static FieldInfo _dataLoaderField;

    private static object DataLoader()
    {
        try
        {
            var t = _modConfigType;
            if (t == null) return null;
            var f = _dataLoaderField;
            if (f == null || f.DeclaringType != t)
            {
                f = t.GetField("_dataLoader", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _dataLoaderField = f;
            }
            return f?.GetValue(null);
        }
        catch { return null; }
    }

    /// <summary>当前 PBS 供生成使用的 `AllTierDataDirty`（拿不到返回 null）。</summary>
    private static object Dirty()
    {
        var dl = DataLoader();
        return dl?.GetType().GetProperty("AllTierDataDirty", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dl);
    }

    /// <summary>枚举名 → 枚举值（`ApbsEquipmentSlots`）。</summary>
    private static object SlotEnum(Type slotEnumType, string name)
    {
        try { return Enum.Parse(slotEnumType, name, true); } catch { return null; }
    }

    /// <summary>MongoId 构造（PBS 池子的键类型）。</summary>
    private static object Mongo(string s, Type mongoType)
    {
        try
        {
            var c = mongoType.GetConstructor(new[] { typeof(string) });
            if (c != null) return c.Invoke(new object[] { s });
            var p = mongoType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (p != null) return p.Invoke(null, new object[] { s });
            return Convert.ChangeType(s, mongoType);
        }
        catch { return null; }
    }

    /// <summary>
    /// ★★★ 把某个事件的官方装备表（`eventGear[key]`）与概率写进 **PBS 的运行期池子**。
    ///
    /// `table` 形如 `{ "assault": { "Headwear": { "&lt;tpl&gt;": 1500 }, "FaceCover": {…} }, … }`
    /// （与 `ExtraDimensions.ReadSlotTable` 读出来的官方结构一致）。
    ///
    /// 角色名会按 PBS 的规则映射（`assault`→`Scav`、`usec`→`PmcUsec`…），并对**全部 Tier 0..7** 生效
    /// （因为不同 role 会落到不同 tier：如 boss 系列固定用 Tier≥4、SCAV 在不开启配件分级时用 Tier1）。
    /// </summary>
    public static int ApplyEventGear(Dictionary<string, Dictionary<string, Dictionary<string, double>>> table,
                                     Action<Action> addRestore = null,
                                     bool keepBossIconic = false)
    {
        if (table == null || table.Count == 0) return 0;
        if (!Probe()) return 0;
        int written = 0;
        try
        {
            var allData = Dirty();
            if (allData == null) { _log?.Warning("[EventRotator][PBS] 拿不到 AllTierDataDirty ⇒ 跳过（PBS 可能尚未加载）"); return 0; }

            var tiers = allData.GetType().GetProperty("Tiers", BindingFlags.Public | BindingFlags.Instance)?.GetValue(allData) as IDictionary;
            if (tiers == null) { _log?.Warning("[EventRotator][PBS] AllTierDataDirty.Tiers 拿不到"); return 0; }

            var tierKeys = new List<int>();
            foreach (DictionaryEntry de in tiers) { try { tierKeys.Add(Convert.ToInt32(de.Key)); } catch { } }
            tierKeys.Sort();
            if (tierKeys.Count == 0) { _log?.Warning("[EventRotator][PBS] Tiers 是空的"); return 0; }

            // ── 角色名映射（PBS 用 roleLowercase；这里把我们的官方表 key 归一化）──
            var roleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["assault"] = "Scav", ["marksman"] = "Scav", ["cursedassault"] = "Scav",
                ["usec"] = "PmcUsec", ["pmcusec"] = "PmcUsec",
                ["bear"] = "PmcBear", ["pmcbear"] = "PmcBear",
                ["bossTagilla"] = "BossTagilla", ["bosstagilla"] = "BossTagilla",
                // ★★★ v2.6.53【P1-5 修复】PBS 的 EquipmentTierData 里**没有**这 7 个随从角色
                //   （日志实证每个角色 184 条 `EquipmentTierData 里没有角色名 'followersanitar'…`）
                //   ⇒ 它们的官方节日装备（圣诞帽/南瓜头…）在 PBS 接管下**从来没出现过**。
                //   修法：就近映射到**同族首领**的 tier 条目（随从本来就与首领穿同一套节日装备；
                //   若首领条目也不存在，则退化为"每个角色只警告一次"，不再刷屏）。
                ["followersanitar"] = "BossSanitar",
                ["followerkojaniy"] = "BossKojaniy",
                ["followerbully"] = "BossBully",
                ["followerboar"] = "BossBoar",
                ["followergluharassault"] = "BossGluhar",
                ["followergluharscout"] = "BossGluhar",
                ["followergluharsecurity"] = "BossGluhar",
                ["followergluharsnipe"] = "BossGluhar",
                ["followerkolontayassault"] = "BossKolontay",
                ["followerkolontaysecurity"] = "BossKolontay",
                ["followertagilla"] = "BossTagilla",
                ["followerbigpipe"] = "BossKnight",
                ["followerbirdeye"] = "BossKnight",
                ["followerzryachiy"] = "BossZryachiy",
            };

            foreach (var tierK in tierKeys)
            {
                var tierInner = tiers[tierK];
                if (tierInner == null) continue;
                var equipData = tierInner.GetType().GetProperty("EquipmentData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tierInner);
                if (equipData == null) continue;
                var equipT = equipData.GetType();

                // 概率表（同一 tier 的 ChancesData）
                var chancesData = tierInner.GetType().GetProperty("ChancesData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tierInner);

                foreach (var botKv in table)
                {
                    var propName = roleMap.TryGetValue(botKv.Key, out var mapped) ? mapped : botKv.Key;
                    if (propName == "Scav" && string.Equals(botKv.Key, "marksman", StringComparison.OrdinalIgnoreCase)) propName = "Scav";
                    var botProp = equipT.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (botProp == null)
                    {
                        // ★ v2.6.53：**每个角色只警告一次**（原来每次启动刷 184×7 条）
                        if (_warnedMissingRole.Add(botKv.Key))
                            _log?.Warning($"[EventRotator][PBS] EquipmentTierData 里没有角色名 '{propName}'"
                                        + $"（来自 '{botKv.Key}'）⇒ 该角色的官方节日装备在 PBS 接管下**不会出现**"
                                        + "（已就近映射同族首领仍未命中；此警告只打一次）");
                        continue;
                    }
                    var botObj = botProp.GetValue(equipData);
                    if (botObj == null) continue;

                    var equipDict = botObj.GetType().GetProperty("Equipment", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botObj) as IDictionary;
                    if (equipDict == null) continue;
                    var slotEnumType = equipDict.GetType().IsGenericType ? equipDict.GetType().GetGenericArguments()[0] : null;
                    var poolType = equipDict.GetType().IsGenericType ? equipDict.GetType().GetGenericArguments()[1] : null;
                    if (slotEnumType == null || poolType == null || !slotEnumType.IsEnum) { _log?.Warning("[EventRotator][PBS] 槽位键不是枚举"); continue; }
                    var mongoType = poolType.IsGenericType ? poolType.GetGenericArguments()[0] : null;
                    if (mongoType == null) continue;

                    // 概率字典：ChancesData.<Role>.Chances.EquipmentChances（PBS 自己那份）
                    IDictionary chanceDict = null;
                    if (chancesData != null)
                    {
                        var botChances = chancesData.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(chancesData);
                        var chObj = botChances?.GetType().GetProperty("Chances", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botChances);
                        chanceDict = chObj?.GetType().GetProperty("EquipmentChances", BindingFlags.Public | BindingFlags.Instance)?.GetValue(chObj) as IDictionary;
                    }

                    foreach (var slotKv in botKv.Value)
                    {
                        // ★★★ v2.6.39【opt-in · 用户裁决】与 `ExtraDimensions.ApplyBotEquipment` 同款守卫：
                        //   首领/随从（boss*/follower*）的 FaceCover/Headwear 槽整槽跳过（保住标志性装备）。
                        //   默认 false ⇒ 与官方逐字一致（往池子里直接赋值、不清理原条目）。
                        if (EventGearSlotFilter.Skip(botKv.Key, slotKv.Key, keepBossIconic))
                        {
                            if (_iconicKeptWarned.Add(botKv.Key + "|" + slotKv.Key))
                                _log?.Info($"[EventRotator][PBS] eventGearKeepBossIconic=true ⇒ 跳过 '{botKv.Key}.{slotKv.Key}'"
                                         + "（保住首领标志性装备；非官方 opt-in，默认不开）");
                            continue;
                        }
                        var slot = SlotEnum(slotEnumType, slotKv.Key);
                        if (slot == null) { _log?.Warning($"[EventRotator][PBS] ApbsEquipmentSlots 里没有 '{slotKv.Key}'"); continue; }
                        if (!equipDict.Contains(slot))
                        {
                            _log?.Warning($"[EventRotator][PBS] {propName}.Equipment 没有槽位 '{slotKv.Key}'（Tier{tierK}）");
                            continue;
                        }
                        var pool = equipDict[slot] as IDictionary;
                        if (pool == null) { _log?.Warning($"[EventRotator][PBS] {propName}.{slotKv.Key} 的池不是字典"); continue; }

                        foreach (var itemKv in slotKv.Value)
                        {
                            var mk = Mongo(itemKv.Key, mongoType);
                            if (mk == null) continue;
                            var newVal = Convert.ToDouble(itemKv.Value);
                            // 官方语义：**直接赋值覆盖**（含原先存在但权重为 0 的占位项）
                            if (pool.Contains(mk))
                            {
                                var capP = pool; var capK = mk; var capOld = pool[mk];
                                addRestore?.Invoke(() => { try { capP[capK] = capOld; } catch { } });
                            }
                            else
                            {
                                var capP = pool; var capK = mk;
                                addRestore?.Invoke(() => { try { if (capP.Contains(capK)) capP.Remove(capK); } catch { } });
                            }
                            pool[mk] = newVal;
                            written++;
                        }

                        // ★ 概率门：官方 `Math.Max(现值, 75)`。PBS 的 `_slotsToCheck` 只含 Pockets/SecuredContainer，
                        //   所以 Headwear/FaceCover/Scabbard 全部要过它自己那份概率 ⇒ 不抬起来就抽不到。
                        if (chanceDict != null)
                        {
                            var had = chanceDict.Contains(slotKv.Key);
                            double oldD = 0;
                            try { if (had) oldD = Convert.ToDouble(chanceDict[slotKv.Key]); } catch { }
                            double want = Math.Max(oldD, 75.0);
                            if (want > oldD)
                            {
                                var capC = chanceDict; var capK = slotKv.Key; var capHad = had; var capOld = had ? chanceDict[slotKv.Key] : null;
                                addRestore?.Invoke(() => { try { if (capHad) capC[capK] = capOld; else capC.Remove(capK); } catch { } });
                                chanceDict[slotKv.Key] = want;
                            }
                        }
                    }
                }
            }

            if (written > 0)
            {
                _log?.Warning($"[EventRotator][PBS] ★已把官方装备写进 PBS 运行期池：{written} 条 / {tierKeys.Count} 个 Tier"
                            + "（只写内存，未改 PBS 任何文件；可还原）");
                // ★ 立刻**回读**池子自证（只看关键几项，不刷屏）
                try
                {
                    var probes = new (string Role, string Slot, string Tpl, double Want)[]
                    {
                        ("Scav",      "Headwear",  "59ef13ca86f77445fd0e2483", 1500),
                        ("PmcBear",   "Headwear",  "59ef13ca86f77445fd0e2483", 25),
                        ("PmcUsec",   "Headwear",  "59ef13ca86f77445fd0e2483", 25),
                        ("BossTagilla","Scabbard", "63495c500c297e20065a08b1", 10),
                    };
                    var sb = new List<string>();
                    foreach (var p in probes)
                    {
                        var got = ReadPoolValue(p.Role, 1, p.Slot, p.Tpl);
                        sb.Add($"{p.Role}.{p.Slot}={got?.ToString() ?? "<缺> "}(期望{p.Want}){(got.HasValue && Math.Abs(got.Value - p.Want) < 0.001 ? "✔" : "✘")}");
                    }
                    _log?.Warning("[EventRotator][PBS] 回读自证（Tier1）：" + string.Join(" ", sb));

                    // ★ 概率门自证：PBS 的 ChancesData（不抬起来池子里有也抽不到）
                    var cbs = new List<string>();
                    foreach (var p in new (string Role, string Slot)[] { ("Scav", "Headwear"), ("Scav", "FaceCover"), ("PmcBear", "FaceCover"), ("BossTagilla", "Scabbard") })
                    {
                        var cv = ReadChanceValue(p.Role, 1, p.Slot);
                        cbs.Add($"{p.Role}.{p.Slot}={(cv.HasValue ? cv.Value.ToString() : "<缺>")}{(cv.HasValue && cv.Value >= 75 ? "✔" : "✘")}");
                    }
                    _log?.Warning("[EventRotator][PBS] 概率门自证（Tier1，官方下界 75）：" + string.Join(" ", cbs));
                }
                catch { }
            }
            return written;
        }
        catch (Exception ex)
        {
            _log?.Warning($"[EventRotator][PBS] 写运行期池失败: {ex.Message}");
            return written;
        }
    }

    /// <summary>只读：回读 PBS 运行期**概率**（`ChancesData.&lt;Role&gt;.Chances.EquipmentChances[slot]`，string 键）。</summary>
    public static double? ReadChanceValue(string botPropName, int tier, string slotName)
    {
        try
        {
            var allData = Dirty();
            var tiers = allData?.GetType().GetProperty("Tiers")?.GetValue(allData) as IDictionary;
            if (tiers == null || !tiers.Contains(tier)) return null;
            var cd = tiers[tier]?.GetType().GetProperty("ChancesData")?.GetValue(tiers[tier]);
            var botChances = cd?.GetType().GetProperty(botPropName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(cd);
            var chObj = botChances?.GetType().GetProperty("Chances", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botChances);
            var dict = chObj?.GetType().GetProperty("EquipmentChances", BindingFlags.Public | BindingFlags.Instance)?.GetValue(chObj) as IDictionary;
            if (dict == null || !dict.Contains(slotName)) return null;
            return Convert.ToDouble(dict[slotName]);
        }
        catch { return null; }
    }

    /// <summary>只读：回读 PBS 运行期池里某角色/某槽/某物品的当前权重（拿不到返回 null）。</summary>
    public static double? ReadPoolValue(string botPropName, int tier, string slotName, string tpl)
    {
        try
        {
            var allData = Dirty();
            var tiers = allData?.GetType().GetProperty("Tiers")?.GetValue(allData) as IDictionary;
            if (tiers == null || !tiers.Contains(tier)) return null;
            var equipData = tiers[tier]?.GetType().GetProperty("EquipmentData")?.GetValue(tiers[tier]);
            var botObj = equipData?.GetType().GetProperty(botPropName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(equipData);
            var equipDict = botObj?.GetType().GetProperty("Equipment")?.GetValue(botObj) as IDictionary;
            if (equipDict == null) return null;
            var slot = SlotEnum(equipDict.GetType().GetGenericArguments()[0], slotName);
            if (slot == null || !equipDict.Contains(slot)) return null;
            var pool = equipDict[slot] as IDictionary;
            if (pool == null) return null;
            var mongoType = pool.GetType().GetGenericArguments()[0];
            var mk = Mongo(tpl, mongoType);
            if (mk == null || !pool.Contains(mk)) return null;
            return Convert.ToDouble(pool[mk]);
        }
        catch { return null; }
    }

    /// <summary>只读诊断：报一下某个角色在某 Tier 的装备槽池子大小（用于验收）。</summary>
    public static string DumpPool(string botPropName, int tier, string slotName)
    {
        if (!Probe()) return "PBS 不在场";
        try
        {
            var allData = Dirty();
            var tiers = allData?.GetType().GetProperty("Tiers")?.GetValue(allData) as IDictionary;
            if (tiers == null || !tiers.Contains(tier)) return "拿不到 Tiers";
            var tierInner = tiers[tier];
            var equipData = tierInner?.GetType().GetProperty("EquipmentData")?.GetValue(tierInner);
            var botObj = equipData?.GetType().GetProperty(botPropName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(equipData);
            var equipDict = botObj?.GetType().GetProperty("Equipment")?.GetValue(botObj) as IDictionary;
            if (equipDict == null) return "拿不到 Equipment";
            var slotEnumType = equipDict.GetType().GetGenericArguments()[0];
            var slot = SlotEnum(slotEnumType, slotName);
            if (slot == null || !equipDict.Contains(slot)) return "无此槽位";
            var pool = equipDict[slot] as IDictionary;
            return $"{botPropName}.Tier{tier}.{slotName} 池子 {pool?.Count ?? 0} 件";
        }
        catch (Exception ex) { return "异常: " + ex.Message; }
    }
}
