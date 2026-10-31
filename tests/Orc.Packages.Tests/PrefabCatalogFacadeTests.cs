using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Effects;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S10 入口二**（R5／R25／D13）：<c>Orc.Game</c> 侧的节点目录门面。
/// <para>
/// 这里落在 <c>Orc.Packages.Tests</c> 是为了复用已装配的 `Orc.Game`（本工程本来就引用它）——
/// **门面本身在 <c>Orc.Game</c> 侧**，`Orc.Packages` 没有新增反向依赖（D13 的分层约束）。
/// </para>
/// </summary>
public class PrefabCatalogFacadeTests
{
    [Fact]
    public void revision与全量目录同源_且带扫描的门面类型清单()
    {
        var facade = new HandlerCatalogFacade();

        var revision = facade.CatalogRevision();
        var catalog = facade.GetCatalog();

        // 同一门面、同一个 catalogRevision（D13）：轻查询与全量不会各说各话。
        Assert.Equal(revision, catalog.CatalogRevision);
        Assert.Same(catalog, facade.GetCatalog()); // 投影一次并缓存

        Assert.Contains("Orc.Game.Effects.EffectRuntime", catalog.ScannedFaces);
        Assert.Contains("Orc.Game.Cards.KeywordEffectActionFace", catalog.ScannedFaces);

        // 合成节点也在目录里（SPEC §12.9：它们不是动作面方法，但必须是合法的 op）。
        var stop = Assert.Single(catalog.Nodes, n => n.Name == "stop");
        Assert.Null(stop.FaceType);
        Assert.True(stop.HasSideEffect);
        Assert.Empty(stop.Inputs);
        Assert.Empty(stop.Outputs);

        // 引脚类型只取封闭集（R16）。
        var closed = new[] { "entity", "number", "string", "boolean", "list<entity>", "list<number>", "list<string>", "list<boolean>" };
        Assert.All(
            catalog.Nodes.SelectMany(n => n.Inputs.Concat(n.Outputs)),
            pin => Assert.Contains(pin.Type, closed));
    }

    [Fact]
    public void 全量目录已含标注展开结果_前端无需自行展开()
    {
        // 构造一份带「标注展开结果」的目录快照：枚举取值集、实体子类型集、多输出拆解。
        var node = new DslNodeSignature(
            "probe",
            inputs: new[]
            {
                new DslPin("target", PinType.Entity.WithSubtypes(["unit"]), required: true),
                new DslPin("mode", PinType.String, required: true, enumValues: ["a", "b"], enumType: "ProbeMode"),
            },
            outputs: new[]
            {
                new DslPin("status", PinType.String, enumType: DslPin.EnumOutputMarker),
                new DslPin("count", PinType.Number),
            });
        var snapshot = new DslCatalog(revision: 7, nodes: new[] { node }, scannedFaces: ["ProbeFace"]);

        var facade = new HandlerCatalogFacade(new HandlerDslEnvironment(new HandlerDslCompiler(), snapshot));
        var catalog = facade.GetCatalog();

        Assert.Equal(7, facade.CatalogRevision());
        Assert.Equal(7, catalog.CatalogRevision);

        var probe = Assert.Single(catalog.Nodes, n => n.Name == "probe");
        Assert.Equal(new[] { "unit" }, probe.Inputs.Single(p => p.Name == "target").Subtypes);
        Assert.Equal(new[] { "a", "b" }, probe.Inputs.Single(p => p.Name == "mode").EnumValues);
        Assert.Equal("ProbeMode", probe.Inputs.Single(p => p.Name == "mode").EnumType);

        // 多输出：两个输出引脚都在（前端据此要求 bind 用对象形态）。
        Assert.Equal(2, probe.Outputs.Count);
        Assert.Equal(DslPin.EnumOutputMarker, probe.Outputs.Single(p => p.Name == "status").EnumType);
    }
}
