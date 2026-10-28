using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orc.Core.Model;

namespace Orc.Script;

/// <summary>
/// 视图静态语法检查（S5 第 8 条／W-16）：用 **Roslyn 语法树**（不做正则／字面匹配）比对
/// <c>view.&lt;Name&gt;</c> 与视图声明、<c>owner.&lt;Name&gt;</c> 与实体成员集合。
/// <para>
/// <b>"算写"的唯一契约（不扩大、不缩小）</b>：直接赋值（<c>=</c>）、<c>+=</c>、<c>-=</c>、
/// <c>++</c>／<c>--</c>、作为 <c>ref</c>／<c>out</c> 实参。其余复合赋值（<c>*=</c> 等）属**未识别形态**：
/// 放行 + 运行期闸门兜底 + 计入留痕（判据见 <see cref="ViewBindingAnalysis"/>）。
/// </para>
/// <para>
/// <b>不算写</b>：注释、字符串字面量（语法树天然排除）。
/// </para>
/// <para>
/// <b>别名</b>：只追踪**直接**别名（<c>var v = view;</c>）；经方法／局部函数传递的 <c>view</c> 不追踪——
/// 这类写法记入 <see cref="ViewBindingAnalysis.UnverifiedViewPasses"/>（放行但留痕）。
/// </para>
/// <para>
/// 判据（W-16）：误判的代价是整包拒绝，漏判的代价只是运行期闸门生效——宁可放行并留痕，不可误杀。
/// </para>
/// </summary>
public static class ViewBindingAnalyzer
{
    private const string ViewName = "view";
    private const string OwnerName = "owner";
    private static readonly string[] BuiltInViewMembers = { "Owner", "Host" };

    /// <summary>
    /// 分析 csx 源码。
    /// </summary>
    /// <param name="source">脚本源码（csx）。</param>
    /// <param name="view">视图声明（<c>view.&lt;Name&gt;</c> 的比对基准）。</param>
    /// <param name="ownerMembers">owner 通道允许的成员（组件私有字段 ∪ 实体公开属性）。</param>
    /// <exception cref="ArgumentNullException">source／view／ownerMembers 为 null。</exception>
    public static ViewBindingAnalysis Analyze(
        string source, ViewSpec view, IReadOnlyCollection<string> ownerMembers)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(ownerMembers);

        var options = new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script);
        var tree = CSharpSyntaxTree.ParseText(source, options);
        var root = tree.GetRoot();
        var text = tree.GetText();

        var ownerNames = new HashSet<string>(ownerMembers, StringComparer.Ordinal);
        foreach (var name in PrefabType.BuiltInOwnerMembers)
        {
            ownerNames.Add(name);
        }

        var viewFields = new HashSet<string>(view.Names, StringComparer.Ordinal);
        foreach (var name in BuiltInViewMembers)
        {
            viewFields.Add(name);
        }

        var aliases = CollectDirectAliases(root);

        // 遮蔽判定（迁移后发现的必要修正）：脚本可合法声明名为 `owner` 的**局部变量**（如 `var owner = ownerOf(self);`）。
        // 此时 `owner.X` 是普通局部对象访问，不是框架的 owner 通道——按通道校验会把合法脚本误判成
        // `undeclared-owner`（误判代价＝整包拒绝，W-16 明令"宁可放行"）。因此检测到遮蔽即**不做** owner 检查，
        // 并记入留痕（该类脚本的 owner 访问由运行期动态访问的 CLR 成员解析兜底）。
        var shadowedNames = CollectDeclaredNames(root);
        var ownerShadowed = shadowedNames.Contains(OwnerName);

        var errors = new List<ViewBindingDiagnostic>();
        var unverified = new List<ViewBindingDiagnostic>();

        if (ownerShadowed)
        {
            var first = root.DescendantNodes().OfType<IdentifierNameSyntax>()
                .FirstOrDefault(i => string.Equals(i.Identifier.ValueText, OwnerName, StringComparison.Ordinal));
            if (first is not null)
            {
                unverified.Add(Diagnostic(
                    "shadowed-owner",
                    OwnerName,
                    first,
                    text,
                    "脚本自行声明了名为 owner 的局部变量：owner 通道的静态检查在本脚本内不适用（放行，由运行期动态访问兜底）。"));
            }
        }

        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var name = identifier.Identifier.ValueText;
            if (string.Equals(name, OwnerName, StringComparison.Ordinal))
            {
                if (!ownerShadowed)
                {
                    InspectOwnerAccess(identifier, ownerNames, errors, text);
                }

                continue;
            }

            if (!aliases.Contains(name))
            {
                continue;
            }

            InspectViewUsage(identifier, view, viewFields, errors, unverified, text);
        }

        return new ViewBindingAnalysis(
            errors.OrderBy(static d => d.Line).ThenBy(static d => d.Column).ToArray(),
            unverified.OrderBy(static d => d.Line).ThenBy(static d => d.Column).ToArray());
    }

    /// <summary>
    /// 收集脚本内**自行声明**的标识符名（局部变量、参数、foreach／catch 变量、本地函数参数）。
    /// 用途：判定 <c>owner</c> 是否被脚本遮蔽（见 <see cref="Analyze"/> 的说明）。
    /// </summary>
    private static HashSet<string> CollectDeclaredNames(SyntaxNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            names.Add(declarator.Identifier.ValueText);
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Identifier.ValueText.Length > 0)
            {
                names.Add(parameter.Identifier.ValueText);
            }
        }

        foreach (var single in root.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
        {
            names.Add(single.Identifier.ValueText);
        }

        foreach (var forEach in root.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            names.Add(forEach.Identifier.ValueText);
        }

        return names;
    }

    /// <summary>收集直接别名（<c>var v = view;</c> 的 <c>v</c>；可链式，最多十轮收敛）。</summary>
    private static HashSet<string> CollectDirectAliases(SyntaxNode root)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal) { ViewName };

        for (var round = 0; round < 10; round++)
        {
            var before = aliases.Count;
            foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer?.Value is not IdentifierNameSyntax initializer)
                {
                    continue;
                }

                if (aliases.Contains(initializer.Identifier.ValueText))
                {
                    aliases.Add(declarator.Identifier.ValueText);
                }
            }

            if (aliases.Count == before)
            {
                break;
            }
        }

        return aliases;
    }

    /// <summary>检查 <c>owner.&lt;Name&gt;</c>：只认已声明成员（私有字段 ∪ 实体公开属性）。</summary>
    private static void InspectOwnerAccess(
        IdentifierNameSyntax identifier,
        HashSet<string> ownerNames,
        List<ViewBindingDiagnostic> errors,
        Microsoft.CodeAnalysis.Text.SourceText text)
    {
        if (identifier.Parent is not MemberAccessExpressionSyntax access
            || !ReferenceEquals(access.Expression, identifier))
        {
            return; // owner 本体被传递：不在本检查范围（只约束字段访问）
        }

        var field = access.Name.Identifier.ValueText;
        if (!ownerNames.Contains(field))
        {
            errors.Add(Diagnostic(
                "undeclared-owner",
                field,
                identifier,
                text,
                $"owner.{field} 未声明：owner 通道只允许实体私有字段与公开属性（R3e／W-14）。"));
        }
    }

    /// <summary>检查 <c>view.&lt;Name&gt;</c> 的读写，以及 <c>view</c> 被传递的形态。</summary>
    private static void InspectViewUsage(
        IdentifierNameSyntax identifier,
        ViewSpec view,
        HashSet<string> viewFields,
        List<ViewBindingDiagnostic> errors,
        List<ViewBindingDiagnostic> unverified,
        Microsoft.CodeAnalysis.Text.SourceText text)
    {
        if (identifier.Parent is MemberAccessExpressionSyntax access
            && ReferenceEquals(access.Expression, identifier))
        {
            var field = access.Name.Identifier.ValueText;
            if (!viewFields.Contains(field))
            {
                errors.Add(Diagnostic(
                    "undeclared-view",
                    field,
                    identifier,
                    text,
                    $"view.{field} 未在视图声明中（视图的名字空间只含视图声明字段；私有字段请走 owner.{field}）。"));
                return;
            }

            var write = ClassifyWrite(access);
            if (write == WriteKind.None)
            {
                return;
            }

            if (write == WriteKind.Unrecognized)
            {
                unverified.Add(Diagnostic(
                    "unverified-pass",
                    field,
                    identifier,
                    text,
                    $"view.{field} 的写入形态不在静态检查范围内（放行，由运行期写入闸门兜底）。"));
                return;
            }

            var declared = view.Find(field);
            if (declared is null)
            {
                // 内建字段（Owner／Host）由框架回填、不来自数据面：只读。
                errors.Add(Diagnostic(
                    "readonly-write",
                    field,
                    identifier,
                    text,
                    $"view.{field} 是框架回填的内建字段（不来自数据面），不可写。"));
                return;
            }

            if (!declared.Writable)
            {
                errors.Add(Diagnostic(
                    "readonly-write",
                    field,
                    identifier,
                    text,
                    $"view.{field} 为只读字段，写入被拒绝（如确需写回数据面，请在视图声明中标记为可写）。"));
            }

            return;
        }

        // 直接别名声明（var v = view;）不算"传递"。
        if (identifier.Parent is EqualsValueClauseSyntax equals
            && equals.Parent is VariableDeclaratorSyntax declarator
            && declarator.Initializer == equals)
        {
            return;
        }

        unverified.Add(Diagnostic(
            "unverified-pass",
            identifier.Identifier.ValueText,
            identifier,
            text,
            "view 被交给方法／局部函数／返回值：该形态不在静态检查范围内（放行，由运行期闸门兜底）。"));
    }

    private enum WriteKind
    {
        None,
        Write,
        Unrecognized,
    }

    /// <summary>"算写"判定：恰为直接赋值／<c>+=</c>／<c>-=</c>／<c>++</c>／<c>--</c>／<c>ref</c>·<c>out</c> 实参。</summary>
    private static WriteKind ClassifyWrite(MemberAccessExpressionSyntax access)
    {
        var parent = access.Parent;

        if (parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, access))
        {
            return assignment.Kind() switch
            {
                SyntaxKind.SimpleAssignmentExpression => WriteKind.Write,
                SyntaxKind.AddAssignmentExpression => WriteKind.Write,
                SyntaxKind.SubtractAssignmentExpression => WriteKind.Write,
                _ => WriteKind.Unrecognized,
            };
        }

        if (parent is PrefixUnaryExpressionSyntax prefix
            && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return WriteKind.Write;
        }

        if (parent is PostfixUnaryExpressionSyntax postfix
            && (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            return WriteKind.Write;
        }

        if (parent is ArgumentSyntax argument
            && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return WriteKind.Write;
        }

        return WriteKind.None;
    }

    private static ViewBindingDiagnostic Diagnostic(
        string kind,
        string field,
        SyntaxNode node,
        Microsoft.CodeAnalysis.Text.SourceText text,
        string message)
    {
        var span = node.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line;
        var snippet = line >= 0 && line < text.Lines.Count ? text.Lines[line].ToString().Trim() : string.Empty;
        return new ViewBindingDiagnostic(
            kind, field, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, snippet, message);
    }
}
