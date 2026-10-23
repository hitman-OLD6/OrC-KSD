using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 后置项 A（反击豁免判定表）：判定表每条规则分支≥1 用例——①目标轰炸机永不反击；②攻击者炮兵不受任何反击；
/// ③攻击者轰炸机不受反击、例外＝目标战斗机可反击；④其余（步·坦·机互攻、被步攻击时炮兵反击）正常反击；
/// 多类型冲突两例（Round 2：炮兵＋轰炸机攻击者 / 轰炸机＋炮兵目标——豁免优先）；
/// 伏击×豁免三态（无资格＝改写不成立按表单方结算〔炮兵 / 轰炸机×非战斗机 / 轰炸机目标三场景〕；
/// 有资格＋命中＝改写〔轰炸机×伏击战斗机例外路径〕；有资格＋不命中＝正常互伤〔既有用例〕）。
/// </summary>
public class CommandImmunityTests
{
    // ---------- 判定表①：目标＝轰炸机 → 永不反击 ----------

    [Fact]
    public async Task Target_Bomber_Never_Counterattacks_When_Attacked()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1); // 攻 1 / 防 2
        var bomber = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 0); // 攻 4 / 防 2（轰炸机）
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, bomber.Ref);

        // 判定表①：目标＝轰炸机 → 永不反击。目标正常受伤（2-1=1、存活）；攻击者不受反击（防 2 不变）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(1, bomber.GetData<UnitStateData>().Defense);
        Assert.False(bomber.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, attacker.GetData<UnitStateData>().Defense);
    }

    // ---------- 判定表②：攻击者＝炮兵 → 不受任何反击 ----------

    [Fact]
    public async Task Attacker_Artillery_Receives_No_Counterattack()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2（炮兵）
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        CommandTestKit.Activate(artillery);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, artillery, infantry.Ref);

        // 判定表②：攻击者＝炮兵 → 不受任何反击（对任意目标）。目标 5-2=3；炮兵防 2 不变（无反击伤害）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(3, infantry.GetData<UnitStateData>().Defense);
        Assert.Equal(2, artillery.GetData<UnitStateData>().Defense);
    }

    // ---------- 判定表③：攻击者＝轰炸机（例外＝目标战斗机可反击） ----------

    [Fact]
    public async Task Attacker_Bomber_Is_Countered_Only_By_Fighter_Target()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1); // 攻 4 / 防 2（轰炸机）
        var fighter = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.FighterId, 1); // 攻 3 / 防 2（战斗机）
        CommandTestKit.Activate(bomber);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, bomber, fighter.Ref);

        // 判定表③例外：目标＝战斗机可反击 → 互伤同归（战斗机 2-4=0 死；轰炸机 2-3=0 死——反击发生的证据）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(fighter.GetData<UnitStateData>().IsDestroyed);
        Assert.True(bomber.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Attacker_Bomber_Is_Not_Countered_By_Non_Fighter_Target()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1); // 攻 4 / 防 2（轰炸机）
        var infantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        CommandTestKit.Activate(bomber);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, bomber, infantry.Ref);

        // 判定表③负例：目标非战斗机 → 不反击。步兵 5-4=1（存活）；轰炸机防 2 不变。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(1, infantry.GetData<UnitStateData>().Defense);
        Assert.Equal(2, bomber.GetData<UnitStateData>().Defense);
    }

    // ---------- 判定表④：其余 → 正常反击 ----------

    [Fact]
    public async Task Regular_Units_Still_Trade_Mutual_Damage()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 判定表④：其余 → 正常反击（常规互伤：5-2=3 与 5-2=3）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(3, target.GetData<UnitStateData>().Defense);
        Assert.Equal(3, attacker.GetData<UnitStateData>().Defense);
    }

    [Fact]
    public async Task Artillery_Target_Counterattacks_When_Attacked_By_Infantry()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2（炮兵）
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, artillery.Ref);

        // 判定表④交叉：被步/坦/机攻击时炮兵反击（目标非轰炸机、攻击者非炮/轰 → 正常资格）。
        // 炮兵 2-2=0 死（死前反击照算）；步兵 5-2=3。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(artillery.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(3, attacker.GetData<UnitStateData>().Defense);
    }

    // ---------- 多类型冲突（Round 2 两例：豁免优先） ----------

    [Fact]
    public async Task Multi_Type_Attacker_Absolute_Immunity_Outranks_Bomber_Exception()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mixAir = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.MixAirId, 1); // 炮兵＋轰炸机；攻 2 / 防 5
        var fighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 0); // 战斗机；攻 3 / 防 2
        CommandTestKit.Activate(mixAir);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, mixAir, fighter.Ref);

        // 多类型冲突例一：攻击者含炮兵＋轰炸机攻击战斗机 → 不受反击（炮兵条款的绝对豁免优先于轰炸机条款的例外）。
        // 战斗机 2-2=0 死；攻击者防 5 不变（若错误适用例外反击 3 → 变为 2）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(fighter.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(5, mixAir.GetData<UnitStateData>().Defense);
    }

    [Fact]
    public async Task Multi_Type_Target_Bomber_Never_Counterattacks()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var beast = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BeastId, 1); // 攻 6 / 防 7
        var mixAir = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.MixAirId, 0); // 轰炸机＋炮兵；攻 2 / 防 5
        CommandTestKit.Activate(beast);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, beast, mixAir.Ref);

        // 多类型冲突例二：目标含轰炸机＋炮兵被步兵攻击 → 不反击（轰炸机「永不反击」优先于炮兵「被步/坦会被反击」）。
        // 目标 5-6=0 死；攻击者防 7 不变（若错误适用炮兵条款反击 2 → 变为 5）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(mixAir.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(7, beast.GetData<UnitStateData>().Defense);
    }

    // ---------- 伏击×豁免（先资格、后条件；无资格＝改写不成立） ----------

    [Fact]
    public async Task Ambush_No_Rewrite_When_Bomber_Attacks_Non_Fighter_Target()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1); // 攻 4 / 防 2
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）
        CommandTestKit.Activate(bomber);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, bomber, ambusher.Ref);

        // 伏击×豁免（无资格）：攻击者＝轰炸机、目标非战斗机 → 无反击资格 → 改写不成立、按表单方结算：
        // 目标正常受伤（6-4=2、存活；对照：伏击条件 5 ＞ 2 本会命中——豁免约束改写的回归锁）；轰炸机不受反击（防 2 不变）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(2, ambusher.GetData<UnitStateData>().Defense);
        Assert.False(ambusher.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, bomber.GetData<UnitStateData>().Defense);
    }

    [Fact]
    public async Task Ambush_No_Rewrite_When_Artillery_Attacks_Target()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）
        CommandTestKit.Activate(artillery);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, artillery, ambusher.Ref);

        // 伏击×豁免（无资格）：攻击者＝炮兵 → 不受任何反击 → 改写不成立、按表单方结算：
        // 目标 6-2=4（存活）；炮兵防 2 不变、不会死亡（对照：若改写误判成立，攻击者将死亡）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(4, ambusher.GetData<UnitStateData>().Defense);
        Assert.False(ambusher.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, artillery.GetData<UnitStateData>().Defense);
        Assert.False(artillery.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Ambush_Rewrites_When_Bomber_Attacks_Fighter_Target()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1); // 攻 4 / 防 2
        var ambushFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushFighterId, 0); // 攻 5 / 防 2（战斗机＋伏击）
        CommandTestKit.Activate(bomber);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, bomber, ambushFighter.Ref);

        // 伏击×豁免（有资格＋命中）：攻击者＝轰炸机、目标＝战斗机 → 例外可反击（资格通过）→ 伏击条件命中
        //（战斗机攻 5 ＞ 轰炸机防 2）→ 改写：轰炸机死亡（统一死亡流程、无 HP 扣减语义）；战斗机不受伤。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(bomber.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, bomber.GetData<UnitStateData>().Defense); // 无 HP 扣减（直接死亡结果）
        Assert.Equal(2, ambushFighter.GetData<UnitStateData>().Defense); // 不受伤
        Assert.False(ambushFighter.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Ambush_No_Rewrite_For_Bomber_Target_With_Ambush()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var beast = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BeastId, 1); // 攻 6 / 防 7
        var ambushBomber = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushBomberId, 0); // 攻 4 / 防 2（轰炸机＋伏击）
        CommandTestKit.Activate(beast);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, beast, ambushBomber.Ref);

        // 伏击×豁免（场景二：目标轰炸机带伏击被步兵攻击）：目标＝轰炸机 → 永不反击（无资格）→ 改写不成立 →
        // 按表：目标单方受伤（2-6 → 死）；攻击者不受伤（防 7 不变——对照：若无豁免将挨 4 伤；若改写误判成立将死亡）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(ambushBomber.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(7, beast.GetData<UnitStateData>().Defense);
        Assert.False(beast.GetData<UnitStateData>().IsDestroyed);
    }
}
