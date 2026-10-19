using Orc.Core;
using Orc.Game.Cards;

namespace Orc.Game.Judicators;

/// <summary>抑制资格失败原因：目标已死亡／目标免疫抑制。</summary>
public enum InhibitEligibilityFailure
{
    /// <summary>目标已死亡（拒绝、零副作用）。</summary>
    Dead,

    /// <summary>目标不可被抑制（词条包装器改写，如「固守」/「无法被抑制」）。</summary>
    Immune,
}

/// <summary>
/// 抑制资格判定器（默认实现；无状态）：已死亡 → <see cref="InhibitEligibilityFailure.Dead"/>；
/// 内建「无法被抑制」→ <see cref="InhibitEligibilityFailure.Immune"/>；否则可抑制（null）。
/// 「被抑制」是纯视觉标记——**不**作为拒绝依据（允许重复抑制）。
/// 词条经 <c>judicatorWrappers</c>（csx 同步包装器）可改写本判定，实现自身免疫而不耦合引擎字面。
/// </summary>
internal sealed class InhibitEligibilityJudicator
    : Judicator<InhibitEligibilityJudicator.InhibitEligibilityRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（目标单位）→ 失败原因（null＝可抑制）。</summary>
    public delegate InhibitEligibilityFailure? InhibitEligibilityRule(UnitCard target);

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(InhibitEligibilityRule handler)
        => args =>
        {
            var result = handler(Unpack<UnitCard>(args, 0));
            return result is { } failure
                ? new object[] { failure }
                : new object[] { null! };
        };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);

    private static InhibitEligibilityFailure? Evaluate(UnitCard target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.TryGetData<UnitStateData>(out var state) && state.IsDestroyed)
        {
            return InhibitEligibilityFailure.Dead;
        }

        if (KeywordRules.HasKeyword(target, KeywordIds.CannotBeInhibited))
        {
            return InhibitEligibilityFailure.Immune;
        }

        return null;
    }
}
