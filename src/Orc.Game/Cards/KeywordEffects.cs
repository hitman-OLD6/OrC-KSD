using Orc.Cards;
using Orc.Core;
using Orc.Game.Players;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 词条行为制品（历史沿革 ＋ A2 现状）
//
// 批 1/2（A/B 档试点）：词条组件收薄为壳，行为由 `PassiveEffect` 子类承载（构造期 `EmbedEffect` 注入内嵌效果通道；
// 订阅建立/撤销随效果生命周期）。批 3/4：闪击/动员改「数据壳＋行为引用」——本文件保留三个**静态行为类**
// （`assemblyKey` 的引用目标），由数据壳 `*.prefab.json` 的 hooks 驱动。
//
// A2（决策门 A，用户裁决）：7 个内建词条的行为改由 **v4 效果组件预制体（csx）** 承载——
// 资产＝`Cards/KeywordPrefabs/*.component.json`（成员 csx ＋ 挂载/包装声明），装载＝`KeywordComponentLibrary`
// （对局构造期装进编排运行时目录），绑定＝`BuiltInPlugin` 的 `DeclareEffectBindings`，
// 生灭＝`KeywordComponent.AttachedComponents`（随词条授予/移除/死亡注销/回滚）。
// 因此本文件**不再有任何 `Effect` 家族类型**：
// ①原 `KeywordSignalEffect`／`SuppressionLifecycleEffect`／`IntelligenceTriggerEffect` 的行为迁
//   `keyword.suppression` / `keyword.intelligence` 组件；
// ②原 `KeywordDamageRewriteEffect`／`AmbushRewriteEffect`／`ArmorReductionEffect`／`ImmuneZeroingEffect` 的行为迁
//   `keyword.ambush` / `keyword.armor` / `keyword.immune` 组件（判定器经 `EffectRuntime.Judge`、
//   死亡动作经 `EffectRuntime.DeathAsync`）。
//
// 本文件剩余的三个静态类只服务 **旧格式数据壳样本/测试**（`*.prefab.json` ＋ `assemblyKey`；
// 生产绑定已切到上面那些 v4 组件，见 `KeywordEffectAssembly` 的 `*ComponentId` 常量组）。
// 它们不继承 `Effect`，也不参与词条装配。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 动员·回合累积行为（批 4 数据化遗留的**行为引用目标**：数据壳 <c>keyword.mobilize.accrual.prefab.json</c>
/// 的 assemblyKey 指向 <see cref="HandleAccrualAsync"/>；生产绑定已切 v4 组件 <c>keyword.mobilize.accrual</c>）。
/// 语义（等价基线）：友方回合开始（拥有者回合开始相位）→ +1/+1（累积——修饰器来源无撤销路径：失去/再获得不清理既得；
/// 来源标识＝宿主卡——黑盒等价＝既得 +1/+1 保留、不随效果卸载撤销）。
/// 仅在场（已单位化、未死亡、有位置）单位获得加成（卡在卡组/手牌/未在场时跳过）。
/// </summary>
public sealed class MobilizeAccrualEffect
{
    private MobilizeAccrualEffect()
    {
    }

    /// <summary>
    /// 行为处理器（数据壳 <c>keyword.mobilize.accrual</c> 的 assemblyKey 目标；签名＝
    /// <c>Func&lt;CardEventView, Context, CancellationToken, Task&gt;</c>）：条件/过滤/守卫保留在行为内部
    /// （数据壳仅承载「触发器/hooks＋行为引用」结构面）——触发过滤（turn.start）由数据壳 hooks 承载、
    /// 归属/在场过滤与加成逐条等价于原效果处理。
    /// </summary>
    internal static async Task HandleAccrualAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (view.Player is not Player current
            || view.Host is not CardBase ownerCard
            || !ReferenceEquals(ownerCard.Owner, current))
        {
            return; // 非该单位拥有者的回合开始：不处理
        }

        if (view.Host is not UnitCard unit
            || !unit.TryGetData<UnitStateData>(out var state)
            || state.IsDestroyed
            || state.Line is null)
        {
            return; // 未在场（未单位化/已死亡/无位置）：不获得（「单位在其回合开始时获得」）
        }

        await unit.Modifiers.AddModifiersAsync(
            new Modifier[]
            {
                new AddModifier(CardStatFields.Attack, 1, ownerCard), // 来源＝宿主卡（改签——既得保留、无撤销路径）
                new AddModifier(CardStatFields.Defense, 1, ownerCard),
            },
            ct);
    }
}

/// <summary>
/// 动员·受伤失去行为（批 4 数据化遗留的**行为引用目标**：数据壳 <c>keyword.mobilize.loss.prefab.json</c>
/// 的 assemblyKey 指向 <see cref="HandleLossAsync"/>；生产绑定已切 v4 组件 <c>keyword.mobilize.loss</c>）。
/// 语义（等价基线）：监听 <see cref="GameUpdates.CardDamaged"/>（「受到伤害」——伤害结算后、实际扣减＞0 恰一次、
/// 先落定后发射；伤害被完全吸收/归零＝不发）→ 失去动员（词条移除链——走移除链、既得 +1/+1 保留）。
/// </summary>
public sealed class MobilizeLossEffect
{
    private MobilizeLossEffect()
    {
    }

    /// <summary>
    /// 行为处理器（数据壳 <c>keyword.mobilize.loss</c> 的 assemblyKey 目标；签名＝
    /// <c>Func&lt;CardEventView, Context, CancellationToken, Task&gt;</c>）：触发过滤（card.damaged）由数据壳 hooks 承载、
    /// 本卡过滤与自我撤销逐条等价于原效果处理。
    /// </summary>
    internal static async Task HandleLossAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (view.Host is not Card card)
        {
            return;
        }

        if (view.Card is not Card damaged || !ReferenceEquals(damaged, card))
        {
            return; // 非本卡受到伤害：不处理
        }

        if (KeywordRules.TryGetKeywordManager(card) is not { } manager)
        {
            return;
        }

        await manager.RevokeAsync(KeywordIds.Mobilize); // 失去＝词条移除（走移除链；幂等防御——词条不在＝无操作）
    }
}

/// <summary>
/// 闪击·部署置位行为（批 4 数据化遗留的**行为引用目标**：数据壳 <c>keyword.blitz.deploy-set.prefab.json</c>
/// 的 assemblyKey 指向 <see cref="HandleDeploySetAsync"/>；生产绑定已切 v4 组件 <c>keyword.blitz.deploy-set</c>）。
/// 行为（等价基线）：监听 <see cref="GameUpdates.UnitDeployed"/>（「单位部署」——单位化完成后发射、恰一次；
/// 载荷＝{ Unit, Position }）→ 过滤 Host==载荷单位 → 置位指挥组件 CanMove/CanAttack＝true（覆盖部署初值 false/false）。
/// 仅部署路径生效：加入链（unit.joined）/部署重放/升级替换（unit.upgraded）等路径不发射 unit.deployed——不置位。
/// 防御：载荷缺失/非本单位/无 CommandData＝跳过、不抛错（沿既有口径）。
/// </summary>
public sealed class BlitzDeployEffect
{
    private BlitzDeployEffect()
    {
    }

    /// <summary>
    /// 行为处理器（数据壳 <c>keyword.blitz.deploy-set</c> 的 assemblyKey 目标；签名＝
    /// <c>Func&lt;CardEventView, Context, CancellationToken, Task&gt;</c>）：触发过滤（unit.deployed）由数据壳
    /// hooks 承载、主机过滤与置位逐条等价于原效果处理。
    /// </summary>
    internal static Task HandleDeploySetAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (view.Unit is not UnitCard deployed || !ReferenceEquals(deployed, view.Host))
        {
            return Task.CompletedTask; // 非本单位部署：不处理（Host==载荷过滤——其他单位部署不误置位）
        }

        KeywordRules.ResetActionQuotas(deployed); // 置额度（覆盖部署初值 0/0；重复信号幂等）

        return Task.CompletedTask; // 无 CommandData（防御——非单位/未单位化）：跳过、不抛错
    }
}
