using Orc.Core;
using Orc.Core.Model;
using Orc.Packages.Editing;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S35 的摊平面**：摊平走同一个 <see cref="PrefabExpander.Expand"/>，因此产物同样注入四类标量空位；
/// 二次摊平（父产物已带注入字段）必须**替换**而不是再追加一条。
/// </summary>
public class SlotInjectionFlattenTests : IDisposable
{
    private const string Script =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;";

    private readonly string _root;
    private readonly string _pack;

    public SlotInjectionFlattenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-slot-flatten-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));
    }

    [Fact]
    public void 摊平产物注入私有字段_二次摊平替换初值而不是追加()
    {
        Write("effects-v4/tpl.param.component.json", Parent);
        Write("modings/demo.first.draft.moding.json", FirstDraft);

        var first = ModingFlattener.FlattenAndWrite(_pack, "modings/demo.first.draft.moding.json");
        Assert.True(first.Success, first.Describe());

        var product = AssetSchema.Read(first.ProductJson!, "first");
        var injected = Assert.Single(product.PrivateFields, f => f.Name == "damage");
        Assert.Equal(ViewFieldType.Number, injected.Type);
        Assert.Equal(9m, injected.InitialValue);
        Assert.Equal(9m, Assert.Single(product.Slots, s => s.Name == "damage").Value);

        // 二次摊平：父产物已带注入字段（damage=9）；草案把 slot 再填成 11。
        Write("modings/demo.second.draft.moding.json", SecondDraft);
        var second = ModingFlattener.Flatten(_pack, "modings/demo.second.draft.moding.json");
        Assert.True(second.Success, second.Describe());

        var secondProduct = AssetSchema.Read(second.ProductJson!, "second");
        var replaced = Assert.Single(secondProduct.PrivateFields, f => f.Name == "damage");
        Assert.Equal(11m, replaced.InitialValue);
        Assert.Equal(11m, Assert.Single(secondProduct.Slots, s => s.Name == "damage").Value);

        // 不出现两份同名私有字段：`Assert.Single` 已经钉住「每 slot 一条」，且父产物的旧值被替换。
    }

    [Fact]
    public void asset_ref与handler不注入私有字段()
    {
        Write(
            "effects-v4/tpl.ref.component.json",
            """
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "tpl.ref",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [ { "name": "keep", "type": "number", "initial": 1 } ],
                "members": [
                  {
                    "name": "onHook",
                    "visibility": [ "callable" ],
                    "view": { "fields": [] },
                    "slot": { "type": "handler" }
                  }
                ],
                "mounts": [],
                "wraps": [],
                "slots": [ { "name": "target", "type": "asset-ref", "value": "tpl.ref" } ]
              }
            }
            """);
        Write(
            "modings/ref.draft.moding.json",
            $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "moding",
                "id": "ref.draft",
                "product": { "id": "ref.product" },
                "extends": "tpl.ref",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [],
                "members": [
                  {
                    "name": "onHook",
                    "visibility": [ "callable" ],
                    "view": { "fields": [] },
                    "handler": {
                      "id": "ref.draft#onHook",
                      "entry": "HandleAsync",
                      "script": "{{Script}}"
                    }
                  }
                ],
                "mounts": [],
                "wraps": []
              }
            }
            """);

        var result = ModingFlattener.Flatten(_pack, "modings/ref.draft.moding.json");
        Assert.True(result.Success, result.Describe());

        var product = AssetSchema.Read(result.ProductJson!, "product");
        Assert.Equal(new[] { "keep" }, product.PrivateFields.Select(f => f.Name));
        Assert.DoesNotContain("target", product.PrivateFields.Select(f => f.Name), StringComparer.Ordinal);
        Assert.DoesNotContain("onHook", product.PrivateFields.Select(f => f.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void 子引入slot撞父的手写私有字段_拒绝()
    {
        Write(
            "effects-v4/tpl.hand.component.json",
            """
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "tpl.hand",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [ { "name": "damage", "type": "number", "initial": 2 } ],
                "members": [
                  {
                    "name": "onHook",
                    "visibility": [ "callable" ],
                    "view": { "fields": [] },
                    "slot": { "type": "handler" }
                  }
                ],
                "mounts": [],
                "wraps": []
              }
            }
            """);
        Write(
            "modings/hand.draft.moding.json",
            $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "moding",
                "id": "hand.draft",
                "product": { "id": "hand.product" },
                "extends": "tpl.hand",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [],
                "members": [
                  {
                    "name": "onHook",
                    "visibility": [ "callable" ],
                    "view": { "fields": [] },
                    "handler": {
                      "id": "hand.draft#onHook",
                      "entry": "HandleAsync",
                      "script": "{{Script}}"
                    }
                  }
                ],
                "mounts": [],
                "wraps": [],
                "slots": [ { "name": "damage", "type": "number", "value": 5 } ]
              }
            }
            """);

        var result = ModingFlattener.Flatten(_pack, "modings/hand.draft.moding.json");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("冲突", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Message.Contains("damage", StringComparison.Ordinal));
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static readonly string Parent = $$"""
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "tpl.param",
            "requires-host": "any",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [ { "name": "seen", "type": "number", "initial": 0 } ],
            "members": [
              {
                "name": "onHook",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "slot": { "type": "handler" }
              }
            ],
            "mounts": [],
            "wraps": [],
            "slots": [ { "name": "damage", "type": "number", "value": 3 } ]
          }
        }
        """;

    private static readonly string FirstDraft = $$"""
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "demo.first.draft",
            "product": { "id": "demo.first" },
            "extends": "tpl.param",
            "requires-host": "any",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onHook",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "demo.first.draft#onHook",
                  "entry": "HandleAsync",
                  "script": "{{Script}}"
                }
              }
            ],
            "mounts": [],
            "wraps": [],
            "slots": [ { "name": "damage", "type": "number", "value": 9 } ]
          }
        }
        """;

    private static readonly string SecondDraft = $$"""
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "demo.second.draft",
            "product": { "id": "demo.second" },
            "extends": "demo.first",
            "requires-host": "any",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [],
            "mounts": [],
            "wraps": [],
            "slots": [ { "name": "damage", "type": "number", "value": 11 } ]
          }
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
