using System;
using System.Linq;
using System.Collections;
using System.Reflection;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace EventRotator
{
    /// <summary>
    /// ★★ 2026-10-31【复仇小鹿语音修正】
    /// <para><b>现象</b>：万圣节 2023 仪式成功后，复仇扎里亚奇（<c>ravangeZryachiyEvent</c>）说话是 **scav 语音**。</para>
    /// <para><b>根因</b>：SPT 原数据里 <c>ravangezryachiyevent.json</c> 的
    /// <c>appearance.voice</c> = <c>5fc614390b735e7b024c76e6</c>，这是**通用 scav/PMC 默认语音**
    /// （assault / pmcb ot / marksman / cursedassault 全都用它）。而真正的扎里亚奇家族用的是
    /// <c>67b877f796760fe84f086992</c>（bosszryachiy / followerzryachiy / sectantpriest / predvestnik）。</para>
    /// <para><b>修法</b>：把该 bot 类型的 <c>appearance.voice</c> 换成扎里亚奇自己的语音 id。幂等，只改这一项。</para>
    /// </summary>
    internal static class RitualVoiceFix
    {
        internal const string RavangeBotType = "ravangezryachiyevent";
        internal const string ScavVoiceId = "5fc614390b735e7b024c76e6";   // 通用 scav/PMC 语音（错的那个）
        internal const string ZryachiyVoiceId = "67b877f796760fe84f086992"; // 扎里亚奇家族语音（正确的）

        internal static void Apply(BotTable botTable, Action<string> info, Action<string> warn)
        {
            try
            {
                if (botTable == null) { warn?.Invoke("[EventRotator][语音] 拿不到 BotTable ⇒ 跳过复仇小鹿语音修正"); return; }
                var types = botTable.GetType().GetProperty("Types")?.GetValue(botTable) as IDictionary;
                if (types == null) { warn?.Invoke("[EventRotator][语音] BotTable.Types 结构不识别 ⇒ 跳过"); return; }

                object bot = null;
                foreach (DictionaryEntry kv in types)
                {
                    if (string.Equals(kv.Key?.ToString(), RavangeBotType, StringComparison.OrdinalIgnoreCase))
                    { bot = kv.Value; break; }
                }
                if (bot == null) { warn?.Invoke($"[EventRotator][语音] 找不到 bot 类型 '{RavangeBotType}' ⇒ 跳过"); return; }

                // ★ 2026-10-31【只改 voice】整段照抄 bosszryachiy 失败（它的 appearance 为 null），
                //   所以回到"只把 voice 指向扎里亚奇语音"这一步——这一步之前实测能写入成功。
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;
                object appearance = null;
                System.Reflection.PropertyInfo voiceProp = null;
                foreach (var pi in bot.GetType().GetProperties(F))
                {
                    var nm = pi.Name.ToLowerInvariant();
                    if (nm.Contains("appearance")) { try { appearance = pi.GetValue(bot); } catch { } }
                }
                if (appearance == null) { warn?.Invoke("[EventRotator][语音] 复仇小鹿没有 appearance 段 ⇒ 跳过"); return; }
                foreach (var pi in appearance.GetType().GetProperties(F))
                    if (pi.Name.ToLowerInvariant().Contains("voice")) { voiceProp = pi; break; }
                if (voiceProp == null || !voiceProp.CanWrite) { warn?.Invoke("[EventRotator][语音] appearance 没有可写 Voice ⇒ 跳过"); return; }

                var dictType = voiceProp.PropertyType;
                if (!dictType.IsGenericType) { warn?.Invoke("[EventRotator][语音] Voice 不是字典 ⇒ 跳过"); return; }
                var keyType = dictType.GetGenericArguments()[0];
                var valType = dictType.GetGenericArguments()[1];
                var fresh = Activator.CreateInstance(dictType) as IDictionary;
                if (fresh == null) { warn?.Invoke("[EventRotator][语音] 构造 Voice 字典失败 ⇒ 跳过"); return; }
                object key, one;
                try { key = keyType == typeof(string) ? (object)ZryachiyVoiceId : Activator.CreateInstance(keyType, ZryachiyVoiceId); } catch { key = ZryachiyVoiceId; }
                try { one = valType == typeof(double) ? (object)1.0 : Convert.ChangeType(1, valType); } catch { one = 1.0; }
                fresh[key] = one;
                voiceProp.SetValue(appearance, fresh);
                info?.Invoke($"[EventRotator][语音] ✔ 复仇小鹿 voice → {ZryachiyVoiceId}（扎里亚奇语音；原来是 scav {ScavVoiceId}）");
                return;
            }
            catch (Exception ex) { warn?.Invoke("[EventRotator][语音] 修正失败（不影响其它功能）: " + ex.Message); }
        }
    }
}
