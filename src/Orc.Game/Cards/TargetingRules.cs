using Orc.Cards;
using Orc.Core;
using Orc.Game.Players;

namespace Orc.Game.Cards;

/// <summary>
/// 「指向」规则助手（引擎级通用接入点）：一次「某方指向某卡」的最终确定。
/// <para>语义边界：目标**最终确定**即触发——效果选靶在最终名单确定时、指挥攻击在复验通过并真正发起后；
/// 目标候选枚举/未选定/取消不触发；后续效果被反制或动作失败**不回滚**已发生的指向。</para>
/// <para>承载：被指向方（<see cref="UnitCard"/> / <see cref="Hq"/>）各持一个具名「指向触发器」，
/// 其默认 handler 声明具名动作缝 <see cref="SeamName"/>（C# 侧可挂前置包装器）；
/// 词条行为经效果预制体 <c>injects</c> 按名订阅（每个目标各一次）。</para>
/// </summary>
public static class TargetingRules
{
    /// <summary>「指向触发器」标识名（具名触发面——供效果预制体 <c>injects</c> 按名解析）。</summary>
    public const string TriggerName = "指向触发器";

    /// <summary>「指向触发器」默认 handler 上的具名动作缝（wrapper 插入点）。</summary>
    public const string SeamName = "指向后";

    /// <summary>指向种类：卡牌效果（最终选定目标）。</summary>
    public const string KindEffect = "effect";

    /// <summary>指向种类：玩家指挥攻击。</summary>
    public const string KindCommand = "command";

    /// <summary>
    /// 发起一次「指向」通知（每个目标一次）：只对 <see cref="UnitCard"/> 与 <see cref="Hq"/> 生效；
    /// 其它卡型＝无操作（本版接入面覆盖单位与总部）。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine 或 target 为 null。</exception>
    public static Task NotifyAsync(
        LogicEngine engine,
        Card target,
        Card? source,
        string kind,
        Card? triggerCard = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        return target switch
        {
            UnitCard unit => unit.InvokeTargetingTriggerAsync(source, kind, triggerCard, ct),
            Hq hq => hq.InvokeTargetingTriggerAsync(source, kind, triggerCard, ct),
            _ => Task.CompletedTask,
        };
    }
}
