using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// 成员内部边次序（R1i 修订 2026-10-29）：**band（段名）取代 <c>own-edge-position</c>**。
/// <para>
/// 三条判据（逐条对应一条语义）：
/// <list type="number">
///   <item>外来边落在**更早的段** ⇒ 它先执行——旧 <c>last</c>（管道型：改写先跑、自有边收口）就是这一形态；</item>
///   <item>外来边与自有边**同段** ⇒ 自有边在前——旧 <c>first</c>（流程型）就是这一形态；</item>
///   <item><c>Band</c> 未在 <c>Bands</c> 中声明 ⇒ 构造期 <see cref="ArgumentException"/>（fail-fast）。</item>
/// </list>
/// </para>
/// <para>
/// 自有边与外来边写入**同一** trace，因此执行序可直接由一条列表断言；
/// "段序"取自旧 <c>ActionBands.cs</c> 的枚举数值序（本文件用两段的最小方案即可表达判据）。
/// </para>
/// </summary>
public class MemberBandOrderTests
{
    /// <summary>两段方案（首段／末段）：足以区分"更早的段"与"同段"两种形态。</summary>
    private static readonly string[] Scheme = { "Initiate", "Commit" };

    private static OrchestrationRuntime BuildRuntime(
        List<string> trace,
        string? band = null,
        IReadOnlyList<string>? bands = null,
        params (string Id, string Label)[] probes)
    {
        var flow = new MemberDefinition(
            "flow",
            ViewSpec.Empty,
            MemberVisibility.All,
            new[] { ModelKit.Trace(trace, "own") },
            band: band,
            bands: bands);

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(flow))
            .Add(ModelKit.CardBase());

        foreach (var probe in probes.Length == 0 ? new[] { ("comp.probe", "probe") } : probes)
        {
            catalog = catalog.Add(new PrefabDefinition(
                probe.Item1,
                EntityKind.Component,
                requiresHost: HostKind.Any,
                members: new[]
                {
                    new MemberDefinition(
                        "probe",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        new[] { ModelKit.Trace(trace, probe.Item2) }),
                }));
        }

        return new OrchestrationRuntime(catalog, new HandlerRegistry());
    }

    [Fact]
    public async Task 外来边段早于自有边段_外来边先执行()
    {
        // 旧 last 语义的 band 写法：自有边落末段（Commit），外来边落首段（Initiate）。
        var trace = new List<string>();
        var runtime = BuildRuntime(trace, band: "Commit", bands: Scheme);
        var host = runtime.CreateEntity("card.base", "宿主");
        var probe = runtime.CreateEntity("comp.probe", "探针", host);
        runtime.MountAtRuntime(probe, "probe", "engine#flow", band: "Initiate");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "probe", "own" }, trace);
    }

    [Fact]
    public async Task 外来边与自有边同段_自有边在前()
    {
        // 旧 first 语义的 band 写法：缺省段名＝与自有边同段；同段内自有边恒在前。
        var trace = new List<string>();
        var runtime = BuildRuntime(trace, band: "Commit", bands: Scheme);
        var host = runtime.CreateEntity("card.base", "宿主");
        var probe = runtime.CreateEntity("comp.probe", "探针", host);
        runtime.MountAtRuntime(probe, "probe", "engine#flow");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "own", "probe" }, trace);
    }

    [Fact]
    public async Task 显式同段的外来边_也排在自有边之后()
    {
        // 显式写 member.Band 与"不写"等价（`edge.Band ?? member.Band`）。
        var trace = new List<string>();
        var runtime = BuildRuntime(trace, band: "Commit", bands: Scheme);
        var host = runtime.CreateEntity("card.base", "宿主");
        var probe = runtime.CreateEntity("comp.probe", "探针", host);
        runtime.MountAtRuntime(probe, "probe", "engine#flow", band: "Commit");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "own", "probe" }, trace);
    }

    [Fact]
    public void Band未在Bands中声明_构造期抛ArgumentException()
    {
        var error = Assert.Throws<ArgumentException>(() => new MemberDefinition(
            "flow",
            ViewSpec.Empty,
            MemberVisibility.All,
            Array.Empty<OwnedEdge>(),
            band: "Veto",
            bands: Scheme));

        Assert.Contains("未在成员的 bands 方案中声明", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Band有值但无Bands方案_构造期抛ArgumentException()
    {
        var error = Assert.Throws<ArgumentException>(() => new MemberDefinition(
            "flow",
            ViewSpec.Empty,
            MemberVisibility.All,
            Array.Empty<OwnedEdge>(),
            band: "Commit"));

        Assert.Contains("未在成员的 bands 方案中声明", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bands含重复名或空白项_构造期抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new MemberDefinition(
            "flow", ViewSpec.Empty, MemberVisibility.All, Array.Empty<OwnedEdge>(),
            bands: new[] { "Initiate", "Initiate" }));
        Assert.Throws<ArgumentException>(() => new MemberDefinition(
            "flow", ViewSpec.Empty, MemberVisibility.All, Array.Empty<OwnedEdge>(),
            bands: new[] { "Initiate", " " }));
    }

    [Fact]
    public void BandIndexOf_缺省段与null方案均为0_未知段名抛错()
    {
        var single = new MemberDefinition("single", ViewSpec.Empty, MemberVisibility.All, Array.Empty<OwnedEdge>());
        Assert.Equal(0, single.BandIndexOf(null));

        var segmented = new MemberDefinition(
            "segmented", ViewSpec.Empty, MemberVisibility.All, Array.Empty<OwnedEdge>(), bands: Scheme);
        Assert.Equal(0, segmented.BandIndexOf(null));
        Assert.Equal(0, segmented.BandIndexOf("Initiate"));
        Assert.Equal(1, segmented.BandIndexOf("Commit"));
        Assert.Throws<ArgumentException>(() => segmented.BandIndexOf("Veto"));
    }

    [Fact]
    public async Task 三段方案下_外来边按段序插入_同段内保持挂载序()
    {
        // 自有边在末段（Apply）；三条外来边分别落 Rewrite／Apply／Rewrite —— 期望：
        // 两条 Rewrite（挂载序：甲 → 丙）→ 自有边 → Apply 段的乙。
        var trace = new List<string>();
        var runtime = BuildRuntime(
            trace,
            band: "Apply",
            bands: new[] { "Rewrite", "Veto", "Apply" },
            ("comp.a", "甲"),
            ("comp.b", "乙"),
            ("comp.c", "丙"));

        var host = runtime.CreateEntity("card.base", "宿主");
        runtime.MountAtRuntime(runtime.CreateEntity("comp.a", "甲", host), "probe", "engine#flow", band: "Rewrite");
        runtime.MountAtRuntime(runtime.CreateEntity("comp.b", "乙", host), "probe", "engine#flow", band: "Apply");
        runtime.MountAtRuntime(runtime.CreateEntity("comp.c", "丙", host), "probe", "engine#flow", band: "Rewrite");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "甲", "丙", "own", "乙" }, trace);
    }

    [Fact]
    public async Task 单段方案_所有边同段_自有边按声明序在前()
    {
        // 无 bands＝单段：等价于旧默认 first（三个非动作引擎流程成员的形态）。
        var trace = new List<string>();
        var runtime = BuildRuntime(trace);
        var host = runtime.CreateEntity("card.base", "宿主");
        runtime.MountAtRuntime(runtime.CreateEntity("comp.probe", "探针", host), "probe", "engine#flow");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "own", "probe" }, trace);
    }
}
