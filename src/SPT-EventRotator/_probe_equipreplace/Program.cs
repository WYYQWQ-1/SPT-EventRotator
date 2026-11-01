// ★ 离线探针：botEquipmentReplace（casus_belli 劫掠者 6 款彩绘 CQCM 面具 + 武器/护甲/插板池）
//
// 只**读**实机（D:\PureEFTv4.1）的 bots/types/arenafighterevent.json + templates/items.json
// 与本工作区的 events.json，用 SPT 的真实模型类型在内存里复现 bot 模板，然后反射调用工作区
// Release 构建出的 `EventRotator.BotEquipmentReplaceDimension.Apply/Restore/OnBotGeneratingPrefix`。
// 不写实机、不启动服务端。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using IOPath = System.IO.Path;

sealed class TableShim { public Dictionary<string, BotType> Types { get; set; } }
sealed class DetailsShim { public string Location { get; set; } }

static class Probe
{
    const string SPT = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    const string Emr = "5c0e53c886f7747fa54205c7";   // 6B13 (EMR)：items.json 的槽名是 front_plate/back_plate **小写**
    const string Korund = "5f5f41476bdad616ad46d631";

    // 本机 arenafighterevent.json 原文里的 6 款彩绘面具（要保留）
    static readonly string[] Expected6 =
    {
        "67a5c657782ce4655104db16", // 亡灵节 El Día de Muertos
        "67a5c5df782ce4655104db14", // 战疤 Scars
        "67a5c5f37f52620c5b05b4d6", // 标靶 Target
        "67a5c61c7f52620c5b05b4d8", // 恶魔 Demon
        "67a5c6068fcd9fb73f0752cf", // 骷髅 Skull
        "68a9a15d73d52d47830759c9", // 恶魔獠牙 Demonic Face
    };

    // 实机 arenafighterevent.json 里**必须被替换掉**的 4 款巴拉克拉瓦
    static readonly string[] Original4Balaclavas =
    {
        "5ab8f39486f7745cd93a1cca", // Cold Fear infrared balaclava
        "5ab8f4ff86f77431c60d91ba", // Ghost balaclava
        "5b432f3d5acfc4704b4a1dfb", // Momex balaclava
        "5fd8d28367cb5e077335170f", // Smoke balaclava
    };

    // ★ 本次需求新增的三档武器（权重）
    static readonly Dictionary<string, int> ExpectedWeapons = new()
    {
        ["5c07c60e0db834002330051f"] = 4,   // ADAR 2-15
        ["643ea5b23db6f9f57107d9fd"] = 3,   // Tokarev SVT-40
        ["606587252535c57a13424cfd"] = 2,   // CMMG Mk47 Mutant
        ["5b0bbe4e5acfc40dc528a72d"] = 1,   // DS Arms SA58
    };

    // ★ 本次需求新增的 5 件护甲（权重 1~2）
    static readonly Dictionary<string, int> ExpectedArmor = new()
    {
        ["5c0e51be86f774598e797894"] = 2,   // 6B13 Flora
        ["5c0e57ba86f7747fa141986d"] = 2,   // 6B23-2
        ["5c0e655586f774045612eeb2"] = 1,   // Trooper
        ["5ca2151486f774244a3b8d30"] = 2,   // Redut-M
        [Korund] = 1,                       // Korund-VM
    };

    // ★ 本次需求新增的 mods 插板池（逐槽，全部 4/5 级、且都在该槽 filter 内）
    static readonly Dictionary<string, Dictionary<string, string[]>> ExpectedMods = new()
    {
        ["5c0e51be86f774598e797894"] = new()
        {
            ["Front_plate"] = new[] { "656f603f94b480b8a500c0d6", "656f611f94b480b8a500c0db" },
            ["Back_plate"] = new[] { "656efaf54772930db4031ff5", "656efd66034e8e01c407f35c" },
        },
        ["5c0e57ba86f7747fa141986d"] = new()
        {
            ["Front_plate"] = new[] { "656f664200d62bcd2e024077", "656f611f94b480b8a500c0db", "656f603f94b480b8a500c0d6" },
            ["Back_plate"] = new[] { "657b22485f444d6dff0c6c2f", "657b2797c3dbcb01d60c35ea" },
        },
        ["5c0e655586f774045612eeb2"] = new()
        {
            ["Front_plate"] = new[] { "656fa0fb498d1b7e3e071d9c", "656fa25e94b480b8a500c0e0", "656fad8c498d1b7e3e071da0", "656fa8d700d62bcd2e024084" },
            ["Back_plate"] = new[] { "656fa0fb498d1b7e3e071d9c", "656fa25e94b480b8a500c0e0", "656fad8c498d1b7e3e071da0", "656fa8d700d62bcd2e024084" },
        },
        ["5ca2151486f774244a3b8d30"] = new()
        {
            ["Front_plate"] = new[] { "656fa0fb498d1b7e3e071d9c", "656fa25e94b480b8a500c0e0", "65573fa5655447403702a816", "656fa53d94b480b8a500c0e4" },
            ["Back_plate"] = new[] { "656fa0fb498d1b7e3e071d9c", "656fa25e94b480b8a500c0e0", "65573fa5655447403702a816", "656fa53d94b480b8a500c0e4" },
            ["Left_side_plate"] = new[] { "6557458f83942d705f0c4962" },
            ["Right_side_plate"] = new[] { "6557458f83942d705f0c4962" },
        },
        [Korund] = new()
        {
            ["Front_plate"] = new[] { "656f664200d62bcd2e024077" },
            ["Back_plate"] = new[] { "657b22485f444d6dff0c6c2f", "657b2797c3dbcb01d60c35ea" },
            ["Left_side_plate"] = new[] { "654a4f8bc721968a4404ef18" },
            ["Right_side_plate"] = new[] { "654a4f8bc721968a4404ef18" },
        },
    };

    static int pass, fail;
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"  [PASS] {name}" + (detail.Length > 0 ? "  | " + detail : "")); }
        else { fail++; Console.WriteLine($"  [FAIL] {name}  | {detail}"); }
    }

    static string Inv(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>规范化逐字节串（顺序 = 字典插入顺序；用于「还原后与原始逐字节相同」）。</summary>
    static string PoolText(Dictionary<MongoId, double> p) =>
        "{" + string.Join(",", p.Select(kv =>
            JsonSerializer.Serialize(kv.Key.ToString()) + ":" + Inv(kv.Value))) + "}";

    /// <summary>键排序后的字符串字典（顺序稳定，避免"换了插入顺序但内容相同"被误判）。</summary>
    static string DictText(Dictionary<string, double> d) =>
        "{" + string.Join(",", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            JsonSerializer.Serialize(kv.Key) + ":" + Inv(kv.Value))) + "}";

    /// <summary>Mods 全量规范化（护甲 tpl → 槽位 → 排序后的插板集合）。</summary>
    static string ModsText(Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>> m) =>
        "{" + string.Join(",", m.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal).Select(kv =>
            JsonSerializer.Serialize(kv.Key.ToString()) + ":{" +
            string.Join(",", kv.Value.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s =>
                JsonSerializer.Serialize(s.Key) + ":[" +
                string.Join(",", s.Value.Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => JsonSerializer.Serialize(x))) + "]")) + "}")) + "}";

    static EquipmentSlots SlotEnum(string name) => name switch
    {
        "FaceCover" => EquipmentSlots.FaceCover,
        "FirstPrimaryWeapon" => EquipmentSlots.FirstPrimaryWeapon,
        "ArmorVest" => EquipmentSlots.ArmorVest,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    static object FindKey(IDictionary d, string s)
    {
        foreach (var k in d.Keys)
            if (string.Equals(k?.ToString(), s, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    /// <summary>取运行期某个护甲某槽的 HashSet（大小写不敏感）；没有返回 null。</summary>
    static HashSet<MongoId> SetAt(Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>> mods, string vest, string slot)
    {
        if (!mods.TryGetValue(new MongoId(vest), out var vd)) return null;
        var key = vd.Keys.FirstOrDefault(k => k.Equals(slot, StringComparison.OrdinalIgnoreCase));
        return key == null ? null : vd[key];
    }

    /// <summary>items.json：该物品该槽的 filter（槽名大小写不敏感）；没有返回 null。</summary>
    static HashSet<string> SlotFilter(JsonElement itemsRoot, string itemTpl, string slotName)
    {
        if (!itemsRoot.TryGetProperty(itemTpl, out var it)) return null;
        if (!it.TryGetProperty("_props", out var pr) || !pr.TryGetProperty("Slots", out var slots)) return null;
        foreach (var s in slots.EnumerateArray())
        {
            if (!s.TryGetProperty("_name", out var n)) continue;
            if (!string.Equals(n.GetString(), slotName, StringComparison.OrdinalIgnoreCase)) continue;
            var f = s.GetProperty("_props").GetProperty("filters")[0].GetProperty("Filter");
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in f.EnumerateArray()) set.Add(x.GetString());
            return set;
        }
        return null;
    }

    static int ArmorClass(JsonElement itemsRoot, string tpl)
    {
        if (!itemsRoot.TryGetProperty(tpl, out var it)) return -1;
        var v = it.GetProperty("_props").GetProperty("armorClass");
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return int.TryParse(s, out var n) ? n : -1;
    }

    static string ItemName(JsonElement itemsRoot, string tpl) =>
        itemsRoot.TryGetProperty(tpl, out var it) && it.TryGetProperty("_props", out var p)
            && p.TryGetProperty("Name", out var n) ? n.GetString() : "??";

    static string BagText(Dictionary<string, Dictionary<string, string[]>> d) =>
        "{" + string.Join(",", d.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv =>
            JsonSerializer.Serialize(kv.Key) + ":{" + string.Join(",", kv.Value.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s =>
                JsonSerializer.Serialize(s.Key) + ":[" + string.Join(",", s.Value.OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => JsonSerializer.Serialize(x))) + "]")) + "}")) + "}";

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var here = AppContext.BaseDirectory;
        var modDir = IOPath.GetFullPath(IOPath.Combine(here, "..", "..", "..", ".."));
        var modDll = IOPath.Combine(modDir, "bin", "Release", "net10.0", "SPT-EventRotator.dll");
        var botJson = IOPath.Combine(SPT, "SPT_Data", "database", "bots", "types", "arenafighterevent.json");
        var itemsJson = IOPath.Combine(SPT, "SPT_Data", "database", "templates", "items.json");
        var eventsJson = IOPath.Combine(modDir, "events.json");

        Console.WriteLine("════════ botEquipmentReplace 离线探针（面具 + 武器/护甲/插板池）════════");
        Console.WriteLine("mod 构建  : " + modDll);
        Console.WriteLine("实机 bot  : " + botJson);
        Console.WriteLine("实机 items: " + itemsJson);
        Console.WriteLine("events    : " + eventsJson);
        Console.WriteLine();

        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var n = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in new[] { SPT, modDir, here })
            {
                var p = IOPath.Combine(d, n);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };

        Check("Release DLL 存在", File.Exists(modDll), modDll);
        Check("实机 arenafighterevent.json 存在（只读）", File.Exists(botJson), botJson);
        Check("实机 items.json 存在（只读）", File.Exists(itemsJson), itemsJson);
        Check("工作区 events.json 存在", File.Exists(eventsJson), eventsJson);
        if (!File.Exists(modDll) || !File.Exists(botJson) || !File.Exists(itemsJson) || !File.Exists(eventsJson)) return 1;

        using var botDoc = JsonDocument.Parse(File.ReadAllText(botJson));
        using var itemsDoc = JsonDocument.Parse(File.ReadAllText(itemsJson));
        var botRoot = botDoc.RootElement;
        var itemsRoot = itemsDoc.RootElement;

        // ═════════ 1. 用实机数据复现 bot 模板（原池 / 原概率 / 原 Mods）═════════
        var invEl = botRoot.GetProperty("inventory");
        var eqEl = invEl.GetProperty("equipment");
        var pools = new Dictionary<string, Dictionary<MongoId, double>>();
        foreach (var slot in new[] { "FaceCover", "FirstPrimaryWeapon", "ArmorVest" })
        {
            var d = new Dictionary<MongoId, double>();
            foreach (var p in eqEl.GetProperty(slot).EnumerateObject()) d[new MongoId(p.Name)] = p.Value.GetDouble();
            pools[slot] = d;
            Console.WriteLine($"实机原文 {slot,-18}: {d.Count} 项");
        }
        var origCounts = pools.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var origFaceKeys = pools["FaceCover"].Keys.Select(k => k.ToString()).ToList();

        var chances = new Dictionary<string, double>();
        foreach (var p in botRoot.GetProperty("chances").GetProperty("equipment").EnumerateObject())
            chances[p.Name] = p.Value.GetDouble();
        var modChances = new Dictionary<string, double>();
        foreach (var p in botRoot.GetProperty("chances").GetProperty("equipmentMods").EnumerateObject())
            modChances[p.Name] = p.Value.GetDouble();
        Console.WriteLine("实机原文 chances       : " + string.Join(" ", new[] { "FaceCover", "FirstPrimaryWeapon", "ArmorVest" }
            .Select(s => $"{s}={chances.GetValueOrDefault(s)}"))
            + $"；equipmentMods: front/back={modChances.GetValueOrDefault("front_plate")}/{modChances.GetValueOrDefault("back_plate")}, "
            + $"left/right={modChances.GetValueOrDefault("left_side_plate")}/{modChances.GetValueOrDefault("right_side_plate")}");

        var botMods = new Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>();
        var modsEl = invEl.GetProperty("mods");
        var presentVests = new List<string>();
        foreach (var vest in ExpectedMods.Keys)
        {
            if (!modsEl.TryGetProperty(vest, out var vEl)) continue;
            presentVests.Add(vest);
            var d = new Dictionary<string, HashSet<MongoId>>(StringComparer.Ordinal);
            foreach (var s in vEl.EnumerateObject())
            {
                var hs = new HashSet<MongoId>();
                foreach (var t in s.Value.EnumerateArray()) hs.Add(new MongoId(t.GetString()));
                d[s.Name] = hs;
            }
            botMods[new MongoId(vest)] = d;
        }
        Console.WriteLine($"实机原文 mods          : {ExpectedMods.Count} 件护甲里存在 {presentVests.Count} 件"
                        + $"（缺={string.Join(",", ExpectedMods.Keys.Except(presentVests))} ⇒ 维度会新建）");
        Console.WriteLine();

        var botType = new BotType
        {
            BotInventory = new BotTypeInventory
            {
                Equipment = new Dictionary<EquipmentSlots, Dictionary<MongoId, double>>
                {
                    [EquipmentSlots.FaceCover] = pools["FaceCover"],
                    [EquipmentSlots.FirstPrimaryWeapon] = pools["FirstPrimaryWeapon"],
                    [EquipmentSlots.ArmorVest] = pools["ArmorVest"],
                },
                Mods = botMods,
            },
            BotChances = new Chances
            {
                EquipmentChances = chances,
                EquipmentModsChances = modChances,
            },
        };
        // 真实 BotTable.Types 的键是**小写** role（见 CustomBotTypes 注释「小写键 = role」）；
        // 配置里写的是 arenaFighterEvent ⇒ 顺便验证"大小写不敏感"匹配。
        var shim = new TableShim { Types = new Dictionary<string, BotType> { ["arenafighterevent"] = botType } };

        var poolBefore = pools.ToDictionary(kv => kv.Key, kv => PoolText(kv.Value));
        var chanceBefore = DictText(chances);
        var modChanceBefore = DictText(modChances);
        var modsBefore = ModsText(botMods);

        // 原文里被我们替换的那些 HashSet 的**实例**（还原时必须写回同一实例）
        var origSetRefs = new Dictionary<(string Vest, string Slot), HashSet<MongoId>>();
        foreach (var vest in presentVests)
            foreach (var slot in ExpectedMods[vest].Keys)
            {
                var set = SetAt(botMods, vest, slot);
                if (set != null) origSetRefs[(vest, slot)] = set;
            }

        // ═════════ 2. 反射载入 mod + EventDef ═════════
        var asm = Assembly.LoadFrom(modDll);
        var tEv = asm.GetType("EventRotator.EventDef", true);
        var tDim = asm.GetType("EventRotator.BotEquipmentReplaceDimension", true);
        const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static;
        const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        var apply = tDim.GetMethod("Apply", PubStatic);
        var restore = tDim.GetMethod("Restore", PubStatic);
        var prefix = tDim.GetMethod("OnBotGeneratingPrefix", PubStatic);
        Check("反射找到 Apply/Restore/OnBotGeneratingPrefix",
              apply != null && restore != null && prefix != null,
              $"Apply={apply != null} Restore={restore != null} Prefix={prefix != null}");
        if (apply == null || restore == null || prefix == null) return 1;

        var msgs = new List<string>();
        tDim.GetField("BotTable", AnyStatic)?.SetValue(null, shim);
        tDim.GetField("Log", AnyStatic)?.SetValue(null, new Action<string>(msgs.Add));
        tDim.GetField("Warn", AnyStatic)?.SetValue(null, new Action<string>(msgs.Add));

        // ═════════ 3. events.json：casus_belli 反序列化 ═════════
        object ev = null;
        using (var ed = JsonDocument.Parse(File.ReadAllText(eventsJson)))
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var el in ed.RootElement.EnumerateArray())
                if (el.TryGetProperty("id", out var idEl) && idEl.GetString() == "casus_belli")
                {
                    ev = JsonSerializer.Deserialize(el.GetRawText(), tEv, opts);
                    break;
                }
        }
        Check("events.json 找到并反序列化 casus_belli", ev != null);
        var pSpec = tEv.GetProperty("BotEquipmentReplace");
        var spec = ev == null ? null : pSpec.GetValue(ev);
        Check("EventDef.BotEquipmentReplace 反序列化成功（字段类型正确）", spec != null);
        if (spec == null) return 1;

        var specType = spec.GetType();
        var maps = (List<string>)specType.GetProperty("Maps").GetValue(spec);
        Check("maps = bigmap/woods/interchange/lighthouse/shoreline",
              maps != null && maps.SequenceEqual(new[] { "bigmap", "woods", "interchange", "lighthouse", "shoreline" }),
              string.Join(",", maps ?? new List<string>()));

        var roles = (IDictionary)specType.GetProperty("Roles").GetValue(spec);
        var role = roles["arenaFighterEvent"];
        Check("Roles 里有 arenaFighterEvent（原大小写）", role != null);
        if (role == null) return 1;
        var roleType = role.GetType();
        var slotsCfg = (IDictionary)roleType.GetProperty("Slots").GetValue(role);
        var modsCfg = (IDictionary)roleType.GetProperty("Mods").GetValue(role);
        Check("角色规格 = 3 个装备槽 + mods（共 4 个键）",
              slotsCfg != null && modsCfg != null && slotsCfg.Count == 3 && modsCfg.Count == 5,
              $"Slots={slotsCfg?.Count} Mods={modsCfg?.Count}；Slots=[{string.Join(",", slotsCfg.Keys.Cast<object>())}]");

        static Dictionary<string, double> CfgPool(IDictionary d)
        {
            var r = new Dictionary<string, double>();
            foreach (var k in d.Keys) r[k.ToString()] = Convert.ToDouble(d[k]);
            return r;
        }

        var cfgFace = CfgPool((IDictionary)slotsCfg["FaceCover"]);
        Check("FaceCover 池 = 恰好 6 个 tpl（保留彩绘面具）",
              cfgFace.Count == 6 && cfgFace.Keys.OrderBy(x => x).SequenceEqual(Expected6.OrderBy(x => x)),
              string.Join(",", cfgFace.Keys));

        var cfgWeapons = CfgPool((IDictionary)slotsCfg["FirstPrimaryWeapon"]);
        Check("FirstPrimaryWeapon 池 = ADAR/SVT-40/Mk47/SA58（权重 4/3/2/1）",
              cfgWeapons.Count == 4 && cfgWeapons.All(kv => ExpectedWeapons.TryGetValue(kv.Key, out var w) && Math.Abs(w - kv.Value) < 1e-9),
              string.Join(",", cfgWeapons.Select(kv => $"{kv.Key}:{Inv(kv.Value)}")));

        var cfgArmor = CfgPool((IDictionary)slotsCfg["ArmorVest"]);
        Check("ArmorVest 池 = 5 件（6B13 Flora/6B23-2/Trooper/Redut-M/Korund-VM，权重 1~2）",
              cfgArmor.Count == 5 && cfgArmor.All(kv => ExpectedArmor.TryGetValue(kv.Key, out var w) && Math.Abs(w - kv.Value) < 1e-9),
              string.Join(",", cfgArmor.Select(kv => $"{kv.Key}:{Inv(kv.Value)}")));

        // ═════════ 4. mods 数据校验：每块板都在该槽 filter 内、且是 4/5 级 ═════════
        Console.WriteLine();
        Console.WriteLine("──── ② mods 逐槽 filter 校验（只读 items.json）────");
        var cfgMods = new Dictionary<string, Dictionary<string, string[]>>();
        foreach (DictionaryEntry ve in modsCfg)
        {
            var vest = ve.Key.ToString();
            var slotMap = new Dictionary<string, string[]>();
            cfgMods[vest] = slotMap;
            var vName = ItemName(itemsRoot, vest);
            Console.WriteLine($"  {vest}  {vName}");
            foreach (DictionaryEntry se in (IDictionary)ve.Value)
            {
                var slot = se.Key.ToString();
                var plates = ((IDictionary)se.Value).Keys.Cast<object>().Select(x => x.ToString())
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                slotMap[slot] = plates;

                var filter = SlotFilter(itemsRoot, vest, slot);
                Check($"    {vName}.{slot} 在 items.json 里存在 filter", filter != null, slot);
                int bad = 0;
                foreach (var p in plates)
                {
                    var inFilter = filter != null && filter.Contains(p);
                    var ac = ArmorClass(itemsRoot, p);
                    if (!inFilter || ac is not (4 or 5)) bad++;
                    Console.WriteLine($"      {p}  ac={ac}  在filter={inFilter}  {ItemName(itemsRoot, p)}");
                }
                Check($"    {vName}.{slot}：{plates.Length} 块板全部在 filter 内且为 4/5 级", bad == 0, $"不合格 {bad} 块");
            }
        }
        Check("mods 配置与需求期望值逐槽一致", BagText(cfgMods) == BagText(ExpectedMods));

        // ═════════ 5. Apply ═════════
        Console.WriteLine();
        apply.Invoke(null, new object[] { ev });
        var eqNow = botType.BotInventory.Equipment;
        var faceNow = eqNow[EquipmentSlots.FaceCover];
        var weapNow = eqNow[EquipmentSlots.FirstPrimaryWeapon];
        var armorNow = eqNow[EquipmentSlots.ArmorVest];
        var faceKeys = faceNow.Keys.Select(k => k.ToString()).ToList();

        Console.WriteLine($"替换后 FaceCover   : {faceNow.Count} 项 → {string.Join(",", faceKeys)}");
        Console.WriteLine($"替换后 主武器       : {weapNow.Count} 项 → {string.Join(",", weapNow.Keys)}");
        Console.WriteLine($"替换后 护甲         : {armorNow.Count} 项 → {string.Join(",", armorNow.Keys)}");
        Console.WriteLine();

        Check("① 替换后 FaceCover 池内只有 6 个 tpl",
              faceNow.Count == 6 && faceKeys.OrderBy(x => x).SequenceEqual(Expected6.OrderBy(x => x)),
              $"{faceNow.Count} 项");
        Check("① 原 4 个巴拉克拉瓦 tpl 已全部不在池内",
              Original4Balaclavas.All(t => !faceKeys.Contains(t)),
              "仍在=" + string.Join(",", Original4Balaclavas.Where(faceKeys.Contains)));
        Check("① 原池 6 项（含下半脸面巾/GP-7）全部不在新池内",
              origFaceKeys.All(t => !faceKeys.Contains(t)),
              "仍在=" + string.Join(",", origFaceKeys.Where(faceKeys.Contains)));

        Check("① 替换后 FirstPrimaryWeapon 池 = 4 把新枪",
              weapNow.Count == 4 && weapNow.Keys.Select(k => k.ToString()).OrderBy(x => x)
                    .SequenceEqual(ExpectedWeapons.Keys.OrderBy(x => x)),
              string.Join(",", weapNow.Keys));
        Check("① 替换后 ArmorVest 池 = 5 件新护甲",
              armorNow.Count == 5 && armorNow.Keys.Select(k => k.ToString()).OrderBy(x => x)
                    .SequenceEqual(ExpectedArmor.Keys.OrderBy(x => x)),
              string.Join(",", armorNow.Keys));

        Check("③ EquipmentChances：FaceCover/FirstPrimaryWeapon/ArmorVest 全部 = 100",
              Math.Abs(chances["FaceCover"] - 100) < 1e-9
              && Math.Abs(chances["FirstPrimaryWeapon"] - 100) < 1e-9
              && Math.Abs(chances["ArmorVest"] - 100) < 1e-9,
              $"FaceCover={chances["FaceCover"]} FirstPrimaryWeapon={chances["FirstPrimaryWeapon"]} ArmorVest={chances["ArmorVest"]}");

        // ① mods 全量替换
        Console.WriteLine();
        var appliedSetRefs = new Dictionary<(string Vest, string Slot), HashSet<MongoId>>();
        foreach (var vest in ExpectedMods.Keys)
        {
            var vd = botType.BotInventory.Mods.TryGetValue(new MongoId(vest), out var x) ? x : null;
            Check($"① mods：{vest} 在 Mods 里", vd != null);
            if (vd == null) continue;
            foreach (var kv in ExpectedMods[vest])
            {
                var set = SetAt(botMods, vest, kv.Key);
                if (set != null) appliedSetRefs[(vest, kv.Key)] = set;
                Check($"    {vest}.{kv.Key} = 恰好 {kv.Value.Length} 块目标插板",
                      set != null && set.Select(t => t.ToString()).OrderBy(x => x, StringComparer.Ordinal)
                                    .SequenceEqual(kv.Value.OrderBy(x => x, StringComparer.Ordinal)),
                      set == null ? "槽不存在" : string.Join(",", set));
            }
        }
        Check("① Korund-VM 原本不在 Mods ⇒ Apply 新建成功（原 4 件 → 现 5 件）",
              !presentVests.Contains(Korund) && botType.BotInventory.Mods.ContainsKey(new MongoId(Korund)),
              $"原存在={presentVests.Contains(Korund)} 现存在={botType.BotInventory.Mods.ContainsKey(new MongoId(Korund))}");

        var plateSlots = cfgMods.Values.SelectMany(v => v.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => s.ToLowerInvariant()).ToList();
        Check("③ EquipmentModsChances：配置里出现的插板槽（含侧板）全部 = 100",
              plateSlots.All(s => modChances.TryGetValue(s, out var v) && Math.Abs(v - 100) < 1e-9),
              string.Join(",", plateSlots.Select(s => $"{s}={modChances.GetValueOrDefault(s)}")));

        // 日志逐字
        var wantEqLogs = new List<string>();
        foreach (var map in maps)
            foreach (var slot in new[] { "FaceCover", "FirstPrimaryWeapon", "ArmorVest" })
            {
                int now = slot == "FaceCover" ? 6 : slot == "FirstPrimaryWeapon" ? 4 : 5;
                wantEqLogs.Add($"[装备替换] {map} arenaFighterEvent.{slot}：{now} 项（原 {origCounts[slot]} 项）｜装备概率 100%");
            }
        Check("日志逐字：5 图 × 3 槽 = 15 行装备替换日志",
              wantEqLogs.All(msgs.Contains), string.Join(" ｜ ", wantEqLogs.Where(m => !msgs.Contains(m))));
        var modLogs = ExpectedMods.SelectMany(kv => kv.Value.Select(s =>
            $"[装备替换] mods '{kv.Key}' {s.Key}：{s.Value.Length} 块插板")).ToList();
        Check($"日志逐字：{modLogs.Count} 行插板池日志", modLogs.All(msgs.Contains),
              string.Join(" ｜ ", modLogs.Where(m => !msgs.Contains(m))));
        Check("日志：Korund-VM 新建插槽池有 warning",
              msgs.Any(m => m.Contains("不在") && m.Contains(Korund) && m.Contains("新建")));
        foreach (var m in msgs.Where(m => m.Contains("mods '")))
            Console.WriteLine("    " + m);

        // ═════════ 6. 按图切换 ═════════
        Console.WriteLine();
        prefix.Invoke(null, new object[] { new DetailsShim { Location = "woods" } });
        Check("③a 目标图 woods ⇒ 三个池子都是新池（FaceCover 6 / 武器 4 / 护甲 5）",
              ReferenceEquals(eqNow[EquipmentSlots.FaceCover], faceNow)
              && ReferenceEquals(eqNow[EquipmentSlots.FirstPrimaryWeapon], weapNow)
              && ReferenceEquals(eqNow[EquipmentSlots.ArmorVest], armorNow)
              && faceNow.Count == 6 && weapNow.Count == 4 && armorNow.Count == 5);
        Check("③a 目标图 woods ⇒ 装备概率 100 + 插板概率 100",
              Math.Abs(chances["FaceCover"] - 100) < 1e-9 && Math.Abs(chances["ArmorVest"] - 100) < 1e-9
              && plateSlots.All(s => Math.Abs(modChances.GetValueOrDefault(s) - 100) < 1e-9));

        prefix.Invoke(null, new object[] { new DetailsShim { Location = "factory4_day" } });
        Check("③b 非目标图 factory4_day ⇒ 三个池子切回**原池实例**",
              ReferenceEquals(eqNow[EquipmentSlots.FaceCover], pools["FaceCover"])
              && ReferenceEquals(eqNow[EquipmentSlots.FirstPrimaryWeapon], pools["FirstPrimaryWeapon"])
              && ReferenceEquals(eqNow[EquipmentSlots.ArmorVest], pools["ArmorVest"]));
        Check("③b 非目标图 ⇒ 装备概率与插板概率逐字节回原值",
              DictText(chances) == chanceBefore && DictText(modChances) == modChanceBefore,
              $"chances={(DictText(chances) == chanceBefore)} modChances={(DictText(modChances) == modChanceBefore)}");
        Check("③b 非目标图 ⇒ Mods 插板 HashSet 全部切回**原实例**",
              origSetRefs.All(kv => ReferenceEquals(SetAt(botMods, kv.Key.Vest, kv.Key.Slot), kv.Value)),
              string.Join(",", origSetRefs.Where(kv => !ReferenceEquals(SetAt(botMods, kv.Key.Vest, kv.Key.Slot), kv.Value)).Select(kv => kv.Key.Vest + "." + kv.Key.Slot)));
        Check("③b 非目标图 ⇒ Korund-VM（原本不存在的键）被整键删除",
              !botType.BotInventory.Mods.ContainsKey(new MongoId(Korund)));

        prefix.Invoke(null, new object[] { new DetailsShim { Location = "woods" } });
        Check("③c 再回目标图 ⇒ 新池实例 + 新 HashSet 实例全部回来",
              ReferenceEquals(eqNow[EquipmentSlots.FaceCover], faceNow) && faceNow.Count == 6 && armorNow.Count == 5
              && appliedSetRefs.All(kv => ReferenceEquals(SetAt(botMods, kv.Key.Vest, kv.Key.Slot), kv.Value)));

        // ═════════ 7. Restore ═════════
        Console.WriteLine();
        restore.Invoke(null, null);
        var modsAfter = ModsText(botType.BotInventory.Mods);
        Console.WriteLine("还原后 Mods 护甲数: " + botType.BotInventory.Mods.Count + "（原始 " + presentVests.Count + " 件）");
        Console.WriteLine();

        Check("④ 还原后三个 Equipment 槽都是**原池实例**",
              ReferenceEquals(botType.BotInventory.Equipment[EquipmentSlots.FaceCover], pools["FaceCover"])
              && ReferenceEquals(botType.BotInventory.Equipment[EquipmentSlots.FirstPrimaryWeapon], pools["FirstPrimaryWeapon"])
              && ReferenceEquals(botType.BotInventory.Equipment[EquipmentSlots.ArmorVest], pools["ArmorVest"]));
        foreach (var slot in new[] { "FaceCover", "FirstPrimaryWeapon", "ArmorVest" })
            Check($"④ 还原后 {slot} 池规范化逐字节 == 原始",
                  PoolText(pools[slot]) == poolBefore[slot],
                  PoolText(pools[slot]) == poolBefore[slot] ? "" : $"原始 {poolBefore[slot]} / 还原 {PoolText(pools[slot])}");
        Check("④ 还原后 chances 逐字节 == 原始", DictText(chances) == chanceBefore,
              DictText(chances) == chanceBefore ? "" : $"原始 {chanceBefore} / 还原 {DictText(chances)}");
        Check("④ 还原后 Mods（全部护甲×全部槽）逐字节 == 原始", modsAfter == modsBefore,
              modsAfter == modsBefore ? "" : "Mods 文本不一致");
        Check("④ 还原后 EquipmentModsChances 逐字节 == 原始", DictText(modChances) == modChanceBefore,
              DictText(modChances) == modChanceBefore ? "" : $"原始 {modChanceBefore} / 还原 {DictText(modChances)}");
        Check("④ 还原后被替换过的插板 HashSet 全部写回**原实例**",
              origSetRefs.All(kv => ReferenceEquals(SetAt(botMods, kv.Key.Vest, kv.Key.Slot), kv.Value)),
              string.Join(",", origSetRefs.Where(kv => !ReferenceEquals(SetAt(botMods, kv.Key.Vest, kv.Key.Slot), kv.Value)).Select(kv => kv.Key.Vest + "." + kv.Key.Slot)));
        Check("④ 还原后 Korund-VM 键已删除（原本不存在）",
              !botType.BotInventory.Mods.ContainsKey(new MongoId(Korund)));
        Check("④ 还原日志存在", msgs.Any(m => m.Contains("已还原")), msgs.FirstOrDefault(m => m.Contains("已还原")) ?? "（无）");

        // ═════════ 8. 槽名大小写：6B13 EMR（items.json 是 front_plate 小写）═════════
        Console.WriteLine();
        Console.WriteLine("──── 大小写：6B13 EMR（5c0e53c886f7747fa54205c7，模板槽名 front_plate/back_plate 小写）────");
        var emrMods = new Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>();
        if (modsEl.TryGetProperty(Emr, out var emrEl))
        {
            var d = new Dictionary<string, HashSet<MongoId>>(StringComparer.Ordinal);
            foreach (var s in emrEl.EnumerateObject())
            {
                var hs = new HashSet<MongoId>();
                foreach (var t in s.Value.EnumerateArray()) hs.Add(new MongoId(t.GetString()));
                d[s.Name] = hs;
            }
            emrMods[new MongoId(Emr)] = d;
        }
        var emrDict = emrMods[new MongoId(Emr)];
        var emrFrontOrig = emrDict["front_plate"];
        var emrBackOrig = emrDict["back_plate"];
        var emrModChances = new Dictionary<string, double>(modChances);
        var emrBefore = ModsText(emrMods);
        var emrChanceBefore = DictText(emrModChances);
        var emrBot = new BotType
        {
            BotInventory = new BotTypeInventory { Mods = emrMods },
            BotChances = new Chances { EquipmentModsChances = emrModChances },
        };
        tDim.GetField("BotTable", AnyStatic)?.SetValue(null,
            new TableShim { Types = new Dictionary<string, BotType> { ["arenafighterevent"] = emrBot } });

        // 配置写成首字母大写 Front_plate/Back_plate（与模板小写键不同）⇒ 必须落到**实际小写键**上
        var mini = "{\"id\":\"probe_case\",\"botEquipmentReplace\":{\"maps\":[],\"roles\":{\"arenaFighterEvent\":{\"mods\":{\""
                 + Emr + "\":{\"Front_plate\":{\"656f603f94b480b8a500c0d6\":1},"
                 + "\"Back_plate\":{\"656efaf54772930db4031ff5\":1}}}}}}}";
        object ev2;
        using (var md = JsonDocument.Parse(mini))
            ev2 = JsonSerializer.Deserialize(md.RootElement.GetRawText(), tEv,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        apply.Invoke(null, new object[] { ev2 });

        Check("大小写：配置 Front_plate 命中模板真实键 front_plate（写进小写键、不新增重复键）",
              emrDict.ContainsKey("front_plate") && !emrDict.ContainsKey("Front_plate")
              && emrDict["front_plate"].Select(x => x.ToString()).SequenceEqual(new[] { "656f603f94b480b8a500c0d6" }),
              string.Join(",", emrDict["front_plate"]));
        Check("大小写：不新增重复键 Front_plate（原键仍只有小写那份）", !emrDict.ContainsKey("Front_plate"));
        Check("大小写：back_plate 同样被替换为 1 块",
              emrDict["back_plate"].Count == 1 && emrDict["back_plate"].First().ToString() == "656efaf54772930db4031ff5");
        Check("大小写：插板概率按小写 front_plate/back_plate = 100",
              Math.Abs(emrModChances["front_plate"] - 100) < 1e-9 && Math.Abs(emrModChances["back_plate"] - 100) < 1e-9,
              $"front={emrModChances.GetValueOrDefault("front_plate")} back={emrModChances.GetValueOrDefault("back_plate")}");

        restore.Invoke(null, null);
        Check("大小写：还原后 Mods 逐字节 == 原始（含小写键）", ModsText(emrMods) == emrBefore);
        Check("大小写：还原后 front_plate/back_plate 是原 HashSet 实例",
              ReferenceEquals(emrDict["front_plate"], emrFrontOrig)
              && ReferenceEquals(emrDict["back_plate"], emrBackOrig));
        Check("大小写：还原后 EquipmentModsChances 逐字节 == 原始", DictText(emrModChances) == emrChanceBefore);

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：PASS {pass} / FAIL {fail} ════════");
        return fail == 0 ? 0 : 1;
    }
}
