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

    /// <summary>
    /// 内核 4 条信号的**中文名**（R35／D25：文案真源在信号定义处，与 <see cref="Names"/> **逐条对齐**）。
    /// <para>
    /// 覆盖率由守护测试锁住（<c>Names ≡ Labels.Keys</c>）——写成单条「39 对 39」会让内核漏写永远绿（D26）。
    /// 与游戏层**分表**：内核中文名必须跟内核信号定义同层，否则内核加信号要跨层改表。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Updates.CardPlaced] = "放置时",
        [Updates.CardDestroyed] = "销毁时",
        [Updates.CardData] = "数据变更时",
        [Updates.EffectRemoved] = "效果移除时",
    };

    /// <summary>内核信号的语义边界说明（R35／D25；可选，模板的 <c>description</c> 直接取它）。</summary>
    public static IReadOnlyDictionary<string, string> Notes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// **主体卡字段白名单**（R32／D27）：信号 → 载荷里表示「宿主卡自身」的字段名。
    /// <para>
    /// 未登记 ⇒ 不出 <c>self</c> 档模板。**判据不是载荷顺序、也不是字段类型**——载荷里
    /// <c>Card</c>／<c>Effect</c> 都是 <c>entity</c>，按序推断必误判（漏登记只少一个模板，
    /// 错登记会造出一个永不触发的模板，方向必须偏向漏登记）。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Subjects { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Updates.CardPlaced] = PayloadCard,
        [Updates.CardDestroyed] = PayloadCard,
        [Updates.CardData] = PayloadCard,
        [Updates.EffectRemoved] = PayloadCard,
    };

    /// <summary>
    /// **玩家字段白名单**（R32／D27）：信号 → 载荷里的玩家字段名。内核 4 条都不带玩家字段
    /// ⇒ 内核信号不出 <c>owner-*</c> 档（反向亦然：游戏层 19 条信号不带主体卡字段）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Players { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

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
