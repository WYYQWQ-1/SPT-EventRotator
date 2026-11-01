namespace EventRotator.ScourgeAi;

/// <summary>
/// 本 mod 两个自定义角色的枚举值（= Patcher/Patcher.cs 的 13790/13791）。
/// 抄自 danauraborealis/Scourge 的 <c>src/Shared/ScourgeRoles.cs</c>，只把值换成本 mod 的。
/// 战术层全部用 <see cref="IsScourge"/> 做门控 ⇒ **只对灾厄小队生效**，不影响任何原版 bot。
/// </summary>
public static class ScourgeRoles
{
    public const string Boss = "bossScourge";
    public const string Follower = "followerScourge";
    public const int BossValue = 13790;
    public const int FollowerValue = 13791;

    public static bool IsScourge(int role) => role == BossValue || role == FollowerValue;
}
