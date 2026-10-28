using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Model;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// U4（S13 位次落地）**逐组等价证据**：7 组动作（hand.add／draw／death／move／join／damage／order）
/// 每组 3 条，逐条对应一条语义：
/// <list type="number">
/// <item><b>段次序</b>：<c>before</c>（前置／门禁相位）→ 外来挂载边（声明序／追加序）→ 自有边（<c>last</c>）→ <c>after</c>；</item>
/// <item><b><c>ctx.Stop()</c> 语义</b>：命中后同成员的后续外来边、自有边与 <c>after</c> 不执行，已执行的 <c>before</c> 写入保留；</item>
/// <item><b>结果位</b>：真实动作返回的 <see cref="ActionOutcomeKind"/>／<see cref="ActionFailureKind"/> 与迁移前一致。</item>
/// </list>
/// <para>
/// 前两条在**新模型**（成员＝有序边集合）上验证：被测成员的声明**直接取自** <see cref="EngineProcesses"/>
/// （名字＋<c>own-edge-position</c>），自有边清单＝该端在旧模型里的段内注册序（迁移映射的一一对照，
/// 见 <see cref="OwnEdges"/> 的注释）。第三条在**旧执行面**（生产路径未切换）上验证外部行为不变——
/// 两条证据合起来才构成"位次标注 + 行为等价"，缺任一条都只是自说自话。
/// </para>
/// <para>
/// 位次一律 <c>last</c> 的理由见 <see cref="EngineProcesses.ActionEndPositions"/>：转发（旧 <c>Commit</c>）与
/// 落地（旧 <c>Apply</c>）都是链上末步，外来边与 before 包装必须先跑；<c>first</c> 只留给"整端就是门禁"的成员
/// （21 端里不存在——门禁挂在端内部，由 <c>before</c> 相位承载）。
/// </para>
/// </summary>
public sealed class ActionEdgeOrderTests
{
    /// <summary>
    /// 各组**主端**的自有边（＝该端在旧模型里的段内注册序，迁移时一一搬进"自有边"）：
    /// <list type="bullet">
    /// <item><c>hand.add</c>：通用前置（Initiate）→ 转发到处理端（Commit）；</item>
    /// <item><c>draw.top</c>：同上；</item>
    /// <item><c>death</c>：同上；</item>
    /// <item><c>move</c>：同上；</item>
    /// <item><c>join.deploy</c>：部署代价校验（Initiate）→ 打出宣告（Announce）→ 部署词条（Commit）→ 转发（Commit，
    /// 与词条**同段**，次序由声明序承担＝D2）；</item>
    /// <item><c>combat-damage</c>：转发到处理端（Commit；重甲在旧 <c>Initiate</c>＝外来边，先跑）；</item>
    /// <item><c>order.cast</c>：施放结算（Resolve；反制与宣告点是外来边，先跑）。</item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, string[]> OwnEdges = new(StringComparer.Ordinal)
    {
        [EngineProcesses.HandAdd] = new[] { "通用前置", "转发到处理端" },
        [EngineProcesses.DrawTop] = new[] { "通用前置", "转发到处理端" },
        [EngineProcesses.Death] = new[] { "通用前置", "转发到处理端" },
        [EngineProcesses.Move] = new[] { "通用前置", "转发到处理端" },
        [EngineProcesses.JoinDeploy] = new[] { "部署代价校验", "打出宣告", "部署词条", "转发到处理端" },
        [EngineProcesses.CombatDamage] = new[] { "转发到处理端" },
        [EngineProcesses.OrderCast] = new[] { "施放结算" },
    };

    // ══════════════════════ ① hand.add ══════════════════════

    /// <summary>1／3：段次序＝前置（before）→ 外来甲、外来乙（追加序）→ 自有边（last，声明序）→ after。</summary>
    [Fact]
    public async Task 加入手牌_段次序_前置先于外来挂载边_自有边last收口()
    {
        var run = await RunOrderAsync(EngineProcesses.HandAdd);

        Assert.Equal(
            new[] { "前置", "外来甲", "外来乙", "通用前置", "转发到处理端", "after" },
            run.Trace);
    }

    /// <summary>
    /// 2／3：门禁相位 <c>before</c> 命中 <c>ctx.Stop()</c> 后——同成员的外来边、自有边与 <c>after</c> 全不执行；
    /// 已执行的 <c>before</c> 写入（<c>amount=7</c>）保留在数据面上。
    /// <para>
    /// **为什么用 <c>before</c> 而非外来挂载边承载 Stop**：R1i 的迁移规则把"否决类门禁"（旧 <c>Veto</c>）
    /// 映射为 `before + Stop`，免疫类验收也按这条口径写；而**外来挂载边内部的 Stop 不外溢到宿主成员**
    /// （`RunEdgesAsync` 以"边＝一次嵌套成员执行"为单位，停止只作用于该嵌套成员——<c>WrapChannelTests</c>
    /// 的"停止作用域限于当前成员"钉住的正是这条）。两者合起来才是现模型的确定语义：门禁要用 <c>before</c>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 加入手牌_Stop语义_门禁命中后后续边与after不执行_且before写入保留()
    {
        var run = await RunOrderAsync(EngineProcesses.HandAdd, stopInBefore: true);

        Assert.Equal(new[] { "前置", "门禁" }, run.Trace);
        Assert.True(run.Result.Stopped);
        Assert.Equal(7, run.Observed); // 门禁读到前置写入的值＝已执行 before 的写入保留（不回滚）
    }

    /// <summary>3／3：结果位＝入列表 Applied；重复加入＝TargetInvalid（与迁移前同一断言）。</summary>
    [Fact]
    public async Task 加入手牌_结果位_入列表Applied且重复加入TargetInvalid()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var action = match.CommandManager.AddToHandAction;
        var card = await CommandTestKit.InstantiateLoadedAsync(match, player);

        var applied = await action.AddAsync(card, player);
        Assert.Equal(ActionOutcomeKind.Applied, applied.Targets[0].Outcome);
        Assert.Equal(1, applied.Targets[0].AppliedAmount);
        Assert.Null(applied.Targets[0].Reason);
        Assert.Contains(card, player.Hand);

        var again = await action.AddAsync(card, player);
        Assert.Equal(ActionOutcomeKind.TargetInvalid, again.Targets[0].Outcome);
        Assert.Equal(ActionFailureKind.TargetInvalid, again.Targets[0].Reason);
        Assert.Equal(0, again.Targets[0].AppliedAmount);
    }

    // ══════════════════════ ② draw ══════════════════════

    /// <summary>1／3：段次序＝前置 → 外来甲、外来乙 → 自有边（last）→ after（库顶端；指定牌端同形）。</summary>
    [Fact]
    public async Task 抽牌_段次序_前置先于外来挂载边_自有边last收口()
    {
        var run = await RunOrderAsync(EngineProcesses.DrawTop);

        Assert.Equal(
            new[] { "前置", "外来甲", "外来乙", "通用前置", "转发到处理端", "after" },
            run.Trace);
    }

    /// <summary>2／3：门禁在 before 相位 Stop 后——两支外来边、自有边、after 全不执行；前置写入保留。</summary>
    [Fact]
    public async Task 抽牌_Stop语义_门禁命中后后续边与after不执行_且before写入保留()
    {
        var run = await RunOrderAsync(EngineProcesses.DrawTop, stopInBefore: true);

        Assert.Equal(new[] { "前置", "门禁" }, run.Trace);
        Assert.True(run.Result.Stopped);
        Assert.Equal(7, run.Observed);
    }

    /// <summary>3／3：结果位＝Applied，且库顶抽牌仍是"离组＋入列表"（与迁移前同一断言）。</summary>
    [Fact]
    public async Task 抽牌_结果位_Applied且离组入列表()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var action = match.CommandManager.DrawAction;
        var deckBefore = player.Deck.Count;
        var handBefore = player.Hand.Count;

        var result = await action.DrawTopAsync(player);

        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.Equal(1, result.Targets[0].AppliedAmount);
        Assert.Null(result.Targets[0].Reason);
        Assert.Equal(deckBefore - 1, player.Deck.Count);
        Assert.Equal(handBefore + 1, player.Hand.Count);
    }

    // ══════════════════════ ③ death ══════════════════════

    /// <summary>1／3：段次序＝前置 → 外来甲、外来乙 → 自有边（last）→ after（免死／死亡链在处理端，同形）。</summary>
    [Fact]
    public async Task 死亡_段次序_前置先于外来挂载边_自有边last收口()
    {
        var run = await RunOrderAsync(EngineProcesses.Death);

        Assert.Equal(
            new[] { "前置", "外来甲", "外来乙", "通用前置", "转发到处理端", "after" },
            run.Trace);
    }

    /// <summary>2／3：门禁在 before 相位 Stop 后——外来边、自有边、after 全不执行；前置写入保留。</summary>
    [Fact]
    public async Task 死亡_Stop语义_门禁命中后后续边与after不执行_且before写入保留()
    {
        var run = await RunOrderAsync(EngineProcesses.Death, stopInBefore: true);

        Assert.Equal(new[] { "前置", "门禁" }, run.Trace);
        Assert.True(run.Result.Stopped);
        Assert.Equal(7, run.Observed);
    }

    /// <summary>3／3：结果位＝Applied，死亡链照常落地（与迁移前同一断言）。</summary>
    [Fact]
    public async Task 死亡_结果位_Applied且死亡链落地()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 0);

        var result = await match.CommandManager.DeathAction.ExecuteAsync(unit);

        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.Equal(1, result.Targets[0].AppliedAmount);
        Assert.Null(result.Targets[0].Reason);
        // 默认处置＝沿用动作默认（Disposition 只由处理端显式改写；回手用例见 P2ActionFamilyTests 的 ToHand）。
        Assert.Null(result.Targets[0].Disposition);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
    }

    // ══════════════════════ ④ move ══════════════════════

    /// <summary>1／3：段次序＝前置 → 外来甲、外来乙 → 自有边（last）→ after（落地在处理端，同形）。</summary>
    [Fact]
    public async Task 移动_段次序_前置先于外来挂载边_自有边last收口()
    {
        var run = await RunOrderAsync(EngineProcesses.Move);

        Assert.Equal(
            new[] { "前置", "外来甲", "外来乙", "通用前置", "转发到处理端", "after" },
            run.Trace);
    }

    /// <summary>2／3：门禁在 before 相位 Stop 后——外来边、自有边、after 全不执行；前置写入保留。</summary>
    [Fact]
    public async Task 移动_Stop语义_门禁命中后后续边与after不执行_且before写入保留()
    {
        var run = await RunOrderAsync(EngineProcesses.Move, stopInBefore: true);

        Assert.Equal(new[] { "前置", "门禁" }, run.Trace);
        Assert.True(run.Result.Stopped);
        Assert.Equal(7, run.Observed);
    }

    /// <summary>3／3：结果位＝Applied，效果移动落位到目标阵线且位置信号恰一次（与迁移前同一断言）。</summary>
    [Fact]
    public async Task 移动_结果位_Applied且落位目标阵线()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, player, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(unit);
        var supportLine = match.Battlefield.GetSupportLine(player);
        var destination = supportLine.GetInsertionGaps().At(0);
        using var recorder = new UpdateRecorder(match.Engine);

        var landed = await match.CommandManager.MoveAction.EffectAsync(unit, destination);

        Assert.Equal(ActionOutcomeKind.Applied, landed.Targets[0].Outcome);
        Assert.Equal(1, landed.Targets[0].AppliedAmount);
        Assert.Null(landed.Targets[0].Reason);
        Assert.Same(supportLine, unit.GetData<UnitStateData>().Line);
        Assert.Equal(1, recorder.Types.Count(type => type == GameUpdates.UnitPositionChanged));
    }

    // ══════════════════════ ⑤ join ══════════════════════

    /// <summary>
    /// 1／3：段次序＝前置 → 外来甲、外来乙 → 自有边（last，四条按声明序：代价校验 → 宣告 → **词条 → 转发**）→ after。
    /// <para>词条先于转发＝D2 的"同段相对次序"：两条同 band 同 priority，次序由声明序承担。</para>
    /// </summary>
    [Fact]
    public async Task 加入战场_段次序_前置先于外来挂载边_自有边按声明序收口()
    {
        var run = await RunOrderAsync(EngineProcesses.JoinDeploy);

        Assert.Equal(
            new[] { "前置", "外来甲", "外来乙", "部署代价校验", "打出宣告", "部署词条", "转发到处理端", "after" },
            run.Trace);
    }

    /// <summary>2／3：门禁在 before 相位 Stop 后——外来边、四条自有边、after 全不执行；前置写入保留。</summary>
    [Fact]
    public async Task 加入战场_Stop语义_门禁命中后后续边与after不执行_且before写入保留()
    {
        var run = await RunOrderAsync(EngineProcesses.JoinDeploy, stopInBefore: true);

        Assert.Equal(new[] { "前置", "门禁" }, run.Trace);
        Assert.True(run.Result.Stopped);
        Assert.Equal(7, run.Observed);
    }

    /// <summary>3／3：结果位＝Applied，部署落地到目标阵线（与迁移前同一断言）。</summary>
    [Fact]
    public async Task 加入战场_结果位_Applied且部署落地()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player);
        var slot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0);

        var result = await match.CommandManager.JoinBattlefieldAction.DeployAsync(unit, slot, player);

        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.Equal(1, result.Targets[0].AppliedAmount);
        Assert.Null(result.Targets[0].Reason);
        Assert.True(unit.TryGetData<UnitStateData>(out var state));
        Assert.Same(match.Battlefield.PlayerASupportLine, state!.Line);
    }

    // ══════════════════════ ⑥ damage ══════════════════════

    /// <summary>
    /// 1／3：段次序＝前置（before）→ 外来甲、外来乙 → 自有边（last）→ after。
    /// <para>外来边先跑＝重甲（旧 <c>Initiate</c>）在"转发"之前改写数值；扣数值在 <c>damage-apply</c>（同为 last）。</para>
    /// </summary>
    [Fact]
    public async Task 伤害_段次序_前置先于外来挂载边_自有边last收口()
    {
        var run = await RunOrderAsync(EngineProcesses.CombatDamage);

        Assert.Equal(new[] { "前置", "外来甲", "外来乙", "转发到处理端", "after" }, run.Trace);
    }

    /// <summary>2／3：免疫（before + Stop）命中后——外来边、自有转发、after 全不执行；前置写入保留。</summary>
    [Fact]
    public async Task 伤害_Stop语义_免疫命中后后续边与after不执行_且before写入保留()
    {
        var run = await RunOrderAsync(EngineProcesses.CombatDamage, stopInBefore: true);

        Assert.Equal(new[] { "前置", "门禁" }, run.Trace);
        Assert.True(run.Result.Stopped);
        Assert.Equal(7, run.Observed);
    }

    /// <summary>3／3：结果位＝Applied，效果伤害扣减防御（与迁移前同一断言）。</summary>
    [Fact]
    public async Task 伤害_结果位_Applied且扣减防御()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var target = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.BeastId, 0);
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.WeakId, 1);
        var defenseBefore = target.Modifiers.GetEffectiveValue(CardStatFields.Defense);

        // 门面（DamagePipeline）把结果压成 bool；结果位断言要动作返回的 ActionResult，故经 .Action 取同一实例。
        var applied = await match.CommandManager.DamagePipeline.Action.EffectAsync(dealer, target, 2);

        Assert.Equal(ActionOutcomeKind.Applied, applied.Targets[0].Outcome);
        Assert.Equal(2, applied.Targets[0].AppliedAmount); // 实扣量＝请求量（无减伤路径）
        Assert.Null(applied.Targets[0].Reason);
        Assert.Equal(defenseBefore - 2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    // ══════════════════════ ⑦ order ══════════════════════

    /// <summary>
    /// 1／3：段次序＝前置（旧宣告点／反制相位）→ 外来甲、外来乙 → 自有边（last＝施放结算）→ after。
    /// </summary>
    [Fact]
    public async Task 指令_段次序_前置先于外来挂载边_自有边last收口()
    {
        var run = await RunOrderAsync(EngineProcesses.OrderCast);

        Assert.Equal(new[] { "前置", "外来甲", "外来乙", "施放结算", "after" }, run.Trace);
    }

    /// <summary>2／3：反制（before + Stop）命中后——外来边、施放结算、after 全不执行；前置写入保留。</summary>
    [Fact]
    public async Task 指令_Stop语义_反制命中后后续边与after不执行_且before写入保留()
    {
        var run = await RunOrderAsync(EngineProcesses.OrderCast, stopInBefore: true);

        Assert.Equal(new[] { "前置", "门禁" }, run.Trace);
        Assert.True(run.Result.Stopped);
        Assert.Equal(7, run.Observed);
    }

    /// <summary>3／3：结果位＝Applied，且宣告 → 扣费 → 离手（与迁移前同一断言）。</summary>
    [Fact]
    public async Task 指令_结果位_Applied且扣费离手()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, player, PlayChainTestKit.CommandCheapId);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.CommandManager.Actions.Order.ExecuteAsync(order, null, 0);

        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.Equal(0, result.Targets[0].AppliedAmount); // 施放完成不产生"实扣量"（扣费在入口，非伤害落地）
        Assert.Null(result.Targets[0].Reason);
        Assert.Equal(new[] { GameUpdates.CardPlayed, GameUpdates.PointChanged }, recorder.Types);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(order, player.Hand);
    }

    // ══════════════════════ 场景构件 ══════════════════════

    /// <summary>一次模型级执行的观测结果（trace／门禁读到的值／成员执行结果）。</summary>
    private sealed record RunResult(
        List<string> Trace, object? Observed, MemberExecutionResult Result);

    /// <summary>
    /// 在**新模型**上按被测成员的真实声明（名字＋位次）跑一次成员执行：
    /// 一个 <c>before</c> 前置（写 <c>amount=7</c>）、两条外来挂载边（追加序，第一条可选 <c>Stop</c>）、
    /// 该端的自有边（声明序）、一个 <c>after</c>。
    /// </summary>
    /// <param name="memberName">被测成员名（<see cref="EngineProcesses"/> 常量）。</param>
    /// <param name="stopInBefore">第二条 <c>before</c>（门禁）是否调用 <c>ctx.Stop()</c>。</param>
    private static async Task<RunResult> RunOrderAsync(string memberName, bool stopInBefore = false)
    {
        // 被测成员的位次必须来自真实声明（U4 的标注如果有漂移，这两条断言先红）。
        var declaration = EngineProcesses.Members.Single(m => m.Name == memberName);
        Assert.Equal(EngineProcesses.PositionOf(memberName), declaration.OwnEdgePosition);
        Assert.Equal(OwnEdgePosition.Last, declaration.OwnEdgePosition);

        var trace = new List<string>();
        var handlers = new HandlerRegistry();
        object? observed = null;
        var amount = new ViewSpec(new[]
        {
            new ViewFieldDefinition("amount", ViewFieldType.Number, writable: true, required: false),
        });

        var member = new MemberDefinition(
            memberName,
            amount,
            MemberVisibility.All,
            ownEdges: OwnEdges[memberName].Select(label => Own(trace, label)).ToArray(),
            ownEdgePosition: declaration.OwnEdgePosition);

        var catalog = new PrefabCatalog()
            .Add(new PrefabDefinition("engine", EntityKind.Engine, members: new[] { member }))
            .Add(new PrefabDefinition("card.base", EntityKind.Card))
            .Add(Probe("comp.a", trace, "外来甲", stop: false))
            .Add(Probe("comp.b", trace, "外来乙", stop: false));

        var runtime = new OrchestrationRuntime(catalog, handlers);
        var target = WrapTarget.ForMember(MemberAddress.Create(AddressScope.Engine, memberName));

        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.Before, "h.before"),
            handlers.Register("h.before", (view, _, _) =>
            {
                trace.Add("前置");
                ((ViewCarrier)view).Set("amount", 7);
                return Task.CompletedTask;
            }),
            new DeclarationOrder(0, 0, 0));
        if (stopInBefore)
        {
            // 门禁相位（旧 Veto／Initiate 的等价形态）：声明序在"前置"之后（before＝FIFO），命中即停。
            runtime.Wraps.Register(
                new WrapDeclaration(target, WrapperPhase.Before, "h.gate"),
                handlers.Register("h.gate", (view, ctx, _) =>
                {
                    trace.Add("门禁");
                    observed = ((ViewCarrier)view).Get("amount");
                    ctx.Stop();
                    return Task.CompletedTask;
                }),
                new DeclarationOrder(0, 1, 0));
        }

        runtime.Wraps.Register(
            new WrapDeclaration(target, WrapperPhase.After, "h.after"),
            handlers.Register("h.after", (_, _, _) =>
            {
                trace.Add("after");
                return Task.CompletedTask;
            }),
            new DeclarationOrder(0, 0, 0));

        var host = runtime.CreateEntity("card.base", "宿主");
        var first = runtime.CreateEntity("comp.a", "甲", host);
        var second = runtime.CreateEntity("comp.b", "乙", host);
        runtime.MountAtRuntime(first, "probe", "engine#" + memberName);
        runtime.MountAtRuntime(second, "probe", "engine#" + memberName);

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        var result = await runtime.RunMemberAsync(runtime.Engine, memberName, data);

        return new RunResult(trace, observed, result);
    }

    /// <summary>外来挂载边（组件成员）：执行时记一条 trace，可选 <c>Stop</c>。</summary>
    private static PrefabDefinition Probe(string id, List<string> trace, string label, bool stop)
        => new(
            id,
            EntityKind.Component,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition("probe", ViewSpec.Empty, MemberVisibility.Mountable, new[] { Own(trace, label, stop) }),
            });

    /// <summary>一条自有边（同步逻辑）：记 trace，可选 <c>ctx.Stop()</c>。</summary>
    private static OwnedEdge Own(List<string> trace, string label, bool stop = false)
        => new((KernelHandler)((_, ctx, _) =>
        {
            trace.Add(label);
            if (stop)
            {
                ctx.Stop();
            }

            return Task.CompletedTask;
        }));
}
