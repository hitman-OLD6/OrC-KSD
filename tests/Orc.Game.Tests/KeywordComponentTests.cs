using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2C-A1（词条组件化体系）验收②③＋体系能力测试：
/// ② 带参值词条（示例「重甲2」）声明 → 装配 → 读取（HasKeyword＋GetKeywordValue）＋参值改写口最小验证（改写→读取新值）；
/// ③ 授予/移除闭环（动态授予含无词条卡「无→有」挂载；回调验证；内嵌效果装载/卸载随动〔挂载→行为生效→卸载→失效→再授予〕）；
/// 移除 vs 死亡注销对照（信息保留差异）；装载链内失败＝整体回滚（内嵌装载失败 / OnGrant 失败——无半态）；
/// 死亡后操作面拒绝（终态；查询面保持可用）；注册面（重复注册拒绝／注销再注册）；旧 KeywordData 用例的等价迁移覆盖。
/// </summary>
public class KeywordComponentTests
{
    // ---------- 注册面（测试唯一命名隔离；类内一次性注册——静态注册面进程级、跨测试共存） ----------

    private const string HeavyArmorKeyword = "重甲";
    private const string DemoKeyword = "演示词条";
    private const string ExplodingLoadKeyword = "测试·装载爆炸";
    private const string ExplodingGrantKeyword = "测试·回调爆炸";
    private const string RevokeExplodingKeyword = "测试·回调撤销爆炸";

    private static readonly object RegistrationGate = new();

    private static void EnsureRegistered(string keyword, Func<Card, int?, KeywordComponent> factory)
    {
        lock (RegistrationGate)
        {
            if (!KeywordRegistry.IsDefined(keyword))
            {
                KeywordRegistry.Register(keyword, factory);
            }
        }
    }

    // ---------- 回调与效果随动记录（测试类内单测串行——静态收集器安全） ----------

    private static readonly List<string> GrantRevokeLog = new();
    private static readonly List<string> EffectLifecycleLog = new();

    /// <summary>演示行为效果（示例词条的主要行为载体）：装载＝行为生效（指挥状态置位）、卸载＝行为失效（恢复）＋生命周期留痕。</summary>
    private sealed class DemoBehaviorEffect : PassiveEffect
    {
        public DemoBehaviorEffect()
            : base("演示行为效果")
        {
        }

        protected override void OnMount()
        {
            EffectLifecycleLog.Add("mount");
            if (Host is CardBase card && card.TryGetData<CommandData>(out var command))
            {
                command.CanMove = true; // 行为生效（可观测载体：指挥状态置位）
            }
        }

        protected override void OnUnmount()
        {
            EffectLifecycleLog.Add("unmount");
            if (Host is CardBase card && card.TryGetData<CommandData>(out var command))
            {
                command.CanMove = false; // 行为失效（恢复）
            }
        }
    }

    /// <summary>演示词条组件（内嵌效果＋回调留痕）：OnGrant/OnRevoke 记录回调触发证据。</summary>
    private sealed class DemoKeywordComponent : KeywordComponent
    {
        public DemoKeywordComponent(int? value = null)
            : base(DemoKeyword, value)
        {
            EmbedEffect(new DemoBehaviorEffect());
        }

        protected internal override void OnGrant() => GrantRevokeLog.Add("grant");

        protected internal override void OnRevoke() => GrantRevokeLog.Add("revoke");
    }

    /// <summary>装载爆炸词条组件：内嵌效果在装载链内（OnMount）抛异常——验证授予整体回滚。</summary>
    private sealed class ExplodingLoadKeywordComponent : KeywordComponent
    {
        public ExplodingLoadKeywordComponent(int? value = null)
            : base(ExplodingLoadKeyword, value)
        {
            EmbedEffect(new ExplodingLoadEffect());
        }
    }

    private sealed class ExplodingLoadEffect : PassiveEffect
    {
        public ExplodingLoadEffect()
            : base("装载爆炸效果")
        {
        }

        protected override void OnMount() => throw new InvalidOperationException("装载链内爆炸（测试）");
    }

    /// <summary>回调爆炸词条组件：OnGrant（挂载链最后一步）抛异常——验证逆序整体回滚（内嵌效果装载后被撤销）。</summary>
    private sealed class ExplodingGrantKeywordComponent : KeywordComponent
    {
        public ExplodingGrantKeywordComponent(int? value = null)
            : base(ExplodingGrantKeyword, value)
        {
            EmbedEffect(new DemoBehaviorEffect());
        }

        protected internal override void OnGrant() => throw new InvalidOperationException("OnGrant 爆炸（测试）");
    }

    /// <summary>撤销爆炸词条组件：OnRevoke（移除链第一步）抛异常——验证操作失败＝上抛、机制面未变更（无半态）。</summary>
    private sealed class RevokeExplodingKeywordComponent : KeywordComponent
    {
        public RevokeExplodingKeywordComponent(int? value = null)
            : base(RevokeExplodingKeyword, value)
        {
            EmbedEffect(new DemoBehaviorEffect());
        }

        protected internal override void OnRevoke() => throw new InvalidOperationException("OnRevoke 爆炸（测试）");
    }

    // ---------- 注册面 ----------

    [Fact]
    public void Registry_Rejects_Duplicate_Registration_And_Supports_Unregister()
    {
        // 四枚生产内建经注册面装配（合法标识集来源＝注册面内容）。
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Blitz));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Fury));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.SmokeScreen));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Ambush));
        Assert.Contains(KeywordIds.Blitz, KeywordRegistry.Registered);

        // 重复注册＝拒绝（注册即配置）。
        Assert.Throws<InvalidOperationException>(
            () => KeywordRegistry.Register(KeywordIds.Blitz, (_, _) => new PlainKeywordComponent(KeywordIds.Blitz)));

        // 注销/再注册面（测试隔离通道；唯一命名——先清理可能的残留）。
        const string tempKeyword = "测试·临时词条";
        KeywordRegistry.Unregister(tempKeyword);
        KeywordRegistry.Register(tempKeyword, (_, _) => new PlainKeywordComponent(tempKeyword));
        Assert.True(KeywordRegistry.IsDefined(tempKeyword));
        Assert.True(KeywordRegistry.Unregister(tempKeyword));
        Assert.False(KeywordRegistry.IsDefined(tempKeyword));
        Assert.False(KeywordRegistry.Unregister(tempKeyword)); // 幂等
    }

    // ---------- 验收②：带参值词条（「重甲2」）声明 → 装配 → 读取 → 改写 ----------

    [Fact]
    public async Task HeavyArmor2_Declared_Attached_Read_And_Rewritten_Via_Unified_Port()
    {
        EnsureRegistered(HeavyArmorKeyword, (_, value) => new PlainKeywordComponent(HeavyArmorKeyword, value));

        // 公开声明 API 全链：声明（标识＋参值）→ 装配（经注册面构造组件、授予链挂载）→ 读取。
        var definition = new CardDefinition(
            "重甲兵", 1, 1, 2, 2,
            keywords: new[] { new KeywordDeclaration(HeavyArmorKeyword, 2) },
            faction: Faction.Germany, rarity: Rarity.Standard);
        var match = CommandTestKit.CreateCommandMatch(
            extraDefinitions: new[] { new CardDefinitionEntry("u_a1_heavy", definition) });
        await match.Initialize();
        var player = match.Players[0];

        var card = await CommandTestKit.InstantiateLoadedAsync(match, player, "u_a1_heavy");

        // 读取（统一读口：卡上实例面与静态便利口同源）：HasKeyword＋GetKeywordValue。
        Assert.True(card.Keywords.Has(HeavyArmorKeyword));
        Assert.Equal(2, card.Keywords.GetValue(HeavyArmorKeyword));
        Assert.True(KeywordRules.HasKeyword(card, HeavyArmorKeyword));
        Assert.Equal(2, KeywordRules.GetKeywordValue(card, HeavyArmorKeyword));

        // 参值改写口（最小验证：改写 → 读取新值；模式 ×2；允许置空参值）。
        card.Keywords.SetValue(HeavyArmorKeyword, 3);
        Assert.Equal(3, KeywordRules.GetKeywordValue(card, HeavyArmorKeyword));
        card.Keywords.SetValue(HeavyArmorKeyword, null);
        Assert.Null(KeywordRules.GetKeywordValue(card, HeavyArmorKeyword));
        Assert.True(card.Keywords.Has(HeavyArmorKeyword)); // 有词条无参值：Has=true 且 Get=null

        // 改写前提＝词条存在：不存在＝明确异常（更新型操作的目标缺失）。
        Assert.Throws<InvalidOperationException>(() => card.Keywords.SetValue("未授予词条", 1));

        // 无词条卡：与「无该词条」语义等价（false/null、不抛错）。
        var plain = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);
        Assert.False(plain.Keywords.Has(HeavyArmorKeyword));
        Assert.Null(plain.Keywords.GetValue(HeavyArmorKeyword));
        Assert.False(KeywordRules.HasKeyword(plain, HeavyArmorKeyword));
        Assert.Null(KeywordRules.GetKeywordValue(plain, HeavyArmorKeyword));
    }

    // ---------- 验收③：授予/移除闭环（回调＋内嵌效果随动＋幂等＋再授予） ----------

    [Fact]
    public async Task Grant_Revoke_Closure_With_EmbeddedEffect_And_Callbacks()
    {
        EnsureRegistered(DemoKeyword, (_, value) => new DemoKeywordComponent(value));
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 无词条卡（步兵；加入路径在场——单位化后含指挥组件）：动态授予＝「无→有」挂载。
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        GrantRevokeLog.Clear();
        EffectLifecycleLog.Clear();

        Assert.False(unit.Keywords.Has(DemoKeyword)); // 无→有前
        Assert.Empty(unit.Effects);

        // ① 授予（挂载）：存在性置位 → 内嵌效果装载（自动）→ OnGrant（最后）。
        Assert.True(await unit.Keywords.GrantAsync(DemoKeyword, 5));

        Assert.True(unit.Keywords.Has(DemoKeyword));
        Assert.Equal(5, unit.Keywords.GetValue(DemoKeyword));
        Assert.Equal(new[] { "grant" }, GrantRevokeLog);      // 回调验证（OnGrant）
        Assert.Equal(new[] { "mount" }, EffectLifecycleLog);  // 内嵌效果装载随动
        var embedded = Assert.Single(unit.Effects);            // 内嵌效果在卡效果面板（与外部效果同待遇）
        Assert.IsType<DemoBehaviorEffect>(embedded);
        Assert.True(unit.GetData<CommandData>().CanMove);      // 行为生效

        // 幂等：重复授予（含不同参值）＝无操作、参值保持（参值变更一律走改写口）。
        Assert.False(await unit.Keywords.GrantAsync(DemoKeyword, 9));
        Assert.Equal(5, unit.Keywords.GetValue(DemoKeyword));
        Assert.Equal(new[] { "grant" }, GrantRevokeLog);

        // ② 移除（完整卸载）：OnRevoke → 内嵌效果卸载 → 存在性清除（参值不可读）。
        Assert.True(await unit.Keywords.RevokeAsync(DemoKeyword));

        Assert.False(unit.Keywords.Has(DemoKeyword));
        Assert.Null(unit.Keywords.GetValue(DemoKeyword));
        Assert.Equal(new[] { "grant", "revoke" }, GrantRevokeLog);       // 回调验证（OnRevoke）
        Assert.Equal(new[] { "mount", "unmount" }, EffectLifecycleLog);  // 内嵌效果卸载随动
        Assert.Empty(unit.Effects);
        Assert.False(unit.GetData<CommandData>().CanMove);               // 行为失效

        // 幂等：卸载不存在词条＝无操作、不报错（false）。
        Assert.False(await unit.Keywords.RevokeAsync(DemoKeyword));

        // ③ 再授予（复装）：新组件实例、行为恢复。
        Assert.True(await unit.Keywords.GrantAsync(DemoKeyword));
        Assert.True(unit.Keywords.Has(DemoKeyword));
        Assert.True(unit.GetData<CommandData>().CanMove);
        Assert.Equal(new[] { "grant", "revoke", "grant" }, GrantRevokeLog);
        Assert.Equal(new[] { "mount", "unmount", "mount" }, EffectLifecycleLog);
    }

    // ---------- 移除 vs 死亡注销对照（信息保留差异）＋死亡后操作面拒绝 ----------

    [Fact]
    public async Task Death_Revokes_Behavior_But_Keeps_Registration_And_Value()
    {
        EnsureRegistered(DemoKeyword, (_, value) => new DemoKeywordComponent(value));
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 脆皮单位（攻 1 / 防 2）在敌方支援线——授予演示词条（行为生效），随后被击杀（跨线紧邻：我方前线 → 敌支援线）。
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);
        GrantRevokeLog.Clear();
        EffectLifecycleLog.Clear();
        Assert.True(await victim.Keywords.GrantAsync(DemoKeyword, 7));
        Assert.True(victim.GetData<CommandData>().CanMove); // 行为生效

        var killer = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BeastId, 1); // 攻 6 / 防 7
        CommandTestKit.Activate(killer);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var kill = await CommandTestKit.RunCommandAsync(match, bridge, killer, victim.Ref);

        Assert.Equal(CommandResultStatus.Success, kill.Status);
        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);

        // 对照：死亡注销＝仅行为撤销——存在保留、参值照常可读、行为注销（OnRevoke 序列触发、内嵌效果卸载）；
        // 对照「移除＝信息清除（Has=false、参值 null）」——信息保留差异即两档操作的分界。
        Assert.True(victim.Keywords.Has(DemoKeyword));           // 存在性保留（vs 移除＝false）
        Assert.Equal(7, victim.Keywords.GetValue(DemoKeyword));  // 参值照常可读（vs 移除＝null）
        Assert.Contains("revoke", GrantRevokeLog);               // OnRevoke 序列触发
        Assert.Contains("unmount", EffectLifecycleLog);          // 内嵌效果卸载随动
        Assert.Empty(victim.Effects);
        Assert.Empty(victim.Keywords.Components);                // 行为态清空（行为注销完成）

        // 查询面保持可用（不抛）；操作面（授予/移除/改写）对已死亡卡＝拒绝（终态）。
        Assert.False(KeywordRules.HasKeyword(victim, "不存在的词条"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => victim.Keywords.GrantAsync(DemoKeyword));
        await Assert.ThrowsAsync<InvalidOperationException>(() => victim.Keywords.RevokeAsync(DemoKeyword));
        Assert.Throws<InvalidOperationException>(() => victim.Keywords.SetValue(DemoKeyword, 8));
    }

    // ---------- 装载链内失败＝整体回滚（无半态） ----------

    [Fact]
    public async Task Grant_Rollback_When_EmbeddedEffect_Fails_To_Mount()
    {
        EnsureRegistered(ExplodingLoadKeyword, (_, value) => new ExplodingLoadKeywordComponent(value));
        EnsureRegistered(DemoKeyword, (_, value) => new DemoKeywordComponent(value));
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);

        // 装载链内失败＝fail-fast：授予整体回滚（存在性回 false、零残留、异常上抛）。
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Keywords.GrantAsync(ExplodingLoadKeyword));

        Assert.False(unit.Keywords.Has(ExplodingLoadKeyword));
        Assert.Empty(unit.Keywords.Components);
        Assert.Empty(unit.Effects); // 零残留：内嵌效果已撤离容器

        // 回滚后状态一致：正常词条授予不受影响。
        Assert.True(await unit.Keywords.GrantAsync(DemoKeyword));
        Assert.True(unit.Keywords.Has(DemoKeyword));
    }

    [Fact]
    public async Task Grant_Rollback_When_Grant_Callback_Throws()
    {
        EnsureRegistered(ExplodingGrantKeyword, (_, value) => new ExplodingGrantKeywordComponent(value));
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        EffectLifecycleLog.Clear();

        // OnGrant（挂载链最后一步）抛异常＝装载链内失败：逆序整体回滚（内嵌效果装载→卸载、存在性回 false）。
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Keywords.GrantAsync(ExplodingGrantKeyword));

        Assert.Equal(new[] { "mount", "unmount" }, EffectLifecycleLog); // 已装载的内嵌效果被逆序撤销
        Assert.False(unit.Keywords.Has(ExplodingGrantKeyword));
        Assert.Empty(unit.Keywords.Components);
        Assert.Empty(unit.Effects);
        Assert.False(unit.GetData<CommandData>().CanMove); // 行为不生效（卸载恢复）
    }

    [Fact]
    public async Task Revoke_Failure_Reports_And_Keeps_Port_Unchanged_No_Half_State()
    {
        EnsureRegistered(RevokeExplodingKeyword, (_, value) => new RevokeExplodingKeywordComponent(value));
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        EffectLifecycleLog.Clear();
        Assert.True(await unit.Keywords.GrantAsync(RevokeExplodingKeyword));
        Assert.Equal(new[] { "mount" }, EffectLifecycleLog);

        // OnRevoke（移除链第一步）抛＝操作失败：上抛、机制面未变更（词条完整在位、内嵌效果未卸）——无半态。
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Keywords.RevokeAsync(RevokeExplodingKeyword));

        Assert.True(unit.Keywords.Has(RevokeExplodingKeyword));  // 存在性未清除
        Assert.Single(unit.Effects);                             // 内嵌效果未卸载（OnRevoke 之外的步骤未执行）
        Assert.IsType<DemoBehaviorEffect>(unit.Effects[0]);
        Assert.Equal(new[] { "mount" }, EffectLifecycleLog);      // 无新卸载事件（链未推进）
        Assert.True(unit.GetData<CommandData>().CanMove);         // 行为仍在位
    }

    // ---------- 迁移覆盖（原 KeywordData 用例的等价面：登记序/宽容查询/空白拒绝/幂等） ----------

    [Fact]
    public async Task Manager_Query_Semantics_Mirrors_Legacy_Registration_Coverage()
    {
        // 旧→新：原 KeywordData_Add_Remove_Contains_Are_Idempotent_And_Ordered / KeywordData_Rejects_Blank_Identifiers
        var definition = new CardDefinition(
            "双词条兵", 1, 1, 2, 4,
            keywords: new[] { new KeywordDeclaration(KeywordIds.Fury), new KeywordDeclaration(KeywordIds.Ambush) },
            faction: Faction.Germany, rarity: Rarity.Standard);
        var match = CommandTestKit.CreateCommandMatch(
            extraDefinitions: new[] { new CardDefinitionEntry("u_a1_multi", definition) });
        await match.Initialize();
        var player = match.Players[0];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, player, "u_a1_multi");

        // 登记序＝声明序（旧：登记序保留）。
        Assert.Equal(
            new[] { KeywordIds.Fury, KeywordIds.Ambush },
            card.Keywords.Components.Select(component => component.Keyword));

        // 存在性查询不抛错（null/空白宽容——旧 Contains(null!) 语义）。
        Assert.False(card.Keywords.Has(null!));
        Assert.False(card.Keywords.Has("   "));
        Assert.Null(card.Keywords.GetValue(null!));

        // 空白标识拒绝（旧 Add("")/Add("  ")/Remove(null!) 拒绝语义——写入面校验）。
        await Assert.ThrowsAsync<ArgumentException>(() => card.Keywords.GrantAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => card.Keywords.GrantAsync("   "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => card.Keywords.GrantAsync(null!));
        Assert.Throws<ArgumentNullException>(() => card.Keywords.SetValue(null!, 1));

        // 幂等（旧 Add/Remove 幂等语义——授予/移除面）：重复授予 false、卸载不存在 false、状态保持。
        Assert.False(await card.Keywords.GrantAsync(KeywordIds.Fury));
        Assert.False(await card.Keywords.RevokeAsync("未授予词条"));
        Assert.True(card.Keywords.Has(KeywordIds.Fury));
        Assert.True(card.Keywords.Has(KeywordIds.Ambush));
    }
}
