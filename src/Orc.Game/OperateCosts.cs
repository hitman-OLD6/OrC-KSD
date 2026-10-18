using Orc.Game.Cards;
using Orc.Game.Managers;

namespace Orc.Game;

/// <summary>
/// 行动费共享单元（K4·B10 扣费写点统一——行动费「取值」全库单源供给）：
/// 有效值读取单源（<see cref="Effective"/>——G5 有效值口径）＋ 扣费动作单源（<see cref="DeductAsync"/>——行动方扣除、恰一次）；
/// 判定侧（<c>LegEligibilityJudicator</c> 条目）与扣费侧（<c>CommandManager</c> 内 FinalizeMove／FinalizeAttack 两写点）
/// 共用同一取值入口——行动费取值表达式全库仅存一处（写点不再内联取值表达式；防旁路直读）。
/// 读取节奏保持现状：判定读一次、扣费读一次（两读之间修饰值变化可反映到扣费额——既有语义；
/// 不引入「判定时缓存费用值供扣费复用」等读取节奏改变）。
/// E1-25 后续：扣费经资源管理器**通用入口**（<c>ChangePointsAsync</c>——发 <c>point.changed</c>）；
/// 脱局（未装配资源管理器）＝降级为既有直写（保持独立构造场景行为）。
/// 与 <see cref="HandLimitBurn"/>（K0·B12）同级的 B 类共享单元先例（根级、internal——不新增对外公共接口）。
/// 无状态、无装配面（静态单元）；不判定器化（无 moding 通道——费用特例承载点＝判定面 K3 leg 条目＋数值面既有费用修饰链〔G5〕）。
/// </summary>
internal static class OperateCosts
{
    /// <summary>
    /// 行动费有效值（G5 有效值读取单源——判定与扣费共用；全库唯一取值表达式所在）。
    /// 下限 0：减费修饰（效果/光环）叠加不得把行动花费压成负数——负费会反向加指挥点并可无限行动。
    /// </summary>
    internal static int Effective(UnitCard unit)
        => Math.Max(0, unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));

    /// <summary>
    /// 行动费扣减（扣费动作单源——行动方扣除行动费有效值、恰一次；两写点〔FinalizeMove／FinalizeAttack〕共用本单元）。
    /// 读值经 <see cref="Effective"/>（与判定读取同源，下限 0）；归口：资源管理器通用入口
    /// （<c>ChangePointsAsync</c>，增量取负）；不可达＝直写兜底。
    /// </summary>
    internal static async Task DeductAsync(UnitCard unit, CancellationToken ct = default)
    {
        var owner = unit.Owner!;
        var cost = Effective(unit);
        if (ResourceManager.ResolveFor(unit) is { } manager)
        {
            await manager.ChangePointsAsync(owner, -cost, PointChangeKind.Add, ct).ConfigureAwait(false);
            return;
        }

        owner.Points -= cost; // 脱局兜底（未装配资源管理器）：保持既有直写语义
    }
}
