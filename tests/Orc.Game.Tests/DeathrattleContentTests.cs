using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 亡计内容结算（§29 第 1 项）：内容＝**卡实体上的 v4 组件**（<c>one-shot: true</c> ＋ 成员 <c>settle</c>
/// ＋ <c>mounts: engine#card.dying</c>），执行体是这个成员 handler。
/// <para>
/// 本组用例取代 §28.5 第 6 条里被删掉的 13 条"亡计结算"用例，逐条对回**行为契约**而不是旧实现形态：
/// ①结算时点（置毁之后、词条注销／修饰清理之前，可观察状态清单）；②恰一次；③撤销后不结算；
/// ④无内容静默；⑤非战斗死亡同源；⑥内容异常隔离且死亡流程继续；⑦再触发驱动的是**同一个成员**。
/// </para>
/// </summary>
public class DeathrattleContentTests
{
    /// <summary>结算时点观察（＝旧「死亡前可观察状态」清单的 v4 形态）。</summary>
    private sealed class Observation
    {
        public bool KeywordActive { get; init; }
        public bool ModifierEffective { get; init; }
        public bool ContentMounted { get; init; }
        public bool LineReadable { get; init; }
        public bool SlotFreed { get; init; }
        public bool Destroyed { get; init; }
        public bool EngineSame { get; init; }
        public bool PayloadCardSame { get; init; }
    }

    /// <summary>
    /// 在卡实体上装配一个亡计内容组件（测试用最小形态）：
    /// 成员 <c>settle</c>（可调用＋可挂载）、可选挂载 <c>engine#card.dying</c>、可选一次性。
    /// </summary>
    private static ComponentAttachment AddContent(
        Match match,
        UnitCard unit,
        string id,
        Action<Context> onSettle,
        bool oneShot = true,
        bool mountOnDeathSignal = true)
    {
        match.EngineRuntime.Catalog.Add(new PrefabDefinition(
            id,
            EntityKind.Component,
            layer: PrefabLayer.Asset,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition(
                    DeathrattleRules.ContentMember,
                    ViewSpec.Empty,
                    MemberVisibility.Callable | MemberVisibility.Mountable,
                    ownEdges: new[]
                    {
                        new OwnedEdge((KernelHandler)((_, ctx, _) =>
                        {
                            onSettle(ctx);
                            return Task.CompletedTask;
                        })),
                    }),
            },
            mounts: mountOnDeathSignal ? new[] { DeathrattleRules.ContentMount() } : Array.Empty<MountDeclaration>(),
            activeWhen: ActiveWhen.Loaded,
            oneShot: oneShot));

        var entity = unit.ModelEntity
            ?? throw new InvalidOperationException($"单位 '{unit.Name}' 没有编排实体（卡牌↔实体桥未建立）。");

        return match.EngineRuntime.AddComponent(entity, id, $"{id}@{entity.Name}");
    }

    // ==================== ① 结算时点（主用例） ====================

    [Fact]
    public async Task Content_Component_Settles_Before_Cleanup_With_Observable_State()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1); // 攻 1 / 防 2
        var victimLine = match.Battlefield.GetSupportLine(playerB);

        // 前置：亡计词条（授予链）＋ 一项修饰（「修饰仍生效」证据）。
        Assert.True(await victim.Keywords.GrantAsync(KeywordIds.Deathrattle));
        await victim.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 4, new object()));

        var observations = new List<Observation>();
        AddContent(match, victim, "test.deathrattle.timing", ctx =>
        {
            var state = victim.GetData<UnitStateData>();
            observations.Add(new Observation
            {
                KeywordActive = victim.Keywords.Has(KeywordIds.Deathrattle),
                ModifierEffective = victim.Modifiers.GetEffectiveValue(CardStatFields.Attack) == 5, // 1 基准 + 4
                ContentMounted = DeathrattleRules.HasContent(victim),
                LineReadable = ReferenceEquals(state.Line, victimLine),
                SlotFreed = !match.Battlefield.GetSupportLine(playerB).Contains(victim),
                Destroyed = state.IsDestroyed,
                EngineSame = ReferenceEquals(ctx.Engine, match.Engine),
                PayloadCardSame = ctx.TryGet(GameUpdates.PayloadCard, out var card) && ReferenceEquals(card, victim),
            });
        });

        using var recorder = new UpdateRecorder(match.Engine);
        await victim.ApplyDefenseDamageAsync(2); // 防御归零 → 统一死亡流程

        // 时序：结算发生在置毁之后、词条注销／修饰清理之前。
        var observation = Assert.Single(observations);
        Assert.True(observation.KeywordActive);
        Assert.True(observation.ModifierEffective);
        Assert.True(observation.ContentMounted);
        Assert.True(observation.LineReadable);
        Assert.True(observation.SlotFreed);
        Assert.True(observation.Destroyed);
        Assert.True(observation.EngineSame);
        Assert.True(observation.PayloadCardSame); // card.dying 的载荷（Card）进入内容成员的数据面

        // 死亡完成（清理落定；card.died 恰一次）。
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.True(victim.Keywords.Has(KeywordIds.Deathrattle)); // 登记保留
        Assert.Empty(victim.Keywords.Components);                 // 行为态清空（注销完成）
        Assert.Null(victim.GetData<UnitStateData>().Line);        // 位置字段置空（终态）
    }

    // ==================== ①b 帧入口语义：ctx.TriggerCard 从载荷写入 ====================

    /// <summary>
    /// §28.5 第 1 条的反向守护：`SignalBridge.EmitAsync` 从载荷的
    /// <see cref="Orc.Core.TriggerPayloadKeys.TriggerCard"/> 键写入帧——订阅体里 `ctx.TriggerCard` 可读。
    /// （此前只有"帧间继承"没有写入点，导致恒 null；本用例是该写入点的唯一断言。）
    /// </summary>
    [Fact]
    public async Task Emit_Writes_TriggerCard_From_Payload()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[1], CommandTestKit.WeakId, 1);
        Assert.True(await unit.Keywords.GrantAsync(KeywordIds.Deathrattle));

        Orc.Cards.Card? observed = null;
        AddContent(match, unit, "test.deathrattle.triggercard", ctx => observed = ctx.TriggerCard);

        await match.Engine.Emit(
            GameUpdates.CardDying,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = unit,
                [Orc.Core.TriggerPayloadKeys.TriggerCard] = unit,
            });

        Assert.Same(unit, observed);
    }

    // ==================== ② 恰一次（信号路径重入） ====================

    [Fact]
    public async Task Content_Settles_Exactly_Once_Per_Component()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[1], CommandTestKit.WeakId, 1);
        Assert.True(await victim.Keywords.GrantAsync(KeywordIds.Deathrattle));

        var executions = 0;
        AddContent(match, victim, "test.deathrattle.once", _ => executions++);

        // 第一次结算（死亡信号）＋同一次死亡里的兜底驱动（死亡链 ③ 步）＋一次重复发射：
        // 三条驱动路径都指向同一成员，一次性额度保证只执行一次。
        await victim.ApplyDefenseDamageAsync(2);
        Assert.Equal(1, executions);

        await match.Engine.Emit(
            GameUpdates.CardDying,
            new Dictionary<string, object?> { ["Card"] = victim });
        Assert.Equal(1, executions); // 一次性额度已消费：再发一次也不执行
    }

    // ==================== ③ 撤销后不结算 ====================

    [Fact]
    public async Task Revoked_Content_Does_Not_Settle()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[1], CommandTestKit.WeakId, 1);
        Assert.True(await victim.Keywords.GrantAsync(KeywordIds.Deathrattle));

        var executions = 0;
        var content = AddContent(match, victim, "test.deathrattle.revoked", _ => executions++);
        content.Dispose(); // 撤销内容（组件实体销毁、边逐句柄撤销）

        Assert.False(DeathrattleRules.HasContent(victim));

        await victim.ApplyDefenseDamageAsync(2); // 致死

        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(0, executions);
    }

    // ==================== ④ 无内容静默 ====================

    [Fact]
    public async Task No_Content_Is_Silent_On_Death()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[1], CommandTestKit.WeakId, 1);
        Assert.True(await victim.Keywords.GrantAsync(KeywordIds.Deathrattle));
        Assert.False(DeathrattleRules.HasContent(victim));

        using var recorder = new UpdateRecorder(match.Engine);
        await victim.ApplyDefenseDamageAsync(2);

        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Empty(victim.Keywords.Components);
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.DoesNotContain(
            match.Engine.RootStream.Entries,
            e => e.Source == "亡计" && e.Keywords.Contains("error"));
    }

    // ==================== ⑤ 结算点＝死亡，不是"卡对象销毁" ====================

    /// <summary>
    /// 结算挂在 <c>card.dying</c>（死亡链里"置毁已置、清理未做"的那个点），**不是** <c>card.destroyed</c>：
    /// 弃置／爆牌／回库回收走 <c>Engine.DestroyCard</c>，那不是死亡，亡计不该结算。
    /// 本用例同时是对"内容挂到 card.destroyed"这条错配的绊线（那条边在单位死亡路径上根本不会触发）。
    /// </summary>
    [Fact]
    public async Task Content_Does_Not_Settle_On_CardObject_Destruction()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[1], CommandTestKit.WeakId, 1);
        Assert.True(await unit.Keywords.GrantAsync(KeywordIds.Deathrattle));

        var executions = 0;
        AddContent(match, unit, "test.deathrattle.destroy", _ => executions++);

        await match.Engine.DestroyCard(unit); // 销毁卡对象（非死亡）

        // 销毁确实发生了（card.destroyed 已发射），但亡计内容不结算。
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Kind == LogEntryKind.Update && e.Message == Orc.Core.Updates.CardDestroyed);
        Assert.Equal(0, executions);
    }

    // ==================== ⑥ 异常隔离、死亡继续 ====================

    [Fact]
    public async Task Content_Exception_Is_Isolated_And_Cleanup_Continues()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[1], CommandTestKit.WeakId, 1);
        Assert.True(await victim.Keywords.GrantAsync(KeywordIds.Deathrattle));

        AddContent(match, victim, "test.deathrattle.explode",
            _ => throw new InvalidOperationException("亡计爆炸（测试）"));

        using var recorder = new UpdateRecorder(match.Engine);
        await victim.ApplyDefenseDamageAsync(2); // 致死：结算抛错

        // 隔离口径：内容抛错不打断死亡链，死亡必须完成（无半死状态）。
        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Null(victim.GetData<UnitStateData>().Line);
        Assert.Empty(victim.Keywords.Components);
        Assert.True(victim.Keywords.Has(KeywordIds.Deathrattle));
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);

        // 隔离记录可查（source＝亡计、keywords 带 exception:<类型>）。
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Source == "亡计" && e.Keywords.Contains("exception:InvalidOperationException"));
    }

    // ==================== ⑦ 再触发＝同一成员 ====================

    [Fact]
    public async Task Retrigger_Drives_The_Same_Content_Member_Once()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.InfantryId, 1);
        Assert.True(await unit.Keywords.GrantAsync(KeywordIds.Deathrattle));

        var executions = 0;
        AddContent(match, unit, "test.deathrattle.retrigger", _ => executions++);

        // 单源：再触发驱动的是同一个成员（不另开第二份内容实现），且不发链级死亡信号。
        using var recorder = new UpdateRecorder(match.Engine);
        var accepted = await RetriggerRules.RequestDeathrattleAsync(unit);

        Assert.True(accepted);
        Assert.Equal(1, executions);
        Assert.DoesNotContain(GameUpdates.CardDied, recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardDying, recorder.Types);
    }
}
