namespace Orc.Core.Model;

/// <summary>
/// 组件资产（S11 新 schema 的领域形态）：一份资产＝一个组件实体预制体的完整声明。
/// <para>
/// 与 <see cref="PrefabDefinition"/> 的分工：本类型是**资产层**（还带 handler 源码与判定器包装声明），
/// 装载器把它转成 <see cref="PrefabDefinition"/>（预制体层）＋ handler 资产（执行层）。
/// </para>
/// </summary>
public sealed class ComponentAsset
{
    /// <summary>创建组件资产。</summary>
    /// <exception cref="ArgumentNullException">id／集合为 null。</exception>
    /// <exception cref="ArgumentException">id 为空或纯空白。</exception>
    public ComponentAsset(
        string id,
        HostKind requiresHost,
        ActiveWhen activeWhen,
        bool oneShot,
        string? disposalSignal,
        IReadOnlyList<PrivateFieldDefinition> privateFields,
        IReadOnlyList<ComponentMemberAsset> members,
        IReadOnlyList<MountDeclaration> mounts,
        IReadOnlyList<WrapDeclaration> wraps,
        IReadOnlyList<ComponentJudgeWrapAsset> judgeWraps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(privateFields);

        Id = id;
        RequiresHost = requiresHost;
        ActiveWhen = activeWhen;
        OneShot = oneShot;
        DisposalSignal = disposalSignal;
        PrivateFields = privateFields;
        Members = members ?? throw new ArgumentNullException(nameof(members));
        Mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        Wraps = wraps ?? throw new ArgumentNullException(nameof(wraps));
        JudgeWraps = judgeWraps ?? throw new ArgumentNullException(nameof(judgeWraps));
    }

    /// <summary>资产 id（也是组件预制体 id；全局唯一）。</summary>
    public string Id { get; }

    /// <summary>组件宿主需求（R3h）。</summary>
    public HostKind RequiresHost { get; }

    /// <summary>装配时点（R3i；声明层显式落值）。</summary>
    public ActiveWhen ActiveWhen { get; }

    /// <summary>一次性组件（R3k；声明层显式落值）。</summary>
    public bool OneShot { get; }

    /// <summary>兜底信号（null＝缺省 <see cref="ComponentDisposalPolicy.DefaultSignal"/>）。</summary>
    public string? DisposalSignal { get; }

    /// <summary>私有字段声明（R3e）。</summary>
    public IReadOnlyList<PrivateFieldDefinition> PrivateFields { get; }

    /// <summary>成员声明（声明序）。</summary>
    public IReadOnlyList<ComponentMemberAsset> Members { get; }

    /// <summary>挂载声明（设计端编排通道；声明序＝执行序依据，R1g）。</summary>
    public IReadOnlyList<MountDeclaration> Mounts { get; }

    /// <summary>包装声明（元效果端修改通道；成员目标与判定器目标共用相位语义，S2a）。</summary>
    public IReadOnlyList<WrapDeclaration> Wraps { get; }

    /// <summary>
    /// 判定器包装声明（旧 <c>judicatorWrappers</c> 的迁移落点）：同步签名
    /// <c>Func&lt;object[]?, object[]?, object[]?&gt;</c>，在装载期编译（与成员包装共用注册面 Wraps，
    /// 只是 handler 形态不同——执行接线属 S9 判定器体系）。
    /// </summary>
    public IReadOnlyList<ComponentJudgeWrapAsset> JudgeWraps { get; }

    /// <summary>
    /// 判定器包装的 handler 资产 id（判定器名 + 资产 id：同一判定器可被多个资产各包一层，
    /// 它们在同一定判器上是不同的包装项，不能共用一个 id）。
    /// </summary>
    public static string JudgeHandlerId(string assetId, string judicator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(judicator);
        return judicator + "|" + assetId;
    }

    /// <summary>
    /// 转成预制体定义（预制体层）：成员的自有边引用**已解析**的 handler 资产（未给 handler 的成员＝纯编排成员）；
    /// 判定器包装声明并入同一 <c>Wraps</c> 集合（S2a：两类包装器共用同一相位语义与同一注册面）。
    /// </summary>
    /// <param name="resolve">按 handler 资产 id 解析（未注册＝抛 <see cref="KeyNotFoundException"/>）。</param>
    /// <exception cref="ArgumentNullException">resolve 为 null。</exception>
    public PrefabDefinition ToPrefabDefinition(Func<string, HandlerAsset> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        var members = new List<MemberDefinition>(Members.Count);
        foreach (var member in Members)
        {
            var ownEdges = member.Handler is { } handler
                ? new[] { new OwnedEdge(resolve(handler.Id)) }
                : Array.Empty<OwnedEdge>();

            members.Add(new MemberDefinition(
                member.Name,
                member.View,
                member.Visibility,
                ownEdges,
                layer: PrefabLayer.Asset,
                declaredBy: Id));
        }

        var wraps = new List<WrapDeclaration>(Wraps.Count + JudgeWraps.Count);
        wraps.AddRange(Wraps);
        foreach (var judgeWrap in JudgeWraps)
        {
            wraps.Add(new WrapDeclaration(
                WrapTarget.ForJudicator(judgeWrap.Judicator),
                judgeWrap.Phase,
                JudgeHandlerId(Id, judgeWrap.Judicator)));
        }

        return new PrefabDefinition(
            Id,
            EntityKind.Component,
            layer: PrefabLayer.Asset,
            requiresHost: RequiresHost,
            members: members,
            privateFields: PrivateFields,
            mounts: Mounts,
            wraps: wraps,
            activeWhen: ActiveWhen,
            oneShot: OneShot,
            disposalSignal: DisposalSignal);
    }
}

/// <summary>组件成员资产：成员名 ＋ 可见性 ＋ 视图声明 ＋ 可选 handler 源码。</summary>
/// <param name="Name">成员名（寻址用的成员名段；零转换，允许含 <c>.</c>）。</param>
/// <param name="Visibility">可见性位标志。</param>
/// <param name="View">视图声明（封闭内建集）。</param>
/// <param name="Handler">handler 声明（null＝纯编排成员，无自有边）。</param>
public sealed record ComponentMemberAsset(
    string Name,
    MemberVisibility Visibility,
    ViewSpec View,
    ComponentHandlerAsset? Handler);

/// <summary>handler 资产声明：id ＋ 入口名 ＋ （csx 源码 或 程序集键）。</summary>
/// <param name="Id">资产 id（全局唯一；多份资产可引用同一 id）。</param>
/// <param name="Entry">入口名（默认 <c>HandleAsync</c>）。</param>
/// <param name="Script">csx 源码（与 <paramref name="AssemblyKey"/> 互斥）。</param>
/// <param name="AssemblyKey">程序集处理器键（已注册＝直接引用；null＝csx 来源）。</param>
public sealed record ComponentHandlerAsset(
    string Id,
    string Entry,
    string? Script,
    string? AssemblyKey);

/// <summary>判定器包装声明（旧 <c>judicatorWrappers</c> 的迁移落点）。</summary>
/// <param name="Judicator">判定器名。</param>
/// <param name="Phase">相位（<c>before</c>／<c>after</c>／<c>replace</c>）。</param>
/// <param name="Entry">入口名（默认 <c>Handle</c>）。</param>
/// <param name="Script">csx 源码（同步签名 <c>Func&lt;object[]?, object[]?, object[]?&gt;</c>）。</param>
public sealed record ComponentJudgeWrapAsset(
    string Judicator,
    WrapperPhase Phase,
    string Entry,
    string Script);
