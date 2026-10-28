using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 卡牌服务的 v4 链（§29 第 2 项；原 <c>CardServiceTests</c> 25 条的重建）。
/// <para>
/// 旧链的驱动件是 <c>CardServiceSceneEffect.CastAsync</c>（<c>CardEffect</c> 子类，随风家族删除）——测试测的其实是
/// "效果执行到卡牌服务"这条组合。新模型里"效果"＝**组件成员**，因此本组用例把驱动件换成组件的 <c>apply</c> 成员：
/// 成员 handler 里调用 <see cref="MatchCardService"/>，与生产（csx 组件的 handler 调服务面）同一形态。
/// </para>
/// <para>
/// 服务面本身**不设复制/转换专属操作**（设计如此，见 `MatchCardService` 文件头）：
/// 复制＝读源卡定义→<c>Create</c>→放置；转换＝离场/销毁→<c>Create</c>→放置。
/// 本组用例按这两条组合断言契约，而不是断言不存在的 API。
/// </para>
/// </summary>
public class CardServiceV4ChainTests
{
    /// <summary>在卡实体上装一个"效果组件"（成员 <c>apply</c>：handler 里跑 <paramref name="body"/>）。</summary>
    private static ComponentAttachment AddEffectComponent(Match match, UnitCard host, string id, Func<MatchCardService, Task> body)
    {
        match.EngineRuntime.Catalog.Add(new PrefabDefinition(
            id,
            EntityKind.Component,
            layer: PrefabLayer.Asset,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition(
                    "apply",
                    ViewSpec.Empty,
                    MemberVisibility.Callable | MemberVisibility.Mountable,
                    ownEdges: new[]
                    {
                        new OwnedEdge((KernelHandler)(async (_, _, _) =>
                            await body(host.Owner!.CardService!))),
                    }),
            },
            activeWhen: ActiveWhen.Loaded));

        var entity = host.ModelEntity
            ?? throw new InvalidOperationException($"单位 '{host.Name}' 没有编排实体。");

        return match.EngineRuntime.AddComponent(entity, id, $"{id}@{entity.Name}");
    }

    /// <summary>执行组件成员（v4 的唯一执行入口：按实体＋成员名跑一次）。</summary>
    private static Task<MemberExecutionResult> RunApplyAsync(Match match, ComponentAttachment component)
        => match.EngineRuntime.RunMemberAsync(
            component.Component, "apply", new Context(), match.Engine, CancellationToken.None);

    // ==================== ① 生成：经创建面 → 构筑外实例 → 落位信号 ====================

    [Fact]
    public async Task Generate_To_Hand_Creates_OutsideDeck_Instance_And_Places()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        CardBase? created = null;
        var effect = AddEffectComponent(match, host, "test.generate.hand", async service =>
        {
            var result = await service.CreateAndPlaceToHandAsync(CommandTestKit.InfantryId, playerA);
            Assert.Equal(CardPlaceStatus.Placed, result.Status);
            created = result.Card;
        });

        using var recorder = new UpdateRecorder(match.Engine);
        var handBefore = playerA.Hand.Count;

        var execution = await RunApplyAsync(match, effect);

        Assert.True(execution.Executed);
        Assert.NotNull(created);
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
        Assert.Same(created, playerA.Hand[^1]);
        Assert.True(match.IsOutsideDeck(created!));           // 运行时创建＝构筑外（旁路生成会让该锚点失效）
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardLoad)); // 生成走完整加载链（组件/词条就绪后才算可用）
    }

    // ==================== ② 生成：放置被拒＝实例回收（无未归属残留） ====================

    [Fact]
    public async Task Generate_Rejected_Placement_Recycles_Created_Instance()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var gap = match.Battlefield.GetSupportLine(playerB).GetInsertionGaps()[0];
        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        CardPlaceResult? first = null;
        CardPlaceResult? second = null;
        var effect = AddEffectComponent(match, host, "test.generate.reject", async service =>
        {
            // 同一空隙连放两次：第二次必然被拒（槽位已被第一次占用）→ 服务侧回收第二个实例。
            first = await service.CreateAndPlaceToSupportLineAsync(CommandTestKit.WeakId, playerB, gap);
            second = await service.CreateAndPlaceToSupportLineAsync(CommandTestKit.WeakId, playerB, gap);
        });

        var cardsBefore = match.Engine.Cards.Count;
        using var recorder = new UpdateRecorder(match.Engine);

        await RunApplyAsync(match, effect);

        Assert.NotNull(first);
        Assert.Equal(CardPlaceStatus.Placed, first!.Status);
        Assert.NotNull(second);
        Assert.Equal(CardPlaceStatus.Rejected, second!.Status);
        Assert.NotNull(second.Card);
        Assert.False(second.Card!.Life.IsAlive);               // 回收＝实例已销毁（生命周期读面）
        Assert.Equal(cardsBefore + 1, match.Engine.Cards.Count); // 只多了成功的那一张（被拒的已回收）
        Assert.Equal(1, recorder.CountOf(Updates.CardDestroyed)); // 回收经销毁面（可观察）
    }

    // ==================== ③ 复制＝定义级新实例（不携带实例状态） ====================

    [Fact]
    public async Task Copy_Is_Definition_Level_New_Instance_Source_Unaffected()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 源卡：敌方场上单位，带实例状态（损伤／修饰／类型增补）——复制不得携带。
        var source = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        await source.ApplyDefenseDamageAsync(1);
        await source.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 5, "copy-source"));
        await source.AddUnitTypeAsync(UnitType.Bomber);

        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        CardBase? copy = null;
        var effect = AddEffectComponent(match, host, "test.copy", async service =>
        {
            // 复制＝读源卡定义 → Create → 放置（服务面不设专属复制操作）。
            Assert.True(service.TryResolveDefinitionId(source, out var definitionId));
            var instance = await service.CreateAsync(definitionId, playerA);
            var placed = await service.PlaceToHandAsync(instance, playerA);
            Assert.Equal(CardPlaceStatus.Placed, placed.Status);
            copy = placed.Card;
        });

        await RunApplyAsync(match, effect);

        Assert.NotNull(copy);
        Assert.NotSame(source, copy);
        Assert.Same(playerA, copy!.Owner);
        Assert.Same(playerB, source.Owner);

        // 静态定义级：新实例未单位化、无修饰、无原实例状态。
        Assert.False(copy.TryGetData<UnitStateData>(out _));
        Assert.Empty(copy.Modifiers.All);
        Assert.True(match.IsOutsideDeck(copy));

        // 源实例状态与位置原样保持。
        var sourceState = source.GetData<UnitStateData>();
        Assert.Equal(1, sourceState.DefenseLoss);
        Assert.Contains(UnitType.Bomber, sourceState.UnitTypes);
        Assert.NotEmpty(source.Modifiers.All);
        Assert.Same(source, match.Battlefield.GetSupportLine(playerB)[1]);
    }

    // ==================== ④ 转换＝离场/销毁 → 同槽新建（静态重建） ====================

    [Fact]
    public async Task Transform_Rebuilds_Static_Instance_At_Same_Slot()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var source = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await source.ApplyDefenseDamageAsync(1);
        await source.AddUnitTypeAsync(UnitType.Bomber);
        var sourceOrdinal = match.Battlefield.GetSupportLine(playerA).IndexOf(source);

        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        CardPlaceResult? outcome = null;
        var effect = AddEffectComponent(match, host, "test.transform", async service =>
        {
            // 转换＝源离场销毁 → 同槽位按新定义新建（服务面无专属转换操作）。
            var target = (UnitCard)await service.CreateAsync(CommandTestKit.BeastId, playerA);
            await match.Engine.DestroyCard(source); // 离场（槽位释放、card.destroyed 留痕）

            // 同槽位＝源离场后空出来的那一格（按序号取，避免用离场前失效的空隙实例）。
            var slot = match.Battlefield.GetSupportLine(playerA).GetInsertionGaps()
                .First(gap => gap.Ordinal == sourceOrdinal);
            outcome = await service.PlaceToSupportLineAsync(target, playerA, slot);
        });

        using var recorder = new UpdateRecorder(match.Engine);
        await RunApplyAsync(match, effect);

        Assert.NotNull(outcome);
        Assert.Equal(CardPlaceStatus.Placed, outcome!.Status);
        var rebuilt = Assert.IsType<UnitCard>(outcome.Card);

        // 同槽位、静态重建（源卡实例状态不随转换带走）。
        Assert.Same(rebuilt, match.Battlefield.GetSupportLine(playerA)[1]);
        Assert.NotSame(source, rebuilt);
        Assert.Equal(0, rebuilt.GetData<UnitStateData>().DefenseLoss);
        Assert.DoesNotContain(UnitType.Bomber, rebuilt.GetData<UnitStateData>().UnitTypes);

        // 源已销毁（离场可见）。
        Assert.Equal(1, recorder.CountOf(Updates.CardDestroyed));
    }

    // ==================== ⑤ 类型增补：受控单入口、恰一次信号、幂等 ====================

    [Fact]
    public async Task Type_Augmentation_Is_Controlled_Single_Entry_And_Idempotent()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);

        var host = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.WeakId, 1);
        var effect = AddEffectComponent(match, host, "test.augment", async _ =>
        {
            await unit.AddUnitTypeAsync(UnitType.Bomber);
            await unit.AddUnitTypeAsync(UnitType.Bomber); // 幂等：不再发信号
        });

        using var recorder = new UpdateRecorder(match.Engine);
        await RunApplyAsync(match, effect);

        Assert.Contains(UnitType.Bomber, unit.GetData<UnitStateData>().UnitTypes);
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitTypesChanged)); // 实际变更恰一次
        Assert.Same(unit, recorder.PayloadOf(GameUpdates.UnitTypesChanged)![GameUpdates.PayloadUnit]);
        Assert.Equal(UnitType.Bomber, recorder.PayloadOf(GameUpdates.UnitTypesChanged)![GameUpdates.PayloadAddedType]);

        // 非法标识＝明确拒绝（受控入口不做静默兜底）。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => unit.AddUnitTypeAsync("未定义类型"));
    }
}
