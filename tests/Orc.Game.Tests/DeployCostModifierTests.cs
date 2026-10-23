using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W3-2 G5 部署费修饰与有效费用——四场景测试：
/// ②设值（花费为 0；手牌中的卡即生效——机制级读取口＋真实打出路径消费「扣 0」）；
/// ③钳制+组（-2 但下限 1；组注册/注销——原子一次衔接、无中间态；边界＝等于下限零发射；真实单位部署消费）；
/// ④按费用筛选（花费 ≤3 消费有效费用读取口——机制级谓词＋随动；经 Targeter 两级筛选流程）；
/// 附：反制路径（评估〔EvaluateUse〕＋翻转〔激活读有效值扣点、取消按实扣额退还——「扣点与退点同额」跨调用守恒，
/// 含修饰漂移增/减两方向边界〕＋承载〔CounterUseTrigger→EvaluateUse〕）统一读有效部署费。
/// 「6 处读取接改」触达：CostCheckTrigger（②③真实校验）／UnitCard 扣费（③真实部署）／OrderCard 扣费（②）／
/// CounterCard 评估与翻转＋CounterUseTrigger 承载（附）。
/// </summary>
public class DeployCostModifierTests
{
    /// <summary>构造指定部署费的指令卡（机制级最小状态——独立构造；部署费域构造期常驻）。</summary>
    private static OrderCard CreateDeployCostCard(LogicEngine engine, int deployCost)
        => new(engine, new CardDefinition(
            $"费用{deployCost}指令", deployCost, 0, 0, 0, CardCategory.Command,
            faction: Faction.Germany, rarity: Rarity.Standard));

    // ---------- ② 设值（花费为 0；手牌中的卡即生效） ----------

    [Fact]
    public async Task SetValue_Zero_In_Hand_Takes_Effect_And_Is_Consumed_By_Play()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, player, PlayChainTestKit.CommandCostlyId); // 部署费 5
        using var recorder = new UpdateRecorder(match.Engine);
        var source = new object();

        // 未修饰：点数 1 < 5 → 预打出拒绝（留手、零副作用）
        var rejected = await match.PlayManager.PlayOrderAsync(command);
        Assert.Equal(PlayResultStatus.Failed, rejected.Status);
        Assert.Equal(PlayFailureReason.PrePlayPointShortage, rejected.FailureReason);
        Assert.Equal(1, player.Points);
        Assert.Contains(command, player.Hand);

        // 设值 0（「花费为 0」）：手牌中的卡即生效（仅凭挂载动作——无需打出/移动）
        await command.Modifiers.AddModifierAsync(new SetModifier(CardStatFields.DeployCost, 0, source));
        Assert.Equal(0, command.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var update = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(new[] { CardStatFields.DeployCost }, ModifierTestKit.ChangedFieldsOf(update.Payload));

        // 真实路径消费有效值：预打出校验（1 ≥ 0）＋打出复验通过 → 扣费读有效值（扣 0——点数保持 1）→ 离手
        // （若读基准 5：校验拒绝；若读实时值扣费：1 − 5 为负——两者均可判别）
        recorder.Clear();
        var played = await match.PlayManager.PlayOrderAsync(command);
        Assert.Equal(PlayResultStatus.Success, played.Status);
        Assert.Equal(1, player.Points);
        Assert.DoesNotContain(command, player.Hand);

        // 撤销：有效回弹 5（读取口径闭环）
        await command.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(5, command.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
    }

    // ---------- ③ 钳制+组（-2 但下限 1；组注册/注销） ----------

    [Fact]
    public async Task Clamp_And_Group_Registration_And_Unregistration()
    {
        var engine = new LogicEngine();
        var card = ModifierTestKit.CreateOrderCard(engine); // 部署费 2（基准）
        using var recorder = new UpdateRecorder(engine);
        var groupSource = new object();

        // 组注册（原子、一次衔接）：[-2 ＋ 下限 1] → 2 − 2 = 0 → 钳制抬升 → 1；
        // 恰一条集中触发（无「仅 -2 已生效」的中间态——逐条衔接将发射两条/中间值 0）
        await card.Modifiers.AddModifiersAsync(new Modifier[]
        {
            new AddModifier(CardStatFields.DeployCost, -2, groupSource),
            new MinClampModifier(CardStatFields.DeployCost, 1, groupSource),
        });
        Assert.Equal(1, card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        Assert.Equal(2, card.Modifiers.All.Count);
        var registered = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(new[] { CardStatFields.DeployCost }, ModifierTestKit.ChangedFieldsOf(registered.Payload));

        // 组注销（按来源整组撤销、一次衔接）：-2 与钳制同时消失 → 有效回落 2
        recorder.Clear();
        await card.Modifiers.RemoveBySourceAsync(groupSource);
        Assert.Empty(card.Modifiers.All);
        Assert.Equal(2, card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var unregistered = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(new[] { CardStatFields.DeployCost }, ModifierTestKit.ChangedFieldsOf(unregistered.Payload));

        // 边界：等于下限＝不产生变化 → 零发射（基线 1：1 − 2 = −1 → 钳制 1 == 基准）
        var lowCard = CreateDeployCostCard(engine, 1);
        recorder.Clear();
        await lowCard.Modifiers.AddModifiersAsync(new Modifier[]
        {
            new AddModifier(CardStatFields.DeployCost, -2, new object()),
            new MinClampModifier(CardStatFields.DeployCost, 1, new object()),
        });
        Assert.Equal(1, lowCard.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder)); // 等于下限：无变化零发射
    }

    [Fact]
    public async Task Clamped_Deploy_Cost_Consumed_By_Real_Unit_Deployment()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge, extraDefinitions: new[]
        {
            new CardDefinitionEntry("w3_deploy2", new CardDefinition(
                "部署费2单位", 2, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
                faction: Faction.Germany, rarity: Rarity.Standard)),
        });
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, "w3_deploy2", toHand: true);
        var source = new object();

        // 未修饰：点数 1 < 2 → 预打出拒绝（留手）
        var rejected = await match.PlayManager.BeginUnitPrePlayAsync(unit);
        Assert.Equal(PlayResultStatus.Failed, rejected.Status);
        Assert.Equal(PlayFailureReason.PrePlayPointShortage, rejected.FailureReason);
        Assert.Equal(1, player.Points);

        // 组修饰 [-2 ＋ 下限 1]：有效 1（钳制可见：2 − 2 = 0 → 抬升 1）
        await unit.Modifiers.AddModifiersAsync(new Modifier[]
        {
            new AddModifier(CardStatFields.DeployCost, -2, source),
            new MinClampModifier(CardStatFields.DeployCost, 1, source),
        });
        Assert.Equal(1, unit.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));

        // 真实部署：预打出校验＋打出复验读有效 1 → 通过；扣费读有效 1（点数 1 → 0；读基准 2 会扣成负）
        bridge.CollectScript = PlayChainTestKit.AllSlotsCandidatesScript(match);
        var task = match.PlayManager.BeginUnitPrePlayAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(
                TargeterTestKit.PrimarySlot(description), match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0).Ref)));
        var deployed = await task;

        Assert.Equal(PlayResultStatus.Success, deployed.Status);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(unit, player.Hand);
        Assert.True(unit.TryGetData<UnitStateData>(out _));
    }

    // ---------- 反制路径（评估＋翻转＋承载——统一读有效部署费） ----------

    [Fact]
    public async Task Counter_Evaluation_Toggle_And_Validation_Read_Effective_Cost()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, player, PlayChainTestKit.CounterCostlyId); // 部署费 3
        var source = new object();

        // 未修饰：点数 1 < 3 → 评估拒绝（前置判定与触发器验证同源）
        var rejected = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Failed, rejected.Status);
        Assert.Equal(PlayFailureReason.CounterPointShortage, rejected.FailureReason);
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(1, player.Points);

        // 降费 -2：有效 1 → 评估与翻转（经使用反制触发器验证＝承载路径）读同一有效值：激活扣 1（点数 0）
        await counter.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.DeployCost, -2, source));
        Assert.Equal(1, counter.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var activated = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, activated.Status);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(0, player.Points); // 扣点读有效值 1（读实时值会扣 3）

        // 取消：退点＝激活实扣额（跨调用守恒）→ 点数回 1
        var deactivated = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, deactivated.Status);
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(1, player.Points);

        // 撤销修饰 → 有效回 3：评估/验证再拒绝（回弹；验证路径读「有效值」的防旁路对照）
        await counter.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(3, counter.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var again = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Failed, again.Status);
        Assert.Equal(PlayFailureReason.CounterPointShortage, again.FailureReason);
    }

    [Fact]
    public async Task Counter_Refund_Equals_Charged_Amount_Despite_Modifier_Drift()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, player, PlayChainTestKit.CounterCostlyId); // 部署费 3
        var discountSource = new object();
        var surgeSource = new object();

        // 方向一（升费漂移）：降费 -2（有效 1）→ 激活扣 1（实扣额 ＝ 1）
        await counter.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.DeployCost, -2, discountSource));
        var activated = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, activated.Status);
        Assert.Equal(0, player.Points);

        // 激活期间升费（+2 → 有效 3）：取消退点＝激活实扣额（1，而非漂移后 3）——「扣点与退点同额」跨调用守恒
        await counter.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.DeployCost, 2, surgeSource));
        Assert.Equal(3, counter.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var deactivated = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, deactivated.Status);
        Assert.Equal(1, player.Points); // 0 ＋ 1（实扣额）——非 0 ＋ 3
        Assert.False(counter.GetData<CounterActivationData>().IsActive);

        // 方向二（减费漂移）：清理修饰回有效 3；推进到 A 回合 5（点数 3）→ 激活扣 3（实扣额 ＝ 3）
        await counter.Modifiers.RemoveBySourceAsync(discountSource);
        await counter.Modifiers.RemoveBySourceAsync(surgeSource);
        Assert.Equal(3, counter.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        await match.EndTurn();
        await match.EndTurn();
        await match.EndTurn();
        await match.EndTurn(); // 回合 5：A 点数 3
        Assert.Equal(3, player.Points);
        var reactivated = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, reactivated.Status);
        Assert.Equal(0, player.Points);

        // 激活期间减费（设值 0 → 有效 0）：取消退点＝实扣额（3，而非漂移后 0）——点数守恒
        await counter.Modifiers.AddModifierAsync(new SetModifier(CardStatFields.DeployCost, 0, discountSource));
        Assert.Equal(0, counter.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var finalDeactivate = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, finalDeactivate.Status);
        Assert.Equal(3, player.Points); // 0 ＋ 3（实扣额）——非 0 ＋ 0
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
    }

    // ---------- ④ 按费用筛选（消费有效费用读取口） ----------

    [Fact]
    public async Task Cost_Filter_Predicate_Consumes_Effective_Deploy_Cost()
    {
        var engine = new LogicEngine();
        var cardCost1 = CreateDeployCostCard(engine, 1);
        var cardCost3 = CreateDeployCostCard(engine, 3);
        var cardCost4 = CreateDeployCostCard(engine, 4);
        var cardCost5 = CreateDeployCostCard(engine, 5);

        // 「消灭花费 ≤3」类效果可用：筛选谓词消费同一有效费用读取口（含边界——恰等入选、超一排除）
        static IReadOnlyList<OrderCard> Filter(IEnumerable<OrderCard> cards)
            => cards.Where(c =>
                c.Modifiers.GetEffectiveValue(CardStatFields.DeployCost) <= 3).ToList();

        var cards = new[] { cardCost1, cardCost3, cardCost4, cardCost5 };
        Assert.Equal(new OrderCard[] { cardCost1, cardCost3 }, Filter(cards).ToArray());

        // 随动：修饰使有效部署费改变（4 → 3）→ 同一筛选读取即时反映；撤销 → 回弹排除
        var source = new object();
        await cardCost4.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.DeployCost, -1, source));
        Assert.Equal(new OrderCard[] { cardCost1, cardCost3, cardCost4 }, Filter(cards).ToArray());
        await cardCost4.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(new OrderCard[] { cardCost1, cardCost3 }, Filter(cards).ToArray());
    }

    [Fact]
    public async Task Cost_Filter_Through_Targeter_Two_Stage_Filtering_With_FollowUp()
    {
        var engine = new LogicEngine();
        var cardCost1 = CreateDeployCostCard(engine, 1);
        var cardCost3 = CreateDeployCostCard(engine, 3);
        var cardCost4 = CreateDeployCostCard(engine, 4);

        // 粗筛＝批量；细筛＝逐项谓词——谓词内消费有效费用读取口（「花费 ≤3」筛选随流程生效）
        var filter = new TargetFilter(
            coarseFilter: refs => refs,
            fineFilter: reference => reference.Value is CardBase card
                && card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost) <= 3);
        TargetingRequestDescription? seen = null;
        var bridge = new MockTargeterBridge
        {
            CollectScript = _ => Task.FromResult(TargeterTestKit.Candidates(
                cardCost1.Ref, cardCost3.Ref, cardCost4.Ref)),
            InteractionScript = (description, responder) =>
            {
                seen = description;
                responder.Complete(
                    description.RequestId,
                    TargeterTestKit.Selection(
                        TargeterTestKit.PrimarySlot(description), description.AllowedTargets[0]));
            },
        };
        var manager = new TargeterManager(bridge);

        // 初始：4 被排除（超一排除；1/3 入选——3 恰等入选）
        var first = await manager.CreateTargeter(filter).Targeting();
        Assert.Equal(TargeterStatus.Ok, first.Status);
        Assert.Equal(new Ref<Entity>[] { cardCost1.Ref, cardCost3.Ref }, seen!.AllowedTargets.ToArray());

        // 随动：4 → 3（修饰）→ 同一流程内筛选即时入选；撤销 → 回弹排除
        var source = new object();
        await cardCost4.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.DeployCost, -1, source));
        var second = await manager.CreateTargeter(filter).Targeting();
        Assert.Equal(TargeterStatus.Ok, second.Status);
        Assert.Equal(
            new Ref<Entity>[] { cardCost1.Ref, cardCost3.Ref, cardCost4.Ref },
            seen!.AllowedTargets.ToArray());

        await cardCost4.Modifiers.RemoveBySourceAsync(source);
        var third = await manager.CreateTargeter(filter).Targeting();
        Assert.Equal(TargeterStatus.Ok, third.Status);
        Assert.Equal(new Ref<Entity>[] { cardCost1.Ref, cardCost3.Ref }, seen!.AllowedTargets.ToArray());
    }
}
