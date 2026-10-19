#pragma warning disable CS8618 // 视图属性值由框架在绑定时提供；声明期不做初始化（视图类不应写属性初始化器）。

using Orc.Cards;
using Orc.Core;

namespace Orc.Tests;

/// <summary>
/// 场景 2『攻击时回血』效果：被动效果——OnMount 中把「回血 handler」经 Inject 注入
/// 内核唯一流程（<see cref="OrderFlow"/>）的 Resolve band（位置语义＝结算完成时：同 band 内以 priority=100
/// 排于默认「施放结算」之后、Finalize band 之前；P5 删除 AttackFlow 后以指令流程作为内核流程驱动器）。
/// 同时记录钩子次数/次序信号/上下文可获得性等观察点（供 D 组断言）。
/// </summary>
public sealed class AttackHealEffect : PassiveEffect
{
    private readonly LogicEngine _engine;
    private readonly int _amount;

    public int MountCount;
    public int UnmountCount;
    public bool MountSawTriggerOnBus;    // OnMount 时主触发器已挂载（挂载先行）
    public bool UnmountSawTriggerOnBus;  // OnUnmount 时主触发器仍挂载（卸载后随）
    public bool SawPlacedAtMount;        // OnMount 时单位初始化已完成（顺序契约②）
    public int? MountHp;                 // OnMount 时数据组件已可用
    public bool ExclusiveCleanupRan;     // 作者专属清理（OnUnmount）执行
    public string? UnmountHostName;      // OnUnmount 时宿主可访问（上下文可获得性）
    public int? TargetHpAtHealTime;      // 回血执行时目标 HP（伤害先行的状态观测）
    public int HealCount;

    private TriggerRegistration? _lastInjection;

    public AttackHealEffect(LogicEngine engine, int amount, string name = "攻击时回血")
        : base(name)
    {
        _engine = engine;
        _amount = amount;
    }

    protected override void OnMount()
    {
        MountCount++;
        // E-D1：销毁兜底由 disposing 触发器承载（订阅 card.destroyed、名称为 "{效果名}/dispose"）。
        MountSawTriggerOnBus = _engine.Bus.GetSubscribers(Updates.CardDestroyed).Contains($"{Name}/dispose");
        SawPlacedAtMount = Host.IsPlaced;
        MountHp = Host.GetData<HealthData>().Hp;
        _lastInjection = Inject(
            _engine.OrderFlow.Trigger, "回血", OrderFlowBands.Resolve, OnFlowResolve, priority: 100);
    }

    protected override void OnUnmount()
    {
        UnmountCount++;
        ExclusiveCleanupRan = true;
        // E-D1：进入即停用——OnUnmount 执行时该效果的总线挂载已先卸载（本断言预期为 false）。
        UnmountSawTriggerOnBus = _engine.Bus.GetSubscribers(Updates.CardDestroyed).Contains($"{Name}/dispose");
        UnmountHostName = Host.Name;
        _lastInjection = null; // 专属清理：清除引用登记
    }

    public bool InjectionRefCleared => _lastInjection is null;

    private Task OnFlowResolve(OrderFlowView view, Context ctx, CancellationToken ct)
    {
        if (!ReferenceEquals(view.Source, Host))
        {
            return Task.CompletedTask; // 只响应本宿主发起的动作（多单位隔离）
        }

        HealCount++;
        TargetHpAtHealTime = view.Target?.GetData<HealthData>().Hp; // 结算已生效的状态观测
        Host.GetData<HealthData>().Hp += _amount;
        _engine.RootStream.WriteLog(
            "测试效果/攻击时回血",
            $"回血 {_amount}（回血时目标 HP＝{TargetHpAtHealTime}）。",
            LogLevel.Info,
            new[] { "test-heal", Host.Name });
        return Task.CompletedTask;
    }
}

/// <summary>记录型被动效果：OnMount/OnUnmount 向共享 trace 追加 mark（装载/清理顺序确定性用）。</summary>
public sealed class RecordingPassiveEffect : PassiveEffect
{
    private readonly string _mark;
    private readonly List<string> _trace;

    public RecordingPassiveEffect(string name, string mark, List<string> trace)
        : base(name)
    {
        _mark = mark;
        _trace = trace;
    }

    protected override void OnMount() => _trace.Add($"{_mark}:mount");

    protected override void OnUnmount() => _trace.Add($"{_mark}:unmount");
}

/// <summary>装载失败效果：OnMount 故意抛出（钩子失败隔离用；W3-A3：含装载尝试/卸载计数观察点——「未装载移除仅容器面」等用例）。</summary>
public sealed class FailingMountEffect : PassiveEffect
{
    public int MountAttempts;  // OnMount 尝试次数（每次装载驱动各计一次）
    public int UnmountCount;   // OnUnmount 次数（未装载不应触发）

    public FailingMountEffect(string name = "装载炸弹")
        : base(name)
    {
    }

    protected override void OnMount()
    {
        MountAttempts++;
        throw new InvalidOperationException("OnMount 故意爆炸");
    }

    protected override void OnUnmount() => UnmountCount++;
}

/// <summary>
/// 摇摆装载效果（W3-A3 用例）：首次装载尝试抛异常、后续成功——放置驱动兜底装载/重试语义
/// 与「初始化先、装载后」顺序契约观察点。
/// </summary>
public sealed class FlakyMountEffect : PassiveEffect
{
    public int Attempts;           // OnMount 尝试次数
    public bool SawPlacedAtMount;  // 成功装载时宿主是否已放置（放置处理内「初始化先」观察点）

    public FlakyMountEffect(string name = "摇摆效果")
        : base(name)
    {
    }

    protected override void OnMount()
    {
        Attempts++;
        if (Attempts == 1)
        {
            throw new InvalidOperationException("首次装载故意失败");
        }

        SawPlacedAtMount = Host.IsPlaced;
    }
}

/// <summary>清理失败效果：OnUnmount 故意抛出（钩子失败隔离用）。</summary>
public sealed class FailingUnmountEffect : PassiveEffect
{
    public FailingUnmountEffect(string name = "卸载炸弹")
        : base(name)
    {
    }

    protected override void OnUnmount() => throw new InvalidOperationException("OnUnmount 故意爆炸");
}

/// <summary>指令视图（施放用例）：来源/目标/伤害量。</summary>
[ContextView]
public class OrderView
{
    [Read]
    public virtual Card Source { get; set; }

    [Read]
    public virtual Card Target { get; set; }

    [Mutate]
    public virtual int Amount { get; set; }
}

/// <summary>
/// 伤害指令（主动效果施放用例）：施放事件 → 直接扣减目标生命（P5：DamageFlow 删除后，
/// 内核测试不再有独立伤害流程；伤害数值本身由游戏层动作实体承载，此处保留原可观测口径：
/// 先写「结算前」条目、再写「伤害生效」条目并扣血）。
/// </summary>
public sealed class DamageOrder : ActiveEffect<OrderView>
{
    private readonly LogicEngine _engine;

    public int CastCount;
    public int MountCount;   // 主动效果不应被调用（恒 0）
    public int UnmountCount; // 主动效果不应被调用（恒 0）

    public DamageOrder(LogicEngine engine, string name = "火球术")
        : base(name)
    {
        _engine = engine;
        CastTrigger.Register("施放", OnCast);
    }

    protected override void OnMount() => MountCount++;

    protected override void OnUnmount() => UnmountCount++;

    private Task OnCast(OrderView view, Context ctx, CancellationToken ct)
    {
        CastCount++;
        KernelFlow.ApplyDamage(_engine, view.Target, view.Amount);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 内核流程驱动器（P5）：<c>AttackFlow</c>／<c>DamageFlow</c> 删除后，内核测试以唯一保留的
/// <see cref="OrderFlow"/> 作为「流程注入 → handler 执行」的驱动器，并以一次性主动效果承载数值落地。
/// </summary>
internal static class KernelFlow
{
    /// <summary>驱动一次「攻击」等价流程：来源发起 → 施放结算（扣目标生命）→ 注入 handler（回血等）。</summary>
    internal static Task<EventStream> AttackAsync(LogicEngine engine, Card source, Card target, int amount)
        => engine.OrderFlow.ExecuteAsync(source, new DirectDamageCast(engine, target, amount), target, amount);

    /// <summary>伤害落地（原 DamageFlow 的同序可观测口径：结算前 → 伤害生效）。</summary>
    internal static void ApplyDamage(LogicEngine engine, Card target, int amount)
    {
        var damage = Math.Max(0, amount);
        engine.RootStream.WriteLog(
            "damage-flow", $"结算前：待结算伤害 {damage}。", LogLevel.Info, new[] { "damage", "preapply", target.Name });

        var health = target.GetData<HealthData>();
        var before = health.Hp;
        health.Hp -= damage;
        engine.RootStream.WriteLog(
            "damage-flow",
            $"伤害生效：'{target.Name}' 承受 {damage}（HP {before}→{health.Hp}）。",
            LogLevel.Info,
            new[] { "damage", "apply", target.Name });
    }

    /// <summary>一次性伤害施放动作（不入效果列表；仅作为流程的主动效果载荷）。</summary>
    private sealed class DirectDamageCast : ActiveEffect<OrderView>
    {
        private readonly LogicEngine _engine;
        private readonly Card _target;
        private readonly int _amount;

        internal DirectDamageCast(LogicEngine engine, Card target, int amount)
            : base("直接伤害")
        {
            _engine = engine;
            _target = target;
            _amount = amount;
            CastTrigger.Register("施放", OnCast);
        }

        private Task OnCast(OrderView view, Context ctx, CancellationToken ct)
        {
            ApplyDamage(_engine, _target, _amount);
            return Task.CompletedTask;
        }
    }
}

/// <summary>样例数据组件（数据组件共存/快照用例）：中性示范——任意作者类皆可作数据组件。</summary>
public sealed class SampleData
{
    /// <summary>样例数值。</summary>
    public int Value { get; set; }
}

/// <summary>S4 测试共享工具。</summary>
internal static class S4TestHelpers
{
    /// <summary>放置驱动：发射 card.placed 更新（载荷携带卡牌对象引用）。</summary>
    internal static Task Place(LogicEngine engine, Card card)
        => engine.Emit(Updates.CardPlaced, new Dictionary<string, object?> { [PayloadKeys.Card] = card });

    /// <summary>低层移除驱动：发射 effect.removed 更新（载荷携带卡牌＋效果两者）。</summary>
    internal static Task RemoveViaUpdate(LogicEngine engine, Card card, Effect effect)
        => engine.Emit(
            Updates.EffectRemoved,
            new Dictionary<string, object?> { [PayloadKeys.Card] = card, [PayloadKeys.Effect] = effect });

    /// <summary>总流条目过滤（按 keywords 全含匹配）。</summary>
    internal static LogEntry[] EntriesWith(LogicEngine engine, params string[] keywords)
        => engine.RootStream.Entries.Where(e => keywords.All(k => e.Keywords.Contains(k))).ToArray();

    /// <summary>总流条目中首个匹配的下标（未命中＝-1）。</summary>
    internal static int IndexOfEntry(LogicEngine engine, Func<LogEntry, bool> predicate)
    {
        var entries = engine.RootStream.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            if (predicate(entries[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
