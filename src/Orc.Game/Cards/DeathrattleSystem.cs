using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 第 2 批·A4 亡计体系（口径依据《需求文档（A4）》Q&A-1/2/6；§29 起内容改由 v4 组件承载）：
// 亡计词条组件（标记型：只表达"这张卡有亡计"，内容不在这里）；
// 亡计内容＝**卡实体上的一个 v4 组件**（`one-shot: true` ＋ 成员 `settle` ＋ `mounts: engine#card.dying`）；
// 亡计执行面（单源：死亡结算与再触发都驱动**同一个成员**，见 DeathrattleRules）。
// 「亡计结算」步＝统一死亡流程（CommandManager.ProcessDeathAsync）置毁之后、词条死亡注销之前。
// 再触发（发布/接收/驱动）与部署重放见 RetriggerSystem.cs；部署逻辑登记/生成面见 DeploymentLogicData.cs。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 亡计词条组件（标记型）。
/// <para>
/// **不再承载内容**：旧形态的内容＝<c>DeathrattleEffect</c>（<c>PassiveEffect</c> 子类，随 <c>Effect</c> 家族删除）。
/// 本组件只保留标识与生命周期（授予/移除/死亡注销仍走统一词条链）；内容改由卡实体上的 v4 组件承载——
/// 组件成员是唯一执行体，因此"授予／撤销"与"内容生灭"解耦为两件事（内容随组件挂载而生、随组件卸载而灭）。
/// </para>
/// </summary>
public sealed class DeathrattleKeywordComponent : KeywordComponent
{
    /// <summary>创建亡计词条组件（无内容形态）。</summary>
    public DeathrattleKeywordComponent()
        : base(KeywordIds.Deathrattle)
    {
    }
}

/// <summary>
/// 亡计规则服务（A4）：亡计执行面（单源共享——死亡结算与再触发）。
/// <para>
/// **内容承载形态（§29 起）**：卡实体上的一个 v4 组件，约定三件事——
/// </para>
/// <list type="number">
/// <item>成员名＝<see cref="ContentMember"/>（<c>settle</c>）：结算执行体就是它，没有第二份 C# 实现；</item>
/// <item>挂载声明＝<c>mounts: engine#card.dying</c>（<see cref="ContentSignal"/>）：死亡流程在
///   **置毁之后、词条注销之前**发射该信号，因此内容天然"在清理前结算"；</item>
/// <item><c>one-shot: true</c>：信号路径与再触发路径都驱动同一成员，一次性额度保证**恰一次**。</item>
/// </list>
/// <para>
/// 为什么不是 <c>engine#card.destroyed</c>（原交接建议）：<c>card.destroyed</c> 是"卡对象被销毁"（弃置／爆牌／
/// 回库回收）的更新，**单位死亡链并不发射它**（死亡链发 <c>card.dying</c> / <c>card.died</c>）；挂在它上面的内容
/// 在战斗致死与防御归零两条路径上都不会触发，正是"边在图上、行为消失"的那类静默失效。结算时点必须是
/// <c>card.dying</c>：它是死亡链里唯一"置毁已置、清理未做"的可挂载信号。
/// </para>
/// </summary>
public static class DeathrattleRules
{
    /// <summary>内容组件的结算成员名（约定；内容组件必须声明该成员）。</summary>
    public const string ContentMember = "settle";

    /// <summary>结算时点信号（＝<c>card.dying</c>；内容组件的挂载目标）。</summary>
    public const string ContentSignal = GameUpdates.CardDying;

    /// <summary>内容组件的标准挂载声明（作者在预制体里照抄即可）。</summary>
    public static MountDeclaration ContentMount() => new($"engine#{ContentSignal}", ContentMember);

    /// <summary>该卡是否承载亡计内容（卡实体上存在声明了 <see cref="ContentMember"/> 的组件实例）。</summary>
    public static bool HasContent(UnitCard unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return ContentComponents(unit).Count > 0;
    }

    /// <summary>该卡的亡计内容组件实例（装配序；无实体／无内容＝空）。</summary>
    public static IReadOnlyList<ComponentAttachment> ContentComponents(UnitCard unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (unit.ModelEntity is not { } entity || unit.Engine.Model is not { } runtime)
        {
            return Array.Empty<ComponentAttachment>();
        }

        return runtime.ComponentsOf(entity)
            .Where(static attachment => attachment.Component.FindMember(ContentMember) is not null)
            .ToArray();
    }

    /// <summary>
    /// 亡计执行面（单源；死亡结算的兜底驱动 ＋ 再触发）：
    /// 在宿主卡的内容组件上执行 <see cref="ContentMember"/> 一次，返回是否**真的执行了**。
    /// <para>
    /// 死亡链的主驱动是 <c>engine#card.dying</c> 上的那条边（组件成员同样是它）；本方法服务两个场景：
    /// ①再触发（<c>RetriggerRules.RequestDeathrattleAsync</c>——不发链级信号，直接驱动内容）；
    /// ②内容组件**没有**声明 <c>card.dying</c> 挂载时的兜底（死亡链在清理前调用）。
    /// 两条路径驱动的是同一个成员，且 <c>one-shot</c> 让"已经结算过"的实例在这里零副作用——
    /// 恰一次由组件的一次性额度保证，不由调用点的次序保证。
    /// </para>
    /// </summary>
    /// <returns>true＝至少执行了一次（false＝无内容，或已消费一次性额度）。</returns>
    /// <exception cref="ArgumentNullException">unit／engine 为 null。</exception>
    internal static async Task<bool> ExecuteAsync(UnitCard unit, LogicEngine engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(engine);

        if (engine.Model is not { } runtime)
        {
            return false; // 未装配编排运行时（独立构造）：无内容执行面
        }

        var executed = false;
        foreach (var attachment in ContentComponents(unit))
        {
            var result = await runtime
                .RunMemberAsync(attachment.Component, ContentMember, new Context(), engine, ct)
                .ConfigureAwait(false);
            executed |= result.Executed;
        }

        return executed;
    }
}
