using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W2a G3 修饰机制测试基础设施：卡构造辅助（机制级最小状态——不依赖对局装载路径）、
/// 固定基准检测器（非单位域注入/接口本体验证）、记录型修饰器（钩子调用记录）、失败注入修饰器（批量原子回滚用）。
/// </summary>
internal static class ModifierTestKit
{
    /// <summary>构造「已就绪单位卡」：独立构造＋AddData(UnitStateData)（机制测试所需最小状态；不依赖装载链）。
    /// W2b：数值经「装配期填充」（CreateInitial 一次性复制）装载——运行期写面已收窄（运行期变更经门户操作面）。</summary>
    public static UnitCard CreateReadyUnit(LogicEngine engine, int attack = 5, int defense = 4, int operateCost = 2)
    {
        var unit = new UnitCard(
            engine,
            new CardDefinition("测试单位", 1, operateCost, attack, defense, faction: Faction.Germany, rarity: Rarity.Standard));
        unit.AddData(UnitStateData.CreateInitial(unit.GetData<BattleStatsData>()));
        return unit;
    }

    /// <summary>构造「未就绪单位卡」：无 UnitStateData（未单位化——用于未就绪不得静默用例）。</summary>
    public static UnitCard CreateBareUnit(LogicEngine engine)
        => new(engine, new CardDefinition("未单位化单位", 1, 2, 3, 4, faction: Faction.Germany, rarity: Rarity.Standard));

    /// <summary>构造指令卡（非单位卡承载用例）。</summary>
    public static OrderCard CreateOrderCard(LogicEngine engine, string name = "测试指令")
        => new(engine, new CardDefinition(name, 2, 0, 0, 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));

    /// <summary>从 card.stat.changed 载荷读取变化字段集合（类型化读取——载荷契约断言辅助）。</summary>
    public static IReadOnlyList<string> ChangedFieldsOf(IReadOnlyDictionary<string, object?>? payload)
    {
        Assert.NotNull(payload);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(payload![GameUpdates.PayloadChangedFields]);
    }

    /// <summary>过滤 recorder 中的 card.stat.changed 更新（相位/其它触发输入的更新不计入——集中触发断言辅助）。</summary>
    public static IReadOnlyList<(string Type, IReadOnlyDictionary<string, object?>? Payload)> StatChangedUpdates(
        UpdateRecorder recorder)
        => recorder.Updates.Where(u => u.Type == GameUpdates.CardStatChanged).ToList();
}

/// <summary>
/// 固定基准检测器（测试用实现）：声明式字段＋固定基准值；就绪恒真。
/// 用途：非单位卡的数据域注入（证明机制不硬编码单位卡）、检测接口本体（四断言域）验证、名单扩展注册验证。
/// </summary>
internal sealed class FixedBaseDetector : IStatUpdateDetector
{
    private readonly Dictionary<string, int> _baseValues;

    public FixedBaseDetector(params (string Field, int BaseValue)[] fields)
    {
        Fields = fields.Select(f => f.Field).ToArray();
        _baseValues = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (field, baseValue) in fields)
        {
            _baseValues[field] = baseValue;
        }
    }

    public IReadOnlyList<string> Fields { get; }

    public bool IsReady => true;

    public int ReadBaseValue(string field) => _baseValues[field];

    public IReadOnlyDictionary<string, int> GenerateSnapshot(IReadOnlyDictionary<string, int> values)
    {
        var snapshot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            snapshot[field] = values[field];
        }

        return snapshot;
    }

    public StatUpdateComparison Compare(
        IReadOnlyDictionary<string, int> newSnapshot, IReadOnlyDictionary<string, int> oldSnapshot)
    {
        var changed = new List<string>();
        foreach (var field in Fields)
        {
            if (newSnapshot[field] != oldSnapshot[field])
            {
                changed.Add(field);
            }
        }

        return new StatUpdateComparison(changed);
    }
}

/// <summary>
/// 记录型修饰器（测试用子类）：记录挂载/注销钩子调用次数；变换逻辑可注入。
/// 同时验证「子类自写挂载/注销（自托管）」契约（钩子由机制统一调用）。
/// </summary>
internal sealed class RecordingModifier : Modifier
{
    public RecordingModifier(
        string field, object source, Func<Card, int, int>? transform = null, ModifierExpiry? expiry = null)
        : base(field, source, expiry)
        => Transform = transform;

    public int MountCalls { get; private set; }

    public int UnmountCalls { get; private set; }

    public Func<Card, int, int>? Transform { get; set; }

    protected internal override void OnMount(ModifierMountContext context)
    {
        MountCalls += 1;
        SubscribeExpiry(context); // 期限能力（有声明＝自订阅；模板子类同走该辅助）
    }

    protected internal override void OnUnmount()
    {
        UnmountCalls += 1;
        UnsubscribeExpiry();
    }

    protected internal override int Apply(Card card, int current)
        => Transform is null ? current : Transform(card, current);
}

/// <summary>失败注入修饰器（测试用）：挂载钩子抛明确异常（批量原子回滚/挂载回滚用例）。</summary>
internal sealed class FailingModifier : Modifier
{
    public const string FailureMessage = "失败注入：挂载钩子异常";

    public FailingModifier(string field, object source)
        : base(field, source)
    {
    }

    protected internal override void OnMount(ModifierMountContext context)
        => throw new InvalidOperationException(FailureMessage);

    protected internal override void OnUnmount()
    {
    }

    protected internal override int Apply(Card card, int current) => current;
}

/// <summary>
/// 重入探测修饰器（测试用）：链节执行窗内尝试重入（重跑 / 挂载）并记录「是否被同步拒绝」——
/// 验证重入策略（拒绝并明确错误；同步拒绝＝调用点即时可观测、不产生嵌套执行）。
/// </summary>
internal sealed class ReentrancyProbeModifier : Modifier
{
    public ReentrancyProbeModifier(string field, object source)
        : base(field, source)
    {
    }

    /// <summary>重跑请求是否被同步拒绝（链节内调用 RequestRerunAsync）。</summary>
    public bool RerunRejectedSync { get; private set; }

    /// <summary>挂载请求是否被同步拒绝（链节内调用 AddModifierAsync）。</summary>
    public bool AddRejectedSync { get; private set; }

    protected internal override void OnMount(ModifierMountContext context) => SubscribeExpiry(context);

    protected internal override void OnUnmount() => UnsubscribeExpiry();

    protected internal override int Apply(Card card, int current)
    {
        // W3-3 泛化随改：机制宿主为引擎薄容器 Card；本测试类宿主为卡类（CardBase 系）——经 cast 访问修饰容器（行为等价）。
        var host = (CardBase)card;
        RerunRejectedSync = RejectedSync(() => { host.Modifiers.RequestRerunAsync(); });
        AddRejectedSync = RejectedSync(() => { host.Modifiers.AddModifierAsync(new AddModifier(Field, 1, new object())); });
        return current;
    }

    private static bool RejectedSync(Action attempt)
    {
        try
        {
            attempt();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
