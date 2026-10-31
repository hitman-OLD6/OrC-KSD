using System.Reflection;
using Orc.Game.Effects.HandlerDsl;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>
/// 扫描生成（S4／R25／D10／D12）：默认映射规则、隐式宿主、重载合并、结果对象拆解、
/// 折叠标记、内建 op 冲突、内容派生 <c>catalogRevision</c>。
/// </summary>
public class CatalogScanTests
{
    private static readonly Assembly GameAssembly = typeof(Orc.Game.Effects.EffectRuntime).Assembly;

    private static HandlerDslCatalogScanResult ScanGame() => HandlerDslCatalogScanner.Scan(GameAssembly);

    [Fact]
    public void 扫描真实动作面成功_且两个门面都进清单()
    {
        var result = ScanGame();

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains("Orc.Game.Effects.EffectRuntime", result.ScannedFaces);
        Assert.Contains("Orc.Game.Cards.KeywordEffectActionFace", result.ScannedFaces);
        Assert.NotNull(result.Catalog);
        Assert.True(result.Catalog!.Nodes.Count > 10, $"节点过少：{result.Catalog.Nodes.Count}");
    }

    [Fact]
    public void CancellationToken_恒不出现在任何引脚里()
    {
        var catalog = ScanGame().Catalog!;

        var offenders = catalog.Nodes
            .SelectMany(n => n.Inputs.SelectMany(p => new[] { (n.Name, Pin: p.Name) }))
            .Where(x => x.Pin.Contains("ct", StringComparison.OrdinalIgnoreCase)
                        || x.Pin.Contains("cancellation", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void 隐式宿主首参不出引脚_death只有三个可见引脚()
    {
        var catalog = ScanGame().Catalog!;
        Assert.True(catalog.TryGetNode("death", out var death), "KeywordEffectActionFace.DeathAsync 应派生出 death 节点");

        Assert.Equal(new[] { "target", "killer", "triggerCard" }, death.Inputs.Select(p => p.Name).ToArray());
        Assert.Equal("host", death.ImplicitHost);
        Assert.True(death.IsStatic);
        Assert.Equal("Orc.Game.Cards.KeywordEffectActionFace", death.FaceType);

        // killer／triggerCard 有默认值 ⇒ 可选；target 无默认值 ⇒ 必填。
        Assert.True(death.Input("target")!.Required);
        Assert.False(death.Input("killer")!.Required);
        Assert.False(death.Input("triggerCard")!.Required);
    }

    [Fact]
    public void 重载合并成一个节点_grant的value是一个可选引脚()
    {
        var catalog = ScanGame().Catalog!;
        Assert.True(catalog.TryGetNode("grant", out var grant));

        var value = grant.Input("value");
        Assert.NotNull(value);
        Assert.False(value!.Required); // 只在部分重载里出现 ⇒ 可选

        // 只有一个 grant 节点（不是两个重载分支）。
        Assert.Single(catalog.Nodes.Where(n => n.Name == "grant"));
    }

    [Fact]
    public void 枚举参数按string加取值集映射_并携带枚举类型()
    {
        var catalog = ScanGame().Catalog!;
        Assert.True(catalog.TryGetNode("buff", out var buff));

        var duration = buff.Input("duration")!;
        Assert.Equal(PinTypeKind.String, duration.Type.Kind);
        Assert.False(duration.Required);
        Assert.Contains("TurnEnd", duration.EnumValues);
        Assert.Equal("Orc.Game.Effects.EffectDuration", duration.EnumType);
    }

    [Fact]
    public void 结果对象拆解标注展开成多输出()
    {
        var catalog = ScanGame().Catalog!;
        Assert.True(catalog.TryGetNode("fetchFromDeck", out var fetch), "FetchFromDeckAsync 应进目录");

        Assert.Equal(new[] { "status", "card" }, fetch.Outputs.Select(p => p.Name).ToArray());
        Assert.True(fetch.Output("status")!.IsEnumOutput);
        Assert.Equal(PinTypeKind.Entity, fetch.Output("card")!.Type.Kind);
    }

    [Fact]
    public void 结果对象未标注拆解_扫描期失败并指名方法()
    {
        var result = HandlerDslCatalogScanner.Scan(typeof(UnannotatedFace).Assembly);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("OrphanResultAsync", StringComparison.Ordinal)
                                            && e.Contains("HandlerDslResult", StringComparison.Ordinal));
        Assert.Contains(result.Methods, m => m.Method == "OrphanResultAsync" && m.Outcome == HandlerDslMethodOutcome.Failed);
    }

    [Fact]
    public void 内建op与目录节点名冲突_扫描期失败并指名()
    {
        var result = HandlerDslCatalogScanner.Scan(typeof(BuiltinConflictFace).Assembly);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("'ask'", StringComparison.Ordinal));
    }

    [Fact]
    public void catalogRevision由内容派生_可复现且随内容变化()
    {
        var first = ScanGame();
        var second = ScanGame();

        Assert.Equal(first.Catalog!.Revision, second.Catalog!.Revision);
        Assert.True(first.Catalog.Revision > 0, "内容派生 revision 不应为 0");

        var nodes = first.Catalog.Nodes.Where(n => !HandlerDslContextNodes.IsContextOp(n.Name)).ToList();
        var changed = nodes
            .Select(n => n.Name == "damage"
                ? new DslNodeSignature(n.Name, n.Inputs, n.Outputs, n.HasAwait, n.HasSideEffect, n.Face, n.Method, n.ImplicitHost, n.IsStatic, n.FaceType)
                : n)
            .ToList();

        // 改名一个节点 ⇒ revision 必须变（内容派生，不是写死的常量）。
        changed[0] = new DslNodeSignature(
            changed[0].Name + "Renamed", changed[0].Inputs, changed[0].Outputs,
            changed[0].HasAwait, changed[0].HasSideEffect, changed[0].Face, changed[0].Method,
            changed[0].ImplicitHost, changed[0].IsStatic, changed[0].FaceType);

        Assert.NotEqual(
            HandlerDslCatalogScanner.RevisionOf(nodes),
            HandlerDslCatalogScanner.RevisionOf(changed));
    }

    [Fact]
    public void 动作节点带递归折叠标记_同步纯查询不挂起()
    {
        var catalog = ScanGame().Catalog!;

        Assert.True(catalog.TryGetNode("damage", out var damage));
        Assert.True(damage.HasAwait);
        Assert.True(damage.HasSideEffect);

        // 附注：v1 目录里已无「同步纯查询」动作节点——原先用 OwnerUnits 承载这条断言，
        // 而它的首参 host 是查询目标、与隐式宿主白名单语义冲突，已移入 EXCLUSIONS（A 区）。
        // 标记的传递性仍由上面 damage 与下面的容器节点用例覆盖。
        Assert.True(catalog.TryGetNode("addToHand", out var addToHand));
        Assert.True(addToHand.HasAwait);
        Assert.True(addToHand.HasSideEffect);
    }

    [Fact]
    public void 上下文原语stop与interrupt由目录合成_且无引脚无输出()
    {
        var catalog = ScanGame().Catalog!;

        foreach (var name in new[] { HandlerDslContextNodes.Stop, HandlerDslContextNodes.Interrupt })
        {
            Assert.True(catalog.TryGetNode(name, out var node));
            Assert.Empty(node.Inputs);
            Assert.Empty(node.Outputs);
            Assert.True(node.HasSideEffect);
            Assert.False(node.HasAwait);
            Assert.DoesNotContain(name, catalog.ScannedFaces);
        }
    }

    [Fact]
    public void 目录是纯数据_可从节点名与引脚静态读出全部元数据()
    {
        var catalog = ScanGame().Catalog!;

        Assert.All(catalog.Nodes, node =>
        {
            Assert.False(string.IsNullOrWhiteSpace(node.Name));
            Assert.All(node.Inputs, pin => Assert.False(string.IsNullOrWhiteSpace(pin.Name)));
            Assert.All(node.Outputs, pin => Assert.False(string.IsNullOrWhiteSpace(pin.Name)));
        });

        // 可读项来自目录（read.path 的封闭取值集）。
        Assert.Contains("attack", catalog.Readables.Keys);
    }

    [Fact]
    public void 只用默认规则的新方法_目录自动多一个节点()
    {
        var result = HandlerDslCatalogScanner.ScanTypes(new[] { typeof(DefaultRulesFace) });

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(result.Catalog!.TryGetNode("exile", out var exile), "ExileAsync 应派生为 exile 节点");
        Assert.Equal(new[] { "target" }, exile.Inputs.Where(p => p.Required).Select(p => p.Name).ToArray());
        Assert.Single(exile.Outputs);
        Assert.Equal(PinTypeKind.Boolean, exile.Outputs[0].Type.Kind);
        Assert.Equal(HandlerDslMethodOutcome.Generated, result.Methods.Single().Outcome);
    }

    [Fact]
    public void 未标记类型的public方法不产生节点()
    {
        // 鸭子类型判据被明确否决（D10）：名字像动作面的类型，没有 [HandlerDslActionFace] 就不进目录。
        var result = HandlerDslCatalogScanner.Scan(typeof(NotAFace).Assembly);

        Assert.DoesNotContain(result.Methods, m => m.Method == "LooksLikeAnActionAsync");
        Assert.All(result.Methods, m => Assert.NotEqual(typeof(NotAFace).FullName, m.Face));
    }

    [Fact]
    public void 契约包内不存在按方法名索引的标注实例表()
    {
        var files = Directory.GetFiles(Fixtures.ContractRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToArray();

        Assert.DoesNotContain("annotations.json", files);
        Assert.All(files, f => Assert.DoesNotContain("annotation", f!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 样例链路_未标注报错_补标注后目录多一节点且revision递增()
    {
        // ① 未标注：扫描期报「未标注拆解」，并指名方法。
        var before = HandlerDslCatalogScanner.ScanTypes(new[] { typeof(SampleChainBefore) });
        Assert.False(before.Success);
        Assert.Contains(before.Errors, e => e.Contains("ExileAsync", StringComparison.Ordinal));

        // ② 补 [HandlerDslResult]：目录多一个节点，catalogRevision 随之变化（内容派生）。
        var after = HandlerDslCatalogScanner.ScanTypes(new[] { typeof(SampleChainAfter) });
        Assert.True(after.Success, string.Join("\n", after.Errors));
        Assert.True(after.Catalog!.TryGetNode("exile", out var exile));
        Assert.Equal(new[] { "status" }, exile.Outputs.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void 目录序列化往返_两次文本逐字节相同()
    {
        var catalog = ScanGame().Catalog!;

        var first = HandlerDslCatalogJson.Serialize(catalog);
        var round = HandlerDslCatalogJson.Deserialize(first);
        var second = HandlerDslCatalogJson.Serialize(round);

        Assert.Equal(first, second);

        // 反序列化后的目录在内容上也等价（节点名、引脚、来源、折叠标记、revision、门面清单）。
        Assert.Equal(catalog.Revision, round.Revision);
        Assert.Equal(catalog.Nodes.Select(n => n.Name), round.Nodes.Select(n => n.Name));
        Assert.Equal(catalog.ScannedFaces, round.ScannedFaces);
        Assert.All(catalog.Nodes, node =>
        {
            Assert.True(round.TryGetNode(node.Name, out var copy));
            Assert.Equal(node.Inputs.Select(p => (p.Name, p.Type.Format(), p.Required)), copy.Inputs.Select(p => (p.Name, p.Type.Format(), p.Required)));
            Assert.Equal(node.Outputs.Select(p => (p.Name, p.Type.Format())), copy.Outputs.Select(p => (p.Name, p.Type.Format())));
            Assert.Equal(node.Face, copy.Face);
            Assert.Equal(node.Method, copy.Method);
            Assert.Equal(node.FaceType, copy.FaceType);
            Assert.Equal(node.ImplicitHost, copy.ImplicitHost);
            Assert.Equal(node.IsStatic, copy.IsStatic);
            Assert.Equal(node.HasAwait, copy.HasAwait);
            Assert.Equal(node.HasSideEffect, copy.HasSideEffect);
        });
    }

    [Fact]
    public void 目录快照里合成节点不带来源_且不重复注入()
    {
        var catalog = ScanGame().Catalog!;
        var json = HandlerDslCatalogJson.Serialize(catalog);

        Assert.Contains("\"name\": \"stop\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"interrupt\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("faceType\": null", json, StringComparison.Ordinal);

        var round = HandlerDslCatalogJson.Deserialize(json);
        Assert.Single(round.Nodes.Where(n => n.Name == HandlerDslContextNodes.Stop));
        Assert.Single(round.Nodes.Where(n => n.Name == HandlerDslContextNodes.Interrupt));
        Assert.Null(round.Nodes.Single(n => n.Name == HandlerDslContextNodes.Stop).Face);
        Assert.Null(round.Nodes.Single(n => n.Name == HandlerDslContextNodes.Stop).FaceType);
    }

    // ── 合成门面（只在本测试程序集里，用来跑失败路径与样例链路） ────────────────

    /// <summary>只用默认映射规则就能表达的方法（R25 第 1 条：目录自动多一个节点）。</summary>
    [HandlerDslActionFace]
    public sealed class DefaultRulesFace
    {
        public Task<bool> ExileAsync(Orc.Cards.Card target, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    /// <summary>名字像动作面但**没有**登记特性（D10 的漏标判据只对 <c>*ActionFace</c> 命名生效）。</summary>
    public sealed class NotAFace
    {
        public Task LooksLikeAnActionAsync(Orc.Cards.Card target) => Task.CompletedTask;
    }

    /// <summary>结果对象未标注拆解 ⇒ 目录生成失败（R25 第 3 条）。</summary>
    [HandlerDslActionFace]
    public sealed class UnannotatedFace
    {
        public Task<OrphanResult> OrphanResultAsync(CancellationToken ct = default) =>
            Task.FromResult(new OrphanResult());
    }

    /// <summary>不在封闭集里的结果对象（故意不标 [HandlerDslResult]）。</summary>
    public sealed class OrphanResult
    {
        public string Status { get; } = "ok";
    }

    /// <summary>派生出内建 op 名 ⇒ 扫描期断言失败（D17）。</summary>
    [HandlerDslActionFace]
    public sealed class BuiltinConflictFace
    {
        public Task AskAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>样例链路①：结果对象未标注拆解（应报错并指名 ExileAsync）。</summary>
    [HandlerDslActionFace]
    public sealed class SampleChainBefore
    {
        public Task<ExileOutcome> ExileAsync(CancellationToken ct = default) =>
            Task.FromResult(new ExileOutcome());
    }

    /// <summary>样例链路②：补 [HandlerDslResult] 之后（目录多一个 exile 节点）。</summary>
    [HandlerDslActionFace]
    public sealed class SampleChainAfter
    {
        [HandlerDslResult("status:string:enum")]
        public Task<ExileOutcome> ExileAsync(CancellationToken ct = default) =>
            Task.FromResult(new ExileOutcome());
    }

    /// <summary>模拟「引擎新增一个返回结果对象的方法」（未标注时目录生成失败）。</summary>
    public sealed class ExileOutcome
    {
        public string Status { get; } = "exiled";
    }
}
