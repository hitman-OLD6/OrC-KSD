using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orc.HandlerDsl;

/// <summary>生成结果：csx 文本 ＋ 内容寻址 handler id ＋ 节点路径 → 行号区间（调试映射，不进文本）。</summary>
/// <param name="Csx">生成的 csx 文本（LF、结尾恰一个换行，见 SPEC §13）。</param>
/// <param name="HandlerId"><c>hdsl/v1/&lt;sha256 前 16 字节 hex&gt;</c>（D9 内容寻址）。</param>
/// <param name="NodeMap">节点路径 → 行号区间（<c>[起始行, 结束行]</c>，1 起）；**不进文本、不进 textHash**。</param>
public sealed record HandlerDslGenerationResult(
    string Csx,
    string HandlerId,
    IReadOnlyDictionary<string, int[]> NodeMap);

/// <summary>
/// **DSL → csx 生成器**（S7）。一个实现、两个调用点（摊平写盘／草案装载不写盘）；
/// **产物装载路径不调用本类**（R28：产物里存的是文本）。
/// <para>
/// 三条确定性硬约束（D9，内容寻址把确定性从验收升级为「身份正确性」的前提）：
/// ①数字一律 <see cref="CultureInfo.InvariantCulture"/>；②不生成任何含时间戳／随机值／哈希的注释；
/// ③变量名由**结构位置**编码（<c>actions[2].then[0]</c> → <c>__actions_2_then_0</c>），不用自增序号。
/// </para>
/// <para>
/// **节点名不进文本**（D12／F8）：注释与变量名里都没有节点名——改个名字不该产生新 handler id。
/// 调试可读性由 <see cref="HandlerDslGenerationResult.NodeMap"/> 补偿。
/// </para>
/// <para>
/// 生成语句形态由本类唯一决定（无任何可调参数，F1–F12 见 SPEC §13）；权威期望文本由
/// <c>contracts/handler-dsl/v1/vectors/gen/*.expected.csx</c> 锁住。
/// </para>
/// </summary>
public static class HandlerDslGenerator
{
    /// <summary>生成器标识（写进产物来源标记 <c>generated.generator</c>，不进 csx 文本）。</summary>
    public const string GeneratorId = "handler-dsl/v1";

    /// <summary>生成类 handler id 的前缀（内容寻址；手写／原样复制的 csx 用 <c>csx/v1/</c>）。</summary>
    public const string HandlerIdPrefix = "hdsl/v1/";

    /// <summary>隐式宿主的固定变量名（<c>host</c>／<c>viewer</c>／<c>self</c> 首参不出引脚，生成码用它）。</summary>
    public const string SelfVariable = "orcSelf";

    /// <summary>
    /// 运行时门面的固定变量名（<see cref="RuntimeVariable"/>）：整段 handler **只解析一次**，
    /// 取不到即早退。与 <see cref="SelfVariable"/> 一样用保留前缀，作者绑定名不得以 <c>__</c> 开头。
    /// </summary>
    public const string RuntimeVariable = "__runtime";

    /// <summary>handler 入口名（csx 固定三参签名）。</summary>
    public const string EntryName = "HandleAsync";

    /// <summary>内容寻址 handler id：<c>hdsl/v1/</c> ＋ sha256(文本) 前 16 字节 hex。</summary>
    /// <param name="csx">生成文本（逐字节参与哈希，不做任何规范化）。</param>
    public static string HandlerIdOf(string csx)
    {
        ArgumentNullException.ThrowIfNull(csx);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(csx));
        var builder = new StringBuilder(HandlerIdPrefix.Length + 32);
        builder.Append(HandlerIdPrefix);
        for (var i = 0; i < 16; i++)
        {
            builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成 csx。**调用前必须已通过 <see cref="HandlerDslValidator"/>**（本方法假定文档合法；
    /// 遇到不可解析的形态＝抛 <see cref="InvalidOperationException"/>，不产出半成品文本）。
    /// </summary>
    /// <param name="document">已校验的文档。</param>
    /// <param name="catalog">节点目录。</param>
    /// <param name="view">视图声明（只用于 <c>read</c> 的类型转换落型）。</param>
    /// <exception cref="ArgumentNullException">document／catalog 为 null。</exception>
    public static HandlerDslGenerationResult Generate(
        DslDocument document, IHandlerDslCatalog catalog, DslView? view = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        var emitter = new Emitter(catalog, view ?? DslView.Empty);
        var lines = emitter.Emit(document);
        var csx = string.Join("\n", lines) + "\n";

        return new HandlerDslGenerationResult(csx, HandlerIdOf(csx), emitter.NodeMap);
    }

    private sealed class Emitter
    {
        private readonly IHandlerDslCatalog _catalog;
        private readonly DslView _view;
        private readonly List<string> _lines = new();
        private readonly SortedDictionary<string, int[]> _nodeMap = new(StringComparer.Ordinal);

        public Emitter(IHandlerDslCatalog catalog, DslView view)
        {
            _catalog = catalog;
            _view = view;
        }

        public IReadOnlyDictionary<string, int[]> NodeMap => _nodeMap;

        public List<string> Emit(DslDocument document)
        {
            _lines.Add($"Func<dynamic, Context, CancellationToken, Task> {EntryName} = async (view, ctx, ct) =>");
            _lines.Add("{");

            if (NeedsSelf(document.Actions))
            {
                Line(1, $"var {SelfVariable} = (Orc.Cards.Card)view.Host;");
            }

            // 运行时门面**解析一次**（R28 之外的行为修正）：
            // ①每动作各调一次 ResolveFor 是重复解析；②`ResolveFor(...)!` 在宿主非卡牌时会 NRE，
            // 而手写 csx 的既有写法是「取不到就优雅返回」。这里统一成一次解析 ＋ 早退。
            if (NeedsRuntime(document.Actions))
            {
                Line(1, $"var {RuntimeVariable} = Orc.Game.Effects.EffectRuntime.ResolveFor({SelfVariable});");
                Line(1, $"if ({RuntimeVariable} is null)");
                Line(1, "{");
                Line(2, "return;");
                Line(1, "}");
            }

            EmitActions(document.Actions, 1);
            _lines.Add("};");
            return _lines;
        }

        // ── 结构 ────────────────────────────────────────────────────────────

        private void EmitActions(IReadOnlyList<DslNode> nodes, int indent)
        {
            foreach (var node in nodes)
            {
                EmitNode(node, indent);
            }
        }

        private void EmitNode(DslNode node, int indent)
        {
            var start = _lines.Count + 1;

            switch (node)
            {
                case SequenceNode sequence:
                    // 序列不产生语句：数组顺序即执行顺序（R18），嵌套不改变缩进层级（F1 只随语句块 +1）。
                    EmitActions(sequence.Actions, indent);
                    break;

                case BranchNode branch:
                    Line(indent, $"if ({Expr(branch.Test)})");
                    Block(branch.Then, indent);
                    if (branch.Else is not null)
                    {
                        Line(indent, "else");
                        Block(branch.Else, indent);
                    }

                    break;

                case LoopNode loop:
                    EmitCountedLoop(
                        loop.Path,
                        loop.Times,
                        test: null,
                        loop.Actions,
                        indent);
                    break;

                case WhileNode whileNode:
                    EmitCountedLoop(
                        whileNode.Path,
                        whileNode.Limit,
                        whileNode.Test,
                        whileNode.Actions,
                        indent);
                    break;

                case IterateNode iterate:
                    Line(indent, $"foreach (var {iterate.As} in {Expr(iterate.Over)})");
                    Block(iterate.Actions, indent);
                    break;

                case ChoiceNode choice:
                    EmitChoice(choice, indent);
                    break;

                case LetNode let:
                    Line(indent, $"var {let.Name} = {Expr(let.Value)};");
                    break;

                case WriteNode write:
                    // write 直写当前执行帧的视图字段，不经 EffectRuntime（R17 第 2 条）。
                    Line(indent, $"view.{write.Field} = {Expr(write.Value)};");
                    break;

                case AskNode ask:
                    EmitAsk(ask, indent);
                    break;

                case ScriptNode script:
                    EmitScript(script, indent);
                    break;

                case ActionNode action:
                    EmitAction(action, indent);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"未知节点形态 '{node.GetType().Name}'（{node.Path}）：生成器只认识契约包的 10 个内建 op ＋ 目录节点。");
            }

            _nodeMap[node.Path] = new[] { start, _lines.Count };
        }

        /// <summary>`{ … }` 块：空块不生成空行占位（F7）。</summary>
        private void Block(IReadOnlyList<DslNode> nodes, int indent)
        {
            Line(indent, "{");
            EmitActions(nodes, indent + 1);
            Line(indent, "}");
        }

        /// <summary>
        /// 有界循环：恒为 <c>for</c>，**不出现裸 <c>while(true)</c>**（R14／D16）。
        /// 上限：字面量直接内联，非字面量落**一个**临时变量（F11 唯一允许的临时变量），循环体内不重算。
        /// </summary>
        private void EmitCountedLoop(
            string path,
            DslExpression bound,
            DslExpression? test,
            IReadOnlyList<DslNode> body,
            int indent)
        {
            var suffix = EncodingOf(path);
            var counter = $"__i{suffix}";
            string limit;

            if (bound is LiteralExpression { Kind: LiteralKind.Number } literal)
            {
                limit = Number(literal);
            }
            else
            {
                var temp = $"__bound{suffix}";
                Line(indent, $"var {temp} = {Expr(bound)};");
                limit = temp;
            }

            var condition = test is null
                ? $"{counter} < {limit}"
                : $"{counter} < {limit} && ({Expr(test)})";
            Line(indent, $"for (var {counter} = 0; {condition}; {counter}++)");
            Block(body, indent);
        }

        private void EmitChoice(ChoiceNode choice, int indent)
        {
            var suffix = EncodingOf(choice.Path);
            var index = $"__choice{suffix}";
            var labels = string.Join(", ", choice.Options.Select(static o => Quote(o.Label)));

            Line(indent, $"var {index} = await Orc.Game.Cards.KeywordEffectActionFace.ChooseAsync("
                + $"{SelfVariable}, {Quote(choice.Prompt)}, new string[] {{ {labels} }}, ct);");
            Line(indent, $"switch ({index})");
            Line(indent, "{");

            for (var i = 0; i < choice.Options.Count; i++)
            {
                Line(indent + 1, $"case {i.ToString(CultureInfo.InvariantCulture)}:");
                EmitActions(choice.Options[i].Actions, indent + 2);
                Line(indent + 2, "break;");
            }

            Line(indent, "}");
        }

        private void EmitAsk(AskNode ask, int indent)
        {
            var call = $"await Orc.Game.Cards.KeywordEffectActionFace.AskAsync("
                + $"{SelfVariable}, {Quote(ask.Kind)}, {Nullable(ask.Side)}, {Nullable(ask.Zone)}, "
                + $"{Quote(ask.Prompt)}, {Quote(FallbackText(ask.Fallback))}, ct)";

            if (ask.Bind?.Single is { } binding)
            {
                Line(indent, $"var {binding} = {call};");
                return;
            }

            Line(indent, $"{call};");
        }

        private void EmitScript(ScriptNode script, int indent)
        {
            // 正文原样内联（只做行尾归一 + 逐行缩进）；脚本节点的存在不改变其它节点的生成文本（R15 第 5 条）。
            foreach (var line in script.Body.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Replace('\r', '\n')
                         .Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Line(indent, line);
            }
        }

        private void EmitAction(ActionNode action, int indent)
        {
            if (HandlerDslContextNodes.IsContextOp(action.NodeName))
            {
                Line(indent, action.NodeName == HandlerDslContextNodes.Stop ? "ctx.Stop();" : "ctx.Interrupt();");
                return;
            }

            if (!_catalog.TryGetNode(action.NodeName, out var signature))
            {
                throw new InvalidOperationException(
                    $"节点 '{action.NodeName}'（{action.Path}）不在目录内：校验应已拒绝，生成器不接受未知节点。");
            }

            var arguments = new List<string>(signature.Inputs.Count + 2);

            // 隐式宿主参数（host／viewer／self）：不出引脚，但**是必经实参**。
            // 不传＝该形参拿到 default（引用类型即 null），调用要么编译不过（无默认值）、
            // 要么在动作内部 NRE——早期版本就是漏在这里。
            if (signature.ImplicitHost is { Length: > 0 } implicitHost)
            {
                arguments.Add($"{implicitHost}: {SelfVariable}");
            }

            foreach (var pin in signature.Inputs)
            {
                var argument = action.Argument(pin.Name);
                if (argument is not null)
                {
                    arguments.Add($"{pin.Name}: {ArgExpr(pin, argument.Value)}");
                    continue;
                }

                // source 折叠（未连线）＝缺省接宿主（SPEC §12.1）。
                if (string.Equals(pin.Name, "source", StringComparison.Ordinal))
                {
                    arguments.Add($"{pin.Name}: {SelfVariable}");
                }
            }

            // CancellationToken **不出引脚但必须传下去**：不传＝动作恒拿 default，
            // 取消/超时永远传导不到动作内部（形参位置不固定，故用命名实参，与位置无关）。
            if (signature.CancellationTokenParameter is { Length: > 0 } tokenParameter)
            {
                arguments.Add($"{tokenParameter}: ct");
            }

            var target = signature.IsStatic && signature.FaceType is { Length: > 0 } faceType
                ? $"{faceType}.{signature.Method}"
                : $"{RuntimeVariable}.{signature.Method}";

            var call = $"{target}({string.Join(", ", arguments)})";
            var awaited = signature.HasAwait ? $"await {call}" : call;

            if (action.Bind is null)
            {
                Line(indent, $"{awaited};");
                return;
            }

            if (action.Bind.IsSingle)
            {
                Line(indent, $"var {action.Bind.Single} = {awaited};");
                return;
            }

            // 多输出：结果对象只求值一次 ⇒ 必须落一个结果变量，再逐输出取名。
            // （F11 的「唯一允许的临时变量」针对**表达式内联**；这里是多输出拆解的结构需要。）
            var resultVariable = $"__res{EncodingOf(action.Path)}";
            Line(indent, $"var {resultVariable} = {awaited};");
            foreach (var pair in action.Bind.Multiple)
            {
                var output = signature.Output(pair.Key);
                var member = $"{resultVariable}.{PascalCase(pair.Key)}";
                if (output is not null && output.IsEnumOutput)
                {
                    // 枚举输出 → string（封闭集）：结果对象的枚举属性落成 .ToString()。
                    member += ".ToString()";
                }

                Line(indent, $"var {pair.Value} = {member};");
            }
        }

        // ── 表达式 ──────────────────────────────────────────────────────────

        /// <summary>引脚实参：枚举引脚落 <c>&lt;枚举类型&gt;.&lt;成员&gt;</c>（值必须是编译期字面量）。</summary>
        private string ArgExpr(DslPin pin, DslExpression value)
        {
            if (pin.IsEnum && value is LiteralExpression { Kind: LiteralKind.String } text)
            {
                var member = text.Text ?? string.Empty;
                if (!pin.EnumValues.Contains(member, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"枚举引脚 '{pin.Name}' 不接受取值 '{member}'（可用：{string.Join("／", pin.EnumValues)}）。");
                }

                return $"{pin.EnumType}.{member}";
            }

            return Expr(value);
        }

        private string Expr(DslExpression expression) => expression switch
        {
            LiteralExpression literal => Literal(literal),
            VarExpression variable => variable.Name,
            ViewExpression view => $"view.{view.Field}",
            ReadExpression read => ReadCall(read),
            OpExpression op => OpExpr(op),
            _ => throw new InvalidOperationException($"未知表达式形态（{expression.Path}）。"),
        };

        private string Literal(LiteralExpression literal) => literal.Kind switch
        {
            LiteralKind.Number => Number(literal),
            LiteralKind.String => Quote(literal.Text ?? string.Empty),
            _ => literal.Boolean ? "true" : "false",
        };

        private string ReadCall(ReadExpression read)
        {
            var call = $"Orc.Game.Cards.KeywordEffectActionFace.ReadValue({SelfVariable}, "
                + $"{Quote(read.Of)}, {Quote(read.Property)})";

            if (!_catalog.Readables.TryGetValue(read.Property, out var type))
            {
                return call;
            }

            // read 的值从 object 落回封闭集的具体 C# 类型，便于参与算术／比较。
            return type.Kind switch
            {
                PinTypeKind.Number => $"(int){call}",
                PinTypeKind.String => $"(string){call}",
                PinTypeKind.Boolean => $"(bool){call}",
                _ => call,
            };
        }

        private string OpExpr(OpExpression op)
        {
            var args = op.Args.Select(Expr).ToArray();
            return op.Op switch
            {
                "add" => $"({args[0]} + {args[1]})",
                "sub" => $"({args[0]} - {args[1]})",
                "mul" => $"({args[0]} * {args[1]})",
                "eq" => $"({args[0]} == {args[1]})",
                "ne" => $"({args[0]} != {args[1]})",
                "lt" => $"({args[0]} < {args[1]})",
                "le" => $"({args[0]} <= {args[1]})",
                "gt" => $"({args[0]} > {args[1]})",
                "ge" => $"({args[0]} >= {args[1]})",
                "and" => $"({args[0]} && {args[1]})",
                "or" => $"({args[0]} || {args[1]})",
                "not" => $"(!{args[0]})",
                "count" => $"({args[0]}).Count",
                // 空集 → 0（SPEC §5）：Max／Min／Sum 在空集上的行为不可依赖，故显式短路。
                "sum" => $"(({args[0]}).Count == 0 ? 0 : System.Linq.Enumerable.Sum({args[0]}))",
                "max" => $"(({args[0]}).Count == 0 ? 0 : System.Linq.Enumerable.Max({args[0]}))",
                "min" => $"(({args[0]}).Count == 0 ? 0 : System.Linq.Enumerable.Min({args[0]}))",
                "empty" => $"(({args[0]}).Count == 0)",
                _ => throw new InvalidOperationException(
                    $"算子 '{op.Op}'（{op.Path}）不在 v1 封闭集内：校验应已拒绝，生成器不接受未知算子。"),
            };
        }

        // ── 工具 ────────────────────────────────────────────────────────────

        private static bool NeedsSelf(IReadOnlyList<DslNode> nodes)
        {
            if (ContainsRead(nodes))
            {
                return true;
            }

            foreach (var node in HandlerDslValidator.Enumerate(nodes))
            {
                switch (node)
                {
                    case AskNode or ChoiceNode:
                        return true;
                    case ActionNode action when !HandlerDslContextNodes.IsContextOp(action.NodeName):
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 是否需要运行时门面变量：存在**非静态**动作节点（静态门面直接 <c>类型.方法</c>，不经 <c>ResolveFor</c>）。
        /// </summary>
        private bool NeedsRuntime(IReadOnlyList<DslNode> nodes)
        {
            foreach (var node in HandlerDslValidator.Enumerate(nodes))
            {
                if (node is not ActionNode action || HandlerDslContextNodes.IsContextOp(action.NodeName))
                {
                    continue;
                }

                if (!_catalog.TryGetNode(action.NodeName, out var signature) || !signature.IsStatic)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsRead(IReadOnlyList<DslNode> nodes)
        {
            foreach (var node in HandlerDslValidator.Enumerate(nodes))
            {
                var found = node switch
                {
                    LetNode let => HasRead(let.Value),
                    WriteNode write => HasRead(write.Value),
                    BranchNode branch => HasRead(branch.Test),
                    LoopNode loop => HasRead(loop.Times),
                    WhileNode whileNode => HasRead(whileNode.Test) || HasRead(whileNode.Limit),
                    IterateNode iterate => HasRead(iterate.Over),
                    ActionNode action => action.Arguments.Any(a => HasRead(a.Value)),
                    _ => false,
                };

                if (found)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRead(DslExpression expression) => expression switch
        {
            ReadExpression => true,
            OpExpression op => op.Args.Any(HasRead),
            _ => false,
        };

        private static string FallbackText(AskFallback fallback) =>
            fallback == AskFallback.First ? "first" : "empty";

        private static string Nullable(string? text) => text is null ? "null" : Quote(text);

        private static string Number(LiteralExpression literal)
        {
            var value = literal.Number;
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            {
                // F4：整数字面量不带小数点、不带正号。
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>F5：常规转义，**不输出 Unicode 转义**。</summary>
        private static string Quote(string text)
        {
            var builder = new StringBuilder(text.Length + 2);
            builder.Append('"');
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(ch);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>F6：结构位置编码——<c>actions[2].then[0]</c> → <c>_actions_2_then_0</c>（前缀 <c>__</c>）。</summary>
        private static string EncodingOf(string path)
        {
            var builder = new StringBuilder(path.Length + 2);
            for (var i = 0; i < path.Length; i++)
            {
                var ch = path[i];
                if (ch == '[')
                {
                    builder.Append('_');
                    continue;
                }

                if (ch == ']')
                {
                    continue;
                }

                builder.Append(ch == '.' ? '_' : ch);
            }

            return "__" + builder;
        }

        private static string PascalCase(string name) =>
            name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

        private void Line(int indent, string text) =>
            _lines.Add(new string(' ', indent * 4) + text);
    }
}
