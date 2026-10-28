using System.Text;

namespace Orc.Cards;

/// <summary>效果快照写出成功条目（快照 id ＋ 目标文件）。</summary>
/// <param name="PrefabId">效果快照 id（＝<c>root.id</c>）。</param>
/// <param name="File">目标文件路径（＝<c>&lt;目录&gt;/&lt;id&gt;.prefab.json</c>）。</param>
public sealed record PrefabSaveSuccess(string PrefabId, string File);

/// <summary>效果快照写出失败（隔离留痕）。</summary>
/// <param name="PrefabId">效果快照 id（可定位）。</param>
/// <param name="File">目标文件路径（预期的落点）。</param>
/// <param name="Error">失败原因。</param>
public sealed record PrefabSaveFailure(string PrefabId, string File, string Error);

/// <summary>
/// 效果快照落盘结果（批 2 加性·写方向）：成功／失败**分列**且可定位——
/// 成功判定＝「全部写成功、零失败」可由结构化清单直接断言（对齐 <c>PrefabLoadReport</c> 的报告式返回与批 1 写侧的分列精神）。
/// 明示：**无 Warnings 分列**——效果快照＝完备数据模型（csx/assemblyKey、hooks、events、modings、injects 全字段直出），
/// 效果侧写方向不存在警告通道（裁定见需求文档第 2 轮）。
/// </summary>
/// <param name="Succeeded">成功条目（快照 id＋目标文件；按处理序）。</param>
/// <param name="Failures">失败条目（快照 id＋目标文件＋原因；单文件级失败＝隔离、其余照常；按处理序）。</param>
public sealed record PrefabSaveResult(
    IReadOnlyList<PrefabSaveSuccess> Succeeded,
    IReadOnlyList<PrefabSaveFailure> Failures);

/// <summary>
/// 效果快照落盘器（批 2 加性·写方向）：把快照集合写成单文件（<c>&lt;root.id&gt;.prefab.json</c>，顶层）。
/// 命名/结构口径与 <see cref="SnapshotStore.LoadDirectory"/> 成对（<c>*.prefab.json</c>、单快照一文件、顶层）。
/// 数据源＝外部快照集合（静态管道——效果创建/编译产物在管理器外部诞生；不提供实例状态导出便捷面）。
/// 失败契约（与读侧「单文件隔离」对偶）：
/// ①单快照级失败＝隔离（记失败条目、跳过该条、其余继续）——含非法 id、批内重复、序列化失败、IO 失败；
/// ②目录准备失败（无法创建/准备目标目录）＝整体性失败（抛出，不依赖单条内容）；
/// ③空输入列表＝空成功（零文件；不做目录准备、目标目录不存在时保持不存在）；
/// ④非法 id＝拒绝（不转义、不清理、不照写——含路径穿越防护：单点校验＋全路径归化前缀二道防线）；
/// ⑤批内重复 id＝首见写出、重复者进失败集（输入序「首见」口径）；跨运行同 id＝内容覆盖（导出语义、不清目录）；
/// ⑥只写不删：从不删除/清理目标目录既有内容（未覆盖的旧文件、无关文件、子目录均保留）；部分失败不回滚（已写成功的文件保留）。
/// 文本契约（写文件）：UTF-8 无 BOM；换行/缩进/尾随换行/键序由落盘出口固定（2 空格缩进、LF、尾随换行、不转义非 ASCII）。
/// </summary>
public static class PrefabWriter
{
    /// <summary>单快照文件名后缀（与读侧扫描模式 <c>*.prefab.json</c> 成对）。</summary>
    public const string FileSuffix = ".prefab.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 目录级落盘：逐快照归约到单文件写（<see cref="SaveFile"/> 为原语）；目录不存在＝自动创建（含多级）。
    /// </summary>
    /// <param name="directory">目标目录（不存在＝自动创建；无法创建＝抛出）。</param>
    /// <param name="snapshots">效果快照集合（声明序；空＝空成功、零文件、不做目录准备）。</param>
    /// <returns>成功／失败分列的落盘结果。</returns>
    /// <exception cref="ArgumentNullException">directory 或 snapshots 为 null；snapshots 含 null 元素。</exception>
    /// <exception cref="ArgumentException">directory 为 null/空白。</exception>
    /// <exception cref="IOException">目录准备失败（整体性——无法创建/准备目标目录）。</exception>
    /// <exception cref="UnauthorizedAccessException">目录准备失败（权限）。</exception>
    public static PrefabSaveResult SaveDirectory(string directory, IEnumerable<EffectSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(snapshots);

        var snapshotList = new List<EffectSnapshot>();
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            snapshotList.Add(snapshot);
        }

        var succeeded = new List<PrefabSaveSuccess>();
        var failures = new List<PrefabSaveFailure>();

        if (snapshotList.Count == 0)
        {
            // 空输入列表＝空成功（零文件——无单条内容、不做目录准备：副作用最小）。
            return new PrefabSaveResult(succeeded, failures);
        }

        Directory.CreateDirectory(directory); // 目录准备失败＝整体性失败（抛出）。

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshotList)
        {
            WriteSingle(directory, snapshot, seenIds, succeeded, failures);
        }

        return new PrefabSaveResult(succeeded, failures);
    }

    /// <summary>
    /// 单文件写（原语：目录＋单快照——复用命名规则 <c>&lt;root.id&gt;.prefab.json</c> 与安全校验；
    /// 不提供任意目标路径形态）。目录不存在＝自动创建。
    /// </summary>
    /// <param name="directory">目标目录（不存在＝自动创建；无法创建＝抛出）。</param>
    /// <param name="snapshot">效果快照（id 取自 <c>root.id</c>）。</param>
    /// <returns>成功／失败分列的落盘结果（单快照时成功/失败条目各 ≤1）。</returns>
    /// <exception cref="ArgumentNullException">directory 或 snapshot 为 null。</exception>
    /// <exception cref="ArgumentException">directory 为 null/空白。</exception>
    /// <exception cref="IOException">目录准备失败（整体性——无法创建/准备目标目录）。</exception>
    /// <exception cref="UnauthorizedAccessException">目录准备失败（权限）。</exception>
    public static PrefabSaveResult SaveFile(string directory, EffectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(directory); // 目录准备失败＝整体性失败（抛出）。

        var succeeded = new List<PrefabSaveSuccess>();
        var failures = new List<PrefabSaveFailure>();
        WriteSingle(directory, snapshot, new HashSet<string>(StringComparer.Ordinal), succeeded, failures);
        return new PrefabSaveResult(succeeded, failures);
    }

    private static void WriteSingle(
        string directory,
        EffectSnapshot snapshot,
        HashSet<string> seenIds,
        List<PrefabSaveSuccess> succeeded,
        List<PrefabSaveFailure> failures)
    {
        var prefabId = snapshot.Root.Id;
        var targetFile = Path.Combine(directory, prefabId + FileSuffix);

        // 非法 id＝拒绝（不转义、不清理、不照写）：文件系统层不可表达/不安全（含路径穿越防护——如 ..、分隔符）。
        if (IsIllegalPrefabId(prefabId) || !IsWithinDirectory(directory, targetFile))
        {
            failures.Add(new PrefabSaveFailure(
                prefabId, targetFile, "效果快照 id 非法（含文件系统非法字符/路径分隔符/穿越序列——拒绝写出）。"));
            return;
        }

        // 同批重复＝首见写出、重复者失败（输入序口径）。
        if (!seenIds.Add(prefabId))
        {
            failures.Add(new PrefabSaveFailure(prefabId, targetFile, "效果快照 id 重复（同批内已写出首见者——跳过）。"));
            return;
        }

        string json;
        try
        {
            json = PrefabJson.SerializeForDisk(snapshot); // 文本契约在落盘出口生效（2 空格缩进/LF/尾随换行/不转义非 ASCII）。
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException
            or ArgumentOutOfRangeException or InvalidOperationException)
        {
            failures.Add(new PrefabSaveFailure(prefabId, targetFile, $"序列化失败：{ex.Message}"));
            return;
        }

        try
        {
            File.WriteAllText(targetFile, json, Utf8NoBom); // UTF-8 无 BOM（文本契约）。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new PrefabSaveFailure(prefabId, targetFile, $"写入失败：{ex.Message}"));
            return;
        }

        succeeded.Add(new PrefabSaveSuccess(prefabId, targetFile));
    }

    /// <summary>
    /// 效果快照 id 合法性判定（批 2 规则——**单一真源**：空白／路径分隔符／平台非法文件名字符／<c>.</c>／<c>..</c>；
    /// 批 5 起可见性放宽〔private→public，加性〕供离线编译驱动复用——驱动侧身份语义层判定与落盘拒绝共用同一规则）。
    /// </summary>
    public static bool IsIllegalPrefabId(string prefabId)
    {
        if (string.IsNullOrWhiteSpace(prefabId))
        {
            return true;
        }

        if (prefabId.Contains('/') || prefabId.Contains('\\'))
        {
            return true;
        }

        if (prefabId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return true;
        }

        // 防御性：." / ".." 相对路径段（前缀拼接后仍属穿越风险面——「拒绝＋报告」为最稳口径，完整沿用卡侧规则）。
        return prefabId is "." or "..";
    }

    private static bool IsWithinDirectory(string directory, string file)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var prefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;

        return Path.GetFullPath(file).StartsWith(prefix, StringComparison.Ordinal);
    }
}
