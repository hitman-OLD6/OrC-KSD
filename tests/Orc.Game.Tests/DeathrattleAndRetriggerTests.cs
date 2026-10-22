using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 第 2 批·A4 验收（亡计与再触发）：
/// ① 亡计——获得亡计（携带内容载荷的授予路径）→ 死亡 → 「清理前结算」执行；时序验证＝「死亡前可观察状态」清单逐项断言
///   （探针：词条仍处行为态、修饰仍生效、效果仍装载、位置字段可读、IsDestroyed 已置位）；必须用例：反例（授予后移除→不结算）、
///   重入恰一次、结算异常隔离继续清理、空内容静默、非战斗死亡来源正例（防御归零）。
/// ② 再触发——「触发所有友方单位的部署/亡计」：总线包装触发器路径（发布→接收→执行同一同步链）；端到端行为断言
///   （遍历、执行序/次数、逐条隔离）；含 DeploymentLogicData 数据面（生成/登记经装配链 → 组件就绪 → 重放→断言）。
/// ③ 边界——非法输入分层（结构性非法＝明确异常；状态失效＝忽略）；终局门禁（零副作用）；不发链级信号。
/// </summary>
public class DeathrattleAndRetriggerTests
{
    private const string ReplayUnitId = "u_replay";

    /// <summary>重放测试单位（攻 2 / 防 5——「u_replay」不与他卡冲突）。</summary>
    private static IReadOnlyList<CardDefinitionEntry> ReplayDefinitions() => new[]
    {
        new CardDefinitionEntry(
            ReplayUnitId,
            new CardDefinition("重放兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    // ---------- 探针与承载 ----------

    /// <summary>探针亡计内容效果：记录执行次数与上下文（动作型——不设订阅自触发）。</summary>
    private sealed class ProbeDeathrattleEffect : DeathrattleEffect
    {
        private readonly Func<DeathrattleContext, Task> _onExecute;

        public ProbeDeathrattleEffect(string name, Func<DeathrattleContext, Task> onExecute)
            : base(name)
        {
            _onExecute = onExecute;
        }

        /// <summary>执行次数（恰一次断言依据）。</summary>
        public int Executions { get; private set; }

        protected override async Task OnExecuteAsync(DeathrattleContext context, CancellationToken ct)
        {
            Executions++;
            await _onExecute(context);
        }
    }

    /// <summary>旁路被动效果（「效果仍装载」断言证据——与内容效果并存的独立效果）。</summary>
    private sealed class StrayPassiveEffect : PassiveEffect
    {
        public StrayPassiveEffect()
            : base("旁路被动效果")
        {
        }
    }

    /// <summary>结算时点观察记录（「死亡前可观察状态」清单逐项）。</summary>
    private sealed class SettlementObservation
    {
        public bool RattleKeywordActive { get; init; }   // 亡计词条仍处行为态
        public bool OtherKeywordActive { get; init; }    // 同卡其它词条仍在行为态
        public bool ModifierEffective { get; init; }     // 修饰仍生效（实时数值含修饰）
        public bool ContentEffectMounted { get; init; }  // 内容效果仍装载（效果列表）
        public bool StrayEffectMounted { get; init; }    // 并存效果仍装载
        public bool LineReadable { get; init; }      // 位置字段可读（＝死亡前槽位引用）
        public bool SlotFreed { get; init; }             // 槽位已释放（死亡即离场）
        public bool Destroyed { get; init; }             // IsDestroyed 已置位
        public bool EngineSame { get; init; }            // 统一上下文：引擎引用
    }

    // ---------- ① 亡计：时序探针（主用例——验收①） ----------

    [Fact]
    public async Task Deathrattle_Settles_Before_Cleanup_With_Observable_State()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1); // 攻 1 / 防 2
        var victimSlot = match.Battlefield.GetSupportLine(playerB).GetInsertionGaps()[1];

        // 前置：同卡其它词条（行为态可查证据）＋一项修饰（修饰仍生效证据）＋一项并存效果（效果仍装载证据）。
        Assert.True(await victim.Keywords.GrantAsync(KeywordIds.Forecast));
        await victim.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 4, new object()));
        var stray = new StrayPassiveEffect();
        victim.AddEffect(stray);
        Assert.True(stray.IsMounted);

        var observations = new List<SettlementObservation>();
        ProbeDeathrattleEffect? probe = null;
        probe = new ProbeDeathrattleEffect("时序探针", context =>
        {
            var unit = context.Unit;
            var state = unit.GetData<UnitStateData>();
            observations.Add(new SettlementObservation
            {
                RattleKeywordActive = unit.Keywords.TryGetActiveComponent<DeathrattleKeywordComponent>(KeywordIds.Deathrattle, out _),
                OtherKeywordActive = unit.Keywords.TryGetActiveComponent<PlainKeywordComponent>(KeywordIds.Forecast, out _),
                ModifierEffective = unit.Modifiers.GetEffectiveValue(CardStatFields.Attack) == 5, // 1 基准 + 4 修饰
                ContentEffectMounted = unit.Effects.Contains(probe!) && probe!.IsMounted,
                StrayEffectMounted = unit.Effects.Contains(stray) && stray.IsMounted,
                LineReadable = ReferenceEquals(state.Line, victimSlot),
                SlotFreed = victimSlot.IsEmpty(),
                Destroyed = state.IsDestroyed,
                EngineSame = ReferenceEquals(context.Engine, match.Engine),
            });
            return Task.CompletedTask;
        });

        // 获得亡计（授予路径——携带内容载荷；内嵌效果通道装载）。
        Assert.True(await DeathrattleRules.GrantAsync(victim, probe));
        Assert.True(victim.Keywords.Has(KeywordIds.Deathrattle));
        Assert.Contains(probe, victim.Effects);
        Assert.True(probe.IsMounted);

        // 战斗致死（统一死亡流程）。
        var killer = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BeastId, 1); // 攻 6 / 防 7
        CommandTestKit.Activate(killer);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var kill = await CommandTestKit.RunCommandAsync(match, bridge, killer, victim.Ref);

        Assert.Equal(CommandResultStatus.Success, kill.Status);

        // 时序验证：「死亡前可观察状态」清单逐项（结算先于词条/修饰/效果清理）。
        var observation = Assert.Single(observations);
        Assert.True(observation.RattleKeywordActive);  // ① 亡计词条仍处行为态
        Assert.True(observation.OtherKeywordActive);   // ①+ 同卡其它词条仍在行为态
        Assert.True(observation.ModifierEffective);    // ② 修饰仍生效
        Assert.True(observation.ContentEffectMounted); // ③ 内容效果仍装载
        Assert.True(observation.StrayEffectMounted);   // ③+ 并存效果仍装载
        Assert.True(observation.LineReadable);     // ④ 位置字段可读（＝死亡前槽位引用）
        Assert.True(observation.SlotFreed);            // ④+ 槽位已释放（死亡即离场——不保留占用）
        Assert.True(observation.Destroyed);            // ⑤ IsDestroyed 已置位（结算可观察）
        Assert.True(observation.EngineSame);           // 统一上下文（宿主卡＋引擎引用）

        // 结算恰一次；死亡完成（清理落定、card.died 恰一次）。
        Assert.Equal(1, probe.Executions);
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.True(victim.Keywords.Has(KeywordIds.Deathrattle)); // 登记保留
        Assert.Empty(victim.Keywords.Components);                 // 行为态清空（注销完成）
        Assert.Empty(victim.Effects);                             // 效果卸载完成
        Assert.Null(victim.GetData<UnitStateData>().Line);    // 位置字段置空（终态）
    }

    // ---------- ① 亡计：必须用例（反例/重入/隔离/空内容/非战斗来源） ----------

    [Fact]
    public async Task Revoked_Before_Death_Does_Not_Settle()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);
        var probe = new ProbeDeathrattleEffect("反例探针", _ => Task.CompletedTask);
        Assert.True(await DeathrattleRules.GrantAsync(victim, probe));

        // 反例：授予后移除 → 死亡 → 不结算。
        Assert.True(await victim.Keywords.RevokeAsync(KeywordIds.Deathrattle));
        Assert.False(victim.Keywords.Has(KeywordIds.Deathrattle));
        Assert.Empty(victim.Effects);

        await victim.ApplyDefenseDamageAsync(2); // 防御归零 → 死亡

        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(0, probe.Executions);
        Assert.Empty(victim.Effects);
    }

    [Fact]
    public async Task Reentry_Of_Same_Card_And_Chain_Is_Skipped_And_Recorded()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var executions = 0;
        var probe = new ProbeDeathrattleEffect("重入探针", async context =>
        {
            executions++;
            // 执行中重入：亡计动作内发起同卡同链再触发——应跳过并记录（防自触发循环）。
            var accepted = await RetriggerRules.RequestDeathrattleAsync(context.Unit);
            Assert.True(accepted); // 请求完成处理（跳过属处理完成）
        });
        Assert.True(await DeathrattleRules.GrantAsync(unit, probe));

        // 首次再触发（发布入口；同步链）——内容执行恰一次（内层重入被跳过）。
        var accepted = await RetriggerRules.RequestDeathrattleAsync(unit);
        Assert.True(accepted);
        Assert.Equal(1, executions);

        // 跳过记录可查（「跳过并记录」——记录形态：事件流条目）。
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Source == "再触发" && e.Keywords.Contains("reentry-skip"));
    }

    [Fact]
    public async Task Settlement_Exception_Is_Isolated_And_Cleanup_Continues()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);
        var probe = new ProbeDeathrattleEffect("爆炸探针", _ => throw new InvalidOperationException("亡计爆炸（测试）"));
        Assert.True(await DeathrattleRules.GrantAsync(victim, probe));
        using var recorder = new UpdateRecorder(match.Engine);

        await victim.ApplyDefenseDamageAsync(2); // 致死（结算内抛异常）

        // 隔离记录并继续后续清理：死亡必须完成（清理落定、信号发射、无半死状态）。
        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Null(victim.GetData<UnitStateData>().Line);
        Assert.Empty(victim.Effects);
        Assert.Empty(victim.Keywords.Components);
        Assert.True(victim.Keywords.Has(KeywordIds.Deathrattle)); // 登记保留
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Source == "亡计" && e.Keywords.Contains("exception:InvalidOperationException"));
    }

    [Fact]
    public async Task Empty_Content_Grant_Is_Silent_On_Death()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);

        // 空内容授予＝合法（空亡计＝无操作）。
        Assert.True(await DeathrattleRules.GrantAsync(victim, content: null));
        Assert.True(victim.Keywords.Has(KeywordIds.Deathrattle));
        Assert.Empty(victim.Effects);

        await victim.ApplyDefenseDamageAsync(2);

        // 静默跳过（无操作、不异常）；死亡照常完成。
        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Empty(victim.Keywords.Components);
        Assert.DoesNotContain(
            match.Engine.RootStream.Entries,
            e => e.Source == "亡计" && e.Keywords.Contains("error"));
    }

    [Fact]
    public async Task Defense_Depletion_Death_Executes_Settlement()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        // 非战斗死亡来源正例（防御归零统一衔接——证「死亡来源不限」）。
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);
        var probe = new ProbeDeathrattleEffect("归零探针", _ => Task.CompletedTask);
        Assert.True(await DeathrattleRules.GrantAsync(victim, probe));

        await victim.ApplyDefenseDamageAsync(victim.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 2 → 0

        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(1, probe.Executions);
    }

    // ---------- ② 再触发：部署（含 DeploymentLogicData 数据面） ----------

    [Fact]
    public async Task Deploy_Retrigger_Replays_Injected_Effects_In_Registration_Order()
    {
        // S6 迁移：部署效果＝效果 + 注入「部署词条触发器」（原 A4 登记面退场）。
        var match = CommandTestKit.CreateCommandMatch(seed: 42, extraDefinitions: ReplayDefinitions());
        await match.Initialize();
        var player = match.Players[0];

        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, ReplayUnitId);
        var order = new List<string>();
        var views = new List<Orc.Game.Triggers.CardTriggerView>();
        unit.AddEffect(new DeployLogicProbeEffect(unit, "A", view => { order.Add("A"); views.Add(view); return Task.CompletedTask; }));
        unit.AddEffect(new DeployLogicProbeEffect(unit, "B", view => { order.Add("B"); views.Add(view); return Task.CompletedTask; }));

        var slot = match.Battlefield.GetSupportLine(player).GetInsertionGaps()[1];
        var joined = await match.PlayManager.JoinUnitAsync(unit, slot);
        Assert.Equal(PlayResultStatus.Success, joined.Status);
        order.Clear();
        views.Clear();

        // 再触发（发布入口；同步链——返回即已完成）：重放部署效果——执行序可观测、上下文＝测试单位＋当前槽位。
        var accepted = await RetriggerRules.RequestDeployAsync(unit);
        Assert.True(accepted);
        Assert.Equal(new[] { "A", "B" }, order);
        Assert.Equal(2, views.Count);
        Assert.All(views, view =>
        {
            Assert.Same(unit, view.Card);
            Assert.Same(slot, view.Position);
        });

        // 非幂等：同一卡同链类型重复请求＝各执行一次（「再触发是动作而非状态确保」）。
        order.Clear();
        accepted = await RetriggerRules.RequestDeployAsync(unit);
        Assert.True(accepted);
        Assert.Equal(new[] { "A", "B" }, order);

        // 不发链级信号（unit.deployed / card.placed / card.died 均不发——避免观察者误判真实入场/死亡）。
        using var recorder = new UpdateRecorder(match.Engine);
        await RetriggerRules.RequestDeployAsync(unit);
        Assert.DoesNotContain(GameUpdates.UnitDeployed, recorder.Types);
        Assert.DoesNotContain(GameUpdates.UnitJoined, recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardDied, recorder.Types);
        Assert.DoesNotContain(Updates.CardPlaced, recorder.Types);
    }

    [Fact]
    public async Task Deploy_Retrigger_Isolates_Single_Effect_Exception()
    {
        var match = CommandTestKit.CreateCommandMatch(seed: 42, extraDefinitions: ReplayDefinitions());
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, ReplayUnitId);
        var order = new List<string>();
        unit.AddEffect(new DeployLogicProbeEffect(unit, "A", _ => { order.Add("A"); return Task.CompletedTask; }));
        unit.AddEffect(new DeployLogicProbeEffect(unit, "B", _ => throw new InvalidOperationException("重放爆炸（测试）")));
        unit.AddEffect(new DeployLogicProbeEffect(unit, "C", _ => { order.Add("C"); return Task.CompletedTask; }));
        await match.PlayManager.JoinUnitAsync(unit, match.Battlefield.GetSupportLine(player).GetInsertionGaps()[1]);

        // 逐条异常隔离（记录并继续）——单个效果异常不阻断后续效果。
        var accepted = await RetriggerRules.RequestDeployAsync(unit);
        Assert.True(accepted);
        Assert.Equal(new[] { "A", "C" }, order);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Keywords.Contains("exception:InvalidOperationException"));
    }

    [Fact]
    public async Task Deploy_Retrigger_Shares_Single_Source_With_Play_Chain()
    {
        // 单源化验证：同一注入面（「部署词条触发器」）——打出路径（部署链①段）与再触发（重放）行为一致。
        var match = CommandTestKit.CreateCommandMatch(seed: 42, extraDefinitions: ReplayDefinitions());
        await match.Initialize();
        var playerA = match.Players[0];

        var played = await CommandTestKit.InstantiateLoadedAsync(match, playerA, ReplayUnitId);
        var order = new List<string>();
        played.AddEffect(new DeployLogicProbeEffect(played, "甲", _ => { order.Add("甲"); return Task.CompletedTask; }));
        played.AddEffect(new DeployLogicProbeEffect(played, "乙", _ => { order.Add("乙"); return Task.CompletedTask; }));

        // 打出（部署链①段）。
        var playResult = await match.PlayManager.PlayUnitAsync(played, match.Battlefield.GetSupportLine(playerA).GetInsertionGaps()[1]);
        Assert.Equal(PlayResultStatus.Success, playResult.Status);
        Assert.Equal(new[] { "甲", "乙" }, order);

        // 再触发（重放——同一触发器）：行为与打出路径一致。
        order.Clear();
        var accepted = await RetriggerRules.RequestDeployAsync(played);
        Assert.True(accepted);
        Assert.Equal(new[] { "甲", "乙" }, order);
    }

    // ---------- ② 再触发：亡计＋遍历＋边界 ----------

    [Fact]
    public async Task Deathrattle_Retrigger_Executes_Once_Per_Request()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var executions = 0;
        var contexts = new List<DeathrattleContext>();
        var probe = new ProbeDeathrattleEffect("再触发探针", context =>
        {
            executions++;
            contexts.Add(context);
            return Task.CompletedTask;
        });
        Assert.True(await DeathrattleRules.GrantAsync(unit, probe));

        // 再触发（不伴随死亡）：驱动内容动作一次；统一上下文（宿主卡＋引擎引用）。
        var accepted = await RetriggerRules.RequestDeathrattleAsync(unit);
        Assert.True(accepted);
        Assert.Equal(1, executions);
        var context = Assert.Single(contexts);
        Assert.Same(unit, context.Unit);
        Assert.Same(match.Engine, context.Engine);

        // 非幂等：重复请求＝各执行一次。
        await RetriggerRules.RequestDeathrattleAsync(unit);
        Assert.Equal(2, executions);

        // 不伴随死亡：存活状态保持、清理不发生。
        Assert.False(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.True(unit.Keywords.Has(KeywordIds.Deathrattle));
        Assert.Contains(probe, unit.Effects);
    }

    [Fact]
    public async Task TriggerAll_Traversal_Skips_Dead_And_OffField_Targets()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var alive1 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var alive2 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var dead = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        var inHand = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId); // 在手（未上场）

        var exec1 = 0;
        var exec2 = 0;
        var execDead = 0;
        await DeathrattleRules.GrantAsync(alive1, new ProbeDeathrattleEffect("存活一", _ => { exec1++; return Task.CompletedTask; }));
        await DeathrattleRules.GrantAsync(alive2, new ProbeDeathrattleEffect("存活二", _ => { exec2++; return Task.CompletedTask; }));
        await DeathrattleRules.GrantAsync(dead, new ProbeDeathrattleEffect("死者", _ => { execDead++; return Task.CompletedTask; }));

        await dead.ApplyDefenseDamageAsync(2); // 死者：死亡结算执行一次（行为态注销）
        Assert.Equal(1, execDead);

        // 「触发所有友方单位的亡计」＝效果侧遍历（单卡请求；目标集筛选/遍历责任在调用方）。
        // 死者＝行为态已注销（忽略）；在手卡＝非在场（忽略）——遍历安全、无异常。
        foreach (var unit in new[] { alive1, alive2, dead, inHand })
        {
            var accepted = await RetriggerRules.RequestDeathrattleAsync(unit);
            Assert.True(accepted);
        }

        Assert.Equal(1, exec1);      // 存活者各执行一次
        Assert.Equal(1, exec2);
        Assert.Equal(1, execDead);   // 死者不再执行（登记保留但行为态注销）
    }

    [Fact]
    public async Task Invalid_Request_Inputs_Are_Rejected_Defensively()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 结构性非法＝明确异常：目标卡为 null。
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => match.RetriggerSystem.RequestAsync(null!, RetriggerChain.Deploy));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => match.RetriggerSystem.RequestAsync(null!, RetriggerChain.Deathrattle));

        // 结构性非法＝明确异常：未知链类型值（枚举形态下 cast 可达）。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => match.RetriggerSystem.RequestAsync(unit, (RetriggerChain)999));

        // 状态失效＝忽略（对已死亡目标：无操作、不异常——与「结构性非法」分层对称）。
        await unit.ApplyDefenseDamageAsync(5);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        var accepted = await match.RetriggerSystem.RequestAsync(unit, RetriggerChain.Deploy);
        Assert.True(accepted); // 请求完成处理（按语义忽略）

        // 目标卡非单位卡＝入口参数类型收窄（编译期排除、运行期不可达——申报对齐「不可达即满足更严格保证」）。
    }

    [Fact]
    public async Task Ended_Match_Requests_Have_Zero_Side_Effect()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var executions = 0;
        await DeathrattleRules.GrantAsync(unit, new ProbeDeathrattleEffect("门禁探针", _ => { executions++; return Task.CompletedTask; }));

        // 终局（巨炮击 HQ——与 MatchOutcomeTests 同构）。
        var striker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.MegaId, 2);
        CommandTestKit.Activate(striker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var strike = await CommandTestKit.RunCommandAsync(match, bridge, striker, playerB.Hq.Ref);
        Assert.Equal(CommandResultStatus.Success, strike.Status);
        Assert.Equal(MatchState.Ended, match.State);

        using var recorder = new UpdateRecorder(match.Engine);
        var acceptedDeathrattle = await RetriggerRules.RequestDeathrattleAsync(unit);
        var acceptedDeploy = await RetriggerRules.RequestDeployAsync(unit);

        // 终局后＝不执行、零副作用（发布入口拒绝；接收环节兜底）。
        Assert.False(acceptedDeathrattle);
        Assert.False(acceptedDeploy);
        Assert.Equal(0, executions);
        Assert.Empty(recorder.Updates);
    }

    // ---------- ③ A4 部署逻辑生成面：随 S6/S7 废弃（原「生成/并存/非单位卡」用例不再适用；
    //    等价能力＝效果 + 注入「部署词条触发器」，覆盖见 PlayChainDeploymentTests 与上方三例） ----------
}
