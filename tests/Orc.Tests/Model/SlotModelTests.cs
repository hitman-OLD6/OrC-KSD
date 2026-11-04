using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// **S27／R39 裁决 3／8**：空位的六种类型、两层作用域（资产级五种／成员级 handler）、
/// 跨层键唯一、enum 取值集与 value 的装载期校验、schema 的扁平合并，以及「args 没有结构通道」。
/// </summary>
public class SlotModelTests
{
    private const string MinimalAsset = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "test.slot",
            "requires-host": "any",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [ { "name": "onHook", "visibility": [ "callable" ], "view": { "fields": [] },
                           "slot": { "type": "handler" }, "handler": { "id": "h#onHook", "entry": "HandleAsync",
                           "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;" } } ],
            "mounts": [],
            "wraps": [],
            "slots": [
              { "name": "damage", "type": "number", "value": 3 },
              { "name": "element", "type": "enum", "values": [ "fire", "ice" ] },
              { "name": "target", "type": "asset-ref", "value": "test.slot" }
            ]
          }
        }
        """;

    [Fact]
    public void 空位类型只有六种_出现第七种即拒绝()
    {
        var json = MinimalAsset.Replace("\"type\": \"number\"", "\"type\": \"list\"", StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "x"));

        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains("六种", error.Message, StringComparison.Ordinal);
        Assert.Contains("第七种", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 资产级只接受五种_成员级只接受handler()
    {
        var assetLevelHandler = MinimalAsset.Replace(
            "\"name\": \"damage\", \"type\": \"number\"", "\"name\": \"damage\", \"type\": \"handler\"", StringComparison.Ordinal);
        var assetError = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(assetLevelHandler, "x"));
        Assert.Contains("handler 只能挂在成员级", assetError.Message, StringComparison.Ordinal);

        var memberScalar = MinimalAsset.Replace(
            "\"slot\": { \"type\": \"handler\" }", "\"slot\": { \"type\": \"number\" }", StringComparison.Ordinal);
        var memberError = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(memberScalar, "x"));
        Assert.Contains("成员级空位只接受 handler", memberError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 跨层重名_资产级空位与成员名相同即拒绝()
    {
        var json = MinimalAsset.Replace("\"name\": \"damage\"", "\"name\": \"onHook\"", StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "x"));

        Assert.Contains("跨层不得重名", error.Message, StringComparison.Ordinal);
        Assert.Contains("onHook", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void enum的value必须在取值集内_装载期拒绝()
    {
        var json = MinimalAsset.Replace(
            "{ \"name\": \"element\", \"type\": \"enum\", \"values\": [ \"fire\", \"ice\" ] }",
            "{ \"name\": \"element\", \"type\": \"enum\", \"values\": [ \"fire\", \"ice\" ], \"value\": \"wind\" }",
            StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "x"));

        Assert.Contains("不在取值集", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void enum必须带非空且唯一的取值集()
    {
        var missing = MinimalAsset.Replace(
            "\"type\": \"enum\", \"values\": [ \"fire\", \"ice\" ]", "\"type\": \"enum\"", StringComparison.Ordinal);
        Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(missing, "x"));

        var duplicated = MinimalAsset.Replace(
            "[ \"fire\", \"ice\" ]", "[ \"fire\", \"fire\" ]", StringComparison.Ordinal);
        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(duplicated, "x"));
        Assert.Contains("重复值", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 非enum类型不能声明取值集()
    {
        var json = MinimalAsset.Replace(
            "{ \"name\": \"damage\", \"type\": \"number\", \"value\": 3 }",
            "{ \"name\": \"damage\", \"type\": \"number\", \"value\": 3, \"values\": [ \"a\" ] }",
            StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "x"));

        Assert.Contains("只有 enum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 空位字段往返_读写出同一文本()
    {
        var first = AssetSchema.Write(AssetSchema.Read(MinimalAsset, "x"));
        var second = AssetSchema.Write(AssetSchema.Read(first, "x"));

        Assert.Equal(first, second);
        Assert.Contains("\"slots\"", first, StringComparison.Ordinal);
        Assert.Contains("\"slot\": {", first, StringComparison.Ordinal);
    }

    [Fact]
    public void schema由两层合并成扁平表_资产级在前成员级在后()
    {
        var asset = AssetSchema.Read(MinimalAsset, "x");

        var schema = SlotSchema.SchemaOf(asset);

        Assert.Equal(new[] { "damage", "element", "target", "onHook" }, schema.Select(s => s.Name));
        Assert.Equal(
            new[] { SlotScope.Asset, SlotScope.Asset, SlotScope.Asset, SlotScope.Member },
            schema.Select(s => s.Scope));
        Assert.Equal(
            new[] { SlotType.Number, SlotType.Enum, SlotType.AssetRef, SlotType.Handler },
            schema.Select(s => s.Type));

        // 已填／未填只看「有没有」（D46）：damage/target/onHook 有值，element 没有。
        Assert.Equal(new[] { true, false, true, true }, schema.Select(s => s.HasValue));
    }

    [Fact]
    public void asset_ref条目标记为无运行期效果_其余五类不是()
    {
        // R39 裁决 15／D49：asset-ref 只作装载期存在性校验与编辑器可选列表，schema 必须标出「不影响行为」。
        var asset = AssetSchema.Read(MinimalAsset, "x");
        var schema = SlotSchema.SchemaOf(asset);

        Assert.False(Assert.Single(schema, e => e.Name == "target").RuntimeEffect);
        Assert.All(
            schema.Where(e => e.Name != "target"),
            e => Assert.True(e.RuntimeEffect, $"{e.Name}（{SlotTypes.Format(e.Type)}）应标记为有运行期效果"));
    }

    [Fact]
    public void 六个类型名封闭_解析不模糊匹配()
    {
        Assert.Equal(6, SlotTypes.All.Count);
        Assert.Equal(5, SlotTypes.AssetLevel.Count);
        Assert.True(SlotTypes.TryParse("asset-ref", out var type));
        Assert.Equal(SlotType.AssetRef, type);
        Assert.False(SlotTypes.TryParse("AssetRef", out _));
        Assert.True(SlotTypes.TryParse("number ", out _)); // 首尾空白允许
        Assert.False(SlotTypes.TryParse("array", out _));
    }

    [Fact]
    public void args不接受结构值_数组与嵌套对象一律拒绝()
    {
        var array = Assert.Throws<PrefabLoadException>(() => SlotSchema.ReadArguments("""{"damage":[1,2]}"""));
        Assert.Contains("不接受结构", array.Message, StringComparison.Ordinal);

        var nested = Assert.Throws<PrefabLoadException>(() => SlotSchema.ReadArguments("""{"onHook":{"members":[]}}"""));
        Assert.Contains("kind", nested.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void handler值只接受三种kind_第四种拒绝()
    {
        var value = SlotSchema.ReadArguments("""{"onHook":{"kind":"csx","text":"x"}}""");
        var handler = Assert.IsType<HandlerSlotArgument>(value["onHook"]);
        Assert.Equal(HandlerSlotKind.Csx, handler.Kind);

        var fourth = Assert.Throws<PrefabLoadException>(
            () => SlotSchema.ReadArguments("""{"onHook":{"kind":"inline-dsl","text":"x"}}"""));
        Assert.Contains("三选一", fourth.Message, StringComparison.Ordinal);

        var missing = Assert.Throws<PrefabLoadException>(
            () => SlotSchema.ReadArguments("""{"onHook":{"kind":"dsl"}}"""));
        Assert.Contains("缺少对应的值字段", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 资产的空位不去重_重名即拒绝()
    {
        var json = MinimalAsset.Replace(
            "{ \"name\": \"element\"", "{ \"name\": \"damage\"", StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "x"));

        Assert.Contains("重复声明资产级空位", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有空位的存量资产_产物文本不出现slots与slot()
    {
        var legacy = """
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "test.legacy",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [],
                "members": [ { "name": "onHook", "visibility": [ "callable" ], "view": { "fields": [] } } ],
                "mounts": [],
                "wraps": []
              }
            }
            """;

        var text = AssetSchema.Write(AssetSchema.Read(legacy, "x"));

        Assert.DoesNotContain("\"slots\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"slot\"", text, StringComparison.Ordinal);
    }
}
