using System.Text;
using System.Text.Json;
using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// lock 条目：一条**已定为正式版**的资产版本（D6/D32）。
/// <para>
/// 含 <c>kind ＋ appId ＋ version</c> 供冲突报告指名（D7：报错并指名双方与版本）；
/// **不含包版本**（包版本不参与任何解析）。
/// </para>
/// </summary>
/// <param name="AssetGuid">资产版本 GUID。</param>
/// <param name="Kind">资产种类。</param>
/// <param name="AppId">应用层 ID。</param>
/// <param name="Version">资产版本。</param>
public sealed record AssetLockEntry(Guid AssetGuid, AssetKind Kind, string AppId, AssetVersion Version);

/// <summary>
/// 一份 lock（D6/D14）：声明某包在某域内的资产版本集合。正式域／测试域各一份文件。
/// </summary>
/// <param name="SchemaVersion">lock schema 版本。</param>
/// <param name="PackageId">所属包 id。</param>
/// <param name="Domain">资产域（一份 lock 只属于一个域，跨域绝不互相合并）。</param>
/// <param name="Assets">锁定的资产版本（稳定序：kind → appId → version）。</param>
public sealed record AssetLock(
    int SchemaVersion,
    string PackageId,
    AssetDomain Domain,
    IReadOnlyList<AssetLockEntry> Assets)
{
    /// <summary>lock schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>由条目构造（自动去重并按稳定序排列）。</summary>
    /// <exception cref="ArgumentException">packageId 为 null/空白。</exception>
    public static AssetLock Create(string packageId, AssetDomain domain, IEnumerable<AssetLockEntry> assets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(assets);

        var ordered = assets
            .GroupBy(entry => (entry.Kind, entry.AppId, entry.Version))
            .Select(group => group.First())
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.AppId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Version)
            .ToArray();
        return new AssetLock(CurrentSchemaVersion, packageId, domain, ordered);
    }

    /// <summary>lock 文件名（域决定）。</summary>
    public string FileName => AssetLockJson.FileNameFor(Domain);
}

/// <summary>
/// lock 读写（S4）：文件位于包根；**格式与键序稳定**（schemaVersion／packageId／domain／assets；条目键序固定）。
/// 读宽（注释／尾逗号／键名大小写）写窄（2 空格／LF／UTF-8 无 BOM）。
/// </summary>
public static class AssetLockJson
{
    /// <summary>正式 lock 文件名。</summary>
    public const string FormalFileName = "lock.formal.json";

    /// <summary>测试 lock 文件名。</summary>
    public const string TestFileName = "lock.test.json";

    /// <summary>域 → lock 文件名。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的域。</exception>
    public static string FileNameFor(AssetDomain domain) => domain switch
    {
        AssetDomain.Formal => FormalFileName,
        AssetDomain.Test => TestFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "未定义的资产域。"),
    };

    /// <summary>包根下的 lock 路径（不保证存在）。</summary>
    /// <exception cref="ArgumentException">packageRoot 为 null/空白。</exception>
    public static string PathFor(string packageRoot, AssetDomain domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        return Path.Combine(packageRoot, FileNameFor(domain));
    }

    /// <summary>读包内某域的 lock（缺失＝null；不可解析＝<see cref="FormatException"/>）。</summary>
    /// <exception cref="ArgumentException">packageRoot 为 null/空白。</exception>
    public static AssetLock? ReadPackage(string packageRoot, AssetDomain domain)
    {
        var path = PathFor(packageRoot, domain);
        return File.Exists(path) ? ReadFile(path) : null;
    }

    /// <summary>读 lock 文件（缺失＝<see cref="FileNotFoundException"/>；非法＝<see cref="FormatException"/>）。</summary>
    /// <exception cref="ArgumentException">path 为 null/空白。</exception>
    public static AssetLock ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>解析 lock JSON（读宽；非法＝<see cref="FormatException"/>）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static AssetLock Parse(string json)
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
            throw new FormatException($"lock 无法解析：{ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("lock 根须为对象。");
            }

            var schemaVersion = ReadSchemaVersion(root);
            var packageId = RequireString(root, "packageId");
            var domainText = RequireString(root, "domain");
            if (!AssetDomainTokens.TryParse(domainText, out var domain))
            {
                throw new FormatException($"lock domain '{domainText}' 非法（期望 formal/test）。");
            }

            if (!TryGet(root, "assets", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("lock 缺少数组字段 'assets'。");
            }

            var entries = new List<AssetLockEntry>();
            foreach (var item in array.EnumerateArray())
            {
                entries.Add(ReadEntry(item));
            }

            return new AssetLock(schemaVersion, packageId, domain, entries);
        }
    }

    /// <summary>写出规范 JSON（固定键序／2 空格／LF）。</summary>
    /// <exception cref="ArgumentNullException">lock 为 null。</exception>
    public static string ToJson(AssetLock @lock)
    {
        ArgumentNullException.ThrowIfNull(@lock);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", @lock.SchemaVersion);
            writer.WriteString("packageId", @lock.PackageId);
            writer.WriteString("domain", AssetDomainTokens.Token(@lock.Domain));

            writer.WriteStartArray("assets");
            foreach (var entry in @lock.Assets)
            {
                writer.WriteStartObject();
                writer.WriteString("guid", entry.AssetGuid.ToString());
                writer.WriteString("kind", AssetKindTokens.Token(entry.Kind));
                writer.WriteString("id", entry.AppId);
                writer.WriteString("version", entry.Version.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>写 lock 文件（UTF-8 无 BOM）。</summary>
    /// <exception cref="ArgumentNullException">lock 为 null。</exception>
    public static void WriteFile(string path, AssetLock @lock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, ToJson(@lock), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (!TryGet(root, "schemaVersion", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var version))
        {
            throw new FormatException("lock 缺少整数字段 'schemaVersion'。");
        }

        if (version != AssetLock.CurrentSchemaVersion)
        {
            throw new FormatException(
                $"lock schemaVersion {version} 不受支持（当前支持 {AssetLock.CurrentSchemaVersion}）。");
        }

        return version;
    }

    private static AssetLockEntry ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("lock 的 assets 条目须为对象。");
        }

        var guidText = RequireString(item, "guid");
        if (!Guid.TryParse(guidText, out var guid))
        {
            throw new FormatException($"lock 条目 guid '{guidText}' 非法（期望 GUID）。");
        }

        var kindText = RequireString(item, "kind");
        if (!AssetKindTokens.TryParse(kindText, out var kind))
        {
            throw new FormatException($"lock 条目种类 '{kindText}' 非法（期望 card/effect/trigger/handler）。");
        }

        var appId = RequireString(item, "id");
        if (!AssetAppId.IsAppId(appId))
        {
            throw new FormatException($"lock 条目 id '{appId}' 非法（期望 pack/{{包id}}/{{局部ID}}）。");
        }

        var versionText = RequireString(item, "version");
        if (!AssetVersion.TryParse(versionText, out var version))
        {
            throw new FormatException($"lock 条目 version '{versionText}' 非法（期望 1..8 段版本）。");
        }

        return new AssetLockEntry(guid, kind, appId, version);
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
            throw new FormatException($"lock 缺少字符串字段 '{name}'。");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException($"lock 字段 '{name}' 为空白。");
        }

        return text!;
    }
}
