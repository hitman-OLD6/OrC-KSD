using Orc.Core.Model;
using Orc.Game.Cards;

namespace Orc.Game.Model;

/// <summary>
/// 卡牌类型预制体（S8 的骨架，本批只为 S7 提供两件事）：
/// ①<see cref="Targeting"/> 成员在**卡与 HQ 上都有**，使 <c>host#targeting</c> 按当前宿主实例天然消歧（R1j）；
/// ②卡级 8 个保留成员（原"预打出／打出／部署／部署词条／加入／单位化／老兵／揭示触发器"）可在预制体中枚举（S7 第 3 条）。
/// <para>
/// 卡牌实例与类型预制体的绑定（<c>typeCategory</c> → 预制体）属 S8（批次 3）；本批不接线，只声明。
/// 成员表统一由类型预制体决定，卡牌 C# 类不得再出现 <c>new Trigger&lt;</c>（S8 第 9 条）。
/// </para>
/// </summary>
public static class CardProcesses
{
    /// <summary>类型预制体 id：薄基类（只含卡与 HQ 共有的接入点）。</summary>
    public const string BasePrefabId = "card.base";

    /// <summary>类型预制体 id：单位卡。</summary>
    public const string UnitPrefabId = "card.unit";

    /// <summary>类型预制体 id：指令卡。</summary>
    public const string OrderPrefabId = "card.order";

    /// <summary>类型预制体 id：反制卡。</summary>
    public const string CounterPrefabId = "card.counter";

    /// <summary>类型预制体 id：HQ。</summary>
    public const string HqPrefabId = "card.hq";

    /// <summary>成员名：指向（原"指向触发器"；卡与 HQ 各有该成员）。</summary>
    public const string Targeting = "targeting";

    /// <summary>成员名：预打出。</summary>
    public const string PrePlay = "pre-play";

    /// <summary>成员名：打出。</summary>
    public const string Play = "play";

    /// <summary>成员名：部署。</summary>
    public const string Deploy = "deploy";

    /// <summary>成员名：部署词条。</summary>
    public const string DeployKeyword = "deploy-keyword";

    /// <summary>成员名：加入。</summary>
    public const string Join = "join";

    /// <summary>成员名：单位化。</summary>
    public const string Unitize = "unitize";

    /// <summary>成员名：老兵（升级）。</summary>
    public const string Veteran = "veteran";

    /// <summary>成员名：揭示。</summary>
    public const string Reveal = "reveal";

    /// <summary>成员名：使用反制（反制卡独有）。</summary>
    public const string UseCounter = "use-counter";

    /// <summary>卡级保留成员名（声明序；单位卡独有 8 项）。</summary>
    public static IReadOnlyList<string> UnitMemberNames { get; } = new[]
    {
        PrePlay,
        Play,
        Deploy,
        DeployKeyword,
        Join,
        Unitize,
        Veteran,
        Reveal,
    };

    /// <summary>指令卡成员名（声明序；S8：各类型**各自声明**，不靠"继承后删成员"）。</summary>
    public static IReadOnlyList<string> OrderMemberNames { get; } = new[]
    {
        PrePlay,
        Play,
    };

    /// <summary>反制卡成员名（声明序）。</summary>
    public static IReadOnlyList<string> CounterMemberNames { get; } = new[]
    {
        UseCounter,
    };

    /// <summary>薄基类成员（卡与 HQ 共有：指向接入点）。</summary>
    public static IReadOnlyList<MemberDefinition> BaseMembers { get; } = new[]
    {
        new MemberDefinition(
            Targeting,
            TargetingPayload(),
            MemberVisibility.Hostable | MemberVisibility.Callable,
            layer: PrefabLayer.Game),
    };

    /// <summary>
    /// 指向成员的视图声明（旧 TargetingView 的载荷字段；<c>Host</c> 由承载视图内建，不来自数据面）。
    /// <para>基类与单位卡覆写共用同一份声明——两处各写一遍会让 <c>host#targeting</c> 的载荷在两型之间漂移。</para>
    /// </summary>
    private static ViewSpec TargetingPayload()
        => SignalMember.Payload(
            ("Source", ViewFieldType.Entity),
            ("Target", ViewFieldType.Entity),
            ("Kind", ViewFieldType.String),
            ("TriggerCard", ViewFieldType.Entity));

    /// <summary>
    /// 单位卡的指向成员：视图与基类**同形**，差别只在声明了具名缝
    /// <see cref="TargetingRules.SeamName"/>（＝<c>指向后</c>）。
    /// <para>
    /// 为什么覆写在 <c>card.unit</c> 而不是 <c>card.base</c>：缝是**handler 内部的插入点**，而调用
    /// <c>ctx.SeamAsync("指向后")</c> 的 handler（<c>UnitCard.HandleTargetingAsync</c>）只挂在单位卡的指向成员上；
    /// 声明在基类上会让 HQ／指令卡／反制卡也声明一条无人调用的缝（虽无副作用，但声明与使用者不符）。
    /// </para>
    /// <para>
    /// 当前**没有任何资产往"指向后"缝上挂包装器**＝空缝：调用不报错、不执行任何包装、无副作用
    /// （能力恢复但暂无用例的预期状态）。
    /// </para>
    /// </summary>
    private static MemberDefinition UnitTargetingMember() => new(
        Targeting,
        TargetingPayload(),
        MemberVisibility.Hostable | MemberVisibility.Callable,
        layer: PrefabLayer.Game,
        seams: new[] { TargetingRules.SeamName });

    /// <summary>
    /// 单位卡成员（卡级 8 项；视图声明随 S11 资产迁移落表）＋指向成员的**覆写**
    /// （追加具名缝声明；成员名与基类相同 ⇒ 整段覆写、位次不变）。
    /// </summary>
    public static IReadOnlyList<MemberDefinition> UnitMembers { get; } = UnitMemberNames
        .Select(static name => new MemberDefinition(
            name,
            ViewSpec.Empty,
            MemberVisibility.Hostable | MemberVisibility.Callable,
            layer: PrefabLayer.Game))
        .Append(UnitTargetingMember())
        .ToArray();

    /// <summary>指令卡成员（预打出／打出：费用校验的承载面）。</summary>
    public static IReadOnlyList<MemberDefinition> OrderMembers { get; } = OrderMemberNames
        .Select(static name => new MemberDefinition(
            name,
            ViewSpec.Empty,
            MemberVisibility.Hostable | MemberVisibility.Callable,
            layer: PrefabLayer.Game))
        .ToArray();

    /// <summary>反制卡成员（使用反制：激活/取消的单入口）。</summary>
    public static IReadOnlyList<MemberDefinition> CounterMembers { get; } = CounterMemberNames
        .Select(static name => new MemberDefinition(
            name,
            ViewSpec.Empty,
            MemberVisibility.Hostable | MemberVisibility.Callable,
            layer: PrefabLayer.Game))
        .ToArray();

    /// <summary>卡牌类型预制体（声明序：base → unit → order → counter → hq）。</summary>
    public static IReadOnlyList<PrefabDefinition> Definitions { get; } = new[]
    {
        new PrefabDefinition(BasePrefabId, EntityKind.Card, layer: PrefabLayer.Game, members: BaseMembers),
        new PrefabDefinition(
            UnitPrefabId, EntityKind.Card, extends: BasePrefabId, layer: PrefabLayer.Game,
            hosts: HostKind.Unit, members: UnitMembers),
        new PrefabDefinition(
            OrderPrefabId, EntityKind.Card, extends: BasePrefabId, layer: PrefabLayer.Game,
            hosts: HostKind.Order, members: OrderMembers),
        new PrefabDefinition(
            CounterPrefabId, EntityKind.Card, extends: BasePrefabId, layer: PrefabLayer.Game,
            hosts: HostKind.Counter, members: CounterMembers),
        new PrefabDefinition(
            HqPrefabId, EntityKind.Card, extends: BasePrefabId, layer: PrefabLayer.Game, hosts: HostKind.Hq),
    };
}
