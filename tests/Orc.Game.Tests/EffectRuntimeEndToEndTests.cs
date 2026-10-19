using Orc.Cards;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.EffectParsing.Compilation;
using Orc.Game.EffectParsing.Dsl;
using Orc.Game.EffectParsing.Parsing;
using Orc.Game.EffectParsing.Templates;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>端到端可运行验证（E1-35 续）：把各模板类别逐一放进真实对局跑，断言世界状态。</summary>
[Collection("IntelligenceStaticSerial")]
public class EffectRuntimeEndToEndMoreTests
{
    /// <summary>「加入时 → 将 1 张“步兵”加入手中」：验证 **卡名 → 牌库 → 手牌** 的真实落地。</summary>
    [Fact]
    public async Task Generated_Effect_Adds_Named_Card_To_Hand()
    {
        const string cardId = "u_parser_hand_e2e";
        var (match, playerA, _) = await AssembleAsync(
            "友方单位加入时，将 1 张“步兵”加入手中。",
            cardId);

        var handBefore = playerA.Hand.Count;
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, cardId, 1);

        Assert.Equal(handBefore + 1, playerA.Hand.Count); // 牌库按名解析成功 → 真加了一张手牌
    }

    /// <summary>
    /// 「攻击后 → 抽一张牌」：验证 **inject 型监听**（挂到对局级「单位攻击触发器」）能真跑。
    /// <para>
    /// **E1-36 途中已修掉的前置项（皆为真 bug）**：
    /// ① 求值器从未装配（csx 恒 `evaluator-missing`）；② `unit.*` 载荷的归属守卫恒假（读 `view.Card` 而载荷键是 `Unit`）；
    /// ③ inject 型主触发器误设为 `active` ⇒ 被 `ApplyFrameworkInjects` 按"非动态被动形态"隔离（须 `passive`）；
    /// ④ **视图类型解析失败**——内核 `DynamicEffectBuilder.ResolveViewType` 用 `Type.GetType(名)`，对**非程序集限定名**
    ///   只搜"调用程序集+核心库"，故跨程序集的 `Orc.Game.Commanding.UnitAttackTriggerView` 解析不了 ⇒ 整个效果被隔离。
    ///   已修：模板用**程序集限定名**（`, Orc.Game`）＋ 编译器剥掉程序集部分再写 csx；
    /// ⑤ 内核 `TriggerReflection.Unregister` 取自**开放式泛型** `typeof(Trigger&lt;&gt;)` ⇒ `Invoke` 必抛（改按实例运行时类型反射）；
    /// ⑥ **纯注入型效果挂不上总线**——`DynamicPassiveEffect.MountMainTrigger` 对"无 hooks"仍调 `Mount` ⇒ 抛 ⇒ `RollbackMount()`
    ///    **连注入一起撤销**（改：无 hooks ⇒ 视为注入载体、不挂总线）。
    /// </para>
    /// <para>
    /// **E1-37 最终根因（决定性探针取证，此前多轮推测均不成立）**：处理器**确实被调用了**，是在**调用内抛异常**——
    /// 根日志留下 <c>[Error] 单位攻击触发器/e1 | … | kw=exception:TargetInvocationException</c>；
    /// 而 `Trigger&lt;TView&gt;.RunEventsAsync` 对"事件内业务异常"是**隔离记录并继续**，故攻击命令照常 `Success`、
    /// 外部表现**等同于"处理器没被调用"**（此前"未被调用"的结论是把隔离记录当成了静默）。
    /// 异常来源＝宿主包装 `WrapWithHostCore` 走 `PropertyInfo.SetValue(view, host)`：
    /// `view` 的真实类型是 `ViewProxyFactory` 生成的**代理**，其 setter 被烘焙成权限闸门（`[Read]` 属性禁止写入）⇒ 必抛。
    /// 修法＝**宿主注入改走数据面**（`ctx.Data["Host"] = HostOrNull`，与内核 hook 路径同手法）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Generated_Attack_Listener_Runs_After_Attack()
    {
        const string cardId = "u_parser_attack_e2e";
        var bridge = new MockTargeterBridge();
        var (match, playerA, playerB) = await AssembleAsync(
            "友方单位攻击后，抽一张牌。",
            cardId,
            bridge);

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, cardId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var handBefore = playerA.Hand.Count;
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        Assert.Equal(handBefore + 1, playerA.Hand.Count); // 攻击链上挂的 csx 真执行了
    }

    /// <summary>
    /// 「本单位造成伤害时 → 抽一张牌」：验证 **E1-47 来源侧伤害信号** `unit.damage.dealt` ＋
    /// **载荷自指守卫**（`ReferenceEquals(view.Unit, self)`）在真对局里生效——
    /// 攻击者自己造成伤害才触发（被攻击者的反击方向不触发：其 `Unit` ≠ 宿主）。
    /// </summary>
    [Fact]
    public async Task Generated_Damage_Dealt_Listener_Draws_Only_For_Own_Damage()
    {
        const string cardId = "u_parser_dealt_e2e";
        var bridge = new MockTargeterBridge();
        var (match, playerA, playerB) = await AssembleAsync(
            "本单位造成伤害时，抽一张牌。",
            cardId,
            bridge);

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, cardId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var handBefore = playerA.Hand.Count;
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.False(attacker.GetData<UnitStateData>().IsDestroyed);

        // 恰好 +1：只有"本单位造成伤害"方向触发（被攻击者的反击方向被自指守卫挡下）
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
    }

    /// <summary>
    /// **E1-54 事件卡属性过滤的四个维度**（tag／卡类型／阵营／卡名）在真对局里逐一生效：
    /// 每个维度一个监听宿主，只有**属性匹配**的被使用卡才放行（不匹配 ⇒ 零效果）。
    /// </summary>
    [Fact]
    public async Task Generated_EventCard_Attribute_Filters_Fire_Only_For_Matching_Cards()
    {
        const string attrHost = "u_host_attr";   // 承载 tag ＋ 阵营 两条监听
        const string kindHost = "u_host_kind";   // 承载 卡类型 ＋ 卡名 两条监听
        const string navyId = "u_navy_probe";
        const string plainId = "u_plain_probe";
        const string britishId = "u_british_probe";
        const string planId = "u_plan_probe";

        // 四个维度各一条监听（支援线位有限 ⇒ 两张宿主各挂两条；维度互斥 ⇒ 一次使用最多命中一条）
        var listeners = new (string Host, string Text)[]
        {
            (attrHost, "友方使用海军牌时，抽一张牌。"),
            (attrHost, "友方使用英国牌时，抽一张牌。"),
            (kindHost, "敌方指令使用时，抽一张牌。"),
            (kindHost, "友方使用“计划”时，抽一张牌。"),
        };

        var parser = EffectParser.CreateDefault(out var lexFailures);
        Assert.Empty(lexFailures);
        var templates = EffectTemplateLoader.LoadDirectory(EffectTemplateLoader.DefaultDirectory);
        Assert.Empty(templates.Failures);
        var ops = OpTemplateCatalog.LoadDirectory(OpTemplateCatalog.DefaultDirectory, out var opFailures);
        Assert.Empty(opFailures);
        var compiler = new EffectCompiler(templates.Templates, ops);
        var registry = new CardEffectRegistry();
        var definitions = new List<CardDefinitionEntry>();
        var prefabs = new List<EffectSnapshot>();
        var prefabIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (host, text) in listeners)
        {
            var parse = parser.Parse(text);
            Assert.Empty(parse.Unresolved);
            Assert.Equal("played_basic", Assert.Single(parse.Effects).Template);
            var snapshot = compiler.Compile(parse.Effects[0], $"effect.e2e.{host}.{prefabs.Count}");
            prefabs.Add(snapshot);
            if (!prefabIds.TryGetValue(host, out var ids))
            {
                prefabIds[host] = ids = new List<string>();
            }

            ids.Add(snapshot.Root.Id);
        }

        foreach (var (host, ids) in prefabIds)
        {
            registry.DeclarePrefab(host, ids);
            definitions.Add(new CardDefinitionEntry(host, new CardDefinition(
                $"宿主{host}", 1, 1, 2, 5,
                unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)));
        }

        definitions.Add(new CardDefinitionEntry(navyId, new CardDefinition(
            "海军兵", 1, 1, 2, 5, tags: new[] { "海军" },
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Japan, rarity: Rarity.Standard)));
        definitions.Add(new CardDefinitionEntry(plainId, new CardDefinition(
            "普通兵", 1, 1, 2, 5,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Japan, rarity: Rarity.Standard)));
        definitions.Add(new CardDefinitionEntry(britishId, new CardDefinition(
            "英国兵", 1, 1, 2, 5,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Britain, rarity: Rarity.Standard)));
        definitions.Add(new CardDefinitionEntry(planId, new CardDefinition(
            "计划", 1, 1, 2, 5,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Japan, rarity: Rarity.Standard)));
        const string commandId = "u_cmd_probe";
        definitions.Add(new CardDefinitionEntry(commandId, new CardDefinition(
            "敌方指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0, CardCategory.Command,
            faction: Faction.Germany, rarity: Rarity.Standard)));

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry,
            extraDefinitions: definitions.ToArray());
        foreach (var prefab in prefabs)
        {
            match.Engine.Prefabs.RegisterPrefab(prefab);
        }

        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 宿主逐一进场（各占一个支援位——其监听效果须真实装载）
        var slot = 1; // 支援位 0 由测试基座占用
        foreach (var host in prefabIds.Keys)
        {
            await CommandTestKit.PrepareOnSupportAsync(match, playerA, host, slot++);
        }

        // 被使用卡只需**实例**（不必上场/入手）：直接实例化并装载（不入手 ⇒ 不干扰手牌计数）
        var navy = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, playerA, navyId, toHand: false);
        var plain = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, playerA, plainId, toHand: false);
        var british = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, playerA, britishId, toHand: false);
        var plan = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, playerA, planId, toHand: false);
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerB, commandId, toHand: false);

        // 反例一律用"普通兵"（无 tag／非指令／日本／名不匹配 ⇒ 四个维度全不命中）
        // ① tag：`海军` 匹配 ⇒ 抽牌；普通兵 ⇒ 不抽
        var before = playerA.Hand.Count;
        await GameUpdates.EmitCardPlayed(match.Engine, plain, playerA);
        Assert.Equal(before, playerA.Hand.Count);
        await GameUpdates.EmitCardPlayed(match.Engine, navy, playerA);
        Assert.Equal(before + 1, playerA.Hand.Count);

        // ② 卡类型＋归属：敌方**指令** ⇒ 抽牌；友方**单位**（普通兵）⇒ category 守卫挡下
        before = playerA.Hand.Count;
        await GameUpdates.EmitCardPlayed(match.Engine, plain, playerA);
        Assert.Equal(before, playerA.Hand.Count);
        await GameUpdates.EmitCardPlayed(match.Engine, command, playerB);
        Assert.Equal(before + 1, playerA.Hand.Count);

        // ③ 阵营：英国单位 ⇒ 抽牌；日本单位（普通兵）⇒ 不抽
        before = playerA.Hand.Count;
        await GameUpdates.EmitCardPlayed(match.Engine, plain, playerA);
        Assert.Equal(before, playerA.Hand.Count);
        await GameUpdates.EmitCardPlayed(match.Engine, british, playerA);
        Assert.Equal(before + 1, playerA.Hand.Count);

        // ④ 卡名：`计划` ⇒ 抽牌；其它名字（普通兵）⇒ 不抽
        before = playerA.Hand.Count;
        await GameUpdates.EmitCardPlayed(match.Engine, plain, playerA);
        Assert.Equal(before, playerA.Hand.Count);
        await GameUpdates.EmitCardPlayed(match.Engine, plan, playerA);
        Assert.Equal(before + 1, playerA.Hand.Count);
    }

    /// <summary>
    /// 「友方单位造成伤害时 → 抽一张牌」：验证 **E1-50 主体归属面守卫**在真对局里生效——
    /// 友方（＝宿主自己）造成伤害才触发；**敌方（被攻击者的反击方向）被守卫挡下**（否则会 +2）。
    /// </summary>
    [Fact]
    public async Task Generated_Friendly_Damage_Dealt_Listener_Ignores_Enemy_Damage()
    {
        const string cardId = "u_parser_owner_e2e";
        var bridge = new MockTargeterBridge();
        var (match, playerA, playerB) = await AssembleAsync(
            "友方单位造成伤害时，抽一张牌。",
            cardId,
            bridge);

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, cardId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BeastId, 0); // 会反击
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var handBefore = playerA.Hand.Count;
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 恰好 +1：友方（宿主）造成伤害命中；敌方的反击方向被归属守卫挡下
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
    }

    /// <summary>
    /// 「友方使用情报牌时 → 抽一张牌」：验证 **E1-42 事件卡属性过滤（词条维度）**在真对局里生效——
    /// 守卫 `view.Card.Keywords.Has("情报")` **只对带该词条的被使用卡**放行（不带 ⇒ 零效果）。
    /// </summary>
    [Fact]
    public async Task Generated_EventCard_Keyword_Listener_Fires_Only_For_Matching_Played_Card()
    {
        const string hostId = "u_parser_intel_e2e";
        const string intelId = "u_intel_probe";
        const string plainId = "u_plain_probe";

        var parser = EffectParser.CreateDefault(out var lexFailures);
        Assert.Empty(lexFailures);
        var parse = parser.Parse("友方使用情报牌时，抽一张牌。");
        Assert.Empty(parse.Unresolved);
        var dsl = Assert.Single(parse.Effects);
        Assert.Equal("played_basic", dsl.Template);

        var templates = EffectTemplateLoader.LoadDirectory(EffectTemplateLoader.DefaultDirectory);
        Assert.Empty(templates.Failures);
        var ops = OpTemplateCatalog.LoadDirectory(OpTemplateCatalog.DefaultDirectory, out var opFailures);
        Assert.Empty(opFailures);
        var snapshot = new EffectCompiler(templates.Templates, ops).Compile(dsl, "effect.e2e.intel");

        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(hostId, new[] { snapshot.Root.Id });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry,
            extraDefinitions: new[]
            {
                new CardDefinitionEntry(hostId, new CardDefinition(
                    "情报监听兵", 1, 1, 2, 5,
                    unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
                new CardDefinitionEntry(intelId, new CardDefinition(
                    "情报兵", 1, 1, 2, 5,
                    keywords: new[] { new KeywordDeclaration(KeywordIds.Intelligence, 1) },
                    unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
                new CardDefinitionEntry(plainId, new CardDefinition(
                    "普通兵", 1, 1, 2, 5,
                    unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
            });

        match.Engine.Prefabs.RegisterPrefab(snapshot);
        await match.Initialize();
        var playerA = match.Players[0];

        await CommandTestKit.PrepareOnSupportAsync(match, playerA, hostId, 1);
        var intel = await CommandTestKit.PrepareOnSupportAsync(match, playerA, intelId, 2);
        var plain = await CommandTestKit.PrepareOnSupportAsync(match, playerA, plainId, 1);

        var handBefore = playerA.Hand.Count;

        // 非情报牌被使用 ⇒ 事件卡词条守卫不通过 ⇒ 零效果
        await GameUpdates.EmitCardPlayed(match.Engine, plain, playerA);
        Assert.Equal(handBefore, playerA.Hand.Count);

        // 情报牌被使用 ⇒ 守卫放行 ⇒ csx 真执行、抽 1 张
        await GameUpdates.EmitCardPlayed(match.Engine, intel, playerA);
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
    }

    /// <summary>
    /// 「友方单位交战并存活后 → 抽一张牌」：验证 **E1-39 新信号 `unit.combat.survived`**（hook 型监听）端到端可跑——
    /// 攻击者（解析兵）与敌方脆皮交战后存活 ⇒ 归属守卫（同主）放行 ⇒ csx 真执行 ⇒ 手牌 +1。
    /// </summary>
    [Fact]
    public async Task Generated_Combat_Survived_Listener_Runs_After_Combat()
    {
        const string cardId = "u_parser_combat_e2e";
        var bridge = new MockTargeterBridge();
        var (match, playerA, playerB) = await AssembleAsync(
            "友方单位交战并存活后，抽一张牌。",
            cardId,
            bridge);

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, cardId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var handBefore = playerA.Hand.Count;
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.False(attacker.GetData<UnitStateData>().IsDestroyed);

        Assert.Equal(handBefore + 1, playerA.Hand.Count); // 交战存活信号上的 csx 真执行了
    }

    private static async Task<(Match Match, Player PlayerA, Player PlayerB)> AssembleAsync(
        string cardFaceText,
        string cardId,
        MockTargeterBridge? bridge = null)
    {
        var parser = EffectParser.CreateDefault(out var lexFailures);
        Assert.Empty(lexFailures);

        var parse = parser.Parse(cardFaceText);
        Assert.Empty(parse.Unresolved);
        return await AssembleFromDslAsync(Assert.Single(parse.Effects), cardId, bridge);
    }

    private static async Task<(Match Match, Player PlayerA, Player PlayerB)> AssembleFromDslAsync(
        DslEffectInstance dsl,
        string cardId,
        MockTargeterBridge? bridge)
    {
        var templates = EffectTemplateLoader.LoadDirectory(EffectTemplateLoader.DefaultDirectory);
        Assert.Empty(templates.Failures);
        var ops = OpTemplateCatalog.LoadDirectory(OpTemplateCatalog.DefaultDirectory, out var opFailures);
        Assert.Empty(opFailures);
        var compiler = new EffectCompiler(templates.Templates, ops);
        var snapshot = compiler.Compile(dsl, "effect.e2e." + cardId);

        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(cardId, new[] { snapshot.Root.Id });

        var match = CommandTestKit.CreateCommandMatch(
            bridge: bridge,
            effectRegistry: registry,
            extraDefinitions: new[]
            {
                new CardDefinitionEntry(
                    cardId,
                    new CardDefinition(
                        "解析兵", 1, 1, 2, 5,
                        unitTypes: new[] { UnitType.Infantry },
                        faction: Faction.Germany,
                        rarity: Rarity.Standard)),
            });

        match.Engine.Prefabs.RegisterPrefab(snapshot);
        await match.Initialize();
        return (match, match.Players[0], match.Players[1]);
    }
}

/// <summary>
/// **DSL 端到端可运行验证**（E1-34）：卡面文本 → 解析 → DSL → 生成带 csx 的效果预制体
/// → 注册进引擎 + 按卡声明 → 真实对局中上场 → **csx 真执行** → 经 <c>EffectRuntime</c> 落到游戏层。
/// 断言的是**世界状态**（敌方防御真的掉了），不是信号或 JSON。
/// </summary>
public class EffectRuntimeEndToEndTests
{
    [Fact]
    public async Task Generated_Effect_Runs_In_Match_And_Deals_Damage()
    {
        // ① 解析 → DSL（加入型监听，避开"部署需真实打出"的路径）
        var parser = EffectParser.CreateDefault(out var lexFailures);
        Assert.Empty(lexFailures);

        var parse = parser.Parse("友方单位加入时，对一个敌方单位造成 1 点伤害。");
        Assert.Empty(parse.Unresolved);
        var dsl = Assert.Single(parse.Effects);
        Assert.Equal("join_basic", dsl.Template);

        // ② 转换 → 带 csx 的效果预制体
        var templates = EffectTemplateLoader.LoadDirectory(EffectTemplateLoader.DefaultDirectory);
        Assert.Empty(templates.Failures);
        var ops = OpTemplateCatalog.LoadDirectory(OpTemplateCatalog.DefaultDirectory, out var opFailures);
        Assert.Empty(opFailures);
        var compiler = new EffectCompiler(templates.Templates, ops);
        var snapshot = compiler.Compile(dsl, "effect.e2e");
        var csx = Assert.Single(snapshot.Root.MainTrigger.Events).CsxSource!;
        Assert.Contains("Orc.Game.Effects.EffectRuntime.ResolveFor", csx, StringComparison.Ordinal);

        // ③ 装配：预制体注册进引擎 + 按卡声明（专用卡 id，避免其他卡共享监听）
        const string parserCardId = "u_parser_e2e";
        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(parserCardId, new[] { snapshot.Root.Id });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry,
            extraDefinitions: new[]
            {
                new CardDefinitionEntry(
                    parserCardId,
                    new CardDefinition(
                        "解析兵", 1, 1, 2, 5,
                        unitTypes: new[] { UnitType.Infantry },
                        faction: Faction.Germany,
                        rarity: Rarity.Standard)),
            });

        match.Engine.Prefabs.RegisterPrefab(snapshot); // 卡加载前注册
        await match.Initialize();

        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // ④ 场上放一个敌方目标（轰炸机：防 2；不放前排则不可被"前线/支援线"选到——放前线）
        var bomber = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 0);
        Assert.Equal(2, bomber.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        // ⑤ 触发：加入路径上场 → 发 unit.joined → 挂载的 csx 处理器执行
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, parserCardId, 1);

        // ⑥ 断言世界状态：敌方防御 2 → 1（效果真的落地了）
        Assert.Equal(1, bomber.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.False(bomber.GetData<UnitStateData>().IsDestroyed);
    }
}
