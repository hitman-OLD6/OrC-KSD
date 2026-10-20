namespace Orc.Output;

/// <summary>
/// 事件载荷可见性判定面（多人支持 S5）：导出段/事件流时，按「观察者」决定实体与条目的可见性。
/// <para>
/// 判定权在内核之外（游戏层知道手牌/卡组语义），本接口只表达契约：
/// ① <see cref="IsEntryHidden"/>＝条目级判定（如"对手抽牌"条目整体隐藏卡实体）；
/// ② <see cref="IsEntityVisible"/>＝实体级兜底（如实体属于对手手牌）。
/// </para>
/// <para>
/// 未提供实现＝不脱敏（单机/同进程观察者的既有行为，零改动）。
/// </para>
/// </summary>
public interface IEventVisibility
{
    /// <summary>
    /// 该条目的实体载荷对 <paramref name="viewer"/> 是否应整体隐藏。
    /// </summary>
    /// <param name="payload">条目的 data 字典。</param>
    /// <param name="viewer">观察者（游戏层解释；内核不约束类型）。</param>
    bool IsEntryHidden(IReadOnlyDictionary<string, object?> payload, object? viewer);

    /// <summary>单个实体/引用对 <paramref name="viewer"/> 是否可见（条目级未命中时的兜底）。</summary>
    bool IsEntityVisible(object? entity, object? viewer);
}
