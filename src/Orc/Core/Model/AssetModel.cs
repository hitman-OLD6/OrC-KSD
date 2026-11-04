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
    /// <exception cref="ArgumentException">id 为空或纯空白；extends 自指；空位重名／跨层重名。</exception>
    /// <param name="extends">父资产 id（null＝自包含）。</param>
    /// <param name="label">展示名（R35；null＝未声明，前端回退显示 id）。</param>
    /// <param name="description">展示描述（R35；null＝未声明）。模板的 description 取信号的 <c>Notes</c>。</param>
    /// <param name="slots">
    /// **资产级空位**（R39 裁决 8；null／空＝无空位）：绑定整个资产，五种标量／引用类型，
    /// 多成员可引用同名。与成员级 <c>members[].slot</c> 共用一套类型与一个扁平 <c>args</c> 表；
    /// **跨层不得重名**（空位名与成员名撞名＝拒绝）。
    /// </param>
    /// <param name="origin">
    /// **出身记录**（R39 裁决 10／S30／S31；null＝手写资产没有）：只用于追溯与编辑器回填，
    /// **不是依赖**、不参与解析、不派生 id；模板／草案被删不影响装载。
    /// </param>
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
        string? label = null,
        string? description = null,
        IReadOnlyList<AssetSlotDeclaration>? slots = null,
        AssetOrigin? origin = null,
        string? save = null)
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
        Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
        Slots = slots?.ToArray() ?? Array.Empty<AssetSlotDeclaration>();
        Origin = origin;
        Save = SavePolicy.Normalize(save);

        if (Extends is not null && string.Equals(Extends, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"资产 '{Id}' 的 extends 指向自身（继承不得自指）。", nameof(extends));
        }

        ValidateSlots();
    }

    /// <summary>
    /// **空位的两层校验**（R39 裁决 8）：只在这里做一次，读写与构造共用。
    /// <list type="bullet">
    ///   <item>资产级空位名在资产内唯一；</item>
    ///   <item>**跨层不得重名**：资产级空位名不得与任何成员名相同（成员级空位的键就是成员名）；</item>
    ///   <item>成员级空位只接受 <c>handler</c>（类型判定在 <see cref="MemberSlotDeclaration"/> 的构造期）。</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">重名或放错层。</exception>
    private void ValidateSlots()
    {
        if (Slots.Count == 0 && Members.All(static m => m.Slot is null))
        {
            return;
        }

        var slotNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in Slots)
        {
            if (!slotNames.Add(slot.Name))
            {
                throw new ArgumentException(
                    $"资产 '{Id}' 重复声明资产级空位 '{slot.Name}'（args 是扁平表，键必须唯一）。", "slots");
            }
        }

        foreach (var member in Members)
        {
            if (slotNames.Contains(member.Name))
            {
                throw new ArgumentException(
                    $"资产 '{Id}' 的资产级空位与成员名同为 '{member.Name}'："
                    + "args 是一层扁平具名表，跨层不得重名（空位名与成员名必须分开，R39 裁决 8）。",
                    "slots");
            }
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

    // 废止留痕（R39 裁决 4／5，S28／S32）：
    //  - 资产级 `authorize-moding`（R2）不再存在：覆写资格只由「链上声明了成员级 slot ∧ 未 sealed」决定；
    //  - 资产级 `abstract`（R30）不再存在：抽象性由「存在未填空位」推导（见 HasUnfilledSlots）。
    // 两者的 JSON 字段在 AssetSchema.Read 里按「已废止字段」拒绝（不是静默忽略）。

    /// <summary>
    /// **本资产是否饱和**（R39 裁决 4／8；S28 的唯一判定入口之一）。
    /// <para>
    /// 未填 ＝ 资产级 <c>slot ∧ ¬value</c> **∨** 成员级 <c>slot ∧ ¬handler</c>——两层取或，
    /// **不是**「先查一层再查另一层」的两段式。装载期、摊平期、DTO 都只调这个谓词（或它在展开层的投影）。
    /// </para>
    /// </summary>
    public bool HasUnfilledSlots => Slots.Any(static slot => !slot.IsFilled) || Members.Any(static m => m.IsUnfilledSlot);

    /// <summary>展示名（R35；null＝未声明，前端回退显示 <see cref="Id"/>）。</summary>
    public string? Label { get; }

    /// <summary>展示描述（R35；null＝未声明）。模板的 description 直接取信号的 <c>Notes</c>。</summary>
    public string? Description { get; }

    /// <summary>
    /// **资产级空位**（R39 裁决 8；空数组＝无空位）：绑定整个资产，多成员可引用同名。
    /// 状态只看条目有没有 <see cref="AssetSlotDeclaration.Value"/>，不另设标志位。
    /// </summary>
    public IReadOnlyList<AssetSlotDeclaration> Slots { get; }

    /// <summary>
    /// **出身记录**（R39 裁决 10／S30／S31；null＝手写资产）：摊平产物带 <c>draftId</c>，
    /// <c>named</c> 实例带 <c>templateId</c>／<c>argsHash</c>／<c>args</c>。
    /// <para>**不是依赖**：装载链不得因它缺失或解析不到而拒绝资产（R8／R10 同向）。</para>
    /// </summary>
    public AssetOrigin? Origin { get; }

    /// <summary>
    /// **存档门户**（本会话 S1／D50；null＝未声明＝不可存档）：存档导出器的服务 id。
    /// **沿 <c>Extends</c> 链继承、子可覆盖**（合并在 <see cref="PrefabCatalog.Build"/>）；
    /// 装载期只校验形态（<see cref="SavePolicy.Normalize"/>），不校验服务存在性。
    /// <para>handler 的存在即开关的存档版：<c>save</c> 字段的**存在**本身就是可存档的开关，不另设布尔标志。</para>
    /// </summary>
    public string? Save { get; }

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
                sealedMember: member.Sealed,
                guard: member.Guard,
                slot: member.Slot,
                hasHandler: member.Handler is not null));
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
            label: Label,
            description: Description,
            slots: Slots,
            // 出身记录与存档门户一并透传（D52：origin 投影到预制体层；save 门户进 Build 沿链合并）。
            origin: Origin,
            save: Save);
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
/// <param name="Guard">守卫档（R32；null＝不守卫）。**摊平产物不得携带本字段**（守卫已烘进 csx）。</param>
/// <param name="Label">展示名（R35；null＝未声明，前端回退显示成员名）。</param>
/// <param name="Slot">
/// **成员级空位**（R39 裁决 8；null＝本成员不是空位）：只接受 <c>handler</c>，绑定单个成员。
/// 与 <paramref name="Handler"/> **正交**（D40）：本字段＝「这里有一个空位」，handler＝「现在填的是什么」。
/// 四行判定表（未填／已填可再填／普通成员／纯编排成员）就是这两个字段的存在性组合，**没有第三个状态位**。
/// </param>
public sealed record ComponentMemberAsset(
    string Name,
    MemberVisibility Visibility,
    ViewSpec View,
    ComponentHandlerAsset? Handler,
    string? Band = null,
    IReadOnlyList<string>? Bands = null,
    IReadOnlyList<string>? Seams = null,
    bool Sealed = false,
    HandlerGuard? Guard = null,
    string? Label = null,
    MemberSlotDeclaration? Slot = null)
{
    /// <summary>
    /// **未填空位**（D40 判定表第 1 行；S28／S32 的**唯一**成员层判据）：
    /// 声明了 <c>slot</c> 却没有 <c>handler</c>。
    /// <para>「已填」只看 handler 是否存在，与内容无关（D40）；没有第三态标志位。</para>
    /// </summary>
    public bool IsUnfilledSlot => Slot is not null && Handler is null;
}

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
