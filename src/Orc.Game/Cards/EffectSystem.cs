using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// X2 效果系统接线（W2c；游戏层效果装载/执行链接线——把引擎 Effect 机制接入卡牌加载与运行）：
// ①效果工厂注册表（CardEffectRegistry）：装配侧注册「效果标识 → 工厂（卡实例 → 效果实例）」＋
//   「卡 id → 效果声明清单」；注册即配置（重复/坏参数＝注册期 fail-fast）。
// ②效果装载上下文（CardEffectLoadContext）：卡加载时点经卡牌库注入（延迟读取——装配顺序下提供器目标在
//   实例化后创建）；独立构造（无装配源）＝null＝跳过装载、不抛错、加载不失败（沿用词条先例）。
// ③效果装载链（CardEffectLoader；加载时＝先于 card.load 广播，与词条装载先例对齐）：
//   声明解析（未知标识＝fail-fast 上抛、工厂返回 null＝fail-fast 上抛）→ 构造（执行异常＝隔离记录、跳过——
//   分界＝错误性质〔配置 vs 执行〕非代码阶段）→ 登记入容器（列表序＝声明序；Add 即装载——W3-A3）→
//   装载兜底（幂等；失败＝引擎装载链既有语义：回滚为未生效、记录、不阻断）＋装载失败残留清理。
//   W3-A3：托管登记（效果卸载 → 框架自动按来源批量撤销该效果施加的全部修饰器/光环声明）已从本链专属
//   移入「通用装载路径」自动化（内核「装载完成动作」扩展点；任何装载入口含复装统一登记）——本链不再手动登记。
// ④失败回滚口径（三层）：装配/配置层＝fail-fast（上抛、加载失败）；运行时装载层＝隔离（回滚为未生效、
//   不阻断其它效果与宿主、广播照常）；独立构造无上下文＝跳过（不抛错）。
// W3-1 G4 托管扩展（加性）：光环声明托管注销——效果卸载（任何路径统一收口）时，框架除按来源撤销该效果
//   施加的全部修饰器外，同点按来源注销其注册进场级收集面（<see cref="Board.GameEnvironment.Auras"/>）的
//   全部光环声明（先结构注销、后受益侧衔接一轮全卡重跑——有命中才跑）；装载失败残留清理同步覆盖——
//   「注册/注销由效果装载链托管」的收口面（注册发生＝效果装载时〔作者 OnMount 正向动作〕；注销＝本链托管）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 效果工厂注册表（X2；装配侧持有——注册源）：
/// 登记「效果标识 → 工厂（卡实例 → 效果实例）」与「卡 id → 效果声明清单（声明序）」。
/// 注册即配置：标识/工厂/卡 id 非法、重复注册/重复声明＝注册期明确拒绝（fail-fast、不吞）。
/// 声明与注册解耦（装载时才解析——未知标识在装载时 fail-fast）；注册表不承载运行期逻辑。
/// </summary>
public sealed class CardEffectRegistry
{
    private readonly Dictionary<string, Func<CardBase, Effect>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _prefabDeclarations = new(StringComparer.Ordinal);

    /// <summary>注册效果工厂（id 为解析键；工厂接收卡实例、返回效果实例——每卡实例各自构造）。</summary>
    /// <exception cref="ArgumentException">effectId 为 null/空白。</exception>
    /// <exception cref="ArgumentNullException">factory 为 null。</exception>
    /// <exception cref="InvalidOperationException">同一标识重复注册（被拒绝——注册即配置）。</exception>
    public void Register(string effectId, Func<CardBase, Effect> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryAdd(effectId, factory))
        {
            throw new InvalidOperationException($"效果标识 '{effectId}' 已注册（重复注册被拒绝——注册即配置）。");
        }
    }

    /// <summary>
    /// 声明某卡（按卡库注册 id）的效果清单（声明序＝装载顺序）。
    /// 清单含 null/空白标识或重复项＝声明期明确拒绝（fail-fast）；同一卡重复声明＝拒绝；
    /// 标识是否已注册不在声明期校验（装载时解析——未知标识在装载时 fail-fast）。
    /// </summary>
    /// <exception cref="ArgumentException">cardId 为 null/空白；清单含 null/空白标识或重复项。</exception>
    /// <exception cref="ArgumentNullException">effectIds 为 null。</exception>
    /// <exception cref="InvalidOperationException">同一卡重复声明（被拒绝——注册即配置）。</exception>
    public void Declare(string cardId, IEnumerable<string> effectIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentNullException.ThrowIfNull(effectIds);

        var list = new List<string>();
        foreach (var effectId in effectIds)
        {
            if (string.IsNullOrWhiteSpace(effectId))
            {
                throw new ArgumentException("效果声明清单含 null/空白标识（配置错误在声明期被拒绝）。", nameof(effectIds));
            }

            if (list.Contains(effectId))
            {
                throw new ArgumentException(
                    $"效果声明清单含重复项 '{effectId}'（重复声明被拒绝——fail-fast）。", nameof(effectIds));
            }

            list.Add(effectId);
        }

        if (!_declarations.TryAdd(cardId, list))
        {
            throw new InvalidOperationException($"卡牌 '{cardId}' 的效果声明已存在（重复声明被拒绝——注册即配置）。");
        }
    }

    /// <summary>读取某卡的效果声明（未声明＝空列表；装载链查询面）。</summary>
    internal IReadOnlyList<string> GetDeclarations(string cardId)
        => _declarations.TryGetValue(cardId, out var list) ? list : Array.Empty<string>();

    /// <summary>
    /// 声明某卡的**效果预制体**清单（S-C9 加性面；声明序＝装载顺序——与代码效果声明各走各的解析）。
    /// 清单含 null/空白标识或重复项＝声明期明确拒绝（fail-fast）；同一卡重复声明＝拒绝；
    /// 预制体是否已注册不在声明期校验（加载时解析）。
    /// </summary>
    /// <exception cref="ArgumentException">cardId 为 null/空白；清单含 null/空白标识或重复项。</exception>
    /// <exception cref="ArgumentNullException">prefabIds 为 null。</exception>
    /// <exception cref="InvalidOperationException">同一卡重复声明（被拒绝——注册即配置）。</exception>
    public void DeclarePrefab(string cardId, IEnumerable<string> prefabIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentNullException.ThrowIfNull(prefabIds);

        var list = new List<string>();
        foreach (var prefabId in prefabIds)
        {
            if (string.IsNullOrWhiteSpace(prefabId))
            {
                throw new ArgumentException("效果预制体声明清单含 null/空白标识（配置错误在声明期被拒绝）。", nameof(prefabIds));
            }

            if (list.Contains(prefabId))
            {
                throw new ArgumentException(
                    $"效果预制体声明清单含重复项 '{prefabId}'（重复声明被拒绝——fail-fast）。", nameof(prefabIds));
            }

            list.Add(prefabId);
        }

        if (!_prefabDeclarations.TryAdd(cardId, list))
        {
            throw new InvalidOperationException($"卡牌 '{cardId}' 的效果预制体声明已存在（重复声明被拒绝——注册即配置）。");
        }
    }

    /// <summary>读取某卡的效果预制体声明（未声明＝空列表；S-C9 加载链查询面）。</summary>
    internal IReadOnlyList<string> GetPrefabDeclarations(string cardId)
        => _prefabDeclarations.TryGetValue(cardId, out var list) ? list : Array.Empty<string>();

    /// <summary>解析效果工厂（未注册＝null；装载链 fail-fast 依据）。</summary>
    internal Func<CardBase, Effect>? Resolve(string effectId)
        => _factories.TryGetValue(effectId, out var factory) ? factory : null;
}

/// <summary>
/// 效果装载上下文（X2；卡加载时点装载效果所需的装配源面）：声明查询＋工厂解析。
/// 由对局加载路径经卡牌库注入（延迟读取）、<see cref="CardBase.LoadAsync"/> 的效果装载步骤按需取用；
/// 语境判定＝是否经对局装载路径装配（有上下文＝对局装载语境：装载链照常执行〔含卡上手动装配的效果〕）；
/// 无效果源（注册表缺省）＝声明为空、工厂解析为 null（声明装载部分跳过、装载照常）；
/// 独立构造（脱离对局——不经卡牌库）＝提供器为 null＝整链跳过（不抛错、加载不失败、功能不可用）。
/// </summary>
public sealed class CardEffectLoadContext
{
    private readonly CardEffectRegistry? _registry;
    private readonly string _cardId;

    internal CardEffectLoadContext(CardEffectRegistry? registry, string cardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        _registry = registry;
        _cardId = cardId;
    }

    /// <summary>本卡的效果声明清单（声明序；无效果源或未声明＝空列表）。</summary>
    public IReadOnlyList<string> Declarations => _registry?.GetDeclarations(_cardId) ?? Array.Empty<string>();

    /// <summary>本卡的效果预制体声明清单（S-C9 加性面；声明序＝代码效果之后的装载顺序；无效果源或未声明＝空列表）。</summary>
    public IReadOnlyList<string> PrefabDeclarations => _registry?.GetPrefabDeclarations(_cardId) ?? Array.Empty<string>();

    /// <summary>解析效果工厂（未注册/无效果源＝null）。</summary>
    internal Func<CardBase, Effect>? ResolveFactory(string effectId) => _registry?.Resolve(effectId);
}

/// <summary>
/// 效果装载链（X2；游戏层装载执行——加载时点驱动、先于 card.load 广播）：
/// ①声明解析（未知标识＝fail-fast 上抛）；②构造（执行异常＝隔离记录并跳过；工厂返回 null＝fail-fast 上抛）；
/// ③登记入容器（Effects 列表序＝声明序；Add 即装载）；④装载兜底（幂等；失败回滚＋记录、不阻断）；
/// ⑤装载失败残留清理（幂等兜底——托管登记自 W3-A3 起随「通用装载路径」自动完成，本链不再手动登记）。
/// 语境：对局装载语境（context 非 null）＝全链执行（声明部分可空——无效果源＝跳过①②③、④⑤照常；
/// 卡上手动装配〔AddEffect〕的效果经 ④⑤ 一并覆盖）；独立构造（context 为 null）＝整链跳过（不抛错、加载不失败）。
/// </summary>
internal static class CardEffectLoader
{
    /// <summary>
    /// 执行效果装载链（由 <see cref="CardBase.LoadAsync"/> 在词条装载之后、card.load 广播之前调用）。
    /// 语境：context 非 null＝对局装载语境（装载链照常执行——含卡上手动装配〔AddEffect〕的效果）；
    /// context 为 null＝独立构造（不经对局卡库——整链跳过：不抛错、加载不失败、功能不可用）。
    /// </summary>
    internal static async Task LoadAsync(
        CardBase card, LogicEngine engine, CardEffectLoadContext? context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(engine);

        if (context is null)
        {
            return; // 独立构造（脱离对局——无对局装载语境）：整链跳过（不抛错——沿用词条先例；功能不可用）
        }

        var declarations = context.Declarations;
        if (declarations.Count > 0)
        {
            // ① 工厂解析（先行、全部）：未知标识＝装配性错误——加载 fail-fast（该卡整体不完成加载）。
            var factories = new List<Func<CardBase, Effect>>(declarations.Count);
            foreach (var effectId in declarations)
            {
                var factory = context.ResolveFactory(effectId)
                    ?? throw new InvalidOperationException(
                        $"卡牌 '{card.Name}' 的效果声明 '{effectId}' 未注册（注册找不到——装配性错误，加载 fail-fast）。");
                factories.Add(factory);
            }

            // ② 构造（分界＝错误性质：执行异常＝隔离记录、跳过该效果；工厂返回 null＝装配性错误上抛）。
            var constructed = new List<Effect>(declarations.Count);
            for (var i = 0; i < declarations.Count; i++)
            {
                Effect? effect;
                try
                {
                    effect = factories[i](card);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    WriteError(
                        engine,
                        declarations[i],
                        "效果构造执行异常（隔离：该效果未生效、其它效果与宿主照常）",
                        ex);
                    continue;
                }

                if (effect is null)
                {
                    throw new InvalidOperationException(
                        $"卡牌 '{card.Name}' 的效果 '{declarations[i]}' 的工厂返回 null（装配性错误——加载 fail-fast）。");
                }

                constructed.Add(effect);
            }

            // ③ 登记入容器（Effects 列表序＝声明序——装载顺序的来源）。
            foreach (var effect in constructed)
            {
                card.AddEffect(effect);
            }
        }

        // ③b 效果预制体装载（S-C8/C9 加性）：按声明序实例化动态效果并入容器（在代码效果之后）。
        //     实例化失败（视图类型不可解析/处理器缺失/脚本求值失败/预制体未注册）＝隔离记录、跳过该条、不阻断。
        foreach (var prefabId in context.PrefabDeclarations)
        {
            var instantiation = engine.Prefabs.TryGetPrefab(prefabId, out var snapshot)
                ? DynamicEffectFactory.Instantiate(engine, snapshot, prefabId)
                : DynamicEffectInstantiation.Fail("prefab-missing", $"未注册效果预制体 '{prefabId}'。");

            if (!instantiation.Success)
            {
                engine.RootStream.WriteLog(
                    "效果预制体装载",
                    $"卡牌 '{card.Name}' 的效果预制体 '{prefabId}' 实例化失败（隔离：该效果未生效）——"
                    + $"[{instantiation.ErrorCategory}] {instantiation.Error}",
                    LogLevel.Error,
                    new[] { "effect", "prefab", "error", prefabId });
                continue;
            }

            var instantiatedEffect = instantiation.Effect!;
            card.AddEffect(instantiatedEffect); // Add 即装载（被动：挂载动态触发器；主动：列表进出）
            ApplyFrameworkInjects(card, engine, snapshot, instantiatedEffect); // S5：injects 声明落地
        }

        // ④ 装载兜底（幂等——Add 即装载后多为跳过；对装载失败回滚者＝重试装载；失败＝回滚为未生效、记录、不阻断）。
        card.MountPassiveEffects();

        // ⑤ 装载失败残留清理（幂等兜底）——托管登记自 W3-A3 起随「通用装载路径」自动完成（任何装载入口含复装均登记），
        //    本链不再手动登记；此处仅对仍未装载（失败回滚）者清理可能的中途残留。
        foreach (var effect in card.Effects.ToArray())
        {
            if (effect.Kind != TriggerKind.Passive)
            {
                continue; // 主动效果不装载、不托管（仅列表进出；施放时调用）
            }

            if (!effect.IsMounted)
            {
                // 装载失败（回滚为未生效）：清理可能的中途残留（幂等——无命中＝无操作；异常隔离、不阻断加载）。
                await CleanupFailedLoadAsync(card, engine, effect, ct);
            }
        }
    }

    /// <summary>
    /// 框架侧注入（S5；P7＝a）：把效果预制体声明的 <c>injects</c> 落地——按名在宿主卡上解析目标触发器、
    /// 取本效果内对应事件的 handler、注册进目标触发器默认区段（卸载随效果自动撤销）。
    /// 失败（非动态被动效果／区段非默认／目标未登记／事件无 handler）＝隔离记录、不阻断。
    /// </summary>
    private static void ApplyFrameworkInjects(
        CardBase card, LogicEngine engine, EffectSnapshot? snapshot, Effect effect)
    {
        if (snapshot is null || snapshot.Root.Injects.Count == 0)
        {
            return;
        }

        if (effect is not DynamicPassiveEffect dynamic)
        {
            WriteInjectError(engine, card, $"效果 '{effect.Name}' 非动态被动形态（隔离：注入跳过）。", effect.Name);
            return;
        }

        foreach (var inject in snapshot.Root.Injects)
        {
            if (!string.IsNullOrWhiteSpace(inject.BandName))
            {
                WriteInjectError(
                    engine, card,
                    $"注入区段 '{inject.BandName}' 不受支持（本版仅默认区段——隔离）。", inject.TargetTriggerName);
                continue;
            }

            if (!card.TryFindNamedTrigger(inject.TargetTriggerName, out var target, out var viewType)
                && !TryFindMatchTrigger(card, inject.TargetTriggerName, out target, out viewType))
            {
                WriteInjectError(
                    engine, card,
                    $"注入目标 '{inject.TargetTriggerName}' 未在宿主或对局级登记（隔离）。", inject.TargetTriggerName);
                continue;
            }

            if (!dynamic.TryGetEventHandler(inject.EventId, out var handler) || handler is null)
            {
                WriteInjectError(engine, card, $"注入事件 '{inject.EventId}' 无 handler（隔离）。", inject.EventId);
                continue;
            }

            try
            {
                effect.InjectByFramework(target!, viewType!, inject.EventId, handler, inject.Priority);
            }
            catch (Exception ex)
            {
                WriteInjectError(engine, card, $"注入失败（隔离）：{ex.Message}", inject.EventId);
            }
        }
    }

    /// <summary>
    /// 对局级具名触发器解析（E1-27 回退路径）：卡 → 玩家 → 对局级注册表（宿主卡未命中时使用）。
    /// </summary>
    private static bool TryFindMatchTrigger(Card card, string name, out object? trigger, out Type? viewType)
    {
        trigger = null;
        viewType = null;
        var registry = card switch
        {
            Players.Hq hq => hq.Owner.MatchTriggers,
            CardBase cardBase => cardBase.Owner?.MatchTriggers,
            _ => null,
        };

        return registry is not null && registry.TryFind(name, out trigger, out viewType);
    }

    private static void WriteInjectError(LogicEngine engine, CardBase card, string message, string detail)
    {
        engine.RootStream.WriteLog(
            "效果注入",
            $"卡牌 '{card.Name}'：{message}",
            LogLevel.Error,
            new[] { "effect", "inject", "error", detail });
    }

    /// <summary>
    /// 托管动作工厂（W3-A3；供游戏层装配期注册进内核「装载完成动作」扩展点——本侧的装载管线接线）：
    /// 装载成功（任何入口含复装）后自动登记托管清理；非 CardBase 宿主（纯内核卡——无修饰器组件/环境面）＝无操作
    /// （内核通用卡不受游戏层托管影响）。
    /// </summary>
    internal static Action<Card, Effect> CreateManagedCleanupAction(LogicEngine engine)
        => (card, effect) =>
        {
            if (card is CardBase cardBase)
            {
                RegisterManagedCleanup(cardBase, engine, effect);
            }
        };

    /// <summary>
    /// 托管清理登记（单效果；单源入口——W3-A3 起由「装载完成动作」在每次装载成功时自动调用〔任何装载入口统一、
    /// 复装自动重建〕，不再由各装载路径手动调用）：效果卸载（任何路径统一收口）时框架自动按来源批量撤销
    /// 该效果施加的全部修饰器/光环声明。
    /// </summary>
    internal static void RegisterManagedCleanup(CardBase card, LogicEngine engine, Effect effect)
        => effect.AddTeardownRegistration(() => RunManagedCleanup(card, engine, effect));

    /// <summary>
    /// 托管清理执行（引擎卸载链中同步调用）：按来源收口该效果的落地物——
    /// ①光环声明注销（W3-1 G4；先结构注销——随后修饰器撤销触发的重跑已反映声明消失；幂等——无命中＝无操作）；
    /// ②按来源批量撤销该效果施加的全部修饰器（含撤销跑链与集中触发）；
    /// ③受益侧衔接（W3-1 G4；有声明注销才跑——全卡重跑使受益卡现收集重算；同步等待：P2 同步/顺序语义，
    ///   调用返回即终态；执行窗内＝合并吸收、不阻塞）。
    /// P2（同步/顺序语义）下的同步等待：正常路径任务同步完成（调用返回即终态）；保证卸载点「无残留」成立。
    /// 异常＝隔离记录（不阻断卸载链其余步骤——与框架清理语义一致）。
    /// </summary>
    private static void RunManagedCleanup(CardBase card, LogicEngine engine, Effect effect)
    {
        var environment = GameEnvironment.ResolveFor(card);

        // W3-1 G4：光环声明托管注销（先结构注销——随后修饰器撤销触发的重跑已反映声明消失；幂等）
        var auraRemoved = 0;
        if (environment is not null)
        {
            try
            {
                auraRemoved = environment.Auras.RemoveBySource(effect);
            }
            catch (Exception ex)
            {
                WriteError(engine, effect.Name, "效果卸载光环注销异常（隔离：不阻断卸载链）", ex);
            }
        }

        try
        {
            card.Modifiers.RemoveBySourceAsync(effect).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteError(engine, effect.Name, "效果卸载托管清理异常（隔离：不阻断卸载链）", ex);
        }

        if (environment is not null && auraRemoved > 0) // W3-1 G4：受益侧衔接（有命中才跑——受益卡随动现收集重算）
        {
            try
            {
                environment.RerunAllCardsAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                WriteError(engine, effect.Name, "效果卸载光环注销衔接异常（隔离：不阻断卸载链）", ex);
            }
        }
    }

    /// <summary>装载失败残留清理（幂等——该来源无修饰/无声明＝无操作；异常隔离记录、不阻断加载）。</summary>
    private static async Task CleanupFailedLoadAsync(
        CardBase card, LogicEngine engine, Effect effect, CancellationToken ct)
    {
        // W3-1 G4：光环声明残留清理（先结构注销——装载失败不留残留；幂等）
        var environment = GameEnvironment.ResolveFor(card);
        var auraRemoved = 0;
        if (environment is not null)
        {
            try
            {
                auraRemoved = environment.Auras.RemoveBySource(effect);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteError(engine, effect.Name, "效果装载失败光环残留清理异常（隔离：加载照常）", ex);
            }
        }

        try
        {
            await card.Modifiers.RemoveBySourceAsync(effect, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteError(engine, effect.Name, "效果装载失败残留清理异常（隔离：加载照常）", ex);
        }

        if (environment is not null && auraRemoved > 0) // W3-1 G4：受益侧衔接（有命中才跑）
        {
            try
            {
                await environment.RerunAllCardsAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteError(engine, effect.Name, "效果装载失败光环衔接异常（隔离：加载照常）", ex);
            }
        }
    }

    /// <summary>写效果装载链错误记录（引擎总流；与既有装载留痕同渠道）。</summary>
    private static void WriteError(LogicEngine engine, string effectId, string message, Exception ex)
    {
        engine.RootStream.WriteLog(
            "效果装载",
            $"{message}：'{effectId}'——{ex.Message}",
            LogLevel.Error,
            new[] { "effect", "error", $"exception:{ex.GetType().Name}", effectId },
            new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            });
    }
}
