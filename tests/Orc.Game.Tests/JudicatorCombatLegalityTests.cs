using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// K1 交战合法性判定族（C1 combat.target.legal／C2 combat.range／C3 combat.guard.eligibility／C4 combat.interception）
/// 收编游戏层测试：
/// ①默认行为等价：单位/HQ 两分支＋C2/C3/C4 各环节（范围/守护/拦截）至少各一例（候选链＋复验链双链断言）；
/// ②五型全覆盖（任意线 3 型＋相邻 2 型）＋零类型一例＋资格组覆盖断言；
/// ③核心演示：单点改写子规则（C2）→ C1 编排对应环节变化 → 候选与复验两链同步生效 → 注销回退（闭环）；
/// ④附加演示：C3/C4 单点改写穿透（候选＋复验）＋回退；C1 整体改写两链同步＋回退；
/// ⑤边界：失效引用＝false、未知载体类型＝false（经注册表直接调用——不异常）；
/// ⑥子规则独立可用（C2/C3/C4 经注册表直接调用）；
/// ⑦独立构造路径（无注册表）默认可用且行为与注册路径一致。
/// 链路口径：全部调用经正常引用面（注册表条目/判定器通道）——不直连判定器实例。
/// </summary>
public class JudicatorCombatLegalityTests
{
    // ---------- ① 默认行为等价：单位分支（范围环节）＋复验链 ----------

    [Fact]
    public async Task Unit_Branch_Range_Default_Equivalence()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // 布置（有序列表模型）：前线首个元素＝插入点 0；敌方支援线 HQ 之后＝插入点 1。
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);

        // 候选链（可用性）：经 combat.target.legal 逐候选过滤——相邻可达、跨线不可达。
        var report = match.CommandManager.GetCommandAvailability(attacker);
        Assert.Contains(enemyOnFront.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(enemyOnSupport.Ref, report.Attack.Candidates);

        // 复验链（执行前兜底）：同源经判定器——范围环节一致。
        var trigger = match.CommandManager.UnitAttackTrigger;
        Assert.True(trigger.Validate(new[] { attacker.Ref, enemyOnFront.Ref }));
        Assert.False(trigger.Validate(new[] { attacker.Ref, enemyOnSupport.Ref }));
    }

    // ---------- ① 默认行为等价：HQ 分支（范围环节） ----------

    [Fact]
    public async Task Hq_Branch_Range_Default_Equivalence()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(artillery);
        CommandTestKit.Activate(infantry);

        // 炮兵（任意线组）→ 敌 HQ：可达（含 HQ 全组合允许）；复验通过。
        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(playerB.Hq.Ref, artilleryReport.Attack.Candidates);
        Assert.True(match.CommandManager.UnitAttackTrigger.Validate(new[] { artillery.Ref, playerB.Hq.Ref }));

        // 步兵（仅相邻）→ 敌 HQ（位于敌方支援线）：不可达；复验拒绝。
        var infantryReport = match.CommandManager.GetCommandAvailability(infantry);
        Assert.DoesNotContain(playerB.Hq.Ref, infantryReport.Attack.Candidates);
        Assert.False(match.CommandManager.UnitAttackTrigger.Validate(new[] { infantry.Ref, playerB.Hq.Ref }));
    }

    // ---------- ① 默认行为等价：C3 守护环节（单位） ----------

    [Fact]
    public async Task Guard_Stage_Unit_Default_Equivalence()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // B 前线：守护兵（位置 0）＋步兵（位置 1）——相邻（索引差 1）→ 步兵获「被守护」。
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.GuardianId, 0);
        var guarded = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 2);
        CommandTestKit.Activate(infantry);
        CommandTestKit.Activate(artillery);

        Assert.True(match.CommandManager.IsUnitGuarded(guarded));

        // 被守护单位仅能被炮/轰攻击（资格环节）：步/坦视角＝剔除；炮/轰可通过。
        var infantryReport = match.CommandManager.GetCommandAvailability(infantry);
        Assert.DoesNotContain(guarded.Ref, infantryReport.Attack.Candidates);
        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(guarded.Ref, artilleryReport.Attack.Candidates);

        // 复验链一致：步兵拒绝、炮兵通过（守护资格环节同源）。
        Assert.False(match.CommandManager.UnitAttackTrigger.Validate(new[] { infantry.Ref, guarded.Ref }));
        Assert.True(match.CommandManager.UnitAttackTrigger.Validate(new[] { artillery.Ref, guarded.Ref }));
    }

    // ---------- ① 默认行为等价：C3 守护环节（HQ） ----------

    [Fact]
    public async Task Guard_Stage_Hq_Default_Equivalence()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // B 支援线：守护兵（位置 1，紧随 HQ）→ HQ 被守护。
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.GuardianId, 1);
        var infantry = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        CommandTestKit.Activate(infantry);
        CommandTestKit.Activate(artillery);

        Assert.True(match.CommandManager.IsHqGuarded(playerB));

        // 被守护 HQ：步/坦不可攻（前线→敌支援线范围可达、但资格环节剔除）；炮/轰可攻。
        var infantryReport = match.CommandManager.GetCommandAvailability(infantry);
        Assert.DoesNotContain(playerB.Hq.Ref, infantryReport.Attack.Candidates);
        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(playerB.Hq.Ref, artilleryReport.Attack.Candidates);

        Assert.False(match.CommandManager.UnitAttackTrigger.Validate(new[] { infantry.Ref, playerB.Hq.Ref }));
        Assert.True(match.CommandManager.UnitAttackTrigger.Validate(new[] { artillery.Ref, playerB.Hq.Ref }));
    }

    // ---------- ① 默认行为等价：C4 拦截环节（单位） ----------

    [Fact]
    public async Task Interception_Stage_Unit_Default_Equivalence()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 0);
        var enemyInfantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(bomber);

        // 轰炸机拦截：目标所在战线存在存活敌方战斗机 → 该战线非战斗机目标置黑；战斗机目标不受拦截。
        var report = match.CommandManager.GetCommandAvailability(bomber);
        Assert.Contains(enemyFighter.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(enemyInfantry.Ref, report.Attack.Candidates);

        // 复验链一致：目标战斗机通过；被拦截目标拒绝。
        var trigger = match.CommandManager.UnitAttackTrigger;
        Assert.True(trigger.Validate(new[] { bomber.Ref, enemyFighter.Ref }));
        Assert.False(trigger.Validate(new[] { bomber.Ref, enemyInfantry.Ref }));
    }

    // ---------- ① 默认行为等价：C4 拦截环节（HQ） ----------

    [Fact]
    public async Task Interception_Stage_Hq_Default_Equivalence()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyFighter = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.FighterId, 1);
        CommandTestKit.Activate(bomber);

        // HQ 拦截：HQ 位于敌方支援线——该战线存在存活敌战斗机时不可选（须先攻击战斗机）。
        var report = match.CommandManager.GetCommandAvailability(bomber);
        Assert.Contains(enemyFighter.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(playerB.Hq.Ref, report.Attack.Candidates);
        Assert.False(match.CommandManager.UnitAttackTrigger.Validate(new[] { bomber.Ref, playerB.Hq.Ref }));
    }

    // ---------- ② 五型全覆盖＋零类型＋资格组覆盖 ----------

    [Fact]
    public async Task Five_Type_Coverage_Typeless_And_Qualification_Group()
    {
        // 任意线 3 型（炮/战/轰）：均可到达敌支援线目标（跨线任意）。
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        var fighter = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FighterId, 2);
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(artillery);
        CommandTestKit.Activate(fighter);
        CommandTestKit.Activate(bomber);

        Assert.Contains(enemyOnSupport.Ref, match.CommandManager.GetCommandAvailability(artillery).Attack.Candidates);
        Assert.Contains(enemyOnSupport.Ref, match.CommandManager.GetCommandAvailability(fighter).Attack.Candidates);
        Assert.Contains(enemyOnSupport.Ref, match.CommandManager.GetCommandAvailability(bomber).Attack.Candidates);
    }

    [Fact]
    public async Task Adjacent_Group_And_Typeless_Coverage()
    {
        // 相邻 2 型（步/坦）＋零类型：支援线 → 敌前线可达；→ 敌支援线不可达（跨线紧邻语义）。
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var tank = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TankId, 2);
        var typeless = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TypelessId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(infantry);
        CommandTestKit.Activate(tank);
        CommandTestKit.Activate(typeless);

        foreach (var attacker in new[] { infantry, tank, typeless })
        {
            var report = match.CommandManager.GetCommandAvailability(attacker);
            Assert.Contains(enemyOnFront.Ref, report.Attack.Candidates);
            Assert.DoesNotContain(enemyOnSupport.Ref, report.Attack.Candidates);
        }
    }

    [Fact]
    public async Task Qualification_Group_Coverage_On_Guarded_Target()
    {
        // 资格组覆盖：被守护单位——炮/轰可通过、战斗机不可（资格组＝炮/轰，不含战斗机）。
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.GuardianId, 0);
        var guarded = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        var fighter = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FighterId, 2);
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        CommandTestKit.Activate(artillery);
        CommandTestKit.Activate(fighter);
        CommandTestKit.Activate(bomber);

        Assert.True(match.CommandManager.IsUnitGuarded(guarded));
        Assert.Contains(guarded.Ref, match.CommandManager.GetCommandAvailability(artillery).Attack.Candidates);
        Assert.DoesNotContain(guarded.Ref, match.CommandManager.GetCommandAvailability(fighter).Attack.Candidates);
        Assert.Contains(guarded.Ref, match.CommandManager.GetCommandAvailability(bomber).Attack.Candidates);
    }

    // ---------- ③ 核心演示：单点改写 C2 穿透组合器＋两链同步＋注销回退 ----------

    [Fact]
    public async Task Range_Moding_Penetrates_Combinator_And_Both_Chains()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        var attackTrigger = match.CommandManager.UnitAttackTrigger;
        var refs = new[] { attacker.Ref, enemyOnSupport.Ref };

        // 基线：范围限制（支援线→敌支援线不允许）——候选与复验两链同为拒绝。
        var before = match.CommandManager.GetCommandAvailability(attacker);
        Assert.DoesNotContain(enemyOnSupport.Ref, before.Attack.Candidates);
        Assert.False(attackTrigger.Validate(refs));

        // 单点改写子规则 C2（combat.range→恒真）：组合器「范围」环节经条目句柄消费——编排同步改判；
        // 候选链与复验链（共用同一 C1 条目）两链同步生效。
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CombatRange),
            _ => new object[] { true });
        Assert.NotNull(moding);

        var moded = match.CommandManager.GetCommandAvailability(attacker);
        Assert.Contains(enemyOnSupport.Ref, moded.Attack.Candidates);
        Assert.True(attackTrigger.Validate(refs));

        // 注销回退：两链同步回退默认（范围限制恢复）。
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        var restored = match.CommandManager.GetCommandAvailability(attacker);
        Assert.DoesNotContain(enemyOnSupport.Ref, restored.Attack.Candidates);
        Assert.False(attackTrigger.Validate(refs));
    }

    // ---------- ④ 附加演示：单点改写 C3 穿透（守护资格环节） ----------

    [Fact]
    public async Task Guard_Eligibility_Moding_Penetrates_Combinator()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.GuardianId, 0);
        var guarded = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(infantry);
        var attackTrigger = match.CommandManager.UnitAttackTrigger;
        var refs = new[] { infantry.Ref, guarded.Ref };

        Assert.True(match.CommandManager.IsUnitGuarded(guarded));
        Assert.DoesNotContain(guarded.Ref, match.CommandManager.GetCommandAvailability(infantry).Attack.Candidates);
        Assert.False(attackTrigger.Validate(refs));

        // 单点改写 C3（combat.guard.eligibility→恒真）：组合器「守护资格」环节经条目句柄消费——穿透生效。
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CombatGuardEligibility),
            _ => new object[] { true });
        Assert.NotNull(moding);
        Assert.Contains(guarded.Ref, match.CommandManager.GetCommandAvailability(infantry).Attack.Candidates);
        Assert.True(attackTrigger.Validate(refs));

        // 注销回退。
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.DoesNotContain(guarded.Ref, match.CommandManager.GetCommandAvailability(infantry).Attack.Candidates);
        Assert.False(attackTrigger.Validate(refs));
    }

    // ---------- ④ 附加演示：单点改写 C4 穿透（拦截环节） ----------

    [Fact]
    public async Task Interception_Moding_Penetrates_Combinator()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 0);
        var enemyInfantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(bomber);
        var attackTrigger = match.CommandManager.UnitAttackTrigger;
        var refs = new[] { bomber.Ref, enemyInfantry.Ref };

        // 基线：拦截成立（同线存活敌战斗机）——非战斗机目标置黑、复验拒绝。
        Assert.Contains(enemyFighter.Ref, match.CommandManager.GetCommandAvailability(bomber).Attack.Candidates);
        Assert.DoesNotContain(enemyInfantry.Ref, match.CommandManager.GetCommandAvailability(bomber).Attack.Candidates);
        Assert.False(attackTrigger.Validate(refs));

        // 单点改写 C4（combat.interception→恒假）：拦截解除（组合器「拦截」环节经条目句柄消费）。
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CombatInterception),
            _ => new object[] { false });
        Assert.NotNull(moding);
        Assert.Contains(enemyInfantry.Ref, match.CommandManager.GetCommandAvailability(bomber).Attack.Candidates);
        Assert.True(attackTrigger.Validate(refs));

        // 注销回退。
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.DoesNotContain(enemyInfantry.Ref, match.CommandManager.GetCommandAvailability(bomber).Attack.Candidates);
        Assert.False(attackTrigger.Validate(refs));
    }

    // ---------- ④ 附加演示：C1 整体改写——全部调用点（候选＋复验）同步生效 ----------

    [Fact]
    public async Task Target_Legal_Moding_Affects_Both_Chains()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        var attackTrigger = match.CommandManager.UnitAttackTrigger;
        var refs = new[] { attacker.Ref, enemyOnSupport.Ref };

        Assert.False(attackTrigger.Validate(refs));

        // 改写 C1 整体（恒真）：候选链与复验链同步受改写（共用同一条目——无独立副本）。
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CombatTargetLegal),
            _ => new object[] { true });
        Assert.NotNull(moding);
        Assert.Contains(enemyOnSupport.Ref, match.CommandManager.GetCommandAvailability(attacker).Attack.Candidates);
        Assert.True(attackTrigger.Validate(refs));

        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.DoesNotContain(enemyOnSupport.Ref, match.CommandManager.GetCommandAvailability(attacker).Attack.Candidates);
        Assert.False(attackTrigger.Validate(refs));
    }

    // ---------- ⑤ 边界：失效引用／未知载体类型＝false ----------

    [Fact]
    public async Task Boundary_Invalid_Reference_And_Unknown_Entity_Return_False()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var entry = match.Judicators.Resolve(JudicatorNames.CombatTargetLegal);

        // 失效引用（IsAlive＝false）＝false（不异常）。
        var ghost = new UnitCard(match.Engine, new CardDefinition(
            "幽灵步兵", deployCost: 1, operateCost: 1, attack: 0, defense: 1,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));
        ghost.Destroy();
        Assert.False(ghost.Ref.IsAlive);
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { attacker, ghost.Ref }));

        // 未知载体类型（槽位引用）＝false（不异常）。
        var slotRef = match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref;
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { attacker, slotRef }));

        // 对照：合法目标引用＝true（判定器真实求值——非恒假）。
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        Assert.Equal(new object[] { true }, entry.Invoke(new object[] { attacker, enemyOnFront.Ref }));
    }

    // ---------- ⑥ 子规则独立可用（经注册表条目直接调用） ----------

    [Fact]
    public async Task Sub_Rule_Entries_Resolvable_And_Directly_Invocable()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 0);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);

        // 载荷口径：C2/C4 的目标参＝**已落地位置**（BoardPosition：线 + 元素位置序号）。
        // 不可用插入空隙序号代替元素序号（插入点是"请求落位"、不是位置），也不可传线本身。
        var enemySupportLine = enemyOnSupport.GetData<UnitStateData>().Line!;
        var enemyFrontLine = enemyOnFront.GetData<UnitStateData>().Line!;
        var enemySupportPosition = new BoardPosition(enemySupportLine, enemySupportLine.IndexOf(enemyOnSupport));
        var enemyFrontPosition = new BoardPosition(enemyFrontLine, enemyFrontLine.IndexOf(enemyOnFront));

        // 默认名恒可解析（固定内置注册段——无条件可用）。
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CombatTargetLegal));
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CombatRange));
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CombatGuardEligibility));
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CombatInterception));

        // C2（combat.range；（攻击者, 目标位置）→ bool）直接调用：炮＝任意线；步＝仅相邻。
        var range = match.Judicators.Resolve(JudicatorNames.CombatRange);
        Assert.Equal(new object[] { true }, range.Invoke(new object[] { artillery, enemySupportPosition }));
        Assert.Equal(new object[] { false }, range.Invoke(new object[] { infantry, enemySupportPosition }));
        Assert.Equal(new object[] { true }, range.Invoke(new object[] { infantry, enemyFrontPosition }));

        // C3（combat.guard.eligibility；（攻击者）→ bool）直接调用：炮/轰＝有资格；步/战斗机＝无资格。
        var guard = match.Judicators.Resolve(JudicatorNames.CombatGuardEligibility);
        Assert.Equal(new object[] { true }, guard.Invoke(new object[] { artillery }));
        Assert.Equal(new object[] { false }, guard.Invoke(new object[] { infantry }));
        Assert.Equal(new object[] { true }, guard.Invoke(new object[] { bomber }));
        Assert.Equal(new object[] { false }, guard.Invoke(new object[] { enemyFighter }));

        // C4（combat.interception；（攻击者, 目标位置, 目标是否战斗机）→ bool）直接调用：
        // 轰炸机∧非战斗机目标∧同线存活敌战斗机＝拦截；目标战斗机/跨线/非轰炸机攻击者＝不拦截。
        var interception = match.Judicators.Resolve(JudicatorNames.CombatInterception);
        Assert.Equal(new object[] { true }, interception.Invoke(new object[] { bomber, enemyFrontPosition, false }));
        Assert.Equal(new object[] { false }, interception.Invoke(new object[] { bomber, enemyFrontPosition, true }));
        Assert.Equal(new object[] { false }, interception.Invoke(new object[] { bomber, enemySupportPosition, false }));
        Assert.Equal(new object[] { false }, interception.Invoke(new object[] { artillery, enemyFrontPosition, false }));

        // 对照（防未使用变量）：目标引用仍在场（本测试仅只读调用、零副作用）。
        Assert.True(enemyOnFront.Ref.IsAlive);
        Assert.True(enemyOnSupport.Ref.IsAlive);
    }

    // ---------- ⑦ 独立构造路径（无注册表）默认可用且行为与注册路径一致 ----------

    [Fact]
    public async Task Standalone_CommandManager_BuiltIn_Defaults_Consistent()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);

        // 独立构造（不经 Match 装配链/注册表）：内置默认交战判定器族（C1＋C2/C3/C4 内置实例）构造即可用。
        var standalone = new CommandManager(
            match.Engine,
            match.Battlefield,
            match.TargeterManager,
            match.Players,
            () => match.CurrentPlayer);

        var registeredReport = match.CommandManager.GetCommandAvailability(attacker);
        var standaloneReport = standalone.GetCommandAvailability(attacker);

        // 行为与注册路径一致（同序同集合）：相邻可达、跨线不可达——默认逻辑真实求值（非恒真）。
        Assert.Equal(registeredReport.Attack.Candidates, standaloneReport.Attack.Candidates);
        Assert.Contains(enemyOnFront.Ref, standaloneReport.Attack.Candidates);
        Assert.DoesNotContain(enemyOnSupport.Ref, standaloneReport.Attack.Candidates);
    }
}
