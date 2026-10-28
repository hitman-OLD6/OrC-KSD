using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Triggers;
using Orc.Game.Ui;

namespace Orc.Game.Plugins;

/// <summary>
/// 对局只读服务面（P10 甲）：供插件构造**需对局协作者**的判定器/资产服务。
/// 访问器一律为**延后求值**（装配期即可安全持有——协作者可能晚于注册就绪）；
/// 只暴露判定器实际需要的只读面（不含任何写路径/可变状态）。
/// </summary>
public interface IMatchServices
{
    /// <summary>战场（只读面）。</summary>
    Battlefield Battlefield { get; }

    /// <summary>当前行动方（未就绪＝null）。</summary>
    Player? CurrentPlayer { get; }

    /// <summary>某玩家之敌方（未就绪＝null）。</summary>
    Player? EnemyOf(Player owner);

    /// <summary>对局生命周期（终局门禁）。</summary>
    MatchLifecycle Lifecycle { get; }

    /// <summary>效果运行时门面（csx 唯一受控入口；未就绪＝null）。</summary>
    EffectRuntime? EffectRuntime { get; }

    /// <summary>单位是否被守护（守护维护链读点；未就绪＝false）。</summary>
    bool IsUnitGuarded(UnitCard unit);

    /// <summary>HQ 是否被守护（未就绪＝false）。</summary>
    bool IsHqGuarded(Hq hq);

    /// <summary>攻击目标合法性（复验链同源转发；未就绪＝false）。</summary>
    bool IsAttackTargetLegal(UnitCard attacker, Ref<Entity> targetRef);
}

/// <summary>
/// 插件上下文（游戏层扩展；P10 甲）：在内核受控注册面之上暴露 <see cref="IMatchServices"/>，
/// 使插件脚本可构造需对局协作者的注册项。宿主提供的上下文实现本接口。
/// </summary>
public interface IMatchPluginContext : IPluginRegistrationContext
{
    /// <summary>对局服务面（宿主未提供＝null）。</summary>
    IMatchServices? Match { get; }

    /// <summary>对局绑定面（仅 <see cref="AssetActivationPhase.PostCommand"/> 相提供；其余＝null）。</summary>
    IMatchBindingServices? Binding { get; }

    /// <summary>UI 投影服务面（前端资产迁移 S7；宿主未提供＝null）。</summary>
    IUiPluginServices? Ui { get; }
}

/// <summary>
/// 对局绑定面（P11 甲·<see cref="AssetActivationPhase.PostCommand"/> 相）：CommandManager 就绪后的内置装配步骤——
/// **收窄为具名步骤**（不把 CommandManager／玩家集合本体暴露给插件）。
/// </summary>
public interface IMatchBindingServices
{
    /// <summary>
    /// 流程装配阶段的**就绪校验**（批次 7／§27）：端＝引擎成员，一条边接入即进账，
    /// 因此本步骤只剩"动作目录已装配"这一条前置检查（不暴露目录本体、不产生第二本登记簿）。
    /// </summary>
    void RegisterFlowTriggers();

    /// <summary>装配词条效果库（行为引用注册 ＋ 数据壳库装载 ＋ 绑定核验）。</summary>
    void AssembleKeywordEffectLibrary();
}

/// <summary>对局包激活结果。</summary>
/// <param name="ActivatedPackages">已激活包（<c>id@version</c>；装配序）。</param>
/// <param name="Failures">失败明细（隔离整包）。</param>
/// <param name="Skipped">被跳过的包（S14：可选包失败＝跳过并留痕；必需包失败＝装配段上抛、对局不开局）。</param>
public sealed record MatchPackageActivationResult(
    IReadOnlyList<string> ActivatedPackages,
    IReadOnlyList<string> Failures,
    IReadOnlyList<MatchPackageSkip>? Skipped = null)
{
    /// <summary>被跳过的包（未提供＝空）。</summary>
    public IReadOnlyList<MatchPackageSkip> SkippedPackages => Skipped ?? Array.Empty<MatchPackageSkip>();

    /// <summary>失败的必需包（非空＝对局不开局）。</summary>
    public IReadOnlyList<MatchPackageSkip> RequiredFailures
        => SkippedPackages.Where(static s => s.Required).ToArray();
}

/// <summary>被跳过的包（S14 第 3 条：包 id ＋ 必需/可选 ＋ 失败项；审查面与初始化报告都读它）。</summary>
/// <param name="PackageId">包标签（<c>id@version</c>）。</param>
/// <param name="Required">是否必需包。</param>
/// <param name="Failures">失败项。</param>
public sealed record MatchPackageSkip(string PackageId, bool Required, IReadOnlyList<string> Failures);

/// <summary>
/// 对局包激活面（**接口倒置**：游戏层不认识宿主工程 <c>Orc.Packages</c>，故以接口由宿主实现）。
/// <see cref="Match"/> 在装配期以本面把"资产目录快照"激活为对局级注册表。
/// </summary>
public interface IMatchPackageActivator
{
    /// <summary>
    /// 把快照中**属于给定执行相**的对局级资产按装配序（插件先、卡包后）激活到本次对局的注册面。
    /// 分相调用（<see cref="AssetActivationPhase.PreCommand"/> 在 CommandManager 之前；
    /// <see cref="AssetActivationPhase.PostCommand"/> 在其之后）——各相只执行声明属该相的脚本。
    /// </summary>
    MatchPackageActivationResult Activate(
        AssetActivationPhase phase,
        IMatchServices? matchServices,
        IMatchBindingServices? bindingServices,
        AssetServiceRegistry assetServices,
        JudicatorRegistry judicators,
        SnapshotStore prefabs,
        IUiPluginServices? uiServices = null);
}
