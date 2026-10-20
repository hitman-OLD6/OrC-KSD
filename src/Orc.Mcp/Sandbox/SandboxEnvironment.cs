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
        Directory.CreateDirectory(EffectsDirectory);

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
