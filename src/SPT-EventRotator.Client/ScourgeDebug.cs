using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using EFT;
using UnityEngine;

namespace EventRotator.Client
{
    /// <summary>
    /// ★★ 2026-10-31【一次性拿全诊断】灾厄小队运行时探针。
    /// 目的：用户反馈"首领站在原地看着你不动，随从正常"。仅靠日志里的换脑行判断不出根因，
    /// 因此这里把每个灾厄单位的**脑类型 / 当前层 / 当前决策 / 位置漂移量**周期性地打进 BepInEx 日志。
    /// <para>开关：F12 → 活动轮换器 →「★灾厄调试日志 (scourge debug)」，默认 **开**（本诊断包里）。</para>
    /// <para>成本：每 5 秒一次、只统计角色为 13790/13791 的单位，量级可忽略。</para>
    /// </summary>
    internal static class ScourgeDebug
    {
        private sealed class Entry
        {
            public BotOwner Bot;
            public int Role;
            public string BrainType;
            public Vector3 LastPos;
            public float LastLog;
            public bool Logged;
        }

        private static readonly List<Entry> _units = new List<Entry>();
        private static float _next;
        private static Action<string> _log;

        internal static void SetLog(Action<string> log) => _log = log;

        /// <summary>换脑成功后登记（由 BossScourgeClient 调用）。</summary>
        internal static void Register(BotOwner bot, string brainType)
        {
            try
            {
                if (bot == null) return;
                foreach (var e in _units) if (e.Bot == bot) { e.BrainType = brainType; return; }
                int role = 0;
                try { role = (int)bot.Profile.Info.Settings.Role; } catch { }
                _units.Add(new Entry
                {
                    Bot = bot,
                    Role = role,
                    BrainType = brainType,
                    LastPos = SafePos(bot)
                });
                _log?.Invoke($"[灾厄诊断] 登记单位 {Nick(bot)}｜角色={role}｜脑={brainType}");
            }
            catch { }
        }

        /// <summary>由 EventsOverlay.Update 周期调用（内部自带 5 秒节流）。</summary>
        internal static void Tick()
        {
            try
            {
                if (!Plugin.ScourgeDebugOn) return;
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 5f;
                if (_units.Count == 0) return;

                var sb = new StringBuilder();
                sb.Append("[灾厄诊断] 本局存活单位：");
                int alive = 0;
                for (int i = _units.Count - 1; i >= 0; i--)
                {
                    var e = _units[i];
                    if (e?.Bot == null) { _units.RemoveAt(i); continue; }
                    bool dead = false;
                    try { dead = e.Bot.IsDead || e.Bot.HealthController == null || !e.Bot.HealthController.IsAlive; } catch { dead = true; }
                    if (dead) { _units.RemoveAt(i); continue; }
                    alive++;

                    var pos = SafePos(e.Bot);
                    float moved = Vector3.Distance(pos, e.LastPos);
                    e.LastPos = pos;

                    string layer = CurrentLayer(e.Bot);
                    string decision = CurrentDecision(e.Bot);
                    sb.Append($"\n  · {Nick(e.Bot)} 角色={e.Role} 脑={e.BrainType} 层={layer} 决策={decision} 5秒位移={moved:F1}m 坐标=({pos.x:F0},{pos.y:F0},{pos.z:F0})");
                }
                if (alive > 0) _log?.Invoke(sb.ToString());
                else { _log?.Invoke("[灾厄诊断] 本局灾厄单位已全部死亡/消失"); _units.Clear(); }
            }
            catch { }
        }

        private static Vector3 SafePos(BotOwner b)
        {
            try { return b.Transform.position; } catch { return Vector3.zero; }
        }

        private static string Nick(BotOwner b)
        {
            try { return b.Profile?.Info?.Nickname ?? "?"; } catch { return "?"; }
        }

        private static object Field(object o, string name)
        {
            try
            {
                if (o == null) return null;
                var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f?.GetValue(o);
            }
            catch { return null; }
        }

        private static PropertyInfo Prop(Type t, string name)
        {
            try { return t?.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return null; }
        }

        /// <summary>尽量取出 BigBrain 当前层名（取不到就返回 ?）。</summary>
        private static string CurrentLayer(BotOwner b)
        {
            try
            {
                var brain = Field(b, "Brain") ?? Field(b, "BaseBrain");
                if (brain == null) return "?";
                var f = brain.GetType().GetField("CurrentLayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var layer = f?.GetValue(brain);
                if (layer == null)
                {
                    var p = Prop(brain.GetType(), "CurrentLayer");
                    layer = p?.GetValue(brain);
                }
                if (layer == null) return "?";
                var name = layer.GetType().GetMethod("GetName", Type.EmptyTypes)?.Invoke(layer, null) as string;
                return name ?? layer.GetType().Name;
            }
            catch { return "?"; }
        }

        /// <summary>当前决策（BotLogicDecision），取不到就返回 ?。</summary>
        private static string CurrentDecision(BotOwner b)
        {
            try
            {
                var brain = Field(b, "Brain") ?? Field(b, "BaseBrain");
                var agent = brain == null ? null : Field(brain, "Agent");
                var dec = agent == null ? null : Field(agent, "CurrentDecision");
                if (dec == null)
                {
                    var kind = b.BotFollower;
                    var last = Field(kind, "LastDecision");
                    return last?.ToString() ?? "?";
                }
                return dec.ToString();
            }
            catch { return "?"; }
        }
    }
}
