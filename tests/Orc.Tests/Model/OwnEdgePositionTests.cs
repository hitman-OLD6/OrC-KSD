using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// 成员自有边位次（R1i 修订 2026-10-26 / D1）：
/// <c>first</c>（流程型，默认）＝自有边 → 外来边；<c>last</c>（管道型）＝外来边 → 自有边。
/// <para>
/// 缺口来源：旧动作管道的"转发"（<c>Commit</c>）与"落地"（<c>Apply</c>）都是链上最后一步，
/// 一律"自有边固定最前"会让外来边与包装永不执行（S13 第一轮 §A0 的 D1）。
/// </para>
/// <para>自有边与外来边写入**同一** trace，因此执行序可直接由一条列表断言。</para>
/// </summary>
public class OwnEdgePositionTests
{
    private static OrchestrationRuntime BuildRuntime(List<string> trace, OwnEdgePosition? position)
    {
        var flow = new MemberDefinition(
            "flow",
            ViewSpec.Empty,
            MemberVisibility.All,
            new[] { ModelKit.Trace(trace, "own") },
            ownEdgePosition: position ?? OwnEdgePosition.First);

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(flow))
            .Add(ModelKit.CardBase())
            .Add(new PrefabDefinition(
                "comp.probe",
                EntityKind.Component,
                requiresHost: HostKind.Any,
                members: new[]
                {
                    new MemberDefinition("probe", ViewSpec.Empty, MemberVisibility.Mountable, new[] { ModelKit.Trace(trace, "probe") }),
                }));

        return new OrchestrationRuntime(catalog, new HandlerRegistry());
    }

    [Fact]
    public async Task 默认位次为first_自有边先跑_外来边续后()
    {
        var trace = new List<string>();
        var runtime = BuildRuntime(trace, position: null);
        var host = runtime.CreateEntity("card.base", "宿主");
        var probe = runtime.CreateEntity("comp.probe", "探针", host);
        runtime.MountAtRuntime(probe, "probe", "engine#flow");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "own", "probe" }, trace);
    }

    [Fact]
    public async Task 声明last_外来边先跑_自有边最后()
    {
        var trace = new List<string>();
        var runtime = BuildRuntime(trace, OwnEdgePosition.Last);
        var host = runtime.CreateEntity("card.base", "宿主");
        var probe = runtime.CreateEntity("comp.probe", "探针", host);
        runtime.MountAtRuntime(probe, "probe", "engine#flow");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "probe", "own" }, trace);
    }

    [Fact]
    public async Task last位次下多条外来边仍按秩次执行_自有边恒最后()
    {
        var trace = new List<string>();
        var runtime = BuildRuntime(trace, OwnEdgePosition.Last);
        var host = runtime.CreateEntity("card.base", "宿主");
        var first = runtime.CreateEntity("comp.probe", "甲", host);
        var second = runtime.CreateEntity("comp.probe", "乙", host);
        runtime.MountAtRuntime(first, "probe", "engine#flow");
        runtime.MountAtRuntime(second, "probe", "engine#flow");

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        // 两条外来边按建边序（appended）执行，自有边恒排最后。
        Assert.Equal(new[] { "probe", "probe", "own" }, trace);
    }
}
