using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// K3 复验消重（C7+C8）游戏层测试：共享 leg 资格（move.leg.eligibility／attack.leg.eligibility）＋推进前置
/// （move.frontline-enemy）与可用性聚合、复验判定器的消重等价断言（同调矩阵）＋ moding 演示（leg 两分区条目＋C8 条目）＋
/// 契约与独立构造路径。
/// 矩阵口径（需求 §五·R5 1.1）：每个条件在其适用的全部调用点各 ≥1 例——
/// owner 非当前／owner 无效／已毁／费用不足＝各 4 例；行动标记 false＝按动作分（移动 2、攻击 2）；被压制＝4 例；
/// 位置非法（源非支援线）＝移动 2 例；前线有敌（C8）＝移动 2 例；攻击目标非法（C1 复用）＝攻击 2 例；
/// 正向＝每动作「可用性 Available＋复验 Valid」；优先级（多条件同时失败）＝每调用点 ≥1 例。
/// 断言层次：可用性侧经公开查询 GetCommandAvailability（取值级）；复验侧经执行前复验公开承载（触发器验证面）——
/// 可经真实指挥流程构造的格（费用不足／被压制／C8）另附真实流程拒绝/通过断言（BeginCommandAsync → 交互 → 执行前复验）。
/// moding 演示：改写 → 两调用点同步行为断言（含对侧动作不受影响的分区精确性）→ 注销回退 → 回退后恢复断言；
/// 复验侧经真实指挥流程（执行前复验拒绝/通过）；leg 演示取「条件豁免/局部改写」形态（如「移动无视被压制」）。
/// 链路口径：一律经判定器条目句柄／公开流程面——不直连判定器实例、不直调内部规则（单源）。
/// </summary>
public class JudicatorRevalidationDedupTests
{
    // ==================== 辅助 ====================

    /// <summary>移动复验（执行前复验公开承载——触发器验证面）。</summary>
    private static bool MoveRecheck(Match match, UnitCard unit, object oldPos, object newPos)
    {
        var oldLine = oldPos switch
        {
            BattleLine bl => bl,
            FormationGap gap => gap.Line,
            BoardPosition position => position.Line,
            _ => null,
        };

        if (oldLine is null || newPos is not FormationGap newGap)
        {
            return false;
        }

        var oldPosition = new BoardPosition(oldLine, oldLine.IndexOf(unit));
        return match.CommandManager.UnitMoveTrigger.Validate(new[] { unit.Ref, oldPosition.Ref, new BoardPosition(newGap.Line, newGap.Ordinal).Ref });
    }
}
