namespace Orc.Core.Model;

/// <summary>
/// 资产包装载结果（S14 的原子口径）：**全绿**（<see cref="Loaded"/>）或**整包失败**
/// （<see cref="Errors"/> 列出全部失败项，且运行时上**零注册残留**）。
/// </summary>
public sealed class AssetPackLoadResult
{
    /// <summary>创建结果。</summary>
    public AssetPackLoadResult(
        string packId,
        bool loaded,
        IReadOnlyList<string> assetIds,
        IReadOnlyList<PrefabLoadError> errors,
        int files)
    {
        PackId = packId;
        Loaded = loaded;
        AssetIds = assetIds;
        Errors = errors;
        Files = files;
    }

    /// <summary>包 id（诊断／审查用；目录装载时＝目录名）。</summary>
    public string PackId { get; }

    /// <summary>是否整包装载成功（true＝全部资产已注册）。</summary>
    public bool Loaded { get; }

    /// <summary>已注册的资产 id（装载失败＝空）。</summary>
    public IReadOnlyList<string> AssetIds { get; }

    /// <summary>失败项（装载成功＝空；失败＝全部失败项，不是首个）。</summary>
    public IReadOnlyList<PrefabLoadError> Errors { get; }

    /// <summary>扫描到的资产文件数。</summary>
    public int Files { get; }
}

/// <summary>
/// 组件资产包装载器（S11／S14）：把一份卡包的组件资产目录装进编排运行时。
/// <para>
/// **两步，顺序不可交换**（S14 第 1 条「先全量校验再统一注册」）：
/// <list type="number">
/// <item><b>全量校验</b>：解析全部文件 → 在**暂存**目录／handler 表里编译全部 csx（S5 第 9 条：装载期预编译）
///   → 用**真实挂载门**（<see cref="MountPlanner"/>／<see cref="OrchestrationRuntime.RegisterWraps"/>）
///   在暂存运行时上逐资产规划边与包装。失败项**收集齐**，不停在第一个。</item>
/// <item><b>统一注册</b>：只有零失败才把暂存的预制体与 handler 资产提交到正式运行时——否则一个都不提交。</item>
/// </list>
/// </para>
/// <para>
/// 因此"部分成功"不存在：不会出现"少了某个组件的卡"或"半包可寻址成员"（R3i／W-17）。
/// </para>
/// </summary>
public static class AssetPackLoader
{
    /// <summary>
    /// 装载一个组件资产目录（原子）：目录中的 <c>*.component.json</c> 全部成功才注册。
    /// </summary>
    /// <param name="runtime">目标编排运行时（正式面；失败时不被触碰）。</param>
    /// <param name="directory">资产目录（不存在＝空包＝成功装载 0 份）。</param>
    /// <param name="evaluator">异步脚本求值器（有 csx 时必须；null＝csx 资产失败，分类 <c>compile</c>）。</param>
    /// <param name="judgeEvaluator">同步脚本求值器（判定器包装声明用；无判定器包装时可为 null）。</param>
    /// <param name="packId">包 id（缺省＝目录名）。</param>
    /// <param name="handlerDsl">
    /// handler DSL 装配件（编译器 ＋ 目录；可空）。**只在草案装载期使用**（R28）：遇到
    /// <c>members[].handler.dsl</c> 时现场生成 csx（**不写盘**）再走既有预编译链。
    /// <para>
    /// null ＝**能力缺失**：含 DSL 成员的草案＝装载拒绝（分类 <see cref="PrefabErrorCategories.Capability"/>），
    /// **不含 DSL 的资产零行为变化**。这是能力依赖、不是 R9 所禁的「专项禁令」——没有编译器就没有编译，
    /// 等同于「没有 Roslyn 就编译不了 csx」，csx 链本来就是这个语义。
    /// </para>
    /// <para>
    /// **产物装载期**（正式段，只吃 csx 文本 ＋ 来源标记）**不碰本参数**：那里没有 DSL，也就不存在生成这一步。
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException">runtime／directory 为 null。</exception>
    public static AssetPackLoadResult Load(
        OrchestrationRuntime runtime,
        string directory,
        IScriptEvaluator? evaluator,
        ISyncScriptEvaluator? judgeEvaluator = null,
        string? packId = null,
        HandlerDslEnvironment? handlerDsl = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(directory);

        var label = packId ?? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, '/'));
        if (string.IsNullOrWhiteSpace(label))
        {
            label = directory;
        }

        if (!Directory.Exists(directory))
        {
            return new AssetPackLoadResult(label, true, Array.Empty<string>(), Array.Empty<PrefabLoadError>(), 0);
        }

        var files = Directory
            .EnumerateFiles(directory, AssetSchema.FilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToArray();

        var errors = new List<PrefabLoadError>();
        var assets = new List<(string File, ComponentAsset Asset)>();

        // ① 解析（结构校验；失败逐文件记录，继续解析其余文件）。
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                assets.Add((name, AssetSchema.Read(File.ReadAllText(file), name)));
            }
            catch (PrefabLoadException ex)
            {
                errors.Add(new PrefabLoadError(ex.Category, name, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new PrefabLoadError(PrefabErrorCategories.Structure, name, $"文件读取失败：{ex.Message}"));
            }
        }

        // 重复 id 在同包里＝结构错误（跨包由正式目录的 Add 兜住）。
        foreach (var duplicate in assets.GroupBy(static a => a.Asset.Id, StringComparer.Ordinal).Where(static g => g.Count() > 1))
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                duplicate.Key,
                $"同一包内重复声明资产 id '{duplicate.Key}'（{string.Join("、", duplicate.Select(static d => d.File))}）。"));
        }

        if (errors.Count > 0)
        {
            return new AssetPackLoadResult(label, false, Array.Empty<string>(), errors, files.Length);
        }

        // ② 暂存面：handler 编译 + 预制体展开 + 真实挂载门校验。全部落在暂存对象上，正式运行时不动。
        var stagedHandlers = new HandlerRegistry();
        var stagedCatalog = new PrefabCatalog().AddRange(runtime.Catalog.Definitions);
        var stagedPrefabs = new List<(string File, PrefabDefinition Prefab)>();
        var failedHandlers = new HashSet<string>(StringComparer.Ordinal);

        // 草案装载期现场生成的 csx（不写盘）：统一注册阶段要用它把 handler 提交到正式运行时。
        var generatedCsx = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (file, asset) in assets)
        {
            foreach (var member in asset.Members)
            {
                if (member.Handler is not { } handler)
                {
                    continue;
                }

                // ── 草案装载期（R28 第 5 条）：handler.dsl ＝现场生成 csx，**不写盘** ──
                // 生成失败＝装载拒绝，错误定位到 DSL 节点路径（不是 csx 行号）。
                var csx = handler.Script;
                var memberAddress = $"self#{member.Name}";

                if (handler.Dsl is not null)
                {
                    if (handlerDsl is null)
                    {
                        // 能力缺失（D6／D14）：不是禁令、不是编译失败——「这台机器上没有编译器」。
                        failedHandlers.Add(handler.Id);
                        errors.Add(new PrefabLoadError(
                            PrefabErrorCategories.Capability,
                            memberAddress,
                            $"本环境未装配 handler DSL 编译器，无法装载含 DSL 成员的草案"
                            + $"（{file} / {asset.Id} 的成员 '{member.Name}' 指向 '{handler.Dsl}'）："
                            + "请装配 engine.HandlerDsl，或改用不含 DSL 成员的资产。"));
                        continue;
                    }

                    var generated = CompileDsl(
                        handlerDsl, directory, handler, member, asset, file, memberAddress, errors);
                    if (generated is null)
                    {
                        failedHandlers.Add(handler.Id);
                        continue;
                    }

                    csx = generated;
                    generatedCsx[handler.Id] = generated;
                }

                if (csx is null)
                {
                    errors.Add(new PrefabLoadError(
                        PrefabErrorCategories.Structure,
                        $"{file} / {asset.Id}#{member.Name}",
                        $"handler '{handler.Id}' 只有 assembly-key，没有 csx 源码：组件资产只接受可预编译的 csx"
                        + "（程序集处理器属于旧来源，批次 5 一并处理）。"));
                    continue;
                }

                // ── 产物装载期（R28 末条）：来源标记的 **textHash** 是唯一校验对象 ──
                // 重算文本哈希，不一致＝「生成物被改写」（提示回草案重新摊平）。产物里没有 DSL，
                // dslHash **不在这里校验**（重算不出来，也不该假装能，D5）。
                if (handler.Generated is { } provenance && !HandlerContent.Matches(csx, provenance.TextHash))
                {
                    failedHandlers.Add(handler.Id);
                    errors.Add(new PrefabLoadError(
                        PrefabErrorCategories.Structure,
                        memberAddress,
                        $"{file} / {asset.Id} 的成员 '{member.Name}' 的 handler 文本与来源标记不符"
                        + "（生成物被改写）：textHash 应匹配由 '" + provenance.Source + "' 生成的文本，"
                        + "请回草案重新摊平，不要手工编辑生成物（R28）。"));
                    continue;
                }

                if (evaluator is null)
                {
                    failedHandlers.Add(handler.Id);
                    errors.Add(new PrefabLoadError(
                        ScriptErrorCategories.Compile,
                        $"{file} / {asset.Id}#{member.Name}",
                        $"handler '{handler.Id}' 有 csx 源码但没有装配脚本求值器（engine.ScriptEvaluator）——"
                        + "装载期预编译无法进行（S5 第 9 条：不保留懒编译）。"));
                    continue;
                }

                try
                {
                    stagedHandlers.RegisterScript(
                        handler.Id,
                        csx,
                        handler.Entry,
                        evaluator,
                        member.View,
                        OwnerMembersOf(asset));
                }
                catch (PrefabLoadException ex)
                {
                    failedHandlers.Add(handler.Id);
                    errors.Add(new PrefabLoadError(ex.Category, $"{file} / {asset.Id}#{member.Name}", ex.Message));
                }
            }

            foreach (var judgeWrap in asset.JudgeWraps)
            {
                if (judgeEvaluator is null)
                {
                    errors.Add(new PrefabLoadError(
                        ScriptErrorCategories.Compile,
                        $"{file} / {asset.Id}",
                        $"判定器包装 '{judgeWrap.Judicator}' 有 csx 源码但没有装配同步脚本求值器——装载期预编译无法进行。"));
                    continue;
                }

                var result = judgeEvaluator.Evaluate(new SyncScriptRequest(judgeWrap.Script, judgeWrap.Entry));
                if (!result.Success || result.Handler is null)
                {
                    errors.Add(new PrefabLoadError(
                        result.ErrorCategory ?? ScriptErrorCategories.Compile,
                        $"{file} / {asset.Id}",
                        $"判定器包装 '{judgeWrap.Judicator}' 装载期校验失败：[{result.ErrorCategory}] {result.Error}"));
                    continue;
                }

                stagedHandlers.RegisterJudge(ComponentAsset.JudgeHandlerId(asset.Id, judgeWrap.Judicator), result.Handler);
            }

            if (asset.Members.Any(m => m.Handler is { } h && failedHandlers.Contains(h.Id)))
            {
                continue; // handler 已单独报错：不再补一条"引用了不存在的 handler"的二次错误。
            }

            try
            {
                stagedPrefabs.Add((file, asset.ToPrefabDefinition(stagedHandlers.Resolve)));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or PrefabLoadException or ArgumentException)
            {
                errors.Add(new PrefabLoadError(
                    ex is PrefabLoadException prefab ? prefab.Category : PrefabErrorCategories.Structure,
                    $"{file} / {asset.Id}",
                    ex.Message));
            }
        }

        // 预制体在暂存目录里展开：继承/可见性/字段撞名/重复组件/one-shot 无触发点等在这里一次性暴露。
        var stagedDefinitions = new List<PrefabDefinition>(stagedPrefabs.Count);
        foreach (var (file, prefab) in stagedPrefabs)
        {
            try
            {
                stagedCatalog.Add(prefab);
                stagedDefinitions.Add(prefab);
            }
            catch (PrefabLoadException ex)
            {
                errors.Add(new PrefabLoadError(ex.Category, $"{file} / {prefab.Id}", ex.Message));
            }
        }

        if (errors.Count > 0)
        {
            return new AssetPackLoadResult(label, false, Array.Empty<string>(), errors, files.Length);
        }

        // ③ 真实挂载门：在暂存运行时上对每个允许宿主类型各规划一次边（host# 的存在性按全部宿主类型校验，R3h）。
        var stagedRuntime = new OrchestrationRuntime(stagedCatalog, stagedHandlers, runtime.Engine.Type.Id);
        foreach (var prefab in stagedDefinitions)
        {
            var type = stagedCatalog.Resolve(prefab.Id);
            var hostTypes = stagedCatalog.HostTypesCovering(type.RequiresHost);
            if (hostTypes.Count == 0)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    prefab.Id,
                    $"组件 '{prefab.Id}' 的 requires-host={AssetSchema.FormatHostKind(type.RequiresHost)} "
                    + "没有任何宿主类型预制体覆盖（组件无处可装）。"));
                continue;
            }

            foreach (var hostType in hostTypes)
            {
                OrchestrationEntity? host = null;
                OrchestrationEntity? component = null;
                try
                {
                    host = stagedRuntime.CreateEntity(hostType.Id, $"probe:{prefab.Id}@{hostType.Id}");
                    component = stagedRuntime.CreateEntity(prefab.Id, $"probe:{prefab.Id}", host);
                    stagedRuntime.Bind(component);
                }
                catch (PrefabLoadException ex)
                {
                    errors.Add(new PrefabLoadError(ex.Category, $"{prefab.Id}（宿主 {hostType.Id}）", ex.Message));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or PermissionDeniedException or InvalidOperationException)
                {
                    errors.Add(new PrefabLoadError(
                        PrefabErrorCategories.Addressing, $"{prefab.Id}（宿主 {hostType.Id}）", ex.Message));
                }
                finally
                {
                    component?.Destroy();
                    host?.Destroy();
                }
            }
        }

        if (errors.Count > 0)
        {
            return new AssetPackLoadResult(label, false, Array.Empty<string>(), errors, files.Length);
        }

        // ④ 统一注册：此刻才触碰正式运行时（先目录后 handler；目录 Add 失败＝抛出，调用方按包级失败处理）。
        var registered = new List<string>(stagedDefinitions.Count);
        try
        {
            foreach (var prefab in stagedDefinitions)
            {
                runtime.Catalog.Add(prefab);
            }

            foreach (var prefab in stagedDefinitions)
            {
                registered.Add(prefab.Id);
            }

            foreach (var member in assets.SelectMany(static a => a.Asset.Members))
            {
                if (member.Handler is not { } handler)
                {
                    continue;
                }

                // 草案装载期生成过的 handler 没有 Script（csx 只在暂存面里），故按 id 取回生成的文本。
                if (handler.Script is null && !generatedCsx.ContainsKey(handler.Id))
                {
                    continue;
                }

                var asset = stagedHandlers.Resolve(handler.Id);
                runtime.Handlers.Register(handler.Id, asset.Handler);
            }

            foreach (var (asset, judgeWrap) in assets.SelectMany(static a => a.Asset.JudgeWraps.Select(w => (a.Asset, w))))
            {
                var id = ComponentAsset.JudgeHandlerId(asset.Id, judgeWrap.Judicator);
                var staged = stagedHandlers.Resolve(id).Judge
                    ?? throw new InvalidOperationException($"判定器包装资产 '{id}' 未编译出同步 handler。");
                runtime.Handlers.RegisterJudge(id, staged);
            }
        }
        catch (Exception ex) when (ex is PrefabLoadException or InvalidOperationException or KeyNotFoundException)
        {
            errors.Add(new PrefabLoadError(
                ex is PrefabLoadException prefab ? prefab.Category : PrefabErrorCategories.Structure,
                label,
                $"统一注册失败：{ex.Message}"));
            return new AssetPackLoadResult(label, false, Array.Empty<string>(), errors, files.Length);
        }

        return new AssetPackLoadResult(label, true, registered, Array.Empty<PrefabLoadError>(), files.Length);
    }

    /// <summary>
    /// **草案装载期**的一段生成（R28 第 5 条）：读 DSL 文档 → 调注入的编译器**现场生成 csx**（不写盘）。
    /// <para>
    /// 失败＝把编译器给的 <see cref="HandlerDslError"/> **逐条**搬进 <paramref name="errors"/>（同一成员可能有多条，
    /// <see cref="PrefabLoadReport"/> 天生收得住，D14）：<c>Code</c>／<c>NodePath</c> 直搬，<c>Target</c>＝成员地址，
    /// <c>Category</c> 按 S6 的映射表由**装载器**自己填（编译器不认识 <see cref="PrefabLoadError"/>）。
    /// </para>
    /// <para>
    /// 视图声明由**挂载点目标成员**的 <see cref="ViewSpec"/> 投影成内核形态（纯数据、文本类型名）——
    /// 内核不认识引脚类型系统，解析与越界判定在实现程序集（D4）。
    /// </para>
    /// </summary>
    /// <returns>生成的 csx 文本；失败＝null（错误已登记）。</returns>
    private static string? CompileDsl(
        HandlerDslEnvironment environment,
        string directory,
        ComponentHandlerAsset handler,
        ComponentMemberAsset member,
        ComponentAsset asset,
        string file,
        string memberAddress,
        List<PrefabLoadError> errors)
    {
        var relative = handler.Dsl!.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.Combine(directory, relative);
        string dslJson;
        try
        {
            dslJson = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                memberAddress,
                $"{file} / {asset.Id} 的成员 '{member.Name}' 引用的 DSL 文档 '{handler.Dsl}' 读不出来：{ex.Message}"));
            return null;
        }

        HandlerDslCompileResult result;
        try
        {
            result = environment.Compiler.Compile(dslJson, environment.Catalog, ProjectView(member.View));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 编译器的契约是「不抛」；实现违约时也不让整包装载炸掉——按结构错误收敛（定位仍是成员地址）。
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                memberAddress,
                $"{file} / {asset.Id} 的成员 '{member.Name}' 的 DSL 编译抛出异常：{ex.Message}"));
            return null;
        }

        if (result.Success && result.Csx is not null)
        {
            return result.Csx;
        }

        foreach (var error in result.Errors)
        {
            errors.Add(new PrefabLoadError(
                DslLoadCategoryOf(error.Code),
                memberAddress,
                $"{file} / {asset.Id} 的成员 '{member.Name}' 的 handler DSL 校验失败：{error.Message}",
                MissingFields: MissingFieldsOf(error),
                Code: error.Code,
                NodePath: error.NodePath));
        }

        if (result.Errors.Count == 0)
        {
            errors.Add(new PrefabLoadError(
                PrefabErrorCategories.Structure,
                memberAddress,
                $"{file} / {asset.Id} 的成员 '{member.Name}' 的 DSL 编译失败但未给出错误（编译器实现违约）。"));
        }

        return null;
    }

    /// <summary>
    /// 图错误码 → 装载诊断大类（D14）：<c>write-forbidden</c> → <see cref="PrefabErrorCategories.View"/>，
    /// 其余 12 码 → <see cref="PrefabErrorCategories.Structure"/>。
    /// <para>
    /// 内核**不引用**实现程序集的 <c>HandlerDslErrorCategories</c>（分层：内核只有纯接口），故此处按 SPEC §14.1
    /// 的字面值判定；一致性由 <c>Orc.Packages.Tests</c> 的装载测试守护。
    /// </para>
    /// </summary>
    private static string DslLoadCategoryOf(string code) =>
        string.Equals(code, "write-forbidden", StringComparison.Ordinal)
            ? PrefabErrorCategories.View
            : PrefabErrorCategories.Structure;

    /// <summary>
    /// 缺失／要求的引脚名或字段名（D14 的 <c>MissingFields</c> 落位）：只有 <c>pin-missing</c> 能**无歧义**地
    /// 从节点路径末段取到（<c>actions[0].amount</c> → <c>amount</c>）；其余码留空——
    /// 编译器的三字段形态里没有字段名，猜它比留空更糟（字段名仍在 <c>Message</c> 里）。
    /// </summary>
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

    /// <summary>
    /// 视图声明投影（<see cref="ViewSpec"/> → 内核形态 <see cref="HandlerDslView"/>）：
    /// 实现只有一份（<see cref="HandlerDslView.FromViewSpec"/>），摊平器与装载器共用——
    /// 两处各自映射就会出现「摊平时能过、装载时不过」的漂移。
    /// </summary>
    private static HandlerDslView ProjectView(ViewSpec view) => HandlerDslView.FromViewSpec(view);

    /// <summary>该资产的 <c>owner</c> 通道允许名字（私有字段 ∪ 实体公开属性；S5 第 6d 条）。</summary>
    private static IReadOnlyCollection<string> OwnerMembersOf(ComponentAsset asset)
    {
        var names = new HashSet<string>(PrefabType.BuiltInOwnerMembers, StringComparer.Ordinal);
        foreach (var field in asset.PrivateFields)
        {
            names.Add(field.Name);
        }

        return names;
    }
}
