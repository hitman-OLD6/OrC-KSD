using Orc.Game.Board;
using Orc.Game.Cards;

namespace Orc.Game.Players;

/// <summary>
/// HQ 状态数据组件（W3-3 G11）：血量本体（单一当前值模型——初始 20＝<see cref="Player.InitialHqHealth"/>；
/// 无独立上限概念）＋占位槽引用（布局语义——邻位/守护/轰炸机拦截基准）。
/// 数值模型：本体＝<see cref="Health"/>（受控写入——伤害经 HQ 数值门户、修饰经修饰机制；
/// 运行期直写为非合规路径）；有效值＝修饰机制链输出（缓存；「获得 +X 防御力」＝链上 +X 修饰贡献）。
/// 以引擎数据组件形态挂载（AddData/GetData）；随 HQ 构造常驻、全生命周期在场。
/// </summary>
public sealed class HqStateData
{
    /// <summary>血量本体（单一当前值；初始 20；受控写入——运行期变更经 HQ 数值路径）。
    /// 可能为负（不设下界钳制——与单位侧损伤量模型同构：修饰贡献与伤害差额不被吞掉；
    /// 数值表现钳制不低于 0 在快照层、读取一律以有效值为准；≤0 由归零响应收敛）。</summary>
    public int Health { get; internal set; } = Player.InitialHqHealth;

    /// <summary>所在阵线（布局语义；入线由对局/战场装配完成；未入线＝null）。</summary>
    public BattleLine? Line { get; internal set; }
}

/// <summary>
/// HQ 血量检测组件（W3-3 G11；更新检测接口的 HQ 专属实现）：
/// 把「HQ 血量」（<see cref="HqStateData.Health"/> 基准）纳入更新检测——「链/检测/集中触发」三段中的检测段；
/// 修饰/伤害导致有效值变化时正常发射（card.stat.changed 线；载荷变化字段集合含
/// <see cref="CardStatFields.HqHealth"/> 标识——「HQ 获得防御力时」/「HQ 伤害可监听」的监听源就绪）；
/// 无变化零发射（管线语义既有）。
/// 就绪条件＝HqStateData 在场（HQ 构造期常驻，恒就绪）。
/// 基准（链起点）＝血量本体；快照（落定输出）＝链输出、数值表现钳制不低于 0（≤0 由归零响应收敛）。
/// 无上限语义（ReadCapBaseValue 默认拒绝——单一当前值、无独立上限概念）、
/// 无表现位同步（本体即受控数据面、无独立表现位——不回写）、
/// 无变更守卫（HQ 不入死亡链——无死亡冻结语义；未实现 IStatChangeGuard 即恒允许）。
/// </summary>
public sealed class HqHealthUpdateDetector : IStatUpdateDetector
{
    /// <summary>HQ 血量字段清单（单列；声明序稳定——变化字段差异的排序来源之一）。</summary>
    private static readonly string[] FieldList = { CardStatFields.HqHealth };

    private readonly Hq _hq;

    /// <summary>创建检测组件（绑定 HQ；由 HQ 构造期装配注册）。</summary>
    internal HqHealthUpdateDetector(Hq hq)
    {
        ArgumentNullException.ThrowIfNull(hq);
        _hq = hq;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Fields => FieldList;

    /// <inheritdoc />
    public bool IsReady => _hq.TryGetData<HqStateData>(out _);

    /// <inheritdoc />
    public int ReadBaseValue(string field)
    {
        if (!string.Equals(field, CardStatFields.HqHealth, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"字段 '{field}' 不在 HQ 血量域（{CardStatFields.HqHealth}）。", nameof(field));
        }

        return _hq.GetData<HqStateData>().Health; // 未就绪＝明确异常（不静默）
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> GenerateSnapshot(IReadOnlyDictionary<string, int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // 机制喂入全量值（缺失＝机制契约违反——fail-fast、不静默）；
            // 数值表现钳制不低于 0（≤0 由归零响应收敛——表现面不出现负数）。
            [CardStatFields.HqHealth] = Math.Max(0, values[CardStatFields.HqHealth]),
        };
    }

    /// <inheritdoc />
    public StatUpdateComparison Compare(
        IReadOnlyDictionary<string, int> newSnapshot, IReadOnlyDictionary<string, int> oldSnapshot)
    {
        ArgumentNullException.ThrowIfNull(newSnapshot);
        ArgumentNullException.ThrowIfNull(oldSnapshot);
        var changed = new List<string>();
        if (newSnapshot[CardStatFields.HqHealth] != oldSnapshot[CardStatFields.HqHealth])
        {
            changed.Add(CardStatFields.HqHealth); // 稳定序＝声明序
        }

        return new StatUpdateComparison(changed);
    }
}
