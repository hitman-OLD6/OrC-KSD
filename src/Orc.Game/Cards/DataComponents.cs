using Orc.Game.Board;

namespace Orc.Game.Cards;

/// <summary>
/// 单位数据组件：所在位置（槽位引用；未部署＝null）、是否被摧毁（初始 false）、单位类型枚举列表（0 个/多个均合法）、
/// 单位三值（行动费 / 攻击力 / 防御力）与防御力损伤量。
/// 数值模型（W2b G3 接线）：三值为「数值变更门户」的受控数据面——
/// 运行期变更的唯一合规入口＝门户操作面（修饰加值/撤销＝卡侧修饰容器 <see cref="CardModifierComponent"/>；
/// 伤害扣减/修复＝<see cref="UnitCard"/> 门户方法）；三值与本组件内数值字段的直接运行期写面收窄
/// （<c>internal set</c>：跨程序集不可写；程序集内直写一经出现即为非合规路径）。
/// 装配期填充（单位化初始值复制 <see cref="CreateInitial"/>）为列明例外（保留直写）。
/// S9 收窄（类型列表）：<see cref="UnitTypes"/> 写路径唯一化——运行期增补经受控入口
/// （<see cref="UnitCard.AddUnitTypeAsync"/>：去重/校验/变更信号）；装配期填充经内部例外
/// （<see cref="FillInitialTypes"/>——不经受控入口、不发信号；与三实时值「装配期填充＝列明例外」同族）；
/// 公开面收窄为只读暴露（形态对齐定义侧只读先例），外部直改不再可行。
/// 防御力上限语义（本体＝损伤量）：本体状态＝<see cref="DefenseLoss"/>（loss＝有效上限−当前值，≥0）；
/// 有效上限＝对战组件基准＋Σ加防修饰（链输出）；当前值＝上限−loss（数值表现钳制不低于 0；≤0 触发死亡判定）。
/// 「加防同步加上限／伤害扣减只扣当前不减上限／修复＝恢复到上限」由链公式与门户操作共同导出。
/// 「单位化」时装配（＋指挥组件共用单位化触发器）；以引擎数据组件形态挂载：经 <see cref="Orc.Cards.Card.AddData"/> 装配、
/// <c>GetData&lt;T&gt;</c> 读取（引用共享）。
/// </summary>
public sealed class UnitStateData
{
    private readonly List<string> _unitTypes = new();

    /// <summary>创建空组件（位置 null、未摧毁、类型列表空、实时值 0——初值请经 <see cref="CreateInitial"/> 或直接赋值装载）。</summary>
    public UnitStateData()
    {
    }

    /// <summary>所在阵线（有序列表模型：位置＝该线中的序号，现算；未入场＝null）。</summary>
    public BattleLine? Line { get; set; }

    /// <summary>是否在场（已入场＝所在线非空）。</summary>
    public bool IsOnBoard => Line is not null;

    /// <summary>是否被摧毁（初始 false；摧毁判定/处理属后续批次）。</summary>
    public bool IsDestroyed { get; set; }

    /// <summary>
    /// 单位类型枚举列表（只读暴露；允许 0 个/多个、不设非空校验；顺序＝登记序）。
    /// 写路径唯一化（S9）：运行期增补经受控入口（<see cref="UnitCard.AddUnitTypeAsync"/>）、装配期填充经内部例外
    /// （<see cref="FillInitialTypes"/>——列明例外、静默）；集合外部直改不再可行。
    /// </summary>
    public IReadOnlyList<string> UnitTypes => _unitTypes;

    /// <summary>
    /// 装配期填充（S9；列明例外——不经受控入口、不发信号；与三实时值「装配期填充＝列明例外」同族）：
    /// 单位化（部署/加入/转换入场）时从定义填充初始类型；调用点收口＝<see cref="UnitCard"/> 单位化段。
    /// </summary>
    internal void FillInitialTypes(IEnumerable<string> types) => _unitTypes.AddRange(types);

    /// <summary>
    /// 运行期受控增补（S9；内部执行面——由 <see cref="UnitCard.AddUnitTypeAsync"/> 受控入口调用，为其唯一调用者）：
    /// 已含＝false（幂等无操作——不重复登记）；实际改变集合＝true（调用方负责变更信号发射）。
    /// </summary>
    internal bool TryAddRuntimeType(string type)
    {
        if (_unitTypes.Contains(type))
        {
            return false;
        }

        _unitTypes.Add(type);
        return true;
    }

    /// <summary>实时行动费（运行值；初始＝对战组件值；运行期变更经门户操作面——直写为非合规路径）。</summary>
    public int OperateCost { get; internal set; }

    /// <summary>实时攻击力（运行值；初始＝对战组件值；运行期变更经门户操作面——直写为非合规路径）。</summary>
    public int Attack { get; internal set; }

    /// <summary>
    /// 防御力当前值（数值表现；初始＝对战组件值；运行期由跑链落定同步写入——＝max(0, 有效上限−损伤量)；
    /// 直写为非合规路径）。
    /// </summary>
    public int Defense { get; internal set; }

    /// <summary>
    /// 防御力损伤量（本体状态；≥0）：loss＝有效上限−当前值；加防修饰同步加上限（loss 不变）、
    /// 伤害扣减只增 loss（上限不变）、修复＝loss 清零（恢复到上限）；撤销/到期移除致当前值≤0＝照常进入死亡判定。
    /// 运行期变更经门户操作面（<see cref="UnitCard.ApplyDefenseDamageAsync"/> / <see cref="UnitCard.RepairDefenseAsync"/>）；
    /// 直写为非合规路径。
    /// </summary>
    public int DefenseLoss { get; internal set; }

    /// <summary>
    /// 在场回合数（G14补 S10；回合事件驱动计数）：单位在场上所历的己方回合数——
    /// 入场即第 1 回合（部署/加入/转换入场＝1，见 <see cref="CreateInitial"/>）；递增＝单位归属玩家的回合正式开始
    /// （turn.start）时 +1（单方步进——对方回合不递增）；死亡后停止递增（值保持最后值、可读）；
    /// S9 转换重建的新实例＝计数重置（静态重建——自 1 起）；静默数据变更（不新增信号/不发更新）。
    /// 运行期递增经内部执行面（<see cref="UnitCard.AdvanceTurnsInPlay"/>）——直写为非合规路径；
    /// 未入场（手牌/卡组）＝无本组件、不适用（读取＝不适用/无值、不抛错）。
    /// </summary>
    public int TurnsInPlay { get; internal set; }

    /// <summary>
    /// 按「初始值复制契约」创建（挂载时用；装配期填充＝列明例外——保留直写）：三实时值初值一次性复制自对战组件
    /// （只读基准）、损伤量清零；位置 null、未摧毁、类型列表空；在场回合数＝1（入场即第 1 回合——
    /// 部署/加入/转换入场统一自 1 起；S10）。后续效果/词条修改实时值不影响基准。
    /// </summary>
    /// <exception cref="ArgumentNullException">stats 为 null。</exception>
    public static UnitStateData CreateInitial(BattleStatsData stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new UnitStateData
        {
            OperateCost = stats.OperateCost,
            Attack = stats.Attack,
            Defense = stats.Defense,
            DefenseLoss = 0,
            TurnsInPlay = 1, // 入场即第 1 回合（G14补 S10——「不存在入场后为 0/未定义状态」）
        };
    }
}

/// <summary>
/// 指挥组件：单位的指挥状态——移动额度 / 攻击额度（int）；初始 0/0。
/// 额度为唯一真源：<see cref="CanMove"/> / <see cref="CanAttack"/> 为派生只读投影（&gt;0 即可用），
/// 读取点（UI/AI/判定器/动作）语义不变。
/// 「非坦克每次行动两额度各减一、坦克只减本次动作对应额度」由 <see cref="Consume"/> 承担；
/// 回合恢复/部署置位经 <see cref="ResetQuotas"/>。以引擎数据组件形态挂载；「单位化」时装配。
/// </summary>
public sealed class CommandData
{
    /// <summary>移动额度（初始 0）。</summary>
    public int MoveQuota { get; private set; }

    /// <summary>攻击额度（初始 0）。</summary>
    public int AttackQuota { get; private set; }

    /// <summary>可移动（派生投影：移动额度 &gt; 0）。</summary>
    public bool CanMove => MoveQuota > 0;

    /// <summary>可攻击（派生投影：攻击额度 &gt; 0）。</summary>
    public bool CanAttack => AttackQuota > 0;

    /// <summary>重置两个额度（回合恢复/部署置位；负值钳制为 0）。</summary>
    public void ResetQuotas(int move, int attack)
    {
        MoveQuota = Math.Max(0, move);
        AttackQuota = Math.Max(0, attack);
    }

    /// <summary>设置移动额度（负值钳制为 0）。</summary>
    public void SetMoveQuota(int value) => MoveQuota = Math.Max(0, value);

    /// <summary>设置攻击额度（负值钳制为 0）。</summary>
    public void SetAttackQuota(int value) => AttackQuota = Math.Max(0, value);

    /// <summary>
    /// 行动收尾消耗：非坦克**两个额度各减一**（不是清零）；坦克只减本次动作对应的额度。
    /// </summary>
    internal void Consume(bool isMove, bool isTank)
    {
        if (isTank)
        {
            if (isMove)
            {
                SetMoveQuota(MoveQuota - 1);
            }
            else
            {
                SetAttackQuota(AttackQuota - 1);
            }

            return;
        }

        SetMoveQuota(MoveQuota - 1);
        SetAttackQuota(AttackQuota - 1);
    }
}

/// <summary>
/// 单位回合统计（本回合；每己方回合开始清零）：供词条与元效果读取（行动/移动/攻击次数、花费指挥点、累计受伤）。
/// 写入面 internal（引擎订阅器与指挥收尾维护）；读面 public。
/// 口径：移动＝任何位置变化（指挥/效果/撤退）；攻击＝仅玩家指挥；行动＝<c>unit.acted</c>。
/// </summary>
public sealed class ActionRecordData
{
    /// <summary>本回合指挥行动次数（unit.acted）。</summary>
    public int TurnActions { get; internal set; }

    /// <summary>本回合移动次数（任何位置变化）。</summary>
    public int TurnMoves { get; internal set; }

    /// <summary>本回合攻击次数（仅玩家指挥）。</summary>
    public int TurnAttacks { get; internal set; }

    /// <summary>本回合花费的指挥点（指挥行动费）。</summary>
    public int TurnPointsSpent { get; internal set; }

    /// <summary>本回合累计受到的伤害。</summary>
    public int TurnDamageTaken { get; internal set; }

    /// <summary>清零（每己方回合开始调用）。</summary>
    public void Reset()
    {
        TurnActions = 0;
        TurnMoves = 0;
        TurnAttacks = 0;
        TurnPointsSpent = 0;
        TurnDamageTaken = 0;
    }

    internal void RecordAction() => TurnActions++;

    internal void RecordMove() => TurnMoves++;

    internal void RecordAttack(int cost)
    {
        TurnAttacks++;
        TurnPointsSpent += Math.Max(0, cost);
    }

    internal void RecordDamage(int amount) => TurnDamageTaken += Math.Max(0, amount);
}

/// <summary>
/// 反制激活状态组件：反制卡当前是否已激活（初始 false）。
/// 激活流程（先验证〔未激活时检查指挥点〕→ 合法 → 反转状态 → 扣/退点）属打出链批次；
/// 翻转/置位逻辑属后批次（本批仅承载字段与读写）。
/// 以引擎数据组件形态挂载；实际挂载时机属后续批次（本批类定义就绪＋测试内构造挂载可测）。
/// </summary>
public sealed class CounterActivationData
{
    /// <summary>是否已激活（初始 false）。</summary>
    public bool IsActive { get; set; }
}
