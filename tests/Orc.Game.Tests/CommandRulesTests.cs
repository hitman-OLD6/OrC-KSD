using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2C 验收②（KARDS 规则矩阵——经可用性聚合判定公开面断言，不必驱动完整交互）：
/// 步兵/坦克仅相邻（支援线→敌前线；前线→敌支援线与 HQ；同线禁止；HQ 攻击门槛）；
/// 空军/炮兵任意阵线（含 HQ、含同线）；零类型＝基线（视同步兵）；多类型＝存在性判定（各规则独立）；
/// 坦克双动/其余二选一（行动后清位面）；烟幕不可被攻击（对一切攻击者）；守护更新
/// （相邻被守护、仅能被炮/轰攻击、守护者不可被守护、HQ 计入、跨线不计、隔空不相邻、维护触点）。
/// </summary>
public class CommandRulesTests
{
    // ---------- 范围矩阵：步/坦仅相邻 ----------

    [Fact]
    public async Task Range_From_Support_Line_Only_Enemy_Front_Units_Are_Targets()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);

        var report = match.CommandManager.GetCommandAvailability(attacker);

        // 支援线 → 敌前线单位：允许；支援线 → 敌支援线单位 / 敌 HQ：禁止（不相邻）。
        Assert.True(report.Attack.CanUse);
        Assert.Contains(enemyOnFront.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(enemyOnSupport.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(playerB.Hq.Ref, report.Attack.Candidates); // W3-3：HQ 目标＝实体引用
    }

    [Fact]
    public async Task Range_From_Front_Line_Enemy_Support_Units_And_Hq_Are_Targets_But_Not_Same_Line()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);

        var report = match.CommandManager.GetCommandAvailability(attacker);

        // 前线 → 敌支援线单位 / 敌 HQ：允许；前线 → 前线（同线）：不允许（相邻＝跨线）。
        Assert.Contains(enemyOnSupport.Ref, report.Attack.Candidates);
        Assert.Contains(playerB.Hq.Ref, report.Attack.Candidates); // W3-3：HQ 目标＝实体引用
        Assert.DoesNotContain(enemyOnFront.Ref, report.Attack.Candidates);
    }

    [Fact]
    public async Task Range_Any_Line_Group_Is_Unrestricted()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        var fighter = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FighterId, 2);
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemyOnFront2 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var enemyOnFront3 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 2);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(artillery);
        CommandTestKit.Activate(fighter);
        CommandTestKit.Activate(bomber);
        var hqRef = playerB.Hq.Ref; // W3-3：HQ 目标＝实体引用

        // 炮兵/战斗机/轰炸机（任意线组）：全组合允许（含 HQ、含敌支援线、含同线）。
        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(enemyOnFront.Ref, artilleryReport.Attack.Candidates);
        Assert.Contains(enemyOnSupport.Ref, artilleryReport.Attack.Candidates);
        Assert.Contains(hqRef, artilleryReport.Attack.Candidates);

        var fighterReport = match.CommandManager.GetCommandAvailability(fighter);
        Assert.Contains(enemyOnSupport.Ref, fighterReport.Attack.Candidates); // Fighter 属任意组（第 14 轮修正）

        var bomberReport = match.CommandManager.GetCommandAvailability(bomber);
        Assert.Contains(enemyOnFront3.Ref, bomberReport.Attack.Candidates);
    }

    [Fact]
    public async Task Range_Typeless_Behaves_As_Infantry_Baseline()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var typeless = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TypelessId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(typeless);

        // 零类型＝基线：仅相邻（视同步兵）——支援线→敌前线允许、→敌支援线/HQ 禁止。
        var report = match.CommandManager.GetCommandAvailability(typeless);
        Assert.Contains(enemyOnFront.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(enemyOnSupport.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(playerB.Hq.Ref, report.Attack.Candidates); // W3-3：HQ 目标＝实体引用
    }

    [Fact]
    public async Task Range_Mixed_Type_Applies_Rules_Independently()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mix = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.MixId, 1);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(mix);

        // 存在性判定（任一命中即适用）：含 Artillery → 范围任意（支援线→敌支援线允许）；
        // 含 Tank → 双动（此处验证攻击后 CanMove 保留——见 ActionAfterMath 测试）。
        var report = match.CommandManager.GetCommandAvailability(mix);
        Assert.Contains(enemyOnSupport.Ref, report.Attack.Candidates);
        Assert.Contains(enemyOnFront.Ref, report.Attack.Candidates);
    }

    // ---------- 坦克双动 / 其余二选一 ----------

    [Fact]
    public async Task Tank_Can_Move_And_Attack_In_Same_Turn_DoubleAction()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var tank = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TankId, 1);
        var enemy = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        await match.EndTurn(); // → 玩家 B 回合
        await match.EndTurn(); // → 玩家 A 回合（回合同步恢复：两 bool＝true；槽 2＝2 点）

        // 动作 1：移动（坦克仅清 CanMove）。
        var moveResult = await CommandTestKit.RunCommandAsync(match, bridge, tank, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);
        Assert.Equal(CommandResultStatus.Success, moveResult.Status);
        Assert.False(tank.GetData<CommandData>().CanMove);
        Assert.True(tank.GetData<CommandData>().CanAttack); // 双动：独立清位、互不影响
        Assert.Equal(1, playerA.Points);

        // 动作 2：攻击（前线坦克 → 敌支援线单位）。
        var attackResult = await CommandTestKit.RunCommandAsync(match, bridge, tank, enemy.Ref);
        Assert.Equal(CommandResultStatus.Success, attackResult.Status);
        Assert.Equal(0, playerA.Points); // 两动作各扣一次行动费
        Assert.False(tank.GetData<CommandData>().CanAttack);
        Assert.Equal(2, enemy.GetData<UnitStateData>().Defense); // 5-3=2（互伤）
    }

    [Fact]
    public async Task Non_Tank_After_Move_Cannot_Attack_Exclusive_Choice()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 提供合法攻击目标
        CommandTestKit.Activate(artillery);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var moveResult = await CommandTestKit.RunCommandAsync(match, bridge, artillery, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);

        // 二选一：非坦克（炮兵）执行移动后，另一动作同被清（禁止再攻击）。
        Assert.Equal(CommandResultStatus.Success, moveResult.Status);
        Assert.False(artillery.GetData<CommandData>().CanMove);
        Assert.False(artillery.GetData<CommandData>().CanAttack);
        var report = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Equal(CommandBlockReason.FlagFalse, report.Attack.BlockReason);
    }

    // ---------- 烟幕 ----------

    [Fact]
    public async Task Smoke_Screen_Blocks_All_Attackers_And_Self_Actions_Are_Free()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 2);
        var smokeOwnerSide = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.SmokeId, 1); // 我方烟幕兵
        CommandTestKit.Activate(infantry);
        CommandTestKit.Activate(artillery);
        CommandTestKit.Activate(smokeOwnerSide);

        // 本批只做被攻击面：烟幕单位自身移动/攻击能力不受词条限制。
        // （适配〔后置项 C〕：先于敌方上线前验证——敌方前线占位将触发推进前置、与本断言语义无关。）
        var selfReport = match.CommandManager.GetCommandAvailability(smokeOwnerSide);
        Assert.True(selfReport.Move.CanUse);

        // 敌方烟幕兵（不可被攻击）：放入敌方前线（对步兵/炮兵均在可达范围内——区分度保留）。
        var enemySmoke = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.SmokeId, 0);

        // 烟幕：不可被攻击（对一切攻击者生效——含炮/轰；不进任何攻击目标候选、置黑）。
        var infantryReport = match.CommandManager.GetCommandAvailability(infantry);
        Assert.DoesNotContain(enemySmoke.Ref, infantryReport.Attack.Candidates);
        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.DoesNotContain(enemySmoke.Ref, artilleryReport.Attack.Candidates);
    }

    // ---------- 守护 ----------

    [Fact]
    public async Task Guard_Protects_Adjacent_Unit_From_Non_Bombard_Attackers()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // B 方前线：守护兵（槽 0）＋步兵（槽 1）——相邻（索引差 1）→ 步兵获「被守护」。
        var guardian = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.GuardianId, 0);
        var guardedUnit = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 2);
        CommandTestKit.Activate(infantry);
        CommandTestKit.Activate(artillery);

        // 可观测状态：相邻单位获得被守护。
        Assert.True(match.CommandManager.IsUnitGuarded(guardedUnit));

        // 被守护单位仅能被炮/轰攻击（步/坦视角＝筛除）；守护者自身不受影响（可被任意合法攻击者攻击）。
        var infantryReport = match.CommandManager.GetCommandAvailability(infantry);
        Assert.DoesNotContain(guardedUnit.Ref, infantryReport.Attack.Candidates);
        Assert.Contains(guardian.Ref, infantryReport.Attack.Candidates);

        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(guardedUnit.Ref, artilleryReport.Attack.Candidates); // 炮/轰可通过被守护
    }

    [Fact]
    public async Task Guard_Protects_Hq_And_Guardian_Itself_Is_Never_Guarded()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // B 支援线：守护兵 x2（位置 1＝紧随 HQ、位置 2）——守护者自身永不获被守护；HQ 由位置 1 的守护者保护。
        var guardian1 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.GuardianId, 1);
        var guardian2 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.GuardianId, 2);
        var infantry = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        CommandTestKit.Activate(infantry);
        CommandTestKit.Activate(artillery);
        var enemyHq = playerB.Hq; // W3-3：HQ 目标＝实体引用

        // 守护者不可被守护（任何情形，含另一守护者）；HQ 计入保护（槽 1 守护者 → HQ 被守护；Player 转发面／实体面双读）。
        Assert.False(match.CommandManager.IsUnitGuarded(guardian1));
        Assert.False(match.CommandManager.IsUnitGuarded(guardian2));
        Assert.True(match.CommandManager.IsHqGuarded(playerB));
        Assert.True(match.CommandManager.IsHqGuarded(playerB.Hq));

        // 被守护 HQ：步/坦（前线）不可攻；炮/轰可攻。
        var infantryReport = match.CommandManager.GetCommandAvailability(infantry);
        Assert.DoesNotContain(enemyHq.Ref, infantryReport.Attack.Candidates);
        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(enemyHq.Ref, artilleryReport.Attack.Candidates);

        // 守护者自身不受「被守护」影响：可被任意合法攻击者攻击（前线步兵 → 敌支援线守护者）。
        Assert.Contains(guardian1.Ref, infantryReport.Attack.Candidates);
    }

    [Fact]
    public async Task Guard_Does_Not_Cross_Lines_And_Requires_Physical_Adjacency()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // 有序列表模型：「物理相邻」＝元素位置序号差 1；"不相邻"不再靠空格，而是靠中间夹一个元素。
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.GuardianId, 1); // 守护者：位置 1（紧随 HQ）
        var nearUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2); // 位置 2：与守护者差 1 → 获
        var farUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 3); // 位置 3：与守护者差 2（中间隔着 nearUnit）→ 不获
        var crossLineUnit = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 跨线 → 不获

        Assert.True(match.CommandManager.IsUnitGuarded(nearUnit));
        Assert.False(match.CommandManager.IsUnitGuarded(farUnit)); // 非邻位不相邻（物理邻位＝索引差 1）
        Assert.False(match.CommandManager.IsUnitGuarded(crossLineUnit)); // 跨线不计

        // HQ 两侧任一邻位同规则：守护者在位置 1（HQ 邻位）→ HQ 获保护。
        Assert.True(match.CommandManager.IsHqGuarded(playerB));
    }

    [Fact]
    public async Task Guard_Hq_Requires_Adjacent_Guardian_Not_Blocked_By_Gap()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];
        // B 支援线：HQ（位置 0）＋中间单位（位置 1）＋守护者（位置 2）——守护者与 HQ 位置差 2 → 不相邻 → 不获保护。
        // 有序列表模型没有"空格"：把一个位置隔开必须由另一个元素占位（判定式"索引差 1"未变）。
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.GuardianId, 2);

        Assert.False(match.CommandManager.IsHqGuarded(playerB)); // 隔一个元素＝不相邻（物理邻位＝索引差 1）
    }

    [Fact]
    public async Task Guard_State_Maintained_On_Join_Move_And_Death_And_Multiple_Sources()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 场景（有序列表适配）：多源场景在无占位的前线表达——X 先入场（插入点 0），
        // 再把两个守护者插到 X 两侧（插入点 0 与末尾）→ [G1, X, G2]（X 与两者均相邻）。
        var x = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        Assert.False(match.CommandManager.IsUnitGuarded(x)); // 入场前无守护源
        var g1 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.GuardianId, 0); // 插到 X 左侧
        var g2 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.GuardianId, 2); // 追加到末尾（右侧）
        Assert.True(match.CommandManager.IsUnitGuarded(x)); // 入场占位触达维护

        // 「移走其一仍保留」→ 新布局等价面：失其一仍保留（多守护源存在性判定——前线单位不可移动
        //〔移动源须∈支援线〕，改以真实战斗击杀其一验证）。
        var beast1 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.BeastId, 1);
        var beast2 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.BeastId, 2);
        await match.EndTurn(); // → 玩家 B 回合（B 恢复：巨兽两 bool＝true；结算把点数刷新为槽值 1）
        await match.ResourceManager.AddPointsAsync(playerB, 2); // 击杀行动费用量（T2 结算后点数 1——两次击杀需 2）
        var killG1 = await CommandTestKit.RunCommandAsync(match, bridge, beast1, g1.Ref);
        Assert.Equal(CommandResultStatus.Success, killG1.Status);
        Assert.True(g1.GetData<UnitStateData>().IsDestroyed);
        Assert.True(match.CommandManager.IsUnitGuarded(x)); // 多守护源：失其一仍保留

        // 再杀 G2 → card.died 触达维护 → X 失去被守护。
        var killG2 = await CommandTestKit.RunCommandAsync(match, bridge, beast2, g2.Ref);
        Assert.Equal(CommandResultStatus.Success, killG2.Status);
        Assert.True(g2.GetData<UnitStateData>().IsDestroyed);
        Assert.False(match.CommandManager.IsUnitGuarded(x)); // 守护者死亡 → 清位后等效触达维护

        // 移动触点（支援线单位移动〔仅推进〕触发维护）：Y（位置 0）＋守护者 H（位置 1）——
        // 布局 [Y, H, HQ]：H 两侧邻位＝Y 与 HQ；H 移动走 → Y／HQ 失去被守护（HQ 联动）。
        await match.EndTurn(); // → 玩家 A 回合
        var y = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        Assert.False(match.CommandManager.IsUnitGuarded(y)); // 入场前无守护源
        var h = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.GuardianId, 1);
        Assert.True(match.CommandManager.IsUnitGuarded(y)); // 入场（Join）触达维护
        Assert.True(match.CommandManager.IsHqGuarded(playerA)); // HQ 邻位：H（位置 1）→ HQ 获守护
        CommandTestKit.Activate(h);
        var moveResult = await CommandTestKit.RunCommandAsync(match, bridge, h, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);
        Assert.Equal(CommandResultStatus.Success, moveResult.Status);
        Assert.False(match.CommandManager.IsUnitGuarded(y)); // 守护者移走（移动触达维护）→ 失去
        Assert.False(match.CommandManager.IsHqGuarded(playerA)); // HQ 无守护源（G2 死、H 走）→ 不获
    }
}
