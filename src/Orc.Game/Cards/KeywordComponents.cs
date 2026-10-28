using Orc.Cards;
using Orc.Core;
using Orc.Game.Commanding;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 生产内建四词条的组件实现（2C-A1 迁移；行为与迁移前逐条保真——见验收汇报「随改清单」）：
// 闪击/伏击/奋战＝能力型；烟幕＝标记型（轻量组件 PlainKeywordComponent——
// 仅数据、无主动逻辑，消费方经词条管理组件查询）。四者均走同一组件基类与同一挂载/卸载机制
// （闪击经批 3/批 4、伏击经批 2 效果化收薄为壳——见下；奋战＝运行逻辑组件的自含行为）。
// 批 2 词条效果化（B 档扩展）：伏击（伤害改写族）行为迁效果承载（组件收薄为壳——标识／授予-移除／读取面；
// 「造成攻击伤害」改写注册/撤销与判定链由内嵌效果 AmbushRewriteEffect 承载）；效果制品与装配
// （构造期 EmbedEffect＋装载/卸载/死亡注销/回滚/复装随词条生灭）见 KeywordEffects.cs。
// 批 3/批 4 词条效果化（C 档首迁／数据化）：闪击（部署置位）行为批 3 迁效果承载、批 4 迁「数据壳＋行为引用」
// （组件收薄为壳——标识／授予-移除／读取面；本组件不再构造 C# 效果——绑定声明于注册面〔KeywordRegistry〕、
// 装载期经授予链实例化数据效果〔KeywordManager 装载期实例化点〕、行为引用目标见 KeywordEffects.cs
// 〔BlitzDeployEffect.HandleDeploySetAsync〕）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 闪击（能力型）词条组件（批 3/批 4 效果化：收薄为壳——标识／授予-移除／读取面）：
/// 部署置位行为（unit.deployed → CanMove/CanAttack 置位）批 4 数据化迁「数据壳＋行为引用」形态——
/// 绑定声明于注册面（<see cref="KeywordRegistry.DeclareEffectBindings"/>：词条 → 效果清单）、
/// 装载期经授予链实例化数据效果并接入内嵌效果通道（<see cref="KeywordManager"/> 装载期实例化点）、
/// 行为引用目标＝<see cref="BlitzDeployEffect"/>（仅部署路径生效——加入链/部署重放/升级替换等路径不发部署信号）。
/// 壳保留：标识（<see cref="KeywordIds.Blitz"/>）、注册面（<see cref="KeywordRegistry"/>——装配面，保留不动）、
/// 授予-移除机制面（<see cref="KeywordManager"/> 挂载/卸载链＋基类机制）；本词条无参值面。
/// </summary>
public sealed class BlitzKeywordComponent : KeywordComponent
{
    /// <summary>创建闪击词条组件。</summary>
    public BlitzKeywordComponent()
        : base(KeywordIds.Blitz)
    {
        // 批 4 数据化：部署置位行为迁「数据壳＋行为引用」（单源——本组件不再构造 C# 效果；装配见 KeywordEffectAssembly）。
    }
}

/// <summary>
/// 奋战（能力型）词条组件：保留运行逻辑承载（攻击记账 / 收尾判定 / 回合清零——非纯标记、不退化为「标记型＋外部读取」）。
/// 自含记账数据（「本轮已攻次数」——不保留通用字符串键计数器形态）：首次攻击后仍可攻、二次后不可；
/// 随回合恢复一并清零。读取方（攻击执行段/收尾/回合恢复）经 <see cref="KeywordRules"/> 静态助手面调用（转发本组件）。
/// </summary>
public sealed class FuryKeywordComponent : KeywordComponent
{
    private int _attackCount;

    /// <summary>创建奋战词条组件。</summary>
    public FuryKeywordComponent()
        : base(KeywordIds.Fury)
    {
    }

    /// <summary>攻击记账（攻击动作成功执行后、由攻击执行段调用）：「本轮已攻次数」+1。</summary>
    internal void RecordAttack() => _attackCount++;

    /// <summary>攻击动作后的 CanAttack 收尾取值：「本轮已攻次数」＜2（首次攻击后保持可攻、二次后不可）。</summary>
    internal bool CanAttackAfterAttack() => _attackCount < 2;

    /// <summary>回合恢复：清零「本轮已攻次数」（随回合恢复一并清零）。</summary>
    internal void ResetTurnCounters() => _attackCount = 0;
}

/// <summary>
/// 伏击（能力型）词条组件（A2：收薄为**纯壳**——标识／授予-移除／读取面）：
/// 「造成攻击伤害」改写逻辑（先资格、后条件的判定链与置改写标志）由 **v4 效果组件** <c>keyword.ambush</c>
/// 的 csx 成员承载（绑定声明见 <see cref="KeywordEffectAssembly.AmbushComponentId"/>；装配/卸载随词条生灭）。
/// 判定器通道在 csx 内经 <c>EffectRuntime.Judge</c> 取用、死亡动作经 <c>EffectRuntime.DeathAsync</c>（完整两端）；
/// 不区分攻击者类型；HQ 攻击不走该流程。
/// </summary>
public sealed class AmbushKeywordComponent : KeywordComponent
{
    /// <summary>创建伏击词条组件。</summary>
    public AmbushKeywordComponent()
        : base(KeywordIds.Ambush)
    {
    }
}
