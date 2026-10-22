using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Targeting;

namespace Orc.Game.Effects;

/// <summary>手牌选靶的**效果面结果**（三态 ＋ 已解引用的手牌；失效引用被剔除、不抛）。</summary>
public sealed class HandCardPick
{
    internal HandCardPick(HandPickStatus status, IReadOnlyList<Card> cards, TargeterFailureReason? reason)
    {
        Status = status;
        Cards = cards;
        Reason = reason;
    }

    /// <summary>结局三态（`Ok + 空列表`＝空选成功）。</summary>
    public HandPickStatus Status { get; }

    /// <summary>选中的手牌（失效引用已剔除）。</summary>
    public IReadOnlyList<Card> Cards { get; }

    /// <summary>失败原因（可选）。</summary>
    public TargeterFailureReason? Reason { get; }

    /// <summary>是否成功提交。</summary>
    public bool IsOk => Status == HandPickStatus.Ok;

    /// <summary>是否取消。</summary>
    public bool IsCancelled => Status == HandPickStatus.Cancelled;

    /// <summary>是否失败。</summary>
    public bool IsFailed => Status == HandPickStatus.Failed;
}

/// <summary>
/// 效果运行时的**动作面补充**（partial）：手牌选靶（R1）＋ 加入战场两触发端（R3）。
/// <para>加入战场复用动作层 <c>join.battlefield</c>（落位规则/门禁在动作层单源实现）：</para>
/// <para>· <see cref="JoinFrontlineAsync"/>：前线存在存活敌方单位＝失败（Error 留痕）；满员＝失败（Warning 留痕）；均不抛。</para>
/// <para>· <see cref="JoinSupportLineAsync"/>：落位＝显式序号 &gt; 守护（HQ 右侧）&gt; 默认最左。</para>
/// <para>落位规则与门禁的判定口径由效果处自行使用：前线归属可经 <c>Judge(JudicatorNames.MoveFrontlineEnemy, owner)</c> 先行判定。</para>
/// </summary>
public sealed partial class EffectRuntime
{
    /// <summary>「加入前线」触发端（供 C# 侧注册/改写；未装配＝null）。</summary>
    public Trigger<JoinBattlefieldTriggerView>? JoinFrontlineTrigger => ResolveJoinAction()?.JoinFrontline;

    /// <summary>「加入支援阵线」触发端（供 C# 侧注册/改写；未装配＝null）。</summary>
    public Trigger<JoinBattlefieldTriggerView>? JoinSupportLineTrigger => ResolveJoinAction()?.JoinSupportLine;

    /// <summary>把单位加入**前线**（不扣费、不走部署词条；发 <c>unit.joined</c>）。</summary>
    /// <param name="host">效果宿主（作为触发者卡写入信号来源）。</param>
    /// <param name="unit">待加入单位。</param>
    /// <param name="index">显式插入序号（插入后的位置）；null＝默认最左。</param>
    /// <returns>动作结局；未装配加入战场动作＝null（功能不可用、不抛）。</returns>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    public async Task<ActionResult?> JoinFrontlineAsync(
        Card host, UnitCard unit, int? index = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var action = ResolveJoinAction();
        if (action is null)
        {
            return null;
        }

        return await action.JoinFrontlineAsync(unit, index, host, ct).ConfigureAwait(false);
    }

    /// <summary>把单位加入**支援阵线**（落位＝显式序号 &gt; 守护〔HQ 右侧〕&gt; 默认最左）。</summary>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    public async Task<ActionResult?> JoinSupportLineAsync(
        Card host, UnitCard unit, int? index = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var action = ResolveJoinAction();
        if (action is null)
        {
            return null;
        }

        return await action.JoinSupportLineAsync(unit, index, host, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 手牌选靶（R1/R2）：从视角卡归属玩家的手牌中选 <paramref name="min"/>..<paramref name="max"/> 张。
    /// <para>`min` 大于手牌数＝<see cref="HandPickStatus.Cancelled"/>（零交互）；`min` 等于手牌数仍发起交互；`max` 收敛为手牌数。</para>
    /// <para>纯选择：不附带弃牌/移动等动作，由调用方自行组合。</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<HandCardPick> PickHandCardsAsync(
        Card viewer, int min, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var result = await HandSelection.PickAsync(viewer, min, max, ct).ConfigureAwait(false);

        var cards = new List<Card>(result.Picks.Count);
        foreach (var reference in result.Picks)
        {
            if (reference.IsAlive && reference.Value is Card card)
            {
                cards.Add(card);
            }
        }

        return new HandCardPick(result.Status, cards, result.TargeterFailure);
    }

    private IJoinBattlefieldAction? ResolveJoinAction()
        => _engine?.Invoke() is { } engine ? ActionRuntime.ResolveJoinBattlefield(engine) : null;
}
