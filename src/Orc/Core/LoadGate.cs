namespace Orc.Core;

/// <summary>包装载结局（S14）：成功／被跳过（可选包失败）／失败（必需包失败＝对局不开始）。</summary>
public enum PackLoadStatus
{
    /// <summary>整包装载成功。</summary>
    Loaded,

    /// <summary>可选包失败：整包跳过、对局照常开始（失败项留痕）。</summary>
    Skipped,

    /// <summary>必需包失败：对局不开局（初始化上抛）。</summary>
    Failed,
}

/// <summary>
/// 一条包装载记录（S14 第 2–3 条）：包 id ＋ 必需/可选 ＋ 结局 ＋ 失败项。
/// <para>
/// "被跳过的可选包"必须能在**审查导出**与初始化报告里看到——否则作者会以为包装载了、实际什么都没生效
/// （R7 的"能力不退化"同一条要求）。本类型是那份留痕的唯一载体（由装载门写入、由审查面读出）。
/// </para>
/// </summary>
/// <param name="PackId">包 id。</param>
/// <param name="Required">是否必需包。</param>
/// <param name="Status">结局。</param>
/// <param name="Failures">失败项（成功＝空）。</param>
public sealed record PackLoadRecord(
    string PackId,
    bool Required,
    PackLoadStatus Status,
    IReadOnlyList<string> Failures)
{
    /// <summary>是否被跳过（可选包失败）。</summary>
    public bool Skipped => Status == PackLoadStatus.Skipped;
}

/// <summary>
/// 装载门报告（S14）：本局每一次包装载的结局（按装载序）。
/// <para>
/// 只收集不抛：必需包的"不开局"由装载方（对局初始化）按 <see cref="RequiredFailures"/> 决定，
/// 这样诊断面能看到"为什么没开局"，而不是只看到一条异常。
/// </para>
/// <para>
/// **卡级失败不在这里**（§34 决定）：卡牌加载失败的留痕归卡牌加载报告（<c>CardLoadReport</c>，
/// <c>items[].fields</c> ＋ <c>slots[].fieldPaths</c>）——那已是卡级判定的冻结 schema，
/// 再开一套平行的卡级记录面只会让"哪张卡坏了"有两个真源。本报告只管**包级**结局。
/// </para>
/// </summary>
public sealed class LoadGateReport
{
    private readonly List<PackLoadRecord> _records = new();

    /// <summary>全部装载记录（装载序）。</summary>
    public IReadOnlyList<PackLoadRecord> Records => _records;

    /// <summary>被跳过的可选包（审查面／诊断的"哪些包没生效"）。</summary>
    public IReadOnlyList<PackLoadRecord> Skipped
        => _records.Where(static r => r.Status == PackLoadStatus.Skipped).ToArray();

    /// <summary>失败的必需包（非空＝对局必须不开局）。</summary>
    public IReadOnlyList<PackLoadRecord> RequiredFailures
        => _records.Where(static r => r.Status == PackLoadStatus.Failed).ToArray();

    /// <summary>登记一条结局。</summary>
    /// <exception cref="ArgumentNullException">record 为 null。</exception>
    public void Record(PackLoadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records.Add(record);
    }

    /// <summary>登记一条结局（便捷）。</summary>
    public void Record(string packId, bool required, PackLoadStatus status, IReadOnlyList<string>? failures = null)
        => _records.Add(new PackLoadRecord(packId, required, status, failures ?? Array.Empty<string>()));
}
