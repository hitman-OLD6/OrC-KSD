using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;

namespace Orc.Game.Actions;

/// <summary>
/// 死亡动作（S8/S10 动作接口）：处理端可改写是否死亡、击杀者归属、处置指向。
/// </summary>
public interface IDeathAction
{
    /// <summary>死亡**触发端**。</summary>
    Trigger<DeathTriggerView> Trigger { get; }

    /// <summary>死亡**处理端**（<c>Rewrite</c> 单改写位 / <c>Veto</c> 免死守卫 / <c>Apply</c> 死亡链）。
    /// 插件/内置词条经此注册改写 handler（替换实现后仍可访问）。</summary>
    Trigger<DeathHandleView> Handle { get; }

    /// <summary>执行一次死亡：触发端 → 处理端 →（<c>Disposition</c> 非空时）入口分支执行处置。</summary>
    Task<ActionResult> ExecuteAsync(
        UnitCard unit, Card? killer = null, Card? triggerCard = null, CancellationToken ct = default);
}

/// <summary>
/// 死亡的**动作服务面**（S4）：处理端 handler 访问对局的受控写面——具名操作、不交出管理器实例。
/// <para>
/// 唯一调用者＝死亡动作默认处理端 handler。只发死亡链归属信号（<c>card.dying</c>／<c>card.died</c>）；
/// 处置（回手等）不在此面——由动作层包装经子动作完成。
/// </para>
/// </summary>
public interface IDeathActionServices
{
    /// <summary>
    /// 执行统一死亡链（槽位释放 → 置毁 → <c>card.dying</c> → 亡计结算 → 词条注销 → 修饰清理 →
    /// 钳击失效 → 效果卸载 → 位置置空 → 以改写后的击杀者发 <c>card.died</c>）。
    /// </summary>
    Task ApplyDeathAsync(UnitCard unit, Card? killer, CancellationToken ct = default);

    /// <summary>
    /// 回手组合的取件＋新建（解析定义 id → 新建同定义实例）；不可用（无卡牌服务／定义未注册）＝<c>null</c>。
    /// 加入手牌本身由动作层包装经「加入手牌」子动作完成。
    /// </summary>
    Task<CardBase?> CreateReturnInstanceAsync(UnitCard unit, CancellationToken ct = default);
}
