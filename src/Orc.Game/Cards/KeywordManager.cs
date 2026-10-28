using System.Diagnostics.CodeAnalysis;
using Orc.Cards;
using Orc.Core;

namespace Orc.Game.Cards;

/// <summary>
/// 词条管理组件（2C-A1；卡上伴生、构造期常驻——轻量伴生非数据组件，对齐卡侧修饰器组件先例）：
/// 词条组件的注册与索引（有无/参值查询）＋词条集合管理（授予/移除/参值改写入口面）＋清空处置面（A2 加性）。
/// 宿主＝引擎薄容器 <see cref="Card"/>（A2 泛化：单位与 HQ 同族承载——对齐修饰机制 W3-3 泛化先例）。
/// 与旧三口的关系＝合并替代（不保留为独立可查询组件）：标识登记 → 本组件注册与索引；逻辑装载 → 词条组件
/// 自含运行逻辑（个体卸载替代整体注销）；运行计数器 → 词条组件自含数据（不保留通用字符串键计数器形态）。
/// 查询面：无词条卡/无该词条/词条无参值＝false/null（不抛错——动态查询语义、存在性判定不含异常面）；
/// 「HasKeyword=true 且 GetKeywordValue=null」⇔ 有词条无参值。
/// 操作面（授予/移除/参值改写）：对已死亡卡＝拒绝（终态；查询面保持可用）；授予/移除幂等（重复＝无操作、返回 false）；
/// 卸载不存在词条不报错；参值改写前提＝词条存在（不存在＝明确异常）。
/// 授予/移除提供可等待完成面（await 返回时装载/卸载已毕、状态一致——内部同步执行、无异步悬置，P2 精神）。
/// 空管理面（无词条卡）零行为负担、可寻址（含无词条卡不抛）。
/// </summary>
public sealed class KeywordManager
{
    private readonly Card _card;
    private readonly LogicEngine _engine;
    private readonly Dictionary<string, KeywordComponent> _components = new(StringComparer.Ordinal);
    private readonly List<KeywordComponent> _active = new(); // 行为态（登记序；死亡注销清空——存在性/参值保留）

    internal KeywordManager(Card card, LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(engine);
        _card = card;
        _engine = engine;
    }

    /// <summary>
    /// 词条装载上下文提供器（A2 加性：自宿主属性内聚至本组件——CardBase 与 HQ 装配时各自转注；
    /// 授予链经此取装载上下文〔延迟读取〕；未装配＝null＝组件运行逻辑装载跳过注册、不抛错）。
    /// </summary>
    internal Func<KeywordLoadContext?>? LoadContextProvider { get; set; }

    // ---------- 查询面（统一读口：有无＋参值；无词条卡/无该词条等价） ----------

    /// <summary>是否存在指定词条（存在性查询；null/空白＝false、不抛错——无词条卡不抛）。</summary>
    public bool Has(string keyword)
        => !string.IsNullOrWhiteSpace(keyword) && _components.ContainsKey(keyword);

    /// <summary>读取词条参值（参值查询；无词条卡/卡无该词条/词条无参值＝null——与 <see cref="Has"/> 组合区分「有词条无参值」）。</summary>
    public int? GetValue(string keyword)
        => !string.IsNullOrWhiteSpace(keyword) && _components.TryGetValue(keyword, out var component)
            ? component.Value
            : null;

    /// <summary>已挂载词条组件（行为态、登记序只读快照——体系遍历面如部署链收尾；死亡注销后为空）。</summary>
    public IReadOnlyList<KeywordComponent> Components => _active.ToArray();

    /// <summary>按标识寻址词条组件（体系面；未注册＝false——null/空白不抛错）。</summary>
    public bool TryGetComponent(string keyword, [NotNullWhen(true)] out KeywordComponent? component)
    {
        if (!string.IsNullOrWhiteSpace(keyword) && _components.TryGetValue(keyword, out var found))
        {
            component = found;
            return true;
        }

        component = null;
        return false;
    }

    /// <summary>按标识寻址强类型词条组件（体系面；类型不匹配＝false）。</summary>
    public bool TryGetComponent<T>(string keyword, [NotNullWhen(true)] out T? component)
        where T : KeywordComponent
    {
        if (TryGetComponent(keyword, out var found) && found is T typed)
        {
            component = typed;
            return true;
        }

        component = null;
        return false;
    }

    /// <summary>
    /// 按标识寻址词条组件（行为态面；A4 加性——「按行为态取组件」的查询承载，如亡计执行面解析）：
    /// 仅当组件已注册（<see cref="Has"/> 面）且处于行为态时命中；死亡注销后＝false
    /// （行为态已清空——登记/参值保留、照常可读，但不可执行）。
    /// </summary>
    /// <exception cref="ArgumentNullException">无（null/空白＝false、不抛错——与查询面一致）。</exception>
    public bool TryGetActiveComponent<T>(string keyword, [NotNullWhen(true)] out T? component)
        where T : KeywordComponent
    {
        if (TryGetComponent(keyword, out var found) && found is T typed && _active.Contains(found))
        {
            component = typed;
            return true;
        }

        component = null;
        return false;
    }

    // ---------- 操作面（统一写口：授予/移除/参值改写；对已死亡卡＝拒绝——终态） ----------

    /// <summary>
    /// 授予（挂载；可等待完成）：①存在性置位（数据面可见）→ ②运行逻辑装载＋内嵌效果装载 → ③OnGrant（授予回调、最后）。
    /// 幂等（已存在＝无操作、返回 false——含不同参值：参值变更一律走 <see cref="SetValue"/>）；
    /// 装载链内失败＝fail-fast（逆序整体回滚：存在性回 false、零残留、无半态；异常上抛）。
    /// </summary>
    /// <exception cref="ArgumentException">keyword 为 null/空白。</exception>
    /// <exception cref="InvalidOperationException">对已死亡卡授予（终态拒绝）；或标识未注册/组件构造失败（fail-fast）。</exception>
    public Task<bool> GrantAsync(string keyword, int? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (IsDead())
        {
            throw new InvalidOperationException(
                $"卡牌 '{_card.Name}' 已死亡（终态）：词条授予被拒绝（查询面保持可用）。");
        }

        var context = LoadContextProvider?.Invoke();
        return Task.FromResult(GrantCore(keyword, value, context));
    }

    /// <summary>
    /// 移除（完整卸载；可等待完成）：①OnRevoke（行为面先撤）→ ②运行逻辑注销＋内嵌效果卸载 → ③存在性清除（参值不可读）。
    /// 幂等（不存在＝无操作、返回 false、不报错）；对已死亡卡＝拒绝（明确异常）。
    /// </summary>
    /// <exception cref="ArgumentException">keyword 为 null/空白。</exception>
    /// <exception cref="InvalidOperationException">对已死亡卡移除（终态拒绝）。</exception>
    public Task<bool> RevokeAsync(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (IsDead())
        {
            throw new InvalidOperationException(
                $"卡牌 '{_card.Name}' 已死亡（终态）：词条移除被拒绝（查询面保持可用）。");
        }

        return Task.FromResult(RevokeCore(keyword));
    }

    /// <summary>
    /// 参值改写（统一写口；纯存储改写——A1 不重载行为面）：前提＝词条存在；允许置空参值（参值位可空、与声明面一致）。
    /// 对已死亡卡＝拒绝（明确异常）。
    /// </summary>
    /// <exception cref="ArgumentException">keyword 为 null/空白。</exception>
    /// <exception cref="InvalidOperationException">对已死亡卡改写（终态拒绝）；或词条不存在（更新型操作的目标缺失——明确异常、
    /// 与「授予/移除＝状态确保型幂等」形成清晰对比）。</exception>
    public void SetValue(string keyword, int? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (IsDead())
        {
            throw new InvalidOperationException(
                $"卡牌 '{_card.Name}' 已死亡（终态）：词条参值改写被拒绝（查询面保持可用）。");
        }

        if (!_components.TryGetValue(keyword, out var component))
        {
            throw new InvalidOperationException(
                $"卡牌 '{_card.Name}' 无词条 '{keyword}' 可改写（参值改写前提＝词条存在；不存在＝明确异常）。");
        }

        component.OverrideValue(value);
    }

    // ---------- 清空处置面（A2 加性：抑制＝清空处置的「清空词条」承载） ----------

    /// <summary>
    /// 清空词条（「除保留项外全部」——抑制清空处置的第一件套；A2 加性）：
    /// 除 <paramref name="preserveKeyword"/>（功能保留位——保住「被抑制」自身存续）外，对全部登记词条
    /// 逐一走移除路径（完整卸载：OnRevoke → 运行逻辑注销＋内嵌效果卸载 → 存在性清除）；
    /// 含其它施加物（被压制）与防护标记（无法被压制/无法被抑制）——字面全量、无保护名单（Q&A-11）。
    /// 逐条异常隔离（记录、继续完成——「卸载力求完成」先例）；返回实际清除数。
    /// 对已死亡卡＝拒绝（终态；死亡注销已完成行为撤销）。
    /// </summary>
    /// <exception cref="ArgumentException">preserveKeyword 为 null/空白。</exception>
    /// <exception cref="InvalidOperationException">对已死亡卡清空（终态拒绝）。</exception>
    public Task<int> ClearAllExceptAsync(string preserveKeyword, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preserveKeyword);
        return ClearCoreAsync(preserveKeyword, ct);
    }

    /// <summary>
    /// 清空全部词条（S1 加性——「无保留位」全清；升级动作「词条集全清（含运行时授予）」的承载）：
    /// 对全部登记词条逐一走移除路径（完整卸载：OnRevoke → 运行逻辑注销＋内嵌效果卸载 → 存在性清除）；
    /// 与 <see cref="ClearAllExceptAsync"/> 同核心（差异＝保留位：本面无保留）。
    /// 逐条异常隔离（记录、继续完成——「卸载力求完成」先例）；返回实际清除数。
    /// 对已死亡卡＝拒绝（终态；死亡注销已完成行为撤销）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对已死亡卡清空（终态拒绝）。</exception>
    public Task<int> ClearAllAsync(CancellationToken ct = default) => ClearCoreAsync(preserveKeyword: null, ct);

    /// <summary>清空核心（preserveKeyword 为 null＝无保留位全清；逐条异常隔离——「卸载力求完成」）。</summary>
    private Task<int> ClearCoreAsync(string? preserveKeyword, CancellationToken ct)
    {
        if (IsDead())
        {
            throw new InvalidOperationException(
                $"卡牌 '{_card.Name}' 已死亡（终态）：清空词条被拒绝（查询面保持可用）。");
        }

        var toRevoke = new List<string>();
        foreach (var keyword in _components.Keys)
        {
            if (preserveKeyword is null || !string.Equals(keyword, preserveKeyword, StringComparison.Ordinal))
            {
                toRevoke.Add(keyword);
            }
        }

        var cleared = 0;
        foreach (var keyword in toRevoke) // 快照遍历（RevokeCore 修改注册表）
        {
            try
            {
                if (RevokeCore(keyword))
                {
                    cleared++;
                }
            }
            catch (Exception ex)
            {
                WriteError("词条清空", $"词条 '{keyword}' 移除异常（隔离：继续完成清空）", ex);
            }
        }

        return Task.FromResult(cleared);
    }

    // ---------- 装载链核心（加载期固有词条与运行时动态授予同一机制） ----------

    /// <summary>
    /// 授予链核心：①存在性置位（数据面可见）→ ②运行逻辑装载＋词条效果库实例化＋内嵌效果装载 → ③OnGrant（最后）。
    /// 装载链内失败（构造/挂载/实例化/回调抛）＝fail-fast：逆序整体回滚（②→①；存在性回 false、零残留）、异常上抛。
    /// 已存在＝幂等无操作（false）；不做死亡校验（公开面已校验、加载期为未死亡卡）。
    /// A4 加性：<paramref name="content"/>＝内容载荷（携带内容形态的授予；组件创建后、装载遍历前经内容装载点注入——
    /// 注入失败＝未置位、无状态变更）。
    /// 批 4 加性：成功后经词条变更观察者通知恰一次（真实授予完成点——对局侧随动，如守护维护重算；
    /// 幂等/回滚＝零通知；无上下文＝跳过、不抛错）。
    /// </summary>
    internal bool GrantCore(string keyword, int? value, KeywordLoadContext? context)
    {
        if (_components.ContainsKey(keyword))
        {
            return false; // 幂等：重复授予（含不同参值）＝无操作、参值保持
        }

        // S5：有对局快照＝按快照构造（运行期覆盖不影响本局）；无快照（独立构造）＝回退静态面。
        var component = context?.Keywords is { } keywordSnapshot
            ? keywordSnapshot.Create(_card, keyword, value)
            : KeywordRegistry.Create(_card, keyword, value); // 构造失败＝fail-fast（未置位、无状态变更）
        _components.Add(keyword, component);
        _active.Add(component); // ① 存在性置位（数据面可见）

        try
        {
            component.Mount(_card, context); // ② 运行逻辑装载（如伏击注册改写；缺上下文＝组件内防御跳过）

            // ② 词条效果装载：绑定 id 在编排目录里解析 → 装配到宿主实体的组件（执行面＝组件成员边）。
            AttachKeywordEffects(component, context);

            component.OnGrant(); // ③ 授予回调（最后）
        }
        catch (Exception)
        {
            RollbackGrant(component);
            throw;
        }

        NotifyKeywordChanged(); // 批 4 加性：真实授予完成点（对局侧随动——无上下文＝跳过）
        return true;
    }

    /// <summary>
    /// 移除链核心：①OnRevoke（作者回调、最先——其异常＝操作失败上抛、机制面未变更、无半态）→
    /// ②运行逻辑注销＋内嵌效果卸载（逆序；异常隔离记录、不阻断后续——对齐效果体系卸载链先例「卸载力求完成」）
    /// → ③存在性清除（参值不可读）。不存在＝幂等无操作（false）。
    /// 批 4 加性：成功后经词条变更观察者通知恰一次（真实移除完成点——存在性清除后、读点所见即终态；
    /// 幂等＝零通知；无上下文＝跳过、不抛错）。
    /// </summary>
    internal bool RevokeCore(string keyword)
    {
        if (!_components.TryGetValue(keyword, out var component))
        {
            return false; // 幂等：卸载不存在词条不报错
        }

        component.OnRevoke(); // ① 行为面先撤（上抛＝操作失败；机制面未变更）

        try
        {
            component.Unmount(); // ② 运行逻辑注销
        }
        catch (Exception ex)
        {
            WriteError("词条移除", "运行逻辑注销异常（隔离：继续完成卸载）", ex);
        }

        component.DisposeAttachedComponents(); // ② v4 组件挂载态卸载（逐句柄撤销；幂等）

        _components.Remove(keyword); // ③ 存在性清除
        _active.Remove(component);
        NotifyKeywordChanged(); // 批 4 加性：真实移除完成点（存在性清除后——读点所见即终态；无上下文＝跳过）
        return true;
    }

    /// <summary>
    /// 死亡注销（仅行为撤销——存在性保留、参值照常可读）：对全部行为态词条（后进先出）执行
    /// OnRevoke → 运行逻辑注销＋内嵌效果卸载；不执行「存在性清除」步。重复调用＝无操作（幂等）。
    /// 回调/注销异常＝隔离记录（不阻断死亡清理链——死亡流程健壮性优先；「清理未完成，以实况计」）。
    /// </summary>
    internal void RevokeOnDeath()
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var component = _active[i];
            try
            {
                component.OnRevoke();
            }
            catch (Exception ex)
            {
                WriteError("词条死亡注销", "OnRevoke 异常（隔离：继续完成注销）", ex);
            }

            try
            {
                component.Unmount();
            }
            catch (Exception ex)
            {
                WriteError("词条死亡注销", "运行逻辑注销异常（隔离：继续完成注销）", ex);
            }

            component.DisposeAttachedComponents(); // v4 组件挂载态卸载（死亡注销＝行为撤销；幂等）
        }

        _active.Clear(); // 行为态清空（存在性/参值保留——登记保留语义；重复注销＝无操作）
    }

    // ---------- 内部辅助 ----------

    /// <summary>
    /// 词条效果装载（批 4 数据化·装载期装配点）：按「词条 → 效果清单」绑定声明（注册面）逐项装配。
    /// <para>
    /// **唯一承载形态＝v4 组件资产**（<c>effects-v4/*.component.json</c>）：绑定 id 在编排运行时目录里可解析
    /// ＋宿主卡有编排实体 ⇒ 直接在宿主实体上装配组件（执行面＝组件成员边，
    /// <see cref="KeywordComponent.AttachedComponents"/> 持有句柄）。
    /// 旧格式数据壳效果（<c>Effect</c> 实例）随 Effect 家族删除——非 v4 宿主（HQ 等）由词条组件自身接线承担。
    /// </para>
    /// 失败语义（沿既有口径）：context 为 null（独立构造）＝不装配/跳过（功能不可用、加载不失败）；
    /// 绑定 id 不在 v4 目录＝fail-fast（异常上抛——授予链整体回滚、零残留）。
    /// </summary>
    private void AttachKeywordEffects(KeywordComponent component, KeywordLoadContext? context)
    {
        // S5：有对局快照＝按快照查绑定（运行期覆盖不影响本局）；无快照＝回退静态面。
        var bindings = context?.Keywords is { } keywords
            ? keywords.GetEffectBindings(component.Keyword)
            : KeywordRegistry.GetEffectBindings(component.Keyword);
        if (bindings.Count == 0)
        {
            return; // 无数据壳绑定（未迁移词条）：零行为变化
        }

        if (context is null)
        {
            return; // 独立构造（无上下文）：不实例化/跳过（功能不可用、加载不失败）
        }

        foreach (var binding in bindings)
        {
            if (TryAttachComponent(component, context, binding))
            {
                continue; // v4 组件资产形态已装配
            }

            if (_card is not CardBase)
            {
                // 非卡牌宿主（HQ 等）无编排实体：v4 组件形态不适用，该宿主的行为由词条组件自身接线承担
                // （先例＝免疫 HQ 侧伤害改写器，见 ImmuneKeywordComponent.Mount）。不视为配置错误。
                continue;
            }

            // 旧格式数据壳效果（Effect 实例）随 Effect 家族删除——绑定 id 必须在 v4 组件目录里可解析，
            // 否则＝配置错误（装配期已由 KeywordEffectAssembly.VerifyBindings 核验过，此处兜底 fail-fast）。
            throw new InvalidOperationException(
                $"词条 '{component.Keyword}' 的效果 '{binding.PrefabId}' 不在编排运行时的 v4 组件目录"
                + "（库键缺失——装载 fail-fast，授予整体回滚）。");
        }
    }

    /// <summary>
    /// v4 组件形态装配（词条效果的首选承载）：绑定 id 在编排运行时目录里可解析 ＋ 宿主卡有编排实体 ⇒
    /// 在宿主实体上装配该组件（装配即建边——执行面不再是 <c>Effect</c> 实例）。
    /// 无运行时／无实体／目录无该 id ＝ false（调用方回退旧格式数据壳效果——过渡期两形态并存）。
    /// 装配失败＝上抛（授予链 fail-fast、整体回滚——与旧形态同一失败口径）。
    /// </summary>
    private bool TryAttachComponent(
        KeywordComponent component, KeywordLoadContext context, KeywordEffectBinding binding)
    {
        if (context.Engine.Model is not { } runtime
            || _card is not CardBase { ModelEntity: { } entity }
            || !runtime.Catalog.Contains(binding.PrefabId))
        {
            return false;
        }

        try
        {
            var attachment = runtime.AddComponent(entity, binding.PrefabId, $"{binding.PrefabId}@{entity.Name}");
            component.AttachComponent(attachment);
            return true; // 动作端接缝（wraps→旧身份触发器）由运行时段事件统一接线（CommandManager 订阅 Assembled）
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"词条 '{component.Keyword}' 的 v4 效果组件 '{binding.PrefabId}' 装配失败：{ex.Message}"
                + "（装载 fail-fast，授予整体回滚）。",
                ex);
        }
    }

    /// <summary>授予链整体回滚（逆序：②组件挂载 → 运行逻辑 → ①存在性）；回滚动作力求完成（异常隔离记录——不掩盖原始失败）。</summary>
    private void RollbackGrant(KeywordComponent component)
    {
        component.DisposeAttachedComponents(); // v4 组件挂载态回滚（授予失败＝零残留）

        try
        {
            component.Unmount();
        }
        catch (Exception ex)
        {
            WriteError("词条授予回滚", "运行逻辑注销异常（隔离）", ex);
        }

        _components.Remove(component.Keyword); // ① 存在性回 false
        _active.Remove(component);
    }

    /// <summary>已死亡判定（终态）：单位卡且 IsDestroyed 置位（非单位卡无死亡语义——恒 false）。</summary>
    private bool IsDead()
        => _card is UnitCard unit
            && unit.TryGetData<UnitStateData>(out var state)
            && state.IsDestroyed;

    /// <summary>
    /// 词条变更通知（批 4 加性；守护词条化的随动驱动——真实授予/移除完成点、恰一次）：
    /// 经词条装载上下文的变更观察者通知对局侧（如守护维护重算——「授予-移除-复装动作完成即重算」）；
    /// 无上下文（独立构造/未装配）＝跳过、不抛错（操作不失败——沿用「可缺省＝防御跳过」先例）。
    /// 幂等重复（false 返回）与失败回滚（异常路径）＝零通知（改变才传播）。
    /// </summary>
    private void NotifyKeywordChanged() => LoadContextProvider?.Invoke()?.KeywordChangeObserver?.Invoke();

    /// <summary>写词条链错误记录（引擎总流；与既有装载留痕同渠道）。</summary>
    private void WriteError(string source, string message, Exception ex)
    {
        _engine.RootStream.WriteLog(
            source,
            $"{message}：{ex.Message}",
            LogLevel.Error,
            new[] { "keyword", "error", $"exception:{ex.GetType().Name}" });
    }
}
