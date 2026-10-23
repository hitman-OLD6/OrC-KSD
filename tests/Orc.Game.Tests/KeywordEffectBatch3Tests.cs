using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 词条效果化·批 3（C 档首迁）验收（「壳＋效果行为」模式——闪击／部署置位）：
/// ①随动：行为随效果独立卸载/复装随动（黑盒证据——真实部署链或部署信号分发观测）。
/// ②专项覆盖：装载（效果在列＋装载态，部署置位由既有 KeywordSystemTests 主断言与本文多单位/闭环用例覆盖）／
/// 卸载（→部署不置位——独立卸载效果与词条移除两路径）／死亡注销（注销不抛错＋登记保留＋注销后不再产生订阅）／
/// 复装（→重新生效）／独立构造不置位（功能不可用、不抛错、加载不失败）／运行期授予闭环（授予→部署生效、
/// 撤销→部署不置位）／多单位过滤（其他单位部署时本单位不被置位——Host==载荷过滤的等价性证据）。
/// 回滚路径＝通道级复用（批 0／批 1 已验——申报引用，不重做失败注入）；机制矩阵（五路径全集）复用批 1 验证。
/// 部署信号手动分发（<see cref="GameUpdates.EmitUnitDeployed"/>）＝对局内由部署链发射同一信号的黑盒等价驱动
/// （效果只经信号响应；用于已部署单位的复装/注销后订阅面观测——真实部署一次性，不可重复）。
/// 批 4（数据化）适配：效果形态由 C# 效果迁「数据壳＋行为引用」（<see cref="DynamicPassiveEffect"/>——
/// 类型断言随形态调整、与行为断言并存；4a 口径）；独立构造语义＝不实例化/跳过（原「装载完成」断言随迁移调整）；
/// 行为等价证据（置位/生灭随动/多单位过滤）保持；回滚专项见 <see cref="KeywordEffectBatch4Tests"/>。
/// </summary>
public class KeywordEffectBatch3Tests
{
    /// <summary>效果名（批 3 制品——「词条名·行为」命名惯性对齐批 1/2；批 4 数据化后仍为容器检索面契约）。</summary>
    private const string BlitzEffectName = "闪击·部署置位";

    private static Match CreateMatch() => CommandTestKit.CreateCommandMatch();

    /// <summary>效果检索（按名称——黑盒观察面：容器在列＋装载态）。</summary>
    private static Effect? FindEffect(Card card, string name)
        => card.Effects.FirstOrDefault(effect => effect.Name == name);

    // ---------- ① 装载/卸载/复装随动（独立卸载效果路径——真实部署＋部署信号分发） ----------

    [Fact]
    public async Task Blitz_Set_Follows_Effect_Detach_And_Reattach()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.BlitzId, toHand: true);
        var line = match.Battlefield.PlayerASupportLine;

        // 效果随词条装载（内嵌效果通道——批 4 数据化：数据效果在列、装载态）。
        var effect = Assert.IsType<DynamicPassiveEffect>(FindEffect(unit, BlitzEffectName));
        Assert.True(effect.IsMounted);

        // 独立卸载效果（词条仍在）：行为随效果消失——真实部署不置位（false/false）。
        unit.RemoveEffect(effect);
        Assert.False(effect.IsMounted);
        Assert.True(unit.Keywords.Has(KeywordIds.Blitz)); // 词条不动（效果独立卸载）

        var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.False(unit.GetData<CommandData>().CanMove);  // 部署已完成：未置位（效果已卸载）
        Assert.False(unit.GetData<CommandData>().CanAttack);

        // 复装（同一实例再装载）：订阅重建——部署信号（分发）再至即置位。
        unit.AddEffect(effect);
        Assert.True(effect.IsMounted);
        await GameUpdates.EmitUnitDeployed(match.Engine, unit, new BoardPosition(unit.GetData<UnitStateData>().Line!, unit.GetData<UnitStateData>().Line!.IndexOf(unit))); // 模拟部署信号分发（对局内由部署链发射）
        Assert.True(unit.GetData<CommandData>().CanMove);   // 复装恢复
        Assert.True(unit.GetData<CommandData>().CanAttack);
    }

    // ---------- ② 词条移除→部署不置位／词条复装→部署置位（词条链路径） ----------

    [Fact]
    public async Task Blitz_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];
        var line = match.Battlefield.PlayerASupportLine;
        await match.ResourceManager.AddPointsAsync(player, 10); // 双部署（各扣部署费 1——费用复验前置）

        // 词条移除（完整卸载）→ 效果卸载、行为消失——真实部署不置位（false/false）。
        var unitX = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.BlitzId, toHand: true);
        Assert.True(await unitX.Keywords.RevokeAsync(KeywordIds.Blitz));
        Assert.False(unitX.Keywords.Has(KeywordIds.Blitz)); // 存在性清除（参值不可读）
        Assert.Null(FindEffect(unitX, BlitzEffectName));

        var resultX = await match.PlayManager.PlayUnitAsync(unitX, line.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, resultX.Status);
        Assert.False(unitX.GetData<CommandData>().CanMove);
        Assert.False(unitX.GetData<CommandData>().CanAttack);

        // 词条复装（移除后再授予——新组件/新效果实例）→ 真实部署置位（恢复）。
        var unitY = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.BlitzId, toHand: true);
        Assert.True(await unitY.Keywords.RevokeAsync(KeywordIds.Blitz));
        Assert.True(await unitY.Keywords.GrantAsync(KeywordIds.Blitz));
        var effectY = Assert.IsType<DynamicPassiveEffect>(FindEffect(unitY, BlitzEffectName));
        Assert.True(effectY.IsMounted);

        var resultY = await match.PlayManager.PlayUnitAsync(unitY, line.GetInsertionGaps().At(3));
        Assert.Equal(PlayResultStatus.Success, resultY.Status);
        Assert.True(unitY.GetData<CommandData>().CanMove);
        Assert.True(unitY.GetData<CommandData>().CanAttack);
    }

    // ---------- ③ 死亡注销（黑盒等效：注销不抛错＋登记保留＋注销后不再产生订阅） ----------

    [Fact]
    public async Task Blitz_Death_Revokes_Subscription_Keeping_Registration()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];
        var line = match.Battlefield.PlayerASupportLine;
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.BlitzId, 1); // 加入路径在场
        Assert.NotNull(FindEffect(unit, BlitzEffectName));

        // 注销前：订阅活着（重置两 bool → 部署信号分发 → 置位）。
        CommandTestKit.Activate(unit, false, false);
        await GameUpdates.EmitUnitDeployed(match.Engine, unit, new BoardPosition(unit.GetData<UnitStateData>().Line!, unit.GetData<UnitStateData>().Line!.IndexOf(unit)));
        Assert.True(unit.GetData<CommandData>().CanMove);

        // 致死：死亡注销撤销行为（效果卸载随动）——不抛错；登记保留（照常可读）。
        await unit.ApplyDefenseDamageAsync(999);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.True(unit.Keywords.Has(KeywordIds.Blitz)); // 登记保留
        Assert.Empty(unit.Effects);                        // 行为撤销（效果卸载随动）

        // 注销后不再产生订阅：重置两 bool → 部署信号分发 → 不再被处理（保持 false/false）。
        CommandTestKit.Activate(unit, false, false);
        await GameUpdates.EmitUnitDeployed(match.Engine, unit, new BoardPosition(unit.GetData<UnitStateData>().Line!, unit.GetData<UnitStateData>().Line!.IndexOf(unit)));
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.False(unit.GetData<CommandData>().CanAttack);
    }

    // ---------- ④ 运行期授予闭环（授予→部署生效；撤销→部署不置位） ----------

    [Fact]
    public async Task Blitz_Runtime_Grant_Revoke_Closure_Follows_Through_Deployment()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];
        var line = match.Battlefield.PlayerASupportLine;
        await match.ResourceManager.AddPointsAsync(player, 10); // 双部署（各扣部署费 1——费用复验前置）

        // 运行期授予（无→有）→ 真实部署置位（生效）。
        var grantee = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);
        Assert.True(await grantee.Keywords.GrantAsync(KeywordIds.Blitz));
        var effect = Assert.IsType<DynamicPassiveEffect>(FindEffect(grantee, BlitzEffectName));
        Assert.True(effect.IsMounted);

        var result = await match.PlayManager.PlayUnitAsync(grantee, line.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.True(grantee.GetData<CommandData>().CanMove);
        Assert.True(grantee.GetData<CommandData>().CanAttack);

        // 运行期撤销 → 真实部署不置位（停止）。
        var revoked = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);
        Assert.True(await revoked.Keywords.GrantAsync(KeywordIds.Blitz));
        Assert.True(await revoked.Keywords.RevokeAsync(KeywordIds.Blitz));
        Assert.Null(FindEffect(revoked, BlitzEffectName));

        var result2 = await match.PlayManager.PlayUnitAsync(revoked, line.GetInsertionGaps().At(3));
        Assert.Equal(PlayResultStatus.Success, result2.Status);
        Assert.False(revoked.GetData<CommandData>().CanMove);
        Assert.False(revoked.GetData<CommandData>().CanAttack);
    }

    // ---------- ⑤ 多单位部署：其他单位部署时本单位不被置位（Host==载荷过滤——两单位交叉部署） ----------

    [Fact]
    public async Task Blitz_MultiUnit_Deployment_Does_Not_Cross_Set_Flags()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];
        var line = match.Battlefield.PlayerASupportLine;
        await match.ResourceManager.AddPointsAsync(player, 10); // 双部署（各扣部署费 1——费用复验前置）

        // X 部署（真实）→ 置位 true/true（装载→部署生效）。
        var unitX = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.BlitzId, toHand: true);
        var resultX = await match.PlayManager.PlayUnitAsync(unitX, line.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, resultX.Status);
        Assert.True(unitX.GetData<CommandData>().CanMove);
        Assert.True(unitX.GetData<CommandData>().CanAttack);

        // 重置 X 两 bool（观测设置：中性化前值，观测「其他单位部署不会误置位本单位」）。
        CommandTestKit.Activate(unitX, false, false);

        // Y 部署（真实）→ Y 自己置位；X 保持 false/false（其他单位部署时本单位不被置位——过滤成立）。
        var unitY = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.BlitzId, toHand: true);
        var resultY = await match.PlayManager.PlayUnitAsync(unitY, line.GetInsertionGaps().At(3));
        Assert.Equal(PlayResultStatus.Success, resultY.Status);
        Assert.True(unitY.GetData<CommandData>().CanMove);   // Y 置位（自己）
        Assert.True(unitY.GetData<CommandData>().CanAttack);
        Assert.False(unitX.GetData<CommandData>().CanMove);  // X 不被误置位（Host==载荷过滤）
        Assert.False(unitX.GetData<CommandData>().CanAttack);
    }

    // ---------- ⑥ 独立构造（无装载上下文）＝不订阅、不置位（功能不可用、不抛错、加载不失败） ----------

    [Fact]
    public async Task Detached_Construction_Does_Not_Set_Flags_And_Loads()
    {
        // 独立构造卡（不经对局装配链——无词条装载上下文注入）：加载不失败、词条完整；
        // 批 4 数据化：无上下文＝数据效果不实例化/跳过（功能不可用、加载不失败）——「不订阅/不置位」由结构承载。
        var engine = new LogicEngine();
        var library = new CardLibrary(engine);
        library.Register("u_fe3_offline", new CardDefinition(
            "离线闪击兵", 1, 1, 2, 3, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Blitz) },
            faction: Faction.Germany, rarity: Rarity.Standard));

        var match = CreateMatch();
        await match.Initialize();
        var offline = library.Instantiate("u_fe3_offline");

        await offline.LoadAsync(match.Players[0]); // 独立构造＝无上下文：不抛错（加载不失败）

        Assert.True(offline.Keywords.Has(KeywordIds.Blitz));
        Assert.Null(FindEffect(offline, BlitzEffectName)); // 数据效果不实例化（跳过——功能不可用）
        Assert.Empty(offline.Effects); // 零残留（无半态）
    }
}
