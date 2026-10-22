using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 部署位置候选判定器（R4a；<c>deploy.position.candidates</c>）：
/// （玩家, 单位）→ 可插入空隙列表（跨线整体：己方支援线 ＋〔词条允许且前线无敌方单位时〕前线）。
/// <para>候选是**临时生成**的 <see cref="FormationGap"/>（N 个元素 ⇒ N+1 个，满员＝空）；占用变化即失效。</para>
/// <para>可改写：本条目为受控判定器——包装器改写即整体替换"能否上前线/候选集合"的规则（单点生效）。</para>
/// 无状态：只读战场布局；不持有跨调用可变状态。
/// </summary>
internal sealed class DeployPositionCandidatesJudicator : Judicator<DeployPositionCandidatesJudicator.CandidatesRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（玩家, 单位）→ 候选空隙。</summary>
    public delegate IReadOnlyList<FormationGap> CandidatesRule(Player owner, UnitCard unit);

    private readonly Battlefield _battlefield;
    private readonly Func<Player, Player?> _enemyOf;

    /// <summary>创建部署候选判定器（装配期注入对局级只读设施）。</summary>
    /// <exception cref="ArgumentNullException">battlefield / enemyOf 为 null。</exception>
    internal DeployPositionCandidatesJudicator(Battlefield battlefield, Func<Player, Player?> enemyOf)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(enemyOf);
        _battlefield = battlefield;
        _enemyOf = enemyOf;
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(CandidatesRule handler)
        => args => new object[] { handler(Unpack<Player>(args, 0), Unpack<UnitCard>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);

    /// <summary>候选求值（支援线恒可选；前线＝词条 <c>deploy.frontline</c> 且前线无存活敌方单位）。</summary>
    private IReadOnlyList<FormationGap> Evaluate(Player owner, UnitCard unit)
    {
        var gaps = new List<FormationGap>();
        gaps.AddRange(_battlefield.GetSupportLine(owner).GetInsertionGaps());

        if (KeywordProgramRuntime.Resolve(unit, "deploy.frontline", 0) != 0 && !EnemyHoldsFrontline(owner))
        {
            gaps.AddRange(_battlefield.FrontLine.GetInsertionGaps());
        }

        return gaps;
    }

    /// <summary>前线是否存在存活敌方单位（与 <c>move.frontline-enemy</c> 同口径）。</summary>
    private bool EnemyHoldsFrontline(Player owner)
    {
        var enemy = _enemyOf(owner);
        if (enemy is null)
        {
            return false;
        }

        foreach (var member in _battlefield.FrontLine.Members)
        {
            if (member is UnitCard candidate
                && candidate.TryGetData<UnitStateData>(out var state)
                && !state.IsDestroyed
                && ReferenceEquals(candidate.Owner, enemy))
            {
                return true;
            }
        }

        return false;
    }
}
