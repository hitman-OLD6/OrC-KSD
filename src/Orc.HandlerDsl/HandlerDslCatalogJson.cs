using System.Text;
using System.Text.Json;

namespace Orc.HandlerDsl;

/// <summary>
/// 节点目录的**纯数据序列化**（SPEC §12／<c>catalog.schema.json</c>）：目录 → JSON 文本 → 目录 → 同一份文本。
/// <para>
/// 为什么在实现程序集里：S4 的验收要求「目录产出是纯数据、可序列化」，而 vectors 快照与前端查询出口
/// （S10）都只能吃文本。字段顺序、缩进、行尾**全部固定**，保证同一目录两次序列化逐字节相同
/// （与 D9 的内容寻址同一口径）。
/// </para>
/// <para>
/// 上下文原语 <c>stop</c>／<c>interrupt</c> 是**合成节点**（SPEC §12.9）：序列化时写入（<c>source</c> 省略、
/// <c>faceType</c> 为空），反序列化时**跳过并交回 <see cref="DslCatalog"/> 构造时重新合成**——
/// 否则同一节点会被注入两次，且快照校验会把它们当成「找不到来源门面」的孤儿。
/// </para>
/// </summary>
public static class HandlerDslCatalogJson
{
    // Utf8JsonWriter 的缩进一律用 LF（net8 起写死 JSON 行尾为 \n），与 SPEC §13 F2 同口径。
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>序列化为规范 JSON 文本（LF、2 空格缩进、固定字段顺序、末尾恰一个换行）。</summary>
    /// <param name="catalog">目录（须是 <see cref="DslCatalog"/>，即带 <see cref="DslCatalog.ScannedFaces"/> 的实例）。</param>
    /// <exception cref="ArgumentNullException">catalog 为 null。</exception>
    public static string Serialize(IHandlerDslCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", HandlerDslContract.SchemaVersion);
            writer.WriteNumber("catalogRevision", catalog.Revision);

            writer.WriteStartArray("scannedFaces");
            foreach (var face in catalog.ScannedFaces)
            {
                writer.WriteStringValue(face);
            }

            writer.WriteEndArray();

            writer.WriteStartObject("readables");
            foreach (var pair in catalog.Readables.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value.Format());
            }

            writer.WriteEndObject();

            writer.WriteStartArray("nodes");
            foreach (var node in catalog.Nodes)
            {
                WriteNode(writer, node);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// 从 JSON 文本还原目录（`catalog.schema.json` 的字段全集；上下文原语由构造器合成）。
    /// </summary>
    /// <param name="json">目录 JSON 文本。</param>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    /// <exception cref="FormatException">结构不符（缺字段／类型不符／引脚类型越界）。</exception>
    public static DslCatalog Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var revision = root.GetProperty("catalogRevision").GetInt32();

            var faces = new List<string>();
            if (root.TryGetProperty("scannedFaces", out var faceArray))
            {
                faces.AddRange(faceArray.EnumerateArray().Select(static f => f.GetString()!));
            }

            var readables = new Dictionary<string, PinType>(StringComparer.Ordinal);
            if (root.TryGetProperty("readables", out var readablesElement))
            {
                foreach (var property in readablesElement.EnumerateObject())
                {
                    readables[property.Name] = PinType.Parse(property.Value.GetString()!);
                }
            }

            var nodes = new List<DslNodeSignature>();
            foreach (var nodeElement in root.GetProperty("nodes").EnumerateArray())
            {
                var node = ReadNode(nodeElement);
                if (HandlerDslContextNodes.IsContextOp(node.Name))
                {
                    // 合成节点：不重复注入（DslCatalog 构造时会重新合成）。
                    continue;
                }

                nodes.Add(node);
            }

            return new DslCatalog(revision, nodes, readables, faces);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new FormatException($"目录 JSON 无法解析：{ex.Message}", ex);
        }
    }

    private static void WriteNode(Utf8JsonWriter writer, DslNodeSignature node)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);

        // 合成节点（stop／interrupt）没有来源门面：省略 source（快照侧不会把它们判成孤儿，SPEC §12.9）。
        if (node.Face is not null || node.Method is not null || node.ImplicitHost is not null || node.FaceType is not null)
        {
            writer.WriteStartObject("source");
            if (node.Face is not null)
            {
                writer.WriteString("face", node.Face);
            }

            if (node.Method is not null)
            {
                writer.WriteString("method", node.Method);
            }

            if (node.ImplicitHost is not null)
            {
                writer.WriteString("implicitHost", node.ImplicitHost);
            }

            if (node.FaceType is not null)
            {
                writer.WriteString("faceType", node.FaceType);
            }

            if (node.IsStatic)
            {
                writer.WriteBoolean("static", true);
            }

            // 不出引脚但必须过快照：丢了它，生成器就不知道要传 ct（动作恒拿 default）。
            if (node.CancellationTokenParameter is not null)
            {
                writer.WriteString("tokenParameter", node.CancellationTokenParameter);
            }

            writer.WriteEndObject();
        }

        writer.WriteBoolean("hasAwait", node.HasAwait);
        writer.WriteBoolean("hasSideEffect", node.HasSideEffect);

        writer.WriteStartArray("inputs");
        foreach (var pin in node.Inputs)
        {
            WritePin(writer, pin);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("outputs");
        foreach (var pin in node.Outputs)
        {
            WritePin(writer, pin);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePin(Utf8JsonWriter writer, DslPin pin)
    {
        writer.WriteStartObject();
        writer.WriteString("name", pin.Name);
        writer.WriteString("type", BaseName(pin.Type));
        writer.WriteBoolean("required", pin.Required);

        var subtypes = pin.Type.IsList ? pin.Type.Element!.Subtypes : pin.Type.Subtypes;
        if (subtypes.Count > 0)
        {
            writer.WriteStartArray("subtypes");
            foreach (var subtype in subtypes)
            {
                writer.WriteStringValue(subtype);
            }

            writer.WriteEndArray();
        }

        if (pin.EnumValues.Count > 0)
        {
            writer.WriteStartArray("enum");
            foreach (var value in pin.EnumValues)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        if (pin.EnumType is not null)
        {
            writer.WriteString("enumType", pin.EnumType);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// 引脚类型的**规范基名**（不含子类型后缀）：<c>entity</c>／<c>list&lt;entity&gt;</c>…
    /// <see cref="PinType.Format"/> 会把子类型拼成 <c>entity{unit,hq}</c>，那是给人看的错误消息形态、
    /// 不是可再解析的契约文本，故序列化只用基名 ＋ 独立的 <c>subtypes</c> 数组（<c>catalog.schema.json</c>）。
    /// </summary>
    private static string BaseName(PinType type) => type.Kind switch
    {
        PinTypeKind.Entity => "entity",
        PinTypeKind.Number => "number",
        PinTypeKind.String => "string",
        PinTypeKind.Boolean => "boolean",
        PinTypeKind.List => $"list<{BaseName(type.Element!)}>",
        _ => throw new InvalidOperationException("未定义的引脚类型种类。"),
    };

    private static DslNodeSignature ReadNode(JsonElement element)
    {
        var name = element.GetProperty("name").GetString()!;

        string? face = null;
        string? method = null;
        string? implicitHost = null;
        string? faceType = null;
        string? tokenParameter = null;
        var isStatic = false;

        if (element.TryGetProperty("source", out var source))
        {
            face = source.TryGetProperty("face", out var f) ? f.GetString() : null;
            method = source.TryGetProperty("method", out var m) ? m.GetString() : null;
            implicitHost = source.TryGetProperty("implicitHost", out var h) ? h.GetString() : null;
            faceType = source.TryGetProperty("faceType", out var t) ? t.GetString() : null;
            isStatic = source.TryGetProperty("static", out var s) && s.GetBoolean();
            tokenParameter = source.TryGetProperty("tokenParameter", out var tp) ? tp.GetString() : null;
        }

        return new DslNodeSignature(
            name,
            ReadPins(element, "inputs"),
            ReadPins(element, "outputs"),
            element.TryGetProperty("hasAwait", out var hasAwait) && hasAwait.GetBoolean(),
            element.TryGetProperty("hasSideEffect", out var hasSideEffect) && hasSideEffect.GetBoolean(),
            face,
            method,
            implicitHost,
            isStatic,
            faceType,
            tokenParameter);
    }

    private static IReadOnlyList<DslPin> ReadPins(JsonElement nodeElement, string field)
    {
        var pins = new List<DslPin>();
        if (!nodeElement.TryGetProperty(field, out var array))
        {
            return pins;
        }

        foreach (var pinElement in array.EnumerateArray())
        {
            var type = PinType.Parse(pinElement.GetProperty("type").GetString()!);
            if (pinElement.TryGetProperty("subtypes", out var subtypes))
            {
                var names = subtypes.EnumerateArray().Select(static s => s.GetString()!).ToArray();
                type = type.IsList ? type.WithElementSubtypes(names) : type.WithSubtypes(names);
            }

            var enumValues = pinElement.TryGetProperty("enum", out var enumElement)
                ? enumElement.EnumerateArray().Select(static s => s.GetString()!).ToArray()
                : null;

            var enumType = pinElement.TryGetProperty("enumType", out var enumTypeElement)
                ? enumTypeElement.GetString()
                : null;

            pins.Add(new DslPin(
                pinElement.GetProperty("name").GetString()!,
                type,
                !pinElement.TryGetProperty("required", out var required) || required.GetBoolean(),
                enumValues,
                enumType));
        }

        return pins;
    }
}
