using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Collections;
using Orc.Game.Commanding;
using Orc.Game.Damage;
using Orc.Game.Effects;
using Orc.Game.Judicators;
using Orc.Game.Managers;
using Orc.Game.Model;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;

namespace Orc.Game;

/// <summary>
/// 对局（KARDS 模仿；第一批对局骨架＋2A 结构层＋2B 打出链＋2C 指挥与词条＋后置项补全）：持有逻辑引擎、双玩家、回合序、战场、管理器群（回合 / 玩家 / 战场 / 资源 / 卡牌库 / 目标选择 / 打出 / 指挥）
/// 与对局级触发器注册表（2A 机制：登记本体＋分层分类；2C 起内置流程触发器注册为底层）。
/// 装配模式＝调用方提供数据、Match 负责装配：创建输入＝双方卡组名单（CardList×2）、卡牌定义集、可选种子（可复现；G8：确定性随机服务的显式化入口——对局内随机消费统一经随机服务）、可选先手指定（默认第一位玩家）、可选规则配置（指挥点上限／起手张数／首回合抽牌）、可选目标选择桥接（第六员装配输入）、可选效果工厂注册表（X2 加性装配输入——卡牌加载时效果装载的装配源）、可选部署逻辑注册表（A4 加性装配输入——卡牌加载时「部署逻辑生成」步骤的装配源）、可选玩家构筑配置（S10 加性装配输入——主国/盟国；按玩家分别提供、提供即校验、初始化期注入）、可选判定器装配段（J1 加性装配输入、J2：外部段＝追加/定制通道；对局装配期一次性执行判定器注册〔注册表创建之后、卡加载之前〕、内置注册段同时固定执行〔默认验证判定器——无条件可用〕；缺省＝null＝无外部追加〔内置验证判定器恒在〕）。
/// 两步式：创建（准备态）→ 显式 <see cref="Initialize"/>（初始化完成置"进行"态）→〔HQ≤0 时〕"结束"态（立即终局：状态置结束＋胜者记录）。
/// 状态门禁：回合推进仅"进行"态允许；准备态访问管理器与转发属性抛错；重复 <see cref="Initialize"/> 抛错（明确拒绝、非幂等）；
/// 终局后（"结束"态）：所有游戏动作入口拒绝（指挥/打出/移动/攻击/回合推进/初始化等——对外面拒绝、零副作用）、
/// 只读查询面（状态/胜者/玩家与 HQ/战场/管理器/集合）保持可用、更新流不再增长（其后所有效果不再处理）。
/// 失败模式：无效创建参数 → 创建期抛参数校验异常；初始化中异常直接传播（不承诺回滚；失败可重建对局）。
/// 初始化流程（2A 固定链＋2C 加性＋W1-1 加性＋W4-1 加性＋A4 加性＋S10 加性＋J1 加性）：管理器群（卡牌库批量注册 → 资源 → 玩家 → 战场〔构造期 HQ 占位〕→ 判定器注册〔J1：注册表创建＋装配期注册段；J2：内置注册段（默认验证判定器）固定执行＋外部追加段——卡加载前就绪〕→ 再触发服务〔A4：接收触发器创建＋总线挂载〕→ 指挥管理器〔2C：流程触发器创建＋底层注册；A4：注入再触发服务〕→ 玩家注入（环境/随机服务/A4 再触发服务/S9 卡牌服务/S10 构筑配置与历史服务））→
/// 双方卡组洗切（W4-1：经统一洗切动作面 <see cref="ShuffleDeckAsync"/>——传对局随机服务〔G8：确定性单流〕、各发一条 deck.shuffled 信号）→ 加载（逐张 card.load；含对局级 ID 分配与元数据装配〔W1-1〕、部署逻辑生成〔A4〕、词条装载〔2C〕；A 组后 B 组、组内洗牌后顺序）→
/// ID 水位线快照〔W1-1：起手装载之前〕→ 起手装载（静默、不发更新；先手 4 / 后手 5——缺省，经 MatchOptions 配置）→
/// 〔2C 接线：回合恢复钩子〕→ 先手回合开始序列（3 条更新入总流）→ 置"进行"。
/// </summary>
public sealed class Match
{
    private CardList _deckForPlayerA;
    private CardList _deckForPlayerB;
    private readonly IReadOnlyList<CardDefinitionEntry> _cardDefinitions;
    private readonly MatchRandomService _randomService;
    private readonly int _firstPlayerIndex;
    private readonly MatchOptions _options;
    private readonly IMatchCardLoader? _cardLoader; // S7：对局卡牌加载器端口（null＝代码内定义集直注册——测试/独立装配路径）

    private TurnManager? _turnManager;
    private PlayerManager? _playerManager;
    private BattlefieldManager? _battlefieldManager;
    private ResourceManager? _resourceManager;
    private CardLibrary? _cardLibrary;
    private TargeterManager? _targeterManager;
    private PlayManager? _playManager;
    private CommandManager? _commandManager;
    private RetriggerSystem? _retriggerSystem; // A4：再触发服务（对局装配期创建——收束于管理器群）
    private JudicatorRegistry? _judicators; // J1：判定器注册表（对局级机制——初始化内创建；卡加载前就绪）
    private readonly ITargeterBridge? _targeterBridge;
    private readonly CardEffectRegistry? _effectRegistry;
    private readonly PlayerDeckConfiguration? _deckConfigForPlayerA; // S10：玩家A 构筑配置（主国/盟国——创建期接收、初始化注入）
    private readonly PlayerDeckConfiguration? _deckConfigForPlayerB; // S10：玩家B 构筑配置
    private readonly Action<JudicatorRegistry>? _judicatorAssembly; // J1：判定器装配段（对局装配期一次性执行判定器注册——注册表创建之后、卡加载之前）
    private readonly MatchLifecycle _lifecycle = new();
    private MulliganManager? _mulliganManager; // A1：换牌管理器（换牌相位承载——Initialize 内创建）
    private GameEnvironment? _environment; // W3-1 G4：游戏环境（对局装配期创建；多对局相互独立）
    private MatchCardService? _cardService; // S9：对局卡牌服务（服务面——卡牌工厂＋放置面；对局装配期创建）
    private MatchHistoryService? _historyService; // S10：对局历史读取服务（最小历史读取面；对局装配期创建）
    private Sandbox.SandboxPrimitives? _sandbox; // 沙盒调试原语族（引擎保留触发器；授权时装配）
    private readonly bool _sandboxAuthorized; // 沙盒授权事实（装配方构造期决定；缺省未授权）
    private EffectRuntime? _effectRuntime; // E1：效果运行时门面（csx 唯一游戏层受控入口；对局装配期创建）
    private AssetServiceRegistry? _assetServices; // 资产化 S12c：资产服务注册表（对局级；与判定器注册表并列）
    private readonly Orc.Game.Plugins.IMatchPackageActivator? _packageActivator; // 资产化 S12c：对局包激活器（快照 → 对局级注册面；null＝空目录）

    /// <summary>
    /// 创建对局（准备态；内部新建 LogicEngine 并公开）。装配校验：卡组名单非 null、非空、不含 null/空白 id；
    /// 先手指定为 0/1；指挥点上限为正整数；定义集允许为空（空库）。"未注册 id"不在创建期校验（初始化实例化时抛错）。
    /// </summary>
    /// <param name="deckForPlayerA">玩家A 的卡组名单（第一位玩家）。</param>
    /// <param name="deckForPlayerB">玩家B 的卡组名单。</param>
    /// <param name="cardDefinitions">卡牌定义集（条目＝id＋定义；初始化时批量注册）。</param>
    /// <param name="seed">可选随机种子（默认自动生成且事后经 <see cref="Seed"/> 可读）。</param>
    /// <param name="firstPlayerIndex">可选先手指定（0＝玩家A、1＝玩家B；默认第一位玩家）。</param>
    /// <param name="options">可选规则配置（指挥点上限／起手张数／首回合抽牌；缺省＝12／4／5／不抽——保持现行行为）。</param>
    /// <param name="targeterBridge">可选目标选择桥接（第六员〔目标选择管理器〕的装配输入；缺省＝null＝允许无桥接装配——Targeting 被调用时以失败结局暴露、不抛）。</param>
    /// <param name="effectRegistry">可选效果工厂注册表（X2 加性装配输入；卡牌加载时效果装载的装配源；缺省＝null＝无效果源——卡加载时无「声明效果」装载，装载语境照常〔卡上手动装配的效果仍被装载〕）。</param>
    /// <param name="deploymentLogicRegistry">可选部署逻辑注册表（A4 加性装配输入；卡牌加载时「部署逻辑生成」步骤的装配源〔为单位卡生成并挂载部署逻辑组件〕；缺省＝null＝无装配源——不生成、加载照常）。</param>
    /// <param name="deckConfigForPlayerA">可选玩家A 构筑配置（S10「G12补」加性装配输入——主国/盟国；提供即校验〔创建期 fail-fast：成对、值域〕、初始化期注入；缺省＝null＝未配置〔读面 null、不抛错〕）。</param>
    /// <param name="deckConfigForPlayerB">可选玩家B 构筑配置（同 deckConfigForPlayerA——按玩家分别提供）。</param>
    /// <param name="judicatorAssembly">可选判定器装配段（J1 加性装配输入；J2：外部段＝追加/定制通道——内置注册段〔默认验证判定器〕固定无条件执行）；对局装配期一次性调用〔注册表创建之后、卡加载之前〕——执行判定器注册、注册动作返回条目等价句柄供持用；缺省＝null＝无外部追加〔内置验证判定器恒在、可达面照常〕）。</param>
    /// <param name="cardLoader">
    /// 可选**卡牌加载器端口**（S7 接线输入；由宿主提供实现——`Orc.Packages` 的卡组装载：
    /// 资产加载器产出分层列表 → 卡牌加载器整批装载 → 合法卡进卡库）。
    /// 提供时：初始化期先整批装载双方牌堆（**不分配实例 ID**）、非法卡位留空，卡库由装载链写入（含资产域）；
    /// 缺省＝null＝沿用代码内定义集直注册（测试/独立装配路径）。
    /// </param>
    /// <exception cref="ArgumentNullException">deckForPlayerA / deckForPlayerB / cardDefinitions 为 null。</exception>
    /// <exception cref="ArgumentException">卡组名单为空或含 null/空白 id；定义集含 null 条目；构筑配置成对缺失（只提供其一）。</exception>
    /// <exception cref="ArgumentOutOfRangeException">先手指定越界；指挥点上限非正整数；起手张数为负；构筑配置值非法（未定义枚举值 / 主国非五主国 / 盟国==主国 / 盟国==Neutral）。</exception>
    public Match(
        CardList deckForPlayerA,
        CardList deckForPlayerB,
        IEnumerable<CardDefinitionEntry> cardDefinitions,
        int? seed = null,
        int? firstPlayerIndex = null,
        MatchOptions? options = null,
        ITargeterBridge? targeterBridge = null,
        CardEffectRegistry? effectRegistry = null,
        PlayerDeckConfiguration? deckConfigForPlayerA = null,
        PlayerDeckConfiguration? deckConfigForPlayerB = null,
        Action<JudicatorRegistry>? judicatorAssembly = null,
        Orc.Game.Plugins.IMatchPackageActivator? packageActivator = null,
        IMatchCardLoader? cardLoader = null)
    {
        ArgumentNullException.ThrowIfNull(deckForPlayerA);
        ArgumentNullException.ThrowIfNull(deckForPlayerB);
        ArgumentNullException.ThrowIfNull(cardDefinitions);

        if (deckForPlayerA.Count == 0)
        {
            throw new ArgumentException("玩家A的卡组名单为空（空名单在创建期被拒绝）。", nameof(deckForPlayerA));
        }

        if (deckForPlayerB.Count == 0)
        {
            throw new ArgumentException("玩家B的卡组名单为空（空名单在创建期被拒绝）。", nameof(deckForPlayerB));
        }

        ValidateDeckIds(deckForPlayerA, nameof(deckForPlayerA));
        ValidateDeckIds(deckForPlayerB, nameof(deckForPlayerB));

        var first = firstPlayerIndex ?? 0;
        if (first is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(firstPlayerIndex), firstPlayerIndex, "先手指定非法（须为 0＝玩家A 或 1＝玩家B）。");
        }

        var resolvedOptions = options ?? new MatchOptions();
        if (resolvedOptions.MaxPointSlots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.MaxPointSlots, "指挥点上限须为正整数（≥1）。");
        }

        // K4 加性（开局常量配置化）：起手张数（0 合法＝空起手；负数创建期 fail-fast——同 MaxPointSlots 先例）。
        if (resolvedOptions.OpeningHandSizeFirstPlayer < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.OpeningHandSizeFirstPlayer,
                "先手起手张数不能为负（0 合法＝空起手）。");
        }

        if (resolvedOptions.OpeningHandSizeSecondPlayer < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.OpeningHandSizeSecondPlayer,
                "后手起手张数不能为负（0 合法＝空起手）。");
        }

        var definitions = cardDefinitions.ToList();
        if (definitions.Any(entry => entry is null))
        {
            throw new ArgumentException("卡牌定义集含 null 条目。", nameof(cardDefinitions));
        }

        // S10「G12补」：玩家构筑配置校验（提供即校验、全部 fail-fast；未提供＝跳过全部校验、不抛错）。
        ValidateDeckConfiguration(deckConfigForPlayerA, nameof(deckConfigForPlayerA));
        ValidateDeckConfiguration(deckConfigForPlayerB, nameof(deckConfigForPlayerB));

        _deckForPlayerA = deckForPlayerA;
        _deckForPlayerB = deckForPlayerB;
        _cardDefinitions = definitions;
        Seed = seed ?? Random.Shared.Next(); // 创建期一次性种子生成（未显式传入时）——不构成对局内随机消费（对局内统一经随机服务）
        _randomService = new MatchRandomService(Seed, () => _lifecycle.State);
        _firstPlayerIndex = first;
        _options = resolvedOptions;
        _targeterBridge = targeterBridge;
        _effectRegistry = effectRegistry;
        _deckConfigForPlayerA = deckConfigForPlayerA;
        _deckConfigForPlayerB = deckConfigForPlayerB;
        _judicatorAssembly = judicatorAssembly;
        _packageActivator = packageActivator;
        _cardLoader = cardLoader;
        _sandboxAuthorized = resolvedOptions.SandboxAuthorized;
        Engine = new LogicEngine();

        // 批次 2（S3／S7）：编排引擎实体——对局级**单例**（R6b），信号＝引擎实体的成员触发器。
        // 批次 3：该运行时同时挂到引擎的审查面（S10 的实体—成员—挂载边图以此为数据源）；
        // 生产发射仍走旧总线直到批次 4（共存边界见 docs/引擎重构-编排实体化/PROGRESS.md）。
        EngineRuntime = GameEnginePrefabs.CreateRuntime();
        Engine.Model = EngineRuntime;

        // E1-34（DSL 可运行）：默认装配脚本求值器——csx 处理器（效果解析器产物）需要它才能编译执行。
        // 批次 4（S14）：**提前到构造期**——装载门要求 csx 在装载期全量预编译（S5 第 9 条），
        // 而效果库目录的装载发生在 Initialize 之前（卡加载链解析效果引用所必需）。
        // 内核零依赖口径不变（接口倒置），实现由 satellite（Orc.Script）提供；宿主已装配＝不覆盖。
        Engine.ScriptEvaluator ??= new Orc.Script.CSharpScriptEvaluator();

        // handler DSL（S8／R28）：把「编译器 ＋ 目录」装到引擎上——含 DSL 成员的草案在装载期现场生成 csx。
        // 未装配＝能力缺失（仅含 DSL 成员的草案受影响，其余资产零行为变化）；宿主已装配＝不覆盖。
        Engine.HandlerDsl ??= Orc.Game.Effects.HandlerDslAssembly.Create();

        // A2（决策门 A）：内建词条行为制品＝v4 效果组件（Cards/KeywordPrefabs/*.component.json）——
        // 构造期装进编排运行时目录：①词条效果绑定核验（Initialize 的装配段）以目录为判据；②授予链的
        // v4 首选路径（KeywordManager.TryAttachComponent）同以目录为判据；两者都在 Initialize 之前就位。
        KeywordComponentLibrary.LoadInto(EngineRuntime, Engine.ScriptEvaluator);
    }

    /// <summary>对局引擎（公开；外部经此访问总线/总流以订阅更新——订阅须在 <see cref="Initialize"/> 前挂接）。</summary>
    public LogicEngine Engine { get; }

    /// <summary>
    /// **组件失败出口**（装配方注入；null＝无报告面）：卡级装载失败（如效果 csx 在 v4 承载视图下编译不过）
    /// 发生时，把定位信息交给宿主——宿主负责回填卡牌加载报告（<c>CardLoadReport</c> 在 <c>Orc.Packages</c>，
    /// 游戏层不引用它，故只能经这个缝交出去）。
    /// <para>
    /// 时序：须在 <see cref="Initialize"/> **之前**设置（装载发生在初始化内）；粒度＝一次装载批量一次。
    /// </para>
    /// </summary>
    public Action<IReadOnlyList<CardComponentFailureData>>? ComponentFailureSink { get; set; }

    /// <summary>
    /// 编排运行时（新模型的引擎实体；对局级单例，R6b）：<c>engine</c> 根下合并内核 4 个信号、
    /// 游戏层 35 个信号与 9 个引擎流程成员（S3／S7）。
    /// <para>
    /// **A2 起它是生产执行面**：<see cref="Engine"/> 的 <c>Emit</c>／<c>Mount</c> 全部经它——
    /// 发射＝对 <c>engine#&lt;信号&gt;</c> 成员的调用，订阅＝该成员上的边（<c>Orc.Core.SignalBridge</c>）。
    /// 总线门面已在 b5 删除（详见 PROGRESS.md §十八）——这里只剩一条执行路。
    /// </para>
    /// </summary>
    public Orc.Core.Model.OrchestrationRuntime EngineRuntime { get; }

    /// <summary>对局状态（准备 / 换牌 / 进行 / 结束——经生命周期对象读；"结束"＝HQ≤0 或认输立即终局，不可逆）。</summary>
    public MatchState State => _lifecycle.State;

    /// <summary>
    /// 对局相位（R1 公开读面；**派生投影**——单一真源＝<see cref="State"/>，不独立存储以避免双真源）：
    /// 换牌／进行／结束；准备态读取＝抛错（未就绪——沿用管理器门禁模式）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未就绪（准备态——须先成功完成 Initialize）。</exception>
    public MatchPhase Phase => State switch
    {
        MatchState.Mulligan => MatchPhase.Mulligan,
        MatchState.InProgress => MatchPhase.Play,
        MatchState.Ended => MatchPhase.Ended,
        _ => throw new InvalidOperationException("对局尚未就绪：相位不可用（须先成功完成 Initialize）。"),
    };

    /// <summary>
    /// 胜者（只读面；仅终局后非 null：＝使对方 HQ 归零的一方；非终局＝null）。
    /// 平局不处理（本批无同时归零路径、不定义平局值）。
    /// </summary>
    public Player? Winner => _lifecycle.Winner;

    /// <summary>终局原因（只读面；仅终局后非 null——HQ 归零／认输，与胜者并列记录）。</summary>
    public MatchEndReason? EndReason => _lifecycle.EndReason;

    /// <summary>本次实际使用的随机种子（传入则＝传入值；未传入则＝自动生成值，支撑事后复现）。</summary>
    public int Seed { get; }

    // ---------- 管理器群（五管理器公开；仅"进行"态可访问） ----------

    /// <summary>回合管理器（回合数、当前行动方真源）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public TurnManager TurnManager => RequireReady(_turnManager);

    /// <summary>玩家管理器（双玩家真源）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public PlayerManager PlayerManager => RequireReady(_playerManager);

    /// <summary>战场管理器（战场真源）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public BattlefieldManager BattlefieldManager => RequireReady(_battlefieldManager);

    /// <summary>资源管理器（指挥点结算规则服务）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public ResourceManager ResourceManager => RequireReady(_resourceManager);

    /// <summary>卡牌库（id → 定义注册表）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public CardLibrary CardLibrary => RequireReady(_cardLibrary);

    /// <summary>
    /// 目标选择管理器（管理器群"第六员"；随管理器群在 Initialize 内加性生成）。
    /// 独立构造路径（Manager 直建）不受影响、行为一致；未 Initialize 时经本属性访问＝沿用既有门禁模式（抛错）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public TargeterManager TargeterManager => RequireReady(_targeterManager);

    /// <summary>
    /// 打出管理器（2B 加性：打出链服务与交互入口——单位预打出 / 单位打出链 / 加入 / 指令 / 反制；
    /// 随管理器群在 Initialize 内加性生成；回合管理器为其反制「仅己方回合」的真源）。
    /// 独立构造路径（Manager 直建）不受影响（回合上下文可缺省、反制使用将抛明确异常）；
    /// 未 Initialize 时经本属性访问＝沿用既有门禁模式（抛错）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public PlayManager PlayManager => RequireReady(_playManager);

    /// <summary>
    /// 换牌管理器（A1；开局换牌相位的承载）：换牌交互与"该方是否已确认"读面；
    /// 随管理器群在 Initialize 内加性生成；准备态访问＝抛错（沿用管理器门禁模式）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入就绪相位。</exception>
    public MulliganManager MulliganManager => RequireReady(_mulliganManager);

    /// <summary>
    /// 指挥管理器（2C 加性：指挥流程入口 / 动作可用性聚合判定 / 移动·攻击触发器 / 「造成攻击伤害」共享触发器 /
    /// 守护维护 / 回合恢复；随管理器群在 Initialize 内加性生成——创建时点为卡牌加载之前〔词条装载依赖〕；
    /// 其四个内置流程触发器在 Initialize 内注册为底层触发器〔进注册表、可查询〕）。
    /// 独立构造路径（Manager 直建）不受影响；未 Initialize 时经本属性访问＝沿用既有门禁模式（抛错）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public CommandManager CommandManager => RequireReady(_commandManager);

    /// <summary>
    /// 再触发服务（A4 加性：发布面＋接收触发器〔包装底层触发器〕＋执行面驱动＋重入防护；随管理器群在 Initialize 内
    /// 加性生成——创建时点为卡牌加载之前）。独立构造路径不受影响；未 Initialize 时经本属性访问＝沿用既有门禁模式（抛错）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public RetriggerSystem RetriggerSystem => RequireReady(_retriggerSystem);

    // ---------- 判定器注册表（对局级机制；J1） ----------

    /// <summary>
    /// 判定器注册表（对局级机制；J1 加性）：「对局加载时加载」——随对局装配链在 Initialize 内创建
    /// （装配期注册段经创建参数一次性执行；卡加载前就绪）；对局内可达面（供注册方与引用方取用——
    /// 注册动作返回条目等价句柄、按名解析所得为同一可寻址锚）；moding（逻辑替换）注入面（双形态）与
    /// 统一解析点（全局生效：按名调用与持有句柄的调用统一按栈顶解析）。
    /// 仅"进行"与"结束"态可读（准备态＝抛错——沿用管理器门禁模式）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public JudicatorRegistry Judicators => RequireReady(_judicators);

    /// <summary>效果运行时门面（csx 唯一游戏层受控入口；对局装配期创建）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public EffectRuntime EffectRuntime => RequireReady(_effectRuntime);

    /// <summary>A.20：可选前端状态提供器（同步、只读）。null＝无头引擎运行（原生规则照常）。</summary>
    public Func<Player, ActionInteractionState?>? InteractionStateReader { get; set; }

    /// <summary>A.20：行动输入准入查询（每次读取前端状态；前端限制只能**收窄**原生合法性）。</summary>
    public bool CanAcceptInput(Player player, Entity? actor = null, Entity? target = null)
        => CombatJudicatorInvoker.InvokeBool(
            Judicators.Resolve(JudicatorNames.ActionInput), player.Index, new object[] { player, actor!, target! });

    // ---------- 构筑外判定（W1-1 G12 加性面） ----------

    /// <summary>
    /// 构筑外判定（W1-1；访问口——转发玩家管理器判定）：卡牌对局级 ID ＞ 初始化水位线＝true（构筑外生成牌）；
    /// ≤＝false（初始化内卡）。判定为纯读、无副作用、不触发更新。
    /// 未分配 ID 的卡（未加载 / 独立构造）＝明确失败（拒绝——不静默返回 false）；水位线未快照＝明确失败。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="InvalidOperationException">card 未分配对局级 ID；或水位线尚未快照；或对局尚未进入"进行"态。</exception>
    public bool IsOutsideDeck(CardBase card) => PlayerManager.IsOutsideDeck(card);

    // ---------- 随机服务（第 2 批 G8） ----------

    /// <summary>
    /// 随机服务（对外取用面；第 2 批 G8 加性）：数值/取样原语（Next / PickOne / PickN——确定性 PRNG、
    /// 单流、顺序确定；效果运行期经「卡 → 玩家 → 服务」接入面取用）。**仅"进行"态可用**：准备态对外请求
    /// 与终局后取用＝明确拒绝（抛错——风格与既有管理器门禁对齐）；对局内部链路（初始化洗切等）不经本属性、
    /// 直接经服务本体消费（准备态合法）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态；或对局已结束（终局）。</exception>
    public MatchRandomService RandomService => State switch
    {
        MatchState.InProgress => _randomService,
        MatchState.Ended => throw new InvalidOperationException("对局已结束（终局），随机服务对外取用被拒绝。"),
        _ => throw new InvalidOperationException("对局尚未进入'进行'态：随机服务对外取用不可用（须先成功完成 Initialize）。"),
    };

    // ---------- 对局卡牌服务（S9 G6+G13） ----------

    /// <summary>
    /// 对局卡牌服务（对外取用面；S9 加性——「卡牌工厂＋放置面」：创建 Create / 放置 Place〔手牌/阵线/卡组顶/洗入〕
    /// / 相邻空槽解析 / 「创建并放置」复合操作；效果运行期经「卡 → 玩家 → 服务」接入面取用——服务操作自身
    /// 亦含「仅"进行"态」门禁）。**仅"进行"态可用**：准备态对外请求与终局后取用＝明确拒绝
    /// （抛错——风格与随机服务/既有管理器门禁对齐）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态；或对局已结束（终局）。</exception>
    public MatchCardService CardService => State switch
    {
        MatchState.InProgress => _cardService
            ?? throw new InvalidOperationException("对局卡牌服务尚未装配（初始化链时序错误——须先成功完成 Initialize）。"),
        MatchState.Ended => throw new InvalidOperationException("对局已结束（终局），卡牌服务对外取用被拒绝。"),
        _ => throw new InvalidOperationException("对局尚未进入'进行'态：卡牌服务对外取用不可用（须先成功完成 Initialize）。"),
    };

    /// <summary>
    /// 对局级**投影注册面**（前端资产迁移 S7）：插件在装配期注册 provider 工厂；
    /// 前端运行期以 artLookup 构造 provider 并**每帧**拉取整盘视图（运行期隔离：投影抛错保留上一帧）。
    /// </summary>
    public Orc.Game.Ui.UiProjectionRegistry UiProjections { get; } = new();

    // ---------- 对局历史读取服务（S10 G14补） ----------

    /// <summary>
    /// 对局历史读取服务（S10「G14补」加性面；最小历史读取面——按条目类型（信号）筛取＋时序取用；
    /// 读源＝事件流总流、读取窗口＝对局自创建以来全程；拉取式按需读取、空结果不抛错）。
    /// 仅"进行"与"结束"态可读（准备态＝抛错——沿用管理器门禁模式；终局后只读查询面保持可用）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public MatchHistoryService HistoryService => RequireReady(_historyService);

    // ---------- 沙盒调试原语（引擎保留触发器族） ----------

    /// <summary>
    /// 沙盒调试原语族（授权门禁承载）：管理器群建齐后装配——六原语各恰一个内置 handler、**构造完成即封闭**。
    /// 未授权实例下全部原语返回 <c>sandbox-not-authorized</c> 且零状态变更；授权只在沙盒装配路径开启
    /// （<see cref="MatchOptions.SandboxAuthorized"/>）。
    /// 主防线＝不可达：本属性只暴露原语入口方法，不暴露任何触发器实例、亦不进入按名解析面与预制体 <c>injects</c>。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态（须先成功完成 Initialize）。</exception>
    public Sandbox.SandboxPrimitives Sandbox => RequireReady(_sandbox);

    /// <summary>
    /// 诊断面（只读）：保留触发器导出（名／封闭态／授权／内置 handler 名）；未装配＝<c>[]</c>。
    /// 可观测、不可写——不构成任何注册或触发入口。
    /// </summary>
    public string ExportReservedTriggersJson() => _sandbox?.ExportReservedTriggersJson() ?? "[]";

    // ---------- 对局受控动作面（W4-1 G14 收尾） ----------

    /// <summary>
    /// 洗切动作（G14 收尾；对指定玩家卡组执行一次洗切＋发射 <see cref="GameUpdates.DeckShuffled"/> 信号——恰一次）；
    /// 「统一经此」的最小公共受控入口：初始化路径（双方卡组自动洗切）与后续「洗切卡组」类效果路径共用本动作面。
    /// 随机源＝对局随机服务（G8：确定性 PRNG、单流——与效果取样共流；同种子可复现）；发射时机＝洗切动作生效处（就地打乱之后）。
    /// 拒绝：player 为 null；玩家管理器尚未创建（未进入初始化）；指定玩家不属于本对局；对局已结束（终局后动作入口拒绝）。
    /// 初始化链内调用（"准备"态）＝合法（对局内部动作面）；对外调用仍受终局门禁。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="InvalidOperationException">玩家管理器尚未创建；或对局已结束（终局）。</exception>
    /// <exception cref="ArgumentException">指定玩家不属于本对局（洗切动作被拒绝）。</exception>
    public async Task ShuffleDeckAsync(Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var manager = _playerManager
            ?? throw new InvalidOperationException("玩家管理器尚未创建（洗切动作不可用——须先进入初始化）。");
        if (State == MatchState.Ended)
        {
            throw new InvalidOperationException("对局已结束（终局），洗切动作被拒绝。");
        }

        if (!manager.Players.Contains(player))
        {
            throw new ArgumentException("指定玩家不属于本对局（洗切动作被拒绝）。", nameof(player));
        }

        // 动作作用域（UI 消费桥接）：洗切动作产生的事件聚合为一段（被 Initialize 包载时合并入初始化大段）。
        await using var _actionScope = Engine.BeginAction();

        player.Deck.Shuffle(_randomService);
        await GameUpdates.EmitDeckShuffled(Engine, player, player.Deck, ct);
    }

    // ---------- 装配链内部访问面（准备态合法；仅供对局内部装配/加载链使用） ----------

    /// <summary>
    /// 双玩家（装配链内部面；准备态合法）：卡加载期的内部步骤（如词条程序的目标可选性声明挂载）
    /// 需要玩家索引，而对外 <see cref="Players"/> 受准备态门禁——内部另开通道，避免装配链自我锁死。
    /// 对外契约不变（<see cref="Players"/> 仍仅非准备态可用）。
    /// </summary>
    /// <exception cref="InvalidOperationException">玩家管理器尚未创建（装配链时序错误）。</exception>
    internal IReadOnlyList<Player> AssemblyPlayers
        => _playerManager?.Players
            ?? throw new InvalidOperationException("玩家管理器尚未创建（装配链内部访问时序错误）。");

    /// <summary>判定器注册表（装配链内部面；准备态合法——口径同 <see cref="AssemblyPlayers"/>）。</summary>
    /// <exception cref="InvalidOperationException">判定器注册表尚未创建（装配链时序错误）。</exception>
    internal JudicatorRegistry AssemblyJudicators
        => _judicators
            ?? throw new InvalidOperationException("判定器注册表尚未创建（装配链内部访问时序错误）。");

    /// <summary>
    /// 玩家管理器（装配链内部面；准备态合法）：S7 时序断言的读取面——
    /// 「ID 水位线快照仍在校牌加载之后原位执行」需要在对局仍处准备态时可读（对外 <see cref="PlayerManager"/> 受就绪门禁）。
    /// </summary>
    internal PlayerManager? AssemblyPlayerManager => _playerManager;

    // ---------- 便捷转发读面（只读、不复制状态；仅"进行"态可访问） ----------

    /// <summary>双玩家（→ 玩家管理器）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public IReadOnlyList<Player> Players => PlayerManager.Players;

    /// <summary>当前行动方（→ 回合管理器）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public Player CurrentPlayer => TurnManager.CurrentPlayer;

    /// <summary>回合数（→ 回合管理器）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public int TurnNumber => TurnManager.TurnNumber;

    /// <summary>战场（→ 战场管理器）。</summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public Battlefield Battlefield => BattlefieldManager.Battlefield;

    /// <summary>
    /// 游戏环境（W3-1 G4 加性面；对局装配期创建）：环境现算查询面（相邻/前线/战线定位——纯只读）＋
    /// 场级收集面（<see cref="Board.GameEnvironment.Auras"/>——光环声明注册/注销）＋全域重跑传播
    /// （环境类事件 → 全卡链重跑；事件驱动自动面＋<see cref="Board.GameEnvironment.RerunAllCardsAsync"/> 显式驱动面）。
    /// 仅"进行"与"结束"态可读（准备态＝抛错——沿用管理器门禁模式）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局尚未进入"进行"态。</exception>
    public GameEnvironment Environment => RequireReady(_environment);

    /// <summary>
    /// 初始化（两步式第二步；完成后置"进行"态）：管理器群（卡牌库批量注册〔S7：有卡牌加载器时＝整批装载双方牌堆〕→ 资源 → 玩家 → 战场〔构造期 HQ 占位〕→ 判定器注册〔J1 加性：注册表创建＋装配期注册段——卡加载前就绪〕→ 指挥管理器〔2C 加性：流程触发器创建＋底层注册〕）→
    /// 双方卡组洗切（W4-1：经 <see cref="ShuffleDeckAsync"/> 各发一条 deck.shuffled 信号）→ 加载（逐张 card.load；含对局级 ID 分配与元数据装配〔W1-1〕、词条装载〔2C〕）→ ID 水位线快照〔W1-1：起手装载之前〕→
    /// 起手装载（静默）→〔2C 接线：回合恢复钩子〕→ 先手回合开始序列（3 条更新）→ 置"进行"。初始化中异常直接传播（对局保持"准备"态）。
    /// </summary>
    /// <exception cref="InvalidOperationException">重复 Initialize（明确拒绝、非幂等）。</exception>
    public async Task Initialize(CancellationToken ct = default)
    {
        if (State != MatchState.Preparing)
        {
            throw new InvalidOperationException($"对局已初始化（当前状态：{State}）；重复 Initialize 被拒绝。");
        }

        // 动作作用域（UI 消费桥接）：初始化＝一次大动作；内部洗切/加载/起始回合等信号合并入本段。
        await using var _actionScope = Engine.BeginAction();

        // 生成并初始化管理器群（2B：卡牌库注入回合上下文提供器——反制「仅己方回合」验证的延迟读取来源；
        // 2C：加注词条装载上下文提供器——词条装载〔伏击效果〕的延迟读取来源；
        // W1-1：加注对局级卡牌 ID 提供器——加载时分配自增 ID〔构筑外判定基础〕的延迟读取来源；
        // X2：加注效果装载上下文提供器〔对局装载语境——效果源可空；注册表缺省时声明为空、装载照常〕；
        // A4：加注部署逻辑装载语境提供器〔装配源可空——缺省时条目为空、生成跳过〕；
        // lambda 延迟求值：加载期读取可能早于目标对象创建，运行期（使用）时已就绪）
        _cardLibrary = new CardLibrary(
            Engine,
            () => _turnManager?.CurrentPlayer,
            () => _commandManager?.KeywordLoadContext,
            () => _playerManager?.NextCardId()
                ?? throw new InvalidOperationException("卡牌 ID 分配不可用：玩家管理器尚未创建（加载链时序错误）。"),
            cardId => new CardEffectLoadContext(_effectRegistry, cardId),
            ResolveJudicatorBinding,
            // P4：反制卡一次性消耗的弃置委托——复用 PlayerManager.DiscardCardAsync（延迟读取：玩家管理器后置创建）。
            (cardPlayer, card, source, ct) => _playerManager!.DiscardCardAsync(cardPlayer, card, ct, source));

        // S7 接线：卡从哪来、是否合法（两条链路，实例化时序两者一致）
        // ① 卡牌加载器链路：资产加载器产出分层列表 → 卡牌加载器**整批**装载双方牌堆 → 合法卡进卡库（含资产域）。
        //    **硬约束**：整批装载只做解析／校验／DTO 生成，**不分配任何实例 ID**——实例化仍在 LoadDecksAsync 内原位进行
        //    （ID 水位线快照时机与顺序不变，见下方 SnapshotCardIdWatermark）。
        // ② 无加载器的独立装配路径（既有测试/工具）：代码内定义集直注册——语义与接线前逐字一致。
        if (_cardLoader is null)
        {
            foreach (var entry in _cardDefinitions)
            {
                _cardLibrary.Register(entry.Id, entry.Definition);
            }
        }
        else
        {
            await LoadDecksFromCardLoaderAsync(_cardLoader, ct);
        }

        // E1-25：资源管理器注入引擎（发槽信号）＋判定器延迟读取面（判定器注册表在本行之后创建——延迟求值）。
        _resourceManager = new ResourceManager(Engine, () => _judicators, _options.MaxPointSlots);
        _playerManager = new PlayerManager(Engine, _cardLibrary, () => _commandManager?.DrawAction);
        // 卡级失败出口：lambda 现读属性——宿主在 Initialize 前任意时刻设置均生效（含装配期后置注入）。
        _playerManager.ComponentFailureSink = failures => ComponentFailureSink?.Invoke(failures);
        _playerManager.CreatePlayers(_deckForPlayerA, _deckForPlayerB);
        // E1-25 后续：资源管理器注入各玩家（「卡 → 玩家 → 服务」路径——卡级扣费/退点经 ResourceManager.ResolveFor 取用）。
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureResourceManager(_resourceManager);
        }

        // 2A 受控变更：战场构造期含 HQ 占位（各支援线槽 2＝对应玩家），创建顺序随之为玩家先、战场后。
        _battlefieldManager = new BattlefieldManager(_playerManager.Players[0], _playerManager.Players[1]);

        // S10「G12补」加性：玩家构筑配置注入（主国/盟国读取面）——「调用方提供数据、对局负责装配」；
        // 时序：玩家注入段（与环境/随机服务注入同序、先于卡组洗切与加载）；未提供＝不注入（读面 null——缺省语义）。
        if (_deckConfigForPlayerA is not null)
        {
            _playerManager.Players[0].ConfigureDeckConfiguration(_deckConfigForPlayerA);
        }

        if (_deckConfigForPlayerB is not null)
        {
            _playerManager.Players[1].ConfigureDeckConfiguration(_deckConfigForPlayerB);
        }

        // W3-1 G4 加性：游戏环境（环境现算查询面＋场级收集面＋全域重跑传播）——随对局装配创建
        // （战场创建后、卡加载前），并注入各玩家（「卡 → 玩家 → 对局/战场」读取路径的玩家环节；
        // 时序：须先于卡加载——效果装载注册与链/条件环境读取依赖环境就绪）。
        _environment = new GameEnvironment(_battlefieldManager.Battlefield, Engine);
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureEnvironment(_environment);
        }

        // G8 加性：对局随机服务注入各玩家——「卡 → 玩家 → 服务」读取路径的玩家环节（效果运行期取用）；
        // 时序与装配一致（先于卡加载；显式、可测试——无隐藏全局单例）。
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureRandomService(_randomService);
        }

        // S9 加性：对局卡牌服务（服务面——卡牌工厂＋放置面）——随对局装配创建（战场/玩家就绪后，卡加载前）；
        // 动作面经延迟读取 lambda 注入（支援线查询〔战场就绪〕／阵线放置〔打出管理器创建较晚〕／洗切动作面〔统一经
        // ShuffleDeckAsync〕／状态读取器〔生命周期门禁〕）；并注入各玩家（「卡 → 玩家 → 服务」读取路径的玩家环节）。
        _cardService = new MatchCardService(
            Engine,
            _cardLibrary,
            player => _battlefieldManager?.Battlefield.GetSupportLine(player)
                ?? throw new InvalidOperationException("卡牌服务支援线查询不可用：战场管理器尚未创建（装配链时序错误）。"),
            (card, slot, ct) => _playManager?.JoinUnitAsync(card, slot, ct)
                ?? throw new InvalidOperationException("卡牌服务阵线放置不可用：打出管理器尚未创建（装配链时序错误）。"),
            (player, ct) => ShuffleDeckAsync(player, ct),
            () => _lifecycle.State,
            // 前端资产迁移 S4：运行期卡注册需登记效果声明（卡 → 预制体 id）。
            _effectRegistry,
            // S7：加入手牌动作面（对局装配＝指挥管理器持有的同一动作实例）。
            () => _commandManager?.AddToHandAction,
            // S7：局内新增卡的单卡装载面（对局装配＝同一卡牌加载器实例；null＝独立装配路径不设装载）。
            _cardLoader,
            // §34：运行期单卡创建同样是"卡级失败"来源——同一出口（宿主据此回填卡牌加载报告）。
            failures => ComponentFailureSink?.Invoke(failures));
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureCardService(_cardService);
        }

        // S10「G14补」加性：对局历史读取服务（最小历史读取面——按条目类型〔信号〕筛取＋时序取用；
        // 读源＝事件流总流、窗口＝对局自创建以来全程）——随对局装配创建并注入各玩家（「卡 → 玩家 → 服务」
        // 读取路径的玩家环节；时序：先于卡加载，显式、可测试——无隐藏全局单例）。
        _historyService = new MatchHistoryService(Engine);
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureHistoryService(_historyService);
        }

        // 第六员（加性，随管理器群生成）：目标选择管理器——桥接可选注入（缺省 null＝允许无桥接装配，
        // Targeting 被调用时以失败结局暴露、不抛）；留痕经引擎既有渠道（总流）。
        // 终局门禁（后置项 B）：装配终局读取提供器——对局已结束＝发起（新入队）即时失败、零副作用。
        _targeterManager = new TargeterManager(_targeterBridge, new EventStreamTargetingTrace(Engine.RootStream));
        _targeterManager.GameEndedProvider = () => _lifecycle.IsEnded;

        // C2 加性：目标选择管理器注入各玩家——「卡 → 玩家 → 服务」读取路径的玩家环节
        // （效果运行期经 TargeterManager.ResolveFor 取用——交互发起面；时序：先于卡加载，显式、可测试——无隐藏全局单例）。
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureTargeterManager(_targeterManager);
        }

        // J1 加性（判定器机制）：「对局加载时加载」——对局级判定器注册表随对局装配链在初始化内创建
        // （『初始化内创建＋注入』范式）；装配期注册段经创建参数一次性执行（注册动作返回条目等价句柄、供注册方持有）；
        // 时点＝卡加载之前（远早于洗切与加载段——就绪由装配链位置保证：「装配期已注册条目在卡加载阶段即可被解析调用」）。
        _judicators = new JudicatorRegistry();
        Engine.Judicators = _judicators; // 效果装载链落地 judicatorWrappers 的解析源（对局级）
        // S9（批次 5）：判定器包装器与成员包装器**同一注册面**（R1i）——组件资产的 judge-wraps 登记在
        // 编排运行时的 WrapRegistry 上，判定器调用点在此接入，执行相位语义与成员包装器相同。
        _judicators.ModelWraps = EngineRuntime.Wraps;
        // A.20：行动输入准入判定器（无状态；每次查询/提交现读前端状态）。
        _judicators.Register(JudicatorNames.ActionInput,
            new ActionInputJudicator(player => InteractionStateReader?.Invoke(player)));

        // 抑制资格判定器（默认：死亡 / 内建「无法被抑制」；词条经 judicatorWrappers 改写）。
        _judicators.Register(JudicatorNames.InhibitLegEligibility, new InhibitEligibilityJudicator());

        // 资产化 S12c：内置判定器**不再硬编码于装配中枢**——改由（可选）资产目录快照经激活器装配
        // （内置插件 `orc.builtin.core` 提供；`null`＝**空目录**＝无任何判定器——P8=i）。
        // 语义、注册序与条目构造与迁移前逐字保持一致（迁至 Orc.Plugins.BuiltIn.BuiltInPlugin.Register）。
        _assetServices = new AssetServiceRegistry();

        var matchServices = new Orc.Game.Plugins.MatchServices(
            _battlefieldManager.Battlefield,
            _lifecycle,
            () => _turnManager?.CurrentPlayer,
            owner => _commandManager?.EnemyOfPlayer(owner),
            () => _effectRuntime,
            unit => _commandManager?.IsUnitGuarded(unit) ?? false,
            hq => _commandManager?.IsHqGuarded(hq) ?? false,
            (attacker, targetRef) => _commandManager?.IsAttackTargetLegal(attacker, targetRef) ?? false);

        if (_packageActivator is not null)
        {
            var activation = _packageActivator.Activate(
                Orc.Core.AssetActivationPhase.PreCommand,
                matchServices,
                bindingServices: null,
                _assetServices,
                _judicators,
                Engine.Prefabs,
                uiServices: new Orc.Game.Ui.UiPluginServices(UiProjections));
            HandleActivation("包激活（前）", activation);
        }

        // E1-34（DSL 可运行）：默认装配脚本求值器——csx 处理器（效果解析器产物）需要它才能编译执行；
        // 内核零依赖口径不变（接口倒置），实现由 satellite（Orc.Script）提供；宿主已装配＝不覆盖。
        Engine.ScriptEvaluator ??= new Orc.Script.CSharpScriptEvaluator();

        // handler DSL（S8／R28）：与上一处同口径——引擎侧装配编译器 ＋ 目录（宿主已装配＝不覆盖）。
        Engine.HandlerDsl ??= Orc.Game.Effects.HandlerDslAssembly.Create();

        // 资产化 S12c：目标候选合法性 / 无头选靶 / 资源判定器同属内置判定器——已随内置插件迁出（见上）。

        // E1 加性：效果运行时门面（csx 的**唯一**游戏层受控入口——消灭[死亡链]/伤害/属性修饰/词条/抽牌/无头选靶/槽加·减）；
        // 协作者以延迟读取 lambda 注入（指挥管理器创建较晚）；并注入各玩家（「卡 → 玩家 → 服务」读取路径的玩家环节）。
        // 批 4 加性（csx 动态效果能力）：加传「对局引擎」与「可操作相位门禁」（动态效果受控面的实例化/预制体库与终局拒斥——
        // 引擎不可经卡/玩家公开面解析，须装配接线；门禁读取对局生命周期——终局后挂载面拒斥、卸载面按失效结果处理）。
        _effectRuntime = new EffectRuntime(
            () => _commandManager,
            () => _playerManager,
            () => _judicators,
            () => _resourceManager,
            () => Engine,
            () => _lifecycle.IsActionAllowed,
            battlefield: () => _battlefieldManager.Battlefield,
            allowRandom: () => _options.AllowRandomTargeting);
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureEffectRuntime(_effectRuntime);
        }

        // 外部装配段（追加/定制通道——最小加性保持；重复注册默认名被拒绝〔J1「注册即配置、重复拒绝」口径〕；
        // 装配期 moding 改写经注册表 moding 面——注册所得/解析所得句柄均可锚定）。
        _judicatorAssembly?.Invoke(_judicators);

        // A4 加性：再触发服务（对局级——创建接收触发器〔包装底层触发器〕并挂载更新总线；请求＝机制内部导航、
        // 发布—接收—执行同一同步链）。创建时点＝指挥管理器之前（指挥管理器死亡链的「亡计结算」经其统一执行面；
        // 与归零检查触发器同批装配、均在卡牌加载之前）。
        _retriggerSystem = new RetriggerSystem(Engine, _lifecycle);

        // 2C 加性：指挥管理器（引擎侧创建四个内置流程触发器——指挥 / 单位移动 / 单位攻击 / 造成攻击伤害）
        // ＋注册为底层触发器（进注册表、分层可查询）；创建时点＝卡牌加载之前（词条装载依赖「造成攻击伤害」就绪）。
        // 后置项 B 加性：注入对局生命周期（终局门禁＋HQ≤0 胜者记录）。
        // A4 加性：注入再触发服务（死亡链的亡计结算统一执行面——单源＋重入防护）。
        // 资产化 S12c：判定条目句柄一经解析持有（快照激活后、卡加载前就绪；与迁移前"注册所得句柄"等价——同一可寻址锚）。
        var combatTargetLegalEntry = _judicators.Resolve(JudicatorNames.CombatTargetLegal);
        var combatCounterEligibilityEntry = _judicators.Resolve(JudicatorNames.CombatCounterEligibility);
        var combatAmbushConditionEntry = _judicators.Resolve(JudicatorNames.CombatAmbushCondition);
        var moveLegEligibilityEntry = _judicators.Resolve(JudicatorNames.MoveLegEligibility);
        var attackLegEligibilityEntry = _judicators.Resolve(JudicatorNames.AttackLegEligibility);
        var moveFrontlineEnemyEntry = _judicators.Resolve(JudicatorNames.MoveFrontlineEnemy);

        _commandManager = new CommandManager(
            Engine,
            _battlefieldManager.Battlefield,
            _targeterManager,
            _playerManager.Players,
            () => _turnManager?.CurrentPlayer,
            _lifecycle,
            _retriggerSystem,
            ResolveJudicatorBinding,
            // K1：目标合法性判定通道——经 combat.target.legal 条目句柄（固定内置注册段注册所得；每次调用经统一解析点）。
            (attacker, targetRef) => CombatJudicatorInvoker.InvokeBool(combatTargetLegalEntry, attacker, targetRef),
            // K2：反击资格判定通道——经 combat.counter.eligibility 条目句柄（默认互伤区与伏击资格共用）；
            // 伏击条件判定通道——经 combat.ambush.condition 条目句柄（伏击效果经装载上下文取用）。
            (attacker, target) => CombatJudicatorInvoker.InvokeBool(combatCounterEligibilityEntry, attacker, target),
            (self, attacker) => CombatJudicatorInvoker.InvokeBool(combatAmbushConditionEntry, self, attacker),
            // K3：leg 资格（move/attack）与推进前置判定通道——经条目句柄（固定内置注册段注册所得；可用性/复验四调用点同源）。
            (unit, position) => LegJudicatorInvoker.InvokeLeg(moveLegEligibilityEntry, unit, position),
            unit => LegJudicatorInvoker.InvokeLeg(attackLegEligibilityEntry, unit, null),
            owner => CombatJudicatorInvoker.InvokeBool(moveFrontlineEnemyEntry, owner),
            // A.20：行动输入准入通道——查询与提交每次读取前端状态（末位位置参数绑定 inputAllowed）。
            CanAcceptInput,
            // 前端资产迁移 S5：**冻结本局词条快照**（运行期"覆盖"只改引擎持有引用，不影响进行中的对局）。
            keywordSnapshot: Orc.Game.Cards.KeywordRegistry.Snapshot());
        // 资产化 P11（甲）：四类内置流程触发器登记**不再硬编码**——由内置插件声明、在本相经绑定面执行。
        if (_packageActivator is not null)
        {
            var binding = new Orc.Game.Plugins.MatchBindingServices(() => _commandManager, Engine);
            var activation = _packageActivator.Activate(
                Orc.Core.AssetActivationPhase.PostCommand,
                matchServices,
                binding,
                _assetServices,
                _judicators,
                Engine.Prefabs,
                uiServices: new Orc.Game.Ui.UiPluginServices(UiProjections));
            HandleActivation("包激活（后）", activation);
        }

        // A2 加性：HQ 词条面的装载上下文提供器注入（延迟读取——与卡牌侧同构；HQ 免疫等词条运行时授予的
        // 上下文来源；对 HQ 归零改写器挂载非必需〔不经上下文〕——注入保持与卡牌侧一致的可扩展性）。
        foreach (var player in _playerManager.Players)
        {
            player.Hq.Keywords.LoadContextProvider = () => _commandManager?.KeywordLoadContext;
        }

        // A4 加性：再触发服务注入各玩家——「卡 → 玩家 → 服务」读取路径的玩家环节（效果运行期经
        // RetriggerSystem.ResolveFor / RetriggerRules 取用；时序：先于卡加载，显式、可测试——无隐藏全局单例）。
        foreach (var player in _playerManager.Players)
        {
            player.ConfigureRetriggerService(_retriggerSystem);
        }

        // W3-3（G11 HQ 实体化）：HQ 数值路径「装配完成点」——初始基线快照（与单位化先例一致：
        // 以基准状态建立修饰机制比较基线；本时点无修饰/无损伤＝零变化、零发射）。
        // 此后一切 HQ 数值变更（伤害/修饰/撤销）均可被检测与集中触发（「改变才传播」）。
        foreach (var player in _playerManager.Players)
        {
            await player.Hq.Modifiers.RequestRerunAsync(ct);
        }

        // 批 4（数据化）加性：词条效果生产装配（单一时序点承担两者——行为引用注册〔键→委托〕＋词条效果库装载
        // 〔预制体注册〕，随后绑定核验；挂于管理器群装配收尾、卡牌加载之前——「先于任何词条装载」的装配期时点）。
        // 不变量：正常对局初始化后，任何词条装载时行为引用与库键均已就绪/可解析；库文件层失败＝隔离记录、
        // 不阻断（报告＋总流留痕）；绑定层缺失＝装配期 fail-fast（初始化失败）。幂等以生命周期结构达成
        // （Initialize 恰一次——重复被拒绝；每对局＝新引擎实例、各自装配，多对局天然隔离）。
        // 资产化 P11（甲）：词条效果生产装配**不再硬编码**——由内置插件声明、在 postCommand 相经绑定面执行
        // （见上「包激活（后）」段）；时点不变（管理器群装配收尾、卡牌加载之前）。

        KeywordProgramRuntime.Configure(this);

        // 双方卡组洗切（初始化内自动；W4-1 G14 收尾：经统一「洗切动作」面——每副各发恰一条 deck.shuffled 信号〔就地打乱之后〕；
        // 固定顺序＝玩家索引升序，保证可复现；G8：随机源＝对局随机服务——洗切与效果取样共用同一流〔单流〕）
        await ShuffleDeckAsync(_playerManager.Players[0], ct);
        await ShuffleDeckAsync(_playerManager.Players[1], ct);

        // 对局开始卡牌加载（2A 新增；固定链：洗牌 → 加载 → 起手装载 → 三连）：
        // 逐张经卡牌基类加载模板（逐张广播 card.load，载荷 {Card, Player}；A 组先、B 组后、组内洗牌后顺序；加载不改计数）
        await _playerManager.LoadDecksAsync(ct);

        // W1-1：卡牌 ID 水位线快照（＝已分配 ID 的最大值；语义＝初始化卡牌加载完成后、任何后续动作〔起手装载等〕之前）
        _playerManager.SnapshotCardIdWatermark();

        // 起手装载（静默、不发更新；先手 4 / 后手 5——缺省，经 MatchOptions 配置）
        var firstPlayer = _playerManager.Players[_firstPlayerIndex];
        var secondPlayer = _playerManager.Players[(_firstPlayerIndex + 1) % 2];
        _playerManager.LoadOpeningHand(firstPlayer, _options.OpeningHandSizeFirstPlayer);
        _playerManager.LoadOpeningHand(secondPlayer, _options.OpeningHandSizeSecondPlayer);

        // 单位回合统计（S1b）：单一订阅器维护行动/移动/攻击/花费/受伤；每己方回合开始清零。
        new Orc.Game.Cards.UnitTurnStatsTracker(Engine, _battlefieldManager.Battlefield).Start();

        // 先手回合开始序列（3 条更新；第 1 回合不抽牌＝无 card.drawn；顺序 await 完结后返回）
        _turnManager = new TurnManager(Engine, _playerManager, _resourceManager, _lifecycle, _options.AllowFirstTurnDraw);
        // 2C 接线：单位行动状态恢复（回合开始处理段——行动方在场单位重置两 bool＋词条运行态清零）
        _turnManager.ActionStateRefresher = _commandManager.RefreshActionStates;
        // 打出管理器（2B 加性：随管理器群生成——打出链服务与交互入口；回合管理器为反制「仅己方回合」真源；
        // 后置项 B 加性：注入对局生命周期——终局门禁）
        var turnOwnerEntry = _judicators.Resolve(JudicatorNames.ActionTurnOwner);
        _judicators.Register(
            JudicatorNames.DeployPositionCandidates,
            new DeployPositionCandidatesJudicator(
                _battlefieldManager.Battlefield,
                owner => _commandManager.EnemyOfPlayer(owner)));

        // R4a: deployment candidates via judicator (rewritable); PlayManager only consumes the provider.
        IReadOnlyList<FormationGap> DeploymentCandidates(UnitCard unit)
        {
            var invoked = _judicators.Invoke(
                JudicatorNames.DeployPositionCandidates, new object[] { unit.Owner!, unit }, unit.Owner?.Index);
            return invoked is { Length: > 0 } && invoked[0] is IReadOnlyList<FormationGap> gaps
                ? gaps
                : Array.Empty<FormationGap>();
        }
        _playManager = new PlayManager(
            Engine,
            _targeterManager,
            _battlefieldManager.Battlefield,
            _turnManager,
            _lifecycle,
            CanAcceptInput,
            player => CombatJudicatorInvoker.InvokeBool(turnOwnerEntry, player.Index, new object[] { player }),
            deploymentCandidates: DeploymentCandidates);

        // 沙盒调试原语族（引擎保留触发器）：管理器群建齐后装配——六原语各注册唯一内置 handler、立即封闭；
        // 授权事实来自构造期（MatchOptions.SandboxAuthorized，缺省未授权）。未授权时原语入口一律拒绝、零状态变更。
        _sandbox = new Sandbox.SandboxPrimitives(this, _sandboxAuthorized);

        // A1 加性：换牌管理器（开局换牌相位承载——特制槽位交互＋换牌执行；双方确认＝执行先手第 1 回合＋置"进行"）
        var turnManager = _turnManager!;
        var playerManager = _playerManager!;
        var targeterManager = _targeterManager!;
        var cardService = _cardService!;
        _mulliganManager = new MulliganManager(
            Engine,
            targeterManager,
            playerManager,
            _randomService,
            _lifecycle,
            card => cardService.TryResolveDefinitionId(card, out var definitionId) ? definitionId : null,
            async enterCt =>
            {
                await turnManager.StartFirstTurn(firstPlayer, enterCt);
                _lifecycle.MarkInProgress();
            });

        if (_options.SkipMulligan)
        {
            // 无换牌装配（既有序列）：先手回合开始序列 → 置"进行"
            await turnManager.StartFirstTurn(firstPlayer, ct);
            _lifecycle.MarkInProgress();
        }
        else
        {
            // A1 受控变更（缺省）：置"换牌"相位——先手回合延后至双方确认（见 MulliganManager）
            _lifecycle.MarkMulligan();
        }
    }

    // ---------- S7：卡牌加载器接线（整批装载双方牌堆） ----------

    /// <summary>
    /// 接入卡牌加载器并把**双方牌堆合并为一次整批装载**（S7）：
    /// 卡库由装载链写入（合法／警报卡＝套件补全后的定义，含资产域）→ 按卡位结局**原位剪除留空卡位**
    /// 并把条目规范为解析后的应用层 ID（卡库键形态）。
    /// <para>
    /// **不做实例化、不分配实例 ID**：实例仍在 <see cref="PlayerManager.LoadDecksAsync"/> 内逐张创建
    /// （ID 水位线快照仍在其后原位执行——时序不因链路替换而前移）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">装载链结构性失败；或卡位结局与输入不同长（装配契约不符）。</exception>
    private async Task LoadDecksFromCardLoaderAsync(IMatchCardLoader loader, CancellationToken ct)
    {
        loader.Attach(_cardLibrary!);

        var slots = new List<MatchCardSlot>(_deckForPlayerA.Count + _deckForPlayerB.Count);
        for (var i = 0; i < _deckForPlayerA.Count; i++)
        {
            slots.Add(MatchCardSlot.ForPlayer(0, i, _deckForPlayerA[i]));
        }

        for (var i = 0; i < _deckForPlayerB.Count; i++)
        {
            slots.Add(MatchCardSlot.ForPlayer(1, i, _deckForPlayerB[i]));
        }

        var outcome = await loader.LoadBatchAsync(slots, ct).ConfigureAwait(false);
        if (outcome.Slots.Count != slots.Count)
        {
            throw new InvalidOperationException(
                $"卡牌加载批次结局与卡位清单不同长（输入 {slots.Count}、结局 {outcome.Slots.Count}）——装配契约不符。");
        }

        var playerACount = _deckForPlayerA.Count;
        _deckForPlayerA = PruneDeck(_deckForPlayerA, outcome.Slots, offset: 0);
        _deckForPlayerB = PruneDeck(_deckForPlayerB, outcome.Slots, offset: playerACount);

        // 牌堆全量留空＝开局无从继续（非「个别卡位非法」场景）：明确失败而不是让抽取阶段报空集合。
        if (_deckForPlayerA.Count == 0 || _deckForPlayerB.Count == 0)
        {
            throw new InvalidOperationException(
                $"整批装载后牌堆为空（玩家A {_deckForPlayerA.Count} 张／玩家B {_deckForPlayerB.Count} 张，批次 {outcome.BatchId}）"
                + "——所有卡位均未产出可玩定义，对局无法开局。");
        }
    }

    /// <summary>按卡位结局重建牌堆：只保留装载成功的卡位，条目写入解析后的应用层 ID（牌堆＝该序）。</summary>
    private static CardList PruneDeck(CardList deck, IReadOnlyList<CardSlotOutcome> slots, int offset)
    {
        var pruned = new CardList();
        for (var i = 0; i < deck.Count; i++)
        {
            var slot = slots[offset + i];
            if (slot.IsLoaded && slot.AppId is { Length: > 0 } appId)
            {
                pruned.Add(appId);
            }
        }

        return pruned;
    }

    // ---------- 对局全流程动作面（A1：开局换牌／认输） ----------

    /// <summary>
    /// 开局换牌（A1；M2=a 唯一对外换牌入口）：仅"换牌"相位且该方未确认——
    /// 经**特制槽位**（<see cref="Targeting.MulliganSelectSlot"/>；M1=b：专用 Kind／呈现标注供前端播放专属动画；
    /// 槽位参数＝该玩家）交互选择退回卡组的手牌（0..手牌数）；确认＝换牌（重洗恰一条 deck.shuffled、抽等量静默）
    /// 并**自动确认该方**；取消＝零副作用；空手牌＝零交互直接确认。
    /// 双方确认＝置"进行"并执行先手第 1 回合。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="InvalidOperationException">对局尚未就绪（换牌管理器未装配）。</exception>
    /// <exception cref="ArgumentException">指定玩家不属于本对局。</exception>
    public Task<MulliganResult> BeginMulliganAsync(Player player, CancellationToken ct = default)
        => MulliganManager.BeginMulliganAsync(player, ct);

    /// <summary>
    /// 确认换牌（不换牌直接确认；M2=a 第二入口）：语义同 <see cref="BeginMulliganAsync"/> 的确认段——
    /// 仅"换牌"相位且该方未确认；双方确认＝置"进行"并执行先手第 1 回合；重复确认＝明确拒绝（零副作用）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="InvalidOperationException">对局尚未就绪（换牌管理器未装配）。</exception>
    /// <exception cref="ArgumentException">指定玩家不属于本对局。</exception>
    public Task<MulliganResult> MulliganDone(Player player, CancellationToken ct = default)
        => MulliganManager.ConfirmAsync(player, ct);

    /// <summary>
    /// 认输（A1／S3）：仅"进行"相位；置**对手为胜者**、相位置"结束"——与 HQ≤0 **共用同一"置结束"单源路径**
    /// （<see cref="MatchLifecycle.End"/>；原因＝<see cref="MatchEndReason.Concede"/>）。
    /// 重复认输／非进行相位＝明确拒绝（零副作用、状态不推进）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="InvalidOperationException">对局尚未就绪（玩家管理器不可用）。</exception>
    /// <exception cref="ArgumentException">指定玩家不属于本对局。</exception>
    public ConcedeResult Concede(Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var players = PlayerManager.Players; // 就绪门禁（未 Initialize＝抛错）
        if (!players.Contains(player))
        {
            throw new ArgumentException("指定玩家不属于本对局（认输被拒绝）。", nameof(player));
        }

        if (_lifecycle.IsEnded)
        {
            return ConcedeResult.Rejected(ConcedeFailureReason.GameEnded);
        }

        if (!_lifecycle.IsActionAllowed)
        {
            return ConcedeResult.Rejected(ConcedeFailureReason.PhaseBlocked);
        }

        var opponent = players[(player.Index + 1) % 2];
        _lifecycle.End(opponent, MatchEndReason.Concede);
        return ConcedeResult.Accepted();
    }

    /// <summary>
    /// 终止对局（管理员主动终止；**无胜者**）：走与 <see cref="Concede"/> 相同的终局收尾路径
    /// （<see cref="MatchLifecycle.End"/>），但 <c>Winner = null</c>、原因＝<see cref="MatchEndReason.Aborted"/>。
    /// <para>
    /// 与认输的区别是硬性的：终止不产生胜者，因此**不得**用"代某一方认输"模拟（会凭空产生胜者，
    /// 污染战绩与回放）。重复终止＝拒绝（零副作用、状态不推进）。
    /// </para>
    /// <para>
    /// **相位不设限**（与 <see cref="Concede"/> 不同）：准备期／换牌期／进行期都可终止——
    /// 管理员收回房间不该被"还没进入进行态"挡住。
    /// </para>
    /// </summary>
    /// <returns>是否被接受（<c>false</c>＝已终局）。</returns>
    /// <exception cref="InvalidOperationException">对局尚未就绪（玩家管理器不可用）。</exception>
    public bool Abort()
    {
        _ = PlayerManager.Players; // 就绪门禁（未 Initialize＝抛错）

        if (_lifecycle.IsEnded)
        {
            return false;
        }

        _lifecycle.End(null, MatchEndReason.Aborted);
        return true;
    }

    /// <summary>结束回合（主路径；无参、自动取当前行动方；仅"进行"态可调用；准备/换牌/终局后拒绝）。</summary>
    /// <exception cref="InvalidOperationException">对局已结束（终局）；或对局不在"进行"相位（准备/换牌推进被拒绝）。</exception>
    public async Task EndTurn(CancellationToken ct = default)
    {
        if (State == MatchState.Ended)
        {
            throw new InvalidOperationException("对局已结束（终局），不能推进回合。");
        }

        if (State != MatchState.InProgress)
        {
            throw new InvalidOperationException(State == MatchState.Mulligan
                ? "对局处于换牌（mulligan）相位，不能推进回合（须先由双方确认）。"
                : "对局尚未进入'进行'态，不能推进回合（须先成功完成 Initialize）。");
        }

        // 动作作用域（UI 消费桥接）：结束回合链产生的事件聚合为一段。
        await using var _actionScope = Engine.BeginAction();

        // A.20：结束回合亦受行动输入准入约束（前端"忙碌/过渡中"不得推进回合）。
        if (!CanAcceptInput(CurrentPlayer, CurrentPlayer.Hq))
            throw new InvalidOperationException("InteractionBlocked");
        await TurnManager.EndTurn(ct);
    }

    /// <summary>
    /// 管理器/转发读面的就绪门禁：准备态不可用（抛错）；"进行"与"结束"态均可读（终局后只读查询面保持可用）。
    /// </summary>
    private T RequireReady<T>(T? manager)
        where T : class
        => manager is not null && State != MatchState.Preparing
            ? manager
            : throw new InvalidOperationException("对局尚未进入'进行'态：管理器群不可用（须先成功完成 Initialize）。");

    /// <summary>
    /// 判定器解析转发（J2；装配链内使用）：按名解析对局注册表条目并包为绑定锚
    /// （「按名解析」动作的调用点——未注册名＝装配期 fail-fast）。
    /// 注册表在装配链固定位置创建（先于卡加载段与指挥管理器创建位——解析调用时已就绪）。
    /// </summary>
    private JudicatorBinding ResolveJudicatorBinding(string name)
        => JudicatorBinding.FromRegistration(
            (_judicators ?? throw new InvalidOperationException("判定器注册表尚未创建（装配链时序错误）。")).Resolve(name));

    /// <summary>创建期名单格式校验：null/空白 id → 拒绝（格式错误 fail-fast；"未注册 id"不在此层校验）。</summary>
    private static void ValidateDeckIds(CardList deck, string paramName)
    {
        foreach (var id in deck)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("卡组名单含 null/空白 id（格式错误在创建期被拒绝）。", paramName);
            }
        }
    }

    /// <summary>
    /// 玩家构筑配置校验（S10「G12补」；对局创建期 fail-fast）：
    /// 提供即校验——①成对齐备（缺失其一＝明确拒绝）；②未定义枚举值＝拒绝；③主国非五主国＝拒绝；
    /// ④盟国==主国＝拒绝；⑤盟国==Neutral＝拒绝。
    /// 未提供（null）＝跳过全部校验、不抛错（缺省合法——读面为 null、消费端降级为「不匹配」）。
    /// </summary>
    private static void ValidateDeckConfiguration(PlayerDeckConfiguration? configuration, string paramName)
    {
        if (configuration is null)
        {
            return; // 未提供：跳过全部校验（缺省语义）
        }

        if (configuration.MainFaction is not { } mainFaction || configuration.AllyFaction is not { } allyFaction)
        {
            throw new ArgumentException(
                "玩家构筑配置须成对（主国＋盟国）——缺失其一被拒绝（请齐备提供或整体缺省）。", paramName);
        }

        if (!Orc.Game.Cards.Faction.IsDefined(mainFaction))
        {
            throw new ArgumentOutOfRangeException(
                paramName, mainFaction, "主国为未定义标识（创建期 fail-fast 拒绝）。");
        }

        if (!FactionRules.IsMajorFaction(mainFaction))
        {
            throw new ArgumentOutOfRangeException(
                paramName, mainFaction, "主国非五大主国（德/苏/美/英/日——创建期 fail-fast 拒绝）。");
        }

        if (!Orc.Game.Cards.Faction.IsDefined(allyFaction))
        {
            throw new ArgumentOutOfRangeException(
                paramName, allyFaction, "盟国为未定义标识（创建期 fail-fast 拒绝）。");
        }

        if (allyFaction == Faction.Neutral)
        {
            throw new ArgumentOutOfRangeException(
                paramName, allyFaction, "盟国不得为 Neutral（创建期 fail-fast 拒绝）。");
        }

        if (allyFaction == mainFaction)
        {
            throw new ArgumentOutOfRangeException(
                paramName, allyFaction, "盟国不得与主国相同（创建期 fail-fast 拒绝）。");
        }
    }

    /// <summary>
    /// 包激活结果落地（S14）：失败逐条写日志 → 可选包跳过与必需包失败写入装载门报告（审查面可见）→
    /// **必需包失败＝对局不开局**（初始化上抛，且不留半初始化状态：此处在初始化早期，异常沿 Initialize 上抛）。
    /// </summary>
    private void HandleActivation(string phase, Orc.Game.Plugins.MatchPackageActivationResult activation)
    {
        foreach (var failure in activation.Failures)
        {
            Engine.RootStream.WriteLog(
                phase, failure, Orc.Core.LogLevel.Error, new[] { "package", "activation" });
        }

        foreach (var skip in activation.SkippedPackages)
        {
            Engine.LoadGate.Record(
                skip.PackageId,
                skip.Required,
                skip.Required ? Orc.Core.PackLoadStatus.Failed : Orc.Core.PackLoadStatus.Skipped,
                skip.Failures);
        }

        var required = activation.RequiredFailures;
        if (required.Count > 0)
        {
            throw new InvalidOperationException(
                $"必需卡包激活失败：{string.Join("；", required.Select(static r => $"{r.PackageId}（{string.Join("／", r.Failures)}）"))}"
                + " —— 必需包失败＝对局不开局（S14 第 2 条）。");
        }
    }
}
