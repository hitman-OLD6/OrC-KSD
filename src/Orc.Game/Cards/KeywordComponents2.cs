using Orc.Cards;
using Orc.Core;
using Orc.Game.Commanding;
using Orc.Game.Players;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 第 2 批·A2 新增词条的组件实现（口径依据《需求文档（A2）》Q&A-1..13）：
// 含行为面六枚——被压制（状态标记＋限期解除）／动员（+1/+1 累积与受伤失去）／钳击（配对与离场失效）／
// 重甲（对战伤害减免；参值 0..3）／情报（触发结构＋待办环节）／免疫（伤害归零）；
// 轻量标记型四枚——被抑制／预报／无法被压制／无法被抑制（PlainKeywordComponent，注册面装配；无行为面）。
// 全部走 A1 组件化体系（同一基类、同一挂载/卸载机制；宿主＝引擎薄容器 Card——单位与 HQ 同族承载）。
// 服务面（施加/读取/待办）见 KeywordServices.cs；钳击关系体系见 PincerSystem.cs。
// 批 1 词条效果化（A 档试点）：被压制／动员／情报三枚行为迁效果承载（组件收薄为壳——标识／参值／
// 授予-移除／读取面）；效果制品与装配（构造期 EmbedEffect＋装载/卸载/死亡注销/回滚/复装随词条生灭）
// 见 KeywordEffects.cs。
// 批 2 词条效果化（B 档扩展）：重甲／免疫两枚（伤害改写族）行为迁效果承载（组件收薄为壳——重甲保留
// 参值/钳制/AppliesToCommandDamage 公开面；免疫无行为性残留）；效果制品与装配（构造期 EmbedEffect＋
// 装载/卸载/死亡注销/回滚/复装随词条生灭）见 KeywordEffects.cs（ArmorReductionEffect／ImmuneZeroingEffect）。
// 批 4 数据化（路线 A）：动员（双效果）由「C# 效果注入」迁「数据壳＋行为引用」形态（绑定声明于注册面、
// 装载期实例化数据效果——见 KeywordManager 装载期实例化点与 KeywordEffectAssembly）；被压制/情报/重甲/免疫
// 保持「C# 效果注入」形态不变。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 被压制（状态标记型＋限期解除；Q&A-5 口径）：施加即时受限（「不能移动或攻击」＝行动合法性消费面读本标记——
/// 指挥可用性/复验侧接线）；标记保留、经移除路径清除。解除＝「施加后首先到来的拥有者回合结束」时
/// （剩余回合数以参值承载——默认 1；「额外压制一回」＝＋1）；「下一个回合」不含当前回合
/// （施加时若处于拥有者回合，本回合结束不递减——装载时判定、skip 不进参值面）。
/// A2：本组件为**纯壳**（标识／参值／授予-移除／读取面）——状态管理（递减/自解除）由 v4 效果组件
/// <c>keyword.suppression</c> 的 csx 成员承载（挂载 <c>engine#turn.end</c>；参值运行期读取、改写随动；
/// 「施加当回合不递减」由组件私有字段 <c>firstOwnerTurnEnd</c> 承担）。
/// </summary>
public sealed class SuppressedKeywordComponent : KeywordComponent
{
    /// <summary>创建被压制组件（参值＝剩余拥有者回合数；缺省＝1——施加侧经服务面传入）。</summary>
    public SuppressedKeywordComponent(int? value = null)
        : base(KeywordIds.Suppressed, value)
    {
    }
}

/// <summary>
/// 动员（能力型；Q&A-6 口径）：友方回合开始（拥有者回合开始相位）时 +1/+1（累积）；
/// 受到实际伤害（净伤害＞0）后失去动员（词条移除链——伤害被完全吸收/归零＝不算）。
/// 既得 +1/+1 保留（失去/再获得不清理——无撤销路径；来源标识批 4 改签＝宿主卡，见实现记录申报）。
/// 仅在场（已单位化、未死亡、有位置）单位获得加成（卡在卡组/手牌/未在场时跳过）。
/// 批 1/批 4 效果化：本组件收薄为壳（标识／参值／授予-移除／读取面）——「回合开始 +1/+1」与「受伤失去」
/// 批 4 数据化迁「数据壳＋行为引用」形态（双效果：绑定声明于注册面——词条 → 效果清单〔N＝2〕；
/// 装载期经授予链实例化并接入内嵌效果通道；行为引用目标＝<see cref="MobilizeAccrualEffect"/> /
/// <see cref="MobilizeLossEffect"/>；受伤失去监听 card.damaged 信号自我撤销——受伤害门户直调路径已退役）。
/// </summary>
public sealed class MobilizeKeywordComponent : KeywordComponent
{
    /// <summary>创建动员词条组件。</summary>
    public MobilizeKeywordComponent()
        : base(KeywordIds.Mobilize)
    {
        // 批 4 数据化：双效果（回合累积/受伤失去）迁「数据壳＋行为引用」（单源——本组件不再构造 C# 效果；装配见 KeywordEffectAssembly）。
    }
}

/// <summary>
/// 钳击（能力型；Q&A-3/Q&A-12 口径）：部署链收尾（扣费后）发起「同伴选择」（可选、既有单选交互）；
/// 形成一对一配对（候选＝己方在场单位〔不含自身/HQ/已配对〕）；双方获得钳击效果（以「钳击关系」为来源的
/// 成组效果——本批示例＝修饰类 +2 攻击力；通用承载面见 <see cref="PincerPair"/>）；任一方离场
/// （「不再在场」——本批覆盖死亡路径）→ 另一方即时失去（按来源整组撤销）；关系终结不自动恢复。
/// 放弃/无候选/取消＝不形成、部署照常完成（该单位仍具「钳击」词条——计数不受配对影响）；加入路径不形成。
/// </summary>
public sealed class PincerKeywordComponent : KeywordComponent
{
    private UnitCard? _self;
    private KeywordLoadContext? _context;

    /// <summary>创建钳击词条组件。</summary>
    public PincerKeywordComponent()
        : base(KeywordIds.Pincer)
    {
    }

    /// <inheritdoc />
    internal override void Mount(Card card, KeywordLoadContext? context)
    {
        _self = card as UnitCard;
        _context = context;
    }

    /// <inheritdoc />
    internal override void Unmount()
    {
        _self = null;
        _context = null;
    }

    /// <inheritdoc />
    internal override Task OnDeployChainFinalizedAsync(Card card, CancellationToken ct)
    {
        if (_self is not { } self || _context is not { } context)
        {
            return Task.CompletedTask; // 非单位/无装载上下文：跳过（防御、不抛错）
        }

        return PincerRules.TryFormPairAsync(self, context, ct);
    }
}

/// <summary>
/// 重甲（参值型；Q&A-7 口径）：受到的对战伤害 -X（攻击伤害与反击伤害均含；指令伤害不减免）。
/// 参值域：下限 0、上限 3（「每张卡最多为 3」＝值封顶——构造/参值改写均在组件侧钳制）。
/// A2：本组件为**纯壳**（标识／参值／钳制／授予-移除／读取面）——减伤行为由 v4 效果组件 <c>keyword.armor</c>
/// 的 csx 成员承载（包装 <c>engine#combat-damage</c> before；参值运行期读取——改写后减伤量随动；
/// 装配/卸载随词条生灭）。判定读点（承载面）：①攻击者侧「无视重甲」（默认不无视——读点经
/// <see cref="ArmorRules.IgnoresArmor"/>，不动）；②指令伤害域（<see cref="AppliesToCommandDamage"/>——公开面保留、
/// 默认关闭、本批不接线）。「+1 重甲」＝当前值 +1（经 <see cref="ArmorRules.AddArmorAsync"/>）。
/// </summary>
public sealed class ArmorKeywordComponent : KeywordComponent
{
    /// <summary>参值下限（负值钳制为 0）。</summary>
    public const int MinValue = 0;

    /// <summary>参值上限（值封顶 3——「重甲叠加数」即重甲值、非「叠加次数」）。</summary>
    public const int MaxValue = 3;

    /// <summary>创建重甲词条组件（参值在组件侧钳制至 [0, 3]；null＝「仅标识」形态保留）。</summary>
    public ArmorKeywordComponent(int? value = null)
        : base(KeywordIds.Armor, Clamp(value))
    {
    }

    /// <summary>是否作用于指令伤害（域扩展开关；默认 false＝仅对战伤害）——内容层效果将来经设置开启（本批承载面就位）。</summary>
    public bool AppliesToCommandDamage { get; set; }

    /// <summary>参值钳制（写入/增改均受上限约束——词条层校验/封顶）。</summary>
    private static int? Clamp(int? value) => value is { } v ? Math.Clamp(v, MinValue, MaxValue) : null;

    /// <inheritdoc />
    internal override void OverrideValue(int? value) => base.OverrideValue(Clamp(value));
}

/// <summary>
/// 情报（参值型；Q&A-8 口径）：声明/装配/读取＋触发结构——「具有情报的卡被使用时」触发
/// （card.played 时点接线；推断口径、官方规则文本未直述）；「情报执行」（明牌 X 张）因 G18 明牌系统未就绪
/// ＝待办环节（<see cref="IntelligenceRules"/>——触发点调用明确待办、申报待接线）。
/// 参值域：下限 0、上限 3（词条层钳制——「每张卡最多为 3」＝值封顶）。
/// A2：本组件为**纯壳**（标识／参值／授予-移除／读取面）——「被使用时触发」结构由 v4 效果组件
/// <c>keyword.intelligence</c> 的 csx 成员承载（挂载 <c>engine#card.played</c>；执行面契约与待办环节保持、参值运行期读取）。
/// </summary>
public sealed class IntelligenceKeywordComponent : KeywordComponent
{
    /// <summary>参值下限（负值钳制为 0）。</summary>
    public const int MinValue = 0;

    /// <summary>参值上限（值封顶 3）。</summary>
    public const int MaxValue = 3;

    /// <summary>创建情报词条组件（参值在组件侧钳制至 [0, 3]；null＝「仅标识」形态保留）。</summary>
    public IntelligenceKeywordComponent(int? value = null)
        : base(KeywordIds.Intelligence, Clamp(value))
    {
    }

    /// <summary>参值钳制（写入/增改均受上限约束——词条层校验/封顶）。</summary>
    private static int? Clamp(int? value) => value is { } v ? Math.Clamp(v, MinValue, MaxValue) : null;

    /// <inheritdoc />
    internal override void OverrideValue(int? value) => base.OverrideValue(Clamp(value));
}

/// <summary>
/// 免疫（能力型；Q&A-9 口径）：不会受到伤害——伤害归零（不限制索敌：目标候选/校验不查免疫；非伤害效果照常）。
/// A2：本组件收薄为壳（标识／授予-移除／读取面）——归零行为由 v4 效果组件 <c>keyword.immune</c> 的 csx 成员
/// 承载（单位侧＝包装 <c>engine#damage-apply</c> before，装配/卸载随词条生灭）；
/// **HQ 侧例外**：HQ 无编排实体（不是 <c>CardBase</c>），其伤害走自己的数值管线（<see cref="Hq.AddDamageRewriter"/>）
/// 而非 <c>damage.apply</c>——故 HQ 宿主在 <see cref="Mount"/> 直接挂「归零改写器」（机制同一、接入点各按路径对齐；
/// 来源标记＝本组件实例）。这一处是 v4 化后唯一保留在 C# 的词条行为，见 PROGRESS「二十」节。
/// 归零后按「0 伤害」正常走（净伤害＝0 不算「受到伤害」——动员等消费自然一致）。「全部来源」：本批伤害通道＝
/// 对战结算（单位）与 HQ 数值路径（HQ）；非对战路径（指令/效果伤害）接入随统一伤害路径批次对齐（申报）。
/// 期限/清除走施加物机制。
/// </summary>
public sealed class ImmuneKeywordComponent : KeywordComponent
{
    private Hq? _hq;
    private HqDamageRewriter? _hqRewriter;

    /// <summary>创建免疫词条组件。</summary>
    public ImmuneKeywordComponent()
        : base(KeywordIds.Immune)
    {
    }

    /// <inheritdoc />
    internal override void Mount(Card card, KeywordLoadContext? context)
    {
        // HQ 侧：宿主即挂归零改写器（不依赖装载上下文——「HQ 宿主上下文依赖差异」逐条保持；
        // HQ 伤害路径不经过 engine#damage-apply，v4 组件无法覆盖该接入点）。
        if (card is not Hq hq)
        {
            return; // 单位/其它卡：归零由 v4 组件 keyword.immune 承载
        }

        _hq = hq;
        _hqRewriter = new HqDamageRewriter(this, static _ => 0);
        hq.AddDamageRewriter(_hqRewriter);
    }

    /// <inheritdoc />
    internal override void Unmount()
    {
        var hq = _hq;
        var rewriter = _hqRewriter;
        _hq = null;
        _hqRewriter = null;
        if (hq is not null && rewriter is not null)
        {
            hq.RemoveDamageRewriter(rewriter); // 幂等（未挂载＝无操作、不抛错）
        }
    }
}
