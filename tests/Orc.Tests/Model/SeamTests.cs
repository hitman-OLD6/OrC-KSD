using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// R1i 修订（2026-10-29 用户裁决：**seam 恢复，不是退役**）的验收：
/// 成员声明具名缝 → handler 内 <c>ctx.SeamAsync</c> 按该缝的包装器列表**管道式 LIFO** 执行；
/// 缝未声明＝<see cref="KeyNotFoundException"/>；声明了但无人挂＝空缝（不报错、无副作用）；
/// 缝内包装只接受 <c>before</c>；未声明任何缝的成员不注入执行器（<see cref="InvalidOperationException"/>）。
/// </summary>
public class SeamTests
{
    private const string Seam = "指向后";

    /// <summary>引擎预制体（只含本组用例的成员）＋薄基类卡牌；返回运行时与其 handler 注册表。</summary>
    private static (OrchestrationRuntime Runtime, HandlerRegistry Handlers, LogicEngine Engine) Build(MemberDefinition member)
    {
        var handlers = new HandlerRegistry();
        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(member))
            .Add(ModelKit.CardBase());

        // 执行帧由运行时补建（帧的流挂在引擎根流上）；纯内核用例不需要真正的对局引擎。
        return (new OrchestrationRuntime(catalog, handlers), handlers, new LogicEngine());
    }

    /// <summary>handler 内打开具名缝的成员（自有边先记一条 trace，再调 <c>ctx.SeamAsync</c>）。</summary>
    private static MemberDefinition SeamMember(List<string> trace, string seamName, params string[] declaredSeams)
        => new(
            "flow",
            ViewSpec.Empty,
            MemberVisibility.All,
            new[]
            {
                new OwnedEdge((KernelHandler)((_, ctx, ct) =>
                {
                    trace.Add("own");
                    return ctx.SeamAsync(seamName, ct);
                })),
            },
            seams: declaredSeams.Length == 0 ? null : declaredSeams);

    /// <summary>
    /// 执行成员：必须走**建帧**重载（<c>engine</c> 具名实参）——缝执行器挂在执行帧上，
    /// 无帧重载下 <c>ctx.SeamAsync</c> 抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    private static Task RunAsync(OrchestrationRuntime runtime, LogicEngine engine)
        => runtime.RunMemberAsync(runtime.Engine, "flow", new Context(), engine);

    private static HandlerAsset Tracing(HandlerRegistry handlers, List<string> trace, string name)
        => handlers.Register($"h.{name}", (_, _, _) =>
        {
            trace.Add(name);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task 成员声明具名缝且包装挂到该缝_handler内调用时执行该包装器()
    {
        var trace = new List<string>();
        var (runtime, handlers, engine) = Build(SeamMember(trace, Seam, Seam));

        // 成员外围的 before 包装（对照项：它只在成员外围跑，与缝内包装互不串味）。
        runtime.Wraps.Register(
            new WrapDeclaration(WrapTarget.ForMember(MemberAddress.Parse("engine#flow")), WrapperPhase.Before, "h.outer"),
            Tracing(handlers, trace, "outer"),
            new DeclarationOrder(0, 0, 0));

        // 挂到 (成员, 缝) 上的包装：只在 ctx.SeamAsync("指向后") 处执行。
        runtime.Wraps.Register(
            new WrapDeclaration(WrapTarget.ForMember(MemberAddress.Parse("engine#flow")), WrapperPhase.Before, "h.seam", Seam),
            Tracing(handlers, trace, "seam"),
            new DeclarationOrder(0, 1, 0));

        await RunAsync(runtime, engine);

        // 外围 before → 自有边 → 缝内包装（插入点在 handler 内部）。
        Assert.Equal(new[] { "outer", "own", "seam" }, trace);
    }

    [Fact]
    public async Task 同一缝上的多个包装器按声明序LIFO执行()
    {
        var trace = new List<string>();
        var (runtime, handlers, engine) = Build(SeamMember(trace, Seam, Seam));

        runtime.Wraps.Register(
            new WrapDeclaration(WrapTarget.ForMember(MemberAddress.Parse("engine#flow")), WrapperPhase.Before, "h.s1", Seam),
            Tracing(handlers, trace, "s1"),
            new DeclarationOrder(0, 0, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(WrapTarget.ForMember(MemberAddress.Parse("engine#flow")), WrapperPhase.Before, "h.s2", Seam),
            Tracing(handlers, trace, "s2"),
            new DeclarationOrder(0, 1, 0));

        await RunAsync(runtime, engine);

        // 管道式（装饰器嵌套）：后声明者在外层、先执行；与成员外围 before 的 FIFO 相反。
        Assert.Equal(new[] { "own", "s2", "s1" }, trace);
    }

    [Fact]
    public async Task 成员未声明该缝时调用抛KeyNotFound()
    {
        var trace = new List<string>();
        var (runtime, _, engine) = Build(SeamMember(trace, Seam, "另一个缝"));

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => RunAsync(runtime, engine));

        Assert.Contains("engine#flow", error.Message, StringComparison.Ordinal);
        Assert.Contains(Seam, error.Message, StringComparison.Ordinal);
        Assert.Contains("另一个缝", error.Message, StringComparison.Ordinal); // 消息列出可用缝名
        Assert.Equal(new[] { "own" }, trace); // 自有边已跑完，失败发生在插入点
    }

    [Fact]
    public async Task 成员声明了缝但没人挂载时是空缝_不抛错且无副作用()
    {
        var trace = new List<string>();
        var (runtime, _, engine) = Build(SeamMember(trace, Seam, Seam));

        await RunAsync(runtime, engine);

        // 空缝≠未声明：不执行任何包装、不抛错（"能力恢复但暂无用例"的预期状态）。
        Assert.Equal(new[] { "own" }, trace);
    }

    [Fact]
    public async Task 成员未声明任何缝时不注入执行器_调用抛InvalidOperation()
    {
        var trace = new List<string>();
        var (runtime, _, engine) = Build(SeamMember(trace, Seam));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runtime, engine));

        Assert.Contains("handler", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "own" }, trace);
    }

    [Fact]
    public void 缝内包装只接受before_after与replace以及判定器目标一律拒绝()
    {
        var address = MemberAddress.Parse("engine#flow");

        Assert.Throws<ArgumentException>(() => new WrapDeclaration(
            WrapTarget.ForMember(address), WrapperPhase.After, "h.any", Seam));
        Assert.Throws<ArgumentException>(() => new WrapDeclaration(
            WrapTarget.ForMember(address), WrapperPhase.Replace, "h.any", Seam));
        Assert.Throws<ArgumentException>(() => new WrapDeclaration(
            WrapTarget.ForJudicator("伤害判定"), WrapperPhase.Before, "h.any", Seam));
        Assert.Throws<ArgumentException>(() => new WrapDeclaration(
            WrapTarget.ForMember(address), WrapperPhase.Before, "h.any", "   "));

        // 合法形态：缝内包装，相位恒为 before，目标键带缝名（与成员外围包装不共键）。
        var declaration = WrapDeclaration.ForSeam("engine#flow", Seam, "h.any");
        Assert.Equal(WrapperPhase.Before, declaration.Phase);
        Assert.Equal(Seam, declaration.Seam);
        Assert.Equal(WrapTarget.ForSeam(address, Seam), declaration.Target);
        Assert.NotEqual(WrapTarget.ForMember(address).Key, declaration.Target.Key);
    }

    [Fact]
    public void 成员缝声明的空白项与重复名一律拒绝()
    {
        Assert.Throws<ArgumentException>(() => new MemberDefinition("flow", seams: new[] { " " }));
        Assert.Throws<ArgumentException>(() => new MemberDefinition("flow", seams: new[] { Seam, Seam }));
        Assert.Throws<ArgumentException>(() => new MemberDefinition("flow", seams: new string[] { null! }));

        // 空／null＝不声明任何缝（不是"声明了空缝"）。
        Assert.Empty(new MemberDefinition("flow").Seams);
        Assert.Empty(new MemberDefinition("flow", seams: Array.Empty<string>()).Seams);
    }

    [Fact]
    public void 资产schema放开members_seams与wraps_seam_往返不丢()
    {
        const string json = """
        {
          "schemaVersion": 4,
          "asset": {
            "id": "comp.seam",
            "members": [
              {
                "name": "targeting",
                "visibility": ["hostable"],
                "view": { "fields": [] },
                "seams": ["指向后"]
              }
            ],
            "wraps": [
              { "target": "host#targeting", "phase": "before", "handler": "h.wrap", "seam": "指向后" }
            ]
          }
        }
        """;

        var asset = AssetSchema.Read(json);

        var member = Assert.Single(asset.Members);
        Assert.Equal(new[] { Seam }, member.Seams);

        var wrap = Assert.Single(asset.Wraps);
        Assert.Equal(Seam, wrap.Seam);
        Assert.Equal(WrapperPhase.Before, wrap.Phase);
        Assert.Equal(WrapTarget.ForSeam(MemberAddress.Parse("host#targeting"), Seam), wrap.Target);

        // 写回再读：seams／seam 都进规范 JSON（往返不丢，S11 资产唯一真源）。
        var text = AssetSchema.Write(asset);
        Assert.Contains("\"seams\"", text, StringComparison.Ordinal);
        Assert.Contains("\"seam\"", text, StringComparison.Ordinal);

        var again = AssetSchema.Read(text);
        Assert.Equal(new[] { Seam }, Assert.Single(again.Members).Seams);
        Assert.Equal(Seam, Assert.Single(again.Wraps).Seam);
    }
}
