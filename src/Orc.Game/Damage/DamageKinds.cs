namespace Orc.Game.Damage;

/// <summary>
/// 伤害种类（R11 三层）：触发端按来源分三条具名触发器，处理端共用一条。
/// 值写入 <c>DamageEventView.Kind</c> / <c>DamageApplyView.Kind</c>，三层载荷口径一致。
/// </summary>
public static class DamageKinds
{
    /// <summary>卡牌对战伤害（真实指挥对战；重甲只减这一类）。</summary>
    public const string Combat = "combat";

    /// <summary>效果伤害（指令 / <c>EffectRuntime.DamageAsync</c>）。</summary>
    public const string Effect = "effect";

    /// <summary>战斗伤害（<c>EffectRuntime.BattleAsync</c>；无射程/守护/伏击/反击资格）。</summary>
    public const string Battle = "battle";

    /// <summary>是否三值之一（写入边界的防拼写漂移校验）。</summary>
    public static bool IsKnown(string? kind)
        => kind is Combat or Effect or Battle;
}
