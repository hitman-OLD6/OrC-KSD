using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.EffectParsing.Compilation;
using Orc.Game.EffectParsing.Dsl;
using Orc.Game.EffectParsing.Templates;
using Orc.Game.Effects;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 词条效果化·批 0（前置机制）验收：
/// ①参值→模板/效果参数化通道（DSL 值字段 → op 模板值占位符渲染 → csx 授予调用携带参值 → 运行时承接）；
/// ②效果运行时口（撤销／参值改写／内容型授予——csx 受控入口；词条面统一判定 CardBase＋Hq）。
/// 覆盖：DSL 赋值与序列化往返、渲染与编译衔接（未提供＝既有形态）、csx 实编译（官方管道带参值授予；
/// 逃生舱撤销/改写）、行为链（重甲减伤读数/行为随动/行为不再生效/钳制）、内容型授予全生命周期、Hq 可达与降级。
/// </summary>
public class KeywordEffectChannelTests
{
    // ---------- 基建 ----------

    /// <summary>逃生舱脚本：对宿主自身撤销「重甲」（csx 调用运行时新口——受控入口）。</summary>
    private const string RevokeArmorScript =
        """
        var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
        if (runtime is not null)
        {
            await runtime.RevokeAsync(self!, "重甲", ct);
        }
        """;

    /// <summary>逃生舱脚本：将宿主自身的「重甲」参值改写为 3。</summary>
    private const string SetArmorThreeScript =
        """
        var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
        if (runtime is not null)
        {
            await runtime.SetKeywordValueAsync(self!, "重甲", 3, ct);
        }
        """;

    /// <summary>逃生舱脚本：将宿主自身的「重甲」参值置空（参值位可空）。</summary>
    private const string ClearArmorValueScript =
        """
        var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
        if (runtime is not null)
        {
            await runtime.SetKeywordValueAsync(self!, "重甲", null, ct);
        }
        """;

    private static EffectSnapshot Compile(DslEffectInstance dsl, string effectId)
    {
        var templates = EffectTemplateLoader.LoadDirectory(EffectTemplateLoader.DefaultDirectory);
        Assert.Empty(templates.Failures);
        var ops = OpTemplateCatalog.LoadDirectory(OpTemplateCatalog.DefaultDirectory, out var opFailures);
        Assert.Empty(opFailures);
        return new EffectCompiler(templates.Templates, ops).Compile(dsl, effectId);
    }

    /// <summary>csx 实编译校验（对齐既有先例：逐事件经求值器编译，编译错误＝断言失败）。</summary>
    private static void AssertCompiles(EffectSnapshot snapshot, string sample)
    {
        var evaluator = new CSharpScriptEvaluator();
        foreach (var node in new[] { snapshot.Root.MainTrigger }.Concat(snapshot.Root.OtherTriggers))
        {
            var viewType = ResolveViewType(node.ViewTypeName);
            foreach (var ev in node.Events)
            {
                var result = evaluator.Evaluate(new ScriptRequest(ev.CsxSource!, ev.EntryName, viewType));
                Assert.True(
                    result.Success,
                    $"{sample} 生成的 csx 编译失败（{result.ErrorCategory}）：{result.Error}{Environment.NewLine}{ev.CsxSource}");
            }
        }
    }

    private static Type ResolveViewType(string name)
    {
        var bareName = name.Split(',')[0].Trim();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(bareName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return typeof(CardEventView);
    }

    private static CardDefinitionEntry HostDefinition(string id, string name, int attack = 3, int defense = 5)
        => new(id, new CardDefinition(
            name, 1, 1, attack, defense,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    /// <summary>组装「加入时 → 授予（keyword, value）」的官方管道路径场景（编译＋csx 实编译校验＋对局＋进场触发授予）。</summary>
    private static async Task<(Match Match, UnitCard Host, MockTargeterBridge Bridge)> AssembleJoinGrantAsync(
        string hostId, string keyword, int value)
    {
        var dsl = new DslEffectInstance(
            "join_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_event"] = new(new[]
                {
                    new DslOp("grant", new DslSelector("self"), keyword: keyword, value: value),
                }),
            });
        var snapshot = Compile(dsl, "effect.p0.grant." + hostId);

        // 渲染衔接：带参值形态进入 csx（四参调用——逗号＋整数字面量）。
        var csx = Assert.Single(snapshot.Root.MainTrigger.Events).CsxSource!;
        Assert.Contains($"GrantAsync(target, \"{keyword}\", {value}, ct, self)", csx, StringComparison.Ordinal);
        AssertCompiles(snapshot, $"grant {keyword}={value}");

        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(hostId, new[] { snapshot.Root.Id });
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(
            bridge: bridge,
            effectRegistry: registry,
            extraDefinitions: new[] { HostDefinition(hostId, "参值授予兵") });
        match.Engine.Prefabs.RegisterPrefab(snapshot);
        await match.Initialize();
        var host = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], hostId, 1);
        return (match, host, bridge);
    }

    /// <summary>组装「使用本卡时 → csx 逃生舱脚本」场景（编译＋csx 实编译校验＋对局＋进场）。</summary>
    private static async Task<(Match Match, UnitCard Host, MockTargeterBridge Bridge)> AssembleEscapeAsync(
        string hostId, string escapeScript)
    {
        var dsl = new DslEffectInstance(
            "played_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_event"] = new(new[] { new DslOp("csx", script: escapeScript) }),
            });
        var snapshot = Compile(dsl, "effect.p0.escape." + hostId);
        AssertCompiles(snapshot, escapeScript);

        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(hostId, new[] { snapshot.Root.Id });
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(
            bridge: bridge,
            effectRegistry: registry,
            extraDefinitions: new[] { HostDefinition(hostId, "逃生舱兵") });
        match.Engine.Prefabs.RegisterPrefab(snapshot);
        await match.Initialize();
        var host = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], hostId, 1);
        return (match, host, bridge);
    }

    /// <summary>宿主攻击敌方坦克（真实攻击结算——反击 3）：返回后即可读双方防御变化。</summary>
    private static async Task AttackTankAsync(Match match, MockTargeterBridge bridge, UnitCard host)
    {
        var playerB = match.Players[1];
        var tank = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.TankId, 0);
        CommandTestKit.Activate(host);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, host, tank.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);
    }

    /// <summary>探针亡计内容效果：记录执行次数（内容可消费/卸载的证据）。</summary>
    private sealed class ProbeDeathrattleEffect : DeathrattleEffect
    {
        private readonly Func<DeathrattleContext, Task> _onExecute;

        public ProbeDeathrattleEffect(string name, Func<DeathrattleContext, Task> onExecute)
            : base(name)
        {
            _onExecute = onExecute;
        }

        public int Executions { get; private set; }

        protected override async Task OnExecuteAsync(DeathrattleContext context, CancellationToken ct)
        {
            Executions++;
            await _onExecute(context);
        }
    }

    // ---------- ① 参值→模板/效果参数化通道 ----------

    [Fact]
    public void Dsl_Value_RoundTrips_And_Validates()
    {
        // 授予 op 带参值 → 序列化往返一致；「未提供」与「显式 0」形态分明。
        var dsl = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[]
                {
                    new DslOp("grant", new DslSelector("self"), keyword: "重甲", value: 3),
                    new DslOp("grant", new DslSelector("self"), keyword: "情报", value: 0),
                }),
            });

        var back = DslJson.Deserialize(DslJson.Serialize(dsl));
        var ops = back.Fills["on_deploy"].Ops;
        Assert.Equal(3, ops[0].Value);
        Assert.Equal(0, ops[1].Value); // 显式 0：往返保持 0（非 null）
        Assert.Null(new DslOp("grant", keyword: "闪击").Value); // 未提供＝null 形态

        // 通道层不设值域校验：负数可携带（语义域由词条组件既有机制钳制）。
        Assert.Empty(DslOpRegistry.Validate(new DslOp("grant", keyword: "重甲", value: -1)));

        // value 仅授予类 op 可用——其它 op 携带＝明确拒绝（防模板静默忽略）。
        Assert.Contains(
            DslOpRegistry.Validate(new DslOp("damage", amount: 2, value: 3)),
            error => error.Contains("不支持参值", StringComparison.Ordinal));

        // JSON 反序列化路径同样拒绝（校验单一真源）。
        var ok = DslJson.TryDeserialize(
            """{ "template": "deploy_basic", "fills": { "on_deploy": { "ops": [ { "op": "draw", "count": 1, "value": 2 } ] } } }""",
            out _, out var error);
        Assert.False(ok);
        Assert.Contains("不支持参值", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Grant_Render_Carries_Value_And_Keeps_Legacy_Shape()
    {
        var catalog = OpTemplateCatalog.LoadDirectory(OpTemplateCatalog.DefaultDirectory, out var failures);
        Assert.Empty(failures);

        // 未提供参值：与既有渲染**逐字**一致（三参调用——不携带参值；既有无参值断言口径零修改）。
        var legacy = catalog.Render(new DslOp("grant", new DslSelector("self"), keyword: "闪击"));
        Assert.Contains("await runtime.GrantAsync(target, \"闪击\", ct, self);", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("\"闪击\", null", legacy, StringComparison.Ordinal);

        // 显式提供（含 0/负数）：四参调用（逗号＋整数字面量——「未提供 vs 显式 0」由此区分）。
        var valued = catalog.Render(new DslOp("grant", new DslSelector("self"), keyword: "重甲", value: 3));
        Assert.Contains("await runtime.GrantAsync(target, \"重甲\", 3, ct, self);", valued, StringComparison.Ordinal);

        var zero = catalog.Render(new DslOp("grant", new DslSelector("self"), keyword: "重甲", value: 0));
        Assert.Contains("await runtime.GrantAsync(target, \"重甲\", 0, ct, self);", zero, StringComparison.Ordinal);

        var negative = catalog.Render(new DslOp("grant", new DslSelector("self"), keyword: "重甲", value: -1));
        Assert.Contains("await runtime.GrantAsync(target, \"重甲\", -1, ct, self);", negative, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grant_With_Value_Through_Pipeline_Reads_Back_And_Applies_Behavior()
    {
        // G1（硬性）：经 DSL/编译管道授予重甲3 → 读回 3＋行为链（真实攻击结算减伤 3——反击 3-3=0）。
        const string hostId = "u_p0_grant";
        var (match, host, bridge) = await AssembleJoinGrantAsync(hostId, KeywordIds.Armor, 3);
        var playerB = match.Players[1];

        Assert.True(host.Keywords.Has(KeywordIds.Armor));
        Assert.Equal(3, KeywordRules.GetKeywordValue(host, KeywordIds.Armor));

        var tank = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.TankId, 0);
        CommandTestKit.Activate(host);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, host, tank.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 减伤 3 生效（未生效则为 5-3=2——可区分）；坦克受击 3 伤照常结算。
        Assert.Equal(5, host.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(2, tank.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task Grant_Overcap_Clamps_And_Intelligence_Reads_Back()
    {
        // G2（硬性）：授予重甲5（同管道）→ 读回 3（既有组件钳制；「放行、组件兜底」）。
        const string armorId = "u_p0_armor5";
        var (_, armorHost, _) = await AssembleJoinGrantAsync(armorId, KeywordIds.Armor, 5);
        Assert.True(armorHost.Keywords.Has(KeywordIds.Armor));
        Assert.Equal(3, KeywordRules.GetKeywordValue(armorHost, KeywordIds.Armor));

        // G3（可选，纳入）：授予情报2 → 读回 2（不承诺行为执行——情报执行面待办）。
        const string intelId = "u_p0_intel2";
        var (_, intelHost, _) = await AssembleJoinGrantAsync(intelId, KeywordIds.Intelligence, 2);
        Assert.True(intelHost.Keywords.Has(KeywordIds.Intelligence));
        Assert.Equal(2, KeywordRules.GetKeywordValue(intelHost, KeywordIds.Intelligence));
    }

    // ---------- ② 运行时口（逃生舱路径：撤销／参值改写） ----------

    [Fact]
    public async Task Revoke_Via_Escape_Hatch_Removes_Keyword_And_Behavior()
    {
        // R1（硬性）：授予重甲3 → 经逃生舱撤销 → 词条不存在＋参值不可读＋行为不再生效（伤害不再被减免）。
        const string hostId = "u_p0_revoke";
        var (match, host, bridge) = await AssembleEscapeAsync(hostId, RevokeArmorScript);
        var playerA = match.Players[0];
        var runtime = EffectRuntime.ResolveFor(host);
        Assert.NotNull(runtime);

        Assert.True(await runtime!.GrantAsync(host, KeywordIds.Armor, 3));
        Assert.Equal(3, KeywordRules.GetKeywordValue(host, KeywordIds.Armor));

        // 逃生舱（csx 实编译执行）：对宿主自身撤销。
        await GameUpdates.EmitCardPlayed(match.Engine, host, playerA);
        Assert.False(host.Keywords.Has(KeywordIds.Armor));
        Assert.Null(KeywordRules.GetKeywordValue(host, KeywordIds.Armor));

        // R2（硬性）：重复撤销＝幂等——逃生舱复跑无异常；直调返回 false。
        await GameUpdates.EmitCardPlayed(match.Engine, host, playerA);
        Assert.False(await runtime.RevokeAsync(host, KeywordIds.Armor));

        // 行为不再生效：反击 3 全数落地（防御 5-3=2；若减伤仍在则为 5——可区分）。
        await AttackTankAsync(match, bridge, host);
        Assert.Equal(2, host.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task SetValue_Via_Escape_Hatch_Changes_Value_And_Behavior_Follows()
    {
        // R3（硬性）：授予重甲1 → 经逃生舱改写为 3 → 读回 3＋行为随动（减伤 3）。
        const string hostId = "u_p0_setv";
        var (match, host, bridge) = await AssembleEscapeAsync(hostId, SetArmorThreeScript);
        var playerA = match.Players[0];
        var runtime = EffectRuntime.ResolveFor(host);
        Assert.NotNull(runtime);

        Assert.True(await runtime!.GrantAsync(host, KeywordIds.Armor, 1));
        Assert.Equal(1, KeywordRules.GetKeywordValue(host, KeywordIds.Armor));

        await GameUpdates.EmitCardPlayed(match.Engine, host, playerA);
        Assert.Equal(3, KeywordRules.GetKeywordValue(host, KeywordIds.Armor));

        // 行为随动：反击 3 全免（防御不变 5；若仍为 1 则 5-2=3——可区分）。
        await AttackTankAsync(match, bridge, host);
        Assert.Equal(5, host.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task SetValue_Via_Escape_Hatch_To_Null_Clears_Value_And_Behavior()
    {
        // R4（硬性）：改写置空（null）→ 读回 null＋行为不再减伤（「参值位可空」语义经新口保持）。
        const string hostId = "u_p0_clear";
        var (match, host, bridge) = await AssembleEscapeAsync(hostId, ClearArmorValueScript);
        var playerA = match.Players[0];
        var runtime = EffectRuntime.ResolveFor(host);
        Assert.NotNull(runtime);

        Assert.True(await runtime!.GrantAsync(host, KeywordIds.Armor, 3));

        await GameUpdates.EmitCardPlayed(match.Engine, host, playerA);
        Assert.True(host.Keywords.Has(KeywordIds.Armor)); // 词条仍在（标识保留）
        Assert.Null(KeywordRules.GetKeywordValue(host, KeywordIds.Armor)); // 参值已空

        // 行为不再减伤：反击 3 全落地（防御 5-3=2；若减伤仍在则为 5——可区分）。
        await AttackTankAsync(match, bridge, host);
        Assert.Equal(2, host.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task SetValue_Direct_Gate_Overcap_Clamps_And_Unknown_Keyword_Throws()
    {
        // R5（建议纳入）：改写超域值 → 钳制；改写不存在的词条 → 明确异常（既有语义透传）。
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);

        Assert.True(await runtime!.GrantAsync(unit, KeywordIds.Armor, 1));
        Assert.True(await runtime.SetKeywordValueAsync(unit, KeywordIds.Armor, 5));
        Assert.Equal(3, KeywordRules.GetKeywordValue(unit, KeywordIds.Armor));

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = runtime.SetKeywordValueAsync(unit, KeywordIds.Intelligence, 1);
        });

        // 参数契约与不可达分列：null target＝fail-fast（ArgumentNullException——非降级）。
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = runtime.RevokeAsync(null!, KeywordIds.Armor);
        });
    }

    // ---------- ② 运行时口（内容型授予：C# 行为级全生命周期） ----------

    [Fact]
    public async Task GrantWithContent_Direct_Gate_Full_Lifecycle()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var anchor = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(anchor);
        Assert.NotNull(runtime);

        // A1（硬性）：授予带内容 → 词条存在＋内容装载 → 死亡结算消费内容（真实链路）。
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 2);
        var probe = new ProbeDeathrattleEffect("门面探针", _ => Task.CompletedTask);
        Assert.True(await runtime!.GrantWithContentAsync(victim, KeywordIds.Deathrattle, probe));
        Assert.True(victim.Keywords.Has(KeywordIds.Deathrattle));
        Assert.Contains(probe, victim.Effects);
        Assert.True(probe.IsMounted);
        await victim.ApplyDefenseDamageAsync(victim.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(1, probe.Executions);

        // A2（硬性）：授予 → 移除 → 内容卸载（不可再消费）。
        var victim2 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        var probe2 = new ProbeDeathrattleEffect("卸载探针", _ => Task.CompletedTask);
        Assert.True(await runtime.GrantWithContentAsync(victim2, KeywordIds.Deathrattle, probe2));
        Assert.True(await runtime.RevokeAsync(victim2, KeywordIds.Deathrattle));
        Assert.False(victim2.Keywords.Has(KeywordIds.Deathrattle));
        Assert.False(probe2.IsMounted);
        await victim2.ApplyDefenseDamageAsync(victim2.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(0, probe2.Executions);

        // A3（硬性）：重复授予＝幂等（无操作 false；内容不重复装载）。
        var victim3 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);
        var probe3 = new ProbeDeathrattleEffect("幂等探针", _ => Task.CompletedTask);
        Assert.True(await runtime.GrantWithContentAsync(victim3, KeywordIds.Deathrattle, probe3));
        Assert.False(await runtime.GrantWithContentAsync(victim3, KeywordIds.Deathrattle, probe3));
        Assert.True(victim3.Keywords.Has(KeywordIds.Deathrattle));
        Assert.Single(victim3.Effects, effect => ReferenceEquals(effect, probe3));

        // A4（建议纳入）：空内容＝合法静默（结算无操作、不抛错）；未注册标识＝fail-fast、零残留。
        var victim4 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 2);
        Assert.True(await runtime.GrantWithContentAsync(victim4, KeywordIds.Deathrattle, content: null));
        Assert.True(victim4.Keywords.Has(KeywordIds.Deathrattle));
        await victim4.ApplyDefenseDamageAsync(victim4.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.True(victim4.GetData<UnitStateData>().IsDestroyed);

        var victim5 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = runtime.GrantWithContentAsync(victim5, "不存在的词条", content: null);
        });
        Assert.False(victim5.Keywords.Has("不存在的词条"));
    }

    // ---------- ② 运行时口（词条面统一判定：Hq 可达＋无词条面降级） ----------

    [Fact]
    public async Task Gates_Reach_Hq_And_Degrade_For_NonKeyword_Faces()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);

        // 词条面统一判定（一致性收编）：HQ 可达——授予（含参值）/改写/撤销全链真实生效。
        var hq = playerA.Hq;
        Assert.True(await runtime!.GrantAsync(hq, KeywordIds.Armor, 2));
        Assert.Equal(2, KeywordRules.GetKeywordValue(hq, KeywordIds.Armor));
        Assert.True(await runtime.SetKeywordValueAsync(hq, KeywordIds.Armor, 3));
        Assert.Equal(3, KeywordRules.GetKeywordValue(hq, KeywordIds.Armor));
        Assert.True(await runtime.RevokeAsync(hq, KeywordIds.Armor));
        Assert.Null(KeywordRules.GetKeywordValue(hq, KeywordIds.Armor));

        // 无词条面（纯内核 Card：非 CardBase/Hq）＝降级 false（服务不可达口径；不抛错）。
        var bare = new Card(new LogicEngine(), "裸卡");
        Assert.False(await runtime.GrantAsync(bare, KeywordIds.Armor));
        Assert.False(await runtime.GrantAsync(bare, KeywordIds.Armor, 2));
        Assert.False(await runtime.RevokeAsync(bare, KeywordIds.Armor));
        Assert.False(await runtime.SetKeywordValueAsync(bare, KeywordIds.Armor, 1));
        Assert.False(await runtime.GrantWithContentAsync(bare, KeywordIds.Deathrattle, content: null));
    }
}
