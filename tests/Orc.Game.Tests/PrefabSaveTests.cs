using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 效果快照落盘（批 2·写方向）：入口契约（SaveDirectory/SaveFile）、命名/覆盖/重复/只写不删、
/// 非法 id 双防线、报告分列（Succeeded/Failures 明细与输入序；无 Warnings 字段）、失败隔离与目录准备抛出、
/// 文本契约（2 空格缩进/LF/尾随换行/无 BOM/键序/不转义非 ASCII）与写入幂等。逐项可独立失败；临时目录（测后清理）。
/// </summary>
public class PrefabSaveTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // ① 入口契约：成功面（命名/报告/读回闭环）。
    // ─────────────────────────────────────────────────────────────────────────



    [Fact]
    public void SaveDirectory_Empty_Input_Is_Empty_Success_And_Does_Not_Prepare_Directory()
    {
        var directory = NewTempDir();
        try
        {
            var result = PrefabWriter.SaveDirectory(directory, Array.Empty<EffectSnapshot>());

            Assert.Empty(result.Succeeded);
            Assert.Empty(result.Failures);
            Assert.False(Directory.Exists(directory), "空输入＝不做目录准备（目标目录不存在时保持不存在）。");
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void SaveFile_Overwrites_Existing_File_Across_Runs()
    {
        var directory = NewTempDir();
        try
        {
            var first = MinimalSnapshot("e2e.save.cover", "样本·初版");
            var second = MinimalSnapshot("e2e.save.cover", "样本·再版");

            var firstResult = PrefabWriter.SaveFile(directory, first);
            Assert.Single(firstResult.Succeeded);
            Assert.Contains(
                "样本·初版", File.ReadAllText(firstResult.Succeeded[0].File), StringComparison.Ordinal);

            // 跨运行重存（同 id 同名文件）＝内容覆盖（只写不删——不清目录、不产生副本）。
            var secondResult = PrefabWriter.SaveFile(directory, second);
            Assert.Empty(secondResult.Failures);
            var text = File.ReadAllText(secondResult.Succeeded[0].File);
            Assert.Contains("样本·再版", text, StringComparison.Ordinal);
            Assert.DoesNotContain("样本·初版", text, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void SaveDirectory_Duplicate_Id_Keeps_First_And_Fails_Rest_With_Input_Order_Report()
    {
        var directory = NewTempDir();
        try
        {
            var first = MinimalSnapshot("e2e.save.dup", "样本·首见");
            var other = MinimalSnapshot("e2e.save.other");
            var duplicate = MinimalSnapshot("e2e.save.dup", "样本·重复");

            var result = PrefabWriter.SaveDirectory(directory, new[] { first, other, duplicate });

            Assert.Equal(
                new[] { "e2e.save.dup", "e2e.save.other" },
                result.Succeeded.Select(item => item.PrefabId));
            var failure = Assert.Single(result.Failures);
            Assert.Equal("e2e.save.dup", failure.PrefabId);
            Assert.Equal(Path.Combine(directory, "e2e.save.dup.prefab.json"), failure.File);
            Assert.Contains("重复", failure.Error, StringComparison.Ordinal);

            // 首见者内容落盘（重复者不覆盖）。
            Assert.Contains(
                "样本·首见",
                File.ReadAllText(Path.Combine(directory, "e2e.save.dup.prefab.json")),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ② 非法 id（完整沿用卡侧规则：单点校验＋全路径归化二道防线）。
    // ─────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> IllegalIdSamples => new()
    {
        "bad/id",
        "bad\\id",
        "..",
        ".",
        "../evil",
    };

    [Theory]
    [MemberData(nameof(IllegalIdSamples))]
    public void SaveDirectory_Rejects_Illegal_Ids(string illegalId)
    {
        var directory = NewTempDir();
        try
        {
            var result = PrefabWriter.SaveDirectory(directory, new[] { MinimalSnapshot(illegalId) });

            Assert.Empty(result.Succeeded);
            var failure = Assert.Single(result.Failures);
            Assert.Contains("非法", failure.Error, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory)); // 拒绝＝不写文件（不转义、不清理、不照写）
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void SaveDirectory_Rejects_Platform_Illegal_Filename_Characters()
    {
        var directory = NewTempDir();
        try
        {
            foreach (var character in new[] { ':', '*', '?', '"', '<', '>', '|' })
            {
                var illegalId = "bad" + character + "id";
                var result = PrefabWriter.SaveDirectory(directory, new[] { MinimalSnapshot(illegalId) });

                if (Path.GetInvalidFileNameChars().Contains(character))
                {
                    // 本平台非法集：必须拒绝（报告式）。
                    Assert.Empty(result.Succeeded);
                    Assert.Single(result.Failures);
                }
                else
                {
                    // 跨平台合法（如 Linux 的 ':'）：按常规文件接受（本断言随平台语义）。
                    Assert.Single(result.Succeeded);
                    Assert.Empty(result.Failures);
                }
            }
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void SaveFile_Reports_Illegal_Id_As_Failure_Without_Throwing()
    {
        var directory = NewTempDir();
        try
        {
            var result = PrefabWriter.SaveFile(directory, MinimalSnapshot("bad\\id"));

            Assert.Empty(result.Succeeded);
            var failure = Assert.Single(result.Failures);
            Assert.Equal("bad\\id", failure.PrefabId);
            Assert.Contains("非法", failure.Error, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory)); // 目录已准备但零文件
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ③ 参数/元素非法分层（参数＝抛出；快照 id 非法＝报告式——分层口径）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_Entry_Points_Throw_On_Null_Or_Blank_Directory_And_Null_Snapshot()
    {
        var directory = NewTempDir();
        try
        {
            var snapshot = MinimalSnapshot("e2e.save.args");

            Assert.Throws<ArgumentNullException>(() => PrefabWriter.SaveDirectory(null!, new[] { snapshot }));
            Assert.Throws<ArgumentException>(() => PrefabWriter.SaveDirectory(" ", new[] { snapshot }));
            Assert.Throws<ArgumentNullException>(() => PrefabWriter.SaveDirectory(directory, null!));

            Assert.Throws<ArgumentNullException>(() => PrefabWriter.SaveFile(null!, snapshot));
            Assert.Throws<ArgumentException>(() => PrefabWriter.SaveFile(" \t", snapshot));
            Assert.Throws<ArgumentNullException>(() => PrefabWriter.SaveFile(directory, null!));
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void SaveDirectory_Throws_On_Null_Collection_And_Null_Elements()
    {
        var directory = NewTempDir();
        try
        {
            Assert.Throws<ArgumentNullException>(() => PrefabWriter.SaveDirectory(directory, null!));
            Assert.Throws<ArgumentNullException>(() => PrefabWriter.SaveDirectory(
                directory, new EffectSnapshot[] { MinimalSnapshot("e2e.save.a"), null! }));
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void Save_Directory_Preparation_Failure_Throws_For_Both_Entry_Points()
    {
        var directory = NewTempDir();
        try
        {
            Directory.CreateDirectory(directory);
            var blocker = Path.Combine(directory, "blocker");
            File.WriteAllText(blocker, "x");

            // 目标目录路径由「文件」占位：目录准备必然失败——整体性失败（抛出，不依赖单条内容）。
            Assert.ThrowsAny<IOException>(() => PrefabWriter.SaveDirectory(
                blocker, new[] { MinimalSnapshot("e2e.save.blocked") }));
            Assert.ThrowsAny<IOException>(() => PrefabWriter.SaveFile(
                blocker, MinimalSnapshot("e2e.save.blocked")));
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ④ 只写不删 / 部分失败不回滚 / 单条 IO 隔离。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveDirectory_Does_Not_Delete_Unrelated_Files_Old_Snapshots_Or_Subdirectories()
    {
        var directory = NewTempDir();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(directory, "old-stale.prefab.json"), "{}");
            var subDirectory = Path.Combine(directory, "sub");
            Directory.CreateDirectory(subDirectory);
            File.WriteAllText(Path.Combine(subDirectory, "nested.txt"), "nested");

            var result = PrefabWriter.SaveDirectory(directory, new[] { MinimalSnapshot("e2e.save.fresh") });

            Assert.Single(result.Succeeded);
            Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(directory, "old-stale.prefab.json")));
            Assert.True(File.Exists(Path.Combine(subDirectory, "nested.txt")));
            Assert.True(File.Exists(Path.Combine(directory, "e2e.save.fresh.prefab.json")));
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void SaveDirectory_Single_Snapshot_Io_Failure_Is_Isolated_And_Partial_Writes_Remain()
    {
        var directory = NewTempDir();
        try
        {
            Directory.CreateDirectory(directory);
            // 单条 IO 失败：同名位置由「目录」占位（写入必然失败）。
            Directory.CreateDirectory(Path.Combine(directory, "e2e.save.blocked.prefab.json"));

            var blocked = MinimalSnapshot("e2e.save.blocked");
            var fine = MinimalSnapshot("e2e.save.fine");
            var illegal = MinimalSnapshot("bad/id");

            var result = PrefabWriter.SaveDirectory(directory, new[] { blocked, fine, illegal });

            // 隔离：仅 fine 成功；两条失败各留痕（IO ＋ 非法 id）；部分失败不回滚（已写保留）。
            var success = Assert.Single(result.Succeeded);
            Assert.Equal("e2e.save.fine", success.PrefabId);
            Assert.Equal(2, result.Failures.Count);
            Assert.Equal("e2e.save.blocked", result.Failures[0].PrefabId);
            Assert.Contains("写入失败", result.Failures[0].Error, StringComparison.Ordinal);
            Assert.Equal("bad/id", result.Failures[1].PrefabId);
            Assert.True(File.Exists(Path.Combine(directory, "e2e.save.fine.prefab.json")));
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ⑤ 文本契约（落盘出口：2 空格缩进、LF、尾随换行、无 BOM、键序确定、不转义非 ASCII）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Saved_Files_Follow_Text_Contract()
    {
        var directory = NewTempDir();
        try
        {
            var result = PrefabWriter.SaveDirectory(directory, new[] { RichSnapshot("e2e.save.text") });
            var text = File.ReadAllText(result.Succeeded[0].File);
            var bytes = File.ReadAllBytes(result.Succeeded[0].File);

            // UTF-8 无 BOM。
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "落盘文件不应含 UTF-8 BOM。");

            // LF：无 \r；尾随单个 \n。
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);
            Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal), "尾随换行应为单个 \\n。");

            // 2 空格缩进多行。
            Assert.Contains("{\n  \"schemaVersion\": 3,\n  \"root\": {", text, StringComparison.Ordinal);
            Assert.Contains("\n    \"mainTrigger\": {", text, StringComparison.Ordinal);
            Assert.Contains("\n      \"hooks\": [", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\n\t", text);

            // 键序确定（DTO 声明序）：顶层与各层键序锁定。
            AssertOrdered(text, "\"schemaVersion\"", "\"root\"");
            AssertOrdered(
                text,
                "\n    \"id\"", "\n    \"version\"", "\n    \"mainTrigger\"",
                "\n    \"otherTriggers\"", "\n    \"wrappers\"", "\n    \"injects\"");
            AssertOrdered(
                text,
                "\n      \"id\"", "\n      \"stableKey\"", "\n      \"kind\"", "\n      \"viewType\"",
                "\n      \"hooks\"", "\n      \"events\"", "\n      \"version\"");

            // 不转义非 ASCII（宽松文本——对齐既有样本实态与卡侧「不转义非 ASCII」口径）。
            Assert.Contains("样本·主", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public void Same_Input_Written_Twice_Is_Byte_Identical()
    {
        var firstDirectory = NewTempDir();
        var secondDirectory = NewTempDir();
        try
        {
            var snapshots = new[] { MinimalSnapshot("e2e.save.det.a"), RichSnapshot("e2e.save.det.b") };

            var first = PrefabWriter.SaveDirectory(firstDirectory, snapshots);
            var second = PrefabWriter.SaveDirectory(secondDirectory, snapshots);

            Assert.Equal(
                first.Succeeded.Select(item => item.PrefabId),
                second.Succeeded.Select(item => item.PrefabId));
            foreach (var item in first.Succeeded)
            {
                var counterpart = second.Succeeded.Single(entry => entry.PrefabId == item.PrefabId);
                Assert.Equal(File.ReadAllBytes(item.File), File.ReadAllBytes(counterpart.File));
            }
        }
        finally
        {
            DeleteTempDir(firstDirectory);
            DeleteTempDir(secondDirectory);
        }
    }

    [Fact]
    public void Write_Read_Write_Is_Byte_Idempotent()
    {
        var firstDirectory = NewTempDir();
        var secondDirectory = NewTempDir();
        try
        {
            var snapshots = new[] { RichSnapshot("e2e.save.idem"), CsxSnapshot("e2e.save.idem.csx") };

            var first = PrefabWriter.SaveDirectory(firstDirectory, snapshots);
            Assert.Empty(first.Failures);

            // 读回（经文件系统反序列化）再写出。
            var readBack = new List<EffectSnapshot>();
            foreach (var item in first.Succeeded)
            {
                readBack.Add(PrefabJson.Deserialize(File.ReadAllText(item.File)));
            }

            var second = PrefabWriter.SaveDirectory(secondDirectory, readBack);
            Assert.Empty(second.Failures);

            foreach (var item in first.Succeeded)
            {
                var counterpart = second.Succeeded.Single(entry => entry.PrefabId == item.PrefabId);
                Assert.Equal(File.ReadAllBytes(item.File), File.ReadAllBytes(counterpart.File));
            }
        }
        finally
        {
            DeleteTempDir(firstDirectory);
            DeleteTempDir(secondDirectory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ⑥ 报告类型形状（无 Warnings 字段——效果侧写方向警告面不存在）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PrefabSave_Report_Types_Expose_Only_Succeeded_And_Failures_Columns()
    {
        Assert.Equal(
            new[] { "Failures", "Succeeded" },
            typeof(PrefabSaveResult).GetProperties()
                .Select(item => item.Name).OrderBy(item => item, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "File", "PrefabId" },
            typeof(PrefabSaveSuccess).GetProperties()
                .Select(item => item.Name).OrderBy(item => item, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "Error", "File", "PrefabId" },
            typeof(PrefabSaveFailure).GetProperties()
                .Select(item => item.Name).OrderBy(item => item, StringComparer.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助。
    // ─────────────────────────────────────────────────────────────────────────

    private const string PlaceholderCsx =
        "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;\n";

    private static EffectSnapshot MinimalSnapshot(string id, string stableKey = "样本·最小")
        => new(new EffectPrefab(
            id,
            new TriggerPrefab(
                "t.main",
                stableKey,
                true,
                hooks: new[] { "unit.deployed" },
                events: new[] { new EventPrefab("e1", assemblyKey: id) })));

    private static EffectSnapshot CsxSnapshot(string id)
        => new(new EffectPrefab(
            id,
            new TriggerPrefab(
                "t.main",
                "样本·csx",
                true,
                hooks: new[] { "unit.deployed" },
                events: new[] { new EventPrefab("e1", csxSource: PlaceholderCsx) })));

    private static EffectSnapshot RichSnapshot(string id)
        => new(new EffectPrefab(
            id,
            new TriggerPrefab(
                "t.main",
                "样本·主",
                true,
                hooks: new[] { "unit.deployed" },
                events: new[]
                {
                    new EventPrefab("e1", assemblyKey: id, downstream: new[] { "t.other" }),
                }),
            otherTriggers: new[]
            {
                new TriggerPrefab(
                    "t.other",
                    "样本·其它",
                    true,
                    hooks: new[] { "turn.start" },
                    events: new[] { new EventPrefab("e2", csxSource: PlaceholderCsx) }),
            },
            wrappers: new[]
            {
                new WrapperPrefab("replace", "e1", null, new EventPrefab("m1", assemblyKey: id + ".moding")),
            },
            injects: new[]
            {
                new InjectPrefab("DeployKeyword", null, "e1", 3),
            }));

    private static void AssertOrdered(string text, params string[] tokens)
    {
        var lastIndex = -1;
        foreach (var token in tokens)
        {
            var index = text.IndexOf(token, StringComparison.Ordinal);
            Assert.True(index >= 0, $"文本契约：缺少标记 {token}。");
            Assert.True(index > lastIndex, $"文本契约：标记序不符（{token} 未出现在前序标记之后）。");
            lastIndex = index;
        }
    }

    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "orc-prefabsave-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempDir(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
