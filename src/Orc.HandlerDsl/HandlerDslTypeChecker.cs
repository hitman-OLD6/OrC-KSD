using Orc.Core;

namespace Orc.HandlerDsl;

/// <summary>
/// v1 算子集（封闭，SPEC §5）。**没有除法**：`div`／`mod`／`pow` 一类一律 <c>expr-op-unknown</c>——
/// 除法一进来就要定除零语义，而整数除 0 在 csx 里会抛异常、handler 异常会上抛。
/// </summary>
public static class HandlerDslOperators
{
    private static readonly Dictionary<string, int> Arities = new(StringComparer.Ordinal)
    {
        ["add"] = 2,
        ["sub"] = 2,
        ["mul"] = 2,
        ["eq"] = 2,
        ["ne"] = 2,
        ["lt"] = 2,
        ["le"] = 2,
        ["gt"] = 2,
        ["ge"] = 2,
        ["and"] = 2,
        ["or"] = 2,
        ["not"] = 1,
        ["count"] = 1,
        ["sum"] = 1,
        ["max"] = 1,
        ["min"] = 1,
        ["empty"] = 1,
    };

    /// <summary>全部算子名（顺序稳定，供契约文档与测试比对）。</summary>
    public static IReadOnlyList<string> V1 { get; } =
        new[] { "add", "sub", "mul", "eq", "ne", "lt", "le", "gt", "ge", "and", "or", "not", "count", "sum", "max", "min", "empty" };

    /// <summary>算子名是否在 v1 封闭集内。</summary>
    public static bool IsKnown(string? op) =>
        !string.IsNullOrEmpty(op) && Arities.ContainsKey(op);

    /// <summary>取算子元数（未知算子＝false）。</summary>
    public static bool TryArity(string op, out int arity) => Arities.TryGetValue(op, out arity);
}

/// <summary>
/// 图校验的**类型层**（R16／D7）：把整棵树走一遍，为每个表达式自底向上推导类型，
/// 再与引脚期望类型比对（<c>pin-type</c>）；表达式内部的错用走 <c>expr-*</c>。
/// <para>
/// 推导规则是**一条统一规则**：每个表达式节点有确定类型，类型只由「节点 kind ＋ 入参类型」决定、
/// **与上下文无关**——不做双向推断、不做隐式转换。因此同一表达式搬到另一引脚，推导结果不变。
/// </para>
/// <para>
/// 本类不承担：可写性（<c>write-forbidden</c>）、交互节点在循环体内（<c>ask-in-loop</c>）等语义规则
/// ——那是校验器的其余部分；这里只保证「类型」这一层可判定且与上下文无关。
/// </para>
/// </summary>
public static class HandlerDslTypeChecker
{
    private static readonly IReadOnlyDictionary<string, PinType> NoViewFields =
        new Dictionary<string, PinType>(StringComparer.Ordinal);

    /// <summary>
    /// 校验整份文档的类型层。
    /// </summary>
    /// <param name="document">已解析的文档。</param>
    /// <param name="catalog">节点目录（引脚类型与可读项的来源）。</param>
    /// <param name="viewFields">挂载点目标成员的视图声明（字段名 → 引脚类型）；null＝无视图声明，
    /// 此时任何 <c>{ "view": … }</c> 都会被判为未声明字段（R13 第 3 条）。</param>
    public static IReadOnlyList<HandlerDslError> Check(
        DslDocument document,
        IHandlerDslCatalog catalog,
        IReadOnlyDictionary<string, PinType>? viewFields = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        var checker = new Checker(catalog, viewFields ?? NoViewFields);
        checker.CheckActions(document.Actions, checker.Root);

        var errors = checker.Errors;
        if (document.CatalogRevision is not null && document.CatalogRevision.Value != catalog.Revision && errors.Count > 0)
        {
            var note = HandlerDslContract.CatalogRevisionNote(document.CatalogRevision, catalog.Revision);
            for (var i = 0; i < errors.Count; i++)
            {
                errors[i] = errors[i] with { Message = errors[i].Message + note };
            }
        }

        return errors;
    }

    /// <summary>
    /// 单个表达式的类型推导（**与目标引脚无关**）：供前端本地预览、S9 向量与单测直接调用。
    /// </summary>
    /// <param name="expression">表达式。</param>
    /// <param name="catalog">节点目录。</param>
    /// <param name="viewFields">视图声明。</param>
    /// <param name="bindings">可见绑定（名 → 类型）。</param>
    /// <param name="errors">可选：收集错误（不传＝丢弃）。</param>
    /// <returns>推导出的类型；有错＝null。</returns>
    public static PinType? Infer(
        DslExpression expression,
        IHandlerDslCatalog catalog,
        IReadOnlyDictionary<string, PinType>? viewFields = null,
        IReadOnlyDictionary<string, PinType>? bindings = null,
        List<HandlerDslError>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(catalog);

        var checker = new Checker(catalog, viewFields ?? NoViewFields, errors);
        var scope = checker.Root;
        if (bindings is not null)
        {
            foreach (var pair in bindings)
            {
                scope.Declare(pair.Key, new Binding(pair.Key, pair.Value, "<调用方提供的绑定>"));
            }
        }

        return checker.InferExpression(expression, scope);
    }

    private sealed record Binding(string Name, PinType Type, string DeclaredAt);

    private sealed class Scope
    {
        private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);

        public Scope(Scope? parent = null) => Parent = parent;

        public Scope? Parent { get; }

        public bool TryLookup(string name, out Binding binding)
        {
            if (_bindings.TryGetValue(name, out binding!))
            {
                return true;
            }

            return Parent is not null && Parent.TryLookup(name, out binding);
        }

        public void Declare(string name, Binding binding) => _bindings[name] = binding;
    }

    private sealed class Checker
    {
        private readonly IHandlerDslCatalog _catalog;
        private readonly IReadOnlyDictionary<string, PinType> _viewFields;
        private readonly List<HandlerDslError> _errors;

        public Checker(
            IHandlerDslCatalog catalog,
            IReadOnlyDictionary<string, PinType> viewFields,
            List<HandlerDslError>? errors = null)
        {
            _catalog = catalog;
            _viewFields = viewFields;
            _errors = errors ?? new List<HandlerDslError>();
        }

        public Scope Root { get; } = new();

        public List<HandlerDslError> Errors => _errors;

        public void CheckActions(IReadOnlyList<DslNode> nodes, Scope scope, bool insideIterate = false)
        {
            foreach (var node in nodes)
            {
                CheckNode(node, scope, insideIterate);
            }
        }

        private void CheckNode(DslNode node, Scope scope, bool insideIterate)
        {
            switch (node)
            {
                case SequenceNode sequence:
                    // 序列体与父同一作用域：声明的绑定对后续兄弟可见（R20「同一 actions 数组内」）。
                    CheckActions(sequence.Actions, scope, insideIterate);
                    break;

                case BranchNode branch:
                    Expect(branch.Test, PinType.Boolean, scope, $"branch.test 必须推导为 boolean");
                    CheckActions(branch.Then, new Scope(scope), insideIterate);
                    if (branch.Else is not null)
                    {
                        CheckActions(branch.Else, new Scope(scope), insideIterate);
                    }

                    break;

                case LoopNode loop:
                    ExpectLoopBound(loop.Times, scope, "times");
                    // 交互限制只针对 iterate 循环体（同一 handler 内不发起重复交互，R23）。
                    CheckActions(loop.Actions, new Scope(scope), insideIterate);
                    break;

                case WhileNode whileNode:
                    Expect(whileNode.Test, PinType.Boolean, scope, "while.test 必须推导为 boolean（每轮重估）");
                    ExpectLoopBound(whileNode.Limit, scope, "limit");
                    CheckActions(whileNode.Actions, new Scope(scope), insideIterate);
                    break;

                case IterateNode iterate:
                    CheckIterate(iterate, scope);
                    break;

                case ChoiceNode choice:
                    if (insideIterate)
                    {
                        RejectInteractiveInIterate(choice, "choice");
                    }

                    // 所有 options[].actions 同层互斥、不叠加：各自独立子作用域。
                    foreach (var option in choice.Options)
                    {
                        CheckActions(option.Actions, new Scope(scope), insideIterate);
                    }

                    break;

                case LetNode letNode:
                    var valueType = InferExpression(letNode.Value, scope);
                    if (valueType is not null)
                    {
                        Declare(letNode.Name, valueType, $"{letNode.Path}.name", scope);
                    }

                    break;

                case WriteNode write:
                    CheckWrite(write, scope);
                    break;

                case AskNode ask:
                    if (insideIterate)
                    {
                        RejectInteractiveInIterate(ask, "ask");
                    }

                    if (ask.Filter is not null)
                    {
                        Expect(ask.Filter, PinType.Boolean, scope, "ask.filter 必须推导为 boolean");
                    }

                    // ask 返回恒为 list<entity>，静态子类型＝kind（R23／SPEC §10.2）。
                    var askType = PinType.List(PinType.Entity.WithSubtypes(ask.Kind));
                    if (ask.Bind?.Single is { } askBinding)
                    {
                        Declare(askBinding, askType, ask.Bind!.Path, scope);
                    }

                    break;

                case ScriptNode:
                    // 脚本节点的引脚 schema 自带、正文不参与 DSL 类型系统（R15 的控制流限制只约束节点层）。
                    break;

                case ActionNode action:
                    CheckAction(action, scope);
                    break;
            }
        }

        private void RejectInteractiveInIterate(DslNode node, string kind) =>
            Error(HandlerDslErrorCodes.AskInLoop, node.Path,
                $"{kind} 不得出现在 iterate 循环体内：同一 handler 内不发起重复交互（表达式里不藏交互副作用）。");

        private void CheckIterate(IterateNode iterate, Scope scope)
        {
            var overType = InferExpression(iterate.Over, scope);
            if (overType is null)
            {
                return;
            }

            if (!overType.IsList)
            {
                Error(HandlerDslErrorCodes.PinType, iterate.Over.Path,
                    $"iterate.over 必须是集合（list<T>），实际 {overType.Format()}：单个实体的遍历没有意义。");
                return;
            }

            var body = new Scope(scope);
            Declare(iterate.As, overType.Element!, $"{iterate.Path}.as", body);
            CheckActions(iterate.Actions, body, insideIterate: true);
        }

        private void CheckWrite(WriteNode write, Scope scope)
        {
            var valueType = InferExpression(write.Value, scope);
            if (valueType is null)
            {
                return;
            }

            // 可写性（未声明／writable:false）由校验器的 write-forbidden 条款负责；这里只判类型。
            if (_viewFields.TryGetValue(write.Field, out var fieldType)
                && !valueType.IsAssignableTo(fieldType, out var mismatch))
            {
                Error(HandlerDslErrorCodes.PinType, write.Value.Path,
                    $"write.field '{write.Field}' 的类型不符：{mismatch}");
            }
        }

        private void CheckAction(ActionNode action, Scope scope)
        {
            if (!_catalog.TryGetNode(action.NodeName, out var signature))
            {
                // 未知节点已在读取期报出（node-unknown）；此处不再重复。
                return;
            }

            foreach (var argument in action.Arguments)
            {
                var pin = signature.Input(argument.Name);
                if (pin is null)
                {
                    continue;
                }

                var type = InferExpression(argument.Value, scope);
                if (type is null)
                {
                    continue;
                }

                if (!type.IsAssignableTo(pin.Type, out var mismatch))
                {
                    var hint = pin.Type.Kind == PinTypeKind.Entity && type.IsList
                        ? "；动作的实体引脚只吃单个 entity，集合请显式 iterate 遍历（R22）"
                        : string.Empty;
                    Error(HandlerDslErrorCodes.PinType, argument.Value.Path,
                        $"节点 '{action.NodeName}' 的引脚 '{argument.Name}' 类型不符：{mismatch}{hint}");
                }
            }

            if (action.Bind is null)
            {
                return;
            }

            if (action.Bind.IsSingle)
            {
                if (signature.Outputs.Count == 1)
                {
                    Declare(action.Bind.Single!, signature.Outputs[0].Type, action.Bind.Path, scope);
                }

                return;
            }

            foreach (var pair in action.Bind.Multiple)
            {
                var output = signature.Output(pair.Key);
                if (output is not null)
                {
                    Declare(pair.Value, output.Type, $"{action.Bind.Path}.{pair.Key}", scope);
                }
            }
        }

        private void ExpectLoopBound(DslExpression expression, Scope scope, string field)
        {
            var type = InferExpression(expression, scope);
            if (type is null)
            {
                return;
            }

            if (type.Kind != PinTypeKind.Number)
            {
                // 上限类型不是 number ＝ loop-unbounded（不落 pin-type，SPEC §14.1）。
                Error(HandlerDslErrorCodes.LoopUnbounded, expression.Path,
                    $"循环上限 {field} 必须推导为 number，实际 {type.Format()}（可为任意 number 表达式，不要求常量）。");
            }
        }

        private void Expect(DslExpression expression, PinType expected, Scope scope, string what)
        {
            var type = InferExpression(expression, scope);
            if (type is null)
            {
                return;
            }

            if (!type.IsAssignableTo(expected, out var mismatch))
            {
                Error(HandlerDslErrorCodes.PinType, expression.Path, $"{what}：{mismatch}");
            }
        }

        private void Declare(string name, PinType type, string path, Scope scope)
        {
            if (_viewFields.ContainsKey(name))
            {
                Error(HandlerDslErrorCodes.BindScope, path,
                    $"绑定名 '{name}' 与视图字段同名（绑定与视图字段不得同名，R19）。");
                return;
            }

            if (scope.TryLookup(name, out var existing))
            {
                Error(HandlerDslErrorCodes.BindScope, path,
                    $"绑定名 '{name}' 重复声明（此前声明于 {existing.DeclaredAt}）：绑定是单赋值，"
                    + "同一可见链上禁止同名与遮蔽。");
                return;
            }

            scope.Declare(name, new Binding(name, type, path));
        }

        public PinType? InferExpression(DslExpression expression, Scope scope)
        {
            switch (expression)
            {
                case LiteralExpression literal:
                    return literal.Kind switch
                    {
                        LiteralKind.Number => PinType.Number,
                        LiteralKind.String => PinType.String,
                        _ => PinType.Boolean,
                    };

                case VarExpression variable:
                    if (!scope.TryLookup(variable.Name, out var binding))
                    {
                        Error(HandlerDslErrorCodes.BindScope, variable.Path,
                            $"未定义的绑定 '{variable.Name}'：绑定只在声明它的 actions 数组内、声明点之后可见。");
                        return null;
                    }

                    return binding.Type;

                case ViewExpression view:
                    if (!_viewFields.TryGetValue(view.Field, out var fieldType))
                    {
                        Error(HandlerDslErrorCodes.PinUnknown, view.Path,
                            $"视图字段 '{view.Field}' 未在挂载点目标成员的视图声明中声明。");
                        return null;
                    }

                    return fieldType;

                case ReadExpression read:
                    if (_catalog.Readables.TryGetValue(read.Property, out var readType))
                    {
                        return readType;
                    }

                    Error(HandlerDslErrorCodes.PinUnknown, read.Path,
                        $"read.path '{read.Property}' 不在目录声明的可读项内。");
                    return null;

                case OpExpression op:
                    return InferOperator(op, scope);

                default:
                    Error(HandlerDslErrorCodes.PinUnknown, expression.Path, "未知的表达式形态。");
                    return null;
            }
        }

        private PinType? InferOperator(OpExpression op, Scope scope)
        {
            if (!HandlerDslOperators.IsKnown(op.Op))
            {
                Error(HandlerDslErrorCodes.ExprOpUnknown, op.Path,
                    $"算子 '{op.Op}' 不在 v1 封闭集内（可用：{string.Join("／", HandlerDslOperators.V1)}；"
                    + "v1 不提供除法）。");
                return null;
            }

            HandlerDslOperators.TryArity(op.Op, out var arity);
            if (op.Args.Count != arity)
            {
                Error(HandlerDslErrorCodes.ExprArity, op.Path,
                    $"算子 '{op.Op}' 需要 {arity} 个入参，实际 {op.Args.Count} 个。");
                return null;
            }

            var types = new List<PinType?>(arity);
            foreach (var argument in op.Args)
            {
                types.Add(InferExpression(argument, scope));
            }

            if (types.Any(t => t is null))
            {
                return null;
            }

            switch (op.Op)
            {
                case "add":
                case "sub":
                case "mul":
                    if (RequireEach(op, types, PinType.Number))
                    {
                        return PinType.Number;
                    }

                    return null;

                case "lt":
                case "le":
                case "gt":
                case "ge":
                    if (RequireEach(op, types, PinType.Number))
                    {
                        return PinType.Boolean;
                    }

                    return null;

                case "and":
                case "or":
                    if (RequireEach(op, types, PinType.Boolean))
                    {
                        return PinType.Boolean;
                    }

                    return null;

                case "not":
                    return RequireEach(op, types, PinType.Boolean) ? PinType.Boolean : null;

                case "eq":
                case "ne":
                    return CheckEquality(op, types) ? PinType.Boolean : null;

                case "count":
                    return RequireList(op, types[0]!, "count") ? PinType.Number : null;

                case "sum":
                case "max":
                case "min":
                    return RequireListOfNumber(op, types[0]!, op.Op) ? PinType.Number : null;

                case "empty":
                    return RequireList(op, types[0]!, "empty") ? PinType.Boolean : null;

                default:
                    Error(HandlerDslErrorCodes.ExprOpUnknown, op.Path, $"算子 '{op.Op}' 不在 v1 封闭集内。");
                    return null;
            }
        }

        private bool RequireEach(OpExpression op, IReadOnlyList<PinType?> types, PinType expected)
        {
            var ok = true;
            for (var i = 0; i < types.Count; i++)
            {
                var actual = types[i]!;
                if (actual.Kind != expected.Kind)
                {
                    Error(HandlerDslErrorCodes.ExprType, op.Args[i].Path,
                        $"算子 '{op.Op}' 的第 {i + 1} 个入参类型不符：期望 {expected.Format()}，实际 {actual.Format()}"
                        + "（不做隐式转换）。");
                    ok = false;
                }
            }

            return ok;
        }

        private bool CheckEquality(OpExpression op, IReadOnlyList<PinType?> types)
        {
            var left = types[0]!;
            var right = types[1]!;

            if (left.IsList || right.IsList)
            {
                Error(HandlerDslErrorCodes.ExprType, op.Path,
                    $"算子 '{op.Op}' 不接受集合：判空用 empty、计数用 count。");
                return false;
            }

            if (!left.Equals(right))
            {
                Error(HandlerDslErrorCodes.ExprType, op.Path,
                    $"算子 '{op.Op}' 两侧类型必须相同：左侧 {left.Format()}，右侧 {right.Format()}。");
                return false;
            }

            return true;
        }

        private bool RequireList(OpExpression op, PinType actual, string what)
        {
            if (actual.IsList)
            {
                return true;
            }

            Error(HandlerDslErrorCodes.ExprType, op.Args[0].Path,
                $"算子 '{what}' 的入参必须是集合（list<T>），实际 {actual.Format()}：聚合输入非集合。");
            return false;
        }

        private bool RequireListOfNumber(OpExpression op, PinType actual, string what)
        {
            if (actual.IsList && actual.Element!.Kind == PinTypeKind.Number)
            {
                return true;
            }

            var message = actual.IsList
                ? $"算子 '{what}' 只接受 list<number>，实际 {actual.Format()}：实体集合用 count 取大小、用 iterate 取元素。"
                : $"算子 '{what}' 的入参必须是集合（list<number>），实际 {actual.Format()}：聚合输入非集合。";
            Error(HandlerDslErrorCodes.ExprType, op.Args[0].Path, message);
            return false;
        }

        private void Error(string code, string nodePath, string message) =>
            _errors.Add(new HandlerDslError(code, nodePath, message));
    }
}
