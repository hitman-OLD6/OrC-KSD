using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// **R39 裁决 4／S28**：抽象性由「存在未填空位」推导——两个手写标志（<c>abstract</c>／
/// <c>authorize-moding</c>）已废止，读到即拒绝。
/// <para>
/// 判据是**一个**合并谓词：资产级 <c>slot ∧ ¬value</c> ∨ 成员级 <c>slot ∧ ¬handler</c>。
/// 判定点仍是**卡牌 components 的引用解析**；资产本身可注册、可展开（草案的父必须能解析）。
/// </para>
/// </summary>
public class AbstractAssetTests
{
    private const string TemplateJson = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "demo.template",
            "requires-host": "unit",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onHook",
                "visibility": [ "mountable" ],
                "view": { "fields": [] },
                "slot": { "type": "handler" },
                "guard": "self"
              },
              {
                "name": "onFixed",
                "visibility": [ "mountable" ],
                "view": { "fields": [] },
                "handler": { "id": "fixed", "entry": "HandleAsync", "script": "x" }
              }
            ],
            "mounts": [ { "target": "engine#unit.deployed", "member": "onHook" } ],
            "wraps": []
          }
        }
        """;

    /// <summary>最小运行时（内核目录；产品装载只需目录 ＋ 空 handler 表）。</summary>
    private static OrchestrationRuntime Runtime()
        => new(KernelSignals.CreateCatalog(), new HandlerRegistry());

    /// <summary>带一个宿主类型的运行时（`requires-host` 的存在性校验需要宿主预制体）。</summary>
    private static OrchestrationRuntime RuntimeWithHost()
        => new(
            KernelSignals.CreateCatalog().Add(
                new PrefabDefinition("card.host", EntityKind.Card, hosts: HostKind.Any)),
            new HandlerRegistry());

    [Fact]
    public void 模板可注册可展开_未填空位由slot与handler正交表达()
    {
        var asset = AssetSchema.Read(TemplateJson, "template");
        Assert.True(asset.HasUnfilledSlots);
        Assert.False(asset.Members.Single(m => m.Name == "onFixed").IsUnfilledSlot);

        var catalog = new PrefabCatalog().AddRange(KernelSignals.CreateCatalog().Definitions);
        catalog.Add(asset.ToStructuralPrefabDefinition());

        var resolved = catalog.Resolve(asset.Id);
        Assert.True(resolved.HasUnfilledSlots);
        Assert.True(resolved.FindMember("onHook")!.IsUnfilledSlot);
        Assert.False(resolved.FindMember("onFixed")!.IsUnfilledSlot);
    }

    [Fact]
    public void 未饱和资产被卡牌components引用_装载期拒绝且列出未填空位()
    {
        var asset = AssetSchema.Read(TemplateJson, "template");
        var catalog = new PrefabCatalog()
            .Add(asset.ToStructuralPrefabDefinition())
            .Add(new PrefabDefinition("card.test", EntityKind.Card, components: new[] { asset.Id }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("card.test"));
        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains("未饱和", error.Message, StringComparison.Ordinal);
        Assert.Contains(asset.Id, error.Message, StringComparison.Ordinal);
        Assert.Contains("onHook", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 只填一层仍判未饱和_半边填满不算具体资产()
    {
        // 成员层填了 handler，但资产级 damage 没有 value ⇒ 仍是模板。
        var json = TemplateJson
            .Replace(
                "\"private-fields\": [],",
                "\"private-fields\": [], \"slots\": [ { \"name\": \"damage\", \"type\": \"number\" } ],",
                StringComparison.Ordinal)
            .Replace(
                "\"slot\": { \"type\": \"handler\" },",
                "\"handler\": { \"id\": \"h\", \"entry\": \"HandleAsync\", \"script\": \"x\" },",
                StringComparison.Ordinal);

        var asset = AssetSchema.Read(json, "template");

        Assert.True(asset.HasUnfilledSlots);
        Assert.False(asset.Members.Single(m => m.Name == "onHook").IsUnfilledSlot);
        Assert.Single(asset.Slots, s => !s.IsFilled);
    }

    [Fact]
    public void 两层全填判为具体资产_可被components引用()
    {
        var json = TemplateJson
            .Replace(
                "\"private-fields\": [],",
                "\"private-fields\": [], \"slots\": [ { \"name\": \"damage\", \"type\": \"number\", \"value\": 3 } ],",
                StringComparison.Ordinal)
            .Replace(
                "\"slot\": { \"type\": \"handler\" },",
                "\"handler\": { \"id\": \"h\", \"entry\": \"HandleAsync\", \"script\": \"x\" },",
                StringComparison.Ordinal);

        var asset = AssetSchema.Read(json, "template");
        Assert.False(asset.HasUnfilledSlots);

        var catalog = new PrefabCatalog()
            .Add(asset.ToStructuralPrefabDefinition())
            .Add(new PrefabDefinition("card.test", EntityKind.Card, components: new[] { asset.Id }));

        var card = catalog.Resolve("card.test");
        Assert.Contains(asset.Id, card.Components);
    }

    [Fact]
    public void 资产级abstract字段已废止_读取即拒绝且报出资产id()
    {
        var json = TemplateJson.Replace(
            "\"requires-host\": \"unit\",",
            "\"abstract\": true,\n    \"requires-host\": \"unit\",",
            StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "template"));

        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains("demo.template", error.Message, StringComparison.Ordinal);
        Assert.Contains("abstract", error.Message, StringComparison.Ordinal);
        Assert.Contains("已废止", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void authorize_moding字段已废止_读取即拒绝()
    {
        var json = TemplateJson.Replace(
            "\"requires-host\": \"unit\",",
            "\"authorize-moding\": true,\n    \"requires-host\": \"unit\",",
            StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "template"));

        Assert.Contains("authorize-moding", error.Message, StringComparison.Ordinal);
        Assert.Contains("已废止", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 成员级abstract字段已废止_读取即拒绝并指出成员名()
    {
        var json = TemplateJson.Replace(
            "\"slot\": { \"type\": \"handler\" },",
            "\"abstract\": true,\n        \"slot\": { \"type\": \"handler\" },",
            StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "template"));

        Assert.Contains("onHook", error.Message, StringComparison.Ordinal);
        Assert.Contains("已废止", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 产物带未填空位_装载链拒绝()
    {
        // 产物被手工塞入一个 slot 但没有 handler ⇒ 未饱和，按 R31 修订后的产物洁净拒绝。
        var directory = Path.Combine(Path.GetTempPath(), "orc-slot-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "demo.template.component.json"), TemplateJson);

            var load = AssetPackLoader.Load(Runtime(), directory, evaluator: null);

            Assert.False(load.Loaded);
            Assert.Contains(load.Errors, e => e.Message.Contains("未填空位", StringComparison.Ordinal));
            Assert.Contains(load.Errors, e => e.Message.Contains("onHook", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 已填的资产级slot是合法字段_产物可装载()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orc-slot-filled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "demo.filled.component.json"),
                """
                {
                  "schemaVersion": 4,
                  "asset": {
                    "kind": "component",
                    "id": "demo.filled",
                    "requires-host": "any",
                    "active-when": "placed",
                    "one-shot": false,
                    "private-fields": [ { "name": "count", "type": "number", "initial": 1 } ],
                    "members": [],
                    "mounts": [],
                    "wraps": [],
                    "slots": [ { "name": "damage", "type": "number", "value": 5 } ]
                  }
                }
                """);

            var load = AssetPackLoader.Load(RuntimeWithHost(), directory, evaluator: null);

            Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => e.Message)));
            Assert.Equal(new[] { "demo.filled" }, load.AssetIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 普通资产使用template前缀_装载链拒绝()
    {
        // R34：template. 是资产 id 的保留前缀（与成员名的 framework. 前缀**不是一个判定函数**）。
        var json = TemplateJson
            .Replace("demo.template", "template.mine", StringComparison.Ordinal)
            .Replace(
                "\"slot\": { \"type\": \"handler\" },",
                "\"handler\": { \"id\": \"h\", \"entry\": \"HandleAsync\", \"script\": \"x\" },",
                StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "orc-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "template.mine.component.json"), json);

            var load = AssetPackLoader.Load(Runtime(), directory, evaluator: null);

            Assert.False(load.Loaded);
            Assert.Contains(load.Errors, e => e.Message.Contains("保留前缀", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 保留前缀两处落点_命名空间不混用()
    {
        // framework. 管成员名、template. 管资产 id：两个判定函数各判各的。
        Assert.True(ComponentDisposalPolicy.IsReservedMember(ComponentDisposalPolicy.ReservedPrefix + "disposal"));
        Assert.False(AssetSchema.IsTemplateId(ComponentDisposalPolicy.ReservedPrefix + "disposal"));
        Assert.True(AssetSchema.IsTemplateId("template.unit.deployed.self"));
        Assert.False(AssetSchema.IsTemplateId("demo.rally"));
        Assert.False(ComponentDisposalPolicy.IsReservedMember("template.unit.deployed.self"));
    }
}
