using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// **R30／R31 抽象资产的装载期边界**（D28：两个层级的判定点是两件事）。
/// <para>
/// 资产级 <c>abstract</c>＝「不是可装配组件」：判定在**卡牌 components 的引用解析**处（装载期拒绝，
/// 错误必须说「模板不可实例化」）；抽象资产本身**仍可注册、可展开**（否则草案的父无从解析）。
/// 成员级 <c>abstract</c>＝「成员还没被实现」：装载链**不读**它的语义，只在产物里作为
/// **结构非法字段**拒绝（手工篡改）。
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
            "abstract": true,
            "requires-host": "unit",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onHook",
                "visibility": [ "mountable" ],
                "view": { "fields": [] },
                "abstract": true,
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

    [Fact]
    public void 抽象资产可注册可展开_成员级抽象与已实现成员共存()
    {
        // R30 第 4 条／R31：资产只有**被 components 引用**时才不可装配；它自己能被登记与展开。
        var asset = AssetSchema.Read(TemplateJson, "template");
        Assert.True(asset.Abstract);
        Assert.True(asset.AuthorizesModing); // D29：未声明 ⇒ 缺省推导 true（零诊断）

        var catalog = new PrefabCatalog().AddRange(KernelSignals.CreateCatalog().Definitions);
        catalog.Add(asset.ToStructuralPrefabDefinition());

        var resolved = catalog.Resolve(asset.Id);
        Assert.True(resolved.Abstract);
        Assert.True(resolved.FindMember("onHook")!.Abstract);
        Assert.False(resolved.FindMember("onFixed")!.Abstract);
    }

    [Fact]
    public void 抽象资产被卡牌components引用_装载期拒绝且文案指明模板不可实例化()
    {
        var asset = AssetSchema.Read(TemplateJson, "template");
        var catalog = new PrefabCatalog()
            .Add(asset.ToStructuralPrefabDefinition())
            .Add(new PrefabDefinition("card.test", EntityKind.Card, components: new[] { asset.Id }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("card.test"));
        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains("模板不可实例化", error.Message, StringComparison.Ordinal);
        Assert.Contains(asset.Id, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 抽象资产显式关闭授权_读取即拒绝且不宽容()
    {
        var json = TemplateJson.Replace(
            "\"abstract\": true,", "\"authorize-moding\": false,\n    \"abstract\": true,", StringComparison.Ordinal);

        var error = Assert.Throws<PrefabLoadException>(() => AssetSchema.Read(json, "template"));
        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains("显式矛盾", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 抽象资产显式打开授权_与缺省推导同值()
    {
        var json = TemplateJson.Replace(
            "\"abstract\": true,", "\"authorize-moding\": true,\n    \"abstract\": true,", StringComparison.Ordinal);

        var asset = AssetSchema.Read(json, "template");
        Assert.True(asset.Abstract);
        Assert.True(asset.AuthorizesModing);
    }

    [Fact]
    public void 产物成员带members_abstract_装载链按结构非法字段拒绝()
    {
        // R31 第 4 条：产物被手工塞入 abstract 字段 ⇒ 拒绝（不是「成员未实现」的语义判定）。
        var productJson = TemplateJson.Replace("\"abstract\": true,\n    \"requires-host\"", "\"requires-host\"", StringComparison.Ordinal);
        var directory = Path.Combine(Path.GetTempPath(), "orc-abstract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "demo.template.component.json"), productJson);

            var runtime = Runtime();
            var load = AssetPackLoader.Load(runtime, directory, evaluator: null);

            Assert.False(load.Loaded);
            Assert.Contains(load.Errors, e => e.Message.Contains("members[].abstract", StringComparison.Ordinal));
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
            .Replace("\"abstract\": true,", string.Empty, StringComparison.Ordinal)
            .Replace("demo.template", "template.mine", StringComparison.Ordinal)
            .Replace(",\n        \"abstract\": true,\n        \"guard\": \"self\"", "\n        \"handler\": { \"id\": \"h\", \"entry\": \"HandleAsync\", \"script\": \"x\" }", StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "orc-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "template.mine.component.json"), json);

            var runtime = Runtime();
            var load = AssetPackLoader.Load(runtime, directory, evaluator: null);

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
