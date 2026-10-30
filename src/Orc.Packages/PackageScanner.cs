using System.Text.Json;
using Orc.Cards;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;

namespace Orc.Packages;

/// <summary>扫描到的一个资产源文件（及其同目录 sidecar）。</summary>
/// <param name="SourceFileName">源文件名（不含目录）。</param>
/// <param name="AbsolutePath">源文件绝对路径。</param>
/// <param name="SidecarPath">sidecar 绝对路径（不存在＝null）。</param>
/// <param name="RelativePath">相对包根的路径（<c>/</c> 分隔；sidecar 的 <c>source</c> 字段基准）。</param>
/// <param name="IsHistorical">是否位于 <c>versions/&lt;version&gt;/</c>（历史版本——只注册、不进当前版 DTO 内容）。</param>
public sealed record ScannedSourceFile(
    string SourceFileName,
    string AbsolutePath,
    string? SidecarPath,
    string RelativePath,
    bool IsHistorical = false)
{
    /// <summary>是否有 sidecar。</summary>
    public bool HasSidecar => SidecarPath is not null;
}

/// <summary>
/// 包目录**结构扫描**结果（纯文件布局，不解析资产语义）：卡目录／效果库／文本／词条／sidecar 列表。
/// </summary>
/// <param name="RootDirectory">包根目录。</param>
/// <param name="Manifest">包清单。</param>
/// <param name="CardDirectories">发现到的卡目录（<c>cards</c>／<c>cards-*</c>；稳定序）。</param>
/// <param name="EffectsDirectory">效果库目录（不存在＝null）。</param>
/// <param name="TextsFile">卡面文本文件（不存在＝null）。</param>
/// <param name="KeywordsFile">词条声明文件（不存在＝null）。</param>
/// <param name="CardFiles">卡源文件（含 sidecar 定位；稳定序）。</param>
/// <param name="DeckFiles">
/// 卡组源文件（<c>decks/</c> 顶层；含 sidecar 定位；稳定序）。<see cref="ScannedSourceFile.IsHistorical"/> 恒 false——
/// 卡组不保留历史版本（R11），「历史卡组」这个概念不存在。
/// </param>
/// <param name="ManifestSidecars">`manifest.metadata` 原样（**声明**；B4 的「声明必须落地」契约来源）。</param>
/// <param name="SidecarFiles">
/// **完整清单**＝`ManifestSidecars` ∪ 发现面（`cards*` 顶层 ＋ `effects/` 递归的 `*.meta.json`，
/// 排除插件清单镜像 `assets.meta.json`）；`/` 归一、去重、`Ordinal` 排序。
/// </param>
public sealed record PackageScan(
    string RootDirectory,
    PackageManifest Manifest,
    IReadOnlyList<string> CardDirectories,
    string? EffectsDirectory,
    string? ComponentsDirectory,
    string? TextsFile,
    string? KeywordsFile,
    IReadOnlyList<ScannedSourceFile> CardFiles,
    IReadOnlyList<ScannedSourceFile> DeckFiles,
    IReadOnlyList<string> ManifestSidecars,
    IReadOnlyList<string> SidecarFiles);

/// <summary>
/// **包扫描器**（I3）：只做「发现包／枚举目录」——卡目录、效果库、文本、词条、sidecar 定位。
/// 与注册逻辑解耦（注册＝<see cref="PackageRegistrar"/>）；同时供 <see cref="EngineHost"/> 枚举包目录。
/// 目录约定只有一条硬约束：<c>versions/&lt;version&gt;/</c>（R3 授权的自动扫描）。
/// </summary>
public static class PackageScanner
{
    /// <summary>卡目录名前缀（<c>cards</c> / <c>cards-support</c> …）。</summary>
    public const string CardsDirectoryPrefix = "cards";

    /// <summary>
    /// 卡组目录名（R11/D55）：卡组固定落 <c>decks/</c> **顶层**，文件名为 <c>&lt;局部ID&gt;.deck.json</c>。
    /// <para>
    /// 只扫顶层、不认 <c>decks-*</c> 变体（与 <c>cards/</c> 口径一致）：卡组的规模不需要分目录；
    /// 非约定位置的文件要走 <c>manifest.metadata</c> 显式声明（「声明即落地」通道）。
    /// </para>
    /// </summary>
    public const string DeckDirectoryName = "decks";

    /// <summary>卡组源文件模式串（单处定义——注册面不得散落字面量）。</summary>
    public const string DeckFilePattern = "*.deck.json";

    /// <summary>效果库目录名（旧格式 <c>*.prefab.json</c>；U5-a 起仅作无 v4 包的过渡兼容）。</summary>
    public const string EffectsDirectoryName = "effects";

    /// <summary>组件资产目录名（v4：<c>*.component.json</c>；生产装载的首选来源）。</summary>
    public const string ComponentsDirectoryName = "effects-v4";

    /// <summary>卡面文本文件名。</summary>
    public const string TextsFileName = "texts.json";

    /// <summary>词条声明文件名。</summary>
    public const string KeywordsFileName = "keywords.json";

    /// <summary>历史版本目录名（<c>versions/&lt;version&gt;/</c>）。</summary>
    public const string VersionsDirectoryName = "versions";

    /// <summary>扫描一个包目录（不做结构性校验；目录不存在＝空布局）。</summary>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public static PackageScan Scan(string packageRoot, PackageManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        var root = Path.GetFullPath(packageRoot);
        var cardDirectories = DiscoverCardDirectories(root);
        var cardFiles = new List<ScannedSourceFile>();
        foreach (var directory in cardDirectories)
        {
            foreach (var file in Directory.EnumerateFiles(directory, CardDataLoader.FilePattern, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                cardFiles.Add(Describe(root, file));
            }
        }

        // 历史版本目录（R3/D55）：按目录约定自动扫描，与当前正式版并存参与注册。
        foreach (var versionDirectory in EnumerateVersionDirectories(root))
        {
            foreach (var directory in DiscoverCardDirectories(versionDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, CardDataLoader.FilePattern, SearchOption.TopDirectoryOnly)
                             .OrderBy(f => f, StringComparer.Ordinal))
                {
                    cardFiles.Add(Describe(root, file, isHistorical: true));
                }
            }
        }

        // 卡组：只扫 decks/ 顶层（D55）。不递归、不认 decks-* 变体——与 cards/ 同口径。
        var deckFiles = new List<ScannedSourceFile>();
        var decksDirectory = Path.Combine(root, DeckDirectoryName);
        if (Directory.Exists(decksDirectory))
        {
            foreach (var file in Directory
                         .EnumerateFiles(decksDirectory, DeckFilePattern, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                deckFiles.Add(Describe(root, file));
            }
        }

        var effectsDirectory = Path.Combine(root, EffectsDirectoryName);
        var componentsDirectory = Path.Combine(root, ComponentsDirectoryName);
        var textsFile = Path.Combine(root, TextsFileName);
        var keywordsFile = Path.Combine(root, KeywordsFileName);

        var manifestSidecars = manifest.MetadataList;
        var sidecarFiles = manifestSidecars
            .Concat(DiscoverSidecars(root, cardDirectories, decksDirectory))
            .Select(relative => relative.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();

        return new PackageScan(
            root,
            manifest,
            cardDirectories,
            Directory.Exists(effectsDirectory) ? effectsDirectory : null,
            Directory.Exists(componentsDirectory) ? componentsDirectory : null,
            File.Exists(textsFile) ? textsFile : null,
            File.Exists(keywordsFile) ? keywordsFile : null,
            cardFiles,
            deckFiles,
            manifestSidecars,
            sidecarFiles);
    }

    /// <summary>
    /// 发现面（F11）：`cards*` **顶层** ＋ `decks/` **顶层** ＋ `effects/` **递归**的 `*.meta.json`（相对包根、`/` 分隔）。
    /// 排除插件清单镜像 <see cref="PluginAssetRegistrar.ManifestFileName"/>（常量复用，不另写字面量）；
    /// 不含 `versions/**`（历史版本仍走卡源文件路径，见已知边界 B-1；卡组更不归档）。
    /// </summary>
    private static IReadOnlyList<string> DiscoverSidecars(
        string root, IReadOnlyList<string> cardDirectories, string decksDirectory)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        var pattern = "*" + AssetSidecarJson.SidecarExtension;

        foreach (var directory in cardDirectories)
        {
            Add(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
        }

        if (Directory.Exists(decksDirectory))
        {
            Add(Directory.EnumerateFiles(decksDirectory, pattern, SearchOption.TopDirectoryOnly));
        }

        var effectsDirectory = Path.Combine(root, EffectsDirectoryName);
        if (Directory.Exists(effectsDirectory))
        {
            Add(Directory.EnumerateFiles(effectsDirectory, pattern, SearchOption.AllDirectories));
        }

        return result.ToArray();

        void Add(IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), PluginAssetRegistrar.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 插件清单镜像不是资产 sidecar
                }

                result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }
    }

    /// <summary>
    /// 枚举**当前版全部 sidecar**（F1 的内容指纹输入面；全仓唯一实现）：递归 `*.meta.json`，
    /// 排除 <c>versions/**</c>（历史归档不参与当前版指纹）与插件清单镜像
    /// <see cref="PluginAssetRegistrar.ManifestFileName"/>；相对路径 `/` 归一、<see cref="StringComparer.Ordinal"/> 排序。
    /// <para>
    /// 与注册发现面（<see cref="Scan"/> 的 <c>cards*</c> 顶层 ＋ <c>effects/</c> 递归）**口径不同**：
    /// 指纹＝包内容，注册＝约定目录内的资产（已知边界 B-7）。
    /// </para>
    /// </summary>
    /// <param name="packageRoot">包根目录（不存在＝空）。</param>
    /// <exception cref="ArgumentException">packageRoot 为 null/空白。</exception>
    public static IReadOnlyList<string> EnumerateCurrentSidecars(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(root, "*" + AssetSidecarJson.SidecarExtension, SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(relative => !IsExcludedFromCurrentSidecars(relative))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsExcludedFromCurrentSidecars(string relative)
        => relative.StartsWith(VersionsDirectoryName + "/", StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               Path.GetFileName(relative),
               PluginAssetRegistrar.ManifestFileName,
               StringComparison.OrdinalIgnoreCase);

    /// <summary>发现卡目录（<c>cards</c>／<c>cards-*</c>；无 sidecar 的子目录不算）。</summary>
    /// <exception cref="ArgumentNullException">packageRoot 为 null。</exception>
    public static IReadOnlyList<string> DiscoverCardDirectories(string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);
        if (!Directory.Exists(packageRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(packageRoot)
            .Where(directory => IsCardDirectory(Path.GetFileName(directory)))
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>枚举历史版本目录（<c>versions/*</c>；稳定序）。</summary>
    /// <exception cref="ArgumentNullException">packageRoot 为 null。</exception>
    public static IReadOnlyList<string> EnumerateVersionDirectories(string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);
        var versionsRoot = Path.Combine(packageRoot, VersionsDirectoryName);
        if (!Directory.Exists(versionsRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(versionsRoot)
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>枚举一个目录下的包根（含 <c>manifest.json</c>；稳定序）。供宿主热加载使用。</summary>
    /// <exception cref="ArgumentNullException">packsDirectory 为 null。</exception>
    public static IReadOnlyList<string> EnumeratePackageDirectories(string packsDirectory)
    {
        ArgumentNullException.ThrowIfNull(packsDirectory);
        if (!Directory.Exists(packsDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(packsDirectory)
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>读取包内 <c>texts.json</c>（不存在＝空；非法＝明确失败）。</summary>
    /// <exception cref="ArgumentNullException">packageRoot 为 null。</exception>
    /// <exception cref="InvalidDataException">结构非法。</exception>
    public static IReadOnlyDictionary<string, string> ReadTexts(string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);
        var path = Path.Combine(packageRoot, TextsFileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in document.RootElement.EnumerateObject())
        {
            texts[pair.Name] = pair.Value.GetString() ?? "";
        }

        return texts;
    }

    /// <summary>读取包内 <c>keywords.json</c>（不存在＝空；非法＝明确失败）。供词条加载插件使用。</summary>
    /// <exception cref="ArgumentNullException">packageRoot 为 null。</exception>
    /// <exception cref="InvalidDataException">结构非法。</exception>
    public static IReadOnlyList<CardPackKeyword> ReadKeywords(string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);
        var path = Path.Combine(packageRoot, KeywordsFileName);
        if (!File.Exists(path))
        {
            return Array.Empty<CardPackKeyword>();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("keywords.json 须为数组。");
            }

            var result = new List<CardPackKeyword>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = Text(item, "id") ?? throw new InvalidDataException("词条缺少 id。");
                if (!seen.Add(id))
                {
                    throw new InvalidDataException($"词条 id 重复：{id}");
                }

                var label = Text(item, "label") ?? id;
                var battle = item.TryGetProperty("battle", out var b) && b.ValueKind == JsonValueKind.True;
                var program = ReadProgram(item, id);
                var bindings = new List<KeywordEffectBinding>();
                if (item.TryGetProperty("bindings", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var binding in list.EnumerateArray())
                    {
                        var prefabId = Text(binding, "prefabId")
                            ?? throw new InvalidDataException($"词条 '{id}' 的绑定缺少 prefabId。");
                        var effectName = Text(binding, "effectName")
                            ?? throw new InvalidDataException($"词条 '{id}' 的绑定缺少 effectName。");
                        bindings.Add(new KeywordEffectBinding(prefabId, effectName));
                    }
                }

                // 行为承载：效果绑定（csx）或词条程序（规则/技能）至少一项。
                if (bindings.Count == 0 && program is null)
                {
                    throw new InvalidDataException($"词条 '{id}' 没有任何效果绑定或程序（无行为词条不允许发布）。");
                }

                result.Add(new CardPackKeyword(id, label, battle, bindings, program));
            }

            return result;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"keywords.json 无法解析：{e.Message}");
        }
    }

    /// <summary>
    /// 读取词条的 <c>program</c> 字段（对象或 JSON 字符串；缺省/空＝null）。
    /// 结构校验交给 <see cref="KeywordProgram.Parse"/>（非法＝明确失败、不静默丢弃）。
    /// </summary>
    private static string? ReadProgram(JsonElement item, string keywordId)
    {
        if (!item.TryGetProperty("program", out var program)
            || program.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var json = program.ValueKind == JsonValueKind.String ? program.GetString() : program.GetRawText();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            KeywordProgram.Parse(json);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"词条 '{keywordId}' 的 program 非法：{e.Message}");
        }

        return json;
    }

    private static ScannedSourceFile Describe(string root, string sourcePath, bool isHistorical = false)
    {
        var sidecar = AssetSidecarJson.SidecarPathFor(sourcePath);
        return new ScannedSourceFile(
            Path.GetFileName(sourcePath),
            sourcePath,
            File.Exists(sidecar) ? sidecar : null,
            Path.GetRelativePath(root, sourcePath).Replace('\\', '/'),
            isHistorical);
    }

    private static bool IsCardDirectory(string name) =>
        string.Equals(name, CardsDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(CardsDirectoryPrefix + "-", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
