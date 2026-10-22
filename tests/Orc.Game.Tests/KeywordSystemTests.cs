using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2C 验收④（对战词条；2C-A1 组件化迁移随改）：四词条各自行为（闪击＝部署（unit.deployed）时置位、仅部署路径；奋战＝记账双攻；
/// 烟幕＝不可被攻击〔CommandRulesTests〕；伏击＝改写〔CommandCombatTests〕）＋挂载面
/// （加载时经授予链挂载可查询＋词条组件注册；无词条卡＝无副作用；死亡后登记保留、行为注销）；定义校验
/// （未注册标识/同标识重复项 fail-fast）；回合恢复（两 bool 恢复＋奋战记账清零）。
/// </summary>
public class KeywordSystemTests
{
    // ---------- 闪击 ----------

    [Fact]
    public async Task Blitz_Deployment_Sets_Flags_Within_Play_Chain()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.BlitzId, toHand: true);
        var line = match.Battlefield.PlayerASupportLine;

        // 观测点（批 4 数据化调整）：unit.deployed 的「外部通道通知段」观测——置位批 4 起经数据效果 hooks 触发器
        // 承载（同一次更新广播的「订阅者广播段」——晚于本探针所处阶段）；本处不断言置位时点（阶段顺序属实现机制、
        // 非外部契约——语义差异文字申报），仅观测链中途费用；置位行为契约由链返回后断言覆盖。
        var probes = new List<(bool CanMove, bool CanAttack, int Points)>();
        using var probe = match.Engine.Subscribe((type, _, _) =>
        {
            if (type == GameUpdates.UnitDeployed)
            {
                var command = unit.GetData<CommandData>();
                probes.Add((command.CanMove, command.CanAttack, player.Points));
            }

            return Task.CompletedTask;
        });

        var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps()[1]);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        var mid = Assert.Single(probes);
        Assert.Equal(1, mid.Points); // 链中途：仍未扣费（扣费时点不变——链收尾）
        Assert.True(unit.GetData<CommandData>().CanMove);  // 链完成后：状态一致（覆盖部署初值）
        Assert.True(unit.GetData<CommandData>().CanAttack);
        Assert.Equal(0, player.Points);
    }

    [Fact]
    public async Task Non_Blitz_Deployment_Keeps_Flags_False()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);

        var result = await match.PlayManager.PlayUnitAsync(unit, match.Battlefield.PlayerASupportLine.GetInsertionGaps()[1]);

        // 非闪击单位部署当回合保持 false/false（回合恢复方可行动）。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.False(unit.GetData<CommandData>().CanAttack);
    }

    [Fact]
    public async Task Blitz_Via_Join_Path_Does_Not_Set_Flags()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 闪击仅部署路径生效：加入路径（unit.joined）不置位——保持 false/false。
        var unit = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.BlitzId);
        await unit.LoadAsync(player);
        var result = await match.PlayManager.JoinUnitAsync(unit, match.Battlefield.FrontLine.GetInsertionGaps()[0]);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.False(unit.GetData<CommandData>().CanAttack);
    }

    // ---------- 奋战 ----------

    [Fact]
    public async Task Fury_First_Attack_Keeps_CanAttack_And_Clears_CanMove_Second_Attack_Locks()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var fury = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FuryId, 1); // 攻 2 / 防 6
        var enemy = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.GuardianId, 0); // 攻 0 / 防 6（零反击肉盾——专测记账）
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        await match.EndTurn(); // → B 回合
        await match.EndTurn(); // → A 回合 3（2 点；两 bool 恢复）

        // 首攻：奋战记账 +1 → 收尾读取：CanAttack 保持可攻；非坦克二选一仍生效（CanMove 清）。
        var first = await CommandTestKit.RunCommandAsync(match, bridge, fury, enemy.Ref);
        Assert.Equal(CommandResultStatus.Success, first.Status);
        Assert.True(fury.GetData<CommandData>().CanAttack);
        Assert.False(fury.GetData<CommandData>().CanMove);
        Assert.Equal(4, enemy.GetData<UnitStateData>().Defense); // 6-2=4

        // 二攻：记账 +2 → CanAttack 置 false（二次后不可）；双方均存活（肉盾无反伤）。
        var second = await CommandTestKit.RunCommandAsync(match, bridge, fury, enemy.Ref);
        Assert.Equal(CommandResultStatus.Success, second.Status);
        Assert.False(fury.GetData<CommandData>().CanAttack);
        Assert.Equal(2, enemy.GetData<UnitStateData>().Defense); // 4-2=2（仍存活）
        Assert.Equal(6, fury.GetData<UnitStateData>().Defense);  // 无反伤
    }

    [Fact]
    public async Task Fury_Tank_Moves_Once_And_Attacks_Twice()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var furyTank = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FuryTankId, 1); // 坦克＋奋战
        var enemy = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2); // 防 5
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        await match.EndTurn();
        await match.EndTurn();
        await match.EndTurn();
        await match.EndTurn(); // → A 回合 5（3 点；两 bool 恢复）

        // 动作 1：移动（坦克双动：仅清 CanMove）。
        var move = await CommandTestKit.RunCommandAsync(match, bridge, furyTank, match.Battlefield.FrontLine.GetInsertionGaps()[0].Ref);
        Assert.Equal(CommandResultStatus.Success, move.Status);
        Assert.False(furyTank.GetData<CommandData>().CanMove);
        Assert.True(furyTank.GetData<CommandData>().CanAttack);

        // 动作 2：首攻（奋战记账；CanAttack 保持）。
        var first = await CommandTestKit.RunCommandAsync(match, bridge, furyTank, enemy.Ref);
        Assert.Equal(CommandResultStatus.Success, first.Status);
        Assert.True(furyTank.GetData<CommandData>().CanAttack);

        // 动作 3：二攻（奋战记账满 → CanAttack 置 false）。
        var second = await CommandTestKit.RunCommandAsync(match, bridge, furyTank, enemy.Ref);
        Assert.Equal(CommandResultStatus.Success, second.Status);
        Assert.False(furyTank.GetData<CommandData>().CanAttack);
        Assert.Equal(0, playerA.Points); // 三次动作各扣一次（移动 1＋攻 2）
        Assert.Equal(1, enemy.GetData<UnitStateData>().Defense); // 5-2-2=1
    }

    [Fact]
    public async Task Fury_Counter_Resets_On_Turn_Refresh_And_Unit_Actionable_Again()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var fury = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FuryId, 1);
        var enemy = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.GuardianId, 0); // 攻 0 / 防 6（零反击肉盾）
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        await match.EndTurn();
        await match.EndTurn(); // → A 回合 3（2 点）

        // 本回合攻两次：CanAttack 置 false（记账满）。
        await CommandTestKit.RunCommandAsync(match, bridge, fury, enemy.Ref);
        await CommandTestKit.RunCommandAsync(match, bridge, fury, enemy.Ref);
        Assert.False(fury.GetData<CommandData>().CanAttack);

        await match.EndTurn(); // → B 回合（A 单位不恢复）
        Assert.False(fury.GetData<CommandData>().CanAttack); // 非行动方不恢复
        await match.EndTurn(); // → A 回合 5（3 点；恢复：两 bool＝true＋记账清零）

        Assert.True(fury.GetData<CommandData>().CanAttack); // 跨回合恢复后可再次行动
        Assert.True(fury.GetData<CommandData>().CanMove);

        // 记账清零验证：再攻一次后（本轮第一次＝count 1）仍保持可攻（若未清零将达 count 3 → 不可攻）。
        var again = await CommandTestKit.RunCommandAsync(match, bridge, fury, enemy.Ref);
        Assert.Equal(CommandResultStatus.Success, again.Status);
        Assert.True(fury.GetData<CommandData>().CanAttack);
    }

    // ---------- 挂载面与加载 ----------

    [Fact]
    public async Task Keywords_Are_Registered_At_Load_And_Kept_After_Death()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 加载时（卡在卡组、未部署）即完成挂载：卡上词条面可查询（卡牌固有属性）。
        var blitz = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.BlitzId);
        Assert.True(blitz.Keywords.Has(KeywordIds.Blitz));
        Assert.True(KeywordRules.HasKeyword(blitz, KeywordIds.Blitz));

        // 无词条卡＝无副作用（不注册词条组件、不装载逻辑；词条面可寻址、空内容）。
        var plain = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId);
        Assert.Empty(plain.Keywords.Components);
        Assert.False(plain.Keywords.Has(KeywordIds.Blitz));

        // 词条卡：词条组件已挂载（闪击＝能力型组件列入 Components——行为经内嵌效果承载）。
        Assert.Contains(blitz.Keywords.Components, component => component.Keyword == KeywordIds.Blitz);

        // 死亡后：登记保留（可查询）；不再参与结算（候选层——尸体不在场）。
        var killer = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BeastId, 4);
        var ambusher = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.AmbushId, 2);
        CommandTestKit.Activate(killer);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var kill = await CommandTestKit.RunCommandAsync(match, bridge, killer, ambusher.Ref);
        Assert.Equal(CommandResultStatus.Success, kill.Status);
        Assert.True(ambusher.GetData<UnitStateData>().IsDestroyed);
        Assert.True(KeywordRules.HasKeyword(ambusher, KeywordIds.Ambush)); // 登记保留（死亡注销仅行为撤销）
        Assert.Empty(ambusher.Keywords.Components); // 行为态清空
        Assert.DoesNotContain(ambusher, CommandTestKit.AllUnitsOf(match)); // 不再在场（不参与筛选/指挥）
    }

    // ---------- 定义校验（fail-fast） ----------

    [Fact]
    public void Definition_Rejects_Unimplemented_Keyword()
    {
        // 批 5 受控适配：样本由「冲击」改合成稳定字面（冲击经批 5 注册——不再属未实现）；
        // 合成样本——不再链式迁移。
        var ex = Assert.Throws<ArgumentException>(() => new CardDefinition(
            "怪卡", 1, 1, 1, 1, keywords: new[] { new KeywordDeclaration("未注册示例词条") }, faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.Contains("未实现标识", ex.Message);
    }

    [Fact]
    public void Definition_Rejects_Duplicate_Keywords()
    {
        // 同标识即重复（不论参值）——不同参值亦拒绝。
        var ex = Assert.Throws<ArgumentException>(() => new CardDefinition(
            "怪卡", 1, 1, 1, 1,
            keywords: new[] { new KeywordDeclaration(KeywordIds.Blitz, 1), new KeywordDeclaration(KeywordIds.Blitz, 2) },
            faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.Contains("重复项", ex.Message);
    }

    [Fact]
    public void Definition_Rejects_Duplicate_UnitTypes()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CardDefinition(
            "怪卡", 1, 1, 1, 1, unitTypes: new[] { UnitType.Tank, UnitType.Tank }, faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.Contains("重复项", ex.Message);
    }

    [Fact]
    public void Definition_Accepts_Empty_And_Copies_Declarations()
    {
        // 缺省＝空列表（零类型/无词条——合法声明）；声明顺序保留。
        var definition = new CardDefinition(
            "标准", 1, 1, 1, 1,
            keywords: new[] { new KeywordDeclaration(KeywordIds.Fury), new KeywordDeclaration(KeywordIds.Ambush) },
            unitTypes: new[] { UnitType.Artillery, UnitType.Bomber }, faction: Faction.Germany, rarity: Rarity.Standard);

        Assert.Equal(
            new[] { new KeywordDeclaration(KeywordIds.Fury), new KeywordDeclaration(KeywordIds.Ambush) },
            definition.Keywords);
        Assert.Equal(new[] { UnitType.Artillery, UnitType.Bomber }, definition.UnitTypes);
        Assert.False(definition.IsGuard);

        // 带参值声明（「仅标识」＝参值位空形态——同一类型、同一解析归一）
        var withValue = new CardDefinition(
            "参值", 1, 1, 1, 1,
            keywords: new[] { new KeywordDeclaration(KeywordIds.Blitz, 2) }, faction: Faction.Germany, rarity: Rarity.Standard);
        var declaration = Assert.Single(withValue.Keywords);
        Assert.Equal(KeywordIds.Blitz, declaration.Id);
        Assert.Equal(2, declaration.Value);

        var plain = new CardDefinition("空白", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard);
        Assert.Empty(plain.Keywords);
        Assert.Empty(plain.UnitTypes);
    }

    // ---------- 单位类型填充（单位化时以定义为单一真源） ----------

    [Fact]
    public async Task Unit_Types_Are_Filled_From_Definition_On_Unitize()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var mix = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.MixId, 1);
        var typeless = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.TypelessId, 2);

        Assert.Equal(new[] { UnitType.Tank, UnitType.Artillery }, mix.GetData<UnitStateData>().UnitTypes);
        Assert.Empty(typeless.GetData<UnitStateData>().UnitTypes); // 未声明＝零类型（显式空列表基线）
    }
}
