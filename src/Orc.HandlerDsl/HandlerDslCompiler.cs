using Orc.Core;

namespace Orc.HandlerDsl;

/// <summary>
/// handler DSL 编译器（S6 校验 ＋ S7 生成）：内核 <see cref="IHandlerDslCompiler"/> 的实现。
/// <para>
/// **只在两处调用点被调用**（R28）：摊平（写盘）与草案装载（不写盘）；**产物装载路径不调用它**
/// ——已发布产物里存的是生成后的 csx 文本，引擎升级不改变它的行为。
/// </para>
/// <para>
/// 视图声明走内核形态 <see cref="HandlerDslView"/>（纯数据、文本类型名），本类负责把它解析成
/// 契约侧的引脚类型（SPEC §6 封闭集）：**类型名越界＝<c>pin-unknown</c> 结构化失败**，不抛异常。
/// <c>view == null</c> ＝「未知视图」＝**保守拒绝**（<c>write</c> → <c>write-forbidden</c>、
/// <c>view</c> 读取 → <c>pin-unknown</c>）。
/// </para>
/// </summary>
public sealed class HandlerDslCompiler : IHandlerDslCompiler
{
    /// <summary>生成器标识（与 <see cref="HandlerDslGenerator.GeneratorId"/> 同源）。</summary>
    public const string Generator = HandlerDslGenerator.GeneratorId;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">dslJson 或 catalog 为 null。</exception>
    public HandlerDslCompileResult Compile(string dslJson, IHandlerCatalog catalog, HandlerDslView? view = null)
    {
        ArgumentNullException.ThrowIfNull(dslJson);
        ArgumentNullException.ThrowIfNull(catalog);

        var generated = CompileToGeneration(
            dslJson, catalog as IHandlerDslCatalog ?? DslCatalog.Empty, view, out var errors);

        return generated is null
            ? HandlerDslCompileResult.Fail(errors)
            : HandlerDslCompileResult.Ok(generated.Csx);
    }

    /// <summary>
    /// 编译并返回**完整生成结果**（csx ＋ 内容寻址 handler id ＋ 节点行号映射）——摊平写盘需要后两者。
    /// </summary>
    /// <param name="dslJson">DSL 文档的 JSON 文本。</param>
    /// <param name="catalog">节点目录（内核最小面；不是 <see cref="IHandlerDslCatalog"/> 时按空目录处理）。</param>
    /// <param name="view">挂载点目标成员的视图声明；null＝未知视图（保守拒绝）。</param>
    /// <param name="errors">失败时的全部错误（成功＝空）。</param>
    /// <exception cref="ArgumentNullException">dslJson 或 catalog 为 null。</exception>
    public HandlerDslGenerationResult? CompileToGeneration(
        string dslJson, IHandlerCatalog catalog, HandlerDslView? view, out IReadOnlyList<HandlerDslError> errors)
    {
        ArgumentNullException.ThrowIfNull(dslJson);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!TryResolveView(view, out var dslView, out var viewError))
        {
            errors = new[] { viewError! };
            return null;
        }

        errors = HandlerDslValidator.Validate(dslJson, catalog as IHandlerDslCatalog ?? DslCatalog.Empty, dslView);
        if (errors.Count > 0)
        {
            return null;
        }

        var read = HandlerDslReader.Read(dslJson, catalog as IHandlerDslCatalog ?? DslCatalog.Empty);
        if (read.Document is null)
        {
            // 理论上不可达（Validate 已保证无错）；保守返回结构化失败，不抛。
            errors = read.Errors;
            return null;
        }

        errors = Array.Empty<HandlerDslError>();
        return HandlerDslGenerator.Generate(
            read.Document, catalog as IHandlerDslCatalog ?? DslCatalog.Empty, dslView ?? DslView.Empty);
    }

    /// <summary>
    /// 内核形态视图 → 契约侧视图（引脚类型解析）。**类型名越界＝<c>pin-unknown</c>**（SPEC §6：目录／DSL 侧
    /// 出现封闭集外的引脚类型名都拒绝；视图声明是同一封闭集的第三个来源）。
    /// <para>
    /// <c>view == null</c> 原样保留为 null（＝**未知视图**，由校验器保守拒绝 <c>write</c>／<c>view</c>）；
    /// 不在这里替换成空视图，否则「调用方没给」与「声明了零个字段」会被混成同一件事。
    /// </para>
    /// </summary>
    private static bool TryResolveView(
        HandlerDslView? view, out DslView? resolved, out HandlerDslError? error)
    {
        resolved = null;
        error = null;

        if (view is null)
        {
            return true;
        }

        var fields = new List<DslViewField>(view.Fields.Count);
        foreach (var field in view.Fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!PinType.TryParse(field.Type, out var type))
            {
                error = new HandlerDslError(
                    HandlerDslErrorCodes.PinUnknown,
                    HandlerDslContract.RootPath,
                    $"视图字段 '{field.Name}' 的类型 '{field.Type}' 超出引脚封闭集："
                    + $"只允许 {string.Join("／", PinType.CanonicalNames)}（不提供结构体、字典、可空类型与任意 CLR 类型）。");
                return false;
            }

            fields.Add(new DslViewField(field.Name, type, field.Writable));
        }

        resolved = new DslView(fields);
        return true;
    }
}
