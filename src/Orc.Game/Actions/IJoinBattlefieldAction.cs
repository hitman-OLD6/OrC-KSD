using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 加入战场动作（S9；S10 动作接口）：两个**触发端**（部署／加入）＋恰一个**共享处理端**。
/// <para>
/// 处理端只做「单位化（单位数据／指挥组件／槽位／跑链）→ <c>card.placed</c>」，天然无入口类型分支；
/// <c>unit.deployed</c>／<c>unit.joined</c>、扣部署费、离手、<c>OnDeployChainFinalized</c> 属**入口收尾**。
/// </para>
/// </summary>
public interface IJoinBattlefieldAction
{
    /// <summary>部署**触发端**。</summary>
    Trigger<JoinBattlefieldTriggerView> Deploy { get; }

    /// <summary>加入**触发端**。</summary>
    Trigger<JoinBattlefieldTriggerView> Join { get; }

    /// <summary>共享**处理端**（单位化 + 落位 + <c>card.placed</c>）。</summary>
    Trigger<JoinBattlefieldHandleView> Handle { get; }

    /// <summary>部署入口（代价校验 → <c>card.played</c> → 部署词条 → 共享处理端）。</summary>
    Task<ActionResult> DeployAsync(
        UnitCard card, Slot slot, Player player, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>加入入口（前置校验 → 共享处理端；无宣告、无部署词条）。</summary>
    Task<ActionResult> JoinAsync(
        UnitCard card, Slot slot, Card? triggerCard = null, CancellationToken ct = default);
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
    /// 落地「单位化 + 落位 + <c>card.placed</c>」（唯一落地口）：加单位组件（位置＝槽位、已毁＝false、
    /// 类型＝从定义填充）＋指挥组件＋实际占用空槽＋跑链一次（建立修饰基线）→ 发射 <c>card.placed</c>。
    /// 结构不变量不满足（槽位非空／已单位化）＝<c>false</c>、零落地。
    /// </summary>
    Task<bool> UnitizeOnJoinAsync(
        UnitCard card, Slot slot, Card? triggerCard, CancellationToken ct = default);

    /// <summary>触发宿主单位的「部署词条触发器」（部署触发端 <c>Commit</c> 前部；无注入＝空转）。</summary>
    Task InvokeDeployKeywordAsync(
        UnitCard card, Player? player, Slot slot, CancellationToken ct = default);
}
