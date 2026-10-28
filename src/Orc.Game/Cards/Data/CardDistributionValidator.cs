using Orc.Cards;

namespace Orc.Game.Cards.Data;

/// <summary>
/// 分发预检条目类别（维度面级，七类；细维度由 <see cref="CardDistributionValidationEntry.Message"/> 原文承载）。
/// 类别字面值为稳定契约（测试锁定，后续不轻易变更）。级别口径：
/// 目录缺失／跨目录与跨来源重复冲突／读面 Failures 映射＝Error；引用未解析（不可确证）＝Warning。
/// </summary>
public enum CardDistributionValidationCategory
{
    /// <summary>目录缺失（输入面）：显式传入的卡目录/效果库目录不存在（预检严格语义——不沿用读面"宽容装载"口径）。</summary>
    DirectoryMissing,

    /// <summary>卡侧汇入（读面事实，照实汇入）：<c>CardDataLoader</c> 的 Failures→Error、Warnings→Warning。</summary>
    CardSideImport,

    /// <summary>库侧汇入：效果库文件的静态读取/结构失败（<c>PrefabJson</c> 静态反序列化，不经管理器件）。</summary>
    LibrarySideImport,

    /// <summary>卡 id 重复（跨卡目录）：合并后双双到达注册面、卡库键唯一——Error。</summary>
    CardIdDuplicate,

    /// <summary>库内重复（同库目录内两条 / 跨库目录）：静态读取下两个快照都会到达注册面——Error。</summary>
    LibraryPrefabDuplicate,

    /// <summary>跨来源冲突（内联×内联跨卡目录、库×内联）：运行期注册面单一名空间下同 id 双来源必然冲突——Error。</summary>
    CrossSourceConflict,

    /// <summary>引用未解析：卡声明的 prefab id 不在〔效果库 ∪ 全部卡成功内联并集〕中（可能由宿主代码注册提供——Warning、不升级）。</summary>
    UnresolvedReference,
}

/// <summary>
/// 分发预检条目（Errors/Warnings 分列；逐项含类别、说明与来源锚点——可定位）。
/// 来源锚点按可用性提供（至少一个）：<see cref="File"/>（完整路径优先、不可得时退化为文件名）、
/// <see cref="CardId"/>、<see cref="PrefabId"/>；读面汇入条目保留原文于 <see cref="Message"/> 并尽可能附结构化来源。
/// </summary>
/// <param name="Category">维度面类别（稳定契约）。</param>
/// <param name="Message">人类可读说明（读面汇入＝原文逐字保留）。</param>
/// <param name="File">来源文件（完整路径或文件名；不可得＝null）。</param>
/// <param name="CardId">来源卡 id（不可得＝null）。</param>
/// <param name="PrefabId">来源效果预制体 id（不可得＝null）。</param>
public sealed record CardDistributionValidationEntry(
    CardDistributionValidationCategory Category,
    string Message,
    string? File = null,
    string? CardId = null,
    string? PrefabId = null);

/// <summary>
/// 分发预检报告（dry-run 校验输出；结构化内存记录，序列化形态由消费方决定）。
/// 汇总统计三段：输入面（卡目录数/库目录数＋路径清单）、通过面（成功卡定义数/成功快照数，分列可辨）、
/// 问题面（Errors/Warnings 分列）。gate 判定面＝<see cref="HasNoErrors"/>（语义＝Errors 集合为空，
/// 恒与 <see cref="ErrorCount"/> 一致）。报告确定性：同输入两次调用＝相同报告（条目顺序稳定）。
/// </summary>
/// <param name="CardDirectories">输入面：去重规范化后的卡目录清单。</param>
/// <param name="PrefabLibraryDirectories">输入面：去重规范化后的效果库目录清单。</param>
/// <param name="SuccessfulCardCount">通过面：成功卡定义数（读面 Definitions 合计）。</param>
/// <param name="SuccessfulLibrarySnapshotCount">通过面：库成功快照数（静态反序列化成功）。</param>
/// <param name="SuccessfulInlineSnapshotCount">通过面：卡成功内联数（读面 Prefabs 合计——被读面隔离者不计）。</param>
/// <param name="UnresolvedReferenceCount">问题面：未解析引用条数（UnresolvedReference 类别）。</param>
/// <param name="Errors">Error 条目（可确证问题；按处理序稳定）。</param>
/// <param name="Warnings">Warning 条目（未解析引用等不可确证问题；按处理序稳定）。</param>
public sealed record CardDistributionValidationReport(
    IReadOnlyList<string> CardDirectories,
    IReadOnlyList<string> PrefabLibraryDirectories,
    int SuccessfulCardCount,
    int SuccessfulLibrarySnapshotCount,
    int SuccessfulInlineSnapshotCount,
    int UnresolvedReferenceCount,
    IReadOnlyList<CardDistributionValidationEntry> Errors,
    IReadOnlyList<CardDistributionValidationEntry> Warnings)
{
    /// <summary>输入面：卡目录数。</summary>
    public int CardDirectoryCount => CardDirectories.Count;

    /// <summary>输入面：效果库目录数。</summary>
    public int PrefabLibraryDirectoryCount => PrefabLibraryDirectories.Count;

    /// <summary>通过面：成功快照数（库成功装载＋卡成功内联合计——分列见 <see cref="SuccessfulLibrarySnapshotCount"/>／<see cref="SuccessfulInlineSnapshotCount"/>）。</summary>
    public int SuccessfulSnapshotCount => SuccessfulLibrarySnapshotCount + SuccessfulInlineSnapshotCount;

    /// <summary>问题面：Error 计数（＝<see cref="Errors"/> 集合大小）。</summary>
    public int ErrorCount => Errors.Count;

    /// <summary>问题面：Warning 计数（＝<see cref="Warnings"/> 集合大小）。</summary>
    public int WarningCount => Warnings.Count;

    /// <summary>
    /// gate 判定面：是否无 Error（语义＝Errors 集合为空；服务 CI"Error&gt;0 不放行"主用途）。
    /// 恒与 <see cref="ErrorCount"/> 一致（同一集合派生）。
    /// </summary>
    public bool HasNoErrors => Errors.Count == 0;
}

/// <summary>
/// 分发预检器（dry-run 校验器；端到端创作卡牌工具链·批 3·设计定稿 §决策 1 方法第 2 步「校验」）：
/// 对分发数据（卡目录＋效果库目录）做**纯只读静态校验**并输出结构化报告（Errors/Warnings 分列＋汇总统计），
/// 在"注册/装载进对局"之前尽早暴露配置错误。
///
/// 校验维度（四维）：a) 读取/结构汇总——复用 <see cref="CardDataLoader"/> 读面结果（Failures→Error、Warnings→Warning）＋
/// 库侧静态读取失败；b) id 重复与冲突——跨卡目录卡 id、跨卡目录内联 id、库内（同库/跨库）与库×内联跨来源；
/// c) 引用完整性——卡声明的 prefab id ∈〔效果库（合并）∪ 全部卡成功内联并集〕，未解析＝Warning（宿主代码注册
/// 为合法通道、静态面无法排除——不升级为 Error）；d) 内联完整性——id 非空/冲突复用读面既有判定并汇入。
///
/// 边界语义（grill 裁定）：
/// ① 输入＝卡目录＋效果库目录（各支持多个、可单边缺省；全空＝参数异常——空报告会被误读为"校验通过"）；
/// ② 同一目录重复传入＝校验器自行去重（不产生假重复）；③ 显式传入目录不存在＝Error（报告式、不抛）；
/// ④ 空目录＝零错误；⑤ 部分失败不早退（报告式全收集）；⑥ 同目录内差异＝读面事实（照实汇入）、
/// 跨目录/跨来源＝本器新增判定（按注册面冲突基准）；⑦ 不启动对局、不注册进引擎实例、不修改任何数据
/// （库侧读取经 <see cref="PrefabJson"/> 静态反序列化，不经 <c>SnapshotStore</c> 实例）。
/// </summary>
public static class CardDistributionValidator
{
    /// <summary>
    /// 执行分发预检（dry-run）。
    /// </summary>
    /// <param name="cardDirectories">卡目录清单（多目录合并校验；null/空＝缺省）。</param>
    /// <param name="prefabLibraryDirectories">效果库目录清单（多库合并为单一逻辑库；null/空＝缺省）。</param>
    /// <returns>结构化报告（Errors/Warnings 分列＋汇总统计＋gate 判定面）。</returns>
    /// <exception cref="ArgumentException">全空输入（无任何目录参数）或目录清单含 null/空白项（参数层 fail-fast）。</exception>
    public static CardDistributionValidationReport Validate(
        IReadOnlyList<string>? cardDirectories = null,
        IReadOnlyList<string>? prefabLibraryDirectories = null)
    {
        var cards = NormalizeDirectories(cardDirectories, nameof(cardDirectories));
        var libraries = NormalizeDirectories(prefabLibraryDirectories, nameof(prefabLibraryDirectories));

        if (cards.Count == 0 && libraries.Count == 0)
        {
            // 全空输入＝参数异常：无输入的"校验"属调用方错误；空报告会被误读为"校验通过"（假阴性）。
            throw new ArgumentException("至少提供一个目录参数（卡目录或效果库目录）——全空输入不产出报告。");
        }

        var errors = new List<CardDistributionValidationEntry>();
        var warnings = new List<CardDistributionValidationEntry>();

        // ── ① 目录缺失（严格语义：显式传入的目录不存在＝Error；其余照常处理——报告式全收集、不早退）──
        var existingCards = CollectExistingDirectories(cards, errors, "卡目录");
        var existingLibraries = CollectExistingDirectories(libraries, errors, "效果库目录");

        // ── ② 卡侧装载（逐目录调用读面：同目录内重复/隔离＝读面事实；文件序显式排序保报告确定性）──
        var perDirectory = new List<(string Directory, CardDataLoadResult Result)>();
        foreach (var directory in existingCards)
        {
            var files = Directory
                .EnumerateFiles(directory, CardDataLoader.FilePattern, SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.Ordinal);
            perDirectory.Add((directory, CardDataLoader.LoadFiles(files)));
        }

        var successfulCardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, result) in perDirectory)
        {
            foreach (var entry in result.Definitions)
            {
                successfulCardIds.Add(entry.Id);
            }
        }

        // 读面 Failures→Error、Warnings→Warning（原文逐字保留；尽可能附结构化来源锚点）。
        foreach (var (_, result) in perDirectory)
        {
            foreach (var failure in result.Failures)
            {
                errors.Add(new CardDistributionValidationEntry(
                    CardDistributionValidationCategory.CardSideImport, failure.Error, File: failure.File));
            }

            foreach (var warning in result.Warnings)
            {
                var (file, cardId) = ExtractWarningSource(warning, successfulCardIds);
                warnings.Add(new CardDistributionValidationEntry(
                    CardDistributionValidationCategory.CardSideImport, warning, File: file, CardId: cardId));
            }
        }

        // ── ③ v4 组件资产（同目录）：行为的**唯一**制品来源（v4-only 后旧格式 *.prefab.json 不再支持）──
        // 旧 prefab 扫描段已删除（`PrefabJson.Deserialize` 随旧装载链移除）——预检的"可解析 id"集合只由
        // *.component.json 与卡内联构成。
        //
        // **「效果引用可解析」的 v4 判据（§29 第 4 项，唯一口径）**：
        //   目录里存在一个 `*.component.json`（`AssetSchema.FilePattern`），且能经 `AssetSchema.Read` 通过
        //   ——即 `schemaVersion = 4`、`asset.kind = component`、字段集合封闭（旧字段名如 `mainTrigger`/`hooks`
        //   一律拒绝）。命中即该 `asset.id` 进入可解析集合；文件存在但解析失败＝**库侧导入 Error**，且不贡献 id
        //   （引用方随后按"未解析"告警，两条记录都保留：装载会失败、引用也没着落，都该看得见）。
        //   旧格式 `*.prefab.json` 不走任何一条通道（v4-only 装载链不读它）——留在目录里只是死文件，不算可解析。
        //   与生产装载同源：`CardPackAssembler` 只装 `effects-v4/*.component.json`，因此"可解析"≡"进对局装得上"。
        var componentAssets = new List<(string Directory, string File, string Id)>();
        foreach (var directory in existingLibraries)
        {
            var files = Directory
                .EnumerateFiles(directory, Orc.Core.Model.AssetSchema.FilePattern, SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.Ordinal);

            foreach (var file in files)
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(new CardDistributionValidationEntry(
                        CardDistributionValidationCategory.LibrarySideImport, $"读取失败：{ex.Message}", File: file));
                    continue;
                }

                try
                {
                    var asset = Orc.Core.Model.AssetSchema.Read(text, Path.GetFileName(file));
                    componentAssets.Add((directory, file, asset.Id));
                }
                catch (Orc.Core.Model.PrefabLoadException ex)
                {
                    errors.Add(new CardDistributionValidationEntry(
                        CardDistributionValidationCategory.LibrarySideImport, ex.Message, File: file));
                }
            }
        }

        // ── ④ 库内重复（同库目录内两条 / 跨库目录）：静态读取下两个资产都会到达注册面——Error ──
        var firstLibrarySource = new Dictionary<string, (string Directory, string File)>(StringComparer.Ordinal);
        foreach (var (directory, file, id) in componentAssets)
        {
            if (firstLibrarySource.TryGetValue(id, out var first))
            {
                var scope = string.Equals(first.Directory, directory, StringComparison.Ordinal) ? "同库目录内" : "跨库目录";
                errors.Add(new CardDistributionValidationEntry(
                    CardDistributionValidationCategory.LibraryPrefabDuplicate,
                    $"效果库预制体 id '{id}' {scope}重复：'{first.File}' 与 '{file}'（运行期重复注册被拒绝——装载进对局必然失败）。",
                    File: file,
                    PrefabId: id));
            }
            else
            {
                firstLibrarySource.Add(id, (directory, file));
            }
        }

        // ── ⑤ 卡 id 重复（跨卡目录：合并后双双到达注册面、卡库键唯一——Error；同目录内由读面 Failures 承载）──
        var firstCardSource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (directory, result) in perDirectory)
        {
            foreach (var entry in result.Definitions)
            {
                if (firstCardSource.TryGetValue(entry.Id, out var firstDirectory))
                {
                    errors.Add(new CardDistributionValidationEntry(
                        CardDistributionValidationCategory.CardIdDuplicate,
                        $"卡 id '{entry.Id}' 跨卡目录重复：'{firstDirectory}' 与 '{directory}'（卡库键唯一——重复注册被拒绝）。",
                        CardId: entry.Id));
                }
                else
                {
                    firstCardSource.Add(entry.Id, directory);
                }
            }
        }

        // ── ⑥ 内联×内联跨卡目录（读面已软隔离同目录第二条——预检只见跨目录残留；按注册面冲突基准＝Error）──
        var inlineIds = new HashSet<string>(StringComparer.Ordinal);
        var firstInlineSource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (directory, result) in perDirectory)
        {
            foreach (var snapshot in result.Prefabs)
            {
                var id = snapshot.Root.Id;
                inlineIds.Add(id);
                if (firstInlineSource.TryGetValue(id, out var firstDirectory))
                {
                    errors.Add(new CardDistributionValidationEntry(
                        CardDistributionValidationCategory.CrossSourceConflict,
                        $"内联预制体 id '{id}' 跨卡目录重复：'{firstDirectory}' 与 '{directory}'（运行期注册面单一名空间下必然冲突）。",
                        PrefabId: id));
                }
                else
                {
                    firstInlineSource.Add(id, directory);
                }
            }
        }

        // ── ⑦ 库×内联（跨来源冲突：同一 id 同时由效果库与卡内联提供——Error；每 id 一条） ──
        var reportedLibraryInline = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, file, id) in componentAssets)
        {
            if (inlineIds.Contains(id) && reportedLibraryInline.Add(id))
            {
                errors.Add(new CardDistributionValidationEntry(
                    CardDistributionValidationCategory.CrossSourceConflict,
                    $"效果预制体 id '{id}' 同时由效果库文件与卡内联提供：'{file}'（运行期注册面单一名空间下必然冲突）。",
                    File: file,
                    PrefabId: id));
            }
        }

        // ── ⑧ 引用未解析（解析集合＝〔效果库（全部库目录合并）∪ 全部卡成功内联并集〕；逐（卡 id，引用 id）一条）──
        var resolutionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, id) in componentAssets)
        {
            resolutionSet.Add(id);
        }

        resolutionSet.UnionWith(inlineIds);

        var unresolvedCount = 0;
        foreach (var (_, result) in perDirectory)
        {
            foreach (var (cardId, prefabIds) in result.EffectDeclarations)
            {
                foreach (var prefabId in prefabIds)
                {
                    if (resolutionSet.Contains(prefabId))
                    {
                        continue;
                    }

                    unresolvedCount++;
                    warnings.Add(new CardDistributionValidationEntry(
                        CardDistributionValidationCategory.UnresolvedReference,
                        libraries.Count == 0
                            ? $"卡 '{cardId}' 声明的效果预制体 '{prefabId}' 未解析：未提供效果库输入，无法静态判定（可能由宿主代码注册 RegisterPrefab 提供）。"
                            : $"卡 '{cardId}' 声明的效果预制体 '{prefabId}' 未在效果库或卡内联中解析（可能由宿主代码注册 RegisterPrefab 提供；纯数据分发请补齐效果库）。",
                        CardId: cardId,
                        PrefabId: prefabId));
                }
            }
        }

        return new CardDistributionValidationReport(
            CardDirectories: cards,
            PrefabLibraryDirectories: libraries,
            SuccessfulCardCount: perDirectory.Sum(item => item.Result.Definitions.Count),
            // 库成功装载数＝v4 组件条目数（旧格式快照条目已不再支持）。
            SuccessfulLibrarySnapshotCount: componentAssets.Count,
            SuccessfulInlineSnapshotCount: perDirectory.Sum(item => item.Result.Prefabs.Count),
            UnresolvedReferenceCount: unresolvedCount,
            Errors: errors.ToArray(),
            Warnings: warnings.ToArray());
    }

    /// <summary>目录清单规范化：null/空＝缺省；元素 null/空白＝参数层拒绝；路径规范化后 Ordinal 去重（同一路径值只处理一次）。</summary>
    private static List<string> NormalizeDirectories(IReadOnlyList<string>? directories, string paramName)
    {
        var result = new List<string>();
        if (directories is null || directories.Count == 0)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in directories)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("目录清单含 null/空白项（参数层拒绝）。", paramName);
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                normalized = raw;
            }

            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    /// <summary>检查目录存在性：不存在＝DirectoryMissing Error（报告式，不抛）；返回存在的目录（保持清单序）。</summary>
    private static List<string> CollectExistingDirectories(
        IReadOnlyList<string> directories,
        List<CardDistributionValidationEntry> errors,
        string label)
    {
        var existing = new List<string>();
        foreach (var directory in directories)
        {
            if (Directory.Exists(directory))
            {
                existing.Add(directory);
                continue;
            }

            errors.Add(new CardDistributionValidationEntry(
                CardDistributionValidationCategory.DirectoryMissing,
                $"{label}不存在：'{directory}'（预检严格语义——请检查路径）。",
                File: directory));
        }

        return existing;
    }

    /// <summary>
    /// 从读面警告文本中**尽力提取来源锚点**（"保留原文可追溯（必须）＋尽可能以结构化字段附上来源"）：
    /// 前缀＝首个全角冒号前段；以 <c>.card.json</c> 结尾＝文件名（读面文件级警告形态）；否则命中成功卡 id 集合＝卡 id。
    /// 不做细维度分类（读面文本不重新解析——细维度由原文承载）。
    /// </summary>
    private static (string? File, string? CardId) ExtractWarningSource(string warning, HashSet<string> successfulCardIds)
    {
        var separatorIndex = warning.IndexOf('：');
        if (separatorIndex <= 0)
        {
            return (null, null);
        }

        var prefix = warning[..separatorIndex];
        if (prefix.EndsWith(".card.json", StringComparison.OrdinalIgnoreCase))
        {
            return (prefix, null);
        }

        return successfulCardIds.Contains(prefix) ? (null, prefix) : (null, null);
    }
}
