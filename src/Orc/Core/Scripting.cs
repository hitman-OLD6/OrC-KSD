using System.Reflection;

namespace Orc.Core;

// ─────────────────────────────────────────────────────────────────────────────
// S-C6 脚本求值的**接口倒置**（内核半段）：
//   内核只定义契约（IScriptEvaluator / ScriptRequest / ScriptEvaluationResult），不引用 Roslyn；
//   具体 csx 编译由 satellite 工程（如 Orc.Script）实现并经 engine.ScriptEvaluator 注入。
//   委托签名为泛型（Func<TView, Context, CancellationToken, Task>），故求值器返回**擦除形态委托**
//   由 ScriptHandlerAdapter 按视图类型绑回——内核不出现泛型耦合。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 脚本失败分类（S5 第 8d 条：取值集合扩为六类，三者指向完全不同的修复动作）。
/// 旧代码使用开放字符串，本类提供**唯一常量来源**，避免各处手写拼写漂移。
/// </summary>
public static class ScriptErrorCategories
{
    /// <summary>静态语法检查失败（视图／owner 字段未声明、写只读字段）——不是编译错误，改法是改字段名/声明。</summary>
    public const string Binding = "binding";

    /// <summary>编译失败（C# 语法／类型错误）。</summary>
    public const string Compile = "compile";

    /// <summary>入口签名不符（形状或首参可接收性）。</summary>
    public const string Signature = "signature";

    /// <summary>沙箱策略拒绝（关键字过滤／引用白名单）。</summary>
    public const string Sandbox = "sandbox";

    /// <summary>执行超时。</summary>
    public const string Timeout = "timeout";

    /// <summary>运行期异常。</summary>
    public const string Runtime = "runtime";
}

/// <summary>
/// 脚本求值请求（S-C6）：源码 + 入口名 + 目标视图类型 + 超时。
/// <para>
/// 编排实体化（S5）起：<see cref="ViewType"/> 对**新模型的成员 handler 恒为承载视图**
/// <see cref="Orc.Core.Model.ViewCarrier"/>（不再是"资产声明的形状"），并新增
/// <see cref="View"/>／<see cref="OwnerMembers"/> 供**装载期静态语法检查**比对；
/// 两者皆 null＝旧路径（不做静态检查），行为与今天完全一致。
/// </para>
/// </summary>
/// <param name="Source">脚本源码文本（csx）。</param>
/// <param name="EntryName">入口函数名（默认 <c>HandleAsync</c>）。</param>
/// <param name="ViewType">目标视图类型（新模型＝承载视图）。</param>
/// <param name="TimeoutMs">求值/编译超时（毫秒；≤0＝不限）。</param>
/// <param name="View">视图声明（静态检查的 `view.&lt;Name&gt;` 比对基准；null＝不检查）。</param>
/// <param name="OwnerMembers">owner 通道允许的成员集合（私有字段 ∪ 实体公开属性；null＝不检查）。</param>
public sealed record ScriptRequest(
    string Source,
    string EntryName,
    Type ViewType,
    int TimeoutMs = 5000,
    Orc.Core.Model.ViewSpec? View = null,
    IReadOnlyCollection<string>? OwnerMembers = null);

/// <summary>
/// 脚本求值结果（S-C6；结构化——不成败不抛异常）：成功携带擦除形态委托，失败携带分类与消息。
/// 分类为开放字符串（如 compile/runtime/entry-missing/sandbox/timeout），与既有沙箱口径对齐。
/// </summary>
public sealed class ScriptEvaluationResult
{
    private ScriptEvaluationResult(
        bool success,
        Delegate? handler,
        string? errorCategory,
        string? error,
        Orc.Core.Model.ViewBindingAnalysis? binding)
    {
        Success = success;
        Handler = handler;
        ErrorCategory = errorCategory;
        Error = error;
        Binding = binding;
    }

    /// <summary>是否成功。</summary>
    public bool Success { get; }

    /// <summary>擦除形态委托（成功时非 null；实际类型为 <c>Func&lt;TView, Context, CancellationToken, Task&gt;</c>）。</summary>
    public Delegate? Handler { get; }

    /// <summary>失败分类（成功＝null）。</summary>
    public string? ErrorCategory { get; }

    /// <summary>失败消息（成功＝null）。</summary>
    public string? Error { get; }

    /// <summary>
    /// 视图静态检查结果（S5）：未提供视图声明（旧路径）＝null；
    /// 提供时含绑定错误与被放行的"未经静态验证的 view 传递"清单（审查面计数用）。
    /// </summary>
    public Orc.Core.Model.ViewBindingAnalysis? Binding { get; }

    /// <summary>构造成功结果。</summary>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    public static ScriptEvaluationResult Ok(
        Delegate handler, Orc.Core.Model.ViewBindingAnalysis? binding = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new ScriptEvaluationResult(true, handler, null, null, binding);
    }

    /// <summary>构造失败结果（不抛错——结构化失败是正常路径）。</summary>
    /// <exception cref="ArgumentNullException">category 为 null。</exception>
    public static ScriptEvaluationResult Fail(
        string category, string error, Orc.Core.Model.ViewBindingAnalysis? binding = null)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new ScriptEvaluationResult(false, null, category, error, binding);
    }
}

/// <summary>
/// 脚本求值器（S-C6；内核定义、satellite 实现）：把 csx 源码的入口函数编译为可调用委托。
/// 契约：**不抛异常**（一切失败经 <see cref="ScriptEvaluationResult.Fail"/> 结构化返回）；
/// 实现须遵守受控引用集与超时（见 csx 编译上下文契约）。
/// </summary>
public interface IScriptEvaluator
{
    /// <summary>求值一个脚本入口。</summary>
    /// <exception cref="ArgumentNullException">request 为 null。</exception>
    ScriptEvaluationResult Evaluate(ScriptRequest request);
}

/// <summary>
/// 委托适配（S-C6）：把求值器返回的擦除形态委托绑回触发器所需的泛型签名
/// <c>Func&lt;TView, Context, CancellationToken, Task&gt;</c>；类型不符＝null（不抛）。
/// </summary>
public static class ScriptHandlerAdapter
{
    /// <summary>按泛型视图类型绑定（直接类型判定；不符＝null）。</summary>
    public static Func<TView, Context, CancellationToken, Task>? TryBind<TView>(Delegate handler)
        where TView : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return handler as Func<TView, Context, CancellationToken, Task>;
    }

    /// <summary>按运行期视图类型绑定（反射到泛型重载；类型不符＝null）。供非泛型装配路径使用。</summary>
    /// <exception cref="ArgumentNullException">viewType 或 handler 为 null。</exception>
    public static object? TryBind(Type viewType, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(viewType);
        ArgumentNullException.ThrowIfNull(handler);

        var method = typeof(ScriptHandlerAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(TryBind) && m.IsGenericMethodDefinition);

        return method.MakeGenericMethod(viewType).Invoke(null, new object[] { handler });
    }
}
