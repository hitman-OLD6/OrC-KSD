using Orc.Core;

namespace Orc.Game.Cards;

/// <summary>
/// 「指向」动作视图（引擎级通用接入点的载荷）：一次「某方指向某卡」的最终确定，
/// 由效果选靶的最终名单 / 交互落定、指挥攻击的发起侧触发（每个目标各一次）。
/// <para>与「受伤」无关：指向只表达“被盯上”，后续动作成败不回滚。</para>
/// <para>默认 handler 声明具名动作缝 <see cref="TargetingRules.SeamName"/>，wrapper 可在此插入逻辑；
/// 词条（csx）经效果预制体 <c>injects</c> 按名订阅「指向触发器」。</para>
/// </summary>
[ContextView]
public class TargetingView
{
    /// <summary>指向方（来源卡；可为空——无来源的指向不触发通道语义）。</summary>
    [Optional]
    [Read]
    public virtual object? Source { get; set; }

    /// <summary>被指向的卡（目标）。</summary>
    [Optional]
    [Read]
    public virtual object? Target { get; set; }

    /// <summary>指向种类：<see cref="TargetingRules.KindEffect"/> / <see cref="TargetingRules.KindCommand"/>。</summary>
    [Read]
    public virtual object? Kind { get; set; }

    /// <summary>触发者卡（主动指向＝空；效果引发＝效果宿主卡）。</summary>
    [Optional]
    [Read]
    public virtual object? TriggerCard { get; set; }

    /// <summary>宿主（本卡；与触发器所有者一致）。</summary>
    [Optional]
    [Read]
    public virtual object? Host { get; set; }
}
