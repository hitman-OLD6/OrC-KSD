namespace Orc.Core.Model;

/// <summary>
/// 组件实例的装配态（S8／R3i）：同一宿主上的一个组件实体，从「构造」到「装配」到「销毁」的完整生命周期。
/// <para>
/// **构造与装配分离**（R3i，收敛原"加载时构造 + 放置驱动装载"两套时点）：
/// <list type="number">
/// <item>宿主基础构造（卡牌／HQ 实例就位）；</item>
/// <item><b>构造</b>组件实体：填宿主引用（<c>owner.host</c> 可用）、初始化私有字段；</item>
/// <item>判定 <c>active-when</c>；</item>
/// <item><b>装配</b>（仅当命中时点）：<see cref="OrchestrationRuntime.Bind"/> 建立声明边 + 框架注销边；</item>
/// <item>卸载：逐句柄撤销边 → 销毁组件实体。</item>
/// </list>
/// **复装＝重建实例**：销毁后再次入场＝新建实例（私有字段复位），本类型不提供"复用实例重跑初始化"的路径。
/// </para>
/// <para>
/// 撤销**按句柄**（R1f）：本实例创建过的每条边（含框架注销边）都有独立句柄，卸载时逐条精确撤销；
/// 代码里不存在"按来源扫描某信号边表"的清理路径（旧按所有者扫表的做法与多组件并发卸载不兼容）。
/// </para>
/// </summary>
public sealed class ComponentAttachment
{
    private readonly List<MountHandle> _handles = new();
    private readonly List<Action> _onDispose = new();
    private bool _assembled;
    private bool _disposed;

    internal ComponentAttachment(
        ComponentAttachmentSet owner, OrchestrationEntity host, OrchestrationEntity component, int segment)
    {
        Owner = owner;
        Host = host;
        Component = component;
        Segment = segment;
        Component.DeclarationSegment = segment;
    }

    internal ComponentAttachmentSet Owner { get; }

    /// <summary>宿主实体（卡牌／HQ）。</summary>
    public OrchestrationEntity Host { get; }

    /// <summary>组件实体（效果＝组件实体的成员 handler 集）。</summary>
    public OrchestrationEntity Component { get; }

    /// <summary>组件预制体类型。</summary>
    public PrefabType Type => Component.Type;

    /// <summary>声明序段（1 起；宿主上的装配序）。</summary>
    public int Segment { get; }

    /// <summary>装配时点（<c>active-when</c>；缺省 placed）。</summary>
    public ActiveWhen ActiveWhen => Type.ActiveWhen;

    /// <summary>是否已装配（装配前不响应任何信号——边尚未建立）。</summary>
    public bool IsAssembled => _assembled;

    /// <summary>是否已销毁。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>本实例建立的边（含框架注销边；装配序）。</summary>
    public IReadOnlyList<MountHandle> Handles => _handles;

    /// <summary>
    /// 执行装配（幂等）：<c>requires-host</c> 校验 → 登记框架注销成员 → 按声明序建边 → 建框架注销边。
    /// 失败一律上抛（组件装配失败＝宿主失败＝卡包失败，R3i：不产生"少了某个组件的卡"）。
    /// </summary>
    /// <exception cref="PrefabLoadException">宿主类型不被 requires-host 覆盖，或装配失败。</exception>
    public ComponentAttachment Assemble()
    {
        if (_assembled || _disposed)
        {
            return this;
        }

        if (!Type.RequiresHost.HasFlag(OrchestrationRuntime.HostKindOf(Host.Type)))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"组件 '{Type.Id}' 的 requires-host={Type.RequiresHost} 不覆盖实际宿主类型 '{Host.Type.Id}'"
                + "（装配被拒绝；如需挂到该宿主，请收窄／扩大 requires-host，R3h）。");
        }

        var runtime = Component.Runtime;

        // 框架注销成员（R1f）：作者不声明、也不能声明（预制体层已拒绝 framework. 前缀）。
        var disposal = Component.FindMember(ComponentDisposalPolicy.DisposalMemberName)
            ?? Component.RegisterMember(new MemberDefinition(
                ComponentDisposalPolicy.DisposalMemberName,
                ViewSpec.Empty,
                MemberVisibility.Callable,
                ownEdges: new[] { new OwnedEdge((_, ctx, _) => DisposeFromFramework(ctx)) },
                layer: PrefabLayer.Kernel,
                declaredBy: Type.Id));

        // ① 作者声明的挂载边（声明序＝组件声明段 + 组件内挂载声明序）。
        foreach (var handle in runtime.Bind(Component))
        {
            _handles.Add(handle);
        }

        // ② 框架注销边（R1f）：位次＝该组件所有已声明挂载之后（FrameworkLast），来源 framework:disposal。
        var engine = runtime.Engine;
        var signal = Type.DisposalSignal;
        if (engine.FindMember(signal) is null)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Addressing,
                $"组件 '{Type.Id}' 的兜底信号 'engine#{signal}' 未在引擎实体上声明"
                + "（框架注销边无法建立——装配被拒绝）。");
        }

        var table = engine.EdgesFor(signal);
        var disposalHandle = table.Attach(
            Component,
            disposal,
            EdgeSource.FrameworkDisposal,
            EdgeOrderSource.Declared,
            DeclarationOrder.FrameworkLast(Segment),
            $"engine#{signal}");
        _handles.Add(disposalHandle);
        Component.Track(disposalHandle);

        _assembled = true;
        return this;
    }

    /// <summary>
    /// 登记一条**随实例卸载执行的清理动作**（A2 加性）：与组件实体同一生灭点——
    /// 供「资产声明与旧执行面之间的接缝」自撤（如把 <c>wraps</c> 落到旧身份触发器上的转发边）。
    /// 幂等：实例已卸载＝立即执行（不留悬空注册）。
    /// </summary>
    /// <exception cref="ArgumentNullException">callback 为 null。</exception>
    public void OnDispose(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed)
        {
            callback();
            return;
        }

        _onDispose.Add(callback);
    }

    /// <summary>卸载：逐句柄撤销本实例创建的边 → 执行清理动作 → 销毁组件实体 → 补发一次卸载完成通知（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _assembled = false;

        for (var i = _handles.Count - 1; i >= 0; i--)
        {
            _handles[i].Revoke();
            _handles.RemoveAt(i);
        }

        for (var i = _onDispose.Count - 1; i >= 0; i--)
        {
            _onDispose[i]();
        }

        _onDispose.Clear();

        Owner.Forget(this);
        Component.Destroy();

        EmitRemoved();
    }

    /// <summary>
    /// 卸载完成通知（b4 泛化）：<c>effect.removed</c> 的**组件形态**——载荷
    /// <c>{ Component: 预制体 id, ComponentInstance: 实体名, Host: 宿主承载对象 }</c>。
    /// <para>
    /// 为什么必须发：<c>effect.removed</c> 的语义是"某个可卸载物完成了卸载"，旧形态只覆盖 <c>Effect</c> 容器；
    /// 组件成为唯一执行面后，若不补发，卸载对任何订阅方（UI／观测面／测试）都是**静默的净减少**（§20.5 B2 原先的
    /// "恰 0 条"就是这条缺口的记录）。
    /// </para>
    /// <para>
    /// 为什么载荷**不含** <c>Card</c>／<c>Effect</c>：旧清理链（<c>CardLoadoutProcessor.OnCleanup</c>）按这两个键定位，
    /// 而它做的是"卡牌登记维护＋效果销毁兜底"——组件卸载两者都不是。互斥的键集让旧链天然幂等无操作，
    /// 不需要在新链路上加"是不是组件"的判断分支。
    /// </para>
    /// <para>
    /// 同步等待的理由与旧路径一致：<c>Effect.EmitRemoved</c> 同样在同步卸载点 <c>GetAwaiter().GetResult()</c>——
    /// 卸载的完成通知必须在卸载返回前对观察者可见（异步化会让"卸载后立刻断言"的用例变成时序竞态）。
    /// </para>
    /// </summary>
    private void EmitRemoved()
    {
        if (Component.Runtime.SignalEmitter is not { } emit)
        {
            return; // 未接线（纯内核／单测直接构造运行时）：不发射，零副作用
        }

        emit(
            Updates.EffectRemoved,
            new Dictionary<string, object?>
            {
                [Orc.Cards.PayloadKeys.Component] = Type.Id,
                [Orc.Cards.PayloadKeys.ComponentInstance] = Component.Name,
                [Orc.Cards.PayloadKeys.Host] = Host.Bound ?? Host,
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 框架注销边的执行体（在注销成员的自有边上运行）：**先按载荷判定"这条兜底是不是关于我的宿主"**，
    /// 命中才撤销本实例全部边并销毁组件实体。
    /// <para>
    /// 为什么必须有这一步（b4 定位到的真实失效）：兜底信号是 <c>engine#card.destroyed</c>——它是**引擎级共享成员**，
    /// 任何一张卡被销毁都会触发全体组件的注销边。无载荷判定时，一张无关卡牌死亡就会把**所有卡上的组件**一并卸载：
    /// 边还在审查图里（装配期确实建了），行为却在信号到达前消失。这是 §21.3 第 5、6 条（<c>card.discarded</c>／
    /// <c>card.died</c>「信号有发射、边也在、就是不执行」）的共同根因。
    /// </para>
    /// <para>
    /// 判定口径与旧 <c>Effect.MatchesDisposalPayload</c> 逐项同源：载荷出现 <c>Effect</c>／<c>Card</c>／<c>Unit</c>
    /// 时按引用相等比对宿主；三者都缺（无定位信息的裸信号）＝照旧注销（保持"兜底恒可达"）。
    /// 注意**不能**用 <c>ctx.Stop()</c> 表达跳过：那会停掉整个共享成员，把别的组件的注销一起吞掉。
    /// </para>
    /// </summary>
    private Task DisposeFromFramework(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (MatchesDisposalPayload(ctx))
        {
            Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>载荷是否指向本组件的宿主（口径同 <c>Effect.MatchesDisposalPayload</c>）。</summary>
    private bool MatchesDisposalPayload(Context ctx)
    {
        var host = Host.Bound ?? Host;

        if (ctx.TryGet(Orc.Cards.PayloadKeys.Effect, out var effect) && effect is not null)
        {
            return ReferenceEquals(effect, host);
        }

        if (ctx.TryGet(Orc.Cards.PayloadKeys.Card, out var card) && card is not null)
        {
            return ReferenceEquals(card, host);
        }

        if (ctx.TryGet("Unit", out var unit) && unit is not null)
        {
            return ReferenceEquals(unit, host);
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Host.Name}←{Type.Id}#{Segment}";
}

/// <summary>
/// 一个宿主上的组件实例集合（R3i）：同一 prefab 至多一份实例、构造／装配分离、复装＝重建实例。
/// </summary>
public sealed class ComponentAttachmentSet
{
    private readonly List<ComponentAttachment> _attachments = new();

    /// <summary>宿主实体。</summary>
    internal OrchestrationEntity Host { get; }

    internal ComponentAttachmentSet(OrchestrationEntity host) => Host = host;

    /// <summary>全部组件实例（装配序＝声明序）。</summary>
    public IReadOnlyList<ComponentAttachment> Attachments => _attachments;

    /// <summary>
    /// 构造一个组件实例（**不装配**）：填宿主引用、初始化私有字段；同一 prefab 重复＝拒绝（R3i）。
    /// </summary>
    /// <exception cref="PrefabLoadException">同一宿主上重复引用同一组件 prefab。</exception>
    internal ComponentAttachment Construct(OrchestrationRuntime runtime, string prefabId, string name)
    {
        if (_attachments.Any(a => string.Equals(a.Type.Id, prefabId, StringComparison.Ordinal)))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"宿主 '{Host.Name}' 重复引用组件 '{prefabId}'（同一 prefab 在同一宿主上至多一份实例，R3i；"
                + "需要两份请做两个组件预制体）。");
        }

        var component = runtime.CreateEntity(prefabId, name, Host);
        var attachment = new ComponentAttachment(this, Host, component, _attachments.Count + 1);
        _attachments.Add(attachment);
        return attachment;
    }

    /// <summary>装配所有命中时点的组件（<c>active-when: loaded</c> 在宿主加载时、<c>placed</c> 在入场时）。</summary>
    internal int Assemble(ActiveWhen phase)
    {
        var count = 0;
        foreach (var attachment in _attachments.ToArray())
        {
            if (attachment.IsDisposed || attachment.IsAssembled || attachment.ActiveWhen != phase)
            {
                continue;
            }

            attachment.Assemble();
            Host.Runtime.RaiseAssembled(attachment);
            count++;
        }

        return count;
    }

    /// <summary>卸载全部组件实例（逐实例销句柄），并清空集合（复装＝重建）。</summary>
    internal void DisposeAll()
    {
        foreach (var attachment in _attachments.ToArray())
        {
            attachment.Dispose();
        }

        _attachments.Clear();
    }

    /// <summary>移除一个已销毁的实例（复装时编号按剩余实例续排）。</summary>
    internal void Forget(ComponentAttachment attachment) => _attachments.Remove(attachment);
}

/// <summary>
/// 编排运行时的**组件面**（S8）：组件实体的构造、装配、卸载与一次性守卫的导出。
/// <para>与挂载面（<see cref="OrchestrationRuntime.Bind"/>）分开：挂载面回答"这条声明挂到哪"，组件面回答"这个组件何时活"。</para>
/// </summary>
public sealed partial class OrchestrationRuntime
{
    private readonly Dictionary<OrchestrationEntity, ComponentAttachmentSet> _componentSets
        = new(ReferenceEqualityComparer.Instance);

    /// <summary>全部组件实例（审查面导出用）。</summary>
    public IReadOnlyList<ComponentAttachment> Components
        => _componentSets.Values.SelectMany(static s => s.Attachments).ToArray();

    /// <summary>某宿主上的组件实例集合（无＝空集合，不创建）。</summary>
    public IReadOnlyList<ComponentAttachment> ComponentsOf(OrchestrationEntity host)
        => _componentSets.TryGetValue(host, out var set) ? set.Attachments : Array.Empty<ComponentAttachment>();

    /// <summary>
    /// 构造一个组件实例（**不装配**，R3i 第 12 条时序的第 2 步）：此后由 <see cref="AssembleComponents"/> 按
    /// <c>active-when</c> 决定何时装配。
    /// </summary>
    /// <exception cref="ArgumentNullException">host／prefabId／name 为 null。</exception>
    /// <exception cref="PrefabLoadException">预制体不是组件，或同一宿主重复引用。</exception>
    public ComponentAttachment ConstructComponent(OrchestrationEntity host, string prefabId, string name)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(prefabId);
        ArgumentNullException.ThrowIfNull(name);

        var type = Catalog.Resolve(prefabId);
        if (type.Kind != EntityKind.Component)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"预制体 '{prefabId}' 不是组件实体（kind={type.Kind}），不能作为组件装配到 '{host.Name}'。");
        }

        if (!_componentSets.TryGetValue(host, out var set))
        {
            set = new ComponentAttachmentSet(host);
            _componentSets[host] = set;
        }

        return set.Construct(this, prefabId, name);
    }

    /// <summary>
    /// **组件装配完成**通知（A2 加性）：每个组件实例装配成功时触发一次（<see cref="AddComponent"/> 与
    /// <see cref="AssembleComponents"/> 两条时点都经此）。
    /// <para>
    /// 用途：资产声明（<c>wraps</c>／<c>mounts</c>）与**旧执行面**之间的接缝——b4 收尾把五个伤害端搬进成员后，
    /// 组件包装已由 <see cref="OrchestrationRuntime.RegisterWraps"/> 直接承载（不再需要转发到旧触发器），
    /// 本事件因此只剩"装配完成"的观测语义（游戏层可用于诊断／统计），撤销仍经
    /// <see cref="ComponentAttachment.OnDispose"/> 与实例同生灭。
    /// </para>
    /// </summary>
    public event Action<ComponentAttachment>? Assembled;

    /// <summary>触发 <see cref="Assembled"/>（同程序集内由组件集合在装配后调用）。</summary>
    internal void RaiseAssembled(ComponentAttachment attachment) => Assembled?.Invoke(attachment);

    /// <summary>
    /// 构造并立即装配（两者合一，供"装配时点已确定"的调用方使用）。仍走同一组装链路（构造 → 装配）。
    /// </summary>
    public ComponentAttachment AddComponent(OrchestrationEntity host, string prefabId, string name)
    {
        var attachment = ConstructComponent(host, prefabId, name).Assemble();
        Assembled?.Invoke(attachment);
        return attachment;
    }

    /// <summary>
    /// 装配命中时点的组件：<c>active-when: loaded</c> 在宿主加载后调用、<c>placed</c> 在宿主入场时调用。
    /// 两者不会同时装配同一实例（各自只装配自己的那一批）。
    /// </summary>
    /// <returns>本次装配的实例数。</returns>
    public int AssembleComponents(OrchestrationEntity host, ActiveWhen phase)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _componentSets.TryGetValue(host, out var set) ? set.Assemble(phase) : 0;
    }

    /// <summary>卸载某宿主的全部组件（逐句柄撤销 + 销毁实例；宿主离场／回手即到此）。</summary>
    public void DisposeComponents(OrchestrationEntity host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_componentSets.TryGetValue(host, out var set))
        {
            set.DisposeAll();
            _componentSets.Remove(host);
        }
    }

    /// <summary>卸载单个组件实例（幂等）。</summary>
    public void DisposeComponent(ComponentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        attachment.Dispose();
    }

    /// <summary>实体类别 → 宿主种类（组件 <c>requires-host</c> 的校验口径）。</summary>
    internal static HostKind HostKindOf(PrefabType type) => type.Hosts;

    /// <summary>
    /// 导出本运行时（新模型部分）的审查图：实体—成员—挂载边（S10）。
    /// 若该运行时已挂到 <see cref="Orc.Core.LogicEngine.Model"/>，用 <c>LogicEngine.BuildAuditGraph()</c>
    /// 可得到含旧路径投影的同一张图。
    /// </summary>
    public AuditGraph BuildAuditGraph() => AuditGraphBuilder.BuildModelOnly(this);
}
