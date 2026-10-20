using System.Text;
using System.Text.Json;
using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 资产引用（D47：元数据内的依赖／冲突声明）：<c>kind ＋ 应用层 ID ＋ 版本约束</c>。
/// 版本约束沿用 <see cref="AssetVersionConstraint"/> 的解析/匹配（包级 dependsOn 退役不波及本复用）。
/// </summary>
/// <param name="Kind">被引用资产的种类。</param>
/// <param name="AppId">应用层 ID（<c>pack/{包id}/{局部ID}</c>；跨包必须写全名）。</param>
/// <param name="VersionConstraint">版本约束（<c>1.2.3</c>／<c>1.*.*</c>／<c>1.^.*</c>）。</param>
public sealed record AssetReference(AssetKind Kind, string AppId, string VersionConstraint)
{
    /// <summary>
    /// 给定版本是否满足本引用（S4：<see cref="AssetVersionConstraint"/> 的复用点——**资产级**依赖匹配；
    /// 包级 <c>dependsOn</c> 已退役，不影响本用法的保留）。
    /// </summary>
    public bool IsSatisfiedBy(AssetVersion version)
        => AssetVersionConstraint.TryParse(VersionConstraint, out var constraint)
            && constraint.IsSatisfiedBy(version);
}

/// <summary>
/// 资产元数据（R1/R2/D47）：sidecar 内的一条资产声明。
/// <paramref name="Guid"/> 由 <see cref="AssetIdentity"/> 派生（读入时以文件值为准，校验一致性由注册面负责）。
/// </summary>
/// <param name="Guid">资产 GUID（版本级身份）。</param>
/// <param name="Kind">资产种类。</param>
/// <param name="Id">包内局部 ID。</param>
/// <param name="Version">资产版本（内含资产＝继承宿主文件版本）。</param>
/// <param name="Anchor">内含资产的锚（指向 <c>stableKey</c> 或事件 <c>id</c>；独立资产＝null）。</param>
/// <param name="Loader">加载器名（须为已注册加载器；内含资产留空＝null）。</param>
/// <param name="DependsOn">依赖声明。</param>
/// <param name="ConflictsWith">冲突声明（双向硬冲突；对内含资产留空）。</param>
/// <param name="ServiceKind">
/// 服务子类（D62；仅 <see cref="AssetKind.Service"/> 使用：judicator／assetService／keyword／cardComponent／action／projection／observer…）。
/// 其余种类为 null。
/// </param>
public sealed record AssetMetadata(
    Guid Guid,
    AssetKind Kind,
    string Id,
    AssetVersion Version,
    string? Anchor,
    string? Loader,
    IReadOnlyList<AssetReference> DependsOn,
    IReadOnlyList<AssetReference> ConflictsWith,
    string? ServiceKind = null)
{
    /// <summary>
    /// 非法版本号原文（**资产级**判定：D27／诊断分级表「版本号非法 ⇒ 该资产非法，不生成」）。
    /// 非 null 时 <see cref="Version"/> 为默认值、注册面须拒绝该资产（不阻断包）。
    /// </summary>
    public string? InvalidVersion { get; init; }
}

/// <summary>
/// 元数据 sidecar（D47）：<c>{ schemaVersion, packageId, source, assets[] }</c>——一个源文件的全部资产条目。
/// </summary>
/// <param name="SchemaVersion">schema 版本（当前＝<see cref="AssetSidecarJson.CurrentSchemaVersion"/>）。</param>
/// <param name="PackageId">所属包 id（人类可读）。</param>
/// <param name="Source">源文件相对路径（<b>不含</b> <c>.meta.json</c> 后缀）。</param>
/// <param name="Assets">该文件声明的全部资产条目。</param>
public sealed record AssetSidecar(
    int SchemaVersion,
    string PackageId,
    string Source,
    IReadOnlyList<AssetMetadata> Assets);

/// <summary>
/// sidecar 读写（R6/D46-D49）：文件名 <c>&lt;源文件名&gt;.meta.json</c>（同目录一对一可逆映射）；
/// **读宽**（注释／尾逗号／键名大小写不敏感）**写窄**（固定键序／2 空格／LF／UTF-8 无 BOM）。
/// 版本一律按段数原样写出、读入后靠<b>规范形</b>归一（禁止字符串比对）。
/// </summary>
public static class AssetSidecarJson
{
    /// <summary>当前 sidecar schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>sidecar 后缀。</summary>
    public const string SidecarExtension = ".meta.json";

    /// <summary>由源文件名推出 sidecar 文件名（<c>a.card.json</c> → <c>a.card.json.meta.json</c>）。</summary>
    /// <exception cref="ArgumentNullException">sourceFileName 为 null。</exception>
    public static string SidecarFileNameFor(string sourceFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        return sourceFileName + SidecarExtension;
    }

    /// <summary>由 sidecar 文件名反推源文件名（非 sidecar ＝ false）。</summary>
    public static bool TryGetSourceFileName(string sidecarFileName, out string sourceFileName)
    {
        sourceFileName = "";
        if (string.IsNullOrWhiteSpace(sidecarFileName)
            || !sidecarFileName.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sourceFileName = sidecarFileName[..^SidecarExtension.Length];
        return sourceFileName.Length > 0;
    }

    /// <summary>源文件绝对路径 → 同目录 sidecar 绝对路径。</summary>
    /// <exception cref="ArgumentException">path 为 null/空白。</exception>
    public static string SidecarPathFor(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var directory = Path.GetDirectoryName(sourcePath);
        var name = SidecarFileNameFor(Path.GetFileName(sourcePath));
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>读 sidecar 文件（不存在＝<see cref="FileNotFoundException"/>；非法＝<see cref="FormatException"/>）。</summary>
    /// <exception cref="ArgumentException">path 为 null/空白。</exception>
    public static AssetSidecar ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>解析 sidecar JSON（读宽：注释／尾逗号／键名大小写；非法＝<see cref="FormatException"/>）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static AssetSidecar Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"sidecar 无法解析：{ex.Message}", ex);
        }

        using (document)
        {
            return Read(document.RootElement);
        }
    }

    /// <summary>从 JSON 根元素读取（结构非法＝<see cref="FormatException"/>）。</summary>
    /// <exception cref="ArgumentNullException">root 为默认值。</exception>
    public static AssetSidecar Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("sidecar 根须为对象。");
        }

        var schemaVersion = ReadSchemaVersion(root);
        var packageId = RequireString(root, "packageId");
        var source = RequireString(root, "source");
        var assets = ReadAssets(root);
        return new AssetSidecar(schemaVersion, packageId, source, assets);
    }

    /// <summary>写出规范 JSON（固定键序／2 空格缩进／LF；不含 BOM）。</summary>
    /// <exception cref="ArgumentNullException">sidecar 为 null。</exception>
    public static string ToJson(AssetSidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", sidecar.SchemaVersion);
            writer.WriteString("packageId", sidecar.PackageId);
            writer.WriteString("source", sidecar.Source);

            writer.WriteStartArray("assets");
            foreach (var asset in sidecar.Assets)
            {
                WriteAsset(writer, asset);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        // 写窄：结构换行统一 LF（Utf8JsonWriter 按平台换行；字符串内的 CR/LF 已被转义，不受影响）。
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>写 sidecar 文件（UTF-8 无 BOM；目录须已存在）。</summary>
    /// <exception cref="ArgumentNullException">sidecar 为 null。</exception>
    public static void WriteFile(string path, AssetSidecar sidecar)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, ToJson(sidecar), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteAsset(Utf8JsonWriter writer, AssetMetadata asset)
    {
        writer.WriteStartObject();
        writer.WriteString("guid", asset.Guid.ToString());
        writer.WriteString("kind", AssetKindTokens.Token(asset.Kind));
        writer.WriteString("id", asset.Id);
        writer.WriteString("version", asset.InvalidVersion ?? asset.Version.ToString());

        if (asset.Anchor is { } anchor)
        {
            writer.WriteString("anchor", anchor);
        }

        if (asset.Loader is { } loader)
        {
            writer.WriteString("loader", loader);
        }

        if (asset.ServiceKind is { } serviceKind)
        {
            writer.WriteString("serviceKind", serviceKind);
        }

        WriteReferences(writer, "dependsOn", asset.DependsOn);
        WriteReferences(writer, "conflictsWith", asset.ConflictsWith);
        writer.WriteEndObject();
    }

    private static void WriteReferences(Utf8JsonWriter writer, string name, IReadOnlyList<AssetReference> references)
    {
        writer.WriteStartArray(name);
        foreach (var reference in references)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", AssetKindTokens.Token(reference.Kind));
            writer.WriteString("id", reference.AppId);
            writer.WriteString("version", reference.VersionConstraint);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (!TryGet(root, "schemaVersion", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var version))
        {
            throw new FormatException("sidecar 缺少整数字段 'schemaVersion'。");
        }

        if (version != CurrentSchemaVersion)
        {
            throw new FormatException(
                $"sidecar schemaVersion {version} 不受支持（当前支持 {CurrentSchemaVersion}）。");
        }

        return version;
    }

    private static IReadOnlyList<AssetMetadata> ReadAssets(JsonElement root)
    {
        if (!TryGet(root, "assets", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("sidecar 缺少数组字段 'assets'。");
        }

        var result = new List<AssetMetadata>();
        var seen = new HashSet<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            var asset = ReadAsset(item);
            if (!seen.Add(asset.Guid))
            {
                throw new FormatException($"sidecar 内资产 GUID '{asset.Guid}' 重复声明。");
            }

            result.Add(asset);
        }

        return result;
    }

    private static AssetMetadata ReadAsset(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("sidecar 的 assets 条目须为对象。");
        }

        var guidText = RequireString(item, "guid");
        if (!Guid.TryParse(guidText, out var guid))
        {
            throw new FormatException($"资产 guid '{guidText}' 非法（期望 GUID）。");
        }

        var kindText = RequireString(item, "kind");
        if (!AssetKindTokens.TryParse(kindText, out var kind))
        {
            throw new FormatException($"资产种类 '{kindText}' 非法（期望 card/effect/trigger/handler/service）。");
        }

        var id = RequireString(item, "id");
        var versionText = RequireString(item, "version");

        // 版本号**不在此层拒绝**：非法版本号是「资产级」判定（该资产非法、不生成），
        // 保留原文交注册面报告，避免把资产级问题误升为包级结构性拒绝。
        if (!AssetVersion.TryParse(versionText, out var version))
        {
            return new AssetMetadata(
                guid,
                kind,
                id,
                default,
                OptionalString(item, "anchor"),
                OptionalString(item, "loader"),
                ReadReferences(item, "dependsOn"),
                ReadReferences(item, "conflictsWith"),
                OptionalString(item, "serviceKind"))
            {
                InvalidVersion = versionText,
            };
        }

        return new AssetMetadata(
            guid,
            kind,
            id,
            version,
            OptionalString(item, "anchor"),
            OptionalString(item, "loader"),
            ReadReferences(item, "dependsOn"),
            ReadReferences(item, "conflictsWith"),
            OptionalString(item, "serviceKind"));
    }

    private static IReadOnlyList<AssetReference> ReadReferences(JsonElement item, string name)
    {
        if (!TryGet(item, name, out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<AssetReference>();
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"资产字段 '{name}' 须为数组。");
        }

        var result = new List<AssetReference>();
        foreach (var entry in array.EnumerateArray())
        {
            var kindText = RequireString(entry, "kind");
            if (!AssetKindTokens.TryParse(kindText, out var kind))
            {
                throw new FormatException($"'{name}' 的资产种类 '{kindText}' 非法。");
            }

            var appId = RequireString(entry, "id");
            var constraint = RequireString(entry, "version");
            if (!AssetVersionConstraint.TryParse(constraint, out _))
            {
                throw new FormatException($"'{name}' 对 '{appId}' 的版本约束 '{constraint}' 非法。");
            }

            result.Add(new AssetReference(kind, appId, constraint));
        }

        return result;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string RequireString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !TryGet(element, name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"sidecar 缺少字符串字段 '{name}'。");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException($"sidecar 字段 '{name}' 为空白。");
        }

        return text!;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"sidecar 字段 '{name}' 须为字符串。");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text;
    }
}
