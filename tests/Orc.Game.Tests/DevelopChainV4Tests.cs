using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// C2「开发/发现」链的 v4 形态（§29 第 3 项；原 <c>C2DevelopChainTests</c> 11 条的重建）。
/// <para>
/// 旧链的驱动件是 <c>C2Kit</c> 里那个 <c>CardEffect</c> 子类（<c>CastAsync</c> 里跑"取样 → 出题 → 提交 → 生成 → 落位"），
/// 随效果家族删除。新模型里"效果"＝**组件成员**：本组用例把同一条链放进组件的 <c>develop</c> 成员，
/// 交互仍走唯一提交通道（<see cref="TargeterManager"/> ＋ <c>cardPicker</c> 选择器），生成仍走
/// <see cref="MatchCardService"/>——三段都还是生产面，没有测试专用旁路。
/// </para>
/// <para>
/// **覆盖面缩减（如实记录）**：旧 11 条里的"同种子复现／换种子变化"两条依赖旧效果对象的取样记录读面
/// （<c>LastDrawnIds</c>/<c>LastPool</c>）；新模型没有"效果对象"，取样由调用方（组件 handler）自己持有，
/// 随机选择器本身的可复现性已由 <c>EffectSelectorChannelTests</c> 在随机服务层覆盖。
/// 本组保留三条核心契约：全链、取消零副作用、池不足链级终止。
/// </para>
/// </summary>
public class DevelopChainV4Tests
{
    private const string SlotName = "develop";

    /// <summary>装一个"开发"组件（成员 <c>develop</c>：取样 → 出题 → 生成落位）。</summary>
    private static ComponentAttachment AddDevelopComponent(
        Match match, UnitCard host, string id, int offerCount)
    {
        OwnedEdge develop = new((KernelHandler)(async (_, ctx, ct) =>
        {
            var owner = host.Owner!;
            var service = owner.CardService!;

            // ① 取样：池＝本局卡牌库已注册定义全体（只读枚举面），取前 N 项（互异）。
            var offers = service.RegisteredDefinitions.Keys
                .OrderBy(static key => key, StringComparer.Ordinal)
                .Take(offerCount)
                .ToArray();

            // ② 池不足（M=0）：链级终止——不构造选择器、零副作用、不抛。
            if (offers.Length == 0)
            {
                ctx.Set("develop.selected", null);
                return;
            }

            // ③ 出题＋提交：唯一提交通道（cardPicker 非引用候选，按标识选择）。
            IReadOnlyList<string>? chosen = null;
            await match.TargeterManager.RunAsync(async flow =>
            {
                var step = await flow.Step(
                    SelectorTemplates.CardPickerList,
                    new IdentifierSetParameter(offers, 1, 1)
                    {
                        Owner = owner,
                        Action = SelectorActions.Effect,
                    });

                if (step.IsCancelled)
                {
                    return TargeterResult.Cancelled();
                }

                if (step.IsFailed)
                {
                    return TargeterResult.FromSelectorFailure(step.Failure);
                }

                chosen = step.Value;
                return TargeterResult.Ok();
            });

            if (chosen is null || chosen.Count == 0)
            {
                ctx.Set("develop.selected", null); // 取消／失败：零副作用
                return;
            }

            var picked = chosen[0];
            ctx.Set("develop.selected", picked);

            // ④ 生成＋落位（构筑外实例、进手牌）。
            await service.CreateAndPlaceToHandAsync(picked, owner, ct);
        }));

        match.EngineRuntime.Catalog.Add(new PrefabDefinition(
            id,
            EntityKind.Component,
            layer: PrefabLayer.Asset,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition(
                    "develop",
                    ViewSpec.Empty,
                    MemberVisibility.Callable | MemberVisibility.Mountable,
                    ownEdges: new[] { develop }),
            },
            activeWhen: ActiveWhen.Loaded));

        var entity = host.ModelEntity
            ?? throw new InvalidOperationException($"单位 '{host.Name}' 没有编排实体。");

        return match.EngineRuntime.AddComponent(entity, id, $"{id}@{entity.Name}");
    }

    private static Task<MemberExecutionResult> RunDevelopAsync(Match match, ComponentAttachment component)
        => match.EngineRuntime.RunMemberAsync(
            component.Component, "develop", new Context(), match.Engine, CancellationToken.None);

    // ==================== ① 全链：三选一 → 提交 → 生成落位 ====================

    [Fact]
    public async Task Develop_Offers_Distinct_Pool_Cards_Then_Generates_Chosen_To_Hand()
    {
        var bridge = new MockTargeterBridge();
        string? offeredChosen = null;
        IReadOnlyList<string> offered = Array.Empty<string>();
        bridge.InteractionScript = (description, responder) =>
        {
            var slot = Assert.Single(description.Slots);
            offered = slot.CardListings!.Select(static listing => listing.Id).ToArray();
            offeredChosen = offered[1]; // 固定选择（不消费随机）
            responder.Complete(description.RequestId, TargeterTestKit.IdentifierSelection(slot.Name, offeredChosen));
        };

        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var effect = AddDevelopComponent(match, host, "test.develop", offerCount: 3);

        var handBefore = playerA.Hand.Count;
        using var recorder = new UpdateRecorder(match.Engine);

        await RunDevelopAsync(match, effect);

        // 取样证据：三张互异、且都来自本局卡牌库。
        Assert.Equal(3, offered.Count);
        Assert.Equal(offered.Count, offered.Distinct().Count());
        Assert.All(offered, id => Assert.True(match.CardLibrary.Definitions.ContainsKey(id)));

        // 交互两端：请求已发出（走唯一提交通道），选择被接受。
        var begin = Assert.Single(bridge.Begins);
        Assert.Equal(TargetSlotKind.CardPicker, Assert.Single(begin.Slots).Kind);
        Assert.Equal(TargetSlotPresentation.CardArray, begin.Slots[0].Presentation);

        // 生成实例：构筑外、进手牌、信号恰一次。
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
        var generated = Assert.IsType<UnitCard>(playerA.Hand[^1]);
        Assert.True(match.CardLibrary.TryGetRegisteredId(generated.Definition, out var generatedId));
        Assert.Equal(offeredChosen, generatedId);
        Assert.True(match.IsOutsideDeck(generated));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
    }

    // ==================== ② 取消＝零副作用（且不污染下一次执行） ====================

    [Fact]
    public async Task Develop_Cancelled_Leaves_Zero_Side_Effects()
    {
        var bridge = new MockTargeterBridge
        {
            InteractionScript = static (description, responder) => responder.Cancel(description.RequestId),
        };

        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var effect = AddDevelopComponent(match, host, "test.develop.cancel", offerCount: 3);

        var handBefore = playerA.Hand.Count;
        var cardsBefore = match.Engine.Cards.Count;
        using var recorder = new UpdateRecorder(match.Engine);

        await RunDevelopAsync(match, effect);

        Assert.Equal(handBefore, playerA.Hand.Count);
        Assert.Equal(cardsBefore, match.Engine.Cards.Count);      // 未创建任何实例（零残留）
        Assert.Equal(0, recorder.CountOf(Updates.CardDestroyed));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Single(bridge.Begins);                             // 请求确实发出过（取消是交互结果，不是没发起）
    }

    // ==================== ③ 池不足：链级终止（不构造选择器） ====================

    [Fact]
    public async Task Develop_Empty_Pool_Terminates_Without_Targeting()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);

        // offerCount=0＝池不足（等价于"本局卡池里没有可选定义"）。
        var effect = AddDevelopComponent(match, host, "test.develop.empty", offerCount: 0);

        var handBefore = playerA.Hand.Count;
        using var recorder = new UpdateRecorder(match.Engine);

        var execution = await RunDevelopAsync(match, effect); // 不抛错

        Assert.True(execution.Executed);
        Assert.Empty(bridge.Begins);                    // 未构造选择器（链级终止）
        Assert.Equal(handBefore, playerA.Hand.Count);   // 零副作用
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
    }
}
