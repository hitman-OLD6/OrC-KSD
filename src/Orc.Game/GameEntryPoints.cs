namespace Orc.Game;

/// <summary>
/// 输入入口交付状态（S6；「本批可核」/「01 待交付」）。
/// </summary>
public enum GameEntryPointStatus
{
    /// <summary>已具备（本批可核——反射存在性可断言）。</summary>
    Available = 0,

    /// <summary>01 待交付（签名已冻结、实现随 01；存在性本批不适用）。</summary>
    PendingDelivery = 1,
}

/// <summary>
/// 输入入口条目（S6 全集清单的一行）：
/// 「入口｜类别｜所在类型｜签名｜语义前置门禁｜结果形态｜交付状态」。
/// </summary>
/// <param name="Name">入口成员名（对局构造＝<c>.ctor</c>）。</param>
/// <param name="Category">类别（见 <see cref="GameEntryPoints"/> 的 Category* 常量）。</param>
/// <param name="Owner">所在类型全名（接口契约面＝接口全名）。</param>
/// <param name="Signature">签名（人类可读）。</param>
/// <param name="Gate">前置门禁（相位／归属／在场／终局／装配性前置）。</param>
/// <param name="Outcome">结果形态。</param>
/// <param name="Status">交付状态。</param>
public sealed record GameEntryPoint(
    string Name,
    string Category,
    string Owner,
    string Signature,
    string Gate,
    string Outcome,
    GameEntryPointStatus Status)
{
    /// <summary>是否本批可核（<see cref="GameEntryPointStatus.Available"/>）。</summary>
    public bool IsAvailable => Status == GameEntryPointStatus.Available;
}

/// <summary>
/// 对外输入入口**全集清单**（02-输入接口 S6；纯声明、零行为改动）：
/// 覆盖对局／出牌／指挥／目标应答／生成放置／注册实例化／手牌资源／门户重发八类；
/// 每条标注前置门禁（相位／归属／在场／终局）与结果形态；2026-10-05 基线（03 交付后）。
/// 边界：本类＝「玩家/UI 发起动作或应答目标选择」的入口面，**不是** 03 定义的接入点／观察面／扩展点
/// （<see cref="GameHooks"/>）；同一成员不得同时登记于两边。
/// </summary>
public static class GameEntryPoints
{
    /// <summary>类别：对局（建局／初始化／回合／终局）。</summary>
    public const string CategoryMatch = "对局";

    /// <summary>类别：出牌（预行为入口与打出链）。</summary>
    public const string CategoryPlay = "出牌";

    /// <summary>类别：指挥（拖拽／独立移动／独立攻击）。</summary>
    public const string CategoryCommand = "指挥";

    /// <summary>类别：目标应答（目标选择发起与桥接应答）。</summary>
    public const string CategoryTargeting = "目标应答";

    /// <summary>类别：生成/放置（卡牌生成与入区）。</summary>
    public const string CategorySpawn = "生成/放置";

    /// <summary>类别：注册/实例化（定义注册与实例创建、加载）。</summary>
    public const string CategoryRegistry = "注册/实例化";

    /// <summary>类别：手牌/资源（手牌操作、指挥点、回合）。</summary>
    public const string CategoryPlayer = "手牌/资源";

    /// <summary>类别：门户/重发（受控数值变更入口与再触发服务）。</summary>
    public const string CategoryGateway = "门户/重发";

    /// <summary>入口全集（稳定序＝类别分组序）。</summary>
    public static IReadOnlyList<GameEntryPoint> All { get; } = new[]
    {
        // ---------- 对局 ----------
        new GameEntryPoint(".ctor", CategoryMatch, "Orc.Game.Match",
            "Match(...)（构造注入：引擎/卡库/提供器/桥接/生命周期/判定器装配）",
            "装配期（构造）", "对局实例（未初始化）", GameEntryPointStatus.Available),
        new GameEntryPoint("Initialize", CategoryMatch, "Orc.Game.Match",
            "Initialize(ct)", "未初始化（可重复调用＝幂等装配）", "void（就绪面开放）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("ShuffleDeckAsync", CategoryMatch, "Orc.Game.Match",
            "ShuffleDeckAsync(player, ct)", "已初始化", "void（deck.shuffled）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("EndTurn", CategoryMatch, "Orc.Game.Match",
            "EndTurn(ct)", "已初始化；非终局（相位门禁随 01）", "void（回合交接）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("Concede", CategoryMatch, "Orc.Game.Match",
            "Concede(player, ct)", "非终局（置 Ended；与 HQ≤0 单源路径）", "void（终局记录）",
            GameEntryPointStatus.PendingDelivery),
        new GameEntryPoint("MulliganReplace", CategoryMatch, "Orc.Game.Match",
            "MulliganReplace(player, handIndexes, ct)", "Mulligan 相位 ∧ 该方未确认", "void（换牌）",
            GameEntryPointStatus.PendingDelivery),
        new GameEntryPoint("MulliganDone", CategoryMatch, "Orc.Game.Match",
            "MulliganDone(player, ct)", "Mulligan 相位 ∧ 该方未确认", "void（确认；双方确认＝进 Play）",
            GameEntryPointStatus.PendingDelivery),

        // ---------- 出牌 ----------
        new GameEntryPoint("BeginUnitPrePlayAsync", CategoryPlay, "Orc.Game.Managers.PlayManager",
            "BeginUnitPrePlayAsync(card, ct)", "非终局 ∧ 归属已加载 ∧ 费用 ∧ 空槽候选非空",
            "PlayResult（确认⇒自动衔接打出链）", GameEntryPointStatus.Available),
        new GameEntryPoint("BeginOrderPrePlayAsync", CategoryPlay, "Orc.Game.Managers.PlayManager",
            "BeginOrderPrePlayAsync(card, ct)", "非终局 ∧ 归属已加载 ∧ 费用",
            "PlayResult（确认⇒自动衔接打出段）", GameEntryPointStatus.Available),
        new GameEntryPoint("PlayUnitAsync", CategoryPlay, "Orc.Game.Managers.PlayManager",
            "PlayUnitAsync(card, target, ct)", "非终局 ∧ 槽位为空 ∧ 未单位化",
            "PlayResult", GameEntryPointStatus.Available),
        new GameEntryPoint("JoinUnitAsync", CategoryPlay, "Orc.Game.Managers.PlayManager",
            "JoinUnitAsync(card, target, ct)", "非终局 ∧ 槽位为空 ∧ 未单位化",
            "PlayResult", GameEntryPointStatus.Available),
        new GameEntryPoint("UseCounterAsync", CategoryPlay, "Orc.Game.Managers.PlayManager",
            "UseCounterAsync(card, ct)", "非终局 ∧ 己方回合 ∧ 费用",
            "PlayResult（激活↔取消）", GameEntryPointStatus.Available),
        // P4 契约白名单：本条目只允许 Name/Signature 改名；Gate 文本是受冻结字段（零 diff），
        // 其中的旧入口名仅作"门禁同源"的描述性引用，不指向任何现存成员——语义仍为「与预行为入口同一门禁」。
        new GameEntryPoint("PlayOrderAsync", CategoryPlay, "Orc.Game.Managers.PlayManager",
            "PlayOrderAsync(card, ct)", "同 BeginCommandPrePlayAsync",
            "PlayResult（与预行为入口同一实现）", GameEntryPointStatus.Available),

        // ---------- 指挥 ----------
        new GameEntryPoint("BeginCommandAsync", CategoryCommand, "Orc.Game.Commanding.CommandManager",
            "BeginCommandAsync(unit, ct, triggerCard?)", "非终局 ∧ 己方回合 ∧ 在场未死亡 ∧ 两动作之一可用",
            "CommandResult（拖拽：候选＝并集）", GameEntryPointStatus.Available),
        new GameEntryPoint("BeginMoveAsync", CategoryCommand, "Orc.Game.Commanding.CommandManager",
            "BeginMoveAsync(unit, ct)", "非终局 ∧ 己方回合 ∧ 在场未死亡 ∧ 移动可用（含候选非空）",
            "CommandResult（候选＝前线空槽）", GameEntryPointStatus.Available),
        new GameEntryPoint("BeginAttackAsync", CategoryCommand, "Orc.Game.Commanding.CommandManager",
            "BeginAttackAsync(unit, ct)", "非终局 ∧ 己方回合 ∧ 在场未死亡 ∧ 攻击可用（含候选非空）",
            "CommandResult（候选＝合法敌方单位/HQ）", GameEntryPointStatus.Available),
        new GameEntryPoint("LeaveBattlefieldAsync", CategoryCommand, "Orc.Game.Commanding.CommandManager",
            "LeaveBattlefieldAsync(unit, ct)", "非终局", "void（离场）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("GetCommandAvailability", CategoryCommand, "Orc.Game.Commanding.CommandManager",
            "GetCommandAvailability(unit)", "纯查询（无门禁副作用；未终局）",
            "CommandAvailability（bool＋原因＋候选）", GameEntryPointStatus.Available),

        // ---------- 目标应答 ----------
        new GameEntryPoint("RunAsync", CategoryTargeting, "Orc.Game.Targeting.TargeterManager",
            "RunAsync(flow, param)", "桥接可缺省（缺失＝失败结局）", "Task<TargeterResult>（三态）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("Step", CategoryTargeting, "Orc.Game.Targeting.ITargeterFlow",
            "Step(selector, parameter)", "流程内产出选择器并等待应答", "Task<SelectorResult<TResult>>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("Retry", CategoryTargeting, "Orc.Game.Targeting.ITargeterFlow",
            "Retry()", "重入当前选择器（同实例、有上限）", "Task<SelectorResult<TResult>>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("BeginTargeting", CategoryTargeting, "Orc.Game.Targeting.ITargeterBridge",
            "BeginTargeting(session)", "桥接已装配", "void（交付会话）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("NextAsync", CategoryTargeting, "Orc.Game.Targeting.ITargeterSession",
            "NextAsync()", "前端逐个取选择器", "Task<ISelectorInstance?>（null＝流程结束）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("Submit", CategoryTargeting, "Orc.Game.Targeting.ISelectorInstance",
            "Submit(selectorEvent)", "已取到选择器", "bool（true＝构成终局）",
            GameEntryPointStatus.Available),

        // ---------- 生成/放置 ----------
        new GameEntryPoint("CreateAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "CreateAsync(definitionId, owner, ct)", "已初始化", "Task<CardBase>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("PlaceToHandAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "PlaceToHandAsync(card, player, ct)", "已初始化", "Task<CardPlaceResult>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("PlaceToSupportLineAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "PlaceToSupportLineAsync(card, player, slot, …)", "已初始化 ∧ 槽位为空",
            "Task<CardPlaceResult>", GameEntryPointStatus.Available),
        new GameEntryPoint("PlaceToDeckTopAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "PlaceToDeckTopAsync(card, player, ct)", "已初始化", "Task<CardPlaceResult>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("PlaceIntoDeckShuffledAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "PlaceIntoDeckShuffledAsync(card, player, …)", "已初始化", "Task<CardPlaceResult>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("CreateAndPlaceToHandAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "CreateAndPlaceToHandAsync(definitionId, player, ct)〔复合入口〕", "已初始化",
            "Task<CardPlaceResult>", GameEntryPointStatus.Available),
        new GameEntryPoint("CreateAndPlaceToSupportLineAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "CreateAndPlaceToSupportLineAsync(definitionId, player, ct)〔复合入口〕", "已初始化 ∧ 槽位为空",
            "Task<CardPlaceResult>", GameEntryPointStatus.Available),
        new GameEntryPoint("CreateAndPlaceToDeckTopAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "CreateAndPlaceToDeckTopAsync(definitionId, player, ct)〔复合入口〕", "已初始化",
            "Task<CardPlaceResult>", GameEntryPointStatus.Available),
        new GameEntryPoint("CreateAndPlaceIntoDeckShuffledAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "CreateAndPlaceIntoDeckShuffledAsync(definitionId, player, ct)〔复合入口〕", "已初始化",
            "Task<CardPlaceResult>", GameEntryPointStatus.Available),
        new GameEntryPoint("CreateAndPlaceToAdjacentAsync", CategorySpawn, "Orc.Game.MatchCardService",
            "CreateAndPlaceToAdjacentAsync(definitionId, player, ct)〔复合入口〕", "已初始化 ∧ 邻位空槽",
            "Task<CardPlaceResult>", GameEntryPointStatus.Available),

        // ---------- 注册/实例化 ----------
        new GameEntryPoint("Register", CategoryRegistry, "Orc.Game.Cards.CardLibrary",
            "Register(id, definition)", "装配期（注册表）", "void",
            GameEntryPointStatus.Available),
        new GameEntryPoint("Instantiate", CategoryRegistry, "Orc.Game.Cards.CardLibrary",
            "Instantiate(id)", "定义已注册", "CardBase（未加载）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("LoadAsync", CategoryRegistry, "Orc.Game.Cards.CardBase",
            "LoadAsync(owner, ct)", "定义已装配", "void（card.load；归属就位）",
            GameEntryPointStatus.Available),

        // ---------- 手牌/资源 ----------
        new GameEntryPoint("LoadOpeningHand", CategoryPlayer, "Orc.Game.Managers.PlayerManager",
            "LoadOpeningHand(player, count)", "已初始化（静默）", "void",
            GameEntryPointStatus.Available),
        new GameEntryPoint("DrawCard", CategoryPlayer, "Orc.Game.Managers.PlayerManager",
            "DrawCard(player, ct)", "已初始化", "Task<CardBase>（card.drawn）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("DiscardCardAsync", CategoryPlayer, "Orc.Game.Managers.PlayerManager",
            "DiscardCardAsync(player, card, ct)", "已初始化 ∧ 卡在手牌", "Task（card.discarded）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("ReturnToDeckTop", CategoryPlayer, "Orc.Game.Managers.PlayerManager",
            "ReturnToDeckTop(player, card)", "已初始化", "void",
            GameEntryPointStatus.Available),
        new GameEntryPoint("ReturnToDeck", CategoryPlayer, "Orc.Game.Managers.PlayerManager",
            "ReturnToDeck(player, card, position)", "已初始化", "void",
            GameEntryPointStatus.Available),
        new GameEntryPoint("IsOutsideDeck", CategoryPlayer, "Orc.Game.Managers.PlayerManager",
            "IsOutsideDeck(card)", "纯查询", "bool",
            GameEntryPointStatus.Available),
        new GameEntryPoint("SettleAsync", CategoryPlayer, "Orc.Game.Managers.ResourceManager",
            "SettleAsync(player, ct)", "回合开始（资源结算；槽变化发 slot.changed）", "Task",
            GameEntryPointStatus.Available),
        new GameEntryPoint("AddPointsAsync", CategoryPlayer, "Orc.Game.Managers.ResourceManager",
            "AddPointsAsync(player, amount, ct)", "已初始化", "Task<bool>（发 point.changed）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("ChangePointsAsync", CategoryPlayer, "Orc.Game.Managers.ResourceManager",
            "ChangePointsAsync(player, value, kind, ct)", "已初始化", "Task<bool>（设为/增减；发 point.changed）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("EndTurn", CategoryPlayer, "Orc.Game.Managers.TurnManager",
            "EndTurn(ct)", "回合进行中", "Task（turn.end.before/turn.end）",
            GameEntryPointStatus.Available),

        // ---------- 门户/重发 ----------
        new GameEntryPoint("ApplyDefenseDamageAsync", CategoryGateway, "Orc.Game.Cards.UnitCard",
            "ApplyDefenseDamageAsync(amount, ct)", "已单位化 ∧ 未死亡", "Task（受控面：仅 DamagePipeline 可调）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("RepairDefenseAsync", CategoryGateway, "Orc.Game.Cards.UnitCard",
            "RepairDefenseAsync(ct)", "已单位化 ∧ 未死亡", "Task（有变化才发）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("AddUnitTypeAsync", CategoryGateway, "Orc.Game.Cards.UnitCard",
            "AddUnitTypeAsync(type, ct)", "已单位化 ∧ 已定义枚举值", "Task（unit.types.changed 恰一次）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("ApplyDamageAsync", CategoryGateway, "Orc.Game.Players.Hq",
            "ApplyDamageAsync(amount, ct)", "非终局（HQ 数值路径与管线）",
            "Task（受控面：仅 DamagePipeline 可调）", GameEntryPointStatus.Available),
        new GameEntryPoint("AddDamageRewriter", CategoryGateway, "Orc.Game.Players.Hq",
            "AddDamageRewriter(rewriter)", "装配/效果装载期", "void",
            GameEntryPointStatus.Available),
        new GameEntryPoint("AddLethalIntervention", CategoryGateway, "Orc.Game.Players.Hq",
            "AddLethalIntervention(intervention)", "装配/效果装载期", "void",
            GameEntryPointStatus.Available),
        new GameEntryPoint("RemovePipelineHooksBySource", CategoryGateway, "Orc.Game.Players.Hq",
            "RemovePipelineHooksBySource(source)", "装载期配对（卸载）", "int（移除计数）",
            GameEntryPointStatus.Available),
        new GameEntryPoint("RequestAsync", CategoryGateway, "Orc.Game.Cards.RetriggerSystem",
            "RequestAsync(target, chain, ct)", "目标在场（重入防护）", "Task<bool>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("RequestDeployAsync", CategoryGateway, "Orc.Game.Cards.RetriggerRules",
            "RequestDeployAsync(target, ct)", "目标在场", "Task<bool>",
            GameEntryPointStatus.Available),
        new GameEntryPoint("RequestDeathrattleAsync", CategoryGateway, "Orc.Game.Cards.RetriggerRules",
            "RequestDeathrattleAsync(target, ct)", "目标在场", "Task<bool>",
            GameEntryPointStatus.Available),
    };
}
