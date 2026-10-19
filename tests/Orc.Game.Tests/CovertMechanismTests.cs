using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S2（隐蔽机制——状态承载／豁免接线／揭示／信号／读取面）验收：
/// C1 被指令指向拒绝（生产装配默认链下经判定器两向断言）／C2 被攻击即揭示（揭示先于伤害结算——公共面行为断言）／
/// C3 主动攻击揭示（含 HQ 目标变体）／C4 移动不解除；C9 例外（请求级覆盖＋moding 改写口）；
/// 剔除点验证（无头选靶／光环受益／钳击配对；攻击候选不受影响）；揭示服务专项（幂等/顺序/重入/恰一次）＋
/// 数据映射专项（自含内嵌数据体 JSON → 加载路径 → 标记可读＋不再留痕）＋契约冻结。
/// 数据构造＝等价数据（内嵌数据体 JSON；经 <see cref="CardDataLoader"/> 真实加载路径——不依赖 outputs 外部产物）。
/// </summary>
public class CovertMechanismTests
{
    // ---------- 卡 id（自含数据；隐蔽词条标记方案代表卡） ----------

    private const string CovertScoutId = "cov_scout";        // 2/5 步兵＋covert（隐蔽侦察兵）
    private const string PlainUnitId = "cov_plain";          // 2/5 步兵（普通——对照）
    private const string CovertTankId = "cov_tank";          // 3/5 坦克＋covert（HQ 攻击场景）
    private const string PincerUnitId = "cov_pincer";        // 2/3 步兵＋pincer（钳击对照）
    private const string CovertPincerId = "cov_spincer";     // 2/3 步兵＋covert＋pincer（隐蔽来源场景）

    /// <summary>自含数据体 JSON 集 → 真实加载路径 → 定义集（每测例独立临时目录；加载后即删除——定义已构造）。</summary>
    private static IReadOnlyList<CardDefinitionEntry> LoadCovertDefinitions()
    {
        var jsons = new[]
        {
            CardJson(CovertScoutId, "隐蔽侦察兵", "Germany", 1, 1, 2, 5, "infantry", "covert", "OnlySpawnable"),
            CardJson(PlainUnitId, "普通警卫兵", "Germany", 1, 1, 2, 5, "infantry"),
            CardJson(CovertTankId, "隐蔽坦克", "Germany", 1, 1, 3, 5, "tank", "covert"),
            CardJson(PincerUnitId, "钳击警卫兵", "Germany", 1, 1, 2, 3, "infantry", "pincer"),
            CardJson(CovertPincerId, "隐蔽钳击兵", "Germany", 1, 1, 2, 3, "infantry", "covert", "pincer"),
        };

        var directory = Path.Combine(Path.GetTempPath(), "orc-covert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < jsons.Length; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"card{i:D2}.card.json"), jsons[i]);
            }

            var loaded = CardDataLoader.LoadDirectory(directory);
            Assert.Empty(loaded.Failures);
            return loaded.Definitions;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CardJson(
        string id, string name, string faction, int kredits, int operationCost, int attack, int defense,
        string unitType, params string[] attributes)
    {
        var attributeList = attributes.Length == 0
            ? "[]"
            : "[" + string.Join(", ", attributes.Select(a => $"\"{a}\"")) + "]";
        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "{{name}}",
          "components": [
            { "component": "typeCategory", "type": ["{{unitType}}"] },
            { "component": "factionCost", "faction": "{{faction}}", "kredits": {{kredits}} },
            { "component": "battleStats", "operationCost": {{operationCost}}, "attack": {{attack}}, "defense": {{defense}} },
            { "component": "tagData", "rarity": "Standard" },
            { "component": "keywords", "attributes": {{attributeList}} }
          ]
        }
        """;
    }

    private static Match CreateMatch(MockTargeterBridge? bridge = null)
        => CommandTestKit.CreateCommandMatch(bridge, extraDefinitions: LoadCovertDefinitions());

    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    // ---------- 契约冻结（触发器官名 / 标记标识 / 读取面 / 具名登记） ----------

    [Fact]
    public void Contract_Trigger_Name_Marker_Id_Are_Frozen_And_Readable()
    {
        // 对外契约（对 S3）：触发器名＝「揭示触发器」（查证无冲突）；标记标识＝「隐蔽」。
        Assert.Equal("揭示触发器", CovertRules.TriggerName);
        Assert.Equal("隐蔽", KeywordIds.Covert);
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Covert));
        Assert.Contains(KeywordIds.Covert, KeywordIds.All);
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Covert)); // 纯内容标记——不打对战词条标

        // 具名登记可寻址（效果预制体 injects 按名解析的注入目标面）。
        var engine = new LogicEngine();
        var unit = new UnitCard(
            engine,
            new CardDefinition("契约检查", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.True(unit.TryFindNamedTrigger(CovertRules.TriggerName, out var trigger, out var viewType));
        Assert.Same(unit.RevealTrigger, trigger);
        Assert.Equal(typeof(CardTriggerView), viewType);

        // 读取面降级：null/非隐蔽卡＝false 降级、不抛错。
        Assert.False(CovertRules.IsCovert(null));
        var command = new OrderCard(
            engine,
            new CardDefinition("指令", 1, 0, 0, 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.False(CovertRules.IsCovert(command));
    }

    // ---------- 数据映射专项（自含数据——真实链路：数据体 JSON → 加载 → 标记可读＋不再留痕） ----------

    [Fact]
    public void Data_Mapping_Loads_Covert_Marker_And_No_Longer_Traces()
    {
        var definitions = LoadCovertDefinitions();
        CardDefinition DefinitionOf(string id) => definitions.Single(entry => entry.Id == id).Definition;

        // `covert` → 「隐蔽」标记随内容落地；不再留痕；其它未实现项照常留痕（原语义保持）。
        var scout = DefinitionOf(CovertScoutId);
        Assert.Contains(scout.Keywords, k => k.Id == KeywordIds.Covert);
        Assert.DoesNotContain("covert", scout.UnmappedAttributes);
        Assert.Contains("OnlySpawnable", scout.UnmappedAttributes);

        // 未声明 covert 的卡不产出「隐蔽」标记。
        var plain = DefinitionOf(PlainUnitId);
        Assert.DoesNotContain(plain.Keywords, k => k.Id == KeywordIds.Covert);
        Assert.Empty(plain.UnmappedAttributes);
    }

    // ---------- C1：隐蔽单位被指令指向被拒绝（生产装配默认链——判定器接线生效；两向断言） ----------

    [Fact]
    public async Task C1_Covert_Unit_Is_Rejected_By_Instruction_Targeting_Through_Production_Judicator()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var covert = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CovertScoutId, 1); // 敌方隐蔽
        var plain = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2);   // 我方普通（对照）
        Assert.True(CovertRules.IsCovert(covert));

        // 生产装配默认链：判定器经内置注册段解析可用（S2 提入生产装配——非测试私装）。
        var registration = match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility);
        var rule = new JudicatorSelectionRule(registration, playerA.Hq, new EffectSelector("all"));
        var targeter = match.TargeterManager.CreateTargeter(filter: rule.AsFilter());
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var task = targeter.Targeting();
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // ① 候选端剔除：隐蔽单位不出现在允许集（前端不可点）；普通单位照常（不分敌我——隐蔽一律剔除）。
        Assert.DoesNotContain(covert.Ref, description.AllowedTargets);
        Assert.Contains(plain.Ref, description.AllowedTargets);

        // ② 强行提交被拒（不在允许集）＝选择器终局 Failed → targeter 内部重入（同一选择器）。
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), covert.Ref)));
        Assert.False(task.IsCompleted);

        // 合法项提交成功（终局）——非法提交后 targeter 异步重入（同一选择器实例）。
        var (reentry, reentryResponder) = await bridge.WaitForNextBeginAsync();
        Assert.True(reentryResponder.Complete(
            reentry.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(reentry), plain.Ref)));
        var result = await task;
        Assert.Equal(TargeterStatus.Ok, result.Status);
        Assert.Same(plain.Ref, result.Outcome!.Single);
    }

    // ---------- C9（路径一）：请求级覆盖——「可以指向隐蔽单位」 ----------

    [Fact]
    public async Task C9_Request_Level_Override_Includes_Covert_Unit()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var covert = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CovertScoutId, 1);
        var plain = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2);

        var registration = match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility);
        var rule = new JudicatorSelectionRule(registration, playerA.Hq, new EffectSelector("all", IncludeCovert: true)); // 请求级覆盖
        var targeter = match.TargeterManager.CreateTargeter(filter: rule.AsFilter());
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var task = targeter.Targeting();
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 隐蔽单位并入候选（仅放宽「隐蔽豁免」一项）；其余基本合法性保持（HQ 仍不可选）。
        Assert.Contains(covert.Ref, description.AllowedTargets);
        Assert.Contains(plain.Ref, description.AllowedTargets);
        Assert.DoesNotContain(playerA.Hq.Ref, description.AllowedTargets);
        Assert.DoesNotContain(playerB.Hq.Ref, description.AllowedTargets);

        // 提交隐蔽单位＝成功（例外生效）。
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), covert.Ref)));
        var result = await task;
        Assert.Equal(TargeterStatus.Ok, result.Status);
        Assert.Same(covert.Ref, result.Outcome!.Single);
    }

    // ---------- C9（路径二）：判定器 moding 整体替换（替换规则自行包含「隐蔽可选」完整逻辑）＋注销回退 ----------

    [Fact]
    public async Task C9_Judicator_Moding_Replacement_Allows_Covert_And_Unregisters()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        var covert = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1);
        var plain = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2); // 对照（保证允许集非空、交互可观测）
        var registration = match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility);
        var rule = new JudicatorSelectionRule(registration, playerA.Hq, new EffectSelector("all")); // 同一包装路径观测三态
        var targeter = match.TargeterManager.CreateTargeter(filter: rule.AsFilter());
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 态 1（默认）：隐蔽单位不可选（判定器默认规则含隐蔽剔除）；普通照常。
        var task1 = targeter.Targeting();
        var (d1, r1) = await bridge.WaitForNextBeginAsync();
        Assert.DoesNotContain(covert.Ref, d1.AllowedTargets);
        Assert.Contains(plain.Ref, d1.AllowedTargets);
        Assert.True(r1.Cancel(d1.RequestId));
        Assert.Equal(TargeterStatus.Cancelled, (await task1).Status);

        // 态 2（改写：替换规则自行包含「隐蔽可选」完整逻辑——允许全部单位）。
        var moding = match.Judicators.RegisterAfterWrapper(
            registration,
            (_, result) => result is { Length: 1 } && result[0] is IReadOnlyList<Ref<Entity>> list
                ? new object[] { list.Append(covert.Ref).ToArray() }
                : result,
            playerIndex: playerA.Index);
        Assert.NotNull(moding);

        var task2 = targeter.Targeting();
        var (d2, r2) = await bridge.WaitForNextBeginAsync();
        Assert.Contains(covert.Ref, d2.AllowedTargets); // 隐蔽出现在允许集（改写生效）

        // 可提交隐蔽（替换规则放行）。
        Assert.True(r2.Complete(
            d2.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(d2), covert.Ref)));
        var result2 = await task2;
        Assert.Equal(TargeterStatus.Ok, result2.Status);

        // 态 3（注销回退）：隐蔽恢复不可选。
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        var task3 = targeter.Targeting();
        var (d3, r3) = await bridge.WaitForNextBeginAsync();
        Assert.DoesNotContain(covert.Ref, d3.AllowedTargets);
        Assert.Contains(plain.Ref, d3.AllowedTargets);
        Assert.True(r3.Cancel(d3.RequestId));
        Assert.Equal(TargeterStatus.Cancelled, (await task3).Status);
    }

    // ---------- C2：隐蔽单位被攻击即揭示（伤害结算前——揭示先于本次战斗数值结算） ----------

    [Fact]
    public async Task C2_Attacked_Covert_Unit_Is_Revealed_Before_Damage_Settlement()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5（支援线→敌前线）
        var defender = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CovertScoutId, 1);                // 隐蔽 2 / 5
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 揭示逻辑探针（「揭示触发器」测试内容）：记录执行时点可读到的公共面状态。
        var defenseAtLogic = -1;
        var revealedSignalsAtLogic = -1;
        defender.RevealTrigger.Register("测试揭示内容", (view, ctx, ct) =>
        {
            defenseAtLogic = DefenseOf(defender); // 伤害结算前的数值（揭示逻辑已可读/生效）
            revealedSignalsAtLogic = recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed);
            return Task.CompletedTask;
        });

        // 攻击（可被攻击——攻击候选不受隐蔽剔除影响；提交隐蔽目标被接受即证）。
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, defender.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 揭示发生：标记移除、信号恰一次。
        Assert.False(CovertRules.IsCovert(defender));
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed
            && ReferenceEquals(u.Payload![GameUpdates.PayloadUnit], defender)));

        // 揭示先于伤害结算：逻辑执行时防御＝满值（尚未受伤）、信号尚未发射（先落定后发射）；
        // 观测序＝unit.revealed 先于本战斗的伤害信号（card.damaged）。
        Assert.Equal(5, defenseAtLogic);
        Assert.Equal(0, revealedSignalsAtLogic);
        var revealedIndex = recorder.Types.ToList().IndexOf(GameUpdates.UnitRevealed);
        var damagedIndex = recorder.Types.ToList().IndexOf(GameUpdates.CardDamaged);
        Assert.True(revealedIndex >= 0);
        Assert.True(damagedIndex > revealedIndex);

        // 结算正常完成：2 伤 → 防御 5→3（伤害照常作用于已揭示单位）。
        Assert.Equal(3, DefenseOf(defender));
    }

    // ---------- C3：隐蔽单位主动攻击时揭示（单位目标） ----------

    [Fact]
    public async Task C3_Covert_Attacker_Is_Revealed_On_Attack()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1); // 隐蔽攻击者（支援线→敌前线）
        var defender = await CommandTestKit.PrepareOnFrontAsync(match, playerB, PlainUnitId, 1);    // 普通目标
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, defender.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 攻击者揭示（恰一次）；未隐蔽的目标不发揭示信号。
        Assert.False(CovertRules.IsCovert(attacker));
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed
            && ReferenceEquals(u.Payload![GameUpdates.PayloadUnit], attacker)));
        Assert.Equal(0, recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed
            && ReferenceEquals(u.Payload![GameUpdates.PayloadUnit], defender)));
    }

    // ---------- C3b：隐蔽攻击者攻击 HQ 时攻击者揭示（「HQ 目标不揭示」仅指 HQ 不作被揭示方） ----------

    [Fact]
    public async Task C3b_Covert_Attacker_Is_Revealed_When_Attacking_Hq_And_Hq_Is_Not_Revealed()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CovertTankId, 0); // 隐蔽坦克 攻 3（前线→敌 HQ）
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, playerB.Hq.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 攻击者揭示（恰一次）；HQ 不作被揭示方（零揭示信号——HQ 非单位）。
        Assert.False(CovertRules.IsCovert(attacker));
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed
            && ReferenceEquals(u.Payload![GameUpdates.PayloadUnit], attacker)));
        Assert.Equal(20 - 3, playerB.HqHealth);
    }

    // ---------- C12：双方同为隐蔽＝各自揭示、各发一次信号（序＝攻击者先、被攻击者后——申报序） ----------

    [Fact]
    public async Task C12_Both_Covert_Sides_Reveal_In_Attacker_First_Order()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1); // 隐蔽攻击者（支援线→敌前线）
        var defender = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CovertScoutId, 1);  // 隐蔽被攻击者
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, defender.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        Assert.False(CovertRules.IsCovert(attacker));
        Assert.False(CovertRules.IsCovert(defender));

        // 两个揭示信号、各自恰一次；先后顺序＝攻击者在前、被攻击者在后（S2 裁定序——申报）。
        var revealUnits = recorder.Updates
            .Where(u => u.Type == GameUpdates.UnitRevealed)
            .Select(u => u.Payload![GameUpdates.PayloadUnit])
            .OfType<UnitCard>()
            .ToList();
        Assert.Equal(new[] { attacker, defender }, revealUnits);
    }

    // ---------- C4：移动不解除隐蔽 ----------

    [Fact]
    public async Task C4_Movement_Does_Not_Reveal_Covert_Unit()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 移动（支援线 → 前线空槽）：成功且解密行为不发生。
        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Battlefield.FrontLine[0].Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        Assert.True(CovertRules.IsCovert(unit)); // 仍隐蔽
        Assert.Same(unit, match.Battlefield.FrontLine[0].Occupant); // 移动已发生
        Assert.DoesNotContain(recorder.Updates, u => u.Type == GameUpdates.UnitRevealed);
    }

    // ---------- 剔除点：无头选靶（SelectAsync）剔除隐蔽（保留 self 分支/random 剔除后取样） ----------

    [Fact]
    public async Task C5_NoHead_Targeting_Excludes_Covert_But_Self_Branch_Is_Kept()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var viewer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1); // 隐蔽视角卡
        var friend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2);   // 普通友方
        var enemyPlain = await CommandTestKit.PrepareOnSupportAsync(match, playerB, PlainUnitId, 1);
        var enemyCovert = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CovertScoutId, 2);
        var runtime = EffectRuntime.ResolveFor(viewer);
        Assert.NotNull(runtime);

        // all：候选基底统一剔除隐蔽（含视角卡自身——不分敌我）。
        var friendly = await runtime!.SelectAsync(viewer, new EffectSelector("all", "friendly"));
        Assert.Equal(new[] { friend }, friendly);

        var enemy = await runtime.SelectAsync(viewer, new EffectSelector("all", "enemy"));
        Assert.Equal(new[] { enemyPlain }, enemy);

        // random：在剔除后的池上取样（池中仅普通单位——取到必然普通）。
        var random = await runtime.SelectAsync(viewer, new EffectSelector("random", "both", Count: 1));
        Assert.DoesNotContain(viewer, random);
        Assert.DoesNotContain(enemyCovert, random);
        Assert.Single(random);

        // self 分支保留（自指效果不经候选剔除）——既有语义保持。
        var self = await runtime.SelectAsync(viewer, new EffectSelector("self"));
        Assert.Equal(new[] { viewer }, self);
    }

    // ---------- 剔除点：光环受益收集剔除隐蔽（来源侧不受限） ----------

    [Fact]
    public async Task C6_Aura_Collection_Excludes_Covert_Beneficiary()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        var covert = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1); // 隐蔽 2/5
        var plain = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2);   // 普通 2/5
        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 1);    // 光环宿主（普通）

        // 普通宿主声明「友方 +1 攻击」光环：隐蔽单位不被收集（无加成）；普通单位照常受益。
        var runtime = EffectRuntime.ResolveFor(host);
        Assert.NotNull(runtime);
        Assert.True(await runtime!.DeclareAuraAsync(host, "attack", 1, new EffectAuraFilter(Side: "friendly")));

        Assert.Equal(2, AttackOf(covert));  // 隐蔽受益者被剔除（数值不变）
        Assert.Equal(3, AttackOf(plain));   // 普通 +1
        Assert.Equal(3, AttackOf(host));    // 宿主自身亦受益（未排除自身）
    }

    [Fact]
    public async Task C6b_Covert_Aura_Source_Is_Unrestricted()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        // 来源侧不受限：隐蔽单位作为光环**提供者**照常作用于其他单位（自身作为受益者仍被剔）。
        var covertHost = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1);
        var friend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2);
        var runtime = EffectRuntime.ResolveFor(covertHost);
        Assert.NotNull(runtime);
        Assert.True(await runtime!.DeclareAuraAsync(covertHost, "attack", 1, new EffectAuraFilter(Side: "friendly")));

        Assert.Equal(3, AttackOf(friend));      // 隐蔽来源的光环照常作用于其他单位
        Assert.Equal(2, AttackOf(covertHost));  // 隐蔽单位自身作为受益者被剔（声明不及自身）
    }

    // ---------- 剔除点：钳击配对候选剔除隐蔽（来源不受限；全隐蔽候选不发起交互） ----------

    [Fact]
    public async Task C7a_Pincer_Candidates_Exclude_Covert_Partner()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);

        var covertFriend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1); // 隐蔽友方
        var plainFriend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2);   // 普通友方
        var pincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 部署（部署链收尾发起同伴选择）：隐蔽友方不可被选为同伴（候选剔除）；强行提交被拒。
        var task = match.PlayManager.PlayUnitAsync(pincer, supportLine[3]);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.DoesNotContain(covertFriend.Ref, description.AllowedTargets);
        Assert.Contains(plainFriend.Ref, description.AllowedTargets);
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), covertFriend.Ref)));

        var (reentry, reentryResponder) = await bridge.WaitForNextBeginAsync();
        Assert.True(reentryResponder.Complete(
            reentry.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(reentry), plainFriend.Ref)));
        var result = await task;
        Assert.Equal(PlayResultStatus.Success, result.Status);

        // 配对成立（来源＝普通部署方＋普通同伴）：双方 +2。
        var pair = match.CommandManager.Pincers.Find(pincer);
        Assert.NotNull(pair);
        Assert.Same(plainFriend, pair!.Second);
        Assert.Equal(4, AttackOf(plainFriend));
    }

    [Fact]
    public async Task C7b_Covert_Pincer_Source_Is_Unrestricted()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);

        var covertFriend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1);
        var plainFriend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2);
        var covertPincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CovertPincerId, toHand: true);
        Assert.True(CovertRules.IsCovert(covertPincer));
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 隐蔽单位自己部署钳击：作为来源不受限（可选他人为同伴——交互照常发起）；
        // 候选面仍剔除其他隐蔽单位（只有普通友方可选）。
        var task = match.PlayManager.PlayUnitAsync(covertPincer, supportLine[3]);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.DoesNotContain(covertFriend.Ref, description.AllowedTargets);
        Assert.Contains(plainFriend.Ref, description.AllowedTargets);

        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), plainFriend.Ref)));
        var result = await task;
        Assert.Equal(PlayResultStatus.Success, result.Status);

        // 配对成立；双方（含隐蔽部署方自身）获得钳击效果（自身词条效果对自身运作——不受限）。
        var pair = match.CommandManager.Pincers.Find(covertPincer);
        Assert.NotNull(pair);
        Assert.True(CovertRules.IsCovert(covertPincer)); // 配对不揭示（机制性操作非「揭示」）
        Assert.Equal(4, AttackOf(plainFriend));          // 2+2
        Assert.Equal(4, AttackOf(covertPincer));         // 2+2（来源自身照常生效）
    }

    [Fact]
    public async Task C7c_Pincer_With_Only_Covert_Candidates_Skips_Interaction()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);

        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1); // 仅隐蔽友方
        var pincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 合法候选为空（隐蔽被剔）＝不发起选择、不形成、部署正常完成。
        var result = await match.PlayManager.PlayUnitAsync(pincer, supportLine[3]);
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Empty(bridge.Begins);
        Assert.False(match.CommandManager.Pincers.IsPaired(pincer));
        Assert.Same(pincer, supportLine[3].Occupant);
    }

    // ---------- 攻击候选不受影响：隐蔽单位出现在攻击候选面（可被攻击） ----------

    [Fact]
    public async Task C8_Attack_Candidates_Include_Covert_Unit()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var defender = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CovertScoutId, 1);
        CommandTestKit.Activate(attacker);

        var availability = match.CommandManager.GetCommandAvailability(attacker);
        Assert.Contains(defender.Ref, availability.Attack.Candidates);
    }

    // ---------- 揭示服务专项：NoOp/fail-fast/幂等/主动揭示（经 EffectRuntime 门面） ----------

    [Fact]
    public async Task C10_Reveal_Service_NoOp_FastFail_And_Idempotence()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        var plain = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 1);
        using var recorder = new UpdateRecorder(match.Engine);

        // 非隐蔽单位＝NoOp（零信号、不抛错）。
        var updatesBefore = recorder.Updates.Count;
        Assert.Equal(RevealOutcome.NoOp, await CovertRules.RevealAsync(plain));
        Assert.Equal(updatesBefore, recorder.Updates.Count); // 零新增（无任何信号）

        // 非单位（指令卡）＝NoOp（经 EffectRuntime 门面——降级不抛错）。
        var commandCard = (OrderCard)match.CardLibrary.Instantiate(CommandTestKit.OrderCardId);
        await commandCard.LoadAsync(playerA);
        var commandRuntime = EffectRuntime.ResolveFor(commandCard);
        Assert.NotNull(commandRuntime);
        Assert.Equal(RevealOutcome.NoOp, await commandRuntime!.RevealAsync(commandCard));

        // 未在场（手牌中的隐蔽卡）＝NoOp（防御细化——揭示只对场上存活单位有意义）。
        var inHand = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CovertScoutId, toHand: true);
        Assert.True(CovertRules.IsCovert(inHand));
        Assert.Equal(RevealOutcome.NoOp, await CovertRules.RevealAsync(inHand));

        // 参数契约错误（null）＝fail-fast。
        await Assert.ThrowsAsync<ArgumentNullException>(() => CovertRules.RevealAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => commandRuntime.RevealAsync(null!));

        // 主动揭示动作可调（「揭示 1 个隐蔽单位」编排：列表读口选中 → RevealAsync）：
        // 隐蔽单位揭示＝Revealed（恰一次信号）；重复调用＝NoOp（不再发）。
        var covert = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 2);
        var listed = CovertRules.CollectCovertUnits(GameEnvironment.ResolveFor(covert)!);
        Assert.Contains(covert, listed);

        var runtime = EffectRuntime.ResolveFor(covert);
        Assert.NotNull(runtime);
        Assert.Equal(RevealOutcome.Revealed, await runtime!.RevealAsync(covert));
        Assert.Equal(RevealOutcome.NoOp, await CovertRules.RevealAsync(covert));
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed
            && ReferenceEquals(u.Payload![GameUpdates.PayloadUnit], covert)));
        Assert.False(CovertRules.IsCovert(covert));
    }

    // ---------- 揭示服务专项：顺序（标记移除→揭示逻辑→信号）与重入防护 ----------

    [Fact]
    public async Task C10b_Reveal_Order_And_Reentry_Protection()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var markerAtLogic = true;
        var signalsAtLogic = -1;
        var reentryOutcome = RevealOutcome.Revealed;
        unit.RevealTrigger.Register("测试揭示内容", async (view, ctx, ct) =>
        {
            markerAtLogic = unit.Keywords.Has(KeywordIds.Covert);                        // ①标记先行移除（落定）
            signalsAtLogic = recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed); // ②信号后发射（逻辑时未发）
            reentryOutcome = await CovertRules.RevealAsync(unit);                        // ③执行中重入＝无操作
        });

        var outcome = await CovertRules.RevealAsync(unit);
        Assert.Equal(RevealOutcome.Revealed, outcome);
        Assert.False(markerAtLogic);
        Assert.Equal(0, signalsAtLogic);
        Assert.Equal(RevealOutcome.NoOp, reentryOutcome);
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.UnitRevealed)); // 恰一次
        Assert.False(CovertRules.IsCovert(unit));
    }

    // ---------- 读取面：列表/枚举读口（查询面——不经豁免剔除） ----------

    [Fact]
    public async Task C11_Collect_Covert_Units_Query_Face()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var covertA = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CovertScoutId, 1);
        var plainA = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PlainUnitId, 2); // 普通（不入列）
        var covertB = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CovertScoutId, 1);
        var covertFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CovertScoutId, 0);

        var environment = GameEnvironment.ResolveFor(covertA);
        Assert.NotNull(environment);

        // 全战场枚举（不分敌我；顺序＝A 支援线 → 前线 → B 支援线）；不含非隐蔽单位。
        var collected = CovertRules.CollectCovertUnits(environment!);
        Assert.Equal(new[] { covertA, covertFront, covertB }, collected);
        Assert.DoesNotContain(plainA, collected);
    }
}
