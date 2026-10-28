using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Model;

namespace Orc.Game.Actions;

/// <summary>
/// **入口分支**的动作服务面（S5；internal）：行动状态（费用 / <c>CanMove</c> / <c>CanAttack</c> /
/// <c>unit.acted</c>）只能由入口分支经本面的具名操作写，处理端禁写。
/// <para>
/// 宿主内部编排面，**不是插件契约**（不进 <c>orc.foundation.actions</c>）；经项目既有
/// <c>InternalsVisibleTo</c> 向测试项目开放。
/// 唯一调用者＝<see cref="OperateFinalizer"/>（不是各入口各写一遍）。
/// </para>
/// <para>
/// 服务方法**只写值、不判规则**：非坦克二选一清位、奋战/攻击次数决定 <c>CanAttack</c> 等规则
/// 由入口分支算完再传值；实现中出现 <c>HasUnitType(Tank)</c>/<c>Suppressed</c>/费用判定即为设计错误。
/// </para>
/// </summary>
internal interface IOperateEntryServices
{
    /// <summary>扣除行动费（行动方、恰一次；读行动费有效值）。</summary>
    Task DeductOperateCostAsync(UnitCard unit, CancellationToken ct = default);

    /// <summary>按本次动作消耗额度（只落值；规则由入口算——非坦克两额度各减一、坦克只减对应）。</summary>
    Task ConsumeOperateQuotaAsync(UnitCard unit, bool isMove, bool isTank, CancellationToken ct = default);

    /// <summary>发射 <c>unit.acted</c>（行动层信号；处理端不得发射）。</summary>
    Task EmitActedAsync(UnitCard unit, CancellationToken ct = default);
}

/// <summary>
/// 入口收尾差异（S5）：移动/攻击入口把差异编码为本结构后交给 <see cref="OperateFinalizer"/>。
/// </summary>
/// <param name="DeductCost">是否扣行动费。</param>
/// <param name="IsMove">本次动作是否为移动（false＝攻击）。</param>
/// <param name="IsTank">单位是否为坦克（决定额度消耗规则：非坦克两额度各减一、坦克只减对应）。</param>
/// <param name="EmitActed">是否发射 <c>unit.acted</c>（仍受「已阵亡不发射」约束）。</param>
internal readonly record struct OperateProfile(
    bool DeductCost, bool IsMove, bool IsTank, bool EmitActed);

/// <summary>
/// 行动入口的统一收尾单元（S5）：移动/攻击入口只构造 <see cref="OperateProfile"/>，
/// 三个具名操作（扣费/写位/发 <c>unit.acted</c>）的唯一调用者＝本类。
/// </summary>
internal sealed class OperateFinalizer
{
    private readonly IOperateEntryServices _services;

    internal OperateFinalizer(IOperateEntryServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>收尾：扣费（按 profile）→ 写两个行动位。顺序与既往「扣费 → 两 bool 更新」一致。</summary>
    internal async Task FinalizeAsync(UnitCard unit, OperateProfile profile, CancellationToken ct = default)
    {
        if (profile.DeductCost)
        {
            await _services.DeductOperateCostAsync(unit, ct).ConfigureAwait(false);
        }

        await _services.ConsumeOperateQuotaAsync(unit, profile.IsMove, profile.IsTank, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 行动后信号：<c>unit.acted</c> 仅从入口路径发射（已阵亡＝不发射，沿既有语义）。
    /// 独立于 <see cref="FinalizeAsync"/>，以保持「收尾 → 结果赋值 → 信号」的既有顺序。
    /// </summary>
    internal async Task EmitActedAsync(UnitCard unit, OperateProfile profile, CancellationToken ct = default)
    {
        if (!profile.EmitActed || unit.GetData<UnitStateData>().IsDestroyed)
        {
            return;
        }

        await _services.EmitActedAsync(unit, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="IOperateEntryServices"/> 的宿主实现（internal sealed；宿主内部编排面）。
/// 服务方法只写值——不含 Tank/Suppressed/费用规则分支。
/// </summary>
internal sealed class OperateEntryServices : IOperateEntryServices
{
    private readonly LogicEngine _engine;

    internal OperateEntryServices(LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc />
    public Task DeductOperateCostAsync(UnitCard unit, CancellationToken ct = default)
        => OperateCosts.DeductAsync(unit, ct);

    /// <inheritdoc />
    public async Task ConsumeOperateQuotaAsync(
        UnitCard unit, bool isMove, bool isTank, CancellationToken ct = default)
    {
        var command = unit.GetData<CommandData>();
        var (moveDelta, attackDelta) = isTank
            ? (isMove ? (-1, 0) : (0, -1))
            : (-1, -1);

        // 额度消耗的可改写点：默认方案先落数据面，注入 handler（如「机动」）就地改写后应用。
        var data = new Dictionary<string, object?>
        {
            ["Unit"] = unit,
            ["IsMove"] = isMove,
            ["IsTank"] = isTank,
            ["MoveDelta"] = moveDelta,
            ["AttackDelta"] = attackDelta,
        };

        // 额度消耗的可改写点＝引擎成员 <c>engine#action-budget</c>：成员边（v4 组件形态的「机动」等）就地改写
        // delta，调用方从**同一份数据面**读回（b4 起就是这条通路，§27 后不再有第二条旧触发器路径）。
        var ctx = new Context(data);
        await _engine.InvokeMemberAsync(EngineProcesses.ActionBudget, ctx, ct).ConfigureAwait(false);

        var finalMove = ctx.TryGet("MoveDelta", out var move) && move is int moved ? moved : moveDelta;
        var finalAttack = ctx.TryGet("AttackDelta", out var attack) && attack is int attacked ? attacked : attackDelta;
        command.SetMoveQuota(command.MoveQuota + finalMove);
        command.SetAttackQuota(command.AttackQuota + finalAttack);

        if (!isMove)
        {
            UnitTurnStatsTracker.RecordAttack(unit, OperateCosts.Effective(unit)); // 攻击仅指挥路径计入统计
        }
    }

    /// <inheritdoc />
    public Task EmitActedAsync(UnitCard unit, CancellationToken ct = default)
        => GameUpdates.EmitUnitActed(_engine, unit, ct);
}
