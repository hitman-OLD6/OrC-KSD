using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Orc.Game.Decks;

namespace Orc.Packages.Decks;

/// <summary>卡组数据体中的一条卡引用（**原文形态**：ID 可写应用层全名或同包裸局部 ID，D48）。</summary>
/// <param name="Id">原文 ID。</param>
/// <param name="Count">张数（解析期已保证 &gt; 0）。</param>
public sealed record DeckCardEntry(string Id, int Count);

/// <summary>
/// 卡组数据体（R2）：<c>{ schemaVersion, id, name, deckConfig{mainFaction, allyFaction?}, cards[{id,count}] }</c>。
/// <para>字段级"缺失"用 null 表达——是否非法由判定层决定（本类型只负责形状）。</para>
/// </summary>
/// <param name="SchemaVersion">schema 版本（当前只有 1）。</param>
/// <param name="Id">包内局部 ID（缺失＝null）。</param>
/// <param name="Name">展示名（缺失＝null；R12 之外唯一的「人读身份」）。</param>
/// <param name="MainFaction">主国原文（缺失/空白＝null）。</param>
/// <param name="AllyFaction">盟国原文（缺失/空白＝null ⇒ 走 R12 推导）。</param>
/// <param name="Cards">卡引用（声明顺序）。</param>
public sealed record DeckDataBody(
    int SchemaVersion,
    string? Id,
    string? Name,
    string? MainFaction,
    string? AllyFaction,
    IReadOnlyList<DeckCardEntry> Cards);

/// <summary>卡组数据体解析结果。</summary>
/// <param name="Body">可解析＝非 null（字段缺失不影响本值）。</param>
/// <param name="Failure">不可解析原因（可解析＝null）。</param>
/// <param name="Violations">解析阶段就能判定的违规（张数非正／空白 ID——D50 要求不等到展开）。</param>
public sealed record DeckDataParse(
    DeckDataBody? Body,
    string? Failure,
    IReadOnlyList<DeckViolation> Violations)
{
    /// <summary>原文是否可解析。</summary>
    public bool IsParsed => Failure is null;
}

/// <summary>
/// 卡组数据体读写（R2/D46-D50）：读宽（键名大小写不敏感、容忍注释与尾逗号）、写窄（固定键序／2 空格／LF）。
/// <para>
/// <c>schemaVersion</c> 非 1 ＝**不可解析**（沿用卡牌「不静默降级」口径）；字段缺失/类型不符不影响可解析性，
/// 交给判定层列全违规——这样一份「缺 <c>name</c>」的卡组能给出可定位诊断，而不是一句"文件坏了"。
/// </para>
/// </summary>
public static class DeckDataJson
{
    /// <summary>当前 schema 版本。</summary>
    public const int SchemaVersion = 1;

    /// <summary>卡组源文件模式。</summary>
    public const string FilePattern = "*.deck.json";

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>解析数据体（不抛错：不可解析走 <see cref="DeckDataParse.Failure"/>）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static DeckDataParse Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            return new DeckDataParse(null, $"卡组数据体无法解析：{ex.Message}", Array.Empty<DeckViolation>());
        }

        using (document)
        {
            return Read(document.RootElement);
        }
    }

    /// <summary>读文件（文件不存在＝<see cref="FileNotFoundException"/>；不可解析走 <see cref="DeckDataParse.Failure"/>）。</summary>
    /// <exception cref="ArgumentException">path 为 null/空白。</exception>
    public static DeckDataParse ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>写出规范 JSON（读宽写窄：<c>count</c> 为 1 时不落盘——缺省即 1）。</summary>
    /// <exception cref="ArgumentNullException">body 为 null。</exception>
    public static string Serialize(DeckDataBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", body.SchemaVersion);
            if (body.Id is { } id)
            {
                writer.WriteString("id", id);
            }

            if (body.Name is { } name)
            {
                writer.WriteString("name", name);
            }

            if (body.MainFaction is not null || body.AllyFaction is not null)
            {
                writer.WriteStartObject("deckConfig");
                if (body.MainFaction is { } main)
                {
                    writer.WriteString("mainFaction", main);
                }

                if (body.AllyFaction is { } ally)
                {
                    writer.WriteString("allyFaction", ally);
                }

                writer.WriteEndObject();
            }

            writer.WriteStartArray("cards");
            foreach (var card in body.Cards)
            {
                writer.WriteStartObject();
                writer.WriteString("id", card.Id);
                if (card.Count != 1)
                {
                    writer.WriteNumber("count", card.Count);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static DeckDataParse Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new DeckDataParse(null, "卡组数据体根须为对象。", Array.Empty<DeckViolation>());
        }

        if (!TryGet(root, "schemaVersion", out var schemaValue)
            || schemaValue.ValueKind != JsonValueKind.Number
            || !schemaValue.TryGetInt32(out var schemaVersion))
        {
            return new DeckDataParse(null, "卡组数据体缺少整数字段 'schemaVersion'。", Array.Empty<DeckViolation>());
        }

        if (schemaVersion != SchemaVersion)
        {
            return new DeckDataParse(
                null,
                $"卡组数据体 schemaVersion {schemaVersion} 不受支持（当前支持 {SchemaVersion}）。",
                Array.Empty<DeckViolation>());
        }

        string? mainFaction = null;
        string? allyFaction = null;
        if (TryGet(root, "deckConfig", out var config) && config.ValueKind == JsonValueKind.Object)
        {
            mainFaction = OptionalString(config, "mainFaction");
            allyFaction = OptionalString(config, "allyFaction");
        }

        var violations = new List<DeckViolation>();
        var cards = ReadCards(root, violations);

        return new DeckDataParse(
            new DeckDataBody(schemaVersion, OptionalString(root, "id"), OptionalString(root, "name"), mainFaction, allyFaction, cards),
            Failure: null,
            violations);
    }

    private static IReadOnlyList<DeckCardEntry> ReadCards(JsonElement root, List<DeckViolation> violations)
    {
        if (!TryGet(root, "cards", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<DeckCardEntry>();
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            violations.Add(new DeckViolation("entry-invalid", null, "卡组字段 'cards' 须为数组。"));
            return Array.Empty<DeckCardEntry>();
        }

        var cards = new List<DeckCardEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                violations.Add(new DeckViolation("entry-invalid", null, "卡组 'cards' 的条目须为对象。"));
                continue;
            }

            var id = OptionalString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                violations.Add(new DeckViolation("blank-id", id, "卡组 'cards' 含空白卡 id（该条目不计入）。"));
                continue;
            }

            var count = 1;
            if (TryGet(item, "count", out var countValue) && countValue.ValueKind != JsonValueKind.Null)
            {
                if (countValue.ValueKind != JsonValueKind.Number || !countValue.TryGetInt32(out count))
                {
                    violations.Add(new DeckViolation(
                        "entry-invalid", id, $"卡条目 '{id}' 的 count 须为整数（实际 {countValue.GetRawText()}）。"));
                    continue;
                }
            }

            if (count <= 0)
            {
                // D50：在解析阶段就判——等到展开时会静默变成 0 条，让「39 张」误通过。
                violations.Add(new DeckViolation("entry-invalid", id, $"卡条目 '{id}' 的 count 为 {count}（须为正整数）。"));
                continue;
            }

            cards.Add(new DeckCardEntry(id, count));
        }

        return cards;
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

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
