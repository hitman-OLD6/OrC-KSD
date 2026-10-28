using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Judicators;

namespace Orc.Game.Triggers;

/// <summary>
/// 守卫边写入数据面时使用的键（拒绝标记）。
/// <para>
/// 为什么经数据面：卡牌成员（<c>host#play</c> 等）的成员执行结果只回答"执行了／停了"，
/// 而入口需要**可辨识的拒绝原因**（<c>PlayVerificationRejected</c>／<c>CounterNotOwnerTurn</c>…）。
/// 守卫边把判定结果写进本次执行的数据面，调用方读同一份 <see cref="Context"/> 取回——
/// 单一真源、不引入第二份状态。
/// </para>
/// </summary>
public static class MemberGuardKeys
{
    /// <summary>拒绝标记键（存在＝本次被守卫拒绝）。</summary>
    public const string Rejected = "guard:rejected";

    /// <summary>拒绝类别键（判定器给出的类别对象；可能为 null＝未提供类别）。</summary>
    public const string Reason = "guard:reason";
}

/// <summary>
/// 卡牌成员的**守卫边**（批次 7／§27：取代 <c>CostCheckTrigger</c>／<c>CounterUseTrigger</c>）。
/// <para>
/// 旧实现是"两个只承载合法性验证的触发器子类"，绑到预打出／打出／使用反制三个成员上，
/// 每次触发前先跑判定器、不合法＝仅本次取消。新模型里成员边就是执行体：
/// 守卫＝**成员的第一条自有边**——它在卡牌自己的链之前跑，判定不合法就 <c>ctx.Stop()</c>
/// （本级剩余步骤不执行，与"仅本次取消"同义），并把拒绝类别写进数据面供入口映射失败原因。
/// </para>
/// <para>
/// 判定源不变：费用检查＝<see cref="JudicatorNames.CostCheck"/>、反制使用检查＝<see cref="JudicatorNames.CounterUse"/>，
/// 装配期按名解析（解析动作即校验、未注册名 fail-fast；moding 改写仍然全局生效）。
/// </para>
/// </summary>
public static class CardPlayGuards
{
    /// <summary>
    /// 费用校验（预打出／打出）：卡主已加载且点数 ≥ 有效部署费＝合法。
    /// </summary>
    /// <param name="card">所属卡牌（被判定对象）。</param>
    /// <param name="resolver">判定器解析器（null＝内置默认）。</param>
    public static ValidationVerdict EvaluateCost(CardBase card, Func<string, JudicatorBinding>? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(card);

        var binding = Resolve(resolver, JudicatorNames.CostCheck);
        return binding.Validate(new[] { card.Ref }, card.Ref, card.Owner?.Index);
    }

    /// <summary>
    /// 反制使用检查：仅己方回合；未激活时另检查指挥点（已激活＝取消流程、无条件通过）。
    /// </summary>
    /// <param name="card">所属反制卡（被判定对象）。</param>
    /// <param name="resolver">判定器解析器（null＝内置默认）。</param>
    public static ValidationVerdict EvaluateCounterUse(CounterCard card, Func<string, JudicatorBinding>? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(card);

        var binding = Resolve(resolver, JudicatorNames.CounterUse);
        return binding.Validate(new[] { card.Ref }, card.Ref, card.Owner?.Index);
    }

    /// <summary>接入费用守卫边（预打出与打出各一条；位次＝自有边首条）。</summary>
    /// <param name="card">所属卡牌。</param>
    /// <param name="memberName">成员名（<see cref="Orc.Game.Model.CardProcesses.PrePlay"/>／<c>Play</c>）。</param>
    /// <param name="resolver">判定器解析器（null＝内置默认）。</param>
    public static void AttachCostGuard(
        CardBase card, string memberName, Func<string, JudicatorBinding>? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(card);

        // 装配期解析一次：未注册名＝现在 fail-fast（与旧 BindValidation 同一时点）。
        var binding = Resolve(resolver, JudicatorNames.CostCheck);
        card.AttachMemberEdge(memberName, new OwnedEdge((_, ctx, ct) => GuardAsync(binding, card, ctx)));
    }

    /// <summary>接入反制使用守卫边（使用反制成员；位次＝自有边首条）。</summary>
    /// <param name="card">所属反制卡。</param>
    /// <param name="resolver">判定器解析器（null＝内置默认）。</param>
    public static void AttachCounterUseGuard(
        CounterCard card, Func<string, JudicatorBinding>? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(card);

        var binding = Resolve(resolver, JudicatorNames.CounterUse);
        card.AttachMemberEdge(
            Orc.Game.Model.CardProcesses.UseCounter,
            new OwnedEdge((_, ctx, ct) => GuardAsync(binding, card, ctx)));
    }

    private static Task GuardAsync(JudicatorBinding binding, CardBase card, Context ctx)
    {
        var verdict = binding.Validate(new[] { card.Ref }, card.Ref, card.Owner?.Index);
        if (verdict.IsValid)
        {
            return Task.CompletedTask;
        }

        // 写拒绝标记（调用方据此映射失败原因），并停止本级（＝旧「仅本次取消」语义：不执行链、不外溢）。
        ctx.Set(MemberGuardKeys.Rejected, true);
        ctx.Set(MemberGuardKeys.Reason, verdict.RejectionReason);
        ctx.Stop();
        return Task.CompletedTask;
    }

    private static JudicatorBinding Resolve(Func<string, JudicatorBinding>? resolver, string name)
        => resolver is not null ? resolver(name) : BuiltInValidationJudicators.Resolve(name);
}
