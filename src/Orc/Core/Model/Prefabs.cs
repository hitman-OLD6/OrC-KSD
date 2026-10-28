namespace Orc.Core.Model;

/// <summary>
/// 预制体声明（R3：实体模板＝OOP 的**类**，纯声明、无运行期状态）。
/// <para>
/// 封装＝成员可见性位标志（<see cref="MemberVisibility"/>）；继承＝<see cref="Extends"/>（全继承）；
/// 多态＝整段覆写（<see cref="MemberDefinition.Override"/>，无 super 调用）。
/// </para>
/// <para>
/// 本类型只是"一份声明"，装载期由 <see cref="PrefabCatalog"/> 沿继承链展开为
/// <see cref="PrefabType"/>（成员表＋私有字段表＋挂载／包装声明），成员表可在装载期**静态枚举**。
/// </para>
/// </summary>
public sealed class PrefabDefinition
{
    /// <summary>创建预制体声明。</summary>
    /// <param name="id">预制体 id（全局唯一；也是成员寻址的"实体"锚点）。</param>
    /// <param name="kind">实体类别。</param>
    /// <param name="extends">父预制体 id（null＝无继承）。</param>
    /// <param name="layer">来源层。</param>
    /// <param name="hosts">本预制体描述的宿主类型（仅类型预制体声明；None＝非宿主类型）。</param>
    /// <param name="requiresHost">组件的宿主需求（null＝继承父声明；根上 null 视为 <see cref="HostKind.Any"/>）。</param>
    /// <param name="members">成员声明（声明序即成员表序）。</param>
    /// <param name="privateFields">私有字段声明（R3e）。</param>
    /// <param name="mounts">挂载声明（设计端编排通道，R1b）。</param>
    /// <param name="wraps">包装声明（元效果端修改通道，R1b／S2a）。</param>
    /// <param name="components">组件引用（宿主预制体里组件的**声明序**；R1g 的唯一次序依据之一）。</param>
    /// <param name="activeWhen">
    /// 装配时点（仅组件实体有意义；R3i 缺省 <see cref="ActiveWhen.Placed"/>）。
    /// <para>
    /// 本批只用于 S3 第 4c 条：「<c>active-when: placed</c> 的组件挂加载期信号＝装载期拒绝」。
    /// 声明的落值由调用方显式给出（预制体文本/dto 里能看到），**不靠 C# 默认参数隐式决定**（S8 第 13 条）。
    /// </para>
    /// </param>
    /// <param name="oneShot">
    /// 一次性组件（R3k）：<c>one-shot: true</c>——该组件任一成员首次执行完成后由框架注销整个组件实例。
    /// 缺省 false；缺省值同样在声明层显式落值（不靠 C# 默认参数，S8 第 13 条的同一口径）。
    /// </param>
    /// <param name="disposalSignal">
    /// 兜底信号（R1f；仅组件有意义）：null＝<see cref="ComponentDisposalPolicy.DefaultSignal"/>。
    /// 声明为 <c>effect.removed</c>＝装载期拒绝（<see cref="ComponentDisposalPolicy"/> 是唯一规则来源）。
    /// </param>
    /// <exception cref="ArgumentException">id 为空或纯空白。</exception>
    public PrefabDefinition(
        string id,
        EntityKind kind,
        string? extends = null,
        PrefabLayer layer = PrefabLayer.Kernel,
        HostKind hosts = HostKind.None,
        HostKind? requiresHost = null,
        IEnumerable<MemberDefinition>? members = null,
        IEnumerable<PrivateFieldDefinition>? privateFields = null,
        IEnumerable<MountDeclaration>? mounts = null,
        IEnumerable<WrapDeclaration>? wraps = null,
        IEnumerable<string>? components = null,
        ActiveWhen activeWhen = ActiveWhen.Placed,
        bool oneShot = false,
        string? disposalSignal = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("预制体 id 不能为空或纯空白。", nameof(id));
        }

        Id = id;
        Kind = kind;
        Extends = extends;
        Layer = layer;
        Hosts = hosts;
        RequiresHost = requiresHost;
        Members = members?.ToArray() ?? Array.Empty<MemberDefinition>();
        PrivateFields = privateFields?.ToArray() ?? Array.Empty<PrivateFieldDefinition>();
        Mounts = mounts?.ToArray() ?? Array.Empty<MountDeclaration>();
        Wraps = wraps?.ToArray() ?? Array.Empty<WrapDeclaration>();
        Components = components?.ToArray() ?? Array.Empty<string>();
        ActiveWhen = activeWhen;
        OneShot = oneShot;
        DisposalSignal = disposalSignal;
    }

    /// <summary>预制体 id（唯一）。</summary>
    public string Id { get; }

    /// <summary>实体类别。</summary>
    public EntityKind Kind { get; }

    /// <summary>父预制体 id（全继承；null＝无父）。</summary>
    public string? Extends { get; }

    /// <summary>来源层。</summary>
    public PrefabLayer Layer { get; }

    /// <summary>本预制体描述的宿主类型（类型预制体专用）。</summary>
    public HostKind Hosts { get; }

    /// <summary>组件宿主需求（null＝继承）。</summary>
    public HostKind? RequiresHost { get; }

    /// <summary>成员声明（声明序）。</summary>
    public IReadOnlyList<MemberDefinition> Members { get; }

    /// <summary>私有字段声明。</summary>
    public IReadOnlyList<PrivateFieldDefinition> PrivateFields { get; }

    /// <summary>挂载声明。</summary>
    public IReadOnlyList<MountDeclaration> Mounts { get; }

    /// <summary>包装声明。</summary>
    public IReadOnlyList<WrapDeclaration> Wraps { get; }

    /// <summary>组件引用的声明序（宿主预制体）。</summary>
    public IReadOnlyList<string> Components { get; }

    /// <summary>装配时点（组件实体；R3i 缺省 <see cref="ActiveWhen.Placed"/>）。</summary>
    public ActiveWhen ActiveWhen { get; }

    /// <summary>一次性组件（R3k；缺省 false，声明层显式落值）。</summary>
    public bool OneShot { get; }

    /// <summary>兜底信号声明（null＝缺省 <see cref="ComponentDisposalPolicy.DefaultSignal"/>；仅组件有意义）。</summary>
    public string? DisposalSignal { get; }
}

/// <summary>
/// 展开后的预制体类型（＝"类"的运行时描述）：沿 <c>extends</c> 链合并后的成员表、私有字段表、挂载／包装声明。
/// <para>
/// **成员表可在装载期静态枚举**（S2 验收）：本类型在装载期构建完毕、之后只读；
/// 成员查找、来源层、位次都可据此推出，不依赖任何运行期装配时序。
/// </para>
/// </summary>
public sealed class PrefabType
{
    /// <summary>owner 通道上恒可访问的实体公开属性（R3e：私有字段 ∪ 实体公开属性）。</summary>
    public static readonly IReadOnlyList<string> BuiltInOwnerMembers = new[] { "Id", "Name", "Ref", "Life", "Host" };

    private readonly Dictionary<string, MemberDefinition> _members;
    private readonly Dictionary<string, PrivateFieldDefinition> _privateFields;

    internal PrefabType(
        PrefabDefinition leaf,
        IReadOnlyList<MemberDefinition> members,
        IReadOnlyList<PrivateFieldDefinition> privateFields,
        IReadOnlyList<MountDeclaration> mounts,
        IReadOnlyList<WrapDeclaration> wraps,
        IReadOnlyList<string> components,
        IReadOnlyList<string> chain,
        HostKind requiresHost,
        string disposalSignal)
    {
        Id = leaf.Id;
        Kind = leaf.Kind;
        Layer = leaf.Layer;
        Hosts = leaf.Hosts;
        RequiresHost = requiresHost;
        Members = members;
        PrivateFields = privateFields;
        Mounts = mounts;
        Wraps = wraps;
        Components = components;
        Chain = chain;
        ActiveWhen = leaf.ActiveWhen;
        OneShot = leaf.OneShot;
        DisposalSignal = disposalSignal;

        _members = new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            _members[member.Name] = member;
        }

        _privateFields = new Dictionary<string, PrivateFieldDefinition>(StringComparer.Ordinal);
        foreach (var field in privateFields)
        {
            _privateFields[field.Name] = field;
        }

        var owner = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in BuiltInOwnerMembers)
        {
            owner.Add(name);
        }

        foreach (var field in privateFields)
        {
            owner.Add(field.Name);
        }

        OwnerMemberNames = owner;
    }

    /// <summary>预制体 id（叶子）。</summary>
    public string Id { get; }

    /// <summary>实体类别。</summary>
    public EntityKind Kind { get; }

    /// <summary>来源层（叶子声明层）。</summary>
    public PrefabLayer Layer { get; }

    /// <summary>本预制体描述的宿主类型。</summary>
    public HostKind Hosts { get; }

    /// <summary>合并后的宿主需求（组件）。</summary>
    public HostKind RequiresHost { get; }

    /// <summary>成员表（合并后；声明序＝父先＋子追加，覆写原位替换）。</summary>
    public IReadOnlyList<MemberDefinition> Members { get; }

    /// <summary>私有字段表（合并后）。</summary>
    public IReadOnlyList<PrivateFieldDefinition> PrivateFields { get; }

    /// <summary>挂载声明（父＋子）。</summary>
    public IReadOnlyList<MountDeclaration> Mounts { get; }

    /// <summary>包装声明（父＋子）。</summary>
    public IReadOnlyList<WrapDeclaration> Wraps { get; }

    /// <summary>组件引用的声明序（宿主预制体）。</summary>
    public IReadOnlyList<string> Components { get; }

    /// <summary>
    /// 装配时点（组件实体；R3i 缺省 <see cref="ActiveWhen.Placed"/>）。
    /// 组件自己的声明决定自己的时点（不随 <c>extends</c> 继承）。
    /// </summary>
    public ActiveWhen ActiveWhen { get; }

    /// <summary>一次性组件（R3k）：任一成员首次执行完成后由框架注销整个实例（随实例生命周期、复装复位）。</summary>
    public bool OneShot { get; }

    /// <summary>兜底信号（恒非空：缺省 <see cref="ComponentDisposalPolicy.DefaultSignal"/>；框架在这条信号上合成注销边）。</summary>
    public string DisposalSignal { get; }

    /// <summary>继承链（根→叶子）。</summary>
    public IReadOnlyList<string> Chain { get; }

    /// <summary>owner 通道允许的名字集合（私有字段 ∪ 实体公开属性；S5 第 6d 条静态检查用）。</summary>
    public IReadOnlySet<string> OwnerMemberNames { get; }

    /// <summary>查找成员；不存在＝null。</summary>
    public MemberDefinition? FindMember(string name)
        => _members.TryGetValue(name, out var member) ? member : null;

    /// <summary>是否有该成员。</summary>
    public bool HasMember(string name) => _members.ContainsKey(name);

    /// <summary>查找私有字段；不存在＝null。</summary>
    public PrivateFieldDefinition? FindPrivateField(string name)
        => _privateFields.TryGetValue(name, out var field) ? field : null;

    /// <summary>成员名清单（声明序）。</summary>
    public IReadOnlyList<string> MemberNames => Members.Select(static m => m.Name).ToArray();
}

/// <summary>
/// 预制体目录（装载期的唯一真源）：登记预制体声明、按 <c>extends</c> 链展开为 <see cref="PrefabType"/>、提供宿主类型查询。
/// <para>
/// 展开是**装载期**动作：继承环、重复 id、可见性放宽、视图字段与私有字段撞名、组件重复引用等错误在此一次性暴露。
/// </para>
/// </summary>
public sealed class PrefabCatalog
{
    private readonly Dictionary<string, PrefabDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PrefabType> _resolved = new(StringComparer.Ordinal);
    private readonly List<PrefabType> _hostTypes = new();

    /// <summary>登记一个预制体声明（重复 id＝拒绝）。</summary>
    /// <exception cref="ArgumentNullException">definition 为 null。</exception>
    /// <exception cref="PrefabLoadException">重复 id。</exception>
    public PrefabCatalog Add(PrefabDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_definitions.ContainsKey(definition.Id))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure, $"预制体 id '{definition.Id}' 重复登记（id 必须唯一）。");
        }

        _definitions[definition.Id] = definition;
        _resolved.Clear();
        _hostTypes.Clear();
        return this;
    }

    /// <summary>登记一批预制体声明。</summary>
    public PrefabCatalog AddRange(IEnumerable<PrefabDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            Add(definition);
        }

        return this;
    }

    /// <summary>是否有该预制体。</summary>
    public bool Contains(string id) => _definitions.ContainsKey(id);

    /// <summary>
    /// 已登记的预制体声明（登记序；装订器在提交前用它复制一份"暂存目录"，S14 的先全量校验再统一注册）。
    /// </summary>
    public IReadOnlyList<PrefabDefinition> Definitions => _definitions.Values.ToArray();

    /// <summary>按 id 展开预制体类型（结果缓存；装载期一次性校验）。</summary>
    /// <exception cref="ArgumentNullException">id 为 null。</exception>
    /// <exception cref="KeyNotFoundException">未登记该预制体。</exception>
    /// <exception cref="PrefabLoadException">继承环、可见性放宽、字段撞名、组件重复引用等。</exception>
    public PrefabType Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (_resolved.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var chain = new List<string>();
        var type = Build(id, chain, 0);
        _resolved[id] = type;
        return type;
    }

    /// <summary>全部宿主类型预制体（<see cref="PrefabType.Hosts"/> 非 None；装载期枚举）。</summary>
    public IReadOnlyList<PrefabType> HostTypes
    {
        get
        {
            EnsureHostTypes();
            return _hostTypes;
        }
    }

    /// <summary>覆盖指定宿主需求的全部宿主类型（R3h：<c>host#</c> 的存在性按"允许的全部宿主类型"取交集校验）。</summary>
    /// <exception cref="ArgumentException">required 为 <see cref="HostKind.None"/>。</exception>
    public IReadOnlyList<PrefabType> HostTypesCovering(HostKind required)
    {
        if (required == HostKind.None)
        {
            throw new ArgumentException("宿主需求不能为 None（组件必须声明 requires-host）。", nameof(required));
        }

        EnsureHostTypes();
        return _hostTypes.Where(t => (t.Hosts & required) != 0).ToArray();
    }

    private void EnsureHostTypes()
    {
        if (_hostTypes.Count > 0)
        {
            return;
        }

        foreach (var definition in _definitions.Values)
        {
            if (definition.Hosts != HostKind.None)
            {
                _hostTypes.Add(Resolve(definition.Id));
            }
        }
    }

    private PrefabType Build(string id, List<string> chain, int depth)
    {
        if (depth > 64)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure, $"预制体继承链过深（'{id}'）：疑似 extends 成环。");
        }

        if (chain.Contains(id, StringComparer.Ordinal))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"预制体继承成环：{string.Join(" → ", chain)} → {id}。");
        }

        if (!_definitions.TryGetValue(id, out var definition))
        {
            throw new KeyNotFoundException($"预制体 '{id}' 未登记（无法展开）。");
        }

        chain.Add(id);

        // 先展开父（全继承：成员清单、视图、可见性、私有字段、挂载/包装声明一并继承）。
        PrefabType? parent = null;
        if (definition.Extends is not null)
        {
            parent = Build(definition.Extends, chain, depth + 1);
        }

        var members = parent is null ? new List<MemberDefinition>() : new List<MemberDefinition>(parent.Members);
        foreach (var member in definition.Members)
        {
            var existing = members.FindIndex(m => string.Equals(m.Name, member.Name, StringComparison.Ordinal));
            if (existing < 0)
            {
                members.Add(member);
            }
            else
            {
                // R3a：覆写＝整段替换（读子预制体即知最终行为，无 super）；可见性只能收紧（R5）。
                members[existing] = members[existing].Override(member);
            }
        }

        var privateFields = parent is null
            ? new List<PrivateFieldDefinition>()
            : new List<PrivateFieldDefinition>(parent.PrivateFields);
        foreach (var field in definition.PrivateFields)
        {
            if (privateFields.Any(f => string.Equals(f.Name, field.Name, StringComparison.Ordinal)))
            {
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"预制体 '{definition.Id}' 重复声明私有字段 '{field.Name}'（子预制体不得重声明父字段；覆写只适用于成员）。");
            }

            privateFields.Add(field);
        }

        var mounts = parent is null
            ? new List<MountDeclaration>()
            : new List<MountDeclaration>(parent.Mounts);
        mounts.AddRange(definition.Mounts);

        var wraps = parent is null
            ? new List<WrapDeclaration>()
            : new List<WrapDeclaration>(parent.Wraps);
        wraps.AddRange(definition.Wraps);

        var components = parent is null
            ? new List<string>()
            : new List<string>(parent.Components);
        foreach (var component in definition.Components)
        {
            if (components.Contains(component, StringComparer.Ordinal))
            {
                // R3i：同一 prefab 在同一宿主上至多一份实例（需要两份＝两个不同预制体；本期不引入组件参数）。
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"预制体 '{definition.Id}' 重复引用组件 '{component}'（同一 prefab 在同一宿主上至多一份实例）。");
            }

            components.Add(component);
        }

        if (components.Count > 0 && definition.Kind != EntityKind.Card)
        {
            // R3d：可组合是**实体的一种声明能力**——目前只有卡牌（含 HQ）具备。
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"预制体 '{definition.Id}'（kind={definition.Kind}）声明了组件引用："
                + "非可组合实体声明组件＝装载期拒绝（R3d；可组合能力目前只对卡牌实体开放）。");
        }

        // 同名禁止（R3e / S5 第 6c 条）：视图字段名与同一实体的私有字段名不得相同——
        // 技术上可区分，但两条通道指向同名状态会让读者误判，代价大于收益。
        var privateNames = new HashSet<string>(privateFields.Select(static f => f.Name), StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (var field in member.View.Fields)
            {
                if (privateNames.Contains(field.Name))
                {
                    throw new PrefabLoadException(
                        PrefabErrorCategories.View,
                        $"成员 '{member.Name}' 的视图字段 '{field.Name}' 与预制体 '{definition.Id}' 的私有字段同名"
                        + "（公共变量与私有变量名字空间必须分离）。");
                }
            }
        }

        var requiresHost = definition.RequiresHost
            ?? parent?.RequiresHost
            ?? (definition.Kind == EntityKind.Component ? HostKind.Any : HostKind.None);

        // 保留成员名（R1f：框架合成的注销成员不得与作者成员撞名；作者也不得声明 framework. 前缀）。
        foreach (var member in members)
        {
            if (ComponentDisposalPolicy.IsReservedMember(member.Name)
                && !string.Equals(member.Name, ComponentDisposalPolicy.DisposalMemberName, StringComparison.Ordinal))
            {
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"预制体 '{definition.Id}' 声明了保留成员名 '{member.Name}'（'{ComponentDisposalPolicy.ReservedPrefix}' "
                    + "前缀由框架专用：注销成员、一次性守卫等不得由作者声明）。");
            }
        }

        // 兜底信号（R1f）：唯一规则来源＝ComponentDisposalPolicy；声明 effect.removed ＝装载期拒绝。
        var disposalSignal = definition.DisposalSignal
            ?? parent?.DisposalSignal
            ?? ComponentDisposalPolicy.DefaultSignal;
        if (ComponentDisposalPolicy.ValidateSignal(disposalSignal, definition.Id) is { } disposalError)
        {
            throw new PrefabLoadException(PrefabErrorCategories.Structure, disposalError);
        }

        // one-shot 组件无任何外部可触发的边＝装载期拒绝（R3k：它永不执行，也就永不注销）。
        if (definition.OneShot
            && definition.Kind == EntityKind.Component
            && mounts.Count == 0
            && !members.Any(static m => m.Has(MemberVisibility.Callable)))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"预制体 '{definition.Id}' 声明 one-shot=true 但没有任何外部可触发的边"
                + "（未挂载任何信号、也没有可被调用的成员）——该组件永远不会执行，也就永远不会注销（R3k）。");
        }

        // 继承链＝父链 + 自身（根→叶子）；可变 chain 只用于成环检测。
        var chainCopy = parent is null
            ? new List<string> { id }
            : new List<string>(parent.Chain) { id };
        chain.RemoveAt(chain.Count - 1);

        return new PrefabType(
            definition,
            members,
            privateFields,
            mounts,
            wraps,
            components,
            chainCopy,
            requiresHost,
            disposalSignal);
    }
}
