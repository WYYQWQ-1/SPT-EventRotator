// ★ 离线探针：casus_belli 劫掠者（arenaFighterEvent）面具**后置强制修正**（FaceCoverFixDimension）
//
// 只**读**实机（D:\PureEFTv4.1）的 SPT 程序集 / 本地化文件，用**真实的 SPT 模型**（Item / MongoId）
// 在内存里搭出"生成后的 bot"，再反射调用工作区 Release 构建出的
// `EventRotator.FaceCoverFixDimension.Apply / OnBotGenerated / Restore`。
// 不写实机、不启动服务端、不加载 PBS 运行时（PBS 只作为"为什么需要本修正"的背景，证据见 audit/pbs_full）。
//
// 结果同时打到控制台与 probe_out.txt（UTF-8），便于逐字核对中文日志格式。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using IOPath = System.IO.Path;

// ── 替身：只提供 dimension 用反射读/写的**成员名**（Info.Settings.Role / Inventory.Items / Inventory.Equipment）──
sealed class SettingsShim { public string Role { get; set; } }
sealed class InfoShim { public SettingsShim Settings { get; set; } }
sealed class InvShim
{
    public List<Item> Items { get; set; } = new();
    public MongoId? Equipment { get; set; }
}
sealed class BotShim { public InfoShim Info { get; set; } public InvShim Inventory { get; set; } }
sealed class DetailsShim
{
    public string Location { get; set; }
    public string LocationId { get; set; }        // 防御性多源（4.1.6 本体没有这个属性，但有就认）
    public string Role { get; set; }
    public string EventRole { get; set; }
    public string RoleLowercase { get; set; }     // SPT GenerateBot:158 写的全小写（PBS 装备池就用它）
}

// ── 替身：只提供 dimension 读 `TemplateTable.Items[tpl].Properties.BlocksFaceCover` 所需的成员名 ──
//   CLR 名（不是 JSON 名）：TemplateItem.Properties.BlocksFaceCover（bool?）
//   证据：`TemplateItem.cs:95/156` + `BotGeneratorHelper.IsItemIncompatibleWithCurrentItems:325`
sealed class PropsShim { public bool? BlocksFaceCover { get; set; } }
sealed class TplShim { public PropsShim Properties { get; set; } }
sealed class TemplateTableShim { public Dictionary<MongoId, TplShim> Items { get; set; } = new(); }


static class Probe
{
    const string SPT = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    const string ModSrc = @"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\fix\SPT-EventRotator";
    static string ModDll => IOPath.Combine(ModSrc, "bin", "Release", "net10.0", "SPT-EventRotator.dll");
    static string EventsJson => IOPath.Combine(ModSrc, "events.json");

    // 6 款彩绘 CQCM 面具（需求原文）
    static readonly string[] Masks =
    {
        "67a5c657782ce4655104db16", // 亡灵节
        "67a5c5df782ce4655104db14", // 战疤
        "67a5c5f37f52620c5b05b4d6", // 标靶
        "67a5c61c7f52620c5b05b4d8", // 恶魔
        "67a5c6068fcd9fb73f0752cf", // 骷髅
        "68a9a15d73d52d47830759c9", // 恶魔獠牙
    };

    const string HalfMask = "572b7fa524597762b747ce82";   // Lower half-mask = 中文「半面巾」
    const string Shroud = "5e54f76986f7740366043752";     // Shroud half-mask
    const string Helmet = "5b432d215acfc4771e1c6624";     // 一件 Headwear（别的槽，验证"别的槽未动"）
    // ★ v1.0.2【问题 B】实机 items.json 里 3 件 BlocksFaceCover=true 的头部槽物品之一（滑雪帽），
    //   外加一件"头盔挂件"（用它验证有挂件时走整体摘除而不是换壳）。
    const string BlockingHat = "5ab8f20c86f7745cdb629fb2";
    const string BlockingHatChild = "5d6d3829a4b9361bc8618943";   // LZSh 面盾（只当一个"以头盔为 parent"的挂件）

    const string Armor = "5c0e51be86f774598e797894";      // ArmorVest
    const string Backpack = "545cdae64bdc2d39198b4568";   // Backpack

    static readonly List<string> Out = new();
    static int pass, fail;

    static void Line(string s) { Out.Add(s); Console.WriteLine(s); }
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Line($"  [PASS] {name}" + (detail.Length > 0 ? "  | " + detail : "")); }
        else { fail++; Line($"  [FAIL] {name}  | {detail}"); }
    }

    // SPT 的 Item 是 record + required；构造最稳的方式 = JSON + SPT 自己的 MongoId 转换器（与 R.FromJson 同一套）
    static JsonSerializerOptions _opts;
    static JsonSerializerOptions Opts()
    {
        if (_opts != null) return _opts;
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var tConv = Type.GetType("SPTarkov.Server.Core.Utils.Json.Converters.StringToMongoIdConverter, SPTarkov.Server.Core", false);
        if (tConv != null) o.Converters.Add((System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(tConv));
        var tEnum = Type.GetType("SPTarkov.Server.Core.Utils.Json.Converters.EftEnumConverterFactory, SPTarkov.Server.Core", false);
        if (tEnum != null) o.Converters.Add((System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(tEnum));
        _opts = o;
        return o;
    }

    static string NewId()
    {
        const string hex = "0123456789abcdef";
        var r = new Random(Guid.NewGuid().GetHashCode());
        var c = new char[24];
        for (int i = 0; i < 24; i++) c[i] = hex[r.Next(16)];
        return new string(c);
    }

    static Item NewItem(string tpl, string slot, string parentId, string id = null)
    {
        id ??= NewId();
        var json = "{\"_id\":\"" + id + "\",\"_tpl\":\"" + tpl + "\","
                   + (parentId == null ? "" : "\"parentId\":\"" + parentId + "\",")
                   + (slot == null ? "" : "\"slotId\":\"" + slot + "\",")
                   + "\"upd\":{\"StackObjectsCount\":1}}";
        return JsonSerializer.Deserialize<Item>(json, Opts());
    }

    /// <summary>搭一个"已经生成好的 bot"：固定 equipId / 各级 id 可预测，便于断言"其它槽未动"。</summary>
    static BotShim BuildBot(string role, string faceCoverTpl, out string equipId, out List<(string Id, string Tpl, string Slot)> others,
                            string helmetTpl = null, string helmetChildTpl = null)
    {
        equipId = "aaaa0000000000000000aaaa";
        var inv = new InvShim { Equipment = new MongoId(equipId) };
        var bot = new BotShim { Info = new InfoShim { Settings = new SettingsShim { Role = role } }, Inventory = inv };

        // SPT 一定会有的根物品（inventory / pockets …），顺便当"别的槽"的对照
        inv.Items.Add(NewItem("55d7217a4bdc2d86028b456d", null, null, equipId));               // inventory root
        inv.Items.Add(NewItem("557ffd194bdc2d28148b457f", "Pockets", equipId, "bbbb0000000000000000bbbb"));
        inv.Items.Add(NewItem(helmetTpl ?? Helmet, "Headwear", equipId, "cccc0000000000000000cccc"));
        if (helmetChildTpl != null)                                                              // 头盔挂件（父 = 头盔 _id）
            inv.Items.Add(NewItem(helmetChildTpl, null, "cccc0000000000000000cccc", "cccc9999000000000000cccc"));
        inv.Items.Add(NewItem(Armor, "ArmorVest", equipId, "dddd0000000000000000dddd"));
        inv.Items.Add(NewItem(Backpack, "Backpack", equipId, "eeee0000000000000000eeee"));
        if (faceCoverTpl != null)
            inv.Items.Add(NewItem(faceCoverTpl, "FaceCover", equipId, "ffff0000000000000000ffff"));

        others = inv.Items
            .Where(x => !string.Equals(x.SlotId, "FaceCover", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Id.ToString(), x.Template.ToString(), x.SlotId ?? ""))
            .ToList();
        return bot;
    }

    static Item HeadwearOf(BotShim bot) =>
        bot.Inventory.Items.FirstOrDefault(x => string.Equals(x.SlotId, "Headwear", StringComparison.OrdinalIgnoreCase));

    static Item FaceCoverOf(BotShim bot) =>
        bot.Inventory.Items.FirstOrDefault(x => string.Equals(x.SlotId, "FaceCover", StringComparison.OrdinalIgnoreCase));

    static string TplOf(Item i) => i?.Template.ToString();

    static int Main()
    {
        Line("════════ FaceCoverFixDimension 离线探针（casus_belli 劫掠者面具 · 生成后强制修正）════════");
        Line("读取：实机 SPT 程序集（只读） + 工作区 events.json / Release DLL");

        if (!File.Exists(ModDll)) { Line($"✗ 找不到 {ModDll}（先 dotnet build -c Release）"); Flush(); return 1; }

        // ═════════ 1. 载入 mod + 反射取类型 ═════════
        var asm = Assembly.LoadFrom(ModDll);
        var tEv = asm.GetType("EventRotator.EventDef", true);
        var tDim = asm.GetType("EventRotator.FaceCoverFixDimension", true);
        var tEng = asm.GetType("EventRotator.EventEngine", true);
        const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static;
        const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        var mApply = tDim.GetMethod("Apply", PubStatic);
        var mGen = tDim.GetMethod("OnBotGenerated", PubStatic);
        var mRestore = tDim.GetMethod("Restore", PubStatic);
        Check("反射找到 FaceCoverFixDimension.Apply / OnBotGenerated / Restore",
              mApply != null && mGen != null && mRestore != null,
              $"Apply={mApply != null} OnBotGenerated={mGen != null} Restore={mRestore != null}");
        if (mApply == null || mGen == null || mRestore == null) { Flush(); return 1; }

        var msgs = new List<string>();
        Action<string> tap = s => msgs.Add(s);
        tDim.GetField("Log", AnyStatic)?.SetValue(null, tap);
        tDim.GetField("Warn", AnyStatic)?.SetValue(null, tap);
        // ★ 诊断走**独立通道**（`[面具修正][诊断]`），必须与功能日志（`[面具修正] … 替换/补件`）分开断言
        var diag = new List<string>();
        var fDiag = tDim.GetField("DiagLog", AnyStatic);
        fDiag?.SetValue(null, (Action<string>)(s => diag.Add(s)));
        var mRaidStart = tDim.GetMethod("OnRaidStart", PubStatic);
        Check("反射找到诊断通道 DiagLog 与 OnRaidStart(每局分母点)", fDiag != null && mRaidStart != null,
              $"DiagLog={fDiag != null} OnRaidStart={mRaidStart != null}");
        tEng.GetField("ModPath", AnyStatic)?.SetValue(null, IOPath.Combine(SPT, "user", "mods", "SPT-EventRotator"));

        // ═════════ 1b. ★ v1.0.2【问题 B】注入运行期 TemplateTable 替身（判 BlocksFaceCover）═════════
        //   数据原文来自实机 items.json（只读取证）：
        //     LZSh / ULACH / ZSh-1-2M / Kiver-M / FAST MT / SFERA-S → BlocksFaceCover=false
        //     滑雪帽 5ab8f20c… / LLCS 618aef6d… / Heavy Trooper 5ea058e0… → BlocksFaceCover=true
        var table = new TemplateTableShim();
        void Reg(string tpl, bool? blocks) => table.Items[new MongoId(tpl)] = new TplShim { Properties = new PropsShim { BlocksFaceCover = blocks } };
        foreach (var t in new[] { Helmet, "5b40e1525acfc4771e1c6611", "5aa7e4a4e5b5b000137b76f2",
                                  "5645bc214bdc2d363b8b4571", "5ac8d6885acfc400180ae7b0", "5aa7d193e5b5b000171d063f" })
            Reg(t, false);
        Reg(BlockingHat, true);
        Reg(BlockingHatChild, false);
        tDim.GetField("TemplateTable", AnyStatic)?.SetValue(null, table);
        Check("注入运行期 TemplateTable 替身（6 件不阻挡 + 1 件阻挡 + 1 件挂件）",
              tDim.GetField("TemplateTable", AnyStatic)?.GetValue(null) != null && table.Items.Count == 8,
              $"Items={table.Items.Count}");

        // ═════════ 2. events.json → casus_belli（真实 EventDef 模型）═════════
        object ev = null;
        using (var doc = JsonDocument.Parse(File.ReadAllText(EventsJson)))
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.TryGetProperty("id", out var idEl) && idEl.GetString() == "casus_belli")
                {
                    ev = JsonSerializer.Deserialize(el.GetRawText(), tEv, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    break;
                }
        Check("events.json 找到并反序列化 casus_belli", ev != null);
        if (ev == null) { Flush(); return 1; }

        // ═════════ 3. Apply（武装）═════════
        msgs.Clear();
        mApply.Invoke(null, new[] { ev });
        Line("  ── Apply 日志逐字 ──");
        foreach (var m in msgs) Line("  " + m);
        Check("① Apply 后输出武装日志（含 6 款面具 + 5 张图）",
              msgs.Any(m => m.Contains("已武装") && m.Contains("面具 6 款")
                            && new[] { "bigmap", "woods", "interchange", "lighthouse", "shoreline" }.All(x => m.Contains(x))),
              msgs.FirstOrDefault(m => m.Contains("已武装")) ?? "(无)");

        // ═════════ 4. ① 有 FaceCover（半面巾）⇒ 被换成 6 款之一 ═════════
        var bot1 = BuildBot("arenaFighterEvent", HalfMask, out var equip1, out var others1);
        var fc1 = FaceCoverOf(bot1);
        var id1 = fc1.Id.ToString();
        var upd1 = fc1.Upd;
        var itemsCount1 = bot1.Inventory.Items.Count;
        var details1 = new DetailsShim { Location = "woods", Role = "arenaFighterEvent" };

        msgs.Clear();
        diag.Clear();
        mGen.Invoke(null, new object[] { details1, bot1 });
        Line("  ── 修正日志逐字（woods / 半面巾）──");
        foreach (var m in msgs) Line("  " + m);
        Line("  ── 诊断（独立通道）逐字 ──");
        foreach (var m in diag) foreach (var piece in m.Split('\n')) Line("  " + piece);

        Check("⑭ 首次进 postfix ⇒ 诊断打「★已接管 bot 生成（进程内首次调用）」+ 逐字段取值",
              diag.Any(m => m.Contains("★已接管 bot 生成（进程内首次调用）")
                            && m.Contains("bot.Info.Settings.Role=arenaFighterEvent")
                            && m.Contains("details.Role=arenaFighterEvent")
                            && m.Contains("details.Location=woods")),
              diag.FirstOrDefault(m => m.Contains("已接管")) ?? "(无)");
        Check("⑭b 诊断行与功能日志分通道（`[面具修正][诊断]` vs `[面具修正] <map> …`）",
              diag.All(m => m.StartsWith("[面具修正][诊断]"))
              && msgs.All(m => !m.Contains("[面具修正][诊断]")),
              $"diag={diag.Count} 行 / 功能={msgs.Count} 行");

        var tpl1 = TplOf(fc1);
        Check("① 半面巾被换成 6 款面具之一", Masks.Contains(tpl1), $"new tpl = {tpl1}（旧 {HalfMask}）");
        Check("② `_id` 未变", fc1.Id.ToString() == id1, $"{id1} → {fc1.Id}");
        Check("②b `upd` 实例/内容未变", ReferenceEquals(fc1.Upd, upd1), $"Upd 引用 {(ReferenceEquals(fc1.Upd, upd1) ? "相同" : "被换")}");
        Check("③ 物品条数未变", bot1.Inventory.Items.Count == itemsCount1, $"{itemsCount1} → {bot1.Inventory.Items.Count}");
        var othersAfter = bot1.Inventory.Items
            .Where(x => !string.Equals(x.SlotId, "FaceCover", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Id.ToString(), x.Template.ToString(), x.SlotId ?? "")).ToList();
        Check("③b 别的槽（inventoryroot/Pockets/Headwear/ArmorVest/Backpack）逐项未动",
              others1.Count == othersAfter.Count && others1.Zip(othersAfter).All(z => z.First == z.Second),
              string.Join(" ", othersAfter.Select(x => x.Item3 + ":" + x.Item2.Substring(0, 6))));
        Check("①b 日志格式逐字 = `[面具修正] woods arenaFighterEvent：替换 1 件 半面巾 → 涂装 CQCM（…）`",
              msgs.Any(m => m.StartsWith("[面具修正] woods arenaFighterEvent：替换 1 件 半面巾 → 涂装 CQCM（") && m.EndsWith("）")),
              msgs.LastOrDefault() ?? "(无)");

        // ═════════ 5. 六款等概率 ═════════
        var hist = new Dictionary<string, int>();
        int n = 600;
        for (int i = 0; i < n; i++)
        {
            var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "bigmap", Role = "arenaFighterEvent" }, b });
            var t = TplOf(FaceCoverOf(b));
            hist[t] = hist.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        Line("  ── 600 次抽样分布 ──  " + string.Join("  ", Masks.Select(t => $"{t[^4..]}×{hist.GetValueOrDefault(t)}")));
        Check("④ 抽样覆盖全部 6 款（等概率）", Masks.All(t => hist.GetValueOrDefault(t) > 0),
              $"缺: {string.Join(",", Masks.Where(t => hist.GetValueOrDefault(t) == 0))}");
        Check("④b 任一档都不超过均匀期望的 1.5 倍（100×1.5=150）", hist.Values.All(v => v <= 150),
              $"max={hist.Values.Max()} min={hist.Values.Min()}");
        Check("④c 从不落到旧 tpl（半面巾）", !hist.ContainsKey(HalfMask));

        // ═════════ 6. 非目标 role ⇒ 不动 ═════════
        foreach (var role in new[] { "assault", "arenafighter", "exusec", "bossknight", "pmcUSEC" })
        {
            var b = BuildBot(role, HalfMask, out _, out _);
            var fc = FaceCoverOf(b);
            var before = TplOf(fc);
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = role }, b });
            Check($"⑤ 非目标 role '{role}' 不动", TplOf(fc) == before && msgs.Count == 0, $"tpl={TplOf(fc)}");
        }

        // ═════════ 7. 非目标地图 ⇒ 不动 ═════════
        foreach (var map in new[] { "laboratory", "tarkovstreets", "rezervbase", "factory4_day", "labyrinth" })
        {
            var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            var fc = FaceCoverOf(b);
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Location = map, Role = "arenaFighterEvent" }, b });
            Check($"⑥ 非目标地图 '{map}' 不动", TplOf(fc) == HalfMask && msgs.Count == 0, $"tpl={TplOf(fc)}");
        }
        // 5 张目标图全部生效
        foreach (var map in new[] { "bigmap", "woods", "interchange", "lighthouse", "shoreline" })
        {
            var b = BuildBot("arenaFighterEvent", Shroud, out _, out _);
            var fc = FaceCoverOf(b);
            mGen.Invoke(null, new object[] { new DetailsShim { Location = map, Role = "arenaFighterEvent" }, b });
            Check($"⑥b 目标地图 '{map}' 生效（Shroud 半面罩 → 6 款之一）", Masks.Contains(TplOf(fc)), $"tpl={TplOf(fc)}");
        }

        // ═════════ 8. 没有 FaceCover ⇒ 补一件 ═════════
        {
            var b = BuildBot("arenaFighterEvent", null, out var equipId, out var others);
            var before = b.Inventory.Items.Count;
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "interchange", Role = "arenaFighterEvent" }, b });
            var added = FaceCoverOf(b);
            Line("  ── 补件日志逐字 ──");
            foreach (var m in msgs) Line("  " + m);
            Check("⑦ 没有 FaceCover 时补了 1 件", added != null && b.Inventory.Items.Count == before + 1,
                  $"items {before} → {b.Inventory.Items.Count}");
            Check("⑦b 补的那件 tpl ∈ 6 款", added != null && Masks.Contains(TplOf(added)), $"tpl={TplOf(added)}");
            Check("⑦c 补的那件 slotId=FaceCover / parentId=Inventory.Equipment（照 SPT GenerateEquipment:471-473）",
                  added != null && added.SlotId == "FaceCover" && added.ParentId == equipId,
                  $"slotId={added?.SlotId} parentId={added?.ParentId}（期望 {equipId}）");
            Check("⑦d 补的那件有合法 _id（24 hex）且 upd 只有 StackObjectsCount=1",
                  added != null && added.Id.ToString().Length == 24
                  && added.Upd?.StackObjectsCount == 1,
                  $"id={added?.Id} stack={added?.Upd?.StackObjectsCount}");
            var othersAfterAdd = b.Inventory.Items
                .Where(x => !string.Equals(x.SlotId, "FaceCover", StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Id.ToString(), x.Template.ToString(), x.SlotId ?? "")).ToList();
            Check("⑦e 补件时别的槽逐项未动",
                  others.Count == othersAfterAdd.Count && others.Zip(othersAfterAdd).All(z => z.First == z.Second));
            Check("⑦f 补件日志格式逐字",
                  msgs.Any(m => m.StartsWith("[面具修正] interchange arenaFighterEvent：原本没有 FaceCover ⇒ 补 1 件 涂装 CQCM（")),
                  msgs.LastOrDefault() ?? "(无)");
        }

        // ═════════ 9. 已经是 6 款之一 ⇒ 幂等（不换、不刷日志）═════════
        {
            var b = BuildBot("arenaFighterEvent", Masks[3], out _, out _);
            var fc = FaceCoverOf(b);
            var id0 = fc.Id.ToString();
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "shoreline", Role = "arenaFighterEvent" }, b });
            Check("⑧ 已经是目标面具 ⇒ 原样保留（幂等、无日志）", TplOf(fc) == Masks[3] && fc.Id.ToString() == id0 && msgs.Count == 0,
                  $"tpl={TplOf(fc)} msgs={msgs.Count}");
        }

        // ═════════ 10. 地图未知 ⇒ 跳过 + 只 warning 一次 ═════════
        {
            var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            var fc = FaceCoverOf(b);
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Location = null, Role = "arenaFighterEvent" }, b });
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "", Role = "arenaFighterEvent" }, b });
            Check("⑨ 地图未知 ⇒ 不修正（且只 warning 一次）",
                  TplOf(fc) == HalfMask && msgs.Count == 1 && msgs[0].Contains("不修正"),
                  $"tpl={TplOf(fc)} msgs={msgs.Count} {string.Join("|", msgs)}");
        }

        // ═════════ 11. 只用 details.EventRole 命中（Role 是别的名字）═════════
        {
            var b = BuildBot("arenaFighter", HalfMask, out _, out _);
            var fc = FaceCoverOf(b);
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "lighthouse", Role = "arenaFighter", EventRole = "arenaFighterEvent" }, b });
            Check("⑩ details.EventRole=arenaFighterEvent 也算目标（bot.Role=arenaFighter）", Masks.Contains(TplOf(fc)), $"tpl={TplOf(fc)}");
        }

        // ═════════ 11b. role 第 4 路来源：details.RoleLowercase（SPT GenerateBot:158 写的全小写）═════════
        {
            var b = BuildBot("arenaFighter", HalfMask, out _, out _);   // bot 身上的 role 不是目标
            var fc = FaceCoverOf(b);
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", RoleLowercase = "arenafighterevent" }, b });
            Check("⑩b details.RoleLowercase='arenafighterevent' 也算目标（大小写不敏感）",
                  Masks.Contains(TplOf(fc)), $"tpl={TplOf(fc)}");
        }

        // ═════════ 11c. Location 多源：Location 为空时用 LocationId / 大小写 ═════════
        {
            var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            var fc = FaceCoverOf(b);
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Role = "arenaFighterEvent", LocationId = "woods" }, b });
            Check("⑨b Location 读不到但 details.LocationId='woods' ⇒ 仍然生效（多源兜底）",
                  Masks.Contains(TplOf(fc)), $"tpl={TplOf(fc)}");

            var b2 = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            var fc2 = FaceCoverOf(b2);
            mGen.Invoke(null, new object[] { new DetailsShim { Role = "arenaFighterEvent", Location = "  Woods " }, b2 });
            Check("⑨c Location='  Woods '（有空格+大小写）⇒ 仍然生效",
                  Masks.Contains(TplOf(fc2)), $"tpl={TplOf(fc2)}");
        }

        // ═════════ 11d. 诊断汇总（每局一条）：累计 30 只后打一次 ═════════
        {
            mRaidStart.Invoke(null, new object[] { "woods" });      // 清上一局统计
            diag.Clear();
            // 第 1 只是目标 bot（会被替换），后 29 只是 assault（role 不命中）
            var hit = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = "arenaFighterEvent" }, hit });
            for (int i = 0; i < 30; i++)
            {
                var b = BuildBot("assault", HalfMask, out _, out _);
                mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = "assault" }, b });
            }

            var sum = diag.FirstOrDefault(m => m.Contains("本局收到 30 只 bot"));
            Line("  ── 诊断汇总逐字 ──");
            foreach (var m in diag) foreach (var piece in m.Split('\n')) Line("  " + piece);
            Check("⑫ 累计满 30 只 ⇒ 打一条本局汇总（含 bot 数 / role Top5 / Location 样例 / 各命中数）",
                  sum != null
                  && sum.Contains("assault×29")
                  && sum.Contains("arenaFighterEvent×1")
                  && sum.Contains("Location 样例=[woods]")
                  && sum.Contains("role 命中 1 只、其中地图命中 1 只")
                  && sum.Contains("替换 1、补件 0、已是面具 0、无 Location 跳过 0"),
                  sum ?? "(无)");
            Check("⑫b 汇总里带上判定条件（role 白名单 + 地图表）便于对照实机",
                  diag.Any(m => m.Contains("判定条件：role 必须 ∈ {arenaFighterEvent}") && m.Contains("woods")),
                  diag.LastOrDefault() ?? "(无)");
            Check("⑫c 只打一条汇总（不刷屏）",
                  diag.Count(m => m.Contains("本局收到")) == 1,
                  $"条数={diag.Count(m => m.Contains("本局收到"))}");
        }

        // ═════════ 11e. 诊断：每局分母点（OnRaidStart）打"上一局" ═════════
        {
            mRaidStart.Invoke(null, new object[] { "woods" });      // 先把统计清零（这条日志不参与断言）
            var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = "arenaFighterEvent" }, b });
            diag.Clear();
            mRaidStart.Invoke(null, new object[] { "interchange" });
            Check("⑬ OnRaidStart ⇒ 把上一局（1 只）打出来再清零",
                  diag.Any(m => m.Contains("上一局结束（新一局开始，地图=interchange）") && m.Contains("本局收到 1 只 bot")),
                  diag.FirstOrDefault() ?? "(无)");
            // 清零后本局 0 只 ⇒ 再开一局必须明确写「收到 0 只」（证明 postfix 没被调用）
            diag.Clear();
            mRaidStart.Invoke(null, new object[] { "woods" });
            Check("⑬b 上局一只都没收到 ⇒ 明确打「收到 **0 只 bot** ⇒ 该 postfix 没被调用」",
                  diag.Any(m => m.Contains("收到 **0 只 bot**") && m.Contains("该 postfix 没被调用")),
                  diag.FirstOrDefault() ?? "(无)");
        }

        // ═════════ 11f. 并发压测：bot 生成是 PLINQ 并行的（BotController.GenerateBotWave:187-188） ═════════
        //   400 只并发进 OnBotGenerated ⇒ ① 不抛异常（集合被写坏会抛/卡）；
        //   ② 每段汇总的「收到 N 只」求和 = 400（计数不丢，证明 Interlocked/锁 都对）。
        {
            mRaidStart.Invoke(null, new object[] { "woods" });      // 清零
            diag.Clear();
            int N = 400;
            var ex0 = 0;
            try
            {
                Parallel.For(0, N, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    bool target = i % 2 == 0;
                    var b = BuildBot(target ? "arenaFighterEvent" : "assault", HalfMask, out _, out _);
                    mGen.Invoke(null, new object[]
                    {
                        new DetailsShim { Location = "woods", Role = target ? "arenaFighterEvent" : "assault" }, b
                    });
                });
            }
            catch (Exception ex) { ex0 = 1; Line("  ✗ 并发抛异常: " + ex); }

            mRaidStart.Invoke(null, new object[] { "interchange" });  // 把余量刷出来
            int sumN = 0;
            foreach (var m in diag)
                foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(m, @"本局收到 (\d+) 只 bot"))
                    sumN += int.Parse(mm.Groups[1].Value);
            int flushed = diag.Count(m => m.Contains("[面具修正][诊断]") && m.Contains("本局收到"));
            Check("⑮ 16 线程并发 400 只：不抛异常 + 各段「收到 N 只」求和 = 400（计数不丢）",
                  ex0 == 0 && sumN == N,
                  $"异常={ex0} 求和={sumN}（期望 {N}）汇总段数={flushed}");
        }
        // ═════════ 12. ★ v1.0.2【问题 B】头盔阻挡 FaceCover ⇒ 换/摘；不阻挡的头盔不动 ═════════
        {
            // ⑯ 非阻挡头盔（LZSh，= 用户截图那顶）⇒ 头盔原样不动，面具照戴
            {
                var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _, helmetTpl: Helmet);
                var head0 = HeadwearOf(b);
                var headId = head0.Id.ToString(); var headTpl = TplOf(head0);
                var n0 = b.Inventory.Items.Count;
                msgs.Clear();
                mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = "arenaFighterEvent" }, b });
                Check("⑯ 非阻挡头盔 LZSh ⇒ 头盔 tpl/_id 原样不动（只换面具）",
                      HeadwearOf(b) != null && TplOf(HeadwearOf(b)) == headTpl && HeadwearOf(b).Id.ToString() == headId
                      && b.Inventory.Items.Count == n0 && Masks.Contains(TplOf(FaceCoverOf(b))),
                      $"head={TplOf(HeadwearOf(b))} mask={TplOf(FaceCoverOf(b))}");
                Check("⑯b 不阻挡时**不打**「阻挡 FaceCover」日志",
                      !msgs.Any(m => m.Contains("阻挡 FaceCover")), string.Join("|", msgs));
            }
            // ⑰ 阻挡头盔、无挂件 ⇒ 换掉（保留 _id ⇒ 物品树不断，物品数不变）
            {
                var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _, helmetTpl: BlockingHat);
                var headId = HeadwearOf(b).Id.ToString();
                var n0 = b.Inventory.Items.Count;
                msgs.Clear();
                mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = "arenaFighterEvent" }, b });
                var head = HeadwearOf(b);
                var swapMsg = msgs.FirstOrDefault(m => m.Contains("阻挡 FaceCover") && m.Contains("已换为"));
                Line("  ── 换头盔日志逐字 ──"); foreach (var m in msgs) Line("  " + m);
                Check("⑰ 阻挡头盔（无挂件）⇒ 换成不阻挡的（_id 不变 / 物品数不变 / 仍在 Headwear 槽）",
                      head != null && head.Id.ToString() == headId && TplOf(head) != BlockingHat
                      && TplOf(head) == Helmet && b.Inventory.Items.Count == n0,
                      $"tpl {BlockingHat} → {TplOf(head)}");
                Check("⑰b 换完照样戴上面具（日志含「保留 FaceCover」+ mask ∈ 6 款）",
                      Masks.Contains(TplOf(FaceCoverOf(b))) && swapMsg != null && swapMsg.Contains("保留 FaceCover"),
                      swapMsg ?? "(无)");
            }
            // ⑱ 阻挡头盔、有挂件 ⇒ 连挂件一起摘除（换壳会让挂件挂在不匹配的壳上）
            {
                var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _, helmetTpl: BlockingHat, helmetChildTpl: BlockingHatChild);
                var n0 = b.Inventory.Items.Count;
                msgs.Clear();
                mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = "arenaFighterEvent" }, b });
                var dropMsg = msgs.FirstOrDefault(m => m.Contains("阻挡 FaceCover") && m.Contains("已摘除头盔"));
                Line("  ── 摘头盔日志逐字 ──"); foreach (var m in msgs) Line("  " + m);
                Check("⑱ 阻挡头盔（有 1 件挂件）⇒ 头盔+挂件一起摘除（物品数 -2）",
                      HeadwearOf(b) == null && b.Inventory.Items.Count == n0 - 2
                      && !b.Inventory.Items.Any(x => x.Id.ToString() == "cccc9999000000000000cccc"),
                      $"items {n0} → {b.Inventory.Items.Count}");
                Check("⑱b 摘完照样戴上面具（日志含「含 1 件挂件，保留 FaceCover」）",
                      Masks.Contains(TplOf(FaceCoverOf(b))) && dropMsg != null && dropMsg.Contains("含 1 件挂件"),
                      dropMsg ?? "(无)");
            }
            // ⑲ 面具已经是目标款 + 阻挡头盔 ⇒ 面具幂等不动，但头盔仍要修
            {
                var b = BuildBot("arenaFighterEvent", Masks[2], out _, out _, helmetTpl: BlockingHat);
                var id0 = FaceCoverOf(b).Id.ToString();
                msgs.Clear();
                mGen.Invoke(null, new object[] { new DetailsShim { Location = "shoreline", Role = "arenaFighterEvent" }, b });
                Check("⑲ 面具已是 6 款之一 + 阻挡头盔 ⇒ 面具不动、头盔仍被换/摘",
                      TplOf(FaceCoverOf(b)) == Masks[2] && FaceCoverOf(b).Id.ToString() == id0
                      && (HeadwearOf(b) == null || TplOf(HeadwearOf(b)) != BlockingHat),
                      $"mask={TplOf(FaceCoverOf(b))} head={TplOf(HeadwearOf(b))}");
            }
        }

        {
            mRestore.Invoke(null, null);
            var b = BuildBot("arenaFighterEvent", HalfMask, out _, out _);
            var fc = FaceCoverOf(b);
            msgs.Clear();
            mGen.Invoke(null, new object[] { new DetailsShim { Location = "woods", Role = "arenaFighterEvent" }, b });
            Check("⑪ Restore 后不再修正（活动关掉即失效）", TplOf(fc) == HalfMask && msgs.Count == 0, $"tpl={TplOf(fc)}");
        }

        Line("");
        Line($"═══ 结果：PASS {pass} / FAIL {fail} ═══");
        Flush();
        return fail == 0 ? 0 : 1;
    }

    static void Flush()
    {
        try { File.WriteAllText(IOPath.Combine(AppContext.BaseDirectory, "..", "..", "..", "probe_out.txt"), string.Join(Environment.NewLine, Out) + Environment.NewLine, new UTF8Encoding(false)); }
        catch { try { File.WriteAllText("probe_out.txt", string.Join(Environment.NewLine, Out) + Environment.NewLine, new UTF8Encoding(false)); } catch { } }
    }
}
