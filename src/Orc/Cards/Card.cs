using Orc.Core;

namespace Orc.Cards;

/// <summary>
/// 卡牌（S4）：薄容器——数据放数据组件（<see cref="AddData"/> / <see cref="GetData{T}"/>）。
/// 继承 <see cref="Entity"/>（具名、可终结、可安全引用）；构造时绑定引擎（引擎首次创建卡牌时挂载其装载链处理器）。
/// 容器语义：键＝类型（每类型恰一份数据组件；重复添加拒绝、缺失读取抛明确异常）；引用共享（读取返回实例、外部可直接修改）。
/// 生命周期（驱动信号＝更新；响应＝装载链处理器）：放置（card.placed）→ 数据/状态就绪标记；
/// 销毁（<see cref="LogicEngine.DestroyCard"/>）＝杀＋card.destroyed → 卡牌登记移除。
/// <para>
/// **逻辑组件已迁出本类**（v4-only）：卡上的效果＝编排实体上的组件实例（<c>OrchestrationEntity</c>），
/// 旧的 <c>Effect</c> 列表容器与装载/销毁链随之删除。
/// </para>
/// </summary>
public class Card : Entity
{
    private readonly Dictionary<Type, object> _data = new();

    /// <summary>创建卡牌并绑定引擎（引擎引用用于：装载链登记、销毁辅助校验、卡牌登记（S5））。</summary>
    /// <exception cref="ArgumentNullException">engine 为 null（name 的 null 校验沿用 <see cref="Entity"/>）。</exception>
    public Card(LogicEngine engine, string name)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
        engine.EnsureCardLoadout();
        engine.RegisterCard(this); // S5 卡牌登记：创建即注册（全量快照数据源；销毁清理响应时移除）
    }

    /// <summary>
    /// 所属引擎（构造期绑定）。**仅游戏层内部使用**：内核未对 Orc.Game 开放 internal
    /// （开放会让游戏层大量 protected internal override 触发 CS0507），故此处保持可读；
    /// 外部程序集不得依赖本属性，扩展组件应经 <c>EffectRuntime.ResolveFor(card)</c> 取服务。
    /// </summary>
    public LogicEngine Engine { get; }

    /// <summary>
    /// 放置初始化标记：经 card.placed 放置处理置位（先于该处理内效果装载步骤）。
    /// W3-A3：本状态保留为查询原语（效果侧「仅在场生效」语义可读——如策略 2 内部门控）。
    /// </summary>
    public bool IsPlaced { get; private set; }

    // ---------- 数据组件（数据面：键＝类型；每类型恰一份） ----------

    /// <summary>
    /// 添加数据组件（任意类；实例由外部构造）。键＝实例的运行时类型。
    /// 同类型重复添加＝拒绝（明确错误，不静默覆盖；内容变更请取出实例改字段——引用共享）。
    /// </summary>
    /// <exception cref="ArgumentNullException">data 为 null。</exception>
    /// <exception cref="InvalidOperationException">已存在同类型数据组件。</exception>
    public void AddData(object data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var type = data.GetType();
        if (_data.ContainsKey(type))
        {
            throw new InvalidOperationException(
                $"卡牌 '{Name}' 已存在类型 '{type.Name}' 的数据组件（每类型恰一份；重复添加被拒绝，修改请取出实例改字段）。");
        }

        _data.Add(type, data);
    }

    /// <summary>读取数据组件（强类型；返回实例引用）。缺失＝明确异常（不返回 null/default，防 null 蔓延）。</summary>
    /// <exception cref="KeyNotFoundException">未添加该类型的数据组件。</exception>
    public T GetData<T>()
        where T : class
    {
        if (_data.TryGetValue(typeof(T), out var value))
        {
            return (T)value;
        }

        throw new KeyNotFoundException($"卡牌 '{Name}' 不存在类型 '{typeof(T).Name}' 的数据组件。");
    }

    /// <summary>试探读取数据组件（加性读面；未添加返回 false 且 data 为 null）。</summary>
    public bool TryGetData<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? data)
        where T : class
    {
        if (_data.TryGetValue(typeof(T), out var value))
        {
            data = (T)value;
            return true;
        }

        data = null;
        return false;
    }

    /// <summary>数据组件全量读面（S5 加性内部面）：类型 → 实例；快照序列化（<c>Orc.Output.SnapshotJson</c>）用。</summary>
    internal IReadOnlyDictionary<Type, object> DataComponents => _data;

    // ---------- 框架内部（装载链/销毁/收集） ----------

    /// <summary>置放置标记（装载链处理器放置处理调用；幂等）。</summary>
    internal void MarkPlaced() => IsPlaced = true;
}
