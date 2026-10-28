namespace Orc.Core;

/// <summary>
/// 执行上下文：一次执行（顶层或嵌套）的数据载体与状态面。
/// 含数据（Data）、最小读取 API 与停止/中断（Stop/Interrupt）语义；链标识不落地（链的可视表达由事件流树结构承担）。
/// </summary>
public sealed class Context
{
    /// <summary>数据载体（库内使用：视图绑定与代理直连此载体，不做拷贝）。</summary>
    internal Dictionary<string, object?> Data { get; }

    /// <summary>
    /// 创建一个上下文。传入的非 null 字典会被拷贝一次，此后由本上下文独占该载体、不再拷贝；传 null 视为空载体。
    /// </summary>
    public Context(IDictionary<string, object?>? data = null)
    {
        Data = data is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(data);
    }

    /// <summary>尝试读取数据载体中的值（供外部观察最终数据）。</summary>
    public bool TryGet(string key, out object? value) => Data.TryGetValue(key, out value);

    /// <summary>读取数据载体中的值；键不存在时抛出 <see cref="KeyNotFoundException"/>。</summary>
    public object? Get(string key) => Data.TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException($"上下文数据中不存在键 '{key}'。");

    /// <summary>
    /// 写入数据面（供**宿主层的守卫边**回填判定结果：如"本次被打出验证拒绝"）。
    /// 与视图写入同一份载体，因此同一执行链的后续步骤与调用方都能读到。
    /// </summary>
    /// <exception cref="ArgumentNullException">key 为 null。</exception>
    public void Set(string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        Data[key] = value;
    }

    private ExecutionFrame? _frame;
    private readonly Stack<Orc.Core.Model.MemberExecutionScope> _memberScopes = new();

    /// <summary>
    /// 本级停止标志（Stop）：停止当前触发器剩余事件；不影响父层与子执行。
    /// 与执行的事件边界检查共用同一状态源；未关联执行会话的上下文恒为 false。
    /// </summary>
    public bool Stopped
        => (_frame?.Stopped ?? false)
           || (_memberScopes.Count > 0 && _memberScopes.Peek().Stopped);

    /// <summary>
    /// 当前成员执行作用域（新模型；无＝null）。作用域的存在使 <see cref="Stop"/> 的停止范围精确等于"当前成员"——
    /// 不外溢到调用方的其它成员（S2a）。
    /// </summary>
    public Orc.Core.Model.MemberExecutionScope? CurrentMemberScope
        => _memberScopes.Count == 0 ? null : _memberScopes.Peek();

    /// <summary>
    /// 链中断标志（Interrupt）：动作链取消，沿嵌套链传播（起点层及全部祖先层在各自下一事件边界停止）。
    /// 与执行的事件边界检查共用同一状态源；未关联执行会话的上下文恒为 false。
    /// </summary>
    public bool Interrupted => _frame?.Interrupted ?? false;

    /// <summary>请求停止本级剩余事件（仅本级；已触发的子执行照常完成）。</summary>
    /// <remarks>
    /// 新模型（编排实体化）下，若当前处于某个成员执行作用域内，则同时标记该作用域停止：
    /// 停止范围＝**当前成员**（本成员剩余单元全部不执行），不外溢到调用方的其它成员（S2a）。
    /// 已执行的 <c>before</c> 包装器不回滚，其写入保留在数据面上。
    /// <para>
    /// 例外＝<see cref="Orc.Core.Model.MemberExecutionScope.PropagatesStop"/> 的作用域（信号端点成员＝订阅体本身，
    /// 自身没有"剩余单元"可停）：停止继续向调用方成员穿透（旧模型里订阅体与信号成员同处一个执行帧）。
    /// </para>
    /// </remarks>
    public void Stop()
    {
        _frame?.RequestStop();

        // 栈枚举＝栈顶（当前成员）先行；遇到不透传的作用域即止（停止不外溢）。
        foreach (var scope in _memberScopes)
        {
            scope.RequestStop();
            if (!scope.PropagatesStop)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 静默停止（框架内部；E-D2）：语义同 <see cref="Stop"/>，但不写事件流条目——
    /// 供 disposing 守卫在"载荷不指向本效果/宿主"时直接结束本轮，避免每张卡销毁都产生 N 条 stop 噪音。
    /// </summary>
    internal void StopSilent() => _frame?.RequestStopSilent();

    /// <summary>
    /// 请求中断动作链（链级）：所在层及全部祖先层在各自下一事件边界停止；已中断状态下新发起的执行空转。
    /// 中断行为写入事件流（Interrupt 取级高于 Stop）。
    /// </summary>
    public void Interrupt() => _frame?.RequestInterrupt();

    /// <summary>
    /// 本次执行所属引擎（S-C2 加性面）：执行期可读、执行外为 null。
    /// 用途：无闭包捕获能力的动态 handler（csx）经此取得引擎以发射更新/调用下游触发器；
    /// 不占数据载体键名、不改变 ctx 数据语义。
    /// </summary>
    public LogicEngine? Engine => _frame?.Engine;

    /// <summary>
    /// 当前 handler 的触发者卡牌（R9；执行期可读、执行外为 null）：
    /// 取值＝执行帧的解析结果（注册者为卡/效果时被改写为其 source 卡；否则继承父帧/入口初值）。
    /// 与视图属性 <c>TriggerCard</c>（数据面同名键）同值；与信号载荷 <c>Source</c>（事件引发者）分工不同。
    /// </summary>
    public Orc.Cards.Card? TriggerCard => _frame?.TriggerCard;

    /// <summary>
    /// 调用当前 handler 的具名动作缝（管道式）：按该缝的前置包装器列表 LIFO 依次执行。
    /// 仅在触发器 handler 执行期间可用；未关联执行帧、未处于 handler 内、或缝未声明均 fail-fast。
    /// </summary>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="InvalidOperationException">不在 handler 执行期间（无执行帧或帧未注入缝执行器）。</exception>
    /// <exception cref="KeyNotFoundException">缝未在目标 handler 上声明。</exception>
    public Task SeamAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var executor = _frame?.SeamExecutor
            ?? throw new InvalidOperationException("动作缝只能在触发器 handler 执行期间调用。");
        return executor(name, ct);
    }

    /// <summary>关联执行会话（框架内部流程调用；状态查询与检查点共用该帧的状态）。</summary>
    internal void AttachFrame(ExecutionFrame frame) => _frame = frame;

    /// <summary>
    /// 是否已关联执行会话（框架内部；成员执行面据此判断"要不要补建帧"）。
    /// <para>
    /// 用途：**沿用调用方上下文**执行成员时，若该上下文还没有帧，必须在进入成员前补建一个——
    /// 否则 <see cref="Engine"/>／<see cref="Stopped"/>／<see cref="Interrupted"/> 三个读面全部退化，
    /// 依赖 <c>ctx.Engine</c> 的组件 handler 会静默早退（§22.1 根因 2 的同型缺陷）。
    /// </para>
    /// </summary>
    internal bool HasFrame => _frame is not null;

    /// <summary>进入一个成员执行作用域（新模型；框架内部）。作用域可嵌套（挂载成员在宿主成员内执行）。</summary>
    internal void PushMemberScope(Orc.Core.Model.MemberExecutionScope scope) => _memberScopes.Push(scope);

    /// <summary>退出成员执行作用域（框架内部；与 <see cref="PushMemberScope"/> 成对）。</summary>
    internal void PopMemberScope(Orc.Core.Model.MemberExecutionScope scope)
    {
        if (_memberScopes.Count > 0 && ReferenceEquals(_memberScopes.Peek(), scope))
        {
            _memberScopes.Pop();
        }
    }
}
