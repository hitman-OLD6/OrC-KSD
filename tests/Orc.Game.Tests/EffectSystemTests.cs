using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W2c X2 效果系统接线验收（演示测试）：
/// ①效果装载/卸载生命周期（加载时装载〔先于 card.load 广播〕、显式移除与死亡清理、幂等）；
/// ②失败回滚与隔离（装载钩子失败回滚〔含监听注册回退〕、装配性错误 fail-fast、构造执行异常隔离）；
/// ③『监听 handler → 条件达成 → 触发主动触发器』模式（总线订阅＋流程 band 注入两类宿主；
///   与真实驱动源联动；订阅生效／状态自持／未达成不触发／达成恰一次＋触发后自清理）；
/// ④修饰器衔接（效果经统一修饰 API 施加〔来源＝本效果〕、走管线集中触发、卸载自动按来源撤销）；
/// ⑤主动效果施放（Cast 调用底层触发器——配合 X1 触发者卡牌显式携带）；
/// ⑥测试内装配（手动 AddEffect 经加载链装载）。
/// </summary>
public class EffectSystemTests
{
    private const string DemoId = "u_demo_w2c";

    // ---------- ① 装载生命周期 ----------

    [Fact]
    public async Task Effects_Load_At_Card_Load_Before_Broadcast_And_Watch_Bus_Is_Mounted()
    {
        var effects = new List<Effect>();
        var registry = new CardEffectRegistry();
        registry.Register("effect.probe", _ =>
        {
            var effect = new LifecycleProbeEffect("装载探针效果");
            effects.Add(effect);
            return effect;
        });
        registry.Declare(DemoId, new[] { "effect.probe" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);

        var mountedAtBroadcast = new List<bool>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardLoad && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadCard], demo))
            {
                mountedAtBroadcast.Add(effects.Single().IsMounted);
            }

            return Task.CompletedTask;
        });

        await demo.LoadAsync(match.Players[0]);

        // 装载完成：挂主触发器＋OnMount（监听 handler 注册）；先于 card.load 广播（消费者收到广播时已就绪）
        var effect = (LifecycleProbeEffect)Assert.Single(effects);
        Assert.True(effect.IsMounted);
        Assert.Equal(1, effect.MountCalls);
        Assert.Same(effect, Assert.Single(demo.Effects));
        Assert.True(Assert.Single(mountedAtBroadcast));

        // 被动效果挂总线（E-D1：disposing 触发器挂载于 card.destroyed 兜底）
        Assert.Contains($"{effect.Name}/dispose", match.Engine.Bus.GetSubscribers(Updates.CardDestroyed));
    }

    [Fact]
    public async Task Manually_Attached_Effect_Is_Loaded_Immediately_And_Load_Is_Idempotent()
    {
        // 无效果源的卡：装载语境照常（对局卡库实例化）——效果经测试内装配（手动 AddEffect；W3-A3：添加即装载），
        // 加载链装载步骤幂等覆盖（已装载跳过）
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var demo = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        var effect = new LifecycleProbeEffect("手动装配效果");
        demo.AddEffect(effect);

        Assert.True(effect.IsMounted); // W3-A3：添加即装载（不再要求已放置）
        Assert.Equal(1, effect.MountCalls);

        await demo.LoadAsync(match.Players[0]);

        Assert.True(effect.IsMounted); // 加载链幂等：已装载跳过、不重复 OnMount
        Assert.Equal(1, effect.MountCalls);
    }

    [Fact]
    public async Task Standalone_Construction_Loads_Effect_And_Skips_Load_Chain()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();

        // 独立构造（脱离对局——不经对局卡库、无对局装载语境）：W3-A3 起装载照常发生（内核装载机制不感知对局语境——
        // 不设语境门控）；「加载链整链跳过」防御语义收窄于 LoadAsync 路径（不抛错、加载不失败、列表保留、不重复装载）
        var standalone = new UnitCard(
            new LogicEngine(),
            new CardDefinition(
                "独立演示兵", deployCost: 1, operateCost: 1, attack: 5, defense: 4,
                unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));
        var effect = new LifecycleProbeEffect("独立构造探针");
        standalone.AddEffect(effect);

        Assert.True(effect.IsMounted); // 装载发生（Add 即装载——不设对局语境门控）
        Assert.Equal(1, effect.MountCalls);

        await standalone.LoadAsync(match.Players[0]);

        Assert.True(effect.IsMounted);
        Assert.Equal(1, effect.MountCalls); // 不重复装载、不抛错
        Assert.Same(effect, Assert.Single(standalone.Effects)); // 列表保留（仅不装载——无持久副作用）
    }

    // ---------- ② 卸载清理 ----------

    [Fact]
    public async Task Effect_Unmount_On_Removal_Revokes_Injection_And_Is_Idempotent()
    {
        Trigger<OrderFlowView>? flowTrigger = null;
        var registry = new CardEffectRegistry();
        registry.Register("effect.injectprobe", _ => new InjectionProbeEffect("注入探针效果", () => flowTrigger));
        registry.Declare(DemoId, new[] { "effect.injectprobe" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        flowTrigger = match.Engine.OrderFlow.Trigger;
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        await demo.LoadAsync(match.Players[0]);
        var effect = (InjectionProbeEffect)Assert.Single(demo.Effects);
        var (source, target) = CreateFlowCards(match.Engine);

        // 装载 → 注入生效：真实流程驱动 handler
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(1, effect.HandlerCalls);

        // 移除（显式移除路径）→ 统一卸载链：OnUnmount → 撤销登记 → 总线卸载
        demo.RemoveEffect(effect);

        Assert.False(effect.IsMounted);
        Assert.Equal(1, effect.UnmountCalls);
        Assert.Empty(demo.Effects);
        Assert.DoesNotContain(effect.Name, match.Engine.Bus.GetSubscribers(Updates.EffectRemoved));

        // 注入撤销后 handler 不再执行（真实流程再驱动）
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(1, effect.HandlerCalls);

        // 重复移除＝幂等（不重复清理）
        demo.RemoveEffect(effect);
        Assert.Equal(1, effect.UnmountCalls);
    }

    [Fact]
    public async Task Effect_Unmount_On_Death_Completes_Before_Card_Died()
    {
        var registry = new CardEffectRegistry();
        registry.Register("effect.life", _ => new LifecycleProbeEffect("死亡清理探针效果"));
        registry.Declare(DemoId, new[] { "effect.life" });

        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(
            bridge, effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        await demo.LoadAsync(playerB);
        var effect = (LifecycleProbeEffect)Assert.Single(demo.Effects);
        var joinResult = await match.PlayManager.JoinUnitAsync(demo, match.Battlefield.FrontLine[0]);
        Assert.Equal(PlayResultStatus.Success, joinResult.Status);

        var mega = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.MegaId, 1); // 攻 30
        CommandTestKit.Activate(mega);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // card.died 发射时（死亡状态就绪后）：效果卸载已完成（「先数值变化→清理→card.died」观察序）
        var unloadedAtDied = false;
        using var diedProbe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardDied && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadCard], demo))
            {
                unloadedAtDied = !effect.IsMounted && demo.Effects.Count == 0;
            }

            return Task.CompletedTask;
        });

        var result = await CommandTestKit.RunCommandAsync(match, bridge, mega, demo.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(demo.GetData<UnitStateData>().IsDestroyed);
        Assert.True(unloadedAtDied);
        Assert.Equal(1, effect.UnmountCalls);
        Assert.False(effect.IsMounted);
        Assert.DoesNotContain(effect.Name, match.Engine.Bus.GetSubscribers(Updates.EffectRemoved));
    }

    [Fact]
    public async Task Repeated_Mount_Is_Idempotent()
    {
        var registry = new CardEffectRegistry();
        registry.Register("effect.probe", _ => new LifecycleProbeEffect("装载幂等探针"));
        registry.Declare(DemoId, new[] { "effect.probe" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        await demo.LoadAsync(match.Players[0]);
        var effect = (LifecycleProbeEffect)Assert.Single(demo.Effects);

        // 重复装载（加载时点入口可重复调用）：已装载＝跳过、不重复 OnMount
        demo.MountPassiveEffects();
        demo.MountPassiveEffects();

        Assert.Equal(1, effect.MountCalls);
    }

    // ---------- ③ 失败回滚与隔离 ----------

    [Fact]
    public async Task Mount_Failure_Rolls_Back_And_Isolates_Other_Effects()
    {
        Trigger<OrderFlowView>? flowTrigger = null;
        var effects = new List<Effect>();
        var registry = new CardEffectRegistry();
        registry.Register("effect.ok1", _ =>
        {
            var effect = new LifecycleProbeEffect("顺常效果一");
            effects.Add(effect);
            return effect;
        });
        registry.Register("effect.fail", _ =>
        {
            var effect = new FailingMountEffect("失败效果", () => flowTrigger);
            effects.Add(effect);
            return effect;
        });
        registry.Register("effect.ok2", _ =>
        {
            var effect = new LifecycleProbeEffect("顺常效果二");
            effects.Add(effect);
            return effect;
        });
        registry.Declare(DemoId, new[] { "effect.ok1", "effect.fail", "effect.ok2" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        flowTrigger = match.Engine.OrderFlow.Trigger;
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);

        using var recorder = new UpdateRecorder(match.Engine);
        await demo.LoadAsync(match.Players[0]); // 不抛——装载失败被隔离

        var ok1 = (LifecycleProbeEffect)effects[0];
        var fail = (FailingMountEffect)effects[1];
        var ok2 = (LifecycleProbeEffect)effects[2];

        // 失败效果回滚为未生效；同卡其它效果不受影响；装载顺序＝Effects 列表序＝声明序
        Assert.True(ok1.IsMounted);
        Assert.Equal(1, ok1.MountCalls);
        Assert.False(fail.IsMounted);
        Assert.True(ok2.IsMounted);
        Assert.Equal(1, ok2.MountCalls);
        Assert.Equal(3, demo.Effects.Count); // 容器列表保留（回滚 ≠ 移除）
        Assert.Same(ok1, demo.Effects[0]);
        Assert.Same(fail, demo.Effects[1]);
        Assert.Same(ok2, demo.Effects[2]);

        // 失败留痕（记录进事件流）
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Keywords.Contains("exception:InvalidOperationException") && e.Keywords.Contains("失败效果"));

        // 监听注册全部回退：失败效果主触发器未挂总线；注入的 handler 被撤销（真实流程驱动不执行）
        Assert.DoesNotContain("失败效果/dispose", match.Engine.Bus.GetSubscribers(Updates.CardDestroyed));
        Assert.Contains("顺常效果一/dispose", match.Engine.Bus.GetSubscribers(Updates.CardDestroyed));
        var (source, target) = CreateFlowCards(match.Engine);
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(0, fail.HandlerCalls);

        // 卡牌加载照常完成（card.load 已广播）
        Assert.Contains(GameUpdates.CardLoad, recorder.Types);

        // 多卡隔离：另一张卡同声明照常完成加载
        var demo2 = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        await demo2.LoadAsync(match.Players[0]);
        Assert.True(((LifecycleProbeEffect)effects[3]).IsMounted);
        Assert.False(((FailingMountEffect)effects[4]).IsMounted);
    }

    [Fact]
    public async Task Assembly_Errors_Fail_Fast_On_Load()
    {
        // 声明期拒绝（注册即配置）：重复项 / 空白 / 重复声明 / 重复注册 / null 工厂
        var configRegistry = new CardEffectRegistry();
        Assert.Throws<ArgumentException>(() => configRegistry.Declare("x", new[] { "a", "a" }));
        Assert.Throws<ArgumentException>(() => configRegistry.Declare("x", new[] { " " }));
        configRegistry.Declare("x", new[] { "a" });
        Assert.Throws<InvalidOperationException>(() => configRegistry.Declare("x", new[] { "b" }));
        configRegistry.Register("a", _ => null!);
        Assert.Throws<InvalidOperationException>(() => configRegistry.Register("a", _ => null!));
        Assert.Throws<ArgumentException>(() => configRegistry.Register(" ", _ => null!));
        Assert.Throws<ArgumentNullException>(() => configRegistry.Register("c", null!));

        // 未知效果类型：加载时 fail-fast（上抛、无部分装配、未达广播）
        var unknownRegistry = new CardEffectRegistry();
        unknownRegistry.Declare(DemoId, new[] { "effect.missing" });
        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: unknownRegistry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        using var recorder = new UpdateRecorder(match.Engine);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => demo.LoadAsync(match.Players[0]));
        Assert.Contains("effect.missing", ex.Message);
        Assert.Empty(demo.Effects);
        Assert.DoesNotContain(GameUpdates.CardLoad, recorder.Types);

        // 工厂返回 null：装配性错误——加载 fail-fast
        var nullRegistry = new CardEffectRegistry();
        nullRegistry.Register("effect.null", _ => null!);
        nullRegistry.Declare(DemoId, new[] { "effect.null" });
        var match2 = CommandTestKit.CreateCommandMatch(
            effectRegistry: nullRegistry, extraDefinitions: new[] { DemoDefinition });
        await match2.Initialize();
        var demo2 = (UnitCard)match2.CardLibrary.Instantiate(DemoId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => demo2.LoadAsync(match2.Players[0]));
        Assert.Empty(demo2.Effects);
    }

    [Fact]
    public async Task Factory_Execution_Exception_Is_Isolated()
    {
        var effects = new List<Effect>();
        var registry = new CardEffectRegistry();
        registry.Register("effect.throwing", _ => throw new InvalidOperationException("工厂执行异常（注入）"));
        registry.Register("effect.ok", _ =>
        {
            var effect = new LifecycleProbeEffect("构造隔离伴随效果");
            effects.Add(effect);
            return effect;
        });
        registry.Declare(DemoId, new[] { "effect.throwing", "effect.ok" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);

        await demo.LoadAsync(match.Players[0]); // 不抛——构造执行异常按装载隔离语义处理

        // 构造失败的效果跳过（未生效）；其它效果照常装载
        Assert.Single(demo.Effects);
        Assert.True(effects.Single().IsMounted);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Keywords.Contains("effect.throwing") && e.Keywords.Contains("error"));
    }

    // ---------- ④ 监听 → 条件 → 触发（G14 模式演示） ----------

    [Fact]
    public async Task Listener_Condition_Trigger_Chain_Via_Bus_Subscription()
    {
        LogicEngine? engine = null;
        var registry = new CardEffectRegistry();
        registry.Register("effect.turnwatch", _ => new TurnEndWatchEffect(engine!, "回合监听效果"));
        registry.Declare(DemoId, new[] { "effect.turnwatch" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        engine = match.Engine;
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        await demo.LoadAsync(match.Players[0]);
        var effect = Assert.IsType<TurnEndWatchEffect>(Assert.Single(demo.Effects));

        // a) 订阅生效：真实回合流程（EndTurn → turn.end）驱动 handler；订阅可查询
        Assert.Contains(effect.WatchTriggerName, match.Engine.Bus.GetSubscribers(GameUpdates.TurnEnd));
        await match.EndTurn();
        Assert.Equal(1, effect.SeenCount);

        // c) 未达成＝不触发（零副作用）
        Assert.Equal(0, effect.FireCount);

        // d) 达成（第 2 次）＝恰一次触发＋触发后自清理（订阅注销）——b) 状态自持（累计正确）体现于上式
        await match.EndTurn();
        Assert.Equal(2, effect.SeenCount);
        Assert.Equal(1, effect.FireCount);
        Assert.True(effect.SubscriptionUnmounted);
        Assert.DoesNotContain(effect.WatchTriggerName, match.Engine.Bus.GetSubscribers(GameUpdates.TurnEnd));

        // 自清理后不再响应（一次性语义）
        await match.EndTurn();
        Assert.Equal(2, effect.SeenCount);
        Assert.Equal(1, effect.FireCount);
    }

    [Fact]
    public async Task Listener_Condition_Trigger_Chain_Via_Flow_Band_Injection()
    {
        LogicEngine? engine = null;
        var registry = new CardEffectRegistry();
        registry.Register("effect.flowwatch", _ => new FlowWatchEffect(engine!, "流程监听效果"));
        registry.Declare(DemoId, new[] { "effect.flowwatch" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        engine = match.Engine;
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        await demo.LoadAsync(match.Players[0]);
        var effect = Assert.IsType<FlowWatchEffect>(Assert.Single(demo.Effects));
        var (source, target) = CreateFlowCards(match.Engine);

        // a) 注入生效：真实流程（内核指令流程）驱动 band 内 handler
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(1, effect.SeenCount);

        // c) 未达成＝不触发（零副作用）
        Assert.Equal(0, effect.FireCount);

        // d) 达成（第 2 次）＝恰一次触发＋触发后自清理（注册撤销）——b) 状态自持（累计正确）
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(2, effect.SeenCount);
        Assert.Equal(1, effect.FireCount);
        Assert.True(effect.RegistrationRevoked);

        // 注册撤销后不再被调用
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(2, effect.SeenCount);
        Assert.Equal(1, effect.FireCount);
    }

    // ---------- ⑤ 修饰器衔接 ----------

    [Fact]
    public async Task Effect_Modifier_Flows_Through_Pipeline_And_Is_Removed_On_Unmount()
    {
        LogicEngine? engine = null;
        var registry = new CardEffectRegistry();
        registry.Register("effect.empower", _ => new EmpowerOnJoinEffect(engine!, "强化效果"));
        registry.Declare(DemoId, new[] { "effect.empower" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { DemoDefinition });
        await match.Initialize();
        engine = match.Engine;
        var demo = (UnitCard)match.CardLibrary.Instantiate(DemoId);
        await demo.LoadAsync(match.Players[0]);
        var effect = Assert.IsType<EmpowerOnJoinEffect>(Assert.Single(demo.Effects));

        // 上场（加入路径）→ 效果施加修饰器（+2 攻击；来源＝本效果）→ 走管线：有效值变化＋集中触发
        using var recorder = new UpdateRecorder(match.Engine);
        var joinResult = await match.PlayManager.JoinUnitAsync(demo, match.Battlefield.FrontLine[0]);
        Assert.Equal(PlayResultStatus.Success, joinResult.Status);

        Assert.Equal(1, effect.AppliedCount);
        Assert.Equal(7, demo.Modifiers.GetEffectiveValue(CardStatFields.Attack)); // 基准 5 ＋ 2
        var modifier = Assert.Single(demo.Modifiers.All);
        Assert.Same(effect, modifier.Source); // 施加即登记「来源＝本效果」
        var firstChange = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Contains(CardStatFields.Attack, ModifierTestKit.ChangedFieldsOf(firstChange.Payload));

        // 无变更轮：零发射（集中触发＝有变更才发、恰一次）
        await demo.Modifiers.RequestRerunAsync();
        Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));

        // 效果卸载 → 框架托管：该效果施加的全部修饰器自动撤销、无残留（走管线可观测）
        demo.RemoveEffect(effect);

        Assert.Empty(demo.Modifiers.All);
        Assert.Equal(5, demo.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var changesAfterUnmount = ModifierTestKit.StatChangedUpdates(recorder);
        Assert.Equal(2, changesAfterUnmount.Count);
        Assert.Contains(CardStatFields.Attack, ModifierTestKit.ChangedFieldsOf(changesAfterUnmount[1].Payload));
        Assert.False(effect.IsMounted);
    }

    // ---------- ⑥ 主动效果施放（效果调用底层触发器——配合 X1） ----------

    [Fact]
    public async Task Active_Effect_Cast_Calls_Bottom_Trigger_With_TriggerCard()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        CommandTestKit.Activate(attacker);

        var attackLog = new List<Card?>();
        var damageLog = new List<Card?>();
        match.CommandManager.UnitAttackTrigger.Register("X1探测-攻击视图", (view, ctx, ct) =>
        {
            attackLog.Add(view.TriggerCard);
            return Task.CompletedTask;
        }, Orc.Game.Actions.ActionTriggerBands.Initiate);
        match.CommandManager.AttackDamageTrigger.Register("X1探测-伤害视图", (view, ctx, ct) =>
        {
            damageLog.Add(view.TriggerCard);
            return Task.CompletedTask;
        }, Orc.Game.Actions.ActionHandleBands.Apply);

        // 主动效果（宿主＝attacker）：施放时调用底层「单位攻击」触发器（显式携带触发者＝宿主卡）
        var effect = new StrikeActiveEffect(match.Engine, "演示打击", match.CommandManager.UnitAttackTrigger);
        attacker.AddEffect(effect);

        await effect.CastAsync(
            match.Engine, new Dictionary<string, object?> { [StrikeTargetView.TargetKey] = target.Ref });

        // 效果调用底层触发器 → 真实攻击结算（目标防御 5 − 2）；触发者＝效果宿主卡（两链路同引用实例）
        Assert.Equal(3, target.GetData<UnitStateData>().Defense);
        Assert.Same(attacker, Assert.Single(attackLog));
        Assert.Same(attacker, Assert.Single(damageLog));
    }

    // ---------- 辅助 ----------

    private static CardDefinitionEntry DemoDefinition => new(
        DemoId,
        new CardDefinition(
            "演示兵", deployCost: 1, operateCost: 1, attack: 5, defense: 4,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    private static (Card Source, Card Target) CreateFlowCards(LogicEngine engine)
    {
        var source = new Card(engine, "流程源");
        source.AddData(new HealthData { Hp = 5 });
        var target = new Card(engine, "流程标");
        target.AddData(new HealthData { Hp = 5 });
        return (source, target);
    }
}

// ---------- 测试效果（演示用最小实现） ----------

/// <summary>装载/卸载探针效果：记录挂载/卸载钩子调用次数。</summary>
internal sealed class LifecycleProbeEffect : PassiveEffect
{
    public LifecycleProbeEffect(string name)
        : base(name)
    {
    }

    public int MountCalls { get; private set; }

    public int UnmountCalls { get; private set; }

    protected override void OnMount() => MountCalls += 1;

    protected override void OnUnmount() => UnmountCalls += 1;
}

/// <summary>注入探针效果：装载时向注入目标（内核指令流程）注册 handler（装载语境 Inject——卸载时框架撤销登记）。</summary>
internal sealed class InjectionProbeEffect : PassiveEffect
{
    private readonly Func<Trigger<OrderFlowView>?> _target;

    public InjectionProbeEffect(string name, Func<Trigger<OrderFlowView>?> target)
        : base(name)
        => _target = target;

    public int MountCalls { get; private set; }

    public int UnmountCalls { get; private set; }

    public int HandlerCalls { get; private set; }

    protected override void OnMount()
    {
        MountCalls += 1;
        var target = _target();
        if (target is not null)
        {
            Inject(target, "流程探针", OrderFlowBands.Resolve, OnFlowAsync);
        }
    }

    protected override void OnUnmount() => UnmountCalls += 1;

    private Task OnFlowAsync(OrderFlowView view, Context ctx, CancellationToken ct)
    {
        HandlerCalls += 1;
        return Task.CompletedTask;
    }
}

/// <summary>失败注入效果：装载钩子先注册注入、后抛异常（装载链回滚＋隔离用例——监听注册回退断言）。</summary>
internal sealed class FailingMountEffect : PassiveEffect
{
    public const string FailureMessage = "装载钩子异常（失败注入）";

    private readonly Func<Trigger<OrderFlowView>?> _injectTarget;

    public FailingMountEffect(string name, Func<Trigger<OrderFlowView>?> injectTarget)
        : base(name)
        => _injectTarget = injectTarget;

    public int HandlerCalls { get; private set; }

    protected override void OnMount()
    {
        var target = _injectTarget();
        if (target is not null)
        {
            Inject(target, "失败探针", OrderFlowBands.Resolve, OnFlowAsync);
        }

        throw new InvalidOperationException(FailureMessage);
    }

    private Task OnFlowAsync(OrderFlowView view, Context ctx, CancellationToken ct)
    {
        HandlerCalls += 1;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 回合监听效果（G14 模式演示——总线订阅宿主）：装载时自建被动触发器（hooks＝turn.end）挂载至总线；
/// handler 状态自持（累计）；条件达成（第 2 次）→ invoke 自持主动触发器（统一入口）＋触发后自清理（订阅注销）。
/// 一次性语义：达成＝恰一次、触发后不再响应。
/// </summary>
internal sealed class TurnEndWatchEffect : PassiveEffect
{
    private readonly LogicEngine _engine;
    private readonly Trigger<CardEventView> _watchTrigger;
    private readonly Trigger<CardEventView> _fireTrigger;
    private int _seen;

    public TurnEndWatchEffect(LogicEngine engine, string name)
        : base(name)
    {
        _engine = engine;
        _watchTrigger = new Trigger<CardEventView>(
            $"{name}/监听",
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<CardEventView>("监听处理", OnWatchedAsync) },
            hooks: new[] { GameUpdates.TurnEnd },
            owner: this);
        _fireTrigger = new Trigger<CardEventView>($"{name}/触发目标", TriggerKind.Active);
        _fireTrigger.Register("发布", OnFireAsync);
    }

    public int SeenCount => _seen;

    public int FireCount { get; private set; }

    public bool SubscriptionUnmounted { get; private set; }

    public string WatchTriggerName => $"{Name}/监听";

    protected override void OnMount() => _engine.Bus.Mount(_watchTrigger);

    protected override void OnUnmount()
    {
        _engine.Bus.UnmountOwner(this); // 专属清理：订阅注销（幂等）
        SubscriptionUnmounted = true;
    }

    private async Task OnWatchedAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        _seen += 1;
        if (_seen != 2)
        {
            return; // 未达成（＜2）或已达成过（保险——触发时订阅已注销）：不触发
        }

        await _fireTrigger.InvokeAsync(_engine, null, ct); // 条件达成 → 触发主动触发器（统一入口）
        _engine.Bus.UnmountOwner(this);                    // 触发后自清理（订阅注销）
        SubscriptionUnmounted = true;
    }

    private Task OnFireAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        FireCount += 1;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 流程监听效果（G14 模式演示——流程 band 注入宿主）：装载时经 Inject 把 handler 注入内核流程
/// 「结算（Resolve）」band（装载语境、经真实流程驱动）；handler 状态自持（累计）；条件达成（第 2 次）→
/// invoke 自持主动触发器＋触发后自清理（注册撤销）。
/// </summary>
internal sealed class FlowWatchEffect : PassiveEffect
{
    private readonly LogicEngine _engine;
    private readonly Trigger<CardEventView> _fireTrigger;
    private TriggerRegistration? _registration;
    private Trigger<OrderFlowView>? _target;
    private int _seen;

    public FlowWatchEffect(LogicEngine engine, string name)
        : base(name)
    {
        _engine = engine;
        _fireTrigger = new Trigger<CardEventView>($"{name}/触发目标", TriggerKind.Active);
        _fireTrigger.Register("发布", OnFireAsync);
    }

    public int SeenCount => _seen;

    public int FireCount { get; private set; }

    public bool RegistrationRevoked { get; private set; }

    protected override void OnMount()
    {
        _target = _engine.OrderFlow.Trigger;
        _registration = Inject(_target, "流程监听", OrderFlowBands.Resolve, OnFlowAsync);
    }

    private async Task OnFlowAsync(OrderFlowView view, Context ctx, CancellationToken ct)
    {
        _seen += 1;
        if (_seen != 2)
        {
            return;
        }

        await _fireTrigger.InvokeAsync(_engine, null, ct); // 条件达成 → 触发主动触发器（统一入口）
        var target = _target;
        var registration = _registration;
        if (target is not null && registration is not null)
        {
            RegistrationRevoked = target.Unregister(registration); // 触发后自清理（注册撤销）
            _registration = null;
        }
    }

    private Task OnFireAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        FireCount += 1;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 强化效果（修饰器衔接演示）：监听 unit.joined——本卡单位加入时施加「+2 攻击」修饰器
/// （统一修饰 API；来源＝本效果）；撤销由框架托管（效果卸载 → 按来源批量撤销）。
/// </summary>
internal sealed class EmpowerOnJoinEffect : PassiveEffect
{
    private readonly LogicEngine _engine;
    private readonly Trigger<UnitJoinWatchView> _watchTrigger;
    private CardBase? _card;

    public EmpowerOnJoinEffect(LogicEngine engine, string name)
        : base(name)
    {
        _engine = engine;
        _watchTrigger = new Trigger<UnitJoinWatchView>(
            $"{name}/监听",
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<UnitJoinWatchView>("施加", OnJoinedAsync) },
            hooks: new[] { GameUpdates.UnitJoined },
            owner: this);
    }

    public int AppliedCount { get; private set; }

    protected override void OnMount()
    {
        _card = Host as CardBase;
        _engine.Bus.Mount(_watchTrigger);
    }

    protected override void OnUnmount() => _engine.Bus.UnmountOwner(this);

    private async Task OnJoinedAsync(UnitJoinWatchView view, Context ctx, CancellationToken ct)
    {
        if (_card is null || AppliedCount > 0)
        {
            return;
        }

        if (view.Unit is not CardBase joined || !ReferenceEquals(joined, _card))
        {
            return; // 仅处理本卡自己的加入
        }

        AppliedCount += 1;
        await _card.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, this)); // 来源＝本效果
    }
}

/// <summary>
/// 打击（主动效果演示）：施放时调用底层「单位攻击」触发器（效果调用底层触发器——配合 X1：
/// 显式携带触发者＝效果宿主卡引用）；真实攻击结算经「攻击→伤害」链路传递触发者。
/// </summary>
internal sealed class StrikeActiveEffect : ActiveEffect<StrikeTargetView>
{
    private readonly LogicEngine _engine;
    private readonly Trigger<UnitAttackTriggerView> _attackTrigger;

    public StrikeActiveEffect(LogicEngine engine, string name, Trigger<UnitAttackTriggerView> attackTrigger)
        : base(name)
    {
        _engine = engine;
        _attackTrigger = attackTrigger;
        CastTrigger.Register("施放打击", OnCastAsync);
    }

    private async Task OnCastAsync(StrikeTargetView view, Context ctx, CancellationToken ct)
    {
        if (view.Target is not { IsAlive: true } targetRef)
        {
            return;
        }

        var data = new Dictionary<string, object?>
        {
            [CommandDataKeys.Attacker] = Host.Ref,
            [CommandDataKeys.Target] = targetRef,
            [CommandDataKeys.TriggerCard] = Host, // X1：效果显式携带触发者（本次操作由本效果宿主卡引发）
        };
        await _attackTrigger.InvokeAsync(_engine, data, ct);
    }
}

// ---------- 测试视图 ----------

/// <summary>unit.joined 监听视图（值＝单位卡牌实例对象引用——与 GameUpdates 载荷键同口径）。</summary>
[ContextView]
public class UnitJoinWatchView
{
    [Optional]
    [Read]
    public virtual object? Unit { get; set; }
}

/// <summary>打击施放视图（施放载荷：目标引用）。</summary>
[ContextView]
public class StrikeTargetView
{
    /// <summary>施放目标键名（视图属性名）。</summary>
    public const string TargetKey = "Target";

    [Optional]
    [Read]
    public virtual Ref<Entity>? Target { get; set; }
}
