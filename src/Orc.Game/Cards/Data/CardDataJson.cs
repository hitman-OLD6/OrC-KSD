using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Orc.Game.Cards;

namespace Orc.Game.Cards.Data;

/// <summary>
/// 卡牌数据体 JSON 读写（S1）：明文 UTF-8、camelCase、读宽（键名大小写不敏感／容忍注释与尾逗号）。
/// 形态：<c>{ schemaVersion, id, name, components: [ { component: "&lt;类型名&gt;", ...字段 } ] }</c>。
/// 版本（沿用 <c>PrefabJson</c> 口径）：<c>schemaVersion</c> 不符＝结构化失败（不静默降级）。
/// 隔离口径：未知组件名／单个组件反序列化失败＝写入告警集并跳过该组件（其余组件照常）；重复组件＝整卡拒绝。
/// 写方向（X1 加性）：<see cref="Serialize(CardDataBody, out IReadOnlyList{string})"/> 为对称入口——
/// 读宽写窄、固定键序、文本级规范化（2 空格缩进／LF／尾换行）；不可还原项＝警告集（不静默丢失）。
/// </summary>
public static class CardDataJson
{
    /// <summary>数据体 schema 版本（本版＝1）。</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>写出选项（文本契约：2 空格缩进多行、固定 LF、不转义非 ASCII——对齐官方样本与人类审查）。</summary>
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读回数据体（失败＝抛，fail-fast）。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <param name="warnings">隔离告警集（未知组件／组件反序列化失败）。</param>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    /// <exception cref="FormatException">JSON 非法、schema 版本不符、缺 id/name、重复组件。</exception>
    public static CardDataBody Deserialize(string json, out IReadOnlyList<string> warnings)
    {
        var result = Read(json);
        if (result.Body is null)
        {
            throw new FormatException(result.Error ?? "卡牌数据体非法。");
        }

        warnings = result.Warnings;
        return result.Body;
    }

    /// <summary>读回数据体（结构化：失败不抛）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static bool TryDeserialize(
        string json,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CardDataBody? body,
        out IReadOnlyList<string> warnings,
        out string? error)
    {
        var result = Read(json);
        body = result.Body;
        warnings = result.Warnings;
        error = result.Error;
        return result.Body is not null;
    }

    /// <summary>读取数据体（结构化结果；不抛——JSON 解析失败也转结构化错误）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static CardDataReadResult Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var warnings = new List<string>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            return new CardDataReadResult(null, warnings, $"卡牌数据体 JSON 非法：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new CardDataReadResult(null, warnings, "卡牌数据体根节点须为对象。");
            }

            if (!TryGetPropertyInsensitive(root, "schemaVersion", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return new CardDataReadResult(null, warnings, "卡牌数据体缺少 schemaVersion（必填、整数）。");
            }

            if (schemaVersion != SchemaVersion)
            {
                return new CardDataReadResult(
                    null,
                    warnings,
                    $"卡牌数据体 schema 版本不符：期望 {SchemaVersion}，实际 {schemaVersion}（不静默降级）。");
            }

            var id = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return new CardDataReadResult(null, warnings, "卡牌数据体缺少 id（必填）。");
            }

            var name = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return new CardDataReadResult(null, warnings, "卡牌数据体缺少 name（必填）。");
            }

            // 可构筑标记（R9/D20/D22）：缺省＝true；非布尔＝结构化失败（选严不选宽——这是防作弊边界）。
            var deckable = true;
            if (TryGetPropertyInsensitive(root, "deckable", out var deckableElement))
            {
                if (deckableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return new CardDataReadResult(null, warnings, "卡牌数据体字段 deckable 须为布尔。");
                }

                deckable = deckableElement.GetBoolean();
            }

            var components = new List<ICardDataComponentDefinition>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            if (TryGetPropertyInsensitive(root, "components", out var componentsElement)
                && componentsElement.ValueKind != JsonValueKind.Null)
            {
                if (componentsElement.ValueKind != JsonValueKind.Array)
                {
                    return new CardDataReadResult(null, warnings, "components 须为数组。");
                }

                foreach (var element in componentsElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        warnings.Add("components 含非对象项（已跳过）。");
                        continue;
                    }

                    var componentName = GetString(element, "component");
                    if (string.IsNullOrWhiteSpace(componentName))
                    {
                        warnings.Add("components 项缺少 component 名（已跳过）。");
                        continue;
                    }

                    if (!seenNames.Add(componentName))
                    {
                        return new CardDataReadResult(
                            null, warnings, $"数据体含重复组件 '{componentName}'（同一组件每卡至多一份——拒绝）。");
                    }

                    if (!CardComponentRegistry.TryResolve(componentName, out var entry))
                    {
                        warnings.Add($"未知组件 '{componentName}'（未注册——隔离：该组件跳过，其余照常）。");
                        continue;
                    }

                    try
                    {
                        var definition = entry.Reader(element)
                            ?? throw new FormatException("反序列化返回 null。");

                        components.Add(definition);
                    }
                    catch (Exception ex) when (ex is FormatException or ArgumentException
                        or ArgumentOutOfRangeException or InvalidOperationException)
                    {
                        warnings.Add($"组件 '{componentName}' 反序列化失败（隔离）：{ex.Message}");
                    }
                }
            }

            return new CardDataReadResult(new CardDataBody(id!, name!, components, deckable), warnings, null);
        }
    }

    /// <summary>
    /// 写出数据体为 JSON 文本（对称 <see cref="Deserialize"/>；X1 加性·写方向）：
    /// camelCase、读宽写窄（<c>schemaVersion</c> 固定 <see cref="SchemaVersion"/>、inline 统一对象态）、
    /// 文本契约（2 空格缩进、LF、尾随换行、固定键序：schemaVersion → id → name → deckable? → components）、
    /// 组件写序＝定义集顺序（保序对称）。
    /// 失败分层：整体性非法（null 等）＝抛出；组件级不可还原（未注册／无 writer／写入异常／无反向映射等）
    /// ＝警告集记录（该组件跳过或项级降级，不阻断其余）。
    /// </summary>
    /// <param name="body">数据体（id/name/组件集）。</param>
    /// <param name="warnings">警告集（卡 id＋组件名＋项原文＋原因类别，中文；不可还原项不静默丢失）。</param>
    /// <exception cref="ArgumentNullException">body 为 null。</exception>
    public static string Serialize(CardDataBody body, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SerializeCore(body.Id, body.Name, body.Components, isGuard: false, body.Deckable, out warnings);
    }

    /// <summary>
    /// 写出定义集条目为 JSON 文本（设计定稿钦定的写出入口：id ＋ 定义）。
    /// </summary>
    /// <param name="entry">定义集条目（id 由外部携带——<see cref="CardDefinition"/> 无 Id 字段）。</param>
    /// <param name="warnings">警告集；isGuard=true＝「无数据体载体」警告（不落盘、读回必然不等值——不静默）。</param>
    /// <exception cref="ArgumentNullException">entry 或其定义为 null。</exception>
    /// <exception cref="ArgumentException">id 为 null/空白。</exception>
    public static string Serialize(CardDefinitionEntry entry, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
        return SerializeCore(
            entry.Id,
            entry.Definition.Name,
            entry.Definition.Components,
            entry.Definition.IsGuard,
            entry.Definition.Deckable,
            out warnings);
    }

    /// <summary>写出（便捷重载：id ＋ 定义——能力与 <see cref="Serialize(CardDefinitionEntry, out IReadOnlyList{string})"/> 一致）。</summary>
    /// <exception cref="ArgumentNullException">definition 为 null。</exception>
    /// <exception cref="ArgumentException">id 为 null/空白。</exception>
    public static string Serialize(string id, CardDefinition definition, out IReadOnlyList<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(definition);
        return SerializeCore(
            id, definition.Name, definition.Components, definition.IsGuard, definition.Deckable, out warnings);
    }

    private static string SerializeCore(
        string id,
        string name,
        IReadOnlyList<ICardDataComponentDefinition> components,
        bool isGuard,
        bool deckable,
        out IReadOnlyList<string> warnings)
    {
        var collected = new List<string>();

        if (isGuard)
        {
            collected.Add(
                $"卡 '{id}'：IsGuard 无数据体 v1 载体（非数据体字段——读回必然不等值；如需承载请改用 'guard' 词条）。");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("id", id);
            writer.WriteString("name", name);

            // 读宽写窄（D22）：true 是缺省值、不落盘；只有 false 才是作者不得不表达的意图。
            if (!deckable)
            {
                writer.WriteBoolean("deckable", false);
            }

            writer.WriteStartArray("components");
            foreach (var component in components)
            {
                WriteComponent(writer, id, component, collected);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        warnings = collected;

        // 文本契约：统一 LF（写出器的平台换行〔Windows＝CRLF〕经规范化——跨平台输出一致）、
        // 文件末尾补单个尾随换行（缩进由写出器固定；编码由文件写入面保证 UTF-8 无 BOM）。
        // Replace 安全性：JSON 字符串内的 CR/LF 必为转义序列（控制字符始终转义）——原始 CR 仅出现在格式换行处。
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static void WriteComponent(
        Utf8JsonWriter target,
        string cardId,
        ICardDataComponentDefinition definition,
        List<string> warnings)
    {
        if (!CardComponentRegistry.TryResolve(definition.ComponentName, out var entry))
        {
            warnings.Add($"卡 '{cardId}'：组件 '{definition.ComponentName}' 未注册（隔离——该组件跳过，其余组件照常）。");
            return;
        }

        if (entry.Writer is null)
        {
            warnings.Add($"卡 '{cardId}'：组件 '{entry.Name}' 已注册但无 writer（无写出器——隔离：该组件跳过，其余组件照常）。");
            return;
        }

        // 组件先写进独立缓冲：writer 抛错＝该组件整体隔离（不污染主体输出、不阻断其余组件；对称读侧组件隔离）。
        var componentBuffer = new ArrayBufferWriter<byte>();
        var componentWarnings = new List<string>();

        try
        {
            using (var writer = new Utf8JsonWriter(componentBuffer))
            {
                writer.WriteStartObject();
                writer.WriteString("component", entry.Name); // 类型名由序列化器单点注入（单一真源，防双写漂移）。
                entry.Writer(definition, writer, componentWarnings);

                if (writer.CurrentDepth != 1)
                {
                    throw new InvalidOperationException("组件写出器破坏了 JSON 结构平衡（组件对象未单层闭合）。");
                }

                writer.WriteEndObject();
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException
            or ArgumentOutOfRangeException or InvalidOperationException)
        {
            warnings.Add($"卡 '{cardId}'：组件 '{entry.Name}' 写入异常（隔离：该组件跳过，其余组件照常）：{ex.Message}");
            return;
        }

        foreach (var warning in componentWarnings)
        {
            warnings.Add($"卡 '{cardId}'：{warning}");
        }

        using var document = JsonDocument.Parse(componentBuffer.WrittenMemory);
        document.RootElement.WriteTo(target);
    }

    private static bool TryGetPropertyInsensitive(JsonElement element, string name, out JsonElement value)
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

    private static string? GetString(JsonElement element, string name)
        => TryGetPropertyInsensitive(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
