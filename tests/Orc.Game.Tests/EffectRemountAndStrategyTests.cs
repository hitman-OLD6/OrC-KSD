using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W3-A3 运行时装载修复——游戏层验收：
/// ①复装闭环（托管重建）：授予→装载→移除→复装→二次卸载仍自动撤销该效果施加的修饰器/光环
///   （两个入口分列：修饰器＝Add 即时装载入口；光环＝加载链装载入口）；
/// ②三策略代表场景（延迟自挂载／内部状态门控／直挂靠触发时机——测试内示例效果验证）；
/// ③card.placed 发射契约（单位化成功后、完成信号前恰一次；加载时点不发射）。
/// </summary>
public class EffectRemountAndStrategyTests
{
    // ---------- ① 复装闭环（托管重建） ----------

    [Fact]
    public async Task Modifier_Effect_Remount_Rebuilds_Managed_Cleanup_For_Second_Unmount()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        Assert.Equal(2, AttackOf(unit)); // 基准（步兵 攻 2）

        var effect = new EmpowerOnMountEffect("驻场强化", CardStatFields.Attack, 2);
        unit.AddEffect(effect); // 授予（Add 即时装载入口）→ OnMount 施加修饰器（来源＝效果）
        Assert.True(effect.IsMounted);
        Assert.Equal(4, AttackOf(unit)); // 2 + 2
        Assert.Single(unit.Modifiers.All);

        unit.RemoveEffect(effect); // 移除→托管撤销（按来源）
        Assert.Equal(2, AttackOf(unit));
        Assert.Empty(unit.Modifiers.All);

        unit.AddEffect(effect); // 复装→重新装载→再次施加＋托管登记自动重建
        Assert.True(effect.IsMounted);
        Assert.Equal(4, AttackOf(unit));

        unit.RemoveEffect(effect); // 二次卸载→托管重建生效：自动撤销（缺失重建时此处残留 +2）
        Assert.Equal(2, AttackOf(unit));
        Assert.Empty(unit.Modifiers.All);
    }

    [Fact]
    public async Task Aura_Effect_Remount_Rebuilds_Managed_Cleanup_For_Second_Unmount()
    {
        var match = AuraTestKit.CreateAuraMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontLine = match.Battlefield.FrontLine;

        var beneficiary = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var source = (UnitCard)match.CardLibrary.Instantiate(AuraTestKit.AdjacentAuraUnitId);
        await source.LoadAsync(playerA); // 加载链装载入口：效果经声明构造并装载（托管登记自动）
        var effect = Assert.Single(source.Effects);
        Assert.True(effect.IsMounted);

        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(source, frontLine.GetInsertionGaps()[1])).Status);
        Assert.Equal(4, AttackOf(beneficiary)); // 2 + 2（相邻光环生效）

        source.RemoveEffect(effect); // 移除→托管注销（按来源；受益侧衔接）
        Assert.Empty(match.Environment.Auras.All);
        Assert.Equal(2, AttackOf(beneficiary));

        source.AddEffect(effect); // 复装→重新装载→声明重新注册（托管登记自动重建）
        Assert.True(effect.IsMounted);
        Assert.Single(match.Environment.Auras.All);
        await match.Environment.RerunAllCardsAsync(); // 注册≠生效：显式驱动一轮（复装不经环境事件）
        Assert.Equal(4, AttackOf(beneficiary)); // 加成恢复

        source.RemoveEffect(effect); // 二次卸载→托管重建生效：自动注销＋受益侧衔接（缺失重建时此处残留声明与加成）
        Assert.Empty(match.Environment.Auras.All);
        Assert.Equal(2, AttackOf(beneficiary));
    }

    // ---------- ② 三策略代表场景 ----------

    [Fact]
    public async Task Strategy1_Deferred_Mount_Mounts_Actual_Logic_On_Card_Placed()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        var effect = new DeferredMountEffect(match.Engine, "延迟挂载效果");
        unit.AddEffect(effect); // 未上场（在手/卡组语境）——添加即装载

        // 统一前提：效果装载已发生（恰一次）——「Add 即装载」为共同起点（效果装载层）
        Assert.True(effect.IsMounted);
        // 上场前：实际逻辑未挂载（可观察：无对应订阅——实际逻辑层）＋行为不生效
        Assert.False(effect.ActualMounted);
        Assert.Contains(effect.WaitTriggerName, match.Engine.Bus.GetSubscribers(Updates.CardPlaced)); // 「等待包装」已挂载
        Assert.DoesNotContain(effect.ActualTriggerName, match.Engine.Bus.GetSubscribers(DeferredMountEffect.ProbeEvent));
        await match.Engine.Emit(DeferredMountEffect.ProbeEvent);
        Assert.Equal(0, effect.PingCount);

        await unit.LoadAsync(playerA);
        var join = await match.PlayManager.JoinUnitAsync(unit, match.Battlefield.FrontLine.GetInsertionGaps()[0]);
        Assert.Equal(PlayResultStatus.Success, join.Status);

        // 上场（card.placed 到达）：实际逻辑挂载（订阅出现）＋行为生效
        Assert.True(effect.ActualMounted);
        Assert.Contains(effect.ActualTriggerName, match.Engine.Bus.GetSubscribers(DeferredMountEffect.ProbeEvent));
        await match.Engine.Emit(DeferredMountEffect.ProbeEvent);
        Assert.Equal(1, effect.PingCount);
    }

    [Fact]
    public async Task Strategy2_Internal_Gate_Takes_Effect_After_Card_Placed()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        var effect = new GatedOnFieldEffect(match.Engine, "内部门控效果");
        unit.AddEffect(effect);

        // 统一前提：效果装载已发生；装载即挂载（策略 2——订阅存在）
        Assert.True(effect.IsMounted);
        Assert.Contains(effect.ProbeTriggerName, match.Engine.Bus.GetSubscribers(DeferredMountEffect.ProbeEvent));
        Assert.False(unit.IsPlaced); // 门控依据（IsPlaced）可读且值正确

        // 上场前：已挂载但行为不生效（处理时依状态跳过）
        await match.Engine.Emit(DeferredMountEffect.ProbeEvent);
        Assert.Equal(0, effect.PingCount);

        await unit.LoadAsync(playerA);
        var join = await match.PlayManager.JoinUnitAsync(unit, match.Battlefield.FrontLine.GetInsertionGaps()[0]);
        Assert.Equal(PlayResultStatus.Success, join.Status);
        Assert.True(unit.IsPlaced);

        // 上场后：行为生效
        await match.Engine.Emit(DeferredMountEffect.ProbeEvent);
        Assert.Equal(1, effect.PingCount);
    }

    [Fact]
    public async Task Strategy3_Direct_Mount_Fires_On_Own_Deploy_Chain_Only()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await match.ResourceManager.AddPointsAsync(playerA, 3); // 受控加点（两次部署的实际扣费）

        var unit = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var effect = new DirectOnDeployEffect(match.Engine, "部署直挂效果");
        unit.AddEffect(effect);

        // 直挂（无门控——触发时机自然保证）：装载即挂载
        Assert.True(effect.IsMounted);
        Assert.Contains(effect.DeployTriggerName, match.Engine.Bus.GetSubscribers(GameUpdates.UnitDeployed));

        // 非该类事件不误触发：另一张卡部署（unit.deployed 被发射）→ 本卡效果过滤、不计数
        var other = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var otherDeploy = await match.PlayManager.PlayUnitAsync(other, match.Battlefield.FrontLine.GetInsertionGaps()[1]);
        Assert.Equal(PlayResultStatus.Success, otherDeploy.Status);
        Assert.Equal(0, effect.DeployCount);

        // 本卡部署：经真实部署链恰触发一次
        var deploy = await match.PlayManager.PlayUnitAsync(unit, match.Battlefield.FrontLine.GetInsertionGaps()[0]);
        Assert.Equal(PlayResultStatus.Success, deploy.Status);
        Assert.Equal(1, effect.DeployCount);
    }

    // ---------- ③ card.placed 发射契约 ----------

    [Fact]
    public async Task Card_Placed_Emitted_After_Unitize_Before_Completion_Signal()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await match.ResourceManager.AddPointsAsync(playerA, 3);

        var unit = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);

        var atPlaced = new List<(bool Unitized, bool Occupied, bool CompletionSeen)>();
        var completionSeen = false;
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (payload is null)
            {
                return Task.CompletedTask;
            }

            if (type == Updates.CardPlaced && ReferenceEquals(payload[GameUpdates.PayloadCard], unit))
            {
                var unitized = unit.TryGetData<UnitStateData>(out var state);
                var occupied = unitized && state!.Line is not null;
                atPlaced.Add((unitized, occupied, completionSeen));
            }
            else if (type == GameUpdates.UnitDeployed && ReferenceEquals(payload[GameUpdates.PayloadUnit], unit))
            {
                completionSeen = true;
            }

            return Task.CompletedTask;
        });

        var result = await match.PlayManager.PlayUnitAsync(unit, match.Battlefield.FrontLine.GetInsertionGaps()[0]);
        Assert.Equal(PlayResultStatus.Success, result.Status);

        // card.placed：单位化（组件挂载＋槽位占用）成功之后、完成信号（unit.deployed）之前——每次成功部署恰一次
        var observed = Assert.Single(atPlaced);
        Assert.True(observed.Unitized);        // 就绪数据可读（组件已挂载）
        Assert.True(observed.Occupied);        // 槽位已占用
        Assert.False(observed.CompletionSeen); // 完成信号尚未到达（先于 unit.deployed）
    }

    [Fact]
    public async Task Load_Time_Does_Not_Emit_Card_Placed()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var placedSeen = new List<object?>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == Updates.CardPlaced && payload is not null)
            {
                placedSeen.Add(payload[GameUpdates.PayloadCard]);
            }

            return Task.CompletedTask;
        });

        var unit = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        await unit.LoadAsync(playerA); // 加载时点装载照常（先于 card.load）——但不发射 card.placed（放置＝「上场」语义）

        Assert.DoesNotContain(unit, placedSeen);
    }

    // ---------- ④ 词条内嵌效果（装载收口——托管随授予/撤销自动） ----------

    [Fact]
    public async Task Embedded_Effect_Managed_Cleanup_Is_Automatic_On_Grant_And_Revoke()
    {
        EnsureKeywordRegistered(EmbeddedModifierKeyword, (_, _) => new EmbeddedModifierKeywordComponent());
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        Assert.Equal(2, AttackOf(unit));

        await unit.Keywords.GrantAsync(EmbeddedModifierKeyword); // 词条授予→内嵌效果装载（通用装载路径自动登记托管）
        Assert.Equal(4, AttackOf(unit)); // 2 + 2（内嵌效果施加）
        Assert.Single(unit.Modifiers.All);

        await unit.Keywords.RevokeAsync(EmbeddedModifierKeyword); // 词条撤销→内嵌效果卸载→托管清理自动撤销（按来源）
        Assert.Equal(2, AttackOf(unit));
        Assert.Empty(unit.Modifiers.All);
    }

    [Fact]
    public async Task Keyword_Grant_Rollback_Unloads_Embedded_Effect_Silently()
    {
        EnsureKeywordRegistered(RollbackSilentKeyword, (_, _) => new RollbackSilentKeywordComponent());
        RollbackSilentLogProbe.Entries.Clear();
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);

        var emitted = new List<object?>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == Updates.EffectRemoved && payload is not null)
            {
                emitted.Add(payload[PayloadKeys.Effect]);
            }

            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unit.Keywords.GrantAsync(RollbackSilentKeyword)); // OnGrant 爆炸→授予链整体回滚

        Assert.Equal(new[] { "mount", "unmount" }, RollbackSilentLogProbe.Entries); // 内嵌效果：装载后被撤销（回滚撤离）
        Assert.Empty(emitted); // 「无痕」回滚静默：不发 effect.removed
        Assert.False(unit.Keywords.Has(RollbackSilentKeyword)); // 存在性回 false（零残留）
    }

    // ---------- 辅助 ----------

    /// <summary>读取攻击有效值（链输出——含修饰与光环收集合成）。</summary>
    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    /// <summary>词条注册（进程级静态注册面；锁保护＋存在性检查——跨测试类共存安全）。</summary>
    private static void EnsureKeywordRegistered(string keyword, Func<Card, int?, KeywordComponent> factory)
    {
        lock (KeywordRegistrationGate)
        {
            if (!KeywordRegistry.IsDefined(keyword))
            {
                KeywordRegistry.Register(keyword, factory);
            }
        }
    }

    private const string EmbeddedModifierKeyword = "测试·内嵌强化";
    private const string RollbackSilentKeyword = "测试·回滚静默";

    private static readonly object KeywordRegistrationGate = new();
}

// ---------- 测试效果（W3-A3 验收用最小实现） ----------

/// <summary>
/// 复装闭环示例效果（修饰器侧）：装载即施加修饰器（来源＝本效果）；撤销由托管收口
/// （效果卸载 → 框架按来源撤销——复装由通用装载路径自动重建登记）。
/// 前提：宿主已单位化（修饰字段就绪）。
/// </summary>
internal sealed class EmpowerOnMountEffect : PassiveEffect
{
    private readonly string _field;
    private readonly int _delta;

    public EmpowerOnMountEffect(string name, string field, int delta)
        : base(name)
    {
        _field = field;
        _delta = delta;
    }

    protected override void OnMount()
    {
        var card = (CardBase)Host;
        card.Modifiers.AddModifierAsync(new AddModifier(_field, _delta, this)).GetAwaiter().GetResult(); // P2 同步等待（挂载自动衔接一轮）
    }
}

/// <summary>
/// 策略 1（延迟自挂载）示例效果：装载时仅注册「等待包装」（订阅本卡 card.placed）；
/// 上场（card.placed 到达本卡）时才挂载实际逻辑（订阅探测事件）；上场判定＝收到事件本身（不读 IsPlaced）。
/// </summary>
internal sealed class DeferredMountEffect : PassiveEffect
{
    /// <summary>探测事件名（测试自控驱动源——实际逻辑的「行为」触发点）。</summary>
    public const string ProbeEvent = "test.probe.ping";

    private readonly LogicEngine _engine;
    private readonly Trigger<CardEventView> _waitTrigger;
    private readonly Trigger<CardEventView> _actualTrigger;
    private int _pinged;

    public DeferredMountEffect(LogicEngine engine, string name)
        : base(name)
    {
        _engine = engine;
        WaitTriggerName = $"{name}/等待上场";
        ActualTriggerName = $"{name}/实际逻辑";
        _waitTrigger = new Trigger<CardEventView>(
            WaitTriggerName,
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<CardEventView>("等待", OnPlacedAsync) },
            hooks: new[] { Updates.CardPlaced },
            owner: this);
        _actualTrigger = new Trigger<CardEventView>(
            ActualTriggerName,
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<CardEventView>("回响", OnPingAsync) },
            hooks: new[] { ProbeEvent },
            owner: this);
    }

    public string WaitTriggerName { get; }

    public string ActualTriggerName { get; }

    public bool ActualMounted { get; private set; }

    public int PingCount => _pinged;

    protected override void OnMount() => _engine.Bus.Mount(_waitTrigger); // 「等待包装」：上场前仅此一挂；实际逻辑未挂载

    protected override void OnUnmount() => _engine.Bus.UnmountOwner(this); // 卸下全部（等待包装＋实际逻辑——幂等）

    private Task OnPlacedAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (!ReferenceEquals(view.Card, Host))
        {
            return Task.CompletedTask; // 仅本卡上场
        }

        _engine.Bus.UnmountOwner(this);    // 卸下「等待包装」（上场驱动已到达）
        _engine.Bus.Mount(_actualTrigger); // 挂载实际逻辑
        ActualMounted = true;
        return Task.CompletedTask;
    }

    private Task OnPingAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        _pinged++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 策略 2（内部门控）示例效果：装载即挂载（订阅存在）；处理时按主机状态（IsPlaced）门控——
/// 未上场不生效、上场后生效。
/// </summary>
internal sealed class GatedOnFieldEffect : PassiveEffect
{
    private readonly LogicEngine _engine;
    private readonly Trigger<CardEventView> _probeTrigger;
    private int _pinged;

    public GatedOnFieldEffect(LogicEngine engine, string name)
        : base(name)
    {
        _engine = engine;
        ProbeTriggerName = $"{name}/探测";
        _probeTrigger = new Trigger<CardEventView>(
            ProbeTriggerName,
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<CardEventView>("回响", OnPingAsync) },
            hooks: new[] { DeferredMountEffect.ProbeEvent },
            owner: this);
    }

    public string ProbeTriggerName { get; }

    public int PingCount => _pinged;

    protected override void OnMount() => _engine.Bus.Mount(_probeTrigger); // 装载即挂载（门控在效果内部）

    protected override void OnUnmount() => _engine.Bus.UnmountOwner(this);

    private Task OnPingAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (!Host.IsPlaced)
        {
            return Task.CompletedTask; // 内部状态门控：未上场不生效（IsPlaced 查询原语）
        }

        _pinged++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 策略 3（直挂）示例效果：装载即挂载、无门控——触发时机自然保证（监听 unit.deployed；本卡部署才响应）。
/// </summary>
internal sealed class DirectOnDeployEffect : PassiveEffect
{
    private readonly LogicEngine _engine;
    private readonly Trigger<UnitDeployWatchView> _deployTrigger;

    public DirectOnDeployEffect(LogicEngine engine, string name)
        : base(name)
    {
        _engine = engine;
        DeployTriggerName = $"{name}/部署监听";
        _deployTrigger = new Trigger<UnitDeployWatchView>(
            DeployTriggerName,
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<UnitDeployWatchView>("部署回响", OnDeployedAsync) },
            hooks: new[] { GameUpdates.UnitDeployed },
            owner: this);
    }

    public string DeployTriggerName { get; }

    public int DeployCount { get; private set; }

    protected override void OnMount() => _engine.Bus.Mount(_deployTrigger); // 直挂（无门控）

    protected override void OnUnmount() => _engine.Bus.UnmountOwner(this);

    private Task OnDeployedAsync(UnitDeployWatchView view, Context ctx, CancellationToken ct)
    {
        if (ReferenceEquals(view.Unit, Host))
        {
            DeployCount++; // 本卡部署才响应（过滤非本卡 unit.deployed）
        }

        return Task.CompletedTask;
    }
}

/// <summary>unit.deployed 监听视图（值＝单位卡牌实例对象引用——与 GameUpdates 载荷键同口径）。</summary>
[ContextView]
public class UnitDeployWatchView
{
    [Optional]
    [Read]
    public virtual object? Unit { get; set; }
}

// ---------- 词条内嵌效果用例（W3-A3：装载收口——托管随授予/撤销自动） ----------

/// <summary>内嵌强化词条组件：内嵌效果在装载时施加「+2 攻」修饰器（来源＝效果——托管撤销收口验证）。</summary>
internal sealed class EmbeddedModifierKeywordComponent : KeywordComponent
{
    public EmbeddedModifierKeywordComponent(int? value = null)
        : base("测试·内嵌强化", value)
    {
        EmbedEffect(new EmbeddedModifierEffect());
    }
}

internal sealed class EmbeddedModifierEffect : PassiveEffect
{
    public EmbeddedModifierEffect()
        : base("内嵌强化效果")
    {
    }

    protected override void OnMount()
    {
        var card = (CardBase)Host;
        card.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, this)).GetAwaiter().GetResult();
    }
}

/// <summary>回滚静默词条组件：OnGrant（挂载链最后一步）爆炸——内嵌效果装载后被回滚撤离；回滚静默（不发 effect.removed）。</summary>
internal sealed class RollbackSilentKeywordComponent : KeywordComponent
{
    public RollbackSilentKeywordComponent(int? value = null)
        : base("测试·回滚静默", value)
    {
        EmbedEffect(new RollbackSilentProbeEffect());
    }

    protected internal override void OnGrant() => throw new InvalidOperationException("OnGrant 爆炸（回滚静默用例）");
}

internal sealed class RollbackSilentProbeEffect : PassiveEffect
{
    public RollbackSilentProbeEffect()
        : base("回滚静默探针效果")
    {
    }

    protected override void OnMount() => RollbackSilentLogProbe.Entries.Add("mount");

    protected override void OnUnmount() => RollbackSilentLogProbe.Entries.Add("unmount");
}

/// <summary>回滚静默探针日志（测试类内单测串行——静态收集器安全）。</summary>
internal static class RollbackSilentLogProbe
{
    public static readonly List<string> Entries = new();
}
