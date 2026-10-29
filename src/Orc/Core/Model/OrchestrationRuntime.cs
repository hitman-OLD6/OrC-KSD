namespace Orc.Core.Model;

/// <summary>规划出的一条挂载边（装载期解析结果，尚未建边）。</summary>
/// <param name="Address">目标成员地址（解析后）。</param>
/// <param name="Declaration">声明原文（<c>作用域#成员名</c>；审查面用它还原作者写下的地址）。</param>
/// <param name="Target">目标成员声明。</param>
/// <param name="Member">被挂载的自身成员声明。</param>
/// <param name="Order">声明序（组件声明序 + 挂载声明序）。</param>
/// <param name="Source">边来源。</param>
public sealed record PlannedMount(
    MemberAddress Address,
    string Declaration,
    MemberDefinition Target,
    MemberDefinition Member,
    DeclarationOrder Order,
    EdgeSource Source);

/// <summary>挂载规划结果：边清单＋错误集合（**收集齐全部失败项**，不遇错即停）。</summary>
public sealed class MountPlan
{
    /// <summary>创建规划结果。</summary>
    public MountPlan(IReadOnlyList<PlannedMount> mounts, PrefabLoadReport report)
    {
        Mounts = mounts;
        Report = report;
    }

    /// <summary>规划出的边（声明序）。</summary>
    public IReadOnlyList<PlannedMount> Mounts { get; }

    /// <summary>错误集合。</summary>
    public PrefabLoadReport Report { get; }
}

/// <summary>
/// 挂载规划器（S4）：在**装载期**把 <c>作用域#成员名</c> 静态解析成具体成员，并完成全部静态校验。
/// <para>
/// 校验项（逐条对应 S4 验收）：
/// ①地址只按第一个 <c>#</c> 切分（成员名可含 <c>.</c>）；
/// ②目标成员存在，且声明了 <see cref="MemberVisibility.Hostable"/>（<c>self#</c> 自挂载不受可见性约束）；
/// ③被挂载成员声明了 <see cref="MemberVisibility.Mountable"/>（同上，<c>self#</c> 例外）；
/// ④子视图字段集 ⊆ 宿主视图字段集 ∪ 子实体自有成员（缺失字段逐个报出，R1c）；
/// ⑤挂载声明携带相位＝通道混用，拒绝（R1b／A-2）；
/// ⑥<code>host#</code> 的存在性按组件 <c>requires-host</c> 允许的**全部**宿主类型校验（R3h）。
/// </para>
/// <para>解析全部发生在装载期（静态成员表），运行期只剩"运行时注册"这一条动态入口（R1e）。</para>
/// </summary>
public static class MountPlanner
{
    /// <summary>规划某实体的全部挂载声明。</summary>
    /// <exception cref="ArgumentNullException">runtime／declaring 为 null。</exception>
    public static MountPlan Plan(OrchestrationRuntime runtime, OrchestrationEntity declaring)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(declaring);

        var report = new PrefabLoadReport();
        var planned = new List<PlannedMount>();
        var type = declaring.Type;
        var engineType = runtime.Engine.Type;
        var componentIndex = ComponentIndexOf(declaring);

        for (var i = 0; i < type.Mounts.Count; i++)
        {
            var declaration = type.Mounts[i];
            var order = new DeclarationOrder(componentIndex, i, 0);

            if (declaration.Phase is not null)
            {
                report.Add(
                    PrefabErrorCategories.Channel,
                    declaration.Target,
                    $"挂载声明不得携带相位 '{declaration.Phase}'：挂载管编排、包装管修改，两条通道不可混用（R1b）。");
                continue;
            }

            if (!MemberAddress.TryParse(declaration.Target, out var address, out var addressError))
            {
                report.Add(PrefabErrorCategories.Addressing, declaration.Target, addressError ?? "地址非法。");
                continue;
            }

            if (address.Scope == AddressScope.Host && declaring.Host is null)
            {
                report.Add(
                    PrefabErrorCategories.Addressing,
                    address.ToString(),
                    "host# 无法解析：该实体没有宿主引用（host 作用域只对组件实体成立，R3d）。");
                continue;
            }

            var member = type.FindMember(declaration.Member);
            if (member is null)
            {
                report.Add(
                    PrefabErrorCategories.Addressing,
                    declaration.Target,
                    $"被挂载的成员 '{declaration.Member}' 在预制体 '{type.Id}' 上不存在（挂载声明无法解析）。");
                continue;
            }

            // host# 在 requires-host 允许多个宿主类型时逐个校验（R3h），但只产生**一条**边：
            // 边最终落在实际宿主实例上，多个宿主类型只是"该成员必须存在于全部允许类型"的静态证明。
            var failed = false;
            PlannedMount? candidate = null;

            foreach (var (targetType, target) in ResolveTargets(runtime, declaring, engineType, address, report))
            {
                if (target is null)
                {
                    failed = true;
                    continue;
                }

                // 无意义挂载＝装载期拒绝（S3 第 4c 条／R1g）：`active-when: placed` 的组件挂到加载期信号上，
                // 信号触发时它还没装配，永远不可能生效——这是配置错误，不是“到时再看”。
                if (declaring.EntityKind == EntityKind.Component
                    && declaring.Type.ActiveWhen == ActiveWhen.Placed
                    && target.SignalTiming == SignalTiming.Load)
                {
                    report.Add(
                        PrefabErrorCategories.Timing,
                        address.ToString(),
                        $"挂载被拒绝：组件 '{type.Id}' 的 active-when=placed，而 '{address}' 是加载期信号"
                        + "（时机 = load）——该组件在信号触发时尚未装配，永不生效（S3 第 4c 条）。"
                        + "如确需在加载期生效，请把 active-when 改为 loaded。");
                    failed = true;
                    continue;
                }

                // 可见性只约束跨实体挂载；self# 自挂载不受约束（R5 补充）。
                if (address.Scope != AddressScope.Self)
                {
                    if (!target.Has(MemberVisibility.Hostable))
                    {
                        report.Add(
                            PrefabErrorCategories.Visibility,
                            address.ToString(),
                            $"目标成员 '{target.Name}'（预制体 '{targetType.Id}'）未声明「可作宿主（Hostable）」，跨实体挂载被拒绝。");
                        failed = true;
                        continue;
                    }

                    if (!member.Has(MemberVisibility.Mountable))
                    {
                        report.Add(
                            PrefabErrorCategories.Visibility,
                            address.ToString(),
                            $"成员 '{member.Name}'（预制体 '{type.Id}'）未声明「可被挂载（Mountable）」，挂到 '{address}' 被拒绝。");
                        failed = true;
                        continue;
                    }
                }

                if (address.Scope != AddressScope.Self)
                {
                    var allowed = new HashSet<string>(target.View.Names, StringComparer.Ordinal);
                    allowed.UnionWith(type.OwnerMemberNames);
                    allowed.UnionWith(type.MemberNames);

                    // 只校验**必填**字段（S11 迁移期的口径收敛）：可选字段不在宿主视图里＝投影为类型默认值，
                    // 与旧路径"载荷缺键即 null／默认"的行为一致（A-1：缺失值的失败面在首次执行，不在装载期）。
                    // 若把可选字段也纳入包含校验，多订阅旧成员（一个 handler 同时挂 turn.start／unit.deployed 等
                    // 载荷形状不同的信号）会被全量误杀——那类成员按信号分支读取可选字段是既有资产的常态。
                    var missing = member.View.Fields
                        .Where(f => f.Required && !allowed.Contains(f.Name))
                        .Select(static f => f.Name)
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        report.Add(new PrefabLoadError(
                            PrefabErrorCategories.View,
                            $"{address} ← {declaration.Member}",
                            $"挂载被拒绝：成员 '{member.Name}' 的**必填**视图字段不在宿主视图声明与自身成员之内"
                            + $"（宿主＝'{targetType.Id}'，缺失：{string.Join("、", missing)}）。",
                            missing));
                        failed = true;
                        continue;
                    }
                }

                candidate ??= new PlannedMount(address, declaration.Target, target, member, order, declaration.Source);
            }

            if (!failed && candidate is not null)
            {
                planned.Add(candidate);
            }
        }

        return new MountPlan(planned, report);
    }

    /// <summary>组件在宿主预制体里的声明序（引擎／卡牌自身＝0，组件从 1 起——R1g 的唯一次序依据）。</summary>
    internal static int ComponentIndexOf(OrchestrationEntity entity)
    {
        if (entity.Host is null)
        {
            return 0;
        }

        if (entity.DeclarationSegment > 0)
        {
            return entity.DeclarationSegment;
        }

        var components = entity.Host.Type.Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (string.Equals(components[i], entity.Type.Id, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// 解析地址到目标（可能是多个：<c>host#</c> 在 <c>requires-host</c> 允许多个宿主类型时逐个校验）。
    /// 解析失败经 <paramref name="report"/> 报出。
    /// </summary>
    private static IEnumerable<(PrefabType Type, MemberDefinition? Member)> ResolveTargets(
        OrchestrationRuntime runtime,
        OrchestrationEntity declaring,
        PrefabType engineType,
        MemberAddress address,
        PrefabLoadReport report)
    {
        switch (address.Scope)
        {
            case AddressScope.Engine:
            {
                // 用**实体**而非 PrefabType 查成员：运行时注册的成员（R1e）进的是实体覆盖层，
                // 静态表看不到它——"注册后立即可被挂载声明引用"要求这里必须看实体。
                var member = runtime.Engine.FindMember(address.Member);
                if (member is null)
                {
                    report.Add(
                        PrefabErrorCategories.Addressing,
                        address.ToString(),
                        $"目标成员不存在：作用域 'engine'（预制体 '{engineType.Id}'）下没有成员 '{address.Member}'。");
                }

                yield return (engineType, member);
                break;
            }

            case AddressScope.Self:
            {
                var member = declaring.FindMember(address.Member);
                if (member is null)
                {
                    report.Add(
                        PrefabErrorCategories.Addressing,
                        address.ToString(),
                        $"目标成员不存在：作用域 'self'（预制体 '{declaring.Type.Id}'）下没有成员 '{address.Member}'。");
                }

                yield return (declaring.Type, member);
                break;
            }

            case AddressScope.Host:
            {
                var hostTypes = runtime.Catalog.HostTypesCovering(declaring.Type.RequiresHost).ToList();
                if (declaring.Host is not null
                    && hostTypes.All(t => !string.Equals(t.Id, declaring.Host.Type.Id, StringComparison.Ordinal)))
                {
                    report.Add(
                        PrefabErrorCategories.Addressing,
                        address.ToString(),
                        $"requires-host={declaring.Type.RequiresHost} 不覆盖实际宿主类型 '{declaring.Host.Type.Id}'。");
                    yield break;
                }

                if (declaring.Host is not null)
                {
                    // 实际宿主类型排在首位：规划出的那一条边落在实际宿主实例上。
                    hostTypes.RemoveAll(t => string.Equals(t.Id, declaring.Host.Type.Id, StringComparison.Ordinal));
                    hostTypes.Insert(0, declaring.Host.Type);
                }

                if (hostTypes.Count == 0)
                {
                    report.Add(
                        PrefabErrorCategories.Addressing,
                        address.ToString(),
                        $"目标成员无法解析：没有宿主类型预制体覆盖 requires-host={declaring.Type.RequiresHost}"
                        + "（'host#' 的存在性按允许的全部宿主类型校验，R3h）。");
                    yield break;
                }

                foreach (var hostType in hostTypes)
                {
                    var member = hostType.FindMember(address.Member);
                    if (member is null)
                    {
                        report.Add(
                            PrefabErrorCategories.Addressing,
                            address.ToString(),
                            $"目标成员不存在：宿主类型 '{hostType.Id}' 下没有成员 '{address.Member}'"
                            + "（如需引用某宿主类型独有的成员，请收窄 requires-host，R3h）。");
                    }

                    yield return (hostType, member);
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(address), address.Scope, "未定义的作用域（解析被拒绝）。");
        }
    }
}

/// <summary>
/// 编排运行时（新模型的装配与执行门面）：持有预制体目录、handler 资产表、包装注册面与**对局级单例引擎实体**。
/// <para>注册（运行期新增可寻址成员）与挂载（往已有成员上挂边）是两个动作、同一门面（R1h），不合并。</para>
/// </summary>
public sealed partial class OrchestrationRuntime
{
    /// <summary>创建运行时（立即实例化引擎实体并登记其包装声明）。</summary>
    /// <param name="catalog">预制体目录（须含引擎预制体）。</param>
    /// <param name="handlers">handler 资产表。</param>
    /// <param name="enginePrefabId">引擎预制体 id（缺省 <c>engine</c>）。</param>
    /// <exception cref="ArgumentNullException">catalog／handlers 为 null。</exception>
    /// <exception cref="KeyNotFoundException">引擎预制体未登记。</exception>
    /// <exception cref="PrefabLoadException">引擎预制体展开或包装登记失败。</exception>
    public OrchestrationRuntime(PrefabCatalog catalog, HandlerRegistry handlers, string enginePrefabId = "engine")
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));

        Engine = new OrchestrationEntity(this, Catalog.Resolve(enginePrefabId), "engine");
        _entities.Add(Engine);
        RegisterWraps(Engine);
    }

    private readonly List<OrchestrationEntity> _entities = new();
    private readonly Dictionary<OrchestrationEntity, ComponentAttachment> _attachments = new(ReferenceEqualityComparer.Instance);

    /// <summary>本运行时的全部存活实体（引擎＋卡牌＋组件；审查面导出用，销毁即移除）。</summary>
    public IReadOnlyList<OrchestrationEntity> Entities => _entities;

    /// <summary>预制体目录。</summary>
    public PrefabCatalog Catalog { get; }

    /// <summary>handler 资产表（全局懒注册、全局一份）。</summary>
    public HandlerRegistry Handlers { get; }

    /// <summary>包装注册面（成员包装器与判定器包装器共用，S2a）。</summary>
    public WrapRegistry Wraps { get; } = new();

    /// <summary>引擎实体（**对局级单例**，R6b：对局内不存在第二个引擎实体）。</summary>
    public OrchestrationEntity Engine { get; }

    /// <summary>
    /// 对局**信号发射通道**（框架注入；<see cref="Orc.Core.LogicEngine"/> 挂上本运行时时自动接线）。
    /// <para>
    /// 用途：组件卸载的完成通知（<c>effect.removed</c> 的组件形态）必须走**生产发射面**，
    /// 才能被订阅方（卡牌清理处理器／UI／测试观察面）看见——模型层不持有 <c>LogicEngine</c>，
    /// 故以单向委托注入（未装配＝null＝不发射，纯内核用例零副作用）。
    /// </para>
    /// </summary>
    internal Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task>? SignalEmitter { get; set; }

    /// <summary>创建实体实例（组件须给宿主；构造期复制私有字段初值）。</summary>
    /// <exception cref="ArgumentNullException">prefabId／name 为 null。</exception>
    /// <exception cref="KeyNotFoundException">预制体未登记。</exception>
    /// <exception cref="InvalidOperationException">预制体是引擎实体（对局内只允许一个引擎实体，R6b）。</exception>
    /// <exception cref="PrefabLoadException">包装声明登记失败。</exception>
    public OrchestrationEntity CreateEntity(string prefabId, string name, OrchestrationEntity? host = null)
    {
        ArgumentNullException.ThrowIfNull(prefabId);
        ArgumentNullException.ThrowIfNull(name);

        var type = Catalog.Resolve(prefabId);
        if (type.Kind == EntityKind.Engine)
        {
            // R6b 方案 A：引擎实体是对局级单例——第二个引擎实体会让 engine 根的寻址产生歧义。
            throw new InvalidOperationException(
                $"拒绝创建第二个引擎实体（预制体 '{prefabId}'）：引擎实体是对局级单例，"
                + $"本运行时已有引擎实体 '{Engine.Name}'（寻址根固定为 engine）。");
        }

        var entity = new OrchestrationEntity(this, type, name, host);
        _entities.Add(entity);
        RegisterWraps(entity);
        return entity;
    }

    /// <summary>实体销毁时从存活集合移除（由 <see cref="OrchestrationEntity.Destroy"/> 调用）。</summary>
    internal void Forget(OrchestrationEntity entity) => _entities.Remove(entity);

    /// <summary>
    /// 装载期绑定：规划该实体的全部挂载声明并建边（声明序）；任一校验失败＝**拒绝装载**（抛出，不留半装配状态）。
    /// </summary>
    /// <exception cref="ArgumentNullException">declaring 为 null。</exception>
    /// <exception cref="PrefabLoadException">规划报告含错误。</exception>
    public IReadOnlyList<MountHandle> Bind(OrchestrationEntity declaring)
    {
        ArgumentNullException.ThrowIfNull(declaring);

        var plan = MountPlanner.Plan(this, declaring);
        plan.Report.ThrowIfInvalid();

        var handles = new List<MountHandle>(plan.Mounts.Count);
        foreach (var mount in plan.Mounts)
        {
            var table = TableFor(declaring, mount.Address);
            var handle = table.Attach(
                declaring, mount.Member, mount.Source, EdgeOrderSource.Declared, mount.Order, mount.Declaration);
            declaring.Track(handle);
            handles.Add(handle);
        }

        return handles;
    }

    /// <summary>
    /// 运行期挂载（R1h）：往**已声明**的成员上挂一条边，位次追加到末尾（<c>order-source: appended</c>）。
    /// 边必然绑 owner（<paramref name="declaring"/>），随 owner 销毁撤销——不存在无主永久边。
    /// </summary>
    /// <param name="declaring">被挂载成员所属实体（owner 回填与撤销依据）。</param>
    /// <param name="memberName">被挂载的自身成员名。</param>
    /// <param name="target">目标地址（<c>作用域#成员名</c>）。</param>
    /// <param name="band">
    /// 外来边在**目标成员**段序方案里的段名（null＝与目标成员的自有边同段）。
    /// 必须出现在目标成员的 <see cref="MemberDefinition.Bands"/> 里，否则＝<see cref="ArgumentException"/>。
    /// </param>
    /// <exception cref="ArgumentNullException">declaring 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">地址非法。</exception>
    /// <exception cref="KeyNotFoundException">成员或目标不存在。</exception>
    /// <exception cref="PermissionDeniedException">可见性越界。</exception>
    /// <exception cref="ArgumentException">band 未在目标成员的段序方案中声明。</exception>
    public MountHandle MountAtRuntime(
        OrchestrationEntity declaring, string memberName, string target, string? band = null)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var address = MemberAddress.Parse(target);
        var member = declaring.FindMember(memberName)
            ?? throw new KeyNotFoundException($"实体 '{declaring.Name}' 上不存在成员 '{memberName}'。");

        var targetEntity = TargetEntityFor(declaring, address);
        var targetMember = targetEntity.FindMember(address.Member)
            ?? throw new KeyNotFoundException($"目标成员不存在：'{address}'（实体 '{targetEntity.Name}'）。");

        _ = targetMember.BandIndexOf(band); // fail-fast：段名必须已在目标成员的方案里声明

        if (address.Scope != AddressScope.Self)
        {
            if (!targetMember.Has(MemberVisibility.Hostable))
            {
                throw new PermissionDeniedException(
                    $"目标成员 '{address}' 未声明「可作宿主（Hostable）」，运行期挂载被拒绝。");
            }

            if (!member.Has(MemberVisibility.Mountable))
            {
                throw new PermissionDeniedException(
                    $"成员 '{memberName}' 未声明「可被挂载（Mountable）」，运行期挂载被拒绝。");
            }
        }

        var table = targetEntity.EdgesFor(address.Member);
        var handle = table.Attach(
            declaring, member, EdgeSource.Explicit, EdgeOrderSource.Appended, default, target, band);
        declaring.Track(handle);
        return handle;
    }

    /// <summary>撤销一条边（句柄即权限；幂等）。</summary>
    public bool Unmount(MountHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle.Revoke();
    }

    /// <summary>
    /// 登记实体的包装声明（S2a）：包装目标只能是**成员**（<c>作用域#成员名</c>），
    /// 包装器资产须在装载期可解析；失败＝拒绝装载（不留半登记）。
    /// </summary>
    /// <exception cref="ArgumentNullException">entity 为 null。</exception>
    /// <exception cref="KeyNotFoundException">包装器资产未注册，或目标成员不存在。</exception>
    /// <exception cref="PermissionDeniedException">目标成员未声明「可作宿主」。</exception>
    public void RegisterWraps(OrchestrationEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var order = MountPlanner.ComponentIndexOf(entity);

        // 先全量校验、再统一登记：任一包装声明失败＝拒绝装载，且不留半登记（与 <see cref="Bind"/> 同一口径）。
        var pending = new List<(WrapTarget Target, WrapperPhase Phase, HandlerAsset Handler)>(entity.Type.Wraps.Count);
        for (var i = 0; i < entity.Type.Wraps.Count; i++)
        {
            var declaration = entity.Type.Wraps[i];
            var handler = Handlers.Resolve(declaration.HandlerId);
            var target = declaration.Target;

            if (target.IsMember)
            {
                var targetEntity = TargetEntityFor(entity, target.Address);
                var targetMember = targetEntity.FindMember(target.Address.Member)
                    ?? throw new KeyNotFoundException(
                        $"包装目标不存在：'{target.Address}'（实体 '{targetEntity.Name}'）。");

                if (!targetMember.Has(MemberVisibility.Hostable))
                {
                    throw new PermissionDeniedException(
                        $"包装目标 '{target.Address}' 未声明「可作宿主（Hostable）」，包装被拒绝。");
                }

                // 包装键归一化为**目标实体自身**的地址：执行期用 AddressOf 比对，
                // 若把 `host#x` 原样登记，宿主执行成员时的键是 `self#x`，该包装会静默不生效。
                var normalized = targetEntity.AddressOf(target.Address.Member);
                if (declaration.Seam is { } seam)
                {
                    // 缝内包装：键必须带上缝名（否则会混进成员外围的 before／after／replace 列表）。
                    // 缝未在该成员上声明＝装载期拒绝（挂到不存在的缝＝永不执行，是静默失效的典型形态）。
                    if (!targetMember.DeclaresSeam(seam))
                    {
                        throw new KeyNotFoundException(
                            $"包装目标 '{target.Address}' 未声明具名缝 '{seam}'"
                            + (targetMember.Seams.Count == 0
                                ? "（该成员没有声明任何缝：seams 为空）。"
                                : $"（已声明：{string.Join("／", targetMember.Seams)}）。"));
                    }

                    target = WrapTarget.ForSeam(normalized, seam);
                }
                else
                {
                    target = WrapTarget.ForMember(normalized);
                }
            }

            pending.Add((target, declaration.Phase, handler));
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            var handle = Wraps.Register(item.Target, item.Phase, item.Handler, new DeclarationOrder(order, i, 0), entity);
            entity.Track(handle);
        }
    }

    /// <summary>
    /// 运行时注册新成员（R1e）：注册后立即可被寻址与挂载；失败＝拒绝注册、不留半个可寻址成员。
    /// </summary>
    /// <exception cref="ArgumentNullException">target／member 为 null。</exception>
    /// <exception cref="PrefabLoadException">重名。</exception>
    public MemberDefinition RegisterMember(OrchestrationEntity target, MemberDefinition member)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(member);
        return target.RegisterMember(member);
    }

    /// <summary>按（实体，成员名）执行成员（框架调用面；不做可见性检查）。</summary>
    /// <exception cref="KeyNotFoundException">成员不存在。</exception>
    public Task<MemberExecutionResult> RunMemberAsync(
        OrchestrationEntity entity, string memberName, IDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        var member = entity.FindMember(memberName)
            ?? throw new KeyNotFoundException($"实体 '{entity.Name}' 上不存在成员 '{memberName}'。");
        var ctx = new Context(data);
        return entity.ExecuteMemberAsync(member, entity.AddressOf(memberName), ctx, ct);
    }

    /// <summary>
    /// 按（实体，成员名）执行成员，**沿用调用方的 <see cref="Context"/>**（b4 收尾：外部 Context 贯通入口）。
    /// <para>
    /// 与 <see cref="RunMemberAsync(OrchestrationEntity, string, IDictionary{string, object?}?, CancellationToken)"/>
    /// 的唯一区别是**不复制数据面**：组件成员对 <c>view.&lt;可写字段&gt;</c> 的写回落在调用方同一份数据面上，
    /// 因而"组件就地改写本次数值并由动作端读回"这条能力成立（减伤／免疫／封顶全部依赖它）。
    /// </para>
    /// <para>
    /// **执行帧**：若该上下文还没有帧（调用方不是从触发器内部来的），本重载会用
    /// <paramref name="engine"/> 为本次成员执行补建一个帧（父帧＝当前会话）——与
    /// <c>SignalBridge.EmitAsync</c> 同一口径。缺了它 <see cref="Context.Engine"/> 恒 null，
    /// 依赖引擎的组件 handler 会静默早退（§22.1 根因 2）。<paramref name="engine"/> 为 null 时只执行、不建帧。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">entity／ctx 为 null。</exception>
    /// <exception cref="KeyNotFoundException">成员不存在。</exception>
    public async Task<MemberExecutionResult> RunMemberAsync(
        OrchestrationEntity entity,
        string memberName,
        Context ctx,
        LogicEngine? engine = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        var member = entity.FindMember(memberName)
            ?? throw new KeyNotFoundException($"实体 '{entity.Name}' 上不存在成员 '{memberName}'。");

        if (engine is null || ctx.HasFrame)
        {
            return await entity.ExecuteMemberAsync(member, entity.AddressOf(memberName), ctx, ct)
                .ConfigureAwait(false);
        }

        var parent = ExecutionFrame.Current;
        var label = $"{entity.Name}#{memberName}";
        var stream = new EventStream();
        stream.AttachTo(
            parent?.Stream ?? engine.RootStream,
            parent is null ? label : ExecutionFrame.BuildSource(parent.TriggerLabel, parent.CurrentEventName));

        var frame = new ExecutionFrame(parent, stream, label, engine)
        {
            // 与 `SignalBridge.EmitAsync` 同口径：**入口从数据面读触发者**（`TriggerCard` 键，只接受 Card），
            // 帧间继承只是兜底。只在帧间继承、不读数据面，会让"由卡发起的成员执行"看不到触发者。
            TriggerCard = ctx.TryGet(Orc.Core.TriggerPayloadKeys.TriggerCard, out var triggerCard)
                && triggerCard is Orc.Cards.Card card
                    ? card
                    : parent?.TriggerCard,
        };

        // 具名缝执行器（R1i 修订 2026-10-29：seam 恢复）：帧级默认——只在成员**声明了缝**时注入，
        // 未声明的成员一律保持 `ctx.SeamAsync` 的 InvalidOperationException（"只能在 handler 内调用"的口径）。
        // 进入成员时 `ExecuteMemberAsync` 会把执行器收窄到**当前**成员（挂载成员在自己的作用域内调用
        // `ctx.SeamAsync` 必须解析到自己的缝），执行完恢复本帧级默认。
        if (member.Seams.Count > 0)
        {
            frame.SeamExecutor = (seam, seamCt) => ExecuteSeamAsync(entity, member, seam, ctx, seamCt);
        }

        ExecutionFrame.Current = frame;

        // 已有帧的上下文**不覆盖**：帧是调用方会话的一部分（覆盖会让调用方的 Stop／Engine 读面错位）。
        // 本分支只在"无帧"时进入，因此此处 AttachFrame 是补建而非替换。
        ctx.AttachFrame(frame);
        frame.AttachContext(ctx);
        try
        {
            return await entity.ExecuteMemberAsync(member, entity.AddressOf(memberName), ctx, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ExecutionFrame.Current = parent;
        }
    }

    /// <summary>按地址执行成员（<c>engine#…</c>／<c>self#…</c>；<c>host#</c> 需要上下文实体，故请用重载）。</summary>
    /// <exception cref="KeyNotFoundException">作用域无法定位实体，或成员不存在。</exception>
    public Task<MemberExecutionResult> RunMemberAsync(
        MemberAddress address, IDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        if (address.Scope != AddressScope.Engine)
        {
            throw new NotSupportedException(
                $"地址 '{address}' 的作用域需要实体上下文（self＝组件自身、host＝宿主），请改用带实体参数的重载；"
                + "只有 engine 作用域可仅凭地址执行（引擎实体是对局级单例）。");
        }

        return RunMemberAsync(Engine, address.Member, data, ct);
    }

    /// <summary>
    /// 执行成员 handler 内部的一个**具名缝**（R1i 修订 2026-10-29：seam 恢复；<see cref="Context.SeamAsync"/> 的实现体）。
    /// <para>
    /// 口径：缝的包装器列表＝**注册面上 <c>(成员, 缝名)</c> 的包装项**（与成员外围包装同一注册面
    /// <see cref="WrapRegistry"/>、同一取列表入口 <see cref="WrapRegistry.Entries"/>、同一执行入口
    /// <c>WrapEntry.Handler.InvokeAsync</c>；唯一差别是取列表时目标键带缝名）。
    /// 执行序＝**声明序 LIFO**（<see cref="Context.SeamAsync"/> 的语义：后声明的包在外层）。
    /// </para>
    /// <para>
    /// 与"未声明"必须区分：**缝已声明但无人挂载＝空缝**，直接返回（不执行、不报错、无副作用）。
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">缝未在该成员上声明。</exception>
    internal async Task ExecuteSeamAsync(
        OrchestrationEntity entity, MemberDefinition member, string name, Context ctx, CancellationToken ct)
    {
        if (!member.DeclaresSeam(name))
        {
            throw new KeyNotFoundException(
                $"成员 '{entity.Name}#{member.Name}' 未声明具名缝 '{name}'"
                + (member.Seams.Count == 0
                    ? "（该成员没有声明任何缝：ctx.SeamAsync 只能在已声明 seams 的成员里调用）。"
                    : $"（已声明：{string.Join("／", member.Seams)}）。"));
        }

        var entries = Wraps.Entries(WrapTarget.ForSeam(entity.AddressOf(member.Name), name), WrapperPhase.Before);
        if (entries.Count == 0)
        {
            return; // 空缝：声明了但没人挂——合法状态，无副作用（与"未声明"抛错区分）
        }

        // 包装器视图：归属实体与被执行成员不同时（引擎成员上的组件包装），owner／host 回填为**声明该包装的实体**
        // （与成员外围 before 的 ViewFor 同一口径）；本方法自建视图、自带封口（成员视图由 ExecuteMemberAsync 负责）。
        var views = new List<ViewCarrier>(entries.Count);
        try
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                // 每个包装器前检查本级停止与链级中断（与 RunEdgesAsync 的单元边界同口径）。
                if (ctx.CurrentMemberScope?.Stopped == true || ctx.Interrupted)
                {
                    return;
                }

                var entry = entries[i];
                var view = entry.Declaring is null || ReferenceEquals(entry.Declaring, entity)
                    ? ViewCarrier.Bind(member.View, ctx, entity, entity.HostForView, member.Name)
                    : ViewCarrier.Bind(member.View, ctx, entry.Declaring, entry.Declaring.HostForView, member.Name);
                views.Add(view);
                await entry.Handler.InvokeAsync(view, ctx, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var view in views)
            {
                view.Seal();
            }
        }
    }

    /// <summary>解析地址对应的目标实体（引擎／自身／宿主）。</summary>
    /// <exception cref="InvalidOperationException">host 作用域但没有宿主引用。</exception>
    internal OrchestrationEntity TargetEntityFor(OrchestrationEntity context, MemberAddress address)
        => address.Scope switch
        {
            AddressScope.Engine => Engine,
            AddressScope.Self => context,
            AddressScope.Host => context.Host
                ?? throw new InvalidOperationException(
                    $"地址 '{address}' 的 host 作用域解析失败：实体 '{context.Name}' 没有宿主引用。"),
            _ => throw new ArgumentOutOfRangeException(nameof(address), address.Scope, "未定义的作用域。"),
        };

    /// <summary>地址对应的边表（建边入口；跨实体挂载时目标是宿主／引擎的表）。</summary>
    private MemberEdgeTable TableFor(OrchestrationEntity declaring, MemberAddress address)
        => TargetEntityFor(declaring, address).EdgesFor(address.Member);
}
