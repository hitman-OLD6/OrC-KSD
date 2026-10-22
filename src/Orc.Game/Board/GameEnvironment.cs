using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Board;

// ─────────────────────────────────────────────────────────────────────────────
// W3-1 G4（持续/条件静态能力）游戏环境（对局/战场级对象；单场唯一、多对局相互独立）：
// ①环境现算查询面（纯只读——无副作用、不触发重跑；每次读取返回当刻真值）：
//   「卡 → 玩家 → 对局/战场」的实时读取承载——相邻单位（同线索引差 1 直接邻位；隔槽不相邻；
//   与 2C 守护判定既有口径一致〔索引差 1〕）、「是否在前线」（中立共享前线线任一槽位）、
//   所在战线（GetLineOf）等；未在场/无位置实体按「不满足」处置（自然产出原值、不抛错）。
// ②场级收集面（Auras）：光环声明注册/注销的结构面——注册时点＝效果装载时（由效果装载链托管时点驱动）、
//   注销＝效果卸载时（装载链托管收口，按来源整组撤销）；受益卡重跑时现收集、过滤、合成（受益侧零增删）。
// ③全域重跑传播：环境类事件 → 全卡链重跑（全卡＝引擎卡登记的全部已加载卡实例——卡组中/手牌中/战场，含 HQ）；
//   无变化不通知（缓存比较既有保证——每卡零发射）；执行窗内到达的新请求＝合并（当前轮结束后重扫，
//   取代嵌套执行——收敛由「改变才传播＋固定点」保证）；逐卡异常隔离（记录、不阻断其它卡）。
// 触发源清单（本批定稿——「复用更新清单＋按需补充」；既有字面值不改不删、新增属加性演进）：
//   [纳入] unit.deployed / unit.joined / unit.position.changed / card.died（布局与在场类）；
//          card.hand.add / card.drawn / card.played（手牌与动作类）；
//          card.stat.changed（数值联动类——「条件依赖他卡数值」类表达的随动通路）；
//   [不纳入] turn 系列（相位——如未来条件表达依赖回合相位，属按需补充范围）；card.load（加载链）。
// 确定性：链/条件节只读本面（纯查询）；随动由「事件 → 重跑」保证（不依赖读取时的隐式副作用）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 游戏环境（W3-1 G4；对局级——随对局装配创建，多对局相互独立）：
/// 环境现算查询面（相邻 / 前线 / 战线定位——纯只读）＋场级收集面（<see cref="Auras"/>——光环声明注册/注销/收集）
/// ＋全域重跑传播（环境类事件 → 全卡链重跑；经总线订阅、同步顺序语义）。
/// 卡侧读取路径＝「卡 → 玩家 → 环境」（经 <see cref="ResolveFor"/>：卡经归属玩家取环境；
/// 未加载 / 独立构造的卡＝不可达＝无环境面〔自然产出原值、不抛错〕）。
/// 生命周期：随对局创建（战场创建后、卡加载前——效果装载注册须环境就绪）；与对局同周期（不做显式释放）。
/// </summary>
public sealed class GameEnvironment
{
    private readonly LogicEngine _engine;
    private bool _rerunRunning; // 全卡重跑执行窗（合并请求用；单线程语义）
    private bool _rerunRequested; // 执行窗内到达的新重跑请求（合并标记）

    /// <summary>
    /// 创建游戏环境（对局装配路径：战场创建后、卡加载前构造并注入各玩家——效果装载注册须环境就绪）。
    /// 构造即订阅总线（环境类事件 → 全卡链重跑；<see cref="HandleUpdateAsync"/>）。
    /// </summary>
    /// <param name="battlefield">战场（布局真源——三线：玩家A 支援线 / 前线 / 玩家B 支援线）。</param>
    /// <param name="engine">对局引擎（卡登记读面〔全卡枚举〕与总线订阅通道）。</param>
    /// <exception cref="ArgumentNullException">battlefield 或 engine 为 null。</exception>
    public GameEnvironment(Battlefield battlefield, LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(engine);

        Battlefield = battlefield;
        _engine = engine;
        Auras = new AuraRegistry();
        _engine.Subscribe(HandleUpdateAsync); // 全域重跑传播订阅（环境类事件 → 全卡链重跑）
    }

    /// <summary>战场（三条战线：玩家A 支援线 / 前线〔中立共享〕/ 玩家B 支援线）。</summary>
    public Battlefield Battlefield { get; }

    /// <summary>场级收集面（光环声明注册/注销的结构面；单场唯一——多对局相互独立）。</summary>
    public AuraRegistry Auras { get; }

    // ---------- ② 环境现算查询面（纯只读；不触发重跑、无副作用） ----------

    /// <summary>
    /// 读取卡所在阵线（纯读；未入场/不在任何线＝null、不抛错）：
    /// 持有单位数据组件（已单位化）＝单位所在线；HQ 实体＝所在线（布局语义）；其余＝null。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public BattleLine? GetPositionOf(Card card) => PositionOf(card);

    /// <summary>
    /// 读取卡所在阵线（静态只读形态——供无环境实例的调用方〔如判定器〕复用同一口径）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public static BattleLine? PositionOf(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (card.TryGetData<UnitStateData>(out var state))
        {
            return state.Line;
        }

        return card is Hq hq ? hq.Line : null;
    }

    /// <summary>读取卡在其阵线中的位置序号（未入场＝-1）。</summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public static int PositionIndexOf(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return PositionOf(card) is { } line ? line.IndexOf(card) : -1;
    }

    /// <summary>
    /// 判定卡是否位于「前线」（中立共享的前线线；与视角无关）。
    /// 未入场/不在线＝不满足（false——自然产出原值、不抛错）；判定随位置实时变化（随动由「事件 → 重跑」保证）。
    /// 概念区分：「在前线」（位置判定）≠「前线占据」（推进前置的状态判定——两概念勿混）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public bool IsOnFrontLine(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return ReferenceEquals(PositionOf(card), Battlefield.FrontLine);
    }

    /// <summary>
    /// 读取卡所在阵线（纯读；未入场＝null、不抛错）。
    /// 用途：阵线归属判定（如「位于某玩家支援线」＝与 <c>Battlefield.GetSupportLine(player)</c> 引用同一性比较）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public BattleLine? GetLineOf(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return PositionOf(card);
    }

    /// <summary>
    /// 判定两卡是否相邻（同线、位置序号差 1 的直接邻位；跨线不算；与 2C 守护判定既有口径一致）。
    /// 任一无位置＝不满足（false）；HQ 参与相邻关系（「相邻」关系对 HQ 成立——关系与计数分离，见 <see cref="GetAdjacentUnits"/>）。
    /// </summary>
    /// <exception cref="ArgumentNullException">a 或 b 为 null。</exception>
    public bool AreAdjacent(Card a, Card b) => AreAdjacent(Battlefield, a, b);

    /// <summary>
    /// 相邻判定的静态形态（同一口径；供判定器等无环境实例的调用方复用）：
    /// 同线、位置序号差 1 的直接邻位；任一无位置＝false；HQ 参与相邻关系。
    /// </summary>
    /// <exception cref="ArgumentNullException">battlefield / a / b 为 null。</exception>
    public static bool AreAdjacent(Battlefield battlefield, Card a, Card b)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var line = PositionOf(a);
        if (line is null || !ReferenceEquals(line, PositionOf(b)))
        {
            return false;
        }

        if (!ReferenceEquals(line, battlefield.PlayerASupportLine)
            && !ReferenceEquals(line, battlefield.FrontLine)
            && !ReferenceEquals(line, battlefield.PlayerBSupportLine))
        {
            return false;
        }

        var indexA = line.IndexOf(a);
        var indexB = line.IndexOf(b);
        return indexA >= 0 && indexB >= 0 && Math.Abs(indexA - indexB) == 1;
    }

    /// <summary>
    /// 读取卡的相邻单位（左右紧邻——同线位置序号差 1；跨线不算）。
    /// 计数对象＝单位：HQ 不计数（HQ 非单位——关系与计数分离）、已死亡单位不计数；
    /// 不分敌我（凡左右紧邻的单位均计入——谓词可进一步收窄）。
    /// 顺序＝位置序号升序（左邻在前、右邻在后）；未入场＝空列表（不抛错）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public IReadOnlyList<UnitCard> GetAdjacentUnits(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var line = PositionOf(card);
        if (line is null)
        {
            return Array.Empty<UnitCard>();
        }

        var index = line.IndexOf(card);
        if (index < 0)
        {
            return Array.Empty<UnitCard>();
        }

        var result = new List<UnitCard>(2);
        CollectAdjacentUnit(line, index - 1, result);
        CollectAdjacentUnit(line, index + 1, result);
        return result;
    }

    // ---------- ③ 场级收集面读取（收集步骤的过滤读取面——纯只读） ----------

    /// <summary>
    /// 收集「受益卡 + 字段」命中的光环声明（纯只读——不触发重跑、无副作用；稳定序＝收集面登记序）：
    /// 过滤链＝① 字段匹配（声明贡献目标字段＝本次求值字段）；② 通用门禁（声明宿主在场/存活/未离场——
    /// 必经兜底：卡组期注册的声明被拦截、死亡/离场后不命中）；③ 声明谓词（附加条件——如「相邻」「源在前线」）；
    /// ④ S2 加性（隐蔽机制·豁免剔除单点收口）：受益者为「隐蔽」单位＝不收集（隐蔽单位不作为光环受益者——
    /// 不被任何光环影响，含友方光环；作为光环**来源**不受限——宿主门禁照常、其光环照常作用于其他单位）。
    /// 合成规则＝命中声明独立参与、按登记序依次施加（确定变换——同输入同序同输出）；零声明＝空列表（快速路径）。
    /// </summary>
    /// <exception cref="ArgumentNullException">beneficiary 为 null。</exception>
    /// <exception cref="ArgumentException">field 为 null/空白。</exception>
    public IReadOnlyList<AuraDeclaration> CollectAuras(Card beneficiary, string field)
    {
        ArgumentNullException.ThrowIfNull(beneficiary);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        // S2：隐蔽单位为受益人＝不收集（零声明快速路径之前先行拦截——语义上不可能有命中）。
        if (beneficiary is UnitCard covertUnit && CovertRules.IsCovert(covertUnit))
        {
            return Array.Empty<AuraDeclaration>();
        }

        var declarations = Auras.All;
        if (declarations.Count == 0)
        {
            return Array.Empty<AuraDeclaration>(); // 零声明快速路径（常态）
        }

        var hits = new List<AuraDeclaration>();
        foreach (var declaration in declarations) // 稳定序＝登记序
        {
            if (!string.Equals(declaration.Field, field, StringComparison.Ordinal))
            {
                continue; // 字段不匹配（贡献目标字段 ≠ 本次求值字段）
            }

            if (!declaration.IsHostActive())
            {
                continue; // 通用门禁：来源未在场/已死亡/已离场 → 不命中（卡组期注册的必经拦截）
            }

            if (declaration.Predicate is { } predicate && !predicate(this, beneficiary))
            {
                continue; // 声明谓词（附加条件——如「相邻单位」「源在前线」）
            }

            hits.Add(declaration);
        }

        return hits;
    }

    // ---------- ④ 全域重跑传播（环境事件 → 全卡链重跑） ----------

    /// <summary>
    /// 全卡链重跑（统一入口；事件驱动自动面＋显式驱动面）：
    /// 遍历引擎卡登记的全部已加载卡实例（卡组中/手牌中/战场；含 HQ 实体——无例外），逐卡请求链重跑
    /// （全量重跑 → 检测比较 → 有变更才发；无变化＝零发射——收敛由既有缓存比较保证）。
    /// 重入合并：执行窗内到达的新请求＝标记（本类当前轮结束后重扫，取代嵌套执行）；遍历快照＝调用时点一致副本。
    /// 逐卡异常隔离：单卡链异常＝记录、不阻断其它卡（取消类异常穿透——与订阅者广播口径一致）。
    /// </summary>
    public async Task RerunAllCardsAsync(CancellationToken ct = default)
    {
        if (_rerunRunning)
        {
            _rerunRequested = true; // 执行窗内到达：合并（当前轮结束后重扫——避免嵌套执行）
            return;
        }

        _rerunRunning = true;
        try
        {
            do
            {
                _rerunRequested = false;
                foreach (var card in _engine.Cards.ToArray()) // 快照（遍历期间卡登记变化不影响本轮）
                {
                    var modifiers = ResolveModifiers(card);
                    if (modifiers is null)
                    {
                        continue; // 非链宿主/未加载：不属于「全卡」域
                    }

                    try
                    {
                        await modifiers.RequestRerunAsync(ct); // 无变化＝零发射（缓存比较既有保证）
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // 取消类异常：与订阅者广播口径一致，穿透上抛
                    }
                    catch (Exception ex)
                    {
                        WriteRerunError(card, ex); // 逐卡隔离：单卡异常不阻断其它卡的随动
                    }
                }
            }
            while (_rerunRequested); // 重跑期间产生的环境事件（如变化发射引发的联动）：再扫一轮（收敛即止）
        }
        finally
        {
            _rerunRunning = false;
        }
    }

    /// <summary>
    /// 卡 → 环境解析（读取路径「卡 → 玩家 → 对局/战场」的收敛点）：
    /// 卡经归属玩家取环境；未加载（无归属）/独立构造（无环境注入）/非卡实体＝null（不可达——调用方按「无环境面」处置）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public static GameEnvironment? ResolveFor(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card switch
        {
            Hq hq => hq.Owner.Environment,
            CardBase cardBase => cardBase.Owner?.Environment,
            _ => null,
        };
    }

    // ---------- 内部：总线订阅与辅助 ----------

    /// <summary>总线回调（环境类事件 → 全卡链重跑；非环境类事件＝忽略）。</summary>
    private Task HandleUpdateAsync(
        string updateType, IReadOnlyDictionary<string, object?>? payload, CancellationToken ct)
        => IsRerunTrigger(updateType) ? RerunAllCardsAsync(ct) : Task.CompletedTask;

    /// <summary>触发源清单判定（本批定稿——见文件头注释；字面值引用常量、不裸字符串）。</summary>
    private static bool IsRerunTrigger(string updateType) => updateType switch
    {
        GameUpdates.UnitDeployed
            or GameUpdates.UnitJoined
            or GameUpdates.UnitPositionChanged
            or GameUpdates.CardDied
            or GameUpdates.CardHandAdd
            or GameUpdates.CardDrawn
            or GameUpdates.CardPlayed
            or GameUpdates.CardStatChanged => true,
        _ => false,
    };

    /// <summary>「全卡」域解析：已加载卡（对局装载路径——归属已装配）与 HQ 实体＝链宿主（返回其修饰容器）；其余＝null。</summary>
    private static CardModifierComponent? ResolveModifiers(Card card) => card switch
    {
        Hq hq => hq.Modifiers,
        CardBase cardBase when cardBase.Owner is not null => cardBase.Modifiers,
        _ => null,
    };

    /// <summary>全卡重跑单卡异常隔离记录（引擎总流；与既有隔离留痕同渠道）。</summary>
    private void WriteRerunError(Card card, Exception ex)
    {
        _engine.RootStream.WriteLog(
            "环境重跑",
            ex.Message,
            LogLevel.Error,
            new[] { $"exception:{ex.GetType().Name}" },
            new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().FullName,
                ["message"] = ex.Message,
                ["card"] = card.Name,
            });
    }

    /// <summary>邻位单位收集（越界/HQ/已死亡＝不计数）。</summary>
    private static void CollectAdjacentUnit(BattleLine line, int index, List<UnitCard> result)
    {
        if (index < 0 || index >= line.Size)
        {
            return;
        }

        if (line[index] is UnitCard unit && !IsDead(unit))
        {
            result.Add(unit);
        }
    }

    /// <summary>死亡判定（防御性过滤——死亡单位不应占位；清位后自然为空槽）。</summary>
    private static bool IsDead(UnitCard unit)
        => unit.TryGetData<UnitStateData>(out var state) && state.IsDestroyed;
}
