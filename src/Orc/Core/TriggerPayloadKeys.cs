namespace Orc.Core;

/// <summary>
/// 触发器载荷键（R9；框架注入面）：与视图属性同名的载荷键约定。
/// <see cref="TriggerCard"/> 由执行链在**进入事件前**写入数据面（视图代理按名直读），
/// 语义＝当前 handler 的「触发者卡牌」（链上最后者胜）；与信号载荷的 <c>Source</c>（事件引发者）分工不同。
/// </summary>
public static class TriggerPayloadKeys
{
    /// <summary>触发者卡牌（值＝<see cref="Orc.Cards.Card"/> 对象引用；可空——无触发者时为 null）。</summary>
    public const string TriggerCard = "TriggerCard";
}
