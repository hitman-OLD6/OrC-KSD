using Orc.Cards;
using Orc.Game.Cards.Data;
using Xunit;
using Xunit.Abstractions;

namespace Orc.Game.Tests;

/// <summary>
/// 分发预检器（dry-run 校验器；X3·批 3）测试：报告维度全覆盖（读取/结构汇总、id 重复与冲突、
/// 引用完整性、内联完整性）＋边界语义（多目录/单边/全空/不存在/空目录/同目录去重）＋冲突矩阵全项
/// ＋报告契约（必须项统计、gate 判定面、稳定类别、来源锚点、逐条未解析）＋确定性＋**纯静态证明**
/// （测试仅经校验器 API＋临时目录数据——全程不构造 Match/LogicEngine/PrefabManager/CardEffectRegistry
/// 等引擎对象；"不修改数据"以调用前后输入目录快照〔文件清单＋内容〕不变作可观察证明；库侧不经管理器件由
/// API 设计保证）。数据构造：好/坏数据各形态；全部测试使用临时目录（GUID 隔离、测后清理），不写共享目录；
/// outputs 全量冒烟（卡语料＋KeywordPrefabs 库，缺失可辨识跳过）。
/// </summary>
public class CardDistributionValidatorTests
{
    private readonly ITestOutputHelper _output;

    public CardDistributionValidatorTests(ITestOutputHelper output) => _output = output;

    // ─────────────────────────────────────────────────────────────────────────
    // 数据构造辅助。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>最小合法卡组件集（factionCost＋tagData 为定义期必填槽位）。</summary>
    private const string BaseComponents =
        "{\"component\":\"factionCost\",\"faction\":\"Germany\",\"kredits\":1},{\"component\":\"tagData\",\"rarity\":\"Standard\"}";

    /// <summary>无效果组件的最小合法卡。</summary>
    private static string CardJson(string id) =>
        $"{{\"schemaVersion\":1,\"id\":\"{id}\",\"name\":\"{id}-name\",\"components\":[{BaseComponents}]}}";

    /// <summary>含 effects 组件的最小合法卡（prefabs 引用清单／inline 快照集由调用方给出——可各自缺省）。</summary>
    private static string CardJsonWithEffects(string id, string[]? prefabIds = null, string[]? inlineSnapshots = null)
    {
        var parts = new List<string>();
        if (prefabIds is not null)
        {
            parts.Add("\"prefabs\":[" + string.Join(",", prefabIds.Select(prefabId => $"\"{prefabId}\"")) + "]");
        }

        if (inlineSnapshots is not null)
        {
            parts.Add("\"inline\":[" + string.Join(",", inlineSnapshots) + "]");
        }

        var body = string.Join(",", parts);
        return $"{{\"schemaVersion\":1,\"id\":\"{id}\",\"name\":\"{id}-name\",\"components\":[{BaseComponents},{{\"component\":\"effects\",{body}}}]}}";
    }

    /// <summary>效果快照 JSON（合法形态：passive 主触发器＋程序集键事件）。</summary>
    private static string SnapshotJson(string id) =>
        $"{{\"schemaVersion\":2,\"root\":{{\"id\":\"{id}\",\"mainTrigger\":{{\"id\":\"main\",\"stableKey\":\"stable\",\"kind\":\"passive\",\"hooks\":[\"unit.deployed\"],\"events\":[{{\"id\":\"evt\",\"entry\":\"HandleAsync\",\"assemblyKey\":\"k\"}}]}}}}}}";

    /// <summary>
    /// 效果库文件的内容（**v4 组件资产**；§29 第 4 项的可解析判据）：
    /// 只有 `schemaVersion=4` ＋ `asset.kind=component` 的 `*.component.json` 才进入"可解析 id 集合"。
    /// </summary>
    private static string LibraryComponentJson(string id) =>
        "{\"schemaVersion\":4,\"asset\":{\"kind\":\"component\",\"id\":\"" + id + "\","
        + "\"requires-host\":\"any\",\"active-when\":\"loaded\",\"one-shot\":false,"
        + "\"private-fields\":[],\"members\":[],\"mounts\":[],\"wraps\":[]}}";

    /// <summary>缺 root.id 的内联快照（读面＝组件级反序列化失败——内联完整性形态）。</summary>
    private static string SnapshotJsonWithoutRootId() =>
        "{\"schemaVersion\":2,\"root\":{\"mainTrigger\":{\"id\":\"main\",\"stableKey\":\"stable\",\"kind\":\"passive\",\"hooks\":[\"unit.deployed\"],\"events\":[{\"id\":\"evt\",\"entry\":\"HandleAsync\",\"assemblyKey\":\"k\"}]}}}";

    /// <summary>临时目录（GUID 隔离；返回规范化完整路径）。</summary>
    private static string NewTempDir(string prefix)
    {
        var directory = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "cdv-" + prefix + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>不存在的目录路径（规范化完整路径——用于目录缺失构造）。</summary>
    private static string NewMissingDirPath(string prefix) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cdv-" + prefix + "-" + Guid.NewGuid().ToString("N")));

    private static void WriteCard(string directory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(directory, fileName + ".card.json"), json);

    private static void WriteLibraryComponent(string directory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(directory, fileName + ".component.json"), json);

    private static void CleanUp(params string[] directories)
    {
        foreach (var directory in directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>输入目录快照（相对路径＋内容；用于"零改动"可观察证明）。</summary>
    private static (string Relative, string Content)[] SnapshotDirectory(string directory) =>
        Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => (Path.GetRelativePath(directory, file), File.ReadAllText(file)))
            .ToArray();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 好数据与统计（验收 10：必须项统计＋gate 判定面；含跨目录共享引用不报冲突）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_Good_Data_Across_Multiple_Directories_Is_Clean_With_Required_Statistics()
    {
        var cardDirA = NewTempDir("cards-a");
        var cardDirB = NewTempDir("cards-b");
        var libDir1 = NewTempDir("lib-1");
        var libDir2 = NewTempDir("lib-2");
        try
        {
            // A：引用库 1 的 lib-x ＋ 本卡内联 inline-a。
            WriteCard(cardDirA, "a1", CardJsonWithEffects(
                "card-a1", prefabIds: new[] { "lib-x" }, inlineSnapshots: new[] { SnapshotJson("inline-a") }));
            // B：引用库 2 的 lib-y ＋ 跨目录共享引用 lib-x（另一目录也引用）＋ 本卡内联 inline-b。
            WriteCard(cardDirB, "b1", CardJsonWithEffects(
                "card-b1", prefabIds: new[] { "lib-y", "lib-x" }, inlineSnapshots: new[] { SnapshotJson("inline-b") }));
            WriteLibraryComponent(libDir1, "lib-x", LibraryComponentJson("lib-x"));
            WriteLibraryComponent(libDir2, "lib-y", LibraryComponentJson("lib-y"));

            var report = CardDistributionValidator.Validate(new[] { cardDirA, cardDirB }, new[] { libDir1, libDir2 });

            // 好数据口径＝Errors 与 Warnings 双零（含跨目录共享引用——引用不产生冲突条目）。
            Assert.Empty(report.Errors);
            Assert.Empty(report.Warnings);

            // 必须项统计：输入面／通过面（分列可辨）／问题面。
            Assert.Equal(2, report.CardDirectoryCount);
            Assert.Equal(2, report.PrefabLibraryDirectoryCount);
            Assert.Equal(new[] { cardDirA, cardDirB }, report.CardDirectories);
            Assert.Equal(new[] { libDir1, libDir2 }, report.PrefabLibraryDirectories);
            Assert.Equal(2, report.SuccessfulCardCount);
            Assert.Equal(2, report.SuccessfulLibrarySnapshotCount);
            Assert.Equal(2, report.SuccessfulInlineSnapshotCount);
            Assert.Equal(4, report.SuccessfulSnapshotCount);
            Assert.Equal(0, report.ErrorCount);
            Assert.Equal(0, report.WarningCount);
            Assert.Equal(0, report.UnresolvedReferenceCount);

            // gate 判定面（与 Errors 计数一致）。
            Assert.True(report.HasNoErrors);
        }
        finally
        {
            CleanUp(cardDirA, cardDirB, libDir1, libDir2);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 引用完整性（验收 8：解析集合＝〔库 ∪ 全部卡成功内联并集〕；退化面；逐条粒度）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cross_Card_Reference_Resolves_Through_Other_Cards_Inline_Across_Directories()
    {
        var cardDirA = NewTempDir("cards-a");
        var cardDirB = NewTempDir("cards-b");
        try
        {
            // A 目录卡引用 B 目录卡的内联 id（解析集合＝全部卡成功内联并集——跨目录无读面裁剪）。
            WriteCard(cardDirA, "a1", CardJsonWithEffects("card-a1", prefabIds: new[] { "inline-b" }));
            WriteCard(cardDirB, "b1", CardJsonWithEffects("card-b1", inlineSnapshots: new[] { SnapshotJson("inline-b") }));

            var report = CardDistributionValidator.Validate(new[] { cardDirA, cardDirB }, null);

            Assert.Empty(report.Errors);
            Assert.Empty(report.Warnings); // 命中即通过——不报（无库输入的退化解析集合）。
            Assert.Equal(1, report.SuccessfulInlineSnapshotCount);
            Assert.True(report.HasNoErrors);
        }
        finally
        {
            CleanUp(cardDirA, cardDirB);
        }
    }

    [Fact]
    public void Unresolved_References_Are_Warnings_With_Host_Registration_Explanation_One_Per_Reference()
    {
        var cardDir = NewTempDir("cards");
        var libDir = NewTempDir("lib");
        try
        {
            WriteCard(cardDir, "a1", CardJsonWithEffects(
                "card-a1", prefabIds: new[] { "lib-x", "missing-1", "missing-2" }));
            WriteLibraryComponent(libDir, "lib-x", LibraryComponentJson("lib-x"));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, new[] { libDir });

            // 未解析不升级为 Error（宿主代码注册为合法通道——静态面无法排除）。
            Assert.Empty(report.Errors);
            Assert.Equal(2, report.Warnings.Count); // 逐（卡 id，引用 id）一条。
            Assert.All(report.Warnings, warning =>
            {
                Assert.Equal(CardDistributionValidationCategory.UnresolvedReference, warning.Category);
                Assert.Equal("card-a1", warning.CardId);
                Assert.Contains("宿主代码注册 RegisterPrefab", warning.Message);
                Assert.Contains("补齐效果库", warning.Message);
            });
            Assert.Equal(new[] { "missing-1", "missing-2" }, report.Warnings.Select(warning => warning.PrefabId));
            Assert.Equal(2, report.UnresolvedReferenceCount);
            Assert.Equal(0, report.ErrorCount);
            Assert.True(report.HasNoErrors);
            Assert.Equal(1, report.SuccessfulLibrarySnapshotCount);
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    [Fact]
    public void Card_Only_Input_Explains_Missing_Library_For_Unresolved_References()
    {
        var cardDir = NewTempDir("cards");
        try
        {
            // 内联并入声明的 inline-a＝命中通过（不报）；引用 ghost＝仅内联面未命中（Warning 含"未提供效果库输入"文案）。
            WriteCard(cardDir, "a1", CardJsonWithEffects(
                "card-a1", prefabIds: new[] { "ghost" }, inlineSnapshots: new[] { SnapshotJson("inline-a") }));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, null);

            Assert.Empty(report.Errors);
            var warning = Assert.Single(report.Warnings);
            Assert.Equal(CardDistributionValidationCategory.UnresolvedReference, warning.Category);
            Assert.Equal("card-a1", warning.CardId);
            Assert.Equal("ghost", warning.PrefabId);
            Assert.Contains("未提供效果库输入", warning.Message);
            Assert.Equal(1, report.UnresolvedReferenceCount);
            Assert.True(report.HasNoErrors);
        }
        finally
        {
            CleanUp(cardDir);
        }
    }

    [Fact]
    public void Isolated_Inline_Does_Not_Participate_In_Resolution_Set()
    {
        var cardDir = NewTempDir("cards");
        try
        {
            // a1 引用 ghost；b1 内联 ghost（与 a1 的引用登记冲突——被读面隔离、不进解析集合）。
            WriteCard(cardDir, "a1", CardJsonWithEffects("card-a1", prefabIds: new[] { "ghost" }));
            WriteCard(cardDir, "b1", CardJsonWithEffects("card-b1", inlineSnapshots: new[] { SnapshotJson("ghost") }));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, null);

            Assert.Empty(report.Errors);
            Assert.Equal(2, report.Warnings.Count);

            // ① 读面隔离 Warning 照实汇入（卡级警告——b1 的内联被隔离）。
            Assert.Equal(CardDistributionValidationCategory.CardSideImport, report.Warnings[0].Category);
            Assert.Contains("与已注册项冲突", report.Warnings[0].Message);
            Assert.Equal("card-b1", report.Warnings[0].CardId);

            // ② 被隔离的内联不参与解析集合——对 ghost 的引用＝未解析 Warning。
            Assert.Equal(CardDistributionValidationCategory.UnresolvedReference, report.Warnings[1].Category);
            Assert.Equal("card-a1", report.Warnings[1].CardId);
            Assert.Equal("ghost", report.Warnings[1].PrefabId);
            Assert.Equal(0, report.SuccessfulInlineSnapshotCount);
        }
        finally
        {
            CleanUp(cardDir);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // id 重复与冲突（跨卡目录卡 id／库内同库与跨库／跨来源冲突）。
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // ⑨ v4 判据（§29 第 4 项）：「效果引用可解析」＝目录内存在 **v4 组件资产**且其 id 匹配。
    //    旧格式文件（*.prefab.json）在 v4-only 装载链下不贡献任何可解析 id——留在目录里只是死文件。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>v4 组件资产是唯一的可解析来源：卡引用它＝零告警；旧格式同 id 文件不被采信＝仍是未解析。</summary>
    [Fact]
    public void V4_Component_Asset_Is_The_Only_Resolvable_Reference_Source()
    {
        var cardDir = NewTempDir("cards-v4");
        var libDir = NewTempDir("lib-v4");
        try
        {
            WriteCard(cardDir, "a1", CardJsonWithEffects("card-a1", prefabIds: new[] { "lib-v4-ok", "lib-legacy-only" }));

            // v4 组件资产（可解析）＋旧格式文件（同名 id，但 v4-only 装载链不读它）。
            WriteLibraryComponent(libDir, "lib-v4-ok", LibraryComponentJson("lib-v4-ok"));
            File.WriteAllText(
                Path.Combine(libDir, "lib-legacy-only.prefab.json"),
                "{\"schemaVersion\":3,\"root\":{\"id\":\"lib-legacy-only\"}}");

            var report = CardDistributionValidator.Validate(new[] { cardDir }, new[] { libDir });

            Assert.Empty(report.Errors);
            Assert.Equal(1, report.SuccessfulLibrarySnapshotCount); // 只有 v4 组件被读面接受
            var warning = Assert.Single(report.Warnings);           // 旧格式文件不贡献可解析 id
            Assert.Equal(CardDistributionValidationCategory.UnresolvedReference, warning.Category);
            Assert.Equal("lib-legacy-only", warning.PrefabId);
            Assert.Contains("未在效果库或卡内联中解析", warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    /// <summary>可解析＝**schema 合法**：`*.component.json` 但携带旧字段（<c>mainTrigger</c>）＝库侧导入错误，且不算解析。</summary>
    [Fact]
    public void Component_File_With_Legacy_Fields_Is_Import_Error_And_Not_Resolvable()
    {
        var cardDir = NewTempDir("cards-v4bad");
        var libDir = NewTempDir("lib-v4bad");
        try
        {
            WriteCard(cardDir, "a1", CardJsonWithEffects("card-a1", prefabIds: new[] { "lib-bad" }));
            WriteLibraryComponent(
                libDir,
                "lib-bad",
                "{\"schemaVersion\":4,\"asset\":{\"kind\":\"component\",\"id\":\"lib-bad\","
                + "\"mainTrigger\":{\"id\":\"main\"}}}");

            var report = CardDistributionValidator.Validate(new[] { cardDir }, new[] { libDir });

            var importError = Assert.Single(
                report.Errors.Where(e => e.Category == CardDistributionValidationCategory.LibrarySideImport));
            Assert.Equal(Path.Combine(libDir, "lib-bad.component.json"), importError.File);
            Assert.Contains("mainTrigger", importError.Message, StringComparison.Ordinal);

            // 该文件没进可解析集合 → 卡引用仍是未解析（Warning）。
            Assert.Contains(
                report.Warnings,
                w => w.Category == CardDistributionValidationCategory.UnresolvedReference
                     && string.Equals(w.PrefabId, "lib-bad", StringComparison.Ordinal));
            Assert.Equal(0, report.SuccessfulLibrarySnapshotCount);
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    [Fact]
    public void Card_Id_Duplicate_Across_Directories_Is_Error()
    {
        var cardDirA = NewTempDir("cards-a");
        var cardDirB = NewTempDir("cards-b");
        try
        {
            WriteCard(cardDirA, "dup", CardJson("dup-card"));
            WriteCard(cardDirB, "dup", CardJson("dup-card"));

            var report = CardDistributionValidator.Validate(new[] { cardDirA, cardDirB }, null);

            var error = Assert.Single(report.Errors);
            Assert.Equal(CardDistributionValidationCategory.CardIdDuplicate, error.Category);
            Assert.Equal("dup-card", error.CardId);
            Assert.Contains($"'{cardDirA}'", error.Message);
            Assert.Contains($"'{cardDirB}'", error.Message);
            Assert.Contains("重复", error.Message);
            Assert.False(report.HasNoErrors);
            Assert.Equal(1, report.ErrorCount);
            Assert.Empty(report.Warnings);
            Assert.Equal(2, report.SuccessfulCardCount); // 两份定义各自成功（重复＝注册面问题，非装载问题）。
        }
        finally
        {
            CleanUp(cardDirA, cardDirB);
        }
    }

    [Fact]
    public void Same_Directory_Passed_Twice_Does_Not_Produce_Phantom_Duplicates()
    {
        var cardDir = NewTempDir("cards");
        var libDir = NewTempDir("lib");
        try
        {
            WriteCard(cardDir, "a1", CardJson("card-a1"));
            WriteLibraryComponent(libDir, "lib-x", LibraryComponentJson("lib-x"));

            var report = CardDistributionValidator.Validate(new[] { cardDir, cardDir }, new[] { libDir, libDir });

            // 校验器自行去重（同一路径值只处理一次——不产生假重复）。
            Assert.Empty(report.Errors);
            Assert.Empty(report.Warnings);
            Assert.Equal(1, report.CardDirectoryCount);
            Assert.Equal(1, report.PrefabLibraryDirectoryCount);
            Assert.Equal(1, report.SuccessfulCardCount);
            Assert.Equal(1, report.SuccessfulLibrarySnapshotCount);
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    [Fact]
    public void Library_Duplicate_Within_Single_Library_Is_Error()
    {
        var libDir = NewTempDir("lib");
        try
        {
            WriteLibraryComponent(libDir, "dup-a", LibraryComponentJson("dup-shot"));
            WriteLibraryComponent(libDir, "dup-b", LibraryComponentJson("dup-shot"));

            var report = CardDistributionValidator.Validate(null, new[] { libDir });

            var error = Assert.Single(report.Errors);
            Assert.Equal(CardDistributionValidationCategory.LibraryPrefabDuplicate, error.Category);
            Assert.Equal("dup-shot", error.PrefabId);
            Assert.Contains("同库目录内", error.Message);
            Assert.Contains(Path.Combine(libDir, "dup-a.component.json"), error.Message);
            Assert.Contains(Path.Combine(libDir, "dup-b.component.json"), error.Message);
            Assert.Equal(Path.Combine(libDir, "dup-b.component.json"), error.File); // 第二来源（触发重复者）。
            Assert.False(report.HasNoErrors);
            Assert.Equal(2, report.SuccessfulLibrarySnapshotCount);
        }
        finally
        {
            CleanUp(libDir);
        }
    }

    [Fact]
    public void Library_Duplicate_Across_Libraries_Is_Error()
    {
        var libDir1 = NewTempDir("lib-1");
        var libDir2 = NewTempDir("lib-2");
        try
        {
            WriteLibraryComponent(libDir1, "x", LibraryComponentJson("dup-shot"));
            WriteLibraryComponent(libDir2, "x", LibraryComponentJson("dup-shot"));

            var report = CardDistributionValidator.Validate(null, new[] { libDir1, libDir2 });

            var error = Assert.Single(report.Errors);
            Assert.Equal(CardDistributionValidationCategory.LibraryPrefabDuplicate, error.Category);
            Assert.Equal("dup-shot", error.PrefabId);
            Assert.Contains("跨库目录", error.Message);
            Assert.False(report.HasNoErrors);
        }
        finally
        {
            CleanUp(libDir1, libDir2);
        }
    }

    [Fact]
    public void Inline_Duplicate_Across_Card_Directories_Is_Cross_Source_Conflict()
    {
        var cardDirA = NewTempDir("cards-a");
        var cardDirB = NewTempDir("cards-b");
        try
        {
            WriteCard(cardDirA, "a1", CardJsonWithEffects("card-a1", inlineSnapshots: new[] { SnapshotJson("shared-inline") }));
            WriteCard(cardDirB, "b1", CardJsonWithEffects("card-b1", inlineSnapshots: new[] { SnapshotJson("shared-inline") }));

            var report = CardDistributionValidator.Validate(new[] { cardDirA, cardDirB }, null);

            var error = Assert.Single(report.Errors);
            Assert.Equal(CardDistributionValidationCategory.CrossSourceConflict, error.Category);
            Assert.Equal("shared-inline", error.PrefabId);
            Assert.Contains("跨卡目录", error.Message);
            Assert.False(report.HasNoErrors);
        }
        finally
        {
            CleanUp(cardDirA, cardDirB);
        }
    }

    [Fact]
    public void Library_And_Inline_Same_Id_Is_Cross_Source_Conflict()
    {
        var cardDir = NewTempDir("cards");
        var libDir = NewTempDir("lib");
        try
        {
            WriteCard(cardDir, "a1", CardJsonWithEffects("card-a1", inlineSnapshots: new[] { SnapshotJson("shared-id") }));
            WriteLibraryComponent(libDir, "shared", LibraryComponentJson("shared-id"));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, new[] { libDir });

            var error = Assert.Single(report.Errors);
            Assert.Equal(CardDistributionValidationCategory.CrossSourceConflict, error.Category);
            Assert.Equal("shared-id", error.PrefabId);
            Assert.Equal(Path.Combine(libDir, "shared.component.json"), error.File);
            Assert.Contains("同时由效果库文件与卡内联提供", error.Message);
            Assert.False(report.HasNoErrors);
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 同目录内差异＝读面事实（照实汇入、不升级）——内联×内联／引用×引用。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inline_Duplicate_Within_Same_Directory_Is_Load_Face_Warning_Not_Upgraded()
    {
        var cardDir = NewTempDir("cards");
        try
        {
            WriteCard(cardDir, "a1", CardJsonWithEffects("card-a1", inlineSnapshots: new[] { SnapshotJson("dup-inline") }));
            WriteCard(cardDir, "b1", CardJsonWithEffects("card-b1", inlineSnapshots: new[] { SnapshotJson("dup-inline") }));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, null);

            // 同目录内第二条被读面软隔离（不进注册面）——照实汇入 Warning、不升级为 Error。
            Assert.Empty(report.Errors);
            var warning = Assert.Single(report.Warnings);
            Assert.Equal(CardDistributionValidationCategory.CardSideImport, warning.Category);
            Assert.Contains("与已注册项冲突", warning.Message);
            Assert.Equal("card-b1", warning.CardId); // 卡级警告前缀＝卡 id（结构化来源锚点提取）。
            Assert.Equal(1, report.SuccessfulInlineSnapshotCount); // 第二条不进成功内联集。
        }
        finally
        {
            CleanUp(cardDir);
        }
    }

    [Fact]
    public void Duplicate_Reference_Within_Same_Directory_Is_Load_Face_Warning()
    {
        var cardDir = NewTempDir("cards");
        try
        {
            WriteCard(cardDir, "a1", CardJsonWithEffects("card-a1", prefabIds: new[] { "ref-x" }));
            WriteCard(cardDir, "b1", CardJsonWithEffects("card-b1", prefabIds: new[] { "ref-x" }));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, null);

            // 同目录内第二条引用被读面裁剪（隔离该条）——照实汇入 Warning（不升级——快照面无双来源）。
            Assert.Empty(report.Errors);
            Assert.Equal(2, report.Warnings.Count);
            Assert.Equal(CardDistributionValidationCategory.CardSideImport, report.Warnings[0].Category);
            Assert.Contains("重复注册（隔离该条）", report.Warnings[0].Message);
            Assert.Equal("card-b1", report.Warnings[0].CardId);

            // 被裁剪的 b1 声明不含 ref-x；a1 的 ref-x 无可解析来源＝未解析 Warning。
            Assert.Equal(CardDistributionValidationCategory.UnresolvedReference, report.Warnings[1].Category);
            Assert.Equal("card-a1", report.Warnings[1].CardId);
            Assert.Equal("ref-x", report.Warnings[1].PrefabId);
        }
        finally
        {
            CleanUp(cardDir);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 读取/结构汇总与内联完整性（读面 Failures/Warnings 汇入；混合场景不早退）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bad_Card_File_Is_Isolated_While_Other_Cards_And_Dimensions_Continue()
    {
        var cardDir = NewTempDir("cards");
        var libDir = NewTempDir("lib");
        try
        {
            WriteCard(cardDir, "good", CardJsonWithEffects("card-good", prefabIds: new[] { "lib-x" }));
            WriteCard(cardDir, "bad", "{ not json");
            WriteLibraryComponent(libDir, "lib-x", LibraryComponentJson("lib-x"));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, new[] { libDir });

            var error = Assert.Single(report.Errors);
            Assert.Equal(CardDistributionValidationCategory.CardSideImport, error.Category);
            Assert.Equal(Path.Combine(cardDir, "bad.card.json"), error.File);
            Assert.Contains("JSON 非法", error.Message);
            Assert.Empty(report.Warnings); // 好卡引用 lib-x 可解析——无未解析。
            Assert.Equal(1, report.SuccessfulCardCount); // 坏文件不阻断其余（报告式全收集）。
            Assert.Equal(1, report.SuccessfulLibrarySnapshotCount);
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    [Fact]
    public void Bad_Library_File_Is_Isolated_While_Other_Snapshots_Continue()
    {
        var libDir = NewTempDir("lib");
        try
        {
            WriteLibraryComponent(libDir, "good", LibraryComponentJson("lib-x"));
            WriteLibraryComponent(libDir, "bad", "{ not json");

            var report = CardDistributionValidator.Validate(null, new[] { libDir });

            var error = Assert.Single(report.Errors);
            Assert.Equal(CardDistributionValidationCategory.LibrarySideImport, error.Category);
            Assert.Equal(Path.Combine(libDir, "bad.component.json"), error.File);
            Assert.Contains("JSON 非法", error.Message);
            Assert.Equal(1, report.SuccessfulLibrarySnapshotCount);
            Assert.Equal(1, report.PrefabLibraryDirectoryCount);
        }
        finally
        {
            CleanUp(libDir);
        }
    }

    [Fact]
    public void Inline_Without_Root_Id_Is_Load_Face_Warning()
    {
        var cardDir = NewTempDir("cards");
        try
        {
            // 内联快照缺 root.id——读面以组件级反序列化失败隔离 effects 组件（内联完整性复用读面判定）。
            WriteCard(cardDir, "a1", CardJsonWithEffects("card-a1", inlineSnapshots: new[] { SnapshotJsonWithoutRootId() }));

            var report = CardDistributionValidator.Validate(new[] { cardDir }, null);

            Assert.Empty(report.Errors);
            var warning = Assert.Single(report.Warnings);
            Assert.Equal(CardDistributionValidationCategory.CardSideImport, warning.Category);
            Assert.Contains("组件 'effects' 反序列化失败", warning.Message);
            Assert.Equal("a1.card.json", warning.File); // 文件级警告前缀＝文件名（来源锚点提取）。
            Assert.Equal(0, report.SuccessfulInlineSnapshotCount);
        }
        finally
        {
            CleanUp(cardDir);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 目录边界（缺失/全缺失/空目录/单边/去重）与参数层。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_Directories_Are_Errors_While_Existing_Ones_Continue()
    {
        var cardDir = NewTempDir("cards");
        var missingCardDir = NewMissingDirPath("missing-cards");
        var missingLibDir = NewMissingDirPath("missing-lib");
        try
        {
            WriteCard(cardDir, "a1", CardJson("card-a1"));
            WriteCard(cardDir, "a2", CardJson("card-a2"));

            var report = CardDistributionValidator.Validate(new[] { cardDir, missingCardDir }, new[] { missingLibDir });

            Assert.Equal(2, report.Errors.Count);
            Assert.All(report.Errors, error =>
                Assert.Equal(CardDistributionValidationCategory.DirectoryMissing, error.Category));
            Assert.Equal(missingCardDir, report.Errors[0].File);
            Assert.Contains("卡目录不存在", report.Errors[0].Message);
            Assert.Equal(missingLibDir, report.Errors[1].File);
            Assert.Contains("效果库目录不存在", report.Errors[1].Message);
            Assert.Equal(2, report.SuccessfulCardCount); // 存在者照常（报告式全收集）。
            Assert.False(report.HasNoErrors);
            Assert.Equal(2, report.CardDirectoryCount); // 输入面统计含缺失目录。
            Assert.Equal(1, report.PrefabLibraryDirectoryCount);
        }
        finally
        {
            CleanUp(cardDir);
        }
    }

    [Fact]
    public void All_Input_Directories_Missing_Still_Produces_Report()
    {
        var missingA = NewMissingDirPath("missing-a");
        var missingB = NewMissingDirPath("missing-b");
        var missingLib = NewMissingDirPath("missing-lib");

        var report = CardDistributionValidator.Validate(new[] { missingA, missingB }, new[] { missingLib });

        // 非异常、非空——报告照常产出（各 Error 条目、其余维度空）。
        Assert.Equal(3, report.Errors.Count);
        Assert.All(report.Errors, error =>
            Assert.Equal(CardDistributionValidationCategory.DirectoryMissing, error.Category));
        Assert.Equal(0, report.SuccessfulCardCount);
        Assert.Equal(0, report.SuccessfulSnapshotCount);
        Assert.False(report.HasNoErrors);
    }

    [Fact]
    public void Empty_Directories_Are_Clean()
    {
        var cardDir = NewTempDir("cards");
        var libDir = NewTempDir("lib");
        try
        {
            var report = CardDistributionValidator.Validate(new[] { cardDir }, new[] { libDir });

            Assert.Empty(report.Errors);
            Assert.Empty(report.Warnings);
            Assert.Equal(0, report.SuccessfulCardCount);
            Assert.Equal(0, report.SuccessfulSnapshotCount);
            Assert.True(report.HasNoErrors);
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    [Fact]
    public void Only_Library_Input_Produces_Report_Without_Reference_Dimension()
    {
        var libDir = NewTempDir("lib");
        try
        {
            WriteLibraryComponent(libDir, "lib-x", LibraryComponentJson("lib-x"));

            var report = CardDistributionValidator.Validate(null, new[] { libDir });

            Assert.Empty(report.Errors);
            Assert.Empty(report.Warnings);
            Assert.Equal(0, report.SuccessfulCardCount);
            Assert.Equal(1, report.SuccessfulLibrarySnapshotCount);
            Assert.Equal(0, report.UnresolvedReferenceCount);
            Assert.True(report.HasNoErrors);
        }
        finally
        {
            CleanUp(libDir);
        }
    }

    [Fact]
    public void Null_Blank_And_All_Empty_Inputs_Throw()
    {
        // 全空输入＝参数异常（空报告会被误读为"校验通过"——假阴性）。
        Assert.Throws<ArgumentException>(() => CardDistributionValidator.Validate());
        Assert.Throws<ArgumentException>(() => CardDistributionValidator.Validate(null, null));
        Assert.Throws<ArgumentException>(() =>
            CardDistributionValidator.Validate(Array.Empty<string>(), Array.Empty<string>()));

        // null/空白元素＝参数层拒绝。
        Assert.Throws<ArgumentException>(() => CardDistributionValidator.Validate(new[] { "   " }, null));
        Assert.Throws<ArgumentException>(() => CardDistributionValidator.Validate(new[] { "" }, null));
        Assert.Throws<ArgumentException>(() => CardDistributionValidator.Validate(null, new string[] { null! }));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 报告契约（确定性、类别锁定、纯静态证明）与 outputs 全量冒烟。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Report_Is_Deterministic_For_Identical_Inputs()
    {
        var cardDirA = NewTempDir("cards-a");
        var cardDirB = NewTempDir("cards-b");
        var libDir = NewTempDir("lib");
        var missingDir = NewMissingDirPath("missing");
        try
        {
            WriteCard(cardDirA, "a1", CardJsonWithEffects("card-dup", prefabIds: new[] { "ghost" }));
            WriteCard(cardDirA, "bad", "{ not json");
            WriteCard(cardDirB, "b1", CardJson("card-dup"));
            WriteLibraryComponent(libDir, "lib-x", LibraryComponentJson("lib-x"));
            WriteLibraryComponent(libDir, "bad", "{ not json");

            var first = CardDistributionValidator.Validate(new[] { cardDirA, cardDirB, missingDir }, new[] { libDir });
            var second = CardDistributionValidator.Validate(new[] { cardDirA, cardDirB, missingDir }, new[] { libDir });

            Assert.Equal(first.CardDirectories, second.CardDirectories);
            Assert.Equal(first.PrefabLibraryDirectories, second.PrefabLibraryDirectories);
            Assert.Equal(first.SuccessfulCardCount, second.SuccessfulCardCount);
            Assert.Equal(first.SuccessfulLibrarySnapshotCount, second.SuccessfulLibrarySnapshotCount);
            Assert.Equal(first.SuccessfulInlineSnapshotCount, second.SuccessfulInlineSnapshotCount);
            Assert.Equal(first.UnresolvedReferenceCount, second.UnresolvedReferenceCount);

            // 条目序列全等（含顺序）——条目为 record、逐项值等。
            Assert.Equal(first.Errors, second.Errors);
            Assert.Equal(first.Warnings, second.Warnings);

            // 覆盖非空面（确认比较有效）。
            Assert.NotEmpty(first.Errors);
            Assert.NotEmpty(first.Warnings);
        }
        finally
        {
            CleanUp(cardDirA, cardDirB, libDir);
        }
    }

    [Fact]
    public void Category_Values_Are_Stable_Seven_Dimension_Faces()
    {
        // 类别字面值为稳定契约（后续不轻易变更）——测试锁定七个维度面。
        var names = Enum.GetNames<CardDistributionValidationCategory>();

        Assert.Equal(
            new[]
            {
                "DirectoryMissing",
                "CardSideImport",
                "LibrarySideImport",
                "CardIdDuplicate",
                "LibraryPrefabDuplicate",
                "CrossSourceConflict",
                "UnresolvedReference",
            },
            names);
    }

    [Fact]
    public void Validation_Does_Not_Modify_Input_Directories()
    {
        var cardDir = NewTempDir("cards");
        var libDir = NewTempDir("lib");
        try
        {
            WriteCard(cardDir, "a1", CardJsonWithEffects(
                "card-a1", prefabIds: new[] { "lib-x" }, inlineSnapshots: new[] { SnapshotJson("inline-a") }));
            WriteCard(cardDir, "bad", "{ not json");
            WriteLibraryComponent(libDir, "lib-x", LibraryComponentJson("lib-x"));

            // 纯只读证明：调用前后输入目录快照（文件清单＋内容）不变。
            var before = SnapshotDirectory(cardDir).Concat(SnapshotDirectory(libDir)).ToArray();
            var report = CardDistributionValidator.Validate(new[] { cardDir }, new[] { libDir });
            var after = SnapshotDirectory(cardDir).Concat(SnapshotDirectory(libDir)).ToArray();

            Assert.Equal(before, after);
            Assert.Equal(1, report.SuccessfulCardCount); // 预检确实做了工作（非空跑）。
            Assert.Single(report.Errors); // 坏卡隔离留痕。
        }
        finally
        {
            CleanUp(cardDir, libDir);
        }
    }

    [Fact]
    public void Official_Corpus_And_Keyword_Prefab_Library_Full_Validation_Is_Error_Free()
    {
        var kards = Path.Combine(FindRepoRoot(), "outputs", "kards-cards");
        var keywordPrefabs = Path.Combine(FindRepoRoot(), "src", "Orc.Game", "Cards", "KeywordPrefabs");

        var cardDirectories = new List<string>();
        var libraryDirectories = new List<string>();
        if (Directory.Exists(kards))
        {
            cardDirectories.Add(kards);
        }
        else
        {
            _output.WriteLine("outputs/kards-cards 缺失——跳过卡语料面（可辨识）。");
        }

        if (Directory.Exists(keywordPrefabs))
        {
            libraryDirectories.Add(keywordPrefabs);
        }
        else
        {
            _output.WriteLine("KeywordPrefabs 库缺失——跳过库面（可辨识）。");
        }

        if (cardDirectories.Count == 0 && libraryDirectories.Count == 0)
        {
            return; // 两面都缺失：跳过（可辨识——非本用例职责）。
        }

        var report = CardDistributionValidator.Validate(
            cardDirectories.Count > 0 ? cardDirectories : null,
            libraryDirectories.Count > 0 ? libraryDirectories : null);

        // 真实规模冒烟：官方 1646 张卡＋KeywordPrefabs 库（若存在）全量预检零问题。
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);

        if (cardDirectories.Count > 0)
        {
            Assert.Equal(1646, report.SuccessfulCardCount);
        }

        if (libraryDirectories.Count > 0)
        {
            // 该目录同时含旧 prefab 与 v4 组件（同一词条的两种承载）；计数是**条目数**，不按 id 去重。
            // 断言下界＝内建词条 v4 组件数（8）：v4-only 后它们才是生产承载，少一个就说明库面退化。
            Assert.True(
                report.SuccessfulLibrarySnapshotCount >= 8,
                $"库成功装载数 {report.SuccessfulLibrarySnapshotCount} 少于内建词条 v4 组件数 8（库面退化）。");
        }
    }
}
