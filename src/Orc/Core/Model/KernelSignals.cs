namespace Orc.Core.Model;

/// <summary>
/// 内核基础预制体（S3 第 7 条／R6b）：声明**内核 4 个信号**的引擎预制体 <c>engine</c>。
/// <para>
/// 游戏层预制体 <c>engine.game</c> 以 <c>extends: engine</c> 追加游戏层 35 个信号与流程成员；
/// 两者同处 <c>engine</c> 根下，寻址解析**跨层合并**成员表（解析对来源层无差别，审查面标出来源层）。
/// </para>
/// <para>
/// 成员名零转换：原 <c>hooks: ["card.placed"]</c> → 成员 <c>card.placed</c>（地址 <c>engine#card.placed</c>）。
/// </para>
/// </summary>
public static class KernelSignals
{
    /// <summary>内核基础引擎预制体 id（游戏层 <c>extends</c> 它）。</summary>
    public const string PrefabId = "engine";

    /// <summary>载荷键：卡牌实例（与 <c>Orc.Cards.PayloadKeys.Card</c> 同值，由守护测试锁住漂移）。</summary>
    public const string PayloadCard = "Card";

    /// <summary>载荷键：效果实例（与 <c>Orc.Cards.PayloadKeys.Effect</c> 同值，由守护测试锁住漂移）。</summary>
    public const string PayloadEffect = "Effect";

    /// <summary>内核 4 个信号名（声明序；＝<see cref="Updates"/> 常量，不自造字面值）。</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        Updates.CardPlaced,
        Updates.CardDestroyed,
        Updates.CardData,
        Updates.EffectRemoved,
    };

    /// <summary>内核 4 个信号成员（声明序，来源层＝内核）。</summary>
    public static IReadOnlyList<MemberDefinition> Members { get; } = Build();

    /// <summary>创建内核基础引擎预制体 <c>engine</c>。</summary>
    public static PrefabDefinition CreateEnginePrefab()
        => new(PrefabId, EntityKind.Engine, layer: PrefabLayer.Kernel, members: Members);

    /// <summary>
    /// 创建只含内核基础预制体的目录（供内核层测试／最小运行时）。
    /// <para>
    /// 同时登记 **A2 信号端点预制体**（<see cref="SignalEndpointPrefab.PrefabId"/>）：生产挂载＝
    /// "触发器 → 端点实体 → <c>engine#&lt;信号&gt;</c> 边"，端点必须是目录里的合法预制体。
    /// </para>
    /// </summary>
    public static PrefabCatalog CreateCatalog()
        => new PrefabCatalog()
            .Add(CreateEnginePrefab())
            .Add(SignalEndpointPrefab.Create());

    private static MemberDefinition[] Build()
        => new[]
        {
            // card.placed：放置＝上场语义（加载时不发射），故为"入场期信号"。
            SignalMember.Create(
                Updates.CardPlaced,
                SignalTiming.Placed,
                SignalMember.Payload((PayloadCard, ViewFieldType.Entity)),
                PrefabLayer.Kernel),

            // card.destroyed：引擎销毁（装载链清理驱动）；组件的框架注销边挂在这里，与装配批次无关。
            SignalMember.Create(
                Updates.CardDestroyed,
                SignalTiming.Any,
                SignalMember.Payload((PayloadCard, ViewFieldType.Entity)),
                PrefabLayer.Kernel),

            // card.data：卡牌数据变更。
            SignalMember.Create(
                Updates.CardData,
                SignalTiming.Any,
                SignalMember.Payload((PayloadCard, ViewFieldType.Entity)),
                PrefabLayer.Kernel),

            // effect.removed：效果移除（载荷＝卡牌＋效果）；**不得**作为组件注销兜底信号（R1f 装载期拒绝）。
            SignalMember.Create(
                Updates.EffectRemoved,
                SignalTiming.Any,
                SignalMember.Payload(
                    (PayloadCard, ViewFieldType.Entity),
                    (PayloadEffect, ViewFieldType.Entity)),
                PrefabLayer.Kernel),
        };
}
