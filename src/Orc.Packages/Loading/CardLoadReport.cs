using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Orc.Core;
using Orc.Game.Cards.Loading;

namespace Orc.Packages.Loading;

/// <summary>
/// 报告中的一条**资产条目**（按资产去重：同一资产版本在整批里只出现一次，引用次数见 <see cref="RefCount"/>）。
/// </summary>
/// <param name="AssetGuid">资产版本 GUID（资产层 ID）。</param>
/// <param name="AssetName">资产名（<c>{种类}：{局部ID}@{包GUID8}#{版本}</c>）。</param>
/// <param name="AppId">应用层 ID。</param>
/// <param name="Kind">资产种类。</param>
/// <param name="Domain">资产域。</param>
/// <param name="Tier">卡级聚合档位。</param>
/// <param name="Fields">字段问题描述（路径／期望类型／默认值／原因枚举）。</param>
/// <param name="RefCount">本批中引用该资产的卡位数。</param>
public sealed record CardLoadReportItem(
    Guid AssetGuid,
    string AssetName,
    string AppId,
    AssetKind Kind,
    AssetDomain Domain,
    CardLoadTier Tier,
    IReadOnlyList<CardFieldIssue> Fields,
    int RefCount);

/// <summary>
/// 报告中的一条**卡位条目**（**每个牌堆卡位有且仅有一条**，含留空位）：
/// 没有它就无法诊断「第几个卡位为什么空了」。
/// </summary>
/// <param name="PlayerId">牌堆归属玩家标识。</param>
/// <param name="Index">卡位序号。</param>
/// <param name="AppId">卡位书写的应用层 ID。</param>
/// <param name="AssetGuid">解析到的资产 GUID（解析失败＝null——留空位）。</param>
/// <param name="Tier">该卡位的档位（解析失败/资产非法＝invalid）。</param>
/// <param name="FieldPaths">相关字段路径（资产条目的 fields 路径；解析失败＝<c>appId</c>）。</param>
public sealed record CardLoadSlot(
    string PlayerId,
    int Index,
    string AppId,
    Guid? AssetGuid,
    CardLoadTier Tier,
    IReadOnlyList<string> FieldPaths);

/// <summary>
/// 报告汇总：<paramref name="Total"/>/<paramref name="Valid"/>/<paramref name="Warning"/>/<paramref name="Invalid"/>
/// 计的是**资产条目**；<paramref name="TotalRefs"/> 计的是卡位引用合计（＝Σ 条目 <c>refCount</c>，
/// 只剩已解析资产的引用；无法解析的卡位见 slots 且不计入）。
/// </summary>
/// <param name="Total">资产条目总数。</param>
/// <param name="Valid">合法条目数。</param>
/// <param name="Warning">警报条目数。</param>
/// <param name="Invalid">非法条目数。</param>
/// <param name="TotalRefs">卡位引用合计。</param>
public sealed record CardLoadSummary(int Total, int Valid, int Warning, int Invalid, int TotalRefs);

/// <summary>
/// **卡牌加载报告**（I10 冻结 schema；键名与取值**不得改名**）：
/// <code>
/// CardLoadReport {
///   schemaVersion: 1,
///   batchId: string,
///   items: [{ assetGuid, assetName, appId, kind, domain, tier,
///             fields: [{ path, expectedType, defaultValue, reason }], refCount }],
///   slots: [{ playerId, index, appId, assetGuid?, tier, fieldPaths: [] }],
///   summary: { total, valid, warning, invalid, totalRefs }
/// }
/// </code>
/// <para>
/// 恒等约定：
/// <list type="bullet">
///   <item><c>batchId</c> 只作「同一次加载的多条日志」关联用，**不承载时间戳语义**（会话内单调序，测试可稳定断言）。</item>
///   <item><c>reason</c> 只取 <see cref="CardIssueReason"/> 的十个 token（禁止自由文本）。</item>
///   <item><c>tier</c> 只取 <c>valid</c>／<c>warning</c>／<c>invalid</c>。</item>
///   <item>报告**不落盘**（R5/D43）：只经返回值与工具返回。</item>
///   <item>与静态预检 <c>CardDistributionValidationReport</c> **不合并、不互转**（运行期装配 vs 静态预检，语义不同）。</item>
///   <item>
///     无资产身份的卡位（应用层 ID 解析失败/歧义）仍占一条 <c>slots</c>（<c>assetGuid: null</c>、
///     <c>tier: "invalid"</c>、<c>fieldPaths: ["appId"]</c>），但**不产生 <c>items</c> 条目**——
///     冻结 schema 的 <c>items[].assetGuid</c> 非空，无可报的资产身份；诊断锚点是卡位本身。
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed record CardLoadReport
{
    /// <summary>报告 schema 版本（冻结＝1）。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>创建报告（条目与卡位按调用方给定顺序保存——报告确定性由产生方负责）。</summary>
    /// <exception cref="ArgumentNullException">batchId/items/slots/summary 为 null。</exception>
    public CardLoadReport(
        string batchId,
        IReadOnlyList<CardLoadReportItem> items,
        IReadOnlyList<CardLoadSlot> slots,
        CardLoadSummary summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(summary);

        BatchId = batchId;
        Items = items;
        Slots = slots;
        Summary = summary;
    }

    /// <summary>schema 版本（恒 <see cref="CurrentSchemaVersion"/>）。</summary>
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>本批标识（会话内单调序，不含时间戳）。</summary>
    public string BatchId { get; }

    /// <summary>资产条目（按资产去重）。</summary>
    public IReadOnlyList<CardLoadReportItem> Items { get; }

    /// <summary>卡位条目（每卡位一条，含留空位）。</summary>
    public IReadOnlyList<CardLoadSlot> Slots { get; }

    /// <summary>汇总计数。</summary>
    public CardLoadSummary Summary { get; }

    /// <summary>序列化为报告 JSON 文本（固定键序、2 空格缩进、LF、尾随换行；**不写盘**）。</summary>
    public string ToJson() => CardLoadReportJson.Serialize(this);
}

/// <summary>
/// 报告 JSON 写出（I10 冻结 schema 的**唯一**序列化入口；固定键序、camelCase、写窄）。
/// 不写文件、不读文件——报告不落盘（D43）。
/// </summary>
public static class CardLoadReportJson
{
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>序列化为 JSON 文本（2 空格缩进、LF、单个尾随换行）。</summary>
    /// <exception cref="ArgumentNullException">report 为 null。</exception>
    public static string Serialize(CardLoadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            Write(writer, report);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>写出报告（键序＝schema 声明序）。</summary>
    /// <exception cref="ArgumentNullException">writer 或 report 为 null。</exception>
    public static void Write(Utf8JsonWriter writer, CardLoadReport report)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", report.SchemaVersion);
        writer.WriteString("batchId", report.BatchId);

        WriteItems(writer, report.Items);

        writer.WriteStartArray("slots");
        foreach (var slot in report.Slots)
        {
            WriteSlot(writer, slot);
        }

        writer.WriteEndArray();

        WriteSummary(writer, report.Summary);
        writer.WriteEndObject();
    }

    /// <summary>
    /// 写出 <c>items</c> 数组（**共享写法**：报告与本局累积视图 <see cref="CardLoadLedgerJson"/> 同一份键名与取值词表）。
    /// </summary>
    /// <exception cref="ArgumentNullException">writer 或 items 为 null。</exception>
    internal static void WriteItems(Utf8JsonWriter writer, IReadOnlyList<CardLoadReportItem> items)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);

        writer.WriteStartArray("items");
        foreach (var item in items)
        {
            WriteItem(writer, item);
        }

        writer.WriteEndArray();
    }

    /// <summary>写出 <c>summary</c> 对象（共享写法；见 <see cref="WriteItems"/>）。</summary>
    /// <exception cref="ArgumentNullException">writer 或 summary 为 null。</exception>
    internal static void WriteSummary(Utf8JsonWriter writer, CardLoadSummary summary)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(summary);

        writer.WriteStartObject("summary");
        writer.WriteNumber("total", summary.Total);
        writer.WriteNumber("valid", summary.Valid);
        writer.WriteNumber("warning", summary.Warning);
        writer.WriteNumber("invalid", summary.Invalid);
        writer.WriteNumber("totalRefs", summary.TotalRefs);
        writer.WriteEndObject();
    }

    private static void WriteItem(Utf8JsonWriter writer, CardLoadReportItem item)
    {
        writer.WriteStartObject();
        writer.WriteString("assetGuid", item.AssetGuid.ToString("D"));
        writer.WriteString("assetName", item.AssetName);
        writer.WriteString("appId", item.AppId);
        writer.WriteString("kind", AssetKindTokens.Token(item.Kind));
        writer.WriteString("domain", AssetDomainTokens.Token(item.Domain));
        writer.WriteString("tier", CardLoadTierTokens.Token(item.Tier));

        writer.WriteStartArray("fields");
        foreach (var field in item.Fields)
        {
            writer.WriteStartObject();
            writer.WriteString("path", field.Path);
            writer.WriteString("expectedType", field.ExpectedType);
            writer.WritePropertyName("defaultValue");
            WriteScalar(writer, field.DefaultValue);
            writer.WriteString("reason", CardIssueReasonTokens.Token(field.Reason));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("refCount", item.RefCount);
        writer.WriteEndObject();
    }

    private static void WriteSlot(Utf8JsonWriter writer, CardLoadSlot slot)
    {
        writer.WriteStartObject();
        writer.WriteString("playerId", slot.PlayerId);
        writer.WriteNumber("index", slot.Index);
        writer.WriteString("appId", slot.AppId);
        if (slot.AssetGuid is { } guid)
        {
            writer.WriteString("assetGuid", guid.ToString("D"));
        }
        else
        {
            writer.WriteNull("assetGuid");
        }

        writer.WriteString("tier", CardLoadTierTokens.Token(slot.Tier));

        writer.WriteStartArray("fieldPaths");
        foreach (var path in slot.FieldPaths)
        {
            writer.WriteStringValue(path);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>默认值只承载标量（0／空串／null／布尔）；复杂类型以文本降级写出（不静默丢失）。</summary>
    private static void WriteScalar(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
