using Orc.Cards;
using Orc.Game.Board;
using Orc.Game.Cards;

namespace Orc.Game.Actions;

/// <summary>
/// 卡牌移动动作（S6/S10 动作接口）。
/// <para>
/// 契约留在宿主侧（R17 第 5 条：<c>I*Action</c> 接口是替换的固定边界）——实现（<c>MoveAction</c>）已迁至
/// <c>orc.foundation.actions</c>，但宿主不能引用插件程序集，故接口与两端描述只在此处声明。
/// </para>
/// <para>落位＝插入空隙（有序列表模型）：目标线内的位置由 <see cref="FormationGap.Ordinal"/> 给出，右侧元素顺移。</para>
/// </summary>
public interface IMoveAction
{
    /// <summary>被指挥移动的底层动作（入口：<c>DispatchMoveAsync</c>；资源收尾由入口经 <c>OperateFinalizer</c> 完成）。</summary>
    Task<ActionResult> CommandAsync(
        UnitCard unit, FormationGap destination, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>效果移动的底层动作（入口：被控位移／撤退；零资源消耗、不受压制阻断）。</summary>
    Task<ActionResult> EffectAsync(
        UnitCard unit, FormationGap destination, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>
    /// 两端触发器（≥1 触发端 + 恰 1 处理端）：由实现自报、宿主按目录装配。
    /// 视图类型随实现迁出宿主程序集，故此处以类型擦除的 <see cref="ActionTriggerEnd"/> 承载。
    /// </summary>
    IReadOnlyList<ActionTriggerEnd> Ends { get; }
}

/// <summary>
/// 卡牌移动的**动作服务面**（S4）：处理端 handler 访问对局的受控写面——具名操作、不交出管理器实例。
/// <para>
/// 唯一调用者＝移动动作默认处理端 handler（S4 对照表）。只写**动作状态**（所在线／位置）并发射
/// 移动归属信号 <c>unit.position.changed</c>；不写行动状态（费用／<c>CanMove</c>/<c>CanAttack</c>/<c>unit.acted</c>）。
/// </para>
/// </summary>
public interface IMoveActionServices
{
    /// <summary>
    /// 落地一次移动：从旧线移除 → 按空隙插入新线 → <c>unit.position.changed</c>（恰一次）。
    /// 不可落地（无位置／已毁／空隙失效／同位置）＝返回 <c>false</c>、零变更零发射。
    /// </summary>
    Task<bool> LandMoveAsync(UnitCard unit, FormationGap destination, CancellationToken ct = default);
}
