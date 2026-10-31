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

// ─────────────────────────────────────────────────────────────────────────────
// handler DSL 编译的**接口倒置**（内核半段）：
//   内核只定义契约（IHandlerDslCompiler / IHandlerCatalog / HandlerDslCompileResult /
//   HandlerDslError），不引用任何 DSL 类型系统，也不认识节点与引脚——**实现由 satellite 注入**
//   （实现落在 src/Orc.HandlerDsl，随引擎一起演进）。
//   **术语**：handler DSL（图形化产出的中立契约）与 Orc.Game.EffectParsing.Dsl（卡面文本效果 DSL）
//   是两个东西，禁止混称；代码标识符统一 HandlerDsl。
//   接口只认 string 进、string 出 ＋ 错误列表：DSL 的类型系统与目录元模型类型留在实现程序集，
//   内核不被契约类型污染（D4）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// handler DSL 编译错误（一条）——**最小接口形态，三字段不膨胀**（D14）。
/// <para>
/// 「码」的取值域是契约包 <c>contracts/handler-dsl/v1/SPEC.md</c> §14 的封闭集
/// （<c>graph-shape</c>／<c>node-unknown</c>／<c>pin-unknown</c>／<c>pin-missing</c>／<c>pin-type</c>／
/// <c>bind-scope</c>／<c>write-forbidden</c>／<c>loop-unbounded</c>／<c>ask-in-loop</c>／<c>schema-version</c>／
/// <c>expr-op-unknown</c>／<c>expr-arity</c>／<c>expr-type</c>）。
/// </para>
/// <para>
/// <see cref="NodePath"/> 是**节点路径**（如 <c>actions[2].then[0]</c>），不是 csx 行号：
/// DSL 无节点 id，一切定位恒用路径。前端**不**直接消费本类型——装载器把它搬进
/// <see cref="Orc.Core.Model.PrefabLoadError"/>（<c>Code</c>／<c>NodePath</c> 直搬，
/// <c>Target</c>／<c>Category</c> 由装载器填）。
/// </para>
/// </summary>
/// <param name="Code">封闭错误码（SPEC §14.1）。</param>
/// <param name="NodePath">节点路径（字段级错误指向该字段，如 <c>actions[0].else</c>）。</param>
/// <param name="Message">人类可读文案（含引脚名／字段名／期望与实际类型等细节）。</param>
public sealed record HandlerDslError(string Code, string NodePath, string Message);

/// <summary>
/// handler DSL 编译结果（成功携带 csx 文本，失败携带**全部**错误——收集齐而非遇错即停，
/// 与 <see cref="Orc.Core.Model.PrefabLoadReport"/> 同口径）。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="Csx">生成的 csx 文本（失败＝null）。</param>
/// <param name="Errors">错误清单（成功＝空）。</param>
public sealed record HandlerDslCompileResult(
    bool Success,
    string? Csx,
    IReadOnlyList<HandlerDslError> Errors)
{
    /// <summary>构造成功结果。</summary>
    /// <exception cref="ArgumentNullException">csx 为 null。</exception>
    public static HandlerDslCompileResult Ok(string csx)
    {
        ArgumentNullException.ThrowIfNull(csx);
        return new HandlerDslCompileResult(true, csx, Array.Empty<HandlerDslError>());
    }

    /// <summary>构造失败结果（结构化失败是正常路径，不抛错）。</summary>
    /// <exception cref="ArgumentNullException">errors 为 null。</exception>
    public static HandlerDslCompileResult Fail(IReadOnlyList<HandlerDslError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new HandlerDslCompileResult(false, null, errors);
    }
}

/// <summary>
/// 挂载点目标成员的**视图字段声明**（内核形态、纯数据）：字段名 ＋ 引脚类型文本名 ＋ 可写性。
/// <para>
/// 类型用**文本**（<c>entity</c>／<c>number</c>／<c>string</c>／<c>boolean</c>／<c>list&lt;…&gt;</c>）而不是 CLR 类型：
/// 内核不认识契约的引脚类型系统，实现程序集负责解析与越界判定。
/// </para>
/// </summary>
/// <param name="Name">字段名（DSL 的 <c>view</c>／<c>write</c> 按它判）。</param>
/// <param name="Type">引脚类型文本名（封闭集）。</param>
/// <param name="Writable">是否可写（<c>write</c> 的唯一依据）。</param>
public sealed record HandlerDslViewField(string Name, string Type, bool Writable);

/// <summary>
/// 挂载点目标成员的**视图声明快照**（内核形态、纯数据、零依赖）。
/// <para>
/// 装载器从 <c>PrefabType.FindMember(name).View</c> 投影成本类型再传给编译器——
/// 内核不引用 <c>Orc.Core.Model.ViewSpec</c> 的具体投影逻辑，也不认识引脚类型系统（D4）。
/// </para>
/// </summary>
/// <param name="Fields">字段声明（顺序即声明序）。</param>
public sealed record HandlerDslView(IReadOnlyList<HandlerDslViewField> Fields)
{
    /// <summary>
    /// 从视图声明投影（**唯一**投影实现：装载器的草案装载期与摊平器共用它，D4／S11）。
    /// <para>
    /// 类型按 SPEC §6.6 的等价关系归一（<c>entity[]</c> ↔ <c>list&lt;entity&gt;</c>，其余同理）：
    /// 内核只给文本名，实现程序集负责解析与越界判定。
    /// </para>
    /// </summary>
    /// <param name="view">展开后的成员视图声明。</param>
    /// <exception cref="ArgumentNullException">view 为 null。</exception>
    public static HandlerDslView FromViewSpec(Orc.Core.Model.ViewSpec view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var fields = new List<HandlerDslViewField>(view.Fields.Count);
        foreach (var field in view.Fields)
        {
            fields.Add(new HandlerDslViewField(field.Name, PinTypeNameOf(field.Type), field.Writable));
        }

        return new HandlerDslView(fields);
    }

    private static string PinTypeNameOf(Orc.Core.Model.ViewFieldType type) => type switch
    {
        Orc.Core.Model.ViewFieldType.Entity => "entity",
        Orc.Core.Model.ViewFieldType.Number => "number",
        Orc.Core.Model.ViewFieldType.String => "string",
        Orc.Core.Model.ViewFieldType.Boolean => "boolean",
        Orc.Core.Model.ViewFieldType.EntityArray => "list<entity>",
        Orc.Core.Model.ViewFieldType.NumberArray => "list<number>",
        Orc.Core.Model.ViewFieldType.StringArray => "list<string>",
        Orc.Core.Model.ViewFieldType.BooleanArray => "list<boolean>",
        _ => throw new InvalidOperationException("未定义的视图字段类型。"),
    };
}

/// <summary>
/// handler DSL 编译器（内核定义、satellite 实现）：把 DSL 文档编译为 csx 源码文本。
/// <para>
/// **只发生在两处调用点**（R28）：摊平（写盘）与草案装载（不写盘）；**产物装载路径不调用生成器**
/// ——已发布产物里存的是生成后的 csx 文本，引擎升级不改变它的行为。
/// </para>
/// <para>
/// <paramref name="catalog"/> 是**调用参数、不是注入物**：<c>catalogRevision</c> 是目录的属性
/// （<see cref="IHandlerCatalog.Revision"/>），不是编译器的属性；测试期按 vectors 快照构造目录直接传入。
/// </para>
/// <para>
/// <paramref name="view"/> 是**挂载点目标成员的视图声明**：<c>view</c>／<c>write</c> 的类型与可写性只能由它给出
/// （R13／R17）。**null ＝「未知视图」＝保守拒绝**：任何 <c>write</c> 一律 <c>write-forbidden</c>、
/// 任何 <c>view</c> 读取一律 <c>pin-unknown</c>——没有视图声明就无从证明可写／可读，放行等于把错误推到运行期。
/// </para>
/// <para>契约：**不抛异常**（一切失败经 <see cref="HandlerDslCompileResult.Fail"/> 结构化返回）。</para>
/// </summary>
public interface IHandlerDslCompiler
{
    /// <summary>编译一份 DSL 文档。</summary>
    /// <param name="dslJson">DSL 文档的 JSON 文本。</param>
    /// <param name="catalog">节点目录（节点／引脚／可读项的只读查询面）。</param>
    /// <param name="view">挂载点目标成员的视图声明；null＝未知视图（保守拒绝 <c>write</c>／<c>view</c>）。</param>
    /// <exception cref="ArgumentNullException">dslJson 或 catalog 为 null。</exception>
    HandlerDslCompileResult Compile(string dslJson, IHandlerCatalog catalog, HandlerDslView? view = null);
}

/// <summary>
/// **handler DSL 的装配件**（内核形态、纯接口）：编译器 ＋ 目录，装配到 <see cref="LogicEngine.HandlerDsl"/>。
/// <para>
/// 为什么把两者捆在一起：<see cref="IHandlerDslCompiler.Compile"/> 的目录是**调用参数**（D4：
/// <c>catalogRevision</c> 是目录的属性，不是编译器的属性），而装载器（<see cref="Orc.Core.Model.AssetPackLoader"/>）
/// 自己不持有任何目录——它只该知道「这台机器上有没有 DSL 能力」。于是注入面就一个：
/// 有装配件＝能编译（用装配件自带的目录）；没装配＝**能力缺失**（<c>capability</c>），不是什么禁令。
/// </para>
/// <para>
/// **这是能力依赖，不是 R9 所禁的「专项禁令」**：R9 禁止的是「这是草案，所以不装」这类**基于身份**的拒绝；
/// 而「没有编译器就没有编译」等同于「没有 Roslyn 就编译不了 csx」——csx 链本来就是这个语义，DSL 只是沿用。
/// 因此纯内核环境（<c>tests/Orc.Tests</c>）装不了含 DSL 成员的草案是**正确结果、不是缺口**。
/// </para>
/// </summary>
/// <param name="Compiler">编译器（实现由 satellite 注入，如 <c>Orc.HandlerDsl.HandlerDslCompiler</c>）。</param>
/// <param name="Catalog">本次装配使用的节点目录（运行期扫描结果）。</param>
public sealed record HandlerDslEnvironment(IHandlerDslCompiler Compiler, IHandlerCatalog Catalog);

/// <summary>
/// 节点目录（内核侧的**最小**只读面）：可调用原语的清单与版本的查询入口。
/// <para>
/// 内核只认 <see cref="Revision"/>；节点／引脚元数据的具体形态留在实现程序集
/// （<c>Orc.HandlerDsl</c> 的 <c>DslNodeSignature</c> 等），避免内核被契约类型污染（D4）。
/// </para>
/// <para>
/// <see cref="Revision"/> 是**软版本轴**（内容版本）：新增节点／新增可选引脚／放宽取值集只递增它，
/// 旧 DSL 照常装载；删节点／删引脚／改名／可选→必填／收窄取值集这类破坏性变更须同时升
/// <c>schemaVersion</c>（契约包 SPEC §1）。
/// </para>
/// </summary>
public interface IHandlerCatalog
{
    /// <summary>目录内容版本（缺省 0＝空目录）。</summary>
    int Revision { get; }
}
