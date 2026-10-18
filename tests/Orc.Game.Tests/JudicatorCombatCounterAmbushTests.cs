using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// K2 反击/伏击判定收编（C5 combat.counter.eligibility／C6 combat.ambush.condition）游戏层测试：
/// ①默认行为等价：五条款逐条（条目级——冲击经批 5 纳入、置于最前）＋流程级（炮兵不受反击／轰炸机×战斗机例外互伤）；
/// ②伏击条件默认：命中/不命中两态＋「攻＝防」严格边界（条目级）＋流程级（改写成立／正常互伤）；
/// ③核心演示：单例改写（C5 恒真——无条件反击）→ 默认互伤区（原本被豁免的回击伤害恢复）与
///   伏击资格（原本被拦资格的改写放行）两处同步行为断言 → 注销回退（闭环）；
/// ④轻量演示：单例改写（C6 恒假）→ 伏击条件判定行为变化（条目级＋流程级）→ 注销回退；
/// ⑤契约：条目经注册表直接调用（可解析、独立可用、载荷契约 fail-fast 边界）＋null 单位语义保持；
/// ⑥独立构造路径（无注册表）内置默认通道构造即可用、行为与注册路径一致。
/// 链路口径：一律经判定器条目句柄／装载上下文通道调用——不直连判定器实例、不直调静态规则（单源）。
/// 多类型单位边界（类型增补联动）复用既有 CardServiceTests.Scenario7 断言陈述覆盖（迁移为经条目读值）。
/// </summary>
public class JudicatorCombatCounterAmbushTests
{
    // ---------- ① 五条款逐条（条目级；含多类型交叉——豁免优先） ----------

    [Fact]
    public async Task Counter_Eligibility_Default_Terms_One_To_Five_Entries()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 炮兵；攻 2 / 防 2
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 2); // 轰炸机；攻 4 / 防 2
        var weak = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1); // 步兵；攻 1 / 防 2
        var shocked = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0); // 冲击步兵（批 5；运行时授予）
        var mixAir = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.MixAirId, 0); // 炮兵＋轰炸机；攻 2 / 防 5
        var enemyInfantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var enemyFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 2); // 攻 3 / 防 2
        var enemyBomber = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 3); // 攻 4 / 防 2

        // 默认名恒可解析（固定内置注册段——无条件可用）。
        var entry = match.Judicators.Resolve(JudicatorNames.CombatCounterEligibility);
        Assert.NotNull(entry);

        // ⓪（批 5）攻击者具冲击 → 不受反击（豁免族最高优先——先于①②③豁免；对照④同组合本应 true）。
        Assert.True(await shocked.Keywords.GrantAsync(KeywordIds.Shock));
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { shocked, enemyInfantry }));

        // ① 目标＝轰炸机 → 永不反击（绝对豁免）。
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { weak, enemyBomber }));

        // ② 攻击者＝炮兵 → 不受任何反击。
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { artillery, enemyInfantry }));

        // ③ 主情形：攻击者＝轰炸机、目标非战斗机 → 不受反击。
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { bomber, enemyInfantry }));

        // ③ 例外子情形：攻击者＝轰炸机、目标＝战斗机 → 可反击。
        Assert.Equal(new object[] { true }, entry.Invoke(new object[] { bomber, enemyFighter }));

        // ④ 其余 → 正常反击。
        Assert.Equal(new object[] { true }, entry.Invoke(new object[] { weak, enemyInfantry }));
        Assert.Equal(new object[] { true }, entry.Invoke(new object[] { enemyInfantry, artillery })); // 步兵×炮兵目标：正常（炮兵仅攻击者侧豁免——目标侧无豁免条款）

        // 交叉（豁免优先，建议项）：炮兵＋轰炸机攻击者 vs 战斗机 → 炮兵条款绝对豁免优先于轰炸机条例外。
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { mixAir, enemyFighter }));

        // 交叉：目标含轰炸机＋炮兵 → 目标轰炸机条款优先（永不反击）。
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { weak, mixAir }));

        // 对照（防未使用变量）：全部引用仍在场（本测试仅只读调用、零副作用）。
        Assert.True(enemyBomber.Ref.IsAlive);
        Assert.True(mixAir.Ref.IsAlive);
    }

    // ---------- ① 流程级：②炮兵不受反击（单方结算）＋③例外（轰炸机×战斗机互伤同归） ----------

    [Fact]
    public async Task Counter_Eligibility_Default_Process_Exemptions_And_Fighter_Exception()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 2); // 攻 4 / 防 2
        var targetInfantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        var targetFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 1); // 攻 3 / 防 2
        CommandTestKit.Activate(artillery);
        CommandTestKit.Activate(bomber);
        await match.ResourceManager.AddPointsAsync(playerA, 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // ② 攻击者＝炮兵 → 不受任何反击（流程级）：目标正常受伤（5-2=3）、炮兵防御不变。
        var first = await CommandTestKit.RunCommandAsync(match, bridge, artillery, targetInfantry.Ref);
        Assert.Equal(CommandResultStatus.Success, first.Status);
        Assert.Equal(3, targetInfantry.GetData<UnitStateData>().Defense);
        Assert.Equal(2, artillery.GetData<UnitStateData>().Defense);
        Assert.False(artillery.GetData<UnitStateData>().IsDestroyed);

        // ③ 例外：攻击者＝轰炸机、目标＝战斗机 → 可反击（互伤同归——反击发生的证据）。
        var second = await CommandTestKit.RunCommandAsync(match, bridge, bomber, targetFighter.Ref);
        Assert.Equal(CommandResultStatus.Success, second.Status);
        Assert.True(targetFighter.GetData<UnitStateData>().IsDestroyed);
        Assert.True(bomber.GetData<UnitStateData>().IsDestroyed);
    }

    // ---------- ② 伏击条件：命中/不命中两态＋「攻＝防」严格边界（条目级） ----------

    [Fact]
    public async Task Ambush_Condition_Default_Hit_Miss_And_Strict_Boundary_Entries()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var weak = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 2); // 攻 1 / 防 2
        var ambusher = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.AmbushId, 1); // 攻 5 / 防 6（伏击）
        var enemyInfantry = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var enemyArtillery = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.ArtilleryId, 2); // 攻 2 / 防 2

        var entry = match.Judicators.Resolve(JudicatorNames.CombatAmbushCondition);
        Assert.NotNull(entry);

        // 命中：被攻击单位攻击有效值 5 ＞ 攻击者防御有效值 2。
        Assert.Equal(new object[] { true }, entry.Invoke(new object[] { ambusher, enemyArtillery }));

        // 不命中：2 ＞ 5 为假。
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { infantry, enemyInfantry }));

        // 严格边界（攻＝防 → 不命中）：2 ＞ 2 为假。
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { infantry, weak }));
    }

    // ---------- ② 流程级：命中→改写成立（攻击者死亡）＋不命中（攻＝防）→正常互伤 ----------

    [Fact]
    public async Task Ambush_Condition_Default_Process_Rewrite_Or_Normal_Trade()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var weak = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1); // 攻 1 / 防 2
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2); // 攻 2 / 防 5
        var ambusherHit = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）
        var ambusherMiss = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 1); // 攻 5 / 防 6（伏击）
        CommandTestKit.Activate(weak);
        CommandTestKit.Activate(infantry);
        await match.ResourceManager.AddPointsAsync(playerA, 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 命中（被攻击单位攻 5 ＞ 攻击者防 2）→ 改写成立：攻击者死亡（无 HP 扣减语义）、伏击者不受伤。
        var first = await CommandTestKit.RunCommandAsync(match, bridge, weak, ambusherHit.Ref);
        Assert.Equal(CommandResultStatus.Success, first.Status);
        Assert.True(weak.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, weak.GetData<UnitStateData>().Defense); // 无 HP 扣减（直接死亡结果）
        Assert.False(ambusherHit.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(6, ambusherHit.GetData<UnitStateData>().Defense);

        // 不命中（攻＝防边界：5 ＞ 5 为假）→ 正常互伤：伏击者受伤（6-2=4）、攻击者受反击致死（5-5=0）。
        var second = await CommandTestKit.RunCommandAsync(match, bridge, infantry, ambusherMiss.Ref);
        Assert.Equal(CommandResultStatus.Success, second.Status);
        Assert.Equal(4, ambusherMiss.GetData<UnitStateData>().Defense);
        Assert.False(ambusherMiss.GetData<UnitStateData>().IsDestroyed);
        Assert.True(infantry.GetData<UnitStateData>().IsDestroyed);
    }

    // ---------- ③ 核心演示：单例改写（C5 恒真）——两调用点同步生效＋注销回退 ----------

    [Fact]
    public async Task Counter_Eligibility_Moding_Syncs_Default_Damage_And_Ambush_Qualification()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var art1 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        var art2 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 2);
        var art3 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        var art4 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.ArtilleryId, 0);
        var art5 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.ArtilleryId, 3);
        var infTarget1 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var infTarget2 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var infTarget3 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var ambusher1 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 2); // 攻 5 / 防 6（伏击）
        var ambusher2 = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.AmbushId, 1); // 攻 5 / 防 6（伏击）
        foreach (var unit in new[] { art1, art2, art3, art4, art5 })
        {
            CommandTestKit.Activate(unit);
        }

        await match.ResourceManager.AddPointsAsync(playerA, 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // ---------- 默认基线（改写前） ----------

        // 调用点一（默认互伤区）：炮兵攻击 → 不受反击（豁免②）——目标正常受伤、炮兵不变。
        var baselineA = await CommandTestKit.RunCommandAsync(match, bridge, art1, infTarget1.Ref);
        Assert.Equal(CommandResultStatus.Success, baselineA.Status);
        Assert.Equal(3, infTarget1.GetData<UnitStateData>().Defense);
        Assert.Equal(2, art1.GetData<UnitStateData>().Defense);
        Assert.False(art1.GetData<UnitStateData>().IsDestroyed);

        // 调用点二（伏击资格）：炮兵攻击带伏击目标 → 无资格（豁免约束改写）→ 改写不成立、按表单方结算。
        var baselineB = await CommandTestKit.RunCommandAsync(match, bridge, art2, ambusher1.Ref);
        Assert.Equal(CommandResultStatus.Success, baselineB.Status);
        Assert.Equal(4, ambusher1.GetData<UnitStateData>().Defense); // 6-2 受伤（对照：条件 5 ＞ 2 本会命中——资格拦截）
        Assert.Equal(2, art2.GetData<UnitStateData>().Defense);
        Assert.False(art2.GetData<UnitStateData>().IsDestroyed);

        // ---------- 单例改写（combat.counter.eligibility → 恒真：无条件反击） ----------

        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CombatCounterEligibility),
            _ => new object[] { true });
        Assert.NotNull(moding);

        // 调用点一（默认互伤区）同步生效：原本被豁免的回击伤害出现——炮兵受反击致死（2-2=0）。
        var modedA = await CommandTestKit.RunCommandAsync(match, bridge, art3, infTarget2.Ref);
        Assert.Equal(CommandResultStatus.Success, modedA.Status);
        Assert.True(art3.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(3, infTarget2.GetData<UnitStateData>().Defense); // 目标照常受伤（5-2=3）

        // 调用点二（伏击资格）同步生效：原本被资格拦截的伏击改写放行——改写成立（炮兵死亡、伏击者不受伤）。
        var modedB = await CommandTestKit.RunCommandAsync(match, bridge, art4, ambusher2.Ref);
        Assert.Equal(CommandResultStatus.Success, modedB.Status);
        Assert.True(art4.GetData<UnitStateData>().IsDestroyed); // 改写成立：攻击者死亡
        Assert.Equal(6, ambusher2.GetData<UnitStateData>().Defense); // 伏击者不受伤
        Assert.False(ambusher2.GetData<UnitStateData>().IsDestroyed);

        // ---------- 注销回退 ----------

        Assert.True(match.Judicators.UnregisterWrapper(moding!));

        // 回退（默认互伤区）：豁免恢复——炮兵不受反击。
        var restored = await CommandTestKit.RunCommandAsync(match, bridge, art5, infTarget3.Ref);
        Assert.Equal(CommandResultStatus.Success, restored.Status);
        Assert.Equal(2, art5.GetData<UnitStateData>().Defense);
        Assert.False(art5.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(3, infTarget3.GetData<UnitStateData>().Defense);
    }

    // ---------- ④ 轻量演示：单例改写（C6 恒假）——条件判定的行为变化＋注销回退 ----------

    [Fact]
    public async Task Ambush_Condition_Moding_Flips_Outcome_And_Unregisters()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var weak1 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1); // 攻 1 / 防 2
        var weak2 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 2);
        var weak3 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        var ambusher1 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）
        var ambusher2 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 1);
        var ambusher3 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 2);
        CommandTestKit.Activate(weak1);
        CommandTestKit.Activate(weak2);
        CommandTestKit.Activate(weak3);
        await match.ResourceManager.AddPointsAsync(playerA, 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 条目可解析（固定内置注册段——无条件可用；moding 寻址前提）。
        var entry = match.Judicators.Resolve(JudicatorNames.CombatAmbushCondition);
        Assert.NotNull(entry);

        // 默认基线：条件命中（5 ＞ 2）→ 改写成立（攻击者死亡、伏击者不受伤）。
        var baseline = await CommandTestKit.RunCommandAsync(match, bridge, weak1, ambusher1.Ref);
        Assert.Equal(CommandResultStatus.Success, baseline.Status);
        Assert.True(weak1.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(6, ambusher1.GetData<UnitStateData>().Defense);

        // 单例改写（combat.ambush.condition → 恒假）：条目级即时可见。
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CombatAmbushCondition),
            _ => new object[] { false });
        Assert.NotNull(moding);
        Assert.Equal(new object[] { false }, entry.Invoke(new object[] { ambusher2, weak2 }));

        // 改写后流程级：条件不命中 → 正常互伤（伏击者受伤 6-1=5、攻击者受反击致死 2-5=0）。
        var moded = await CommandTestKit.RunCommandAsync(match, bridge, weak2, ambusher2.Ref);
        Assert.Equal(CommandResultStatus.Success, moded.Status);
        Assert.Equal(5, ambusher2.GetData<UnitStateData>().Defense);
        Assert.False(ambusher2.GetData<UnitStateData>().IsDestroyed);
        Assert.True(weak2.GetData<UnitStateData>().IsDestroyed);

        // 注销回退：条目级恢复默认（命中）＋流程级恢复改写成立。
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.Equal(new object[] { true }, entry.Invoke(new object[] { ambusher3, weak3 }));
        var restored = await CommandTestKit.RunCommandAsync(match, bridge, weak3, ambusher3.Ref);
        Assert.Equal(CommandResultStatus.Success, restored.Status);
        Assert.True(weak3.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(6, ambusher3.GetData<UnitStateData>().Defense);
    }

    // ---------- ⑤ 契约：条目直调（可解析、独立可用、载荷 fail-fast 边界） ----------

    [Fact]
    public async Task Entries_Resolvable_Directly_Invocable_And_Contract_Boundaries()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        var infantry = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2); // 攻 2 / 防 5
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）

        // 默认名恒可解析（两条内置注册——无条件可用）。
        var counter = match.Judicators.Resolve(JudicatorNames.CombatCounterEligibility);
        var condition = match.Judicators.Resolve(JudicatorNames.CombatAmbushCondition);
        Assert.NotNull(counter);
        Assert.NotNull(condition);

        // C5（（攻击者, 目标）→ bool）直接调用：炮兵攻击者 → 不受反击；步兵×步兵 → 正常反击。
        Assert.Equal(new object[] { false }, counter.Invoke(new object[] { artillery, infantry }));
        Assert.Equal(new object[] { true }, counter.Invoke(new object[] { infantry, ambusher }));

        // C6（（被攻击单位, 攻击者）→ bool）直接调用：5 ＞ 2 命中；2 ＞ 5 不命中。
        Assert.Equal(new object[] { true }, condition.Invoke(new object[] { ambusher, artillery }));
        Assert.Equal(new object[] { false }, condition.Invoke(new object[] { infantry, artillery }));

        // 载荷契约（fail-fast 族——与既有判定器口径一致）：缺失/类型不符＝ArgumentException。
        Assert.Throws<ArgumentException>(() => counter.Invoke(new object[] { artillery })); // 缺 target
        Assert.Throws<ArgumentException>(() => counter.Invoke(new object[] { "非法载荷", ambusher }));
        Assert.Throws<ArgumentException>(() => condition.Invoke(null)); // 空载荷
        Assert.Throws<ArgumentException>(() => condition.Invoke(new object[] { "非法载荷", artillery }));

        // null 单位（引用类型载荷合法值）→ 原静态入口语义保持：ArgumentNullException。
        Assert.Throws<ArgumentNullException>(() => counter.Invoke(new object[] { null!, ambusher }));
        Assert.Throws<ArgumentNullException>(() => counter.Invoke(new object[] { artillery, null! }));
    }

    // ---------- ⑥ 独立构造路径（无注册表）：内置默认通道构造即可用、行为与注册路径一致 ----------

    [Fact]
    public async Task Standalone_CommandManager_BuiltIn_Counter_And_Ambush_Defaults_Consistent()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        var weak = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 2); // 攻 1 / 防 2
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）
        var enemyBomber = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 1); // 攻 4 / 防 2

        // 独立构造（不经 Match 装配链/注册表）：内置默认判定器通道构造即可用（零配置）。
        var standalone = new CommandManager(
            match.Engine,
            match.Battlefield,
            match.TargeterManager,
            match.Players,
            () => match.CurrentPlayer);

        var standaloneContext = standalone.KeywordLoadContext;
        Assert.NotNull(standaloneContext.CounterEligibility); // K2：反击资格通道（构造即可用）
        Assert.NotNull(standaloneContext.AmbushCondition); // K2：伏击条件通道（构造即可用）

        // 行为与注册路径一致（同组合同结果——默认逻辑真实求值、非恒真恒假）。
        var registeredCounter = match.Judicators.Resolve(JudicatorNames.CombatCounterEligibility);
        Assert.Equal(new object[] { false }, registeredCounter.Invoke(new object[] { artillery, ambusher }));
        Assert.False(standaloneContext.CounterEligibility!(artillery, ambusher)); // ② 炮兵不受反击
        Assert.Equal(new object[] { false }, registeredCounter.Invoke(new object[] { weak, enemyBomber }));
        Assert.False(standaloneContext.CounterEligibility!(weak, enemyBomber)); // ① 目标轰炸机永不反击

        var registeredCondition = match.Judicators.Resolve(JudicatorNames.CombatAmbushCondition);
        Assert.Equal(new object[] { true }, registeredCondition.Invoke(new object[] { ambusher, artillery }));
        Assert.True(standaloneContext.AmbushCondition!(ambusher, artillery)); // 5 ＞ 2 命中
        Assert.Equal(new object[] { false }, registeredCondition.Invoke(new object[] { weak, ambusher }));
        Assert.False(standaloneContext.AmbushCondition!(weak, ambusher)); // 1 ＞ 6 不命中
    }
}
