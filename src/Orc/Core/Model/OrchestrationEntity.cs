using System.Dynamic;

namespace Orc.Core.Model;

/// <summary>
/// 成员执行作用域（S2a）：<c>ctx.Stop()</c> 的作用范围＝**当前成员**。
/// <para>
/// 成员执行开始时压栈、结束时弹栈（可嵌套：挂载成员在宿主成员内执行）。
/// 停止只标记栈顶作用域——已执行的 <c>before</c> 包装器不回滚，其写入保留在数据面上；
/// 停止**不外溢**到调用方的其它成员。
/// </para>
/// </summary>
public sealed class MemberExecutionScope
{
    internal MemberExecutionScope(OrchestrationEntity owner, string memberName)
    {
        Owner = owner;
        MemberName = memberName;
    }

    /// <summary>成员所属实体。</summary>
    public OrchestrationEntity Owner { get; }

    /// <summary>成员名。</summary>
    public string MemberName { get; }

    /// <summary>
    /// 停止是否**穿透到调用方成员**（信号端点成员专用）。
    /// <para>
    /// 端点成员（<c>legacy.signal.endpoint</c> 上运行期注册的订阅成员）**不是独立成员**：它的全部内容就是
    /// 那条订阅体/检查边，没有"本成员剩余单元"可停。因此它里面的 <c>ctx.Stop()</c> 语义必须是
    /// 「停调用我的那个成员」（与旧模型"订阅体与信号/流程成员同处一个执行帧"逐项等价）——
    /// 否则反制的 <c>Stop</c> 只能停自己：被反制的施放照常宣告与结算（现实后果＝反制形同虚设）。
    /// </para>
    /// </summary>
    internal bool PropagatesStop { get; init; }

    /// <summary>是否已请求停止（仅本成员生效）。</summary>
    public bool Stopped { get; private set; }

    internal void RequestStop() => Stopped = true;
}

/// <summary>成员执行结果（诊断用）。</summary>
/// <param name="Executed">是否进入执行（false＝被合法性检验拒绝）。</param>
/// <param name="Stopped">执行期间是否被 <c>ctx.Stop()</c> 提前终止。</param>
/// <param name="ValidationRejected">是否被合法性检验拒绝。</param>
/// <param name="Reason">拒绝原因（合法＝null）。</param>
public readonly record struct MemberExecutionResult(bool Executed, bool Stopped, bool ValidationRejected, string? Reason)
{
    /// <summary>正常执行完成。</summary>
    public static MemberExecutionResult Ok(bool stopped) => new(true, stopped, false, null);

    /// <summary>被合法性检验拒绝（仅本次取消，不传播）。</summary>
    public static MemberExecutionResult Rejected(string? reason) => new(false, false, true, reason);
}

/// <summary>
/// 编排实体实例（四类实体共用一种运行期形态，<see cref="EntityKind"/> 区分）。
/// <para>
/// 成员表来自 <see cref="PrefabType"/>（装载期静态可枚举）；运行期注册的成员进入覆盖层（R1e：注册后全局可用）。
/// 私有状态（R3e）落在 <c>_state</c>，只经 <c>owner.&lt;Name&gt;</c>（动态）或 <see cref="GetPrivateField"/> 访问，**不进数据面**。
/// </para>
/// <para>
/// **执行序（R1i／band）**：成员的边集合按 **band（段名）** 排序——
/// 段下标来自成员的 <see cref="MemberDefinition.Bands"/> 方案（自有边＝<see cref="MemberDefinition.Band"/>，
/// 外来边＝<see cref="MountEdge.Band"/>，缺省与自有边同段），同段内**自有边在前**（声明序 → 运行期接入序）、
/// 外来边按声明序 → 追加序；框架注销边作为声明序边的一部分，位次最后。
/// "自有边最前／最后"是结果而非概念：落在首段即最前，落在末段即最后。
/// 包装（S2a）：<c>before</c>（FIFO）→ 边集合（或被 <c>replace</c> 接管）→ <c>after</c>（FIFO）。
/// </para>
/// </summary>
public sealed class OrchestrationEntity : Entity
{
    private readonly Dictionary<string, object?> _state = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemberEdgeTable> _edgeTables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemberDefinition> _runtimeMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<OwnedEdge>> _ownedEdgeOverlay = new(StringComparer.Ordinal);
    private readonly List<MountHandle> _mountHandles = new();
    private readonly List<WrapHandle> _wrapHandles = new();
    private bool _oneShotConsumed;

    /// <summary>创建实体实例（仅由 <see cref="OrchestrationRuntime"/> 创建：私有字段初值在此复制）。</summary>
    /// <exception cref="ArgumentNullException">runtime／type／name 为 null。</exception>
    internal OrchestrationEntity(
        OrchestrationRuntime runtime, PrefabType type, string name, OrchestrationEntity? host = null)
        : base(name)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Host = host;

        foreach (var field in type.PrivateFields)
        {
            _state[field.Name] = field.InitialValue;
        }
    }

    /// <summary>所属运行时（提供引擎实体、包装注册面、handler 资产）。</summary>
    public OrchestrationRuntime Runtime { get; }

    /// <summary>预制体类型（＝类）。</summary>
    public PrefabType Type { get; }

    /// <summary>宿主实体（组件的宿主＝卡牌／HQ；其余＝null；R3d 的 <c>owner.host</c> 链）。</summary>
    public OrchestrationEntity? Host { get; }

    /// <summary>
    /// 视图 <c>Host</c> 面的投影值（组件＝**宿主承载对象**，如卡牌实例；无承载对象＝宿主实体本身）。
    /// <para>
    /// 为什么不能直接把 <see cref="Host"/> 塞进视图：迁移后的 csx 写的仍是 <c>view.Host is Orc.Cards.Card</c>
    /// （requirements「转换语义不变」），而承载实体与卡牌对象是两个类型——不投影会让**每一条**按宿主过滤的
    /// handler 静默 no-op（A3 端到端用例发现的第一处真实失效）。
    /// </para>
    /// </summary>
    internal object? HostForView => Host is null ? null : Host.Bound ?? Host;

    /// <summary>
    /// 本实体承载的对象（**卡牌↔实体桥接缝**：卡牌实例、HQ 实例等；组件实体＝null，经 <see cref="Host"/> 回溯）。
    /// <para>
    /// 用途：判定器包装器的"哪张卡注册了我"回填（旧 <c>judicatorWrappers</c> 的载荷末位宿主卡约定）与审查面识别。
    /// 未绑定＝null（模型内部实体／测试探针实体）。
    /// </para>
    /// </summary>
    public object? Bound { get; set; }

    /// <summary>
    /// 声明序段（R1g）：组件在宿主上的装配序（1 起；引擎／卡牌自身＝0）。
    /// <para>
    /// 由 <see cref="OrchestrationRuntime"/> 在装配组件时写入：卡牌预制体不逐卡声明 <c>components</c>
    /// （组件来自卡数据），因此"组件声明序"在运行期＝该宿主上的装配序；这仍是**静态可复推**的——
    /// 同一份卡数据重装后序号不变（复装的边回填原位）。
    /// </para>
    /// </summary>
    internal int DeclarationSegment { get; set; }

    /// <summary>实体类别。</summary>
    public EntityKind EntityKind => Type.Kind;

    /// <summary>
    /// 一次性状态（R3k；**实体内置状态**，不占用作者私有字段）：执行**前**检查、执行**后**置位——
    /// 同一执行链内连续两次命中，第二次不执行；复装＝新建实体＝复位。
    /// </summary>
    public bool OneShotConsumed => _oneShotConsumed;

    /// <summary>是否为一次性组件（R3k）。</summary>
    public bool IsOneShot => Type.OneShot;

    /// <summary>成员清单（预制体成员＋运行期注册成员，声明序）。</summary>
    public IReadOnlyList<MemberDefinition> Members => Type.Members.Concat(_runtimeMembers.Values).ToArray();

    /// <summary>查找成员（运行期覆盖层优先）。</summary>
    public MemberDefinition? FindMember(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _runtimeMembers.TryGetValue(name, out var runtimeMember) ? runtimeMember : Type.FindMember(name);
    }

    /// <summary>
    /// 运行时注册成员（R1e）：注册后立即可被寻址与挂载；**失败不留半个可寻址成员**（先校验、后一次写入）。
    /// </summary>
    /// <exception cref="ArgumentNullException">member 为 null。</exception>
    /// <exception cref="PrefabLoadException">同名成员已存在。</exception>
    public MemberDefinition RegisterMember(MemberDefinition member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (FindMember(member.Name) is not null)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"运行时注册成员 '{member.Name}' 失败：实体 '{Name}' 上已存在同名成员（注册被拒绝，未产生任何注册残留）。");
        }

        _runtimeMembers[member.Name] = member;
        return member;
    }

    /// <summary>取某成员的边表（不存在则创建；边表按成员名归属本实体）。</summary>
    /// <exception cref="ArgumentNullException">memberName 为 null。</exception>
    public MemberEdgeTable EdgesFor(string memberName)
    {
        ArgumentNullException.ThrowIfNull(memberName);

        if (!_edgeTables.TryGetValue(memberName, out var table))
        {
            table = new MemberEdgeTable(MemberAddress.Create(AddressScopeForThis, memberName));
            _edgeTables[memberName] = table;
        }

        return table;
    }

    /// <summary>该成员上的边表（不存在＝null；只读查询用）。</summary>
    public MemberEdgeTable? FindEdges(string memberName)
        => _edgeTables.TryGetValue(memberName, out var table) ? table : null;

    /// <summary>
    /// 本实体是否已承载**不可无损迁移的运行期状态**（运行期注册的成员／已建立的边／已登记的句柄）。
    /// <para>
    /// 用途：<see cref="Orc.Core.LogicEngine.Model"/> 的赋值守卫——运行时身份被替换会让这些边挂在旧实例上、
    /// 之后静默不执行（R6b：一个 <see cref="Orc.Core.LogicEngine"/> 只有一个引擎实体）。
    /// </para>
    /// </summary>
    internal bool HasRuntimeState
        => _runtimeMembers.Count > 0
            || _mountHandles.Count > 0
            || _wrapHandles.Count > 0
            || _edgeTables.Values.Any(static table => table.Count > 0);

    /// <summary>读取私有字段（owner 通道；未声明＝<see cref="PermissionDeniedException"/>）。</summary>
    public object? GetPrivateField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Type.FindPrivateField(name) is null)
        {
            throw new PermissionDeniedException(
                $"实体 '{Name}' 未声明私有字段 '{name}'（owner 通道只允许已声明的私有字段与实体公开属性）。");
        }

        return _state[name];
    }

    /// <summary>写私有字段（owner 通道；类型与声明不符＝拒绝）。</summary>
    public void SetPrivateField(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        var field = Type.FindPrivateField(name)
            ?? throw new PermissionDeniedException(
                $"实体 '{Name}' 未声明私有字段 '{name}'（owner 通道只允许已声明的私有字段与实体公开属性）。");

        if (value is not null && !ViewFieldTypes.Accepts(field.Type, value))
        {
            throw new InvalidCastException(
                $"私有字段 '{name}' 声明为 {ViewFieldTypes.Format(field.Type)}，不接受 {value.GetType().Name}。");
        }

        _state[name] = value ?? ViewFieldTypes.DefaultValue(field.Type);
    }

    /// <summary>动态读取私有字段（csx：<c>owner.&lt;Name&gt;</c>）。CLR 公开属性优先于本回退。</summary>
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        ArgumentNullException.ThrowIfNull(binder);

        if (Type.FindPrivateField(binder.Name) is null)
        {
            throw new PermissionDeniedException(
                $"实体 '{Name}' 上不存在可动态访问的成员 '{binder.Name}'（owner 通道只允许已声明的私有字段与实体公开属性）。");
        }

        result = _state[binder.Name];
        return true;
    }

    /// <summary>动态写私有字段（csx：<c>owner.&lt;Name&gt; = …</c>）：实体状态写入，**不产生数据面写回**。</summary>
    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        ArgumentNullException.ThrowIfNull(binder);
        SetPrivateField(binder.Name, value);
        return true;
    }

    /// <summary>可动态访问的成员名（私有字段＋公开属性；诊断用）。</summary>
    public override IEnumerable<string> GetDynamicMemberNames()
        => Type.PrivateFields.Select(static f => f.Name)
            .Concat(PrefabType.BuiltInOwnerMembers);

    /// <summary>本实体作为"自身作用域"时的地址作用域（引擎实体＝<c>engine</c>，其余＝<c>self</c>）。</summary>
    internal AddressScope AddressScopeForThis
        => EntityKind == EntityKind.Engine ? AddressScope.Engine : AddressScope.Self;

    /// <summary>本实体某成员的地址（寻址/包装键的唯一来源）。</summary>
    public MemberAddress AddressOf(string memberName)
        => MemberAddress.Create(AddressScopeForThis, memberName);

    /// <summary>
    /// 调用一个成员（跨实体调用入口；要求 <see cref="MemberVisibility.Callable"/>）。
    /// </summary>
    /// <exception cref="KeyNotFoundException">成员不存在。</exception>
    /// <exception cref="PermissionDeniedException">成员未声明"可被调用"。</exception>
    public async Task<MemberExecutionResult> InvokeAsync(
        string memberName, IDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        var member = FindMember(memberName)
            ?? throw new KeyNotFoundException($"实体 '{Name}' 上不存在成员 '{memberName}'。");

        if (!member.Has(MemberVisibility.Callable))
        {
            throw new PermissionDeniedException(
                $"成员 '{memberName}' 未声明为可被调用（Callable）；跨实体调用被拒绝。");
        }

        var ctx = new Context(data);
        return await ExecuteMemberAsync(member, AddressOf(memberName), ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用一个成员（**沿用调用方上下文**的入口；A2 加性）：与上一个重载的唯一区别是不复制数据面——
    /// 组件成员的写回落在调用方同一份 <see cref="Context"/> 上（动作端接缝用：csx 对 <c>view.Amount</c> 的改写
    /// 必须回到动作端视图）。
    /// </summary>
    /// <param name="memberName">成员名（须声明为可被调用）。</param>
    /// <param name="ctx">调用方上下文（数据面共享；成员作用域压栈/弹栈仍由本实体负责）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="KeyNotFoundException">成员不存在。</exception>
    /// <exception cref="PermissionDeniedException">成员未声明"可被调用"。</exception>
    public async Task<MemberExecutionResult> InvokeAsync(
        string memberName, Context ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var member = FindMember(memberName)
            ?? throw new KeyNotFoundException($"实体 '{Name}' 上不存在成员 '{memberName}'。");

        if (!member.Has(MemberVisibility.Callable))
        {
            throw new PermissionDeniedException(
                $"成员 '{memberName}' 未声明为可被调用（Callable）；跨实体调用被拒绝。");
        }

        return await ExecuteMemberAsync(member, AddressOf(memberName), ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行成员（框架调用面；不做可见性检查——自带合法性检验与边集合）。
    /// </summary>
    internal async Task<MemberExecutionResult> ExecuteMemberAsync(
        MemberDefinition member, MemberAddress address, Context ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(ctx);

        var scope = new MemberExecutionScope(this, member.Name)
        {
            // 信号端点成员＝订阅体本身（无"本成员剩余单元"）：Stop 穿透到调用方成员。
            PropagatesStop = string.Equals(Type.Id, Orc.Core.SignalEndpointPrefab.PrefabId, StringComparison.Ordinal),
        };
        ctx.PushMemberScope(scope);
        var view = ViewCarrier.Bind(member.View, ctx, this, HostForView, member.Name);

        // 具名缝执行器收窄到**当前**成员（R1i 修订 2026-10-29：seam 恢复）：帧级默认由建帧点给出，
        // 而挂载成员／嵌套成员与宿主共用同一个执行帧——不收窄就会把组件的缝调用解析到**外层成员**的缝上
        // （要么静默跑错包装，要么误报"未声明"）。未声明缝的成员＝执行器置 null（保持
        // `ctx.SeamAsync` 的 InvalidOperationException，与"只能在已声明 seams 的 handler 内调用"一致）。
        var frame = ctx.Frame;
        var outerSeamExecutor = frame?.SeamExecutor;
        if (frame is not null)
        {
            frame.SeamExecutor = member.Seams.Count > 0
                ? (seam, seamCt) => Runtime.ExecuteSeamAsync(this, member, seam, ctx, seamCt)
                : null;
        }

        // 包装器视图：归属实体与被执行成员不同时（引擎成员上的组件包装），owner／host 回填为**声明该包装的实体**
        // （R4 的 owner 回填；等价于旧 injects 把宿主卡放进载荷的 `Host`）。数据面仍是同一份（写回对后续步骤可见）。
        var wrapperViews = new List<ViewCarrier>(2);
        ViewCarrier ViewFor(WrapEntry entry)
        {
            if (entry.Declaring is null || ReferenceEquals(entry.Declaring, this))
            {
                return view;
            }

            var bound = ViewCarrier.Bind(member.View, ctx, entry.Declaring, entry.Declaring.HostForView, member.Name);
            wrapperViews.Add(bound);
            return bound;
        }

        try
        {
            // R3k 重入安全：**执行前**检查一次性状态——已消费＝本次不执行（同一执行链内连续命中亦然）。
            // 框架保留成员（注销成员）不参与一次性判定：它承担"撤销"，被一次性挡住会让组件永远无法注销。
            var oneShotGoverned = Type.OneShot && !ComponentDisposalPolicy.IsReservedMember(member.Name);
            if (oneShotGoverned && _oneShotConsumed)
            {
                return new MemberExecutionResult(false, false, true, "one-shot：该组件实例的一次性额度已消费（R3k）。");
            }

            if (member.Validation?.Validate(view, ctx) is { } reason)
            {
                return MemberExecutionResult.Rejected(reason);
            }

            var target = WrapTarget.ForMember(address);
            var wraps = Runtime.Wraps;

            // ① before：改写输入，FIFO（声明序正序；R1i 修订——与旧 Initiate/Announce 的 priority 升序等价）。
            foreach (var wrapper in wraps.BeforeExecutionOrder(target))
            {
                if (scope.Stopped || ctx.Interrupted)
                {
                    break;
                }

                await wrapper.Handler.InvokeAsync(ViewFor(wrapper), ctx, ct).ConfigureAwait(false);
            }

            // ② 边集合：replace 最后声明者接管整个边集合；否则按 band（段序）排列自有边与外来边。
            if (!scope.Stopped && !ctx.Interrupted)
            {
                var replace = wraps.LastReplace(target);
                if (replace is not null)
                {
                    await replace.Handler.InvokeAsync(ViewFor(replace), ctx, ct).ConfigureAwait(false);
                }
                else
                {
                    await RunEdgesAsync(member, view, ctx, scope, ct).ConfigureAwait(false);
                }
            }

            // ③ after：尾部副作用，FIFO；被 Stop 后不再执行（免疫语义，R1i）。
            if (!scope.Stopped && !ctx.Interrupted)
            {
                foreach (var wrapper in wraps.Entries(target, WrapperPhase.After))
                {
                    if (scope.Stopped)
                    {
                        break;
                    }

                    await wrapper.Handler.InvokeAsync(ViewFor(wrapper), ctx, ct).ConfigureAwait(false);
                }
            }

            // R3k：执行**后**立即置位（本成员执行完成＝一次性额度用完；框架据此注销整个组件实例）。
            if (oneShotGoverned)
            {
                _oneShotConsumed = true;
            }

            return MemberExecutionResult.Ok(scope.Stopped);
        }
        finally
        {
            foreach (var wrapperView in wrapperViews)
            {
                wrapperView.Seal();
            }

            view.Seal();
            if (frame is not null)
            {
                frame.SeamExecutor = outerSeamExecutor;
            }

            ctx.PopMemberScope(scope);
        }
    }

    /// <summary>
    /// 执行边集合：**统一按 band 排序**（R1i 修订 2026-10-29）。
    /// <para>
    /// 每条执行单元的段名＝自有边取 <see cref="MemberDefinition.Band"/>、外来边取
    /// <see cref="MountEdge.Band"/>（null＝与成员自有边同段）；段下标＝该段名在
    /// <see cref="MemberDefinition.Bands"/> 里的下标（null 方案／null 段名＝0）。
    /// 排序键＝<c>(段下标, 同段内序)</c>，同段内序＝自有边（声明序 → 运行期接入序）在前，
    /// 外来边（表内快照序：声明序 → 追加序）在后。
    /// </para>
    /// <para>
    /// 于是"自有边最前／最后"不再是独立概念：自有边落在首段＝最前，落在末段（且外来边在更早的段）＝最后。
    /// 每个执行单元前检查停止（本级 <c>Stop</c>）与链级中断（<c>Interrupt</c>）。
    /// </para>
    /// </summary>
    private async Task RunEdgesAsync(
        MemberDefinition member, ViewCarrier view, Context ctx, MemberExecutionScope scope, CancellationToken ct)
    {
        var units = CollectUnits(member);

        foreach (var unit in units)
        {
            if (scope.Stopped || ctx.Interrupted)
            {
                return; // 本级停止（Stop）或链级中断（Interrupt）：不再推进后续单元
            }

            if (unit.Owned is { } owned)
            {
                await owned.InvokeAsync(view, ctx, ct).ConfigureAwait(false);
                continue;
            }

            var edge = unit.Edge!;

            // 被挂载成员保留自己的视图与合法性检验；数据面仍是**宿主**这一份（R1c：写回对后续步骤可见）。
            await edge.Declaring
                .ExecuteMemberAsync(edge.Member, edge.Declaring.AddressOf(edge.Member.Name), ctx, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把成员的边集合摊成按 band 排好序的执行单元（排序在**执行前一次算定**，不边跑边排）。
    /// </summary>
    private List<(int Segment, int Kind, int Index, OwnedEdge? Owned, MountEdge? Edge)> CollectUnits(
        MemberDefinition member)
    {
        var units = new List<(int Segment, int Kind, int Index, OwnedEdge? Owned, MountEdge? Edge)>();

        // ① 自有边：段名＝成员的 Band；同段内序＝声明序（OwnEdges）→ 运行期接入序（覆盖层）。
        var segment = member.BandIndexOf(member.Band);
        var index = 0;
        foreach (var owned in member.OwnEdges)
        {
            units.Add((segment, 0, index++, owned, null));
        }

        if (_ownedEdgeOverlay.TryGetValue(member.Name, out var overlay))
        {
            foreach (var owned in overlay)
            {
                units.Add((segment, 0, index++, owned, null));
            }
        }

        // ② 外来边：段名＝边自己声明的 Band，缺省则与成员自有边同段；同段内序＝边表快照序。
        if (_edgeTables.TryGetValue(member.Name, out var table))
        {
            var snapshot = table.Snapshot();
            for (var i = 0; i < snapshot.Count; i++)
            {
                var edge = snapshot[i];
                units.Add((member.BandIndexOf(edge.Band ?? member.Band), 1, i, null, edge));
            }
        }

        // 稳定排序（OrderBy 系列是稳定排序：(Kind, Index) 已足以在同段内保持既有次序）。
        return units
            .OrderBy(static u => u.Segment)
            .ThenBy(static u => u.Kind)
            .ThenBy(static u => u.Index)
            .ToList();
    }

    /// <summary>
    /// 运行期向**已声明的成员**接入一条自有边（b4 收尾的加性面）。
    /// <para>
    /// 为什么需要这条通道：动作实现（伤害／攻击）的"默认流程"是 **C# 闭包**（需要本对局的
    /// <c>IDamageActionServices</c>／攻击结算依赖），无法写进**跨对局共享**的静态
    /// <see cref="PrefabType"/> 声明里；而它又必须落在**成员自己的段**上（例：处理端的 <c>Apply</c> 段——
    /// 外来改写边与包装先跑、默认流程最后跑），否则"包装整个成员"没有着力点。
    /// </para>
    /// <para>
    /// 与 <see cref="RegisterMember"/> 的分工：后者新增**可寻址成员**（R1e），本方法只给**已存在**的成员
    /// 追加一条可执行单元——不改视图、不改可见性、不改位次，因此不会产生"声明漂移"。
    /// 覆盖层是**实体级**（每局一份）：静态声明不被任何一局写入。
    /// </para>
    /// </summary>
    /// <param name="memberName">成员名（须已在本实体上声明）。</param>
    /// <param name="edge">自有边（执行体）。</param>
    /// <returns>传入的边（便于调用点留档）。</returns>
    /// <exception cref="ArgumentNullException">memberName／edge 为 null。</exception>
    /// <exception cref="KeyNotFoundException">成员未声明。</exception>
    public OwnedEdge AttachOwnedEdge(string memberName, OwnedEdge edge)
    {
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(edge);

        _ = FindMember(memberName)
            ?? throw new KeyNotFoundException(
                $"实体 '{Name}' 上不存在成员 '{memberName}'（自有边无法接入：成员必须先声明）。");

        if (!_ownedEdgeOverlay.TryGetValue(memberName, out var edges))
        {
            edges = new List<OwnedEdge>();
            _ownedEdgeOverlay[memberName] = edges;
        }

        edges.Add(edge);
        return edge;
    }

    /// <summary>该成员上运行期接入的自有边（诊断／审查面读点；未接入＝空）。</summary>
    public IReadOnlyList<OwnedEdge> AttachedOwnedEdges(string memberName)
        => _ownedEdgeOverlay.TryGetValue(memberName, out var edges) ? edges : Array.Empty<OwnedEdge>();

    /// <summary>登记一条挂载句柄（owner 销毁时逐句柄撤销，R1f：不按来源扫表）。</summary>
    internal void Track(MountHandle handle) => _mountHandles.Add(handle);

    /// <summary>登记一条包装句柄。</summary>
    internal void Track(WrapHandle handle) => _wrapHandles.Add(handle);

    /// <summary>撤销本实体创建的全部边与包装（幂等；句柄级精确撤销）。</summary>
    public void RevokeAll()
    {
        foreach (var handle in _mountHandles)
        {
            handle.Revoke();
        }

        _mountHandles.Clear();

        foreach (var handle in _wrapHandles)
        {
            Runtime.Wraps.Revoke(handle);
        }

        _wrapHandles.Clear();
    }

    /// <summary>销毁实体：先卸载其组件（宿主离场＝组件销毁，R3i）→ 撤销自己创建的全部边（R1h）→ 置生命周期失效。</summary>
    public override void Destroy()
    {
        Runtime.DisposeComponents(this);
        RevokeAll();
        Runtime.Forget(this);
        base.Destroy();
    }
}
