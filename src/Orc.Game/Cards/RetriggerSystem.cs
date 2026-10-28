using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Players;
using Orc.Game.Triggers;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 第 2 批·A4 再触发体系（口径依据《需求文档（A4）》Q&A-3/4/7）：
// 针对性语义＋经事件总线包装底层触发器：发布「触发请求」（参数＝目标卡＋链类型）→ 接收触发器（被动、挂载更新总线，
// 与归零检查触发器同构）→ 按参数驱动该卡相关效果再触发；其余触发逻辑照走总线主动触发器（现状模式不变）。
// 链类型参数化（部署/亡计）——部署＝重放「部署效果段」（单源：DeploymentLogicRules——与部署链①段共享）；
// 亡计＝亡计执行面驱动（单源：DeathrattleRules——死亡结算与再触发共用）。
// 请求更新＝机制内部导航（非对外冻结契约——不列入对外订阅契约清单）；发布—接收—执行同一同步链（P2；返回即已完成）。
// 门禁（终局零副作用）以执行/接收环节为兜底保证点；非法输入分层（结构性非法＝防御异常；状态失效＝忽略）；
// 最小自重入防护（同卡同链执行中重入＝跳过并记录——防自触发循环；跨卡循环终止性＝卡面/作者责任）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>再触发链类型（参数化；可扩展——未来其他链）。</summary>
public enum RetriggerChain
{
    /// <summary>部署链：重放该卡的「部署效果段」（<see cref="DeploymentLogicData"/> 有效条目按登记序；不含单位化/入槽/链级信号/打出收尾）。</summary>
    Deploy,

    /// <summary>亡计链：亡计执行面驱动（解析宿主卡亡计词条 → 驱动其内部效果动作一次——与死亡结算共用同一执行面）。</summary>
    Deathrattle,
}

/// <summary>
/// 再触发请求视图（内部导航载荷；数据键＝属性名）：目标卡（<see cref="Target"/>——单位卡对象引用）
/// ＋链类型（<see cref="Chain"/>——<see cref="RetriggerChain"/> 值）。全可选（解析侧防御处理）。
/// </summary>
[ContextView]
public class RetriggerRequestView
{
    /// <summary>目标卡引用（单位卡对象引用）。</summary>
    [Optional]
    [Read]
    public virtual object? Target { get; set; }

    /// <summary>链类型（<see cref="RetriggerChain"/> 值）。</summary>
    [Optional]
    [Read]
    public virtual object? Chain { get; set; }
}

/// <summary>
/// 再触发系统（A4；对局级服务——随对局装配创建）：发布面＋接收触发器（包装底层触发器）＋执行面驱动＋重入防护。
/// 语义（Q&A-3/4/7）：单卡请求（批量＝调用方遍历）；发布—接收—执行同一同步链（返回即已完成）；
/// 目标集筛选/遍历责任在调用方（效果侧）；仅「在场存活」对象有意义（状态失效＝忽略）；
/// 再触发仅执行效果动作、不发射链级信号（unit.deployed/card.placed/card.died 等均不发）。
/// 非幂等：同一卡同链类型的请求重复发布＝各执行一次（「再触发是动作而非状态确保」）；
/// 恰一次保证仅属「单次请求的接收-执行」＋执行中重入防护（该链在自身执行未完成期间收到的同卡同链重入＝跳过并记录）。
/// 重复/冲突对齐既有语义：目标卡非单位卡＝入口参数类型收窄（编译期排除——申报对齐，运行期不可达）。
/// </summary>
public sealed class RetriggerSystem
{
    /// <summary>请求更新字符串（机制内部导航——非对外冻结契约；Q&A-4 第 4 点）。</summary>
    internal const string RequestUpdate = "retrigger.request";

    /// <summary>载荷键：目标卡（值＝<see cref="UnitCard"/> 对象引用）。</summary>
    internal const string PayloadTargetKey = "Target";

    /// <summary>载荷键：链类型（值＝<see cref="RetriggerChain"/> 值）。</summary>
    internal const string PayloadChainKey = "Chain";

    private readonly LogicEngine _engine;
    private readonly MatchLifecycle? _lifecycle;
    private readonly HashSet<UnitCard> _deployExecuting = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<UnitCard> _deathrattleExecuting = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 创建再触发系统（创建接收触发器并挂载更新总线——「包装底层触发器」，与归零检查触发器同构）。
    /// </summary>
    /// <param name="engine">对局引擎（发射请求更新 / 执行面驱动）。</param>
    /// <param name="lifecycle">对局生命周期（终局门禁——执行/接收环节兜底保证点；缺省＝null＝独立构造场景无门禁）。</param>
    /// <exception cref="ArgumentNullException">engine 为 null。</exception>
    internal RetriggerSystem(LogicEngine engine, MatchLifecycle? lifecycle)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _lifecycle = lifecycle;

        // 请求以一条（内部）更新/请求消息发布 → 信号成员上的边接收 → 解析参数 → 驱动对应执行面
        // （顺序化、可留痕；§27 后不再有独立触发器实例）。
        _engine.SubscribeMember(
            RequestUpdate,
            "再触发接收触发器",
            this,
            MemberEdges.For<RetriggerRequestView>(HandleRequestAsync));
    }

    /// <summary>接收面身份（信号成员地址 <c>engine#retrigger.request</c>；诊断读面）。</summary>
    public MemberAddress RequestTrigger => MemberAddress.Create(AddressScope.Engine, RequestUpdate);

    /// <summary>
    /// 卡 → 再触发服务解析（读取路径「卡 → 玩家 → 服务」的收敛点；与随机服务/环境解析同构）：
    /// 卡经归属玩家取服务；未加载（无归属）/独立构造（未注入）/非卡实体＝null（不可达——调用侧按「功能不可用、不抛错」处置）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public static RetriggerSystem? ResolveFor(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card switch
        {
            Hq hq => hq.Owner.RetriggerService,
            CardBase cardBase => cardBase.Owner?.RetriggerService,
            _ => null,
        };
    }

    /// <summary>
    /// 发布再触发请求（单卡；发布—接收—执行同一同步链——返回即已完成处理）。
    /// 结构性非法＝明确异常（防御拒绝：目标卡为 null / 未知链类型值；目标卡非单位卡＝入口参数类型收窄为编译期排除——
    /// 申报对齐）；终局门禁＝拒绝（返回 false、零副作用；接收环节为兜底保证点）。
    /// </summary>
    /// <param name="target">目标卡（单位卡——参数类型收窄）。</param>
    /// <param name="chain">链类型（部署/亡计）。</param>
    /// <returns>true＝请求已完成处理（执行/按语义忽略）；false＝终局门禁拒绝（未处理）。</returns>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">未知链类型值（枚举形态下 cast 可达——防御拒绝）。</exception>
    public async Task<bool> RequestAsync(UnitCard target, RetriggerChain chain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(chain))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chain), chain, "未知链类型值（防御拒绝——结构性非法）。");
        }

        if (_lifecycle?.IsEnded == true)
        {
            return false; // 终局：不执行、零副作用（发布入口防御；接收环节兜底）
        }

        await _engine.Emit(
            RequestUpdate,
            new Dictionary<string, object?>
            {
                [PayloadTargetKey] = target,
                [PayloadChainKey] = chain,
            },
            ct);
        return true;
    }

    /// <summary>
    /// 亡计执行面（防护统一入口；死亡结算与再触发共用——Q&A-2 单源化）：
    /// 驱动宿主卡亡计词条内部效果动作一次；同卡同链执行中重入＝跳过并记录（防自触发循环）。
    /// </summary>
    internal async Task ExecuteDeathrattleAsync(UnitCard unit, CancellationToken ct)
    {
        if (!_deathrattleExecuting.Add(unit))
        {
            RecordReentrySkip(unit, RetriggerChain.Deathrattle);
            return; // 执行中重入：跳过并记录
        }

        try
        {
            await DeathrattleRules.ExecuteAsync(unit, _engine, ct);
        }
        finally
        {
            _deathrattleExecuting.Remove(unit);
        }
    }

    /// <summary>
    /// 部署重放执行面（防护入口）：重放该卡的部署效果段（单源——与部署链①段共享同一执行实现）。
    /// 同卡同链执行中重入＝跳过并记录。
    /// </summary>
    internal async Task ExecuteDeployReplayAsync(UnitCard unit, CancellationToken ct)
    {
        if (!_deployExecuting.Add(unit))
        {
            RecordReentrySkip(unit, RetriggerChain.Deploy);
            return; // 执行中重入：跳过并记录
        }

        try
        {
            if (!unit.TryGetData<UnitStateData>(out var state) || state.Line is not { } line)
            {
                return; // 防御（接收侧已校验「在场存活」）：非在场＝无操作
            }

            // 位置上下文＝当前快照中「该单位所在位置序号」的插入点（现取现用；Ordinal 即位置序号）。
            // 位置模型重构时此处曾退化为 null，导致重放上下文丢失位置信息（Slot 无对应物≠可以不给位置）。
            var position = line.GetInsertionGaps().FirstOrDefault(gap => gap.Ordinal == line.IndexOf(unit));
            await unit.InvokeDeployKeywordAsync(unit.Owner, position, ct);
        }
        finally
        {
            _deployExecuting.Remove(unit);
        }
    }

    /// <summary>接收处理（总线触发器阶段）：解析参数 → 门禁兜底 → 状态失效忽略 → 驱动对应执行面。</summary>
    private async Task HandleRequestAsync(RetriggerRequestView view, Context ctx, CancellationToken ct)
    {
        if (view.Target is not UnitCard target || view.Chain is not RetriggerChain chain)
        {
            _engine.RootStream.WriteLog(
                "再触发",
                "请求载荷无效（防御拒绝——结构性非法：目标/链类型缺失或类型不符）。",
                LogLevel.Warning,
                new[] { "retrigger", "invalid-request" });
            return;
        }

        if (!Enum.IsDefined(chain))
        {
            _engine.RootStream.WriteLog(
                "再触发",
                $"请求载荷无效（防御拒绝——未知链类型值 '{chain}'）。",
                LogLevel.Warning,
                new[] { "retrigger", "invalid-request" });
            return;
        }

        if (_lifecycle?.IsEnded == true)
        {
            return; // 门禁兜底（零副作用——执行/接收环节为兜底保证点）
        }

        if (!IsOnFieldAlive(target))
        {
            return; // 状态失效＝忽略（已离场/已死亡/非在场——无操作、不报错；「触发所有」遍历安全）
        }

        switch (chain)
        {
            case RetriggerChain.Deploy:
                await ExecuteDeployReplayAsync(target, ct);
                break;
            case RetriggerChain.Deathrattle:
                await ExecuteDeathrattleAsync(target, ct);
                break;
        }
    }

    /// <summary>「在场存活」判定（状态失效忽略的判据）：已单位化 ∧ 未死亡 ∧ 有位置（未离场）。</summary>
    private static bool IsOnFieldAlive(UnitCard unit)
        => unit.TryGetData<UnitStateData>(out var state)
            && !state.IsDestroyed
            && state.Line is not null;

    /// <summary>重入跳过记录（「跳过并记录」——记录形态：事件流条目；source「再触发」）。</summary>
    private void RecordReentrySkip(UnitCard unit, RetriggerChain chain)
    {
        _engine.RootStream.WriteLog(
            "再触发",
            $"同卡同链执行中重入：跳过（'{unit.Name}'；链：{chain}）——防自触发循环。",
            LogLevel.Warning,
            new[] { "retrigger", "reentry-skip", chain.ToString() });
    }
}

/// <summary>
/// 再触发规则服务（A4；效果作者可达的发布入口面——静态便利口）：
/// 「触发所有友方单位的部署/亡计」＝效果作者经本面按调用方遍历序逐卡发起（单卡一条请求；目标集筛选/遍历责任在调用方）。
/// 发布—接收—执行同一同步链（返回即已完成处理）；无服务面（独立构造/未装配）＝防御跳过（不抛错、功能不可用）。
/// </summary>
public static class RetriggerRules
{
    /// <summary>
    /// 发布「部署再触发」请求（单卡；返回＝请求已完成处理——false＝终局门禁拒绝）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public static Task<bool> RequestDeployAsync(UnitCard target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var service = RetriggerSystem.ResolveFor(target);
        return service is null
            ? Task.FromResult(false) // 无服务面：防御跳过（不抛错、功能不可用）
            : service.RequestAsync(target, RetriggerChain.Deploy, ct);
    }

    /// <summary>
    /// 发布「亡计再触发」请求（单卡；返回＝请求已完成处理——false＝终局门禁拒绝）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public static Task<bool> RequestDeathrattleAsync(UnitCard target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var service = RetriggerSystem.ResolveFor(target);
        return service is null
            ? Task.FromResult(false)
            : service.RequestAsync(target, RetriggerChain.Deathrattle, ct);
    }
}
