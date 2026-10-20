using Orc.Core;
using Orc.Game.Cards.Data;
using Orc.Game.Players;

namespace Orc.Game.Cards;

/// <summary>
/// 卡牌基类骨架（三大类卡牌体系；2A 结构层）：单位 / 指令 / 反制的公共基座。
/// 继承引擎薄容器 <see cref="Orc.Cards.Card"/>——数据组件经既有组件体系挂载（AddData/GetData）、效果经 AddEffect 体系，
/// 与既有 CardDefinition / CardLibrary / 集合类衔接形态不变。
/// ①实例化路径数据组件装配（按类别差异化，需求原文 E 区）：全类别＝【阵营〔国籍〕＋部署费】
///   合并组件（<see cref="FactionCostData"/>——S10 重构：原独立指挥点花费组件已退役、不保留兼容读面）；
///   单位＝另加【对战】（<see cref="UnitCard"/> 构造装配）；指令＝合并组件（无其他）；反制＝合并组件（激活状态组件类就绪、挂载属后续批次）。
///   四合一 CardStatsData 与旧独立花费组件均已退役、无双真源。
/// ②对局开始卡牌加载模板（<see cref="LoadAsync"/>）：固定链＝【重建/装配（可重写扩展点、默认空实现）→ 对局级 ID 分配（W1-1 加性）→ 元数据装配（W1-1 加性：TagData〔稀有度＋开放 tag；S10 起国籍移出〕）→ 词条装载（2C 加性）→ 效果装载（X2 加性）→ 广播 card.load】；
///   持久化重建本批仅显式预留空位（无任何重建逻辑；总装阶段实现）。
/// ③触发器声明位：预打出 / 打出 / 使用反制——对象在位于具体子类（构造期创建、装配点就绪）；链内容属打出链批次（2B）。
/// ④修饰器组件（W2a G3 加性）：所有卡类构造期常驻持有 <see cref="Modifiers"/>（轻量伴生容器——非数据组件、不进
///   装配/加载/快照语义；空状态零行为负担）；挂载/注销由效果装载链托管属后续批次（W2c）、本单先提供机制与装配接口。
/// 加载失败（未注册 id、加载模板异常等）＝初始化 fail-fast（不进入进行态、明确上抛——加载路径保证）。
/// </summary>
public abstract class CardBase : Orc.Cards.Card
{
    private readonly LogicEngine _engine;

    /// <summary>创建卡牌实例（引擎绑定＋名称取自定义＋全类别基础数据组件装配〔阵营〔国籍〕＋部署费合并组件〕）。</summary>
    /// <exception cref="ArgumentNullException">engine 或 definition 为 null（name 校验沿用 Entity）。</exception>
    protected CardBase(LogicEngine engine, CardDefinition definition)
        : base(engine, DefinitionName(definition))
    {
        _engine = engine;
        Definition = definition;

        // 实例化路径数据组件装配（旧 → 新：原四合一 CardStatsData → 2A 拆分〔指挥点花费＋对战〕→
        // S10 再重构：指挥点花费并入「阵营〔国籍〕＋部署费」合并组件〔FactionCostData——全类别构造期常驻〕；
        // 单位另加对战组件〔见 UnitCard〕）。
        LoadConstructionComponents(engine);

        // W2a G3 修饰机制：卡侧修饰器组件构造期常驻（所有卡类；轻量伴生、非数据组件——不进装配/加载/快照语义）。
        Modifiers = new CardModifierComponent(this, engine);

        // 2C-A1 词条组件化：卡上词条管理组件构造期常驻（所有卡类；轻量伴生、非数据组件——空管理面零行为负担、可寻址不抛）。
        Keywords = new KeywordManager(this, engine);
    }

    /// <summary>本卡的定义（代码注册形态；名称/类别/四项数值可读）。</summary>
    public CardDefinition Definition { get; }

    /// <summary>
    /// 资产域（R14/D20/S6）：实例**随身携带**的域标记——由加载段（<see cref="CardLibrary.Instantiate"/> 经
    /// <see cref="CardLibrary.DomainOf"/>）写入，未归域的独立构造默认正式域。
    /// <para>
    /// **不得参与任何玩法逻辑判定**：合法消费方只有构筑/存档、UI 展示、诊断报告三类。
    /// 需要区分域的玩法逻辑视为需求缺口（回退讨论），不得就地读本属性。
    /// </para>
    /// </summary>
    public Orc.Core.AssetDomain AssetDomain { get; internal set; } = Orc.Core.AssetDomain.Formal;

    /// <summary>是否为测试资产（域的诊断/展示读面；同样**不得**用于玩法判定）。</summary>
    public bool IsTestAsset => AssetDomain == Orc.Core.AssetDomain.Test;

    private readonly NamedTriggerTable _namedTriggers = new();

    /// <summary>登记具名触发器（S5；子类构造期调用）：供效果预制体 <c>injects</c> 声明按名解析注入目标。</summary>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="ArgumentNullException">trigger 或 viewType 为 null。</exception>
    protected void RegisterNamedTrigger(string name, object trigger, Type viewType)
        => _namedTriggers.Register(name, trigger, viewType);

    /// <summary>按名查找具名触发器（S5 注入目标解析；未登记＝false）。</summary>
    public bool TryFindNamedTrigger(
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? trigger,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? viewType)
        => _namedTriggers.TryFind(name, out trigger, out viewType);

    /// <summary>
    /// 卡侧修饰器组件（W2a G3 修饰机制）：每实例构造期常驻的专用轻量容器
    /// （修饰器集＋更新检测组件名单＋各组件缓存＋全量重跑链与「每轮管线」）。
    /// 空状态零行为负担（无修饰不发任何更新、不影响既有装配/加载/快照行为）；集中查询面＝<see cref="CardModifierComponent.All"/>。
    /// 不纳入 AddData 数据组件体系（轻量伴生；不进快照导出）。
    /// </summary>
    public CardModifierComponent Modifiers { get; }

    /// <summary>
    /// 词条管理组件（2C-A1；每实例构造期常驻的卡上伴生——轻量伴生、非数据组件，对齐 <see cref="Modifiers"/> 先例）：
    /// 词条组件的注册与索引（有无/参值查询）＋授予/移除/参值改写统一读写口。加载时词条装载经其授予链完成
    /// （<see cref="LoadKeywords"/>——先于 card.load）；运行时动态授予（含「无词条卡 → 有」）经其操作面。
    /// 空管理面（无词条卡）零行为负担、可寻址（含无词条卡不抛——统一根写法：<c>card.Keywords.Has(...)</c>）。
    /// </summary>
    public KeywordManager Keywords { get; }

    /// <summary>
    /// 所属玩家（2B 加性面；加载时装配——<see cref="LoadAsync"/> 记录；未加载＝null）。
    /// 打出链的验证（指挥点）、扣费与离手（自手牌移除）以归属为前提；未加载卡不可驱动打出链。
    /// </summary>
    public Player? Owner { get; private set; }

    /// <summary>
    /// 回合上下文（2B 加性面；internal）：当前行动方读取提供器——反制「仅己方回合」验证所需的对局上下文；
    /// 由对局加载路径经卡牌库注入（延迟读取、随对局回合推进取当前值）；独立构造（脱离对局）＝null
    /// （反制使用将无法通过回合验证——拒绝、可观测）。
    /// </summary>
    internal Func<Player?>? TurnPlayerProvider { get; set; }

    /// <summary>
    /// 词条装载上下文提供器（2C-A1 加性面；internal；A2 加性：存储内聚至词条管理组件——本属性为其转发面）：
    /// 加载时（<see cref="LoadAsync"/> 的词条装载步骤）与运行时授予时装载词条组件运行逻辑所需的对局级服务
    /// （如「造成攻击伤害」触发器——伏击/免疫/重甲效果的改写注册口；钳击/压制所需的对局服务）；由对局加载路径经卡牌库注入（延迟读取）；
    /// 独立构造（脱离对局）＝null（词条组件运行逻辑装载跳过注册、不抛错——功能不可用、加载不失败）。
    /// </summary>
    internal Func<KeywordLoadContext?>? KeywordLoadContextProvider
    {
        get => Keywords.LoadContextProvider;
        set => Keywords.LoadContextProvider = value;
    }

    /// <summary>
    /// 效果装载上下文提供器（X2 加性面；internal）：加载时（<see cref="LoadAsync"/> 的效果装载步骤）装载卡牌效果
    /// 所需的对局装载语境与装配源（效果声明查询＋工厂解析；效果源可空——无注册表时声明为空、装载照常）；
    /// 由对局加载路径经卡牌库注入（延迟读取）；独立构造（脱离对局——不经卡牌库）＝null（效果装载整链跳过——不抛错、加载不失败、功能不可用）。
    /// </summary>
    internal Func<CardEffectLoadContext?>? EffectLoadContextProvider { get; set; }

    /// <summary>
    /// 弃置动作提供器（P4 加性面；internal）：反制卡一次性消耗时调用既有
    /// <c>PlayerManager.DiscardCardAsync</c>（手牌移除 → 销毁 → <c>card.discarded</c>），不新建第二条弃置路径。
    /// 由对局加载路径经卡牌库注入；独立构造（缺省）＝null——消耗路径走同序同信号的脱局兜底。
    /// </summary>
    internal Func<Player, CardBase, Orc.Cards.Card?, CancellationToken, Task>? DiscardCardProvider { get; set; }

    /// <summary>
    /// 对局级卡牌 ID（W1-1 加性面；internal）：加载时分配——由 <see cref="MatchCardIdProvider"/> 取新值；
    /// 未分配（未加载 / 独立构造 / 无提供器）＝null。既有 <see cref="Orc.Core.Entity.Id"/> 为 Guid 不可比较，
    /// 本整数序号承载「构筑外判定」（ID ＞ 初始化水位线）。
    /// 「同一实例同一 ID」：重复加载不重分配（幂等——判定不失真）。
    /// </summary>
    internal int? MatchCardId { get; private set; }

    /// <summary>
    /// 对局级卡牌 ID 提供器（W1-1 加性面；internal）：加载时（<see cref="LoadAsync"/> 的 ID 分配步骤）调用以获取下一个自增 ID；
    /// 由对局加载路径经卡牌库注入（延迟读取——对局装配顺序下提供器目标在实例化后才就绪）；
    /// 独立构造（脱离对局）＝null（不分配 ID——无对局上下文，判定侧以「未分配」明确拒绝）。
    /// </summary>
    internal Func<int>? MatchCardIdProvider { get; set; }

    // ---------- 受控变更门户（S10「G12补」；「阵营〔国籍〕＋部署费」合并组件的受控写面） ----------

    /// <summary>
    /// 门户：设置国籍（S10 受控变更面——合并组件 <see cref="FactionCostData"/> 的国籍写面；
    /// 运行时修改国籍的唯一合规入口）：
    /// 值域＝已定义枚举值（11 值）；非法值＝明确拒绝（fail-fast、值不变——不产生半改）；
    /// 修改为静默数据变更（不发射/不通知——延续「元数据静默变更」先例；读取与筛选随动即观测面，
    /// 单源直读——既有筛选/读取消费立即读到新值）。
    /// 未装配合并组件（独立构造未加载卡等）＝明确异常（装配性错误——不静默）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">faction 为未定义枚举值（非法修改被拒绝）。</exception>
    /// <exception cref="KeyNotFoundException">合并组件缺失（未装配实例取件＝明确错误）。</exception>
    public void SetFaction(string faction) => GetData<FactionCostData>().SetFactionCore(faction);

    /// <summary>
    /// 门户：设置部署费基准值（S10 受控变更面——修改合并组件部署费「本体」（基准值）的唯一合规入口）：
    /// 值域＝非负整数（0 合法）；非法值＝明确拒绝（fail-fast、值不变——不产生半改）；
    /// 合法修改＝落值 → 经既有数据改变管线传播（链重跑＋集中触发——有变更才发
    /// <see cref="GameUpdates.CardStatChanged"/>、无变更零发射；复用既有信号、不新增）；
    /// 消费点行为不变（打出校验/扣费/复验等仍读「有效值」——基准→链→有效值）。
    /// 未装配合并组件＝明确异常（装配性错误——不静默）。
    /// </summary>
    /// <param name="deployCost">部署费基准值（≥0）。</param>
    /// <exception cref="ArgumentOutOfRangeException">deployCost 为负（非法修改被拒绝）。</exception>
    /// <exception cref="KeyNotFoundException">合并组件缺失（未装配实例取件＝明确错误）。</exception>
    public async Task SetDeployCostBaseAsync(int deployCost, CancellationToken ct = default)
    {
        GetData<FactionCostData>().SetDeployCostCore(deployCost); // 受控落值（值域校验先行——非法值不落）
        await Modifiers.RequestRerunAsync(ct); // 基准变更 → 链重跑＋集中触发（有变更才发）
    }

    /// <summary>
    /// 对局开始卡牌加载（模板方法；由加载路径逐张调用）：
    /// ① 装配归属（Owner＝所属卡组玩家；2B 加性：打出链验证/扣费/离手依据）；
    /// ② 重建/装配步骤（可重写扩展点 <see cref="RebuildFromPersistence"/>；默认空实现——持久化重建后置、本批无重建逻辑）；
    /// ③ 对局级卡牌 ID 分配（W1-1 加性：经提供器分配自增 ID；幂等——已分配保持原值；先于 card.load 广播）；
    /// ④ 元数据装配（W1-1 加性：从定义读稀有度/开放 tag → 装配 TagData〔S10：国籍移出——国籍于构造期经 FactionCostData 装配〕；先于 card.load 广播——消费者查询不到未就绪状态）；
    /// ⑤ 部署逻辑生成（A4 加性：数据装配组收尾——仅单位卡、经装配源查询〔无登记＝不生成〕，
    ///    为单位卡生成并挂载部署逻辑组件；先于 card.load 广播——「加载时生成部署组件」；生成失败＝记录、不阻断加载）；
    /// ⑥ 词条装载（2C-A1 加性：从定义读词条声明〔标识＋可选参值〕→ 逐条经词条管理组件的授予链挂载
    ///    〔存在性置位 → 运行逻辑装载＋内嵌效果装载 → OnGrant；伏击在运行逻辑装载时挂到「造成攻击伤害」〕；
    ///    无词条卡＝无副作用；装载先于 card.load 广播——消费者查询不到未就绪状态）；
    /// ⑦ 效果装载（X2 加性：从装配源解析效果声明 → 构造＋登记＋装载被动效果〔挂主触发器＋OnMount；含监听 handler 注册〕
    ///    ＋托管登记〔卸载自动按来源撤销修饰器〕；无装配源＝跳过；先于 card.load 广播——消费者查询不到未就绪状态）；
    /// ⑧ 广播 card.load（载荷 {Card, Player}——Card＝本实例、Player＝所属卡组玩家）。
    /// 加载路径保证每卡恰一次调用；失败直接上抛（初始化 fail-fast）。
    /// </summary>
    /// <exception cref="ArgumentNullException">owner 为 null。</exception>
    public async Task LoadAsync(Player owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        Owner = owner;
        LoadMatchCardId();
        await LoadComponentPhaseAsync(ct); // 两段式：第一段＝内置注册序；第二段＝扩展／社区组件按数据体声明序
        await LoadEffectsAsync(ct);
        await GameUpdates.EmitCardLoad(_engine, owner, this, ct);
    }

    /// <summary>
    /// 对局级卡牌 ID 分配（W1-1；加载时）：未分配且提供器在位＝经提供器取新 ID；已分配＝保持原值
    /// （幂等——「同一实例同一 ID」；重复加载不重分配、判定不失真）。提供器缺席（独立构造）＝不分配。
    /// </summary>
    private void LoadMatchCardId()
    {
        if (MatchCardId is null && MatchCardIdProvider is not null)
        {
            MatchCardId = MatchCardIdProvider();
        }
    }

    /// <summary>
    /// 构造期组件装配（P4；构造期相位）：按注册面注册序执行「构造期」loader——
    /// 仅执行本卡组件集确实声明了的项（typeCategory／factionCost／battleStats）；
    /// 构造期 loader 同步完成（返回已完成任务——P2 同步语义），此处按完成等待。
    /// </summary>
    private void LoadConstructionComponents(LogicEngine engine)
    {
        var context = new CardComponentLoadContext(engine);

        foreach (var entry in CardComponentRegistry.Registered)
        {
            if (entry.Phase != CardComponentPhase.Construction)
            {
                continue;
            }

            var definition = FindComponent(entry.Name);
            if (definition is null)
            {
                continue;
            }

            entry.Loader(this, definition, context).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 加载期两段式组件装配（P4／Q9a）：
    /// 第一段＝**内置**注册项且加载相位——按游戏层注册序（防组件间依赖丢失）；
    /// 第二段＝本卡剩余组件定义中的**扩展／社区**项——按数据体声明序（未注册项＝隔离记录）。
    /// 第一段失败＝fail-fast（内置配置错误——上抛、该卡加载失败）；第二段失败＝隔离（不阻断加载）。
    /// 全部先于 <c>card.load</c> 广播（消费者查询不到未就绪状态）。
    /// </summary>
    private async Task LoadComponentPhaseAsync(CancellationToken ct)
    {
        var context = new CardComponentLoadContext(
            _engine,
            () => KeywordLoadContextProvider?.Invoke(),
            () => EffectLoadContextProvider?.Invoke());

        var consumed = new HashSet<Type>();

        // 第一段：内置 + 加载相位，按注册序。
        foreach (var entry in CardComponentRegistry.Registered)
        {
            if (!entry.IsBuiltIn || entry.Phase != CardComponentPhase.Load)
            {
                continue;
            }

            var definition = FindComponent(entry.Name);
            if (definition is null)
            {
                continue;
            }

            await entry.Loader(this, definition, context);
            consumed.Add(definition.GetType());
        }

        // 第二段：剩余组件定义（数据体声明序；扩展／社区组件）。
        foreach (var definition in Definition.Components)
        {
            if (consumed.Contains(definition.GetType()))
            {
                continue;
            }

            if (!CardComponentRegistry.TryResolve(definition.ComponentName, out var entry))
            {
                WriteComponentIsolation($"未知组件 '{definition.ComponentName}'（未注册——隔离：该组件跳过）");
                continue;
            }

            if (entry.Phase != CardComponentPhase.Load)
            {
                continue; // 构造期项已在构造期执行
            }

            try
            {
                await entry.Loader(this, definition, context);
                consumed.Add(definition.GetType());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteComponentIsolation(
                    $"组件 '{definition.ComponentName}' 装配失败（隔离：该组件跳过）：{ex.Message}");
            }
        }
    }

    /// <summary>按组件类型名在本卡组件集内查找定义（未含＝null）。</summary>
    private ICardDataComponentDefinition? FindComponent(string componentName)
    {
        foreach (var definition in Definition.Components)
        {
            if (string.Equals(definition.ComponentName, componentName, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        return null;
    }

    /// <summary>组件隔离留痕（引擎总流；对齐既有装载链隔离记录形态）。</summary>
    private void WriteComponentIsolation(string message)
    {
        _engine.RootStream.WriteLog(
            "组件装载",
            $"卡牌 '{Name}'：{message}",
            LogLevel.Error,
            new[] { "component", "error" });
    }



    /// <summary>
    /// 效果装载（X2；加载时＝与 card.load 同时完成「声明解析＋构造＋登记＋装载」）：
    /// 经效果装载链（<see cref="CardEffectLoader"/>）执行——声明校验与工厂解析 fail-fast（未知标识/工厂返回 null 上抛）、
    /// 构造执行异常隔离（跳过该效果）、被动效果装载（挂主触发器＋OnMount——含监听 handler 注册；失败回滚、不阻断）、
    /// 托管登记（卸载自动按来源撤销修饰器）；无装配源（独立构造）＝整链跳过、不抛错、加载不失败；无声明卡＝无副作用。
    /// </summary>
    private async Task LoadEffectsAsync(CancellationToken ct)
    {
        var context = EffectLoadContextProvider?.Invoke();
        await CardEffectLoader.LoadAsync(this, _engine, context, ct);
    }


    private static string DefinitionName(CardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Name;
    }
}
