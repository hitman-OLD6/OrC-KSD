using Orc.Core;

namespace Orc.HandlerDsl;

/// <summary>
/// 图校验器（S6／R26）：在 <see cref="HandlerDslReader"/>（结构）与 <see cref="HandlerDslTypeChecker"/>
/// （类型／作用域／绑定）之上，补**本批新增的语义条款**：
/// <list type="bullet">
/// <item><c>write-forbidden</c>：<c>write</c> 的目标字段必须在挂载点目标成员的视图声明里且 <c>writable: true</c>；</item>
/// <item><c>node-unknown</c> 的「疑似改名」提示（**只提示、不改判定**，D12）。</item>
/// </list>
/// <para>
/// 作用域与类型判定**全部复用批次 1** 的实现（<see cref="HandlerDslTypeChecker"/>）——本类不另写一套规则，
/// 否则「前端能过、后端不过」会变成常态（R26 第 3 条）。
/// </para>
/// </summary>
public static class HandlerDslValidator
{
    /// <summary>
    /// 校验一份文档（结构 ＋ 类型 ＋ 视图可写性）。
    /// </summary>
    /// <param name="json">文档 JSON。</param>
    /// <param name="catalog">节点目录。</param>
    /// <param name="view">挂载点目标成员的视图声明；null＝空视图。</param>
    /// <returns>全部错误（收集齐，不是遇错即停）。</returns>
    public static IReadOnlyList<HandlerDslError> Validate(
        string json, IHandlerDslCatalog catalog, DslView? view = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(catalog);

        var read = HandlerDslReader.Read(json, catalog);
        if (read.Document is null)
        {
            return read.Errors;
        }

        // 结构错误（读取期）与语义错误（类型／作用域／可写性）**一起收**：
        // 同一成员可能同时有 pin-missing 与 write-forbidden，审查面要一次看全（D14）。
        var errors = new List<HandlerDslError>(read.Errors);
        errors.AddRange(Validate(read.Document, catalog, view));
        return errors;
    }

    /// <summary>
    /// 校验一份已解析的文档。
    /// </summary>
    /// <param name="document">已解析文档。</param>
    /// <param name="catalog">节点目录。</param>
    /// <param name="view">视图声明；null＝空视图。</param>
    public static IReadOnlyList<HandlerDslError> Validate(
        DslDocument document, IHandlerDslCatalog catalog, DslView? view = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        var viewKnown = view is not null;
        var effectiveView = view ?? DslView.Empty;
        var errors = new List<HandlerDslError>(HandlerDslTypeChecker.Check(document, catalog, effectiveView.Types));

        // 可写性（R17／SPEC §11.3）：未声明字段、writable:false、以及「视图声明里一个可写字段都没有」都归 write-forbidden。
        foreach (var write in Enumerate(document.Actions).OfType<WriteNode>())
        {
            if (!effectiveView.TryGetField(write.Field, out var field))
            {
                errors.Add(new HandlerDslError(
                    HandlerDslErrorCodes.WriteForbidden,
                    $"{write.Path}.field",
                    viewKnown
                        ? $"write 的目标字段 '{write.Field}' 未在挂载点目标成员的视图声明中声明"
                          + $"（已声明：{DescribeFields(effectiveView)}）；write 只改当前执行帧的视图字段（R17）。"
                        : $"write 的目标字段 '{write.Field}' 无法判定可写：调用方未提供视图声明（view＝null）——"
                          + "没有视图声明就无从证明可写，保守拒绝（放行等于把错误推到运行期）。"));
                continue;
            }

            if (!field.Writable)
            {
                errors.Add(new HandlerDslError(
                    HandlerDslErrorCodes.WriteForbidden,
                    $"{write.Path}.field",
                    $"write 的目标字段 '{write.Field}' 声明为 writable:false；"
                    + "可写字段一个都没有时不要用 write（R17 第 3 条）。"));
            }
        }

        return errors;
    }

    /// <summary>
    /// 深度优先按执行顺序枚举全部节点（含各子数组与 <c>options[].actions</c>）。
    /// </summary>
    /// <param name="nodes">根节点数组。</param>
    public static IEnumerable<DslNode> Enumerate(IReadOnlyList<DslNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        foreach (var node in nodes)
        {
            yield return node;

            if (node is ChoiceNode choice)
            {
                foreach (var option in choice.Options)
                {
                    foreach (var child in Enumerate(option.Actions))
                    {
                        yield return child;
                    }
                }

                continue;
            }

            foreach (var child in Enumerate(node.Children))
            {
                yield return child;
            }
        }
    }

    private static string DescribeFields(DslView view) =>
        view.Fields.Count == 0
            ? "无字段"
            : string.Join("／", view.Fields.Select(static f => f.Writable ? f.Name + "(可写)" : f.Name));
}

/// <summary>
/// <b>13 个图错误码 → 装载诊断大类</b>的映射（D14）。**由装载器调用**（S8）：
/// 编译器只产出 <see cref="HandlerDslError"/>（三字段），不认识 <c>PrefabLoadError</c>。
/// <para>
/// 除 <c>write-forbidden</c> → <c>view</c> 外，其余 12 码 → <c>structure</c>。理由：前者判的是
/// 「这个字段不能写」（与 <c>binding</c> 同源，作者心智是字段），后者判的是图结构与类型。
/// 不把 13 个码塞进 <c>Category</c>——<c>structure</c> 与 <c>pin-type</c> 是「类」与「项」的关系，不是并列。
/// </para>
/// </summary>
public static class HandlerDslErrorCategories
{
    /// <summary>码 → <see cref="PrefabErrorCategories"/> 的类名。</summary>
    /// <param name="code">图错误码（封闭集）。</param>
    /// <exception cref="ArgumentNullException">code 为 null。</exception>
    public static string LoadCategoryOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return string.Equals(code, HandlerDslErrorCodes.WriteForbidden, StringComparison.Ordinal)
            ? Orc.Core.Model.PrefabErrorCategories.View
            : Orc.Core.Model.PrefabErrorCategories.Structure;
    }

    /// <summary>全部 13 个码 → 类别的完整映射（供测试与审查面逐条比对）。</summary>
    public static IReadOnlyDictionary<string, string> LoadCategories { get; } =
        HandlerDslErrorCodes.All.ToDictionary(
            static code => code,
            LoadCategoryOf,
            StringComparer.Ordinal);
}
