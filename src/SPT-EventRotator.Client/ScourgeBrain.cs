using EFT;

namespace EventRotator.ScourgeAi;

/// <summary>
/// ★★★ 灾厄小队专用脑（掉帧修复 A）：行为 100% 继承 <see cref="ArenaFighterLayersStrategy"/>，
/// **只把 <see cref="ShortName"/> 换成 "ScourgeBrain"**。
///
/// <para><b>为什么必须换名</b>（audit/灾厄掉帧-定位.md）：
/// 我们原来直接 `new ArenaFighterLayersStrategy(owner)`，而 SAIN 对脑名 "ArenaFighter" 是
/// **无角色门槛**预注册了 6 个战斗层（BrainAssignment.AddCustomLayersToBrains(new[]{ "ArenaFighter" }, …)）；
/// BigBrain 的层注入按 <c>BaseBrain.ShortName()</c> 精确匹配 ⇒ 同一个脑上并存
/// 「SAIN 6 层 + ScourgeAi 3 层」⇒ 每帧多 6 次层判定、高优先层抢决策 ⇒ 掉帧 / 行为被抢。
/// <c>SainExclude</c> 只能挡"建 SAIN 组件"，撤不掉已注册的层。
///
/// <para><b>为什么继承可行</b>（Assembly-CSharp 实测）：
/// <c>ArenaFighterLayersStrategy : BaseBrain</c>，**非 sealed**，<c>public ArenaFighterLayersStrategy(BotOwner owner)</c>，
/// <c>public virtual string ShortName()</c> 在该类里 override 成字面量 "ArenaFighter"。
/// 我们 override 成 "ScourgeBrain" ⇒ ScourgeAi 的 3 层注册到新名，SAIN 那 6 层不再匹配。
/// AI 能力（决策/战斗层全继承）一点不少，只是换了个"脑名"。</para>
/// </summary>
internal sealed class ScourgeBrain : ArenaFighterLayersStrategy
{
    /// <summary>BigBrain / ScourgeAi 用来匹配层注册的脑短名。</summary>
    internal const string BrainName = "ScourgeBrain";

    public ScourgeBrain(BotOwner owner) : base(owner) { }

    /// <summary>★ 关键：返回新名字，不再叫 "ArenaFighter"（否则 SAIN 预注册的 6 层会照样命中）。</summary>
    public override string ShortName() => BrainName;
}
