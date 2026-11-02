using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;

namespace Orc.Game.Effects;

/// <summary>
/// 授予的**生命周期**（R37）：效果实例活到什么时候。
/// <para>
/// 只有两档是有意的：「到回合结束」不加——<c>one-shot</c> 与效果自己调 <c>Revoke</c> 已经能表达，
/// 再加一档等于让引擎承担时序编排。
/// </para>
/// </summary>
public enum GrantLifetime
{
    /// <summary>随宿主销毁（缺省）：宿主还在，效果就在。</summary>
    UntilDestroyed = 0,

    /// <summary>宿主**离场即撤销**：非死亡离场（回手／移除／撤退）与死亡都算。</summary>
    /// <remarks>
    /// 为什么两条路都接：`card.leaving` 只表示**非死亡**离场，死亡走 `card.dying`／`card.died`
    /// （三者互斥）。只接 `card.leaving` 会让「被打死」的卡把授予的效果带进弃牌堆。
    /// </remarks>
    UntilLeaving = 1,
}

/// <summary>
/// 一次授予的句柄（R37）：**撤销靠它，不按效果 id 找**。
/// <para>
/// 同一张卡重复授予同一效果＝两份独立实例、两个句柄——所以「撤销哪一个」不需要仲裁。
/// </para>
/// </summary>
public sealed class GrantHandle : IDisposable
{
    private readonly List<IDisposable> _subscriptions;

    internal GrantHandle(ComponentAttachment attachment, string prefabId, List<IDisposable> subscriptions)
    {
        Attachment = attachment;
        PrefabId = prefabId;
        _subscriptions = subscriptions;
    }

    /// <summary>被授予的组件实例（已装配）。</summary>
    public ComponentAttachment Attachment { get; }

    /// <summary>授予用的预制体 id。</summary>
    public string PrefabId { get; }

    /// <summary>是否已撤销（撤销后实例不可再用）。</summary>
    public bool IsRevoked => Attachment.IsDisposed;

    /// <summary>
    /// 撤销本次授予：卸掉本实例的全部边并销毁实例；同时注销为此授予挂的生命周期订阅。
    /// <para>**幂等**：已撤销再调＝无操作（宿主离场与调用方主动撤销可能同时命中）。</para>
    /// </summary>
    public void Revoke()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        Attachment.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => Revoke();
}

/// <summary>
/// **运行期授予效果**（R37）：给一张已经在场的卡凭空加一份效果。
/// <para>
/// 与旧 DSL 的 <c>grantEffect</c>／<c>bindEvent</c> 对应。v4 的**效果＝组件实例**，所以授予走既有装配链
/// （<see cref="OrchestrationRuntime.GrantComponent"/>）；<c>bindEvent</c> 那种**内联**声明不在引擎侧实现——
/// 宿主先把嵌套 effect 编译成组件资产，再调本方法（v4 的效果是资产文本，内联声明没有承载形态）。
/// </para>
/// <para>
/// **授予者死亡不撤销**：授予是「一次性给出去了」，不是持续依附；要持续依附该用 <c>wraps</c> 或光环。
/// </para>
/// </summary>
public static class EffectGrantService
{
    private static long _sequence;

    /// <summary>
    /// 给 <paramref name="target"/> 授予一份效果并立即生效（同帧后续动作就能看到它的边）。
    /// </summary>
    /// <param name="target">宿主卡；必须已经在编排运行时里（<see cref="CardBase.ModelEntity"/> 非空）。</param>
    /// <param name="effectPrefabId">效果预制体 id（已注册的组件资产）。</param>
    /// <param name="lifetime">生命周期（缺省随宿主销毁）。</param>
    /// <param name="name">实例名（缺省按 id ＋ 序号生成；仅诊断用）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> 为 null。</exception>
    /// <exception cref="ArgumentException">prefabId 为空。</exception>
    /// <exception cref="InvalidOperationException">目标卡还没进编排运行时（未加载／已销毁）。</exception>
    /// <exception cref="PrefabLoadException">预制体未注册、不是组件，或装配失败（不留半装配状态）。</exception>
    public static GrantHandle Grant(
        CardBase target, string effectPrefabId, GrantLifetime lifetime = GrantLifetime.UntilDestroyed, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectPrefabId);

        var host = target.ModelEntity
            ?? throw new InvalidOperationException(
                $"卡牌 '{target.Name}' 尚未进入编排运行时（未加载或已销毁），无法授予效果 '{effectPrefabId}'。");

        var instanceName = name ?? $"{effectPrefabId}@grant{Interlocked.Increment(ref _sequence)}";
        var attachment = host.Runtime.GrantComponent(host, effectPrefabId, instanceName);

        var subscriptions = new List<IDisposable>(2);
        var handle = new GrantHandle(attachment, effectPrefabId, subscriptions);

        if (lifetime == GrantLifetime.UntilLeaving)
        {
            // LogicEngine（不是 host.Runtime.Engine——后者是**引擎实体**，两回事）。
            var engine = target.Engine;
            var onLeaving = MemberEdges.For<CardEventView>(
                (view, _, _) =>
                {
                    if (ReferenceEquals(view.Card, target))
                    {
                        handle.Revoke();
                    }

                    return Task.CompletedTask;
                });

            // card.leaving＝非死亡离场；card.dying／card.died＝死亡。三者互斥，所以要接两条路。
            subscriptions.Add(engine.AttachMemberEdge(
                GameUpdates.CardLeaving, $"授予撤销（离场）:{instanceName}", handle, onLeaving));
            subscriptions.Add(engine.AttachMemberEdge(
                GameUpdates.CardDying, $"授予撤销（死亡）:{instanceName}", handle, onLeaving));
        }

        return handle;
    }
}
