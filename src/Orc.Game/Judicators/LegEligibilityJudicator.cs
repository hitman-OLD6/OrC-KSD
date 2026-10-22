using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Commanding;

namespace Orc.Game.Judicators;

/// <summary>
/// leg 资格失败原因（K3；C7 共享『leg 资格』条件的结构化结果词汇）：
/// 通过＝null（空标记）；失败＝本枚举成员——可用性侧直接映射为 <c>CommandBlockReason</c>（映射表见 CommandManager，
/// 映射为纯读取、非条件重判）；复验侧按 bool 语义消费（非 null＝Invalid）。
/// 词汇对齐「leg 条件」清单（owner==current／!destroyed／CanMove·CanAttack／被压制／行动费／位置〔源∈支援线〕）。
/// </summary>
public enum LegEligibilityFailure
{
    /// <summary>非己方回合（当前行动方未就绪，或单位归属≠当前行动方）。</summary>
    NonOwnerTurn,

    /// <summary>归属无效（Owner＝null——无归属单位不可被指挥）。</summary>
    OwnerInvalid,

    /// <summary>单位已死亡（尸体不可被指挥）。</summary>
    UnitDead,

    /// <summary>行动状态不可用（可移动/可攻击 bool＝false——按动作面）。</summary>
    FlagFalse,

    /// <summary>被压制（不能移动或攻击）。</summary>
    Suppressed,

    /// <summary>行动费不足（所有者的指挥点数 ＜ 行动费有效值——G5 有效值读取口）。</summary>
    PointShortage,

    /// <summary>源位置不在支援线（仅推进；move 消费——位置参数由调用点提供：可用性＝当前位置、复验＝原槽位）。</summary>
    PositionNotInSupportLine,
}

/// <summary>
/// leg 资格判定器（K3；C7 共享『leg 资格』条件承载——move/attack 分区条目、一份逻辑×两条目）：
/// 承载「动作执行前置资格」的共享条件序列（owner==current／!destroyed／CanMove·CanAttack／被压制／行动费／
/// 位置〔源∈支援线，move〕），供四个调用点统一取用——可用性聚合（ComputeMove/AttackAvailability）与
/// 复验判定器（Move/AttackRevalidationJudicator）同源（单源、无第二真源）。
/// 条件判定逻辑单源（一份实现、双实例配置：move＝检查位置、attack＝不检查）；短路顺序＝可用性侧现状优先级
/// （current→owner→destroyed→can→suppressed→cost→position——两侧现状均不改变；复验降维 bool 消费）。
/// 输出契约：结构化失败原因或通过标记（null＝通过）；载荷契约：<c>[unit, position]</c>（position 可空——attack 忽略）。
/// 改写＝moding（分区条目：改写单动作条目仅影响该动作；条目为「特例改写」的天然承载点——如「移动无视被压制」）；
/// 四调用点每次调用经统一解析点（动态取栈顶）。
/// 无状态：不持有跨调用可变状态；装配期注入对局级只读设施（当前行动方提供器／战场布局——仅只读使用）。
/// </summary>
internal sealed class LegEligibilityJudicator : Judicator<LegEligibilityJudicator.LegEligibilityRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（单位, 源位置）→ leg 资格结果（null＝通过）。</summary>
    public delegate LegEligibilityFailure? LegEligibilityRule(UnitCard unit, BoardPosition? position);

    private readonly Func<Player?> _currentPlayerProvider;
    private readonly Battlefield? _battlefield;
    private readonly bool _isMove;
    private readonly Func<Player, bool>? _turnOwner;

    /// <summary>创建 leg 资格判定器（装配期注入对局级只读设施引用）。</summary>
    /// <param name="currentPlayerProvider">当前行动方提供器（延迟读取——回合未就绪判定）。</param>
    /// <param name="battlefield">战场（布局读取——move 的位置条件〔源∈支援线〕；attack＝null）。</param>
    /// <param name="isMove">动作面（true＝move：含位置条件；false＝attack：位置不适用）。</param>
    /// <param name="turnOwner">回合归属判定器（R4；缺省＝null＝按 currentPlayerProvider 引用比较）。</param>
    /// <exception cref="ArgumentNullException">currentPlayerProvider 为 null；isMove 时 battlefield 为 null。</exception>
    internal LegEligibilityJudicator(
        Func<Player?> currentPlayerProvider,
        Battlefield? battlefield,
        bool isMove,
        Func<Player, bool>? turnOwner = null)
    {
        ArgumentNullException.ThrowIfNull(currentPlayerProvider);
        if (isMove)
        {
            ArgumentNullException.ThrowIfNull(battlefield);
        }

        _currentPlayerProvider = currentPlayerProvider;
        _battlefield = battlefield;
        _isMove = isMove;
        _turnOwner = turnOwner;
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(LegEligibilityRule handler)
        => args =>
        {
            var result = handler(Unpack<UnitCard>(args, 0), Unpack<BoardPosition?>(args, 1));
            return result is { } failure
                ? new object[] { failure }
                : new object[] { null! }; // 通过标记：数组元素运行时承载 null（判定器输出约定的合法值）
        };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);

    /// <summary>leg 资格求值（条件序列单源；短路顺序＝可用性侧现状优先级——逐条与收编前一致）。</summary>
    private LegEligibilityFailure? Evaluate(UnitCard unit, BoardPosition? position)
    {
        var state = unit.GetData<UnitStateData>();
        var command = unit.GetData<CommandData>();

        var current = _currentPlayerProvider();
        if (current is null)
        {
            return LegEligibilityFailure.NonOwnerTurn; // 回合未就绪（当前行动方未定）
        }

        var owner = unit.Owner;
        if (owner is null)
        {
            return LegEligibilityFailure.OwnerInvalid; // 无归属单位不可被指挥
        }

        var ownerTurn = _turnOwner is not null ? _turnOwner(owner) : ReferenceEquals(owner, current);
        if (!ownerTurn)
        {
            return LegEligibilityFailure.NonOwnerTurn; // 仅限己方回合操作己方单位（R4：判定器同源）
        }

        if (state.IsDestroyed)
        {
            return LegEligibilityFailure.UnitDead; // 尸体不可被指挥
        }

        var canAction = _isMove ? command.CanMove : command.CanAttack;
        if (!canAction || KeywordProgramRuntime.Resolve(unit,_isMove ? "move.allowed" : "attack.allowed",1)==0)
        {
            return LegEligibilityFailure.FlagFalse;
        }

        if (KeywordRules.HasKeyword(unit, KeywordIds.Suppressed))
        {
            return LegEligibilityFailure.Suppressed; // A2：被压制（不能移动或攻击）
        }

        if (owner.Points < OperateCosts.Effective(unit))
        {
            return LegEligibilityFailure.PointShortage; // W2b：行动费读「有效值」（K4：经 OperateCosts 统一取值入口——判定/扣费同源、防旁路直读）
        }

        if (_isMove && !CommandMovementRules.CanStart(_battlefield!, unit, position))
        {
            return LegEligibilityFailure.PositionNotInSupportLine; // 仅推进：源位置须为支援线
        }

        return null; // 通过
    }
}
