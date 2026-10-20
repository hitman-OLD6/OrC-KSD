using Orc.Core;
using Orc.Game.Cards.Loading;

namespace Orc.Packages.Loading;

/// <summary>
/// 加载批次的**报告收集器**（随 <see cref="CardLoadBatchView"/> 传入触发器执行会话）：
/// 批次执行体把结果（或结构性失败）写回此处，调用方在会话结束后读取——避免依赖事件流的返回值承载业务结果。
/// </summary>
public sealed class CardLoadBatchOutcome
{
    private CardLoadResult? _result;
    private Exception? _failure;

    /// <summary>批次结果（执行体未完成＝null）。</summary>
    public CardLoadResult? Result => _result;

    /// <summary>结构性失败（非 null 时批次未产出任何卡）。</summary>
    public Exception? Failure => _failure;

    /// <summary>标记成功完成（重复标记＝内部错误，拒绝）。</summary>
    /// <exception cref="ArgumentNullException">result 为 null。</exception>
    /// <exception cref="InvalidOperationException">已标记过结果或失败。</exception>
    internal void Complete(CardLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_result is not null || _failure is not null)
        {
            throw new InvalidOperationException("加载批次结果被重复标记（一次执行只允许一个结局）。");
        }

        _result = result;
    }

    /// <summary>标记结构性失败（重复标记＝内部错误，拒绝）。</summary>
    /// <exception cref="ArgumentNullException">failure 为 null。</exception>
    /// <exception cref="InvalidOperationException">已标记过结果或失败。</exception>
    internal void Fail(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (_result is not null || _failure is not null)
        {
            throw new InvalidOperationException("加载批次结果被重复标记（一次执行只允许一个结局）。");
        }

        _failure = failure;
    }
}

/// <summary>
/// **卡牌加载批次的专用视图**（I5；承载整批清单与报告收集器）。
/// <para>
/// 本视图**不是事件视图**：加载是**命令**（调用方要求装载一批卡），不是「发生了某事」的广播载荷。
/// 因此它不复用任何现有事件视图（<c>CardTriggerView</c> 等），也不挂总线——唯一入口是
/// <see cref="CardLoaderTrigger.LoadBatchAsync"/>。专用类型让「命令不是事件」在类型系统上成立：
/// 任何把本视图挂上总线/当成事件载荷的写法都会在评审中显形（且有静态用例守住）。
/// </para>
/// </summary>
[ContextView]
public class CardLoadBatchView
{
    /// <summary>整批卡位清单（单卡＝一元清单）。</summary>
    [Optional]
    [Read]
    public virtual IReadOnlyList<CardLoadItem>? Batch { get; set; }

    /// <summary>报告收集器（执行体把结果/结构性失败写回）。</summary>
    [Optional]
    [Read]
    public virtual CardLoadBatchOutcome? Outcome { get; set; }
}
