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
    /// <exception cref="ArgumentNullException">runtime／directory 为 null。</exception>
    public static AssetPackLoadResult Load(
        OrchestrationRuntime runtime,
        string directory,
        IScriptEvaluator? evaluator,
        ISyncScriptEvaluator? judgeEvaluator = null,
        string? packId = null)
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

        foreach (var (file, asset) in assets)
        {
            foreach (var member in asset.Members)
            {
                if (member.Handler is not { } handler)
                {
                    continue;
                }

                if (handler.Script is null)
                {
                    errors.Add(new PrefabLoadError(
                        PrefabErrorCategories.Structure,
                        $"{file} / {asset.Id}#{member.Name}",
                        $"handler '{handler.Id}' 只有 assembly-key，没有 csx 源码：组件资产只接受可预编译的 csx"
                        + "（程序集处理器属于旧来源，批次 5 一并处理）。"));
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
                        handler.Script,
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
                if (member.Handler is not { } handler || handler.Script is null)
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
