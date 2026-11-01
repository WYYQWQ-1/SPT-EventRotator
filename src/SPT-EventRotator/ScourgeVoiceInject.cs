using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using IOPath = System.IO.Path;
using SPTarkov.Server.Core.Utils;

namespace EventRotator;

/// <summary>
/// 把 **Scourge 的独特语音**接进本 mod（照 danauraborealis/Scourge 的 `ScourgeVoiceRegistration` 语义）：
///
///   ① 包里自带 `bundles.json` + `bundles/assets/content/audio/phrases/bossscourge_voice.bundle`
///      ⇒ SPT 按 bundles.json 把语音库发给客户端（**客户端不需要插件**）
///   ② 把 `db/voices/bossscourge.json` 反序列化成 <see cref="CustomizationItem"/> 塞进
///      `TemplateTable.Customization`（id = 6acf6e6e35ee26cb5b0a5ab1，parent = 5fc100cf95572123ae738483 = 声音类）
///   ③ `db/bots/types/bossScourge.json` 的 `appearance.voice` 指向该 id（数据侧已改）
///
/// 许可：语音包与语音 customization 来自 Scourge 的 **data/**，CC BY-NC-SA 4.0（署名 Archangel），
///       见 `THIRDPARTY-NOTICES.md`。任一环缺失 ⇒ 只 warning、不抛（没有语音也能正常玩）。
/// </summary>
internal static class ScourgeVoiceInject
{
    internal const string VoiceId = "6acf6e6e35ee26cb5b0a5ab1";
    private const string VoiceJsonRel = "db/voices/bossscourge.json";
    private const string BundleRel = "bundles/assets/content/audio/phrases/bossscourge_voice.bundle";

    internal static Action<string> Info;
    internal static Action<string> Warn;
    private static bool _done;

    internal static void Apply(object templateTable, string modPath, JsonUtil jsonUtil)
    {
        if (_done) return;
        _done = true;
        try
        {
            if (templateTable == null) { Warn?.Invoke("[EventRotator][语音] 拿不到 TemplateTable ⇒ 天灾语音未注册"); return; }

            var voiceJson = IOPath.Combine(modPath, VoiceJsonRel);
            var bundle = IOPath.Combine(modPath, BundleRel);
            if (!File.Exists(bundle))
            {
                Warn?.Invoke($"[EventRotator][语音] 找不到语音包 {BundleRel} ⇒ 天灾用默认语音（其它功能不受影响）");
                return;
            }
            if (!File.Exists(voiceJson)) { Warn?.Invoke($"[EventRotator][语音] 找不到 {VoiceJsonRel} ⇒ 跳过"); return; }

            var custProp = templateTable.GetType().GetProperty("Customization", BindingFlags.Public | BindingFlags.Instance);
            if (custProp?.GetValue(templateTable) is not IDictionary dict)
            {
                Warn?.Invoke("[EventRotator][语音] TemplateTable.Customization 不是字典 ⇒ 跳过");
                return;
            }

            // 键类型是 MongoId（若拿不到就退回字符串键）
            object key = VoiceId;
            try
            {
                var kt = dict.GetType().GetGenericArguments().FirstOrDefault();
                if (kt != null && kt.Name.Contains("MongoId"))
                    key = Activator.CreateInstance(kt, VoiceId) ?? VoiceId;
            }
            catch { }

            if (dict.Contains(key)) { Info?.Invoke("[EventRotator][语音] 天灾语音已存在（其它 mod 提供），不覆盖"); return; }

            var item = Deserialize(voiceJson, jsonUtil);
            if (item == null) { Warn?.Invoke("[EventRotator][语音] 语音定义反序列化失败 ⇒ 跳过"); return; }

            dict[key] = item;
            Info?.Invoke("[EventRotator][语音] ✔ 已注册天灾独特语音 BossScourge（语音包由 bundles.json 提供给客户端）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke("[EventRotator][语音] 注册失败（已忽略）：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static CustomizationItem Deserialize(string path, JsonUtil jsonUtil)
    {
        var text = File.ReadAllText(path);
        try { return jsonUtil?.Deserialize<CustomizationItem>(text); }
        catch (Exception ex) { Warn?.Invoke("[EventRotator][语音] jsonUtil 反序列化失败：" + ex.Message); }
        try { return System.Text.Json.JsonSerializer.Deserialize<CustomizationItem>(text); }
        catch (Exception ex) { Warn?.Invoke("[EventRotator][语音] STJ 反序列化失败：" + ex.Message); return null; }
    }
}
