using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Orc.Core.Model;

/// <summary>
/// 成员 handler 的委托形态（R4：全局函数 + owner 参数自动回填）。
/// <para>
/// 第一个参数是**承载视图**（<see cref="ViewCarrier"/>，此处按 <see cref="object"/> 擦除——csx 的
/// <c>Func&lt;dynamic, Context, CancellationToken, Task&gt;</c> 在运行期即本形态）；
/// owner 不占参数位，由框架填入视图的 <see cref="ViewCarrier.Owner"/>（等价 C# 的 <c>this</c>／Python 的 <c>self</c>）。
/// </para>
/// <para>handler **不持有 per-instance 状态**：状态只能落在 owner 实体的私有字段或执行数据面上（R4／R3e）。</para>
/// </summary>
/// <param name="view">承载视图（按视图声明投影出的动态读写视图）。</param>
/// <param name="ctx">执行上下文（数据面＋Stop／Interrupt）。</param>
/// <param name="ct">取消令牌。</param>
public delegate Task KernelHandler(object view, Context ctx, CancellationToken ct);

/// <summary>
/// handler 资产（R4）：**全局一份**的可执行节点，被任意多个预制体／成员引用。
/// 不是每实例对象——"调用成员"＝调用本资产，owner 经视图回填。
/// </summary>
public sealed class HandlerAsset
{
    internal HandlerAsset(
        string id,
        KernelHandler handler,
        string? scriptSource = null,
        Func<object[]?, object[]?, object[]?>? judge = null)
    {
        Id = id;
        Handler = handler;
        ScriptSource = scriptSource;
        Judge = judge;
    }

    /// <summary>资产 id（预制体按 id 引用；全局唯一）。</summary>
    public string Id { get; }

    /// <summary>可执行体（owner 经视图回填）。</summary>
    public KernelHandler Handler { get; }

    /// <summary>csx 源码（脚本 handler 才有；审查／诊断用）。</summary>
    public string? ScriptSource { get; }

    /// <summary>
    /// 判定器包装器的同步可执行体（旧 <c>judicatorWrappers</c> 的签名；null＝不是判定器包装器）。
    /// <para>
    /// 与 <see cref="Handler"/> 并列而非合一：二者签名与调用时机完全不同（R1a 的成员 handler 是
    /// <c>(view, ctx, ct) → Task</c>；判定器包装器是 <c>(args, result) → result</c> 的同步改写）。
    /// 注册面仍共用 <see cref="WrapRegistry"/>（S2a：判定器包装器与成员包装器同一相位语义与同一注册面）。
    /// </para>
    /// </summary>
    public Func<object[]?, object[]?, object[]?>? Judge { get; }

    /// <summary>执行一次（框架调用面）。</summary>
    internal Task InvokeAsync(ViewCarrier view, Context ctx, CancellationToken ct) => Handler(view, ctx, ct);
}

/// <summary>
/// handler 签名校验（S5 第 4 条）：按**形状 + 首参可接收性**判定，**不按类型名逐字相等**。
/// <para>
/// 形状＝四个参数（首参、<see cref="Context"/>、<see cref="CancellationToken"/>）且返回 <see cref="Task"/>；
/// 首参可接收性＝<c>firstParamType.IsAssignableFrom(typeof(ViewCarrier))</c>。
/// 因此 <c>Func&lt;dynamic, …&gt;</c>（擦除为 <c>object</c>）与 <c>Func&lt;ViewCarrier, …&gt;</c> 都合格，
/// 而旧式 <c>Func&lt;CardEventView, …&gt;</c> 不合格（<c>CardEventView</c> 接收不了承载视图实例）。
/// </para>
/// </summary>
public static class HandlerSignature
{
    /// <summary>判定委托类型的形状与首参可接收性。</summary>
    /// <exception cref="ArgumentNullException">delegateType 为 null。</exception>
    public static bool IsCompatible(Type delegateType)
    {
        ArgumentNullException.ThrowIfNull(delegateType);
        return Inspect(delegateType) is not null;
    }

    /// <summary>给出签名不符的原因（合格＝null）。</summary>
    public static string? Describe(Type delegateType)
    {
        ArgumentNullException.ThrowIfNull(delegateType);
        var invoke = delegateType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
        if (invoke is null)
        {
            return $"类型 '{delegateType.Name}' 不是委托类型。";
        }

        var parameters = invoke.GetParameters();
        if (parameters.Length != 3)
        {
            return $"入口须为三参委托（view, Context, CancellationToken）；实际 {parameters.Length} 参。";
        }

        if (parameters[1].ParameterType != typeof(Context))
        {
            return $"第二参数须为 {nameof(Context)}；实际 {parameters[1].ParameterType.Name}。";
        }

        if (parameters[2].ParameterType != typeof(CancellationToken))
        {
            return $"第三参数须为 {nameof(CancellationToken)}；实际 {parameters[2].ParameterType.Name}。";
        }

        if (invoke.ReturnType != typeof(Task))
        {
            return $"返回值须为 {nameof(Task)}；实际 {invoke.ReturnType.Name}。";
        }

        if (!parameters[0].ParameterType.IsAssignableFrom(typeof(ViewCarrier)))
        {
            return $"首参类型 '{parameters[0].ParameterType.Name}' 不能接收承载视图 {nameof(ViewCarrier)}。";
        }

        return null;
    }

    /// <summary>
    /// 把任意形状合格的委托绑定为 <see cref="KernelHandler"/>（不合格＝null）。
    /// 采用表达式编译而非每次反射调用：绑定发生在装载期，执行期为纯委托调用。
    /// </summary>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    public static KernelHandler? TryBindCarrier(Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var invoke = Inspect(handler.GetType());
        if (invoke is null)
        {
            return null;
        }

        var view = Expression.Parameter(typeof(object), "view");
        var ctx = Expression.Parameter(typeof(Context), "ctx");
        var ct = Expression.Parameter(typeof(CancellationToken), "ct");

        var body = Expression.Invoke(
            Expression.Constant(handler),
            Expression.Convert(view, invoke.GetParameters()[0].ParameterType),
            ctx,
            ct);

        var lambda = Expression.Lambda<KernelHandler>(
            Expression.Convert(body, typeof(Task)), view, ctx, ct);
        return lambda.Compile();
    }

    private static MethodInfo? Inspect(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
        if (invoke is null)
        {
            return null;
        }

        var parameters = invoke.GetParameters();
        if (parameters.Length != 3
            || parameters[1].ParameterType != typeof(Context)
            || parameters[2].ParameterType != typeof(CancellationToken)
            || invoke.ReturnType != typeof(Task)
            || !parameters[0].ParameterType.IsAssignableFrom(typeof(ViewCarrier)))
        {
            return null;
        }

        return invoke;
    }
}

/// <summary>
/// handler 注册表（R4：**懒注册 + 全局一份**）。
/// <para>
/// 三种入口：①<see cref="Register"/>（直接给可执行体）；②<see cref="RegisterFactory"/>（首次被引用时物化——懒注册）；
/// ③<see cref="RegisterScript"/>（csx 在**装载期**预编译，编译失败＝拒绝注册，S5 第 9 条）。
/// 同一 id 重复注册＝返回既有资产（全局一份；不会产生第二份实例）。
/// </para>
/// </summary>
public sealed class HandlerRegistry
{
    private readonly ConcurrentDictionary<string, HandlerAsset> _assets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<HandlerAsset>> _factories = new(StringComparer.Ordinal);

    /// <summary>已物化的 handler 资产数（诊断／"被 100 个预制体引用仍只有 1 份"的观测点）。</summary>
    public int Count => _assets.Count;

    /// <summary>注册（或取得已有的）handler 资产。</summary>
    /// <exception cref="ArgumentNullException">id／handler 为 null。</exception>
    /// <exception cref="ArgumentException">id 为空白。</exception>
    public HandlerAsset Register(string id, KernelHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(handler);

        return _assets.GetOrAdd(id, static (key, value) => new HandlerAsset(key, value), handler);
    }

    /// <summary>
    /// 注册懒工厂：资产在**首次被引用**（<see cref="Resolve"/>）时物化，之后全局复用（R4 懒注册）。
    /// </summary>
    /// <exception cref="ArgumentNullException">id／factory 为 null。</exception>
    /// <exception cref="ArgumentException">id 为空白。</exception>
    public void RegisterFactory(string id, Func<HandlerAsset> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[id] = factory;
    }

    /// <summary>解析 handler 资产（未注册＝<see cref="KeyNotFoundException"/>；懒工厂在此刻物化）。</summary>
    /// <exception cref="ArgumentNullException">id 为 null。</exception>
    /// <exception cref="KeyNotFoundException">未注册该 id。</exception>
    public HandlerAsset Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (_assets.TryGetValue(id, out var existing))
        {
            return existing;
        }

        if (_factories.TryGetValue(id, out var factory))
        {
            var created = factory();
            // 并发下可能已由另一线程注册：GetOrAdd 保证全局只有一份。
            return _assets.GetOrAdd(id, created);
        }

        throw new KeyNotFoundException($"handler 资产 '{id}' 未注册（预制体引用了不存在的 handler）。");
    }

    /// <summary>尝试解析（不抛异常；懒工厂仍会在此刻物化）。</summary>
    public bool TryResolve(string id, out HandlerAsset? asset)
    {
        try
        {
            asset = Resolve(id);
            return true;
        }
        catch (KeyNotFoundException)
        {
            asset = null;
            return false;
        }
    }

    /// <summary>
    /// 注册 csx handler：**装载期全量预编译**（S5 第 9 条），编译／静态检查／签名任一失败＝拒绝注册，
    /// 且不留半个可寻址资产（原子）。
    /// </summary>
    /// <param name="id">资产 id。</param>
    /// <param name="source">csx 源码（入口名固定 <c>HandleAsync</c>）。</param>
    /// <param name="evaluator">脚本求值器（内核经接口倒置依赖，不直接引用 Roslyn）。</param>
    /// <param name="view">该 handler 所属成员声明的视图（静态语法检查的比对基准）。</param>
    /// <param name="ownerMembers">owner 通道允许的成员集合（组件私有字段 ∪ 实体公开属性）。</param>
    /// <exception cref="ArgumentNullException">任一必需参数为 null。</exception>
    /// <exception cref="PrefabLoadException">静态检查（binding）／编译（compile）／签名（signature）失败；此时不注册任何资产。</exception>
    public HandlerAsset RegisterScript(
        string id, string source, IScriptEvaluator evaluator, ViewSpec view, IReadOnlyCollection<string> ownerMembers)
        => RegisterScript(id, source, "HandleAsync", evaluator, view, ownerMembers);

    /// <summary>
    /// 注册 csx handler（**入口名可指定**；旧资产的入口名不全是 <c>HandleAsync</c>，如 <c>Handle</c>／
    /// <c>HandleMovedAsync</c>——迁移后的资产必须原样保留入口名，否则装载期预编译会报 entry-missing）。
    /// </summary>
    /// <exception cref="ArgumentNullException">任一必需参数为 null。</exception>
    /// <exception cref="ArgumentException">id／entry 为空白。</exception>
    /// <exception cref="PrefabLoadException">静态检查／编译／签名失败；此时不注册任何资产。</exception>
    public HandlerAsset RegisterScript(
        string id,
        string source,
        string entry,
        IScriptEvaluator evaluator,
        ViewSpec view,
        IReadOnlyCollection<string> ownerMembers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(ownerMembers);

        // id 已被另一份源码占用＝拒绝（不静默复用旧资产：那会让"改了源码却不生效"变成静默失败）。
        if (_assets.TryGetValue(id, out var existing) && !string.Equals(existing.ScriptSource, source, StringComparison.Ordinal))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"handler 资产 id '{id}' 已被另一份 csx 占用（同一 id 不允许指向两份不同源码）。");
        }

        var request = new ScriptRequest(
            source,
            entry,
            typeof(ViewCarrier),
            View: view,
            OwnerMembers: ownerMembers);
        var result = evaluator.Evaluate(request);
        if (!result.Success || result.Handler is null)
        {
            throw new PrefabLoadException(
                result.ErrorCategory ?? ScriptErrorCategories.Compile,
                $"handler 资产 '{id}' 的 csx 装载期校验失败：[{result.ErrorCategory}] {result.Error}");
        }

        var bound = HandlerSignature.TryBindCarrier(result.Handler);
        if (bound is null)
        {
            throw new PrefabLoadException(
                ScriptErrorCategories.Signature,
                $"handler 资产 '{id}' 的入口签名不符：{HandlerSignature.Describe(result.Handler.GetType())}（装载期拒绝注册）。");
        }

        var asset = new HandlerAsset(id, bound, source);
        return _assets.GetOrAdd(id, asset);
    }

    /// <summary>
    /// 注册判定器包装器资产（同步签名）：与成员 handler 共用 id 空间（全局一份），但**不可**当作成员 handler 调用。
    /// </summary>
    /// <exception cref="ArgumentNullException">id／judge 为 null。</exception>
    /// <exception cref="ArgumentException">id 为空白。</exception>
    public HandlerAsset RegisterJudge(string id, Func<object[]?, object[]?, object[]?> judge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(judge);

        return _assets.GetOrAdd(
            id,
            static (key, value) => new HandlerAsset(
                key,
                (_, _, _) => throw new InvalidOperationException(
                    $"handler 资产 '{key}' 是判定器包装器（同步签名），不能按成员 handler 调用。"),
                judge: value),
            judge);
    }

    /// <summary>
    /// 运行期注册同一门面（R1e／R1h）：失败＝抛出且**不残留**可寻址资产。
    /// 与 <see cref="Register"/> 的差别只在语义标注：此处供"运行时向引擎注册新触发器"的资产侧使用。
    /// </summary>
    public HandlerAsset RegisterAtRuntime(string id, KernelHandler handler) => Register(id, handler);
}
