using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;

namespace Orc.Packages.Editing;

/// <summary>摊平失败/检查差异的原因（**同一套「草案已陈旧」机制**，D11：不新造第二套）。</summary>
public enum ModingStaleReason
{
    /// <summary>草案记录的 <c>dslHash</c> 与 DSL 文件当前内容不符（文案「DSL 已变更，请重新摊平」）。</summary>
    DslChanged = 0,

    /// <summary>父新增了成员而草案未同步（R8 第 8 条：不静默按旧快照产出）。</summary>
    ParentAhead = 1,

    /// <summary>草案本身读不出来（语法／结构错误）——同样按「不一致」拒绝打包。</summary>
    DraftUnreadable = 2,
}

/// <summary>一条陈旧/差异项（`flatten --check` 逐项列出，不只报「不一致」）。</summary>
/// <param name="Reason">原因。</param>
/// <param name="Target">出错对象（成员名／DSL 路径／产物成员）。</param>
/// <param name="Expected">期望值（草案记录的哈希／产物里应有的成员）。</param>
/// <param name="Actual">实际值（文件当前哈希／父里的成员）。</param>
/// <param name="Message">人类可读文案。</param>
public sealed record ModingStaleItem(
    ModingStaleReason Reason,
    string Target,
    string Expected,
    string Actual,
    string Message);

/// <summary>`flatten --check` 的结果：一致＝<see cref="UpToDate"/>，否则逐项列出差异（D11）。</summary>
/// <param name="UpToDate">草案与产物是否一致。</param>
/// <param name="Items">差异项（一致＝空）。</param>
public sealed record ModingFlattenCheckResult(bool UpToDate, IReadOnlyList<ModingStaleItem> Items)
{
    /// <summary>差异项的展示文本（「拒绝打包」时的原因清单）。</summary>
    public string Describe()
        => UpToDate
            ? "草案与产物一致。"
            : "草案与产物不一致（拒绝打包）：" + string.Join(
                "；", Items.Select(static i => $"[{i.Reason}] {i.Target}：{i.Message}"));
}

/// <summary>
/// 摊平结果（R8／S11）：**纯函数产出**（csx 文本，不含 DSL 节点结构，R28 第 1 条）。
/// <para>
/// <see cref="ProductJson"/> 是**自包含的组件资产**文本（`*.component.json` 的形态）：
/// 无 <c>extends</c>、不引用父资产的任何 handler id（R8 第 4 条），装载时**不要求父存在**（R8 第 1 条）。
/// </para>
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="ProductId">产物资产 id（稳定：二次摊平不变，R8 第 5 条）。</param>
/// <param name="ProductRelativePath">产物在包内的相对路径（<c>effects-v4/&lt;id&gt;.component.json</c>）。</param>
/// <param name="ProductJson">产物文本（失败＝null）。</param>
/// <param name="Errors">失败项（成功＝空）。</param>
public sealed record ModingFlattenResult(
    bool Success,
    string? ProductId,
    string? ProductRelativePath,
    string? ProductJson,
    IReadOnlyList<PrefabLoadError> Errors)
{
    /// <summary>失败结果的展示文本。</summary>
    public string Describe()
        => Success
            ? $"摊平成功：{ProductRelativePath}。"
            : "摊平失败：" + string.Join(
                "；", Errors.Select(static e => $"[{e.Category}] {e.Target}：{e.Message}"));
}

/// <summary>
/// **摊平器**（R8／R28／S11）：把 moding 草案 ＋ 父资产摊平成**自包含**的组件资产文本。
/// <para>
/// 成员来源三分（R28 已裁决，按来源判定而不是按「是否需要生成」判定）：
/// <list type="number">
///   <item><b>未修改且父有 csx 原文</b> → 原样复制文本，**不带**来源标记；handler id 按 <c>csx/v1/&lt;hash&gt;</c>
///     重新派生（这样「产物不引用父的任何 id」才字面成立）。</item>
///   <item><b>由 DSL 生成</b>（重载或新增）→ 生成文本 ＋ <c>generated</c> 块；id 按 <c>hdsl/v1/&lt;hash&gt;</c>。</item>
///   <item><b>未修改但父本身就是生成物</b> → 原样复制父的**已生成文本与 generated 块**，**不重新生成**；
///     **沿用父的 id**（文本与内容寻址结果本就一致，改 id 反而会破坏「摊平的摊平不重新生成」）。</item>
/// </list>
/// </para>
/// <para>
/// **生成只发生在这里与草案装载两处**（R28）：产物装载路径不调用生成器，引擎升级不改变已发布产物的行为。
/// 因此第 ③ 类**必须**只复制——重生成会把 R28 想挡掉的跨版本漂移从后门放回来。
/// </para>
/// <para>
/// **lock／版本对 DSL 完全无感（有意，D11）**：改 DSL 不 bump 任何资产版本、不重算 lock。
/// 本类因此只产出文本；版本递增由编辑会话在提交产物时按 <see cref="EditorVersionRules"/> 处理。
/// </para>
/// </summary>
public static class ModingFlattener
{
    /// <summary>产物所在目录（与生产装载链同一约定：<c>effects-v4/*.component.json</c>）。</summary>
    public const string ProductDirectory = "effects-v4";

    /// <summary>草案所在目录。</summary>
    public const string DraftDirectory = PrefabDescriber.ModingsDirectory;

    /// <summary>草案文件后缀。</summary>
    public const string DraftSuffix = PrefabDescriber.ModingFileSuffix;

    /// <summary>
    /// 摊平一份草案（**不写盘**）：成功返回产物文本，失败返回逐条错误（整包原子：失败不产出半成品）。
    /// </summary>
    /// <param name="packRoot">包根目录。</param>
    /// <param name="draftPath">草案路径（相对包根或绝对）。</param>
    /// <param name="dsl">
    /// handler DSL 装配件（编译器 ＋ 目录）。**含 DSL 成员时必需**：null＝能力缺失
    /// （分类 <see cref="PrefabErrorCategories.Capability"/>，与草案装载期同一口径）。
    /// </param>
    /// <param name="templateRoot">
    /// **模板库根路径**（R33／D29：编辑期只读依赖层，可空）。合并进目录后**只允许** <c>extends</c> 到
    /// <c>abstract: true</c> 的资产——指向普通资产仍按「跨包」拒绝，包依赖机制一点都没引入。
    /// null／空白＝模板不可用（行为与今天一致，R33 第 5 条）。
    /// </param>
    public static ModingFlattenResult Flatten(
        string packRoot, string draftPath, HandlerDslEnvironment? dsl = null, string? templateRoot = null)
    {
        ArgumentNullException.ThrowIfNull(packRoot);
        ArgumentNullException.ThrowIfNull(draftPath);

        var errors = new List<PrefabLoadError>();
        var root = Path.GetFullPath(packRoot);
        var draftAbsolute = ResolvePath(root, draftPath);

        var draft = ReadAsset(draftAbsolute, draftPath, errors);
        if (draft is null)
        {
            return new ModingFlattenResult(false, null, null, null, errors);
        }

        if (draft.Extends is null)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                draft.Id,
                "草案缺少 extends（父效果 id）：摊平必须有父才能复制未修改成员（R1／R8）。"));
            return new ModingFlattenResult(false, null, null, null, errors);
        }

        var productId = ResolveProductId(draftAbsolute, draft, errors);
        if (productId is null)
        {
            return new ModingFlattenResult(false, null, null, null, errors);
        }

        var assets = LoadPackAssets(root, errors);

        // 只读层（R33）：模板库根合并进目录。**只并进目录**，不并进「本包声明」（FindDeclaration 仍以本包为准，
        // 否则产物会复制出模板库的 handler 文本而绕过摊平来源分流）。
        var templateAssets = LoadTemplateAssets(templateRoot, errors);
        foreach (var pair in templateAssets)
        {
            assets.TryAdd(pair.Key, pair.Value);
        }

        // 跨包继承的口子只开在模板上（R33 第 4 条）：父只在模板层里且不是 abstract ⇒ 拒绝。
        if (templateAssets.ContainsKey(draft.Extends) && !templateAssets[draft.Extends].Abstract)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                draft.Extends,
                $"父资产 '{draft.Extends}' 来自模板库但不是抽象资产（abstract: false）："
                + "跨包继承仅限模板（只声明、不实现的效果模板）——指向普通资产仍按跨包拒绝（R33 第 4 条）。"));
            return new ModingFlattenResult(false, productId, null, null, errors);
        }

        var catalog = BuildCatalog(assets, errors);

        PrefabType parent;
        try
        {
            parent = catalog.Resolve(draft.Extends);
        }
        catch (Exception ex) when (ex is PrefabLoadException or KeyNotFoundException)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                draft.Extends,
                $"父资产 '{draft.Extends}' 无法展开（{ex.Message}）：摊平需要父的展开成员表（R8）。"
                + (templateRoot is null ? "（若父是模板，请配置模板库根路径。）" : string.Empty)));
            return new ModingFlattenResult(false, productId, null, null, errors);
        }

        var draftMembers = new Dictionary<string, ComponentMemberAsset>(StringComparer.Ordinal);
        foreach (var declaredMember in draft.Members)
        {
            if (!draftMembers.TryAdd(declaredMember.Name, declaredMember))
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"self#{declaredMember.Name}",
                    $"草案重复声明成员 '{declaredMember.Name}'（成员名必须唯一）。"));
            }
        }

        // ── R31：成员级抽象的**唯一**强制点（摊平期）───────────────────────────
        // 一次性收集**全部**缺失成员再报（不立即返回）：作者一次就能看到要补哪些。
        ValidateAbstractMembers(parent, draftMembers, errors);

        // ── R32／D24：模板声明的 requires-host 校验（多抽象成员取交集）────────────
        ValidateTemplateRequirements(parent, errors);

        var output = new List<ComponentMemberAsset>(parent.Members.Count + draftMembers.Count);

        // ① ② ③：父展开表里的每个成员按来源分流（顺序＝父的成员表序，摊平确定性的一部分）。
        foreach (var member in parent.Members)
        {
            if (draftMembers.TryGetValue(member.Name, out var overridden))
            {
                // ② 草案重载 → 生成或原样取草案的 csx。守卫档从**父展开表**读（作者看不见也改不到，R32）。
                var guard = ResolveGuardSpec(parent, member, errors);
                if (guard is not null)
                {
                    ValidateGuardFields(overridden, $"self#{member.Name}", guard, errors);
                }

                var built = BuildFromDeclaration(
                    root, overridden, memberAddress: $"self#{member.Name}", dsl, errors, inherited: null, guard);
                if (built is not null)
                {
                    output.Add(built);
                }

                continue;
            }

            var declared = FindDeclaration(assets, member);
            if (declared?.Handler is not { } handler)
            {
                // 纯编排成员（没有 handler）：原样复制声明，无来源标记、无 handler id 重写问题。
                output.Add(new ComponentMemberAsset(
                    member.Name, member.Visibility, member.View, null, member.Band, member.Bands, member.Seams, member.Sealed));
                continue;
            }

            if (handler.Generated is not null)
            {
                // ③ 父已是生成物 → **逐字复制**父的已生成文本与 generated 块，**沿用父的 id**，**不重新生成**。
                // 这是硬要求：重生成会把 R28 想挡掉的跨版本漂移从后门放回来。
                // 守卫已在父的 csx 里（产物不带 guard 字段）——所以这里不重新合成，否则会重复加守卫（D24 推论）。
                output.Add(new ComponentMemberAsset(
                    member.Name,
                    declared!.Visibility,
                    declared.View,
                    handler,
                    declared.Band,
                    declared.Bands,
                    declared.Seams,
                    declared.Sealed));
                continue;
            }

            if (handler.Script is null)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"self#{member.Name}",
                    $"父成员 '{member.Name}' 的 handler '{handler.Id}' 既无 csx 文本也无来源标记（只有 assembly-key）："
                    + "摊平只能复制文本或生成文本，程序集处理器不在此列（R28）。"));
                continue;
            }

            // ① 未修改且父有 csx 原文 → 原样复制文本，不带来源标记；id 重新按内容派生（csx/v1/…）。
            output.Add(new ComponentMemberAsset(
                member.Name,
                declared!.Visibility,
                declared.View,
                handler with { Id = HandlerContent.AddressOf(handler.Script, generated: false), Generated = null },
                declared.Band,
                declared.Bands,
                declared.Seams,
                declared.Sealed));
        }

        // 草案新增的成员（父里没有）：② 语义——必须自带 handler 来源，生成或取 csx。
        foreach (var declared in draft.Members)
        {
            if (parent.FindMember(declared.Name) is not null)
            {
                continue;
            }

            var built = BuildFromDeclaration(
                root, declared, memberAddress: $"self#{declared.Name}", dsl, errors, inherited: null, guard: null);
            if (built is not null)
            {
                output.Add(built);
            }
        }

        if (errors.Count > 0)
        {
            return new ModingFlattenResult(false, productId, null, null, errors);
        }

        // 挂载与包装：父的 ＋ 草案的（草案只存增量，产物必须自包含全部行为）。
        var mounts = parent.Mounts.Concat(draft.Mounts).ToArray();
        var wraps = parent.Wraps.Concat(draft.Wraps).ToArray();
        // 判定器包装声明也随继承链继承（它们并入同一 Wraps 集合，S2a）：沿父链逐层收集＋草案的增量。
        var judgeWraps = parent.Chain
            .Select(id => assets.TryGetValue(id, out var ancestor) ? ancestor.JudgeWraps : null)
            .Where(static list => list is { Count: > 0 })
            .SelectMany(static list => list!)
            .Concat(draft.JudgeWraps)
            .ToArray();

        var product = new ComponentAsset(
            productId,
            parent.RequiresHost,
            parent.ActiveWhen,
            parent.OneShot,
            parent.DisposalSignal,
            parent.PrivateFields,
            output,
            mounts,
            wraps,
            judgeWraps,
            // R8 第 4 条：产物**不含 extends**（自包含，装载不要求父存在）。
            extends: null,
            // R2 的缺省：产物不自动继承「可被 moding」的授权，作者要开放就显式打开。
            authorizesModing: false);

        var json = AssetSchema.Write(product);
        return new ModingFlattenResult(
            true, productId, ProductDirectory + "/" + productId + ".component.json", json, Array.Empty<PrefabLoadError>());
    }

    /// <summary>
    /// 摊平并**原子写入**：先写同目录临时文件再 <see cref="File.Move(string, string, bool)"/> 覆盖。
    /// 失败（含生成失败）＝一个字节都不写，既有版本保持完好可装载（R8 第 10 条）。
    /// </summary>
    /// <returns>摊平结果；`Success=false` 时磁盘未被触碰。</returns>
    public static ModingFlattenResult FlattenAndWrite(
        string packRoot, string draftPath, HandlerDslEnvironment? dsl = null, string? templateRoot = null)
    {
        var result = Flatten(packRoot, draftPath, dsl, templateRoot);
        if (!result.Success || result.ProductJson is null || result.ProductRelativePath is null)
        {
            return result;
        }

        var root = Path.GetFullPath(packRoot);
        var target = Path.Combine(root, result.ProductRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);

        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, result.ProductJson, Utf8NoBom);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return result;
    }

    /// <summary>
    /// **`flatten --check`**（D11／R8 第 9 条）：遍历草案的成员引用，逐个比对 <c>dslHash</c> 与文件当前哈希；
    /// 另比对产物成员集与父的展开成员集（「草案落后于父」）。
    /// <para>
    /// **不引入依赖图、不新增错误码**：它就是「草案已陈旧」的同一套判定，两种原因共用一个差异项模型。
    /// 与 <c>lock</c> 无关——DSL 与草案都不进正式 lock。
    /// </para>
    /// </summary>
    public static ModingFlattenCheckResult Check(string packRoot, string draftPath)
    {
        ArgumentNullException.ThrowIfNull(packRoot);
        ArgumentNullException.ThrowIfNull(draftPath);

        var root = Path.GetFullPath(packRoot);
        var items = new List<ModingStaleItem>();
        var errors = new List<PrefabLoadError>();

        var draft = ReadAsset(ResolvePath(root, draftPath), draftPath, errors);
        if (draft is null)
        {
            return new ModingFlattenCheckResult(false, errors
                .Select(e => new ModingStaleItem(
                    ModingStaleReason.DraftUnreadable, draftPath, string.Empty, string.Empty, e.Message))
                .ToArray());
        }

        var assets = LoadPackAssets(root, errors);
        var productId = ResolveProductId(ResolvePath(root, draftPath), draft, new List<PrefabLoadError>());
        var productAsset = productId is not null && assets.TryGetValue(productId, out var found) ? found : null;

        // ① 逐个成员引用比对 dslHash（「DSL 已变更，请重新摊平」）。
        foreach (var member in draft.Members)
        {
            if (member.Handler is not { Dsl: { } dslRelative } handler)
            {
                continue;
            }

            // 与 Flatten 同一基准（先产物目录、再包根）：否则 check 会把「DSL 与组件同目录」误报成文件不存在。
            var dslPath = ResolvePath(Path.Combine(root, ProductDirectory), dslRelative);
            if (!File.Exists(dslPath))
            {
                dslPath = ResolvePath(root, dslRelative);
            }

            if (!File.Exists(dslPath))
            {
                items.Add(new ModingStaleItem(
                    ModingStaleReason.DslChanged,
                    $"self#{member.Name}",
                    handler.DslHash ?? string.Empty,
                    "<文件不存在>",
                    $"DSL 文档 '{dslRelative}' 不存在：DSL 已变更，请重新摊平。"));
                continue;
            }

            var current = File.ReadAllText(dslPath);
            var actual = HandlerContent.Hash(current);
            if (handler.DslHash is null || !HandlerContent.Matches(current, handler.DslHash))
            {
                // 差异项里也写上产物当前的 handler id：重新摊平后它会随内容变化（D9：id 跟内容走，
                // 资产 id 稳定），审查面读这一行就能看出「哪些 id 会换掉」。
                var currentHandlerId = productAsset?.Members
                    .FirstOrDefault(m => string.Equals(m.Name, member.Name, StringComparison.Ordinal))
                    ?.Handler?.Id;

                items.Add(new ModingStaleItem(
                    ModingStaleReason.DslChanged,
                    $"self#{member.Name}",
                    handler.DslHash ?? "<未记录>",
                    actual,
                    $"成员 '{member.Name}' 引用的 '{dslRelative}' 内容已变化：DSL 已变更，请重新摊平"
                    + (currentHandlerId is null
                        ? "。"
                        : $"（产物当前 handler id：{currentHandlerId}；重新摊平后该 id 会随文本变化，资产 id 不变）。")));
            }
        }

        // ② 产物落后于父（R8 第 8 条）：父新增成员而产物里没有＝明确结果，不静默。
        if (draft.Extends is { } parentId)
        {
            var catalog = BuildCatalog(assets, errors);
            if (catalog.Contains(parentId))
            {
                try
                {
                    var parent = catalog.Resolve(parentId);
                    var productMembers = productAsset is not null
                        ? productAsset.Members.Select(static m => m.Name).ToHashSet(StringComparer.Ordinal)
                        : null;

                    if (productMembers is not null)
                    {
                        foreach (var member in parent.Members)
                        {
                            if (!productMembers.Contains(member.Name))
                            {
                                items.Add(new ModingStaleItem(
                                    ModingStaleReason.ParentAhead,
                                    $"self#{member.Name}",
                                    "产物成员表包含该成员",
                                    "产物成员表缺失该成员",
                                    $"父 '{parentId}' 有成员 '{member.Name}' 而产物没有：草案落后于父，请重新摊平。"));
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is PrefabLoadException or KeyNotFoundException)
                {
                    items.Add(new ModingStaleItem(
                        ModingStaleReason.ParentAhead,
                        parentId,
                        "父可展开",
                        ex.Message,
                        $"父 '{parentId}' 无法展开，无法判定草案是否落后：{ex.Message}"));
                }
            }
        }

        return new ModingFlattenCheckResult(items.Count == 0, items);
    }

    // ── 内部：成员来源 ② ────────────────────────────────────────────────────

    private static ComponentMemberAsset? BuildFromDeclaration(
        string root,
        ComponentMemberAsset declared,
        string memberAddress,
        HandlerDslEnvironment? dsl,
        List<PrefabLoadError> errors,
        ComponentMemberAsset? inherited,
        HandlerGuardSpec? guard = null)
    {        _ = inherited;

        if (declared.Handler is not { } handler)
        {
            // 重载一个成员却不给 handler ＝把该成员降级成纯编排成员。合法（作者可能就要清空逻辑），
            // 但这不是「未修改」——它是显式声明，照抄即可。
            return new ComponentMemberAsset(
                declared.Name, declared.Visibility, declared.View, null,
                declared.Band, declared.Bands, declared.Seams, declared.Sealed);
        }

        if (handler.Dsl is { } dslRelative)
        {
            if (dsl is null)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Capability,
                    memberAddress,
                    $"成员 '{declared.Name}' 指向 DSL 文档 '{dslRelative}'，但本环境未装配 handler DSL 编译器："
                    + "摊平需要生成 csx（请装配 engine.HandlerDsl）。"));
                return null;
            }

            // 路径基准必须与**装载期一致**：`AssetPackLoader.CompileDsl` 按「组件资产所在目录」解析
            // `handler.dsl`（`Path.Combine(directory, relative)`），而产物落在 `effects-v4/`。
            // 摊平曾只按包根解析 ⇒ 「DSL 与组件同目录」（samples 与前端产物的实际布局）的草案永远摊不平。
            // 这里先试产物目录，再回退包根：两种布局都能工作，存量资产零改动。
            var dslPath = ResolvePath(Path.Combine(root, ProductDirectory), dslRelative);
            if (!File.Exists(dslPath))
            {
                dslPath = ResolvePath(root, dslRelative);
            }

            if (!File.Exists(dslPath))
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    memberAddress,
                    $"成员 '{declared.Name}' 引用的 DSL 文档 '{dslRelative}' 不存在。"));
                return null;
            }

            var dslText = File.ReadAllText(dslPath);

            // 守卫合成（R32）：走**带守卫的编译**接口；编译器不支持＝能力缺失（不静默不加守卫）。
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
                    // 来源标记（R28／D5）：生成器 ＋ 目录版本 ＋ DSL 路径与双哈希 ＋ 节点映射。
                    // nodeMap 目前为空：生成器还没有行号表（见 PROGRESS 的遗留风险），字段保留契约形状。
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
                declared.Sealed);
        }

        if (handler.Script is { } script)
        {
            if (guard is not null)
            {
                // 守卫只能由生成器合成在文本首段；手写 csx 无法保证「作者改不到」，故拒绝而不是静默不加。
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    memberAddress,
                    $"成员 '{declared.Name}' 的父模板要求守卫（{HandlerGuardNames.Format(guard.Guard)}），"
                    + "但草案给的是手写 csx：守卫只能由生成器合成（作者不可编辑），请改用手写 DSL 表达效果体。"));
                return null;
            }

            // 草案直接给 csx（不是 DSL）：原样取文本，按内容寻址派生 csx id，无来源标记。
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
                declared.Sealed);
        }

        errors.Add(new PrefabLoadError(
            PrefabErrorCategories.Structure,
            memberAddress,
            $"成员 '{declared.Name}' 的 handler '{handler.Id}' 只有 assembly-key：摊平只能复制或生成 csx 文本（R28）。"));
        return null;
    }

    // ── 内部：抽象强制与守卫（R31／R32）──────────────────────────────────────

    /// <summary>
    /// **成员级抽象的摊平期强制**（R31 第 1／3 条）：父展开表里 <c>abstract: true</c> 且未被草案实现的成员＝错误。
    /// <para>
    /// 一次性收集**全部**缺失成员（不是只报第一条）：与 <see cref="Flatten"/> 末尾汇总 errors 的形态一致。
    /// 抽象性沿继承链合并——<c>parent.Members</c> 已是展开结果（子覆写即不再抽象），故这里只看展开表。
    /// </para>
    /// </summary>
    private static void ValidateAbstractMembers(
        PrefabType parent,
        IReadOnlyDictionary<string, ComponentMemberAsset> draftMembers,
        List<PrefabLoadError> errors)
    {
        foreach (var member in parent.Members)
        {
            if (!member.Abstract)
            {
                continue;
            }

            if (draftMembers.TryGetValue(member.Name, out var declared))
            {
                if (declared.Abstract)
                {
                    errors.Add(new PrefabLoadError(
                        PrefabErrorCategories.Structure,
                        $"self#{member.Name}",
                        $"成员 '{member.Name}' 在模板 '{parent.Id}' 里被声明为抽象（必须实现），"
                        + "草案又把它重新声明为 abstract: true：仍然没有实现体（R31 第 3 条）。"));
                }

                continue;
            }

            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                $"self#{member.Name}",
                $"成员 '{member.Name}' 是模板 '{parent.Id}' 的抽象成员（尚未实现）：草案必须提供它的实现"
                + "（成员级 abstract 在摊平期强制，R31）。"));
        }
    }

    /// <summary>
    /// **模板声明的 <c>requires-host</c> 校验**（R32 第 6／7 条／D24）：多抽象成员取交集，
    /// 声明更宽（「某成员在声明宿主上永不命中」）或交集为空（互斥宿主组合）＝拒绝。
    /// <para>
    /// 读的是 <c>guard</c>（模板资产才有的声明）而非 <c>abstract</c>——与 R31「装载期不认识 abstract」不冲突。
    /// </para>
    /// </summary>
    private static void ValidateTemplateRequirements(PrefabType parent, List<PrefabLoadError> errors)
    {
        var guarded = new List<GuardedMember>();
        foreach (var member in parent.Members)
        {
            if (member.Guard is not { } guard || guard == HandlerGuard.Any)
            {
                continue;
            }

            if (ResolveSignalOf(parent, member.Name) is not { } signal)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"self#{member.Name}",
                    $"成员 '{member.Name}' 声明了守卫（{HandlerGuardNames.Format(guard)}）但没有任何挂载声明指向它："
                    + "守卫的比较对象来自信号（挂载目标 engine#<信号>），无挂载则无从推导（R32）。"));
                continue;
            }

            guarded.Add(new GuardedMember(member.Name, signal, guard));
        }

        if (guarded.Count == 0)
        {
            return;
        }

        if (!SignalGuards.TryDeriveRequiresHost(guarded, parent.RequiresHost, out _, out var error))
        {
            errors.Add(new PrefabLoadError(PrefabErrorCategories.Structure, parent.Id, error!));
        }
    }

    /// <summary>
    /// 从父展开表读成员守卫并解析成合成参数（R32）：信号由该成员的**挂载声明**给出
    /// （<c>engine#&lt;信号&gt;</c>），比较对象由信号白名单解析（不按载荷顺序或类型推断）。
    /// </summary>
    private static HandlerGuardSpec? ResolveGuardSpec(
        PrefabType parent, MemberDefinition member, List<PrefabLoadError> errors)
    {
        if (member.Guard is not { } guard || guard == HandlerGuard.Any)
        {
            return null;
        }

        var signal = ResolveSignalOf(parent, member.Name);
        if (signal is null)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                $"self#{member.Name}",
                $"成员 '{member.Name}' 声明了守卫（{HandlerGuardNames.Format(guard)}）但没有挂载声明指向信号："
                + "无法推导比较对象（R32）。"));
            return null;
        }

        if (!SignalGuards.TryResolve(guard, signal, out var spec, out var error))
        {
            errors.Add(new PrefabLoadError(PrefabErrorCategories.Structure, $"self#{member.Name}", error!));
            return null;
        }

        return spec;
    }

    /// <summary>
    /// 守卫要读的载荷字段必须出现在**草案重载后的视图**里（R32）：视图整段替换（R3a），
    /// 作者若把守卫比较用的字段从视图里删掉，守卫代码会在运行期取不到字段——在摊平期就拒绝。
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

    /// <summary>成员所挂的信号名（父展开表里第一条 <c>engine#&lt;信号&gt;</c> 挂载；无＝null）。</summary>
    private static string? ResolveSignalOf(PrefabType parent, string memberName)
    {
        foreach (var mount in parent.Mounts)
        {
            if (!string.Equals(mount.Member, memberName, StringComparison.Ordinal))
            {
                continue;
            }

            var hash = mount.Target.IndexOf('#', StringComparison.Ordinal);
            if (hash > 0)
            {
                return mount.Target[(hash + 1)..];
            }
        }

        return null;
    }

    // ── 内部：读写与索引 ────────────────────────────────────────────────────

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string ResolvePath(string root, string relative)
        => Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static ComponentAsset? ReadAsset(string absolute, string display, List<PrefabLoadError> errors)
    {
        if (!File.Exists(absolute))
        {
            errors.Add(new PrefabLoadError(PrefabErrorCategories.Structure, display, $"文件不存在：'{display}'。"));
            return null;
        }

        try
        {
            return AssetSchema.Read(File.ReadAllText(absolute), display);
        }
        catch (Exception ex) when (ex is PrefabLoadException or IOException or UnauthorizedAccessException)
        {
            errors.Add(new PrefabLoadError(
                ex is PrefabLoadException prefab ? prefab.Category : PrefabErrorCategories.Structure,
                display,
                ex.Message));
            return null;
        }
    }

    /// <summary>
    /// 产物 id（R8 第 5 条「首次摊平生成 id、二次不新建」）：草案里的 <c>asset.product.id</c> 优先；
    /// 缺省按**确定性规则**派生——去掉 <c>.draft</c> 后缀，仍与草案 id 相同则追加 <c>.moding</c>。
    /// <para>
    /// **草案 id ≠ 产物 id** 是硬要求（R11 第 5 条：同 id 两副面孔＝资产声明错误）。
    /// </para>
    /// </summary>
    private static string? ResolveProductId(string draftAbsolutePath, ComponentAsset draft, List<PrefabLoadError> errors)
    {
        var declared = ReadDeclaredProductId(draftAbsolutePath);
        var productId = declared ?? DeriveProductId(draft.Id);

        if (string.Equals(productId, draft.Id, StringComparison.Ordinal))
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                draft.Id,
                $"草案 id 与产物 id 相同（'{productId}'）：草案与 moding 产物必须是两个不同的应用层 id（R11 第 5 条）。"));
            return null;
        }

        if (draft.Extends is { } parent && string.Equals(productId, parent, StringComparison.Ordinal))
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                productId,
                $"产物 id 不能等于父 id（'{parent}'）：moding 产物必须是新 id、不得覆盖父（Constraints「id 唯一」）。"));
            return null;
        }

        return productId;
    }

    private static string DeriveProductId(string draftId)
    {
        const string draftSuffix = ".draft";
        return draftId.EndsWith(draftSuffix, StringComparison.Ordinal) && draftId.Length > draftSuffix.Length
            ? draftId[..^draftSuffix.Length]
            : draftId + ".moding";
    }

    /// <summary>
    /// 读草案的**血缘字段** <c>asset.product.id</c>（首次摊平由编辑器写入；本类只读）。
    /// <para>
    /// 为什么不进 <see cref="ComponentAsset"/>：它是**草案独有**的编辑期血缘，产物里不许出现
    /// （R8 第 4 条：产物不引用父的任何 id，也不背自己的血缘）。为它污染组件资产模型得不偿失。
    /// </para>
    /// </summary>
    private static string? ReadDeclaredProductId(string draftPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(draftPath));
            if (document.RootElement.TryGetProperty("asset", out var asset)
                && asset.TryGetProperty("product", out var product)
                && product.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
            {
                return id.GetString()!.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 血缘字段缺失／不可读＝走确定性派生（不阻断摊平）。
        }

        return null;
    }

    private static Dictionary<string, ComponentAsset> LoadPackAssets(string root, List<PrefabLoadError> errors)
    {
        var assets = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal);

        void AddAll(string directory, string pattern)
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly)
                         .OrderBy(static f => f, StringComparer.Ordinal))
            {
                var asset = ReadAsset(file, Path.GetFileName(file), errors);
                if (asset is not null)
                {
                    assets.TryAdd(asset.Id, asset);
                }
            }
        }

        AddAll(ProductDirectory, "*.component.json");
        AddAll(DraftDirectory, "*" + DraftSuffix);
        return assets;
    }

    /// <summary>
    /// 读**模板库**（R33：编辑期只读依赖层）：<c>&lt;templateRoot&gt;/effects-v4/*.component.json</c>，
    /// 与包内资产**同一发现规则**（<see cref="AssetSchema.FilePattern"/> ＋ <c>TopDirectoryOnly</c>）。
    /// <para>
    /// **不经过** <see cref="AssetPackLoader"/>（D29）：模板只作编辑期只读层，不注册进对局装载面。
    /// null／空白／目录不存在＝空层（模板不可用，行为与今天一致，R33 第 5 条）。
    /// </para>
    /// </summary>
    private static Dictionary<string, ComponentAsset> LoadTemplateAssets(string? templateRoot, List<PrefabLoadError> errors)
    {
        var assets = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(templateRoot))
        {
            return assets;
        }

        var directory = Path.Combine(
            Path.GetFullPath(templateRoot), ProductDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            return assets;
        }

        foreach (var file in Directory.EnumerateFiles(directory, AssetSchema.FilePattern, SearchOption.TopDirectoryOnly)
                     .OrderBy(static f => f, StringComparer.Ordinal))
        {
            var asset = ReadAsset(file, Path.GetFileName(file), errors);
            if (asset is not null)
            {
                assets.TryAdd(asset.Id, asset);
            }
        }

        return assets;
    }

    private static PrefabCatalog BuildCatalog(
        IReadOnlyDictionary<string, ComponentAsset> assets, List<PrefabLoadError> errors)
    {
        var catalog = GameEnginePrefabs.CreateCatalog();
        foreach (var asset in assets.Values.OrderBy(static a => a.Id, StringComparer.Ordinal))
        {
            try
            {
                catalog.Add(asset.ToStructuralPrefabDefinition());
            }
            catch (Exception ex) when (ex is PrefabLoadException or ArgumentException)
            {
                errors.Add(new PrefabLoadError(
                    ex is PrefabLoadException prefab ? prefab.Category : PrefabErrorCategories.Structure,
                    asset.Id,
                    $"资产 '{asset.Id}' 无法进入目录：{ex.Message}"));
            }
        }

        return catalog;
    }

    /// <summary>父成员在**声明它的资产**里的原始声明（继承链上逐层覆写后的最终声明）。</summary>
    private static ComponentMemberAsset? FindDeclaration(
        IReadOnlyDictionary<string, ComponentAsset> assets, MemberDefinition member)
    {
        if (member.DeclaredBy is null || !assets.TryGetValue(member.DeclaredBy, out var asset))
        {
            return null;
        }

        return asset.Members.FirstOrDefault(m => string.Equals(m.Name, member.Name, StringComparison.Ordinal));
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
