using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Model;

/// <summary>
/// 旧"显示名触发器"到成员地址的映射表（R1j／S7 第 1–2 条）。
/// <para>
/// **唯一真源**：迁移器（S11）按本表转换 <c>injects[].target</c>，实现侧按本表声明预制体成员，
/// 审查面按本表把边反查回旧名——三处同源，任何一处独立命名都会漂移；守护测试
/// （<c>EngineEntityTests</c>）把"常量集合 == 预制体成员集合"钉成红灯。
/// </para>
/// <para>
/// 成员名是**新的稳定名**（kebab-case）；旧中文显示名只作为迁移期的一次性桥，迁移完成后资产里不得再出现它。
/// </para>
/// </summary>
public static class EngineProcesses
{
    /// <summary>成员名：指挥流程（原"指挥触发器"）。</summary>
    public const string Command = "command";

    /// <summary>成员名：单位移动（原"单位移动触发器"）。</summary>
    public const string UnitMove = "unit-move";

    /// <summary>成员名：造成攻击伤害（原"造成攻击伤害触发器"）。</summary>
    public const string AttackDamage = "attack-damage";

    /// <summary>成员名：对战伤害触发端（原"对战伤害触发器"）。</summary>
    public const string CombatDamage = "combat-damage";

    /// <summary>成员名：效果伤害触发端（原"效果伤害触发器"）。</summary>
    public const string EffectDamage = "effect-damage";

    /// <summary>成员名：战斗伤害触发端（原"战斗伤害触发器"）。</summary>
    public const string BattleDamage = "battle-damage";

    /// <summary>成员名：伤害处理端（原"伤害处理触发器"）。</summary>
    public const string DamageApply = "damage-apply";

    /// <summary>成员名：单位攻击（原"单位攻击触发器"）。</summary>
    public const string UnitAttack = "unit-attack";

    /// <summary>成员名：行动额度（原"行动额度触发器"）。</summary>
    public const string ActionBudget = "action-budget";

    // ── 动作管道成员（S13 解锁 U1；R1j 同源表） ──
    // 声明形态只为"可寻址、可挂载、可登记"；自有边与视图载荷在 U3/U4 逐动作迁移时补齐
    // （那时的字段名权威＝旧 C# 视图类，与 DamageApply 已在声明的 damageApply 视图同源）。

    /// <summary>成员名：加入手牌触发端（通用前置）。</summary>
    public const string HandAdd = "hand.add";

    /// <summary>成员名：加入手牌处理端（入列表／爆牌＝落地型，自有边最后）。</summary>
    public const string HandAddHandle = "hand.add.handle";

    /// <summary>成员名：抽牌（卡组顶）。</summary>
    public const string DrawTop = "draw.top";

    /// <summary>成员名：抽牌（指定牌）。</summary>
    public const string DrawSpecific = "draw.specific";

    /// <summary>成员名：抽牌处理端（落地型，自有边最后）。</summary>
    public const string DrawHandle = "draw.handle";

    /// <summary>成员名：死亡触发端。</summary>
    public const string Death = "death";

    /// <summary>成员名：死亡处理端（落地型，自有边最后）。</summary>
    public const string DeathHandle = "death.handle";

    /// <summary>成员名：卡牌移动触发端。</summary>
    public const string Move = "move";

    /// <summary>成员名：卡牌移动处理端（落地型，自有边最后）。</summary>
    public const string MoveHandle = "move.handle";

    /// <summary>成员名：加入战场（部署入口）。</summary>
    public const string JoinDeploy = "join.deploy";

    /// <summary>成员名：加入战场（加入前线入口）。</summary>
    public const string JoinFrontline = "join.frontline";

    /// <summary>成员名：加入战场（加入支援阵线入口）。</summary>
    public const string JoinSupport = "join.support";

    /// <summary>成员名：加入战场处理端（单位化落位＝落地型，自有边最后）。</summary>
    public const string JoinHandle = "join.handle";

    // ── 指令流程成员（S13／U3：父 agent 裁决「order.cast／order.finalize 升为引擎成员」） ──
    // 命名理由：OrderFlow 是指令流程的合法一环（内核唯一 flow），两端与其它动作同样处于 `engine` 根下，
    // 因此「动作两端都是引擎成员」这条不变量对 21/21 端成立。成员名只在此声明一次，
    // 内核 OrderFlow 不自造字面值——它经 IOrderAction 由实现自报地址（见 OrderAction）。

    /// <summary>成员名：指令施放（宣告＋结算；U4 起＝末步型，自有边最后）。</summary>
    public const string OrderCast = "order.cast";

    /// <summary>成员名：指令收尾（扣费／离手＝落地型，自有边最后）。</summary>
    public const string OrderFinalize = "order.finalize";

    // ── 动作端点位次表（U4／D1；21 端，与 MatchActionCatalog 装配的端清单同源） ──
    // 判据（R1i 修订／D1-D2）：**自有边是链上末步的端＝last**（外来挂载边与 before 包装先跑，再跑自有边）；
    // 只有"该端整体就是一道前置门禁、没有末步落地"时才用 first。动作管道的"转发"（旧 Commit）与
    // "落地"（旧 Apply）都是末步，因此 **21 个动作端全部是 last**——这不是"没有 first 组"，而是
    // "门禁相位不是独立端"：旧 Initiate／Announce／CounterGuard 的每一条都挂在这些端内部，
    // 迁移到新模型后由 `before` 相位承载（R1i 的 `Veto → before+Stop`／`Rewrite → before` 规则同源）。
    // 21 端的"整端就是门禁"反例不存在；若将来拆出独立的门禁成员（R1i「需要中间插入就拆成员」），
    // 它才应标 first。守护测试锁住这 21 条（EngineEntityTests 与 ActionEdgeOrderTests）。

    /// <summary>
    /// 动作端点位次表（U4）：<b>21 端</b>逐条给出角色、旧末步段与自有边位次。
    /// <para>
    /// **唯一真源**：<see cref="Members"/> 的自有边位次由此表推出（不在 <c>Build</c> 里另写一份），
    /// 因此"哪一端是 last"与"为什么"始终同处可读；守护测试逐条比对预制体成员声明。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ActionEndPosition> ActionEndPositions { get; } = new ActionEndPosition[]
    {
        new(HandAdd, "触发端", OwnEdgePosition.Last, "Commit 转发",
            "转发是末步：外来挂载边（含门禁）先跑，自有边最后跑"),
        new(HandAddHandle, "处理端", OwnEdgePosition.Last, "Apply 落地", "落地是末步"),
        new(DrawTop, "触发端（卡组顶）", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(DrawSpecific, "触发端（指定牌）", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(DrawHandle, "处理端", OwnEdgePosition.Last, "Apply 落地", "同上：落地末步"),
        new(Death, "触发端", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(DeathHandle, "处理端", OwnEdgePosition.Last, "Veto 免死／Apply 死亡链",
            "免死守卫与死亡链同属末步段（旧 Veto＞Rewrite 的外来改写先跑）"),
        new(Move, "触发端", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(MoveHandle, "处理端", OwnEdgePosition.Last, "Apply 落地", "同上：落地末步"),
        new(JoinDeploy, "触发端（部署）", OwnEdgePosition.Last, "Commit 词条＋转发",
            "词条与转发同段，按声明序（词条先）；代价校验／宣告同端 before 相位"),
        new(JoinFrontline, "触发端（加入前线）", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(JoinSupport, "触发端（加入支援阵线）", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(JoinHandle, "处理端", OwnEdgePosition.Last, "Apply 落地", "同上：落地末步"),
        new(CombatDamage, "触发端（对战伤害）", OwnEdgePosition.Last, "Commit 转发",
            "重甲等 foreign 改写先跑（旧 Initiate），转发末步"),
        new(EffectDamage, "触发端（效果伤害）", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(BattleDamage, "触发端（战斗伤害）", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(DamageApply, "处理端", OwnEdgePosition.Last, "Apply 落地",
            "免疫等 foreign 改写先跑（旧 Rewrite），扣数值末步"),
        new(UnitAttack, "触发端（单位攻击）", OwnEdgePosition.Last, "Commit 转发", "同上：转发末步"),
        new(AttackDamage, "处理端", OwnEdgePosition.Last, "Apply 落地",
            "伏击等 foreign 改写先跑（旧 Rewrite），结算末步"),
        new(OrderCast, "触发端（指令施放）", OwnEdgePosition.Last, "Resolve 结算",
            "反制（旧 Counter）与宣告点先跑，施放结算末步"),
        new(OrderFinalize, "处理端（指令收尾）", OwnEdgePosition.Last, "Finalize 收尾", "收尾是末步"),
    };

    /// <summary>动作端点数（U4 的不变量：21）。</summary>
    public const int ActionEndCount = 21;

    /// <summary>按成员名查动作端点位次（不在表内＝非动作端点，抛错）。</summary>
    /// <exception cref="KeyNotFoundException">该成员不是动作端点。</exception>
    public static OwnEdgePosition PositionOf(string member)
        => ActionEndPositions.FirstOrDefault(e => string.Equals(e.Member, member, StringComparison.Ordinal))?.Position
            ?? throw new KeyNotFoundException($"'{member}' 不是动作端点（{ActionEndCount} 端见 {nameof(ActionEndPositions)}）。");

    /// <summary>引擎流程成员名（声明序；与预制体成员集合同源，守护测试锁漂移）。</summary>
    public static IReadOnlyList<string> MemberNames { get; } = new[]
    {
        Command,
        UnitMove,
        AttackDamage,
        CombatDamage,
        EffectDamage,
        BattleDamage,
        DamageApply,
        UnitAttack,
        ActionBudget,
        HandAdd,
        HandAddHandle,
        DrawTop,
        DrawSpecific,
        DrawHandle,
        Death,
        DeathHandle,
        Move,
        MoveHandle,
        JoinDeploy,
        JoinFrontline,
        JoinSupport,
        JoinHandle,
        OrderCast,
        OrderFinalize,
    };

    /// <summary>引擎流程成员（声明序，来源层＝游戏）。</summary>
    public static IReadOnlyList<MemberDefinition> Members { get; } = Build();

    /// <summary>
    /// 资产正在引用的 4 条注入映射（R1j 表；`injects[].target` 显示名 → 成员地址）。
    /// <para><c>指向触发器</c> 用 <c>host#</c>：卡与 HQ 各有该成员，按当前宿主实例解析天然消歧——不需要为此收窄 requires-host。</para>
    /// </summary>
    public static IReadOnlyList<LegacyTriggerMapping> InjectTargets { get; } = new[]
    {
        new LegacyTriggerMapping("伤害处理触发器", "engine#" + DamageApply, DamageApply, AddressScope.Engine, "Rewrite"),
        new LegacyTriggerMapping("单位攻击触发器", "engine#" + UnitAttack, UnitAttack, AddressScope.Engine, "Commit"),
        new LegacyTriggerMapping("行动额度触发器", "engine#" + ActionBudget, ActionBudget, AddressScope.Engine, null),
        new LegacyTriggerMapping("指向触发器", "host#" + CardProcesses.Targeting, CardProcesses.Targeting, AddressScope.Host, null),
    };

    /// <summary>
    /// 未被资产引用、但仍是 C# 侧直接调用入口的具名触发器（S7 第 3 条：全部保留并给出成员名）。
    /// </summary>
    public static IReadOnlyList<LegacyTriggerMapping> Retained { get; } = new[]
    {
        new LegacyTriggerMapping("预打出触发器", "host#" + CardProcesses.PrePlay, CardProcesses.PrePlay, AddressScope.Host, null),
        new LegacyTriggerMapping("打出触发器", "host#" + CardProcesses.Play, CardProcesses.Play, AddressScope.Host, null),
        new LegacyTriggerMapping("部署触发器", "host#" + CardProcesses.Deploy, CardProcesses.Deploy, AddressScope.Host, null),
        new LegacyTriggerMapping("部署词条触发器", "host#" + CardProcesses.DeployKeyword, CardProcesses.DeployKeyword, AddressScope.Host, null),
        new LegacyTriggerMapping("加入触发器", "host#" + CardProcesses.Join, CardProcesses.Join, AddressScope.Host, null),
        new LegacyTriggerMapping("单位化触发器", "host#" + CardProcesses.Unitize, CardProcesses.Unitize, AddressScope.Host, null),
        new LegacyTriggerMapping("老兵触发器", "host#" + CardProcesses.Veteran, CardProcesses.Veteran, AddressScope.Host, null),
        new LegacyTriggerMapping("揭示触发器", "host#" + CardProcesses.Reveal, CardProcesses.Reveal, AddressScope.Host, null),
        new LegacyTriggerMapping("指挥触发器", "engine#" + Command, Command, AddressScope.Engine, null),
        new LegacyTriggerMapping("单位移动触发器", "engine#" + UnitMove, UnitMove, AddressScope.Engine, null),
        new LegacyTriggerMapping("造成攻击伤害触发器", "engine#" + AttackDamage, AttackDamage, AddressScope.Engine, null),
        new LegacyTriggerMapping("对战伤害触发器", "engine#" + CombatDamage, CombatDamage, AddressScope.Engine, null),
        new LegacyTriggerMapping("效果伤害触发器", "engine#" + EffectDamage, EffectDamage, AddressScope.Engine, null),
        new LegacyTriggerMapping("战斗伤害触发器", "engine#" + BattleDamage, BattleDamage, AddressScope.Engine, null),
    };

    /// <summary>全部旧显示名映射（注入目标＋保留成员）。</summary>
    public static IReadOnlyList<LegacyTriggerMapping> All { get; } = InjectTargets.Concat(Retained).ToArray();

    /// <summary>按旧显示名查映射（迁移器用）。</summary>
    /// <param name="displayName">旧显示名（如"伤害处理触发器"）。</param>
    public static LegacyTriggerMapping? Find(string displayName)
        => All.FirstOrDefault(m => string.Equals(m.DisplayName, displayName, StringComparison.Ordinal));

    private static MemberDefinition[] Build()
    {
        // 视图字段集镜像旧 C# 视图类（S11 迁移时的权威字段名来源）；字段一律可选（装配期不保证全给）。
        // 说明：旧视图的 `Host` 字段**不**声明为载荷字段——宿主引用由承载视图内建（框架回填，R4），不来自数据面。
        var damageApply = SignalMember.Payload(
            SignalMember.Field("Source", ViewFieldType.Entity),
            SignalMember.Field("Target", ViewFieldType.Entity),
            SignalMember.Field("Amount", ViewFieldType.Number, writable: true),
            SignalMember.Field("Kind", ViewFieldType.String),
            SignalMember.Field("IsZeroed", ViewFieldType.Boolean, writable: true));

        var damageEvent = SignalMember.Payload(
            SignalMember.Field("Source", ViewFieldType.Entity),
            SignalMember.Field("Target", ViewFieldType.Entity),
            SignalMember.Field("Amount", ViewFieldType.Number, writable: true),
            SignalMember.Field("Kind", ViewFieldType.String));

        // 「造成攻击伤害」处理端的视图（b4 收尾修正）：字段权威＝旧视图 `AttackDamageTriggerView`
        // （Attacker／Target／Resolution／TriggerCard／Result）。此前这里错用了触发端的 damageEvent 形状
        // （Source／Target／Amount／Kind）——成员一旦**真的被执行**，挂在它上面的组件（伏击）读 `view.Attacker`
        // 就会直接撞上与声明不符的字段名（承载视图的字段名字空间＝目标成员的视图）。
        var attackDamage = SignalMember.Payload(
            SignalMember.Field("Attacker", ViewFieldType.Entity),
            SignalMember.Field("Target", ViewFieldType.Entity),
            SignalMember.Field("Resolution", ViewFieldType.Entity),
            SignalMember.Field("TriggerCard", ViewFieldType.Entity),
            SignalMember.Field("Result", ViewFieldType.Entity));

        var unitAttack = SignalMember.Payload(
            SignalMember.Field("Attacker", ViewFieldType.Entity),
            SignalMember.Field("Target", ViewFieldType.Entity),
            SignalMember.Field("TriggerCard", ViewFieldType.Entity),
            SignalMember.Field("IsCommandAttack", ViewFieldType.Boolean),
            SignalMember.Field("Result", ViewFieldType.Entity));

        var actionBudget = SignalMember.Payload(
            SignalMember.Field("Unit", ViewFieldType.Entity),
            SignalMember.Field("IsMove", ViewFieldType.Boolean),
            SignalMember.Field("IsTank", ViewFieldType.Boolean),
            SignalMember.Field("MoveDelta", ViewFieldType.Number, writable: true),
            SignalMember.Field("AttackDelta", ViewFieldType.Number, writable: true));

        return new[]
        {
            // 非动作端点（引擎级流程成员，不参与动作目录装配）：保持默认 first。
            Process(Command),
            Process(UnitMove),

            // 动作端点（U4：位次一律取自 ActionEndPositions 表，不在此另写一份）。
            Act(AttackDamage, attackDamage),
            Act(CombatDamage, damageEvent),
            Act(EffectDamage, damageEvent),
            Act(BattleDamage, damageEvent),
            Act(DamageApply, damageApply),
            Act(UnitAttack, unitAttack),
            Process(ActionBudget, actionBudget),

            Act(HandAdd),
            Act(HandAddHandle),
            Act(DrawTop),
            Act(DrawSpecific),
            Act(DrawHandle),
            Act(Death),
            Act(DeathHandle),
            Act(Move),
            Act(MoveHandle),
            Act(JoinDeploy),
            Act(JoinFrontline),
            Act(JoinSupport),
            Act(JoinHandle),
            Act(OrderCast),
            Act(OrderFinalize),
        };
    }

    /// <summary>声明一个**动作端点**：位次取自 <see cref="ActionEndPositions"/>（U4 的唯一真源）。</summary>
    private static MemberDefinition Act(string name, ViewSpec? view = null)
        => Process(name, view, PositionOf(name));

    private static MemberDefinition Process(
        string name, ViewSpec? view = null, OwnEdgePosition ownEdgePosition = OwnEdgePosition.First)
        => new(
            name,
            view,
            MemberVisibility.Hostable | MemberVisibility.Callable,
            layer: PrefabLayer.Game,
            ownEdgePosition: ownEdgePosition);
}

/// <summary>
/// 一条"旧显示名 → 成员地址"的映射（R1j）：供迁移器转换 <c>injects[].target</c>、供审查面把边反查回旧名。
/// </summary>
/// <param name="DisplayName">旧显示名（迁移完成后不得再出现在资产里）。</param>
/// <param name="Address">成员地址（<c>作用域#成员名</c>）。</param>
/// <param name="MemberName">成员名。</param>
/// <param name="Scope">作用域（engine／host）。</param>
/// <param name="LegacyBand">旧 <c>injects[].band</c>（null＝无 band）：`Rewrite`→wrap/before、`Commit`→wrap/after、null→外来挂载边（S11 第 1d 条）。</param>
public sealed record LegacyTriggerMapping(
    string DisplayName,
    string Address,
    string MemberName,
    AddressScope Scope,
    string? LegacyBand);

/// <summary>
/// 一个动作端点的位次标注（U4／D1）：成员名 ＋ 角色 ＋ 旧末步段 ＋ 自有边位次 ＋ 判据。
/// <para>
/// <see cref="OldTail"/> 只作迁移期留档（"这一端在旧模型里收尾于哪个段"）；新模型不看段，只看
/// <see cref="Position"/>。位次一律 <c>last</c> 的判据见 <see cref="EngineProcesses.ActionEndPositions"/>。
/// </para>
/// </summary>
/// <param name="Member">成员名（<c>EngineProcesses</c> 常量；落地址时为 <c>engine#&lt;成员名&gt;</c>）。</param>
/// <param name="Role">动作端角色（触发端／处理端；多入口动作带入口名）。</param>
/// <param name="Position">自有边位次（U4：21 端全为 <see cref="OwnEdgePosition.Last"/>）。</param>
/// <param name="OldTail">旧模型的末步段（如 <c>Commit 转发</c>／<c>Apply 落地</c>；仅迁移留档）。</param>
/// <param name="Rationale">判据（一句话；审查面与文档直接引用）。</param>
public sealed record ActionEndPosition(
    string Member,
    string Role,
    OwnEdgePosition Position,
    string OldTail,
    string Rationale);
