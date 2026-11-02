using Orc.Core.Model;
using Orc.Game.Model;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R30 第 4 条／R31 第 5 条**：模板与普通组件同 schema、同发现规则、同目录，**无需新入口**；
/// 装载链只按 R30 拒绝「抽象资产被引用」，**不判定成员是否已实现**（D28：那是编辑期概念）。
/// </summary>
public class AbstractAssetLoadTests
{
    private static readonly string AbstractAsset = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "demo.abstract.combo",
            "abstract": true,
            "requires-host": "any",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [],
            "mounts": [],
            "wraps": []
          }
        }
        """;

    [Fact]
    public void 模板与普通组件同目录_同一套装载链读到且无需新入口()
    {
        var plain = AbstractAsset
            .Replace("\"abstract\": true,", string.Empty, StringComparison.Ordinal)
            .Replace("demo.abstract.combo", "demo.plain", StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "orc-samedir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "demo.abstract.combo.component.json"), AbstractAsset);
            File.WriteAllText(Path.Combine(directory, "demo.plain.component.json"), plain);

            var load = AssetPackLoader.Load(GameEnginePrefabs.CreateRuntime(), directory, evaluator: null);

            Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => e.Message)));
            Assert.Equal(new[] { "demo.abstract.combo", "demo.plain" }, load.AssetIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 装载链不判定成员是否已实现_无handler的成员照常装载()
    {
        // 成员没有 handler 也没有 abstract：装载链把它当「纯编排成员」，不做任何「未实现」判定（R31 第 5 条）。
        var json = AbstractAsset
            .Replace("\"abstract\": true,", string.Empty, StringComparison.Ordinal)
            .Replace("demo.abstract.combo", "demo.memberless", StringComparison.Ordinal)
            .Replace(
                "\"members\": [],",
                "\"members\": [ { \"name\": \"onHook\", \"visibility\": [ \"mountable\" ], \"view\": { \"fields\": [] } } ],",
                StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "orc-memberless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "demo.memberless.component.json"), json);

            var load = AssetPackLoader.Load(GameEnginePrefabs.CreateRuntime(), directory, evaluator: null);

            Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => e.Message)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
