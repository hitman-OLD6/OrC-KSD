using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Orc.Core.Model;

/// <summary>实例化模式（R39 裁决 9）：**只决定身份与寿命，不决定形态**。</summary>
public enum ExpansionMode
{
    /// <summary>对局级：缓存键内部派生、不进资产命名空间、不落盘（寿命随引擎实例）。</summary>
    Inline,

    /// <summary>具名：资产 id 由调用方提供（必填）、可解析、<c>Save</c> 才落盘。</summary>
    Named,
}

/// <summary>
/// **值表面提供的填充值**（S29）：资产级空位值 ＋ 成员级 handler 值。
/// <para>
/// 它是「值从哪来」的唯一差异点；**填空位这件事本身只有一份实现**（<see cref="PrefabExpander"/>）。
/// 声明面（摊平）用空表——它的值来自草案声明；值表面（实例化）用 <c>args</c>。
/// </para>
/// </summary>
public sealed class SlotBindings
{
    /// <summary>无绑定（声明面：值全部来自声明）。</summary>
    public static SlotBindings Empty { get; } = new(
        new Dictionary<string, object?>(StringComparer.Ordinal),
        new Dictionary<string, HandlerSlotArgument>(StringComparer.Ordinal));

    /// <summary>创建绑定。</summary>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public SlotBindings(
        IReadOnlyDictionary<string, object?> assetValues,
        IReadOnlyDictionary<string, HandlerSlotArgument> handlers)
    {
        ArgumentNullException.ThrowIfNull(assetValues);
        ArgumentNullException.ThrowIfNull(handlers);
        AssetValues = assetValues;
        Handlers = handlers;
    }

    /// <summary>资产级空位值（键＝空位名）。</summary>
    public IReadOnlyDictionary<string, object?> AssetValues { get; }

    /// <summary>成员级 handler 值（键＝成员名；三种 kind 之一）。</summary>
    public IReadOnlyDictionary<string, HandlerSlotArgument> Handlers { get; }
}

/// <summary>一个未填的空位（<see cref="ExpandedPrefab.Unfilled"/> 的元素；供 DTO 投影复用，S33）。</summary>
/// <param name="Scope">作用域。</param>
/// <param name="Name">空位名（成员级＝成员名）。</param>
/// <param name="OwnerId">声明它的资产 id。</param>
public sealed record SlotGap(SlotScope Scope, string Name, string OwnerId);

/// <summary>**共用展开核** <see cref="PrefabExpander.Expand"/> 的输入。</summary>
public sealed class ExpandRequest
{
    /// <summary>目标目录（**不得已登记** <see cref="Declaration"/> 的 id：叶子由核登记后展开）。</summary>
    public required PrefabCatalog Catalog { get; init; }

    /// <summary>输入面构造的声明（摊平＝草案；实例化＝模板）。</summary>
    public required ComponentAsset Declaration { get; init; }

    /// <summary>产出资产 id（null＝沿用 <see cref="Declaration"/> 的 id；摊平传产物 id）。</summary>
    public string? ResultId { get; init; }

    /// <summary>填充值（声明面＝空表）。</summary>
    public SlotBindings Bindings { get; init; } = SlotBindings.Empty;

    /// <summary>链上的资产声明（复制父的 handler 源码、收集 judge-wraps、合并资产级空位用）。</summary>
    public IReadOnlyDictionary<string, ComponentAsset> Declarations { get; init; } =
        new Dictionary<string, ComponentAsset>(StringComparer.Ordinal);

    /// <summary>handler DSL 装配件（null＝含 DSL 值＝能力缺失，与既有口径一致）。</summary>
    public HandlerDslEnvironment? HandlerDsl { get; init; }

    /// <summary>DSL 相对路径 → 绝对路径（null＝不存在）。路径基准属输入面（产物目录／包根），核不做 IO 约定。</summary>
    public Func<string, string?>? ResolveDslPath { get; init; }

    /// <summary>父展开结果（摊平已经解析过；null＝由核解析）。</summary>
    public PrefabType? Parent { get; init; }

    /// <summary>
    /// 取某个父成员的守卫合成参数（null＝本环境不解析守卫）。
    /// 守卫**由核合成**进 csx（与 <c>args</c> 无关），字段名白名单的查表属输入面（内核见不到游戏层信号表）。
    /// </summary>
    public Func<MemberDefinition, HandlerGuardSpec?>? GuardOf { get; init; }

    /// <summary>饱和判定之后、填充之前的声明面校验（摊平用它跑模板 <c>requires-host</c> 交集，保持错误顺序）。</summary>
    public Action<List<PrefabLoadError>>? AfterSaturation { get; init; }

    /// <summary>成员层饱和判定的来源（摊平＝父展开表；实例化＝模板自身的展开表）。</summary>
    public PrefabType? SaturationSource { get; init; }

    /// <summary>
    /// 产物要带的**出身记录**（R39 裁决 10／S30／S31；null＝不带）。
    /// <para>摊平传 <see cref="AssetOrigin.ForDraft"/>，实例化传 <see cref="AssetOrigin.ForTemplate"/>——
    /// 同一结构、同一个产出函数，核只负责把它写进产物，不解释、不校验来源是否存在。</para>
    /// </summary>
    public AssetOrigin? Origin { get; init; }
}

/// <summary>**共用展开核**的产出：填满后的资产形态 ＋ 展开类型 ＋ 未填充位清单。</summary>
/// <param name="Asset">填满后的资产（产物写盘／实例化登记的形态）。</param>
/// <param name="Type">展开后的预制体类型（合并、覆写保序、饱和判定都已在此完成）。</param>
/// <param name="Unfilled">未填充位清单（空＝饱和；DTO 的「是否需要实现」只读它，S33）。</param>
public sealed record ExpandedPrefab(
    ComponentAsset Asset, PrefabType Type, IReadOnlyList<SlotGap> Unfilled);

/// <summary>**实例化（值表面）** 的输入。</summary>
public sealed class InstantiationRequest
{
    /// <summary>被实例化的模板（其 <c>extends</c> 链必须在 <see cref="Declarations"/> 里）。</summary>
    public required ComponentAsset Template { get; init; }

    /// <summary><c>args</c>：一层扁平具名表（只装六种类型的值，**不能新增成员**）。</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>具名度（只决定身份与寿命，不决定形态）。</summary>
    public ExpansionMode Mode { get; init; } = ExpansionMode.Inline;

    /// <summary><c>named</c> 的资产 id（<see cref="ExpansionMode.Named"/> 时必填、由调用方提供）。</summary>
    public string? Id { get; init; }

    /// <summary>资产声明表（模板链；用于解析父链与复制未填成员的源码）。</summary>
    public IReadOnlyDictionary<string, ComponentAsset> Declarations { get; init; } =
        new Dictionary<string, ComponentAsset>(StringComparer.Ordinal);

    /// <summary>handler DSL 装配件（<c>args</c> 里出现 <c>dsl</c> 值时必须）。</summary>
    public HandlerDslEnvironment? HandlerDsl { get; init; }

    /// <summary>DSL 相对路径 → 绝对路径（null＝不存在）。</summary>
    public Func<string, string?>? ResolveDslPath { get; init; }

    /// <summary>守卫解析（null＝不解析；带守卫的成员＝能力缺失）。</summary>
    public Func<MemberDefinition, HandlerGuardSpec?>? GuardOf { get; init; }

    /// <summary>
    /// **对局级展开表**（仅 <see cref="ExpansionMode.Inline"/>）：非 null ⇒ 成功后自动登记，
    /// 随所属 <see cref="OrchestrationRuntime"/> 丢弃（Constraint 3）。
    /// </summary>
    public InlinePrefabTable? InlineTable { get; init; }
}

/// <summary>实例化结果：形态 ＋ 两种具名度各自的身份字段。</summary>
/// <param name="Id">产物 id（<c>named</c>＝调用方给的 id；<c>inline</c>＝内部缓存键，**不对外**）。</param>
/// <param name="CacheKey"><c>inline</c> 的内部缓存键（<c>named</c>＝null；不进资产命名空间）。</param>
/// <param name="ArgsHash"><c>args</c> 的规范化哈希（<c>sha256:…</c>；用于缓存键与 origin.argsHash）。</param>
/// <param name="Expanded">展开结果。</param>
public sealed record InstantiatedPrefab(string Id, string? CacheKey, string ArgsHash, ExpandedPrefab Expanded);

/// <summary>
/// **共用展开核（R39 裁决 10／S29）**：沿 <c>Extends</c> 链合并 → 填空位 → **饱和校验** → 产出预制体类型。
/// <para>
/// 这是**唯一**的合并／填充／饱和实现：值表面（<see cref="Instantiate"/>）与声明面（摊平器）都调 <see cref="Expand"/>。
/// 「新增成员」是结构扩展，只能由声明面表达——值表面只能填空位，故 <c>args</c> 装不进结构。
/// </para>
/// <para>
/// 合并与覆写保序**原样复用** <see cref="PrefabCatalog"/>（不重写）：核把输入面构造的声明登记进目录再
/// <see cref="PrefabCatalog.Resolve"/>，因此继承环、可见性收紧、覆写授权门、声明序全部走既有实现。
/// </para>
/// <para>
/// 落点：本类型在 <c>Orc.Core.Model</c>（D46 推论）——<see cref="AssetPackLoader"/> 已在核内完成 csx 编译与
/// DSL 现场生成，故「handler 空位 → 可执行体」不需要反向依赖 <c>Orc.Game</c>。
/// </para>
/// </summary>
public static class PrefabExpander
{
    /// <summary>
    /// 展开一份声明（**声明面与值表面共用**）。
    /// </summary>
    /// <param name="request">输入。</param>
    /// <param name="errors">错误收集器（与既有摊平器同一口径：一次收全部，不提前返回）。</param>
    /// <returns>展开结果；目录/父链致命错误＝null（其余错误已登记，调用方按「有错误即失败」处理）。</returns>
    /// <exception cref="ArgumentNullException">request／errors 为 null。</exception>
    public static ExpandedPrefab? Expand(ExpandRequest request, List<PrefabLoadError> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(errors);

        var leaf = request.Declaration;
        var catalog = request.Catalog;

        // ① 沿 Extends 链合并（复用既有目录实现）：叶子登记后解析。
        try
        {
            catalog.Add(leaf.ToStructuralPrefabDefinition());
        }
        catch (Exception ex) when (ex is PrefabLoadException or ArgumentException)
        {
            errors.Add(new PrefabLoadError(
                ex is PrefabLoadException prefab ? prefab.Category : PrefabErrorCategories.Structure,
                leaf.Id,
                $"资产 '{leaf.Id}' 无法进入目录：{ex.Message}"));
            return null;
        }

        PrefabType type;
        try
        {
            type = catalog.Resolve(leaf.Id);
        }
        catch (Exception ex) when (ex is PrefabLoadException or KeyNotFoundException)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                leaf.Id,
                $"资产 '{leaf.Id}' 无法展开（{ex.Message}）。"));
            return null;
        }

        // ② 父展开结果（摊平已解析；实例化按链解析）。ActiveWhen／OneShot 沿用既有摊平口径：取父的声明。
        var parent = request.Parent;
        if (parent is null && leaf.Extends is { } parentId)
        {
            try
            {
                parent = catalog.Resolve(parentId);
            }
            catch (Exception ex) when (ex is PrefabLoadException or KeyNotFoundException)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    parentId,
                    $"父资产 '{parentId}' 无法展开（{ex.Message}）。"));
                return null;
            }
        }

        // ③ 资产级空位：**沿链合并在目录里已经做过**（`PrefabCatalog.Build` → `AssetSlotMerge`，
        //    类型沿链不可变），这里只把绑定值「再填一次」（D46：已填仍可再填）。
        var slots = ApplyAssetBindings(type.Slots, leaf.Id, request.Bindings.AssetValues, errors);

        // ④ 饱和判定（**唯一一处**）：成员层 ＋ 资产层合并成一个结论。
        var gaps = new List<SlotGap>();
        ValidateMemberSaturation(request, type, errors, gaps);
        ValidateAssetSaturation(slots, errors, gaps);
        request.AfterSaturation?.Invoke(errors);

        // ⑤ 填空位：按展开成员序逐个决定「值从哪来」（绑定值 → 声明 → 父声明复制）。
        var output = new List<ComponentMemberAsset>(type.Members.Count);
        foreach (var member in type.Members)
        {
            var filled = FillMember(member, type, parent, request, errors);
            if (filled is not null)
            {
                output.Add(filled);
            }
        }

        // ⑥ 标量值注入（R39 裁决 14／S35）：在成员填好之后、组装产物之前。
        //    **不参与饱和判定**（注入不改变 `slot ∧ ¬value` 的事实），也不新增任何 API。
        var privateFields = InjectScalarFields(
            slots,
            parent?.PrivateFields ?? type.PrivateFields,
            output,
            parent,
            leaf,
            errors);

        // ⑦ 组装产物资产（R8 第 4 条：不含 extends；产物不自动获得「可被 moding」的授权）。
        var product = new ComponentAsset(
            request.ResultId ?? leaf.Id,
            parent?.RequiresHost ?? type.RequiresHost,
            parent?.ActiveWhen ?? type.ActiveWhen,
            parent?.OneShot ?? type.OneShot,
            parent?.DisposalSignal ?? type.DisposalSignal,
            privateFields,
            output,
            type.Mounts,
            type.Wraps,
            CollectJudgeWraps(type.Chain, request.Declarations),
            extends: null,
            label: null,
            description: null,
            slots: slots,
            origin: request.Origin);

        return new ExpandedPrefab(product, type, gaps);
    }

    /// <summary>
    /// **值表面：实例化**——按 schema 校验 <c>args</c> → 调同一个 <see cref="Expand"/> → 按 mode 定身份。
    /// <para>
    /// <b>形态与 mode 无关</b>：<c>inline</c> 与 <c>named</c> 得到同一份 <see cref="ExpandedPrefab"/>，
    /// 差别只有 id 与寿命（Constraint 1）。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">request 为 null。</exception>
    /// <exception cref="PrefabLoadException">args 不合 schema／展开失败／id 非法。</exception>
    public static InstantiatedPrefab Instantiate(InstantiationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = request.Template;

        // ① schema 校验（封闭集：值只能填已声明的空位，不能新增成员）。
        var args = request.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        var schemaErrors = SlotSchema.Validate(template, args);
        if (schemaErrors.Count > 0)
        {
            throw new PrefabLoadException(schemaErrors[0].Category, schemaErrors[0].Message);
        }

        // ② 身份（裁决 9）：named 的 id 必填、由调用方提供；inline 的键内部派生。
        var argsHash = SlotArguments.Hash(template, args, request.ResolveDslPath);
        string resultId;
        string? cacheKey = null;
        if (request.Mode == ExpansionMode.Named)
        {
            resultId = RequireNamedId(request.Id, request.Declarations);
        }
        else
        {
            cacheKey = InlinePrefix + template.Id + "/" + argsHash["sha256:".Length..][..16];
            resultId = cacheKey;
        }

        // 值表面的**幂等命中**（R39 裁决 17）：同模板 ＋ 同 args ⇒ 同一个缓存键 ⇒ 直接返回已展开的形态，
        // 不重复展开（`InstantiatedPrefab` 是同一个对象，`PrefabType` 也就同一个）。
        if (cacheKey is not null
            && request.InlineTable is { } inlineTable
            && inlineTable.TryGet(cacheKey, out var cached))
        {
            return cached!;
        }

        // ③ 目录：模板自身不进目录（由核登记），父链必须在。
        var catalog = new PrefabCatalog();
        foreach (var declaration in request.Declarations.Values)
        {
            if (string.Equals(declaration.Id, template.Id, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                catalog.Add(declaration.ToStructuralPrefabDefinition());
            }
            catch (Exception ex) when (ex is PrefabLoadException or ArgumentException)
            {
                throw new PrefabLoadException(
                    ex is PrefabLoadException prefab ? prefab.Category : PrefabErrorCategories.Structure,
                    $"资产 '{declaration.Id}' 无法进入目录：{ex.Message}");
            }
        }

        var bindings = BindValues(template, args);
        var errors = new List<PrefabLoadError>();
        var expanded = Expand(
            new ExpandRequest
            {
                Catalog = catalog,
                Declaration = template,
                ResultId = resultId,
                Bindings = bindings,
                Declarations = request.Declarations,
                HandlerDsl = request.HandlerDsl,
                ResolveDslPath = request.ResolveDslPath,
                GuardOf = request.GuardOf,
                Origin = AssetOrigin.ForTemplate(template.Id, argsHash, args),
                // 值表面不传 SaturationSource：核用**模板自身的展开表**判饱和，
                // 与声明面（传父展开表）走同一段代码（S32：饱和只判一次）。
                SaturationSource = null,
            },
            errors);

        if (expanded is null || errors.Count > 0)
        {
            // 逐条保留 `Expand` 的未填空位清单（与摊平入口同一份错误、同一措辞，S32）。
            var message = errors.Count > 0
                ? string.Join("；", errors.Select(static e => e.Message))
                : "实例化失败。";
            var category = errors.Count > 0 ? errors[0].Category : PrefabErrorCategories.Structure;
            throw new PrefabLoadException(category, message);
        }

        var result = new InstantiatedPrefab(resultId, cacheKey, argsHash, expanded);
        if (request.Mode == ExpansionMode.Inline)
        {
            request.InlineTable?.Add(result);
        }

        return result;
    }

    private const string InlinePrefix = "inline/";

    /// <summary>
    /// 成员层饱和判定（**唯一一处**；R39 裁决 5／D40／S32）：判据＝<c>slot ∧ ¬handler</c>。
    /// <para>
    /// 读的是 <see cref="MemberDefinition.IsUnfilledSlot"/>——一个谓词，不再读任何手写 `abstract` 字段。
    /// <list type="bullet">
    ///   <item>父展开表里的未填空位、草案没声明 ⇒ 报「必须实现」（逐条收集，不提前返回）；</item>
    ///   <item>草案声明了但自己仍是 `slot ∧ ¬handler` ⇒ 报「重新声明仍是空位」；</item>
    ///   <item>草案声明了且填了 handler ⇒ 通过。</item>
    /// </list>
    /// </para>
    /// </summary>
    private static void ValidateMemberSaturation(
        ExpandRequest request, PrefabType type, List<PrefabLoadError> errors, List<SlotGap> gaps)
    {
        // 声明面传父展开表（只有它同时掌握「草案填了什么」与「父的成员位」）；
        // 值表面不传 ⇒ 用模板自身的展开表（模板的空洞必须由 args 填满）。
        // **两个入口都走这一段**：这是 S32「饱和只判一次」的落地形态。
        var source = request.SaturationSource ?? type;
        var declarationIsSource = string.Equals(source.Id, request.Declaration.Id, StringComparison.Ordinal);

        var declared = new Dictionary<string, ComponentMemberAsset>(StringComparer.Ordinal);
        foreach (var member in request.Declaration.Members)
        {
            declared.TryAdd(member.Name, member);
        }

        foreach (var member in source.Members)
        {
            if (!member.IsUnfilledSlot)
            {
                continue;
            }

            // 值表面：args 给了 handler ⇒ 已填（不在这里判类型，`SlotSchema.Validate` 已拒掉非 handler 值）。
            if (request.Bindings.Handlers.ContainsKey(member.Name))
            {
                continue;
            }

            if (declared.TryGetValue(member.Name, out var mine))
            {
                if (mine.Handler is not null)
                {
                    continue; // 草案填了 handler（即使没重声明 slot，也已在 Override 里继承）
                }

                if (mine.IsUnfilledSlot)
                {
                    if (declarationIsSource)
                    {
                        gaps.Add(new SlotGap(SlotScope.Member, member.Name, source.Id));
                        errors.Add(new PrefabLoadError(
                            PrefabErrorCategories.Structure,
                            $"self#{member.Name}",
                            $"成员 '{member.Name}' 是模板 '{source.Id}' 的空位（尚未填充 handler）：草案必须提供它的实现"
                            + "（成员级 slot 在摊平期强制，R31／R39 裁决 5）。"));
                    }
                    else
                    {
                        errors.Add(new PrefabLoadError(
                            PrefabErrorCategories.Structure,
                            $"self#{member.Name}",
                            $"成员 '{member.Name}' 在模板 '{source.Id}' 里是未填空位（必须实现），"
                            + "草案又把它重新声明为 slot 且没有 handler：仍然没有实现体（R31 第 3 条／R39 裁决 5）。"));
                    }
                }

                continue; // 显式声明成普通成员/纯编排成员：合法降级，沿用既有语义
            }

            gaps.Add(new SlotGap(SlotScope.Member, member.Name, source.Id));
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                $"self#{member.Name}",
                $"成员 '{member.Name}' 是模板 '{source.Id}' 的空位（尚未填充 handler）：草案必须提供它的实现"
                + "（成员级 slot 在摊平期强制，R31／R39 裁决 5）。"));
        }
    }

    private static void ValidateAssetSaturation(
        IReadOnlyList<AssetSlotDeclaration> slots, List<PrefabLoadError> errors, List<SlotGap> gaps)
    {
        foreach (var slot in slots)
        {
            if (slot.IsFilled)
            {
                continue;
            }

            gaps.Add(new SlotGap(SlotScope.Asset, slot.Name, string.Empty));
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                $"slots.{slot.Name}",
                $"资产级空位 '{slot.Name}' 未填（{SlotTypes.Format(slot.Type)}）：展开必须饱和"
                + "（未填的空位＝抽象，抽象资产不可被装配，R39 裁决 4／8）。"));
        }
    }

    private static IReadOnlyList<AssetSlotDeclaration> ApplyAssetBindings(
        IReadOnlyList<AssetSlotDeclaration> slots,
        string assetId,
        IReadOnlyDictionary<string, object?> values,
        List<PrefabLoadError> errors)
    {
        if (values.Count == 0)
        {
            return slots;
        }

        var known = new HashSet<string>(slots.Select(static s => s.Name), StringComparer.Ordinal);
        foreach (var key in values.Keys)
        {
            if (!known.Contains(key))
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"args.{key}",
                    $"args 的键 '{key}' 不是资产 '{assetId}' 的资产级空位（成员级 handler 走 args 的 handler 形态）。"));
            }
        }

        var result = new List<AssetSlotDeclaration>(slots.Count);
        foreach (var slot in slots)
        {
            if (!values.TryGetValue(slot.Name, out var value) || value is null)
            {
                result.Add(slot);
                continue;
            }

            var entry = new SlotSchemaEntry(
                slot.Name, slot.Type, SlotScope.Asset, slot.IsSealed, slot.IsFilled, slot.EnumValues);
            if (SlotSchema.ValidateValue(assetId, entry, value) is { } error)
            {
                errors.Add(error);
                result.Add(slot);
                continue;
            }

            try
            {
                result.Add(slot.WithValue(value));
            }
            catch (ArgumentException ex)
            {
                errors.Add(new PrefabLoadError(PrefabErrorCategories.Structure, $"args.{slot.Name}", ex.Message));
                result.Add(slot);
            }
        }

        return result;
    }

    /// <summary>
    /// **标量值注入**（R39 裁决 14／S35）：把已填的四类标量空位生成**同名私有字段**，
    /// 初值＝<c>args</c> 覆盖后的当前值；handler 用既有的 <c>owner.&lt;slot名&gt;</c> 读取。
    /// <para>
    /// <b>零新 API</b>：不加 <c>ctx.Slots</c>、不加模板语法——`OrchestrationEntity._state` ＋ `GetPrivateField`
    /// 已是 per-instance 只读通道，私有字段初值本来就在构造期从 <see cref="PrefabType.PrivateFields"/> 复制。
    /// </para>
    /// <para>
    /// 不注入的两类：<c>asset-ref</c>（**本轮零运行期行为**，D49／裁决 15）与 <c>handler</c>（本身就是成员实现）。
    /// 冲突拒绝（不静默覆盖）：①与既有私有字段同名且无法解释为「上一次注入的残留」；
    /// ②与任一成员的视图字段同名（R3e 口径）。
    /// </para>
    /// </summary>
    private static IReadOnlyList<PrivateFieldDefinition> InjectScalarFields(
        IReadOnlyList<AssetSlotDeclaration> slots,
        IReadOnlyList<PrivateFieldDefinition> existing,
        IReadOnlyList<ComponentMemberAsset> members,
        PrefabType? parent,
        ComponentAsset leaf,
        List<PrefabLoadError> errors)
    {
        var injectable = slots.Where(static slot => MapSlotType(slot.Type) is not null && slot.IsFilled).ToArray();
        if (injectable.Length == 0)
        {
            return existing;
        }

        var viewFields = new HashSet<string>(
            members.SelectMany(static member => member.View.Fields).Select(static field => field.Name),
            StringComparer.Ordinal);
        var result = new List<PrivateFieldDefinition>(existing);

        foreach (var slot in injectable)
        {
            var mapped = MapSlotType(slot.Type)!.Value;

            if (viewFields.Contains(slot.Name))
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"slots.{slot.Name}",
                    $"空位 '{slot.Name}' 与某个成员的视图字段同名：注入的私有字段与视图字段不得同名"
                    + "（公共变量与私有变量名字空间必须分离，R3e／S35）。"));
                continue;
            }

            var index = result.FindIndex(field => string.Equals(field.Name, slot.Name, StringComparison.Ordinal));
            if (index < 0)
            {
                result.Add(new PrivateFieldDefinition(slot.Name, mapped, slot.Value));
                continue;
            }

            // 同名私有字段：**只有**「父链已声明同名 slot」（＝上一轮注入的残留）才算可替换；
            // 父链没有该 slot 却是作者手写字段 ⇒ 冲突（S35 ①：不静默覆盖）。
            var residue = parent is null || parent.Slots.Any(s => string.Equals(s.Name, slot.Name, StringComparison.Ordinal));
            if (!residue || result[index].Type != mapped)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"slots.{slot.Name}",
                    $"空位 '{slot.Name}' 与既有私有字段 '{result[index].Name}' 冲突"
                    + $"（字段类型 {ViewFieldTypes.Format(result[index].Type)}，空位需要 {ViewFieldTypes.Format(mapped)}）："
                    + "注入不覆盖作者手写的私有字段，请改名或删除其一（S35 ①）。"));
                continue;
            }

            // 再填的幂等：同一 slot 恒一条，初值换成新值（D46「再填一次」）。
            result[index] = new PrivateFieldDefinition(slot.Name, mapped, slot.Value);
        }

        return result;
    }

    /// <summary>
    /// 空位类型 → 私有字段类型（裁决 14：用 <see cref="ViewFieldType"/> 既有封闭集，不为 slot 新增类型）。
    /// <para><c>enum</c> → <c>String</c>（存枚举文本名）是推论：封闭集里没有 enum 专用类型，
    /// 且与 <c>argsHash</c>「enum 按字符串」同口径。</para>
    /// <para>internal 供存档读面（<see cref="SaveContext.ReadPrivateFields"/>）复用：注入规则与
    /// 「哪些空位会以私有字段形态出现在展开产物里」必须是同一份映射，不能两套各写一遍。</para>
    /// </summary>
    internal static ViewFieldType? MapSlotType(SlotType type) => type switch
    {
        SlotType.Number => ViewFieldType.Number,
        SlotType.String => ViewFieldType.String,
        SlotType.Bool => ViewFieldType.Boolean,
        SlotType.Enum => ViewFieldType.String,
        _ => null,
    };

    /// <summary>
    /// 填一个成员（**填充动作只有这一份**）：
    /// 绑定值（值表面）→ 本层声明（声明面）→ 父声明复制（既有的三类来源分流）。
    /// </summary>
    private static ComponentMemberAsset? FillMember(
        MemberDefinition member,
        PrefabType type,
        PrefabType? parent,
        ExpandRequest request,
        List<PrefabLoadError> errors)
    {
        var address = $"self#{member.Name}";
        var declared = request.Declaration.Members.FirstOrDefault(
            m => string.Equals(m.Name, member.Name, StringComparison.Ordinal));

        // 值表面：args 里给了 handler 值 ⇒ 它覆盖声明期的已填值（D46：已填但可再填）。
        if (request.Bindings.Handlers.TryGetValue(member.Name, out var argument))
        {
            var guardSource = parent?.FindMember(member.Name) ?? (parent is null ? type.FindMember(member.Name) : null);
            var guard = guardSource is null ? null : request.GuardOf?.Invoke(guardSource);
            var baseMember = (declared ?? new ComponentMemberAsset(
                member.Name, member.Visibility, member.View, null, member.Band, member.Bands, member.Seams, member.Sealed))
                with { Handler = HandlerFromArgument(argument), Slot = member.Slot ?? declared?.Slot };
            return BuildHandler(baseMember, address, request, guard, errors);
        }

        // 声明面：本层声明优先（草案重载／新增成员）。
        if (declared is not null)
        {
            var guardSource = parent?.FindMember(member.Name);
            var guard = guardSource is null ? null : request.GuardOf?.Invoke(guardSource);
            if (guard is not null)
            {
                ValidateGuardFields(declared, address, guard, errors);
            }

            // **slot 沿链继承**（D39：填一次不消灭空位）：草案只给 handler，产物仍要带 slot 声明，
            // 否则下一层草案无法再填。取合并后的 MemberDefinition.Slot（子显式声明时以子为准）。
            return BuildHandler(declared with { Slot = member.Slot ?? declared.Slot }, address, request, guard, errors);
        }

        // 继承成员：按既有的三类来源分流（复制父声明，不重新生成已生成物）。
        var source = FindDeclaration(request.Declarations, member);
        if (source?.Handler is not { } handler)
        {
            return new ComponentMemberAsset(
                member.Name, member.Visibility, member.View, null,
                member.Band, member.Bands, member.Seams, member.Sealed, Slot: member.Slot);
        }

        if (handler.Generated is not null)
        {
            return new ComponentMemberAsset(
                member.Name, source.Visibility, source.View, handler,
                source.Band, source.Bands, source.Seams, source.Sealed, Slot: member.Slot);
        }

        if (handler.Script is null)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                address,
                $"父成员 '{member.Name}' 的 handler '{handler.Id}' 既无 csx 文本也无来源标记（只有 assembly-key）："
                + "摊平只能复制文本或生成文本，程序集处理器不在此列（R28）。"));
            return null;
        }

        return new ComponentMemberAsset(
            member.Name,
            source.Visibility,
            source.View,
            handler with { Id = HandlerContent.AddressOf(handler.Script, generated: false), Generated = null },
            source.Band,
            source.Bands,
            source.Seams,
            source.Sealed,
            Slot: member.Slot);
    }

    private static ComponentHandlerAsset HandlerFromArgument(HandlerSlotArgument argument) => argument.Kind switch
    {
        HandlerSlotKind.Csx => new ComponentHandlerAsset(
            HandlerContent.AddressOf(argument.Text, generated: false), "HandleAsync", argument.Text, AssemblyKey: null),
        HandlerSlotKind.Dsl => new ComponentHandlerAsset(
            argument.Text, "HandleAsync", Script: null, AssemblyKey: null, Dsl: argument.Text),
        _ => new ComponentHandlerAsset(argument.Text, "HandleAsync", Script: null, AssemblyKey: argument.Text),
    };

    /// <summary>
    /// 成员值的构造（原摊平器的 <c>BuildFromDeclaration</c>，逐字搬入核）：
    /// <c>dsl</c> → 现场生成 csx ＋ 来源标记；<c>script</c> → 原样取文本 ＋ 内容寻址；
    /// 没有 handler → 纯编排成员（合法降级）。
    /// </summary>
    private static ComponentMemberAsset? BuildHandler(
        ComponentMemberAsset declared,
        string memberAddress,
        ExpandRequest request,
        HandlerGuardSpec? guard,
        List<PrefabLoadError> errors)
    {
        if (declared.Handler is not { } handler)
        {
            return new ComponentMemberAsset(
                declared.Name, declared.Visibility, declared.View, null,
                declared.Band, declared.Bands, declared.Seams, declared.Sealed, Slot: declared.Slot);
        }

        if (handler.Dsl is { } dslRelative)
        {
            var dsl = request.HandlerDsl;
            if (dsl is null)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Capability,
                    memberAddress,
                    $"成员 '{declared.Name}' 指向 DSL 文档 '{dslRelative}'，但本环境未装配 handler DSL 编译器："
                    + "摊平需要生成 csx（请装配 engine.HandlerDsl）。"));
                return null;
            }

            var dslPath = request.ResolveDslPath?.Invoke(dslRelative);
            if (dslPath is null || !File.Exists(dslPath))
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    memberAddress,
                    $"成员 '{declared.Name}' 引用的 DSL 文档 '{dslRelative}' 不存在。"));
                return null;
            }

            var dslText = File.ReadAllText(dslPath);

            var guardedCompiler = guard is null ? null : dsl.Compiler as IHandlerDslGuardedCompiler;
            if (guard is not null && guardedCompiler is null)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Capability,
                    memberAddress,
                    $"成员 '{declared.Name}' 的父模板要求守卫（{HandlerGuardNames.Format(guard.Guard)}），"
                    + "但本环境的 handler DSL 编译器不支持守卫合成（未实现 IHandlerDslGuardedCompiler）："
                    + "守卫不可省略——请装配实现了该接口的编译器（Orc.HandlerDsl）。"));
                return null;
            }

            HandlerDslCompileResult compiled;
            try
            {
                var view = HandlerDslView.FromViewSpec(declared.View);
                compiled = guardedCompiler is null
                    ? dsl.Compiler.Compile(dslText, dsl.Catalog, view)
                    : guardedCompiler.CompileGuarded(dslText, dsl.Catalog, view, guard!);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure, memberAddress, $"成员 '{declared.Name}' 的 DSL 编译抛出异常：{ex.Message}"));
                return null;
            }

            if (!compiled.Success || compiled.Csx is null)
            {
                foreach (var error in compiled.Errors)
                {
                    errors.Add(new PrefabLoadError(
                        string.Equals(error.Code, "write-forbidden", StringComparison.Ordinal)
                            ? PrefabErrorCategories.View
                            : PrefabErrorCategories.Structure,
                        memberAddress,
                        $"成员 '{declared.Name}' 的 handler DSL 校验失败：{error.Message}",
                        MissingFields: MissingFieldsOf(error),
                        Code: error.Code,
                        NodePath: error.NodePath));
                }

                if (compiled.Errors.Count == 0)
                {
                    errors.Add(new PrefabLoadError(
                        PrefabErrorCategories.Structure,
                        memberAddress,
                        $"成员 '{declared.Name}' 的 DSL 编译失败但未给出错误（编译器实现违约）。"));
                }

                return null;
            }

            var csx = compiled.Csx;
            return new ComponentMemberAsset(
                declared.Name,
                declared.Visibility,
                declared.View,
                new ComponentHandlerAsset(
                    HandlerContent.AddressOf(csx, generated: true),
                    string.IsNullOrWhiteSpace(handler.Entry) ? "HandleAsync" : handler.Entry,
                    csx,
                    AssemblyKey: null,
                    Dsl: null,
                    DslHash: null)
                {
                    Generated = new HandlerGeneratedBlock(
                        HandlerGeneratedBlock.CurrentGenerator,
                        dsl.Catalog.Revision,
                        dslRelative,
                        HandlerContent.Hash(dslText),
                        HandlerContent.Hash(csx),
                        NodeMap: null),
                },
                declared.Band,
                declared.Bands,
                declared.Seams,
                declared.Sealed,
                Slot: declared.Slot);
        }

        if (handler.Script is { } script)
        {
            if (guard is not null)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    memberAddress,
                    $"成员 '{declared.Name}' 的父模板要求守卫（{HandlerGuardNames.Format(guard.Guard)}），"
                    + "但草案给的是手写 csx：守卫只能由生成器合成（作者不可编辑），请改用手写 DSL 表达效果体。"));
                return null;
            }

            return new ComponentMemberAsset(
                declared.Name,
                declared.Visibility,
                declared.View,
                new ComponentHandlerAsset(
                    HandlerContent.AddressOf(script, generated: false),
                    string.IsNullOrWhiteSpace(handler.Entry) ? "HandleAsync" : handler.Entry,
                    script,
                    AssemblyKey: null),
                declared.Band,
                declared.Bands,
                declared.Seams,
                declared.Sealed,
                Slot: declared.Slot);
        }

        // 值表面的 assembly 形态：直接引用程序集键（装载链对「只有 assembly-key」的资产仍按既有口径拒绝）。
        if (handler.AssemblyKey is { } assemblyKey)
        {
            return new ComponentMemberAsset(
                declared.Name,
                declared.Visibility,
                declared.View,
                new ComponentHandlerAsset(assemblyKey, "HandleAsync", null, assemblyKey),
                declared.Band,
                declared.Bands,
                declared.Seams,
                declared.Sealed,
                Slot: declared.Slot);
        }

        errors.Add(new PrefabLoadError(
            PrefabErrorCategories.Structure,
            memberAddress,
            $"成员 '{declared.Name}' 的 handler '{handler.Id}' 只有 assembly-key：摊平只能复制或生成 csx 文本（R28）。"));
        return null;
    }

    /// <summary>
    /// 守卫要读的载荷字段必须出现在重载后的视图里（R32）：视图整段替换（R3a），
    /// 作者若把守卫比较用的字段删掉，守卫代码会在运行期取不到字段——在展开期就拒绝。
    /// </summary>
    private static void ValidateGuardFields(
        ComponentMemberAsset declared, string memberAddress, HandlerGuardSpec guard, List<PrefabLoadError> errors)
    {
        var field = guard.Guard == HandlerGuard.Self ? guard.SubjectField : guard.PlayerField;
        if (declared.View.Fields.Any(f => string.Equals(f.Name, field, StringComparison.Ordinal)))
        {
            return;
        }

        errors.Add(new PrefabLoadError(
            PrefabErrorCategories.View,
            memberAddress,
            $"成员 '{declared.Name}' 的视图被草案重载后缺少字段 '{field}'：模板守卫（{HandlerGuardNames.Format(guard.Guard)}）"
            + "要用它做比较，删掉它会让守卫在运行期取不到载荷字段（R32）。"));
    }

    private static ComponentMemberAsset? FindDeclaration(
        IReadOnlyDictionary<string, ComponentAsset> declarations, MemberDefinition member)
    {
        if (member.DeclaredBy is null || !declarations.TryGetValue(member.DeclaredBy, out var asset))
        {
            return null;
        }

        return asset.Members.FirstOrDefault(m => string.Equals(m.Name, member.Name, StringComparison.Ordinal));
    }

    private static IReadOnlyList<ComponentJudgeWrapAsset> CollectJudgeWraps(
        IReadOnlyList<string> chain, IReadOnlyDictionary<string, ComponentAsset> declarations)
    {
        var wraps = new List<ComponentJudgeWrapAsset>();
        foreach (var id in chain)
        {
            if (declarations.TryGetValue(id, out var asset))
            {
                wraps.AddRange(asset.JudgeWraps);
            }
        }

        return wraps;
    }

    private static SlotBindings BindValues(
        ComponentAsset template, IReadOnlyDictionary<string, object?> args)
    {
        var schema = SlotSchema.SchemaOf(template);
        var assetValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        var handlers = new Dictionary<string, HandlerSlotArgument>(StringComparer.Ordinal);

        foreach (var entry in schema)
        {
            if (!args.TryGetValue(entry.Name, out var value) || value is null)
            {
                continue;
            }

            if (entry.Scope == SlotScope.Asset)
            {
                assetValues[entry.Name] = value;
            }
            else if (value is HandlerSlotArgument argument)
            {
                handlers[entry.Name] = argument;
            }
        }

        return new SlotBindings(assetValues, handlers);
    }

    private static string RequireNamedId(
        string? id, IReadOnlyDictionary<string, ComponentAsset> declarations)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                "named 模式必须由调用方提供资产 id（不从模板派生；确定性由 origin.argsHash 承担，R39 裁决 9）。");
        }

        var trimmed = id.Trim();
        if (AssetSchema.IsTemplateId(trimmed))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"named 实例的 id '{trimmed}' 使用了保留前缀 '{AssetSchema.TemplateIdPrefix}'："
                + "该命名空间归模板库专用（派生实例是普通组件资产，R39 裁决 9）。");
        }

        if (trimmed.IndexOfAny(new[] { '#', ':', '/', ' ', '\t' }) >= 0)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"named 实例的 id '{trimmed}' 含非法字符（不得含空白、'#'（版本分隔符）、':'（种类分隔符）、'/'）："
                + "id 必须是合法资产名（R39 裁决 9）。");
        }

        if (declarations.ContainsKey(trimmed))
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"named 实例的 id '{trimmed}' 与现有资产重名：两处都拒绝同 id、不覆盖"
                + "（Instantiate 时早失败，Save 时再查一次，R39 裁决 13）。");
        }

        return trimmed;
    }

    /// <summary>缺失引脚名（与装载器同一口径：只有 <c>pin-missing</c> 能无歧义从节点路径末段取到）。</summary>
    private static IReadOnlyList<string>? MissingFieldsOf(HandlerDslError error)
    {
        if (!string.Equals(error.Code, "pin-missing", StringComparison.Ordinal))
        {
            return null;
        }

        var dot = error.NodePath.LastIndexOf('.');
        var name = dot >= 0 && dot + 1 < error.NodePath.Length ? error.NodePath[(dot + 1)..] : error.NodePath;
        return string.IsNullOrEmpty(name) ? null : new[] { name };
    }
}

/// <summary>
/// <c>args</c> 的**规范化哈希**（D42 NOTE 2／D43 NOTE 3／D45）：按键名排序后序列化；
/// <c>enum</c> 按字符串、<c>asset-ref</c> 按**应用层 ID**、<c>number</c> 按 invariant culture、
/// <c>handler</c> 按**既有**源标识（csx 地址派生／dsl 内容哈希／assembly 键）。
/// <para>
/// 用途只有两处：<c>inline</c> 缓存键与 <c>origin.argsHash</c>——**不承担 id 派生**。
/// 守卫不参与（由模板决定、核内合成）。
/// </para>
/// </summary>
public static class SlotArguments
{
    /// <summary>计算规范化哈希（<c>sha256:&lt;64 hex&gt;</c>）。</summary>
    /// <param name="asset">提供 schema（决定每个值的类型解释）。</param>
    /// <param name="args">扁平参数表。</param>
    /// <param name="resolveDslPath">dsl 值的路径解析（null＝不解析，按路径文本参与哈希）。</param>
    /// <exception cref="ArgumentNullException">asset／args 为 null。</exception>
    /// <exception cref="PrefabLoadException">值形态不受支持，或 dsl 文档不可读。</exception>
    public static string Hash(
        ComponentAsset asset,
        IReadOnlyDictionary<string, object?> args,
        Func<string, string?>? resolveDslPath = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(args);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            foreach (var entry in SlotSchema.SchemaOf(asset).OrderBy(static e => e.Name, StringComparer.Ordinal))
            {
                args.TryGetValue(entry.Name, out var value);
                writer.WritePropertyName(entry.Name);
                WriteToken(writer, entry, value, resolveDslPath);
            }

            writer.WriteEndObject();
        }

        return HandlerContent.Hash(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteToken(
        Utf8JsonWriter writer,
        SlotSchemaEntry entry,
        object? value,
        Func<string, string?>? resolveDslPath)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (entry.Type)
        {
            case SlotType.Number when value is IConvertible convertible:
                writer.WriteNumberValue(convertible.ToDecimal(CultureInfo.InvariantCulture));
                break;
            case SlotType.String or SlotType.Enum or SlotType.AssetRef when value is string text:
                writer.WriteStringValue(text);
                break;
            case SlotType.Bool when value is bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case SlotType.Handler when value is HandlerSlotArgument argument:
                writer.WriteStringValue(HandlerToken(argument, resolveDslPath));
                break;
            default:
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"args.{entry.Name} 的值类型 {value.GetType().Name} 不符合声明类型 {SlotTypes.Format(entry.Type)}。");
        }
    }

    private static string HandlerToken(HandlerSlotArgument argument, Func<string, string?>? resolveDslPath)
    {
        switch (argument.Kind)
        {
            case HandlerSlotKind.Csx:
                // 「csx 用 HandlerContent 的既有文本规范化 ＋ 地址派生」——不自造第二套规范化。
                return HandlerContent.AddressOf(argument.Text, generated: false);
            case HandlerSlotKind.Assembly:
                return argument.Text;
            default:
                var path = resolveDslPath?.Invoke(argument.Text);
                if (path is null || !File.Exists(path))
                {
                    if (resolveDslPath is null)
                    {
                        // 无解析器（纯哈希场景）：按路径文本参与，仍保持确定性。
                        return "dsl:" + argument.Text;
                    }

                    throw new PrefabLoadException(
                        PrefabErrorCategories.Structure,
                        $"args 引用的 DSL 文档 '{argument.Text}' 不存在：无法取既有 dslHash（不另造哈希口径）。");
                }

                // 「dsl 用既有 dslHash」＝文档内容的内容哈希（与 generated.dslHash 同口径）。
                return HandlerContent.Hash(File.ReadAllText(path));
        }
    }
}
