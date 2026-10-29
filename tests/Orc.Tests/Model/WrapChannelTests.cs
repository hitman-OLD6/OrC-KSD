using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S2a 验收（包装通道）：
/// 包装只指向成员（无二级地址）、before FIFO／after FIFO／replace 最后声明者、
/// 具名缝（seam）与成员外围包装不共键（R1i 修订 2026-10-29：seam 恢复，见 <see cref="SeamTests"/>）、
/// 判定器包装器共用同一相位语义与同一注册面、
/// 免疫（before + Stop）后 after 与后续边不执行、<c>ctx.Stop()</c> 作用域限于当前成员。
/// </summary>
public class WrapChannelTests
{
    private static OrchestrationRuntime BuildRuntime(List<string> trace, HandlerRegistry handlers)
    {
        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(new MemberDefinition(
                "flow",
                new ViewSpec(new[]
                {
                    new ViewFieldDefinition("amount", ViewFieldType.Number, writable: true, required: false),
                }),
                MemberVisibility.All,
                new[] { ModelKit.Trace(trace, "own") })))
            .Add(ModelKit.CardBase());

        return new OrchestrationRuntime(catalog, handlers);
    }

    private static HandlerAsset TraceHandler(HandlerRegistry handlers, List<string> trace, string name, Action<ViewCarrier, Context>? extra = null)
        => handlers.Register($"h.{name}", (view, ctx, ct) =>
        {
            trace.Add(name);
            extra?.Invoke((ViewCarrier)view, ctx);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task before按FIFO_after按FIFO_replace取最后声明者()
    {
        var trace = new List<string>();
        var handlers = new HandlerRegistry();
        var runtime = BuildRuntime(trace, handlers);
        var target = WrapTarget.ForMember(MemberAddress.Parse("engine#flow"));

        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.Before, "h.before1"),
            TraceHandler(handlers, trace, "before1"),
            new DeclarationOrder(0, 0, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.Before, "h.before2"),
            TraceHandler(handlers, trace, "before2"),
            new DeclarationOrder(0, 1, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.After, "h.after1"),
            TraceHandler(handlers, trace, "after1"),
            new DeclarationOrder(0, 0, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.After, "h.after2"),
            TraceHandler(handlers, trace, "after2"),
            new DeclarationOrder(0, 1, 0));

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        // R1i 修订（D3）：before 与 after 同为声明序正序；旧 LIFO 期望 "before2, before1" 已按 FIFO 改写。
        Assert.Equal(new[] { "before1", "before2", "own", "after1", "after2" }, trace);
    }

    [Fact]
    public async Task replace接管整个边集合时自有边不执行且最后声明者生效()
    {
        var trace = new List<string>();
        var handlers = new HandlerRegistry();
        var runtime = BuildRuntime(trace, handlers);
        var target = WrapTarget.ForMember(MemberAddress.Parse("engine#flow"));

        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.Replace, "h.replace1"),
            TraceHandler(handlers, trace, "replace1"),
            new DeclarationOrder(0, 0, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.Replace, "h.replace2"),
            TraceHandler(handlers, trace, "replace2"),
            new DeclarationOrder(0, 1, 0));

        await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "replace2" }, trace); // 自有边（own）被接管、不执行
    }

    [Fact]
    public async Task 免疫命中后after包装器与后续边不执行且已执行的before写入保留()
    {
        var trace = new List<string>();
        var handlers = new HandlerRegistry();
        var runtime = BuildRuntime(trace, handlers);
        var target = WrapTarget.ForMember(MemberAddress.Parse("engine#flow"));
        object? observed = null;

        // FIFO（R1i 修订）：声明的 write（order 0）先执行、immunity（order 1）后执行——
        // 停止后已执行的 before 不回滚（其写入对同链后续单元可见），但 after 与本成员的边不再执行。
        // 改写理由：旧用例靠 LIFO 让"后声明的 write"先跑；before 改 FIFO 后若保持原声明序，
        // immunity 会先执行并 Stop，write 永不执行→断言"已执行 before 的写入保留"无从验证。
        // 故交换两者的声明序（**语义未变**：仍验证"先跑一个写、再被免疫停住"）。
        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.Before, "h.write"),
            TraceHandler(handlers, trace, "write", static (view, _) => view.Set("amount", 99)),
            new DeclarationOrder(0, 0, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.Before, "h.immunity"),
            TraceHandler(handlers, trace, "immunity", (view, ctx) =>
            {
                observed = view.Get("amount");
                ctx.Stop();
            }),
            new DeclarationOrder(0, 1, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.After, "h.after"),
            TraceHandler(handlers, trace, "after"),
            new DeclarationOrder(0, 0, 0));

        var result = await runtime.RunMemberAsync(runtime.Engine, "flow");

        Assert.Equal(new[] { "write", "immunity" }, trace); // own 与 after 均不执行
        Assert.True(result.Stopped);
        Assert.Equal(99, observed); // 已执行的 before 写入保留
    }

    [Fact]
    public async Task 停止作用域限于当前成员不外溢到调用方其它成员()
    {
        var trace = new List<string>();

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(new MemberDefinition("outer", ViewSpec.Empty, MemberVisibility.All)))
            .Add(ModelKit.CardBase())
            .Add(new PrefabDefinition(
                "comp.stop",
                EntityKind.Component,
                members: new[]
                {
                    new MemberDefinition(
                        "inner1",
                        visibility: MemberVisibility.Mountable,
                        ownEdges: new[]
                        {
                            ModelKit.Trace(trace, "i1"),
                            ModelKit.Own(static (_, ctx) => ctx.Stop()),
                            ModelKit.Trace(trace, "i1-after"),
                        }),
                    new MemberDefinition(
                        "inner2",
                        visibility: MemberVisibility.Mountable,
                        ownEdges: new[] { ModelKit.Trace(trace, "i2") }),
                },
                mounts: new[]
                {
                    new MountDeclaration("engine#outer", "inner1"),
                    new MountDeclaration("engine#outer", "inner2"),
                }));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.stop", "组件", host);
        runtime.Bind(component);

        await runtime.RunMemberAsync(runtime.Engine, "outer");

        // inner1 的剩余自有边被停止；调用方（outer）的后续边 i2 照常执行（停止不外溢）。
        Assert.Equal(new[] { "i1", "i2" }, trace);
    }

    [Fact]
    public void 包装目标只能是成员地址_二级地址无法解析()
    {
        // 包装声明可寻址到的只是"成员"：不存在"成员＋来源"的二级地址概念。
        // `engine#damage-apply#on-before` 按第一个 '#' 切分后，成员名整体是 `damage-apply#on-before`——
        // 成员不存在 ⇒ 装载期拒绝（不会把后半段当成"来源"解析）。
        var secondary = new WrapDeclaration(
            WrapTarget.ForMember(MemberAddress.Parse("engine#damage-apply#on-before")),
            WrapperPhase.Before,
            "h.any");

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(new MemberDefinition("damage-apply", ViewSpec.Empty, MemberVisibility.All)))
            .Add(new PrefabDefinition("comp.wrap", EntityKind.Component, wraps: new[] { secondary }));

        var handlers = new HandlerRegistry();
        handlers.Register("h.any", static (_, _, _) => Task.CompletedTask);
        var runtime = new OrchestrationRuntime(catalog, handlers);

        var error = Assert.Throws<KeyNotFoundException>(() => runtime.CreateEntity("comp.wrap", "组件"));
        Assert.Contains("engine#damage-apply#on-before", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 具名缝挂在成员目标上_不引入二级地址且与成员外围包装不共键()
    {
        // R1i 修订 2026-10-29（用户裁决）：seam 恢复，不是退役。
        // 形态未变：包装目标仍是**成员**（无"成员＋来源"的二级地址）；缝名是包装声明上的一个附加维度，
        // 只在成员外围包装之外多出一个"handler 内部的插入点"口径。
        var address = MemberAddress.Parse("engine#flow");
        var seamWrap = WrapDeclaration.ForSeam("engine#flow", "指向后", "h.wrap");

        Assert.Equal("指向后", seamWrap.Seam);
        Assert.Equal(WrapperPhase.Before, seamWrap.Phase);
        Assert.Equal(address, seamWrap.Target.Address);
        Assert.True(seamWrap.Target.IsMember);
        Assert.NotEqual(WrapTarget.ForMember(address).Key, seamWrap.Target.Key);

        // 成员外围包装：缝为 null（走 phase 语义），键与旧口径一致。
        var outerWrap = WrapDeclaration.ForMember("engine#flow", WrapperPhase.After, "h.outer");
        Assert.Null(outerWrap.Seam);
        Assert.Equal(WrapTarget.ForMember(address).Key, outerWrap.Target.Key);

        // 包装器不携带"成员＋来源"的二级地址：只有目标＋相位＋资产（＋缝名这一维度）。
        Assert.Equal(
            new[] { "HandlerId", "Phase", "Seam", "Target" },
            typeof(WrapDeclaration).GetProperties().Select(static p => p.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task 判定器包装器与成员包装器共用同一相位语义与同一注册面()
    {
        var handlers = new HandlerRegistry();
        var trace = new List<string>();
        var runtime = BuildRuntime(trace, handlers);

        var judicator = WrapTarget.ForJudicator("伤害判定");
        Assert.False(judicator.IsMember);

        runtime.Wraps.Register(
            new WrapDeclaration(judicator, WrapperPhase.Before, "h.j1"),
            TraceHandler(handlers, trace, "j1"),
            new DeclarationOrder(0, 0, 0));
        runtime.Wraps.Register(
            new WrapDeclaration(judicator, WrapperPhase.Before, "h.j2"),
            TraceHandler(handlers, trace, "j2"),
            new DeclarationOrder(0, 1, 0));

        // 同一注册面、同一相位语义（before＝FIFO，R1i 修订）。
        var order = runtime.Wraps.BeforeExecutionOrder(judicator);
        Assert.Equal(new[] { "h.j1", "h.j2" }, order.Select(static e => e.Handler.Id).ToArray());
    }

    [Fact]
    public async Task host作用域的包装在宿主成员执行时生效()
    {
        var trace = new List<string>();
        var handlers = new HandlerRegistry();

        // 包装键在执行期按"目标实体自身的地址"（self#played）比对：
        // `host#played` 的声明必须归一化为宿主实体地址，否则会静默不生效。
        TraceHandler(handlers, trace, "host-before");

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab())
            .Add(ModelKit.CardBase(new MemberDefinition(
                "played",
                ViewSpec.Empty,
                MemberVisibility.All,
                new[] { ModelKit.Trace(trace, "host-owned") })))
            .Add(new PrefabDefinition(
                "comp.wraps-host",
                EntityKind.Component,
                wraps: new[]
                {
                    WrapDeclaration.ForMember("host#played", WrapperPhase.Before, "h.host-before"),
                }));

        var runtime = new OrchestrationRuntime(catalog, handlers);
        var host = runtime.CreateEntity("card.base", "卡");
        runtime.CreateEntity("comp.wraps-host", "组件", host);

        await runtime.RunMemberAsync(host, "played");

        Assert.Equal(new[] { "host-before", "host-owned" }, trace);
    }

    [Fact]
    public void 包装声明校验失败时不留半登记()
    {
        var handlers = new HandlerRegistry();
        handlers.Register("h.ok", static (_, _, _) => Task.CompletedTask);

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(
                new MemberDefinition("flow", ViewSpec.Empty, MemberVisibility.All),
                new MemberDefinition("private", ViewSpec.Empty, MemberVisibility.None)))
            .Add(new PrefabDefinition(
                "comp.bad-second-wrap",
                EntityKind.Component,
                wraps: new[]
                {
                    WrapDeclaration.ForMember("engine#flow", WrapperPhase.Before, "h.ok"),
                    WrapDeclaration.ForMember("engine#private", WrapperPhase.Before, "h.ok"), // 未声明可作宿主
                }));

        var runtime = new OrchestrationRuntime(catalog, handlers);

        Assert.Throws<PermissionDeniedException>(() => runtime.CreateEntity("comp.bad-second-wrap", "组件"));

        // 第一条合法包装同样不登记：失败＝拒绝装载，不留半登记。
        Assert.Empty(runtime.Wraps.Entries(WrapTarget.ForMember(MemberAddress.Parse("engine#flow"))));
    }
}
