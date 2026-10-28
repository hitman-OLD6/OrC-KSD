namespace Orc.Core.Model;

/// <summary>
/// 组件注销兜底的**唯一规则来源**（R1f／S8 第 3 条）：兜底信号恒含 <c>card.destroyed</c>、拒绝
/// <c>effect.removed</c>（销毁入口的完成通知，作为兜底会自指递归）。
/// <para>
/// 为什么单独抽一个类型（而不是在预制体层与 <see cref="Orc.Cards.Effect"/> 各写一份）：本次重构前同一条规则
/// 存在两份实现（<c>EffectPrefab.NormalizeDisposingTrigger</c> 与 <c>Effect.NormalizeDisposalHooks</c>），
/// 是已记录的漂移源。批次 3 的裁决是「规则只有一处、旧入口退化为薄适配」——旧类型本身要到批次 5 才退役，
/// 期间两个调用点都必须走本类，任何一处自行实现都算漂移。
/// </para>
/// <para>
/// 本类**不含任何优先级/位次语义**：框架注销边的位次由 <see cref="DeclarationOrder.FrameworkLast"/> 唯一确定
/// （该组件所有已声明挂载之后），与旧"保留槽"（触发器内部按 priority 排序的段）不是同一件事。
/// </para>
/// </summary>
public static class ComponentDisposalPolicy
{
    /// <summary>兜底信号的缺省值（恒含，不可移除）。</summary>
    public const string DefaultSignal = Updates.CardDestroyed;

    /// <summary>不得作为兜底信号的时机（自指）。</summary>
    public const string ForbiddenSignal = Updates.EffectRemoved;

    /// <summary>框架合成的注销成员名（组件实体上的保留名；作者不得声明 <c>framework.</c> 前缀的成员）。</summary>
    public const string DisposalMemberName = "framework.dispose";

    /// <summary>保留成员名前缀（框架专用：注销成员、一次性守卫等）。</summary>
    public const string ReservedPrefix = "framework.";

    /// <summary>是否为框架保留成员名。</summary>
    public static bool IsReservedMember(string name)
        => name.StartsWith(ReservedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 归一化兜底信号清单：<see cref="DefaultSignal"/> 恒在最前、去重保序、拒绝 <see cref="ForbiddenSignal"/>。
    /// </summary>
    /// <param name="signals">声明的兜底信号（null／空＝只用缺省项）。</param>
    /// <param name="ownerId">归属标识（效果 id／组件预制体 id；只用于错误消息）。</param>
    /// <exception cref="ArgumentException">含 null／空白项，或含 <see cref="ForbiddenSignal"/>。</exception>
    public static IReadOnlyList<string> NormalizeSignals(IEnumerable<string>? signals, string ownerId)
    {
        var list = new List<string> { DefaultSignal };
        if (signals is null)
        {
            return list;
        }

        foreach (var signal in signals)
        {
            if (string.IsNullOrWhiteSpace(signal))
            {
                throw new ArgumentException("兜底时机不能包含 null/空白项。", nameof(signals));
            }

            if (string.Equals(signal, ForbiddenSignal, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{ForbiddenSignal} 是显式销毁入口的完成通知，不能作为兜底时机（会与销毁入口自指）；归属：'{ownerId}'。",
                    nameof(signals));
            }

            if (!list.Contains(signal, StringComparer.Ordinal))
            {
                list.Add(signal);
            }
        }

        return list;
    }

    /// <summary>
    /// 校验单个兜底信号（预制体层：<c>disposal-signal</c> 字段）。
    /// </summary>
    /// <returns>null＝合法；非 null＝拒绝原因（由调用方决定异常类型）。</returns>
    public static string? ValidateSignal(string signal, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(signal))
        {
            return $"兜底信号不能为空（归属：'{ownerId}'）。";
        }

        if (string.Equals(signal, ForbiddenSignal, StringComparison.Ordinal))
        {
            return $"预制体 '{ownerId}' 把 {ForbiddenSignal} 声明为兜底信号——"
                   + "它是显式销毁入口的完成通知，作为兜底会自指（R1f 的装载期拒绝）。";
        }

        return null;
    }
}
