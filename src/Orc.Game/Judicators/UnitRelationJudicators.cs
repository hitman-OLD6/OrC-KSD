using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// **相邻关系判定器**（通用原子判定；<see cref="JudicatorNames.UnitAdjacent"/>）：
/// 签名＝（卡A, 卡B）→ bool——两者均在场、同一条线、槽位索引差 1；任一方无位置＝false。
/// <para>
/// 纯几何关系：不含归属/兵种过滤（那些由 <see cref="JudicatorNames.UnitSide"/> /
/// <see cref="JudicatorNames.UnitTypeMatch"/> 或调用方组合）；口径与
/// <see cref="GameEnvironment.AreAdjacent(Card, Card)"/> **同一实现**（静态形态转发，无第二份邻位算法）。
/// </para>
/// <para>无状态：不持有对局状态，全部经注入的战场只读读取。</para>
/// </summary>
public sealed class UnitAdjacentJudicator : Judicator<UnitAdjacentJudicator.AdjacentRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（卡A, 卡B）→ 是否相邻。</summary>
    public delegate bool AdjacentRule(Card a, Card b);

    private readonly AdjacentRule _rule;

    /// <summary>创建判定器（战场为邻位判定的布局真源）。</summary>
    /// <param name="battlefield">战场（三线布局）。</param>
    /// <exception cref="ArgumentNullException">battlefield 为 null。</exception>
    public UnitAdjacentJudicator(Battlefield battlefield)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        _rule = (a, b) => GameEnvironment.AreAdjacent(battlefield, a, b);
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(AdjacentRule handler)
        => args => new object[] { handler(Unpack<Card>(args, 0), Unpack<Card>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(_rule)(args);
}

/// <summary>
/// **兵种判定器**（通用原子判定；<see cref="JudicatorNames.UnitTypeMatch"/>）：
/// 签名＝（卡, 兵种标识）→ bool——卡持有单位数据且兵种集含该标识（序数不敏感）；非单位/标识空白＝false。
/// <para>参数化兵种（而非写死某兵种）：同一判定器服务「是否步兵/坦克/炮兵」等全部同类卡面条件。</para>
/// <para>无状态：只读单位数据组件。</para>
/// </summary>
public sealed class UnitTypeJudicator : Judicator<UnitTypeJudicator.TypeRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（卡, 兵种标识）→ 是否属于该兵种。</summary>
    public delegate bool TypeRule(Card card, string unitType);

    private readonly TypeRule _rule;

    /// <summary>创建判定器（默认规则＝单位数据组件兵种集包含判定）。</summary>
    public UnitTypeJudicator()
    {
        _rule = (card, unitType) =>
        {
            if (string.IsNullOrWhiteSpace(unitType) || !card.TryGetData<UnitStateData>(out var state))
            {
                return false;
            }

            foreach (var type in state.UnitTypes)
            {
                if (string.Equals(type, unitType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        };
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(TypeRule handler)
        => args => new object[] { handler(Unpack<Card>(args, 0), Unpack<string>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(_rule)(args);
}

/// <summary>
/// **归属判定器**（通用原子判定；<see cref="JudicatorNames.UnitSide"/>）：
/// 签名＝（视角卡, 目标卡）→ bool——两者归属同一玩家（「友方」）；任一归属不可达＝false。
/// <para>不含任何兵种/位置条件：可独立复用于「友方单位」「敌方单位」等相对性判定。</para>
/// <para>无状态：只读卡归属（未装载/独立构造＝null＝false，不抛错）。</para>
/// </summary>
public sealed class UnitSideJudicator : Judicator<UnitSideJudicator.SideRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（视角卡, 目标卡）→ 是否同一归属。</summary>
    public delegate bool SideRule(Card viewer, Card target);

    private readonly SideRule _rule;

    /// <summary>创建判定器（默认规则＝归属引用同一性）。</summary>
    public UnitSideJudicator()
    {
        _rule = (viewer, target) =>
        {
            var viewerOwner = OwnerOf(viewer);
            var targetOwner = OwnerOf(target);
            return viewerOwner is not null && ReferenceEquals(viewerOwner, targetOwner);
        };
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(SideRule handler)
        => args => new object[] { handler(Unpack<Card>(args, 0), Unpack<Card>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(_rule)(args);

    /// <summary>卡 → 归属玩家解析（单位卡经卡基归属；HQ 自带归属；其余＝null）。</summary>
    private static Player? OwnerOf(Card card) => card switch
    {
        Hq hq => hq.Owner,
        CardBase cardBase => cardBase.Owner,
        _ => null,
    };
}
