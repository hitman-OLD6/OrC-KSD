using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// K4·B10 行动费统一（扣费写点与读取口径单源）游戏层测试：
/// ①同值口径＋恰一次（≥2 例）——同一修饰状态下「判定读取所用值＝实际扣取值」，每写点各 ≥1 例
/// （move＝FinalizeMove／attack＝FinalizeAttack）；两例均以「点数恰等有效值＝临界通过＋扣费后点数恰＝0」夹逼锁定
/// （判定读基础值/扣费读基础值/重复扣费均会使断言失败）。
/// ②「行动费条件豁免」moding 演示（move.leg.eligibility）——改写→行动费判定行为变化（可用性＋复验两点同步）→
/// 注销回退；含分区精确性（attack 侧不受影响）与局部精确性（豁免仅限费用——被压制仍拦截）。
/// 口径：驱动经真实指挥流程（BeginCommandAsync → 交互 → 执行）；链路口径＝判定器条目/公开流程面（不直调内部规则）。
/// 说明：演示不做「豁免后真实执行」——费用不足下豁免真实执行必然透支（扣费读有效值、无下限钳制——既有边界行为），
/// 超出本演示聚焦面；「leg 改写→真实流程成功→扣费按有效值」机制证据引用 K3 演示一/二（同机制不同条件维度，独立呈现见实现记录）。
/// </summary>
public class OperateCostUnificationTests
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
