using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Model;
using Orc.Game.Triggers;

namespace Orc.Game.Actions;

/// <summary>
/// 动作目录（S10/S15）：**每对局一份**，按依赖序构造八个内置动作实例并持有其两端触发器。
/// <para>
/// 依赖序（单向、无环）：加入手牌（无依赖）→ 抽牌；伤害（无依赖）→ 攻击；死亡 → 加入手牌
/// （<c>Disposition=ToHand</c> 的子动作）；移动／加入战场／指令无依赖。
/// 构造后由 <see cref="Validate"/> 做装配期校验：实例非空、<see cref="ActionId"/> 唯一、依赖存在、图无环、
/// 每动作恰一个处理端且至少一个触发端——任一不满足即 fail-fast。
/// </para>
/// <para>
/// 归属：对局级（由 <c>CommandManager</c> 构造并持有，随对局销毁）；插件只持工厂与 handler 引用（S6）。
/// 工厂经 <see cref="ActionImplementationRegistry.Resolve"/> 解析（R17：整包默认 + 单动作覆盖），
/// 本类不再硬编码内置实现、也不持有默认实现集。
/// </para>
/// </summary>
internal sealed class MatchActionCatalog
{
    private readonly IReadOnlyList<MatchActionEntry> _entries;

    /// <summary>按 S10 依赖序构造八个动作实例并校验合并图。</summary>
    /// <param name="engine">对局引擎（执行流挂载点／信号发射）。</param>
    /// <param name="services">动作服务面（对局级单实例；全部动作共用）。</param>
    /// <param name="attackTargetLegal">攻击目标合法性判定通道。</param>
    /// <param name="counterEligibility">反击资格判定通道。</param>
    /// <param name="factories">动作实现集（缺省＝<see cref="ActionImplementationRegistry.Resolve"/> 的合并结果）。</param>
    /// <exception cref="ArgumentNullException">任一必需参数为 null。</exception>
    /// <exception cref="InvalidOperationException">未注册实现、依赖缺失、id 重复或图成环（装配期 fail-fast）。</exception>
    internal MatchActionCatalog(
        LogicEngine engine,
        MatchActionServices services,
        Func<UnitCard, Ref<Entity>, bool> attackTargetLegal,
        Func<UnitCard, UnitCard, bool> counterEligibility,
        ActionImplementationSet? factories = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(attackTargetLegal);
        ArgumentNullException.ThrowIfNull(counterEligibility);

        // R17：默认集 + 单动作覆盖的合并结果；未注册任何实现集时在此 fail-fast（错误信息含「未注册动作实现」）。
        var factory = factories ?? ActionImplementationRegistry.Resolve();
        EnsureComplete(factory);

        // ① 无依赖者先建：加入手牌、伤害、移动、加入战场、指令。
        AddToHand = factory.CreateAddToHand!(engine, services);
        Damage = factory.CreateDamage!(engine, services);
        Move = factory.CreateMove!(engine, services);
        JoinBattlefield = factory.CreateJoinBattlefield!(engine, services);
        Order = factory.CreateOrder!(engine.OrderFlow);

        // ② 依赖上一步的实例：抽牌、死亡（加入手牌）；攻击（伤害）。
        Draw = factory.CreateDraw!(engine, services, AddToHand);
        Death = factory.CreateDeath!(engine, services, AddToHand);
        Attack = factory.CreateAttack!(engine, Damage, attackTargetLegal, counterEligibility);

        _entries = new[]
        {
            Entry(ActionId.HandAdd, AddToHand, Array.Empty<ActionId>(), AddToHand.Ends.ToArray()),
            Entry(ActionId.Draw, Draw, new[] { ActionId.HandAdd }, Draw.Ends.ToArray()),
            Entry(ActionId.Damage, Damage, Array.Empty<ActionId>(), Damage.Ends.ToArray()),
            Entry(ActionId.Death, Death, new[] { ActionId.HandAdd }, Death.Ends.ToArray()),
            Entry(ActionId.Attack, Attack, new[] { ActionId.Damage },
                new ActionTriggerEnd("触发端", Attack.Trigger),
                new ActionTriggerEnd("处理端", Attack.Handle)),
            // 移动已迁出宿主程序集（P5b）：两端由实现自行暴露（类型擦除），目录不再引用其视图类型。
            // U2：成员地址同样由实现自报（`ActionTriggerEnd.Member`），目录只按其登记。
            Entry(ActionId.Move, Move, Array.Empty<ActionId>(), Move.Ends.ToArray()),
            Entry(ActionId.JoinBattlefield, JoinBattlefield, Array.Empty<ActionId>(), JoinBattlefield.Ends.ToArray()),
            // 指令的两端由内核唯一 flow 承载（阶段 1＝施放结算、阶段 2＝收尾）；U3 起两端**同样是引擎成员**
            // （`order.cast`／`order.finalize`）：地址由实现自报、实例仍来自 flow——目录不再有"无地址端"。
            Entry(ActionId.Order, Order, Array.Empty<ActionId>(), Order.Ends.ToArray()),
        };

        Validate(_entries);

        // R17：目录实例登记到引擎引用表——卡牌/管理器经 ActionRuntime 取同一实例（不引用实现类型）。
        foreach (var entry in _entries)
        {
            ActionRuntime.Register(engine, entry.Id, entry.Instance);
        }
    }

    /// <summary>加入手牌（<c>hand.add</c>；无依赖，是抽牌与死亡的子动作）。</summary>
    internal IAddToHandAction AddToHand { get; }

    /// <summary>抽牌（<c>draw</c>；依赖加入手牌）。</summary>
    internal IDrawAction Draw { get; }

    /// <summary>伤害结算（<c>damage</c>；无依赖）。</summary>
    internal IDamageAction Damage { get; }

    /// <summary>死亡（<c>death</c>；依赖加入手牌处置 <c>ToHand</c>）。</summary>
    internal IDeathAction Death { get; }

    /// <summary>攻击（<c>attack</c>；依赖伤害，互伤只经 <see cref="IDamageAction"/>）。</summary>
    internal IAttackAction Attack { get; }

    /// <summary>卡牌移动（<c>move</c>；无依赖；实现可来自插件程序集，故只暴露契约类型）。</summary>
    internal IMoveAction Move { get; }

    /// <summary>加入战场（<c>join.battlefield</c>；无依赖）。</summary>
    internal IJoinBattlefieldAction JoinBattlefield { get; }

    /// <summary>指令施放（<c>order.cast</c>；无静态依赖——运行期经 <c>OrderFlow</c>／<c>EffectRuntime</c>）。</summary>
    internal IOrderAction Order { get; }

    /// <summary>按登记序枚举八个动作条目（装配期审计／测试用）。</summary>
    internal IReadOnlyList<MatchActionEntry> Entries => _entries;

    private static MatchActionEntry Entry(
        ActionId id, object instance, IReadOnlyList<ActionId> dependsOn, params ActionTriggerEnd[] ends)
        => new(id, instance, dependsOn, ends);

    /// <summary>
    /// 构造一端（U2：**非泛型**——实例以 <see cref="object"/> 传入）。
    /// <para>
    /// 成员地址（<c>engine#&lt;成员名&gt;</c>）由 <see cref="EngineProcesses"/> 的常量给出：本类与预制体同源表，
    /// 因此"这一端是哪个成员"可被审查面与注册面直接读出，而不必反推触发器实例。
    /// </para>
    /// <para>
    /// **U3 后的唯一用途＝攻击动作**：其余 7 个动作的两端都由实现自报（<c>X.Ends</c>）。
    /// 攻击两端是宿主建的本体（<c>UnitAttackTrigger</c>／<c>AttackDamageTrigger</c>），
    /// 因此仍由此处补地址——把它并进 `IAttackAction.Ends` 即 U4 的收口项（届时本方法删除）。
    /// </para>
    /// </summary>
    /// <param name="role">角色（触发端／处理端）。</param>
    /// <param name="trigger">触发器本体（类型擦除）。</param>
    /// <param name="member">引擎成员名（null＝该端尚无引擎成员声明）。</param>
    /// <summary>装配期校验实现集是否覆盖八个内置动作（缺任一＝fail-fast，不静默降级）。</summary>
    private static void EnsureComplete(ActionImplementationSet factory)
    {
        var missing = ActionId.All.Where(id => !factory.HasImplementation(id)).Select(id => id.Value).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"未注册动作实现：动作 {string.Join("、", missing.Select(value => $"'{value}'"))} 缺少实现（default/override 均为空）。");
        }
    }

    /// <summary>
    /// 装配期校验（S10 第 4 条）：实例非空、id 唯一、依赖存在、图无环、两端齐备。
    /// 任何一项不满足即抛 <see cref="InvalidOperationException"/>（fail-fast，不静默降级）。
    /// </summary>
    internal static void Validate(IReadOnlyList<MatchActionEntry> entries)
    {
        var byId = new Dictionary<ActionId, MatchActionEntry>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry.Instance);
            if (!byId.TryAdd(entry.Id, entry))
            {
                throw new InvalidOperationException($"动作目录：动作 id '{entry.Id}' 重复（插件追加动作不得与内置八个冲突）。");
            }

            if (entry.Ends.Count == 0)
            {
                throw new InvalidOperationException($"动作目录：'{entry.Id}' 缺少触发端（R5：两端齐备）。");
            }

            if (entry.HandleEndCount != 1)
            {
                throw new InvalidOperationException($"动作目录：'{entry.Id}' 的处理端数量为 {entry.HandleEndCount}（R8：恰 1 处理端）。");
            }
        }

        foreach (var entry in entries)
        {
            foreach (var dependency in entry.DependsOn)
            {
                if (!byId.ContainsKey(dependency))
                {
                    throw new InvalidOperationException($"动作目录：'{entry.Id}' 依赖 '{dependency}'，但该动作未在目录中（缺依赖 fail-fast）。");
                }
            }
        }

        DetectCycles(entries, byId);
    }

    /// <summary>DFS 三色标记环检测（覆盖内置 + 插件合并图；发现回边即 fail-fast）。</summary>
    private static void DetectCycles(IReadOnlyList<MatchActionEntry> entries, IReadOnlyDictionary<ActionId, MatchActionEntry> byId)
    {
        var state = new Dictionary<ActionId, int>(); // 0＝未访问 / 1＝访问中 / 2＝已完成

        void Visit(ActionId id, Stack<ActionId> path)
        {
            if (state.TryGetValue(id, out var mark))
            {
                if (mark == 2)
                {
                    return;
                }

                if (mark == 1)
                {
                    var cycle = string.Join(" → ", path.Reverse().Concat(new[] { id }).Select(x => x.Value));
                    throw new InvalidOperationException($"动作目录：依赖图成环（{cycle}）。");
                }
            }

            state[id] = 1;
            path.Push(id);
            foreach (var dependency in byId[id].DependsOn)
            {
                Visit(dependency, path);
            }

            path.Pop();
            state[id] = 2;
        }

        foreach (var entry in entries)
        {
            Visit(entry.Id, new Stack<ActionId>());
        }
    }
}

/// <summary>动作目录条目（S15）：稳定 id、实例、声明依赖与两端触发器（装配期可枚举、可校验）。</summary>
internal sealed class MatchActionEntry
{
    internal MatchActionEntry(
        ActionId id, object instance, IReadOnlyList<ActionId> dependsOn, IReadOnlyList<ActionTriggerEnd> ends)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Id = id;
        Instance = instance;
        DependsOn = dependsOn;
        Ends = ends;
    }

    /// <summary>稳定动作 id（<c>damage</c>／<c>move</c>…）。</summary>
    internal ActionId Id { get; }

    /// <summary>动作实例本体。</summary>
    internal object Instance { get; }

    /// <summary>声明的子动作依赖（单向边；装配期据此做缺依赖与环检测）。</summary>
    internal IReadOnlyList<ActionId> DependsOn { get; }

    /// <summary>两端触发器（≥1 触发端 + 恰 1 处理端）。</summary>
    internal IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <summary>处理端数量（校验用）。</summary>
    internal int HandleEndCount => Ends.Count(end => end.Role == ActionTriggerEnd.HandleRole);
}
