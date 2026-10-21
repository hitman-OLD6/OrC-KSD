using Orc.Core;

namespace Orc.Cards;

/// <summary>
/// 卡牌（S4）：薄容器——数据放数据组件（<see cref="AddData"/> / <see cref="GetData{T}"/>）、逻辑放逻辑组件（<see cref="AddEffect"/> / <see cref="Effects"/>）。
/// 继承 <see cref="Entity"/>（具名、可终结、可安全引用）；构造时绑定引擎（引擎首次创建卡牌时挂载其装载链处理器）。
/// 容器语义：键＝类型（每类型恰一份数据组件；重复添加拒绝、缺失读取抛明确异常）；引用共享（读取返回实例、外部可直接修改）；
/// Effects 为只读枚举面（增删经 Add/Remove API）；单线程语义、无额外保护。
/// 生命周期（驱动信号＝更新；响应＝装载链处理器/效果主触发器）：
/// 放置（card.placed）→ 初始化＋效果装载幂等兜底（被动：挂载＋OnMount；装载不再以放置状态为前提——三入口：加载时点/放置驱动/Add 即时）；
/// 加载时点装载（MountPassiveEffects）→ 挂主触发器＋OnMount（幂等；与放置驱动/Add 即时共用同一装载链——重复驱动幂等跳过）；
/// 移除（RemoveEffect）/ 效果移除更新（effect.removed）→ disposing 触发器统一销毁（容器移除＋恰一次 effect.removed）；销毁（<see cref="LogicEngine.DestroyCard"/>）＝杀＋card.destroyed（各效果 disposing 默认兜底）。
/// </summary>
public class Card : Entity
{
    private readonly Dictionary<Type, object> _data = new();
    private readonly List<Effect> _effects = new();

    /// <summary>创建卡牌并绑定引擎（引擎引用用于：装载链登记、已放置卡上的即时装载、销毁辅助校验、卡牌登记（S5））。</summary>
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
    /// W3-A3：本状态保留为查询原语（效果侧「仅在场生效」语义可读——如策略 2 内部门控）；不再作为框架即时装载的门控
    /// （Add 即装载——未上场语义由效果按三策略自持）。
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

    // ---------- 逻辑组件（效果列表） ----------

    /// <summary>效果列表（只读枚举面；顺序＝添加序——装载/清理顺序确定性的来源）。</summary>
    public IReadOnlyList<Effect> Effects => _effects;

    /// <summary>
    /// 添加效果。被动效果＝即时执行装载链（与放置驱动/加载时点装载行为一致——「Add 即装载」；
    /// 装载不再以放置状态为前提——「仅在场生效」的未上场语义由效果按三策略自持）；复装（移除后再添加）＝重新装载
    /// （托管登记随通用装载路径自动重建）。主动效果始终仅进入列表（等待施放）。
    /// </summary>
    /// <exception cref="ArgumentNullException">effect 为 null。</exception>
    /// <exception cref="InvalidOperationException">该实例已在列表（重复添加被拒绝）；或该效果实例已属于另一张卡牌。</exception>
    /// <param name="mountMainTriggers">
    /// false＝即时装载时只挂 disposing、跳过主触发器（宿主自持调用面——反制卡效果装配经装载链传入）；
    /// 默认 true＝既有行为。非反制卡装载路径一律不传本参数。
    /// </param>
    public void AddEffect(Effect effect, bool mountMainTriggers = true)
    {
        ArgumentNullException.ThrowIfNull(effect);

        if (_effects.Contains(effect))
        {
            throw new InvalidOperationException(
                $"卡牌 '{Name}' 已持有效果 '{effect.Name}'（同一实例重复添加被拒绝；先移除再添加）。");
        }

        if (effect.HasHost && !ReferenceEquals(effect.HostOrNull, this))
        {
            throw new InvalidOperationException(
                $"效果 '{effect.Name}' 已属于另一张卡牌（须先移除再添加）。");
        }

        effect.ValidateForUse(); // E-D1：一次性效果必须有可执行的业务主触发器（装配期 fail-fast）

        _effects.Add(effect);
        effect.SetHost(this);

        if (effect.Kind == TriggerKind.Passive)
        {
            CardLoadout.MountEffect(this, effect, mountMainTriggers); // 即时装载（幂等：未装载才执行；失败＝记录＋回滚为未生效、不阻断容器添加）
        }
        else
        {
            // 主动效果不装载钩子，但 disposing 触发器照常挂载（随卡销毁/兜底时机清理额外副作用）。
            effect.MountDisposal(Engine.Bus);
        }
    }

    /// <summary>
    /// 移除效果（规范入口）：E-D1 销毁唯一入口——进入即停用 → disposing 事件链 → 最终 teardown → 容器移除 → 发射；
    /// 未装载 → 仅容器面移除（无运行态销毁动作）。
    /// 发射（W3-A3）：实际移除命中＝恰发射一次 effect.removed（载荷＝{Card, Effect}；先清理〔落定〕、后发射〔对外通知〕）；
    /// 幂等无操作（不存在/已移除/非本卡）＝不发射。
    /// 幂等：移除不存在/已移除的效果＝无操作、不抛错；重复移除＝幂等。
    /// </summary>
    /// <exception cref="ArgumentNullException">effect 为 null。</exception>
    public void RemoveEffect(Effect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        CardLoadout.DisposeEffect(this, effect);
    }

    /// <summary>
    /// 移除效果（静默回滚路径；E-D1 收窄）：**仅限未成功事务的回滚**（装载失败/动态回滚）——
    /// 只做容器面移除＋清宿主引用，**不执行 disposing、不发射** effect.removed。
    /// 正常（成功事务）路径一律使用 <see cref="RemoveEffect"/>（完整销毁）。
    /// </summary>
    /// <exception cref="ArgumentNullException">effect 为 null。</exception>
    public void RemoveEffectSilently(Effect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        if (!ContainsEffect(effect) && !ReferenceEquals(effect.HostOrNull, this))
        {
            return; // 幂等
        }

        if (effect.IsMounted)
        {
            // 已装载的静默销毁：执行完整销毁链但不发射 effect.removed（回滚/无痕场景）。
            CardLoadout.DisposeEffect(this, effect, DisposalSource.Early, emitRemoved: false);
            return;
        }

        // 未装载（装载失败/回滚后的残留）：仅容器面移除——不执行 disposing。
        RemoveEffectCore(effect);
        effect.ClearHost();
    }

    /// <summary>
    /// 装载全部被动效果（加载时点装载入口；加性公共面——供游戏层在「卡牌加载」时点驱动）：
    /// 按 Effects 列表序逐效果执行装载链（挂主触发器 → OnMount；幂等——已装载者跳过；失败＝记录 ＋ 回滚为未生效、不阻断其它效果与宿主）。
    /// 与放置驱动装载（card.placed 处理器）/ Add 即时装载共用同一装载链与幂等语义：
    /// Add 即装载（W3-A3）后本面多为幂等跳过；对仍未装载者（如装载失败回滚后）＝兜底重试装载；主动效果不装载（仅列表进出）。
    /// </summary>
    /// <param name="mountMainTriggers">
    /// false＝只挂 disposing、跳过主触发器（宿主自己承载调用面的装载方使用——反制卡效果装配）；
    /// 默认 true＝既有行为（挂主触发器）。
    /// </param>
    public void MountPassiveEffects(bool mountMainTriggers = true)
    {
        foreach (var effect in EffectsSnapshot())
        {
            if (effect.Kind == TriggerKind.Passive)
            {
                CardLoadout.MountEffect(this, effect, mountMainTriggers);
            }
            else
            {
                effect.MountDisposal(Engine.Bus); // 主动效果：disposing 照常挂载（幂等）
            }
        }
    }

    // ---------- 框架内部（装载链/销毁/收集） ----------

    /// <summary>置放置标记（装载链处理器放置处理调用；幂等）。</summary>
    internal void MarkPlaced() => IsPlaced = true;

    /// <summary>从列表移除（不清算；销毁链调用）。返回是否命中。</summary>
    internal bool RemoveEffectCore(Effect effect) => _effects.Remove(effect);

    /// <summary>列表是否含该效果（归属校验用）。</summary>
    internal bool ContainsEffect(Effect effect) => _effects.Contains(effect);

    /// <summary>列表快照（遍历中允许清理动作修改列表）。</summary>
    internal IReadOnlyList<Effect> EffectsSnapshot() => _effects.ToArray();
}
