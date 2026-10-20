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
/// <param name="SidecarFiles">manifest 元数据列表指向的 sidecar（相对路径；原样）。</param>
public sealed record PackageScan(
    string RootDirectory,
    PackageManifest Manifest,
    IReadOnlyList<string> CardDirectories,
    string? EffectsDirectory,
    string? TextsFile,
    string? KeywordsFile,
    IReadOnlyList<ScannedSourceFile> CardFiles,
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

    /// <summary>效果库目录名。</summary>
    public const string EffectsDirectoryName = "effects";

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

        var effectsDirectory = Path.Combine(root, EffectsDirectoryName);
        var textsFile = Path.Combine(root, TextsFileName);
        var keywordsFile = Path.Combine(root, KeywordsFileName);

        return new PackageScan(
            root,
            manifest,
            cardDirectories,
            Directory.Exists(effectsDirectory) ? effectsDirectory : null,
            File.Exists(textsFile) ? textsFile : null,
            File.Exists(keywordsFile) ? keywordsFile : null,
            cardFiles,
            manifest.MetadataList);
    }

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
