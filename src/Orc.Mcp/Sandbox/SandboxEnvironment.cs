namespace Orc.Mcp.Sandbox;

/// <summary>沙盒脚手架创建结果。</summary>
public sealed record SandboxScaffoldResult(
    string PackRoot,
    string CardsDirectory,
    string EffectsDirectory,
    string ManifestFile,
    string PlaceholderCardFile,
    bool CreatedManifest,
    bool CreatedPlaceholder);

/// <summary>沙盒包元数据同步结果（补齐 sidecar 与 lock 的结果）。</summary>
/// <param name="Wrote">是否实际写入了文件（全 Skip ＝ false）。</param>
/// <param name="WrittenFiles">已写入的相对/绝对路径（诊断用）。</param>
/// <param name="Obstacles">无法生成元数据的条目与原因（不阻断，仅报告）。</param>
public sealed record SandboxMetadataResult(
    bool Wrote,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> Obstacles);

/// <summary>
/// 沙盒环境：**唯一可写根**的结构、脚手架与读面。
/// <para>
/// 目录约定（与 <see cref="Orc.Packages.PackageScanner"/> 一致）：<c>&lt;SandboxRoot&gt;/pack/{manifest.json, cards/, effects/}</c>；
/// 卡数据体文件＝<c>cards/*.card.json</c>，效果预制体＝<c>effects/*.prefab.json</c>。
/// 脚手架随**沙盒模式启动**创建（manifest 的 <c>guid</c> 一次性生成并持久化——保证资产名稳定）。
/// </para>
/// <para>
/// 占位卡：引擎在创建期**拒绝空卡组名单**（<c>Match</c> 构造契约），故脚手架生成一张占位卡
/// <c>&lt;PlaceholderCardId&gt;.card.json</c>，沙盒局以它为双方卡组底（起手张数＝0，故不进手牌）。
/// 该卡对 agent 可见、可改写、可删除（删除后建局会重新生成）。
/// </para>
/// </summary>
public sealed class SandboxEnvironment
{
    private const string PlaceholderCardJsonTemplate = """
{
  "schemaVersion": 1,
  "id": "{ID}",
  "name": "沙盒占位卡（引擎要求卡组名单非空）",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "neutral", "kredits": 1 },
    { "component": "battleStats", "operationCost": 0, "attack": 0, "defense": 1 },
    { "component": "tagData", "rarity": "standard", "tags": ["sandbox"] }
  ]
}
""";

    private const string ManifestJsonTemplate = """
{
  "kind": "cardpack",
  "id": "{ID}",
  "guid": "{GUID}",
  "version": "0.0.1",
  "dependsOn": [],
  "assets": []
}
""";

    public SandboxEnvironment(McpServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Guard = new SandboxPathGuard(options.SandboxRoot);
    }

    /// <summary>服务层选项。</summary>
    public McpServiceOptions Options { get; }

    /// <summary>路径守卫（唯一合法写路径来源）。</summary>
    public SandboxPathGuard Guard { get; }

    /// <summary>沙盒卡包根（<c>&lt;root&gt;/pack</c>）。</summary>
    public string PackRoot => Guard.Resolve("pack");

    /// <summary>卡数据体目录。</summary>
    public string CardsDirectory => Guard.Resolve("pack/cards");

    /// <summary>效果预制体目录。</summary>
    public string EffectsDirectory => Guard.Resolve("pack/effects");

    /// <summary>卡包清单文件。</summary>
    public string ManifestFile => Guard.Resolve("pack/manifest.json");

    /// <summary>占位卡文件。</summary>
    public string PlaceholderCardFile => Guard.Resolve($"pack/cards/{Options.PlaceholderCardId}.card.json");

    /// <summary>沙盒是否已建脚手架（manifest 存在）。</summary>
    public bool IsScaffolded => File.Exists(ManifestFile);

    /// <summary>
    /// 创建脚手架（幂等）：目录 ＋ <c>manifest.json</c>（guid 一次性生成并持久化）＋ 占位卡。
    /// </summary>
    public SandboxScaffoldResult EnsureScaffold()
    {
        Directory.CreateDirectory(PackRoot);
        Directory.CreateDirectory(CardsDirectory);

        // v4-only（§23.8 第 1 项）：旧格式 `effects/` 已随旧装载链删除，装配器遇到它会把**整包**判非法
        // （`CardPackAssembler`：「旧格式效果库目录已不再支持（v4-only）」）。脚手架本身不产效果资产，
        // 所以这里**不创建**该目录——建了就等于把自己的沙盒包判成非法，表现为服务端开局全部失败
        // （19 条房间/重连/卡池集成用例连锁红，且报错只指向一个空目录，极难从症状反推）。

        var createdManifest = false;
        if (!File.Exists(ManifestFile))
        {
            var json = ManifestJsonTemplate
                .Replace("{ID}", Options.SandboxPackId, StringComparison.Ordinal)
                .Replace("{GUID}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
            File.WriteAllText(ManifestFile, json);
            createdManifest = true;
        }

        var createdPlaceholder = false;
        if (!File.Exists(PlaceholderCardFile))
        {
            var json = PlaceholderCardJsonTemplate.Replace("{ID}", Options.PlaceholderCardId, StringComparison.Ordinal);
            File.WriteAllText(PlaceholderCardFile, json);
            createdPlaceholder = true;
        }

        return new SandboxScaffoldResult(
            PackRoot, CardsDirectory, EffectsDirectory, ManifestFile, PlaceholderCardFile, createdManifest, createdPlaceholder);
    }

    /// <summary>
    /// 为沙盒卡包补齐元数据（每源文件一份 sidecar ＋ 两域 lock），使其可用于正式装配链。
    /// <para>
    /// **为什么必须做**：装配链由 lock 驱动（<c>MatchCardAssembly.Register</c> 读 <c>lock.formal.json</c>），
    /// 而脚手架只产出 manifest ＋ 占位卡；不补这一步，沙盒包会被判「无 lock」而整体非法，
    /// 进而被开局装配的降级路径剔除——表现为「调试面造出的卡进不了对局」。
    /// </para>
    /// <para>
    /// 幂等：既有 sidecar 一律 Skip（<c>force:false</c>），只补新增源文件与 lock；源文件不可解析时
    /// 记 <see cref="SandboxMetadataResult.Obstacles"/> 并跳过该文件，**不写坏**既有 sidecar，也不抛。
    /// </para>
    /// </summary>
    public SandboxMetadataResult EnsurePackageMetadata()
    {
        if (!Directory.Exists(PackRoot))
        {
            return new SandboxMetadataResult(false, Array.Empty<string>(), Array.Empty<string>());
        }

        Orc.Packages.MigrationPlan plan;
        try
        {
            plan = Orc.Packages.AssetMigrationTool.Plan(PackRoot, force: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
            or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
        {
            return new SandboxMetadataResult(
                false,
                Array.Empty<string>(),
                new[] { $"元数据规划失败：{ex.GetType().Name}: {ex.Message}" });
        }

        var obstacles = plan.Obstacles
            .Select(obstacle => $"{obstacle.File}: {obstacle.Reason}")
            .ToArray();

        if (!plan.HasPendingWrites)
        {
            return new SandboxMetadataResult(false, Array.Empty<string>(), obstacles);
        }

        try
        {
            var applied = Orc.Packages.AssetMigrationTool.Apply(plan);
            return new SandboxMetadataResult(true, applied.WrittenFiles, obstacles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or FormatException or InvalidOperationException)
        {
            return new SandboxMetadataResult(
                false,
                Array.Empty<string>(),
                obstacles.Append($"元数据写入失败：{ex.GetType().Name}: {ex.Message}").ToArray());
        }
    }

    /// <summary>列出沙盒卡包内的卡数据体文件（绝对路径；按文件名排序＝确定性）。</summary>
    public IReadOnlyList<string> ListCardFiles()
    {
        if (!Directory.Exists(CardsDirectory))
        {
            return Array.Empty<string>();
        }

        var files = Directory.GetFiles(CardsDirectory, "*.card.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>列出沙盒卡包内的效果预制体文件（绝对路径；确定性排序）。</summary>
    public IReadOnlyList<string> ListEffectFiles()
    {
        if (!Directory.Exists(EffectsDirectory))
        {
            return Array.Empty<string>();
        }

        var files = Directory.GetFiles(EffectsDirectory, "*.prefab.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
