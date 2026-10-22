using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 加入战场动作（S9；有序列表模型）：**三个触发端**（部署／加入前线／加入支援阵线）＋恰一个**共享处理端**。
/// <para>
/// 处理端只做「单位化（单位数据／指挥组件／插入阵线／跑链）→ <c>card.placed</c>」，天然无入口类型分支；
/// <c>unit.deployed</c>／<c>unit.joined</c>、扣部署费、离手、<c>OnDeployChainFinalized</c> 属**入口收尾**。
/// </para>
/// </summary>
public interface IJoinBattlefieldAction
{
    /// <summary>部署**触发端**（打出链）。</summary>
    Trigger<JoinBattlefieldTriggerView> Deploy { get; }

    /// <summary>加入前线**触发端**（效果侧）：前线存在存活敌方单位＝拒绝（Error 留痕）。</summary>
    Trigger<JoinBattlefieldTriggerView> JoinFrontline { get; }

    /// <summary>加入支援阵线**触发端**（效果侧）。</summary>
    Trigger<JoinBattlefieldTriggerView> JoinSupportLine { get; }

    /// <summary>共享**处理端**（单位化 + 插入阵线 + <c>card.placed</c>）。</summary>
    Trigger<JoinBattlefieldHandleView> Handle { get; }

    /// <summary>部署入口（代价校验 → <c>card.played</c> → 部署词条 → 共享处理端）。</summary>
    Task<ActionResult> DeployAsync(
        UnitCard card, FormationGap gap, Player player, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>加入前线入口（门禁 → 落位 → 共享处理端；无宣告、无部署词条）。</summary>
    /// <param name="index">显式目标序号（插入后的位置）；null＝按默认规则（守护＝HQ 右侧，否则最左）。</param>
    Task<ActionResult> JoinFrontlineAsync(
        UnitCard card, int? index = null, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>加入支援阵线入口（落位 → 共享处理端；无宣告、无部署词条）。</summary>
    /// <param name="index">显式目标序号（插入后的位置）；null＝按默认规则（守护＝HQ 右侧，否则最左）。</param>
    Task<ActionResult> JoinSupportLineAsync(
        UnitCard card, int? index = null, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>按**已确定空隙**加入（内部共享执行入口：两个加入触发端与卡侧加入链共用；不做落位解析、不设门禁）。</summary>
    Task<ActionResult> JoinAtAsync(
        UnitCard card, FormationGap gap, Card? triggerCard = null, CancellationToken ct = default);
}

/// <summary>加入战场的落位失败原因（不抛；调用方据以留痕/判别）。</summary>
public enum JoinPlacementFailure
{
    /// <summary>无失败。</summary>
    None = 0,

    /// <summary>目标阵线已满（元素数＝容量）。</summary>
    LineFull = 1,

    /// <summary>显式序号越界（0..元素数）。</summary>
    IndexOutOfRange = 2,

    /// <summary>前线被敌方存活单位占据（加入前线的前置不满足）。</summary>
    EnemyHoldsFrontline = 3,

    /// <summary>单位已入场（已单位化）。</summary>
    AlreadyOnBoard = 4,
}

/// <summary>
/// 加入战场的**动作服务面**（S4）：两端的默认 handler 访问对局的受控写面——具名操作、不交出管理器实例。
/// <para>
/// 唯一调用者＝加入战场动作默认 handler（部署触发端 <c>Commit</c> 的部署词条步；处理端 <c>Apply</c> 的单位化步）。
/// 信号发射权按动作归属：本接口只发 <c>card.placed</c>；<c>card.played</c> 由触发端宣告步发、完成信号由入口发。
/// </para>
/// </summary>
public interface IJoinActionServices
{
    /// <summary>
    /// 落地「单位化 + 插入阵线 + <c>card.placed</c>」（唯一落地口）：加单位组件（所在线＝空隙所属线、已毁＝false、
    /// 类型＝从定义填充）＋指挥组件＋按空隙插入＋跑链一次（建立修饰基线）→ 发射 <c>card.placed</c>。
    /// 结构不变量不满足（空隙失效／已单位化）＝<c>false</c>、零落地。
    /// </summary>
    Task<bool> UnitizeOnJoinAsync(
        UnitCard card, FormationGap gap, Card? triggerCard, CancellationToken ct = default);

    /// <summary>触发宿主单位的「部署词条触发器」（部署触发端 <c>Commit</c> 前部；无注入＝空转）。</summary>
    Task InvokeDeployKeywordAsync(
        UnitCard card, Player? player, FormationGap gap, CancellationToken ct = default);

    /// <summary>
    /// 按落位规则解析目标空隙（优先级：显式序号 &gt; 守护（HQ 右侧，仅支援线）&gt; 默认最左）。
    /// 不可用＝null＋<paramref name="failure"/> 给出原因（不抛）。
    /// </summary>
    FormationGap? ResolveJoinGap(
        UnitCard card, string zone, int? index, out JoinPlacementFailure failure);

    /// <summary>前线是否被存活敌方单位占据（复用判定器 <c>move.frontline-enemy</c> 的单一真源）。</summary>
    bool EnemyHoldsFrontline(Player? owner);
}
