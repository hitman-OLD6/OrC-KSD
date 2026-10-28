using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Collections;
using Orc.Game.Players;

namespace Orc.Game;

/// <summary>
/// 游戏更新常量集：统一承载回合五连（<c>turn.*</c>）、通用三项（<c>card.played</c> / <c>card.drawn</c> / <c>card.stat.changed</c>）、
/// 第二批 hooks 六项（<c>card.load</c> / <c>card.hand.add</c> / <c>card.died</c> / <c>unit.joined</c> / <c>unit.deployed</c> / <c>unit.position.changed</c>）
/// 与 W4-1（G14 收尾）洗切一项（<c>deck.shuffled</c>）、G7 弃置一项（<c>card.discarded</c>）、
/// S9 类型变更一项（<c>unit.types.changed</c>）、Kb 爆牌一项（<c>card.burned</c>）、
/// E1-25 资源六项（<c>slot.gained</c> / <c>slot.lost</c> / <c>slot.changed</c> ＋ <c>point.gained</c> / <c>point.lost</c> / <c>point.changed</c>）、
/// E1-33 两项（<c>card.damaged</c> / <c>unit.acted</c>）、E1-39 一项（<c>unit.combat.survived</c>）、E1-47 一项（<c>unit.damage.dealt</c>）、
/// E1-53 一项（<c>counter.triggered</c>）、S1 一项（<c>unit.upgraded</c>）、S2 一项（<c>unit.revealed</c>）——合计 31 条。
/// 全部为新定义、与 Orc 既有常量集无重叠；字面值为对外订阅契约，一经定稿即冻结（引擎总线同一性＝ordinal 序数、大小写敏感）。
/// 发射统一经引擎总线 <see cref="LogicEngine.Emit"/>（本类提供可选静态发射助手，内部即走该通路）。
/// 实际发射（2B 后）：turn 五连、card.drawn 与 card.drawn→card.hand.add 连发（抽牌链路；粒度不同、并存）、card.load（初始化逐张加载）
/// 与打出链接线点（card.played〔部署/指令打出链〕、unit.joined〔加入路径〕、unit.deployed〔部署链〕）；
/// 2C 后：card.died（战斗死亡）与 unit.position.changed（移动/位置联动）调用点已接线；
/// W2a（G3 修饰机制）起：card.stat.changed 升级为载荷化契约（{ Card, ChangedFields }——目标卡＋本轮变化字段集合），
/// 调用点＝修饰机制集中触发（有变更轮恰一次、无变更零发射）；既有流程调用点接入属 W2b；
/// W4-1（G14 收尾）：card.played 升级为载荷化契约（{ Card, Player }——被使用卡实例＋使用方），调用点＝部署/指令打出链「打出宣告」；
/// deck.shuffled 新增（初始化双方卡组洗切各一条——「统一经洗切动作面〔Match.ShuffleDeckAsync〕、恰一次」；「是否首次」不入信号）。
/// G7（手牌操作与弃置）：card.discarded 新增（载荷＝{ Card, Player }〔被弃卡在前、玩家在后——对齐"卡的动作事件"族：
/// card.played / card.hand.add / card.load〕；调用点＝弃置动作——销毁先于信号、恰一次；
/// 不携带原因/路径——信号只报事实）。
/// Kb（爆牌独立化与术语更名）：card.burned 新增（载荷＝{ Card, Player }——被爆卡在前、玩家在后；调用点＝爆牌路径
/// 〔满手裁决两路〕——销毁先于信号、恰一次；独立信号、不复用 card.discarded——爆牌不走弃牌路线）。
/// </summary>
public static class GameUpdates
{
    // ---------- 回合五连 ----------

    /// <summary>回合开始前（"turn.start.before"）：尚未结算、未抽牌。</summary>
    public const string TurnStartBefore = "turn.start.before";

    /// <summary>回合开始（"turn.start"）：回合开始信号；结算与抽牌随后执行（先于 after 通知）。</summary>
    public const string TurnStart = "turn.start";

    /// <summary>回合开始后（"turn.start.after"）：回合开始处理（结算＋抽牌）完成后发出。</summary>
    public const string TurnStartAfter = "turn.start.after";

    /// <summary>回合结束前（"turn.end.before"）。</summary>
    public const string TurnEndBefore = "turn.end.before";

    /// <summary>回合结束（"turn.end"）：其后执行切换当前方与回合数 +1（X3：点数保留——不清零）。</summary>
    public const string TurnEnd = "turn.end";

    // ---------- 通用三项 ----------

    /// <summary>
    /// 打牌（"card.played"；W4-1 升级版载荷＝{ Card, Player }——被使用卡实例＋使用方〔玩家对象引用〕）。
    /// 2B 打出链接线：部署/指令打出链发射，恰一次、先于后续结算；旧「空载荷＝硬约定」随升级废止
    /// （无载荷发射在升级语义下无定义）。监听方按「卡的定义」识别卡种（同名卡＝同一定义——G14 使用计数口径）。
    /// </summary>
    public const string CardPlayed = "card.played";

    /// <summary>抽牌（"card.drawn"；载荷＝{ 玩家, 卡牌实例 }）。</summary>
    public const string CardDrawn = "card.drawn";

    /// <summary>
    /// 数值更新（"card.stat.changed"；W2a 升级版载荷＝{ Card, ChangedFields }——目标卡＋本轮变化字段集合
    /// 〔字段标识、开放集合、稳定序〕）。语义＝「有变更才发、单条携带本轮全部变化字段、无变更零发射」；
    /// 旧「无载荷＝硬约定」随升级废止（无载荷发射在升级语义下无定义）。
    /// </summary>
    public const string CardStatChanged = "card.stat.changed";

    // ---------- 第二批 hooks：卡牌加载与手牌 ----------

    /// <summary>卡牌加载（"card.load"；载荷＝{ Card, Player }——Player＝所属卡组玩家）。对局初始化时对双方卡组逐张发射。</summary>
    public const string CardLoad = "card.load";

    /// <summary>
    /// 手牌加入（"card.hand.add"；载荷＝{ Card, Player }）：语义＝对局开始之后任意来源进手牌（含抽牌）；
    /// 与 card.drawn 并存、粒度不同（drawn＝抽牌动作信号、hand.add＝归属结果）；起手装载体不发（静默约定）。
    /// 本批调用点＝抽牌链路（drawn → hand.add 连发）；其他来源调用点随各流程批次接入。
    /// </summary>
    public const string CardHandAdd = "card.hand.add";

    /// <summary>
    /// 弃置（"card.discarded"；G7 新增；载荷＝{ Card, Player }——被弃卡实例在前、玩家在后〔对齐"卡的动作事件"族：
    /// card.played / card.hand.add / card.load〕；Player＝该卡被弃置时所归属的玩家）。
    /// 语义＝「某卡被弃置」——只报事实、不携带原因/路径（选择/随机/指定为调用方语义；弃置路径专属——
    /// 爆牌不走弃牌路线、经独立信号 <see cref="CardBurned"/>）。
    /// 次序＝移除 → 销毁（含资源清理）→ 本信号（动作完成后发信号、观察者所见即终态）；恰一次。
    /// </summary>
    public const string CardDiscarded = "card.discarded";

    /// <summary>
    /// 爆牌（"card.burned"；Kb 新增——合计第 18 条；载荷＝{ Card, Player }——被爆卡实例在前、玩家在后〔对齐"卡的动作事件"族〕；
    /// Player＝该卡被爆时所归属的玩家）。语义＝「某卡被爆（满手裁决）」——只报事实、不携带原因/路径
    /// （两路差异仅由是否发 card.drawn 体现）；次序＝销毁（含资源清理）→ 本信号（动作完成后发信号、观察者所见即终态）；恰一次；
    /// 独立信号、不复用 card.discarded（爆牌不走弃牌路线）。
    /// </summary>
    public const string CardBurned = "card.burned";

    /// <summary>
    /// 游戏层死亡（"card.died"；载荷＝{ Card }）。与引擎内置 <see cref="Updates.CardDestroyed"/>（"card.destroyed"）并存：
    /// 前者＝游戏层死亡（战斗数值链判定）、后者＝引擎销毁（装载链清理驱动）——近似名不同义，勿混用。
    /// 2C 起调用点＝战斗死亡流程（清理就绪后发射、恰一次；同归于尽＝两枚均发射、被攻击者在前）。
    /// </summary>
    public const string CardDied = "card.died";

    /// <summary>死亡已确认、效果卸载前的宿主结算阶段；载荷同 CardDied。</summary>
    public const string CardDying = "card.dying";

    /// <summary>单位即将离开战场（槽位释放之前）；载荷＝{ Card, Reason }。</summary>
    public const string CardLeaving = "card.leaving";

    /// <summary>词条临时增益到期相位（攻击收尾后）；载荷＝{ Card }。</summary>
    public const string KeywordAttackFinished = "keyword.attack.finished";

    // ---------- 第二批 hooks：单位系列 ----------

    /// <summary>单位加入（"unit.joined"；载荷＝{ Unit, Position }——Unit＝单位卡牌实例、Position＝槽位引用）：加入途径（非部署）入场。2B 加入路径调用点已接线（单位化完成后发射、恰一次）。</summary>
    public const string UnitJoined = "unit.joined";

    /// <summary>单位部署（"unit.deployed"；载荷＝{ Unit, Position }）：部署途径（打出部署链）入场。2B 部署链调用点已接线（单位化完成后发射、恰一次）。</summary>
    public const string UnitDeployed = "unit.deployed";

    /// <summary>单位位置变化（"unit.position.changed"；载荷＝{ Unit, OldPosition, NewPosition }——均为槽位引用）。2C 起调用点＝移动执行段（槽位变更后、收尾段之前）；守护维护由本更新驱动。</summary>
    public const string UnitPositionChanged = "unit.position.changed";

    /// <summary>
    /// 单位升级为老兵（"unit.upgraded"；S1 新增——合计第 30 条；载荷＝{ Unit }——升级完成单位〔复用
    /// <see cref="PayloadUnit"/> 键、不新设键；监听者从单位自读归属/数值/词条——「读取现势」惯例〕）。
    /// 语义＝「某单位完成了一次真实形态切换（升为老兵）」——**先落定后发射、恰一次**：全部结果（数值/词条/标记）
    /// 落定后统一发射；顺序＝先数值结果（<see cref="CardStatChanged"/>，如有变更）、后机制宣告（本信号）——
    /// 观察者所见即终态（形态＋数值均就绪）。调用点＝升级执行收尾（<c>UnitCard</c> 升级门户）；
    /// 幂等（已是老兵形态）/拒绝＝零发射（「未落定＝不发射」）。
    /// 消费用途＝「升为老兵时」「友方（敌方）单位升为老兵时」监听（跨卡监听判定＝监听者从单位自读归属）。
    /// </summary>
    public const string UnitUpgraded = "unit.upgraded";

    /// <summary>
    /// 单位被揭示（"unit.revealed"；S2 新增——合计第 31 条；载荷＝{ Unit }——被揭示单位〔复用
    /// <see cref="PayloadUnit"/> 键；不携归属——监听者读取现势自判，对齐 <see cref="UnitUpgraded"/> 惯例〕）。
    /// 语义＝「某单位完成了一次真实揭示（移除「隐蔽」标记）」——**先落定后发射、恰一次**：标记移除＋
    /// 本人揭示内容（「揭示触发器」）均生效后统一发射——观察者所见即终态（揭示全链含本人内容）。
    /// 调用点＝揭示服务收尾（<c>CovertRules.RevealAsync</c>——攻击执行段入口/主动揭示动作）；
    /// 幂等（已揭示/非隐蔽/未在场上/已死亡）＝零发射（「未落定＝不发射」）。「机制性清理」（死亡注销等）
    /// 不属揭示、不发本信号。消费用途＝「被揭示时」「友方（敌方）单位被揭示时」监听。
    /// </summary>
    public const string UnitRevealed = "unit.revealed";

    // ---------- E1-33（监听类补信号） ----------

    /// <summary>受到伤害（"card.damaged"；载荷＝{ Card, Amount }）。调用点＝伤害结算后（单位侧 <c>UnitCard.ApplyDefenseDamageAsync</c>、HQ 侧 <c>Hq.ApplyDamageAsync</c>）——实际扣减＞0、恰一次、先落定后发射。</summary>
    public const string CardDamaged = "card.damaged";

    /// <summary>单位行动后（"unit.acted"；载荷＝{ Unit }）。调用点＝攻击/移动外层收尾成功后（<c>CommandManager.DispatchAttackAsync</c> / <c>DispatchMoveAsync</c>）——各自恰一次。</summary>
    public const string UnitActed = "unit.acted";

    /// <summary>
    /// **造成伤害**（"unit.damage.dealt"；载荷＝{ Unit, Card, Amount }——**施动方单位**＋**受方**（单位或 HQ）＋实际伤害量）。
    /// 语义＝「某单位实际造成了伤害」（**来源侧**归属；与 <see cref="CardDamaged"/>「受方侧」互补）。
    /// 调用点＝①单位互伤（<c>CommandManager.HandleDefaultAttackDamageAsync</c> 双方各自）②HQ 简路（<c>HandleUnitAttackAsync</c>）
    /// ——**先落定后发射**、实际变化量 &gt; 0 才发（改变才传播）。
    /// </summary>
    public const string UnitDamageDealt = "unit.damage.dealt";

    /// <summary>
    /// **反制触发**（"counter.triggered"；载荷＝{ Card, Player }——被使用的反制卡＋其归属玩家）。
    /// 语义＝「某张反制被**使用/触发**」（<c>CounterCard.HandleUseCounterAsync</c> 的**激活**分支——恰一次）；
    /// 取消分支不发（退点撤销，不属"触发"）。
    /// </summary>
    public const string CounterTriggered = "counter.triggered";

    /// <summary>
    /// **反制解除**（"counter.disarmed"；载荷＝{ Card, Player, Reason }——被解除的反制卡＋归属玩家＋解除原因
    /// 〔本次固定 <c>"turn-start"</c>〕）。
    /// 语义＝「己方回合开始，反制武装窗口到期、自动解除」——与 <see cref="CounterTriggered"/> 是同一生命周期两端，
    /// 故成对登记为对外信号（只登记一端会迫使卡牌作者轮询补洞）。
    /// 与玩家主动取消不同：主动取消按实扣额退点且**不发**信号（退点撤销不属解除事实）；本信号只报「未命中且窗口到期」。
    /// 次序＝解除**之后**发射（注销 → 置未激活 → 清实扣额 → 本信号）：读者在信号时点即读到 <c>IsActive == false</c>；恰一次。
    /// </summary>
    public const string CounterDisarmed = "counter.disarmed";

    /// <summary>
    /// 交战并存活（"unit.combat.survived"；载荷＝{ Unit }——幸存单位）。
    /// 语义＝「一次单位对战结算结束后，**参战且未阵亡**的单位各发射一次」（每单位各自恰一次；观察序＝被攻击者在前、攻击者在后）。
    /// 调用点＝<c>CommandManager.HandleDefaultAttackDamageAsync</c> 结算收尾（含伏击改写路径——被攻击者不受伤即视为存活）。
    /// 已阵亡/未参战＝不发射（死亡判定之后才发——不误导"幸存"）。
    /// </summary>
    public const string UnitCombatSurvived = "unit.combat.survived";

    // ---------- W4-1（G14 收尾）洗切一项：卡组信号 ----------

    /// <summary>
    /// 卡组洗切（"deck.shuffled"；载荷＝{ Player, Deck }——被洗切卡组归属玩家〔Player 对象引用〕＋被洗切卡组〔<see cref="CardList"/> 对象引用〕）。
    /// 语义＝「某玩家的卡组发生了一次洗切」——单副卡组粒度、恰一次；初始化双方卡组洗切各一条（玩家索引升序）。
    /// 调用点＝洗切动作面（<see cref="Match.ShuffleDeckAsync"/>——初始化路径与后续「洗切卡组」类效果路径统一经此）。
    /// 「是否首次」不入信号——由监听者自持标志判断（私有闭环）；监听窗口自订阅/装载时点起、不回溯。
    /// </summary>
    public const string DeckShuffled = "deck.shuffled";

    // ---------- S9（G6+G13）类型变更一项：单位信号 ----------

    /// <summary>
    /// 单位类型变更（"unit.types.changed"；S9 新增——16→17 条；载荷＝{ Unit, AddedType }——被变更单位＋新增类型〔增量〕）。
    /// 语义＝「实际发生变更才发、恰一次」（重复增补/无变化＝零发射——「无变零发射」原则）；载荷为增量形态
    /// （完整类型集由监听者从单位读取——「读取现势」原则）。
    /// 调用点＝类型增补受控入口（<see cref="Cards.UnitCard.AddUnitTypeAsync"/>——运行时受控路径唯一）；
    /// 装配期初始化填充不发射（「初始化设定 ≠ 变更」——部署/加入/转换入场静默）。
    /// 消费用途＝供「类型条件类观察者」消费（如以类型为条件的持续效果/触发联动）；本批不建特定生产消费者
    /// （信号为机制面；验收在测试内监听）。
    /// </summary>
    public const string UnitTypesChanged = "unit.types.changed";

    // ---------- E1-25（指挥点槽事件改进）：资源信号三项 ----------

    /// <summary>
    /// 额外获得（"slot.gained"；载荷＝{ Player, Amount }——Amount＝**实际变化量** Δ）。
    /// 语义＝**语义前置**信号：槽实际增加才发（已在上限＝零发射）；其后接 <see cref="SlotChanged"/>（唯一「槽值真的变了」信号）。
    /// **回合开始的递增不走本信号**（<c>ResourceManager.SettleAsync</c> 直发 slot.changed）。
    /// </summary>
    public const string SlotGained = "slot.gained";

    /// <summary>
    /// 失去（"slot.lost"；载荷＝{ Player, Amount }——Amount＝**实际变化量** Δ）。
    /// 语义＝**语义前置**信号：槽实际减少才发（已为 0＝零发射）；其后接 <see cref="SlotChanged"/>。
    /// </summary>
    public const string SlotLost = "slot.lost";

    /// <summary>
    /// 指挥点槽改变（"slot.changed"；载荷＝{ Player, OldSlots, NewSlots }）。
    /// 语义＝**唯一**「槽值真的变了」的信号（槽值未变化＝零发射）；回合开始递增与额外获得/失去皆**汇聚**于此。
    /// </summary>
    public const string SlotChanged = "slot.changed";

    // ---------- E1-25 后续（指挥点事件改造）：点数信号三项 ----------

    /// <summary>
    /// 额外获得点数（"point.gained"；载荷＝{ Player, Amount }——Amount＝**实际变化量** Δ）。
    /// 语义＝**语义前置**信号：**卡效果**「获得 n 个指挥点」走此路（点数实际增加才发）；其后接 <see cref="PointChanged"/>。
    /// **游戏内通用来源**（打牌/指挥/反制扣费、回合开始设为）**不走**本信号（直发 point.changed）。
    /// </summary>
    public const string PointGained = "point.gained";

    /// <summary>
    /// 失去点数（"point.lost"；载荷＝{ Player, Amount }——Amount＝**实际变化量** Δ）。
    /// 语义＝**语义前置**信号：**卡效果**「失去 n 个指挥点」走此路；其后接 <see cref="PointChanged"/>。
    /// </summary>
    public const string PointLost = "point.lost";

    /// <summary>
    /// 指挥点改变（"point.changed"；载荷＝{ Player, OldPoints, NewPoints }）。
    /// 语义＝**唯一**「点数值真的变了」的信号（值未变化＝零发射）；通用入口 <c>ChangePointsAsync</c>（设为/增减）
    /// 与额外获得/失去皆**汇聚**于此。
    /// </summary>
    public const string PointChanged = "point.changed";

    // ---------- 载荷键（对外订阅契约；实现内部引用常量而非裸字符串） ----------

    /// <summary>载荷键：玩家（值＝<see cref="Player"/> 对象引用；turn 五连与 card.drawn 携带）。</summary>
    public const string PayloadPlayer = "Player";

    /// <summary>载荷键：回合数（值＝int；turn 五连携带）。</summary>
    public const string PayloadTurnNumber = "TurnNumber";

    /// <summary>载荷键：卡牌实例（值＝<see cref="Card"/> 对象引用；card.drawn / card.load / card.hand.add / card.died 携带）。</summary>
    public const string PayloadCard = "Card";

    /// <summary>载荷键：单位（值＝单位卡牌实例引用；unit.* 系列携带）。</summary>
    public const string PayloadUnit = "Unit";

    /// <summary>载荷键：词条标识（值＝string；keyword.* 系列携带）。</summary>
    public const string PayloadKeyword = "Keyword";

    /// <summary>载荷键：位置（值＝<see cref="Slot"/> 引用；unit.joined / unit.deployed 携带）。</summary>
    public const string PayloadPosition = "Position";

    /// <summary>载荷键：原位置（值＝<see cref="Slot"/> 引用；unit.position.changed 携带）。</summary>
    public const string PayloadOldPosition = "OldPosition";

    /// <summary>载荷键：新位置（值＝<see cref="Slot"/> 引用；unit.position.changed 携带）。</summary>
    public const string PayloadNewPosition = "NewPosition";

    /// <summary>载荷键：变化字段集合（值＝字段标识集合〔有序只读列表、稳定序〕；card.stat.changed 携带——W2a 升级版）。</summary>
    public const string PayloadChangedFields = "ChangedFields";

    /// <summary>载荷键：卡组（值＝<see cref="CardList"/> 对象引用；deck.shuffled 携带——被洗切卡组；与 <see cref="PayloadPlayer"/> 归属并列）。</summary>
    public const string PayloadDeck = "Deck";

    /// <summary>载荷键：新增类型（值＝<see cref="Cards.UnitType"/>；unit.types.changed 携带——本次增补的类型〔增量〕）。</summary>
    public const string PayloadAddedType = "AddedType";

    /// <summary>载荷键：数量（值＝int；slot.gained / slot.lost 携带——**实际变化量** Δ）。</summary>
    public const string PayloadAmount = "Amount";

    /// <summary>
    /// 载荷键：击杀者（值＝<see cref="Cards.UnitCard"/> 对象引用，可缺省/为 null——非归属驱动来源的死亡无击杀者）。
    /// <c>card.died</c> 携带（E1-47 加性：死亡＋**归属**）。
    /// </summary>
    public const string PayloadKiller = "Killer";

    /// <summary>载荷键：原槽值（值＝int；slot.changed 携带）。</summary>
    public const string PayloadOldSlots = "OldSlots";

    /// <summary>载荷键：新槽值（值＝int；slot.changed 携带）。</summary>
    public const string PayloadNewSlots = "NewSlots";

    /// <summary>载荷键：原点数（值＝int；point.changed 携带）。</summary>
    public const string PayloadOldPoints = "OldPoints";

    /// <summary>载荷键：新点数（值＝int；point.changed 携带）。</summary>
    public const string PayloadNewPoints = "NewPoints";

    /// <summary>
    /// 载荷键：来源（R9；值＝<see cref="Card"/> 对象引用，可空）：**事件引发者**——恒写、缺省 null
    /// （「无引发者」与「键缺失」不混淆）。回合五连与玩家主动操作恒 null；
    /// 与视图属性 <c>TriggerCard</c>（当前 handler 的触发者，链上最后者胜）分工不同。
    /// </summary>
    public const string PayloadSource = "Source";

    // ---------- 发射助手（可选便捷层；统一走总线 Emit 通路） ----------

    /// <summary>发射 card.played（W4-1 升级版；载荷＝{ Card, Player }——被使用卡实例＋使用方；部署/指令打出链调用点）。</summary>
    /// <exception cref="ArgumentNullException">engine / card / player 为 null。</exception>
    public static Task EmitCardPlayed(
        LogicEngine engine, Card card, Player player, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            CardPlayed,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                [PayloadPlayer] = player,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 deck.shuffled（载荷＝{ Player, Deck }；洗切动作面统一使用——初始化双方卡组洗切各一条、恰一次）。</summary>
    /// <exception cref="ArgumentNullException">engine / player / deck 为 null。</exception>
    public static Task EmitDeckShuffled(
        LogicEngine engine, Player player, CardList deck, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(deck);
        return engine.Emit(
            DeckShuffled,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadDeck] = deck,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>
    /// 发射 unit.types.changed（S9；载荷＝{ Unit, AddedType }——被变更单位＋新增类型〔增量〕）。
    /// 语义＝实际发生变更才发（无变化零发射——调用方不得在无变化时调用；恰一次）。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine / unit 为 null。</exception>
    public static Task EmitUnitTypesChanged(
        LogicEngine engine, Card unit, string addedType, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        return engine.Emit(
            UnitTypesChanged,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadAddedType] = addedType,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>
    /// 发射 card.stat.changed（W2a 升级版；载荷＝{ Card, ChangedFields }——目标卡＋本轮变化字段集合）。
    /// 语义＝有变更才发（无变更零发射——调用方不得以空集合调用）；单条携带本轮全部变化字段。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine / card / changedFields 为 null。</exception>
    /// <exception cref="ArgumentException">changedFields 为空集合（无变更不发——空集合调用属逻辑错误）。</exception>
    public static Task EmitCardStatChanged(
        LogicEngine engine, Card card, IReadOnlyList<string> changedFields, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(changedFields);
        if (changedFields.Count == 0)
        {
            throw new ArgumentException(
                "变化字段集合为空（card.stat.changed 为「有变更才发」——空集合调用属逻辑错误）。", nameof(changedFields));
        }

        return engine.Emit(
            CardStatChanged,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                // 载荷值归一到封闭内建类型（视图声明的 ChangedFields 为 string[]）：
                // 传入 IReadOnlyList<string> 时物化为数组，避免"声明是数组、实际是列表"导致投影期类型不符。
                [PayloadChangedFields] = changedFields as string[] ?? changedFields.ToArray(),
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 card.drawn（载荷＝{ 玩家, 卡牌实例 }；抽牌链路实际使用）。</summary>
    public static Task EmitCardDrawn(LogicEngine engine, Player player, Card card, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(
            CardDrawn,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadCard] = card,
                [PayloadSource] = source,
            },
            ct);
    }

    // ---------- 发射助手（第二批 hooks；统一走总线 Emit 通路） ----------

    /// <summary>发射 card.placed（内核装载链「放置驱动」契约：载荷＝{ Card }——卡牌对象引用；
    /// W3-A3 起调用点＝部署链/加入链〔单位化成功后、完成信号 unit.deployed / unit.joined 之前〕；
    /// 加载时点不发射——放置＝「上场」语义。消费＝装载链放置处理器〔初始化＋装载幂等兜底〕＋策略 1 效果订阅作上场驱动）。</summary>
    /// <exception cref="ArgumentNullException">engine / card 为 null。</exception>
    public static Task EmitCardPlaced(LogicEngine engine, Card card, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(
            Updates.CardPlaced,
            new Dictionary<string, object?>
            {
                [PayloadKeys.Card] = card,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 card.load（载荷＝{ 玩家, 卡牌实例 }；对局初始化逐张加载实际使用）。</summary>
    public static Task EmitCardLoad(LogicEngine engine, Player player, Card card, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(
            CardLoad,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadCard] = card,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 card.hand.add（载荷＝{ 玩家, 卡牌实例 }；抽牌链路实际使用——drawn 之后连发；其他来源调用点随各流程批次）。</summary>
    public static Task EmitCardHandAdd(LogicEngine engine, Player player, Card card, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(
            CardHandAdd,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadCard] = card,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 card.discarded（G7；载荷＝{ Card, Player }——被弃卡实例在前、玩家在后；弃置路径专用——销毁先于信号、恰一次）。</summary>
    /// <exception cref="ArgumentNullException">engine / card / player 为 null。</exception>
    public static Task EmitCardDiscarded(
        LogicEngine engine, Card card, Player player, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            CardDiscarded,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                [PayloadPlayer] = player,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 card.burned（Kb；载荷＝{ Card, Player }——被爆卡实例在前、玩家在后；爆牌路径专用——销毁先于信号、恰一次；不复用 card.discarded）。</summary>
    /// <exception cref="ArgumentNullException">engine / card / player 为 null。</exception>
    public static Task EmitCardBurned(
        LogicEngine engine, Card card, Player player, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            CardBurned,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                [PayloadPlayer] = player,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>死亡已确认、卸载前结算宿主效果；载荷同完成死亡事件。</summary>
    public static Task EmitCardDying(LogicEngine engine, Card card, Card? killer = null, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(CardDying, new Dictionary<string, object?>
        {
            [PayloadCard] = card,
            [PayloadKiller] = killer,
            [PayloadSource] = source,
        }, ct);
    }

    /// <summary>发射 card.leaving（载荷＝{ Card, Reason }；槽位释放之前，恰一次）。</summary>
    public static Task EmitCardLeaving(LogicEngine engine, Card card, string reason, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(CardLeaving, new Dictionary<string, object?>
        {
            [PayloadCard] = card,
            ["Reason"] = reason,
            [PayloadSource] = source,
        }, ct);
    }

    /// <summary>发射 keyword.attack.finished（载荷＝{ Card }；词条临时增益的到期相位）。</summary>
    public static Task EmitKeywordAttackFinished(LogicEngine engine, Card card, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(KeywordAttackFinished, new Dictionary<string, object?>
        {
            [PayloadCard] = card,
            [PayloadSource] = source,
        }, ct);
    }

    /// <summary>发射 card.died（载荷＝{ 卡牌实例, Killer? }；2C 起调用点＝战斗死亡流程——清理就绪后、恰一次）。</summary>
    /// <param name="killer">击杀者（E1-47 加性；null＝非归属驱动来源，如修饰到期致防御归零）。</param>
    public static Task EmitCardDied(LogicEngine engine, Card card, Card? killer = null, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(
            CardDied,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                [PayloadKiller] = killer,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 counter.triggered（E1-53；载荷＝{ 反制卡, 归属玩家 }；激活分支调用一次）。</summary>
    /// <exception cref="ArgumentNullException">engine / card / player 为 null。</exception>
    public static Task EmitCounterTriggered(LogicEngine engine, Card card, Player player, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            CounterTriggered,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                [PayloadPlayer] = player,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>
    /// 发射 counter.disarmed（R1.3；载荷＝{ 反制卡, 归属玩家, Reason }——解除落定后调用、恰一次）。
    /// 与 <see cref="EmitCounterTriggered"/> 对称：Source 口径一致（＝反制卡自身）。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine / card / player / reason 为 null。</exception>
    public static Task EmitCounterDisarmed(
        LogicEngine engine, Card card, Player player, string reason, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(reason);
        return engine.Emit(
            CounterDisarmed,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                [PayloadPlayer] = player,
                // 键名对齐既有 card.leaving（"Reason"）——载荷键族一律 PascalCase。
                ["Reason"] = reason,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.damage.dealt（E1-47；载荷＝{ 施动方, 受方, 伤害量 }；实际变化量 &gt; 0 才发）。</summary>
    /// <exception cref="ArgumentNullException">engine / unit / target 为 null。</exception>
    public static Task EmitUnitDamageDealt(
        LogicEngine engine, Card unit, Card target, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(target);
        return engine.Emit(
            UnitDamageDealt,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadCard] = target,
                [PayloadAmount] = amount,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 card.damaged（载荷＝{ 卡牌实例, 伤害量 }；伤害结算后调用——单位/HQ 各自路径、恰一次）。</summary>
    /// <exception cref="ArgumentNullException">engine / card 为 null。</exception>
    public static Task EmitCardDamaged(LogicEngine engine, Card card, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(card);
        return engine.Emit(
            CardDamaged,
            new Dictionary<string, object?>
            {
                [PayloadCard] = card,
                [PayloadAmount] = amount,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.acted（载荷＝{ 单位 }；攻击/移动外层收尾成功后调用——各自恰一次）。</summary>
    /// <exception cref="ArgumentNullException">engine / unit 为 null。</exception>
    public static Task EmitUnitActed(LogicEngine engine, Card unit, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        return engine.Emit(
            UnitActed,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.combat.survived（载荷＝{ 单位 }；对战结算收尾后对每个幸存参战单位调用——各自恰一次）。</summary>
    /// <exception cref="ArgumentNullException">engine / unit 为 null。</exception>
    public static Task EmitUnitCombatSurvived(LogicEngine engine, Card unit, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        return engine.Emit(
            UnitCombatSurvived,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.joined（载荷＝{ 单位, 位置 }；2B 加入路径调用点——单位化完成后）。</summary>
    public static Task EmitUnitJoined(LogicEngine engine, Card unit, BoardPosition position, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(position);
        return engine.Emit(
            UnitJoined,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadPosition] = position,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.deployed（载荷＝{ 单位, 位置 }；2B 部署链调用点——单位化完成后）。</summary>
    public static Task EmitUnitDeployed(LogicEngine engine, Card unit, BoardPosition position, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(position);
        return engine.Emit(
            UnitDeployed,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadPosition] = position,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.position.changed（载荷＝{ 单位, 原位置, 新位置 }；2C 起调用点＝移动执行段——位置变更后、恰一次）。</summary>
    public static Task EmitUnitPositionChanged(
        LogicEngine engine, Card unit, BoardPosition oldPosition, BoardPosition newPosition, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(oldPosition);
        ArgumentNullException.ThrowIfNull(newPosition);
        return engine.Emit(
            UnitPositionChanged,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadOldPosition] = oldPosition,
                [PayloadNewPosition] = newPosition,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.upgraded（S1；载荷＝{ Unit }——升级完成单位；升级落定后调用、恰一次——先数值结果、后机制宣告）。</summary>
    /// <exception cref="ArgumentNullException">engine / unit 为 null。</exception>
    public static Task EmitUnitUpgraded(LogicEngine engine, Card unit, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        return engine.Emit(
            UnitUpgraded,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 unit.revealed（S2；载荷＝{ Unit }——被揭示单位；揭示落定后调用、恰一次——先落定、后发射）。</summary>
    /// <exception cref="ArgumentNullException">engine / unit 为 null。</exception>
    public static Task EmitUnitRevealed(LogicEngine engine, Card unit, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        return engine.Emit(
            UnitRevealed,
            new Dictionary<string, object?>
            {
                [PayloadUnit] = unit,
                [PayloadSource] = source,
            },
            ct);
    }

    // ---------- 发射助手（E1-25 指挥点槽事件；统一走总线 Emit 通路） ----------

    /// <summary>发射 slot.gained（载荷＝{ Player, Amount }——Amount＝实际变化量 Δ；槽实际增加才发、其后接 slot.changed）。</summary>
    /// <exception cref="ArgumentNullException">engine / player 为 null。</exception>
    public static Task EmitSlotGained(
        LogicEngine engine, Player player, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            SlotGained,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadAmount] = amount,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 slot.lost（载荷＝{ Player, Amount }——Amount＝实际变化量 Δ；槽实际减少才发、其后接 slot.changed）。</summary>
    /// <exception cref="ArgumentNullException">engine / player 为 null。</exception>
    public static Task EmitSlotLost(
        LogicEngine engine, Player player, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            SlotLost,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadAmount] = amount,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>
    /// 发射 slot.changed（载荷＝{ Player, OldSlots, NewSlots }——唯一「槽值真的变了」的信号）。
    /// 语义＝槽值实际变化才发（无变化零发射——调用方不得在无变化时调用）。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine / player 为 null。</exception>
    public static Task EmitSlotChanged(
        LogicEngine engine, Player player, int oldSlots, int newSlots, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            SlotChanged,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadOldSlots] = oldSlots,
                [PayloadNewSlots] = newSlots,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 point.gained（载荷＝{ Player, Amount }——Amount＝实际变化量 Δ；点数实际增加才发、其后接 point.changed）。</summary>
    /// <exception cref="ArgumentNullException">engine / player 为 null。</exception>
    public static Task EmitPointGained(
        LogicEngine engine, Player player, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            PointGained,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadAmount] = amount,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>发射 point.lost（载荷＝{ Player, Amount }——Amount＝实际变化量 Δ；点数实际减少才发、其后接 point.changed）。</summary>
    /// <exception cref="ArgumentNullException">engine / player 为 null。</exception>
    public static Task EmitPointLost(
        LogicEngine engine, Player player, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            PointLost,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadAmount] = amount,
                [PayloadSource] = source,
            },
            ct);
    }

    /// <summary>
    /// 发射 point.changed（载荷＝{ Player, OldPoints, NewPoints }——唯一「点数值真的变了」的信号）。
    /// 语义＝点数值实际变化才发（无变化零发射——调用方不得在无变化时调用）。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine / player 为 null。</exception>
    public static Task EmitPointChanged(
        LogicEngine engine, Player player, int oldPoints, int newPoints, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        return engine.Emit(
            PointChanged,
            new Dictionary<string, object?>
            {
                [PayloadPlayer] = player,
                [PayloadOldPoints] = oldPoints,
                [PayloadNewPoints] = newPoints,
                [PayloadSource] = source,
            },
            ct);
    }
}
