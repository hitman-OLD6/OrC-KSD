namespace Orc.HandlerDsl;

/// <summary>
/// 契约常量（与文本包 <c>contracts/handler-dsl/v1/SPEC.md</c> 一一对应）。
/// <para>
/// **本类是派生产物**：改值必须**先**改文本包并补齐 vector（D2）。
/// <c>schemaVersion</c>（硬轴）与 <c>catalogRevision</c>（软轴）是两条独立的轴——
/// 前者保证「已存 DSL 能否照原样装载」，后者只是目录内容版本（SPEC §1）。
/// </para>
/// </summary>
public static class HandlerDslContract
{
    /// <summary>本实现的契约版本（硬轴；写进每份 DSL 文档）。</summary>
    public const int SchemaVersion = 1;

    /// <summary>嵌套深度上限（与既有预制体 chain 深度口径一致：<c>PrefabCatalog.Build</c> 的 <c>depth &gt; 64</c>）。</summary>
    public const int MaxDepth = 64;

    /// <summary>内建 op（10 个，封闭）。与目录节点名冲突＝扫描期断言失败。</summary>
    public static IReadOnlyList<string> BuiltinOps { get; } = new[]
    {
        "sequence", "branch", "loop", "while", "iterate", "choice",
        "let", "write", "ask", "script",
    };

    /// <summary>是否为内建 op。</summary>
    public static bool IsBuiltinOp(string? op) =>
        !string.IsNullOrEmpty(op) && BuiltinOps.Contains(op, StringComparer.Ordinal);

    /// <summary>根节点路径（一切定位的起点，SPEC §14.2）。</summary>
    public const string RootPath = "$";

    /// <summary>
    /// 「目录版本不一致」的诊断后缀（SPEC §12.7）：**只在已有错误上留痕**，绝不单独构成拒绝理由。
    /// </summary>
    public static string CatalogRevisionNote(int? documentRevision, int currentRevision) =>
        documentRevision is null
            ? string.Empty
            : $"（本文档针对 catalogRevision={documentRevision}，当前 {currentRevision}）";
}

/// <summary>
/// 封闭错误码集（SPEC §14.1，13 个）。集合外的码＝契约违规（vector 失败）。
/// <para>
/// 映射到装载诊断时：除 <see cref="WriteForbidden"/> → <c>view</c> 外，其余 12 个 → <c>structure</c>；
/// 该映射由**装载器**执行，编译器不认识 <c>PrefabLoadError</c>。
/// </para>
/// </summary>
public static class HandlerDslErrorCodes
{
    /// <summary>不是对象／子节点不是数组／深度超限／<c>else: []</c>／<c>then</c> 缺失或空。</summary>
    public const string GraphShape = "graph-shape";

    /// <summary>op 缺失、非字符串、或不在「10 内建 ∪ 目录节点名」。</summary>
    public const string NodeUnknown = "node-unknown";

    /// <summary>表外字段／未声明引脚／视图字段未声明／read.path 越界／表达式不是五种变体之一／引脚类型名越界。</summary>
    public const string PinUnknown = "pin-unknown";

    /// <summary>必填字段／必填引脚缺失。</summary>
    public const string PinMissing = "pin-missing";

    /// <summary>引脚类型不符（含 entity 接 number、实体引脚接 list&lt;entity&gt;、静态子类型不匹配）。</summary>
    public const string PinType = "pin-type";

    /// <summary>绑定形态错误、键集不符、绑定名非法／重名／遮蔽／跨作用域、未定义 var。</summary>
    public const string BindScope = "bind-scope";

    /// <summary>write 目标未声明或 writable:false（映射到装载诊断的 <c>view</c> 类）。</summary>
    public const string WriteForbidden = "write-forbidden";

    /// <summary>循环缺上限、或上限类型不是 number。</summary>
    public const string LoopUnbounded = "loop-unbounded";

    /// <summary>ask／choice 出现在 iterate 循环体内。</summary>
    public const string AskInLoop = "ask-in-loop";

    /// <summary>schemaVersion 不匹配（错误含双方版本）。</summary>
    public const string SchemaVersion = "schema-version";

    /// <summary>算子名不在 v1 封闭集内（含 div 一类未提供的算子）。</summary>
    public const string ExprOpUnknown = "expr-op-unknown";

    /// <summary>算子入参个数不符。</summary>
    public const string ExprArity = "expr-arity";

    /// <summary>算子入参类型不符／聚合输入非集合。</summary>
    public const string ExprType = "expr-type";

    /// <summary>全部码（封闭集，顺序稳定）。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        GraphShape, NodeUnknown, PinUnknown, PinMissing, PinType, BindScope, WriteForbidden,
        LoopUnbounded, AskInLoop, SchemaVersion, ExprOpUnknown, ExprArity, ExprType,
    };

    /// <summary>是否为封闭集内的码。</summary>
    public static bool IsKnown(string? code) =>
        !string.IsNullOrEmpty(code) && All.Contains(code, StringComparer.Ordinal);
}
