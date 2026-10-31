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
        IReadOnlyList<ComponentJudgeWrapAsset> judgeWraps,
        string? extends = null,
        bool authorizesModing = false)
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
        Extends = string.IsNullOrWhiteSpace(extends) ? null : extends.Trim();
        AuthorizesModing = authorizesModing;

        if (Extends is not null && string.Equals(Extends, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"资产 '{Id}' 的 extends 指向自身（继承不得自指）。", nameof(extends));
        }
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
    /// 父资产 id（R1／R2；null＝自包含资产）。**只有 moding 草案**会带它——摊平产物必须为 null
    /// （R8 第 4 条：产物内不存在指向父资产的 <c>extends</c>）。
    /// <para>
    /// 声明了 <c>extends</c> ＝本资产只保存**增量**（重载／新增的成员、挂载、包装），
    /// 展开后的成员表每次现算（D13：不落盘展开表）。
    /// </para>
    /// </summary>
    public string? Extends { get; }

    /// <summary>
    /// 资产级「本资产可被 moding」开关（R2；缺省关闭）。父未打开＝任何成员覆写都是装载期拒绝。
    /// </summary>
    public bool AuthorizesModing { get; }

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
        return Project(resolve);
    }

    /// <summary>
    /// **编辑期结构投影**（S10／D13）：与 <see cref="ToPrefabDefinition"/> 走**同一套** asset→prefab 映射，
    /// 但成员**不带自有边**（不解析 handler 资产）。
    /// <para>
    /// 为什么需要它：编辑器的只读查询（<c>AssetEditor.DescribePrefab</c>）只需要成员表／视图／可见性／seams／
    /// 挂载与包装声明——这些全在预制体层；而 handler 资产的可执行体只在装载期存在，编辑器手里没有。
    /// 若为它另写一套映射，就会出现 R4 末条要防的「编辑器显示的可覆盖成员装载后不可覆盖」漂移，
    /// 故这里是**同一个映射函数**的两种取边方式，不是第二套展开。
    /// </para>
    /// <para><b>不得用于装载</b>：没有自有边的成员装载后不会执行任何东西。</para>
    /// </summary>
    public PrefabDefinition ToStructuralPrefabDefinition() => Project(resolve: null);

    private PrefabDefinition Project(Func<string, HandlerAsset>? resolve)
    {
        var members = new List<MemberDefinition>(Members.Count);
        foreach (var member in Members)
        {
            var ownEdges = resolve is not null && member.Handler is { } handler
                ? new[] { new OwnedEdge(resolve(handler.Id)) }
                : Array.Empty<OwnedEdge>();

            members.Add(new MemberDefinition(
                member.Name,
                member.View,
                member.Visibility,
                ownEdges,
                layer: PrefabLayer.Asset,
                declaredBy: Id,
                band: member.Band,
                bands: member.Bands,
                seams: member.Seams,
                sealedMember: member.Sealed));
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
            extends: Extends,
            layer: PrefabLayer.Asset,
            requiresHost: RequiresHost,
            members: members,
            privateFields: PrivateFields,
            mounts: Mounts,
            wraps: wraps,
            activeWhen: ActiveWhen,
            oneShot: OneShot,
            disposalSignal: DisposalSignal,
            authorizesModing: AuthorizesModing);
    }
}

/// <summary>组件成员资产：成员名 ＋ 可见性 ＋ 视图声明 ＋ 可选 handler 源码 ＋ 可选段声明 ＋ 可选具名缝声明。</summary>
/// <param name="Name">成员名（寻址用的成员名段；零转换，允许含 <c>.</c>）。</param>
/// <param name="Visibility">可见性位标志。</param>
/// <param name="View">视图声明（封闭内建集）。</param>
/// <param name="Handler">handler 声明（null＝纯编排成员，无自有边）。</param>
/// <param name="Band">自有边所属段名（null＝默认段；R1i band 修订）。</param>
/// <param name="Bands">段序声明（有序段名；null＝单段方案）。</param>
/// <param name="Seams">handler 内部的具名插入点（null／空＝不声明；R1i 修订 2026-10-29：seam 恢复）。</param>
/// <param name="Sealed">成员级封死（R2；缺省 false＝可覆写；父用它逐个排除成员）。</param>
public sealed record ComponentMemberAsset(
    string Name,
    MemberVisibility Visibility,
    ViewSpec View,
    ComponentHandlerAsset? Handler,
    string? Band = null,
    IReadOnlyList<string>? Bands = null,
    IReadOnlyList<string>? Seams = null,
    bool Sealed = false);

/// <summary>handler 资产声明：id ＋ 入口名 ＋ （csx 源码 ／ 程序集键 ／ handler DSL 文档，三选一）。</summary>
/// <param name="Id">资产 id（全局唯一；多份资产可引用同一 id）。</param>
/// <param name="Entry">入口名（默认 <c>HandleAsync</c>）。</param>
/// <param name="Script">csx 源码（与 <paramref name="AssemblyKey"/>／<paramref name="Dsl"/> 互斥）。</param>
/// <param name="AssemblyKey">程序集处理器键（已注册＝直接引用；null＝csx 来源）。</param>
/// <param name="Dsl">
/// handler DSL 文档的**相对包根路径**（如 <c>handler-dsl/grenadier-ondeployed.hdsl.json</c>；D11：DSL 不是资产，
/// 没有 id／版本／lock）。非 null ＝成员由 DSL 描述，csx 在**草案装载期现场生成**（不写盘，R28）。
/// </param>
/// <param name="DslHash">引用 DSL 时快照的内容哈希（<c>flatten --check</c> 与「草案落后于父」比对用；不参与产物装载期校验）。</param>
public sealed record ComponentHandlerAsset(
    string Id,
    string Entry,
    string? Script,
    string? AssemblyKey,
    string? Dsl = null,
    string? DslHash = null)
{
    /// <summary>来源形态（三选一；由 schema 保证恰有一种）。</summary>
    public HandlerSourceKind SourceKind =>
        Dsl is not null ? HandlerSourceKind.Dsl
        : Script is not null ? HandlerSourceKind.Script
        : HandlerSourceKind.AssemblyKey;

    /// <summary>
    /// **来源标记**（R28／S11；只出现在摊平产物里，草案恒为 null）：handler 文本由 handler DSL 生成时，
    /// 记生成器版本、目录内容版本、DSL 文档路径与双哈希、节点路径→行号区间。
    /// <para>
    /// 摊平产物内每个「由 DSL 生成的 handler」都带它；原样复制的手写 csx **不带**
    /// （R28 第 4 条）。「摊平的摊平」对父已是生成物的成员**原样复制该块**、不重新生成（S11 第 ③ 类）。
    /// </para>
    /// </summary>
    public HandlerGeneratedBlock? Generated { get; init; }
}

/// <summary>
/// handler 文本的**来源标记**（R28／S11；D5 的双哈希）。随生成文本一起写入摊平产物。
/// <para>
/// <see cref="TextHash"/> 是**产物装载期唯一校验的对象**（重算文本哈希，不一致＝「生成物被改写」诊断）；
/// <see cref="DslHash"/> **不进装载期**（产物里没有 DSL，重算不出来），只服务血缘溯源、
/// 「草案落后于父」比对与 <c>flatten --check</c>。
/// </para>
/// <para>
/// 两者都**不参与行为判定**，也**不得**被当作缓存键或去重键（D5）。
/// </para>
/// </summary>
/// <param name="Generator">生成器标识（当前恒 <c>handler-dsl/v1</c>）。</param>
/// <param name="CatalogRevision">生成时的节点目录内容版本（诊断用；不是门）。</param>
/// <param name="Source">DSL 文档相对包根的路径（可读性靠它，不靠 handler id；D9／D11）。</param>
/// <param name="DslHash">生成时 DSL 文档的内容哈希（<c>sha256:…</c>；只服务血缘与 <c>flatten --check</c>）。</param>
/// <param name="TextHash">生成后 csx 文本的内容哈希（<c>sha256:…</c>；产物装载期唯一校验对象）。</param>
/// <param name="NodeMap">节点路径 → 行号区间（调试可读性补偿；**不进注释、不进 textHash**，D12）。</param>
public sealed record HandlerGeneratedBlock(
    string Generator,
    int CatalogRevision,
    string Source,
    string DslHash,
    string TextHash,
    IReadOnlyDictionary<string, HandlerLineRange>? NodeMap = null)
{
    /// <summary>当前生成器标识（与契约包 <c>contracts/handler-dsl/v1</c> 对应）。</summary>
    public const string CurrentGenerator = "handler-dsl/v1";
}

/// <summary>行号区间（1 起、闭区间；<see cref="HandlerGeneratedBlock.NodeMap"/> 的值）。</summary>
public sealed record HandlerLineRange(int Start, int End);

/// <summary>成员 handler 的来源形态（三选一互斥；D11）。</summary>
public enum HandlerSourceKind
{
    /// <summary>csx 源码（原样编译）。</summary>
    Script,

    /// <summary>程序集键（旧来源；组件资产不接受）。</summary>
    AssemblyKey,

    /// <summary>handler DSL 文档（草案装载期现场生成 csx；R28）。</summary>
    Dsl,
}

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
