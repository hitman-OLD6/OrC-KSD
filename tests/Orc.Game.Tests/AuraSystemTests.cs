using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W3-1 G4 持续/条件静态能力验收（四场景 + 查询面/合成补充）：
/// ①相邻光环随动（源出现/移动两态/离开/死亡 → 加成随动、失效即消失；完整装载链路径 + 装载链托管注销 + 零增删）；
/// ①b 双源叠加（多声明独立参与合成——推荐增强）；
/// ②自条件（在前线时 +1 攻；离开前线 → 条件失效产出原值）；
/// ③源条件全体（本单位在前线时 → 友方支援线全体 +1 攻；≥2 受益单位；离开前线/死亡 → 全体消失）；
/// ④计数型（每有 1 个相邻单位 +2；HQ 不计数；递进两级 + 死亡/离开回落）；
/// 环境现算查询面（相邻/前线/战线定位——纯只读、未在场安全；关系与计数分离〔HQ〕；隔槽不相邻）。
/// 机制链路真实（装载/收集/重跑/发射不经桩）；驱动动作按授权采用机制级构造（槽位变更 + 对应更新发射）。
/// </summary>
public class AuraSystemTests
{
    // ---------- ① 相邻光环随动（完整装载链路径） ----------

    [Fact]
    public async Task Scenario1_AdjacentAura_Follows_Source_Presence_Movement_And_Death()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        // 受益单位（步兵 攻 2）于前线槽 2；记录条目集合基线（零增删判据：全程不变）
        var beneficiary = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        Assert.Equal(2, AttackOf(beneficiary));
        var memberBaseline = beneficiary.Modifiers.All.Count;

        // 光环源：对局装配 → 卡加载装载 → 声明注册（完整装载链路径）
        var source = (UnitCard)match.CardLibrary.Instantiate(AuraTestKit.AdjacentAuraUnitId);
        var registeredAtLoad = false;
        using (var loadProbe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardLoad && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadCard], source))
            {
                // 时序证据：装载时注册（注册先于 card.load 广播——消费者收到广播时声明已可被收集可见）
                registeredAtLoad = match.Environment.Auras.All.Any(d => ReferenceEquals(d.Host, source));
            }

            return Task.CompletedTask;
        }))
        {
            await source.LoadAsync(playerA);
        }

        Assert.True(registeredAtLoad);
        Assert.Contains(match.Environment.Auras.All, d => ReferenceEquals(d.Host, source)); // 卡组期：声明已注册可见

        // 卡组期注册不生效：显式驱动一轮全卡重跑——受益卡不受影响（通用门禁：源未在场）
        await match.Environment.RerunAllCardsAsync();
        Assert.Equal(2, AttackOf(beneficiary));

        // （a）源出现于相邻位（槽 1；加入路径）→ 加成出现（unit.joined → 全域重跑自动随动）
        var joinResult = await match.PlayManager.JoinUnitAsync(source, frontLine.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, joinResult.Status);
        Assert.Equal(4, AttackOf(beneficiary)); // 2 + 2

        // （b1）移动：相邻位 → 另一相邻位（槽 1 → 槽 3）→ 加成持续（防「移动即误撤销」）
        using var recorder = new UpdateRecorder(match.Engine);
        await MoveUnitAsync(match, source, frontLine.GetInsertionGaps().At(3));
        Assert.Equal(4, AttackOf(beneficiary));
        Assert.DoesNotContain( // 无变化不通知：无新发射（缓存比较既有保证）
            recorder.Updates,
            u => u.Type == GameUpdates.CardStatChanged
                && u.Payload is not null
                && ReferenceEquals(u.Payload[GameUpdates.PayloadCard], beneficiary));

        // （b2）移动：离开相邻（槽 3 → 槽 0；间隔槽 1、2）→ 加成消失（防漏撤）
        await MoveUnitAsync(match, source, frontLine.GetInsertionGaps().At(0));
        Assert.Equal(2, AttackOf(beneficiary));

        // 移回相邻位（槽 0 → 槽 1）→ 加成再现（随动闭环）
        await MoveUnitAsync(match, source, frontLine.GetInsertionGaps().At(1));
        Assert.Equal(4, AttackOf(beneficiary));

        // （d）源死亡 → 加成消失（失效即消失）＋ 声明注销（效果卸载链托管收口——按来源整组撤销）
        await KillAsync(source);
        Assert.True(source.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, AttackOf(beneficiary));
        Assert.Empty(match.Environment.Auras.All); // 托管注销：装载链收口面（RemoveBySource）

        // 零增删：全过程中受益卡链上条目集合不变（仅有效值随重跑变化）
        Assert.Equal(memberBaseline, beneficiary.Modifiers.All.Count);
        Assert.Empty(beneficiary.Modifiers.All);
    }

    // ---------- ①b 双源叠加（多声明独立参与合成——推荐增强断言） ----------

    [Fact]
    public async Task Scenario1b_AdjacentAura_Stacks_From_Multiple_Sources()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        var beneficiary = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        Assert.Equal(2, AttackOf(beneficiary));

        var sourceLeft = (UnitCard)match.CardLibrary.Instantiate(AuraTestKit.AdjacentAuraUnitId);
        await sourceLeft.LoadAsync(playerA);
        var sourceRight = (UnitCard)match.CardLibrary.Instantiate(AuraTestKit.AdjacentAuraUnitId);
        await sourceRight.LoadAsync(playerA);

        // 左源出现 → +2；右源出现 → 再 +2（同字段多命中：各声明独立参与合成、按登记序依次施加）
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(sourceLeft, frontLine.GetInsertionGaps().At(1))).Status);
        Assert.Equal(4, AttackOf(beneficiary));
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(sourceRight, frontLine.GetInsertionGaps().At(3))).Status);
        Assert.Equal(6, AttackOf(beneficiary)); // 2 + 2 + 2

        // 单源注销（死亡）→ 回落一档；双源全注销 → 回落原值
        await KillAsync(sourceLeft);
        Assert.Equal(4, AttackOf(beneficiary));
        await KillAsync(sourceRight);
        Assert.Equal(2, AttackOf(beneficiary));
        Assert.Empty(match.Environment.Auras.All);
    }

    // ---------- ② 自条件（条件评估节） ----------

    [Fact]
    public async Task Scenario2_SelfCondition_FrontLine_Attack_Bonus_Follows_Position()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var frontLine = match.Battlefield.FrontLine;

        // 条件评估节挂载（机制公共面）：条件＝本卡在前线（读本卡＋经查询面的环境）；成立施加 +1、不成立产出原值
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var source = new object();
        await unit.Modifiers.AddModifierAsync(new ConditionalModifier(
            CardStatFields.Attack,
            card => match.Environment.IsOnFrontLine(card),
            (card, current) => current + 1,
            source));
        Assert.Equal(2, AttackOf(unit)); // 支援线：条件不成立——产出原值

        // 移动至前线 → 条件成立（+1）
        await MoveUnitAsync(match, unit, frontLine.GetInsertionGaps().At(2));
        Assert.Equal(3, AttackOf(unit));

        // 离开前线（机制级构造——本批无后撤真实路径）→ 条件失效产出原值（加成消失）
        await MoveUnitAsync(match, unit, supportLine.GetInsertionGaps().At(1));
        Assert.Equal(2, AttackOf(unit));

        // 撤销修饰器（按来源）→ 行为不变、无残留
        await unit.Modifiers.RemoveBySourceAsync(source);
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(2, AttackOf(unit));
    }

    // ---------- ③ 源条件全体（光环收集——支援线全体） ----------

    [Fact]
    public async Task Scenario3_SourceCondition_Grants_Whole_Support_Line()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var frontLine = match.Battlefield.FrontLine;

        // 受益：源方支援线 ≥2 个单位（防「单受益者巧合」——「全体」语义证据）
        var first = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var second = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        Assert.Equal(2, AttackOf(first));
        Assert.Equal(2, AttackOf(second));

        // 源：完整装载链路径（声明注册）→ 加入支援线槽 3（尚未在前线——条件不成立）
        var source = (UnitCard)match.CardLibrary.Instantiate(AuraTestKit.FrontLineAuraUnitId);
        await source.LoadAsync(playerA);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(source, supportLine.GetInsertionGaps().At(3))).Status);
        Assert.Equal(2, AttackOf(first)); // 源不在前线：不生效
        Assert.Equal(2, AttackOf(second));

        // 源推进至前线 → 全体 +1
        await MoveUnitAsync(match, source, frontLine.GetInsertionGaps().At(1));
        Assert.Equal(3, AttackOf(first));
        Assert.Equal(3, AttackOf(second));

        // 源仍在前线（换槽）→ 持续
        await MoveUnitAsync(match, source, frontLine.GetInsertionGaps().At(4));
        Assert.Equal(3, AttackOf(first));
        Assert.Equal(3, AttackOf(second));

        // 源离开前线 → 全体加成消失
        await MoveUnitAsync(match, source, supportLine.GetInsertionGaps().At(3));
        Assert.Equal(2, AttackOf(first));
        Assert.Equal(2, AttackOf(second));

        // 复位：源回前线 → 再生效；源死亡 → 全体消失（推荐并测）
        await MoveUnitAsync(match, source, frontLine.GetInsertionGaps().At(2));
        Assert.Equal(3, AttackOf(first));
        await KillAsync(source);
        Assert.Equal(2, AttackOf(first));
        Assert.Equal(2, AttackOf(second));
        Assert.Empty(match.Environment.Auras.All); // 托管注销
    }

    // ---------- ④ 计数型（每有 1 个相邻单位 +2） ----------

    [Fact]
    public async Task Scenario4_AdjacentCount_Scales_And_Falls_Back()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var frontLine = match.Battlefield.FrontLine;

        // 计数单位（步兵 攻 2）于支援线槽 1：邻位槽 0＝HQ（「相邻单位」计数不含 HQ——非单位）、槽 2 空 → 攻 2
        var counting = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var source = new object();
        await counting.Modifiers.AddModifierAsync(new ConditionalModifier(
            CardStatFields.Attack,
            card => match.Environment.GetAdjacentUnits(card).Count > 0,
            (card, current) => current + 2 * match.Environment.GetAdjacentUnits(card).Count,
            source));
        Assert.Equal(2, AttackOf(counting));

        // 移至前线槽 2（无相邻单位）→ 原值
        await MoveUnitAsync(match, counting, frontLine.GetInsertionGaps().At(2));
        Assert.Equal(2, AttackOf(counting));

        // 递进（至少两级）：n=1 → +2；n=2 → +4（n1 为敌方单位——「不分敌我」计数）
        var n1 = await CommandTestKit.PrepareUnitAsync(match, playerB, CommandTestKit.InfantryId, frontLine.GetInsertionGaps().At(1));
        Assert.Equal(4, AttackOf(counting));
        var n2 = await CommandTestKit.PrepareUnitAsync(match, playerA, CommandTestKit.InfantryId, frontLine.GetInsertionGaps().At(3));
        Assert.Equal(6, AttackOf(counting));

        // 回落（死亡）：相邻单位死亡 → 回落一档
        await KillAsync(n1);
        Assert.Equal(4, AttackOf(counting));

        // 回落（离开）：相邻单位移走 → 回落原值
        await MoveUnitAsync(match, n2, frontLine.GetInsertionGaps().At(4));
        Assert.Equal(2, AttackOf(counting));
    }

    // ---------- 环境现算查询面（纯只读 / 未在场安全 / 关系与计数分离） ----------

    [Fact]
    public async Task Environment_Queries_Are_ReadOnly_And_Safe()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        // 未在场实体：相关判定按「不满足」处置（不抛错、自然产出原值）
        var notOnField = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId);
        Assert.Null(match.Environment.GetPositionOf(notOnField));
        Assert.False(match.Environment.IsOnFrontLine(notOnField));
        Assert.Null(match.Environment.GetLineOf(notOnField));
        Assert.Empty(match.Environment.GetAdjacentUnits(notOnField));
        Assert.False(match.Environment.AreAdjacent(notOnField, notOnField));

        // 在场单位：查询纯只读（不触发重跑、不发任何更新、不改状态）
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        using var recorder = new UpdateRecorder(match.Engine);
        Assert.True(match.Environment.IsOnFrontLine(unit));
        Assert.Same(match.Battlefield.FrontLine, match.Environment.GetLineOf(unit));
        Assert.Empty(match.Environment.GetAdjacentUnits(unit));
        Assert.Empty(recorder.Updates); // 查询零副作用
        Assert.Equal(2, AttackOf(unit)); // 状态不变

        // 隔槽不相邻（槽 2 与槽 4 间隔空槽 3）
        var far = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 4);
        Assert.False(match.Environment.AreAdjacent(unit, far));

        // 关系 vs 计数分离：HQ 参与相邻关系（槽 1 邻槽 0），但「相邻单位」计数不含 HQ（非单位——槽 0 不计数、槽 2 空）
        var besideHq = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        Assert.True(match.Environment.AreAdjacent(besideHq, playerA.Hq));
        Assert.Empty(match.Environment.GetAdjacentUnits(besideHq));

        // 跨线不算（前线槽 4 与支援线槽 2）
        var supportUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        Assert.False(match.Environment.AreAdjacent(far, supportUnit));
    }

    // ---------- ①c 显式效果移除（非死亡路径——托管注销 + 受益侧衔接） ----------

    [Fact]
    public async Task Scenario1c_AdjacentAura_Follows_Explicit_Effect_Removal()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        var beneficiary = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var source = (UnitCard)match.CardLibrary.Instantiate(AuraTestKit.AdjacentAuraUnitId);
        await source.LoadAsync(playerA);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(source, frontLine.GetInsertionGaps().At(1))).Status);
        Assert.Equal(4, AttackOf(beneficiary));

        // 显式移除效果（非死亡路径）→ 统一卸载链 → 托管注销（按来源整组撤销）＋受益侧衔接（有命中才跑）
        var effect = Assert.Single(source.Effects);
        source.RemoveEffect(effect);

        Assert.False(effect.IsMounted);
        Assert.Empty(source.Effects);
        Assert.Empty(match.Environment.Auras.All); // 托管注销：装载链收口面
        Assert.Equal(2, AttackOf(beneficiary)); // 衔接重跑：加成随动消失（受益侧零增删）
    }

    // ---------- 组合形态（修饰器 × 光环同字段）与现算读取 ----------

    [Fact]
    public async Task Modifier_And_Aura_Compose_On_Same_Field()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        var beneficiary = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var source = (UnitCard)match.CardLibrary.Instantiate(AuraTestKit.AdjacentAuraUnitId);
        await source.LoadAsync(playerA);

        // 挂载固定值修饰器（+1）→ 生效（固定值贡献节）
        var origin = new object();
        await beneficiary.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 1, origin));
        Assert.Equal(3, AttackOf(beneficiary));

        // 光环出现 → 叠加（固定序、文档化：先挂载修饰器〔挂载序〕、后光环收集〔登记序〕→ 2 + 1 + 2）
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(source, frontLine.GetInsertionGaps().At(1))).Status);
        Assert.Equal(5, AttackOf(beneficiary));
        Assert.Equal(5, beneficiary.Modifiers.ComputeEffectiveValue(CardStatFields.Attack)); // 现算读取同构含光环

        // 撤销修饰器 → 4；注销光环（源死亡）→ 2
        await beneficiary.Modifiers.RemoveBySourceAsync(origin);
        Assert.Equal(4, AttackOf(beneficiary));
        await KillAsync(source);
        Assert.Equal(2, AttackOf(beneficiary));
    }

    // ---------- 字段无关（非攻字段同样合成）与机制级直接注册面 ----------

    [Fact]
    public async Task Aura_Collects_Across_Fields_And_Unregisters_By_Source()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        // 受益单位（步兵 行动费 1）与源（相邻）
        var beneficiary = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var source = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 1);
        Assert.Equal(1, beneficiary.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));

        // 机制级直接注册（注册面公共面）：相邻 → 行动费 +3（机制字段无关——非攻字段同样现收集合成）
        var origin = new object();
        var declaration = AuraDeclaration.Add(
            source, CardStatFields.OperateCost, 3, origin,
            (environment, card) => environment.AreAdjacent(source, card));
        Assert.True(match.Environment.Auras.Register(declaration)); // 新增
        Assert.False(match.Environment.Auras.Register(declaration)); // 幂等：同一实例重复注册＝无操作
        await match.Environment.RerunAllCardsAsync();

        Assert.Equal(4, beneficiary.Modifiers.GetEffectiveValue(CardStatFields.OperateCost)); // 1 + 3（行动费域）
        Assert.Equal(1, source.Modifiers.GetEffectiveValue(CardStatFields.OperateCost)); // 源自身不满足相邻（自己）→ 原值

        // 按来源注销 → 回落
        Assert.Equal(1, match.Environment.Auras.RemoveBySource(origin));
        Assert.Equal(0, match.Environment.Auras.RemoveBySource(origin)); // 幂等：无命中＝0
        await match.Environment.RerunAllCardsAsync();
        Assert.Equal(1, beneficiary.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));
    }

    // ---------- card.stat.changed 联动（条件依赖他卡数值的随动通路） ----------

    [Fact]
    public async Task StatChanged_Drives_Full_Rerun_For_Cross_Card_Conditions()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        // 观察者：条件依赖「相邻单位攻击有效值」（他卡数值）
        var watcher = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var neighbor = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await watcher.Modifiers.AddModifierAsync(new ConditionalModifier(
            CardStatFields.Attack,
            card => match.Environment.GetAdjacentUnits(card).Any(
                unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack) >= 10),
            (card, current) => current + 5,
            new object()));
        Assert.Equal(2, AttackOf(watcher)); // 相邻单位攻 2 < 10：条件不成立

        // 相邻单位经修饰达到 10 → 其 card.stat.changed（数值联动）→ 全域重跑 → 观察者条件成立（跨卡随动）
        await neighbor.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 8, new object()));
        Assert.Equal(10, AttackOf(neighbor));
        Assert.Equal(7, AttackOf(watcher)); // 2 + 5（他卡数值条件的随动——stat.changed 纳入重跑触发的行为证据）
    }

    // ---------- 测试辅助 ----------

    /// <summary>读取攻击有效值（链输出——含修饰与光环收集合成）。</summary>
    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    /// <summary>
    /// 机制级移动（授权口径：直接操作槽位占用并发射对应更新——重跑链真实、驱动动作不作硬性要求；
    /// 步骤与移动执行段一致：清旧位 → 入新位 → 改状态 → 发 unit.position.changed）。
    /// </summary>
    private static async Task MoveUnitAsync(Match match, UnitCard unit, FormationGap target)
    {
        var state = unit.GetData<UnitStateData>();
        var old = state.Line ?? throw new InvalidOperationException("移动源未在场（测试装配错误）。");
        var oldIndex = old.IndexOf(unit);
        old.TryRemove(unit);
        target.Line.TryInsertAt(target.Ordinal, unit);
        state.Line = target.Line;
        await GameUpdates.EmitUnitPositionChanged(
            match.Engine, unit, new BoardPosition(old, oldIndex), new BoardPosition(target.Line, target.Ordinal));
    }

    /// <summary>击杀（门户伤害——防御归零 → 统一死亡流程：清位/置毁/清理/效果卸载托管/发 card.died）。</summary>
    private static Task KillAsync(UnitCard unit) => unit.ApplyDefenseDamageAsync(999);
}
