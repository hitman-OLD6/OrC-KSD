using Orc.Core.Model;
using Orc.Game.Model;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R39 裁决 4（S28）／R31 第 5 条**：饱和资产与普通组件同 schema、同发现规则、同目录，**无需新入口**；
/// 装载链按「存在未填空位」拒绝模板（产物洁净），但**不判定纯编排成员「是否已实现」**（那是编辑期概念）。
/// </summary>
public class AbstractAssetLoadTests
{
    private static readonly string SaturatedAsset = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "demo.param.combo",
            "requires-host": "any",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [],
            "mounts": [],
            "wraps": [],
            "slots": [ { "name": "damage", "type": "number", "value": 3 } ]
          }
        }
        """;

    [Fact]
    public void 饱和资产与普通组件同目录_同一套装载链读到且无需新入口()
    {
        var plain = SaturatedAsset.Replace("demo.param.combo", "demo.plain", StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "orc-samedir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "demo.param.combo.component.json"), SaturatedAsset);
            File.WriteAllText(Path.Combine(directory, "demo.plain.component.json"), plain);

            var load = AssetPackLoader.Load(GameEnginePrefabs.CreateRuntime(), directory, evaluator: null);

            Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => e.Message)));
            Assert.Equal(new[] { "demo.param.combo", "demo.plain" }, load.AssetIds);
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
        var json = SaturatedAsset
            .Replace("demo.param.combo", "demo.memberless", StringComparison.Ordinal)
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
