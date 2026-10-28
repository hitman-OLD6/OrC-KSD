using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;
using Orc.Game.Cards.Loading;
using Orc.Game.Model;
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
/// 卡牌装载模板自身判定的失败（<see cref="CardLoadFailedException"/>，如效果 csx 编译不过）由装载链收下：
/// 写入卡牌加载报告的卡位/条目（<c>CardLoadReport.slots[]</c>／<c>items[].fields</c>，reason＝<c>scriptCompileFailed</c>）
/// 后再决定处置（卡组装载＝该卡不产生、其余照常）。
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
    /// 卡牌成员表（S8：成员清单由**类型预制体**声明，卡牌类只做绑定）。
    /// 构造期由子类经 <see cref="BindMembers"/> 填入；成员集合＝类型预制体的成员表（<c>card.base</c> → <c>card.unit</c> 等）。
    /// </summary>
    public CardMemberTable MemberTable { get; private set; } = null!;

    /// <summary>
    /// 绑定类型预制体并物化成员（构造期调用；唯一改动点 A＝各卡牌类的宿主种类）。
    /// 两条实例化路径（直接构造／<c>typeCategory</c>）共用同一张表，不一致＝装载期拒绝。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine 为 null。</exception>
    /// <exception cref="Orc.Core.Model.PrefabLoadException">绑定或成员物化失败。</exception>
    protected CardMemberTable BindMembers(
        LogicEngine engine, HostKind host, Func<string, JudicatorBinding>? validationJudicatorResolver)
    {
        ModelHostKind = host;
        MemberTable = CardTypeBinder.Bind(
            this, host, engine, Definition, validationJudicatorResolver);
        return MemberTable;
    }

    /// <summary>本卡的宿主种类（构造期由 <see cref="BindMembers"/> 写入；未绑定＝null）。</summary>
    internal HostKind? ModelHostKind { get; private set; }

    /// <summary>
    /// 本卡的编排实体（**卡牌 ↔ OrchestrationEntity 桥**；加载期创建，销毁时撤销）。
    /// <para>
    /// 实体承载本卡（<see cref="OrchestrationEntity.Bound"/>＝this）：卡数据里声明的效果＝该实体上的组件
    /// （A-3：一条声明＝一个组件实例，声明序＝装配序）。未装配编排运行时（独立构造／单测）＝null——纯加性，
    /// 旧执行面（<c>CardEffectLoader</c> 装载的 <c>Effect</c> 链）不受影响。
    /// </para>
    /// </summary>
    public OrchestrationEntity? ModelEntity { get; private set; }

    /// <summary>加载期创建承载实体（幂等；无运行时／无宿主种类＝null——不阻断加载）。</summary>
    private OrchestrationEntity? EnsureModelEntity()
    {
        if (ModelEntity is not null)
        {
            return ModelEntity;
        }

        var runtime = _engine.Model;
        if (runtime is null || ModelHostKind is not { } host)
        {
            return null;
        }

        try
        {
            // 实体名带对局内卡 id（同名卡可同时在场；审查面按名建节点时不会相互顶掉）。
            var entityName = MatchCardId is { } cardId ? $"{Name}#{cardId}" : Name;
            var entity = runtime.CreateEntity(CardTypeBinder.PrefabIdFor(host), entityName);
            entity.Bound = this; // 卡牌↔实体桥的绑定点（R1h/R3d：实体销毁不回写卡；由卡销毁驱动实体销毁）
            ModelEntity = entity;
            FlushPendingEdges(entity);
            return entity;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or Orc.Core.Model.PrefabLoadException or InvalidOperationException)
        {
            // 装配性错误只留痕、不阻断卡加载（模型面是加性观测面，旧执行面仍是唯一行为源）。
            _engine.RootStream.WriteLog(
                "编排实体",
                $"卡牌 '{Name}' 的编排实体创建失败（隔离：该卡无实体）：{ex.Message}",
                LogLevel.Error,
                new[] { "model", "entity", "error" });
            return null;
        }
    }

    /// <summary>
    /// 加载期装配本卡的效果组件（v4 唯一路径）。
    /// <para>
    /// 两条来源，装载面一致、**互不重叠**（同一 prefab id 只装一次）：
    /// </para>
    /// <list type="number">
    /// <item>数据面直引：<c>effects.prefabs</c> 里的 id → 编排目录（<c>runtime.Catalog</c>）按 id 直取；</item>
    /// <item>代码声明面：<c>CardEffectRegistry.DeclarePrefab</c> 声明的 id → 目录无该 id 时取
    ///   <see cref="Orc.Cards.SnapshotStore"/> 里的旧效果快照，经 <c>LegacySnapshotComponentBinder</c> 现场转成
    ///   v4 组件后装配（零新序列化：复用落盘文本 ＋ 资产迁移器）。</item>
    /// </list>
    /// <para>
    /// 单条失败＝隔离留痕、跳过，不阻断卡加载；旧链已不存在，**不再有回退**——无法承载的旧来源
    /// （<c>assemblyKey</c> 成员）一律写 Error 日志并跳过该条。
    /// </para>
    /// <para>
    /// **例外（§33）**：csx 在承载视图下**编译不过**＝该效果永远不可能生效 ⇒ 判**卡牌加载失败**
    /// （<see cref="CardLoadFailedException"/>），不按隔离处理——否则症状（效果没发生）与原因（编译失败）隔两层。
    /// </para>
    /// </summary>
    private (int Declared, int Mounted) LoadModelComponents()
    {
        if (ModelEntity is not { } entity)
        {
            return (0, 0);
        }

        var declared = 0;
        var mounted = 0;
        var mountedIds = new HashSet<string>(StringComparer.Ordinal);

        // ① 数据面直引（effects.prefabs → 编排目录）。
        foreach (var definition in Definition.Components)
        {
            if (definition is not EffectsDefinition effects)
            {
                continue;
            }

            foreach (var assetId in effects.Prefabs)
            {
                if (!mountedIds.Add(assetId))
                {
                    continue; // 同一 id 重复声明：只装一次（幂等）
                }

                declared++;
                if (MountComponent(entity, assetId, assetId))
                {
                    mounted++;
                }
            }
        }

        // ② 代码声明面（DeclarePrefab）：目录里没有的 id 走"旧快照 → v4 组件"。
        var context = EffectLoadContextProvider?.Invoke();
        if (context is not null)
        {
            foreach (var prefabId in context.PrefabDeclarations)
            {
                if (!mountedIds.Add(prefabId))
                {
                    continue; // 数据面已装（或声明内重复）：不重复装
                }

                declared++;
                if (MountDeclaredComponent(entity, prefabId))
                {
                    mounted++;
                }
            }
        }

        return (declared, mounted);
    }

    /// <summary>按目录 id 装配一个组件（失败＝隔离留痕；不抛）。</summary>
    private bool MountComponent(OrchestrationEntity entity, string assetId, string instanceName)
    {
        var runtime = entity.Runtime;
        if (!runtime.Catalog.Contains(assetId))
        {
            _engine.RootStream.WriteLog(
                "编排组件",
                $"卡牌 '{Name}' 的组件 '{assetId}' 不在编排目录中（隔离：该组件不生效）。",
                LogLevel.Error,
                new[] { "model", "component", "error", assetId });
            return false;
        }

        try
        {
            runtime.AddComponent(entity, assetId, instanceName);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _engine.RootStream.WriteLog(
                "编排组件",
                $"卡牌 '{Name}' 的组件 '{assetId}' 装配失败（隔离：该组件不生效）：{ex.Message}",
                LogLevel.Error,
                new[] { "model", "component", "error", assetId });
            return false;
        }
    }

    /// <summary>
    /// 按声明 id 装配一个组件：目录已含＝直取；否则取旧快照转 v4 组件。
    /// 旧来源无法转（assemblyKey／转换期其他异常）＝Error 留痕并跳过，**不回退旧链**（旧链已删除）；
    /// csx 编译不过＝**抛出**（判卡牌加载失败——见 <see cref="LoadModelComponents"/>）。
    /// </summary>
    /// <exception cref="CardLoadFailedException">效果 csx 在承载视图下编译不过（卡牌加载失败）。</exception>
    private bool MountDeclaredComponent(OrchestrationEntity entity, string prefabId)
    {
        var runtime = entity.Runtime;
        if (runtime.Catalog.Contains(prefabId))
        {
            return MountComponent(entity, prefabId, prefabId);
        }

        if (!_engine.Prefabs.TryGetPrefab(prefabId, out var snapshot))
        {
            _engine.RootStream.WriteLog(
                "编排组件",
                $"卡牌 '{Name}' 声明的效果 '{prefabId}' 既不在编排目录、也没有效果快照（隔离：该效果不生效）。",
                LogLevel.Error,
                new[] { "model", "component", "error", prefabId });
            return false;
        }

        string assetId;
        try
        {
            assetId = LegacySnapshotComponentBinder.Register(
                runtime, snapshot, _engine.ScriptEvaluator, _engine.SyncScriptEvaluator);
        }
        catch (LegacySnapshotComponentBinder.LegacyScriptUnsupportedException ex)
        {
            // csx 在 v4 承载视图（dynamic）下**编译不过**：该效果永远不可能生效（旧链已删、无回退可走）。
            // 这不是"少一条可选效果"——卡的能力已经不可信 ⇒ **判卡牌加载失败**（§33，用户口径）。
            // 留痕进卡牌加载报告（由装载链补卡库键、宿主回填报告），并上抛：处置（摘除该卡／整局不开）归装载链。
            _engine.RootStream.WriteLog(
                "编排组件",
                $"卡牌 '{Name}' 的效果 '{prefabId}' 的 csx 在承载视图下编译失败（卡牌加载失败）：{ex.Message}",
                LogLevel.Error,
                new[] { "model", "component", "script-compile", prefabId });
            throw new CardLoadFailedException(
                Name, MatchCardId, prefabId, CardIssueReason.ScriptCompileFailed, ex.Message, ex);
        }
        catch (LegacySnapshotComponentBinder.LegacyAssemblyKeyNotSupportedException ex)
        {
            // assemblyKey 成员（C# 委托来源）不在 v4 覆盖面内：暂按原口径隔离（用户口径只点名"编译不过"）。
            // 旧链已删除，无回退可走——明确拒绝 + 日志（不静默）。
            _engine.RootStream.WriteLog(
                "编排组件",
                $"卡牌 '{Name}' 的效果 '{prefabId}' 是旧来源，v4 不承载（已跳过——需改写成 csx）：{ex.Message}",
                LogLevel.Error,
                new[] { "model", "component", "legacy-unsupported", prefabId });
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _engine.RootStream.WriteLog(
                "编排组件",
                $"卡牌 '{Name}' 的效果 '{prefabId}' 转 v4 装配失败（隔离：该效果不生效）：{ex.Message}",
                LogLevel.Error,
                new[] { "model", "component", "error", prefabId });
            return false;
        }

        return MountComponent(entity, assetId, prefabId);
    }

    /// <summary>销毁：先撤销本卡的编排实体（卸载组件 → 撤销它创建的边与包装），再走既有卡销毁链。</summary>
    public override void Destroy()
    {
        var entity = ModelEntity;
        ModelEntity = null;
        if (entity is not null)
        {
            entity.Bound = null;
            entity.Destroy();
        }

        base.Destroy();
    }

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

    /// <summary>待接入的成员边（构造期登记、实体创建后落到宿主实体上；顺序＝接入序）。</summary>
    private readonly List<(string Member, OwnedEdge Edge)> _pendingEdges = new();

    /// <summary>
    /// 给本卡宿主的某成员接一条**自有边**（构造期调用；实体尚未创建时先挂起，创建后按序落表）。
    /// <para>
    /// 位次＝该成员声明的自有边组内（声明序 → 接入序）；卡的默认链就是自有边，
    /// 因此"守卫边先、链后"由接入顺序表达，与旧 <c>Register</c> 的注册序同构。
    /// </para>
    /// </summary>
    /// <param name="memberName">成员名（<see cref="Orc.Game.Model.CardProcesses"/> 常量）。</param>
    /// <param name="edge">边体。</param>
    /// <exception cref="ArgumentNullException">memberName／edge 为 null。</exception>
    protected internal void AttachMemberEdge(string memberName, OwnedEdge edge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(edge);

        // 构造期就声明驱动：未声明成员＝立即失败（不要等到打出的那一刻才发现拼错）。
        _ = MemberTable.Member(memberName);

        if (ModelEntity is { } entity)
        {
            entity.AttachOwnedEdge(memberName, edge);
            return;
        }

        _pendingEdges.Add((memberName, edge));
    }

    /// <summary>把挂起的边落到刚创建的宿主实体上（幂等：清空挂起表）。</summary>
    private void FlushPendingEdges(OrchestrationEntity entity)
    {
        foreach (var (member, edge) in _pendingEdges)
        {
            entity.AttachOwnedEdge(member, edge);
        }

        _pendingEdges.Clear();
    }

    /// <summary>
    /// 执行本卡宿主的某成员（<c>host#&lt;成员&gt;</c>）：数据面沿调用方上下文（写回可读）。
    /// </summary>
    /// <param name="memberName">成员名（须由类型预制体声明）。</param>
    /// <param name="ctx">调用方上下文（同一份数据面）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">本卡没有编排实体（未加载／引擎未装配编排运行时）。</exception>
    public Task<MemberExecutionResult> InvokeMemberAsync(
        string memberName, Context ctx, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(ctx);

        if (ModelEntity is null || _engine.Model is not { } runtime)
        {
            throw new InvalidOperationException(
                $"卡牌 '{Name}' 没有编排实体（未加载或引擎未装配编排运行时）：成员 '{memberName}' 无法执行。");
        }

        return runtime.RunMemberAsync(ModelEntity, memberName, ctx, _engine, ct);
    }

    /// <summary>成员的宿主地址（<c>host#&lt;成员名&gt;</c>）。</summary>
    /// <param name="memberName">成员名。</param>
    public MemberAddress AddressOf(string memberName) => MemberTable.AddressOf(memberName);

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
    /// <exception cref="CardLoadFailedException">卡牌装载模板判定该卡加载失败（§33：效果 csx 编译不过等）。</exception>
    public async Task LoadAsync(Player owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        Owner = owner;
        LoadMatchCardId();
        EnsureModelEntity(); // ① 卡牌↔实体桥（无运行时＝null，不阻断）
        await LoadComponentPhaseAsync(ct); // 两段式：第一段＝内置注册序；第二段＝扩展／社区组件按数据体声明序
        LoadModelComponents(); // ② 效果组件（数据面直引 ＋ 代码声明面；唯一装配路径）
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



    private static string DefinitionName(CardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Name;
    }
}
