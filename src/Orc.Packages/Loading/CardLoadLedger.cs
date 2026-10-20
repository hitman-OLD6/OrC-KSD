using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Orc.Core;
using Orc.Game.Cards.Loading;

namespace Orc.Packages.Loading;

/// <summary>
/// **本局累积视图**（S9）：把同一局内多次卡牌加载批次的 <see cref="CardLoadReport"/> 按 **GUID 合并**、
/// <c>refCount</c> **累加**后的汇总（R5 的「按资产去重＋引用计数」在整局尺度上的延伸）。
/// <para>
/// 约定（与冻结报告 schema 对齐，不新增第二套字段名）：
/// <list type="bullet">
///   <item><c>items</c>：按 **GUID** 合并；同一 GUID 的多批出现只留一条，<c>refCount</c> 为各批之和。</item>
///   <item>除 <c>refCount</c> 外的字段取**最近一次**出现（档位／字段问题反映最新判定结果）。</item>
///   <item>条目顺序＝**首次出现顺序**（确定性；不依赖字典序或时间）。</item>
///   <item><c>summary</c> 计的是**合并后条目**：<c>totalRefs</c>＝Σ <c>refCount</c>。</item>
///   <item>**不含 <c>slots</c>**：卡位序号是「某一批的牌堆位置」，跨批合并无意义（卡位仍见各批报告）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class CardLoadLedgerSnapshot
{
    /// <summary>累积视图 schema 版本（冻结＝1）。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>空表（未记录任何批次；诊断面无需分支即可读取）。</summary>
    public static CardLoadLedgerSnapshot Empty { get; } = new(
        0, null, Array.Empty<CardLoadReportItem>(), new CardLoadSummary(0, 0, 0, 0, 0));

    internal CardLoadLedgerSnapshot(
        int batchCount,
        string? lastBatchId,
        IReadOnlyList<CardLoadReportItem> items,
        CardLoadSummary summary)
    {
        BatchCount = batchCount;
        LastBatchId = lastBatchId;
        Items = items;
        Summary = summary;
    }

    /// <summary>schema 版本（恒 <see cref="CurrentSchemaVersion"/>）。</summary>
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>已记录的批次数（本局）。</summary>
    public int BatchCount { get; }

    /// <summary>最近一批的 <c>batchId</c>（未记录任何批次＝null）。</summary>
    public string? LastBatchId { get; }

    /// <summary>合并后的资产条目（首次出现顺序）。</summary>
    public IReadOnlyList<CardLoadReportItem> Items { get; }

    /// <summary>合并后的汇总计数。</summary>
    public CardLoadSummary Summary { get; }

    /// <summary>是否为空（尚未记录任何批次，或所有批次都没有资产条目）。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>序列化为 JSON 文本（固定键序、2 空格缩进、LF、尾随换行；**不写盘**）。</summary>
    public string ToJson() => CardLoadLedgerJson.Serialize(this);
}

/// <summary>
/// **卡牌加载台账**（S9；宿主／对局侧持有的唯一持有者）：记录本局每一次卡牌加载批次，
/// 并给出「最近一批」与「本局累积视图」。**不做任何装载动作**——只消费
/// <see cref="CardLoadReport"/>（报告本身仍不落盘，R5）。
/// <para>
/// 模型来自 <see cref="CardLoadReport"/>（本程序集），故工具面（<c>Orc.Mcp</c>）只读取本类型的结果、
/// **不复制任何报告模型**（S9 的硬要求）。
/// </para>
/// <para>线程安全（内部加锁）；对局装配期为单线程，但诊断读取可与装载并发。</para>
/// </summary>
public sealed class CardLoadLedger
{
    private readonly object _gate = new();
    private readonly List<Guid> _order = new();
    private readonly Dictionary<Guid, CardLoadReportItem> _items = new();
    private CardLoadReport? _latest;
    private int _batches;

    /// <summary>已记录的批次数。</summary>
    public int BatchCount
    {
        get
        {
            lock (_gate)
            {
                return _batches;
            }
        }
    }

    /// <summary>最近一批的完整报告（未记录＝null）。</summary>
    public CardLoadReport? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>
    /// 记录一批报告（按 GUID 合并、<c>refCount</c> 累加；除计数外字段取最近一次出现）。
    /// </summary>
    /// <param name="report">卡牌加载报告（I10 冻结 schema）。</param>
    /// <exception cref="ArgumentNullException">report 为 null。</exception>
    public void Record(CardLoadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            foreach (var item in report.Items)
            {
                if (_items.TryGetValue(item.AssetGuid, out var existing))
                {
                    _items[item.AssetGuid] = item with { RefCount = existing.RefCount + item.RefCount };
                }
                else
                {
                    _items.Add(item.AssetGuid, item);
                    _order.Add(item.AssetGuid);
                }
            }

            _batches++;
            _latest = report;
        }
    }

    /// <summary>取累积视图快照（不可变；与后续记录互不影响）。</summary>
    public CardLoadLedgerSnapshot Snapshot()
    {
        lock (_gate)
        {
            var items = _order.Select(guid => _items[guid]).ToArray();
            var summary = new CardLoadSummary(
                items.Length,
                items.Count(item => item.Tier == CardLoadTier.Valid),
                items.Count(item => item.Tier == CardLoadTier.Warning),
                items.Count(item => item.Tier == CardLoadTier.Invalid),
                items.Sum(item => item.RefCount));

            return new CardLoadLedgerSnapshot(_batches, _latest?.BatchId, items, summary);
        }
    }

    /// <summary>清空台账（新对局／宿主切换时；非对局语义，仅重置持有者状态）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _items.Clear();
            _latest = null;
            _batches = 0;
        }
    }
}

/// <summary>
/// 累积视图的 JSON 写出（**唯一**序列化入口；与 <see cref="CardLoadReportJson"/> 共用条目/汇总写法，
/// 不复制键名）。不写文件、不读文件。
/// </summary>
public static class CardLoadLedgerJson
{
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>序列化为 JSON 文本（2 空格缩进、LF、单个尾随换行）。</summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public static string Serialize(CardLoadLedgerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            Write(writer, snapshot);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>写出累积视图（键序：schemaVersion／batchCount／lastBatchId／items／summary）。</summary>
    /// <exception cref="ArgumentNullException">writer 或 snapshot 为 null。</exception>
    public static void Write(Utf8JsonWriter writer, CardLoadLedgerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(snapshot);

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", snapshot.SchemaVersion);
        writer.WriteNumber("batchCount", snapshot.BatchCount);
        if (snapshot.LastBatchId is { } batchId)
        {
            writer.WriteString("lastBatchId", batchId);
        }
        else
        {
            writer.WriteNull("lastBatchId");
        }

        CardLoadReportJson.WriteItems(writer, snapshot.Items);
        CardLoadReportJson.WriteSummary(writer, snapshot.Summary);
        writer.WriteEndObject();
    }
}
