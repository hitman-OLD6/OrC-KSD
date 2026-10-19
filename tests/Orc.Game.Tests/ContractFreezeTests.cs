using System.Text.Json.Nodes;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 契约冻结门（S17；唯一一份）：把当前代码的公共门面反射快照与
/// <c>tests/Orc.Game.Tests/Baselines/contract-freeze.json</c> 逐条比对，失败输出字段级 unified diff。
/// <para>
/// 生成基线只经 <c>ORC_WRITE_BASELINE=1</c> 的专用生成测试（<see cref="BaselineGeneratorTests"/>）；
/// 本测试在 <c>ORC_REGENERATE_BASELINE=1</c> 时只告警并跳过比对——**不静默重写**基线。
/// </para>
/// <para>
/// 唯一白名单（P4）：<c>GameEntryPoints</c> 恰好两条改名（<c>PlayCommandAsync</c>→<c>PlayOrderAsync</c>、
/// <c>BeginCommandPrePlayAsync</c>→<c>BeginOrderPrePlayAsync</c>），且仅 <c>Name</c>/<c>Signature</c> 两字段可 diff。
/// </para>
/// </summary>
public class ContractFreezeTests
{
    private static readonly string[] NonEntryPointSections =
    {
        "gameUpdates", "effectRuntime", "resultShapes", "schemaDto", "prefabJsonDtos",
    };

    [Fact]
    public void Contract_Baseline_Is_Unchanged()
    {
        if (Environment.GetEnvironmentVariable("ORC_REGENERATE_BASELINE") == "1")
        {
            // 该开关只用于"人工确认要跳过比对"的场合；必须判失败，否则 CI 误设变量会造成门禁假绿。
            Assert.Fail(
                "ORC_REGENERATE_BASELINE=1 —— 契约门被要求跳过比对，测试判失败以防假绿。" +
                "若确需重写基线，请用 ORC_WRITE_BASELINE=1 运行 BaselineGeneratorTests；" +
                "本测试永不重写基线文件（基线是受版本控制的资产）。");
        }

        var baselinePath = ContractSnapshot.BaselinePath;
        Assert.True(File.Exists(baselinePath), $"契约基线缺失：{baselinePath}（应由 ORC_WRITE_BASELINE=1 的生成测试产出）。");

        var baseline = JsonNode.Parse(File.ReadAllText(baselinePath))!.AsObject();
        var actual = ContractSnapshot.CaptureBody();

        // 基线必须带 P1 前 HEAD commit 与生成时间（S17 第 1 条）。
        var commit = baseline["commit"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(commit), "契约基线缺少 commit 头。");
        Assert.Matches("^[0-9a-f]{40}$", commit!);
        Assert.False(string.IsNullOrWhiteSpace(baseline["generatedAt"]?.GetValue<string>()), "契约基线缺少 generatedAt 头。");

        // 防止空基线「什么都不比」也通过。
        Assert.True(baseline["gameUpdates"]!.AsArray().Count >= 30, "契约基线 gameUpdates 面不完整。");
        Assert.True(baseline["effectRuntime"]!.AsArray().Count >= 20, "契约基线 effectRuntime 面不完整。");
        Assert.True(baseline["gameEntryPoints"]!.AsArray().Count >= 20, "契约基线 gameEntryPoints 面不完整。");

        var diff = ContractSnapshot.Diff(baseline, actual);
        if (diff.Count > 0 && IsOnlyP4Whitelist(baseline, actual))
        {
            return; // P4 白名单恰好命中：允许（本阶段 P1 不应出现）。
        }

        Assert.True(diff.Count == 0, "契约面发生未授权变更：\n" + string.Join("\n", diff));
    }

    /// <summary>P4 白名单：恰好两条入口改名，仅 Name/Signature 变化；其余面零 diff。</summary>
    private static bool IsOnlyP4Whitelist(JsonObject baseline, JsonObject actual)
    {
        foreach (var section in NonEntryPointSections)
        {
            if (!JsonNode.DeepEquals(baseline[section], actual[section]))
            {
                return false;
            }
        }

        if (baseline["gameEntryPoints"] is not JsonArray before || actual["gameEntryPoints"] is not JsonArray after)
        {
            return false;
        }

        if (before.Count != after.Count)
        {
            return false;
        }

        var changed = new List<int>();
        for (var i = 0; i < before.Count; i++)
        {
            if (!JsonNode.DeepEquals(before[i], after[i]))
            {
                changed.Add(i);
            }
        }

        if (changed.Count != 2)
        {
            return false; // 「恰好两条」，不是「至多两条」。
        }

        var pairs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PlayCommandAsync"] = "PlayOrderAsync",
            ["BeginCommandPrePlayAsync"] = "BeginOrderPrePlayAsync",
        };

        var hits = 0;
        foreach (var index in changed)
        {
            var oldEntry = before[index]!.AsObject();
            var newEntry = after[index]!.AsObject();
            var oldName = oldEntry["name"]!.GetValue<string>();
            var newName = newEntry["name"]!.GetValue<string>();
            if (!pairs.TryGetValue(oldName, out var expected) || !string.Equals(expected, newName, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var field in new[] { "category", "owner", "gate", "outcome", "status" })
            {
                if (!string.Equals(
                        oldEntry[field]?.GetValue<string>(), newEntry[field]?.GetValue<string>(), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            hits++;
        }

        return hits == 2;
    }
}

/// <summary>
/// 基线生成器（一次性资产生成；只在显式开关下写文件）。
/// CI/阶段门禁不设置 <c>ORC_WRITE_BASELINE</c>——生成动作只能由人主动触发。
/// </summary>
public class BaselineGeneratorTests
{
    [Fact]
    public void Write_Contract_Baseline_When_Requested()
    {
        if (Environment.GetEnvironmentVariable("ORC_WRITE_BASELINE") != "1")
        {
            return; // 常规测试运行：生成器不动作。
        }

        var path = ContractSnapshot.BaselinePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = ContractSnapshot.Capture(ContractSnapshot.CurrentCommit(), DateTimeOffset.Now);
        File.WriteAllText(path, ContractSnapshot.ToJson(document), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"契约基线已写入：{path}（commit={document["commit"]}）。");
    }
}
