using Orc.Core;
using Orc.HandlerDsl;

namespace Orc.Game.Effects;

/// <summary>
/// 目录里的一个引脚（**纯数据**，S10 入口二）：名、封闭类型名、必填性、实体子类型集、枚举取值集。
/// <para>请求全量目录时，**标注展开结果已经在这里**（多输出拆解、枚举取值集）——前端无需自行展开（D8）。</para>
/// </summary>
/// <param name="Name">引脚名（契约可见；改名＝删除引脚）。</param>
/// <param name="Type">封闭类型名（<c>entity</c>／<c>number</c>／<c>string</c>／<c>boolean</c>／<c>list&lt;…&gt;</c>）。</param>
/// <param name="Required">是否必填（有默认值／只在部分重载出现＝可省略）。</param>
/// <param name="Subtypes">实体引脚的运行期接受子类型集（<c>unit</c>／<c>hq</c>／<c>card</c>；其余类型为空）。</param>
/// <param name="EnumValues">枚举取值的允许集合（仅 <c>string</c> 引脚；空＝不限）。</param>
/// <param name="EnumType">枚举引脚/枚举输出的 CLR 类型全名或保留标记（可空）。</param>
public sealed record HandlerCatalogPinDto(
    string Name,
    string Type,
    bool Required,
    IReadOnlyList<string> Subtypes,
    IReadOnlyList<string> EnumValues,
    string? EnumType);

/// <summary>目录里的一个节点（**纯数据**）：引脚 ＋ 折叠标记 ＋ 扫描溯源。</summary>
/// <param name="Name">节点名（<c>op</c> 的合法取值）。</param>
/// <param name="HasAwait">**单节点**标记：本节点自身会挂起（返回 Task）。子树聚合由前端在具体树上做（SPEC §12.3）。</param>
/// <param name="HasSideEffect">**单节点**标记：本节点自身有副作用。子树聚合同上。</param>
/// <param name="FaceType">来源门面类型全名（合成节点 <c>stop</c>／<c>interrupt</c> 为空）。</param>
/// <param name="Inputs">输入引脚。</param>
/// <param name="Outputs">输出引脚（空＝无返回值）。</param>
public sealed record HandlerCatalogNodeDto(
    string Name,
    bool HasAwait,
    bool HasSideEffect,
    string? FaceType,
    IReadOnlyList<HandlerCatalogPinDto> Inputs,
    IReadOnlyList<HandlerCatalogPinDto> Outputs);

/// <summary>
/// **节点目录的全量导出**（S10 入口二／R5／R25／D13）：节点 ＋ 引脚 ＋ **标注展开结果** ＋ 扫描的门面类型清单。
/// <para>
/// 前端首次连接拉一次全量、按 <see cref="CatalogRevision"/> 缓存；之后只查 revision 判断是否重拉。
/// **能本地复现的只有默认映射规则与表达式类型推导**——节点清单与最终引脚集必须查询
/// （是「本地预览不必每次问后端」，**不是**「离线可全量复现」，D8）。
/// </para>
/// </summary>
/// <param name="CatalogRevision">目录内容版本（与 <see cref="HandlerCatalogFacade.CatalogRevision"/> 恒一致）。</param>
/// <param name="ScannedFaces">本次扫描的门面类型名清单（回答「这个节点来自哪个门面」）。</param>
/// <param name="Readables">可读项（<c>read.path</c> 的封闭取值集 → 类型名）。</param>
/// <param name="Nodes">全部节点（含合成节点 <c>stop</c>／<c>interrupt</c>）。</param>
public sealed record HandlerCatalogDto(
    int CatalogRevision,
    IReadOnlyList<string> ScannedFaces,
    IReadOnlyDictionary<string, string> Readables,
    IReadOnlyList<HandlerCatalogNodeDto> Nodes);

/// <summary>
/// **节点目录门面**（S10 入口二／D13）：回答「引擎能表达什么」，与「这个资产能编辑什么」
/// （<c>AssetEditor.DescribePrefab</c>）是两个不同的问题，**不合并**。
/// <para>
/// 为什么在 <c>Orc.Game</c> 而不是 <c>Orc.Packages</c>：目录与卡包无关，只与 <c>EffectRuntime</c> 的反射结果有关；
/// 而 <c>AssetEditor</c> 在 <c>Orc.Packages</c>。硬合并就得让 <c>Orc.Packages</c> 反向依赖
/// <c>Orc.Game</c> 才能拿到动作面，等于把分层拆掉（D13）。
/// </para>
/// <para>
/// **同一门面、同一个 <c>catalogRevision</c>**：<see cref="CatalogRevision"/> 与 <see cref="GetCatalog"/>
/// 共享同一份扫描结果（构造时扫描一次），不会出现「revision 说没变、全量却是新的」。
/// </para>
/// </summary>
public sealed class HandlerCatalogFacade
{
    private readonly HandlerDslEnvironment _environment;
    private HandlerCatalogDto? _snapshot;

    /// <summary>以默认装配（扫描本程序集的动作面）构造。</summary>
    public HandlerCatalogFacade()
        : this(HandlerDslAssembly.Create())
    {
    }

    /// <summary>以给定装配件构造（测试注入快照目录，避免依赖运行期扫描结果）。</summary>
    /// <param name="environment">编译器 ＋ 目录。</param>
    /// <exception cref="ArgumentNullException">environment 为 null。</exception>
    public HandlerCatalogFacade(HandlerDslEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>轻查询：只回目录内容版本（前端据此决定要不要重拉全量）。</summary>
    public int CatalogRevision() => _environment.Catalog.Revision;

    /// <summary>
    /// 全量目录（**纯数据**、无运行期对象引用；引脚集**已含标注展开结果**——多输出拆解与枚举取值集）。
    /// <para>同一份扫描只投影一次并缓存：同一实例多次调用返回同一个对象实例（revision 相同＝内容相同）。</para>
    /// </summary>
    public HandlerCatalogDto GetCatalog()
        => _snapshot ??= Project(_environment);

    private static HandlerCatalogDto Project(HandlerDslEnvironment environment)
    {
        // 目录的**内容版本**是编译器的入参属性（D4）；这里只做投影，不重算、不猜。
        var catalog = environment.Catalog as IHandlerDslCatalog
                      ?? throw new InvalidOperationException(
                          "装配件的目录不是 handler DSL 目录实现（无法导出节点与引脚）：请传入 HandlerDslAssembly.Create() 的结果。");

        return new HandlerCatalogDto(
            catalog.Revision,
            catalog.ScannedFaces,
            catalog.Readables.ToDictionary(static p => p.Key, static p => p.Value.Format(), StringComparer.Ordinal),
            catalog.Nodes.Select(ProjectNode).ToArray());
    }

    private static HandlerCatalogNodeDto ProjectNode(DslNodeSignature node)
        => new(
            node.Name,
            node.HasAwait,
            node.HasSideEffect,
            node.FaceType,
            node.Inputs.Select(ProjectPin).ToArray(),
            node.Outputs.Select(ProjectPin).ToArray());

    private static HandlerCatalogPinDto ProjectPin(DslPin pin)
    {
        // 实体引脚的子类型集挂在「元素」上（列表引脚同样是元素级约束，SPEC §6.4）。
        var subtypes = pin.Type.IsList ? pin.Type.Element!.Subtypes : pin.Type.Subtypes;
        return new HandlerCatalogPinDto(
            pin.Name,
            BaseName(pin.Type),
            pin.Required,
            subtypes,
            pin.EnumValues,
            pin.EnumType);
    }

    /// <summary>引脚类型的规范基名（不含子类型后缀；子类型集走独立字段，与 <c>catalog.schema.json</c> 同口径）。</summary>
    private static string BaseName(PinType type) => type.Kind switch
    {
        PinTypeKind.Entity => "entity",
        PinTypeKind.Number => "number",
        PinTypeKind.String => "string",
        PinTypeKind.Boolean => "boolean",
        PinTypeKind.List => $"list<{BaseName(type.Element!)}>",
        _ => throw new InvalidOperationException("未定义的引脚类型种类。"),
    };
}
