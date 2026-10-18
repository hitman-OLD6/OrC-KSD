using Orc.Core;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 回合归属准入判定器（R4）：回答「该玩家是否为当前行动方」。
/// <para>三处入口（打牌 / 移动·攻击 leg / 反制使用）同源调用；各入口保留自己的拒绝类别映射。</para>
/// <para>无状态：仅经装配期注入的当前行动方提供器只读读取；玩家域可包装（额外行动类效果）。</para>
/// </summary>
internal sealed class ActionTurnOwnerJudicator : Judicator<ActionTurnOwnerJudicator.Rule>
{
    /// <summary>强类型 delegate（该判定器签名）：（玩家）→ 是否为其回合。</summary>
    /// <param name="player">被判定玩家（不得为 null）。</param>
    /// <returns>true＝当前行动方。</returns>
    public delegate bool Rule(Player player);

    private readonly Rule _rule;
    private readonly Func<Player?> _currentPlayerProvider;

    /// <summary>创建判定器。</summary>
    /// <param name="currentPlayerProvider">当前行动方提供器（延迟读取；未就绪＝null）。</param>
    /// <exception cref="ArgumentNullException">currentPlayerProvider 为 null。</exception>
    internal ActionTurnOwnerJudicator(Func<Player?> currentPlayerProvider)
    {
        ArgumentNullException.ThrowIfNull(currentPlayerProvider);
        _currentPlayerProvider = currentPlayerProvider;
        _rule = IsOwnerTurn;
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(Rule handler)
        => args => new object[] { handler(Unpack<Player>(args, 0)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(_rule)(args);

    private bool IsOwnerTurn(Player player) => ReferenceEquals(player, _currentPlayerProvider());
}
