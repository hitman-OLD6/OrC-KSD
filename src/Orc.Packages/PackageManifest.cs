using System.Text.Json;
using Orc.Core;

namespace Orc.Packages;

/// <summary>包类型（S5）：插件（冷加载、静态资产）／卡包（热加载、纯卡牌数据）。</summary>
public enum PackageKind
{
    /// <summary>插件。</summary>
    Plugin = 0,

    /// <summary>卡包。</summary>
    CardPack = 1,
}

/// <summary>包级依赖（manifest 的 <c>dependsOn</c> 条目；**已退役**——读宽保留、写窄不再产出）。</summary>
/// <param name="Id">目标包 id。</param>
/// <param name="Version">版本约束（<c>x.*.*</c> / <c>x.y.*</c> 等；仅作迁移提示）。</param>
public sealed record PackageDependency(string Id, string Version);

/// <summary>
/// 包内资产声明（manifest 的 <c>assets</c> 条目）：加载方/来源类型 ＋ 包内短名 ＋ 相对路径 ＋ 作用域/相。
/// 资产版本**不再随包版本**（S4：包版本＝内容指纹，不参与任何身份）——由 <paramref name="Version"/> 自持。
/// </summary>
/// <param name="Type">加载方/来源 token（<c>json</c> / <c>csx</c> / <c>asm</c>）。</param>
/// <param name="Name">包内短名（包内唯一）。</param>
/// <param name="Path">相对包根的资源路径。</param>
/// <param name="Scope">作用域（默认 <see cref="AssetScope.Match"/>）。</param>
/// <param name="Phase">对局级执行相（默认 <see cref="AssetActivationPhase.PreCommand"/>；Process 作用域下无意义）。</param>
/// <param name="Version">资产版本（缺省＝1.0.0）。</param>
public sealed record PackageAsset(
    string Type,
    string Name,
    string Path,
    AssetScope Scope = AssetScope.Match,
    AssetActivationPhase Phase = AssetActivationPhase.PreCommand,
    AssetVersion? Version = null)
{
    /// <summary>生效的资产版本（未声明＝1.0.0）。</summary>
    public AssetVersion EffectiveVersion => Version ?? new AssetVersion(1, 0, 0);
}

/// <summary>
/// 包清单（manifest.json；v2＝**元数据列表容器**）。
/// 字段：<c>schemaVersion</c>／<c>id</c>／<c>guid</c>／<c>kind</c>／<c>name?</c>／<c>version</c>／<c>metadata[]</c>。
/// 读宽写窄（键名大小写不敏感；非法＝结构化失败或抛错由调用方定）。
/// <para>
/// **字段分工（D50 补全）**：<paramref name="Id"/>＝人类可读包标识（应用层前缀 <c>pack/{id}/</c> 与目录名用）；
/// <paramref name="Guid"/>＝包稳定身份（资产名 <c>@{包GUID8}</c> 与 GUID 派生输入）。二者**均必填**。
/// </para>
/// <para>
/// **包版本＝内容指纹**（<see cref="PackageVersion"/>）：不参与依赖、不进 lock、不进资产 GUID、不参与择版。
/// </para>
/// <para>
/// 过渡期兼容：<paramref name="Dependencies"/>／<paramref name="Assets"/> 是 v1 的包级声明（读宽沿用；
/// 包级 <c>dependsOn</c> **读时忽略并产出一条迁移告警、写时不再产出**；<c>assets[]</c> 归 S10a 迁移后移除）。
/// </para>
/// </summary>
/// <param name="Kind">包类型。</param>
/// <param name="Id">人类可读包 id。</param>
/// <param name="Guid">稳定身份（跨版本不变；资产名继承其前 8 位）。</param>
/// <param name="Version">包版本＝内容指纹（含 <c>-tmp</c> 标记）。</param>
/// <param name="Dependencies">包级依赖（已退役；读入保留供迁移告警）。</param>
/// <param name="Assets">包内资产声明（v1 遗留；读入保留）。</param>
/// <param name="Name">显示名（可选）。</param>
/// <param name="Metadata">sidecar 路径列表（v2 元数据列表；相对包根）。</param>
/// <param name="SchemaVersion">清单 schema 版本（缺省＝2；读到 v1 时为 1）。</param>
/// <param name="Requires">
/// **纯声明性**包依赖（包 ID 列表）：不参与依赖解析、不进 lock、不驱动装载顺序（D31 的核心意图不变）。
/// 唯一用途是承载 R8「新建卡包必须给定期望依赖的包 ID」，供编辑器与诊断展示。
/// </param>
public sealed record PackageManifest(
    PackageKind Kind,
    string Id,
    Guid Guid,
    PackageVersion Version,
    IReadOnlyList<PackageDependency> Dependencies,
    IReadOnlyList<PackageAsset> Assets,
    string? Name = null,
    IReadOnlyList<string>? Metadata = null,
    int SchemaVersion = PackageManifest.CurrentSchemaVersion,
    IReadOnlyList<string>? Requires = null)
{
    /// <summary>纯声明性包依赖（未声明＝空）。</summary>
    public IReadOnlyList<string> RequiredPackages => Requires ?? Array.Empty<string>();

    /// <summary>当前 manifest schema 版本。</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>sidecar 路径列表（v2；未声明＝空）。</summary>
    public IReadOnlyList<string> MetadataList => Metadata ?? Array.Empty<string>();

    /// <summary>包 GUID 前 8 位（小写十六进制；资产名继承用）。</summary>
    public string Guid8 => Guid.ToString("N")[..8];

    /// <summary>缓存域（<c>{包GUID8}#{版本}</c>；插件脚本求值缓存键用）。</summary>
    public string CacheScope => $"{Guid8}#{Version}";

    /// <summary>派生资产全名：<c>{类型名}：{资产名}@{包GUID8}#{资产版本}</c>（包版本不参与身份）。</summary>
    /// <exception cref="ArgumentNullException">asset 为 null。</exception>
    /// <exception cref="FormatException">资产类型 token 非法。</exception>
    public AssetName AssetNameOf(PackageAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!AssetName.TryParseKind(asset.Type, out var kind))
        {
            throw new FormatException($"资产 '{asset.Name}' 的类型 '{asset.Type}' 非法（期望 json/csx/asm）。");
        }

        return new AssetName(kind, asset.Name, Guid8, asset.EffectiveVersion);
    }

    /// <summary>解析并校验 manifest JSON 文本（非法＝<see cref="FormatException"/>）。</summary>
    public static PackageManifest Parse(string json) => Parse(json, out _);

    /// <summary>解析并校验 manifest JSON 文本，并返回迁移告警（如已退役的 <c>dependsOn</c>）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static PackageManifest Parse(string json, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return Read(document.RootElement, out warnings);
    }

    /// <summary>从 JSON 元素读取并校验（非法＝<see cref="FormatException"/>）。</summary>
    public static PackageManifest Read(JsonElement root) => Read(root, out _);

    /// <summary>从 JSON 元素读取并校验，并返回迁移告警。</summary>
    /// <exception cref="ArgumentNullException">root 为默认值。</exception>
    public static PackageManifest Read(JsonElement root, out IReadOnlyList<string> warnings)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("manifest 根须为对象。");
        }

        var schemaVersion = ReadSchemaVersion(root);
        var kind = ReadKind(RequireString(root, "kind"));
        var id = RequireString(root, "id");
        var guidText = RequireString(root, "guid");
        if (!Guid.TryParse(guidText, out var guid))
        {
            throw new FormatException($"manifest guid '{guidText}' 非法（期望 GUID）。");
        }

        var versionText = RequireString(root, "version");
        if (!PackageVersion.TryParse(versionText, out var version))
        {
            throw new FormatException($"manifest version '{versionText}' 非法（期望内容指纹串，可带 -tmp）。");
        }

        warnings = ReadDependencies(root, id, out var dependencies);
        return new PackageManifest(
            kind,
            id,
            guid,
            version,
            dependencies,
            ReadAssets(root),
            ReadName(root),
            ReadMetadata(root),
            schemaVersion,
            ReadRequires(root));
    }

    /// <summary>写出规范 manifest JSON（读宽写窄；v2 键序固定）。</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("id", Id);
            writer.WriteString("guid", Guid.ToString());
            writer.WriteString("kind", KindToken(Kind));
            if (Name is { } name)
            {
                writer.WriteString("name", name);
            }

            writer.WriteString("version", Version.ToString());

            writer.WriteStartArray("metadata");
            foreach (var sidecarPath in MetadataList)
            {
                writer.WriteStringValue(sidecarPath);
            }

            writer.WriteEndArray();

            // 纯声明性依赖（R8）：只写包 ID，不承载版本，不参与任何解析。
            if (Requires is { Count: > 0 })
            {
                writer.WriteStartArray("requires");
                foreach (var required in Requires)
                {
                    writer.WriteStringValue(required);
                }

                writer.WriteEndArray();
            }

            // 过渡期（S2/S10a）：v1 的 assets[] 仍按原样写出（含各自资产版本）；dependsOn 已退役、不再产出。
            if (Assets.Count > 0)
            {
                writer.WriteStartArray("assets");
                foreach (var asset in Assets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", asset.Type.ToLowerInvariant());
                    writer.WriteString("name", asset.Name);
                    writer.WriteString("path", asset.Path);
                    writer.WriteString("scope", ScopeToken(asset.Scope));
                    writer.WriteString("phase", PhaseToken(asset.Phase));
                    if (asset.Version is { } assetVersion)
                    {
                        writer.WriteString("version", assetVersion.ToString());
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string KindToken(PackageKind kind) => kind switch
    {
        PackageKind.Plugin => "plugin",
        PackageKind.CardPack => "cardpack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未定义的包类型。"),
    };

    private static PackageKind ReadKind(string token) => token.Trim().ToLowerInvariant() switch
    {
        "plugin" => PackageKind.Plugin,
        "cardpack" or "card_pack" => PackageKind.CardPack,
        _ => throw new FormatException($"manifest kind '{token}' 非法（期望 plugin/cardpack）。"),
    };

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (!TryGet(root, "schemaVersion", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 1; // 无 schemaVersion ＝ v1（读宽：旧清单仍可解析）
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version))
        {
            throw new FormatException("manifest schemaVersion 须为整数。");
        }

        if (version is < 1 or > CurrentSchemaVersion)
        {
            throw new FormatException(
                $"manifest schemaVersion {version} 不受支持（当前支持 1..{CurrentSchemaVersion}）。");
        }

        return version;
    }

    private static string? ReadName(JsonElement root)
    {
        if (!TryGet(root, "name", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("manifest name 须为字符串。");
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<string> ReadMetadata(JsonElement root)
    {
        if (!TryGet(root, "metadata", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("manifest metadata 须为数组。");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new FormatException("manifest metadata 条目须为非空白字符串（sidecar 相对路径）。");
            }

            var path = item.GetString()!;
            if (!seen.Add(path))
            {
                throw new FormatException($"manifest metadata 条目 '{path}' 重复。");
            }

            result.Add(path);
        }

        return result;
    }

    /// <summary>读取纯声明性包依赖（<c>requires</c>：[包 ID…]）；读宽：去重、忽略大小写差异保留原样。</summary>
    private static IReadOnlyList<string> ReadRequires(JsonElement root)
    {
        if (!TryGet(root, "requires", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("manifest requires 须为数组（包 ID 列表）。");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new FormatException("manifest requires 条目须为非空白字符串（包 ID）。");
            }

            var id = item.GetString()!.Trim();
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ReadDependencies(
        JsonElement root, string packageId, out IReadOnlyList<PackageDependency> dependencies)
    {
        var result = new List<PackageDependency>();
        dependencies = result;
        if (!TryGet(root, "dependsOn", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("manifest dependsOn 须为数组。");
        }

        foreach (var item in array.EnumerateArray())
        {
            var id = RequireString(item, "id");
            var version = RequireString(item, "version");
            result.Add(new PackageDependency(id, version));
        }

        // 读宽：旧清单仍可解析，但包级依赖已退役——忽略并产出一条迁移告警（不报错）。
        return result.Count == 0
            ? Array.Empty<string>()
            : new[]
            {
                $"{packageId}：manifest 含已退役的包级 dependsOn（{result.Count} 项，已忽略）"
                + "——依赖须迁移为资产级（sidecar 的 dependsOn）。",
            };
    }

    private static IReadOnlyList<PackageAsset> ReadAssets(JsonElement root)
    {
        var result = new List<PackageAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!TryGet(root, "assets", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("manifest assets 须为数组。");
        }

        foreach (var item in array.EnumerateArray())
        {
            var type = RequireString(item, "type");
            if (!AssetName.TryParseKind(type, out _))
            {
                throw new FormatException($"资产类型 '{type}' 非法（期望 json/csx/asm）。");
            }

            var name = RequireString(item, "name");
            var path = RequireString(item, "path");
            if (!seen.Add(name))
            {
                throw new FormatException($"manifest 资产短名 '{name}' 重复（包内须唯一）。");
            }

            result.Add(new PackageAsset(type, name, path, ReadScope(item), ReadPhase(item), ReadAssetVersion(item)));
        }

        return result;
    }

    /// <summary>作用域 token（规范小写）。</summary>
    public static string ScopeToken(AssetScope scope) => scope switch
    {
        AssetScope.Process => "process",
        AssetScope.Match => "match",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "未定义的资产作用域。"),
    };

    /// <summary>执行相 token（规范小写）。</summary>
    public static string PhaseToken(AssetActivationPhase phase) => phase switch
    {
        AssetActivationPhase.PostCommand => "postCommand",
        AssetActivationPhase.PreCommand => "preCommand",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "未定义的资产执行相。"),
    };

    private static AssetScope ReadScope(JsonElement item)
    {
        if (!TryGet(item, "scope", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return AssetScope.Match; // 缺省＝对局级（向后兼容）
        }

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            null or "" or "match" => AssetScope.Match,
            "process" => AssetScope.Process,
            var other => throw new FormatException($"资产 scope '{other}' 非法（期望 process/match）。"),
        };
    }

    private static AssetActivationPhase ReadPhase(JsonElement item)
    {
        if (!TryGet(item, "phase", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return AssetActivationPhase.PreCommand; // 缺省＝前置相（向后兼容）
        }

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            null or "" or "precommand" => AssetActivationPhase.PreCommand,
            "postcommand" => AssetActivationPhase.PostCommand,
            var other => throw new FormatException($"资产 phase '{other}' 非法（期望 preCommand/postCommand）。"),
        };
    }

    private static AssetVersion? ReadAssetVersion(JsonElement item)
    {
        if (!TryGet(item, "version", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null; // 缺省＝1.0.0（与包版本解耦）
        }

        if (value.ValueKind != JsonValueKind.String || !AssetVersion.TryParse(value.GetString(), out var version))
        {
            throw new FormatException("manifest assets.version 非法（期望 1..8 段版本）。");
        }

        return version;
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
        if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"manifest 缺少字符串字段 '{name}'。");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException($"manifest 字段 '{name}' 为空白。");
        }

        return text!;
    }
}
