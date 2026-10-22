using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Targeting;

/// <summary>手牌选靶结局（三态；`Ok + 空列表`＝空选成功）。</summary>
public enum HandPickStatus
{
    /// <summary>玩家提交（数量满足约束；可能是空选）。</summary>
    Ok,

    /// <summary>取消（玩家取消、或"最小张数 &gt; 手牌数"的自动取消）。</summary>
    Cancelled,

    /// <summary>失败（系统原因：无 targeter、异常、重试超限等）。</summary>
    Failed,
}

/// <summary>
/// 手牌选靶结果：三态 ＋ 选中的手牌引用（R1/R2/I5）。
/// <para><see cref="Status"/>＝<see cref="HandPickStatus.Ok"/> 且 <see cref="Picks"/> 为空 ＝「一张都没选」的空选成功。</para>
/// </summary>
public sealed class HandPickResult
{
    private HandPickResult(HandPickStatus status, IReadOnlyList<Ref<Entity>> picks,
        SelectorFailureReason? selectorFailure = null, TargeterFailureReason? targeterFailure = null)
    {
        Status = status;
        Picks = picks;
        SelectorFailure = selectorFailure;
        TargeterFailure = targeterFailure;
    }

    /// <summary>结局三态。</summary>
    public HandPickStatus Status { get; }

    /// <summary>选中的手牌引用（顺序＝玩家提交序）。</summary>
    public IReadOnlyList<Ref<Entity>> Picks { get; }

    /// <summary>选择器级失败原因（仅 Failed 时可能非空）。</summary>
    public SelectorFailureReason? SelectorFailure { get; }

    /// <summary>targeter 级失败原因（仅 Failed 时可能非空）。</summary>
    public TargeterFailureReason? TargeterFailure { get; }

    /// <summary>是否成功提交。</summary>
    public bool IsOk => Status == HandPickStatus.Ok;

    /// <summary>是否取消。</summary>
    public bool IsCancelled => Status == HandPickStatus.Cancelled;

    /// <summary>是否失败。</summary>
    public bool IsFailed => Status == HandPickStatus.Failed;

    /// <summary>创建成功结果。</summary>
    public static HandPickResult Ok(IReadOnlyList<Ref<Entity>> picks)
    {
        ArgumentNullException.ThrowIfNull(picks);
        return new HandPickResult(HandPickStatus.Ok, picks);
    }

    /// <summary>创建取消结果。</summary>
    public static HandPickResult Cancelled() => new(HandPickStatus.Cancelled, Array.Empty<Ref<Entity>>());

    /// <summary>创建失败结果。</summary>
    public static HandPickResult Failed(SelectorFailureReason? selectorFailure = null, TargeterFailureReason? targeterFailure = null)
        => new(HandPickStatus.Failed, Array.Empty<Ref<Entity>>(), selectorFailure, targeterFailure);
}

/// <summary>
/// 手牌选靶（R1/R2/R2a/R2b）：从**己方手牌**选一张/多张的通用流程。
/// <para>数量语义：`min &gt; 手牌数` ⇒ 自动取消（零交互）；`min == 手牌数` ⇒ 仍发起交互（保留玩家手动取消的机会）；</para>
/// <para>`max &gt; 手牌数` ⇒ 收敛为手牌数（仍须满足 `min`）。</para>
/// </summary>
public static class HandSelection
{
    /// <summary>从视角卡归属玩家的手牌中选牌。</summary>
    /// <param name="viewer">视角卡（决定归属玩家与 targeter）。</param>
    /// <param name="min">至少选几张（&lt;0 按 0 处理）。</param>
    /// <param name="max">至多选几张（&lt;1 按 1 处理；大于手牌数时收敛为手牌数）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public static async Task<HandPickResult> PickAsync(Card viewer, int min, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (min < 0)
        {
            min = 0;
        }

        if (max < 1)
        {
            max = 1;
        }

        if (max < min)
        {
            max = min;
        }

        var player = OwnerOf(viewer);
        if (player is null)
        {
            return HandPickResult.Failed(targeterFailure: TargeterFailureReason.Other);
        }

        var hand = player.Hand.ToArray();

        // R2：最小张数大于手牌数 ⇒ 自动取消（不产出选择器、零交互）。
        if (min > hand.Length)
        {
            return HandPickResult.Cancelled();
        }

        // R2b：上界收敛为手牌数。
        var effectiveMax = Math.Max(min, Math.Min(max, hand.Length));
        var candidates = hand.Select(card => card.Ref).ToArray();

        var targeterManager = player.TargeterManager;
        if (targeterManager is null)
        {
            return HandPickResult.Failed(targeterFailure: TargeterFailureReason.BridgeNotAssembled);
        }

        IReadOnlyList<Ref<Entity>> picks = Array.Empty<Ref<Entity>>();
        var result = await targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.Hand,
                new ReferenceSetParameter(
                    candidates,
                    min,
                    effectiveMax,
                    domainValidator: reference => player.Hand.Any(card => ReferenceEquals(card, reference.Value)))
                {
                    Owner = player,
                    Action = SelectorActions.Effect,
                });

            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
            {
                step = await flow.Retry<IReadOnlyList<Ref<Entity>>>();
            }

            if (step.IsCancelled)
            {
                return TargeterResult.Cancelled();
            }

            if (step.IsFailed)
            {
                return TargeterResult.FromSelectorFailure(step.Failure);
            }

            picks = step.Value ?? Array.Empty<Ref<Entity>>();
            return TargeterResult.Ok();
        }).ConfigureAwait(false);

        if (result.IsOk)
        {
            return HandPickResult.Ok(picks);
        }

        return result.IsCancelled
            ? HandPickResult.Cancelled()
            : HandPickResult.Failed(targeterFailure: result.Reason);
    }

    /// <summary>视角卡归属玩家（HQ／卡牌；不可达＝null）。</summary>
    private static Player? OwnerOf(Card card) => card switch
    {
        Hq hq => hq.Owner,
        CardBase cardBase => cardBase.Owner,
        _ => null,
    };
}
